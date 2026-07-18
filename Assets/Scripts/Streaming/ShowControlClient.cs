#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    // ---- show.json layout（ゾーン形状・カメラ割当のデータ）----
    // Tracking 側（ZoneLayoutApplier）が読むため public。Streaming → Tracking の参照は作らない
    // （このデータは純データで Tracking の型を持ち込まない）。JsonUtility でパースする。

    /// <summary>course space のフロア寸法 (m)。</summary>
    [Serializable] public sealed class ShowFloorDef { public float w = 1.8f; public float d = 1.8f; }

    /// <summary>L 字壁の記述（描画・登録基準点導出用。Solver は使わない）。</summary>
    [Serializable] public sealed class ShowWallDef
    {
        public float[] corner = System.Array.Empty<float>();
        public float[] endX = System.Array.Empty<float>();
        public float[] endZ = System.Array.Empty<float>();
    }

    /// <summary>ループ上の切れ目。s ∈ [0,1)、camAfter = このカット以降のカメラ index。</summary>
    [Serializable] public sealed class ShowCutDef { public float s; public int camAfter; }

    /// <summary>
    /// v2: タイルペイント（grid）モデル。フロアを正方タイルに切り、cells の各文字でカメラを塗る。
    /// cells は rows 本の文字列。row 0 = 北端（z=+d/2 側）、col 0 = 西端（x=-w/2）。
    /// 文字 '0'..'8' = カメラ index、'.' = 未割当。JsonUtility は string[] をパースできる。
    /// </summary>
    [Serializable] public sealed class ShowGridDef
    {
        public float tileM = 0.15f;
        public int cols;
        public int rows;
        public string[] cells = System.Array.Empty<string>();

        /// <summary>塗られたタイルが 1 つでもあれば present（cells に非空文字列が 1 行でもあるか）。</summary>
        public bool HasData()
        {
            if (cells == null || cols <= 0 || rows <= 0 || tileM <= 0f) return false;
            foreach (var row in cells) if (!string.IsNullOrEmpty(row)) return true;
            return false;
        }
    }

    /// <summary>
    /// 周回定義（フロアマップ UI で編集・layout と同じ保存単位）。
    /// order = 順方向のカメラ巡回順（カメラ index）。order[0] = スタート領域のカメラ。
    /// LapCounter がこの順で周回を数える（順方向一致でのみ前進）。
    /// </summary>
    [Serializable] public sealed class ShowCourseDef
    {
        public int[] order = System.Array.Empty<int>();

        /// <summary>2 カメラ以上の巡回順があれば present。</summary>
        public bool HasData() => order != null && order.Length > 0;
    }

    /// <summary>show.json の layout セクション。cuts / grid のどちらも無ければ「layout 未設定」として扱う。</summary>
    [Serializable] public sealed class ShowLayoutDef
    {
        public int rev;
        public ShowFloorDef? floor;
        public ShowWallDef? wall;
        public ShowCutDef[] cuts = System.Array.Empty<ShowCutDef>();
        public ShowGridDef? grid;   // v2: grid があれば grid 優先（cuts は後方互換）
        public ShowCourseDef? course;   // 周回定義（LapCounter が読む。ゾーン生成には使わない）
        public float overlapM = 0.08f;
        public float hysteresisM = 0.12f;

        /// <summary>ゾーン生成に使える layout データ（grid か cuts）を持つか。grid 優先の判定は Applier 側。</summary>
        public bool HasData()
            => (grid != null && grid.HasData()) || (cuts != null && cuts.Length > 0);
    }

    /// <summary>事前オーサリング済みスケジュール 1 行。camera はカメラ index。lap は 1 始まり。</summary>
    [Serializable] public sealed class ShowScheduleEntryDef
    {
        public int lap;
        public int camera;
        public string cueId = "";
        public float delaySec;      // ゾーン進入からの遅延
        public bool once = true;    // true = そのランで 1 回だけ
    }

    /// <summary>show.json の schedule セクション。rev で変更検出する。</summary>
    [Serializable] public sealed class ShowScheduleDef
    {
        public int rev;
        public ShowScheduleEntryDef[] entries = System.Array.Empty<ShowScheduleEntryDef>();

        /// <summary>1 行でもエントリがあれば present。</summary>
        public bool HasData() => entries != null && entries.Length > 0;
    }

    /// <summary>
    /// Web オペレータ卓（show.json）の状態を long-poll で受けて Unity 側へ適用するクライアント。
    /// Screen GameObject（MjpegScreen / ScreenOverlayController と同居）に付ける。
    ///
    /// 適用対象:
    ///   - cameras[].host/port/auth → 各 CameraStream の接続先を実行時上書き（DHCP ズレを Web から復旧）
    ///   - cameras[].post           → カメラ別の画像加工（明るさ等）。アクティブカメラ切替時に適用
    ///   - post.*（global）         → カメラ別 post 未設定時のフォールバック（ショー全体グレーディング）
    ///   - control.activeCue        → ScreenOverlayController.PlayCue / StopOverlay
    ///   - control.cameraOverride   → registry.SetActive + ゾーン Tracker の無効化
    /// 逆方向: /unity/heartbeat へ 2s ごとに現状（アクティブカメラ・fps・発火中 cue）を報告。
    ///
    /// 永続化（Quest 単体ビルドで PC 不在でも Web 設定を参照するため）:
    ///   受信した cameras 設定 + post を persistentDataPath/show_config.json にキャッシュし、
    ///   次回起動時に server 接続前へ適用する。優先順位は 焼き込み.asset < 端末キャッシュ < ライブ long-poll。
    ///   サーバ不在でもキャッシュ適用とカメラ別 post（ゾーン切替連動）は動き続ける。
    /// </summary>
    public sealed class ShowControlClient : MonoBehaviour
    {
        private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        private static readonly int ContrastId = Shader.PropertyToID("_Contrast");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int TemperatureId = Shader.PropertyToID("_Temperature");
        private static readonly int VignetteId = Shader.PropertyToID("_Vignette");
        private static readonly int GrainId = Shader.PropertyToID("_Grain");
        private static readonly int ScanlineId = Shader.PropertyToID("_Scanline");

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

        private ScreenOverlayController? _overlay;
        private Material? _material;
        private int _rev = -1;
        private string _appliedCue = "";
        private string _appliedOverride = "";

        // ---- 発見（discovery）連携用の公開状態 ----
        // DiscoveryClient が「PC 卓が不通か」を判定するために最後に /state 応答を得た時刻を持つ。
        // control.discoveryEnabled はキルスイッチ（false で probe/切替を全停止。省略時 true）。
        private float _lastServerContactTime = -999f;

        /// <summary>最後に /state を成功受信した realtimeSinceStartup。未接続なら大きな負値。</summary>
        public float LastServerContactTime => _lastServerContactTime;

        // コントローラ操作モード（RUN/STAFF/REG）。OvrControllerBridge が遷移時に push し、heartbeat に載せる。
        private string _controllerMode = "RUN";

        // server 未設定、または /state を ServerStaleSeconds 受信できていない = 不通と見なす。
        // long-poll 上限(35s)より長くとり、健全な idle 接続を誤って不通判定しない。
        private const float ServerStaleSeconds = 40f;

        /// <summary>server が実効的に通じているか（cue 試射のローカルフォールバック分岐に使う）。</summary>
        private bool ServerReachable
            => server != null && (Time.realtimeSinceStartup - _lastServerContactTime) < ServerStaleSeconds;

        /// <summary>コントローラ操作モードのラベル（RUN/STAFF/REG）を設定する。heartbeat で卓へ報告する。</summary>
        public void SetControllerMode(string mode) => _controllerMode = string.IsNullOrEmpty(mode) ? "RUN" : mode;

        /// <summary>卓サーバの接続先設定（null なら卓連携なし）。DiscoveryClient が現エンドポイント比較に読む。</summary>
        public ShowServerSource? Server => server;

        /// <summary>show.json control.discoveryEnabled（省略時 true）。DiscoveryClient のキルスイッチ上書き。</summary>
        public bool DiscoveryEnabled { get; private set; } = true;

        /// <summary>index 番カメラが卓で手動固定（pinned）されているか。pinned には discovery を適用しない。</summary>
        public bool IsCameraPinned(int index)
            => index >= 0 && index < _cameras.Length && _cameras[index] != null && _cameras[index]!.pinned;

        // 直近に解決したカメラ別設定 / global post（ライブ or 端末キャッシュ由来）。
        // ゾーン自律切替（ActiveChanged）でカメラ別 post を再適用するため保持する。
        private CameraDef[] _cameras = Array.Empty<CameraDef>();
        private PostParams _globalPost = new PostParams();
        private bool _subscribed;

        // ゾーン layout（ライブ or 端末キャッシュ由来）。Tracking 側（ZoneLayoutApplier）が読む。
        private ShowLayoutDef? _layout;
        private int _appliedLayoutRev = -1;

        // cue 定義（scheduler の cue 解決 + 手動 activeCue 発火の両方が引く）。
        private CueDef[] _cues = Array.Empty<CueDef>();
        // 事前オーサリング済みスケジュール（ライブ or 端末キャッシュ由来）。CueScheduler へ供給。
        private ShowScheduleDef? _schedule;
        private int _appliedScheduleRev = -1;

        /// <summary>現在の layout（未設定なら null）。ZoneLayoutApplier が Rebuild で参照する。</summary>
        public ShowLayoutDef? Layout => _layout;

        /// <summary>layout（cuts/floor/overlap 等）が変わった時に発火する。</summary>
        public event Action? LayoutChanged;

        /// <summary>周回巡回順（layout.course.order）。未設定なら空配列。LapCounter が読む。</summary>
        public int[] CourseOrder
            => _layout != null && _layout.course != null && _layout.course.order != null
                ? _layout.course.order
                : Array.Empty<int>();

        /// <summary>course（周回巡回順）が変わった時に発火する。LapCounter が購読して order を再取得する。</summary>
        public event Action? CourseChanged;

        // heartbeat に載せる HMD の course space XZ・現在ゾーンラベルの供給元。
        // Streaming → Tracking の参照を作らないため Func で注入する（ZoneLayoutApplier が設定）。
        /// <summary>HMD 位置を course space XZ で返す供給元（null なら heartbeat に載せない）。</summary>
        public Func<Vector2>? HeadCourseXZProvider;
        /// <summary>現在ゾーンのラベルを返す供給元（null なら空文字）。</summary>
        public Func<string>? CurrentZoneLabelProvider;

        private string ConfigCachePath => Path.Combine(Application.persistentDataPath, configCacheFileName);

        [Serializable] private class ShowState
        {
            public int rev;
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public CueDef[] cues = Array.Empty<CueDef>();
            public PostParams? post;
            public ControlState? control;
            public ShowLayoutDef? layout;
            public ShowScheduleDef? schedule;
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
        }
        [Serializable] private class PostParams
        {
            public float exposure;
            public float contrast = 1f;
            public float saturation = 1f;
            public float temperature;
            public float vignette;
            public float grain;
            public float scanline;
        }
        // discoveryEnabled は「省略時 true」を守るため C# 初期化子で true にする
        // （JsonUtility.FromJson は既定コンストラクタで初期化子を走らせてから present なキーだけ上書きするため、
        //  JSON にキーが無ければ true が残る。control ブロックごと無い場合も呼び出し側が true 扱いにする）。
        [Serializable] private class ControlState
        {
            public string? activeCue;
            public string? cameraOverride;
            public bool discoveryEnabled = true;
        }

        private void Awake()
        {
            _overlay = GetComponent<ScreenOverlayController>();
            var renderer = GetComponent<Renderer>();
            _material = renderer != null ? renderer.material : null;

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
            // 優先順位: 焼き込み StreamingAssets < 端末キャッシュ < ライブ long-poll（後勝ち）。
            // 焼き込みの読込は UnityWebRequest（Android は jar: URL）なので非同期。
            // registry の Awake（stream 生成）が済んだ後に、この初期化列を回す。
            _ = RunInitAsync();
        }

        private async Task RunInitAsync()
        {
            try { await InitializeAsync(destroyCancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 初期化失敗: {e.Message}"); }
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            // 1) 焼き込み StreamingAssets/show/show.json（最下位）。無ければ何もしない。
            await LoadBakedShowAsync(ct);
            // 2) 端末キャッシュ（焼き込みを上書き）。ライブが既に適用済みなら両方スキップ（ライブ優先）。
            if (_rev < 0) LoadAndApplyCache();
            // 3) 統合後の接続先・post を一度反映（焼き込み/キャッシュのどちらが勝っても 1 回）。
            ApplyCameraEndpoints();
            ApplyPostForActive();
            // 4) 周回順・スケジュールを LapCounter / CueScheduler へ供給。
            PushCourseAndSchedule();
            // 5) layout / course が入っていれば通知（ZoneLayoutApplier / LapCounter が再取得）。
            if (_layout != null)
            {
                LayoutChanged?.Invoke();
                CourseChanged?.Invoke();
            }
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
            // server 未設定でも component は生かす（端末キャッシュ適用・カメラ別 post の
            // ゾーン切替連動は server なしで成立する）。long-poll / heartbeat だけスキップ。
            if (server == null)
            {
                Debug.Log("[ShowControl] server 未設定。オペレータ卓なしで続行（キャッシュ設定のみ適用）。");
                return;
            }
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _ = PollLoopAsync(_loopCts.Token);
            _ = HeartbeatLoopAsync(_loopCts.Token);
        }

        private void OnDisable()
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
        }

        /// <summary>
        /// long-poll / heartbeat ループを掴み直す（server の接続先が発見で張り替わった時に呼ぶ）。
        /// 進行中の long-poll（最大 35s ハング）を即座に切って新エンドポイントで再起動する
        /// （URL 自体は BuildUrl が毎回 EffectiveHost を読むので次周回で反映されるが、
        ///  ハング中の request を待たせないためループごと差し替える。OnEnable と同じ _loopCts 再生成方式）。
        /// </summary>
        public void RestartServerLoop()
        {
            if (server == null) return;
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
            if (!isActiveAndEnabled) return;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _ = PollLoopAsync(_loopCts.Token);
            _ = HeartbeatLoopAsync(_loopCts.Token);
        }

        /// <summary>
        /// 発見した PC 卓（role="show-server"）の現在 IP を server へ反映しループを掴み直す。
        /// DiscoveryClient が「server 未通 N 秒」を確認した時のみ呼ぶ。接続先が実際に変わった時だけ再起動。
        /// </summary>
        public void ApplyDiscoveredServer(string host, int port)
        {
            if (server == null)
            {
                Debug.LogWarning("[ShowControl] server 未設定のため発見した show-server を適用できない（ShowServer.asset を割り当てよ）。");
                return;
            }
            string before = server.Endpoint;
            server.ApplyRuntimeEndpoint(host, port);
            if (server.Endpoint != before)
            {
                Debug.Log($"[ShowControl] 発見した show-server を適用: {host}:{port}（loop 再起動）");
                RestartServerLoop();
            }
        }

        private void OnActiveCameraChanged(int _) => ApplyPostForActive();

        // ---- state long-poll ----

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string url = server!.BuildUrl($"/state?rev={_rev}");
                    using var req = UnityWebRequest.Get(url);
                    req.timeout = 35; // サーバ側 long-poll 上限 25s より長く
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        await Task.Delay(2000, ct); // サーバ不在。静かにリトライ
                        continue;
                    }
                    // /state を成功受信した = 卓が通じている。DiscoveryClient の「不通 N 秒」判定の基準。
                    _lastServerContactTime = Time.realtimeSinceStartup;
                    var state = JsonUtility.FromJson<ShowState>(req.downloadHandler.text);
                    if (state == null)
                    {
                        // 200 で空/壊れ JSON を返し続ける異常系でホットループにしない
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    if (state.rev != _rev)
                    {
                        _rev = state.rev;
                        Apply(state);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ShowControl] poll error: {e.Message}");
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private void Apply(ShowState state)
        {
            // 1) カメラ設定（IP / 認証 / カメラ別画像加工）を反映
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            // ライブ受信パース直後だけ post の null 判定が信頼できる → ここで hasPost を確定
            foreach (var c in _cameras) if (c != null) c.hasPost = c.post != null;
            ApplyCameraEndpoints();
            if (state.post != null) _globalPost = state.post;
            ApplyPostForActive(); // global + アクティブカメラの個別 post をマテリアルへ

            // 1.3) cue 定義を保持（scheduler の cue 解決 + 手動 activeCue 発火が引く）。
            _cues = state.cues ?? Array.Empty<CueDef>();

            // 1.5) ゾーン layout（cuts/floor/overlap/course）。JsonUtility は null 入れ子を既定値で書くため
            //      「cuts が空でない」を present 判定に使い、rev で変更検出する。course は layout に内包。
            bool layoutChanged = false;
            if (state.layout != null && state.layout.HasData()
                && state.layout.rev != _appliedLayoutRev)
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
                layoutChanged = true;
            }

            // 1.6) スケジュール（rev で変更検出）→ CueScheduler へ供給。cue 解決関数は毎回張り直す
            //      （_cues の参照が更新されるため）。rev>0 を present 判定に使い、JsonUtility が
            //      schedule 欠落時に書く既定オブジェクト（rev=0）で焼き込み/キャッシュを潰さない。
            //      rev>0 なら entries 空でも適用する（オペレータの「全消去」を通す）。
            if (state.schedule != null && state.schedule.rev > 0
                && state.schedule.rev != _appliedScheduleRev)
            {
                _schedule = state.schedule;
                _appliedScheduleRev = state.schedule.rev;
                cueScheduler?.SetScheduleFromDefs(_schedule.entries);
            }
            cueScheduler?.SetCueResolver(ResolveCue);

            // 端末キャッシュへ保存（次回 PC 不在起動で参照）
            SaveCache();
            if (layoutChanged)
            {
                LayoutChanged?.Invoke();
                CourseChanged?.Invoke(); // course は layout に内包 → 同時通知
            }

            // 1.7) discovery キルスイッチ（control.discoveryEnabled、省略時 true）。
            //      DiscoveryClient がこれを AND して probe/切替を全停止できる（従来の静的 IP 運用へ縮退）。
            DiscoveryEnabled = state.control?.discoveryEnabled ?? true;

            // 2) カメラ手動 override（show.cameras の並び = registry sources の並びが前提）
            string ovr = state.control?.cameraOverride ?? "";
            if (ovr != _appliedOverride)
            {
                _appliedOverride = ovr;
                bool hasOverride = !string.IsNullOrEmpty(ovr);
                if (zoneTrackerToDisable != null) zoneTrackerToDisable.enabled = !hasOverride;
                if (hasOverride && registry != null)
                {
                    int idx = Array.FindIndex(state.cameras, c => c.id == ovr);
                    if (idx >= 0) registry.SetActive(idx);
                    else Debug.LogWarning($"[ShowControl] unknown camera id: {ovr}");
                }
            }

            // 3) cue 発火 / 停止（ライブ手動オーバーライド）。
            //    activeCue 非空の間は CueScheduler を抑止する（ライブ優先）。毎回同期する。
            string cueId = state.control?.activeCue ?? "";
            cueScheduler?.SetLiveCueActive(!string.IsNullOrEmpty(cueId));
            if (cueId != _appliedCue)
            {
                if (_overlay == null) return;
                if (string.IsNullOrEmpty(cueId))
                {
                    _appliedCue = cueId;
                    _overlay.StopOverlay();
                }
                else
                {
                    OverlayCueData? data = ResolveCue(cueId);
                    if (data == null)
                    {
                        // _appliedCue は確定しない: Web 側で cue を保存し直した後の再 poll で
                        // 同じ activeCue 文字列でも再解決できるようにする。
                        Debug.LogWarning($"[ShowControl] unknown cue id: {cueId}");
                        return;
                    }
                    _appliedCue = cueId;
                    _overlay.PlayCue(data);
                }
            }
        }

        /// <summary>
        /// cueId を _cues から OverlayCueData へ解決する（sa:// / server 相対 URL を実機で開ける URL へ）。
        /// ライブ手動発火（Apply）と CueScheduler の自動発火の両方が使う。未定義なら null。
        /// </summary>
        private OverlayCueData? ResolveCue(string cueId)
        {
            if (string.IsNullOrEmpty(cueId)) return null;
            var def = Array.Find(_cues, c => c != null && c.id == cueId);
            if (def == null) return null;
            return new OverlayCueData
            {
                id = def.id,
                displayName = string.IsNullOrEmpty(def.name) ? def.id : def.name,
                sourceUrl = ShowAssetResolver.Resolve(def.sourceUrl, server),
                maskUrl = ShowAssetResolver.Resolve(def.maskUrl, server),
                strength = def.strength,
                loop = def.loop,
                fadeInSeconds = def.fadeIn,
                fadeOutSeconds = def.fadeOut,
                trimStart = def.trimStart,
                trimEnd = def.trimEnd,
            };
        }

        // 焼き込み StreamingAssets / 端末キャッシュ由来の schedule / course を消費者へ供給する。
        private void PushCourseAndSchedule()
        {
            if (cueScheduler != null)
            {
                cueScheduler.SetScheduleFromDefs(_schedule?.entries);
                cueScheduler.SetCueResolver(ResolveCue);
                cueScheduler.SetLiveCueActive(!string.IsNullOrEmpty(_appliedCue));
            }
        }

        // ---- 焼き込み StreamingAssets/show/show.json の起動時ロード（最下位優先）----

        private async Task LoadBakedShowAsync(CancellationToken ct)
        {
            string uri = ShowAssetResolver.StreamingAssetsUri("show/show.json");
            try
            {
                using var req = UnityWebRequest.Get(uri);
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                // 焼き込みが無い（新規ビルドで export していない）のは正常。静かに続行。
                if (req.result != UnityWebRequest.Result.Success) return;
                var state = JsonUtility.FromJson<ShowState>(req.downloadHandler.text);
                if (state == null) return;
                ApplyBaked(state);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 焼き込み show.json 読込失敗: {e.Message}"); }
        }

        // 焼き込み値をフィールドへ流し込む（最下位優先。接続反映・イベント発火は InitializeAsync が一括で行う）。
        // ライブが既に適用済み（_rev>=0）なら焼き込みで上書きしない（ライブ優先）。
        private void ApplyBaked(ShowState state)
        {
            if (_rev >= 0) return;
            _cameras = state.cameras ?? Array.Empty<CameraDef>();
            foreach (var c in _cameras) if (c != null) c.hasPost = c.post != null;
            if (state.post != null) _globalPost = state.post;
            if (state.layout != null && state.layout.HasData())
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
            }
            _cues = state.cues ?? Array.Empty<CueDef>();
            if (state.schedule != null && state.schedule.HasData())
            {
                _schedule = state.schedule;
                _appliedScheduleRev = state.schedule.rev;
            }
            Debug.Log($"[ShowControl] 焼き込み show.json を適用: cameras={_cameras.Length}, " +
                      $"cues={_cues.Length}, schedule={( _schedule != null ? _schedule.entries.Length : 0)}, " +
                      $"course={( _layout?.course != null ? _layout.course.order.Length : 0)}");
        }

        // ---- コントローラ等からのローカル発火（演出トグル）----

        [Serializable] private class CommandMsg { public string type = ""; public string id = ""; }

        /// <summary>
        /// 今アクティブなカメラの cue（id = cue_&lt;camId&gt;）を ON/OFF する。コントローラのグリップ単押し等から呼ぶ。
        ///
        /// - server 接続中: show.json を唯一の正に保つため、ローカルで直接 PlayCue せず /command（playCue/stopCue）を
        ///   サーバへ送る → 自分の long-poll が即座に戻り Apply が実再生/停止する（web UI 表示・heartbeat とも整合）。
        /// - server 未設定 / 不通: 焼き込み・端末キャッシュの cue 定義から <see cref="ResolveCue"/> して
        ///   ScreenOverlayController を**ローカル直呼び**する（PC 不在では /command が届かず発火できない既知の穴を塞ぐ）。
        ///   CueScheduler が引くのと同じ _cues プールなので焼き込み cue はそのまま鳴る。
        /// </summary>
        public void ToggleActiveCameraCue()
        {
            if (registry == null) return;
            int idx = registry.ActiveIndex;
            if (idx < 0) return;
            string camId = (idx < _cameras.Length && _cameras[idx] != null && !string.IsNullOrEmpty(_cameras[idx]!.id))
                ? _cameras[idx]!.id
                : ((char)('A' + idx)).ToString(); // long-poll 前のフォールバック（show.json は A/B/C 順）
            string cueId = $"cue_{camId}";
            bool playingThis = _overlay != null && _overlay.Current != null && _overlay.Current.id == cueId;

            if (ServerReachable)
            {
                if (playingThis) SendCommand("stopCue", "");
                else SendCommand("playCue", cueId);
                Debug.Log($"[ShowControl] grip cue toggle: cam={camId} -> {(playingThis ? "stop" : cueId)}");
                return;
            }

            // ローカルフォールバック（server 未設定 / 不通）。
            if (_overlay == null)
            {
                Debug.LogWarning("[ShowControl] grip cue toggle: ScreenOverlayController 不在のためローカル発火不可");
                return;
            }
            if (playingThis)
            {
                _overlay.StopOverlay();
                Debug.Log($"[ShowControl] grip cue toggle (local): cam={camId} -> stop");
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

        private void SendCommand(string type, string id)
        {
            if (server == null) { Debug.LogWarning("[ShowControl] server 未設定: 演出はオペレータ卓接続時のみ発火可"); return; }
            _ = SendCommandAsync(type, id, destroyCancellationToken);
        }

        private async Task SendCommandAsync(string type, string id, CancellationToken ct)
        {
            try
            {
                string json = JsonUtility.ToJson(new CommandMsg { type = type, id = id });
                using var req = UnityWebRequest.Post(server!.BuildUrl("/command"), json, "application/json");
                req.timeout = 3;
                var op = req.SendWebRequest();
                while (!op.isDone) { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning($"[ShowControl] command '{type}' failed: {req.error}");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] command '{type}' error: {e.Message}"); }
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

        /// <summary>アクティブカメラの個別 post（無ければ global post）をマテリアルへ適用する。</summary>
        private void ApplyPostForActive()
        {
            if (_material == null) return;
            int idx = registry != null ? registry.ActiveIndex : -1;
            PostParams p = _globalPost;
            if (idx >= 0 && idx < _cameras.Length && _cameras[idx] != null
                && _cameras[idx]!.hasPost && _cameras[idx]!.post != null)
                p = _cameras[idx]!.post!;
            _material.SetFloat(ExposureId, p.exposure);
            _material.SetFloat(ContrastId, p.contrast);
            _material.SetFloat(SaturationId, p.saturation);
            _material.SetFloat(TemperatureId, p.temperature);
            _material.SetFloat(VignetteId, p.vignette);
            _material.SetFloat(GrainId, p.grain);
            _material.SetFloat(ScanlineId, p.scanline);
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

        // ---- 端末ローカル設定キャッシュ（PC 不在起動でも Web 設定を参照するため）----

        private void SaveCache()
        {
            try
            {
                var cfg = new CachedConfig
                {
                    cameras = _cameras,
                    post = _globalPost,
                    layout = _layout,   // course を内包
                    cues = _cues,
                    schedule = _schedule,
                };
                File.WriteAllText(ConfigCachePath, JsonUtility.ToJson(cfg));
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ保存失敗: {e.Message}"); }
        }

        // 端末キャッシュを読み、焼き込み値の上へ「データを持つ項目だけ」上書きする（空で潰さない）。
        // 接続反映・イベント発火・post 適用は呼び出し側（InitializeAsync）が一括で行う。
        private void LoadAndApplyCache()
        {
            try
            {
                if (!File.Exists(ConfigCachePath)) return;
                var cfg = JsonUtility.FromJson<CachedConfig>(File.ReadAllText(ConfigCachePath));
                if (cfg == null) return;
                if (cfg.cameras != null && cfg.cameras.Length > 0) _cameras = cfg.cameras;
                if (cfg.post != null) _globalPost = cfg.post;
                // キャッシュ済み layout も復元（grid か cuts があるもののみ / course も内包）。
                if (cfg.layout != null && cfg.layout.HasData())
                {
                    _layout = cfg.layout;
                    _appliedLayoutRev = cfg.layout.rev;
                }
                if (cfg.cues != null && cfg.cues.Length > 0) _cues = cfg.cues;
                if (cfg.schedule != null && cfg.schedule.HasData())
                {
                    _schedule = cfg.schedule;
                    _appliedScheduleRev = cfg.schedule.rev;
                }
                Debug.Log($"[ShowControl] 端末キャッシュ設定を適用: {ConfigCachePath} " +
                          $"(cameras={_cameras.Length}, layout={( _layout != null ? "yes" : "no")}, " +
                          $"cues={_cues.Length}, schedule={( _schedule != null ? _schedule.entries.Length : 0)})");
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ読込失敗: {e.Message}"); }
        }

        // ---- heartbeat ----

        [Serializable] private class Heartbeat
        {
            public string activeCamera = "";
            public int activeIndex = -1;
            public float recvFps;
            public string playingCue = "";
            public string cameraOverride = "";
            // コントローラ操作モード（RUN/STAFF/REG）。スタッフが遠隔でモードを把握するため。
            // サーバ側は未知フィールドを無視するので送るだけでよい。
            public string mode = "RUN";
            // ここまで適用した show.json の rev。UI / 自動検証が「Unity 反映済み」を機械判定する。
            public int appliedRev = -1;
            // ライブモニタ用（任意）: HMD の course space XZ と現在ゾーンラベル。
            // 供給元（ZoneLayoutApplier）未注入なら 0 / 空文字。
            public float headCourseX;
            public float headCourseZ;
            public string currentZone = "";
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var hb = new Heartbeat();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var active = registry != null ? registry.GetActive() : null;
                    hb.activeCamera = active?.DisplayName ?? "";
                    hb.activeIndex = registry != null ? registry.ActiveIndex : -1;
                    hb.recvFps = active?.ReceivedFps ?? 0f;
                    hb.playingCue = _overlay?.Current?.id ?? "";
                    hb.cameraOverride = _appliedOverride;
                    hb.mode = _controllerMode;
                    hb.appliedRev = _rev;
                    if (HeadCourseXZProvider != null)
                    {
                        Vector2 c = HeadCourseXZProvider();
                        hb.headCourseX = c.x;
                        hb.headCourseZ = c.y;
                    }
                    hb.currentZone = CurrentZoneLabelProvider != null ? CurrentZoneLabelProvider() : "";

                    string json = JsonUtility.ToJson(hb);
                    using var req = UnityWebRequest.Post(
                        server!.BuildUrl("/unity/heartbeat"), json, "application/json");
                    req.timeout = 3;
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* heartbeat はベストエフォート */ }

                try { await Task.Delay((int)(heartbeatInterval * 1000), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
