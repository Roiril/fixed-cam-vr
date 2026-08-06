#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 「装置らしさ」を画に出す。<c>_NoiseDark</c> / <c>_NoiseFixed</c> / <c>_ExposureBias</c> /
    /// <c>_VignetteBias</c> / <c>_Echo</c> / <c>_EchoTex</c> の**唯一の writer**
    /// （<see cref="GlitchFx"/> / <see cref="SignalLostFx"/> と同じ流儀）。
    ///
    /// <b>なぜ要るか</b>: 現行の加工（post 12 項目）はすべて全域一様で、すべてに物理的な言い訳が付く。
    /// 乱れは自動的に機材のせいになるので、強くしても「そういう画」に慣れて終わる。
    /// ここが持つのは、その言い訳の内側で効く 3 つ:
    ///
    ///   1. <b>暗部ノイズ</b> — 暗いところほど粒が乗る（実センサの SN）。**暗がりが物を隠せる状態**を作る。
    ///      固定視点なので、映っていない領域の存在は確かめられない。恐怖は物を足すより
    ///      観測できる面積を削るほうが安く作れる。
    ///   2. <b>固定パターンノイズ</b> — 時間で動かない粒。画面に貼り付いた汚れとして静止し、
    ///      その中で**動いているものだけが浮く**（変化検出は差分で働く）。
    ///   3. <b>自動露出の追従遅れ</b> — 明るさが遅れて追いつき、行き過ぎて戻る。
    ///      画に映っている範囲では何も変わっていないのに明るさが動くと、
    ///      「画面の外で何かが起きた」と読まれる。装置が世界に反応する唯一の手段。
    ///
    /// 加えて、<b>凍らせた 1 枚</b>（ホールド / 焼き付き）をカットから発火できる。
    /// フレーム履歴ではなく指定した瞬間の 1 枚なので決定的で、同じ show.json は同じ絵になる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraFeelFx : MonoBehaviour
    {
        private static readonly int NoiseDarkId = Shader.PropertyToID("_NoiseDark");
        private static readonly int NoiseFixedId = Shader.PropertyToID("_NoiseFixed");
        private static readonly int ExposureBiasId = Shader.PropertyToID("_ExposureBias");
        private static readonly int VignetteBiasId = Shader.PropertyToID("_VignetteBias");
        private static readonly int EchoId = Shader.PropertyToID("_Echo");
        private static readonly int EchoTexId = Shader.PropertyToID("_EchoTex");

        [Tooltip("ライブ映像の供給元（平均輝度と、凍らせる 1 枚の複製元）。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private MjpegScreen? screen;

        [Tooltip("ホールド中に素材の時計も止めるための参照。null なら同 GameObject → シーンから探す。")]
        [SerializeField] private ScreenOverlayController? overlay;

        private readonly CameraFeelLogic _logic = new CameraFeelLogic();
        private Material? _material;
        private RenderTexture? _echoTex;
        private bool _frozenApplied;

        // show.json `feel` 由来の静的な強さ。**既定値のままでも効く**ようにしてある
        // （設定を書かないと怖くならない、では現場で使われない）。
        private float _noiseDark = ShowFeelDef.DefaultNoiseDark;
        private float _noiseFixed = ShowFeelDef.DefaultNoiseFixed;
        private float _agc = ShowFeelDef.DefaultAgc;

        /// <summary>いまの露出バイアス (EV)。診断用。</summary>
        public float ExposureBias => _logic.ExposureBias;

        /// <summary>凍らせた 1 枚の混合率 0..1。診断用。</summary>
        public float Echo => _logic.Echo;

        /// <summary>画を止める（カットの <c>hold</c>）。</summary>
        public void Hold(float sec) => _logic.Hold(sec);

        /// <summary>焼き付き（カットの <c>burn</c>）。少し前の姿がそこに薄く残る。</summary>
        public void Burn(float amount, float sec = 2.5f) => _logic.Burn(amount, sec);

        /// <summary>すべて畳む（ラン開始・体験の終了）。</summary>
        public void ResetAll()
        {
            _logic.Reset();
            ApplyFrozen(false);
            Write();
        }

        /// <summary>
        /// show.json <c>feel</c> の適用。キーが無ければコード既定のまま。
        /// **「全部 0」の実体は未指定**として扱う（JsonUtility が欠落キーをそう埋めるため）。
        /// </summary>
        public void Configure(ShowFeelDef? def)
        {
            ShowFeelDef d = (def == null || def.LooksUnset()) ? new ShowFeelDef() : def;
            _noiseDark = Mathf.Clamp(d.noiseDark, 0f, 0.5f);
            _noiseFixed = Mathf.Clamp(d.noiseFixed, 0f, 0.3f);
            _agc = Mathf.Clamp01(d.agc);
            _logic.TargetLuma = d.targetLuma > 0f ? d.targetLuma : ShowFeelDef.DefaultTargetLuma;
            _logic.FollowHalfLifeSec = d.followSec > 0f ? d.followSec : ShowFeelDef.DefaultFollowSec;
        }

        private void Awake()
        {
            var r = GetComponent<Renderer>();
            _material = r != null ? r.material : null;
            if (screen == null) screen = GetComponent<MjpegScreen>();
            if (screen == null) screen = FindObjectOfType<MjpegScreen>();
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
            if (overlay == null) overlay = FindObjectOfType<ScreenOverlayController>();
        }

        private void OnEnable()
        {
            _logic.Reset();
            Write();
        }

        private void OnDisable()
        {
            _logic.Reset();
            ApplyFrozen(false);
            Write();
        }

        private void OnDestroy()
        {
            if (_echoTex == null) return;
            _echoTex.Release();
            if (Application.isPlaying) Destroy(_echoTex); else DestroyImmediate(_echoTex);
            _echoTex = null;
        }

        private void Update()
        {
            if (screen != null) _logic.ObserveLuma(screen.SourceLuma);
            // 切替 dip・乱れと同じく unscaledDeltaTime（timeScale=0 で凍りつかせない）。
            _logic.Tick(Time.unscaledDeltaTime);
            if (_logic.ConsumeSnapshotRequest()) CaptureEcho();
            ApplyFrozen(_logic.Frozen);
            Write();
        }

        // 「いまの画」を 1 枚だけ RT へ複製する。Blit なのでフォーマット違い（RGB24 → ARGB32）も通る。
        private void CaptureEcho()
        {
            Texture? live = screen != null ? screen.LiveTexture : null;
            if (live == null || live.width <= 4 || live.height <= 4) return;

            if (_echoTex == null || _echoTex.width != live.width || _echoTex.height != live.height)
            {
                if (_echoTex != null)
                {
                    _echoTex.Release();
                    if (Application.isPlaying) Destroy(_echoTex); else DestroyImmediate(_echoTex);
                }
                _echoTex = new RenderTexture(live.width, live.height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "ScreenEcho",
                    useMipMap = false,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _echoTex.Create();
            }
            Graphics.Blit(live, _echoTex);
            _material?.SetTexture(EchoTexId, _echoTex);
        }

        private void ApplyFrozen(bool on)
        {
            if (_frozenApplied == on) return;
            _frozenApplied = on;
            overlay?.SetFrozen(on);
        }

        private void Write()
        {
            if (_material == null) return;
            _material.SetFloat(NoiseDarkId, _noiseDark);
            _material.SetFloat(NoiseFixedId, _noiseFixed);
            // 自動露出の効き（agc）は「装置がどれだけ律儀に追うか」。0 で追従なし。
            _material.SetFloat(ExposureBiasId, _logic.ExposureBias * _agc);
            _material.SetFloat(VignetteBiasId, _logic.VignetteBias * _agc);
            // 凍らせた 1 枚がまだ無いうちに混ぜると黒が出る。
            _material.SetFloat(EchoId, _echoTex != null ? _logic.Echo : 0f);
        }
    }
}
