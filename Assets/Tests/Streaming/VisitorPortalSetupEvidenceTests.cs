using System.Reflection;
using System.Collections.Generic;
using System;
using NUnit.Framework;
using UnityEngine;
namespace FixedCamVr.Streaming.Tests
{
    public sealed class VisitorPortalSetupEvidenceTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Call(VisitorPortal p, string method, params object[] args)
            => typeof(VisitorPortal).GetMethod(method, Private).Invoke(p, args);
        private static VisitorPortalLogic.PreparationPulse Proof(VisitorPortal p, int seq, int revision,
            bool complete = true, bool staff = false, string tablet = "tablet")
            => new VisitorPortalLogic.PreparationPulse { portalSessionId = p.CreateHeartbeatSnapshot().portalSessionId,
                tabletSessionId = tablet, seq = seq, revision = revision, completed = complete, staffConfirmed = staff };
        [TestCase("0", "1", "true", false)]
        [TestCase("0", "1", "false", true)]
        [TestCase("1", "1", "true", true)]
        [TestCase("-1", "1", "true", false)]
        [TestCase("1", "0", "true", false)]
        [TestCase("1", "2147483648", "true", false)]
        [TestCase("2147483648", "1", "true", false)]
        [TestCase("1.1", "1", "true", false)]
        [TestCase("1", "1", "null", false)]
        public void PreparationParsingRejectsInvalidSequenceRevisionAndCompletion(string seq, string revision, string completed, bool valid)
        {
            string body = "{\"tabletSessionId\":\"tablet\",\"portalSessionId\":\"portal\",\"seq\":" + seq
                + ",\"briefingRevision\":" + revision + ",\"briefingCompleted\":" + completed + "}";
            Assert.AreEqual(valid, VisitorPortalLogic.TryParsePreparation(body, out _));
        }
        [Test] public void PreparationRouteKeepsLegacyPulseButRejectsPreviousVisitorAndIncompleteStaffApproval()
        {
            var req = new VisitorPortalLogic.Request { method = "POST", path = "/tablet/pulse" };
            int applied = 0, reset = 0;
            Func<string, VisitorPortalLogic.Response> route = body => VisitorPortalLogic.Route(req, body, "{}",
                (l, r, t) => 1, () => { }, "current", _ => { }, _ => applied++, (_, __) => reset++);
            Assert.AreEqual(200, route("{\"tabletSessionId\":\"tablet\"}").status);
            string proof = "{\"tabletSessionId\":\"tablet\",\"portalSessionId\":\"old\",\"seq\":1,\"briefingRevision\":1,\"briefingCompleted\":true}";
            Assert.AreEqual(409, route(proof).status); Assert.AreEqual(0, applied);
            Assert.AreEqual(200, route(proof.Replace("old", "current")).status); Assert.AreEqual(1, applied);
            Assert.AreEqual(400, route(proof.Replace("old", "current").Replace("true}", "false,\"staffConfirmed\":true}")).status);
            string resetBody = "{\"tabletSessionId\":\"tablet\",\"portalSessionId\":\"current\",\"staffReset\":true,\"resetRequestId\":\"reset\",\"settingsSeq\":0}";
            Assert.AreEqual(200, route(resetBody).status); Assert.AreEqual(1, reset);
            Assert.AreEqual(409, route(resetBody.Replace("current", "old")).status);
            Assert.AreEqual(400, route(resetBody.Replace(",\"settingsSeq\":0", "")).status);
            Assert.AreEqual(400, route(resetBody.Replace("true", "false")).status);
        }
        [Test] public void TransportReceiptDoesNotConfirmUntilQueueAndActualSettingsApply()
        {
            var go = new GameObject("briefing-proof"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                int seq = (int)Call(p, "OnSetFromThread", ShowLang.En, true, "tablet");
                Call(p, "OnPreparationFromThread", Proof(p, seq, 1, true, true));
                Assert.IsFalse(p.CurrentVisitorStaffConfirmed); Call(p, "Update");
                Assert.IsFalse(p.CurrentVisitorBriefingCompleted); VisitorPrefs.ApplyPending();
                Assert.IsTrue(p.CurrentVisitorStaffConfirmed);
                Call(p, "OnPreparationFromThread", Proof(p, seq, 2, false));
                Assert.IsFalse(p.CurrentVisitorBriefingCompleted, "queued redo closes gate immediately");
                Call(p, "OnPreparationFromThread", Proof(p, seq, 1, true, true)); Call(p, "Update");
                Assert.IsFalse(p.CurrentVisitorStaffConfirmed, "late previous completion cannot override redo");
                Call(p, "OnPreparationFromThread", Proof(p, seq, 3, true)); Call(p, "Update");
                Assert.IsTrue(p.CurrentVisitorBriefingCompleted); Assert.IsFalse(p.CurrentVisitorStaffConfirmed);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void ResetNewSettingsAndDifferentTabletDiscardCapturedCompletion()
        {
            var go = new GameObject("briefing-generation"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                int seq = (int)Call(p, "OnSetFromThread", ShowLang.Ja, false, "tablet"); Call(p, "Update"); VisitorPrefs.ApplyPending();
                var oldProof = Proof(p, seq, 1, true, true); Call(p, "OnPreparationFromThread", oldProof);
                var queue = (List<Action>)typeof(VisitorPortal).GetField("_queue", Private).GetValue(p);
                var oldBatch = queue.ToArray(); queue.Clear(); p.BeginVisitorSession();
                int next = (int)Call(p, "OnSetFromThread", ShowLang.En, true, "other-tablet"); Call(p, "Update"); VisitorPrefs.ApplyPending();
                foreach (var a in oldBatch) a(); Call(p, "OnPreparationFromThread", oldProof); Call(p, "Update");
                Assert.IsFalse(p.CurrentVisitorStaffConfirmed);
                Call(p, "OnPreparationFromThread", Proof(p, next, 20, true, true)); Call(p, "Update");
                Assert.IsFalse(p.CurrentVisitorStaffConfirmed, "different tablet cannot approve current settings");
                Call(p, "OnPreparationFromThread", Proof(p, next, 1, true, true, "other-tablet")); Call(p, "Update");
                Assert.IsTrue(p.CurrentVisitorStaffConfirmed);
                Call(p, "OnClearFromThread"); Call(p, "Update"); Assert.IsFalse(p.CurrentVisitorStaffConfirmed);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void TabletResetDuplicatesAndConcurrentSettingsCannotResetNextVisitor()
        {
            var go = new GameObject("tablet-reset-proof"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession(); int resets = 0;
                p.StaffResetProvider = () => { resets++; p.BeginVisitorSession(); return true; };
                Call(p, "OnStaffResetFromThread", "reset-one", 0); Call(p, "OnStaffResetFromThread", "reset-one", 0);
                Call(p, "Update"); Assert.AreEqual(1, resets); Assert.AreEqual(2, p.VisitorGeneration);
                Call(p, "OnStaffResetFromThread", "reset-old", 0);
                Call(p, "OnSetFromThread", ShowLang.En, true, "tablet"); Call(p, "Update");
                Assert.AreEqual(1, resets, "new settings invalidate queued reset");
                Call(p, "OnStaffResetFromThread", "reset-late", 0); Call(p, "Update");
                Assert.AreEqual(1, resets, "arrival with stale settings version is rejected");
                p.StaffResetProvider = () => false; Call(p, "OnStaffResetFromThread", "reset-denied", 1); Call(p, "Update");
                Assert.AreEqual(2, p.VisitorGeneration);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void ClearConsumedProofBlocksImmediatelyAndCannotBeRestoredByOldPulse()
        {
            var go = new GameObject("clear-consumed-proof"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                int seq = (int)Call(p, "OnSetFromThread", ShowLang.Ja, false, "tablet"); Call(p, "Update"); VisitorPrefs.ApplyPending();
                var proof = Proof(p, seq, 1, true, true); Call(p, "OnPreparationFromThread", proof); Call(p, "Update");
                VisitorPrefs.Consume(); Assert.IsTrue(p.CurrentVisitorStaffConfirmed);
                Call(p, "OnClearVersionedFromThread", seq); Assert.IsTrue(p.HasQueuedClear);
                Assert.IsFalse(p.CurrentVisitorStaffConfirmed); Call(p, "Update");
                Call(p, "OnPreparationFromThread", proof); Call(p, "Update");
                Assert.IsFalse(p.CurrentVisitorSettingsApplied); Assert.IsFalse(p.CurrentVisitorStaffConfirmed);
                Assert.IsNull(p.LatestRequest);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void AcceptedStaffResetClosesStartBeforeUnityProcessesIt()
        {
            var go = new GameObject("reset-start-race"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                int seq = (int)Call(p, "OnSetFromThread", ShowLang.Ja, false, "tablet"); Call(p, "Update"); VisitorPrefs.ApplyPending();
                Call(p, "OnPreparationFromThread", Proof(p, seq, 1, true, true)); Call(p, "Update");
                Assert.IsTrue(p.CurrentVisitorStaffConfirmed);
                p.StaffResetProvider = () => { p.BeginVisitorSession(); return true; };
                Call(p, "OnStaffResetFromThread", "reset-start", seq);
                Assert.IsTrue(p.HasQueuedStaffReset); Assert.IsFalse(p.CurrentVisitorStaffConfirmed);
                Call(p, "Update"); Assert.IsFalse(p.HasQueuedStaffReset); Assert.IsFalse(p.CurrentVisitorSettingsApplied);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void AnotherTabletPulseCannotRefreshCurrentVisitorsTabletEvidence()
        {
            var go = new GameObject("tablet-owner-proof"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                Call(p, "OnSetFromThread", ShowLang.Ja, false, "owner");
                Call(p, "OnPulseFromThread", "other", "127.0.0.1"); Call(p, "Update");
                Assert.Less(p.LatestTabletAgeSec, 1f); Assert.AreEqual(float.PositiveInfinity, p.CurrentVisitorTabletAgeSec);
                Call(p, "OnPulseFromThread", "owner", "127.0.0.1"); Call(p, "Update");
                Assert.Less(p.CurrentVisitorTabletAgeSec, 1f);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void ClearForPreviousSettingsCannotDeleteNewSettings()
        {
            var go = new GameObject("clear-settings-race"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                int old = (int)Call(p, "OnSetFromThread", ShowLang.Ja, false, "tablet"); Call(p, "Update");
                Call(p, "OnClearVersionedFromThread", old);
                int next = (int)Call(p, "OnSetFromThread", ShowLang.En, true, "tablet"); Call(p, "Update");
                Call(p, "OnClearVersionedFromThread", old); Call(p, "Update");
                Assert.AreEqual(next, VisitorPrefs.PendingSeq); Assert.IsFalse(p.HasQueuedClear);
                VisitorPrefs.ApplyPending(); Assert.IsTrue(p.CurrentVisitorSettingsApplied);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void ResetDropsQueuedSettingsAndClearEvenIfBatchWasAlreadyCaptured()
        {
            VisitorPrefs.Reset(); var go = new GameObject("portal-cycle-test");
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                Call(p, "OnSetFromThread", ShowLang.En, true, "tablet"); Call(p, "OnClearFromThread");
                var queue = (List<Action>)typeof(VisitorPortal).GetField("_queue", Private).GetValue(p);
                var old = queue.ToArray(); queue.Clear(); p.BeginVisitorSession();
                Assert.IsFalse(p.HasQueuedSettings); Assert.IsNull(p.LatestRequest);
                int seq = (int)Call(p, "OnSetFromThread", ShowLang.Fr, false, "tablet"); Call(p, "Update");
                VisitorPrefs.ApplyPending(); foreach (var action in old) action();
                Assert.AreEqual(seq, VisitorPrefs.PendingSeq); Assert.AreEqual(ShowLang.Fr, VisitorPrefs.PendingLang);
                Assert.AreEqual(seq, p.LatestRequest.seq); Assert.IsTrue(p.CurrentVisitorSettingsApplied);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        [Test] public void DoubleResetChangesTokenAndCannotReuseAppliedOrConsumedSettings()
        {
            VisitorPrefs.Reset(); var go = new GameObject("portal-cycle-test");
            try
            {
                var p = go.AddComponent<VisitorPortal>(); p.BeginVisitorSession();
                string token = p.CreateHeartbeatSnapshot().portalSessionId;
                int seq = (int)Call(p, "OnSetFromThread", ShowLang.En, true, "tablet"); Call(p, "Update");
                Assert.IsFalse(p.CurrentVisitorSettingsApplied); VisitorPrefs.ApplyPending();
                Assert.IsTrue(p.CurrentVisitorSettingsApplied); VisitorPrefs.Consume();
                Assert.IsTrue(p.CurrentVisitorSettingsApplied, "title consumption keeps the same visitor proof");
                p.BeginVisitorSession(); p.BeginVisitorSession();
                Assert.AreNotEqual(token, p.CreateHeartbeatSnapshot().portalSessionId);
                Assert.AreEqual(3, p.VisitorGeneration); Assert.IsFalse(p.CurrentVisitorSettingsApplied);
                Assert.AreEqual(0, VisitorPrefs.AppliedSeq); Assert.AreEqual(0, VisitorPrefs.ConsumedSeq);
                Assert.AreEqual(1, VisitorPrefs.ApplyCount, "cumulative metric is retained");
                int next = (int)Call(p, "OnSetFromThread", ShowLang.Ja, false, "tablet"); Assert.Greater(next, seq);
                Call(p, "Update"); VisitorPrefs.ApplyPending(); Assert.IsTrue(p.CurrentVisitorSettingsApplied);
                VisitorPrefs.Clear(); Assert.IsFalse(p.CurrentVisitorSettingsApplied);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); ShowLanguage.Reset(); HorrorRelief.Reset(); }
        }
        private static VisitorPortal.StaffStatus SampleStaff(VisitorPortal.StaffCameraStatus[] cameras) => new VisitorPortal.StaffStatus
        {
            stage = "setup", reason = "camera", positionConfirmed = false, resetProgress = 0,
            position = "needed", tablet = "ok", content = true, cameras = cameras,
        };
        private static VisitorPortal.StaffCameraStatus[] SampleCameras() => new[]
        {
            new VisitorPortal.StaffCameraStatus { id = "A", state = "ok", problem = "" },
            new VisitorPortal.StaffCameraStatus { id = "B", state = "trouble", problem = "nostream" },
            new VisitorPortal.StaffCameraStatus { id = "C", state = "checking", problem = "" },
        };
        [Test] public void StaffSetupJsonShapeIsFixed()
        {
            var sb = new System.Text.StringBuilder();
            var run = new VisitorPortal.RunStatus { phase = "RUN", lap = 2, laps = 3, sec = 84, outro = "off", ending = "" };
            VisitorPortal.AppendStaffSetupJson(sb, SampleStaff(SampleCameras()), run);
            Assert.AreEqual("{\"stage\":\"setup\",\"reason\":\"camera\",\"positionConfirmed\":false,\"resetProgress\":0,"
                + "\"position\":\"needed\",\"cameras\":[{\"id\":\"A\",\"state\":\"ok\",\"problem\":\"\"},"
                + "{\"id\":\"B\",\"state\":\"trouble\",\"problem\":\"nostream\"},{\"id\":\"C\",\"state\":\"checking\",\"problem\":\"\"}],"
                + "\"tablet\":\"ok\",\"content\":true,"
                + "\"run\":{\"phase\":\"RUN\",\"lap\":2,\"laps\":3,\"sec\":84,\"outro\":\"off\",\"ending\":\"\"}}", sb.ToString());
        }
        [Test] public void StaffSetupJsonWithoutProviderStaysParseableWithEmptyValues()
        {
            var sb = new System.Text.StringBuilder();
            VisitorPortal.AppendStaffSetupJson(sb, default, default);
            Assert.AreEqual("{\"stage\":\"\",\"reason\":\"\",\"positionConfirmed\":false,\"resetProgress\":0,"
                + "\"position\":\"\",\"cameras\":[],\"tablet\":\"\",\"content\":false,"
                + "\"run\":{\"phase\":\"\",\"lap\":0,\"laps\":0,\"sec\":0,\"outro\":\"\",\"ending\":\"\"}}", sb.ToString());
        }
        [TestCase(OutroStage.Off, "off")]
        [TestCase(OutroStage.Collapse, "playing")]
        [TestCase(OutroStage.Dark, "playing")]
        [TestCase(OutroStage.Report, "playing")]
        [TestCase(OutroStage.Done, "done")]
        public void OutroCodeMapsStages(OutroStage stage, string code) => Assert.AreEqual(code, VisitorPortal.OutroCode(stage));
        [TestCase(ShowEndingOutcome.Released, "released")]
        [TestCase(ShowEndingOutcome.Trapped, "trapped")]
        [TestCase(ShowEndingOutcome.Pending, "")]
        [TestCase(ShowEndingOutcome.Interrupted, "")]
        public void EndingCodeMapsOutcomes(ShowEndingOutcome outcome, string code) => Assert.AreEqual(code, VisitorPortal.EndingCode(outcome));
        [Test] public void StatusCarriesStaffSetupAndRebuildsWhenReusedCameraArrayChanges()
        {
            var go = new GameObject("portal-staff-status"); VisitorPrefs.Reset();
            try
            {
                var p = go.AddComponent<VisitorPortal>();
                // 開いているシーンの実行体を拾わず、この試験の実行体だけを観測する。
                var run = go.AddComponent<ShowRunDirector>();
                var outro = go.AddComponent<OutroDirector>();
                typeof(VisitorPortal).GetField("_run", Private).SetValue(p, run);
                typeof(VisitorPortal).GetField("_outro", Private).SetValue(p, outro);
                var cameras = SampleCameras();
                p.StaffStatusProvider = () => SampleStaff(cameras);
                Call(p, "RefreshStatus", true);
                string json = (string)typeof(VisitorPortal).GetField("_statusJson", Private).GetValue(p);
                StringAssert.Contains("\"staffSetup\":{\"stage\":\"setup\"", json);
                StringAssert.Contains("{\"id\":\"B\",\"state\":\"trouble\",\"problem\":\"nostream\"}", json);
                StringAssert.Contains("\"run\":{\"phase\":\"INTRO\",\"lap\":1,\"laps\":3,\"sec\":0,\"outro\":\"off\",\"ending\":\"\"}", json);
                cameras[1] = new VisitorPortal.StaffCameraStatus { id = "B", state = "ok", problem = "" };
                Call(p, "RefreshStatus", false);
                json = (string)typeof(VisitorPortal).GetField("_statusJson", Private).GetValue(p);
                StringAssert.Contains("{\"id\":\"B\",\"state\":\"ok\",\"problem\":\"\"}", json);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); }
        }
        [Test] public void AcceptedHttpRequestBlocksBeforeMainThreadAppliesQueue()
        {
            VisitorPrefs.Reset();
            var go = new GameObject("portal-evidence-test");
            try
            {
                var portal = go.AddComponent<VisitorPortal>();
                Assert.IsFalse(portal.HasQueuedSettings);
                var f = typeof(VisitorPortal).GetMethod("OnSetFromThread", BindingFlags.Instance | BindingFlags.NonPublic);
                f.Invoke(portal, new object[] { ShowLang.En, true, "test-tablet" });
                Assert.IsFalse(VisitorPrefs.HasPending); Assert.IsTrue(portal.HasQueuedSettings);
                typeof(VisitorPortal).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
                Assert.IsTrue(VisitorPrefs.HasUnapplied); Assert.IsFalse(portal.HasQueuedSettings);
                Assert.That(portal.LatestTabletAgeSec, Is.EqualTo(float.PositiveInfinity), "受理はpulseの代わりではない");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); VisitorPrefs.Reset(); }
        }
    }
}
