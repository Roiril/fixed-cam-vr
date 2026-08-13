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
    ///   2. ステータス表示中（右 B トグル）→ 相 / lap / ゾーン / 次の演出 / カメラ / 異常 1 件と直し方
    ///   3. それ以外 → 非表示
    ///
    /// <b>この 2 つは「スタッフが被っている」ことの印でもある</b>（<see cref="StaffViewing"/>）。
    /// HMD 内の他の文字面（導入の合図・黒の上の 1 行・手元の操作早見表）はここを見て出入りする。
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

        [Tooltip("この範囲の頭の動きではパネル不動（微小 jitter 吸収）。頭の手前この角度でパネルは止まる。")]
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

        // ⚠ 旧 `recenterAutoShowSec`（要再登録が立った瞬間、非表示中でも 5 秒だけ自動表示）は
        //    2026-08-07 に廃止した。**体験者が被っている最中に業務連絡が視界へ勝手に出る**唯一の
        //    経路で、しかも読んでも体験者には何もできない（機器の言葉がホラー体験の中に出る）。
        //    異常はスタッフが右 B で開けば最優先の 1 件として必ず出る（PickAlert + RecoveryGuidance）。
        //    卓の heartbeat にも出ているので、気づく経路が消えたわけではない。

        private readonly StringBuilder _sb = new(256);
        private readonly YawFollowLogic _yawFollow = new();
        private bool _yawSeeded;

        private bool _visible;          // 右 B トグルによる手動表示
        private float _hideAt;          // autoHideSec の失効時刻（_visible=true のとき有効）

        private float _accum;
        private string _lastGuidance = "";

        // コントローラ操作モードのラベル（NORMAL/REG）。OvrControllerBridge が遷移時に push する。
        private string _modeLabel = "";

        /// <summary>ステータスの表示・非表示を外部から切り替える（右 B / Editor H）。</summary>
        public void SetVisible(bool v)
        {
            _visible = v;
            if (v && autoHideSec > 0f) _hideAt = Time.unscaledTime + autoHideSec;
        }

        /// <summary>現在ステータスを手動表示中か（トグルの真実源）。</summary>
        public bool IsVisible => _visible;

        /// <summary>
        /// <b>HMD の中に文字を出してよいか ＝ 被っているのがスタッフか。</b>
        /// 導入の合図（<c>IntroPrompt</c>）・黒の上の 1 行（<c>ShowEndingFader</c>）・
        /// 手元の操作早見表（<c>ControllerGuidePanel</c>）が全部ここを見る。
        ///
        /// 印は 2 つだけ。どちらも<b>コントローラを持っている人にしか起こせない</b>ので、
        /// 体験者が被っている間は構造的に立たない（体験者はコントローラを持たない運用）。
        ///   - 右 B のステータス表示（スタッフが自分で開いた）
        ///   - 位置合わせ作業中（登録は必ずスタッフの仕事）
        ///
        /// 2026-08-07 にこの門を作った。それまでは体験者にも文字が出ていて、機器の言葉が
        /// ホラー体験の入口に混ざっていた（ユーザー指摘「世界観を壊すので消して」）。
        /// スタッフが確認したいときは従来どおり全部読める ＝ 現地のリハ・切り分けの手段は減らない。
        /// </summary>
        public bool StaffViewing => _visible || RegistrationActive;

        /// <summary>
        /// 位置合わせ作業中か（この面が登録ガイダンスを最優先で強制表示している状態）。
        ///
        /// ⚠ **他の文字面はこれが true の間 自分を出してはいけない。** 登録ガイダンスはこの面が
        /// <see cref="distance"/> = 1.6m に出しており、<c>IntroPrompt</c>(1.5m) と
        /// <c>ShowEndingFader</c>(0.3m) はどちらもその手前に重なる。譲らないと
        /// **作業中のスタッフに登録の文字が一切見えない**。
        ///
        /// 2026-08-07 実害: トリガー長押しで登録へ入っても導入の「そのまま前へ進んでください」
        /// しか見えず、モードが変わっていないように見えた（同日に門を <see cref="StaffViewing"/> へ
        /// 統一した際、登録中も他面が開くようになったのが原因）。
        /// </summary>
        public bool RegistrationActive => registration != null && registration.IsActive;

        /// <summary>コントローラ操作モードのラベル（NORMAL/REG）を保持する。ステータス行の行頭
        /// プレフィックス表示は廃止したが（REG 中は登録ガイダンス強制表示で自明）、API と保持値は残す。</summary>
        public void SetModeLabel(string label) => _modeLabel = label ?? "";

        /// <summary>直近に push されたモードラベル（NORMAL/REG）。</summary>
        public string ModeLabel => _modeLabel;

        private void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;

            // 世界空間 Canvas には描く相手を渡しておく（UGUI の作法）。
            // ⚠ **これは `Screen position out of view frustum` の対策ではない。**
            //    2026-08-14 にそのつもりで入れたが、実機の警告は 1 行も減らなかった
            //    （`canon/OPEN.md` の未解決項目）。**効かなかった対策を「効いた」と書かない。**
            var canvas = GetComponent<Canvas>();
            if (canvas != null && canvas.renderMode == RenderMode.WorldSpace && canvas.worldCamera == null)
            {
                canvas.worldCamera = head != null ? head.GetComponent<Camera>() : null;
                if (canvas.worldCamera == null) canvas.worldCamera = Camera.main;
            }

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

            // HUD は<b>頭の正面まで来ない方が正しい</b>（視界の真ん中に居座ると本編の映像を隠す）ので、
            // 動き出す閾値と止まる位置を従来どおり同じ値にする。スクリーン（ScreenAnchor）とは意図が逆。
            float yaw = _yawFollow.Step(headYaw, Time.deltaTime, yawDeadzoneDeg, yawDeadzoneDeg,
                                        smoothTime, maxYawSpeedDegPerSec,
                                        catchUpThresholdDeg, catchUpBoost);
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

            // 2. ステータス表示中（右 B の手動トグルだけ）。自動で開く経路は持たない
            //    ＝ 体験者が被っている最中に文字が湧く道を 1 本も残さない。
            if (!_visible)
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

        // 全編を日本語・直感表記へ（記号を廃し、意味を平文で）。既存参照の append のみで新規 GC を出さない。
        // 体験の骨格。ShowControlClient が実行時に自動生成することがあるので遅延解決する。
        private ShowRunDirector? _run;

        private ShowRunDirector? ResolveRun()
        {
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            return _run;
        }

        // m:ss（GC ゼロ。sb.Append(int) だけで組む）。
        private static void AppendClock(StringBuilder sb, float sec)
        {
            if (sec < 0f) sec = 0f;
            int total = Mathf.FloorToInt(sec);
            sb.Append(total / 60);
            sb.Append(':');
            int s = total % 60;
            if (s < 10) sb.Append('0');
            sb.Append(s);
        }

        // モードラベル（[NORMAL]/[REG]）は行頭に出さない（REG 中は登録ガイダンスが強制表示されるため不要。
        // SetModeLabel API と heartbeat 連携は別途維持）。
        private void BuildStatus(StringBuilder sb)
        {
            sb.Clear();

            // 行1: 体験の相と周回。導入中・終了は周回より先にそれを言う（スタッフが最初に知りたいのはここ）。
            ShowRunDirector? run = ResolveRun();
            if (run != null && run.Phase == ShowPhase.Intro)
            {
                sb.Append("導入中 ");
                AppendClock(sb, run.IntroElapsedSec);
            }
            else if (run != null && run.Phase == ShowPhase.Finished)
            {
                sb.Append("体験おわり（次の体験者へ） ");
                AppendClock(sb, run.RunElapsedSec);
            }
            else
            {
                bool hasLap = lapCounter != null && lapCounter.CurrentLap >= 1;
                // 走り切る周数を超えた周＝「帰りのスタート区間」（体験はここで終わる）。
                // 「4周目/3周」と出ると壊れて見えるので、そこだけ言い方を変える。
                if (hasLap && run != null && lapCounter!.CurrentLap > run.TotalLaps)
                {
                    // 「もどり」だけでは何のことか分からない（開発語）。体験の位置を言う。
                    sb.Append("最後の区間 ・ 経過 ");
                    AppendClock(sb, run.RunElapsedSec);
                }
                else
                {
                    if (hasLap) { sb.Append(lapCounter!.CurrentLap); sb.Append("周目"); }
                    else sb.Append("周回 -");
                    if (run != null)
                    {
                        sb.Append('/');
                        sb.Append(run.TotalLaps);
                        sb.Append("周 ・ 経過 ");
                        AppendClock(sb, run.RunElapsedSec);
                    }
                }
            }
            sb.Append(" ・ いまの場所: ");
            var zone = tracker != null ? tracker.CurrentZone : null;
            sb.Append(zone != null ? zone.Label : "-");
            sb.Append(" ・ 表示中: カメラ");
            if (registry != null && registry.Count > 0) sb.Append(registry.ActiveIndex + 1);
            else sb.Append('-');

            // 行2（次の cue がある時のみ）: 次の演出: {lap}周目 カメラ{c+1} 「{cueId}」
            if (cueScheduler != null && lapCounter != null && lapCounter.Order.Length > 0
                && cueScheduler.TryGetNextCue(lapCounter.CurrentLap, lapCounter.Position, lapCounter.Order, out var next))
            {
                // ⚠ cue の id は出さない。`cue_B_2` を読んでもスタッフには何もできない（開発語で、
                // 直し方にも繋がらない）。知りたいのは「次に何かが起きるのはどこか」だけ。
                sb.Append("\n次の演出: ");
                sb.Append(next.lap);
                sb.Append("周目 カメラ");
                sb.Append(next.camera + 1);
            }

            // 行3: カメラ1○ カメラ2○ カメラ3×（○=映像が届いている / ×=届いていない・半角スペース 2 個区切り）。
            sb.Append('\n');
            if (registry != null && registry.Count > 0)
            {
                int n = registry.Count;
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append("  ");
                    sb.Append("カメラ");
                    sb.Append(i + 1);
                    var s = registry.Get(i);
                    sb.Append(s != null && s.IsConnected ? '○' : '×');
                }
            }
            else sb.Append('-');

            // 行3.5: 映像の遅れ（企画書「視覚遅延は 100ms 程度以内を目標として管理する」）。
            // **絶対の end-to-end ではない** — Unity が観測できる分（到着の揺らぎ + 展開 + 提示）だけ。
            // 配信端末が熱で品質を落としている時はそれも言う（原因が経路だと誤解させない）。
            // ⚠ **常時は出さない。** 普段この数値を読んでもスタッフには何もできない
            // （数値を残す基準は「その数字で判断が変わるか」）。企画書 2.3 の目標 100ms を超えたときだけ
            // 出す — そのときは「Wi-Fi を替える」という行動がある。揺らぎの内訳は落とした（判断が
            // 変わらない）。発熱は下の「異常 1 件」へ移した（手順とセットで出すため）。
            const int LatencyWarnMs = 100;
            var activeStream = registry != null ? registry.GetActive() : null;
            if (activeStream != null)
            {
                int ms = Mathf.RoundToInt(activeStream.Latency.ObservedMs);
                if (ms > LatencyWarnMs)
                {
                    sb.Append("\n映像が ");
                    sb.Append(ms);
                    sb.Append("ms 遅れています（Wi-Fi が 2.4GHz なら 5GHz へ）");
                }
            }

            // 行4-5: 異常は**最優先の 1 件だけ**を「何が起きたか」＋「何をすれば直るか」の 2 行で出す。
            //
            // 旧実装は異常を状態の報告だけで並べていて（`⚠映像が届いていません（砂嵐表示中）`
            // `⚠ヘッドセットの位置を見失っています` 等）、**8 種のうち復帰動作を書いているものが 0 だった**。
            // 読んだスタッフは何をすればいいか分からず現場で黙って立つ。手順は RecoveryGuidance が
            // 1 箇所で持ち、異常を足すには手順を書く以外の道が無い（テストが機械で固定する）。
            //
            // 複数を並べないのは、5 行のステータスに 3 行の手順が積むと HMD では読み切れないため。
            ShowAlert alert = PickAlert(out int alertCam);
            if (alert != ShowAlert.None)
            {
                sb.Append('\n');
                sb.Append(RecoveryGuidance.What(alert, alertCam));
                sb.Append('\n');
                sb.Append(RecoveryGuidance.How(alert));
                return;
            }

            // ここから下は「異常が無いとき」だけ。正常時の確認に使う情報。
            // `カメラ切替中…` は削除した（dip は 0.17 秒で、読む前に消える）。
            if (switchDirector != null && switchDirector.SwitchSuppressed)
                sb.Append("\n演出中はカメラが切り替わりません");

            if (courseFrame != null && courseFrame.HasRegistration)
            {
                sb.Append("\n位置合わせOK");
                if (courseFrame.MaxResidualM > 0f)
                {
                    int cm = Mathf.RoundToInt(courseFrame.MaxResidualM * 100f);
                    if (cm > 0) // 丸めて 0cm になる微小のずれは「位置合わせOK」のみに畳む
                    {
                        sb.Append("（ずれ ");
                        sb.Append(cm);
                        sb.Append("cm）");
                    }
                }
            }
        }

        /// <summary>
        /// いま出すべき異常を 1 つ選ぶ（<see cref="ShowAlert"/> の宣言順が優先度）。
        /// 上にあるほど「体験が止まっている / 先に直さないと先へ進めない」。
        ///
        /// 導入の中止は <see cref="ShowRunDirector.BlackoutMessage"/> の非空で判定する
        /// （公開プロパティだけで済み、IntroDirector への新しい参照を作らない。終了時は空）。
        /// </summary>
        private ShowAlert PickAlert(out int cameraNumber)
        {
            cameraNumber = registry != null && registry.Count > 0 ? registry.ActiveIndex + 1 : 0;

            ShowRunDirector? run = ResolveRun();
            if (run != null && run.Phase == ShowPhase.Intro && !string.IsNullOrEmpty(run.BlackoutMessage))
                return ShowAlert.IntroAborted;

            if (courseFrame != null && courseFrame.NeedsReRegistration) return ShowAlert.NeedsReRegistration;
            if (courseFrame != null && !courseFrame.HasRegistration) return ShowAlert.NotRegistered;
            if (signalFx != null && signalFx.SignalLost) return ShowAlert.NoVideo;
            if (signalFx != null && signalFx.TrackingFrozen) return ShowAlert.TrackingLost;

            var active = registry != null ? registry.GetActive() : null;
            if ((active?.Health?.throttleStage ?? 0) > 0) return ShowAlert.Throttled;

            // 床の高さを測っていない位置合わせ＝線や人形が沈んで見える原因。体験は成立するので最後。
            if (courseFrame != null && courseFrame.HasRegistration && !courseFrame.HasFloorY)
                return ShowAlert.FloorNotMeasured;

            return ShowAlert.None;
        }
    }
}
