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
        // HMD を外す / システムメニュー等で app が pause された間は true。
        // この間メインスレッドの Tick は凍結されるため、計測を止めて復帰時にリセットする。
        private bool _suspended;
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
        // 低遅延優先のため反応速度を上げる: 窓 1.5s / 閾値 0.7 / cooldown 5s。
        private const float LagDetectWindowSec = 1.5f;
        private const float LagThresholdRatio  = 0.7f;
        private const float LagReconnectCooldownSec = 5.0f;
        // この秒数を超える unscaledDeltaTime は「フリーズ明け（HMD 着脱 / OS pause）」とみなす。
        // 通常フレームは ~0.01s、ヒッチでも <0.2s。0.5s なら誤検知なく pause だけ拾える。
        private const float ResumeGapSec = 0.5f;
        private float _lagWindowAccum;
        private float _lastReconnectTime;

        // /health の latestFrameAgeMs と receivedTickMs から推定する E2E 遅延（ms）。
        // 「frame が capture されてから Unity がテクスチャに上げるまで」の参考値。
        public float EstimatedLatencyMs { get; private set; }

        // 直近フレームの seq。歯抜け検出用。
        private long _lastSeq;
        public long LastFrameSeq => _lastSeq;
        public long DroppedFrames { get; private set; }

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

        /// <summary>
        /// 外部から強制的に MJPEG 接続を張り直す。デバッグ用ホットキーや lag 手動発火に使う。
        /// （内蔵 lag 検知は CameraStream.Tick から自動で呼ばれる）
        /// </summary>
        public void ForceReconnect() => _receiver.RequestReconnect();

        /// <summary>
        /// app の pause / 入力フォーカス喪失（HMD を外す・システムメニュー）に応じて受信ユニットを
        /// 一時停止/再開する。CameraStreamRegistry の OnApplicationPause/Focus から呼ばれる。
        ///
        /// 停止中: Tick を no-op にし、フレーム消費・fps 計測・lag 検出を全て止める。
        /// 復帰時: 凍結中に Time.realtimeSinceStartup / unscaledDeltaTime が実時間ぶん進み、
        ///   復帰初回フレームで「recv_fps が激減した」と誤検知 → 不要な強制再接続が走るのを防ぐため、
        ///   計測ウィンドウの基準を全てリセットしてから再開する。受信スレッド自体は止めない
        ///   （ソケットを温存し、被り直し時に即復帰させるため）。
        /// </summary>
        public void SetSuspended(bool suspended)
        {
            if (_disposed || _suspended == suspended) return;
            _suspended = suspended;
            Debug.Log($"[HmdLife] {_source.DisplayName} SetSuspended({suspended})");
            if (suspended) return;

            _recvWindowStart = 0f;
            _recvFramesInWindow = 0;
            _recvFps = 0f;
            _lagWindowAccum = 0f;
            _metaRefreshAccum = 0f;
            _healthRefreshAccum = 0f;
            _lastReconnectTime = Time.realtimeSinceStartup;
        }

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
            _receiver = new MjpegStreamReceiver(source.BuildUrl(), basicAuthToken: source.BasicAuthToken);
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
                var meta = await StreamMetadataFetcher.FetchInfoAsync(url, basicAuthToken: _source.BasicAuthToken);
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
                var h = await StreamMetadataFetcher.FetchHealthAsync(url, basicAuthToken: _source.BasicAuthToken);
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

            // フリーズ明け自己回復（コールバック非依存）:
            // HMD 着脱や OS pause で Update が数秒凍結すると復帰初回フレームの
            // unscaledDeltaTime が巨大になる。Quest/Link は resume コールバック
            // (OnApplicationPause(false)/Focus(true)) を確実には配送しないため
            // （実機ログで実証済み）、ここで巨大 dt を「フリーズ明け」と判定し
            // 計測ウィンドウを全リセットしてこの 1 フレームをスキップする。
            // → lag-detect 誤発火を防ぎつつ次フレームから正常復帰。
            //   suspend で Tick を凍結する旧方式（resume 来ず永久凍結=画面が戻らない）を置換。
            if (Time.unscaledDeltaTime > ResumeGapSec)
            {
                _recvWindowStart = 0f;
                _recvFramesInWindow = 0;
                _recvFps = 0f;
                _lagWindowAccum = 0f;
                _metaRefreshAccum = 0f;
                _healthRefreshAccum = 0f;
                _lastReconnectTime = Time.realtimeSinceStartup;
                Debug.Log($"[HmdLife] {_source.DisplayName} resume-gap (dt={Time.unscaledDeltaTime:F2}s) -> reset");
                return;
            }

            // 単一スロット最新フレームを取り出す（既に MjpegStreamReceiver 側で「最新だけ」保持）。
            // バッファは swap で受け渡され、毎フレーム new は発生しない。
            if (_receiver.TryConsumeFrame(ref _scratch, out int len, out var meta) && len > 0 && _scratch != null)
            {
                // markNonReadable=false: 連続 LoadImage 上書きで texture 再利用するため CPU 側を残す
                _texture.LoadImage(_scratch, markNonReadable: false);
                _recvFramesInWindow++;

                if (_lastSeq != 0 && meta.seq > _lastSeq + 1)
                    DroppedFrames += (meta.seq - _lastSeq - 1);
                _lastSeq = meta.seq;

                // E2E 遅延推定: /health の clockSkew 補正は無いので、ここでは「Unity 受信からテクスチャ反映」までを表示
                if (meta.captureNs != 0)
                {
                    EstimatedLatencyMs = MjpegStreamReceiver.NowMs() - meta.receivedTickMs;
                }
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
