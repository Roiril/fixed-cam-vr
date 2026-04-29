#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// CameraSource の MJPEG をテクスチャに焼き、Renderer.material.mainTexture に流し込む。
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class MjpegScreen : MonoBehaviour
    {
        [SerializeField] private CameraSource? source;
        [SerializeField] private string overrideUrl = "";

        private MjpegStreamReceiver? _receiver;
        private Texture2D? _tex;
        private byte[]? _scratch;
        private Renderer? _renderer;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _renderer.material.mainTexture = _tex;
        }

        private void OnEnable()
        {
            string url = !string.IsNullOrEmpty(overrideUrl) ? overrideUrl : source?.BuildUrl() ?? "";
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogError("[MjpegScreen] No CameraSource or overrideUrl set.");
                enabled = false;
                return;
            }
            Debug.Log($"[MjpegScreen] connecting: {url}");
            _receiver = new MjpegStreamReceiver(url);
            _receiver.Start();
        }

        private void OnDisable()
        {
            _receiver?.Dispose();
            _receiver = null;
        }

        private void Update()
        {
            if (_receiver == null || _tex == null) return;
            if (_receiver.TryConsumeFrame(ref _scratch, out int len) && len > 0 && _scratch != null)
            {
                // LoadImage はサイズ自動調整。新規 Texture2D は確保しない。
                _tex.LoadImage(_scratch, markNonReadable: false);
            }
        }
    }
}
