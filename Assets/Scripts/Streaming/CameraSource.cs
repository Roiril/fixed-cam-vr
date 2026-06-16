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

        private string EffectiveHost => _hasOverride ? _ovrHost : host;
        private int EffectivePort => _hasOverride ? _ovrPort : port;
        private string EffectiveUser => _hasOverride ? _ovrUser : username;
        private string EffectivePass => _hasOverride ? _ovrPass : password;

        public string DisplayName => displayName;
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

        /// <summary>接続パラメータの変化検知キー（host|port|user）。再接続要否の判定に使う。</summary>
        public string ConnectionKey => $"{EffectiveHost}|{EffectivePort}|{EffectiveUser}";

        /// <summary>HTTP Basic 認証トークン（"user:pass" の Base64）。username 未設定なら null = 認証なし。</summary>
        public string? BasicAuthToken =>
            string.IsNullOrEmpty(EffectiveUser)
                ? null
                : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{EffectiveUser}:{EffectivePass}"));

        public string BuildUrl() => BuildUrlWith(videoPath);
        public string BuildInfoUrl() => string.IsNullOrEmpty(infoPath) ? "" : BuildUrlWith(infoPath);
        public string BuildHealthUrl() => string.IsNullOrEmpty(healthPath) ? "" : BuildUrlWith(healthPath);

        private string BuildUrlWith(string p)
        {
            var prefixed = string.IsNullOrEmpty(p) ? "/" : (p.StartsWith("/") ? p : "/" + p);
            return $"http://{EffectiveHost}:{EffectivePort}{prefixed}";
        }
    }
}
