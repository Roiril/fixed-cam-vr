#nullable enable
using System;

namespace FixedCamVr.Input
{
    public enum StaffSetupStage { Devices, Alignment, Welcome, HandedOff }
    public enum VisitorPreparationStage { ResetRequired, SettingsRequired, ExplanationRequired, Ready }
    public enum SetupDeviceState { Unknown, Checking, Ready, Trouble }
    public enum SetupCameraProblem { None, NoStream, StaleFrame, IdentityUnknown, WrongCamera, WrongShow }

    public struct SetupCameraEvidence
    {
        public bool connected, suspended, freshDecode, identityKnown, identityMatches;
        public long connection;
        public int endpoint;
        public SetupCameraProblem problem;
        public bool Valid => connected && !suspended && freshDecode && identityKnown && identityMatches;
    }

    /// <summary>現在の観測だけ。通信とフォーカス演出を独立させ、古い世代の入力を捨てる。</summary>
    public struct StaffSetupSnapshot
    {
        public int generation;
        public bool tabletFresh, settingsSettled, settingsRequested;
        public bool visitorSettingsApplied;
        public bool visitorBriefingCompleted;
        public bool contentReady, hasPosition, positionInvalid, registrationActive;
        public SetupCameraEvidence[] cameras;
    }

    public sealed class StaffSetupLogic
    {
        public const float StableSec = 0.5f;
        public const float FocusSec = 0.35f;
        public const float TroubleAfterSec = 5f;
        public const int CameraCount = 3;
        private readonly float[] _stable = new float[CameraCount];
        private readonly SetupCameraEvidence[] _previous = new SetupCameraEvidence[CameraCount];
        private readonly SetupDeviceState[] _states = new SetupDeviceState[CameraCount];
        private StaffSetupSnapshot _latest;
        private float _elapsed;
        private bool _tabletVerified, _positionConfirmed;
        private bool _started;
        private bool _visitorResetCompleted;
        public int Generation { get; private set; } = 1;
        public StaffSetupStage Stage { get; private set; }
        public bool PositionConfirmed => _positionConfirmed;
        public bool Started => _started;
        public bool SettingsSettled => _latest.settingsSettled;
        public VisitorPreparationStage VisitorStage => !_visitorResetCompleted ? VisitorPreparationStage.ResetRequired
            : !_latest.visitorSettingsApplied || !_latest.settingsSettled ? VisitorPreparationStage.SettingsRequired
            : !_latest.visitorBriefingCompleted ? VisitorPreparationStage.ExplanationRequired
            : VisitorPreparationStage.Ready;
        public int FocusIndex => Math.Min(3, (int)(_elapsed / FocusSec));
        public SetupDeviceState CameraState(int i) => _states[i];
        public SetupCameraProblem CameraProblem(int i) => _previous[i].problem;
        public SetupDeviceState TabletState => _latest.tabletFresh
            ? (_latest.settingsSettled ? SetupDeviceState.Ready : SetupDeviceState.Checking)
            : (_elapsed < TroubleAfterSec && !_tabletVerified ? SetupDeviceState.Unknown : SetupDeviceState.Trouble);
        public bool CamerasReady
        {
            get { for (int i = 0; i < CameraCount; i++) if (_states[i] != SetupDeviceState.Ready) return false; return true; }
        }
        public bool DevicesReady => CamerasReady && _latest.tabletFresh && _latest.settingsSettled;
        public bool ReadyToHandOff => VisitorStage == VisitorPreparationStage.Ready && DevicesReady && _positionConfirmed && _latest.hasPosition
                                      && !_latest.positionInvalid && !_latest.registrationActive && _latest.contentReady;
        public bool CanBeginExperience => Stage == StaffSetupStage.HandedOff && _tabletVerified
            && VisitorStage == VisitorPreparationStage.Ready
            && _positionConfirmed && _latest.hasPosition && !_latest.positionInvalid
            && !_latest.registrationActive && _latest.contentReady && CamerasReady && _latest.settingsSettled;
        public bool BlocksVisitor => !_started && !CanBeginExperience;

        public bool Observe(in StaffSetupSnapshot snapshot, float dt)
        {
            if (snapshot.generation != Generation) return false;
            _latest = snapshot;
            _elapsed += Math.Max(0f, Math.Min(dt, 0.25f));
            if (!snapshot.hasPosition || snapshot.positionInvalid) _positionConfirmed = false;
            if (snapshot.tabletFresh && snapshot.settingsSettled) _tabletVerified = true;
            for (int i = 0; i < CameraCount; i++)
            {
                SetupCameraEvidence e = snapshot.cameras != null && i < snapshot.cameras.Length
                    ? snapshot.cameras[i] : default;
                if (e.endpoint != _previous[i].endpoint || e.connection != _previous[i].connection || !e.Valid)
                    _stable[i] = 0f;
                if (e.Valid) _stable[i] += Math.Max(0f, Math.Min(dt, 0.25f));
                _states[i] = e.Valid
                    ? (_stable[i] >= StableSec ? SetupDeviceState.Ready : SetupDeviceState.Checking)
                    : (_elapsed < TroubleAfterSec && e.problem != SetupCameraProblem.WrongCamera
                        && e.problem != SetupCameraProblem.WrongShow && _states[i] != SetupDeviceState.Ready
                        && _states[i] != SetupDeviceState.Trouble ? SetupDeviceState.Unknown : SetupDeviceState.Trouble);
                _previous[i] = e;
            }
            // 通信喪失で済んだ位置合わせを消さない。基準変更だけを別の理由で戻す。
            if (!_started && (Stage == StaffSetupStage.Welcome || Stage == StaffSetupStage.HandedOff)
                && !_positionConfirmed) Stage = StaffSetupStage.Alignment;
            if (!_started && Stage == StaffSetupStage.HandedOff
                && (!snapshot.visitorSettingsApplied || !snapshot.visitorBriefingCompleted || !snapshot.settingsSettled))
                Stage = StaffSetupStage.Welcome;
            return true;
        }

