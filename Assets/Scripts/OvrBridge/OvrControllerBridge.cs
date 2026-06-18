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

        private bool _hudVisible = true;
        private float _gripHold;
        private bool _calibToggleFired;

        private void Start()
        {
            // HUD の初期表示状態に _hudVisible を合わせる。RuntimeDebugHud が既定 OFF
            // （startVisible=false）のとき true 固定のままだと、初回 Y 押下が「既に隠れている
            // HUD を隠す」空振りになり、表示するのに 2 回押す羽目になる（最近の「ボタンが意図と
            // 違う」系の罠と同質）。型は MonoBehaviour で緩く受けたまま安全にキャストして読む。
            if (hud is FixedCamVr.Diagnostics.RuntimeDebugHud rdh) _hudVisible = rdh.IsVisible;
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
                        pick = OVRInput.GetDown(OVRInput.Button.One),       // A (右): レイ先を選択
                        grab = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch) > 0.5f, // 右トリガ: ドラッグ
                        save = OVRInput.GetDown(OVRInput.Button.Three),     // X (左)
                        reset = OVRInput.GetDown(OVRInput.Button.Four),     // Y (左)
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
            // 左コントローラ Y ボタンで HUD 表示トグル
            if (OVRInput.GetDown(hudToggleButton, OVRInput.Controller.LTouch))
            {
                _hudVisible = !_hudVisible;
                if (hud != null)
                    hud.SendMessage("SetVisible", _hudVisible, SendMessageOptions.DontRequireReceiver);
            }
        }
    }
}
