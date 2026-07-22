#nullable enable
using System.Text;
using FixedCamVr.Streaming;
using FixedCamVr.Tracking;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// HMD 内テキストの**単一サーフェス**（計画 2026-07-20_staff-input-hud-redesign.md）。
    /// 旧 RuntimeDebugHud（診断詳細・剛体 head-lock）+ StaffPanel（操作チートシート）+
    /// CourseRegGuidance（登録ガイダンス）の 3 枚を 1 枚に統合し、重なりを構造で根絶する。
    ///
    /// 内容モード（優先度順・常に最大 1 枚）:
    ///   1. 登録中（<see cref="registration"/>.IsActive）→ **登録ガイダンスを強制表示**（最優先・オートハイド対象外）
    ///   2. ステータス表示中（右 B トグル or 要再登録オートショウ）→ lap / ゾーン / 次の cue / 信号 / 要再登録
    ///   3. それ以外 → 非表示
    ///
    /// 配置は剛体 head-lock を廃し、<see cref="YawFollowLogic"/> の deadzone + SmoothDamp 緩追従
    /// （ScreenAnchor と同型）。頭を回すと遅れてついてくるが、視線だけ動かせば静止して読める。
    /// 距離・角度・サイズ・追従・オートハイドはすべて SerializeField（現場調整前提）。
    /// 診断詳細（FPS / HMD 座標 / DISC）は載せない（HudLogDumper のログ + Web 卓 heartbeat が担う）。
    /// </summary>
    public sealed class StatusHud : MonoBehaviour
    {
        [Header("Output")]
        [Tooltip("出力先 TMP_Text (1 個)。")]
        [SerializeField] private TMP_Text? text;

        [Header("Status sources")]
        [Tooltip("アクティブカメラ / 接続状態の取得元。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("現在ゾーン取得元。")]
        [SerializeField] private PlayerZoneTracker? tracker;

        [Tooltip("周回数（lap）取得元。次の cue 照会にも使う（Position / Order）。")]
        [SerializeField] private LapCounter? lapCounter;

        [Tooltip("次に発火する予定の cue 照会先。null なら『次』行を出さない。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("信号ロスト / 追従凍結の状態取得元。null なら信号状態を出さない。")]
        [SerializeField] private SignalLostFx? signalFx;

        [Tooltip("切替抑止 / dip 状態の取得元（任意）。null なら SW 表示を出さない。")]
        [SerializeField] private CameraSwitchDirector? switchDirector;

        [Tooltip("『要再登録』バッジと登録ガイダンスの取得元。")]
        [SerializeField] private CourseFrame? courseFrame;

        [Tooltip("登録ガイダンス文字列の供給元（登録中は強制表示）。")]
        [SerializeField] private CourseRegistrationController? registration;

        [Header("Follow placement (緩追従・現場調整可)")]
        [Tooltip("追従先（CenterEyeAnchor）。null なら Camera.main。")]
        [SerializeField] private Transform? head;

        [Tooltip("頭からの距離 (m)。")]
        [SerializeField] private float distance = 1.6f;

        [Tooltip("スクリーン中心の高さオフセット (m)。負で下げる（視線中心から下）。" +
                 "-distance·tan15° ≈ -0.43m で約 15 度下。")]
        [SerializeField] private float heightOffset = -0.43f;

        [Tooltip("パネルの前傾角 (deg)。負で上向きに傾け、下方配置でも視線に正対させる。")]
        [SerializeField] private float pitchDeg = -15f;

        [Tooltip("この範囲の頭の動きではパネル不動（微小 jitter 吸収）。")]
        [SerializeField] private float yawDeadzoneDeg = 10f;

        [Tooltip("臨界減衰の時定数 (秒)。大きいほどゆっくり追う。")]
        [SerializeField] private float smoothTime = 0.30f;

        [Tooltip("full-field スライドの角速度上限 (度/秒)。")]
        [SerializeField] private float maxYawSpeedDegPerSec = 110f;

        [Tooltip("この角度以上置いていかれたら速度上限を catchUpBoost 倍にして追いつく。")]
        [SerializeField] private float catchUpThresholdDeg = 45f;

        [Tooltip("逆走ガード時の速度上限倍率。")]
        [SerializeField] private float catchUpBoost = 2f;

        [Tooltip("この秒数を超える unscaledDeltaTime は pause / HMD 着脱明けとみなし、現在ヨーから合流し直す。")]
        [SerializeField] private float resumeGapSec = 0.5f;

        [Header("Behavior")]
        [Tooltip("ステータス更新間隔 (秒)。90Hz を守るため毎フレームの文字列構築を間引く。")]
        [SerializeField] private float updateInterval = 0.25f;

        [Tooltip("起動時にステータスを表示するか。本番は false（視界保護）。右 B でトグル。")]
        [SerializeField] private bool startVisible = false;

        [Tooltip("ステータス表示をこの秒数で自動的に隠す（0 = 無効）。登録ガイダンスには適用しない。")]
        [SerializeField] private float autoHideSec = 0f;

        [Tooltip("『要再登録』が発生した時、非表示中でもステータスを自動表示する秒数（0 = しない）。")]
        [SerializeField] private float recenterAutoShowSec = 5f;

        private readonly StringBuilder _sb = new(256);
        private readonly YawFollowLogic _yawFollow = new();
        private bool _yawSeeded;

        private bool _visible;          // 右 B トグルによる手動表示
        private float _hideAt;          // autoHideSec の失効時刻（_visible=true のとき有効）
        private float _autoShowUntil;   // 要再登録オートショウの失効時刻
        private bool _prevNeedsReReg;

        private float _accum;
        private string _lastGuidance = "";

        // コントローラ操作モードのラベル（NORMAL/REG）。OvrControllerBridge が遷移時に push する。
        private string _modeLabel = "";

        // 右コントローラ接続状態。OvrControllerBridge が毎フレーム push する（Diagnostics は OVRInput 非依存
        // のため直読みできない）。既定 true（push 前に「未接続」を誤表示しない）。
        private bool _controllerConnected = true;

        /// <summary>ステータスの表示・非表示を外部から切り替える（右 B / Editor H）。</summary>
        public void SetVisible(bool v)
        {
            _visible = v;
            if (v && autoHideSec > 0f) _hideAt = Time.unscaledTime + autoHideSec;
        }

        /// <summary>現在ステータスを手動表示中か（トグルの真実源）。</summary>
        public bool IsVisible => _visible;

        /// <summary>コントローラ操作モードのラベル（NORMAL/REG）をステータス行へ反映する。</summary>
        public void SetModeLabel(string label) => _modeLabel = label ?? "";

        /// <summary>右コントローラの接続状態をステータス行へ反映する（OvrControllerBridge が push）。</summary>
        public void SetControllerConnected(bool connected) => _controllerConnected = connected;

        private void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;

            // 既定 LiberationSans SDF は日本語グリフを持たない（ガイダンス・ステータス行が豆腐化する）。
            // OS フォントから日本語対応の動的 TMP フォントを生成して差し替える（失敗時は既定のまま）。
            if (text != null)
            {
                var jp = JapaneseHudFont.TryGet();
                if (jp != null) text.font = jp;
            }
        }

        private void OnEnable()
        {
            _visible = startVisible;
            if (_visible && autoHideSec > 0f) _hideAt = Time.unscaledTime + autoHideSec;
        }

        private void Update()
        {
            // 要再登録の立ち上がりでオートショウ。
            bool needsReReg = courseFrame != null && courseFrame.NeedsReRegistration;
            if (needsReReg && !_prevNeedsReReg && recenterAutoShowSec > 0f)
                _autoShowUntil = Time.unscaledTime + recenterAutoShowSec;
            _prevNeedsReReg = needsReReg;

            // オートハイド（手動表示のみ・登録ガイダンスは対象外）。
            if (_visible && autoHideSec > 0f && Time.unscaledTime >= _hideAt) _visible = false;

            RenderContent();
        }

        private void LateUpdate()
        {
            if (head == null) return;

            float headYaw = head.eulerAngles.y;
            bool resumeGap = Time.unscaledDeltaTime > resumeGapSec;
            if (!_yawSeeded || resumeGap)
            {
                _yawFollow.Reseat(headYaw);
                _yawSeeded = true;
                ApplyPose(_yawFollow.CurrentYaw);
                return;
            }

            float yaw = _yawFollow.Step(headYaw, Time.deltaTime, yawDeadzoneDeg, smoothTime,
                                        maxYawSpeedDegPerSec, catchUpThresholdDeg, catchUpBoost);
            ApplyPose(yaw);
        }

        // 指定ヨーでパネルを頭の前 distance・高さ head.y+heightOffset に配置し、pitchDeg で前傾させる。
        private void ApplyPose(float yaw)
        {
            if (head == null) return;
            Quaternion faceYaw = Quaternion.Euler(0f, yaw, 0f);
            Vector3 fwd = faceYaw * Vector3.forward; // 水平前方（fwd.y=0）
            Vector3 pos = head.position + fwd * distance;
            pos.y = head.position.y + heightOffset;
            transform.position = pos;
            transform.rotation = Quaternion.Euler(pitchDeg, yaw, 0f);
        }

        // 内容モードを解決して text へ反映する。
        private void RenderContent()
        {
            if (text == null) return;

            // 1. 登録中はガイダンスを強制表示（最優先）。
            if (registration != null && registration.IsActive)
            {
                string g = registration.GuidanceText;
                if (!ReferenceEquals(g, _lastGuidance) && g != _lastGuidance)
                {
                    text.SetText(g);
                    _lastGuidance = g;
                }
                text.color = registration.GuidanceColor;
                text.enabled = !string.IsNullOrEmpty(g);
                return;
            }
            _lastGuidance = "";

            // 2. ステータス表示中（手動トグル or 要再登録オートショウ）。
            bool show = _visible || Time.unscaledTime < _autoShowUntil;
            if (!show)
            {
                text.enabled = false;
                return;
            }

            _accum += Time.unscaledDeltaTime;
            if (_accum < updateInterval && text.enabled) return; // 直前の文字列を維持
            _accum = 0f;

            BuildStatus(_sb);
            text.SetText(_sb);
            text.color = new Color(0.9f, 1f, 0.95f, 1f);
            text.enabled = true;
        }

        // lap / ゾーン / 次の cue / 信号・要再登録 の 3〜4 行。既存参照の append のみで新規 GC を出さない。
        private void BuildStatus(StringBuilder sb)
        {
            sb.Clear();

            // 行1: [MODE] lap N | zone <label> (cam<idx>)
            if (_modeLabel.Length > 0) { sb.Append('['); sb.Append(_modeLabel); sb.Append("] "); }
            sb.Append("lap ");
            sb.Append(lapCounter != null ? lapCounter.CurrentLap : -1);
            sb.Append(" | zone ");
            var zone = tracker != null ? tracker.CurrentZone : null;
            sb.Append(zone != null ? zone.Label : "-");
            if (registry != null && registry.Count > 0)
            {
                sb.Append(" (cam");
                sb.Append(registry.ActiveIndex + 1);
                sb.Append(')');
            }

            // 行2: 次: lap<L> cam<c> → <cueId>
            if (cueScheduler != null && lapCounter != null && lapCounter.Order.Length > 0
                && cueScheduler.TryGetNextCue(lapCounter.CurrentLap, lapCounter.Position, lapCounter.Order, out var next))
            {
                sb.Append("\n次: lap");
                sb.Append(next.lap);
                sb.Append(" cam");
                sb.Append(next.camera + 1);
                sb.Append(" → ");
                sb.Append(string.IsNullOrEmpty(next.cueId) ? "-" : next.cueId);
            }

            // 行3: 信号 ●●○ [ロスト/凍結] [SW ...] [⚠要再登録]
            sb.Append("\n信号 ");
            if (registry != null && registry.Count > 0)
            {
                int n = registry.Count;
                for (int i = 0; i < n; i++)
                {
                    var s = registry.Get(i);
                    sb.Append(s != null && s.IsConnected ? '●' : '○');
                }
            }
            else sb.Append('-');

            if (signalFx != null)
            {
                if (signalFx.SignalLost) sb.Append(" ロスト");
                else if (signalFx.TrackingFrozen) sb.Append(" 凍結");
            }
            if (switchDirector != null)
            {
                if (switchDirector.Dipping) sb.Append(" SW:dip");
                else if (switchDirector.SwitchSuppressed) sb.Append(" SW:hold");
            }
            // 登録状態バッジ（要再登録 > 未登録 > 登録済(残差) の優先順位で 1 つだけ）。
            if (courseFrame != null)
            {
                if (courseFrame.NeedsReRegistration) sb.Append("  ⚠要再登録");
                else if (!courseFrame.HasRegistration) sb.Append("  ⚠未登録");
                else
                {
                    sb.Append("  登録済");
                    if (courseFrame.MaxResidualM > 0f)
                    {
                        sb.Append("(残差");
                        sb.Append(courseFrame.MaxResidualM.ToString("0.00"));
                        sb.Append("m)");
                    }
                }
            }

            // 行4: Rコン接続 / 未接続（未接続は目立たせる。押しても振動しない時の切り分け＝streamer 層でなく
            // コントローラ電池切れ / スリープ / ペアリング落ちを疑うための表示）。
            sb.Append('\n');
            sb.Append(_controllerConnected ? "Rコン●" : "⚠Rコン未接続");
        }
    }
}
