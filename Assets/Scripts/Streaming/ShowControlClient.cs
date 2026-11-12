#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>show.json（体験の台本と現場設定）を読み、体験の各部品へ配る中心。</b>Screen GameObject に付ける。
    ///
    /// 設定の出どころ:
    ///   - 実機（Player）は APK に焼き込んだ <c>StreamingAssets/show/show.json</c> だけで動く（<see cref="BakedOnly"/>）
    ///   - Editor だけ、焼き込み &lt; 端末キャッシュ &lt; 卓の long-poll の順に上書きする
    ///
    /// 役目ごとにファイルを分けてある（partial class。中身は 1 つのコンポーネント）:
    ///   - <c>ShowControlClient.cs</c>              … 起動・終了、設定の保持、画像加工（post）とカメラ設定の適用、他部品への中継口
    ///   - <c>ShowControlClient.VisitorRun.cs</c>   … 体験 1 回ぶん。新しい体験者のリセット（<see cref="ResetForNewVisitor"/>）・
    ///                                               周の数え直しの合図・滞在の計測・体験者の報告（X/Y）
    ///   - <c>ShowControlClient.Live.cs</c>         … 卓とのやり取り。状態の long-poll と適用（<c>Apply</c>）・コマンド送信・heartbeat
    ///   - <c>ShowControlClient.BakedAndCache.cs</c> … 焼き込みの読み込みと、端末の設定キャッシュ
    ///   - <c>ShowControlClient.Assets.cs</c>       … cue と素材の URL・入れ替わりの無人プレート・先読み・目の視界ジャックの写真
    ///   - <c>ShowConfigTypes.cs</c>                … show.json の形（JsonUtility で読む純データの型）
    ///
    /// 契約（破ると壊れること）は <c>.claude/rules/stream-link.md</c>、体験の骨格は <c>.claude/rules/show-run.md</c>。
    /// </summary>
    public sealed partial class ShowControlClient : MonoBehaviour
    {
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        private static readonly int ContrastId = Shader.PropertyToID("_Contrast");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int TemperatureId = Shader.PropertyToID("_Temperature");
        private static readonly int VignetteId = Shader.PropertyToID("_Vignette");
        private static readonly int GrainId = Shader.PropertyToID("_Grain");
        private static readonly int ScanlineId = Shader.PropertyToID("_Scanline");
        private static readonly int LiftId = Shader.PropertyToID("_Lift");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int AberrationId = Shader.PropertyToID("_Aberration");
        private static readonly int PixelateId = Shader.PropertyToID("_Pixelate");
        private static readonly int ScanlineCountId = Shader.PropertyToID("_ScanlineCount");

        [SerializeField] private ShowServerSource? server;
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("カメラ手動 override 中に無効化するゾーン Tracker（PlayerZoneTracker）。" +
                 "asmdef 循環回避のため Behaviour 参照で持つ。null なら override 時も Tracker は動き続ける。")]
        [SerializeField] private Behaviour? zoneTrackerToDisable;

        [Tooltip("heartbeat 送信間隔 (秒)。")]
        [SerializeField, Min(0.5f)] private float heartbeatInterval = 2f;

        [Tooltip("端末ローカルへ保存する設定キャッシュのファイル名（persistentDataPath 配下）。" +
                 "Quest 単体ビルドで PC 不在時、前回 Web で設定した IP / 画像加工を起動時に再適用する。")]
        [SerializeField] private string configCacheFileName = "show_config.json";

        [Tooltip("事前オーサリング済み cue スケジュールを駆動する CueScheduler。" +
                 "schedule / cue 解決 / activeCue 抑止状態を供給する。null なら自動発火なし。")]
        [SerializeField] private CueScheduler? cueScheduler;

        [Tooltip("カメラ override を出どころ Override として通す CameraSwitchDirector。" +
                 "null なら従来どおり registry.SetActive を直接叩く（後方互換・その場合 LapCounter 側は External 扱い）。")]
        [SerializeField] private CameraSwitchDirector? switchDirector;

        [Tooltip("タイムライン（show.json timeline スキーマ v2）を CueScheduler / InsertController / " +
                 "post 上書きへ分配する TimelineDirector。null なら timeline は無視され従来 schedule で動く（後方互換）。")]
        [SerializeField] private TimelineDirector? timelineDirector;

        [Tooltip("BGM 再生器。show.json の bgmTracks / bgm と区間 bgm 指示を受ける。" +
                 "null ならシーンから探す（[Bgm]）。見つからなければ BGM 制御なし（後方互換）。")]
        [SerializeField] private BgmDirector? bgmDirector;

        private ScreenOverlayController? _overlay;
        private Material? _material;
        private int _rev = -1;
        [NonSerialized] private bool _bakedOnlyOverride; // EditMode テストで Player のゲートを駆動する。
        // 体験の再生（PC）も焼き込みだけで動く。端末と同じ著作を読み、卓へは繋がない。
        private bool BakedOnly => !Application.isEditor || _bakedOnlyOverride || Replay.XpReplay.Active;
        public string ContentId { get; private set; } = "";
        public string ContentPolicy => BakedShowManifest.Policy;
        public bool ContentVerified { get; private set; }
        // 適用に失敗した版と、その版で何回失敗したか（`PollLoopAsync` の巻き戻し用）。
        private int _applyFailRev = int.MinValue;
        private int _applyFailCount;
        // 同じ版で何回まで再試行するか。超えたら諦めて先へ進む（恒久的に壊れた設定で
        // 2 秒ごとに例外を吐き続けると、logcat のリングバッファを食い潰して走行の前半が落ちる）。
        private const int ApplyRetryMax = 3;
        private string _appliedCue = "";
        private string _appliedOverride = "";

        // ---- 発見（discovery）連携用の公開状態 ----
        // DiscoveryClient が「PC 卓が不通か」を判定するために最後の応答時刻を持つ。
        // control.discoveryEnabled はキルスイッチ（false で probe/切替を全停止。省略時 true）。
        private float _lastServerContactTime = -999f;

        /// <summary>最後に卓から応答を得た realtimeSinceStartup。未接続なら大きな負値。</summary>
        public float LastServerContactTime => _lastServerContactTime;

        // コントローラ操作モード（NORMAL/REG）。OvrControllerBridge が遷移時に push し、heartbeat に載せる。
        private string _controllerMode = "NORMAL";

        // server 未設定、または /state を ServerStaleSeconds 受信できていない = 不通と見なす。
        // long-poll 上限(35s)より長くとり、健全な idle 接続を誤って不通判定しない。
        private const float ServerStaleSeconds = 40f;

        /// <summary>server が実効的に通じているか（cue 試射のローカルフォールバック分岐に使う）。</summary>
        private bool ServerReachable
            => !BakedOnly && server != null && (WallClock.Now - _lastServerContactTime) < ServerStaleSeconds;

        /// <summary>コントローラ操作モードのラベル（NORMAL/REG）を設定する。heartbeat で卓へ報告する。</summary>
        public void SetControllerMode(string mode) => _controllerMode = string.IsNullOrEmpty(mode) ? "NORMAL" : mode;

        /// <summary>
        /// いまのコントローラ操作モード（<c>NORMAL</c> / <c>REG</c>）。テレメトリが読む。
        ///
        /// ⚠ これが無かったせいで、<b>「トリガー長押しが発火していないのか、発火しても
        /// 画に出ていないのか」を実機ログから切り分けられなかった</b>（2026-08-09）。
        /// 卓の heartbeat にしか出ておらず、卓が居ない現場では観測手段がゼロだった。
        /// </summary>
        public string ControllerMode => _controllerMode;

        /// <summary>卓サーバの接続先設定（null なら卓連携なし）。DiscoveryClient が現エンドポイント比較に読む。</summary>
        public ShowServerSource? Server => server;

        /// <summary>show.json control.discoveryEnabled（省略時 true）。DiscoveryClient のキルスイッチ上書き。</summary>
        public bool DiscoveryEnabled { get; private set; } = true;

        /// <summary>index 番カメラが卓で手動固定（pinned）されているか。pinned には discovery を適用しない。</summary>
        public bool IsCameraPinned(int index)
            => index >= 0 && index < _cameras.Length && _cameras[index] != null
               && (_cameras[index]!.pinned || (BakedOnly && !string.IsNullOrEmpty(_cameras[index]!.host)));

        // 直近に解決したカメラ別設定 / global post（ライブ or 端末キャッシュ由来）。
        // ゾーン自律切替（ActiveChanged）でカメラ別 post を再適用するため保持する。
        private CameraDef[] _cameras = Array.Empty<CameraDef>();
        private PostParams _globalPost = new PostParams();
        private bool _subscribed;

        // ---- post 上書き層（タイムライン）----
        // 区間 post は TimelineDirector が SetPostOverride で掛け外し（区間滞在中のみ）。
        // インサート表示中は _insertPostActive の insert 層が segment 層より優先する
        // （insert 中は insert.post ?? インサート先カメラ post ?? global。segment 層は素通ししない）。
        private PostParams? _segmentPostOverride;
        private bool _insertPostActive;
        private PostParams? _insertPostOverride;

        // ゾーン layout（ライブ or 端末キャッシュ由来）。Tracking 側（ZoneLayoutApplier）が読む。
        private ShowLayoutDef? _layout;
        private int _appliedLayoutRev = -1;

        // 素材スロットの束縛（control.slots 由来）。slot://name を実 URL へ解決するのに引く。
        // ラン中に卓が差し替えるので timeline とは独立に持つ（timeline を触ると once が再武装される）。
        private ShowSlotDef[] _slots = Array.Empty<ShowSlotDef>();

        // 端末内録画の設定（record 由来）。SegmentRecorder が読む。
        private ShowRecordDef? _record;

        /// <summary>端末内録画の設定（show.json <c>record</c>）。未指定なら null。</summary>
        public ShowRecordDef? RecordConfig => _record;

        // 体験の骨格（run 由来）。ShowRunDirector が読む。欠落ならコード既定で 3 周・導入あり。
        private ShowRunDef? _run;

        /// <summary>体験の骨格（show.json <c>run</c>）。未指定なら null（＝コード既定）。</summary>
        public ShowRunDef? RunConfig => _run;

        private ShowFeelDef? _feel;

        /// <summary>撮像の質（show.json <c>feel</c>）。未指定なら null（＝コード既定で効く）。</summary>
        public ShowFeelDef? FeelConfig => _feel;

        private CameraFeelFx? _feelFx;

        // 撮像の質の writer。シーンに居なければ何もしない（配線前でも体験は成立する）。
        private CameraFeelFx? ResolveFeelFx()
        {
            if (_feelFx != null) return _feelFx;
            _feelFx = FindObjectOfType<CameraFeelFx>();
            return _feelFx;
        }

        private void PushFeel() => ResolveFeelFx()?.Configure(_feel);

        /// <summary>いま効いている設定がどこから来たか（<c>none</c> / <c>baked</c> / <c>cache</c> / <c>live</c>）。
        ///
        /// **端末キャッシュは焼き込みより優先される**ので、古いキャッシュが残っていると APK を焼き直しても
        /// 設定が変わらない。実測（2026-07-31）で 2 台の Quest のキャッシュを突き合わせたところ、
        /// 片方にだけ <c>run.intro.startLineId</c> が無く、**導入の始まり方が機ごとに違っていた**。
        /// しかも <c>timeline.rev</c> は両方 21 で一致しており、rev では検出できない。
        ///
        /// 解析（tools/analyze-xp-log.py）は PC の show.json を期待値にするので、実機がどの設定で
        /// 走ったのかを言えないと「出なかった演出」を誤検出する。<see cref="DescribeConfig"/> と対で使う。</summary>
        public string ConfigOrigin { get; private set; } = "none";

        /// <summary>効いている設定の骨格を 1 行で要約する。解析器が PC の show.json から同じ要約を作って
        /// 突き合わせる。ハッシュではなく**項目を並べる**のは、食い違ったときにどこが違うかを名指しするため
        /// （ハッシュだと「違う」としか言えず、現場で直せない）。</summary>
        public string DescribeConfig()
        {
            int segs = _timeline?.segments?.Length ?? 0;
            int takes = 0;
            if (_timeline?.segments != null)
                foreach (var s in _timeline.segments) takes += s?.takes?.Length ?? 0;

            ShowIntroDef? intro = _run?.intro;
            bool introOn = (_run?.introEnabled ?? true) && (intro == null || intro.enabled);
            string startLine = intro != null && !string.IsNullOrEmpty(intro.startLineId)
                ? intro.startLineId : "-";

            // 録る周は指紋に入れる。ここがずれていると **rec カットが全部無言で飛ぶ**（録画は
            // ラン中にしか作れないので、後から気づいても取り返せない）。
            string recLaps = "-";
            if (_record != null && _record.enabled && _record.laps != null && _record.laps.Length > 0)
                recLaps = string.Join(",", _record.laps);

            // 締めの線（3 周目 A の凍結点・③a の引き金・0233）。台本から導くので、ずれていれば
            // 「③a が線で出るはずなのに時計で出た」の切り分けがここで付く。
            string closingLine = "-";
            if (_timeline?.segments != null)
            {
                var allTakes = new List<ShowTakeDef?>();
                foreach (var s in _timeline.segments)
                    if (s?.takes != null) allTakes.AddRange(s.takes);
                string id = TakeSchema.ResolveClosingLineId(allTakes.ToArray());
                if (!string.IsNullOrEmpty(id)) closingLine = id;
            }

            return $"src={ConfigOrigin} rev={_rev} tlrev={(_timeline?.rev ?? -1)} " +
                   $"segs={segs} takes={takes} cues={_cues.Length} cams={_cameras.Length} " +
                   $"lines={(_layout?.lines?.Length ?? 0)} laps={(_run?.totalLaps ?? -1)} " +
                   $"intro={(introOn ? 1 : 0)} startLine={startLine} " +
                   $"rec={((_record?.enabled ?? false) ? 1 : 0)} recLaps={recLaps} " +
                   $"closingLine={closingLine} calib={(Replay.XpReplay.CalibOverridden ? "twin" : "show")}";
        }

        // 卓からの手動グリッチ / 導入終了 / 体験終了の世代カウンタ（runEpoch と同じ「変化のみ発火」方式）。
        private int _knownGlitchEpoch;
        private bool _glitchEpochKnown;
        private int _knownIntroEpoch;
        private bool _introEpochKnown;
        private int _knownRunEndEpoch;
        private bool _runEndEpochKnown;

        // CG レイヤの人形定義（actors 由来）。ShowCgLayer が id で引く。
        private ShowActorDef[] _actors = Array.Empty<ShowActorDef>();

        /// <summary>CG レイヤの人形定義を id で引く。未定義なら null。</summary>
        public ShowActorDef? FindActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (ShowActorDef? a in _actors)
                if (a != null && a.id == id) return a;
            return null;
        }

        /// <summary>index 番カメラの course 空間姿勢（CG レイヤ用）。未著作なら false。</summary>
        public bool TryGetCameraPose(int index, out ShowCameraPoseDef pose)
        {
            pose = null!;
            if (index < 0 || index >= _cameras.Length) return false;
            CameraDef? c = _cameras[index];
            if (c == null || !c.hasPose || c.pose == null) return false;
            pose = c.pose;
            return true;
        }

        /// <summary>
        /// index 番カメラの**較正結果**（実映像から解いた実測値）。未較正なら false。
        /// 呼び出し側は受信中フレームの実寸・lensId で <see cref="ShowCameraCalibDef.MatchesSource"/> を
        /// 必ず確認すること（解像度やレンズを変えたら較正は無効）。
        /// </summary>
        public bool TryGetCameraCalib(int index, out ShowCameraCalibDef calib)
        {
            calib = null!;
            if (index < 0 || index >= _cameras.Length) return false;
            CameraDef? c = _cameras[index];
            if (c == null || !c.hasCalib || c.calib == null || !c.calib.IsUsable()) return false;
            calib = c.calib;
            return true;
        }

        /// <summary>index 番カメラが演出専用（<c>role:"fx"</c>）か。ゾーン割当・スタッフ巡回から外す。</summary>
        public bool IsFxCamera(int index)
            => index >= 0 && index < _cameras.Length && _cameras[index] != null
               && CameraRoles.IsFx(_cameras[index]!.role);

        // cue 定義（scheduler の cue 解決 + 手動 activeCue 発火の両方が引く）。
        private CueDef[] _cues = Array.Empty<CueDef>();
        // 事前オーサリング済みスケジュール（ライブ or 端末キャッシュ由来）。CueScheduler へ供給。
        private ShowScheduleDef? _schedule;
        private int _appliedScheduleRev = -1;
        // タイムライン（スキーマ v2・ライブ or 端末キャッシュ由来）。TimelineDirector へ供給。
        private ShowTimelineDef? _timeline;
        private int _appliedTimelineRev = -1;

        // BGM ライブラリ + ラン既定（ライブ or 端末キャッシュ由来）。BgmDirector へ供給。
        private ShowBgmTrackDef[] _bgmTracks = Array.Empty<ShowBgmTrackDef>();
        private ShowBgmDef? _bgmDefault;

        // timeline が有効（rev>0 && segments 非空）で、かつ TimelineDirector が配線されているか。
        // これが true の間だけ timeline が schedule.entries を supersede する。
        private bool TimelineActive
            => _timeline != null && _timeline.rev > 0
               && _timeline.segments != null && _timeline.segments.Length > 0;

        /// <summary>現在の layout（未設定なら null）。ZoneLayoutApplier が Rebuild で参照する。</summary>
        public ShowLayoutDef? Layout => _layout;

        /// <summary>
        /// 部屋の 3D プロキシ（<c>layout.room</c>）。未著作なら null。
        /// present-flag は **宣言 bool AND 実データ**で確定する（JsonUtility が null 入れ子を既定オブジェクトとして
        /// 書き戻すため、`!=null` 純導出だと端末キャッシュ往復で幽霊の部屋が湧く）。
        /// </summary>
        public ShowRoomDef? Room
        {
            get
            {
                ShowLayoutDef? l = _layout;
                if (l == null || !l.hasRoom) return null;
                return (l.room != null && l.room.HasData()) ? l.room : null;
            }
        }

        /// <summary>layout（cuts/floor/overlap 等）が変わった時に発火する。</summary>
        public event Action? LayoutChanged;

        /// <summary>
        /// レイアウトを直接差し替えて <see cref="LayoutChanged"/> を発火する **Editor プレビュー / デバッグ起動フック /
        /// テスト専用**フック。show.json を読まずに任意の <see cref="ShowLayoutDef"/> を注入して
        /// 登録ビュー（ZoneGridFootprint / ワイヤーフレーム）を組ませるためのもの。運用コード（long-poll・
        /// キャッシュ・焼き込み経路）からは呼ばない。ネットワーク系の状態（rev / server / heartbeat）には触れない。
        /// </summary>
        public void SetLayoutForPreview(ShowLayoutDef? layout)
        {
            _layout = layout;
            LayoutChanged?.Invoke();
        }

        /// <summary>周回巡回順（layout.course.order）。未設定なら空配列。LapCounter が読む。</summary>
        public int[] CourseOrder
            => _layout != null && _layout.course != null && _layout.course.order != null
                ? _layout.course.order
                : Array.Empty<int>();

        /// <summary>course（周回巡回順）が変わった時に発火する。LapCounter が購読して order を再取得する。</summary>
        public event Action? CourseChanged;

        // ---- ラン概念（runEpoch）----
        // control.runEpoch が変わった＝新しい体験者のランが始まった。周回リセット + cue 発火済みフラグ全消去。
        // 「既知値」を起動時にキャッシュ / 焼き込みから初期化し、以後の変化のみ発火する
        // （起動のたびに誤リセットしない）。
        private int _knownRunEpoch;

        // 走行中の演出を中止する世代（control.takeAbortEpoch）。runEpoch と同じ「変化のみ発火」方式。
        // 起動時は初回 Apply / 焼き込みロードで現在値へ同期し、そこからの増分だけを中止として扱う。
        private int _knownTakeAbortEpoch;
        private bool _takeAbortEpochKnown;

        // カメラ切替の現場調整（control 由来。0=未指定でコード既定）。ライブ / キャッシュ / 焼き込みで更新し
        // ApplySwitchTiming で CameraSwitchDirector へ流す。キャッシュへ往復させ PC 不在起動でも値を保つ。
        private float _switchDwellSec;
        private float _switchCooldownSec;

        // heartbeat に載せる HMD の course space XZ・現在ゾーンラベルの供給元。
        // Streaming → Tracking の参照を作らないため Func で注入する（ZoneLayoutApplier が設定）。
        /// <summary>HMD 位置を course space XZ で返す供給元（null なら heartbeat に載せない）。</summary>
        public Func<Vector2>? HeadCourseXZProvider;
        /// <summary>現在ゾーンのラベルを返す供給元（null なら空文字）。</summary>
        public Func<string>? CurrentZoneLabelProvider;
        /// <summary>
        /// <b>いまの区間をどこまで来たか</b>の供給元（ZoneLayoutApplier が注入・<c>canon/LEDGER.md</c> 0093）。
        /// null なら <see cref="ZoneSpan.valid"/> が false ＝ 位置では測らない（演出はカットの終わりに任せる）。
        /// </summary>
        public Func<ZoneSpan>? ZoneSpanProvider;

        /// <summary>
        /// コントローラの生死（左接続 / 左位置 / 右接続 / 右位置）。<c>OvrControllerBridge</c> が配線する
        /// （Streaming は OVR を参照しない規約なので、向こうから書きに来る — 既存の provider と同じ流儀）。
        ///
        /// ⚠⚠ heartbeat で卓へ送る。**切れても画にも音にも 1 ビットも出ない** — 左は体験者の唯一の
        /// 入力（報告）なので、電池切れ・スリープで死ぬと報告が 1 件も上がらないまま終幕を迎える。
        /// null = 未配線（卓側は「不明」として扱う）。
        /// </summary>
        public Func<(bool lConnected, bool lTracked, bool rConnected, bool rTracked)>? ControllerStateProvider;

        // 音の実行体。**音は録画に映らない**ので、鳴っているかの唯一の証拠を heartbeat へ載せる。
        // 後から生えることがあるので、解決できるまで heartbeat のたびに探す（周期は秒単位）。
        private ShowSoundDirector? _soundDirector;
        private ShowSoundDirector? ResolveSoundDirector()
        {
            if (_soundDirector != null) return _soundDirector;
            _soundDirector = FindObjectOfType<ShowSoundDirector>();
            return _soundDirector;
        }

        /// <summary>いまの区間の進み。供給元が居なければ「測れない」を返す。</summary>
        public ZoneSpan ZoneSpan => ZoneSpanProvider != null ? ZoneSpanProvider() : default;
        /// <summary>
        /// course space の (XZ, y) をワールド座標へ変換する供給元（CourseFrame.CourseToWorld を注入）。
        /// CG レイヤの仮想カメラ・人形を位置合わせ済みの実空間へ置くのに使う。null ならワールド＝course。
        /// </summary>
        public Func<Vector2, float, Vector3>? CourseToWorldProvider;
        /// <summary>course space の yaw（度）を返す供給元（CourseFrame.YawDeg を注入）。null なら 0。</summary>
        public Func<float>? CourseYawProvider;
        /// <summary>
        /// HMD 位置合わせ（登録）が済んでいるかの供給元（CourseFrame.HasRegistration を注入）。
        /// **未登録のまま CG を出すと人形が全く違う場所に立つ**（course→world が identity へ落ちるため）。
        /// null なら「登録の有無を知らない」＝ CG を止めない（オフラインテスト・Editor プレビュー用）。
        /// </summary>
        public Func<bool>? CourseRegisteredProvider;

        /// <summary>
        /// 「要再登録」の供給元（CourseFrame.NeedsReRegistration を注入）。OS の recenter で立つ。
        /// **これが立ったまま体験を始めると、ゾーンが実空間に対してズレたまま動く**
        /// （「歩いても切り替わらない」の最有力原因）。HMD 内には出るが体験者が被っている間は
        /// 誰も読めないので、heartbeat で卓の本番前チェックへ届ける（2026-07-28）。
        /// </summary>
        public Func<bool>? CourseNeedsReRegProvider;

        /// <summary>
        /// 位置合わせが<b>確定保存された時刻</b>の供給元（CourseFrame.SavedAtIso を注入）。
        /// これが変わった＝スタッフが B で確定した、という<b>事象</b>を検出するために使う。
        ///
        /// <see cref="IntroDirector"/> が導入の中止から自力で復帰するのに要る。中止の復帰は本来
        /// 「① 位置合わせを撃ち直す ② ランリセット」の 2 段で、**順序を逆にすると直らない**
        /// （先にランリセットしても要再登録フラグが立ったままで即また中止される）。人間に順序を
        /// 暗記させる代わりに、確定を検出して導入をやり直すことで 1 段にする。
        ///
        /// ⚠ <c>CourseNeedsReRegProvider</c> の false 化では代用できない。プレビュー
        /// （<c>SetRegistration(save:false)</c>）でもフラグは降りるので、B 確定の前に導入が再開して
        /// 登録ビューと演出が混ざる。<c>SavedAtIso</c> は <c>SaveRegistration</c> でしか変わらない。
        /// </summary>
        public Func<string>? CourseRegistrationStampProvider;

        private string ConfigCachePath => Path.Combine(Application.persistentDataPath, configCacheFileName);

        /// <summary>
        /// 焼き込みが持つ「卓の URL → <c>sa://</c> URL」の 1 対。<c>export_build.bake</c> が出す。
        /// ⚠ <c>JsonUtility</c> は Dictionary を読めないので配列で受ける。
        /// </summary>
        [Serializable] private class AssetMapEntry
        {
            public string from = "";
            public string to = "";
        }

        [Serializable] private class ShowState
        {
            public int rev;
            /// <summary>焼き込みだけが持つ素材の対応表（ライブ / キャッシュには無い）。</summary>
            public AssetMapEntry[] assetMap = Array.Empty<AssetMapEntry>();
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public CueDef[] cues = Array.Empty<CueDef>();
            public PostParams? post;
            public ControlState? control;
            public ShowLayoutDef? layout;
            public ShowScheduleDef? schedule;
            public ShowTimelineDef? timeline;   // スキーマ v2（present なら schedule を supersede）
            public ShowBgmTrackDef[]? bgmTracks;  // BGM ライブラリ
            public ShowBgmDef? bgm;               // ラン既定 BGM（IsActionable() が present 判定）
            public ShowRecordDef? record;         // 端末内録画の設定（欠落 = 無効）
            public ShowActorDef[]? actors;        // CG レイヤの人形定義
            public ShowRunDef? run;               // 体験の骨格（周数・導入・終端）。欠落 = コード既定
            public ShowFeelDef? feel;             // 撮像の質（装置らしさ）。欠落 = コード既定
            public ShowEyeJackDef? eyejack;       // 目の視界ジャックの当日写真（欠落 = 0 枚）
        }
        [Serializable] private class CameraDef
        {
            public string id = "";
            public string sourceId = "";
            public string host = "";
            public int port;
            public string auth = "";       // "user:pass"（空=認証なし）
            public PostParams? post;        // カメラ別画像加工（null=global にフォールバック）
            // JsonUtility は null の入れ子クラスを既定値オブジェクトとして書き出すため、
            // キャッシュ往復後に post の null 判定が壊れる。「個別 post を持つか」は明示 bool を正にする
            // （ライブ受信パース直後に post!=null から確定し、キャッシュにも保存して往復させる）。
            public bool hasPost;
            // 卓で host を手入力すると自動で true。true のカメラには DiscoveryClient が
            // 発見層を適用しない（手動固定を尊重）。キャッシュへも往復させる（CachedConfig.cameras 経由）。
            public bool pinned;
            // "zone"（既定・ゾーンに割り当てる）| "fx"（演出専用。ゾーン自動切替にもスタッフ巡回にも出さない）
            public string role = CameraRoles.Zone;
            // CG レイヤの仮想カメラが構える姿勢（course 空間）。hasPose が present-flag。
            // **人がドラッグして置く概算**で、較正の解ではない（calib があればそちらが勝つ）。
            public ShowCameraPoseDef? pose;
            public bool hasPose;
            // 較正の解（卓が実映像上の床点から解いた実測値）。hasCalib が present-flag。
            // 空の既定オブジェクトが来ても IsUsable() が false になるので幽霊武装しない。
            public ShowCameraCalibDef? calib;
            public bool hasCalib;
        }

        // 端末ローカルへ保存する設定キャッシュ（show.json のうち実機が参照する部分のみ）。
        // course は layout に内包されるため layout の保存で往復する。
        [Serializable] private class CachedConfig
        {
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public PostParams? post;
            public ShowLayoutDef? layout;
            public CueDef[] cues = Array.Empty<CueDef>();
            public ShowScheduleDef? schedule;
            public ShowTimelineDef? timeline;   // スキーマ v2（オフライン supersede 用）
            public ShowBgmTrackDef[] bgmTracks = Array.Empty<ShowBgmTrackDef>();
            public ShowBgmDef? bgm;             // ラン既定 BGM（PC 不在起動でも同じ曲で始まる）
            public ShowRecordDef? record;       // 端末内録画の設定（PC 不在でも録れるように往復させる）
            public ShowActorDef[] actors = Array.Empty<ShowActorDef>();
            public ShowFeelDef? feel;           // 撮像の質（PC 不在でも同じ画で始まる）
            // 体験の骨格（PC 不在の現地でも 3 周で終わるように往復させる）。
            public ShowRunDef? run;
            // 視界ジャックの当日写真リスト（PC 不在の再起動でも同じ写真で走る。実バイトは別途
            // persistentDataPath/eyejack/ に写真ごとキャッシュされる — EyeJackPhotoStore）。
            public ShowEyeJackDef? eyejack;
            // 直近に既知だった runEpoch。起動時にこれを「既知値」として復元し、
            // PC 不在の再起動で同一 epoch を誤リセットしない。
            public int runEpoch;
            // カメラ切替の現場調整（control 由来）。0=未指定でコード既定。PC 不在起動でも値が生きるよう往復させる。
            public float switchDwellSec;
            public float switchCooldownSec;

            /// <summary>
            /// このキャッシュを書いた APK の識別子（<c>Application.buildGUID</c>）。
            ///
            /// ⚠⚠ <b>キャッシュは焼き込みより優先されるので、これが無いと「APK を焼き直しても
            /// 設定が変わらない」</b>。2026-07-31 に PC の show.json・焼き込み・Quest 2 台の
            /// キャッシュの<b>4 者がずれていて正しいのは 1 つだけ</b>という実測がある
            /// （片方の機にだけ古い <c>startLineId</c> が残り、導入の始まり方が機ごとに違った）。
            /// しかも <c>timeline.rev</c> は全部一致していたので<b>版番号では気づけない</b>。
            /// </summary>
            public string buildGuid = "";
        }
        [Serializable] private class CueDef
        {
            public string id = "";
            public string name = "";
            public string maskUrl = "";
            public string sourceUrl = "";
            public float strength = 1f;
            public bool loop = true;
            public float fadeIn = 0.5f;
            public float fadeOut = 0.5f;
            public float trimStart = 0f;
            public float trimEnd = 0f;   // <=0 = 最後まで

            // 色統計マッチング（企画書 2.3「差し替え素材の全体には色統計マッチングを施し、実写映像と
            // 継ぎ目なく合成する」）。卓が cue 保存時に Reinhard per-channel を解いて **gain/offset へ落として**
            // 焼く。実機は overlay サンプル後に out = src*gain + offset を掛けるだけ（6 float・ほぼ無料）。
            // 統計そのものを実機で毎フレーム取ると Quest には重く、しかもマスク領域が動かない固定視点では
            // 事前に解ける値なので、卓で解いて配るのが正しい分担。
            public bool hasMatch;
            public float[] matchGain = { 1f, 1f, 1f };
            public float[] matchOffset = { 0f, 0f, 0f };

            // 滲んで現れる・長押しで布が膨らむ（0349）。どれも省略時は従来どおり。
            //   blend        "replace"（既定）/ "multiply"（素材は比の画。ライブに掛ける）
            //   revealUrl    枠空間の順の画。空なら strength のフェードで出入りする
            //   pressMaskUrl 枠空間の press マスク。markStartCueId に名指しされたとき切り替えずに膨らませる
            public string blend = "";
            public string revealUrl = "";
            public float revealSec;
            public float revealOutSec = OverlayRevealLogic.DefaultOutSec;
            public float revealDelaySec;
            public string pressMaskUrl = "";
            public float[] pressCenter = { 0.5f, 0.5f };
        }
        // PostParams は public トップレベルへ昇格済み（ファイル冒頭）。CameraDef.post / _globalPost /
        // タイムライン各定義 / SetPostOverride が共有する。
        // discoveryEnabled は「省略時 true」を守るため C# 初期化子で true にする
        // （JsonUtility.FromJson は既定コンストラクタで初期化子を走らせてから present なキーだけ上書きするため、
        //  JSON にキーが無ければ true が残る。control ブロックごと無い場合も呼び出し側が true 扱いにする）。
        [Serializable] private class ControlState
        {
            public string? activeCue;
            public string? cameraOverride;
            public bool discoveryEnabled = true;
            // 体験者 1 人分のラン識別子。Web の「ラン開始」で ++ される。変化＝周回 / cue のリセット。
            public int runEpoch;
            // 走行中の演出（Take）の中止世代。Web の「■ 画面を取り返す」で ++ される。
            // ⚠ activeCue を空にする形では伝わらない（自動発火の演出は activeCue が空のまま走るため）。
            public int takeAbortEpoch;
            // カメラ切替の現場調整（CameraSwitchDirector へ流す）。present 判定は「>0 で適用 / 0=未指定でコード既定」。
            public float minDwellSec;
            public float switchCooldownSec;
            // 素材スロットの束縛（slot://name → 実 URL）。timeline を触らずに素材だけ差し替えるための面。
            public ShowSlotDef[] slots = Array.Empty<ShowSlotDef>();

            // ゾーン切替そのものに重ねる「映像の乱れ」の強さ（0 = 重ねない）。
            public float switchGlitch;
            // 卓からの手動発火。**変化**で 1 回走る（「空を空にする」形では伝わらないため世代カウンタ）。
            public int glitchEpoch;
            public float glitchLevel;
            public float glitchSec;

            // 導入を終えて本編へ進める合図（世代カウンタ）。卓の「⏭ 導入を終える」。
            public int introAdvanceEpoch;
            // 体験を終える合図（世代カウンタ）。卓の「■ 体験を終える」。
            public int runEndEpoch;
        }

        private void Awake()
        {
            _overlay = GetComponent<ScreenOverlayController>();
            var renderer = GetComponent<Renderer>();
            _material = renderer != null ? renderer.material : null;

            // 入れ替わりの差分マスク（0095）へ無人プレートを配る。SwapMorphFx は同じ
            // Screen GameObject に居る（MainSceneBuilder）。居なければ配線しないだけで、
            // 入れ替わりは CG の形へ縮退する（SwapMorphFx 側が警告を出す）。
            _swapFx = GetComponent<SwapMorphFx>();
            if (_swapFx == null) _swapFx = FindObjectOfType<SwapMorphFx>();
            _swapFx?.SetPlateProvider(SwapPlateFor);

            // ゾーン自律切替・オペレータ override でアクティブカメラが変わったら
            // そのカメラ別 post を貼り直す（server 不在でも効かせたいので Awake で購読）。
            if (registry != null && !_subscribed)
            {
                registry.ActiveChanged += OnActiveCameraChanged;
                _subscribed = true;
            }
        }

        private void Start()
        {
            // 端末 ID（heartbeat で名乗る。博士タブレットアプリが役 α/β と結ぶ・0185）。
            // メインスレッドでしか読めないので、ここで 1 度だけ取る。
            _deviceId = SystemInfo.deviceUniqueIdentifier ?? "";
            _deviceModel = SystemInfo.deviceModel ?? "";
            EnsureVisitorPortal();
            // 優先順位: 焼き込み StreamingAssets < 端末キャッシュ < ライブ long-poll（後勝ち）。
            // 焼き込みの読込は UnityWebRequest（Android は jar: URL）なので非同期。
            // registry の Awake（stream 生成）が済んだ後に、この初期化列を回す。
            _ = RunInitAsync();
        }

        // ---- タブレット状態 API（0185 / 0187）----
        private string _deviceId = "";
        private string _deviceModel = "";
        private TitleScreen? _titleForHb;   // heartbeat の titleStage 用（遅延解決・無ければ空文字）
        private VisitorPortal? _portal;

        /// <summary>この機の端末 ID（<c>SystemInfo.deviceUniqueIdentifier</c>）。テレメトリの <c>ev=boot</c> も出す。</summary>
        public string DeviceId => _deviceId;

        /// <summary>タブレット状態 API（この機の HTTP）。テレメトリが読む。</summary>
        public VisitorPortal? Portal => _portal;

        /// <summary>
        /// タブレット状態 API をこの機の中に立てる（<c>VisitorPortal</c>・:8090）。**卓が居ても居なくても立つ** —
        /// 0187「メインのウェブ卓は経由せずにクエストとタブレットを直接つなぐ」。
        /// シーンには焼かない（SegmentRecorder と同じ流儀。<c>menu scene</c> の焼き直しが要らない）。
        /// </summary>
        private void EnsureVisitorPortal()
        {
            if (_portal != null || Replay.XpReplay.Active) return;   // 再生はタブレットを待たない
            _portal = FindObjectOfType<VisitorPortal>();
            if (_portal != null) return;
            var go = new GameObject("[VisitorPortal]");
            _portal = go.AddComponent<VisitorPortal>();
        }

        private async Task RunInitAsync()
        {
            try { await InitializeAsync(destroyCancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 初期化失敗: {e.Message}"); }
        }

        /// <summary>
        /// 初期化の 1 工程。**例外をここで止める**（隣の工程へ波及させない）。
        ///
        /// ⚠⚠ 旧実装は 9 工程を 1 個の try で囲んでいたので、**途中の 1 つが投げると
        /// それ以降が永久に走らなかった**（2026-08-30 に分離）。とくに <c>PushCourseAndSchedule</c> が
        /// 飛ぶと <c>LapCounter</c> へ <c>course.order</c> が届かず、**周回が 1 も進まない
        /// ＝ 体験が終わらない**。卓が居れば long-poll の <c>Apply</c> が上書き復旧するが、
        /// **PC 不在の現場では復旧経路がゼロ**。工程は互いに独立なので、1 つ落ちても残りは意味を持つ。
        ///
        /// 失敗は <c>LogError</c>（<c>LogWarning</c> ではない）。走行レポートの「実機ログの警告」節が
        /// エラーを FAIL として拾うので、**気づけない初期化の片落ち**を作らない。
        /// </summary>
        private void InitStep(string name, Action body)
        {
            try { body(); }
            catch (Exception e) { Debug.LogError($"[ShowControl] 初期化の「{name}」で失敗（他の工程は続けます）: {e}"); }
        }

        private async Task InitStepAsync(string name, Func<Task> body)
        {
            try { await body(); }
            catch (OperationCanceledException) { throw; }   // 破棄・無効化はここで握らない
            catch (Exception e) { Debug.LogError($"[ShowControl] 初期化の「{name}」で失敗（他の工程は続けます）: {e}"); }
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            // ⚠ 工程ごとに独立して落とす（機序は InitStep の doc）。順序は変えていない。
            // 1) 焼き込み StreamingAssets/show/show.json（最下位）。無ければ何もしない。
            await InitStepAsync("焼き込みの読込", () => LoadBakedShowAsync(ct));
            // 2) 端末キャッシュ（焼き込みを上書き）。ライブが既に適用済みなら両方スキップ（ライブ優先）。
            if (!BakedOnly)
                InitStep("端末キャッシュの適用", () => { if (_rev < 0) LoadAndApplyCache(); });
            // 2.5) 録画係。**卓が居なくても録れなければならない**（1 周目を録って 3 周目に流すのは
            //      現地 PC 不在でも成立する体験）。旧実装はライブ受信の Apply でしか EnsureRecorder を
            //      呼んでおらず、焼き込み / 端末キャッシュの record.enabled は読むだけで録画係が
            //      生成されず、Quest 単体では 1 フレームも録れなかった（2026-07-29 修正）。
            InitStep("録画係の生成", EnsureRecorder);
            // 2.6) 体験の骨格。録画係と同じ理由で**卓が居なくても成立させる**（3 周で終わることは
            //      現地 PC 不在でも体験の一部）。相は Intro から始まる（起動＝導入）。
            InitStep("骨格の設定", () => ResolveRunDirector()?.Configure(_run));
            InitStep("撮像の質", PushFeel);
            // 3) 統合後の接続先・post を一度反映（焼き込み/キャッシュのどちらが勝っても 1 回）。
            InitStep("カメラの接続先", ApplyCameraEndpoints);
            InitStep("画像加工", ApplyPostForActive);
            // 3.5) カメラ切替タイミング（焼き込み / キャッシュ由来。未指定なら Director がコード既定へ戻す）。
            InitStep("切替タイミング", ApplySwitchTiming);
            // 3.6) BGM。トラック表 + ラン既定を供給して再生を開始する（show.json に bgm 指定が
            //      無ければ BgmDirector の既定クリップ = 従来の固定ループがそのまま鳴る）。
            InitStep("BGM", () =>
            {
                var bgm = ResolveBgmDirector();
                if (bgm != null)
                {
                    bgm.SetServer(server);
                    PushBgm();
                    bgm.Begin();
                }
            });
            // 4) 周回順・スケジュールを LapCounter / CueScheduler へ供給。
            //    ⚠ **ここが飛ぶと体験が終わらない**（course.order が届かず周回が進まない）。
            InitStep("周回順・演出表", PushCourseAndSchedule);
            // 5) layout / course が入っていれば通知（ZoneLayoutApplier / LapCounter が再取得）。
            InitStep("layout / course の通知", () =>
            {
                if (_layout != null)
                {
                    LayoutChanged?.Invoke();
                    CourseChanged?.Invoke();
                }
            });
        }

        private void OnDestroy()
        {
            if (registry != null && _subscribed)
            {
                registry.ActiveChanged -= OnActiveCameraChanged;
                _subscribed = false;
            }
        }

        // enable 期間だけ生きる CTS。destroy token と束ねて disable / destroy 両方で止める。
        // これが無いと disable→enable のたびに long-poll / heartbeat が多重起動する。
        private CancellationTokenSource? _loopCts;

        private void OnEnable()
        {
            // 実測滞在時間の計時はサーバの有無と無関係に回す（卓が後から立ち上がっても
            // 直近の滞在を送れるように送信待ちへ溜めておく。上限 64 で古い方から捨てる）。
            SubscribeDwell();

            // server 未設定でも component は生かす（端末キャッシュ適用・カメラ別 post の
            // ゾーン切替連動は server なしで成立する）。long-poll / heartbeat だけスキップ。
            if (server == null)
            {
                Debug.Log("[ShowControl] server 未設定。オペレータ卓なしで続行（キャッシュ設定のみ適用）。");
                return;
            }
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            if (!BakedOnly) _ = PollLoopAsync(_loopCts.Token);
            if (!Replay.XpReplay.Active) _ = HeartbeatLoopAsync(_loopCts.Token);
        }

        private void OnDisable()
        {
            UnsubscribeDwell();
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
        }

        private void OnActiveCameraChanged(int _) => ApplyPostForActive();

        // カメラ override を出どころ Override として適用する（LapCounter が周回に数えないため）。
        // director があればそこを通し、無ければ従来どおり registry を直接叩く（後方互換）。
        private void SetActiveOverride(int index)
        {
            var dir = ResolveSwitchDirector();
            if (dir != null)
                dir.SetActiveExternal(index, CameraSwitchDirector.SwitchSource.Override);
            else
                registry?.SetActive(index);
        }

        // switchDirector 参照を遅延解決する。既存シーンの YAML に SerializeField 参照が焼かれていなくても
        // （シーン再生成前のビルドで動く）タイミング現場調整・Override 経路を成立させるためのフォールバック。
        // ShowControlClient は Screen GameObject（Director と同居）に付くため GetComponent が第一選択。
        private CameraSwitchDirector? ResolveSwitchDirector()
        {
            if (switchDirector != null) return switchDirector;

            switchDirector = GetComponent<CameraSwitchDirector>();
            if (switchDirector != null) return switchDirector;

            // シーン全体からの拾い上げは最後の手段。**自分と違う registry を回している Director は採らない**
            // （別リグ・EditMode テストの偽 registry と本物のシーンが混ざると、切替が別のカメラ群へ飛ぶ。
            //   2026-07-27 実害: Main.unity を開いたまま EditMode を回すと override のテストが赤くなった）。
            CameraSwitchDirector? found = FindObjectOfType<CameraSwitchDirector>();
            if (found != null && (registry == null || found.Registry == null || found.Registry == registry))
                switchDirector = found;
            return switchDirector;
        }

        // control 由来のカメラ切替タイミングを Director へ流す（present 判定は Director 側の ResolveTiming）。
        private void ApplySwitchTiming()
            => ResolveSwitchDirector()?.ApplyTimingOverride(_switchDwellSec, _switchCooldownSec);

        // 骨格の実行体。既存シーンに未配置でも自分で載せる（prefab / シーンの SerializeField 欠落で
        // 機能が全死した過去の事故対策。EnsureRecorder と同流儀）。
        private ShowRunDirector? _runDirector;

        /// <summary>
        /// 導入演出の実行体。**居なければ null を返すだけで自動生成はしない** — 導入は
        /// シーンに焼かれた覆い・線・パススルー面と組でしか成立しないので、
        /// コンポーネントだけ湧かせても何も見えない（`Build Main Scene` の再実行が要る）。
        /// </summary>
        private IntroDirector? _introDirector;
        private IntroDirector? ResolveIntroDirector()
        {
            if (_introDirector != null) return _introDirector;
            _introDirector = FindObjectOfType<IntroDirector>();
            return _introDirector;
        }

        private ShowRunDirector? ResolveRunDirector()
        {
            if (_runDirector != null) return _runDirector;
            _runDirector = FindObjectOfType<ShowRunDirector>();
            if (_runDirector == null)
            {
                var go = new GameObject("[ShowRun]");
                _runDirector = go.AddComponent<ShowRunDirector>();
                _runDirector.Configure(_run);
                Debug.Log("[ShowControl] 体験の骨格（ShowRunDirector）をシーンへ自動生成した");
            }
            return _runDirector;
        }

        /// <summary>体験の骨格（相・経過・周数）。卓の heartbeat と HMD 表示が読む。</summary>
        public ShowRunDirector? RunDirector => ResolveRunDirector();

        // 既存シーンで未配線でも動くよう遅延解決する（BgmDirector と同流儀）。
        private Recording.SegmentRecorder? _recorder;

        private Recording.SegmentRecorder? ResolveRecorder()
        {
            if (_recorder != null) return _recorder;
            _recorder = FindObjectOfType<Recording.SegmentRecorder>();
            return _recorder;
        }

        /// <summary>
        /// show.json の <c>record.enabled</c> が立っていて録画係がシーンに居なければ自分で載せる。
        /// prefab / シーンの SerializeField 欠落で機能が全死した過去の事故を繰り返さないための自己修復
        /// （<see cref="TimelineDirector"/> が <see cref="TakeRunner"/> を載せるのと同じ流儀）。
        /// </summary>
        private void EnsureRecorder()
        {
            if (_record == null || !_record.enabled) return;
            if (ResolveRecorder() != null) return;
            var go = new GameObject("[SegmentRecorder]");
            _recorder = go.AddComponent<Recording.SegmentRecorder>();
            _recorder.ResetRun(_knownRunEpoch);
            Debug.Log("[ShowControl] 端末内録画が有効なので SegmentRecorder を自動生成した");
        }

        // ---- BGM（bgmTracks / ラン既定 / 区間指示は TimelineDirector 経由）----

        // 既存シーンで bgmDirector が未配線でも動くよう遅延解決する（post/switch の ResolveXxx と同流儀）。
        private BgmDirector? ResolveBgmDirector()
        {
            if (bgmDirector != null) return bgmDirector;
            bgmDirector = FindObjectOfType<BgmDirector>();
            return bgmDirector;
        }

        // トラック表と既定 BGM を BgmDirector へ流す。既定は「実際に変わった時だけ」適用する
        // （show.json の rev はカメラ設定の変更等でも上がるため、毎回適用すると曲が鳴り直す）。
        private string _appliedBgmSignature = "\u0000";
        private void PushBgm()
        {
            var dir = ResolveBgmDirector();
            if (dir == null) return;
            // ⚠ 接続先を先に渡す。`SetTracks` は即座に先読みを起こすので、ここが後だと
            //   相対 URL（/audio/*.mp3）を解決できないまま取りに行く（2026-08-31 実測）。
            dir.SetServer(server);
            dir.SetTracks(_bgmTracks);
            string sig = _bgmDefault == null ? ""
                : $"{_bgmDefault.action}|{_bgmDefault.trackId}|{_bgmDefault.loop}|{_bgmDefault.startSec}|"
                  + $"{_bgmDefault.loopStartSec}|{_bgmDefault.loopEndSec}|{_bgmDefault.volume}";
            if (sig == _appliedBgmSignature) return;
            _appliedBgmSignature = sig;
            dir.SetShowDefault(_bgmDefault, _bgmDefault != null);
        }

        // ---- コントローラ等からのローカル発火（演出トグル）----

        [Serializable] private class CommandMsg { public string type = ""; public string id = ""; }

        /// <summary>
        /// グリップ単押しの緊急復帰トグル。**何か再生中なら無条件で停止**し、何も再生していない時だけ
        /// 今アクティブなカメラの cue（id = cue_&lt;camId&gt;）を発火する。
        ///
        /// 停止を「cue_&lt;camId&gt; 一致」に依存させない理由: スケジューラ発火の別 id cue（cue_A_1 等）が
        /// 表示中だと固定一致では stop 側に入らず、黒/演出を止められない穴があった。緊急復帰の役目を果たすため、
        /// 再生中判定（<see cref="ScreenOverlayController.Current"/> が非 null）を最優先の無条件停止にする。
        ///
        /// - server 接続中: show.json を唯一の正に保つため、再生は /command（playCue）をサーバへ送る
        ///   → 自分の long-poll が即座に戻り Apply が実再生する（web UI 表示・heartbeat とも整合）。
        ///   **停止は server へ "stopCue"（空 id）を送りつつ、ローカルでも必ず StopOverlay を併用する**。
        ///   スケジューラ発火 cue は server の activeCue が空のままなので、stopCue 送信だけでは Apply の
        ///   遷移判定（cueId != _appliedCue）が起きずローカル再生が止まらない穴があった（緊急停止の役目を果たすため
        ///   ローカル停止を必ず走らせる。コマンド送信は Web 表示との整合維持のため残す）。
        /// - server 未設定 / 不通: 焼き込み・端末キャッシュの cue 定義から <see cref="ResolveCue"/> して
        ///   ScreenOverlayController を**ローカル直呼び**する（PC 不在では /command が届かず発火できない既知の穴を塞ぐ）。
        ///   CueScheduler が引くのと同じ _cues プールなので焼き込み cue はそのまま鳴る。
        /// </summary>
        public void ToggleActiveCameraCue()
        {
            if (registry == null) return;
            int idx = registry.ActiveIndex;
            if (idx < 0) return;

            // 最優先: 何か再生中なら無条件停止（緊急復帰）。id 一致に依存しない。
            bool anythingPlaying = _overlay != null && _overlay.Current != null;
            if (anythingPlaying)
            {
                if (ServerReachable)
                {
                    // server へ stopCue を送って Web 表示・heartbeat と整合させつつ、ローカルでも即停止する。
                    // スケジューラ発火 cue は server の activeCue が空のままで Apply の遷移判定が起きないため、
                    // stopCue 送信だけでは止まらない。ローカル StopOverlay を必ず併用する（緊急復帰の担保）。
                    SendCommand("stopCue", "");
                    _overlay!.StopOverlay();
                    Debug.Log("[ShowControl] grip cue toggle: 再生中 -> stop（server + ローカル併用・無条件）");
                }
                else
                {
                    _overlay!.StopOverlay();
                    Debug.Log("[ShowControl] grip cue toggle (local): 再生中 -> stop（無条件）");
                }
                return;
            }

            // 何も再生していない → アクティブカメラの cue を発火。
            string camId = (idx < _cameras.Length && _cameras[idx] != null && !string.IsNullOrEmpty(_cameras[idx]!.id))
                ? _cameras[idx]!.id
                : ((char)('A' + idx)).ToString(); // long-poll 前のフォールバック（show.json は A/B/C 順）
            string cueId = $"cue_{camId}";

            if (ServerReachable)
            {
                SendCommand("playCue", cueId);
                Debug.Log($"[ShowControl] grip cue toggle: cam={camId} -> {cueId}");
                return;
            }

            // ローカルフォールバック（server 未設定 / 不通）。
            if (_overlay == null)
            {
                Debug.LogWarning("[ShowControl] grip cue toggle: ScreenOverlayController 不在のためローカル発火不可");
                return;
            }
            OverlayCueData? data = ResolveCue(cueId);
            if (data == null)
            {
                Debug.LogWarning($"[ShowControl] grip cue toggle (local): cue 未定義 {cueId}" +
                                 "（焼き込み/端末キャッシュに cues があるか確認）");
                return;
            }
            _overlay.PlayCue(data);
            Debug.Log($"[ShowControl] grip cue toggle (local): cam={camId} -> {cueId}");
        }

        // ---- カメラ設定 / 画像加工 の適用 ----

        /// <summary>cameras[i].host/port/auth を registry の各 stream へ実行時上書きする（変化時のみ再接続）。</summary>
        private void ApplyCameraEndpoints()
        {
            if (registry == null || _cameras.Length == 0) return;
            int n = Mathf.Min(_cameras.Length, registry.SourceCount);
            for (int i = 0; i < n; i++)
            {
                var c = _cameras[i];
                if (c == null) continue;
                SplitAuth(c.auth, out string user, out string pass);
                registry.ApplyEndpoint(i, c.host, c.port, user, pass);
            }
        }

        /// <summary>
        /// タイムライン区間の post 上書き層を設定する（TimelineDirector 用）。
        /// null で解除するとアクティブカメラ post → global へフォールバックする。
        /// </summary>
        public void SetPostOverride(PostParams? p)
        {
            _segmentPostOverride = p;
            ApplyPostForActive();
        }

        /// <summary>
        /// インサート表示中の post 層を設定する（InsertController 用）。active 中は segment 層より優先する。
        /// p が null なら「インサート先カメラの post / global」へフォールバックする（segment 層は素通ししない）。
        /// </summary>
        public void SetInsertPostOverride(bool active, PostParams? p)
        {
            _insertPostActive = active;
            _insertPostOverride = p;
            ApplyPostForActive();
        }

        /// <summary>
        /// アクティブカメラの映像へ適用する post を解決してマテリアルへ書く。
        /// 段階（上ほど優先）:
        ///   1. インサート層（_insertPostActive 中）: insert.post ?? アクティブ(=insert)カメラ post ?? global
        ///   2. 区間層（_segmentPostOverride）: 区間 post ?? アクティブカメラ post ?? global
        ///   3. アクティブカメラ個別 post（cameras[i].post）
        ///   4. global post
        /// インサート層と区間層は排他（インサート中は区間層を素通りせず insert 側で解決する）。
        /// </summary>
        /// <summary>
        /// いま画面へ書いた post（テレメトリ・診断用）。null = 一度も適用していない。
        /// <see cref="HasScreenMaterial"/> が false なら **画像加工は 1 つも効いていない**
        /// （ShowControlClient がスクリーンの Renderer と同じ GameObject に無い）。
        /// </summary>
        public PostParams? AppliedPost { get; private set; }

        /// <summary>post を書き込む material を解決できているか。false なら画像加工が丸ごと不発。</summary>
        public bool HasScreenMaterial => _material != null;

        /// <summary>
        /// HMD を実際に被っているか（<c>OVRManager.isUserPresent</c>）。
        /// Streaming asmdef は OVR を参照しない規約なので、Assembly-CSharp 側の
        /// <c>OvrControllerBridge</c> が実行時に差し込む。未設定なら true（被っている扱い）＝
        /// Editor / Standalone の検証では従来どおり動く。
        ///
        /// 導入演出は**被った状態でだけ始める**（机に置いた HMD が位置条件をたまたま満たして
        /// 勝手に始まり、体験者が被ったときには終わっている、という事故を防ぐ）。
        /// </summary>
        public Func<bool>? UserPresentProvider;

        /// <summary>
        /// パススルーを<b>アプリから実際に有効化できているか</b>（<c>-1</c>=判定不能 / <c>0</c>=無効 / <c>1</c>=有効）。
        /// <see cref="UserPresentProvider"/> と同じ理由で Assembly-CSharp 側の
        /// <c>PassthroughStyler</c> が実行時に差し込む（Streaming asmdef は OVR を参照しない規約）。
        ///
        /// 導入演出は主役が現実の映像なので、<c>OculusProjectConfig</c> の
        /// <c>_insightPassthroughSupport</c> が 0 だと <c>Failure_NotInitialized</c> で
        /// <b>演出が丸ごと死ぬ</b>。それでも段は進むのでテレメトリの遷移からは気づけない
        /// （2026-07-31 実害）。ここが「効果の実在」を出す口。
        /// </summary>
        public Func<int>? PassthroughStateProvider;

        /// <summary>
        /// <b>位置合わせ（登録モード）がいま動いているか。</b> Tracking asmdef を参照しない規約なので、
        /// 両方を知っている Assembly-CSharp 側の <c>OvrControllerBridge</c> が差し込む。
        /// 未設定なら false（＝従来どおり）。
        ///
        /// 位置合わせは**現実に線を重ねる作業**なので、動いている間は「現実を隠すもの」を全部どける:
        /// <list type="bullet">
        ///   <item>導入演出（<c>IntroDirector</c>）— 覆いは queue 4900 の乗算なので、
        ///         走っていると重ねる対象の線と文字（queue 3000）を黒へ潰す</item>
        ///   <item>中止・終了の黒（<c>ShowRunDirector.ShouldBlackout</c>）— 中止の唯一の直し方が
        ///         再登録なので、黒が居座ると**直す作業が黒に隠れて詰む**</item>
        ///   <item>パススルーの電源（<c>PassthroughStyler</c>）— 演出の外では切られていた</item>
        /// </list>
        /// これを起こせるのはコントローラを持っている人だけ（体験者は持たない運用）なので、
        /// 体験の最中に勝手に立つことはない。
        /// </summary>
        public Func<bool>? CourseRegistrationActiveProvider;

        /// <summary>位置合わせが動作中か（未配線なら false）。</summary>
        public bool CourseRegistrationActive => CourseRegistrationActiveProvider?.Invoke() ?? false;

        /// <summary>
        /// <b>人が「体験を始めてよい」と言ったか。</b> 実体は「タイトルが画面を手放したか」
        /// （＝ スタッフが A を押して題字が焼け切った）で、差し込むのは <c>OvrControllerBridge</c>。
        ///
        /// ⚠⚠ <b>これを <see cref="UserPresentProvider"/> に混ぜてはいけない</b>（2026-08-14 に分けた）。
        /// 旧実装は「被っている ＆ タイトルが立っていない」を 1 つの provider で表していたが、
        /// 自動走行（<c>ShowWalkDebugDriver</c>）が<b>被り検知だけを無効化する目的で全体を true に
        /// 上書きする</b>ので、走行ではタイトルが立ったまま導入が始まって題字が飛んでいた
        /// （＝ タイトルは自動走行で 1 度も検証されていなかった）。
        ///
        /// ⚠ <b>未配線なら true</b>（Editor・テスト・タイトルを持たない構成で体験が止まらない）。
        /// タイトルの実体を組めない現場でも <c>TitleScreen.IsBlocking</c> が false になるので、
        /// ここが false のまま居座ることは無い（「タイトルが壊れると二度と始まらない」を作らない）。
        /// </summary>
        public Func<bool>? StartAuthorizedProvider;
        public Func<bool>? StaffSetupActiveProvider;
        public bool StaffSetupActive => StaffSetupActiveProvider?.Invoke() ?? false;

        /// <summary>人が始めてよいと言ったか（未配線なら true ＝ 止めない）。</summary>
        public bool StartAuthorized => StartAuthorizedProvider?.Invoke() ?? true;

        /// <summary>
        /// <b>スタッフのステータス表示（右 B）が出ているか。</b> Diagnostics asmdef を参照しない規約なので、
        /// 両方を知っている <c>OvrControllerBridge</c> が差し込む。未配線なら false。
        ///
        /// ⚠ <b>2026-09-14 から読むのは heartbeat（<c>statusHud</c>）だけ。</b> それまでは
        /// タイトルに黒を譲らせるためのものだったが、面が黒の上に描かれるようになって譲りは廃止した
        /// （譲っているあいだ体験者の開始押しが黙って無視されていた）。
        /// </summary>
        public Func<bool>? StatusVisibleProvider;

        /// <summary>スタッフがステータスを開いているか（未配線なら false）。</summary>
        public bool StatusVisible => StatusVisibleProvider?.Invoke() ?? false;

        private void ApplyPostForActive()
        {
            if (_material == null) return;
            int idx = registry != null ? registry.ActiveIndex : -1;
            // ベース = アクティブカメラ個別 post（あれば）→ 無ければ global。
            PostParams basePost = _globalPost;
            if (idx >= 0 && idx < _cameras.Length && _cameras[idx] != null
                && _cameras[idx]!.hasPost && _cameras[idx]!.post != null)
                basePost = _cameras[idx]!.post!;
            // 上書き層: インサート中は insert 層（未指定ならベース）、そうでなければ区間層（未指定ならベース）。
            PostParams p = _insertPostActive
                ? (_insertPostOverride ?? basePost)
                : (_segmentPostOverride ?? basePost);
            _material.SetFloat(ExposureId, p.exposure);
            _material.SetFloat(ContrastId, p.contrast);
            _material.SetFloat(SaturationId, p.saturation);
            _material.SetFloat(TemperatureId, p.temperature);
            _material.SetFloat(VignetteId, p.vignette);
            _material.SetFloat(GrainId, p.grain);
            _material.SetFloat(ScanlineId, p.scanline);
            _material.SetFloat(LiftId, p.lift);
            _material.SetFloat(TintId, p.tint);
            _material.SetFloat(AberrationId, p.aberration);
            _material.SetFloat(PixelateId, p.pixelate);
            // 走査線の本数は「0 = 未指定」。実機 material の 240 固定と卓の canvas 高さで
            // 縞のピッチが 1.5〜2 倍食い違っていたので、値を持たせて両者を揃える。
            _material.SetFloat(ScanlineCountId, p.ResolveScanlineCount());
            AppliedPost = p;
        }

        private static void SplitAuth(string auth, out string user, out string pass)
        {
            user = ""; pass = "";
            if (string.IsNullOrEmpty(auth)) return;
            int i = auth.IndexOf(':');
            if (i < 0) { user = auth; return; }
            user = auth.Substring(0, i);
            pass = auth.Substring(i + 1);
        }
    }
}
