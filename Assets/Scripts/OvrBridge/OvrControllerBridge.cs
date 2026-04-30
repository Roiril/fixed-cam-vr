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
        }
    }
}
