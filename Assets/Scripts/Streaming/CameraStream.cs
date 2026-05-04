#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 単一 CameraSource に紐付く受信 + テクスチャ更新ユニット。
    /// MonoBehaviour ではなくプレーンクラス。Tick() をメインスレッドから呼ぶこと（LoadImage のため）。
    /// </summary>
    public sealed class CameraStream : IDisposable
    {
        private readonly CameraSource _source;
        private readonly MjpegStreamReceiver _receiver;
        private readonly Texture2D _texture;
        private byte[]? _scratch;
        private bool _disposed;
        private StreamMetadata? _metadata;
        private StreamHealth? _health;

        public string DisplayName => _source.DisplayName;
        public Texture2D Texture => _texture;
        public bool IsConnected => _receiver.IsConnected;
        public string? LastError => _receiver.LastError;

        /// <summary>fixed-cam-streamer の /info から取得したメタ情報。未取得 / 非対応サーバなら null。</summary>
        public StreamMetadata? Metadata => _metadata;

        /// <summary>fixed-cam-streamer の /health から取得した最新の統計。未取得なら null。</summary>
        public StreamHealth? Health => _health;

        /// <summary>Metadata が更新された時に呼ばれる。MjpegScreen 等が orientation を反映するためのフック。</summary>
        public event Action<StreamMetadata>? MetadataUpdated;

        public CameraStream(CameraSource source)
        {
            _source = source;
            _texture = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = $"CameraStream_{source.DisplayName}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            // 未接続中に未初期化メモリが Quest GPU 上で白ノイズ化しチカチカする問題を回避するため、
            // 初回 LoadImage が走る前に黒で埋めておく（4 px 分のみ; LoadImage で正しいサイズへ再確保される）。
            _texture.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
            _texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _receiver = new MjpegStreamReceiver(source.BuildUrl());
        }

        public void Start()
        {
            if (_disposed) return;
            _receiver.Start();

            // /info を一度だけバックグラウンドで取得。失敗しても配信本体は影響なし（DroidCam 等の互換）。
            _ = FetchMetadataOnceAsync();
        }

        private async System.Threading.Tasks.Task FetchMetadataOnceAsync()
        {
            string url = _source.BuildInfoUrl();
            if (string.IsNullOrEmpty(url)) return;
            var meta = await StreamMetadataFetcher.FetchInfoAsync(url);
            if (_disposed || meta == null) return;
            _metadata = meta;
            try { MetadataUpdated?.Invoke(meta); }
            catch (Exception ex) { Debug.LogWarning($"[CameraStream] MetadataUpdated handler threw: {ex.Message}"); }
        }

        /// <summary>HudDump 等から呼ばれる任意のリフレッシュ。/health は時間経過で値が変わるので明示更新。</summary>
        public async System.Threading.Tasks.Task RefreshHealthAsync()
        {
            string url = _source.BuildHealthUrl();
            if (string.IsNullOrEmpty(url)) return;
            var h = await StreamMetadataFetcher.FetchHealthAsync(url);
            if (_disposed || h == null) return;
            _health = h;
        }

        /// <summary>
        /// メインスレッドから毎フレーム呼ぶ。新フレームがあればテクスチャを更新する。
        /// </summary>
        public void Tick()
        {
            if (_disposed) return;
            if (_receiver.TryConsumeFrame(ref _scratch, out int len) && len > 0 && _scratch != null)
            {
                _texture.LoadImage(_scratch, markNonReadable: false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _receiver.Dispose(); } catch { }
            if (_texture != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_texture);
                else UnityEngine.Object.DestroyImmediate(_texture);
            }
        }
    }
}
