#nullable enable
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// キーボードから StatusHud を<b>ピン留め</b>する補助コンポーネント（<see cref="StatusHud.SetVisible"/>）。
    /// Editor / Flat シーンでのフォールバック扱い。
    ///
    /// ⚠ 実機の右 B は<b>押しているあいだだけ</b>の表示（<c>StatusHud.SetHeld</c>）で、こちらとは別系統。
    /// 真実源は 1 つ（<see cref="StatusHud.IsVisible"/> ＝ ピン留め or 押下ビューが見えている）。
    /// ピン留めはラン開始で落ちる。
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
            // ⚠⚠ **`UnityEngine.` を省かない。** この asmdef は 2026-09-14 から `FixedCamVr.Input`
            //    （純ロジックの置き場）を参照しており、`FixedCamVr.Diagnostics` の中では素の `Input` が
            //    **そちらの名前空間**へ解決される（`Input.GetKeyDown` が名前空間の中の型として探される）。
            if (UnityEngine.Input.GetKeyDown(keyboardToggleKey))
            {
                // ローカルにコピーを持たず真実源（hud.IsVisible）を反転する。
                // シャドウコピーは OvrControllerBridge 側のトグルと併用した時に desync して
                // 「初回押下が空振り」になる（2026-06-18 の既知バグ類型）。
                hud.SetVisible(!hud.IsVisible);
            }
        }
    }
}
