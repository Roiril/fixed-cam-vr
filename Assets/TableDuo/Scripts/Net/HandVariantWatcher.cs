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
                // セッション中に切り替わると条件が壊れるため、tdv_hand で固定された時だけトグルを無効化する。
                // ⚠ LaunchedWithStudyFlags で判定しない（tdv_role だけでも立つため、スクリプト起動で
                //   Y トグルが常時死ぬ・2026-07-11 実害）。
                if (StudyConfig.HandVariantLockedByFlag)
                {
                    Debug.Log("[TableDuo] 手バリアントは tdv_hand で固定中のため切替無効");
                    return;
                }
                StudyConfig.CycleHandVariant();
                Debug.Log($"[TableDuo] 手の見た目を切替 → {StudyConfig.SelectedHandVariant}");
            }
        }
    }
}
