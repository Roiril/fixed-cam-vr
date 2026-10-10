#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Input;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// asmdef を持たないため Assembly-CSharp に入る。Meta XR SDK の OVRInput にアクセスできる
    /// 唯一の場所として、コントローラ入力を Streaming / Tracking / Diagnostics のコンポーネントに
    /// 転送する橋渡し。配置先は [Streaming] GameObject 等。
    ///
    /// スタッフ操作は右コントローラの A / B / トリガーで完結する。右グリップは読まない。
    /// <b>左グリップだけは撮影</b>（説明資料用・2026-09-30。<see cref="ExperienceShotCapture"/>）。
    /// 体験者の入力ではなく、Development ビルドでのみ既定で有効。展示本番の機では OFF にする。
    /// モードは <see cref="ControllerModeLogic"/> の 2 状態（Normal / Registration）でゲートする:
    ///   - Normal: A 2 秒長押し=体験者リセット / B=押している間ステータスを表示 /
    ///             トリガー 2 秒長押し=位置合わせ入場
    ///   - Registration: A=点サンプル(やり直し) / B=確定 / トリガー 2 秒長押し=キャンセル退場
    ///
    /// ⚠ <b>A のカメラ手動送りは 2026-08-12 に撤去した</b>。通常時の短押しでは何もしない。
    /// キーボード経由の切替（<c>CameraSwitchInput</c> の Tab / 1-9・Editor 用）は残っている。
    /// 封印モード（旧 Run/Staff）・スティック・cue 試射は撤去した。
    ///
    /// <b>左は体験者の手。読むのはXとYだけ。</b>
    /// <b>1 秒長押し</b>で異変の報告になる（<see cref="VisitorMarkHoldLogic"/>。
    /// 2026-08-16 に 2 秒から半分へ・<c>canon/LEDGER.md</c> 0059）。
    /// 押し方と進捗は<b>AIエージェントからの連絡の面（<see cref="CommsPanel"/>）の下段</b>が出す
    /// （2026-08-16・<c>canon/LEDGER.md</c> 0058。コントローラに追従する面は廃止した）。
    /// 導入では左コントローラの接続と位置を確認し、同じ 1 秒長押しを一度練習する。
    /// 練習は本編の報告件数へ渡さない。題字が出たあとは X / Y の短押しで本編へ進む。
    /// HMD 非装着→SignalLostFx / OS recenter→CourseRegistrationController.NotifyRecentered のパッシブ系は現状維持。
    /// </summary>
    public sealed class OvrControllerBridge : MonoBehaviour
    {
        [Tooltip("トラッキングロスト（HMD 非装着）を通知する SignalLostFx（Screen 上）。null なら通知しない。")]
        [SerializeField] private SignalLostFx? signalFx;

        [Tooltip("演出モードのラベルを heartbeat へ載せる ShowControlClient（Screen 上）。未割当なら Start で自動取得。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("タイトル画面（[Title] 上）。導入の報告練習後に自動表示し、左 X / Y の短押しで閉じる。")]
        [SerializeField] private TitleScreen? titleScreen;

        [Header("Mappings（右コントローラのみ）")]
        [Tooltip("体験者リセット（Normal・2 秒長押し）/ 点サンプル・やり直し（Registration）に使う右手ボタン。既定 A。")]
        [SerializeField] private OVRInput.Button primaryButton = OVRInput.Button.One;   // A (右)

        [Tooltip("ステータス表示（Normal・押している間）/ 登録確定（Registration）に使う右手ボタン。既定 B。")]
        [SerializeField] private OVRInput.Button statusButton = OVRInput.Button.Two;  // B (右)

        [Header("Status HUD")]
        [Tooltip("単一サーフェス StatusHud（[StatusHud] 上）。B を押しているあいだステータスを表示する。")]
        [SerializeField] private StatusHud? statusHud;

        [Tooltip("スタッフ用コントローラ操作ガイドパネル（右コントローラに追従）。現在モードの操作説明を常時表示。" +
                 "null でも全機能は従来通り動く（ガイドが出ないだけ）。")]
        [SerializeField] private ControllerGuidePanel? guidePanel;

        [Header("Haptics")]
        [Tooltip("右コントローラの触覚フィードバック（[Streaming] 上・ControllerHaptics）。null でも全機能は従来通り動く" +
                 "（振動が鳴らないだけ）。押下の受理 / 長押し進行 / 発火 / 失敗を振動で伝える。")]
        [SerializeField] private ControllerHaptics? haptics;

        [Header("Run reset")]
        [Tooltip("A 2 秒長押し＝体験者リセット（周回リセット + cue 発火済みクリア）の対象 LapCounter。" +
                 "runEpoch とは独立の現地手段（PC 卓不在でも体験者交代でリセットできる）。null なら cueScheduler へフォールバック。")]
        [SerializeField] private LapCounter? lapCounter;

        [Tooltip("LapCounter 未配線時にランリセットを行う CueScheduler（発火済みフラグのみクリア）。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Header("Course registration")]
        [Tooltip("CourseRegistrationController（[Tracker] 上）。トリガー 2 秒長押しで登録モードへ入り、" +
                 "登録中は入力（A=マーク/やり直し, B=確定）を転送する。")]
        [SerializeField] private CourseRegistrationController? courseRegistration;

        [Tooltip("OS recenter（Oculus ボタン長押し）検知のフォールバック先 CourseFrame（[Tracker] 上）。")]
        [SerializeField] private CourseFrame? courseFrame;

        // トリガー / A の長押し閾値 (秒)。SerializeField にすると既存シーン YAML に未記載で 0 と読まれ
        // 判定が壊れる（unity-prefab-fields の罠）。調整不要なので const 固定。
        private const float LongPressSec = 2.0f;

        /// <summary>
        /// トリガー／A の長押しで、HoldTick（右の連続の振動）を鳴らし始めるまでの間（秒）。
        /// 頭の 0.3 秒を鳴らさず、短押しのたびに進捗振動が出るのを防ぐ。
        /// ⚠ 登録の A 0.5 秒ホールド（<c>SampleHoldProgress01</c>）には掛けない —
        /// あれは 0.5 秒で 0→1 を走るので、掛けるとランプの 6 割が消える。
        /// </summary>
        private const float HoldTickDeadSec = 0.3f;

        // モード状態機械（純ロジック。入力を bool/float で Tick する）。
        private readonly ControllerModeLogic _modeLogic = new();

        // 体験者の報告ボタン（左のどれか）の長押し（純ロジック）。
        private readonly VisitorMarkHoldLogic _markHold = new();
        private readonly HmdOnboardingLogic _onboarding = new();
        private readonly ControllerConnectionGate _leftConnectionGate = new();
        private readonly ControllerConnectionGate _rightConnectionGate = new();
        private readonly RegistrationSampleInputGate _registrationSampleInputGate = new();
        private int _titleSequence = -1;
        private bool _markNeedsRelease = true;
        private bool _prevAHeld, _prevBHeld, _prevTriggerHeld;
        private StaffSetupPanel? _staffSetup;
        private readonly StaffSetupButtonLogic _setupButton = new StaffSetupButtonLogic();

        // 左グリップ = 体験中の撮影（説明資料用・2026-09-30）。押した瞬間の画を 4 種類保存する。
        // ⚠ 体験者の入力ではない（報告・言語・題字には使わない）。SerializeField にしない
        //    （StreamingLogic.prefab の YAML に無いフィールドは 0 / null で読まれる）。
        private bool _prevGripRawHeld;
        private ExperienceShotCapture? _shots;
        private OVRCameraRig? _cameraRig;

        // OS recenter 購読済みフラグ（OVRManager.display は初期化順で null のことがあるためリトライする）。
        private bool _recenterSubscribed;

        [Tooltip("AIエージェントからの連絡の面。届いた瞬間の振動と、報告の押し方・ゲージの出し先。" +
                 "null でも報告そのものは動く（画に出ないだけ）。")]
        [SerializeField] private CommsPanel? comms;
        private int _lastCommsPulse;
        private bool _pendingLeftNotify;

        /// <summary>
        /// 直近に見た <c>ShowControlClient.CurseReleasedCount</c>。増えた瞬間が
        /// 「呪いが排除されて画がリアルタイム映像へ戻った」縁（2026-09-05・<c>canon/LEDGER.md</c> 0156）。
        /// ⚠ 0 で始めるので、起動直後に空振りしない（あちらも 0 始まりで単調）。
        /// </summary>
        private int _lastCurseReleased;
        private bool _pendingClosing;
        // 連絡の面を毎フレーム探しに行かないための再試行の間隔（面が無い構成での 90Hz 全走査を断つ）。
        private const float CommsResolveRetrySec = 2f;
        private float _commsRetryAt;

        // コントローラの生死（heartbeat 経由で卓が読む）。⚠ 接続と位置は別物 — 電源が入っていれば
        // 接続は true だが、カメラから見えていないと姿勢は無効（memory/hmd 系の既知の罠）。
        private bool _lConnected, _lTracked, _rConnected, _rTracked;

        private void Start()
        {
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();

            if (titleScreen == null) titleScreen = FindObjectOfType<TitleScreen>();

            // 導入演出は「HMD を被った状態で」始める。Streaming asmdef は OVR を参照しない規約なので、
            // Assembly-CSharp 側のここから判定を差し込む（未設定なら被っている扱いで従来どおり動く）。
            //
            // ⚠⚠ **「被っている」と「人が始めてよいと言った」は別の provider に分ける**（2026-08-14）。
            //    旧実装は 1 つに畳んで `userPresent && !title.IsBlocking` を返していたが、
            //    自動走行（`ShowWalkDebugDriver`）は**被り検知だけを外す目的で全体を true に
            //    上書きする**ので、走行ではタイトルが立ったまま導入が始まって題字が飛んでいた
            //    ＝ タイトル画面は自動走行で 1 度も検証されていなかった。
            //
            // ⚠ **必ず素通しへ倒す。** `IsBlocking` はタイトルの実体を組めたときしか true にならない
            //    （シェーダ剥がれ・テクスチャ欠落なら false）。ここをラッチにすると、タイトルが
            //    出せない現場で**体験が二度と始まらない**（2026-07-31 のシェーダ剥がれと同型）。
            if (showControl != null)
            {
                showControl.UserPresentProvider = () => OVRPlugin.userPresent;
                showControl.StartAuthorizedProvider = () => _onboarding.StartAuthorized;
            }

            // ステータスが出ているかを卓の heartbeat（`statusHud`）へ流す。
            // ⚠ **2026-09-14 まではタイトルに黒を譲らせるためのものだった。** いまは面が
            //    黒の上に描かれる（Overlay ＋ queue 5000）ので、譲りは要らない ＝ B を押している
            //    あいだも体験者の X / Y 短押しはそのまま通る（`TitleScreen.IsYielding` も参照）。
            if (showControl != null && statusHud != null)
            {
                var hud = statusHud;
                showControl.StatusVisibleProvider = () => hud.IsVisible;
            }

            // 位置合わせ中は「現実を隠すもの」を全部どける（演出の覆い・中止の黒・パススルーの電源）。
            // Streaming も Tracking も互いを参照しない規約なので、両方を知っているここが唯一の配線点。
            if (showControl != null && courseRegistration != null)
            {
                var reg = courseRegistration;
                showControl.CourseRegistrationActiveProvider = () => reg.IsActive;
            }

            // コントローラが生きているかを卓へ流す（2026-08-30）。
            // ⚠⚠ **切れても画にも音にも出ない。** 左は体験者の唯一の入力なので、電池切れ・スリープで
            //    死ぬと `CommsPanel.ApplyHint` が押し方の案内を黙って空文字にするだけで、卓にも HMD にも
            //    何も出ない。オペレータが気づくのは終幕の報告が「０」になったとき ＝ もう手遅れ。
            //    Streaming は OVR を参照しない規約なので、両方を知っているここから書きに来る。
            if (showControl != null)
            {
                showControl.ControllerStateProvider = () => (_lConnected, _lTracked, _rConnected, _rTracked);
            }

            _onboarding.Reset();
            _titleSequence = titleScreen != null ? titleScreen.Sequence : -1;

            _modeLogic.Configure(LongPressSec);
            _modeLogic.Reset(ControllerModeLogic.Mode.Normal);
            _modeLogic.ModeChanged += OnModeChanged;
            _modeLogic.RunResetRequested += ResetRun;
            guidePanel?.SetRegistration(courseRegistration);
            PushModeLabel(_modeLogic.Current); // 初期状態 NORMAL を StatusHud / heartbeat へ

            // 登録フローの節目を触覚へ（点サンプル確定=Action / 残差NG=Error / 確定保存=Fire）。
            // 購読はこの Assembly-CSharp 側で行い、Tracking asmdef に OVRInput 依存を作らない。
            if (courseRegistration != null)
            {
                courseRegistration.PointCaptured += OnRegPointCaptured;
                courseRegistration.FitRejected += OnRegFitRejected;
                courseRegistration.FitAccepted += OnRegFitAccepted;
                courseRegistration.SampleAborted += OnRegSampleAborted;
                courseRegistration.RegistrationConfirmed += OnRegConfirmed;
            }

            TrySubscribeRecenter();
            courseRegistration?.DeferStartupForStaffSetup();
            _staffSetup = StaffSetupPanel.Ensure();
            if (showControl != null)
            {
                showControl.StartAuthorizedProvider = () => _onboarding.StartAuthorized
                    && _staffSetup != null && _staffSetup.Logic.Started;
                showControl.StaffSetupActiveProvider = () =>
                {
                    _staffSetup?.Refresh(0f);
                    return _staffSetup != null && _staffSetup.Visible;
                };
            }
        }

        private void OnDestroy()
        {
            _modeLogic.ModeChanged -= OnModeChanged;
            _modeLogic.RunResetRequested -= ResetRun;
            if (courseRegistration != null)
            {
                courseRegistration.PointCaptured -= OnRegPointCaptured;
                courseRegistration.FitRejected -= OnRegFitRejected;
                courseRegistration.FitAccepted -= OnRegFitAccepted;
                courseRegistration.SampleAborted -= OnRegSampleAborted;
                courseRegistration.RegistrationConfirmed -= OnRegConfirmed;
            }
            if (_recenterSubscribed && OVRManager.display != null)
                OVRManager.display.RecenteredPose -= OnRecentered;
            _recenterSubscribed = false;
        }

        // OVRManager.display は OVRManager の初期化で生えるため、Start 時点で null のことがある
        // （→黙って恒久無効＝OS recenter で登録がズレたまま・ログにも残らない）。生えるまで Update で再試行。
        private void TrySubscribeRecenter()
        {
            if (_recenterSubscribed || (courseRegistration == null && courseFrame == null) || OVRManager.display == null) return;
            OVRManager.display.RecenteredPose += OnRecentered;
            _recenterSubscribed = true;
        }

        private void OnRecentered()
        {
            if (courseRegistration != null) courseRegistration.NotifyRecentered();
            else courseFrame?.MarkNeedsReRegistration();
        }

        private void Update()
        {
            if (showControl?.Portal != null && showControl.Portal.StaffResetProvider == null)
                showControl.Portal.StaffResetProvider = TryResetVisitorFromTablet;
            // 接続状態と物理ボタンは最初に一度だけ読む。SDK の Down は切断前の状態を持ち越し得るため、
            // 再接続後の解放を確認した入力から Down を組み立てる。
            bool lConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            bool lTracked = lConnected && OVRInput.GetControllerPositionValid(OVRInput.Controller.LTouch);
            bool rConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            bool rTracked = rConnected && OVRInput.GetControllerPositionValid(OVRInput.Controller.RTouch);
            bool xRawHeld = OVRInput.Get(OVRInput.RawButton.X, OVRInput.Controller.LTouch);
            bool yRawHeld = OVRInput.Get(OVRInput.RawButton.Y, OVRInput.Controller.LTouch);
            bool aRawHeld = OVRInput.Get(primaryButton, OVRInput.Controller.RTouch);
            bool bRawHeld = OVRInput.Get(statusButton, OVRInput.Controller.RTouch);
            bool triggerRawHeld = OVRInput.Get(OVRInput.RawButton.RIndexTrigger, OVRInput.Controller.RTouch);
            bool gripRawHeld = OVRInput.Get(OVRInput.RawButton.LHandTrigger, OVRInput.Controller.LTouch);

            ControllerConnectionGate.Result leftConnection =
                _leftConnectionGate.Tick(lConnected, xRawHeld || yRawHeld);
            ControllerConnectionGate.Result rightConnection =
                _rightConnectionGate.Tick(rConnected, aRawHeld || bRawHeld || triggerRawHeld);

            _lConnected = lConnected;
            _lTracked = lTracked;
            _rConnected = rConnected;
            _rTracked = rTracked;
            _staffSetup?.SetRightController(rConnected, rTracked);
            _staffSetup?.Refresh(Time.unscaledDeltaTime);
            guidePanel?.SetControllerState(rConnected, rTracked);
            statusHud?.SetRightControllerState(rConnected, rTracked);

            if (leftConnection.Disconnected)
            {
                _markHold.Reset();
                _markNeedsRelease = true;
                haptics?.StopLeft();
            }
            if (rightConnection.Disconnected)
            {
                _modeLogic.DiscardHolds();
                haptics?.StopRight();
            }

            if (rightConnection.Reconnected && _pendingClosing)
            {
                _pendingClosing = false;
                haptics?.Closing();
            }

            bool xHeld = leftConnection.AcceptInput && xRawHeld;
            bool yHeld = leftConnection.AcceptInput && yRawHeld;
            bool aHeld = rightConnection.AcceptInput && aRawHeld;
            bool bHeld = rightConnection.AcceptInput && bRawHeld;
            bool rTrigger = rightConnection.AcceptInput && triggerRawHeld;
            if (rTrigger) _setupButton.Tick(false, 0f, false);
            bool aDown = aHeld && !_prevAHeld;
            bool bDown = bHeld && !_prevBHeld;
            bool triggerDown = rTrigger && !_prevTriggerHeld;
            _prevAHeld = aHeld;
            _prevBHeld = bHeld;
            _prevTriggerHeld = rTrigger;

            // 左グリップの押下の縁。前フレームは**生の値**で持つ — 再接続をまたいで握られていた
            // グリップが、接続復帰の 1 フレーム目に「新しい押下」として撃たれないように。
            bool gripDown = leftConnection.AcceptInput && gripRawHeld && !_prevGripRawHeld;
            _prevGripRawHeld = gripRawHeld;
            if (gripDown) CaptureExperienceShots();

            // ---- パッシブ状態（モードでゲートしない。ゲストが誘発する操作ではなくフェイルソフト表示）----
            // OVRManager.display が後から生えるケースに備え、未購読なら毎フレーム再試行（生えたら 1 回で確定）。
            if (!_recenterSubscribed) TrySubscribeRecenter();

            // トラッキングロスト（HMD 非装着 = プロキシ）を SignalLostFx へ通知。初期化前（instance==null）は
            // present 扱いにして誤発火を避ける。位置トラッキングの一時ロストは resume-gap 側で拾う。
            bool hmdPresent = OVRManager.instance == null || OVRManager.isHmdPresent;
            signalFx?.ReportTrackingLost(!hmdPresent);

            // 表示レートの要求（起動直後だけ。成功か時間切れで以後は何もしない）。
            // ここに置くのは、既存シーン / prefab へコンポーネントを 1 個増やさずに済ませるため。
            DisplayRateRequester.Tick(Time.unscaledDeltaTime);

            // ---- 体験者の手（左）。X / Y を物理ボタン名で読む -----------------------
            // Button.Three / Four と LTouch の組み合わせは SDK の仮想マップ上で None になる。
            // RawButton を使い、練習と本編の両方を同じ入力に揃える。
            // 体験者へ説明する X / Y だけを使う。左グリップ、左インデックストリガー、
            // 左スティック押し込みは導入・題字・本編報告のどこでも読まない。
            bool leftMarkHeld = xHeld || yHeld;

            // 監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る＝「入力は届いている」）。
            // アクション実行時は switch 内で Action を後着し、ピーク優先で Ack を昇格させる。
            if (aDown || bDown || triggerDown)
                haptics?.Ack();

            // 右コントローラの状態は Update 冒頭でガイドと StatusHud へ push 済み。
            //
            // ⚠⚠ **接続と位置は別々に見る**（2026-08-16 実機で踏んだ）。
            //    電源が入っていれば `IsControllerConnected` は true だが、カメラから見えていないと
            //    姿勢は無効で、`OVRCameraRig` は**アンカーをトラッキング原点（床の中心）へ置く**
            //    （`OVRCameraRig.UpdateAnchors` は有効なコントローラが 1 つも無いと
            //     `GetLocalControllerPosition(Controller.None)` ＝ ゼロを書く）。
            //    接続だけを見ていたので、**手元の面が床の原点に出て「遠くに小さく」見えていた**。
            //    人形の左腕（`OvrHandTrackingBridge.TryReadController`）は最初から
            //    `GetControllerPositionValid` を見ていて、そこだけ正しかった。
            bool regActive = courseRegistration != null && courseRegistration.IsActive;
            bool registrationInputAccepted = _registrationSampleInputGate.Tick(
                rTracked,
                aRawHeld,
                regActive && courseRegistration != null && courseRegistration.SampleHoldProgress01 > 0f);
            bool registrationMarkHeld = aHeld && registrationInputAccepted;
            bool registrationMarkDown = aDown && registrationMarkHeld;

            bool leftNotifyAllowed = !regActive
                && !(showControl != null && showControl.IsVisitorMarkBlocked);
            if (_pendingLeftNotify && lConnected && leftNotifyAllowed)
            {
                _pendingLeftNotify = false;
                haptics?.LeftNotify();
            }

            // AIエージェントからの連絡が届いたら、体験者の手（左）を震わせる。
            // ⚠ 連絡の面（Diagnostics）も体験の骨格（Streaming）も OVR を参照しない規約なので、
            //    **向こうから読みに来る**（ShowBodyInput / UserPresentProvider と同じ流儀）。
            // ⚠ 未配線のときは**間隔を置いて**探す（2026-08-30）。素の `if (comms == null)` は
            //    `[Comms]` を持たない構成で **90Hz で全 GameObject を走査し続ける**。
            //    `ShowTelemetryHost` は同じ解決を最初から間隔つきでやっていて、ここだけ非対称だった。
            if (comms == null && Time.unscaledTime >= _commsRetryAt)
            {
                comms = FindObjectOfType<CommsPanel>();
                if (comms == null) _commsRetryAt = Time.unscaledTime + CommsResolveRetrySec;
            }
            if (comms != null && comms.PulseCount != _lastCommsPulse)
            {
                _lastCommsPulse = comms.PulseCount;
                if (leftNotifyAllowed)
                {
                    if (lConnected) haptics?.LeftNotify();
                    else _pendingLeftNotify = true;
                }
            }

            // 体験者が最後の異変を排除して、画がリアルタイム映像へ戻った ＝ 締めが始まった。
            // **スタッフの手（右）**を震わせて知らせる（2026-09-05・`canon/LEDGER.md` 0156）。
            //
            // ⚠ スタッフは HMD の中を見ていないので、締めが始まったことは画にも音にも出ない。
            //    ここから電源断まで数秒しかないため、引き渡しの支度を始める合図がこれ以外に無い。
            // ⚠ **体験者の手（左）へは出さない** — 体験者に「終わりだ」を先に教えることになる。
            // ⚠ **縁は `ShowControlClient` から読む**（`comms.PulseCount` と同じ流儀）。
            //    `RecordVisitorMark` の直後に鳴らす形にすると、**自動走行（`ShowWalkDebugDriver`）が
            //    直に呼ぶ経路**が漏れる。増分を見れば号令元がどこでも拾える。
            if (showControl != null && showControl.CurseReleasedCount != _lastCurseReleased)
            {
                _lastCurseReleased = showControl.CurseReleasedCount;
                if (rConnected) haptics?.Closing();
                else _pendingClosing = true;
                Debug.Log("[Haptics] 締めの合図（右・スタッフ）— 呪いが排除されて画がライブへ戻った");
            }

            // ---- モード遷移（副作用は OnModeChanged / ResetRun が担う）----
            // A の操作中に人差し指がトリガーへ掛かっても、位置合わせの長押しには数えない。
            // ⚠⚠ **B は外してある**（2026-09-14）。B は押しているあいだステータスを読む操作なので、
            //    含めると**早見表を読みながらトリガー長押しで位置合わせへ入れない**。
            //    2026-09-11 の事故（右が震え続ける・ランリセットが撃たれる）は**握り込み × A** なので、
            //    B を外しても回帰しない。
            // 機器確認と位置合わせは並行してよい（2026-10-10）。初回の段でも右トリガー 2 秒で入れる。
            int voidedBefore = _modeLogic.VoidedHolds;
            _modeLogic.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = Time.deltaTime,
                triggerHeld = rTrigger,
                resetHeld = aHeld,
                registrationActive = regActive,
                faceButtonHeld = aHeld,
            });
            _staffSetup?.SetResetProgress(_modeLogic.ResetHoldProgress01);
            if (_modeLogic.VoidedHolds != voidedBefore)
            {
                // 振動は画にも音にも出ないので、重なりが起きたことはここに残す。
                Debug.Log("[OvrBridge] 右トリガーの長押しを無効にした（A と重なった入力）"
                          + $" trigger={(rTrigger ? 1 : 0)}"
                          + $" triggerAxis={OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch):F2}");
            }
            ControllerModeLogic.Mode mode = _modeLogic.Current;

            // ---- 導入（左の接続確認 → 報告練習 → 題字）-------------------------------
            // ランの開始と同じ縁で純ロジックも戻す。練習成功は RecordVisitorMark へ渡さない。
            if (titleScreen != null && titleScreen.Sequence != _titleSequence)
            {
                _titleSequence = titleScreen.Sequence;
                _onboarding.Reset();
                _markHold.Reset();
                _markNeedsRelease = true;
                _staffSetup?.NewVisitor();
            }

            // HMDを持たない自動走行は、題字を自前で検証した後に開始門を true へ差し替える。
            // 通常時はこの provider 自体が _onboarding.StartAuthorized を返すので、この分岐へ入らない。
            if (showControl != null && showControl.StartAuthorized && !_onboarding.StartAuthorized)
            {
                _onboarding.CompleteForAutomation();
                _staffSetup?.UseAutomation();
            }

            bool setupBlocked = _staffSetup == null || _staffSetup.BlocksVisitor;

            CommsNotice expectedOnboardingNotice = NoticeFor(_onboarding.Prompt);
            HmdOnboardingAction onboardingAction = _onboarding.Tick(new HmdOnboardingInput
            {
                dt = regActive || setupBlocked ? 0f : Time.unscaledDeltaTime,
                hmdPresent = OVRManager.instance == null || OVRPlugin.userPresent,
                leftConnected = _lConnected,
                leftPositionValid = _lTracked,
                xHeld = xHeld && mode == ControllerModeLogic.Mode.Normal,
                yHeld = yHeld && mode == ControllerModeLogic.Mode.Normal,
                titleAvailable = titleScreen != null && titleScreen.IsBuilt,
                titleReady = titleScreen != null && titleScreen.ReadyForStart,
                titleDone = titleScreen == null || titleScreen.ClosedAlready,
                promptFullyShown = comms == null
                    || comms.IsOnboardingNoticeFullyShown(expectedOnboardingNotice),
                inputBlocked = regActive || setupBlocked,
            });

            if (onboardingAction == HmdOnboardingAction.TutorialAccepted)
            {
                haptics?.LeftMark();
                Debug.Log("[Onboarding] 報告の練習を受け取りました");
            }
            else if (onboardingAction == HmdOnboardingAction.DismissTitle)
            {
                if (titleScreen != null && titleScreen.ReadyForStart
                    && _staffSetup != null && _staffSetup.TryBeginExperience()
                    && titleScreen != null && titleScreen.DismissTitle()) haptics?.Fire();
            }

            // タイトル表示は一時的な譲り（ステータス面・位置合わせ）が終わったあとも再試行する。
            if (_onboarding.Stage == HmdOnboardingStage.Title
                && !setupBlocked && titleScreen != null && titleScreen.Stage == TitleStage.Wait)
                titleScreen.ShowTitle();

            ApplyOnboardingPresentation();

            // ---- 本編の報告（左 X / Y の 1 秒長押し）-------------------------------
            // 導入が完了するまでは本編件数を一切触らない。位置合わせ中と締めの冒頭でも積まない。
            if (!_onboarding.StartAuthorized) _markNeedsRelease = true;
            else if (!leftMarkHeld) _markNeedsRelease = false;
            bool markTooEarly = showControl != null && showControl.IsMarkTooEarly;
            bool takeoverBlocked = (showControl != null && showControl.IsVisitorMarkBlocked)
                                   || (comms != null && comms.TakeoverInputBlocked);
            bool markBlocked = regActive || markTooEarly || takeoverBlocked;
            if (markBlocked) _markNeedsRelease = true;
            bool markFired = _markHold.Tick(Time.deltaTime,
                                            leftMarkHeld && _onboarding.StartAuthorized
                                            && !_markNeedsRelease
                                            && mode == ControllerModeLogic.Mode.Normal,
                                            blocked: markBlocked);
            if (_markHold.StartedThisTick) showControl?.NotifyVisitorMarkStarted();
            if (markFired)
            {
                if (showControl != null && showControl.RecordVisitorMark()) haptics?.LeftMark();
            }
            if (comms != null)
            {
                comms.SetControllerState(_lConnected, _lTracked);
                if (_onboarding.StartAuthorized)
                    comms.SetMarkState(_markHold.Progress01, _markHold.Confirming);
                else
                    comms.SetMarkState(_onboarding.TutorialProgress01,
                                       _onboarding.Stage == HmdOnboardingStage.TutorialAccepted);
            }
            float visitorHoldProgress = _onboarding.StartAuthorized
                ? _markHold.Progress01
                : _onboarding.TutorialProgress01;
            if (markBlocked) visitorHoldProgress = 0f;
            if (regActive || takeoverBlocked) haptics?.StopLeft();
            haptics?.SetLeftHoldProgress(visitorHoldProgress);

            // 長押しカウント進行を HoldTick 振動へ（トリガー入場 / A の体験者リセット / 登録の 0.5s ホールド
            // 平均サンプリングの最大を流す。登録中は SampleHoldProgress01 が 0.5 秒ホールドの進行ランプを鳴らす）。
            // ⚠ トリガー／A の頭 HoldTickDeadSec は鳴らさない。右グリップは読みもしない。
            float longPress = _modeLogic.TriggerHoldProgress01;
            if (mode == ControllerModeLogic.Mode.Normal)
                longPress = Mathf.Max(longPress, _modeLogic.ResetHoldProgress01);
            if (longPress < HoldTickDeadSec / LongPressSec) longPress = 0f;
            float holdProgress = longPress;
            if (courseRegistration != null && registrationInputAccepted)
                holdProgress = Mathf.Max(holdProgress, courseRegistration.SampleHoldProgress01);
            haptics?.SetHoldProgress(holdProgress);

            // ---- ステータス表示（右 B を押しているあいだ）--------------------------
            // ⚠⚠ **モードに関わらず毎フレーム渡す**（Registration では false）。渡さないと
            //    離した猶予（0.3 秒）が進まず、**位置合わせへ入った瞬間の姿で面が凍る**。
            // ⚠ 振動は「面が実際に出た」瞬間に 1 粒だけ（離すときは鳴らさない）。
            //    down の Ack は上で全ダウン共通に鳴っているので、ここは Action の後着で昇格する。
            if (statusHud != null)
            {
                bool statusShown = statusHud.SetHeld(bHeld && mode == ControllerModeLogic.Mode.Normal
                    && !(_staffSetup != null && _staffSetup.Visible));
                if (statusShown) haptics?.Action();
            }

            // ---- モード別の入力分配 ----
            switch (mode)
            {
                case ControllerModeLogic.Mode.Registration:
                    _setupButton.Tick(false, 0f, false);
                    // 登録モード中は通常マッピングを抑止し、登録入力（A=マーク/やり直し・B=確定）を転送。
                    courseRegistration?.Feed(new CourseRegistrationController.RegInput
                    {
                        mark = registrationMarkDown, // A (右) Down: サンプリング開始 / Verify やり直し
                        markHeld = registrationMarkHeld, // A (右) ホールド: 0.5s 平均サンプリングの継続
                        confirm = bDown,    // B (右): Verify で確定
                        deltaTime = Time.deltaTime, // ホールド平均計時（純ロジック HoldAverageSampler へ供給）
                    });
                    break;

                case ControllerModeLogic.Mode.Normal:
                default:
                    // B 短押しは体験者を迎える段の引き渡しだけ。初回の段では画面の呼び戻し（B 1 秒）だけが効く。
                    var setupAction = _setupButton.Tick(bHeld, Time.unscaledDeltaTime,
                        _staffSetup != null && _staffSetup.Visible && (OVRManager.instance == null || OVRPlugin.userPresent) && rTracked && !aHeld && !rTrigger,
                        _staffSetup != null && _staffSetup.Logic.Stage == StaffSetupStage.Welcome
                            && _staffSetup.Logic.ReadyToHandOff);
                    if (setupAction == StaffSetupButtonAction.RecallScreen) _staffSetup?.RecallScreen();
                    else if (setupAction == StaffSetupButtonAction.Continue)
                    {
                        if (_staffSetup != null && _staffSetup.Continue()) haptics?.Action();
                        else haptics?.Error();
                    }
                    // B は押しているあいだの表示なので、ここでは何もしない（上の SetHeld が担う）。
                    // 右 A の長押しは _modeLogic が体験者リセットへ送る。短押しでは何もしない。
                    // タイトル開始は体験者の左 X / Y 短押しだけ。
                    // 体験者の報告はモードの外で数える（上を見る）。
                    //    ここに置くと位置合わせから戻った 1 フレームで進捗の押し戻しが起きる。
                    // トリガー長押し=Registration 入場も _modeLogic が担う。
                    break;
            }
        }

        /// <summary>
        /// 左グリップの押下 → 体験中の撮影（<see cref="ExperienceShotCapture"/>）。
        /// 無効な機（展示本番の OFF・Release）では実行体すら作らない。
        /// 受理したら左を 1 発だけ震わせる（<c>LeftMark</c> を使い回す。新しい振動パターンは作らない）。
        /// </summary>
        private void CaptureExperienceShots()
        {
            if (!ExperienceShotCapture.Enabled) return;
            if (_shots == null) _shots = ExperienceShotCapture.Ensure();
            _shots.HmdViewProvider ??= ResolveHmdView;
            if (_shots.Request()) haptics?.LeftMark();
        }

        /// <summary>④ の視点。左眼の位置と、その投影で描くためのカメラ（中央のアンカー上にある）。</summary>
        private ExperienceShotCapture.HmdView? ResolveHmdView()
        {
            if (_cameraRig == null) _cameraRig = FindObjectOfType<OVRCameraRig>();
            if (_cameraRig == null) return null;
            Transform? center = _cameraRig.centerEyeAnchor;
            Camera? cam = center != null ? center.GetComponent<Camera>() : null;
            if (cam == null) cam = _cameraRig.GetComponentInChildren<Camera>();
            if (cam == null) return null;
            Transform eye = _cameraRig.leftEyeAnchor != null ? _cameraRig.leftEyeAnchor : cam.transform;
            return new ExperienceShotCapture.HmdView(cam, eye);
        }

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

        // ランリセット（現地手段）。LapCounter.ResetRun が周回リセット + cue 発火済みクリア + 現在ゾーン再シードを行う。
        // LapCounter 未配線なら CueScheduler 単独で発火済みだけクリアする（周回は動かないが安全側）。
        private bool TryResetVisitorFromTablet()
        {
            if (_staffSetup == null || !_staffSetup.Logic.PositionConfirmed
                || _modeLogic.Current != ControllerModeLogic.Mode.Normal
                || (courseRegistration != null && courseRegistration.IsActive)) return false;
            var run = FindObjectOfType<ShowRunDirector>();
            var outro = FindObjectOfType<OutroDirector>();
            bool runFinished = run != null && run.Phase == ShowPhase.Finished;
            bool outroPlaying = outro != null && outro.Stage != OutroStage.Off && outro.Stage != OutroStage.Done;
            if (!StaffSetupLogic.CanResetFromTablet(_staffSetup.Logic.Started, runFinished, outroPlaying)) return false;
            ResetRun();
            return _staffSetup.Logic.VisitorStage == VisitorPreparationStage.SettingsRequired;
        }

        private void ResetRun()
        {
            // **号令元は ShowControlClient 1 か所に寄せる**（卓の ▶ ラン開始と同じ経路）。
            // 個別に叩いていた旧実装は、実測滞在が前の体験者の分と混ざる・体験の骨格（相）が
            // 戻らない、という非対称を持っていた。
            var show = FindObjectOfType<ShowControlClient>();
            int previousTitleSequence = titleScreen != null ? titleScreen.Sequence : -1;
            if (show?.Portal != null) show.Portal.BeginVisitorSession();
            else VisitorPrefs.BeginVisitor();
            if (show != null)
            {
                show.BeginNewVisitorRunLocal();
            }
            else
            {
                // ShowControlClient が居ないシーン（診断用の最小構成など）向けのフォールバック。
                // 走行中の演出を先に畳む（不変条件 8）。これが無いと、演出が画面を凍結したまま周回だけ
                // リセットされ、卓が無い現場では watchdog（45s）まで出口が無くなる。
                FindObjectOfType<TimelineDirector>()?.ResetRun();
                FindObjectOfType<FixedCamVr.Streaming.Recording.SegmentRecorder>()?.ResetRunLocal();
                if (lapCounter != null) lapCounter.ResetRun();
                else cueScheduler?.ResetRun();
                FindObjectOfType<BgmDirector>()?.ResetRun();
                FindObjectOfType<ShowRunDirector>()?.BeginRun();
            }
            // 体験者が代わるので、進行中の報告の長押しと余韻も落とす。
            // 実リセット完了を確認。通常のTitle.Sequence通知と二重に新訪問を作らない。
            if (titleScreen != null && titleScreen.Sequence != previousTitleSequence && titleScreen.Stage == TitleStage.Wait)
            {
                _titleSequence = titleScreen.Sequence;
                _onboarding.Reset();
                _setupButton.Tick(false, 0f, false);
                _staffSetup?.VisitorResetCompleted();
            }
            else _staffSetup?.NewVisitor();
            _markNeedsRelease = true;
            _markHold.Reset();
            haptics?.Fire(); // 長押し発火（ランリセット）
            Debug.Log("[OvrBridge] Normal: 体験者リセット（右 A 2 秒長押し）");
        }

        // ---- 登録フローの触覚（購読は Assembly-CSharp 側・Tracking は OVRInput 非依存）----
        private void OnRegPointCaptured() => haptics?.Action(); // 点サンプル確定
        private void OnRegFitRejected() => haptics?.Error();    // 残差 NG・やり直し
        private void OnRegFitAccepted() => haptics?.Fire();     // 残差ガード通過・Verify 遷移（FitRejected と対称）
        private void OnRegSampleAborted()
        {
            // 切断が原因の中断では、冒頭で止めた右振動を再び予約しない。
            if (_rConnected) haptics?.Error();
        }
        // 確定保存 = Fire。この直後に IsActive=false → 次フレーム ModeChanged(Reg→Normal) でも Fire が来るが、
        // HapticSequenceLogic のピア優先（同ピークは再生中なら無視）で 1 回に畳まれる。
        private void OnRegConfirmed() => haptics?.Fire();

        // ---- モード遷移の副作用（登録開始/停止・状態露出）----

        private void OnModeChanged(ControllerModeLogic.Mode from, ControllerModeLogic.Mode to)
        {
            switch (to)
            {
                case ControllerModeLogic.Mode.Normal:
                    // Registration からの復帰でまだアクティブ＝トリガーキャンセル。登録を止める。
                    if (courseRegistration != null && courseRegistration.IsActive) courseRegistration.Toggle();
                    break;

                case ControllerModeLogic.Mode.Registration:
                    if (courseRegistration != null && !courseRegistration.IsActive) courseRegistration.Toggle(); // 開始
                    break;
            }
            haptics?.Fire(); // モード遷移（登録入場 / キャンセル・確定退場）
            PushModeLabel(to);
            // 実機ログに 1 行残す。**「長押しが効いていない」の切り分けはここが唯一の一次証拠**
            // （画が変わらないと発火したかどうか体験者にも分からない）。
            Debug.Log($"[Ctrl] モード {from} → {to}");
        }

        private void PushModeLabel(ControllerModeLogic.Mode mode)
        {
            string label = mode == ControllerModeLogic.Mode.Registration ? "REG" : "NORMAL";
            statusHud?.SetModeLabel(label);
            guidePanel?.SetMode(label);
            showControl?.SetControllerMode(label);
        }
    }
}
