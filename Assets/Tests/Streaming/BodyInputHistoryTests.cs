#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 映像遅延の補償（過去の身体入力を読む）を固定する。
    ///
    /// スクリーンに映っているのは撮影から 100〜200ms 前の姿なので、「いま」の体験者の位置に人形を描くと
    /// 歩いているあいだずっと先行してずれる。ここが壊れると follow の人形が体験者を追い越す / 遅れる。
    /// </summary>
    public sealed class BodyInputHistoryTests
    {
        private static ShowBodyInput Body(float x, float yaw = 0f, bool hands = true)
            => new ShowBodyInput(true, new Vector3(x, 1.6f, 0f), yaw,
                                 hands, new Vector3(x - 0.2f, 1.0f, 0f),
                                 hands, new Vector3(x + 0.2f, 1.0f, 0f));

        [Test]
        public void Empty_ReturnsNone()
        {
            var h = new BodyInputHistory();
            Assert.IsFalse(h.Sample(0f).HasHead);
            Assert.AreEqual(0, h.Count);
        }

        [Test]
        public void SingleSample_ReturnsItRegardlessOfTime()
        {
            var h = new BodyInputHistory();
            h.Push(10f, Body(1f));
            Assert.AreEqual(1f, h.Sample(0f).HeadPos.x, 1e-4f);
            Assert.AreEqual(1f, h.Sample(99f).HeadPos.x, 1e-4f);
        }

        [Test]
        public void InterpolatesBetweenSamples()
        {
            var h = new BodyInputHistory();
            h.Push(0f, Body(0f));
            h.Push(1f, Body(2f));
            Assert.AreEqual(1f, h.Sample(0.5f).HeadPos.x, 1e-4f, "中点で線形補間");
            Assert.AreEqual(0.5f, h.Sample(0.25f).HeadPos.x, 1e-4f);
        }

        [Test]
        public void ClampsAtBothEnds_DoesNotExtrapolate()
        {
            // 外挿すると、供給が一瞬途切れただけで人形が飛ぶ。端で頭打ちにする。
            var h = new BodyInputHistory();
            h.Push(1f, Body(0f));
            h.Push(2f, Body(1f));
            Assert.AreEqual(0f, h.Sample(-5f).HeadPos.x, 1e-4f);
            Assert.AreEqual(1f, h.Sample(99f).HeadPos.x, 1e-4f);
        }

        [Test]
        public void CompensatesWalkingOffset()
        {
            // **この機能の存在理由**: 1 m/s で歩く体験者を 0.15 秒前の位置で描くと 15cm 後ろになる。
            var h = new BodyInputHistory();
            for (int i = 0; i <= 20; i++) h.Push(i * 0.05f, Body(i * 0.05f));   // 1 m/s
            float now = 1.0f;
            Assert.AreEqual(1.0f, h.Sample(now).HeadPos.x, 1e-3f);
            Assert.AreEqual(0.85f, h.Sample(now - 0.15f).HeadPos.x, 1e-3f);
        }

        [Test]
        public void RingBuffer_KeepsNewestWhenOverflowing()
        {
            var h = new BodyInputHistory();
            int n = BodyInputHistory.Capacity * 2;
            for (int i = 0; i < n; i++) h.Push(i, Body(i));
            Assert.AreEqual(BodyInputHistory.Capacity, h.Count);
            Assert.AreEqual(n - 1, h.LatestTime, 1e-4f);
            Assert.AreEqual(n - 1, h.Sample(n).HeadPos.x, 1e-4f);
            // 溢れて捨てられた古い時刻は、残っている最古で頭打ちになる
            Assert.AreEqual(n - BodyInputHistory.Capacity, h.Sample(0f).HeadPos.x, 1e-4f);
        }

        [Test]
        public void Clear_DropsEverything()
        {
            var h = new BodyInputHistory();
            h.Push(0f, Body(1f));
            h.Clear();
            Assert.AreEqual(0, h.Count);
            Assert.IsFalse(h.Sample(0f).HasHead);
        }

        // ---- 有効フラグの扱い（ここを補間すると腕が飛ぶ）----

        [Test]
        public void HandValidity_IsNotBlended_TakesNearerSample()
        {
            // 手が「取れた / 取れない」の境界を混ぜると、無効な手の座標（0,0,0）へ引っ張られて腕が飛ぶ。
            var h = new BodyInputHistory();
            h.Push(0f, Body(0f, hands: true));
            h.Push(1f, Body(1f, hands: false));

            Assert.IsTrue(h.Sample(0.2f).LeftValid, "手前寄りなら有効側を採る");
            Assert.IsFalse(h.Sample(0.8f).LeftValid, "奥寄りなら無効側を採る");
        }

        [Test]
        public void HandPosition_IsNotDraggedTowardInvalidSample()
        {
            var h = new BodyInputHistory();
            h.Push(0f, Body(0f, hands: true));
            h.Push(1f, new ShowBodyInput(true, new Vector3(1f, 1.6f, 0f), 0f,
                                         false, Vector3.zero, false, Vector3.zero));
            ShowBodyInput s = h.Sample(0.4f);
            Assert.IsTrue(s.LeftValid);
            // 有効側（-0.2）のまま。原点（0,0,0）へ引っ張られていないこと。
            Assert.AreEqual(-0.2f, s.LeftHandPos.x, 1e-4f);
            Assert.AreEqual(1.0f, s.LeftHandPos.y, 1e-4f);
        }

        [Test]
        public void HeadYaw_WrapsTheShortWay()
        {
            // 350° → 10° は「20° 進む」であって「340° 戻る」ではない。
            var h = new BodyInputHistory();
            h.Push(0f, Body(0f, yaw: 350f));
            h.Push(1f, Body(0f, yaw: 10f));
            float yaw = h.Sample(0.5f).HeadYawDeg;
            float delta = Mathf.DeltaAngle(0f, yaw);
            Assert.AreEqual(0f, delta, 1e-3f, $"中点は 0°(=360°) のはず。実測 {yaw}");
        }

        [Test]
        public void HeadPosition_IsInterpolatedInThreeAxes()
        {
            var h = new BodyInputHistory();
            h.Push(0f, new ShowBodyInput(true, new Vector3(0f, 1f, -1f), 0f, false, Vector3.zero, false, Vector3.zero));
            h.Push(1f, new ShowBodyInput(true, new Vector3(2f, 2f, 1f), 0f, false, Vector3.zero, false, Vector3.zero));
            Vector3 p = h.Sample(0.5f).HeadPos;
            Assert.AreEqual(1f, p.x, 1e-4f);
            Assert.AreEqual(1.5f, p.y, 1e-4f);
            Assert.AreEqual(0f, p.z, 1e-4f);
        }
    }
}
