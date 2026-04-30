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

        public string DisplayName => _source.DisplayName;
        public Texture2D Texture => _texture;
        public bool IsConnected => _receiver.IsConnected;
        public string? LastError => _receiver.LastError;

        public CameraStream(CameraSource source)
        {
            _source = source;
            _texture = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = $"CameraStream_{source.DisplayName}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _receiver = new MjpegStreamReceiver(source.BuildUrl());
        }

        public void Start()
        {
            if (_disposed) return;
            _receiver.Start();
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
