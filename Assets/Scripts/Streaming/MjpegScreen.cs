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

        [Header("Auto Orient (fixed-cam-streamer)")]
        [Tooltip("/info の rotationDeg / アスペクトを使ってスクリーンを自動回転 + アスペクト補正する。")]
        [SerializeField] private bool autoOrient = true;

        [Tooltip("回転 / スケールを反映する Transform。null なら自身の Transform を使う。子に Content を置きたい場合に使う。")]
        [SerializeField] private Transform? orientTarget;

        private MjpegStreamReceiver? _receiver;
        private Texture2D? _tex;
        private byte[]? _scratch;
        private Renderer? _renderer;
        private Texture? _lastAssigned;
        private CameraStream? _lastSubscribed;
        private Quaternion _baseLocalRotation;
        private Vector3 _baseLocalScale;

        private bool UseRegistry => registry != null;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            if (!UseRegistry)
            {
                _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                _renderer.material.mainTexture = _tex;
            }

            // 初期 transform を覚えておき、Metadata 反映時にこの上に rotation/scale を適用する。
            var t = orientTarget != null ? orientTarget : transform;
            _baseLocalRotation = t.localRotation;
            _baseLocalScale = t.localScale;
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

        private void OnDestroy()
        {
            // 単一モードで自前確保した Texture2D を解放（registry モードでは _tex は null）。
            if (_tex != null)
            {
                if (Application.isPlaying) Destroy(_tex);
                else DestroyImmediate(_tex);
                _tex = null;
            }
            UnsubscribeOrient();
        }

        private void SubscribeOrient(CameraStream stream)
        {
            if (!autoOrient) return;
            if (ReferenceEquals(stream, _lastSubscribed)) return;
            UnsubscribeOrient();
            _lastSubscribed = stream;
            stream.MetadataUpdated += OnMetadataUpdated;
        }

        private void UnsubscribeOrient()
        {
            if (_lastSubscribed != null)
            {
                _lastSubscribed.MetadataUpdated -= OnMetadataUpdated;
                _lastSubscribed = null;
            }
        }

        private void OnMetadataUpdated(StreamMetadata meta) => ApplyOrient(meta);

        /// <summary>
        /// rotationDeg と effective aspect を Transform に適用する。
        /// - rotation: 初期回転に Z 軸 -rotationDeg を掛ける（反時計回り = 画像を正立させる）
        /// - scale:    初期 scale から effective aspect (W/H) に合わせて X/Y を調整。Y を保ち X = Y * aspect。
        /// 既存の物理寸法を完全に保てるわけではないが、縦持ちと横持ちの切替には対応できる。
        /// </summary>
        private void ApplyOrient(StreamMetadata meta)
        {
            if (!autoOrient) return;
            var t = orientTarget != null ? orientTarget : transform;
            t.localRotation = _baseLocalRotation * Quaternion.Euler(0f, 0f, -meta.rotationDeg);

            float aspect = meta.EffectiveAspect();
            if (aspect > 0f && _baseLocalScale.y > 0f)
            {
                t.localScale = new Vector3(_baseLocalScale.y * aspect, _baseLocalScale.y, _baseLocalScale.z);
            }
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
                    SubscribeOrient(active);
                    // 既に Metadata 取得済みなら即適用（subscribe より前にイベントが発火していたケース）。
                    if (active.Metadata != null) ApplyOrient(active.Metadata);
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
