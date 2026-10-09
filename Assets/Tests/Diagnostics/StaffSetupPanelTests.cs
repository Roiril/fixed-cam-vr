using System.Reflection;
using FixedCamVr.Input;
using NUnit.Framework;
using UnityEngine;
namespace FixedCamVr.Diagnostics.Tests
{
    public sealed class StaffSetupPanelTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static SetupCameraEvidence Good() => new SetupCameraEvidence { connected = true, freshDecode = true,
            identityKnown = true, identityMatches = true, connection = 1 };
        private static StaffSetupSnapshot Snapshot(StaffSetupPanel p) => new StaffSetupSnapshot { generation = p.Logic.Generation,
            cameras = new[] { Good(), Good(), Good() }, tabletFresh = true, settingsSettled = true,
            contentReady = true, hasPosition = true };
        private static StaffSetupPanel Built(GameObject go)
        {
            var p = go.AddComponent<StaffSetupPanel>();
            typeof(StaffSetupPanel).GetMethod("Build", Flags).Invoke(p, new object[] { 2.4f });
            return p;
        }
        private static void Render(StaffSetupPanel p, StaffSetupSnapshot s)
        {
            typeof(StaffSetupPanel).GetField("_previewSnapshot", Flags).SetValue(p, s);
            typeof(StaffSetupPanel).GetMethod("Render", Flags).Invoke(p, null);
        }
        private static TMPro.TMP_Text Text(StaffSetupPanel p, string field)
            => (TMPro.TMP_Text)typeof(StaffSetupPanel).GetField(field, Flags).GetValue(p);
        private static TMPro.TMP_Text Row(StaffSetupPanel p, string field, int i)
            => ((TMPro.TMP_Text[])typeof(StaffSetupPanel).GetField(field, Flags).GetValue(p))[i];

