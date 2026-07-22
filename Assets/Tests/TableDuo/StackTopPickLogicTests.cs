#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// StackTopPickLogic（ピンチ掴みのスタック最上段優先の純計算）の EditMode テスト。
    /// </summary>
    public class StackTopPickLogicTests
    {
        [Test]
        public void PickStackTop_SameStack_PicksHighestY()
        {
            // 同一 XZ に 3 枚積み（Y のみ異なる）。最寄り = 一番下(index0) でも最上段(index2)を返すべき
            var pos = new List<Vector3>
            {
                new Vector3(0.4f, 0.750f, -0.2f),
                new Vector3(0.4f, 0.752f, -0.2f),
                new Vector3(0.4f, 0.754f, -0.2f),
            };

            int top = StackTopPickLogic.PickStackTop(pos, bestIndex: 0);

            Assert.AreEqual(2, top);
        }

        [Test]
        public void PickStackTop_WithinRadius_GroupsAndPicksTop()
        {
            // XZ 10mm ズレ（< 15mm）＝同一スタック扱い。上の駒(index1)を返す
            var pos = new List<Vector3>
            {
                new Vector3(0.4f, 0.750f, -0.2f),
                new Vector3(0.410f, 0.756f, -0.2f), // XZ 10mm 離れ・Y 上
            };

            int top = StackTopPickLogic.PickStackTop(pos, bestIndex: 0);

            Assert.AreEqual(1, top);
        }

        [Test]
        public void PickStackTop_BeyondRadius_NotGrouped()
        {
            // XZ 20mm ズレ（> 15mm）＝別スタック。Y が高くても最寄り(index0)のまま
            var pos = new List<Vector3>
            {
                new Vector3(0.4f, 0.750f, -0.2f),
                new Vector3(0.420f, 0.760f, -0.2f), // XZ 20mm 離れ
            };

            int top = StackTopPickLogic.PickStackTop(pos, bestIndex: 0);

            Assert.AreEqual(0, top, "15mm を超える隣接はグループ化しない（ガイスター駒・海底探検チップは不変）");
        }

        [Test]
        public void PickStackTop_SingleCandidate_ReturnsUnchanged()
        {
            var pos = new List<Vector3> { new Vector3(0.4f, 0.75f, -0.2f) };

            int top = StackTopPickLogic.PickStackTop(pos, bestIndex: 0);

            Assert.AreEqual(0, top);
        }

        [Test]
        public void PickStackTop_EqualYWithinStack_KeepsLowestIndexDeterministic()
        {
            // 同一 XZ・同一 Y は先着（低 index）維持 = 決定的
            var pos = new List<Vector3>
            {
                new Vector3(0.4f, 0.750f, -0.2f),
                new Vector3(0.4f, 0.750f, -0.2f),
            };

            int top = StackTopPickLogic.PickStackTop(pos, bestIndex: 1);

            Assert.AreEqual(1, top, "厳密に上のものだけ差し替え、同 Y は現状(bestIndex)維持");
        }

        [Test]
        public void PickStackTop_BestIndexOutOfRange_ReturnsAsIs()
        {
            var pos = new List<Vector3> { new Vector3(0.4f, 0.75f, -0.2f) };

            Assert.AreEqual(-1, StackTopPickLogic.PickStackTop(pos, bestIndex: -1));
            Assert.AreEqual(5, StackTopPickLogic.PickStackTop(pos, bestIndex: 5));
        }
    }
}
