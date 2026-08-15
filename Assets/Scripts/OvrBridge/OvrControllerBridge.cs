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
    ///   - Normal: A=タイトルを閉じて体験を始める / B=ステータス表示トグル /
    ///             グリップ 2 秒長押し=ランリセット / トリガー 2 秒長押し=位置合わせ入場
    ///   - Registration: A=点サンプル(やり直し) / B=確定 / トリガー 2 秒長押し=キャンセル退場
    ///
    /// ⚠ <b>A のカメラ手動送りは 2026-08-12 に撤去した</b>（ユーザー宣言「カメラの手送り機能は
    /// 要らないです」）。A は<b>タイトルを閉じる 1 つだけ</b>で、閉じた後の A は何もしない。
    /// キーボード経由の切替（<c>CameraSwitchInput</c> の Tab / 1-9・Editor 用）は残っている。
    /// 封印モード（旧 Run/Staff）・スティック・cue 試射は撤去した。
    ///
    /// <b>左は体験者の手。読むのは X / Y だけ</b>（2026-08-15・<c>canon/LEDGER.md</c> 0042 / 0050）:
    /// どちらを押しても同じで、<b>2 秒長押し</b>で異変の報告になる（<see cref="VisitorMarkHoldLogic"/>）。
    /// 押し方と進捗は左コントローラに追従する <see cref="VisitorMarkPanel"/> が出す。
    /// スティック・トリガー・グリップ・A/B は左からは 1 ビットも読まない。
    /// HMD 非装着→SignalLostFx / OS recenter→CourseFrame.MarkNeedsReRegistration のパッシブ系は現状維持。
    /// </summary>
    public sealed class OvrControllerBridge : MonoBehaviour
    {
        [Tooltip("トラッキングロスト（HMD 非装着）を通知する SignalLostFx（Screen 上）。null なら通知しない。")]
        [SerializeField] private SignalLostFx? signalFx;

        [Tooltip("演出モードのラベルを heartbeat へ載せる ShowControlClient（Screen 上）。未割当なら Start で自動取得。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("タイトル画面（[Title] 上）。**A の行き先**。null だと A は Normal で何もしない" +
                 "（タイトルが出ないだけで、ほかの操作は従来どおり動く）。")]
        [SerializeField] private TitleScreen? titleScreen;

        [Header("Mappings（右コントローラのみ）")]
        [Tooltip("タイトルを閉じて体験を始める（Normal）/ 点サンプル・やり直し（Registration）に使う右手ボタン。既定 A。")]
        [SerializeField] private OVRInput.Button primaryButton = OVRInput.Button.One;   // A (右)

        [Tooltip("ステータス表示トグル（Normal）/ 登録確定（Registration）に使う右手ボタン。既定 B。")]
        [SerializeField] private OVRInput.Button statusButton = OVRInput.Button.Two;  // B (右)

        [Header("Status HUD")]
        [Tooltip("単一サーフェス StatusHud（[StatusHud] 上）。B 押下でステータス表示をトグルする。")]
        [SerializeField] private StatusHud? statusHud;

        [Tooltip("スタッフ用コントローラ操作ガイドパネル（右コントローラに追従）。現在モードの操作説明を常時表示。" +
                 "null でも全機能は従来通り動く（ガイドが出ないだけ）。")]
        [SerializeField] private ControllerGuidePanel? guidePanel;

        [Tooltip("体験者の報告ボタンの面（左コントローラに追従）。押し方と長押しゲージを出す。" +
                 "null でも報告そのものは動く（面が出ないだけ）。")]
        [SerializeField] private VisitorMarkPanel? markPanel;

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

        // 体験者の報告ボタン（左 X / 左 Y）の 2 秒長押し（純ロジック）。
        private readonly VisitorMarkHoldLogic _markHold = new();

        // OS recenter 購読済みフラグ（OVRManager.display は初期化順で null のことがあるためリトライする）。
        private bool _recenterSubscribed;

        [Tooltip("上司からの連絡の面。届いた瞬間に左コントローラを震わせるためだけに読む。")]
        [SerializeField] private CommsPanel? comms;
        private int _lastCommsPulse;

        private void Start()
        {
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();

            if (titleScreen == null) titleScreen = FindObjectOfType<TitleScreen>();

            // 体験者の報告ボタンの面。⚠ **ここで 1 度だけ探す**（毎フレーム FindObjectOfType を
            // 走らせない）。シーンを焼いていなければ見つからず、報告そのものは面が無くても動く。
            if (markPanel == null) markPanel = FindObjectOfType<VisitorMarkPanel>();

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
                var title = titleScreen;
                showControl.UserPresentProvider = () => OVRPlugin.userPresent;
                showControl.StartAuthorizedProvider = () => !(title != null && title.IsBlocking);
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

            // ---- 入力を 1 回だけ読む（同じボタンを複数箇所で拾わないため）----
            // Button.One/Two はコントローラ未指定だと両手から拾う（One=A|X 等）ため、必ず RTouch を明示する。
            bool aDown = OVRInput.GetDown(primaryButton, OVRInput.Controller.RTouch); // A: タイトルを閉じる / マーク・やり直し
            bool aHeld = OVRInput.Get(primaryButton, OVRInput.Controller.RTouch);     // A: 押しっぱなし（登録のホールド平均用）
            bool bDown = OVRInput.GetDown(statusButton, OVRInput.Controller.RTouch); // B: ステータストグル / 確定
            bool rGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool rTrigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            bool gripDown = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

            // ---- 体験者の手（左）。**X でも Y でもよい**（2026-08-15・canon/LEDGER.md 0050）----
            // ⚠⚠ **`Button.Three` / `Button.Four` を `Controller.LTouch` と組み合わせてはいけない。**
            //    LTouch の仮想マップは `Three = RawButton.None` / `Four = RawButton.None` で
            //    （`OVRInput.cs` の `OVRControllerLTouch`）、**押しても永遠に false になる**。
            //    Three=X / Four=Y が生きているのは左右をまとめた `Controller.Touch` のマップだけで、
            //    `ShouldResolveController` は LTouch 指定のとき Touch を弾く。
            //    2026-08-15 まで記録ボタンはこの形で書かれていて、**実機で一度も発火していなかった**
            //    （実機で押した記録が無く、ログにも `[XP] ev=mark` が 1 行も出ていない）。
            // ⇒ **物理ボタンを名指しする `RawButton` を使う**（X / Y は左にしか無いので取り違えない）。
            bool leftMarkHeld = OVRInput.Get(OVRInput.RawButton.X | OVRInput.RawButton.Y,
                                             OVRInput.Controller.LTouch);

            // 監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る＝「入力は届いている」）。
            // アクション実行時は switch 内で Action を後着し、ピーク優先で Ack を昇格させる。
            if (aDown || bDown || gripDown || triggerDown) haptics?.Ack();

            // 右コントローラ接続状態をガイドパネルへ push（未接続時のパネル非表示に使う。
            // Diagnostics は OVRInput 非依存のため直読み不可）。StatusHud の接続行は廃止したため push しない。
            bool rConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            guidePanel?.SetControllerConnected(rConnected);

            bool regActive = courseRegistration != null && courseRegistration.IsActive;

            // 上司からの連絡が届いたら、体験者の手（左）を震わせる。
            // ⚠ 連絡の面（Diagnostics）も体験の骨格（Streaming）も OVR を参照しない規約なので、
            //    **向こうから読みに来る**（ShowBodyInput / UserPresentProvider と同じ流儀）。
            if (comms == null) comms = FindObjectOfType<CommsPanel>();
            if (comms != null && comms.PulseCount != _lastCommsPulse)
            {
                _lastCommsPulse = comms.PulseCount;
                haptics?.LeftNotify();
            }

            // ---- モード遷移（副作用は OnModeChanged / ResetRun が担う）----
            _modeLogic.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = Time.deltaTime,
                triggerHeld = rTrigger,
                gripHeld = rGrip,
                registrationActive = regActive,
            });
            ControllerModeLogic.Mode mode = _modeLogic.Current;

            // ---- 体験者の報告（左 X / 左 Y の 2 秒長押し）-------------------------------
            // 紙（調査依頼書）の「気になるものが見えたら、手元のボタンを押してください」が指しているのがこれ。
            // **体験者が持つ唯一の入力**で、左コントローラは他に何も読まない。
            //
            // ⚠ 体験の進行には 1 ビットも使わない（押さなくても同じように進む）。
            //    正誤も返さない — 返すと答え合わせになり、装置が「何が異変か」を判定してしまう。
            // ⚠ **短押しでは通さない**（2026-08-15）。歩きながら握り込むので、押した瞬間に決まると
            //    「触れただけ」が報告になる。位置合わせの点サンプルと同じ「意思のある長押し」にする。
            // ⚠ 位置合わせ中（スタッフ作業）は数えない。作業のあいだ手元でゲージが伸びない。
            bool markFired = _markHold.Tick(Time.deltaTime,
                                            leftMarkHeld && mode == ControllerModeLogic.Mode.Normal);
            if (markFired)
            {
                showControl?.RecordVisitorMark();
                haptics?.LeftMark();   // 返すのは「受け取った」の 1 種類だけ
            }
            if (markPanel != null)
            {
                markPanel.SetControllerConnected(OVRInput.IsControllerConnected(OVRInput.Controller.LTouch));
                markPanel.SetMarkState(_markHold.Progress01, _markHold.Confirming);
            }
            // 長押しの手応えも左へ返す（右の HoldTick とは別の時間軸）。
            // 進捗 1 で HoldTick は止まり、代わりに上の LeftMark が鳴る。
            haptics?.SetLeftHoldProgress(_markHold.Progress01);

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
                    // A: **真っ暗から題字を呼び出す**（2026-08-12 のユーザー指示で意味が変わった。
                    //    それまでは「立っている題字を閉じる」だった）。呼び出したあとは
                    //    2 秒で自動的に閉じてパススルーへ渡るので、押すのは 1 回だけ。
                    //    Normal での A はこれ 1 つだけで、タイトルが立っていなければ何も起きない。
                    //
                    //    カメラ手動送りは 2026-08-12 に撤去した（ユーザー宣言「カメラの手送り機能は
                    //    要らないです」）。設営でカメラを見たいときは Web 卓の 📺 カメラ固定か、
                    //    Editor のキーボード（CameraSwitchInput の Tab / 1-9）を使う。
                    if (aDown)
                    {
                        if (titleScreen != null && titleScreen.RequestAdvance())
                        {
                            haptics?.Fire();   // 体験の開始。短押しより強い手応えを返す
                            Debug.Log("[Title] A を受け取りました（真っ暗なら題字を呼び出す / 立っていれば閉じる）");
                        }
                        else
                        {
                            // ⚠ **空振りを黙らせない**（2026-08-14）。旧実装は成功したときだけログを出して
                            //    いたので、押しても何も起きない現場では「A が壊れた」としか見えなかった。
                            //    もう閉じている（＝スタッフの二度押し）は正常なので振動は返さない。
                            string why = titleScreen == null
                                ? "タイトルがシーンに居ない（menu scene で焼き直す）"
                                : titleScreen.DescribeAdvanceBlock();
                            bool benign = titleScreen != null && titleScreen.ClosedAlready;
                            if (benign) Debug.Log($"[Title] A は何もしませんでした（{why}）");
                            else
                            {
                                haptics?.Error();
                                Debug.LogWarning($"[Title] A が効きませんでした（{why}）");
                            }
                        }
                    }
                    // B: ステータス表示トグル（真実源 IsVisible の反転）。
                    if (bDown) { ToggleStatus(); haptics?.Action(); }

                    // ⚠ 体験者の報告（左 X / 左 Y の 2 秒長押し）は**モードの外**で数える（上を見る）。
                    //    ここに置くと位置合わせから戻った 1 フレームで進捗の押し戻しが起きる。
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
            // 体験者が代わるので、進行中の報告の長押しと余韻も落とす。
            _markHold.Reset();
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
