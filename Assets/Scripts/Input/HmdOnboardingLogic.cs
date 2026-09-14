#nullable enable

namespace FixedCamVr.Input
{
    /// <summary>HMDを装着してから本編へ渡すまでの導入段。</summary>
    public enum HmdOnboardingStage
    {
        Greeting,
        WaitingForController,
        ControllerConfirmed,
        Tutorial,
        TutorialAccepted,
        Reminder,
        TitleTransition,
        Title,
        TitleDismissing,
        Complete,
    }

    /// <summary>連絡面へ出す導入文。文面は実行体が言語ごとに持つ。</summary>
    public enum HmdOnboardingPrompt
    {
        None,
        Greeting,
        ControllerDisconnected,
        ControllerUntracked,
        ControllerStaff,
        ControllerConfirmed,
        Tutorial,
        TutorialShort,
        TutorialAccepted,
        TutorialReconnect,
        Reminder,
    }

    /// <summary>1フレームだけ発生する導入の指示。</summary>
    public enum HmdOnboardingAction
    {
        None,
        TutorialAccepted,
        ShowTitle,
        DismissTitle,
    }

    /// <summary>導入が読む観測値。</summary>
    public struct HmdOnboardingInput
    {
        public float dt;
        public bool hmdPresent;
        public bool leftConnected;
        public bool leftPositionValid;
        public bool xHeld;
        public bool yHeld;
        public bool titleAvailable;
        public bool titleReady;
        public bool titleDone;
    }

    /// <summary>
    /// 左コントローラーの確認、報告練習、タイトル開始を一続きに扱う純ロジック。
    /// 練習の成功は外へ報告せず、<see cref="HmdOnboardingAction.TutorialAccepted"/>だけを返す。
    /// </summary>
    public sealed class HmdOnboardingLogic
    {
        public const float MaxStepSec = 0.25f;
        public const float ControllerStableSec = 0.5f;
        public const float ControllerTroubleSec = 15f;
        public const float GreetingSec = 3f;
        public const float ControllerConfirmedSec = 1.2f;
        public const float TutorialHoldSec = VisitorMarkHoldLogic.DefaultHoldSec;
        public const float TutorialAcceptedMinSec = 1.0f;
        public const float ReminderSec = 4f;
        public const float TitleTransitionSec = 0.5f;
        public const float TapMaxSec = 0.5f;
        public const float HintSec = 1.6f;

        private HmdOnboardingStage _stage = HmdOnboardingStage.Greeting;
        private float _stageSec;
        private float _readySec;
        private float _troubleSec;
        private float _xSec;
        private float _ySec;
        private bool _xWasHeld;
        private bool _yWasHeld;
        private bool _needsRelease;
        private float _shortHintSec;
        private float _titlePressSec;
        private bool _titleWasHeld;
        private float _titleLongHintSec;
        private bool _tutorialSucceeded;

        public HmdOnboardingStage Stage => _stage;
        public bool StartAuthorized => _stage == HmdOnboardingStage.Complete;
        public float TutorialProgress01 => Clamp01(Max(_xSec, _ySec) / TutorialHoldSec);
        public bool TutorialSucceeded => _tutorialSucceeded;
        public bool NeedsRelease => _needsRelease;
        public bool TitleLongPressHint => _titleLongHintSec > 0f;

