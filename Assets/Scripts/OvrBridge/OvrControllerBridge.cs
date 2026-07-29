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
    /// 操作は<b>右コントローラ 4 入力だけ</b>で完結する（計画 2026-07-20_staff-input-hud-redesign.md）。
    /// モードは <see cref="ControllerModeLogic"/> の 2 状態（Normal / Registration）でゲートする:
    ///   - Normal: A=カメラ手動送り Next / B=ステータス表示トグル /
    ///             グリップ 2 秒長押し=ランリセット / トリガー 2 秒長押し=位置合わせ入場
    ///   - Registration: A=点サンプル(やり直し) / B=確定 / トリガー 2 秒長押し=キャンセル退場
    /// 体験者はコントローラを持たないため封印モード（旧 Run/Staff）・左手・スティック・cue 試射は撤去した。
    /// HMD 非装着→SignalLostFx / OS recenter→CourseFrame.MarkNeedsReRegistration のパッシブ系は現状維持。
    /// </summary>
    public sealed class OvrControllerBridge : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("カメラ切替を一本化する CameraSwitchDirector（Screen 上）。割当時はここ経由" +
                 "（時間ガード + dip 演出）。null なら registry を直接叩く（後方互換）。")]
        [SerializeField] private CameraSwitchDirector? switchDirector;

        [Tooltip("トラッキングロスト（HMD 非装着）を通知する SignalLostFx（Screen 上）。null なら通知しない。")]
        [SerializeField] private SignalLostFx? signalFx;

        [Tooltip("演出モードのラベルを heartbeat へ載せる ShowControlClient（Screen 上）。未割当なら Start で自動取得。")]
        [SerializeField] private ShowControlClient? showControl;

        [Header("Mappings（右コントローラのみ）")]
        [Tooltip("カメラ手動送り Next（Normal）/ 点サンプル・やり直し（Registration）に使う右手ボタン。既定 A。")]
        [SerializeField] private OVRInput.Button nextButton = OVRInput.Button.One;   // A (右)

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
        [Tooltip("グリップ 2 秒長押し＝ランリセット（周回リセット + cue 発火済みクリア）の対象 LapCounter。" +
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

        // トリガー / グリップの長押し閾値 (秒)。SerializeField にすると既存シーン YAML に未記載で 0 と読まれ
        // 判定が壊れる（unity-prefab-fields の罠）。調整不要なので const 固定。
        private const float LongPressSec = 2.0f;

        // モード状態機械（純ロジック。入力を bool/float で Tick する）。
        private readonly ControllerModeLogic _modeLogic = new();

        // OS recenter 購読済みフラグ（OVRManager.display は初期化順で null のことがあるためリトライする）。
        private bool _recenterSubscribed;

        private void Start()
        {
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();

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
            if (signalFx != null)
            {
                bool present = OVRManager.instance == null || OVRManager.isHmdPresent;
                signalFx.ReportTrackingLost(!present);
            }

            // 表示レートの要求（起動直後だけ。成功か時間切れで以後は何もしない）。
            // ここに置くのは、既存シーン / prefab へコンポーネントを 1 個増やさずに済ませるため。
            DisplayRateRequester.Tick(Time.unscaledDeltaTime);

            // ---- 入力を 1 回だけ読む（右手のみ。同じボタンを複数箇所で拾わないため）----
            // Button.One/Two はコントローラ未指定だと両手から拾う（One=A|X 等）ため、必ず RTouch を明示する。
            bool aDown = OVRInput.GetDown(nextButton, OVRInput.Controller.RTouch);   // A: Next / マーク・やり直し
            bool aHeld = OVRInput.Get(nextButton, OVRInput.Controller.RTouch);       // A: 押しっぱなし（登録のホールド平均用）
            bool bDown = OVRInput.GetDown(statusButton, OVRInput.Controller.RTouch); // B: ステータストグル / 確定
            bool rGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool rTrigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            bool gripDown = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

            // 監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る＝「入力は届いている」）。
            // アクション実行時は switch 内で Action を後着し、ピーク優先で Ack を昇格させる。
            if (aDown || bDown || gripDown || triggerDown) haptics?.Ack();

            // 右コントローラ接続状態をガイドパネルへ push（未接続時のパネル非表示に使う。
            // Diagnostics は OVRInput 非依存のため直読み不可）。StatusHud の接続行は廃止したため push しない。
            bool rConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            guidePanel?.SetControllerConnected(rConnected);

            bool regActive = courseRegistration != null && courseRegistration.IsActive;

            // ---- モード遷移（副作用は OnModeChanged / ResetRun が担う）----
            _modeLogic.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = Time.deltaTime,
                triggerHeld = rTrigger,
                gripHeld = rGrip,
                registrationActive = regActive,
            });
            ControllerModeLogic.Mode mode = _modeLogic.Current;

            // 長押しカウント進行を HoldTick 振動へ（トリガー入場 / グリップ ランリセット / 登録の 0.5s ホールド
            // 平均サンプリングの最大を流す。登録中は SampleHoldProgress01 が 0.5 秒ホールドの進行ランプを鳴らす）。
            float holdProgress = Mathf.Max(_modeLogic.TriggerHoldProgress01, _modeLogic.GripHoldProgress01);
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
                    // A: カメラ手動送り Next（設営・リハ確認用。誤爆しても Zone 自動が復帰する）。
                    if (aDown && registry != null && registry.Count > 0)
                    {
                        if (switchDirector != null)
                        {
                            if (switchDirector.Next()) haptics?.Action(); // 受理＝アクション実行（Ack を昇格）
                            else if (switchDirector.InsertActive)
                            {
                                // インサート差し込み中は手動切替を破棄し、赤メッセージ + 失敗振動で伝える。
                                guidePanel?.ShowTransient("演出中は切り替えできません");
                                haptics?.Error();
                            }
                            // それ以外の false（クールダウン中等）は Ack 済みのため追加フィードバックなし。
                        }
                        else { registry.Next(); haptics?.Action(); }
                    }
                    // B: ステータス表示トグル（真実源 IsVisible の反転）。
                    if (bDown) { ToggleStatus(); haptics?.Action(); }
                    // グリップ長押し=ランリセット / トリガー長押し=Registration 入場は _modeLogic が担う。
                    break;
            }
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
            haptics?.Fire(); // 長押し発火（ランリセット）
            Debug.Log("[OvrBridge] Normal: ランリセット（右グリップ 2 秒長押し）");
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
