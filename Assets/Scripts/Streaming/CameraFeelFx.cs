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
        private static readonly int SrcFrameId = Shader.PropertyToID("_SrcFrame");
        private static readonly int ChromaKillId = Shader.PropertyToID("_ChromaKill");
        private static readonly int GlareId = Shader.PropertyToID("_Glare");
        private static readonly int MonoId = Shader.PropertyToID("_Mono");
        private static readonly int CrtRoundId = Shader.PropertyToID("_CrtRound");
        private static readonly int CrtEdgeId = Shader.PropertyToID("_CrtEdge");
        private static readonly int CrtEdgeWidthId = Shader.PropertyToID("_CrtEdgeWidth");
        private static readonly int EchoId = Shader.PropertyToID("_Echo");
        private static readonly int EchoTexId = Shader.PropertyToID("_EchoTex");
        private static readonly int CoarseBlocksId = Shader.PropertyToID("_CoarseBlocks");

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

        /// <summary>
        /// いま**画に掛かっている**露出バイアス (EV)。agc 込み＝シェーダへ書いた値そのもの。
        /// <see cref="Cg.ShowCgLayer"/> が人形の明るさを「画に出た後の明るさ」へ寄せるのに読む。
        /// </summary>
        public float AppliedExposureBias => _logic.ExposureBias * _agc;

        /// <summary>凍らせた 1 枚の混合率 0..1。診断用。</summary>
        public float Echo => _logic.Echo;

        /// <summary>
        /// いま書いている「枠を横切るブロック数」（0 = 量子化していない）。診断用。
        /// 決めるのは <see cref="ScreenDecayLogic"/>、回すのは <see cref="ShowRunDirector"/>。
        /// </summary>
        public float CoarseBlocks { get; private set; }

        /// <summary>
        /// 書く先（スクリーンの Renderer のマテリアル）を掴めているか。
        /// <b>ここが false だと、進みがいくら動いても画は 1 画素も変わらない。</b>
        /// テレメトリが出す（「状態が進んだ」ではなく「効果が出た」を観測するため）。
        /// </summary>
        public bool HasMaterial => _material != null;

        /// <summary>
        /// 周回で進む解像度の劣化を書く（<see cref="ShowRunDirector"/> が毎フレーム押す）。
        /// 0 = 量子化しない。**ここが唯一の入口**で、値の意味も対応表も
        /// <see cref="ScreenDecayLogic"/> が持つ。
        /// </summary>
        public void SetCoarseBlocks(float blocks) => CoarseBlocks = blocks > 0f ? blocks : 0f;

        /// <summary>
        /// 夜間モードへの進み 0..1（<see cref="ShowRunDirector"/> が毎フレーム押す）。
        /// **解像度の劣化と同じ <see cref="ScreenDecayLogic.Progress"/> を渡す**
        /// — 1 周目は暖色、3 周目の A で完全な無彩（`canon/LEDGER.md` 0019）。
        /// </summary>
        public void SetMono(float progress) => Mono = Mathf.Clamp01(progress);

        /// <summary>いま書いている夜間モードの進み 0..1。診断用。</summary>
        public float Mono { get; private set; }

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
        /// show.json <c>feel</c> の解決結果。**「全部 0」の実体は未指定**として扱う
        /// （JsonUtility が欠落キーをそう埋めるため）。
        ///
        /// 構造体に切り出したのは、<b>Editor の合成プレビューが同じ解決を通れる</b>ようにするため。
        /// あちらが独自に既定値を持つと、「プレビューでは馴染むのに実機では浮く」が黙って起きる。
        /// </summary>
        public readonly struct Settings
        {
            public readonly float NoiseDark;
            public readonly float NoiseFixed;
            public readonly float Agc;
            public readonly float TargetLuma;
            public readonly float FollowSec;

            private Settings(float noiseDark, float noiseFixed, float agc, float targetLuma, float followSec)
            {
                NoiseDark = noiseDark; NoiseFixed = noiseFixed; Agc = agc;
                TargetLuma = targetLuma; FollowSec = followSec;
            }

            public static Settings Resolve(ShowFeelDef? def)
            {
                ShowFeelDef d = (def == null || def.LooksUnset()) ? new ShowFeelDef() : def;
                return new Settings(
                    Mathf.Clamp(d.noiseDark, 0f, 0.5f),
                    Mathf.Clamp(d.noiseFixed, 0f, 0.3f),
                    Mathf.Clamp01(d.agc),
                    d.targetLuma > 0f ? d.targetLuma : ShowFeelDef.DefaultTargetLuma,
                    d.followSec > 0f ? d.followSec : ShowFeelDef.DefaultFollowSec);
            }
        }

        /// <summary>
        /// uniform の書き方の**単一の正**。実行時（<see cref="Write"/>）と Editor の合成プレビューが
        /// ここを通る。片方だけ直したときに黙って食い違うのを防ぐ。
        /// </summary>
        /// <param name="coarseBlocks">
        /// 周回で進む解像度の劣化（枠を横切るブロック数・0 = 量子化しない）。
        /// **引数にしてあるのは、静止画のプレビューに「進みを知らないから 0」を黙って選ばせないため** —
        /// 実機だけが持つ加工をプレビューが書き落として「プレビューの方が綺麗」になる事故を
        /// 2026-08-07 に踏んでいる（feel の 5 項目）。
        /// </param>
        /// <param name="srcFrame">
        /// ソース映像の連番。**粒はこの時計で動く**（VR の 90Hz ではない）。
        /// 静止画のプレビューでは 0 のままでよい（時刻を持たないので粒も動かない）。
        /// </param>
        /// <param name="mono">
        /// 夜間モードへの進み 0..1（1 = 完全な無彩）。**解像度の劣化と同じ進み**を渡すこと
        /// — 別々に動かすと「色は残っているのに粒だけ多い」ような、装置として説明の付かない絵になる。
        /// </param>
        public static void WriteUniforms(Material? mat, float noiseDark, float noiseFixed,
                                         float exposureBias, float echo,
                                         float coarseBlocks, float srcFrame, float mono)
        {
            if (mat == null) return;
            mat.SetFloat(MonoId, mono);
            mat.SetFloat(NoiseDarkId, noiseDark);
            mat.SetFloat(NoiseFixedId, noiseFixed);
            mat.SetFloat(ExposureBiasId, exposureBias);
            mat.SetFloat(EchoId, echo);
            mat.SetFloat(CoarseBlocksId, coarseBlocks);
            mat.SetFloat(SrcFrameId, srcFrame);
            mat.SetFloat(ChromaKillId, ChromaKill);
            mat.SetFloat(GlareId, Glare);
            mat.SetFloat(CrtRoundId, CrtRound);
            mat.SetFloat(CrtEdgeId, CrtEdge);
            mat.SetFloat(CrtEdgeWidthId, CrtEdgeWidth);
        }

        /// <summary>
        /// ブラウン管の面。角の丸みと、管の縁が落ちる暗さ。
        /// **形（曲面）はメッシュが持つ**（<see cref="CrtScreenMesh"/>）。
        ///
        /// ⚠ ヴィネット（<c>_Vignette</c>）とは別のもの。あちらは**レンズに光が届かない**話で
        /// 現像より前に効く。こちらは**管の形**なので post の最後、枠の座標で掛かる。
        /// 混ぜると「暗い所をもう一度暗くする」だけになって、どちらの理由も画から読めなくなる。
        /// </summary>
        public const float CrtRound = 0.07f;
        public const float CrtEdge = 0.34f;
        public const float CrtEdgeWidth = 0.17f;

        /// <summary>
        /// レンズの内面反射（ベイリンググレア）。明るい所の光が暗い所へ薄く回り込む量。
        /// 安いレンズほど強く、監視カメラの画では必ず出る（光源のまわりが滲む）。
        /// <see cref="ChromaKill"/> と同じく**その装置の素性**なので show.json へは出していない。
        /// </summary>
        public const float Glare = 0.55f;

        /// <summary>
        /// 暗部の色を殺す量。安い ISP はノイズリダクションで**暗い所の色差から捨てる**ので、
        /// 明るい所に色が残り、暗がりだけが無彩へ落ちる。
        ///
        /// show.json へ出していないのは、これが現場で調整するものではなく
        /// **その装置の素性**だから（`_CgSoften` と同じ扱い）。
        /// </summary>
        public const float ChromaKill = 0.85f;

        /// <summary>
        /// show.json <c>feel</c> の適用。キーが無ければコード既定のまま。
        /// </summary>
        public void Configure(ShowFeelDef? def)
        {
            Settings s = Settings.Resolve(def);
            _noiseDark = s.NoiseDark;
            _noiseFixed = s.NoiseFixed;
            _agc = s.Agc;
            _logic.TargetLuma = s.TargetLuma;
            _logic.FollowHalfLifeSec = s.FollowSec;
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
            // 自分を外したら画は素へ戻す（このコンポーネントが無い状態と同じ画にして去る）。
            // ⚠ これは「終了で畳む」とは別の話 — 終了しても Update は回り続けるので値は保たれる。
            CoarseBlocks = 0f;
            Mono = 0f;
            ApplyFrozen(false);
            ClearSplit();
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

        // ---- 左右分割（canon/LEDGER.md 0050）--------------------------------------
        //
        //   3 周目 A（左＝反転したライブ → 凍結 → 1 周目の録画 / 右＝ライブ → 環境＋人形）と
        //   4 周目 A（左＝大量の人形 / 右＝体験者人形＋環境）が使う。
        //   **writer をここへ寄せる**のは、同じマテリアルを掴んでいる唯一のコンポーネントだから
        //   （書き手が 2 つになると、どちらが最後に書いたかで画が変わる）。
        //
        //   ⚠ **演出の外では必ず 0 へ戻す。** 残すと画が割れたまま・凍ったままになる。
        //   凍結が解けない事故をこの codebase は 4 回踏んでいる。

        private static readonly int SplitXId = Shader.PropertyToID("_SplitX");
        private static readonly int SplitFlipLeftId = Shader.PropertyToID("_SplitFlipLeft");
        private static readonly int SplitFreezeLeftId = Shader.PropertyToID("_SplitFreezeLeft");
        private static readonly int Overlay2TexId = Shader.PropertyToID("_Overlay2Tex");
        private static readonly int Mask2TexId = Shader.PropertyToID("_Mask2Tex");
        private static readonly int Overlay2ScaleId = Shader.PropertyToID("_Overlay2Scale");
        private static readonly int Overlay2StrengthId = Shader.PropertyToID("_Overlay2Strength");

        /// <summary>
        /// いま書いている分割位置 0..1（0 = 割っていない）。診断用。
        /// <b>書く先を掴めていなければ画は 1 画素も割れない</b>ので、<see cref="HasMaterial"/> と対で読む。
        /// </summary>
        public float SplitX { get; private set; }

        /// <summary>いま書いている第 2 層の合成の重み 0..1（0 = 出ていない）。診断用。</summary>
        public float Overlay2Strength { get; private set; }

        /// <summary>左右分割を設定する。<paramref name="splitX"/> が 0 なら分割なし。</summary>
        /// <param name="splitX">分割位置（0..1・枠の座標）。境目は環境の縦線へ置く</param>
        /// <param name="flipLeft">左半分だけ左右反転してライブを読む（環境が左右対称なカメラだけ）</param>
        /// <param name="freezeLeft">左半分だけ凍らせる量（0..1）。1 で完全に止まる</param>
        public void SetSplit(float splitX, bool flipLeft, float freezeLeft)
        {
            SplitX = Mathf.Clamp01(splitX);
            if (_material == null) return;
            _material.SetFloat(SplitXId, SplitX);
            _material.SetFloat(SplitFlipLeftId, flipLeft ? 1f : 0f);
            _material.SetFloat(SplitFreezeLeftId, Mathf.Clamp01(freezeLeft));
        }

        /// <summary>第 2 の差し替え層（左右へ別の素材を同時に置く）。<paramref name="tex"/> が null なら消える。</summary>
        public void SetOverlay2(Texture? tex, Texture? mask, Vector2 containScale, float strength)
        {
            Overlay2Strength = tex == null ? 0f : Mathf.Clamp01(strength);
            if (_material == null) return;
            _material.SetTexture(Overlay2TexId, tex);
            _material.SetTexture(Mask2TexId, mask);
            _material.SetVector(Overlay2ScaleId, new Vector4(containScale.x, containScale.y, 0f, 0f));
            _material.SetFloat(Overlay2StrengthId, Overlay2Strength);
        }

        /// <summary>分割と第 2 層を演出の外の状態へ戻す。**畳むすべての経路から呼ぶ**。</summary>
        public void ClearSplit()
        {
            SetSplit(0f, false, 0f);
            SetOverlay2(null, null, Vector2.one, 0f);
        }

        /// <summary>「いまの画」を凍結の 1 枚として捕まえる（全画面は凍らせない）。</summary>
        public void CaptureFreezeFrame() => CaptureEcho();

        private void ApplyFrozen(bool on)
        {
            if (_frozenApplied == on) return;
            _frozenApplied = on;
            overlay?.SetFrozen(on);
        }

        private void Write()
            // 自動露出の効き（agc）は「装置がどれだけ律儀に追うか」。0 で追従なし。
            // 凍らせた 1 枚がまだ無いうちに混ぜると黒が出る。
            //
            // ⚠ 解像度の劣化は <see cref="ResetAll"/> で畳まない。畳むと**終了の瞬間に画が急に鮮明になる**
            //    （終了条件が立ってから走行中の演出を見せ切る猶予がある）。落とすのは体験者の交代だけで、
            //    その号令は ShowRunDirector.BeginRun が ScreenDecayLogic.Reset へ出す。
            => WriteUniforms(_material, _noiseDark, _noiseFixed,
                             _logic.ExposureBias * _agc,
                             _echoTex != null ? _logic.Echo : 0f,
                             CoarseBlocks,
                             screen != null ? screen.SourceFrameId : 0f,
                             Mono);
    }
}