        public HmdOnboardingPrompt Prompt
        {
            get
            {
                if (_stage == HmdOnboardingStage.Greeting) return HmdOnboardingPrompt.Greeting;
                if (_stage == HmdOnboardingStage.WaitingForController)
                {
                    if (_troubleSec >= ControllerTroubleSec) return HmdOnboardingPrompt.ControllerStaff;
                    return _lastConnected
                        ? HmdOnboardingPrompt.ControllerUntracked
                        : HmdOnboardingPrompt.ControllerDisconnected;
                }
                if (_stage == HmdOnboardingStage.ControllerConfirmed)
                    return HmdOnboardingPrompt.ControllerConfirmed;
                if (_stage == HmdOnboardingStage.Tutorial)
                {
                    if (!ControllerReady) return HmdOnboardingPrompt.TutorialReconnect;
                    return _shortHintSec > 0f
                        ? HmdOnboardingPrompt.TutorialShort
                        : HmdOnboardingPrompt.Tutorial;
                }
                if (_stage == HmdOnboardingStage.TutorialAccepted)
                    return ControllerReady
                        ? HmdOnboardingPrompt.TutorialAccepted
                        : HmdOnboardingPrompt.TutorialReconnect;
                if (_stage == HmdOnboardingStage.Reminder)
                    return ControllerReady
                        ? HmdOnboardingPrompt.Reminder
                        : HmdOnboardingPrompt.TutorialReconnect;
                return HmdOnboardingPrompt.None;
            }
        }

        private bool _lastConnected;
        private bool _lastTracked;
        private bool ControllerReady => _lastConnected && _lastTracked && _readySec >= ControllerStableSec;

        public void Reset()
        {
            _stage = HmdOnboardingStage.Greeting;
            _stageSec = 0f;
            _readySec = 0f;
            _troubleSec = 0f;
            _xSec = 0f;
            _ySec = 0f;
            _xWasHeld = false;
            _yWasHeld = false;
            _needsRelease = true;
            _shortHintSec = 0f;
            _titlePressSec = 0f;
            _titleWasHeld = false;
            _titleLongHintSec = 0f;
            _tutorialSucceeded = false;
            _lastConnected = false;
            _lastTracked = false;
        }

        /// <summary>コントローラーを持たない自動走行だけが使う退避路。</summary>
        public void CompleteForAutomation()
        {
            Enter(HmdOnboardingStage.Complete, false);
            _tutorialSucceeded = false;
            _readySec = 0f;
            _troubleSec = 0f;
        }

