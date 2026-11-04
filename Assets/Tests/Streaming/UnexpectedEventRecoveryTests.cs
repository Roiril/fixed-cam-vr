#nullable enable
using System;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 想定外の事態からの復帰（2026-10-21）。休止明けの時計・強制リセットの口・台車の印・リセットの隔離。
    /// </summary>
    public sealed class UnexpectedEventRecoveryTests
    {
        // ---- 休止を数えない時計 -------------------------------------------------------

        [Test]
        public void Clock_DropsTheSleptSeconds()
        {
            Assert.AreEqual(ShowClock.MaxStepSec, ShowClock.Clamp(232f, out bool gap), 1e-6f);
            Assert.IsTrue(gap, "実機で観測した 232 秒の休止は 1 フレームぶんに切る");
            Assert.AreEqual(0.011f, ShowClock.Clamp(0.011f, out gap), 1e-6f);
            Assert.IsFalse(gap);
            Assert.AreEqual(0f, ShowClock.Clamp(-1f, out _));
            Assert.AreEqual(0f, ShowClock.Clamp(float.NaN, out _));
        }

        [Test]
        public void Run_DoesNotTimeOut_OnTheFirstFrameAfterSleep()
        {
            var logic = new ShowRunLogic();
            logic.Configure(false, 0f, true, 3, 300f);
            logic.BeginRun();
            for (int i = 0; i < 100; i++) logic.Tick(1f, false, false);
            // 被り直した最初のフレーム。素の dt（200 秒）なら時間切れ（300 秒）で終幕へ飛んでいた。
            ShowRunEvent ev = logic.Tick(ShowClock.Clamp(200f, out _), false, false);
            Assert.AreEqual(ShowRunEvent.None, ev);
            Assert.AreEqual(ShowPhase.Run, logic.Phase);
        }

        [Test]
        public void ClosingPrompt_DoesNotTrap_OnTheFirstFrameAfterSleep()
        {
            var d = new EndingDecisionLogic();
            float now = 10f;
            d.NotifyPromptReadable(now);
            now += ShowClock.Clamp(120f, out _);
            d.Tick(now, true, 4, 0);
            Assert.AreEqual(ShowEndingOutcome.Pending, d.Outcome, "休止していた時間で帰還不能にしない");
        }

        // ---- 強制リセットの口 ---------------------------------------------------------

        private static VisitorPortalLogic.Response Pulse(string body, Action<string>? onForce,
                                                         out int normalResets, string portal = "p1")
        {
            int n = 0;
            var req = new VisitorPortalLogic.Request { method = "POST", path = "/tablet/pulse", contentLength = body.Length };
            var res = VisitorPortalLogic.Route(req, body, "{\"ok\":true}", (l, r, t) => 1, () => { }, portal,
                                               onStaffReset: (id, seq) => n++, onForceReset: onForce);
            normalResets = n;
            return res;
        }

        private const string ForceBody =
            "{\"tabletSessionId\":\"t1\",\"staffReset\":true,\"resetRequestId\":\"r1\",\"portalSessionId\":\"p1\",\"settingsSeq\":3,\"force\":true}";

        [Test]
        public void ForceReset_GoesToTheForcePath_NotTheNormalOne()
        {
            string? got = null;
            var res = Pulse(ForceBody, id => got = id, out int normal);
            Assert.AreEqual(200, res.status);
            Assert.AreEqual("r1", got);
            Assert.AreEqual(0, normal);
        }

        [Test]
        public void ForceReset_FromAnotherQuestBoot_IsRejected()
        {
            string? got = null;
            var res = Pulse(ForceBody, id => got = id, out _, portal: "p2");
            Assert.AreEqual(409, res.status);
            Assert.IsNull(got);
        }

        [Test]
        public void NormalReset_IsUnchanged()
        {
            string body = ForceBody.Replace(",\"force\":true", "");
            string? got = null;
            var res = Pulse(body, id => got = id, out int normal);
            Assert.AreEqual(200, res.status);
            Assert.IsNull(got);
            Assert.AreEqual(1, normal);
        }

        // ---- リセットの隔離 ---------------------------------------------------------

        [Test]
        public void ResetChain_KeepsGoing_WhenOneStepThrows()
        {
            int after = 0;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                RuntimeHealth.Step("boom", () => throw new InvalidOperationException("test"));
                RuntimeHealth.Step("after", () => after++);
                Action handlers = () => throw new InvalidOperationException("test");
                handlers += () => after++;
                RuntimeHealth.Raise("event", handlers);
            }
            finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
            Assert.AreEqual(2, after, "1 つが投げても後ろの工程と購読者は走る");
        }
    }
}
