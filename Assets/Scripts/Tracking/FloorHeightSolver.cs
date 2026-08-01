#nullable enable
using System;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 位置合わせのタッチ位置から**床の高さ**（course y=0 のワールド高さ）を解く。
    ///
    /// XZ + yaw を解く <see cref="RigidFit2D"/> とは別の測定。基準点はすべて床のマーカーなので、
    /// タッチ位置の y は「床 + 構えた高さ」であり、構えた高さが既知なら床が出る。
    ///
    /// **中央値を使う**（平均ではなく）。1 点だけ床に着け損ねた登録で、その 1 点に床が引っ張られると
    /// ワイヤーも人形も全部ずれる。中央値なら 3 点中 1 点の失敗は結果に影響しない。
    /// </summary>
    public static class FloorHeightSolver
    {
        /// <summary>
        /// これを超えるばらつきは「床に着けていない点がある」とみなして警告する (m)。
        /// 不合格にはしない — 現地で登録できなくなる方が損（残差ゲートは XZ が担っている）。
        /// </summary>
        public const float SpreadWarnM = 0.06f;

        public readonly struct Result
        {
            /// <summary>解けたか（点が 1 つも無ければ false）。</summary>
            public readonly bool Ok;

            /// <summary>床のワールド高さ (m)。</summary>
            public readonly float FloorY;

            /// <summary>タッチ位置の y のばらつき（最大 − 最小, m）。</summary>
            public readonly float SpreadM;

            public Result(bool ok, float floorY, float spreadM)
            {
                Ok = ok;
                FloorY = floorY;
                SpreadM = spreadM;
            }

            /// <summary>ばらつきが大きすぎる（床に着けていない点がありそう）。</summary>
            public bool Suspicious => Ok && SpreadM > SpreadWarnM;
        }

        /// <param name="sampleWorldY">各基準点をタッチしたときのワールド y（ホールド平均後）。</param>
        /// <param name="touchHeightM">床から何 m の高さに構えたか（<c>layout.regTouchHeightM</c>）。</param>
        public static Result Solve(float[]? sampleWorldY, float touchHeightM)
        {
            if (sampleWorldY == null || sampleWorldY.Length == 0) return new Result(false, 0f, 0f);

            var sorted = new float[sampleWorldY.Length];
            Array.Copy(sampleWorldY, sorted, sampleWorldY.Length);
            Array.Sort(sorted);

            int n = sorted.Length;
            float median = (n & 1) == 1
                ? sorted[n / 2]
                : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);

            return new Result(true, median - touchHeightM, sorted[n - 1] - sorted[0]);
        }
    }
}