        public HmdOnboardingAction Tick(in HmdOnboardingInput input)
        {
            float dt = Clamp(input.dt, 0f, MaxStepSec);
            _lastConnected = input.leftConnected;
            _lastTracked = input.leftPositionValid;
            bool rawReady = input.leftConnected && input.leftPositionValid;
            bool anyHeld = input.xHeld || input.yHeld;

            if (input.hmdPresent)
            {
                if (rawReady)
                {
                    _readySec += dt;
                    _troubleSec = 0f;
                }
                else
                {
                    _readySec = 0f;
                    _troubleSec += dt;
                }
            }
            else
            {
                _readySec = 0f;
            }

            if (_shortHintSec > 0f) _shortHintSec = Max(0f, _shortHintSec - dt);
            if (_titleLongHintSec > 0f) _titleLongHintSec = Max(0f, _titleLongHintSec - dt);
            if (!input.hmdPresent) return HmdOnboardingAction.None;

            switch (_stage)
            {
                case HmdOnboardingStage.Greeting:
                    _stageSec += dt;
                    if (_stageSec >= GreetingSec)
                        Enter(ControllerReady
                            ? HmdOnboardingStage.ControllerConfirmed
                            : HmdOnboardingStage.WaitingForController, anyHeld);
                    break;

                case HmdOnboardingStage.WaitingForController:
                    if (ControllerReady)
                        Enter(HmdOnboardingStage.ControllerConfirmed, anyHeld);
                    break;

                case HmdOnboardingStage.ControllerConfirmed:
                    if (!rawReady)
                    {
                        Enter(HmdOnboardingStage.WaitingForController, anyHeld);
                        break;
                    }
                    _stageSec += dt;
                    if (_stageSec >= ControllerConfirmedSec && !anyHeld)
                        Enter(HmdOnboardingStage.Tutorial, false);
                    break;

                case HmdOnboardingStage.Tutorial:
                    if (!ControllerReady)
                    {
                        ResetTutorialPress();
                        _needsRelease = true;
                        break;
                    }
                    if (_needsRelease)
                    {
                        ResetTutorialPress();
                        if (!anyHeld) _needsRelease = false;
                        break;
                    }

                    float xBefore = _xSec;
                    float yBefore = _ySec;
                    _xSec = input.xHeld ? _xSec + dt : 0f;
                    _ySec = input.yHeld ? _ySec + dt : 0f;
                    bool shortRelease = (_xWasHeld && !input.xHeld && xBefore < TutorialHoldSec)
                                        || (_yWasHeld && !input.yHeld && yBefore < TutorialHoldSec);
                    _xWasHeld = input.xHeld;
                    _yWasHeld = input.yHeld;
                    if (_xSec >= TutorialHoldSec || _ySec >= TutorialHoldSec)
                    {
                        _tutorialSucceeded = true;
                        Enter(HmdOnboardingStage.TutorialAccepted, anyHeld);
                        return HmdOnboardingAction.TutorialAccepted;
                    }
                    if (shortRelease && !anyHeld) _shortHintSec = HintSec;
                    break;

                case HmdOnboardingStage.TutorialAccepted:
                    _stageSec += dt;
                    if (!ControllerReady)
                    {
                        _needsRelease = true;
                        break;
                    }
                    if (!anyHeld) _needsRelease = false;
                    if (!_needsRelease && _stageSec >= TutorialAcceptedMinSec)
                        Enter(HmdOnboardingStage.Reminder, false);
                    break;

                case HmdOnboardingStage.Reminder:
                    if (!ControllerReady)
                    {
                        _needsRelease = true;
                        break;
                    }
                    if (_needsRelease)
                    {
                        if (!anyHeld) _needsRelease = false;
                        break;
                    }
                    _stageSec += dt;
                    if (_stageSec >= ReminderSec)
                        Enter(HmdOnboardingStage.TitleTransition, anyHeld);
                    break;

                case HmdOnboardingStage.TitleTransition:
                    // 通信面を先に畳む。題字と重ねず、同じ黒の中で静かに受け渡す。
                    _stageSec += dt;
                    if (_stageSec >= TitleTransitionSec)
                    {
                        Enter(HmdOnboardingStage.Title, anyHeld);
                        return HmdOnboardingAction.ShowTitle;
                    }
                    break;

                case HmdOnboardingStage.Title:
                    if (!input.titleAvailable || input.titleDone)
                    {
                        Enter(HmdOnboardingStage.Complete, anyHeld);
                        break;
                    }
                    if (!ControllerReady || !input.titleReady)
                    {
                        _needsRelease = true;
                        _titlePressSec = 0f;
                        _titleWasHeld = anyHeld;
                        break;
                    }
                    if (_needsRelease)
                    {
                        if (!anyHeld)
                        {
                            _needsRelease = false;
                            _titleWasHeld = false;
                            _titlePressSec = 0f;
                        }
                        break;
                    }
                    if (anyHeld)
                    {
                        _titlePressSec += dt;
                        _titleWasHeld = true;
                    }
                    else if (_titleWasHeld)
                    {
                        float pressSec = _titlePressSec;
                        _titleWasHeld = false;
                        _titlePressSec = 0f;
                        if (pressSec > 0f && pressSec <= TapMaxSec)
                        {
                            Enter(HmdOnboardingStage.TitleDismissing, false);
                            return HmdOnboardingAction.DismissTitle;
                        }
                        _titleLongHintSec = HintSec;
                    }
                    break;

                case HmdOnboardingStage.TitleDismissing:
                    if (input.titleDone && !anyHeld)
                        Enter(HmdOnboardingStage.Complete, false);
                    break;
            }
            return HmdOnboardingAction.None;
        }

        private void Enter(HmdOnboardingStage next, bool needsRelease)
        {
            _stage = next;
            _stageSec = 0f;
            _needsRelease = needsRelease;
            ResetTutorialPress();
            _titlePressSec = 0f;
            _titleWasHeld = needsRelease;
        }

        private void ResetTutorialPress()
        {
            _xSec = 0f;
            _ySec = 0f;
            _xWasHeld = false;
            _yWasHeld = false;
        }

        private static float Clamp01(float v) => Clamp(v, 0f, 1f);
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        private static float Max(float a, float b) => a > b ? a : b;
    }
}
