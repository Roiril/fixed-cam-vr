#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 配信スマホ 1 台分の接続情報。MJPEG over HTTP を吐くサーバなら機種・アプリを問わない。
    ///
    /// プリセット例:
    /// - fixed-cam-streamer (Android 標準): port=8080 / videoPath=/video / infoPath=/info / healthPath=/health
    /// - IP Camera Lite (iPhone): port=8081 / videoPath=/video / infoPath="" / healthPath="" / username,password=admin,admin（既定）
    /// - DroidCam (Android 緊急時): port=4747 / videoPath=/mjpegfeed?640x480 / infoPath=""
    /// - IP Webcam (Android 代替): port=8080 / videoPath=/video / infoPath=""
    ///
    /// /info /health 非対応サーバではパスを空にする → 自動回転メタ・lag 検出・E2E 遅延推定が
    /// 無効化されるだけで、映像受信そのものは全機能動く。
    /// </summary>
    [CreateAssetMenu(fileName = "CameraSource", menuName = "FixedCamVr/Camera Source", order = 0)]
    public sealed class CameraSource : ScriptableObject
    {
        [SerializeField] private string displayName = "Phone 01";

        [Tooltip("端末内在カメラ ID（\"A\"/\"B\"/\"C\"）。ゾーン・post・cue の割当先スロットと同じキー。" +
                 "発見プロトコルで「この ID の現在 IP」を解決するため、配信スマホ側で刻んだ cameraId と照合する。" +
                 "host（DHCP で揺れる）と違い恒久設定なのでコミット可。空 = discovery 非対象（iPhone 等）。")]
        [SerializeField] private string cameraId = "";

        [SerializeField] private string host = "192.168.1.10";
        [SerializeField] private int port = 8080;

        [Tooltip("MJPEG 配信パス。fixed-cam-streamer は /video。DroidCam は /mjpegfeed?WxH。")]
        [SerializeField] private string videoPath = "/video";

        [Tooltip("メタデータ JSON のパス。fixed-cam-streamer は /info。空にすると問い合わせをスキップ（DroidCam 等向け）。")]
        [SerializeField] private string infoPath = "/info";

        [Tooltip("配信統計 JSON のパス。fixed-cam-streamer は /health。空にすると問い合わせをスキップ。")]
        [SerializeField] private string healthPath = "/health";

        [Header("Auth（IP Camera Lite 等 Basic 認証付きサーバのみ）")]
        [Tooltip("Basic 認証ユーザ名。空なら認証ヘッダを送らない（fixed-cam-streamer / DroidCam は空のまま）。")]
        [SerializeField] private string username = "";

        [Tooltip("Basic 認証パスワード。アプリ既定値（admin 等）以外の実パスワードを設定した asset はコミットしない。")]
        [SerializeField] private string password = "";

        [Tooltip("MJPEG フレーム想定解像度（バッファ事前確保用ヒント。実値は /info か X-Width ヘッダで上書きされる）。")]
        [SerializeField] private int width = 1280;
        [SerializeField] private int height = 720;

        // ---- 実行時オーバーライド（show.json / 端末キャッシュ由来）----------------------
        // Web オペレータ卓（show.json cameras[]）や端末ローカルキャッシュで設定した host/port/auth を
        // 「焼き込み .asset を書き換えずに」実行時だけ反映するためのフィールド。
        // [NonSerialized] なので asset に永続化されず、ドメインリロードでリセットされる
        // （= Editor の Phone*.asset が実行時操作で汚れない。git 巻き込み事故を防ぐ）。
        [NonSerialized] private bool _hasOverride;
        [NonSerialized] private string _ovrHost = "";
        [NonSerialized] private int _ovrPort;
        [NonSerialized] private string _ovrUser = "";
        [NonSerialized] private string _ovrPass = "";

        // ---- 発見（discovery）オーバーライド層 -------------------------------------------
        // DiscoveryClient が「cameraId → 現在 IP」を実時間解決し、接続断時に張り替える最上位層。
        // show.json（runtime）や焼き込み .asset より優先する（pin されたカメラには DiscoveryClient が
        // そもそも適用しない）。host/port のみ差し替え、認証は runtime/baked を維持する
        // （発見対象 = fixed-cam-streamer は認証なし。IP Camera Lite 等は cameraId 空で discovery 非対象）。
        [NonSerialized] private bool _hasDiscovery;
        [NonSerialized] private string _discHost = "";
        [NonSerialized] private int _discPort;

        // 優先順位: discovery > runtime(show.json) > baked(.asset)。認証層は discovery を通さない。
        private string EffectiveHost => _hasDiscovery ? _discHost : (_hasOverride ? _ovrHost : host);
        private int EffectivePort => _hasDiscovery ? _discPort : (_hasOverride ? _ovrPort : port);
        private string EffectiveUser => _hasOverride ? _ovrUser : username;
        private string EffectivePass => _hasOverride ? _ovrPass : password;

        /// <summary>接続先を解決している層。HUD の出所表示（baked/show/disc）に使う。</summary>
        public enum EndpointLayer { Baked, Runtime, Discovery }

        /// <summary>いま EffectiveHost/Port を決めている層（pin は DiscoveryClient 側の状態なのでここには出ない）。</summary>
        public EndpointLayer ActiveLayer =>
            _hasDiscovery ? EndpointLayer.Discovery
            : (_hasOverride ? EndpointLayer.Runtime : EndpointLayer.Baked);

        public string DisplayName => displayName;

        /// <summary>端末内在カメラ ID（"A"/"B"/"C"）。空なら discovery 非対象。</summary>
        public string CameraId => cameraId;

        /// <summary>現在の実効ホスト（HUD 表示・discovery の現エンドポイント比較用）。</summary>
        public string EffectiveHostPublic => EffectiveHost;
        /// <summary>現在の実効ポート。</summary>
        public int EffectivePortPublic => EffectivePort;

        public int Width => width;
        public int Height => height;

        /// <summary>
        /// 接続先を実行時に差し替える（show.json cameras[] / 端末キャッシュ適用）。
        /// host が空文字なら override を解除し、焼き込み .asset 値へ戻す（Web 未設定カメラの保護）。
        /// </summary>
        public void ApplyRuntimeEndpoint(string host, int port, string user, string pass)
        {
            if (string.IsNullOrEmpty(host)) { ClearRuntimeEndpoint(); return; }
            _hasOverride = true;
            _ovrHost = host;
            _ovrPort = port > 0 ? port : this.port;
            _ovrUser = user ?? "";
            _ovrPass = pass ?? "";
        }

        public void ClearRuntimeEndpoint() => _hasOverride = false;

        /// <summary>
        /// 発見プロトコルで解決した現在 IP を最上位層として適用する（DiscoveryClient から /info 照合後にのみ呼ぶ）。
        /// host が空 / port が不正なら discovery 層を解除する。認証・runtime・baked 層はそのまま。
        /// </summary>
        public void ApplyDiscoveryEndpoint(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || port <= 0) { ClearDiscoveryEndpoint(); return; }
            _hasDiscovery = true;
            _discHost = host;
            _discPort = port;
        }

        public void ClearDiscoveryEndpoint() => _hasDiscovery = false;

        /// <summary>接続パラメータの変化検知キー（host|port|user|pass 指紋）。再接続要否の判定に使う。</summary>
        public string ConnectionKey => $"{EffectiveHost}|{EffectivePort}|{EffectiveUser}|{PassKey}";

        // 生パスワードはキー文字列に載せない（ConnectionKey は public string で将来ログに出うるため、
        // プロジェクトの秘密非漏洩規約に沿い非可逆化）。空パスワードは空文字（認証なしカメラの後方互換）。
        private string PassKey => string.IsNullOrEmpty(EffectivePass) ? "" : PassFingerprint(EffectivePass);

        // 変化検知用の非可逆フィンガープリント。実行内 equality 比較のみに使うので暗号強度不要。
        // FNV-1a 32bit は決定的・依存ゼロ（string.GetHashCode は .NET Core でランダム化されうり非決定的）。
        private static string PassFingerprint(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h.ToString("x8");
        }

        /// <summary>
        /// 任意の host:port に対する /info URL を作る（discovery の切替前照合用）。
        /// infoPath 未設定（IP Camera Lite 等）なら空 = 照合不能 → 発見対象外。
        /// </summary>
        public string BuildInfoUrlFor(string host, int port)
        {
            if (string.IsNullOrEmpty(infoPath) || string.IsNullOrWhiteSpace(host)) return "";
            var prefixed = infoPath.StartsWith("/") ? infoPath : "/" + infoPath;
            return $"http://{host}:{port}{prefixed}";
        }

        /// <summary>HTTP Basic 認証トークン（"user:pass" の Base64）。username 未設定なら null = 認証なし。</summary>
        public string? BasicAuthToken =>
            string.IsNullOrEmpty(EffectiveUser)
                ? null
                : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{EffectiveUser}:{EffectivePass}"));

        public string BuildUrl() => BuildUrlWith(videoPath);
        public string BuildInfoUrl() => string.IsNullOrEmpty(infoPath) ? "" : BuildUrlWith(infoPath);
        public string BuildHealthUrl() => string.IsNullOrEmpty(healthPath) ? "" : BuildUrlWith(healthPath);

        /// <summary>
        /// <b>host が空なら空文字を返す。</b> 現場に置いていないカメラ枠（show.json で host 未設定）は
        /// 接続しないのが正しく、異常ではない。
        ///
        /// ⚠ ここを素通しにすると <c>http://:8080/info</c> という URL ができ、呼び出し側の
        /// 「空なら叩かない」ガードをすり抜ける。2026-08-02 の実機走行では毎秒 1 行の失敗警告が
        /// **359 行**出て、logcat のリングバッファの先頭（<c>ev=boot</c> / <c>ev=config</c> / 導入の記録）を
        /// 押し流した ＝ **観測が丸ごと落ちて「導入が動いていない」と誤読した**。
        /// 2026-07-31 に <c>MjpegStreamReceiver</c> 側は直したが、ここは残っていた。
        /// </summary>
        private string BuildUrlWith(string p)
        {
            if (string.IsNullOrWhiteSpace(EffectiveHost)) return "";
            var prefixed = string.IsNullOrEmpty(p) ? "/" : (p.StartsWith("/") ? p : "/" + p);
            return $"http://{EffectiveHost}:{EffectivePort}{prefixed}";
        }
    }
}
