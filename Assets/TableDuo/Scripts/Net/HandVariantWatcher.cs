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
                // 手バリアントは調査条件（tdv_hand でブロックごとに固定・study-design §2）。
                // セッション中に切り替わると条件が壊れるため、調査フラグ起動時はトグルを無効化する
                if (StudyConfig.LaunchedWithStudyFlags)
                {
                    Debug.Log("[TableDuo] 調査セッション中は手バリアント切替を無効化（tdv_hand で固定）");
                    return;
                }
                StudyConfig.CycleHandVariant();
                Debug.Log($"[TableDuo] 手の見た目を切替 → {StudyConfig.SelectedHandVariant}");
            }
        }
    }
}
