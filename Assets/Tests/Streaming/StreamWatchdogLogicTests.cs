#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using Reason = FixedCamVr.Streaming.StreamWatchdogLogic.ReconnectReason;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// StreamWatchdogLogic の suspend/resume-gap/stall/lag/decode-fail を EditMode で固定。
    /// A1（resume-gap で suspend 解除）/ A2（stall が LastFrameRealtime を汚さない）の回帰をここに載せる。
    /// 時刻・dt はすべて引数注入で決定的に駆動する（now は sentinel 誤判定を避けるため常に &gt;0）。
    /// </summary>
    public sealed class StreamWatchdogLogicTests
    {
        // recvFps を実測ロールで >0 に立ち上げるヘルパー。窓開始 → decode 蓄積 → 1s 後にロール。
        private static StreamWatchdogLogic PrimeRecvFps(out float windowStart)
        {
            var w = new StreamWatchdogLogic();
            windowStart = 10f;
            // 窓を 10.0 で開始（recvFps=0）。
            w.EndTick(now: 10f, unscaledDt: 0.016f, phoneFps: 0f);
            // 3 フレーム decode を蓄積。
            w.OnFrameDecoded(10.2f);
            w.OnFrameDecoded(10.4f);
            w.OnFrameDecoded(10.6f);
            // 1.0s 経過でロール → recvFps = 3 / 1.0 = 3。
            w.EndTick(now: 11.0f, unscaledDt: 0.016f, phoneFps: 0f);
            return w;
        }

        // ---- A1: resume-gap ---------------------------------------------------------

        [Test]
        public void A1_ResumeGap_ClearsSuspend()
        {
            var w = new StreamWatchdogLogic();
            Assert.That(w.SetSuspended(true, 0f), Is.True);
            Assert.That(w.IsSuspended, Is.True);

            Assert.That(w.BeginTick(now: 10f, unscaledDt: 5f), Is.True);
            Assert.That(w.IsSuspended, Is.False, "resume-gap は suspend を解除する");
        }

        [Test]
        public void A1_ResumeGap_ResetsWindow_WhenNotSuspended()
        {
            var w = PrimeRecvFps(out _);
            Assert.That(w.ReceivedFps, Is.GreaterThan(0f), "前提: recvFps を立ち上げておく");

            Assert.That(w.BeginTick(now: 20f, unscaledDt: 1f), Is.True, "dt>ResumeGapSec でフレームスキップ");
            Assert.That(w.ReceivedFps, Is.EqualTo(0f), "窓がリセットされ recvFps=0");
        }

        [Test]
        public void A1_StallGatedBySuspended()
        {
            var w = new StreamWatchdogLogic();
            w.OnFrameDecoded(1f);            // _lastFrameTime=1
            Assert.That(w.SetSuspended(true, 1f), Is.True);

            // now-_lastFrameTime=14>=10 だが suspend 中なので発火しない。
            var reason = w.EndTick(now: 15f, unscaledDt: 0.016f, phoneFps: 0f);
            Assert.That(reason, Is.EqualTo(Reason.None), "!_suspended ゲートで stall 不発火");
        }

        [Test]
        public void A1_SuspendStickinessRecovers_StallWatchdogResumes()
        {
            var w = new StreamWatchdogLogic();
            w.OnFrameDecoded(1f);
            Assert.That(w.SetSuspended(true, 1f), Is.True);

            // suspend 中: stall 不発火。
            Assert.That(w.EndTick(now: 15f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.None));

            // resume-gap で自己回復（_lastFrameTime=20, _lastReconnectTime=20, suspend 解除）。
            Assert.That(w.BeginTick(now: 20f, unscaledDt: 5f), Is.True);
            Assert.That(w.IsSuspended, Is.False);

            // StallReconnectSec 未満はまだ None。
            Assert.That(w.EndTick(now: 25f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.None));
            // StallReconnectSec 経過で stall watchdog が復帰する。
            Assert.That(w.EndTick(now: 31f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
        }

        [Test]
        public void Stall_NeverFires_WhenStreamHasNeverReceivedAFrame()
        {
            // 現場に居ないカメラ（show.json 未設定 → 焼き込みの古い IP へ繋ぎに行く）に対して、
            // 10 秒ごとに永久に張り直しを続けていた（走行 240 秒で 24 回 ＝ 再接続ログの約半分）。
            var w = new StreamWatchdogLogic();
            Assert.That(w.EndTick(now: 30f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.None));
            Assert.That(w.EndTick(now: 60f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.None),
                "一度も映っていない stream は watchdog では直せない — receiver のバックオフに任せる");
        }

        [Test]
        public void Stall_FiresOnceStreamHasReceived_ThenGoesQuiet()
        {
            // 一度でも映ったなら、以後の無フレームは本物の stall として扱う（従来どおり）。
            var w = new StreamWatchdogLogic();
            w.OnFrameDecoded(1f);
            Assert.That(w.EndTick(now: 12f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
        }

        // ---- A2: stall watchdog が LastFrameRealtime を汚さない ---------------------

        [Test]
        public void A2_Stall_DoesNotOverwriteLastFrameRealtime()
        {
            var w = new StreamWatchdogLogic();
            w.OnFrameDecoded(5f);
            Assert.That(w.LastFrameRealtime, Is.EqualTo(5f));

            var reason = w.EndTick(now: 15f, unscaledDt: 0.016f, phoneFps: 0f);
            Assert.That(reason, Is.EqualTo(Reason.Stall));
            Assert.That(w.LastFrameRealtime, Is.EqualTo(5f),
                "stall 発火で _lastFrameTime を書き換えない（SignalLostFx 砂嵐消灯バグの回帰）");
        }

        [Test]
        public void A2_Stall_RefireEvery10s()
        {
            var w = new StreamWatchdogLogic();
            w.OnFrameDecoded(5f);

            // 1 回目 Stall（now=15 → _lastReconnectTime=15）。
            Assert.That(w.EndTick(now: 15f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
            // 5s 後はまだ None（再発火周期=StallReconnectSec=10s、cooldown=5s ではない）。
            Assert.That(w.EndTick(now: 20f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.None));
            // 10s 後に再発火。
            Assert.That(w.EndTick(now: 25f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
        }

        [Test]
        public void A2_Stall_LastFrameRealtimeInvariantAcrossMultipleFires()
        {
            var w = new StreamWatchdogLogic();
            w.OnFrameDecoded(5f);

            Assert.That(w.EndTick(now: 15f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
            Assert.That(w.EndTick(now: 25f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
            Assert.That(w.EndTick(now: 35f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.Stall));
            Assert.That(w.LastFrameRealtime, Is.EqualTo(5f), "連続砂嵐の担保: 最終 decode 時刻で不変");
        }

        // ---- Lag ---------------------------------------------------------------------

        // recvFps を任意の値へ立ち上げるヘルパー（窓 1.0s に frames 枚 ＝ recvFps = frames）。
        private static StreamWatchdogLogic PrimeRecvFpsN(int frames)
        {
            var w = new StreamWatchdogLogic();
            w.EndTick(now: 10f, unscaledDt: 0.016f, phoneFps: 0f);
            for (int i = 0; i < frames; i++) w.OnFrameDecoded(10f + 0.9f * i / frames);
            w.EndTick(now: 11.0f, unscaledDt: 0.016f, phoneFps: 0f);
            return w;
        }

        [Test]
        public void Lag_DoesNotFire_WhenReceivingEnough_EvenIfPhoneRunsFast()
        {
            // 配信は明るい場所で 60fps に張り付く。素の比で測ると受信 40fps でも ratio=0.67 で誤爆し、
            // 再接続がフレームを落として悪化する（2026-07-30 実機: 本編 69 秒で再接続 152 回）。
            var w = PrimeRecvFpsN(40);
            Assert.That(w.ReceivedFps, Is.EqualTo(40f).Within(0.5f));
            Assert.That(w.EndTick(now: 11.5f, unscaledDt: 2f, phoneFps: 60f), Is.EqualTo(Reason.None),
                "配信 60fps でも受信 40fps は十分な品質 — 張り直さない");
        }

        [Test]
        public void Lag_StillFires_WhenReceptionIsActuallyBad_WithFastPhone()
        {
            var w = PrimeRecvFpsN(15);
            Assert.That(w.EndTick(now: 11.5f, unscaledDt: 2f, phoneFps: 60f), Is.EqualTo(Reason.Lag),
                "受信 15fps は実用上限 30fps の 7 割を割る — 本物の詰まりは今までどおり拾う");
        }

        [Test]
        public void Lag_UsesPhoneFps_WhenSourceIsSlowerThanReference()
        {
            // 暗所で配信が 15fps へ落ちた場合。分母は配信 fps のままなので従来の感度を保つ。
            var w = PrimeRecvFpsN(9);
            Assert.That(w.EndTick(now: 11.5f, unscaledDt: 2f, phoneFps: 15f), Is.EqualTo(Reason.Lag),
                "9/15=0.6 < 0.7");
        }

        [Test]
        public void Lag_FiresAfterWindow_AndResets()
        {
            var w = PrimeRecvFps(out _);
            Assert.That(w.ReceivedFps, Is.GreaterThan(0f).And.LessThan(StreamWatchdogLogic.LagThresholdRatio * 30f),
                "recvFps=3, phoneFps=30 なら ratio=0.1<0.7");

            // 同一窓内（now-_recvWindowStart<1）に留めて recvFps を保ちつつ dt で lag 窓を貯める。
            var reason = w.EndTick(now: 11.5f, unscaledDt: StreamWatchdogLogic.LagDetectWindowSec, phoneFps: 30f);
            Assert.That(reason, Is.EqualTo(Reason.Lag));

            // 発火直後は _lagWindowAccum リセットで即再発火しない。
            Assert.That(w.EndTick(now: 11.6f, unscaledDt: 0.016f, phoneFps: 30f), Is.EqualTo(Reason.None));
        }

        [Test]
        public void Lag_DoesNotFire_WhenNoFramesOrPhoneFpsTooLow()
        {
            var noRecv = new StreamWatchdogLogic();
            Assert.That(noRecv.EndTick(now: 10f, unscaledDt: 2f, phoneFps: 30f), Is.EqualTo(Reason.None),
                "recvFps=0 では lag 不発火");

            var w = PrimeRecvFps(out _);
            Assert.That(w.EndTick(now: 11.5f, unscaledDt: 2f, phoneFps: 1f), Is.EqualTo(Reason.None),
                "phoneFps<=1 では lag 不発火");
        }

        // ---- DecodeFail --------------------------------------------------------------

        [Test]
        public void DecodeFail_FiresAtCount_ThenStreakResets()
        {
            var w = new StreamWatchdogLogic();
            // now=5 固定（cooldown: now-_lastReconnectTime(0)=5>=5）。同 now なので sinceSec=0 → 枚数経路のみ。
            for (int i = 1; i < StreamWatchdogLogic.DecodeFailReconnectCount; i++)
                Assert.That(w.OnFrameDecodeFailed(5f, out _, out _), Is.False, $"{i} 枚目はまだ発火しない");

            Assert.That(w.OnFrameDecodeFailed(5f, out int streak, out _), Is.True,
                "DecodeFailReconnectCount 枚目で発火");
            Assert.That(streak, Is.EqualTo(StreamWatchdogLogic.DecodeFailReconnectCount));

            // 発火で streak リセット + _lastReconnectTime=5。同 now の追加失敗は over 未達で false。
            Assert.That(w.OnFrameDecodeFailed(5f, out _, out _), Is.False);
        }

        [Test]
        public void DecodeFail_FiresByElapsedTime_LowFps()
        {
            var w = new StreamWatchdogLogic();
            Assert.That(w.OnFrameDecodeFailed(10f, out _, out _), Is.False, "1 枚目");
            // 2 枚目、sinceSec=2>=DecodeFailReconnectSec で発火（低 fps 経路）。
            Assert.That(w.OnFrameDecodeFailed(12f, out _, out float sinceSec), Is.True);
            Assert.That(sinceSec, Is.EqualTo(2f).Within(0.001f));
        }

        [Test]
        public void Cooldown_BlocksDecodeFail_WithinLagReconnectCooldown()
        {
            var w = new StreamWatchdogLogic();
            // 枚数経路で 1 度発火させて _lastReconnectTime=5。
            for (int i = 1; i < StreamWatchdogLogic.DecodeFailReconnectCount; i++)
                w.OnFrameDecodeFailed(5f, out _, out _);
            Assert.That(w.OnFrameDecodeFailed(5f, out _, out _), Is.True);

            // cooldown 内（now=8, 3s < 5s）に時間経路で over 到達しても cooldown が阻む。
            Assert.That(w.OnFrameDecodeFailed(6f, out _, out _), Is.False, "streak=1, sinceSec=0");
            Assert.That(w.OnFrameDecodeFailed(8f, out _, out float sinceSec), Is.False,
                "sinceSec=2>=2 で over だが now-_lastReconnectTime=3<5 の cooldown で不発火");
            Assert.That(sinceSec, Is.EqualTo(2f).Within(0.001f));
        }

        // ---- NotifyEndpointChanged ---------------------------------------------------

        [Test]
        public void NotifyEndpointChanged_ResetsWindows()
        {
            var w = PrimeRecvFps(out _);
            Assert.That(w.ReceivedFps, Is.GreaterThan(0f));

            w.NotifyEndpointChanged(50f);
            Assert.That(w.ReceivedFps, Is.EqualTo(0f), "窓リセットで recvFps=0");

            // _lastReconnectTime=50 にリセットされたので、50 直後の stall 判定は cooldown で抑止される
            // （_lastFrameTime は NotifyEndpointChanged で変えないため 10.6 のまま古いが、
            //  now-_lastReconnectTime=1<10 で再発火ゲートが阻む）。
            Assert.That(w.EndTick(now: 51f, unscaledDt: 0.016f, phoneFps: 0f), Is.EqualTo(Reason.None));
        }
    }
}
