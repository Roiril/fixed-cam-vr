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
    /// スタッフ操作は右コントローラの A / B / トリガーで完結する。グリップは読まない。
    /// モードは <see cref="ControllerModeLogic"/> の 2 状態（Normal / Registration）でゲートする:
    ///   - Normal: A 2 秒長押し=体験者リセット / B=ステータス表示トグル /
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
    /// HMD 非装着→SignalLostFx / OS recenter→CourseFrame.MarkNeedsReRegistration のパッシブ系は現状維持。
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

        [Tooltip("ステータス表示トグル（Normal）/ 登録確定（Registration）に使う右手ボタン。既定 B。")]
        [SerializeField] private OVRInput.Button statusButton = OVRInput.Button.Two;  // B (右)

        [Header("Status HUD")]
        [Tooltip("単一サーフェス StatusHud（[StatusHud] 上）。B 押下でステータス表示をトグルする。")]
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

        [Tooltip("OS recenter（Oculus ボタン長押し）検知で『要再登録』を立てる CourseFrame（[Tracker] 上）。")]
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

        private const OVRInput.RawButton LeftReportMask =
            OVRInput.RawButton.X | OVRInput.RawButton.Y;

        // モード状態機械（純ロジック。入力を bool/float で Tick する）。
        private readonly ControllerModeLogic _modeLogic = new();

        // 体験者の報告ボタン（左のどれか）の長押し（純ロジック）。
        private readonly VisitorMarkHoldLogic _markHold = new();
        private readonly HmdOnboardingLogic _onboarding = new();
        private int _titleSequence = -1;
        private bool _markNeedsRelease = true;

        // OS recenter 購読済みフラグ（OVRManager.display は初期化順で null のことがあるためリトライする）。
        private bool _recenterSubscribed;

        [Tooltip("AIエージェントからの連絡の面。届いた瞬間の振動と、報告の押し方・ゲージの出し先。" +
                 "null でも報告そのものは動く（画に出ないだけ）。")]
        [SerializeField] private CommsPanel? comms;
        private int _lastCommsPulse;

        /// <summary>
        /// 直近に見た <c>ShowControlClient.CurseReleasedCount</c>。増えた瞬間が
        /// 「呪いが排除されて画がリアルタイム映像へ戻った」縁（2026-09-05・<c>canon/LEDGER.md</c> 0156）。
        /// ⚠ 0 で始めるので、起動直後に空振りしない（あちらも 0 始まりで単調）。
        /// </summary>
        private int _lastCurseReleased;
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

            // スタッフがステータスを開いているあいだ、タイトルの黒（0.3m・queue 4950）が
            // StatusHud（1.6m・TMP）を丸ごと塗り潰す。**引き渡し直前にカメラの○×も位置合わせの
            // 残差も確認できない**ので、タイトル側に譲らせる（位置合わせ中と同じ扱い）。
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
            if (_recenterSubscribed || courseFrame == null || OVRManager.display == null) return;
            OVRManager.display.RecenteredPose += OnRecentered;
            _recenterSubscribed = true;
        }

        private void OnRecentered() => courseFrame?.MarkNeedsReRegistration();

        private void Update()
        {
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

            // ---- 入力を 1 回だけ読む（同じボタンを複数箇所で拾わないため）----
            // Button.One/Two はコントローラ未指定だと両手から拾う（One=A|X 等）ため、必ず RTouch を明示する。
            bool aDown = OVRInput.GetDown(primaryButton, OVRInput.Controller.RTouch); // A: 登録のマーク・やり直し
            bool aHeld = OVRInput.Get(primaryButton, OVRInput.Controller.RTouch);     // A: 押しっぱなし（登録のホールド平均用）
            bool bDown = OVRInput.GetDown(statusButton, OVRInput.Controller.RTouch); // B: ステータストグル / 確定
            bool bHeld = OVRInput.Get(statusButton, OVRInput.Controller.RTouch);     // B: 押しっぱなし（長押しの無効化の門）
            bool rTrigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

            // ---- 体験者の手（左）。X / Y を物理ボタン名で読む -----------------------
            // Button.Three / Four と LTouch の組み合わせは SDK の仮想マップ上で None になる。
            // RawButton を使い、練習と本編の両方を同じ入力に揃える。
            bool xHeld = OVRInput.Get(OVRInput.RawButton.X, OVRInput.Controller.LTouch);
            bool yHeld = OVRInput.Get(OVRInput.RawButton.Y, OVRInput.Controller.LTouch);
            bool leftMarkHeld = OVRInput.Get(LeftReportMask, OVRInput.Controller.LTouch);

            // 監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る＝「入力は届いている」）。
            // アクション実行時は switch 内で Action を後着し、ピーク優先で Ack を昇格させる。
            if (aDown || bDown || triggerDown)
                haptics?.Ack();

            // 右コントローラの状態をガイドパネルへ push（Diagnostics は OVRInput 非依存のため直読み不可）。
            //
            // ⚠⚠ **接続と位置は別々に見る**（2026-08-16 実機で踏んだ）。
            //    電源が入っていれば `IsControllerConnected` は true だが、カメラから見えていないと
            //    姿勢は無効で、`OVRCameraRig` は**アンカーをトラッキング原点（床の中心）へ置く**
            //    （`OVRCameraRig.UpdateAnchors` は有効なコントローラが 1 つも無いと
            //     `GetLocalControllerPosition(Controller.None)` ＝ ゼロを書く）。
            //    接続だけを見ていたので、**手元の面が床の原点に出て「遠くに小さく」見えていた**。
            //    人形の左腕（`OvrHandTrackingBridge.TryReadController`）は最初から
            //    `GetControllerPositionValid` を見ていて、そこだけ正しかった。
            bool rConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            bool rTracked = rConnected && OVRInput.GetControllerPositionValid(OVRInput.Controller.RTouch);
            guidePanel?.SetControllerState(rConnected, rTracked);
            _rConnected = rConnected;
            _rTracked = rTracked;

            bool regActive = courseRegistration != null && courseRegistration.IsActive;

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
                haptics?.LeftNotify();
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
                haptics?.Closing();
                Debug.Log("[Haptics] 締めの合図（右・スタッフ）— 呪いが排除されて画がライブへ戻った");
            }

            // ---- モード遷移（副作用は OnModeChanged / ResetRun が担う）----
            // A / B の操作中に人差し指がトリガーへ掛かっても、位置合わせの長押しには数えない。
            int voidedBefore = _modeLogic.VoidedHolds;
            _modeLogic.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = Time.deltaTime,
                triggerHeld = rTrigger,
                resetHeld = aHeld,
                registrationActive = regActive,
                faceButtonHeld = aHeld || bHeld,
            });
            if (_modeLogic.VoidedHolds != voidedBefore)
            {
                // 振動は画にも音にも出ないので、重なりが起きたことはここに残す。
                Debug.Log("[OvrBridge] 右トリガーの長押しを無効にした（A/B と重なった入力）"
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
            }

            // HMDを持たない自動走行は、題字を自前で検証した後に開始門を true へ差し替える。
            // 通常時はこの provider 自体が _onboarding.StartAuthorized を返すので、この分岐へ入らない。
            if (showControl != null && showControl.StartAuthorized && !_onboarding.StartAuthorized)
                _onboarding.CompleteForAutomation();

            _lConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            _lTracked = _lConnected && OVRInput.GetControllerPositionValid(OVRInput.Controller.LTouch);
            HmdOnboardingAction onboardingAction = _onboarding.Tick(new HmdOnboardingInput
            {
                dt = Time.unscaledDeltaTime,
                hmdPresent = hmdPresent,
                leftConnected = _lConnected,
                leftPositionValid = _lTracked,
                xHeld = xHeld && mode == ControllerModeLogic.Mode.Normal,
                yHeld = yHeld && mode == ControllerModeLogic.Mode.Normal,
                titleAvailable = titleScreen != null && titleScreen.IsBuilt,
                titleReady = titleScreen != null && titleScreen.ReadyForStart,
                titleDone = titleScreen == null || titleScreen.ClosedAlready,
            });

            if (onboardingAction == HmdOnboardingAction.TutorialAccepted)
            {
                haptics?.LeftMark();
                Debug.Log("[Onboarding] 報告の練習を受け取りました");
            }
            else if (onboardingAction == HmdOnboardingAction.DismissTitle)
            {
                if (titleScreen != null && titleScreen.DismissTitle()) haptics?.Fire();
            }

            // タイトル表示は一時的な譲り（ステータス面・位置合わせ）が終わったあとも再試行する。
            if (_onboarding.Stage == HmdOnboardingStage.Title
                && titleScreen != null && titleScreen.Stage == TitleStage.Wait)
                titleScreen.ShowTitle();

            ApplyOnboardingPresentation();

            // ---- 本編の報告（左 X / Y の 1 秒長押し）-------------------------------
            // 導入が完了するまでは本編件数を一切触らない。位置合わせ中と締めの冒頭でも積まない。
            if (!_onboarding.StartAuthorized) _markNeedsRelease = true;
            else if (!leftMarkHeld) _markNeedsRelease = false;
            bool markTooEarly = showControl != null && showControl.IsMarkTooEarly;
            bool markFired = _markHold.Tick(Time.deltaTime,
                                            leftMarkHeld && _onboarding.StartAuthorized
                                            && !_markNeedsRelease
                                            && !markTooEarly
                                            && mode == ControllerModeLogic.Mode.Normal);
            if (markFired)
            {
                showControl?.RecordVisitorMark();
                haptics?.LeftMark();
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
            haptics?.SetLeftHoldProgress(visitorHoldProgress);

            // 長押しカウント進行を HoldTick 振動へ（トリガー入場 / A の体験者リセット / 登録の 0.5s ホールド
            // 平均サンプリングの最大を流す。登録中は SampleHoldProgress01 が 0.5 秒ホールドの進行ランプを鳴らす）。
            // ⚠ トリガー／A の頭 HoldTickDeadSec は鳴らさない。右グリップは読みもしない。
            float longPress = _modeLogic.TriggerHoldProgress01;
            if (mode == ControllerModeLogic.Mode.Normal)
                longPress = Mathf.Max(longPress, _modeLogic.ResetHoldProgress01);
            if (longPress < HoldTickDeadSec / LongPressSec) longPress = 0f;
            float holdProgress = longPress;
            if (courseRegistration != null)
                holdProgress = Mathf.Max(holdProgress, courseRegistration.SampleHoldProgress01);
            haptics?.SetHoldProgress(holdProgress);

            // ---- モード別の入力分配 ----
            switch (mode)
            {
                case ControllerModeLogic.Mode.Registration:
                    // 登録モード中は通常マッピングを抑止し、登録入力（A=マーク/やり直し・B=確定）を転送。
                    courseRegistration?.Feed(new CourseRegistrationController.RegInput
                    {
                        mark = aDown,       // A (右) Down: サンプリング開始 / Verify やり直し
                        markHeld = aHeld,   // A (右) ホールド: 0.5s 平均サンプリングの継続
                        confirm = bDown,    // B (右): Verify で確定
                        deltaTime = Time.deltaTime, // ホールド平均計時（純ロジック HoldAverageSampler へ供給）
                    });
                    break;

                case ControllerModeLogic.Mode.Normal:
                default:
                    // B: ステータス表示トグル（真実源 IsVisible の反転）。
                    if (bDown) { ToggleStatus(); haptics?.Action(); }

                    // 右 A の長押しは _modeLogic が体験者リセットへ送る。短押しでは何もしない。
                    // タイトル開始は体験者の左 X / Y 短押しだけ。
                    // 体験者の報告はモードの外で数える（上を見る）。
                    //    ここに置くと位置合わせから戻った 1 フレームで進捗の押し戻しが起きる。
                    // トリガー長押し=Registration 入場も _modeLogic が担う。
                    break;
            }
        }

        private void ApplyOnboardingPresentation()
        {
            CommsNotice notice = CommsNotice.None;
            switch (_onboarding.Prompt)
            {
                case HmdOnboardingPrompt.Greeting: notice = CommsNotice.Greeting; break;
                case HmdOnboardingPrompt.ControllerDisconnected: notice = CommsNotice.ControllerDisconnected; break;
                case HmdOnboardingPrompt.ControllerUntracked: notice = CommsNotice.ControllerUntracked; break;
                case HmdOnboardingPrompt.ControllerStaff: notice = CommsNotice.ControllerStaff; break;
                case HmdOnboardingPrompt.ControllerConfirmed: notice = CommsNotice.ControllerConfirmed; break;
                case HmdOnboardingPrompt.Tutorial: notice = CommsNotice.Tutorial; break;
                case HmdOnboardingPrompt.TutorialShort: notice = CommsNotice.TutorialShort; break;
                case HmdOnboardingPrompt.TutorialAccepted: notice = CommsNotice.TutorialAccepted; break;
                case HmdOnboardingPrompt.TutorialReconnect: notice = CommsNotice.TutorialReconnect; break;
                case HmdOnboardingPrompt.Reminder: notice = CommsNotice.TutorialReminder; break;
            }
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

        // ランリセット（現地手段）。LapCounter.ResetRun が周回リセット + cue 発火済みクリア + 現在ゾーン再シードを行う。
        // LapCounter 未配線なら CueScheduler 単独で発火済みだけクリアする（周回は動かないが安全側）。
        private void ResetRun()
        {
            // **号令元は ShowControlClient 1 か所に寄せる**（卓の ▶ ラン開始と同じ経路）。
            // 個別に叩いていた旧実装は、実測滞在が前の体験者の分と混ざる・体験の骨格（相）が
            // 戻らない、という非対称を持っていた。
            var show = FindObjectOfType<ShowControlClient>();
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
            _markHold.Reset();
            haptics?.Fire(); // 長押し発火（ランリセット）
            Debug.Log("[OvrBridge] Normal: 体験者リセット（右 A 2 秒長押し）");
        }

        // ---- 登録フローの触覚（購読は Assembly-CSharp 側・Tracking は OVRInput 非依存）----
        private void OnRegPointCaptured() => haptics?.Action(); // 点サンプル確定
        private void OnRegFitRejected() => haptics?.Error();    // 残差 NG・やり直し
        private void OnRegFitAccepted() => haptics?.Fire();     // 残差ガード通過・Verify 遷移（FitRejected と対称）
        private void OnRegSampleAborted() => haptics?.Error();  // 0.5s 未満で離してホールド中断
        // 確定保存 = Fire。この直後に IsActive=false → 次フレーム ModeChanged(Reg→Normal) でも Fire が来るが、
        // HapticSequenceLogic のピア優先（同ピークは再生中なら無視）で 1 回に畳まれる。
        private void OnRegConfirmed() => haptics?.Fire();

        private void ToggleStatus()
        {
            if (statusHud != null) statusHud.SetVisible(!statusHud.IsVisible);
        }

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
