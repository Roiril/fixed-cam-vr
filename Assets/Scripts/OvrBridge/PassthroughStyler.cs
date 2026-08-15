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

        // ⚠⚠ **終幕はここから外した**（2026-08-15・canon/LEDGER.md 0048）。
        //    終幕はパススルーへ戻さなくなった（「背景が黒いまま」）ので、読むと真逆のことをする —
        //    終幕の頭で現実が立ち上がり、Done で切らない分岐が残って最後まで現実が見えたままになる。
        //    連鎖して OutroDirector.PassthroughReadyProvider / OutroLogic.WarmMaxSec も消えている。

        private bool _styled;          // 一度でも style を当てたか（戻す必要があるか）
        private bool _wasActive;
        private bool _turnedOff;

        // --- 位置合わせ中に現実を開ける（カメラ背景の alpha）---
        private Camera[] _rigCameras = System.Array.Empty<Camera>();
        private Color[] _rigClearColors = System.Array.Empty<Color>();
        private bool _backgroundOpened;

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
            // 自分が当てた style と背景 alpha を残して去らない
            // （alpha 0 のまま去ると、次に覆いが走る段で枠の外を黒く閉じられなくなる）。
            RestoreStyle();
            if (_backgroundOpened) SetBackgroundOpen(false);
        }

        private void Resolve()
        {
            if (layer == null) layer = FindObjectOfType<OVRPassthroughLayer>();
            if (director == null) director = FindObjectOfType<IntroDirector>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();

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
            // ⚠ **位置合わせ中は、演出より先に現実を開ける。**
            // Underlay のパススルーは「アプリが alpha 0 を書いた画素」にしか出ないが、
            // alpha 0 を書く面は覆い（IntroVeil）だけで、しかも覆いは演出専用。つまり
            // **演出が走っていない全期間、現実は原理的に 1 画素も見えなかった**
            // （2026-08-09 ユーザー報告「トリガーを長押ししてもパススルーは何も変わらない」）。
            //
            // ここで覆いを流用しない理由: 覆いは queue 4900 の `Blend Zero SrcAlpha` なので、
            // 全開で走らせると**合わせる対象である登録ワイヤーと文字（queue 3000）を黒へ潰す**。
            // 現実を出したいだけなら背景の alpha を 0 にすれば足り、VR 側の面は普通に重なる。
            //
            // ⚠ この alpha 0 は**位置合わせの間だけ**。焼き込んだり常時にしたりしてはいけない
            // （乗算ブレンドの覆いは alpha を 0 から 1 へ戻せないので、枠の外を黒く閉じる演出が
            //  原理的に起こらなくなる — rules/meta-xr.md の 2026-07-31 実害）。
            bool registering = showControl != null && showControl.CourseRegistrationActive;
            if (registering != _backgroundOpened) SetBackgroundOpen(registering);
            if (registering)
            {
                _turnedOff = false;
                SetPassthrough(true);
                if (_styled) RestoreStyle();   // 作業中は素の現実（格下げの色を残さない）
                _wasActive = false;            // 抜けたら演出側が改めて立ち上げ直す
                return;
            }

            if (director == null) { Resolve(); return; }

            // 読むのは導入だけ（終幕はパススルーを使わない — 上の注記）。
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
                // ⚠ **終幕でも切ったままにする**（2026-08-15）。終幕は黒のまま装置が力尽きる演出なので、
                // ここで現実が戻ると「背景が黒いまま」が壊れる。旧実装は `OutroStage.Done` を
                // 見て切らない分岐を持っていた（パススルーで体験を閉じていたころの名残）。
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
                layer.edgeColor = new Color(director.EdgeColor.r, director.EdgeColor.g,
                                            director.EdgeColor.b, e);
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

        /// <summary>
        /// OVRCameraRig の全カメラの背景 alpha を開ける / 閉じる。
        /// <b>元の色は必ず覚えてから書き換える</b>（戻すときに定数 (0,0,0,1) を当てると、
        /// 現場で背景色を変えていた場合に黙って上書きしてしまう）。
        /// </summary>
        private void SetBackgroundOpen(bool open)
        {
            if (open)
            {
                var rig = FindObjectOfType<OVRCameraRig>();
                Camera[] cams = rig != null
                    ? rig.GetComponentsInChildren<Camera>(includeInactive: true)
                    : System.Array.Empty<Camera>();
                _rigCameras = cams;
                _rigClearColors = new Color[cams.Length];
                for (int i = 0; i < cams.Length; i++)
                {
                    _rigClearColors[i] = cams[i].backgroundColor;
                    Color c = cams[i].backgroundColor;
                    cams[i].backgroundColor = new Color(c.r, c.g, c.b, 0f);
                }
                if (cams.Length == 0)
                    Debug.LogWarning("[Passthrough] OVRCameraRig のカメラが見つかりません — " +
                                     "位置合わせ中も現実が出ません");
                _backgroundOpened = true;
                return;
            }

            for (int i = 0; i < _rigCameras.Length; i++)
            {
                if (_rigCameras[i] == null) continue;
                _rigCameras[i].backgroundColor = _rigClearColors[i];
            }
            _rigCameras = System.Array.Empty<Camera>();
            _rigClearColors = System.Array.Empty<Color>();
            _backgroundOpened = false;
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
