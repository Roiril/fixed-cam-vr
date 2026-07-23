#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// ReplayCursor.Advance（時刻→フレーム選択・順=前進 O(1)・逆=二分探索・境界）。
    /// times = {100,200,300,400} を基準に固定。
    /// </summary>
    public class ReplayCursorTests
    {
        private static List<long> Times() => new() { 100, 200, 300, 400 };

        [Test]
        public void Advance_BeforeFirst_ReturnsMinusOne()
        {
            int cursor = 0;
            Assert.AreEqual(-1, ReplayCursor.Advance(Times(), ref cursor, 50));
        }

        [Test]
        public void Advance_Empty_ReturnsMinusOne()
        {
            var times = new List<long>();
            int cursor = 0;
            Assert.AreEqual(-1, ReplayCursor.Advance(times, ref cursor, 100));
        }

        [Test]
        public void Advance_ExactMatch()
        {
            int cursor = 0;
            Assert.AreEqual(2, ReplayCursor.Advance(Times(), ref cursor, 300));
        }

        [Test]
        public void Advance_Between_ReturnsFloor()
        {
            int cursor = 0;
            Assert.AreEqual(1, ReplayCursor.Advance(Times(), ref cursor, 250));
        }

        [Test]
        public void Advance_Forward_MovesCursorIncrementally()
        {
            int cursor = 1;
            Assert.AreEqual(3, ReplayCursor.Advance(Times(), ref cursor, 400));
            Assert.AreEqual(3, cursor);
        }

        [Test]
        public void Advance_ReverseSeek_BinarySearch()
        {
            int cursor = 3;
            Assert.AreEqual(0, ReplayCursor.Advance(Times(), ref cursor, 150));
            Assert.AreEqual(0, cursor);
        }

        [Test]
        public void Advance_AtOrAfterLast()
        {
            int cursor = 0;
            Assert.AreEqual(3, ReplayCursor.Advance(Times(), ref cursor, 999));
        }

        [Test]
        public void Advance_CursorClampsIfOutOfRange()
        {
            int cursor = 99; // 範囲外 → cursor>=Count クランプ経路
            Assert.AreEqual(1, ReplayCursor.Advance(Times(), ref cursor, 250));
        }
    }
}
