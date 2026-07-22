#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// バンディド（トンネル札）のリリース時スナップの純計算（シーン・ネットワーク非依存 = EditMode テスト可能）。
    /// カードは 1:2 の縦長（短辺 = 1 セル・長辺 = 2 セル）で、卓上どこでも常に格子へ吸着する（盤外概念なし）。
    /// スナップ内容:
    /// - yaw: 現姿勢の水平 yaw を最寄りの 90° 倍数へ丸める（縦置き 0/180・横置き 90/270）
    /// - 表裏保持: 現姿勢の up が上向きなら表・下向きなら裏のまま平置き化（傾いたままは置けない）
    /// - 格子スナップ（bandy 中心 = 格子原点。u = pos - origin）:
    ///     縦置き（長辺 Z）: ux/uz を最寄りの k·p へ丸める
    ///     横置き（長辺 X）: ux/uz を最寄りの (k+0.5)·p へ丸める
    ///   1×2 セルのカードはこの規則で縦横どちらでもセル境界が一致し、トンネルの道が繋がる
    ///   （bandy 自身＝縦置き原点がこの格子の定義）。
    /// Y（接地高さ・積み上げ）は bounds・他カード依存のため呼び出し側（BandidoCardSnap）が確定する。
    /// </summary>
    public static class BandidoSnapLogic
    {
        /// <summary>格子定義（ワールド XZ・bandy 中心基準・正方ピッチ p = カード短辺）。</summary>
        public struct Config
        {
            public float gridOriginX;
            public float gridOriginZ;
            public float cellPitch;
        }

        public struct Result
        {
            public float x;
            public float z;
            /// <summary>90° 倍数に丸めた yaw（[0,360)）。0/180 = 縦置き・90/270 = 横置き。</summary>
            public float yawDeg;
            /// <summary>表向き（up が上）なら true。裏向きなら false（呼び出し側が長軸まわり 180° ロールで平置き）。</summary>
            public bool faceUp;
        }

        /// <summary>リリース姿勢から格子スナップ先（XZ・yaw・表裏）を決める。</summary>
        public static Result SnapRelease(Vector3 pos, Quaternion rot, in Config c)
        {
            float yaw = Mathf.Repeat(Mathf.Round(GeisterSnapLogic.ExtractYawDeg(rot) / 90f) * 90f, 360f);
            bool vertical = Mathf.Approximately(Mathf.Repeat(yaw, 180f), 0f); // 0/180 = 縦置き（長辺 Z）
            bool faceUp = Vector3.Dot(rot * Vector3.up, Vector3.up) >= 0f;

            float ux = pos.x - c.gridOriginX;
            float uz = pos.z - c.gridOriginZ;
            float sx, sz;
            if (vertical)
            {
                sx = Mathf.Round(ux / c.cellPitch) * c.cellPitch;
                sz = Mathf.Round(uz / c.cellPitch) * c.cellPitch;
            }
            else
            {
                // 半セルオフセット（横置きは長軸が 2 セルにまたがるので中心が半格子上に来る）
                sx = (Mathf.Round(ux / c.cellPitch - 0.5f) + 0.5f) * c.cellPitch;
                sz = (Mathf.Round(uz / c.cellPitch - 0.5f) + 0.5f) * c.cellPitch;
            }

            return new Result
            {
                x = c.gridOriginX + sx,
                z = c.gridOriginZ + sz,
                yawDeg = yaw,
                faceUp = faceUp,
            };
        }
    }
}
