#nullable enable
using FixedCamVr.Input;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using UnityEngine;

namespace FixedCamVr.OvrBridge
{
    /// <summary>
    /// asmdef を持たないため Assembly-CSharp に入る。Meta XR SDK の OVRInput にアクセスできる
    /// 唯一の場所として、コントローラ入力を Streaming / Tracking のコンポーネントに転送する橋渡し。
    /// 配置先は [Streaming] GameObject 等。
    ///
    /// 操作は <see cref="ControllerModeLogic"/> のモードでゲートする（計画 2026-07-19_controller-roles.md）:
    ///   - Run（既定・ゲスト安全）: 両グリップ 3 秒長押し（→ Staff）以外すべて不活性
    ///   - Staff: カメラ切替 / anchor / HUD / cue 試射 / 登録入口が有効。入場で HUD 自動 ON + チートシート表示
    ///   - Registration: 既存の 2 点登録フロー（A=マーク / B=確定 / スティック微調整）へ入力を転送
    /// ボタン割当・各機能の実体は不変で、入口のモードゲートだけを足している。
    /// </summary>
    public sealed class OvrControllerBridge : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private ScreenAnchor? screenAnchor;

        [Tooltip("カメラ切替を一本化する CameraSwitchDirector（Screen 上）。割当時はここ経由" +
                 "（時間ガード + dip 演出）。null なら registry を直接叩く（後方互換）。")]
        [SerializeField] private CameraSwitchDirector? switchDirector;

        [Tooltip("トラッキングロスト（HMD 非装着）を通知する SignalLostFx（Screen 上）。null なら通知しない。")]
        [SerializeField] private SignalLostFx? signalFx;

        [Tooltip("演出 cue を発火する ShowControlClient（Screen 上）。未割当なら Start で自動取得。")]
        [SerializeField] private ShowControlClient? showControl;

        [Header("Mappings")]
        [SerializeField] private OVRInput.Button nextButton = OVRInput.Button.One;        // A (右)
        [SerializeField] private OVRInput.Button prevButton = OVRInput.Button.Two;        // B (右)
        [SerializeField] private OVRInput.Button anchorToggleButton = OVRInput.Button.Three; // X (左)
        [SerializeField] private OVRInput.Button hudToggleButton = OVRInput.Button.Four;     // Y (左)

        [Header("HUD")]
        [Tooltip("RuntimeDebugHud をアサイン。Y ボタン押下時に SetVisible(bool) を SendMessage で呼ぶ。" +
                 " Diagnostics 名前空間に直接依存しないため MonoBehaviour で受ける。")]
        [SerializeField] private MonoBehaviour? hud = null;

        [Header("Staff mode")]
        [Tooltip("Staff モード中に出すチートシート（バインディング一覧）。入場で表示・退場で非表示にする。")]
        [SerializeField] private StaffPanel? staffPanel;

        [Tooltip("Staff で無操作がこの秒数続いたら Run へ戻る（ゲスト安全へのフェイルセーフ）。")]
        [SerializeField, Min(1f)] private float staffIdleTimeoutSec = 120f;

        [Header("Course registration")]
        [Tooltip("CourseRegistrationController（[Tracker] 上）。Staff で右スティック押込により登録モードへ入り、" +
                 "登録中は入力（A=マーク/やり直し, B=確定, スティック=微調整）を転送する。")]
        [SerializeField] private CourseRegistrationController? courseRegistration;

        [Tooltip("OS recenter（Oculus ボタン長押し）検知で『要再登録』を立てる CourseFrame（[Tracker] 上）。")]
        [SerializeField] private CourseFrame? courseFrame;

        [Tooltip("Run⇄Staff の切替・Registration キャンセルに必要な両グリップの長押し秒数。")]
        [SerializeField, Min(0.2f)] private float calibToggleHoldSec = 3.0f;

        // 片手グリップの『単押し』と判定する最大長さ(秒)。これ以下の片手タップで（Staff 中のみ）今見ている
        // カメラの演出をトグルする。両手押し / 長押しは Run⇄Staff 儀式側なので cue は発火しない。
        // ※ SerializeField にすると既存シーンの YAML に未記載で 0 と読まれ判定が壊れる
        //   （unity-prefab-fields の罠）。調整不要なので const 固定。
        private const float GripTapMaxSec = 0.4f;

        // スティックが「動いている」と見なす軸の二乗しきい値（Staff 無操作リセットの活動判定）。
        private const float StickActivitySqr = 0.15f * 0.15f;

        // モード状態機械（純ロジック。入力を bool/float で Tick する）。
        private readonly ControllerModeLogic _modeLogic = new();

