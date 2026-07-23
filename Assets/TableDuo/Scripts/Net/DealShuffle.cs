#nullable enable
using System.Collections.Generic;

namespace TableDuoVr.Net
{
    /// <summary>
    /// Fisher-Yates シャッフルの純ロジック（GameObject/UnityEngine 非依存）。
    /// AlgoDealer・BandidoDealer・バケで三重実装だった手書き FY を一本化し、
    /// 「本当に全単射なシャッフル」を EditMode で決定的に固定する（off-by-one 退行防止）。
    ///
    /// 乱数は <c>rng(maxExclusive) → [0, maxExclusive)</c> のデリゲート注入。
    /// 本番は <c>m => UnityEngine.Random.Range(0, m)</c>、テストは seeded System.Random を渡す。
    /// </summary>
    public static class DealShuffle
    {
        /// <summary>
        /// <c>perm[i]</c> = スロット i に置くカードの元 index。恒等列 [0..n) を
        /// Fisher-Yates（i = n-1..1: j = rng(i+1); swap(perm[i], perm[j])）で shuffle した順列を返す。
        /// n=0 は空配列、n=1 は {0}。手書き実装と rng 消費順・意味は同一。
        /// </summary>
        public static int[] BuildPermutation(int n, System.Func<int, int> rng)
        {
            if (rng == null) throw new System.ArgumentNullException(nameof(rng));
            if (n < 0) n = 0;
            var perm = new int[n];
            for (int i = 0; i < n; i++) perm[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng(i + 1);
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            return perm;
        }

        /// <summary>
        /// in-place Fisher-Yates（BandidoDealLogic の g/l/rest シャッフル用）。
        /// 要素のマルチセットを保存し、同一 rng で決定的。
        /// </summary>
        public static void ShuffleInPlace<T>(IList<T> list, System.Func<int, int> rng)
        {
            if (list == null) throw new System.ArgumentNullException(nameof(list));
            if (rng == null) throw new System.ArgumentNullException(nameof(rng));
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
