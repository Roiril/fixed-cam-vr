#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ピンチ掴みの「スタック最上段優先」アシストの純計算（シーン非依存 = EditMode テスト可能）。
    /// 半径内最寄りの候補（bestIndex）と同じ縦スタック（XZ 水平距離が <see cref="StackXZRadius"/> 以内）の中で
    /// Y が最大のものへ差し替える。積んだ山札（バンディド・アルゴ）で「一番上を取る」を成立させる。
    ///
    /// 15mm 許容の根拠: バンディド山札・アルゴ山札は同一 XZ に真上へ積む（水平ズレ ≈0）ので確実にグループ化され、
    /// ガイスター駒（65mm ピッチ）・海底探検チップ（33mm 間隔）は 15mm を超えて隣接するため巻き込まれず挙動不変。
    /// </summary>
    public static class StackTopPickLogic
    {
        /// <summary>同一縦スタックとみなす XZ 水平距離のしきい値（m）。</summary>
        public const float StackXZRadius = 0.015f;

        /// <summary>
        /// 候補群の XZ 最寄り（bestIndex）と同じ縦スタックのうち Y が最大の index を返す。
        /// bestIndex が範囲外なら素通し。同 Y は先着（低 index）維持 = 決定的。
        /// </summary>
        public static int PickStackTop(IReadOnlyList<Vector3> positions, int bestIndex)
        {
            if (positions == null || bestIndex < 0 || bestIndex >= positions.Count) return bestIndex;
            Vector3 anchor = positions[bestIndex];
            float rSq = StackXZRadius * StackXZRadius;
            int top = bestIndex;
            float bestY = positions[bestIndex].y;
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                float dx = p.x - anchor.x;
                float dz = p.z - anchor.z;
                if (dx * dx + dz * dz > rSq) continue; // 別スタック
                if (p.y > bestY)
                {
                    bestY = p.y;
                    top = i;
                }
            }
            return top;
        }
    }
}
