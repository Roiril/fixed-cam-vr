using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    public sealed class StaffSetupLogicTests
    {
        private static StaffSetupSnapshot Good(StaffSetupLogic l) => new StaffSetupSnapshot
        {
            generation = l.Generation, tabletFresh = true, settingsSettled = true, contentReady = true,
            hasPosition = true, visitorSettingsApplied = true, visitorBriefingCompleted = true,
            cameras = new[] { Camera(), Camera(), Camera() },
        };
        private static SetupCameraEvidence Camera() => new SetupCameraEvidence
        { connected = true, freshDecode = true, identityKnown = true, identityMatches = true, connection = 1 };
        private static void Stable(StaffSetupLogic l, StaffSetupSnapshot s)
        { l.Observe(s, .25f); l.Observe(s, .25f); }
        private static StaffSetupSnapshot HandedOff(StaffSetupLogic l)
        {
            var s = Good(l); Stable(l, s);
            Assert.IsTrue(l.ConfirmPosition(s));
            l.VisitorResetCompleted(); s.generation = l.Generation; l.Observe(s, 0f);
            Assert.IsTrue(l.HandOff(s)); return s;
        }
        [Test] public void AllConnected_StableGateDoesNotWaitForFocusAnimation()
        {
            var l = new StaffSetupLogic(); var s = Good(l);
            l.Observe(s, .25f); Assert.IsFalse(l.DevicesReady);
            l.Observe(s, .25f); Assert.IsTrue(l.DevicesReady); Assert.Less(l.FocusIndex, 3);
        }
        [Test] public void DelayedCamera_DoesNotHideOtherGoodCameras()
        {
            var l = new StaffSetupLogic(); var s = Good(l); s.cameras[2] = default;
            Stable(l, s); Assert.AreEqual(SetupDeviceState.Ready, l.CameraState(0));
            Assert.AreEqual(SetupDeviceState.Ready, l.CameraState(1)); Assert.IsFalse(l.DevicesReady);
            s.cameras[2] = Camera(); Stable(l, s); Assert.IsTrue(l.DevicesReady);
        }
        [TestCase(false, true)] [TestCase(true, false)]
        public void IdentityMustBeKnownAndMatch(bool known, bool match)
        {
            var l = new StaffSetupLogic(); var s = Good(l);
            s.cameras[1].identityKnown = known; s.cameras[1].identityMatches = match;
            Stable(l, s); Assert.IsFalse(l.DevicesReady);
        }
        [Test] public void ConnectionGenerationChange_RequiresNewStableEvidence()
        {
            var l = new StaffSetupLogic(); var s = Good(l); Stable(l, s);
            s.cameras[0].connection++; l.Observe(s, 0f); Assert.IsFalse(l.CamerasReady);
            Stable(l, s); Assert.IsTrue(l.CamerasReady);
            s.cameras[0].endpoint++; l.Observe(s, 0f); Assert.IsFalse(l.CamerasReady);
        }
        [Test] public void KnownWrongIdentityIsAnImmediateProblemEvenDuringBoot()
        {
            var l = new StaffSetupLogic(); var s = Good(l);
            s.cameras[0].identityMatches = false; s.cameras[0].problem = SetupCameraProblem.WrongCamera;
            l.Observe(s, 0f); Assert.AreEqual(SetupDeviceState.Trouble, l.CameraState(0));
        }
        [Test] public void SavedPositionDoesNotAutoPass_NoChangesOnCancelledAlignment()
        {
            var l = new StaffSetupLogic(); var s = Good(l); Stable(l, s);
            Assert.IsFalse(l.PositionConfirmed);
            s.registrationActive = true; l.Observe(s, .25f); Assert.IsFalse(l.ConfirmPosition(s));
            s.registrationActive = false; l.Observe(s, .25f); Assert.AreEqual(StaffSetupStage.Devices, l.Stage);
            Assert.IsFalse(l.PositionConfirmed);
        }
        [Test] public void CameraDisconnectRevokesGreenGateButKeepsPosition_RecoveryResumes()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l);
            s.cameras[2].connected = false; l.Observe(s, 0f);
            Assert.AreEqual(SetupDeviceState.Trouble, l.CameraState(2)); Assert.IsTrue(l.PositionConfirmed);
            Assert.IsTrue(l.BlocksVisitor); Assert.IsFalse(l.TryBeginExperience(s));
            s.cameras[2].connected = true; Stable(l, s); Assert.IsFalse(l.BlocksVisitor);
        }
        [Test] public void UnrequestedSettingsUseActualDefault_NoAckWait()
        {
            var l = new StaffSetupLogic(); var s = Good(l); s.settingsRequested = false;
            Stable(l, s); Assert.IsTrue(l.DevicesReady);
        }
        [Test] public void TabletLossBeforeHandOffBlocks_ButPulseNotRequiredAfterVerifiedHandOff()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); s.tabletFresh = false;
            l.Observe(s, 0f); Assert.AreEqual(SetupDeviceState.Trouble, l.TabletState);
            Assert.IsTrue(l.CanBeginExperience);
            l = new StaffSetupLogic(); s = Good(l); Stable(l, s); l.ConfirmPosition(s);
            s.tabletFresh = false; Assert.IsFalse(l.HandOff(s));
        }
        [Test] public void NewUnappliedRequestBlocksStart_ThenAppliedValuesRestoreGate()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l);
            s.settingsRequested = true; s.settingsSettled = false;
            Assert.IsFalse(l.TryBeginExperience(s)); s.settingsSettled = true;
            Assert.IsFalse(l.TryBeginExperience(s)); Assert.IsTrue(l.HandOff(s));
            Assert.IsTrue(l.TryBeginExperience(s));
        }
        [Test] public void RecenterRequiresNewVisualConfirmation_StoredValueDoesNotPass()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); s.positionInvalid = true;
            l.Observe(s, 0f); Assert.IsFalse(l.PositionConfirmed); Assert.AreEqual(StaffSetupStage.Alignment, l.Stage);
            Assert.IsFalse(l.ConfirmPosition(s)); s.positionInvalid = false;
            Assert.IsFalse(l.CanBeginExperience); Assert.IsTrue(l.ConfirmPosition(s));
        }
        [Test] public void ResumeCannotPassWithSuspendedOrStaleDecode()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); s.cameras[1].suspended = true;
            Assert.IsFalse(l.TryBeginExperience(s)); s.cameras[1].suspended = false; s.cameras[1].freshDecode = false;
            Stable(l, s); Assert.IsFalse(l.CanBeginExperience);
            s.cameras[1].freshDecode = true; Stable(l, s); Assert.IsTrue(l.CanBeginExperience);
        }
        [Test] public void StartPressRechecksCurrentStateRatherThanPreviousGreen()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); Assert.IsTrue(l.CanBeginExperience);
            s.contentReady = false; Assert.IsFalse(l.TryBeginExperience(s)); Assert.IsFalse(l.Started);
        }
        [Test] public void OldGenerationCannotConfirmOrStartNextVisitor()
        {
            var l = new StaffSetupLogic(); var old = HandedOff(l); l.NewVisitor();
            Assert.IsFalse(l.Observe(old, .25f)); Assert.IsFalse(l.ConfirmPosition(old));
            Assert.IsFalse(l.TryBeginExperience(old)); var current = Good(l);
            l.Observe(current, 0f); Assert.IsFalse(l.CanBeginExperience);
            Assert.AreEqual(VisitorPreparationStage.ResetRequired, l.VisitorStage);
        }
        [Test] public void PlayingIsNotInterruptedByTabletOrNextVisitorSettings()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); Assert.IsTrue(l.TryBeginExperience(s));
            s.tabletFresh = false; s.settingsSettled = false; l.Observe(s, 0f);
            Assert.IsFalse(l.BlocksVisitor); l.NewVisitor(); s.generation = l.Generation;
            l.Observe(s, 0f); Assert.IsTrue(l.BlocksVisitor);
        }
        [Test] public void HandOffCannotSkipPositionOrVisitorReset()
        {
            var l = new StaffSetupLogic(); var s = Good(l); Stable(l, s);
            Assert.IsFalse(l.HandOff(s)); Assert.AreEqual(StaffSetupStage.Devices, l.Stage);
            Assert.IsTrue(l.ConfirmPosition(s)); Assert.IsFalse(l.HandOff(s));
            Assert.AreEqual(StaffSetupStage.Welcome, l.Stage);
        }
        [Test] public void PositionCanBeConfirmedBeforeDevicesAreReady_HandOffStillChecksEverything()
        {
            var l = new StaffSetupLogic(); var s = Good(l);
            s.cameras[1] = default; s.tabletFresh = false; Stable(l, s);
            Assert.IsFalse(l.DevicesReady); Assert.AreEqual(StaffSetupStage.Devices, l.Stage);
            Assert.IsTrue(l.ConfirmPosition(s)); Assert.AreEqual(StaffSetupStage.Welcome, l.Stage);
            l.VisitorResetCompleted(); s.generation = l.Generation; Stable(l, s);
            Assert.IsFalse(l.ReadyToHandOff); Assert.IsFalse(l.HandOff(s)); Assert.IsFalse(l.ChecklistComplete);
            s.cameras[1] = Camera(); s.tabletFresh = true; Stable(l, s);
            Assert.IsTrue(l.ChecklistComplete); Assert.IsTrue(l.HandOff(s));
        }
        [Test] public void StageCodeFollowsSetupVisitorAndRunStages()
        {
            var l = new StaffSetupLogic(); var s = Good(l); s.visitorSettingsApplied = false;
            s.visitorBriefingCompleted = false; Stable(l, s);
            Assert.AreEqual("setup", l.StageCode(false));
            l.ConfirmPosition(s); Assert.AreEqual("reset", l.StageCode(false));
            l.VisitorResetCompleted(); s.generation = l.Generation; l.Observe(s, 0f);
            Assert.AreEqual("settings", l.StageCode(false));
            s.visitorSettingsApplied = true; l.Observe(s, 0f); Assert.AreEqual("explanation", l.StageCode(false));
            s.visitorBriefingCompleted = true; l.Observe(s, 0f); Assert.AreEqual("ready", l.StageCode(false));
            Assert.IsTrue(l.HandOff(s)); Assert.AreEqual("handedOff", l.StageCode(false));
            Assert.IsTrue(l.TryBeginExperience(s));
            Assert.AreEqual("playing", l.StageCode(false)); Assert.AreEqual("ended", l.StageCode(true));
            l.NewVisitor(); s.generation = l.Generation; s.positionInvalid = true; l.Observe(s, 0f);
            Assert.AreEqual("alignment", l.StageCode(true));
        }
        [Test] public void PositionCodeDistinguishesNeededAligningConfirmedAndRecenter()
        {
            var l = new StaffSetupLogic(); var s = Good(l); Stable(l, s);
            Assert.AreEqual("needed", l.PositionCode);
            s.registrationActive = true; l.Observe(s, 0f); Assert.AreEqual("aligning", l.PositionCode);
            s.registrationActive = false; l.Observe(s, 0f); l.ConfirmPosition(s);
            Assert.AreEqual("confirmed", l.PositionCode);
            s.positionInvalid = true; l.Observe(s, 0f); Assert.AreEqual("recenter", l.PositionCode);
        }
        [TestCase(SetupDeviceState.Ready, "ok")]
        [TestCase(SetupDeviceState.Unknown, "checking")]
        [TestCase(SetupDeviceState.Checking, "checking")]
        [TestCase(SetupDeviceState.Trouble, "trouble")]
        public void StateCodeMapsDeviceStates(SetupDeviceState state, string code)
            => Assert.AreEqual(code, StaffSetupLogic.StateCode(state));
        [TestCase(SetupCameraProblem.None, "")]
        [TestCase(SetupCameraProblem.NoStream, "nostream")]
        [TestCase(SetupCameraProblem.StaleFrame, "stale")]
        [TestCase(SetupCameraProblem.IdentityUnknown, "identity")]
        [TestCase(SetupCameraProblem.WrongCamera, "wrongcam")]
        [TestCase(SetupCameraProblem.WrongShow, "wrongshow")]
        public void ProblemCodeMapsCameraProblems(SetupCameraProblem problem, string code)
            => Assert.AreEqual(code, StaffSetupLogic.ProblemCode(problem));
        [TestCase(false, false, false, true)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, true, false)]
        [TestCase(true, true, false, true)]
        public void TabletResetWaitsForFinishedOutro(bool started, bool runFinished, bool outroPlaying, bool expected)
            => Assert.AreEqual(expected, StaffSetupLogic.CanResetFromTablet(started, runFinished, outroPlaying));
        [Test] public void StartCanFireOnlyOnceForTheVisitor()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l);
            Assert.IsTrue(l.TryBeginExperience(s)); Assert.IsFalse(l.TryBeginExperience(s));
        }
        [Test] public void FirstVisitorMustResetAfterAlignmentEvenIfOldSettingsLookApplied()
        {
            var l = new StaffSetupLogic(); var s = Good(l); Stable(l, s);
            l.ConfirmPosition(s);
            Assert.IsFalse(l.HandOff(s)); Assert.IsFalse(l.CanBeginExperience);
            l.VisitorResetCompleted(); s.generation = l.Generation; s.visitorSettingsApplied = false;
            Assert.IsFalse(l.HandOff(s)); Assert.AreEqual(VisitorPreparationStage.SettingsRequired, l.VisitorStage);
            s.visitorSettingsApplied = true; Assert.IsTrue(l.HandOff(s));
        }
        [Test] public void EveryCompletedResetKeepsAlignmentButRequiresNewAppliedSettingsAndHandOff()
        {
            var l = new StaffSetupLogic(); var old = HandedOff(l); l.TryBeginExperience(old);
            for (int i = 0; i < 2; i++)
            {
                l.VisitorResetCompleted(); var s = Good(l); s.visitorSettingsApplied = false;
                l.Observe(s, 0f); Assert.IsTrue(l.PositionConfirmed); Assert.IsTrue(l.CamerasReady);
                Assert.AreEqual(StaffSetupStage.Welcome, l.Stage); Assert.IsFalse(l.TryBeginExperience(s));
                Assert.IsFalse(l.Observe(old, 0f));
                s.visitorSettingsApplied = true; Assert.IsTrue(l.HandOff(s));
                Assert.IsTrue(l.TryBeginExperience(s)); old = s;
            }
        }
        [Test] public void ResetBeforePositionConfirmationDoesNotSatisfyRequiredOrder()
        {
            var l = new StaffSetupLogic(); l.VisitorResetCompleted(); var s = Good(l); Stable(l, s);
            l.ConfirmPosition(s);
            Assert.AreEqual(VisitorPreparationStage.ResetRequired, l.VisitorStage); Assert.IsFalse(l.HandOff(s));
        }
        [Test] public void ReconnectAndResumeCannotSupplyMissingVisitorSettings()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); l.VisitorResetCompleted();
            s.generation = l.Generation; s.visitorSettingsApplied = false; s.cameras[0].suspended = true;
            Stable(l, s); s.cameras[0].suspended = false; Stable(l, s);
            Assert.IsTrue(l.CamerasReady); Assert.IsFalse(l.HandOff(s)); Assert.IsTrue(l.BlocksVisitor);
        }
        [Test] public void ClearedAppliedSettingsRevokeHandOffBeforeStart()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l); s.visitorSettingsApplied = false;
            Assert.IsFalse(l.TryBeginExperience(s)); Assert.IsTrue(l.BlocksVisitor);
        }
        [Test] public void BlockedTriggerHoldNeedsFreshRelease()
        {
            var mode = new ControllerModeLogic();
            for (int i = 0; i < 12; i++) mode.Tick(new ControllerModeLogic.Frame
                { triggerHeld = true, triggerBlocked = i < 4, deltaTime = .25f });
            Assert.AreEqual(ControllerModeLogic.Mode.Normal, mode.Current);
            mode.Tick(new ControllerModeLogic.Frame { deltaTime = .25f });
            for (int i = 0; i < 8; i++) mode.Tick(new ControllerModeLogic.Frame { triggerHeld = true, deltaTime = .25f });
            Assert.AreEqual(ControllerModeLogic.Mode.Registration, mode.Current);
        }
        [Test] public void ExplanationIsSeparateFromSettingsAndRedoRevokesHandOff()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l);
            s.visitorBriefingCompleted = false; l.Observe(s, 0f);
            Assert.AreEqual(StaffSetupStage.Welcome, l.Stage);
            Assert.AreEqual(VisitorPreparationStage.ExplanationRequired, l.VisitorStage);
            Assert.IsFalse(l.HandOff(s)); Assert.IsFalse(l.TryBeginExperience(s));
            s.visitorBriefingCompleted = true; Assert.IsFalse(l.TryBeginExperience(s));
            Assert.IsTrue(l.HandOff(s)); Assert.IsFalse(l.Started);
            Assert.IsTrue(l.TryBeginExperience(s));
        }
        [Test] public void BriefingDisconnectRecoveryRetainsValidResetPositionAndCompletion()
        {
            var l = new StaffSetupLogic(); var s = HandedOff(l);
            s.tabletFresh = false; s.cameras[0].suspended = true; l.Observe(s, 0f);
            Assert.IsTrue(l.PositionConfirmed); Assert.AreEqual(VisitorPreparationStage.Ready, l.VisitorStage);
            Assert.IsFalse(l.TryBeginExperience(s));
            s.cameras[0].suspended = false; Stable(l, s);
            Assert.IsTrue(l.CanBeginExperience); Assert.IsFalse(l.Started);
        }
    }
    public sealed class StaffSetupButtonLogicTests
    {
        [Test] public void HmdRemovalWhileHeldCannotConfirmOnRewear()
        {
            var b = new StaffSetupButtonLogic(); b.Tick(false, 0f, true);
            b.Tick(true, .1f, true); b.Tick(true, 0f, false);
            b.Tick(true, .1f, true);
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, 0f, true));
            b.Tick(true, .1f, true);
            Assert.AreEqual(StaffSetupButtonAction.Continue, b.Tick(false, 0f, true));
        }
        [TestCase(.5f, StaffSetupButtonAction.Continue)]
        [TestCase(.75f, StaffSetupButtonAction.None)]
        public void MediumHoldDoesNotBecomeShortPress(float seconds, StaffSetupButtonAction expected)
        {
            var b = new StaffSetupButtonLogic(); b.Tick(false, 0f, true);
            for (float t = 0f; t < seconds; t += .25f) b.Tick(true, .25f, true);
            Assert.AreEqual(expected, b.Tick(false, 0f, true));
        }
        [Test] public void SettingsBecomeAppliedWhileBHeldRequireReleaseAndFreshPress()
        {
            var b = new StaffSetupButtonLogic(); b.Tick(false, 0f, true, false);
            b.Tick(true, .1f, true, false); b.Tick(true, .1f, true, true);
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, 0f, true, true));
            b.Tick(true, .1f, true, true);
            Assert.AreEqual(StaffSetupButtonAction.Continue, b.Tick(false, 0f, true, true));
        }
        [Test] public void ShortReleaseAdvancesOnce_LongRecallDoesNotAdvanceOnRelease()
        {
            var b = new StaffSetupButtonLogic(); b.Tick(false, 0f, true);
            b.Tick(true, .1f, true); Assert.AreEqual(StaffSetupButtonAction.Continue, b.Tick(false, 0f, true));
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, 0f, true));
            for (int i = 0; i < 3; i++) Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(true, .25f, true));
            Assert.AreEqual(StaffSetupButtonAction.RecallScreen, b.Tick(true, .25f, true));
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(true, .25f, true));
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, 0f, true));
        }
        [Test] public void DisconnectAndModeChangeRequireFreshRelease()
        {
            var b = new StaffSetupButtonLogic(); b.Tick(false, 0f, true); b.Tick(true, .1f, true);
            b.Tick(true, 0f, false); b.Tick(true, .25f, true);
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, .1f, true));
        }
        [Test] public void RegistrationConfirmHeldThenExitCannotRecallOrAdvanceSetup()
        {
            var b = new StaffSetupButtonLogic(); b.Tick(true, .25f, false);
            for (int i = 0; i < 8; i++) Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(true, .25f, true));
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, 0f, true));
            b.Tick(true, .1f, true); Assert.AreEqual(StaffSetupButtonAction.Continue, b.Tick(false, 0f, true));
        }
        [Test] public void TriggerCompetitionCannotReleaseIntoContinue()
        {
            var b = new StaffSetupButtonLogic(); b.Tick(false, 0f, true); b.Tick(true, .25f, true);
            b.Tick(true, .25f, false); // トリガーまたは位置合わせ入場が入力を占有
            Assert.AreEqual(StaffSetupButtonAction.None, b.Tick(false, 0f, true));
        }
    }
}
