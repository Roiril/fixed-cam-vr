#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>導入演出が「体験者が入ってきた」で始まることを固定する。</b>
    ///
    /// 2026-08-09 の実害: 円の判定が「いま中に 0.5 秒居る」という<b>状態</b>だったので、
    /// 起動直後に条件がたまたま揃うと演出が即座に走り出した（ユーザー報告
    /// 「体験者の位置を基準にトリガーしてほしいが、今はすぐに起動してしまう」）。
    /// </summary>
    public sealed class StartSpotLogicTests
    {
        private const float Dt = 1f / 72f;
        private const float R = 0.35f;
        private const float Hold = 0.5f;

        /// <summary>円の外の待機位置（半径 + 余白より確実に外）。</summary>
        private const float OutX = 1.0f;

        /// <summary>武装直後の落ち着き待ちを終えて、円の外に立っている状態。</summary>
        private static StartSpotLogic Waiting()
        {
            var s = new StartSpotLogic();
            for (float t = 0f; t < StartSpotLogic.SettleSec + 4f * Dt; t += Dt)
                s.Tick(OutX, 0f, 0f, 0f, R, Hold, Dt);
            return s;
        }

        /// <summary>その場に留まる。</summary>
        private static bool Stay(StartSpotLogic s, float x, float z, float sec)
        {
            bool fired = false;
            for (float t = 0f; t < sec; t += Dt) fired = s.Tick(x, z, 0f, 0f, R, Hold, Dt);
            return fired;
        }

        /// <summary>
        /// (fromX, fromZ) から (toX, toZ) まで<b>歩いて</b>移動する。1 フレームの移動は
        /// <see cref="StartSpotLogic.MaxStepM"/> より十分小さくする — テレポートさせると
        /// 軌跡が切れて（それが正しい挙動）「入ってきた」にならない。
        /// </summary>
        private static bool Walk(StartSpotLogic s, float fromX, float fromZ, float toX, float toZ,
                                 float speedMps = 1.0f)
        {
            float dx = toX - fromX, dz = toZ - fromZ;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(dx * dx + dz * dz) / (speedMps * Dt)));
            bool fired = false;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                fired = s.Tick(fromX + dx * t, fromZ + dz * t, 0f, 0f, R, Hold, Dt);
            }
            return fired;
        }

        [Test]
        public void StandingInsideFromTheStart_NeverFires()
        {
            // **これが実害そのもの**。起動時から円の中に居る（HMD を置いてある / 前の体験者が
            // 立ったままリセットされた）だけで始まってはいけない。
            var s = new StartSpotLogic();
            Assert.That(Stay(s, 0f, 0f, 10f), Is.False,
                "円の中に居続けただけで導入が始まった（外から入ってきた事象を見ていない）");
        }

        [Test]
        public void WalkingIn_Fires()
        {
            var s = Waiting();
            // 1 m/s で歩いて入ると、円（半径 0.35m）の中に居るのは到着までに 0.35 秒。
            Assert.That(Walk(s, OutX, 0f, 0f, 0f), Is.False, "入った瞬間に成立した（滞在を見ていない）");
            Assert.That(Stay(s, 0f, 0f, 0.1f), Is.False, "滞在が足りないのに成立した");
            Assert.That(Stay(s, 0f, 0f, 0.2f), Is.True, "歩いて入って留まったのに成立しない");
        }

        [Test]
        public void PassingThrough_DoesNotFire()
        {
            var s = Waiting();
            // 円を突っ切って反対側へ抜ける（速いので滞在が積み上がらない）。
            Assert.That(Walk(s, OutX, 0f, -OutX, 0f, speedMps: 2.0f), Is.False,
                "通り抜けただけで導入が始まった");
        }

        [Test]
        public void Rearm_ForgetsEverything()
        {
            var s = Waiting();
            Walk(s, OutX, 0f, 0f, 0f);
            Assert.That(Stay(s, 0f, 0f, Hold * 2f), Is.True);

            // ラン開始（体験者の交代）。**前の人が満たした条件を持ち越さない**。
            s.Rearm();
            Assert.That(s.Fired, Is.False);
            Assert.That(Stay(s, 0f, 0f, 10f), Is.False,
                "武装し直した後も、円の中に居るだけで成立している（前の体験者の条件が漏れている）");
        }

        [Test]
        public void FiredLatches_UntilRearm()
        {
            var s = Waiting();
            Walk(s, OutX, 0f, 0f, 0f);
            Assert.That(Stay(s, 0f, 0f, Hold * 2f), Is.True);
            // 段 0 を抜けるまで保持する（1 フレーム外れただけで振り出しに戻さない）。
            Assert.That(Walk(s, 0f, 0f, OutX, 0f), Is.True);
        }

        [Test]
        public void TrackingJump_InvalidatesTheTrail()
        {
            var s = Waiting();
            // トラッキング初期化 / recenter で 1 フレームに大きく飛ぶ。その軌跡は信用しない。
            s.Tick(0f, 0f, 0f, 0f, R, Hold, Dt);       // OutX からのジャンプではなく…
            s.Tick(6f, 6f, 0f, 0f, R, Hold, Dt);       // 1 フレームで 8m 超
            s.Tick(0f, 0f, 0f, 0f, R, Hold, Dt);       // 飛んだ先が円の中
            Assert.That(Stay(s, 0f, 0f, Hold * 3f), Is.False,
                "飛んだ先が円の中だったので、外から入ってきたことにされた");
        }

        [Test]
        public void DtDiscontinuity_InvalidatesTheTrail()
        {
            var s = Waiting();
            // HMD 着脱・アプリ復帰でフレームが飛ぶ。
            s.Tick(0.5f, 0f, 0f, 0f, R, Hold, StartSpotLogic.MaxContinuousDtSec + 0.1f);
            Assert.That(Stay(s, 0f, 0f, Hold * 3f), Is.False);
        }

        [Test]
        public void Unavailable_DoesNotCarryDwellAcrossTheGap()
        {
            var s = Waiting();
            Walk(s, OutX, 0f, 0f, 0f);                                  // 滞在 0.35s
            Assert.That(Stay(s, 0f, 0f, 0.1f), Is.False, "前提が崩れている（ここで成立してはいけない）");
            s.NotifyUnavailable();          // 位置合わせが外れた / 頭のポーズが取れない
            Assert.That(Stay(s, 0f, 0f, 0.2f), Is.False, "中断を跨いで滞在が積み上がった");
        }

        [Test]
        public void BoundaryJitter_DoesNotCountAsHavingBeenOutside()
        {
            var s = new StartSpotLogic();
            // 円の縁ぎりぎりで震えているだけ（半径 + 余白より外へ出ていない）。
            for (float t = 0f; t < 5f; t += Dt)
            {
                float x = R + (t % 0.2f < 0.1f ? 0.02f : -0.02f);
                s.Tick(x, 0f, 0f, 0f, R, Hold, Dt);
            }
            Assert.That(Stay(s, 0f, 0f, Hold * 3f), Is.False,
                "境界のふらつきを「外に居た」と数えている");
        }
    }
}
