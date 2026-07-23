#nullable enable
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;
using Kind = TableDuoVr.Net.BandidoDealLogic.Kind;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// BandidoDealLogic（バンディド配札の純ロジック）の EditMode テスト。
    /// 「各席 手札 = l×1+g×2 または g×3」は調査条件そのものなので、分類・席分け・手札制約・
    /// 全単射を GameObject 生成なしで固定する。乱数列の bit 一致は元々未固定なので不問
    /// （有効な配札 = 制約充足 + 全単射が保存されることを invariant で担保する）。
    /// 実バケ相当: g24 + l7 = 31 枚（開始札 bandy は配りスロットに含まれない）、手札 6・山札 25。
    /// </summary>
    public class BandidoDealLogicTests
    {
        private static System.Func<int, int> SeededRng(int seed)
        {
            var rand = new System.Random(seed);
            return m => rand.Next(m);
        }

        // 実バケ相当のスロット位置: 6 個ユニーク XZ（手札）+ 25 個同一 XZ（山札スタック・Y のみ差）。
        // 手札の z は distinct にして席分け（前半/後半）を決定的にする。
        private static Vector3[] RealisticSlots()
        {
            var pos = new List<Vector3>();
            for (int k = 0; k < 6; k++) pos.Add(new Vector3(0.1f * (k + 1), 0f, 0.1f * (k + 1))); // 手札 6
            for (int k = 0; k < 25; k++) pos.Add(new Vector3(2f, 0.01f * k, 2f));                 // 山札 25（同 XZ）
            return pos.ToArray();
        }

        // 実バケ相当の Kind 列: g24 + l7。カード index と位置は独立（logic は 2 配列を別概念で扱う）。
        private static Kind[] RealisticKinds(int gCount = 24, int lCount = 7)
        {
            var kinds = new List<Kind>();
            for (int i = 0; i < gCount; i++) kinds.Add(Kind.G);
            for (int i = 0; i < lCount; i++) kinds.Add(Kind.L);
            return kinds.ToArray();
        }

        [Test]
        public void ClassifySlots_UniqueAndSharedXZ_SplitsAndSortsHandByZ()
        {
            // 6 ユニーク XZ（手札）+ 25 同一 XZ（山札）を z がスクランブルされた順で投入
            var pos = new List<Vector3>();
            // 山札を先頭に混ぜる
            for (int k = 0; k < 25; k++) pos.Add(new Vector3(2f, 0.01f * k, 2f));
            // 手札を z 降順（スクランブル）で追加
            float[] handZ = { 0.6f, 0.1f, 0.4f, 0.2f, 0.5f, 0.3f };
            foreach (var z in handZ) pos.Add(new Vector3(z, 0f, z));

            var hand = new List<int>();
            var deck = new List<int>();
            BandidoDealLogic.ClassifySlots(pos, hand, deck);

            Assert.AreEqual(6, hand.Count, "単独 XZ = 手札 6");
            Assert.AreEqual(25, deck.Count, "共有 XZ = 山札 25");
            // 手札は z 昇順にソートされる（入力スクランブルでも）
            for (int i = 0; i + 1 < hand.Count; i++)
                Assert.LessOrEqual(pos[hand[i]].z, pos[hand[i + 1]].z, "手札は z 昇順");
        }

        [Test]
        public void ClassifySlots_Boundary_UnderOneMmShared_OverOneMmSeparate()
        {
            // 0.9mm 差 → 同一扱い（両方 = 山札・共有）
            var same = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.0009f, 0f, 0f) };
            var hs = new List<int>();
            var ds = new List<int>();
            BandidoDealLogic.ClassifySlots(same, hs, ds);
            Assert.AreEqual(0, hs.Count);
            Assert.AreEqual(2, ds.Count, "0.9mm 差は同一 XZ 扱い（共有）");

            // 1.1mm 差 → 別扱い（両方 = 手札・単独）
            var diff = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.0011f, 0f, 0f) };
            hs = new List<int>();
            ds = new List<int>();
            BandidoDealLogic.ClassifySlots(diff, hs, ds);
            Assert.AreEqual(2, hs.Count, "1.1mm 差は別 XZ 扱い（単独）");
            Assert.AreEqual(0, ds.Count);
        }

        [Test]
        public void Assign_ManySeeds_IsBijectionOverAllSlots()
        {
            var pos = RealisticSlots();
            var kinds = RealisticKinds();
            for (int seed = 0; seed < 200; seed++)
            {
                var assign = BandidoDealLogic.Assign(pos, kinds, SeededRng(seed));
                Assert.AreEqual(31, assign.Length);
                CollectionAssert.AreEquivalent(Enumerable.Range(0, 31), assign,
                    $"seed {seed}: assign が 0..30 の順列（全単射・-1 なし）");
            }
        }

        [Test]
        public void Assign_EachSeatHand_HasZeroOrOneL_RestG()
        {
            var pos = RealisticSlots();
            var kinds = RealisticKinds();
            var hand = new List<int>();
            var deck = new List<int>();
            BandidoDealLogic.ClassifySlots(pos, hand, deck); // z 昇順: 前半3=席0・後半3=席1

            for (int seed = 0; seed < 300; seed++)
            {
                var assign = BandidoDealLogic.Assign(pos, kinds, SeededRng(seed));
                for (int seat = 0; seat < 2; seat++)
                {
                    int lCount = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int slot = hand[seat * 3 + k];
                        if (kinds[assign[slot]] == Kind.L) lCount++;
                    }
                    Assert.That(lCount, Is.EqualTo(0).Or.EqualTo(1),
                        $"seed {seed} seat {seat}: 手札の L 枚数は 0 か 1（l×1+g×2 or g×3）");
                }
            }
        }

        [Test]
        public void Assign_DeckSlots_AllFilled()
        {
            var pos = RealisticSlots();
            var kinds = RealisticKinds();
            var hand = new List<int>();
            var deck = new List<int>();
            BandidoDealLogic.ClassifySlots(pos, hand, deck);

            var assign = BandidoDealLogic.Assign(pos, kinds, SeededRng(7));
            Assert.AreEqual(25, deck.Count);
            foreach (var slot in deck)
                Assert.GreaterOrEqual(assign[slot], 0, "山札 25 スロットすべてに割当（-1 が残らない）");
        }

        [Test]
        public void Assign_HandNot6_FallbackStillBijection()
        {
            var kinds = RealisticKinds();

            // 全ユニーク XZ → handSlots=31（≠6）→ フォールバック
            var allUnique = new List<Vector3>();
            for (int k = 0; k < 31; k++) allUnique.Add(new Vector3(0.1f * k, 0f, 0.05f * k));
            var a1 = BandidoDealLogic.Assign(allUnique.ToArray(), kinds, SeededRng(3));
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 31), a1, "全ユニーク: フォールバックでも全単射");

            // 全共有 XZ → handSlots=0（≠6）→ フォールバック
            var allShared = new List<Vector3>();
            for (int k = 0; k < 31; k++) allShared.Add(new Vector3(1f, 0.01f * k, 1f));
            var a2 = BandidoDealLogic.Assign(allShared.ToArray(), kinds, SeededRng(3));
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 31), a2, "全共有: フォールバックでも全単射");
        }

        [Test]
        public void Assign_AllG_EverySeatIsThreeG_AndBijection()
        {
            var pos = RealisticSlots();
            var kinds = RealisticKinds(gCount: 31, lCount: 0); // L 皆無
            var hand = new List<int>();
            var deck = new List<int>();
            BandidoDealLogic.ClassifySlots(pos, hand, deck);

            for (int seed = 0; seed < 100; seed++)
            {
                var assign = BandidoDealLogic.Assign(pos, kinds, SeededRng(seed));
                foreach (var slot in hand)
                    Assert.AreEqual(Kind.G, kinds[assign[slot]], $"seed {seed}: L 皆無なら全席 g×3");
                CollectionAssert.AreEquivalent(Enumerable.Range(0, 31), assign, $"seed {seed}: 全単射");
            }
        }

        [Test]
        public void Assign_SameSeed_Deterministic()
        {
            var pos = RealisticSlots();
            var kinds = RealisticKinds();
            var a = BandidoDealLogic.Assign(pos, kinds, SeededRng(555));
            var b = BandidoDealLogic.Assign(pos, kinds, SeededRng(555));
            CollectionAssert.AreEqual(a, b, "同一シードの rng で 2 回の Assign が完全一致");
        }
    }
}
