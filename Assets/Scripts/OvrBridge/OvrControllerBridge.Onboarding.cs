#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Input;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.Replay;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    // OvrControllerBridge の一部: HMD を被った後の導入（左の接続確認 → 報告の練習 → 名乗り → 題字）を画面と連絡の面へ映す。状態機械そのものは HmdOnboardingLogic、入力を渡すのは Update の「導入」の節。
    // 全体の入口とファイルの一覧は OvrControllerBridge.cs の先頭。
    public sealed partial class OvrControllerBridge
    {
        /// <summary>導入の段（ツイン走行が人と同じ順にボタンを押すために読む）。</summary>
        public HmdOnboardingStage OnboardingStage => _onboarding.Stage;

        /// <summary>
        /// 導入の段をスタッフの面（<see cref="StatusHud"/>）の言葉にする。
        ///
        /// ⚠ <b>enum の名前をそのまま出さない</b>（開発語）。読むのは現場のスタッフで、
        /// 知りたいのは「いま何を待っているか」だけ。
        /// ⚠ 導入が終わったら空にする — 本編の 1 行目は周回なので、段が残ると嘘になる。
        /// </summary>
        private static string IntroStepLabel(HmdOnboardingStage stage) => stage switch
        {
            HmdOnboardingStage.Greeting => "名乗り",
            HmdOnboardingStage.WaitingForController => "左の接続待ち",
            HmdOnboardingStage.ControllerConfirmed => "報告の練習",
            HmdOnboardingStage.Tutorial => "報告の練習",
            HmdOnboardingStage.TutorialAccepted => "報告の練習",
            HmdOnboardingStage.Reminder => "報告の練習",
            HmdOnboardingStage.TitleTransition => "題字",
            HmdOnboardingStage.Title => "題字",
            HmdOnboardingStage.TitleDismissing => "題字",
            _ => "",   // Complete
        };

        // 直近に push した導入の段（変わったときだけ書きに行く）。
        private HmdOnboardingStage _pushedIntroStage = (HmdOnboardingStage)(-1);

        private void ApplyOnboardingPresentation()
        {
            if (_staffSetup == null || _staffSetup.BlocksVisitor)
            {
                comms?.ClearOnboardingNotice();
                titleScreen?.SetStartGuidance(TitleStartGuidance.Hidden);
                return;
            }
            // 導入の段をスタッフの面へ。⚠ **導入の状態機械は Assembly-CSharp 側にしか無い**ので、
            //    StatusHud からは見えない（Diagnostics は OVR も導入の入力も知らない）。
            if (_onboarding.Stage != _pushedIntroStage)
            {
                _pushedIntroStage = _onboarding.Stage;
                statusHud?.SetIntroStep(IntroStepLabel(_pushedIntroStage));
            }

            CommsNotice notice = NoticeFor(_onboarding.Prompt);
            if (notice == CommsNotice.None) comms?.ClearOnboardingNotice();
            else comms?.SetOnboardingNotice(notice);

            TitleStartGuidance guidance = TitleStartGuidance.Hidden;
            if (_onboarding.Stage == HmdOnboardingStage.Title)
            {
                if (!_lConnected) guidance = TitleStartGuidance.Reconnect;
                else if (!_lTracked) guidance = TitleStartGuidance.MoveIntoView;
                else if (_onboarding.NeedsRelease) guidance = TitleStartGuidance.Release;
                else if (_onboarding.TitleLongPressHint) guidance = TitleStartGuidance.ShortPress;
                else guidance = TitleStartGuidance.Ready;
            }
            titleScreen?.SetStartGuidance(guidance);
        }

        private static CommsNotice NoticeFor(HmdOnboardingPrompt prompt) => prompt switch
        {
            HmdOnboardingPrompt.Greeting => CommsNotice.Greeting,
            HmdOnboardingPrompt.ControllerDisconnected => CommsNotice.ControllerDisconnected,
            HmdOnboardingPrompt.ControllerUntracked => CommsNotice.ControllerUntracked,
            HmdOnboardingPrompt.ControllerStaff => CommsNotice.ControllerStaff,
            HmdOnboardingPrompt.ControllerConfirmed => CommsNotice.ControllerConfirmed,
            HmdOnboardingPrompt.Tutorial => CommsNotice.Tutorial,
            HmdOnboardingPrompt.TutorialShort => CommsNotice.TutorialShort,
            HmdOnboardingPrompt.TutorialAccepted => CommsNotice.TutorialAccepted,
            HmdOnboardingPrompt.TutorialReconnect => CommsNotice.TutorialReconnect,
            HmdOnboardingPrompt.Reminder => CommsNotice.TutorialReminder,
            _ => CommsNotice.None,
        };
    }
}
