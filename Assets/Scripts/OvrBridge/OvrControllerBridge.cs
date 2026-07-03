#nullable enable
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// asmdef を持たないため Assembly-CSharp に入る。Meta XR SDK の OVRInput にアクセスできる
    /// 唯一の場所として、コントローラ入力を Streaming アセンブリのコンポーネントに転送する橋渡し。
    /// 配置先は [Streaming] GameObject 等。
    /// </summary>
    public sealed class OvrControllerBridge : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private ScreenAnchor? screenAnchor;

        [Tooltip("演出 cue を発火する ShowControlClient（Screen 上）。未割当なら Start で自動取得。")]
        [SerializeField] private ShowControlClient? showControl;

        [Header("Mappings")]
        [SerializeField] private OVRInput.Button nextButton = OVRInput.Button.One;        // A (右)
        [SerializeField] private OVRInput.Button prevButton = OVRInput.Button.Two;        // B (右)
        [SerializeField] private OVRInput.Button anchorToggleButton = OVRInput.Button.Three; // X (左)
        [SerializeField] private OVRInput.Button hudToggleButton = OVRInput.Button.Four;     // Y (左)

        [Header("HUD")]
        [Tooltip("RuntimeDebugHud をアサイン。Y ボタン押下時に SetVisible(bool) を SendMessage で呼ぶ。" +
                 " Diagnostics 名前空間に直接依存しないため MonoBehaviour で受ける。")]
        [SerializeField] private MonoBehaviour? hud = null;

        [Header("Zone calibration")]
        [Tooltip("ZoneCalibrator（[Tracker] 上）。両グリップ 3 秒長押しで校正モード切替、" +
                 "校正中は通常のボタン操作を抑止して入力（レイ選択・ドラッグ・サイズ）を転送する。")]
        [SerializeField] private ZoneCalibrator? zoneCalibrator;

        [Tooltip("校正モード切替に必要な両グリップの長押し秒数。")]
        [SerializeField, Min(0.2f)] private float calibToggleHoldSec = 3.0f;

        // 片手グリップの『単押し』と判定する最大長さ(秒)。これ以下の片手タップで今見ているカメラの
        // 演出をトグル（両手押し / 長押しは校正側なので cue は発火しない）。
        // ※ SerializeField にすると既存シーンの YAML に未記載で 0 と読まれ判定が壊れる
        //   （unity-prefab-fields の罠）。調整不要なので const 固定。
        private const float GripTapMaxSec = 0.4f;

        // HUD の型付き参照（Start で hud からキャスト）。トグルは真実源 IsVisible を反転する。
        private FixedCamVr.Diagnostics.RuntimeDebugHud? _hudTyped;
        private float _gripHold;
        private bool _calibToggleFired;
        // グリップ単押し検出（release ベース。両手 or 長押しは校正なので除外）。
        private bool _gripPressActive;
        private float _gripPressStart;
        private bool _gripPressBoth;

        private void Start()
        {
            // 型付きで保持し、トグル時に真実源（IsVisible）を毎回読む。ローカル bool の
            // シャドウコピーは HudToggleInput（H キー）併用時に desync して「初回押下が空振り」
            // になる（2026-06-18 の既知バグ類型）。
            _hudTyped = hud as FixedCamVr.Diagnostics.RuntimeDebugHud;
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
        }

        private void Update()
        {
            // 両グリップ長押しで校正モード切替（押しっぱなしで連続トグルしない）
            if (zoneCalibrator != null)
            {
                bool bothGrips =
                    OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch) &&
                    OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
                if (bothGrips)
                {
                    _gripHold += Time.deltaTime;
                    if (!_calibToggleFired && _gripHold >= calibToggleHoldSec)
                    {
                        _calibToggleFired = true;
                        zoneCalibrator.Toggle();
                    }
                }
                else
                {
                    _gripHold = 0f;
                    _calibToggleFired = false;
                }

                // 校正モード中は通常マッピング（カメラ切替/HUD 等）を抑止して入力を転送
                if (zoneCalibrator.IsActive)
                {
                    zoneCalibrator.Feed(new ZoneCalibrator.CalibInput
                    {
                        size = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch),
                        rotate = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x, // 左スティック横: レイアウト回転
                        // Button.One/Three/Four はコントローラ未指定だと両手から拾う
                        //（One=A|X 等）ため、必ず RTouch/LTouch を明示する（通常マッピングと同じ規約）。
                        pick = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch),   // A (右): レイ先を選択
                        grab = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch) > 0.5f, // 右トリガ: ドラッグ
                        save = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch), // X (左)
                        reset = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch), // Y (左)
                    });
                    return;
                }
            }

            // カメラ切替は右コントローラ限定で読む。Button.One/Two はコントローラ未指定だと
            // A(右)|X(左) / B(右)|Y(左) を両手から拾うため、左の X/Y までカメラ切替に化けていた。
            // RTouch を明示して A=Next / B=Prev に限定 → 左の X/Y は本来の anchor/HUD だけになる。
            if (registry != null && registry.Count > 0)
            {
                if (OVRInput.GetDown(nextButton, OVRInput.Controller.RTouch)) registry.Next();
                if (OVRInput.GetDown(prevButton, OVRInput.Controller.RTouch)) registry.Prev();
            }
            if (screenAnchor != null)
            {
                if (OVRInput.GetDown(anchorToggleButton, OVRInput.Controller.LTouch)) screenAnchor.Toggle();
            }
            // 左コントローラ Y ボタンで HUD 表示トグル（真実源 IsVisible の反転）
            if (OVRInput.GetDown(hudToggleButton, OVRInput.Controller.LTouch))
            {
                if (_hudTyped != null)
                    _hudTyped.SetVisible(!_hudTyped.IsVisible);
                else if (hud != null) // RuntimeDebugHud 以外は状態が読めないので非表示のみ
                    hud.SendMessage("SetVisible", false, SendMessageOptions.DontRequireReceiver);
            }

            // 片手グリップの「単押し」で、今見ているカメラの演出を ON/OFF（トグル）。
            // 両手グリップ長押し（校正）と衝突しないよう release ベースで判定する：
            // 押下中に両手になった or 規定時間を超えた押下は cue を発火しない。
            bool lGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            bool rGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            if (lGrip || rGrip)
            {
                if (!_gripPressActive)
                {
                    _gripPressActive = true;
                    _gripPressStart = Time.unscaledTime;
                    _gripPressBoth = lGrip && rGrip;
                }
                else if (lGrip && rGrip)
                {
                    _gripPressBoth = true; // 途中で両手になったら校正ジェスチャ扱い
                }
            }
            else if (_gripPressActive)
            {
                _gripPressActive = false;
                if (!_gripPressBoth && (Time.unscaledTime - _gripPressStart) < GripTapMaxSec)
                    showControl?.ToggleActiveCameraCue();
            }
        }
    }
}
