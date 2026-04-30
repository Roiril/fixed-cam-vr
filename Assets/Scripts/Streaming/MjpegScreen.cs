#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// MJPEG をテクスチャに焼き、Renderer.material.mainTexture に流し込む。
    /// 2 モード:
    ///   - registry モード: CameraStreamRegistry のアクティブ stream の Texture を毎フレ参照（複数台切替対応）
    ///   - 単一モード（互換）: registry が未設定なら従来通り CameraSource / overrideUrl から自前で受信
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class MjpegScreen : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private CameraSource? source;
        [SerializeField] private string overrideUrl = "";

        private MjpegStreamReceiver? _receiver;
        private Texture2D? _tex;
        private byte[]? _scratch;
        private Renderer? _renderer;
        private Texture? _lastAssigned;

        private bool UseRegistry => registry != null;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            if (!UseRegistry)
            {
                _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                _renderer.material.mainTexture = _tex;
            }
        }

        private void OnEnable()
        {
            if (UseRegistry) return;

            string url = !string.IsNullOrEmpty(overrideUrl) ? overrideUrl : source?.BuildUrl() ?? "";
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogError("[MjpegScreen] No registry / CameraSource / overrideUrl set.");
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
            if (UseRegistry)
            {
                var active = registry!.GetActive();
                if (active == null || _renderer == null) return;
                var tex = active.Texture;
                if (!ReferenceEquals(tex, _lastAssigned))
                {
                    _renderer.material.mainTexture = tex;
                    _lastAssigned = tex;
                }
                return;
            }

            if (_receiver == null || _tex == null) return;
            if (_receiver.TryConsumeFrame(ref _scratch, out int len) && len > 0 && _scratch != null)
            {
                _tex.LoadImage(_scratch, markNonReadable: false);
            }
        }
    }
}