        [TestCase("192.168.10.31", "クエスト α")]
        [TestCase("192.168.10.32", "クエスト β")]
        [TestCase("192.168.10.131", "192.168.10.131")]
        [TestCase("10.0.0.5", "10.0.0.5")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void DeviceLabelDerivesAlphaBetaFromStaticAddress(string ip, string expected)
            => Assert.AreEqual(expected, StaffSetupPanel.DeviceLabel(ip));

        [Test] public void FirstSetupIsAChecklist_TroubleRowNamesTheFix()
        {
            var go = new GameObject("setup-checklist-test");
            try
            {
                var p = Built(go); var s = Snapshot(p);
                s.cameras[1] = new SetupCameraEvidence { problem = SetupCameraProblem.NoStream };
                for (int i = 0; i < 24; i++) p.Logic.Observe(s, .25f);
                Render(p, s);
                Assert.AreEqual(StaffSetupPanel.HeaderText, Text(p, "_header").text);
                Assert.AreEqual("位置合わせ", Row(p, "_rows", 0).text);
                Assert.AreEqual("まだです", Row(p, "_statuses", 0).text);
                StringAssert.Contains("右トリガーを 2 秒", Row(p, "_fixes", 0).text);
                Assert.AreEqual(StaffSetupPanel.MarkReady, Row(p, "_marks", 1).text);
                Assert.AreEqual(StaffSetupPanel.MarkTrouble, Row(p, "_marks", 2).text);
                Assert.AreEqual("映像が届いていません", Row(p, "_statuses", 2).text);
                Assert.AreEqual("スマホ B の FixedCam Streamer を開く", Row(p, "_fixes", 2).text);
                Assert.AreEqual("つながっています", Row(p, "_statuses", 4).text);
                Assert.AreEqual(StaffSetupPanel.NotReadyText, Text(p, "_footer").text);
                Assert.AreEqual(StaffSetupPanel.RecallHint, Text(p, "_hint").text);
                Assert.IsFalse(Text(p, "_call").enabled);
                Assert.IsFalse(p.Continue(), "初回の段の B 短押しは何もしない");
                Assert.AreEqual(StaffSetupStage.Devices, p.Logic.Stage);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void WelcomeShowsTabletNoticeAndReadyOnceChecklistIsComplete()
        {
            var go = new GameObject("setup-welcome-test");
            try
            {
                var p = Built(go); var s = Snapshot(p);
                p.Logic.Observe(s, .25f); p.Logic.Observe(s, .25f);
                Assert.IsTrue(p.Logic.ConfirmPosition(s));
                Render(p, s);
                Assert.AreEqual(StaffSetupPanel.WelcomeText, Text(p, "_body").text);
                Assert.AreEqual(StaffSetupPanel.ReadyText, Text(p, "_footer").text);
                Assert.AreEqual("済み", Row(p, "_statuses", 0).text);
                s.cameras[2] = new SetupCameraEvidence { problem = SetupCameraProblem.WrongCamera };
                p.Logic.Observe(s, 0f); Render(p, s);
                Assert.AreEqual("別のカメラが映っています", Row(p, "_statuses", 3).text);
                Assert.AreEqual("スマホの A／B／C の設定を確かめる", Row(p, "_fixes", 3).text);
                Assert.AreEqual(StaffSetupPanel.NotReadyText, Text(p, "_footer").text);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void HandedOffRecoveryCallsStaffInThreeLanguagesFirst()
        {
            var go = new GameObject("setup-recovery-render-test");
            try
            {
                var p = Built(go); var s = Snapshot(p);
                p.Logic.Observe(s, .25f); p.Logic.Observe(s, .25f); p.Logic.ConfirmPosition(s);
                p.Logic.VisitorResetCompleted(); s.generation = p.Logic.Generation;
                s.visitorSettingsApplied = true; s.visitorBriefingCompleted = true;
                Assert.IsTrue(p.Logic.HandOff(s));
                s.cameras[2].connected = false; p.Logic.Observe(s, 0f);
                Render(p, s);
                var call = Text(p, "_call");
                Assert.IsTrue(call.enabled); Assert.IsFalse(Text(p, "_header").enabled);
                StringAssert.StartsWith("スタッフをお呼びください", call.text);
                StringAssert.Contains("Please call a member of staff.", call.text);
                StringAssert.Contains("Veuillez appeler un membre du personnel.", call.text);
                StringAssert.StartsWith("カメラ C：", Text(p, "_body").text);
                Assert.IsFalse(Text(p, "_footer").enabled, "被っている来場者に外させる指示は出さない");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void BriefWaitingDisconnectBlocksImmediatelyWithoutFlashingFullScreen()
        {
            var go = new GameObject("setup-recovery-test");
            try
            {
                var p = go.AddComponent<StaffSetupPanel>();
                var e = Good();
                var s = Snapshot(p);
                var field = typeof(StaffSetupPanel).GetField("_previewSnapshot", Flags);
                field.SetValue(p, s); p.Refresh(.25f); p.Refresh(.25f);
                Assert.IsFalse(p.Continue(), "機器確認から位置合わせへの直列は無い");
                Assert.IsTrue(p.Logic.ConfirmPosition(s));
                p.Logic.VisitorResetCompleted(); s.generation = p.Logic.Generation; s.visitorSettingsApplied = true; s.visitorBriefingCompleted = true;
                field.SetValue(p, s); Assert.IsTrue(p.Continue());
                Assert.IsFalse(p.Visible);
                s.cameras[2].connected = false; field.SetValue(p, s); p.Refresh(0f);
                Assert.IsTrue(p.BlocksVisitor); Assert.IsFalse(p.Visible);
                for (int i = 0; i < 6; i++) p.Refresh(.25f);
                Assert.IsTrue(p.Visible); Assert.IsTrue(p.Logic.PositionConfirmed);
                s.cameras[2] = e; field.SetValue(p, s); p.Refresh(.25f); p.Refresh(.25f);
                Assert.IsFalse(p.Visible); Assert.IsFalse(p.BlocksVisitor);
                s.settingsSettled = false; field.SetValue(p, s); p.Refresh(0f);
                Assert.IsTrue(p.Visible, "未反映設定はネットワーク揺らぎの猶予を使わない");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
