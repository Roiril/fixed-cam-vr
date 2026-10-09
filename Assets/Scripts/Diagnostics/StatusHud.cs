#nullable enable
using System.Text;
using FixedCamVr.Input;
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
    ///   1. 登録中（<see cref="registration"/>.IsActive）→ **登録ガイダンスを強制表示**（最優先）
    ///   2. ステータス表示中（<b>右 B を押しているあいだ</b>、または Editor / 卓からのピン留め）
    ///      → 相 / lap / ゾーン / 次の演出 / カメラ / 報告の件数 / 異常 1 件と直し方
    ///   3. それ以外 → 非表示
    ///
    /// ⚠⚠ <b>2026-09-14 にトグル（ラッチ）をやめた。</b> 右は本編中もスタッフの手にあるので、
    /// ラッチだと<b>開いたまま体験者へ渡る</b>。自然な手順（A 2 秒でリセット → B で確認 → 被せる）は
    /// 消す縁（ラン開始）が B より先に来るため、導入から終幕までずっと出たままになる。
    /// いまは押下中の表示（<see cref="StatusViewLogic"/>）で、誤押しの害は押していた長さに有界。
    /// 真実源は 1 つ: <see cref="IsVisible"/> ＝ ピン留め or 押下ビューが見えている。
    ///
    /// <b>この 2 つは「スタッフが被っている」ことの印でもある</b>（<see cref="StaffViewing"/>）。
    /// HMD 内の他の文字面（黒の上の 1 行・手元の操作早見表）はここを見て出入りする。
    ///
    /// ⚠ <b>黒の上に描く。</b> 題字の覆い（queue 4950・ZTest Always）と導入の覆い
    /// （queue 4900・乗算ブレンド）に先に描かれると必ず潰されるので、面の TMP は
    /// <c>TextMeshPro/Distance Field Overlay</c>（ZTest Always）＋ <c>renderQueue 5000</c> で描く
    /// （<see cref="OutroReport"/> / <see cref="TitleNotice"/> と同じ形）。譲らせないので、
    /// B を押しているあいだも体験者の X / Y 短押しはそのまま通る。
    ///
    /// 配置は剛体 head-lock を廃し、<see cref="YawFollowLogic"/> の deadzone + SmoothDamp 緩追従
    /// （ScreenAnchor と同型）。頭を回すと遅れてついてくるが、視線だけ動かせば静止して読める。
    /// 距離・角度・追従はすべて SerializeField（現場調整前提）。
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

        // ⚠ 旧 `startVisible` / `autoHideSec` は 2026-09-14 に削除した。どちらもラッチ表示の
        //    ための設定で、**既定のまま一度も使われていない死んだつまみ**だった
        //    （`autoHideSec` は既定 0 ＝ 無効・`startVisible` は既定 false）。
        //    押下中の表示になったので、開けっ放しを閉じる仕掛けそのものが要らない。

        // ⚠ 旧 `recenterAutoShowSec`（要再登録が立った瞬間、非表示中でも 5 秒だけ自動表示）は
        //    2026-08-07 に廃止した。**体験者が被っている最中に業務連絡が視界へ勝手に出る**唯一の
        //    経路で、しかも読んでも体験者には何もできない（機器の言葉がホラー体験の中に出る）。
        //    異常はスタッフが右 B で開けば最優先の 1 件として必ず出る（PickAlert + RecoveryGuidance）。
        //    卓の heartbeat にも出ているので、気づく経路が消えたわけではない。

        private readonly StringBuilder _sb = new(256);
        private readonly YawFollowLogic _yawFollow = new();
        private bool _yawSeeded;

        /// <summary>押下中の表示（右 B）。<b>純ロジック</b>なので秒とフェードの規則はここに無い。</summary>
        private readonly StatusViewLogic _view = new();

        private bool _pinned;           // Editor の H キー / menu hud / 卓からのピン留め
        private float _probeLeft;       // 自動走行のプローブ（Probe(sec)）の残り時間

        private float _accum;
        private string _lastGuidance = "";

        // コントローラ操作モードのラベル（NORMAL/REG）。OvrControllerBridge が遷移時に push する。
        private string _modeLabel = "";

        // 導入の段（左の接続待ち / 報告の練習 / 題字 …）。OvrControllerBridge が変化時に push する。
        private string _introStep = "";

        // 登録中の右手の状態。Bridge が毎フレーム更新する。未配線のプレビューでは従来の案内を出す。
        private bool _rightConnected = true;
        private bool _rightTracked = true;

        /// <summary>面が出た理由。<b>テレメトリの <c>ev=status src=</c> がそのまま読む。</b></summary>
        public enum ShowSource
        {
            /// <summary>右 B を押している（本番の経路）。</summary>
            Held,
            /// <summary>Editor の H キー / <c>menu hud</c> / 卓からのピン留め。</summary>
            Pinned,
            /// <summary>自動走行のプローブ（<see cref="Probe"/>）。</summary>
            Probe,
        }

        /// <summary>
        /// ステータスを<b>ピン留め</b>する（Editor の H キー / <c>menu hud</c> のプレビュー）。
        /// ⚠ <b>右 B はこれを呼ばない</b> — B は押しているあいだだけの表示で、状態を残さない。
        /// ラン開始（<see cref="OnRunRestarted"/>）で必ず落ちる。
        /// </summary>
        public void SetVisible(bool v) => _pinned = v;

        /// <summary>
        /// <b>右 B を押しているか</b>を毎フレーム渡す（<c>OvrControllerBridge</c> が呼ぶ）。
        /// <b>戻り値は「このフレームで面が出た」</b> ＝ 振動を 1 粒鳴らす縁。
        ///
        /// ⚠ <b>計時はここでやる</b>（<c>Update</c> ではない）。Bridge と StatusHud の Update 順は
        /// 保証されないので、押した縁と振動が 1 フレームずれる形を作らない。
        /// </summary>
        public bool SetHeld(bool held)
        {
            _view.Tick(held, Time.unscaledDeltaTime);
            return _view.JustShown;
        }

        /// <summary>
        /// <paramref name="sec"/> 秒だけ面を立てる（自動走行のプローブ・<c>-e xpstatus 1</c>）。
        /// 人が被らなくても「黒の上に文字が出る」を録画で確かめるための口。
        /// </summary>
        public void Probe(float sec)
        {
            if (sec <= 0f) return;
            if (sec > _probeLeft) _probeLeft = sec;
        }

        /// <summary>
        /// <b>いま面が出ているか。真実源はここ 1 つ</b>
        /// ＝ ピン留め or プローブ中 or 右 B の押下ビューが見えている。
        /// </summary>
        public bool IsVisible => _pinned || _probeLeft > 0f || _view.Visible;

        /// <summary>
        /// いま書くべき不透明度 [0,1]。<b>手元の早見表（<see cref="ControllerGuidePanel"/>）も
        /// 同じ値を使う</b> — 2 枚が別々に薄れると、同じ操作で出た面が 2 通りの速さで消える。
        /// 位置合わせ中とピン留め・プローブは読ませ続けるので 1。
        /// </summary>
        public float StaffAlpha01 =>
            RegistrationActive || _pinned || _probeLeft > 0f ? 1f : _view.Alpha01;

        /// <summary>直近に面が出たときの理由（テレメトリ用）。</summary>
        public ShowSource LastShowSource { get; private set; } = ShowSource.Held;

        /// <summary>
        /// ラン開始からの<b>面が出ていた累計 (秒)</b>。
        /// ⚠ <b>画にも音にも出ない</b>ので、体験者の走行中に業務表示が出ていたかはここにしか残らない。
        /// </summary>
        public float VisibleSecSinceRun { get; private set; }

        /// <summary>ラン開始から面が出た回数。</summary>
        public int ShowCountSinceRun { get; private set; }

        /// <summary>導入の段（「左の接続待ち」「報告の練習」「題字」…）。空なら段を出さない。</summary>
        public void SetIntroStep(string label) => _introStep = label ?? "";

        /// <summary>位置合わせに使う右手の接続と位置追跡を受け取る。</summary>
        public void SetRightControllerState(bool connected, bool tracked)
        {
            _rightConnected = connected;
            _rightTracked = connected && tracked;
        }

        /// <summary>
        /// <b>HMD の中に文字を出してよいか ＝ 被っているのがスタッフか。</b>
        /// 黒の上の 1 行（<c>ShowEndingFader</c>）・手元の操作早見表
        /// （<c>ControllerGuidePanel</c>）が全部ここを見る。
        ///
        /// 印は 2 つだけ。どちらも<b>コントローラを持っている人にしか起こせない</b>。
        ///   - 右 B のステータス表示（スタッフが自分で押している）
        ///   - 位置合わせ作業中（登録は必ずスタッフの仕事）
        ///
        /// 2026-08-07 にこの門を作った。それまでは体験者にも文字が出ていて、機器の言葉が
        /// ホラー体験の入口に混ざっていた（ユーザー指摘「世界観を壊すので消して」）。
        /// スタッフが確認したいときは従来どおり全部読める ＝ 現地のリハ・切り分けの手段は減らない。
        ///
        /// ⚠ <b>2026-09-14 まで「押した人 ＝ 被っている人」を仮定していた</b>が、右は本編中も
        /// スタッフの手にある。押しているあいだだけ出す形にして、開けっ放しの経路を無くした。
        /// </summary>
        public bool StaffViewing => IsVisible || RegistrationActive;

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

            // ⚠ 旧「世界空間 Canvas に描く相手を渡す」は 2026-09-14 に消した。面は 3D の
            //   TextMeshPro になり Canvas を持たない（黒の上に描くため・下の UseOverlayShader）。

            // 既定 LiberationSans SDF は日本語グリフを持たない（ガイダンス・ステータス行が豆腐化する）。
            // OS フォントから日本語対応の動的 TMP フォントを生成して差し替える（失敗時は既定のまま）。
            if (text != null)
            {
                var jp = JapaneseHudFont.TryGet();
                if (jp != null) text.font = jp;

                // ⚠⚠ **題字と導入の覆いより後に、深度を無視して描く。**
                //    題字の黒（queue 4950・ZTest Always）と導入の覆い（queue 4900・乗算ブレンド）は
                //    先に描かれた文字を必ず潰すので、**描画順（queue 5000）と ZTest Always の両方**が要る。
                //    fontMaterial の getter がインスタンスを作るので共有マテリアルは汚れない。
                UseOverlayShader(text);
                text.fontMaterial.renderQueue = RenderQueue;
            }
        }

        /// <summary>タイトルの黒（4950）・導入の覆い（4900）より後に描く。<b>5000 を超えない</b>
        /// （URP の透明パスは [2501, 5000] しか描かない・<c>rules/unity-vr.md</c>）。</summary>
        private const int RenderQueue = 5000;

        /// <summary>TMP の Overlay 版（<c>ZTest Always</c>）。</summary>
        private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";

        /// <summary>
        /// TMP の <b>Overlay 版</b>（<c>ZTest Always</c>）へ差し替える。既定の
        /// <c>TextMeshPro/Distance Field</c> は ZTest をグローバル（<c>unity_GUIZTestMode</c>）で引くので
        /// マテリアルからは上書きできない。見つからないときは<b>差し替えずに続ける</b>
        /// （<see cref="OutroReport"/> と同文。マテリアルを壊して字が化けるよりはよい）。
        /// </summary>
        private static void UseOverlayShader(TMP_Text tmp)
        {
            var overlay = Shader.Find(OverlayShaderName);
            if (overlay == null)
            {
                Debug.LogWarning($"[StatusHud] {OverlayShaderName} が見つかりません。" +
                                 "題字や導入の黒にステータスが隠れる可能性があります");
                return;
            }
            tmp.fontMaterial.shader = overlay;
        }

        // ⚠⚠ **ラン開始でピン留めを必ず落とす**（2026-09-04）。押下中の表示は離せば消えるが、
        //    ピン留め（Editor の H キー / `menu hud`）は残るので、ここが受け皿になる。
        private void Start()
        {
            _runDirector = FindObjectOfType<ShowRunDirector>();
            if (_runDirector != null)
            {
                _runDirector.RunRestarted += OnRunRestarted;
                _runRestartHooked = true;
            }
        }

        private void OnDestroy()
        {
            if (_runRestartHooked && _runDirector != null) _runDirector.RunRestarted -= OnRunRestarted;
            _runRestartHooked = false;
        }

        // ⚠ 落とすのは**ピン留めだけ**（押下中の表示は指を離せば消える）。累計も 0 へ戻すので、
        //   `hudSec` / `hudN` は「この体験者の走行で何秒出ていたか」を意味する。
        private void OnRunRestarted()
        {
            _pinned = false;
            _probeLeft = 0f;
            VisibleSecSinceRun = 0f;
            ShowCountSinceRun = 0;
        }

        private ShowRunDirector? _runDirector;
        private bool _runRestartHooked;

        // 直前のフレームで面が出ていたか（出入りの縁をログへ残すためだけに持つ）。
        private bool _lastVisible;
        // いま出ている面が出てからの秒数（消えるときにログへ書く）。
        private float _shownSec;

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (_probeLeft > 0f)
            {
                _probeLeft -= dt;
                if (_probeLeft < 0f) _probeLeft = 0f;
            }

            TrackVisibility(dt);
            RenderContent();
        }

        /// <summary>
        /// 面の出入りを数えてログへ残す。
        ///
        /// ⚠⚠ <b>画にも音にも出ない。</b> 体験者の走行中に業務表示が出ていたかは、ここと
        /// テレメトリ（<c>ev=status</c> / <c>ev=sum</c> の <c>hudSec</c>）にしか残らない。
        /// 2026-09-14 までは 1 ビットも観測しておらず、卓でも解析器でも当日パネルでも
        /// 「開いたまま体験者へ渡った走行」を検出できなかった。
        /// </summary>
        private void TrackVisibility(float dt)
        {
            bool now = IsVisible;
            if (now)
            {
                VisibleSecSinceRun += dt;
                _shownSec += dt;
            }

            if (now == _lastVisible) return;
            _lastVisible = now;

            if (now)
            {
                // 同時に立っていたら「人の手でピン留めした」側を名乗る（プローブ > ピン > 押下）。
                LastShowSource = _probeLeft > 0f ? ShowSource.Probe
                               : _pinned ? ShowSource.Pinned
                               : ShowSource.Held;
                _shownSec = 0f;
                ShowCountSinceRun++;
                Debug.Log($"[StatusHud] 表示 on src={SourceTag(LastShowSource)}");
            }
            else
            {
                Debug.Log($"[StatusHud] 表示 off src={SourceTag(LastShowSource)} sec={_shownSec:F1}");
                _shownSec = 0f;
            }
        }

        /// <summary>ログとテレメトリで同じ綴りを使う（解析器が `src=` をそのまま読む）。</summary>
        public static string SourceTag(ShowSource src) => src switch
        {
            ShowSource.Pinned => "pin",
            ShowSource.Probe => "probe",
            _ => "held",
        };

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
            var setup = StaffSetupPanel.Instance;
            if (setup != null && setup.Visible)
            {
                if (text != null) text.enabled = false;
                return;
            }
            if (text == null) return;

            // 1. 登録中はガイダンスを強制表示（最優先）。
            if (registration != null && registration.IsActive)
            {
                string g = !_rightConnected
                    ? "右コントローラーを接続してください"
                    : !_rightTracked
                        ? "右コントローラーを前に出してください"
                        : registration.GuidanceText;
                if (!ReferenceEquals(g, _lastGuidance) && g != _lastGuidance)
                {
                    text.SetText(g);
                    _lastGuidance = g;
                }
                // 色の正は HmdTextStyle 1 か所。Tracking は「対応が要る行か」だけを返す。
                text.color = !_rightConnected || !_rightTracked || registration.GuidanceIsAlert
                    ? HmdTextStyle.Alert : HmdTextStyle.Ink;
                text.alpha = 1f;
                text.enabled = !string.IsNullOrEmpty(g);
                return;
            }
            _lastGuidance = "";

            // 2. ステータス表示中（右 B を押しているあいだ・ピン留め・プローブ）。
            //    自動で開く経路は持たない ＝ 体験者が被っている最中に文字が湧く道を 1 本も残さない。
            if (!IsVisible)
            {
                text.enabled = false;
                return;
            }

            _accum += Time.unscaledDeltaTime;
            if (_accum >= updateInterval || !text.enabled)   // 間引きの間は直前の文字列を維持
            {
                _accum = 0f;
                BuildStatus(_sb);
                text.SetText(_sb);
                // 対応が要る 1 件が出ているときだけ警告色（面の色は 2 色しか無い — HmdTextStyle）。
                text.color = _alertShown ? HmdTextStyle.Alert : HmdTextStyle.Ink;
                text.enabled = true;
            }

            // ⚠⚠ **濃さは色の後に書く。** `TMP_Text.color` の setter は alpha ごと上書きするので、
            //    先に書くと立ち上がり（0.15 秒）も離してからの猶予（0.3 秒）も 1 フレームで消える。
            //    ⚠ 文字列の組み立ては間引くが、濃さは毎フレーム書く（薄れ方が階段になる）。
            text.alpha = StaffAlpha01;
        }

        // 直近の BuildStatus が異常の 2 行を出したか（面の色を選ぶためだけに持つ）。
        private bool _alertShown;

        // 全編を日本語・直感表記へ（記号を廃し、意味を平文で）。既存参照の append のみで新規 GC を出さない。
        // 体験の骨格。ShowControlClient が実行時に自動生成することがあるので遅延解決する。
        private ShowRunDirector? _run;

        private ShowRunDirector? ResolveRun()
        {
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            return _run;
        }

        // 報告の件数の出どころ。ShowRunDirector と同じ流儀で遅延解決する（居なければ行を出さない）。
        private ShowControlClient? _show;

        private ShowControlClient? ResolveShow()
        {
            if (_show == null) _show = FindObjectOfType<ShowControlClient>();
            return _show;
        }

        // 体験者の左コントローラの生死。⚠ **これを読む唯一の面がここ**（連絡の面は体験者へ出す面で、
        // 「繋がっていません」を体験者に見せるわけにはいかない）。居なければ判定しない。
        private CommsPanel? _comms;

        private CommsPanel? ResolveComms()
        {
            if (_comms == null) _comms = FindObjectOfType<CommsPanel>();
            return _comms;
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

            // ⚠⚠ **1 行に情報を 2 つまで**（<see cref="HmdTextStyle"/> の規約）。
            //    2026-08-15 まで 1 行目は「相 ・ 経過 ・ いまの場所 ・ 表示中」の 4 情報を
            //    `・` で数珠つなぎにしていて、**面の幅を 3 割超えてはみ出していた**（折り返しは無効）。
            //    ラベルは体言 ＋ 全角コロンで揃える（操作の `入力：動作` と同じ形）。

            // 行1: 体験の相と経過。導入中・終了は周回より先にそれを言う（最初に知りたいのはここ）。
            ShowRunDirector? run = ResolveRun();
            if (run != null && run.Phase == ShowPhase.Intro)
            {
                // ⚠ 導入は 1 分近くあり、「導入中」だけでは**止まっているのか進んでいるのか読めない**。
                //   段（左の接続待ち / 報告の練習 / 題字）は OvrControllerBridge が push する
                //   （導入の状態機械は Assembly-CSharp 側にあり、この面からは見えない）。
                sb.Append("導入中");
                if (!string.IsNullOrEmpty(_introStep))
                {
                    sb.Append('：');
                    sb.Append(_introStep);
                }
            }
            else if (run != null && run.Phase == ShowPhase.Finished)
            {
                sb.Append("体験おわり");
            }
            else
            {
                bool hasLap = lapCounter != null && lapCounter.CurrentLap >= 1;
                // 走り切る周数を超えた周＝「帰りのスタート区間」（体験はここで終わる）。
                // 「4周目/3周」と出ると壊れて見えるので、そこだけ言い方を変える。
                if (hasLap && run != null && lapCounter!.CurrentLap > run.TotalLaps)
                {
                    // 「もどり」だけでは何のことか分からない（開発語）。体験の位置を言う。
                    sb.Append("最後の区間");
                }
                else if (hasLap)
                {
                    sb.Append(lapCounter!.CurrentLap);
                    sb.Append("周目");
                    // UI の区切りは全角（`X／Y` と揃える）。半角のままにしてよいのは
                    // 時刻 `1:05` やゾーン名 `C:North` のような**データそのもの**の中だけ。
                    if (run != null) { sb.Append("／全"); sb.Append(run.TotalLaps); sb.Append('周'); }
                }
                else sb.Append("周回 未確定");
            }
            if (run != null)
            {
                sb.Append("　経過 ");
                AppendClock(sb, run.Phase == ShowPhase.Intro ? run.IntroElapsedSec : run.RunElapsedSec);
            }

            // 行2: 場所と表示中のカメラ。
            sb.Append("\n場所：");
            var zone = tracker != null ? tracker.CurrentZone : null;
            sb.Append(zone != null ? zone.Label : "未確定");
            sb.Append("　表示：カメラ");
            if (registry != null && registry.Count > 0) sb.Append(registry.ActiveIndex + 1);
            else sb.Append('-');

            // 行3（次の cue がある時のみ）: 次の演出：{lap}周目 カメラ{c+1}
            if (cueScheduler != null && lapCounter != null && lapCounter.Order.Length > 0
                && cueScheduler.TryGetNextCue(lapCounter.CurrentLap, lapCounter.Position, lapCounter.Order, out var next))
            {
                // ⚠ cue の id は出さない。`cue_B_2` を読んでもスタッフには何もできない（開発語で、
                // 直し方にも繋がらない）。知りたいのは「次に何かが起きるのはどこか」だけ。
                sb.Append("\n次の演出：");
                sb.Append(next.lap);
                sb.Append("周目 カメラ");
                sb.Append(next.camera + 1);
            }

            // 行4: 受信：1○ 2× 3○（○=映像が届いている / ×=届いていない）。
            // ⚠ ラベルを付ける。○× を並べただけでは「接続」「受信」「選択中」のどれか読めない。
            sb.Append("\n受信：");
            if (registry != null && registry.Count > 0)
            {
                int n = registry.Count;
                for (int i = 0; i < n; i++)
                {
                    // 番号と記号を密着させない（`1○` は一瞬では読めない）。台どうしは全角空白で離す。
                    if (i > 0) sb.Append('　');
                    sb.Append(i + 1);
                    sb.Append(' ');
                    var s = registry.Get(i);
                    sb.Append(s != null && s.IsConnected ? '○' : '×');
                }
            }
            else sb.Append('-');

            // 行5: 報告：N 件（M 回押した）。**押されているかはスタッフに 1 ビットも届いていなかった**
            // （気づくのは終幕の報告が「０」になったとき ＝ もう手遅れ）。
            // N = 報告した異変の数（終幕に出る数・同じ演出は 1・`canon/LEDGER.md` 0234）/ M = 押した回数。
            // 両方出すのは、押しているのに N が増えない（演出の無い所で押している）を現場で見分けるため。
            // ⚠ 半角数字。全角にしてよいのは体験者が読む面（終幕の報告・紙と揃える）だけで、
            //   ここは業務表示なので他の数値（周回・カメラ番号・ずれ cm）と揃える。
            ShowControlClient? show = ResolveShow();
            if (show != null)
            {
                sb.Append("\n報告：");
                sb.Append(show.ReportedAnomalyCount);
                sb.Append(" 件（");
                sb.Append(show.VisitorMarkCount);
                sb.Append(" 回押した）");
            }

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
                    // 数値と単位のあいだは半角空白（HmdTextStyle の規約）。手は次の行へ分ける。
                    sb.Append("\n映像の遅れ ");
                    sb.Append(ms);
                    sb.Append(" ms\nWi-Fi が 2.4GHz なら 5GHz へ");
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
            _alertShown = alert != ShowAlert.None;
            if (_alertShown)
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
                sb.Append("\n位置合わせ済み");
                if (courseFrame.MaxResidualM > 0f)
                {
                    int cm = Mathf.RoundToInt(courseFrame.MaxResidualM * 100f);
                    if (cm > 0) // 丸めて 0 cm になる微小のずれは「位置合わせ済み」のみに畳む
                    {
                        sb.Append("（ずれ ");
                        sb.Append(cm);
                        sb.Append(" cm）");
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

            // 体験者の左コントローラ。⚠⚠ **切れても画にも音にも出ない**（報告の押し方の案内が
            //    黙って消えるだけ）。気づく経路は卓の heartbeat とここの 2 つしかない。
            CommsPanel? comms = ResolveComms();
            if (comms != null && !comms.LeftConnected) return ShowAlert.VisitorControllerLost;
            if (comms != null && !comms.LeftTracked) return ShowAlert.VisitorControllerUntracked;

            var active = registry != null ? registry.GetActive() : null;
            if ((active?.Health?.throttleStage ?? 0) > 0) return ShowAlert.Hot;

            // 床の高さを測っていない位置合わせ＝線や人形が沈んで見える原因。体験は成立するので最後。
            if (courseFrame != null && courseFrame.HasRegistration && !courseFrame.HasFloorY)
                return ShowAlert.FloorNotMeasured;

            return ShowAlert.None;
        }
    }
}