        // HUD の型付き参照（Start で hud からキャスト）。トグルは真実源 IsVisible を反転する。
        private FixedCamVr.Diagnostics.RuntimeDebugHud? _hudTyped;
        // Staff 入場前の HUD 表示状態（退場時に復元する）。
        private bool _hudPrevVisible;

        // グリップ単押し検出（release ベース。両手 or 長押しは儀式なので除外）。
        private bool _gripPressActive;
        private float _gripPressStart;
        private bool _gripPressBoth;
        // OS recenter 購読済みフラグ（OVRManager.display は初期化順で null のことがあるためリトライする）。
        private bool _recenterSubscribed;

        private void Start()
        {
            // 型付きで保持し、トグル時に真実源（IsVisible）を毎回読む。ローカル bool の
            // シャドウコピーは HudToggleInput（H キー）併用時に desync して「初回押下が空振り」
            // になる（2026-06-18 の既知バグ類型）。
            _hudTyped = hud as FixedCamVr.Diagnostics.RuntimeDebugHud;
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();

            // 新規 SerializeField は既存シーン YAML に未記載だと型 default(0) で読まれる（unity-prefab-fields の罠）。
            // idle=0 は Staff を即 Run へ弾いてモードを壊すため、Setup 未再実行の instance でも安全側へ寄せる。
            float gripHoldSec = calibToggleHoldSec >= 0.2f ? calibToggleHoldSec : 3f;
            float idleTimeoutSec = staffIdleTimeoutSec >= 1f ? staffIdleTimeoutSec : 120f;
            _modeLogic.Configure(gripHoldSec, idleTimeoutSec);
            _modeLogic.Reset(ControllerModeLogic.Mode.Run);
            _modeLogic.ModeChanged += OnModeChanged;
            PushModeLabel(_modeLogic.Current); // 初期状態 RUN を HUD / heartbeat へ

            TrySubscribeRecenter();
        }

        private void OnDestroy()
        {
            _modeLogic.ModeChanged -= OnModeChanged;
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

            // ---- 入力を 1 回だけ読む（同じボタンを複数箇所で拾わないため）----
            bool lGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            bool rGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            bool bothGrips = lGrip && rGrip;

            // Button.One/Two はコントローラ未指定だと両手から拾う（One=A|X 等）ため、必ず RTouch/LTouch を明示する。
            bool nextDown = OVRInput.GetDown(nextButton, OVRInput.Controller.RTouch);   // A (右)
            bool nextHeld = OVRInput.Get(nextButton, OVRInput.Controller.RTouch);       // A (右) 押しっぱなし（登録のホールド平均用）
            bool prevDown = OVRInput.GetDown(prevButton, OVRInput.Controller.RTouch);   // B (右)
            bool anchorDown = OVRInput.GetDown(anchorToggleButton, OVRInput.Controller.LTouch); // X (左)
            bool hudDown = OVRInput.GetDown(hudToggleButton, OVRInput.Controller.LTouch);        // Y (左)
            bool stickPressDown = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
            Vector2 lStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            Vector2 rStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            bool stickMoved = lStick.sqrMagnitude > StickActivitySqr || rStick.sqrMagnitude > StickActivitySqr;

            bool regActive = courseRegistration != null && courseRegistration.IsActive;
            bool staffActivity = nextDown || prevDown || anchorDown || hudDown
                                 || stickPressDown || stickMoved || lGrip || rGrip;

            // ---- モード遷移（副作用は OnModeChanged が担う）----
            _modeLogic.Tick(new ControllerModeLogic.Frame
            {
                deltaTime = Time.deltaTime,
                bothGrips = bothGrips,
                registrationActive = regActive,
                stickPressDown = stickPressDown,
                staffActivity = staffActivity,
            });
            ControllerModeLogic.Mode mode = _modeLogic.Current;

            // 片手グリップ単押しの検出（release ベース）。cue 発火は Staff 中のみ許可。
            UpdateGripTap(lGrip, rGrip, mode);

            // ---- モード別の入力分配 ----
            switch (mode)
            {
                case ControllerModeLogic.Mode.Registration:
                    // 登録モード中は通常マッピングを抑止し、登録入力（A=マーク/やり直し・B=確定・スティック微調整）を転送。
                    courseRegistration?.Feed(new CourseRegistrationController.RegInput
                    {
                        mark = nextDown,        // A (右) Down: サンプリング開始 / Verify やり直し
                        markHeld = nextHeld,    // A (右) ホールド: 0.5s 平均サンプリングの継続
                        confirm = prevDown,     // B (右)
                        nudgeMove = lStick,     // 左スティック: 平行移動
                        nudgeYaw = rStick.x,    // 右スティック横: yaw
                    });
                    break;

                case ControllerModeLogic.Mode.Staff:
                    // カメラ切替（A=Next / B=Prev。右手限定）。
                    if (registry != null && registry.Count > 0)
                    {
                        if (switchDirector != null)
                        {
                            if (nextDown) switchDirector.Next();
                            if (prevDown) switchDirector.Prev();
                        }
                        else
                        {
                            if (nextDown) registry.Next();
                            if (prevDown) registry.Prev();
                        }
                    }
                    // 左 X: スクリーン追従の凍結トグル。
                    if (screenAnchor != null && anchorDown) screenAnchor.Toggle();
                    // 左 Y: HUD 表示トグル（真実源 IsVisible の反転）。
                    if (hudDown) ToggleHud();
                    // 右グリップ単押し: cue 試射（UpdateGripTap の release で発火）。
                    break;

                case ControllerModeLogic.Mode.Run:
                default:
                    // 封印。両グリップ 3 秒（Staff 入口）以外は何もしない。
                    break;
            }
        }