        /// <summary>
        /// スタッフ画面の行（位置合わせ・カメラ・タブレット・素材）が全部そろったか。
        /// 体験者の準備（リセット・設定・説明）は含めない。そちらはタブレットで進む。
        /// </summary>
        public bool ChecklistComplete => _positionConfirmed && _latest.hasPosition && !_latest.positionInvalid
            && !_latest.registrationActive && CamerasReady && TabletState == SetupDeviceState.Ready && _latest.contentReady;

        // ---- タブレットへ渡す状態の語（/status の staffSetup）------------------------------
        // 機器確認と位置合わせは並行してよい（2026-10-10）。初回の位置確定前は Devices のまま "setup"。

        public const string StageSetup = "setup", StageAlignment = "alignment", StageReset = "reset",
            StageSettings = "settings", StageExplanation = "explanation", StageReady = "ready",
            StageHandedOff = "handedOff", StagePlaying = "playing", StageEnded = "ended";

        /// <summary>段の語。<paramref name="runFinished"/> は本編の相が Finished か。</summary>
        public string StageCode(bool runFinished)
        {
            if (_started) return runFinished ? StageEnded : StagePlaying;
            switch (Stage)
            {
                case StaffSetupStage.Devices: return StageSetup;
                case StaffSetupStage.Alignment: return StageAlignment;
                case StaffSetupStage.HandedOff: return StageHandedOff;
            }
            switch (VisitorStage)
            {
                case VisitorPreparationStage.ResetRequired: return StageReset;
                case VisitorPreparationStage.SettingsRequired: return StageSettings;
                case VisitorPreparationStage.ExplanationRequired: return StageExplanation;
                default: return StageReady;
            }
        }

        public const string PositionConfirmedCode = "confirmed", PositionNeededCode = "needed",
            PositionRecenterCode = "recenter", PositionAligningCode = "aligning";

        /// <summary>位置合わせの語。作業中 → 確定済み → 基準変更 → 未了の順に決める。</summary>
        public string PositionCode => _latest.registrationActive ? PositionAligningCode
            : _positionConfirmed ? PositionConfirmedCode
            : _latest.positionInvalid ? PositionRecenterCode : PositionNeededCode;

        public const string StateOk = "ok", StateChecking = "checking", StateTrouble = "trouble";

        /// <summary>機器の状態の語。Ready → ok / Unknown・Checking → checking / Trouble → trouble。</summary>
        public static string StateCode(SetupDeviceState state)
            => state == SetupDeviceState.Ready ? StateOk : state == SetupDeviceState.Trouble ? StateTrouble : StateChecking;

        /// <summary>カメラの異常の語。異常が無ければ空。</summary>
        public static string ProblemCode(SetupCameraProblem problem)
        {
            switch (problem)
            {
                case SetupCameraProblem.NoStream: return "nostream";
                case SetupCameraProblem.StaleFrame: return "stale";
                case SetupCameraProblem.IdentityUnknown: return "identity";
                case SetupCameraProblem.WrongCamera: return "wrongcam";
                case SetupCameraProblem.WrongShow: return "wrongshow";
                default: return "";
            }
        }

        /// <summary>
        /// タブレットから次の体験者へ切り替えてよいか。
        /// <c>Finished</c> は終幕の開始なので、終幕が再生中の間は画と音を切らない。
        /// </summary>
        public static bool CanResetFromTablet(bool started, bool runFinished, bool outroPlaying)
            => !started || (runFinished && !outroPlaying);

        public bool ConfirmPosition(in StaffSetupSnapshot current)
        {
            if (!Observe(current, 0f) || !current.hasPosition || current.positionInvalid || current.registrationActive)
                return false;
            _positionConfirmed = true;
            Stage = StaffSetupStage.Welcome;
            return true;
        }

        /// <summary>引き渡し。Welcome 段だけで受け付け、一括検査（<see cref="ReadyToHandOff"/>）を通す。</summary>
        public bool HandOff(in StaffSetupSnapshot current)
        {
            if (!Observe(current, 0f) || Stage != StaffSetupStage.Welcome || !ReadyToHandOff) return false;
            Stage = StaffSetupStage.HandedOff;
            return true;
        }

        /// <summary>題字開始の押下時にも実状態を確認する。練習の認可は呼び出し側で別に守る。</summary>
        public bool TryBeginExperience(in StaffSetupSnapshot current)
        {
            if (_started || !Observe(current, 0f) || !CanBeginExperience) return false;
            _started = true;
            return true;
        }

        public void NewVisitor()
        {
            Generation++;
            _started = false;
            _latest = default;
            _visitorResetCompleted = false;
            if (Stage == StaffSetupStage.HandedOff)
                Stage = _positionConfirmed ? StaffSetupStage.Welcome : StaffSetupStage.Alignment;
        }

        /// <summary>実際の体験リセットが完了した時だけ呼ぶ。初回の位置確認後に必須。</summary>
        public void VisitorResetCompleted()
        {
            NewVisitor();
            _visitorResetCompleted = _positionConfirmed;
            if (_positionConfirmed) Stage = StaffSetupStage.Welcome;
        }
    }
}
