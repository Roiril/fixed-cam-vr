#nullable enable
using System;
using FixedCamVr.Input;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// メインスクリーン上のスタッフ用準備。通信は既存の全台並列受信を観測するだけ。
    ///
    /// 2026-10-10 に作り直した（<c>.claude/plans/2026-10-10_staff-flow-redesign.md</c> §3）。
    /// 初回の準備と体験者を迎える段は<b>チェックリスト</b>で、順序を強制しない
    /// （右トリガー 2 秒の位置合わせは機器がそろう前でも入れる）。
    /// 主画面はタブレットなので、ここには手順の説明を流さない。
    /// </summary>
    public sealed class StaffSetupPanel : MonoBehaviour
    {
        public const float TabletFreshSec = 12f; // 既存タブレットpulseは5秒間隔
        public const float IdentityFreshSec = 4.5f;
        private static Color SecondaryInk => Color.Lerp(HmdTextStyle.InkDim, HmdTextStyle.Ink, .6f);
        /// <summary>済みの行の色。</summary>
        public static readonly Color ReadyInk = new Color(.48f, .82f, .66f);
        public static StaffSetupPanel? Instance { get; private set; }
        public StaffSetupLogic Logic { get; } = new StaffSetupLogic();
        private CameraStreamRegistry? _registry;
        private ShowControlClient? _show;
        private CourseFrame? _frame;
        private CourseRegistrationController? _registration;
        private DiscoveryClient? _discovery;
        private ScreenAnchor? _anchor;
        private ShowRunDirector? _run;
        private float _runRetryAt;
        private GameObject? _visual;
        private Material? _backMaterial;
        private TMP_Text? _header, _device, _call, _body, _guideAction, _guideDetails, _footer, _hint;
        private const int RowCount = 5; // 位置合わせ・カメラ A・B・C・タブレット
        private readonly TMP_Text?[] _marks = new TMP_Text?[RowCount];
        private readonly TMP_Text?[] _rows = new TMP_Text?[RowCount];
        private readonly TMP_Text?[] _statuses = new TMP_Text?[RowCount];
        private readonly TMP_Text?[] _fixes = new TMP_Text?[RowCount];
        private readonly SetupCameraEvidence[] _cameras = new SetupCameraEvidence[3];
        private readonly VisitorPortal.StaffCameraStatus[] _statusCameras = new VisitorPortal.StaffCameraStatus[3];
        private bool _confirmPending, _automation;
        private bool _rightConnected, _rightTracked;
        private string _lastLog = "";
        private string? _deviceIp;
        private string _deviceLabel = "";
        private float _recoverySec;
        private float _resetProgress, _resetNoticeUntil;
        private VisitorPortal? _statusPortal;
        private Func<VisitorPortal.StaffStatus>? _statusProvider;
        private const float RecoveryScreenDelaySec = 1.5f;
#if UNITY_EDITOR
        private StaffSetupSnapshot? _previewSnapshot;
        private string? _previewDeviceIp;
#endif

        // ---- 画面の文言 ------------------------------------------------------------------
        // ⚠ 字を足したら `.\tools\unity.ps1 menu hud-font`（このファイルは収集元）。
        // ⚠ 記号は同梱フォントにある字だけ。バツ印 U+2715 は SourceHanSans に無いので × を使う。

        public const string HeaderText = "スタッフ用準備";
        public const string MarkReady = "●", MarkChecking = "…", MarkTrouble = "×";
        public const string CallStaffText = "スタッフをお呼びください\nPlease call a member of staff.\nVeuillez appeler un membre du personnel.";
        public const string WelcomeText = "次の体験者の準備はタブレットで行います";
        public const string NotReadyText = "項目がそろったらヘッドセットを外して台に置きます";
        public const string ReadyText = "準備ができました\nヘッドセットを台に置きます。次の体験者の準備はタブレットで行います";
        public const string RecallHint = "画面が見えないとき：B を 1 秒押す";
        public const string CancelHint = "中止：右トリガーを 2 秒押し続ける（確定前の位置へ戻します）";
        private const string PositionLabel = "位置合わせ", TabletLabel = "タブレット";
        private static readonly string[] CameraLabels = { "カメラ A", "カメラ B", "カメラ C" };
        private static readonly string[] CameraIds = { "A", "B", "C" };
        private const string PositionDone = "済み", PositionNeeded = "まだです", PositionRedo = "やり直しが必要です";
        private const string PositionFix = "右トリガーを 2 秒押し続けると始まります";
        private const string CameraOk = "映っています", Checking = "確認しています";
        private const string CameraNoStream = "映像が届いていません", CameraStale = "映像が止まっています",
            CameraIdentity = "どのカメラか確認できません", CameraWrong = "別のカメラが映っています",
            CameraWrongShow = "別の作品の設定です";
        private static readonly string[] OpenStreamerFix =
        {
            "スマホ A の FixedCam Streamer を開く",
            "スマホ B の FixedCam Streamer を開く",
            "スマホ C の FixedCam Streamer を開く",
        };
        private const string CheckSlotFix = "スマホの A／B／C の設定を確かめる";
        private const string CheckShowFix = "スマホの作品の設定を確かめる";
        private const string TabletOk = "つながっています", TabletTrouble = "応答がありません";
        private const string TabletFix = "タブレットで「廻リ視 博士」を開く";
        private const string ContentMissing = "体験の素材を確認できません　正しい APK で起動し直す";
        private const string RecoveryFallback = "開始の条件を確認しています";
        private const string ResetDone = "リセットしました　右 A：離す";
        private const string ResetCancelled = "リセットを中止しました";

        private void Awake() => Instance = this;
        public bool Visible => !_automation && !Logic.Started
            && (Logic.Stage != StaffSetupStage.HandedOff || (Logic.BlocksVisitor
                && (!Logic.SettingsSettled || _recoverySec >= RecoveryScreenDelaySec)));
        public bool BlocksVisitor => !_automation && Logic.BlocksVisitor;

        public static StaffSetupPanel? Ensure()
        {
            var existing = FindObjectOfType<StaffSetupPanel>();
            if (existing != null) return existing;
            var screen = FindObjectOfType<MjpegScreen>();
            if (screen == null) { Debug.LogError("[StaffSetup] メインスクリーンがありません"); return null; }
            var go = new GameObject("[StaffSetup]");
            go.transform.SetParent(screen.transform, false);
            Vector3 s = screen.transform.lossyScale;
            go.transform.localScale = new Vector3(1f / Mathf.Max(0.001f, Mathf.Abs(s.x)),
                1f / Mathf.Max(0.001f, Mathf.Abs(s.y)), 1f / Mathf.Max(0.001f, Mathf.Abs(s.z)));
            go.transform.localPosition = new Vector3(0f, 0f, -0.06f);
            var panel = go.AddComponent<StaffSetupPanel>();
            panel._anchor = screen.GetComponent<ScreenAnchor>();
            var mesh = screen.GetComponent<MeshFilter>();
            float width = mesh != null && mesh.sharedMesh != null ? mesh.sharedMesh.bounds.size.x * Mathf.Abs(s.x) : 2.4f;
            panel.Build(Mathf.Max(1.5f, width));
            panel.Resolve();
            return panel;
        }

        private void Resolve()
        {
            _registry = FindObjectOfType<CameraStreamRegistry>();
            _show = FindObjectOfType<ShowControlClient>();
            _frame = FindObjectOfType<CourseFrame>();
            _registration = FindObjectOfType<CourseRegistrationController>();
            _discovery = FindObjectOfType<DiscoveryClient>();
            if (_registration != null)
            {
                _registration.RegistrationConfirmed += PositionConfirmed;
                _registration.RegistrationReviewed += PositionConfirmed;
            }
        }
        private void PositionConfirmed() => _confirmPending = true;
        public void SetRightController(bool connected, bool tracked) { _rightConnected = connected; _rightTracked = tracked; }
        public void SetResetProgress(float progress)
        {
            if (_resetProgress > 0f && _resetProgress < 1f && progress == 0f)
                _resetNoticeUntil = Time.realtimeSinceStartup + 2f;
            _resetProgress = progress;
        }

        /// <summary>
        /// 本体名。現地の静的割当で末尾 .31 がクエスト α、.32 がクエスト β。
        /// それ以外は IP をそのまま出し、取れなければ空。
        /// </summary>
        public static string DeviceLabel(string? ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            if (ip!.EndsWith(".31", StringComparison.Ordinal)) return "クエスト α";
            if (ip.EndsWith(".32", StringComparison.Ordinal)) return "クエスト β";
            return ip;
        }

        private void ResolveRun()
        {
            if (_run != null || Time.realtimeSinceStartup < _runRetryAt) return;
            // ShowRunDirector は ShowControlClient が後から作ることがある。無い構成で毎フレーム全走査しない。
            _run = FindObjectOfType<ShowRunDirector>();
            if (_run == null) _runRetryAt = Time.realtimeSinceStartup + 2f;
        }

        private VisitorPortal.StaffStatus GetStaffStatus()
        {
            Refresh(0f);
            var current = Current();
            ResolveRun();
            string stage = Logic.StageCode(_run != null && _run.Phase == ShowPhase.Finished);
            string reason = !current.contentReady ? "content" : !Logic.CamerasReady ? "camera"
                : !current.tabletFresh && Logic.Stage != StaffSetupStage.HandedOff ? "tablet"
                : !Logic.PositionConfirmed ? "position" : stage == StaffSetupLogic.StageSettings ? "settings"
                : stage == StaffSetupLogic.StageExplanation ? "explanation" : "";
            for (int i = 0; i < _statusCameras.Length; i++)
                _statusCameras[i] = new VisitorPortal.StaffCameraStatus
                {
                    id = CameraIds[i],
                    state = StaffSetupLogic.StateCode(Logic.CameraState(i)),
                    problem = StaffSetupLogic.ProblemCode(Logic.CameraProblem(i)),
                };
            return new VisitorPortal.StaffStatus
            {
                stage = stage, reason = reason,
                positionConfirmed = Logic.PositionConfirmed, resetProgress = Mathf.RoundToInt(_resetProgress * 100f),
                position = Logic.PositionCode,
                tablet = StaffSetupLogic.StateCode(Logic.TabletState),
                content = current.contentReady,
                cameras = _statusCameras,
            };
        }
        public void NewVisitor() { _confirmPending = false; Logic.NewVisitor(); Refresh(0f); }
        public void VisitorResetCompleted() { _confirmPending = false; Logic.VisitorResetCompleted(); Refresh(0f); }
        public void UseAutomation() { _automation = true; _anchor?.SetSetupFrozen(false); }

        public StaffSetupSnapshot Current()
        {
#if UNITY_EDITOR
            if (_previewSnapshot.HasValue) return _previewSnapshot.Value;
#endif
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < 3; i++)
            {
                var s = _registry?.Get(i);
                var src = _registry?.GetSource(i);
                var m = s?.Metadata;
                bool known = s != null && m != null && s.MetadataFromCurrentConnection
                    && now >= s.MetadataUpdatedRealtime && now - s.MetadataUpdatedRealtime <= IdentityFreshSec;
                known &= m != null && !string.IsNullOrEmpty(m.cameraId) && !string.IsNullOrEmpty(m.show)
                    && src != null && !string.IsNullOrEmpty(src.CameraId);
                SetupCameraProblem problem = s == null || !s.IsConnected ? SetupCameraProblem.NoStream
                    : !s.HasFreshDecodedFrame ? SetupCameraProblem.StaleFrame
                    : !known ? SetupCameraProblem.IdentityUnknown
                    : m!.cameraId != src!.CameraId ? SetupCameraProblem.WrongCamera
                    : m.show != (_discovery != null ? _discovery.ShowToken : "mawarimi") ? SetupCameraProblem.WrongShow
                    : SetupCameraProblem.None;
                _cameras[i] = new SetupCameraEvidence
                {
                    connected = s != null && s.IsConnected, suspended = s != null && s.IsSuspended,
                    freshDecode = s != null && s.HasFreshDecodedFrame, identityKnown = known,
                    identityMatches = known && src != null && !string.IsNullOrEmpty(src.CameraId)
                        && m!.cameraId == src.CameraId && m.show == (_discovery != null ? _discovery.ShowToken : "mawarimi"),
                    endpoint = s != null ? s.EndpointGeneration : 0,
                    connection = s != null ? s.ConnectionSerial : 0,
                    problem = problem,
                };
            }
            bool settled = !VisitorPrefs.HasUnapplied;
            // 実値を照合するのは今回まだ枠にある設定だけ。消費済みの前の体験者のACKは持ち越さない。
            if (VisitorPrefs.HasPending)
                settled &= VisitorPrefs.AppliedSeq == VisitorPrefs.PendingSeq
                    && ShowLanguage.Current == VisitorPrefs.PendingLang && HorrorRelief.Enabled == VisitorPrefs.PendingRelief;
            var portal = _show?.Portal;
            if (portal != null && _statusPortal != portal)
            {
                _statusProvider ??= GetStaffStatus;
                _statusPortal = portal;
                portal.StaffStatusProvider = _statusProvider;
            }
            settled &= portal == null || !portal.HasQueuedSettings;
            return new StaffSetupSnapshot
            {
                generation = Logic.Generation, cameras = _cameras,
                tabletFresh = portal != null && portal.IsListening && portal.CurrentVisitorTabletAgeSec <= TabletFreshSec,
                settingsSettled = settled, settingsRequested = VisitorPrefs.HasPending,
                visitorSettingsApplied = portal != null && portal.CurrentVisitorSettingsApplied,
                visitorBriefingCompleted = portal != null && portal.CurrentVisitorBriefingCompleted,
                contentReady = _show != null && _show.ContentVerified,
                hasPosition = _frame != null && _frame.HasRegistration,
                positionInvalid = _frame != null && _frame.NeedsReRegistration,
                registrationActive = _registration != null && _registration.IsActive,
            };
        }

        public void Refresh(float dt)
        {
            if (_automation) return;
            Logic.Observe(Current(), dt);
            if (Logic.Stage == StaffSetupStage.Welcome && _show?.Portal?.CurrentVisitorStaffConfirmed == true)
                Logic.HandOff(Current());
            _recoverySec = Logic.BlocksVisitor ? _recoverySec + Mathf.Clamp(dt, 0f, .25f) : 0f;
            if (_confirmPending && !(_registration != null && _registration.IsActive))
            {
                _confirmPending = false;
                Logic.ConfirmPosition(Current());
            }
            _anchor?.SetSetupFrozen(Visible || (_registration != null && _registration.IsActive));
        }

        /// <summary>
        /// 本体の B 短押し（予備の引き渡し）。体験者を迎える段だけで効き、一括検査を通す。
        /// 機器確認と位置合わせは並行するので、初回の段では何もしない。
        /// </summary>
        public bool Continue()
        {
            Refresh(0f);
            if (Logic.Stage != StaffSetupStage.Welcome) return false;
            bool done = Logic.HandOff(Current());
            if (done) _recoverySec = 0f;
            return done;
        }
        public bool TryBeginExperience() => _automation || Logic.TryBeginExperience(Current());
        public void RecallScreen() { if (Visible) _anchor?.RepositionForSetup(); }

        private void LateUpdate()
        {
            Refresh(0f);
            if (_visual == null) return;
            _visual.SetActive(Visible);
            if (!Visible) return;
            Render();
        }

        // 配置（幅 w、高さ h = 9/16 w。原点は中央）。上から 題字／呼び出し → 本文 1 行 → 5 行 → 次の一手 → 補助行。
        // 行の数は常に 5 で、次の一手の位置は状態で動かない。
        private void Build(float w)
        {
            float h = w * 9f / 16f;
            _visual = new GameObject("Visual");
            _visual.transform.SetParent(transform, false);
            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            back.name = "SetupBackground";
            back.transform.SetParent(_visual.transform, false);
            back.transform.localScale = new Vector3(w, h, 1f);
            var collider = back.GetComponent<Collider>();
            if (collider != null) { if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider); }
            var shader = Shader.Find(TitleScreen.VeilShaderName);
            if (shader != null)
            {
                _backMaterial = new Material(shader) { renderQueue = 4998 };
                _backMaterial.SetFloat("_Opacity", 1f); _backMaterial.SetFloat("_Grain", 0f);
                _backMaterial.SetColor("_CoreColor", new Color(0.025f, 0.031f, 0.036f, 1f));
                back.GetComponent<Renderer>().sharedMaterial = _backMaterial;
            }
            float left = -w * .46f;
            _header = LeftLabel("Header", left, w * .60f, h * .43f, w * .032f, h * .09f, TextAlignmentOptions.MidlineLeft);
            _device = Label("Device", w * .29f, h * .43f, w * .024f, w * .34f, h * .09f);
            _device.alignment = TextAlignmentOptions.Right;
            _device.margin = new Vector4(0f, 0f, _device.rectTransform.sizeDelta.x * .02f, 0f);
            _call = LeftLabel("CallStaff", left, w * .66f, h * .385f, w * .024f, h * .19f, TextAlignmentOptions.TopLeft);
            _body = LeftLabel("Body", left, w * .92f, h * .255f, w * .022f, h * .06f, TextAlignmentOptions.MidlineLeft);
            for (int i = 0; i < RowCount; i++)
            {
                float y = h * (.165f - i * .07f);
                _marks[i] = LeftLabel("Mark" + i, left, w * .03f, y, w * .020f, h * .06f, TextAlignmentOptions.MidlineLeft);
                _rows[i] = LeftLabel("Row" + i, -w * .42f, w * .14f, y, w * .020f, h * .06f, TextAlignmentOptions.MidlineLeft);
                _statuses[i] = LeftLabel("RowStatus" + i, -w * .27f, w * .28f, y, w * .020f, h * .06f, TextAlignmentOptions.MidlineLeft);
                _fixes[i] = LeftLabel("RowFix" + i, w * .02f, w * .44f, y, w * .020f, h * .06f, TextAlignmentOptions.MidlineLeft);
            }
            // 位置合わせ中は 5 行の代わりに、位置合わせの案内（操作 → 補助指標）を出す。
            _guideAction = LeftLabel("GuideAction", left, w * .92f, h * .23f, w * .026f, h * .20f, TextAlignmentOptions.TopLeft);
            _guideDetails = LeftLabel("GuideDetails", left, w * .92f, h * .02f, w * .019f, h * .20f, TextAlignmentOptions.TopLeft);
            _footer = LeftLabel("Next", left, w * .92f, h * -.245f, w * .023f, h * .14f, TextAlignmentOptions.TopLeft);
            _hint = LeftLabel("ScreenRecall", left, w * .92f, h * -.44f, w * .015f, h * .05f, TextAlignmentOptions.MidlineLeft);
        }
        private TMP_Text LeftLabel(string name, float leftX, float width, float y, float size, float height, TextAlignmentOptions alignment)
        {
            var t = Label(name, leftX + width * .5f, y, size, width, height);
            t.alignment = alignment;
            float inset = t.rectTransform.sizeDelta.x * .01f;
            t.margin = new Vector4(inset, 0f, inset, 0f);
            return t;
        }
        private TMP_Text Label(string name, float x, float y, float size, float w, float h)
        {
            var go = new GameObject(name); go.transform.SetParent(_visual!.transform, false);
            go.transform.localPosition = new Vector3(x, y, -.01f);
            var t = go.AddComponent<TextMeshPro>();
            const float meshFontSize = .07f;
            float scale = size / (meshFontSize * HmdTextStyle.MeshFontScale);
            go.transform.localScale = Vector3.one * scale;
            t.font = JapaneseHudFont.TryGet(); t.fontSize = meshFontSize;
            t.rectTransform.sizeDelta = new Vector2(w / scale, h / scale); t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Overflow;
            t.color = HmdTextStyle.Ink;
            var shader = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (shader != null) t.fontMaterial.shader = shader;
            t.fontMaterial.renderQueue = 5000;
            return t;
        }

        private void Render()
        {
            if (_header == null) return;
            var current = Current();
            bool registering = current.registrationActive;
            // 引き渡し後に開始条件が崩れて戻った画面は、被っている来場者が読む。最上段で呼び出しを頼む。
            bool recovery = !registering && Logic.Stage == StaffSetupStage.HandedOff;

            string ip = _show?.Portal?.LocalIp ?? "";
#if UNITY_EDITOR
            if (_previewDeviceIp != null) ip = _previewDeviceIp;
#endif
            if (!ReferenceEquals(ip, _deviceIp)) { _deviceIp = ip; _deviceLabel = DeviceLabel(ip); }
            _device!.text = _deviceLabel;
            _device.color = SecondaryInk;
            _header.text = HeaderText;
            _header.enabled = !recovery;
            _call!.text = CallStaffText;
            _call.color = HmdTextStyle.Alert;
            _call.enabled = recovery;

            _guideAction!.enabled = _guideDetails!.enabled = registering;
            _body!.enabled = !registering;
            for (int i = 0; i < RowCount; i++)
                _marks[i]!.enabled = _rows[i]!.enabled = _statuses[i]!.enabled = _fixes[i]!.enabled = !registering;

            if (registering) RenderRegistration(current);
            else RenderChecklist(current, recovery);

            string key = Logic.Stage + "/" + Logic.PositionCode + "/" + Logic.CamerasReady + "/" + current.tabletFresh + "/" + current.settingsSettled;
            if (key != _lastLog) { _lastLog = key; Debug.Log("[StaffSetup] " + key + " content=" + current.contentReady + " position=" + Logic.PositionConfirmed); }
        }

        private void RenderRegistration(in StaffSetupSnapshot current)
        {
            bool handReady = _rightConnected && _rightTracked;
            _guideAction!.text = !_rightConnected ? "右コントローラーを接続してください"
                : !_rightTracked ? "右コントローラーを前に出してください"
                : _registration!.GuidanceAction;
            _guideAction.color = !handReady || current.positionInvalid || _registration!.GuidanceIsAlert
                ? HmdTextStyle.Alert : HmdTextStyle.Ink;
            _guideDetails!.text = handReady ? _registration!.GuidanceDetails : "位置合わせを続けるには右手の追跡が必要です";
            _guideDetails.color = handReady && _registration!.GuidanceIsAlert ? HmdTextStyle.Alert : SecondaryInk;
            _footer!.text = handReady ? _registration!.GuidanceControls : "接続と追跡を確認してください";
            _footer.color = HmdTextStyle.Ink;
            _footer.enabled = true;
            _hint!.text = CancelHint;
            _hint.color = SecondaryInk;
        }

        private void RenderChecklist(in StaffSetupSnapshot current, bool recovery)
        {
            // 位置合わせ
            string position = Logic.PositionCode;
            if (position == StaffSetupLogic.PositionConfirmedCode) SetRow(0, MarkReady, ReadyInk, PositionLabel, PositionDone, "");
            else if (position == StaffSetupLogic.PositionRecenterCode) SetRow(0, MarkTrouble, HmdTextStyle.Alert, PositionLabel, PositionRedo, PositionFix);
            else SetRow(0, MarkChecking, HmdTextStyle.Ink, PositionLabel, PositionNeeded, PositionFix);
            // カメラ A・B・C
            for (int i = 0; i < 3; i++)
            {
                SetupDeviceState state = Logic.CameraState(i);
                if (state == SetupDeviceState.Ready) SetRow(1 + i, MarkReady, ReadyInk, CameraLabels[i], CameraOk, "");
                else if (state == SetupDeviceState.Trouble)
                    SetRow(1 + i, MarkTrouble, HmdTextStyle.Alert, CameraLabels[i], CameraTroubleText(Logic.CameraProblem(i)),
                        CameraFix(i, Logic.CameraProblem(i)));
                else SetRow(1 + i, MarkChecking, HmdTextStyle.Ink, CameraLabels[i], Checking, "");
            }
            // タブレット
            SetupDeviceState tablet = Logic.TabletState;
            if (tablet == SetupDeviceState.Ready) SetRow(4, MarkReady, ReadyInk, TabletLabel, TabletOk, "");
            else if (tablet == SetupDeviceState.Trouble) SetRow(4, MarkTrouble, HmdTextStyle.Alert, TabletLabel, TabletTrouble, TabletFix);
            else SetRow(4, MarkChecking, HmdTextStyle.Ink, TabletLabel, Checking, "");

            // 本文 1 行：呼び出しの原因 → リセットの手応え → 素材 → 体験者を迎える段
            string body = "";
            bool alert = false;
            if (recovery) { body = RecoveryCause(current); alert = true; }
            else if (Logic.Stage == StaffSetupStage.Welcome && Logic.VisitorStage == VisitorPreparationStage.ResetRequired
                     && _resetProgress > 0f && _resetProgress < 1f)
                body = "リセット " + Mathf.RoundToInt(_resetProgress * 100f) + "%（右 A を離すと中止）";
            else if (Logic.VisitorStage != VisitorPreparationStage.ResetRequired && _resetProgress >= 1f) body = ResetDone;
            else if (Time.realtimeSinceStartup < _resetNoticeUntil && Logic.VisitorStage == VisitorPreparationStage.ResetRequired)
                body = ResetCancelled;
            else if (!current.contentReady) { body = ContentMissing; alert = true; }
            else if (Logic.Stage == StaffSetupStage.Welcome) body = WelcomeText;
            _body!.text = body;
            _body.color = alert ? HmdTextStyle.Alert : HmdTextStyle.Ink;

            // 次の一手。呼び出し中は被っている来場者に外させる指示を出さない（位置は空けたまま）。
            bool complete = Logic.ChecklistComplete;
            _footer!.text = recovery ? "" : complete ? ReadyText : NotReadyText;
            _footer.color = complete ? HmdTextStyle.Ink : SecondaryInk;
            _footer.enabled = !recovery;
            _hint!.text = RecallHint;
            _hint.color = SecondaryInk;
        }

        private void SetRow(int i, string mark, Color markColor, string label, string status, string fix)
        {
            _marks[i]!.text = mark; _marks[i]!.color = markColor;
            _rows[i]!.text = label; _rows[i]!.color = HmdTextStyle.Ink;
            _statuses[i]!.text = status; _statuses[i]!.color = markColor;
            _fixes[i]!.text = fix; _fixes[i]!.color = SecondaryInk;
        }

        private static string CameraTroubleText(SetupCameraProblem problem)
        {
            switch (problem)
            {
                case SetupCameraProblem.StaleFrame: return CameraStale;
                case SetupCameraProblem.IdentityUnknown: return CameraIdentity;
                case SetupCameraProblem.WrongCamera: return CameraWrong;
                case SetupCameraProblem.WrongShow: return CameraWrongShow;
                default: return CameraNoStream;
            }
        }

        private static string CameraFix(int camera, SetupCameraProblem problem)
            => problem == SetupCameraProblem.WrongCamera ? CheckSlotFix
                : problem == SetupCameraProblem.WrongShow ? CheckShowFix
                : OpenStreamerFix[camera];

        /// <summary>引き渡し後に開始が止まった原因（スタッフ向け 1 行）。</summary>
        private string RecoveryCause(in StaffSetupSnapshot current)
        {
            if (!current.contentReady) return ContentMissing;
            for (int i = 0; i < 3; i++)
                if (Logic.CameraState(i) != SetupDeviceState.Ready)
                    return CameraLabels[i] + "：" + (Logic.CameraState(i) == SetupDeviceState.Trouble
                        ? CameraTroubleText(Logic.CameraProblem(i)) : Checking);
            if (!current.settingsSettled) return "タブレットの設定を反映しています";
            return RecoveryFallback;
        }

        private void OnDestroy()
        {
            if (_statusPortal != null && _statusPortal.StaffStatusProvider == _statusProvider)
                _statusPortal.StaffStatusProvider = null;
            if (Instance == this) Instance = null;
            _anchor?.SetSetupFrozen(false);
            if (_registration != null)
            { _registration.RegistrationConfirmed -= PositionConfirmed; _registration.RegistrationReviewed -= PositionConfirmed; }
            if (_backMaterial != null) { if (Application.isPlaying) Destroy(_backMaterial); else DestroyImmediate(_backMaterial); }
        }
    }
}
