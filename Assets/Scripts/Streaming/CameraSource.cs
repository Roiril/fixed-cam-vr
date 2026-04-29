#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    [CreateAssetMenu(fileName = "CameraSource", menuName = "FixedCamVr/Camera Source", order = 0)]
    public sealed class CameraSource : ScriptableObject
    {
        [SerializeField] private string displayName = "Phone 01";
        [SerializeField] private string host = "192.168.1.10";
        [SerializeField] private int port = 8080;
        [SerializeField] private string path = "/video";
        [SerializeField] private int width = 1280;
        [SerializeField] private int height = 720;

        public string DisplayName => displayName;
        public int Width => width;
        public int Height => height;

        public string BuildUrl()
        {
            var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith("/") ? path : "/" + path);
            return $"http://{host}:{port}{p}";
        }
    }
}
