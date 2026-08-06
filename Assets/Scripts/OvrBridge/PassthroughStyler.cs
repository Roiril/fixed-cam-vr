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

        [Tooltip("パススルーの状態をテレメトリへ逆流させる先。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("格下げが最大のときの明るさ。わずかに落とす（-1..0）。")]
        [SerializeField, Range(-1f, 0f)] private float brightnessAtFull = -0.15f;

        [Tooltip("格下げが最大のときのコントラスト（0..1）。")]
        [SerializeField, Range(0f, 1f)] private float contrastAtFull = 0.35f;

        [Tooltip("演出が終わったらパススルーを切るか。切ると数百 ms の黒が出るが、その時点で覆いが黒いので見えない。")]
        [SerializeField] private bool disableWhenDone = true;

        [Tooltip("終幕（2D スクリーン → パススルー）。導入と同じ重みの語彙を出すので、走っている方を読む。")]
        [SerializeField] private OutroDirector? outro;

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
            if (outro == null) outro = FindObjectOfType<OutroDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();

            // 終幕は「パススルーが実際に出た」のを待ってから枠を開ける（黒いまま開いても現実が見えない）。
            // Streaming asmdef は OVR を参照しない規約なので、判定はこちら側から差し込む。
            if (outro != null) outro.PassthroughReadyProvider = () => ReadPassthroughState() == 1;

            // テレメトリは Streaming asmdef 越しにしかパススルーを見られない（OVR を参照しない規約）。
            // UserPresentProvider と同じ形で、OVR を知っているこちら側から読み口を差し込む。
            if (showControl != null) showControl.PassthroughStateProvider = ReadPassthroughState;
        }

        /// <summary>
        /// パススルーが<b>アプリから実際に有効化できているか</b>を返す（-1=判定不能 / 0=無効 / 1=有効）。
        /// <b>観測専用</b> — ここで状態を書き換えない。
        ///
        /// 1 の条件は「<c>OVRManager</c> が有効を報告し、かつ <see cref="OVRPassthroughLayer"/> がシーンに居る」。
        /// レイヤが無ければ見た目を当てる先が無く、画には何も出ない（＝無効と同じ）。
        /// </summary>
        private int ReadPassthroughState()
        {
            var m = OVRManager.instance;
            if (m == null) return -1;
            return m.isInsightPassthroughEnabled && layer != null ? 1 : 0;
        }

        private void Update()
        {
            if (director == null && outro == null) { Resolve(); return; }

            // 導入と終幕は同じ重みの語彙（IntroWeights）を出す。走っている方を読む。
            // 両方が同時に走ることは無い（導入は Intro 相・終幕は Finished 相）が、
            // 万一重なったら終幕を優先する（後から始まった方が現在の見えを持っている）。
            bool outroActive = outro != null && outro.Active;
            bool introActive = director != null && director.Active;
            bool active = introActive || outroActive;

            if (active && !_wasActive)
            {
                _turnedOff = false;
                SetPassthrough(true);
            }
            _wasActive = active;

            if (!active)
            {
                if (_styled) RestoreStyle();
                // ⚠ **終幕が終わった後は切らない。** 終幕は素のパススルーで体験を閉じるので、
                // ここで切ると最後に真っ黒になって「現実へ戻った」が台無しになる。
                bool outroDone = outro != null && outro.Stage == OutroStage.Done;
                if (disableWhenDone && !_turnedOff && !outroDone)
                {
                    // ここで黒が出るが、覆いが既に黒いので体験者には見えない。
                    // 本編は黒背景なので、パススルーを回しっぱなしにする理由が無い（90Hz を守る）。
                    SetPassthrough(false);
                    _turnedOff = true;
                }
                return;
            }

            if (layer == null) return;

            var w = outroActive ? outro!.Weights : director!.Weights;
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
                // 輪郭の色は導入の設定を流用する（同じ部屋の輪郭なので、終幕で別の色にする理由が無い）。
                // 終幕だけが配線された構成でも落ちないよう既定を持つ。
                Color c = director != null ? director.EdgeColor : Color.white;
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
