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

    /// <summary>show.json の layout セクション。cuts が空なら「layout 未設定」として扱う。</summary>
    [Serializable] public sealed class ShowLayoutDef
    {
        public int rev;
        public ShowFloorDef? floor;
        public ShowWallDef? wall;
        public ShowCutDef[] cuts = System.Array.Empty<ShowCutDef>();
        public float overlapM = 0.08f;
        public float hysteresisM = 0.12f;
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

        private ScreenOverlayController? _overlay;
        private Material? _material;
        private int _rev = -1;
        private string _appliedCue = "";
        private string _appliedOverride = "";

        // 直近に解決したカメラ別設定 / global post（ライブ or 端末キャッシュ由来）。
        // ゾーン自律切替（ActiveChanged）でカメラ別 post を再適用するため保持する。
        private CameraDef[] _cameras = Array.Empty<CameraDef>();
        private PostParams _globalPost = new PostParams();
        private bool _subscribed;

        // ゾーン layout（ライブ or 端末キャッシュ由来）。Tracking 側（ZoneLayoutApplier）が読む。
        private ShowLayoutDef? _layout;
        private int _appliedLayoutRev = -1;

        /// <summary>現在の layout（未設定なら null）。ZoneLayoutApplier が Rebuild で参照する。</summary>
        public ShowLayoutDef? Layout => _layout;

        /// <summary>layout（cuts/floor/overlap 等）が変わった時に発火する。</summary>
        public event Action? LayoutChanged;

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
        }

        // 端末ローカルへ保存する設定キャッシュ（show.json のうち実機が参照する部分のみ）。
        [Serializable] private class CachedConfig
        {
            public CameraDef[] cameras = Array.Empty<CameraDef>();
            public PostParams? post;
            public ShowLayoutDef? layout;
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
        [Serializable] private class ControlState { public string? activeCue; public string? cameraOverride; }

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
            // registry の Awake（stream 生成）が済んだ後に、端末キャッシュの IP / 画像加工を適用する。
            // server が後で繋がればライブ値で上書きされる（後勝ち）。
            LoadAndApplyCache();
            ApplyPostForActive();
            // キャッシュ復元で layout が入った場合、ここで一度通知する
            // （ZoneLayoutApplier は Start でも直接 Layout を読むので二重適用にはならない）。
            if (_layout != null) LayoutChanged?.Invoke();
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

            // 1.5) ゾーン layout（cuts/floor/overlap）。JsonUtility は null 入れ子を既定値で書くため
            //      「cuts が空でない」を present 判定に使い、rev で変更検出する。
            bool layoutChanged = false;
            if (state.layout != null && state.layout.cuts != null && state.layout.cuts.Length > 0
                && state.layout.rev != _appliedLayoutRev)
            {
                _layout = state.layout;
                _appliedLayoutRev = state.layout.rev;
                layoutChanged = true;
            }

            // 端末キャッシュへ保存（次回 PC 不在起動で参照）
            SaveCache();
            if (layoutChanged) LayoutChanged?.Invoke();

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

            // 3) cue 発火 / 停止
            string cueId = state.control?.activeCue ?? "";
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
                    var def = Array.Find(state.cues, c => c.id == cueId);
                    if (def == null)
                    {
                        // _appliedCue は確定しない: Web 側で cue を保存し直した後の再 poll で
                        // 同じ activeCue 文字列でも再解決できるようにする。
                        Debug.LogWarning($"[ShowControl] unknown cue id: {cueId}");
                        return;
                    }
                    _appliedCue = cueId;
                    _overlay.PlayCue(new OverlayCueData
                    {
                        id = def.id,
                        displayName = string.IsNullOrEmpty(def.name) ? def.id : def.name,
                        sourceUrl = server!.Absolute(def.sourceUrl),
                        maskUrl = server.Absolute(def.maskUrl),
                        strength = def.strength,
                        loop = def.loop,
                        fadeInSeconds = def.fadeIn,
                        fadeOutSeconds = def.fadeOut,
                        trimStart = def.trimStart,
                        trimEnd = def.trimEnd,
                    });
                }
            }
        }

        // ---- コントローラ等からのローカル発火（演出トグル）----

        [Serializable] private class CommandMsg { public string type = ""; public string id = ""; }

        /// <summary>
        /// 今アクティブなカメラの cue（id = cue_&lt;camId&gt;）を ON/OFF する。コントローラのグリップ単押し等から呼ぶ。
        /// show.json を唯一の正に保つため、ローカルで直接 PlayCue せず /command（playCue/stopCue）をサーバへ送る
        /// → 自分の long-poll が即座に戻り Apply が実再生/停止する（web UI 表示・heartbeat とも整合）。
        /// サーバ未接続時は cue 定義自体が無いので発火不可（その旨をログ）。
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
            if (playingThis) SendCommand("stopCue", "");
            else SendCommand("playCue", cueId);
            Debug.Log($"[ShowControl] grip cue toggle: cam={camId} -> {(playingThis ? "stop" : cueId)}");
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
                var cfg = new CachedConfig { cameras = _cameras, post = _globalPost, layout = _layout };
                File.WriteAllText(ConfigCachePath, JsonUtility.ToJson(cfg));
            }
            catch (Exception e) { Debug.LogWarning($"[ShowControl] 設定キャッシュ保存失敗: {e.Message}"); }
        }

        private void LoadAndApplyCache()
        {
            try
            {
                if (!File.Exists(ConfigCachePath)) return;
                var cfg = JsonUtility.FromJson<CachedConfig>(File.ReadAllText(ConfigCachePath));
                if (cfg == null) return;
                _cameras = cfg.cameras ?? Array.Empty<CameraDef>();
                if (cfg.post != null) _globalPost = cfg.post;
                // キャッシュ済み layout も復元（cuts が空でないもののみ）。event は Start() 末で発火する。
                if (cfg.layout != null && cfg.layout.cuts != null && cfg.layout.cuts.Length > 0)
                {
                    _layout = cfg.layout;
                    _appliedLayoutRev = cfg.layout.rev;
                }
                ApplyCameraEndpoints(); // post はこの後 Start() の ApplyPostForActive() で当てる
                Debug.Log($"[ShowControl] 端末キャッシュ設定を適用: {ConfigCachePath} (cameras={_cameras.Length}, layout={( _layout != null ? "yes" : "no")})");
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
