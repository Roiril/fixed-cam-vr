#nullable enable
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// DealShuffle（Fisher-Yates 抽出）の EditMode テスト。
    /// AlgoDealer の完全ランダム配札 + BandidoDealLogic の g/l/rest シャッフルの基盤なので、
    /// 「真の全単射シャッフル」であること（off-by-one 退行防止）を決定的に固定する。
    /// 乱数は seeded System.Random を rng デリゲートに注入して決定化する。
    /// </summary>
    public class DealShuffleTests
    {
        private static System.Func<int, int> SeededRng(int seed)
        {
            var rand = new System.Random(seed);
            return m => rand.Next(m);
        }

        [Test]
        public void BuildPermutation_Size24_IsBijection()
        {
            var perm = DealShuffle.BuildPermutation(24, SeededRng(12345));

            Assert.AreEqual(24, perm.Length);
            // 0..23 の順列（重複なし・全要素被覆）
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 24), perm);
        }

        [Test]
        public void BuildPermutation_ManySeeds_AlwaysBijection()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var perm = DealShuffle.BuildPermutation(24, SeededRng(seed));
                CollectionAssert.AreEquivalent(Enumerable.Range(0, 24), perm,
                    $"seed {seed} で全単射でない");
            }
        }

        [Test]
        public void BuildPermutation_ZeroAndOne_EdgeCases()
        {
            Assert.AreEqual(0, DealShuffle.BuildPermutation(0, SeededRng(1)).Length);
            var one = DealShuffle.BuildPermutation(1, SeededRng(1));
            Assert.AreEqual(1, one.Length);
            Assert.AreEqual(0, one[0]);
        }

        [Test]
        public void BuildPermutation_SameSeed_Deterministic()
        {
            var a = DealShuffle.BuildPermutation(30, SeededRng(777));
            var b = DealShuffle.BuildPermutation(30, SeededRng(777));
            CollectionAssert.AreEqual(a, b);
        }

        [Test]
        public void ShuffleInPlace_PreservesMultiset()
        {
            var list = new List<int> { 5, 5, 7, 1, 9, 2, 2, 2, 8 };
            var expected = new List<int>(list);

            DealShuffle.ShuffleInPlace(list, SeededRng(42));

            CollectionAssert.AreEquivalent(expected, list, "マルチセット（重複込みの要素集合）が保存される");
            Assert.AreEqual(expected.Count, list.Count);
        }

        [Test]
        public void ShuffleInPlace_SameSeed_Deterministic()
        {
            var a = new List<string> { "a", "b", "c", "d", "e", "f" };
            var b = new List<string> { "a", "b", "c", "d", "e", "f" };

            DealShuffle.ShuffleInPlace(a, SeededRng(99));
            DealShuffle.ShuffleInPlace(b, SeededRng(99));

            CollectionAssert.AreEqual(a, b);
        }
    }
}
