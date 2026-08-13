#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 導入を始める合図「体験エリアへ<b>近づいてきた</b>」（<see cref="ApproachLogic"/>）の検証。
    ///
    /// ここで守るのは 2 つで、**どちらか一方だけを満たす実装は現場で壊れる**:
    ///   (A) <b>止まっている人では始まらない</b>（前の体験者が立ったまま・エリア内に置いた HMD・起動直後）
    ///   (B) <b>箱の近くから始める現場でも始まる</b>（2026-08-13 ユーザー指摘
    ///       「開始位置が箱に近かったりするとバグるよね」）。旧実装は「一度 1.35m 以上離れる」
    ///       ことだけを武装の条件にしていたので、そこまで下がれない現場では**自動で二度と始まらなかった**
    /// </summary>
    public sealed class ApproachLogicTests
    {
        private const float NearM = IntroLogic.ApproachNearM;      // 1.0
        private const float HoldSec = IntroLogic.ApproachHoldSec;  // 0.4
        private const float Dt = 0.05f;

        private static ApproachLogic Fresh()
        {
            var l = new ApproachLogic();
            l.Rearm();
            return l;
        }

        /// <summary>その距離に留まったまま時間を進める。発火したら true。</summary>
        private static bool Hold(ApproachLogic l, float outsideM, float sec)
        {
            bool fired = false;
            int n = (int)System.Math.Round(sec / Dt);
            for (int i = 0; i < n; i++) fired |= l.Tick(outsideM, NearM, HoldSec, Dt, valid: true);
            return fired;
        }

        /// <summary><paramref name="fromM"/> から <paramref name="toM"/> まで歩く。発火したら true。</summary>
        private static bool Walk(ApproachLogic l, float fromM, float toM, float sec)
        {
            bool fired = false;
            int n = (int)System.Math.Round(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                float d = UnityEngine.Mathf.Lerp(fromM, toM, (i + 1f) / n);
                fired |= l.Tick(d, NearM, HoldSec, Dt, valid: true);
            }
            return fired;
        }

        // ---- (A) 止まっている人では始まらない -----------------------------------

        [Test]
        public void StandingStillNearTheBox_NeverFires()
        {
            // 前の体験者が箱のそばに立ったまま、スタッフがランをやり直した。
            var l = Fresh();
            Assert.IsFalse(Hold(l, 0.5f, 30f), "止まっているのに始まった（0005 が禁じた形）");
            Assert.IsFalse(l.Armed);
        }

        [Test]
        public void StandingStillInsideTheBox_NeverFires()
        {
            var l = Fresh();
            Assert.IsFalse(Hold(l, 0f, 30f), "エリアの中に置いた HMD で始まった");
        }

        [Test]
        public void HeadJitterDoesNotArmIt()
        {
            // 立っている人の頭は数 cm 揺れる。それを「近づいてきた」と読んではいけない。
            var l = Fresh();
            bool fired = false;
            for (int i = 0; i < 600; i++)
            {
                float d = 0.6f + 0.04f * UnityEngine.Mathf.Sin(i * 0.7f);
                fired |= l.Tick(d, NearM, HoldSec, Dt, valid: true);
            }
            Assert.IsFalse(fired, "頭の揺れで武装した（ApproachDeltaM が小さすぎる）");
        }

        // ---- (B) 箱の近くから始める現場でも始まる -------------------------------

        [Test]
        public void WalkingInFromCloseRange_Fires()
        {
            // ⚠ **これが旧実装で壊れていたケース。** 開始位置が箱から 1.0m しかない現場。
            //    従来の武装距離（1.35m）には一度も届かないので、旧実装は永久に待っていた。
            var l = Fresh();
            Hold(l, 1.0f, 1f);                       // 立って待つ（まだ始まらない）
            Assert.IsFalse(l.Armed, "立っただけで武装した");
            Assert.IsTrue(Walk(l, 1.0f, 0.4f, 2f), "近い所から歩いてきても始まらない");
        }

        [Test]
        public void WalkingInFromFarRange_StillFires()
        {
            // 従来どおりの現場（十分下がれる）。挙動を変えていないこと。
            var l = Fresh();
            Hold(l, 3f, 1f);
            Assert.IsTrue(l.Armed, "十分離れているのに武装していない");
            Assert.IsTrue(Walk(l, 3f, 0.5f, 3f));
        }

        [Test]
        public void PassingByDoesNotFire()
        {
            // 通りすがりでは始まらない（近い帯に留まる秒数が要る）。
            var l = Fresh();
            Hold(l, 3f, 1f);
            Assert.IsFalse(Walk(l, 3f, 0.5f, HoldSec * 0.5f), "通り抜けただけで始まった");
        }

        // ---- 不連続ガード -------------------------------------------------------

        [Test]
        public void ATrackingJumpDoesNotCountAsApproaching()
        {
            // ⚠ トラッキングの立ち上がり・recenter・HMD 着脱で距離は飛ぶ。
            //    飛びをそのまま「近づいてきた」に読むと、被った瞬間に演出が走り出す。
            var l = Fresh();
            Hold(l, 0.5f, 1f);
            // 1 フレームで 3m 先へ飛び、次のフレームで戻る（= 見かけ上 2.5m 近づいた）。
            l.Tick(3.5f, NearM, HoldSec, Dt, valid: true);
            bool fired = l.Tick(0.5f, NearM, HoldSec, Dt, valid: true);
            Assert.IsFalse(fired, "軌跡の飛びで発火した");
            Assert.IsFalse(l.Armed, "軌跡の飛びで武装した");
        }

        [Test]
        public void ALongFrameGapResetsTheObservation()
        {
            var l = Fresh();
            Hold(l, 3f, 1f);
            Assert.IsTrue(l.Armed);
            // アプリ復帰 / HMD 着脱。
            l.Tick(3f, NearM, HoldSec, ApproachLogic.MaxContinuousDtSec + 0.5f, valid: true);
            Assert.IsFalse(l.Armed, "dt が飛んだのに武装が残っている");
        }

        [Test]
        public void SettleWindowIgnoresTheFirstFrames()
        {
            // 武装直後は値が立ち上がる途中。ここで発火すると「被った瞬間に始まる」になる。
            var l = Fresh();
            Assert.IsFalse(l.Tick(0.1f, NearM, HoldSec, Dt, valid: true));
            Assert.IsFalse(Hold(l, 0.1f, ApproachLogic.SettleSec * 0.8f));
        }

        [Test]
        public void InvalidDistanceDisarmsIt()
        {
            // 未登録・layout 未着。**復帰した瞬間に古い滞在で発火させない**。
            var l = Fresh();
            Hold(l, 3f, 1f);
            Assert.IsTrue(l.Armed);
            l.Tick(0f, NearM, HoldSec, Dt, valid: false);
            Assert.IsFalse(l.Armed);
            Assert.IsFalse(Hold(l, 0.2f, HoldSec + 0.2f), "判定不能から戻った直後に発火した");
        }

        [Test]
        public void RearmForgetsEverything()
        {
            // 体験者交代。前の人の観測が 1 つも残らないこと。
            var l = Fresh();
            Hold(l, 3f, 1f);
            Assert.IsTrue(l.Armed);
            l.Rearm();
            Assert.IsFalse(l.Armed);
            Assert.IsFalse(Hold(l, 0.5f, 10f), "リセット後に前の体験者の観測で始まった");
        }
    }
}
