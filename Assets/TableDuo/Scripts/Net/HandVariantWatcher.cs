#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 左コントローラ Y ボタン（<c>Button.Two</c> / LTouch）タップで手の見た目を巡回切替
    /// （Default→Realistic→Robot→Default）。気軽な実機切替用。
    ///
    /// コントローラ専用入力なのでハンドトラッキング中は発火しない（調査本番のジェスチャーを汚さない）。
    /// 調査は起動フラグ tdv_hand で固定するのが基本で、これは設営・お試し時の便宜。
    /// 実際のメッシュ再構築は <see cref="StudyConfig.HandVariantChanged"/> 購読側（ローカル手 / リモート手）が行う。
    /// </summary>
    public sealed class HandVariantWatcher : MonoBehaviour
    {
        private void Update()
        {
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            {
                StudyConfig.CycleHandVariant();
                Debug.Log($"[TableDuo] 手の見た目を切替 → {StudyConfig.SelectedHandVariant}");
            }
        }
    }
}
