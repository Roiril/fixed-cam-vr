#nullable enable
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// キーボードから StatusHud をトグルする補助コンポーネント。
    /// 実機 Quest 3 では OvrControllerBridge 側から <see cref="StatusHud.SetVisible"/> を呼ぶ想定で、
    /// このコンポーネントは Editor / Flat シーンでのフォールバック扱い。
    /// </summary>
    public sealed class HudToggleInput : MonoBehaviour
    {
        [Tooltip("トグル対象の StatusHud。")]
        [SerializeField] private StatusHud? hud;

        [Tooltip("Editor 用フォールバック。実機 Quest 3 では OvrControllerBridge から StatusHud.SetVisible(...) を呼ぶ想定。")]
        [SerializeField] private KeyCode keyboardToggleKey = KeyCode.H;

        private void Update()
        {
            if (hud == null) return;
            if (Input.GetKeyDown(keyboardToggleKey))
            {
                // ローカルにコピーを持たず真実源（hud.IsVisible）を反転する。
                // シャドウコピーは OvrControllerBridge 側のトグルと併用した時に desync して
                // 「初回押下が空振り」になる（2026-06-18 の既知バグ類型）。
                hud.SetVisible(!hud.IsVisible);
            }
        }
    }
}
