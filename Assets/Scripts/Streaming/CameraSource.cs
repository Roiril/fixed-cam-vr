#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 配信スマホ 1 台分の接続情報。fixed-cam-streamer 既定: port=8080 / videoPath=/video / infoPath=/info。
    /// DroidCam 互換が必要な場合は port=4747, videoPath=/mjpegfeed?640x480, infoPath="" にする。
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

        [Tooltip("MJPEG フレーム想定解像度（バッファ事前確保用ヒント。実値は /info か X-Width ヘッダで上書きされる）。")]
        [SerializeField] private int width = 1280;
        [SerializeField] private int height = 720;

        public string DisplayName => displayName;
        public int Width => width;
        public int Height => height;

        public string BuildUrl() => BuildUrlWith(videoPath);
        public string BuildInfoUrl() => string.IsNullOrEmpty(infoPath) ? "" : BuildUrlWith(infoPath);
        public string BuildHealthUrl() => string.IsNullOrEmpty(healthPath) ? "" : BuildUrlWith(healthPath);

        private string BuildUrlWith(string p)
        {
            var prefixed = string.IsNullOrEmpty(p) ? "/" : (p.StartsWith("/") ? p : "/" + p);
            return $"http://{host}:{port}{prefixed}";
        }
    }
}
