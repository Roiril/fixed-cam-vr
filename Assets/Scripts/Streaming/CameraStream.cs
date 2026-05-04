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

        // 内蔵 polling タイマー。HUD 等の外部呼び出しに依存せず、
        // /info と /health を自動でリフレッシュする（スマホ向き / 統計値の追従用）。
        private const float MetadataRefreshInterval = 1.5f;
        private const float HealthRefreshInterval   = 2.0f;
        private float _metaRefreshAccum;
        private float _healthRefreshAccum;
        private bool _metaInflight;
        private bool _healthInflight;

        // Lag 検出 → 強制再接続。
        // PHONE_FPS に対して RECV_FPS が一定割合を下回る状態が連続したとき、
        // TCP 輻輳ウィンドウや kernel バッファ滞留が原因と推定して接続を張り直す。
        // Wi-Fi の断続的なパケロスで TCP cwnd が縮みっぱなしになるケースで効果が大きい。
        private const float LagDetectWindowSec = 3.0f;     // 評価ウィンドウ
        private const float LagThresholdRatio  = 0.6f;     // RECV / PHONE 比率がこれ未満なら lag
        private const float LagReconnectCooldownSec = 8.0f;// 連続再接続のクールダウン
        private float _lagWindowAccum;
        private float _lastReconnectTime;

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
            if (_metaInflight) return;
            _metaInflight = true;
            try
            {
                string url = _source.BuildInfoUrl();
                if (string.IsNullOrEmpty(url)) return;
                var meta = await StreamMetadataFetcher.FetchInfoAsync(url);
                if (_disposed || meta == null) return;

                // 比較: 向き反映に効く 4 値のいずれか変化で発火。
                // 特に isPortrait は fixed-cam-streamer 側で rotationDeg が固定でも
                // 動的更新されるため、これが回ったら必ず ApplyOrient したい。
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
            finally { _metaInflight = false; }
        }

        /// <summary>HudDump 等から呼ばれる任意のリフレッシュ。/health は時間経過で値が変わるので明示更新。</summary>
        public async System.Threading.Tasks.Task RefreshHealthAsync()
        {
            if (_healthInflight) return;
            _healthInflight = true;
            try
            {
                string url = _source.BuildHealthUrl();
                if (string.IsNullOrEmpty(url)) return;
                var h = await StreamMetadataFetcher.FetchHealthAsync(url);
                if (_disposed || h == null) return;
                _health = h;
            }
            finally { _healthInflight = false; }
        }

        /// <summary>
        /// メインスレッドから毎フレーム呼ぶ。新フレームがあればテクスチャを更新する。
        /// </summary>
        public void Tick()
        {
            if (_disposed) return;

            // フレームをドレインして「最新フレームだけ」表示。
            // 単一 frame/Tick だと FIFO に溜まったバーストで「加速⇄スロー」が起きるため、
            // 古いフレームは捨てて常に最新で更新する（固定カメラ用途は最新優先）。
            byte[]? lastBuf = null;
            int lastLen = 0;
            int drained = 0;
            while (_receiver.TryConsumeFrame(ref _scratch, out int len) && len > 0 && _scratch != null)
            {
                lastBuf = _scratch;
                lastLen = len;
                drained++;
            }
            if (lastBuf != null && lastLen > 0)
            {
                _texture.LoadImage(lastBuf, markNonReadable: false);
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

            // /info / /health を内蔵タイマーで定期 refresh（HUD 不在シーンでも動くように）。
            float dt = Time.unscaledDeltaTime;
            _metaRefreshAccum += dt;
            if (_metaRefreshAccum >= MetadataRefreshInterval)
            {
                _metaRefreshAccum = 0f;
                _ = RefreshMetadataAsync();
            }
            _healthRefreshAccum += dt;
            if (_healthRefreshAccum >= HealthRefreshInterval)
            {
                _healthRefreshAccum = 0f;
                _ = RefreshHealthAsync();
            }

            // Lag 検出。PHONE_FPS と RECV_FPS が両方読めるときのみ評価。
            float phoneFps = _health?.fps ?? 0f;
            if (phoneFps > 1f && _recvFps > 0f)
            {
                float ratio = _recvFps / phoneFps;
                if (ratio < LagThresholdRatio)
                {
                    _lagWindowAccum += dt;
                    if (_lagWindowAccum >= LagDetectWindowSec
                        && now - _lastReconnectTime >= LagReconnectCooldownSec)
                    {
                        Debug.Log($"[CameraStream] lag detected (recv={_recvFps:F1}/phone={phoneFps:F1} ratio={ratio:F2}). reconnecting.");
                        _receiver.RequestReconnect();
                        _lastReconnectTime = now;
                        _lagWindowAccum = 0f;
                    }
                }
                else
                {
                    _lagWindowAccum = 0f;
                }
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
