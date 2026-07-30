#nullable enable

using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// 導入演出のあいだ、<b>パススルー自体の見た目</b>を当てる係。
    /// <see cref="IntroDirector.Weights"/> を毎フレーム読み、彩度・コントラスト・輪郭線へ流す。
    ///
    /// ここが Assembly-CSharp 側（asmdef なし）に居るのは、<b>Streaming asmdef から OVR を
    /// 参照しない</b>という既存の規約のため（ハンドトラッキングの <c>OvrHandTrackingBridge</c> と同型）。
    /// Streaming は値を公開するだけで、OVR を知らない。
    ///
    /// 確認済みの API（実パッケージ <c>com.meta.xr.sdk.core@201.0.0</c> のソース）:
    ///   - <c>SetBrightnessContrastSaturation(brightness, contrast, saturation)</c> — 各 <b>[-1, 1]</b>、0 = 変更なし
    ///   - <c>edgeRenderingEnabled</c> / <c>edgeColor</c>（alpha が輪郭の強さ）
    ///   - どちらも <c>styleDirty</c> 経由で LateUpdate に 1 回だけ反映される（レイヤ再生成なし）
    ///
    /// ⚠ <c>textureOpacity</c> は触らない。Underlay では「暗くする」意味しか持たず、
    /// 枠の外を黒にするのは <c>IntroVeil</c>（alpha を書く面）の仕事。役割を混ぜると
    /// どちらが効いているのか分からなくなる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassthroughStyler : MonoBehaviour
    {
        [Tooltip("パススルーのレイヤ。null なら実行時に探す（OVRCameraRig に付けるのが定石）。")]
        [SerializeField] private OVRPassthroughLayer? layer;

        [Tooltip("導入演出の実行体。null なら実行時に探す。")]
        [SerializeField] private IntroDirector? director;

        [Tooltip("格下げが最大のときの明るさ。わずかに落とす（-1..0）。")]
        [SerializeField, Range(-1f, 0f)] private float brightnessAtFull = -0.15f;

        [Tooltip("格下げが最大のときのコントラスト（0..1）。")]
        [SerializeField, Range(0f, 1f)] private float contrastAtFull = 0.35f;

        [Tooltip("演出が終わったらパススルーを切るか。切ると数百 ms の黒が出るが、その時点で覆いが黒いので見えない。")]
        [SerializeField] private bool disableWhenDone = true;

        private bool _styled;          // 一度でも style を当てたか（戻す必要があるか）
        private bool _wasActive;
        private bool _turnedOff;

        private void Awake() => Resolve();

        private void OnEnable()
        {
            Resolve();
            // 導入演出があるなら、まずパススルーを立てる。
            // ⚠ 有効化は非同期で数百 ms かかる（公式に "black flicker"）。だから起動時に 1 回だけ立て、
            // 演出中は切らない。切るのは演出が終わって覆いが黒くなってから。
            if (director != null && director.Active) SetPassthrough(true);
        }

        private void OnDisable()
        {
            // 自分が当てた style を残して去らない。
            RestoreStyle();
        }

        private void Resolve()
        {
            if (layer == null) layer = FindObjectOfType<OVRPassthroughLayer>();
            if (director == null) director = FindObjectOfType<IntroDirector>();
        }

        private void Update()
        {
            if (director == null) { Resolve(); return; }

            bool active = director.Active;
            if (active && !_wasActive)
            {
                _turnedOff = false;
                SetPassthrough(true);
            }
            _wasActive = active;

            if (!active)
            {
                if (_styled) RestoreStyle();
                if (disableWhenDone && !_turnedOff)
                {
                    // ここで黒が出るが、覆いが既に黒いので体験者には見えない。
                    // 本編は黒背景なので、パススルーを回しっぱなしにする理由が無い（90Hz を守る）。
                    SetPassthrough(false);
                    _turnedOff = true;
                }
                return;
            }

            if (layer == null) return;

            var w = director.Weights;
            float d = Mathf.Clamp01(w.degrade);

            // 色 → コントラスト → 輪郭 の順に効く。d=0 なら全部 0（＝素のパススルー）。
            layer.SetBrightnessContrastSaturation(
                brightness: brightnessAtFull * d,
                contrast: contrastAtFull * d,
                saturation: -d);          // -1 で完全なグレースケール

            float e = Mathf.Clamp01(w.edge);
            layer.edgeRenderingEnabled = e > 0.01f;
            if (e > 0.01f)
            {
                var c = director.EdgeColor;
                layer.edgeColor = new Color(c.r, c.g, c.b, e);
            }
            _styled = true;
        }

        private void RestoreStyle()
        {
            if (layer == null || !_styled) return;
            layer.DisableColorMap();
            layer.edgeRenderingEnabled = false;
            _styled = false;
        }

        private static void SetPassthrough(bool on)
        {
            var m = OVRManager.instance;
            if (m == null) return;
            if (m.isInsightPassthroughEnabled == on) return;
            m.isInsightPassthroughEnabled = on;
        }
    }
}