        // 片手グリップの単押し（tap）を release ベースで検出し、Staff 中のみ cue をトグルする。
        // 両手押し / 規定時間超過の押下は儀式扱いで cue を発火しない。
        private void UpdateGripTap(bool lGrip, bool rGrip, ControllerModeLogic.Mode mode)
        {
            if (lGrip || rGrip)
            {
                if (!_gripPressActive)
                {
                    _gripPressActive = true;
                    _gripPressStart = Time.unscaledTime;
                    _gripPressBoth = lGrip && rGrip;
                }
                else if (lGrip && rGrip)
                {
                    _gripPressBoth = true; // 途中で両手になったら儀式扱い
                }
            }
            else if (_gripPressActive)
            {
                _gripPressActive = false;
                bool wasTap = !_gripPressBoth && (Time.unscaledTime - _gripPressStart) < GripTapMaxSec;
                if (wasTap && mode == ControllerModeLogic.Mode.Staff)
                    showControl?.ToggleActiveCameraCue();
            }
        }

        private void ToggleHud()
        {
            if (_hudTyped != null)
                _hudTyped.SetVisible(!_hudTyped.IsVisible);
            else if (hud != null) // RuntimeDebugHud 以外は状態が読めないので非表示のみ
                hud.SendMessage("SetVisible", false, SendMessageOptions.DontRequireReceiver);
        }

        // ---- モード遷移の副作用（HUD 自動 ON/復元・チートシート・登録開始/停止・状態露出）----

        private void OnModeChanged(ControllerModeLogic.Mode from, ControllerModeLogic.Mode to)
        {
            switch (to)
            {
                case ControllerModeLogic.Mode.Run:
                    // 登録が残っていれば止める（保険。通常 Registration から直接 Run へは来ない）。
                    if (courseRegistration != null && courseRegistration.IsActive) courseRegistration.Toggle();
                    staffPanel?.SetVisible(false);
                    RestoreHud();
                    break;

                case ControllerModeLogic.Mode.Staff:
                    if (from == ControllerModeLogic.Mode.Run) CaptureAndEnableHud();
                    // Registration からの復帰でまだアクティブ＝グリップキャンセル。登録を止める。
                    if (from == ControllerModeLogic.Mode.Registration
                        && courseRegistration != null && courseRegistration.IsActive)
                        courseRegistration.Toggle();
                    staffPanel?.SetVisible(true);
                    break;

                case ControllerModeLogic.Mode.Registration:
                    staffPanel?.SetVisible(false); // 登録側の [CourseRegGuidance] に任せる
                    if (courseRegistration != null && !courseRegistration.IsActive) courseRegistration.Toggle(); // 開始
                    break;
            }
            PushModeLabel(to);
        }

        private void CaptureAndEnableHud()
        {
            if (_hudTyped == null) return;
            _hudPrevVisible = _hudTyped.IsVisible;
            _hudTyped.SetVisible(true);
        }

        private void RestoreHud()
        {
            if (_hudTyped != null) _hudTyped.SetVisible(_hudPrevVisible);
        }

        private void PushModeLabel(ControllerModeLogic.Mode mode)
        {
            string label = mode switch
            {
                ControllerModeLogic.Mode.Staff => "STAFF",
                ControllerModeLogic.Mode.Registration => "REG",
                _ => "RUN",
            };
            _hudTyped?.SetModeLabel(label);
            showControl?.SetControllerMode(label);
        }
    }
}
