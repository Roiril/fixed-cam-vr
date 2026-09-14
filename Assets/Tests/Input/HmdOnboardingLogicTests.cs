#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    public sealed class HmdOnboardingLogicTests
    {
        private const float Dt = 1f / 90f;

        private static HmdOnboardingInput Frame(bool connected = true, bool tracked = true,
                                                bool x = false, bool y = false,
                                                bool titleAvailable = true, bool titleReady = false,
                                                bool titleDone = false)
            => new HmdOnboardingInput
            {
                dt = Dt,
                hmdPresent = true,
                leftConnected = connected,
                leftPositionValid = tracked,
                xHeld = x,
                yHeld = y,
                titleAvailable = titleAvailable,
                titleReady = titleReady,
                titleDone = titleDone,
            };

        private static HmdOnboardingAction Run(HmdOnboardingLogic l, float sec,
                                               HmdOnboardingInput input)
        {
            HmdOnboardingAction action = HmdOnboardingAction.None;
            for (float t = 0f; t < sec; t += Dt)
            {
                HmdOnboardingAction next = l.Tick(input);
                if (next != HmdOnboardingAction.None) action = next;
            }
            return action;
        }

        private static void ReachTutorial(HmdOnboardingLogic l)
        {
            Run(l, HmdOnboardingLogic.GreetingSec + HmdOnboardingLogic.ControllerStableSec + 0.2f, Frame());
            Run(l, HmdOnboardingLogic.ControllerConfirmedSec + 0.2f, Frame());
            Assert.AreEqual(HmdOnboardingStage.Tutorial, l.Stage);
        }

        private static void CompleteTutorial(HmdOnboardingLogic l)
        {
            ReachTutorial(l);
            l.Tick(Frame());
            Assert.AreEqual(HmdOnboardingAction.TutorialAccepted,
                            Run(l, HmdOnboardingLogic.TutorialHoldSec + 0.1f, Frame(x: true)));
            Run(l, HmdOnboardingLogic.TutorialAcceptedMinSec + 0.1f, Frame());
            Assert.AreEqual(HmdOnboardingStage.Reminder, l.Stage);
        }

        private static HmdOnboardingAction ReachTitle(HmdOnboardingLogic l,
                                                       HmdOnboardingInput input)
        {
            Run(l, HmdOnboardingLogic.ReminderSec + 0.1f, input);
            Assert.AreEqual(HmdOnboardingStage.TitleTransition, l.Stage,
                            "通信面を畳む前に題字を重ねてはいけない");
            return Run(l, HmdOnboardingLogic.TitleTransitionSec + 0.1f, input);
        }

        [Test]
        public void ControllerMustBeTrackedContinuouslyBeforeTheTutorial()
        {
            var l = new HmdOnboardingLogic();
            Run(l, HmdOnboardingLogic.GreetingSec + 0.1f, Frame(tracked: false));
            Assert.AreEqual(HmdOnboardingStage.WaitingForController, l.Stage);
            Assert.AreEqual(HmdOnboardingPrompt.ControllerUntracked, l.Prompt);

            Run(l, HmdOnboardingLogic.ControllerStableSec * 0.7f, Frame());
            Run(l, 0.1f, Frame(tracked: false));
            Run(l, HmdOnboardingLogic.ControllerStableSec * 0.7f, Frame());
            Assert.AreEqual(HmdOnboardingStage.WaitingForController, l.Stage,
                            "途切れた位置認識を足してはいけない");
            Run(l, HmdOnboardingLogic.ControllerStableSec, Frame());
            Assert.AreEqual(HmdOnboardingStage.ControllerConfirmed, l.Stage);
        }

        [Test]
        public void FifteenSecondsOfTroubleAsksForStaff()
        {
            var l = new HmdOnboardingLogic();
            Run(l, HmdOnboardingLogic.ControllerTroubleSec + 0.2f,
                Frame(connected: false, tracked: false));
            Assert.AreEqual(HmdOnboardingPrompt.ControllerStaff, l.Prompt);
        }

        [Test]
        public void TutorialAcceptsOnlyOneContinuousXOrYHold()
        {
            var l = new HmdOnboardingLogic();
            ReachTutorial(l);
            l.Tick(Frame());

            Run(l, HmdOnboardingLogic.TutorialHoldSec * 0.6f, Frame(x: true));
            Run(l, HmdOnboardingLogic.TutorialHoldSec * 0.6f, Frame(y: true));
            Assert.AreEqual(HmdOnboardingStage.Tutorial, l.Stage,
                            "XからYへ持ち替えた時間を継ぎ足してはいけない");

            Assert.AreEqual(HmdOnboardingAction.TutorialAccepted,
                            Run(l, HmdOnboardingLogic.TutorialHoldSec + 0.1f, Frame(y: true)));
            Assert.AreEqual(HmdOnboardingStage.TutorialAccepted, l.Stage);
        }

        [Test]
        public void TutorialDoesNotAcceptTriggerOrGripBecauseTheyAreNotInputs()
        {
            var l = new HmdOnboardingLogic();
            ReachTutorial(l);
            Run(l, HmdOnboardingLogic.TutorialHoldSec * 2f, Frame());
            Assert.AreEqual(HmdOnboardingStage.Tutorial, l.Stage);
            Assert.AreEqual(0f, l.TutorialProgress01);
        }

        [Test]
        public void DisconnectDuringTutorialResetsProgressAndRequiresRelease()
        {
            var l = new HmdOnboardingLogic();
            ReachTutorial(l);
            l.Tick(Frame());
            Run(l, 0.7f, Frame(x: true));
            l.Tick(Frame(connected: false, tracked: false, x: true));
            Assert.AreEqual(0f, l.TutorialProgress01);
            Assert.AreEqual(HmdOnboardingPrompt.TutorialReconnect, l.Prompt);

            Run(l, HmdOnboardingLogic.ControllerStableSec + 0.1f, Frame(x: true));
            Assert.IsTrue(l.NeedsRelease);
            Run(l, 2f, Frame(x: true));
            Assert.AreEqual(HmdOnboardingStage.Tutorial, l.Stage,
                            "再接続時の持ち越し入力で成功してはいけない");
            l.Tick(Frame());
            Assert.AreEqual(HmdOnboardingAction.TutorialAccepted,
                            Run(l, HmdOnboardingLogic.TutorialHoldSec + 0.1f, Frame(x: true)));
        }

        [Test]
        public void DisconnectAfterTutorialKeepsTheSuccessButBlocksTheTitle()
        {
            var l = new HmdOnboardingLogic();
            ReachTutorial(l);
            l.Tick(Frame());
            Run(l, HmdOnboardingLogic.TutorialHoldSec + 0.1f, Frame(x: true));
            Run(l, 2f, Frame(connected: false, tracked: false));
            Assert.IsTrue(l.TutorialSucceeded);
            Assert.AreEqual(HmdOnboardingStage.TutorialAccepted, l.Stage);
            Assert.AreEqual(HmdOnboardingPrompt.TutorialReconnect, l.Prompt);

            Run(l, HmdOnboardingLogic.ControllerStableSec + HmdOnboardingLogic.TutorialAcceptedMinSec + 0.2f,
                Frame());
            Assert.AreEqual(HmdOnboardingStage.Reminder, l.Stage);
        }

        [Test]
        public void TitleNeedsANewShortPressAndNeverAcceptsAHold()
        {
            var l = new HmdOnboardingLogic();
            CompleteTutorial(l);
            Assert.AreEqual(HmdOnboardingAction.ShowTitle,
                            ReachTitle(l, Frame(x: true)));
            Assert.AreEqual(HmdOnboardingStage.Title, l.Stage);

            Run(l, 1f, Frame(x: true, titleReady: true));
            Assert.AreEqual(HmdOnboardingStage.Title, l.Stage,
                            "練習からの持ち越しでタイトルを閉じてはいけない");
            l.Tick(Frame(titleReady: true));
            Run(l, HmdOnboardingLogic.TapMaxSec + 0.2f, Frame(y: true, titleReady: true));
            l.Tick(Frame(titleReady: true));
            Assert.AreEqual(HmdOnboardingStage.Title, l.Stage,
                            "長押しを開始操作として受けてはいけない");

            Run(l, 0.2f, Frame(x: true, titleReady: true));
            Assert.AreEqual(HmdOnboardingAction.DismissTitle,
                            l.Tick(Frame(titleReady: true)));
            Assert.AreEqual(HmdOnboardingStage.TitleDismissing, l.Stage);
        }

        [Test]
        public void StartIsAuthorizedOnlyAfterTitleIsGoneAndButtonsAreReleased()
        {
            var l = new HmdOnboardingLogic();
            CompleteTutorial(l);
            ReachTitle(l, Frame());
            Run(l, 0.2f, Frame(x: true, titleReady: true));
            l.Tick(Frame(titleReady: true));
            Assert.IsFalse(l.StartAuthorized);
            Run(l, 2f, Frame(titleReady: true));
            Assert.IsFalse(l.StartAuthorized);
            l.Tick(Frame(titleReady: true, titleDone: true));
            Assert.IsTrue(l.StartAuthorized);
        }

        [Test]
        public void MissingTitleFailsSoftAfterTheTutorial()
        {
            var l = new HmdOnboardingLogic();
            CompleteTutorial(l);
            ReachTitle(l, Frame(titleAvailable: false));
            l.Tick(Frame(titleAvailable: false));
            Assert.IsTrue(l.StartAuthorized);
        }

        [Test]
        public void StaffForceCloseStillLeavesTheOnboarding()
        {
            var l = new HmdOnboardingLogic();
            CompleteTutorial(l);
            ReachTitle(l, Frame());
            l.Tick(Frame(titleDone: true));
            Assert.IsTrue(l.StartAuthorized);
        }

        [Test]
        public void AutomationCanCompleteWithoutForgingAReport()
        {
            var l = new HmdOnboardingLogic();
            l.CompleteForAutomation();
            Assert.IsTrue(l.StartAuthorized);
            Assert.IsFalse(l.TutorialSucceeded,
                           "自動走行の退避路を体験者の練習成功として記録してはいけない");
        }
    }
}
