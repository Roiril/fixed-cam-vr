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

        // 受信側 (Unity) の実 fps 計測。1 秒ウィンドウで texture 更新回数を数える。
        private float _recvWindowStart;
        private int _recvFramesInWindow;
        private float _recvFps;

        public string DisplayName => _source.DisplayName;
        public Texture2D Texture => _texture;
        public bool IsConnected => _receiver.IsConnected;
        public string? LastError => _receiver.LastError;

        /// <summary>fixed-cam-streamer の /info から取得したメタ情報。未取得 / 非対応サーバなら null。</summary>
        public StreamMetadata? Metadata => _metadata;

        /// <summary>fixed-cam-streamer の /health から取得した最新の統計。未取得なら null。</summary>
        public StreamHealth? Health => _health;

        /// <summary>Unity 受信側の実 fps（直近 1 秒の Texture2D.LoadImage 回数）。</summary>
        public float ReceivedFps => _recvFps;

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

            // /info を起動直後に 1 回。以降は RefreshMetadataAsync を HUD 等が定期的に呼ぶ
            // （スマホの向き変更を Unity 側でも追従させるため）。失敗時はフェイルオープン。
            _ = RefreshMetadataAsync();
        }

        /// <summary>
        /// /info を取得して Metadata を更新。値が変わった時のみ MetadataUpdated を発火し、
        /// 重複イベント（毎回 Quad 再回転）を避ける。
        /// </summary>
        public async System.Threading.Tasks.Task RefreshMetadataAsync()
        {
            string url = _source.BuildInfoUrl();
            if (string.IsNullOrEmpty(url)) return;
            var meta = await StreamMetadataFetcher.FetchInfoAsync(url);
            if (_disposed || meta == null) return;

            // 比較: rotationDeg と widthPx/heightPx だけ気にする（向き反映に必要）
            var prev = _metadata;
            bool changed = prev == null
                || prev.rotationDeg != meta.rotationDeg
                || prev.widthPx != meta.widthPx
                || prev.heightPx != meta.heightPx
                || prev.isPortrait != meta.isPortrait;
            _metadata = meta;
            if (!changed) return;

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
                _recvFramesInWindow++;
            }
            // 受信 fps 計測（1 秒ウィンドウ）
            float now = Time.realtimeSinceStartup;
            if (_recvWindowStart == 0f) _recvWindowStart = now;
            if (now - _recvWindowStart >= 1f)
            {
                _recvFps = _recvFramesInWindow / (now - _recvWindowStart);
                _recvWindowStart = now;
                _recvFramesInWindow = 0;
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
