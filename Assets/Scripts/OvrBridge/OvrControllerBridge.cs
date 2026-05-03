#nullable enable
using FixedCamVr.Streaming;
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

        private bool _hudVisible = true;

        private void Update()
        {
            if (registry != null && registry.Count > 0)
            {
                if (OVRInput.GetDown(nextButton)) registry.Next();
                if (OVRInput.GetDown(prevButton)) registry.Prev();
            }
            if (screenAnchor != null)
            {
                if (OVRInput.GetDown(anchorToggleButton)) screenAnchor.Toggle();
            }
            // 左コントローラ Y ボタンで HUD 表示トグル（要実機確認）
            if (OVRInput.GetDown(hudToggleButton))
            {
                _hudVisible = !_hudVisible;
                if (hud != null)
                    hud.SendMessage("SetVisible", _hudVisible, SendMessageOptions.DontRequireReceiver);
            }
        }
    }
}
