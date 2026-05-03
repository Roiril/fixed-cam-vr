#nullable enable
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// キーボードから RuntimeDebugHud をトグルする補助コンポーネント。
    /// 実機 Quest 3 では OvrControllerBridge 側から <see cref="RuntimeDebugHud.SetVisible"/> を呼ぶ想定で、
    /// このコンポーネントは Editor / Flat シーンでのフォールバック扱い。
    /// </summary>
    public sealed class HudToggleInput : MonoBehaviour
    {
        [Tooltip("トグル対象の RuntimeDebugHud。")]
        [SerializeField] private RuntimeDebugHud? hud;

        [Tooltip("Editor 用フォールバック。実機 Quest 3 では OvrControllerBridge から RuntimeDebugHud.SetVisible(...) を呼ぶ想定。")]
        [SerializeField] private KeyCode keyboardToggleKey = KeyCode.H;

        private bool _visible = true;

        private void Start()
        {
            if (hud != null) _visible = hud.IsVisible;
        }

        private void Update()
        {
            if (hud == null) return;
            if (Input.GetKeyDown(keyboardToggleKey))
            {
                _visible = !_visible;
                hud.SetVisible(_visible);
            }
        }
    }
}
