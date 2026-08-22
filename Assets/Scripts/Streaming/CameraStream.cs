#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 単一 CameraSource に紐付く受信 + テクスチャ更新ユニット。
    /// MonoBehaviour ではなくプレーンクラス。Tick() をメインスレッドから呼ぶこと（LoadImage のため）。
    ///
    /// suspend/resume/stall/lag/decode-fail/resume-gap の判定は純ロジック <see cref="StreamWatchdogLogic"/>
    /// へ委譲する。このクラスは Unity API 依存部（Time / Socket / Texture / async polling）だけを持つ。
    /// </summary>
    public sealed class CameraStream : IDisposable
    {
        private readonly CameraSource _source;
        private MjpegStreamReceiver _receiver;
        private readonly Texture2D _texture;
        private bool _started;
        private byte[]? _scratch;
        private bool _disposed;

        private readonly StreamWatchdogLogic _watchdog = new StreamWatchdogLogic();

        private StreamMetadata? _metadata;
        private StreamHealth? _health;

        // 内蔵 polling タイマー。HUD 等の外部呼び出しに依存せず、
        // /info と /health を自動でリフレッシュする（スマホ向き / 統計値の追従用）。
        private const float MetadataRefreshInterval = 1.5f;
        private const float HealthRefreshInterval   = 2.0f;
        private float _metaRefreshAccum;
        private float _healthRefreshAccum;
        private bool _metaInflight;
        private bool _healthInflight;

        // エンドポイント世代。ReapplyConnection でインクリメントし、in-flight の /info /health
        // フェッチが await 復帰後に「旧エンドポイント由来の結果」を書き込むのを破棄するためのトークン。
        // これが無いと、張替直後に旧端末の rotation/cameraId が _metadata に残留し、
        // さらに _metaInflight ガードが新エンドポイントの再取得をブロックしていた。
        private int _endpointGen;

        // 受信スレッドの払い出し → メインスレッドの展開完了 (ms)。**これは E2E ではない**。
        // 内訳つきの推定は <see cref="Latency"/> を読むこと。
        public float EstimatedLatencyMs { get; private set; }

        // 遅延の内訳（到着の揺らぎ / 展開 / 提示 / 配信側の鮮度）。
        private readonly LatencyEstimatorLogic _latency = new LatencyEstimatorLogic();

        /// <summary>遅延の内訳（企画書「100ms 程度以内を目標として管理する」の観測面）。</summary>
        public LatencyEstimatorLogic Latency => _latency;

        // 直近フレームの seq。歯抜け検出用。
        private long _lastSeq;
        public long LastFrameSeq => _lastSeq;
        public long DroppedFrames { get; private set; }

        public string DisplayName => _source.DisplayName;
        public Texture2D Texture => _texture;
        public bool IsConnected => _receiver.IsConnected;
        public string? LastError => _receiver.LastError;

        /// <summary>HMD 着脱 / OS pause で凍結中か。DiscoveryClient は suspend 中を「フレーム断」に数えない。</summary>
        public bool IsSuspended => _watchdog.IsSuspended;

        /// <summary>
        /// 最後にテクスチャへ反映したフレームの realtimeSinceStartup。未受信 / リセット直後は 0。
        /// SignalLostFx が配信断（フレーム断 &gt; 閾値）を判定するために読む。
        /// </summary>
        public float LastFrameRealtime => _watchdog.LastFrameRealtime;

        /// <summary>fixed-cam-streamer の /info から取得したメタ情報。未取得 / 非対応サーバなら null。</summary>
        public StreamMetadata? Metadata => _metadata;

        /// <summary>
        /// Metadata を最後に取得成功した realtimeSinceStartup。未取得 / 張替直後は 0。
        /// DiscoveryClient の cameraId 継続照合が「メタが新鮮なときだけ不一致を数える」ために読む
        /// （/info が失敗し続けて古い id が残留したメタで誤検知しないため）。
        /// </summary>
        public float MetadataUpdatedRealtime { get; private set; }

        /// <summary>fixed-cam-streamer の /health から取得した最新の統計。未取得なら null。</summary>
        public StreamHealth? Health => _health;

        /// <summary>Unity 受信側の実 fps（直近 1 秒の Texture2D.LoadImage 回数）。</summary>
        public float ReceivedFps => _watchdog.ReceivedFps;

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
            if (_disposed) return;
            bool changed = _watchdog.SetSuspended(suspended, Time.realtimeSinceStartup);
            if (!changed) return;
            Debug.Log($"[HmdLife] {_source.DisplayName} SetSuspended({suspended})");
            if (!suspended) { _metaRefreshAccum = 0f; _healthRefreshAccum = 0f; }
        }

        /// <summary>Metadata が更新された時に呼ばれる。MjpegScreen 等が orientation を反映するためのフック。</summary>
        public event Action<StreamMetadata>? MetadataUpdated;

        /// <summary>
        /// デコード成功したフレームの**生 JPEG バイト列**を渡すフック（buffer, length）。
        /// 端末内録画（<c>SegmentRecorder</c>）が使う。**同期的にコピーすること** — 戻った時点で
        /// バッファは次のフレームで上書きされる。壊れ JPEG では呼ばれない。
        /// </summary>
        public Action<byte[], int>? FrameTap;

        public CameraStream(CameraSource source)
        {
            _source = source;
            // ⚠ **mipChain は必須**（2026-08-12）。周回で痩せる伝送は `ScreenComposite` が mip を引いて
            //   作る（＝面積平均のピラミッド ＝「先に帯域を落としてから間引く」という実物の順序）。
            //   mip が無いと LOD 指定は黙って無視され、**画は 1 画素も変わらない**。
            //   trilinear は中間の LOD を補間するので、粗さの進みが連続量のまま画に出る。
            _texture = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: true)
            {
                name = $"CameraStream_{source.DisplayName}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            // 未接続中に未初期化メモリが Quest GPU 上で白ノイズ化しチカチカする問題を回避するため、
            // 初回 LoadImage が走る前に黒で埋めておく（4 px 分のみ; LoadImage で正しいサイズへ再確保される）。
            _texture.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
            _texture.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            _receiver = new MjpegStreamReceiver(source.BuildUrl(), basicAuthToken: source.BasicAuthToken);
        }

        public void Start()
        {
            if (_disposed) return;
            _started = true;
            _receiver.Start();

            // /info を起動直後に 1 回。以降は RefreshMetadataAsync を HUD 等が定期的に呼ぶ
            // （スマホの向き変更を Unity 側でも追従させるため）。失敗時はフェイルオープン。
            _ = RefreshMetadataAsync();
        }

        /// <summary>CameraSource の接続パラメータ（host|port|user|pass 指紋）の変化検知キー。</summary>
        public string ConnectionKey => _source.ConnectionKey;

        /// <summary>
        /// show.json / 端末キャッシュで host・port・認証が差し替わった時に MJPEG 接続を張り直す。
        /// 現場で配信スマホの DHCP IP がズレたケースを、ビルドし直さず Web から復旧できるようにする。
        /// 旧 receiver を破棄して新 URL で作り直す（URL は CameraSource.BuildUrl 経由なので
        /// ApplyRuntimeEndpoint 後に呼ぶこと）。
        /// </summary>
        public void ReapplyConnection()
        {
            if (_disposed) return;
            try { _receiver.Dispose(); } catch { }
            _receiver = new MjpegStreamReceiver(_source.BuildUrl(), basicAuthToken: _source.BasicAuthToken);
            // メタ/統計と計測ウィンドウは接続先が変わったらリセット。
            // 世代を進めて in-flight フェッチの結果を無効化し、inflight ガードも解除する
            // （旧: 旧フェッチが in-flight だと新エンドポイントの /info 取得がブロックされ、
            //   さらに旧端末のメタが完了時に書き戻されていた）。
            _endpointGen++;
            _metaInflight = false;
            _healthInflight = false;
            _metadata = null;
            _health = null;
            MetadataUpdatedRealtime = 0f;
            // X-Frame-Seq は端末・接続ごとに独立した系列。旧系列を持ち越すと別端末の seq と
            // 比較して偽の DroppedFrames を計上する。E2E 推定値も旧接続由来なのでクリア。
            _lastSeq = 0;
            DroppedFrames = 0;
            EstimatedLatencyMs = 0f;
            // 遅延の窓も捨てる（別端末の時計を基準にした最小値を持ち越すと揺らぎが嘘になる）。
            _latency.Reset();
            _watchdog.NotifyEndpointChanged(Time.realtimeSinceStartup);
            if (_started)
            {
                // suspend 中でも必ず起動する。受信スレッドは suspend 中も温存する設計
                // （SetSuspended は Tick を止めるだけ）なので起動は無害。旧実装は suspend 中の
                // 張替（focus 喪失中に show.json / discovery 適用）で新 receiver が未起動のまま残り、
                // resume は Start() を呼ばないため恒久黒画面になっていた。
                _receiver.Start();
                _ = RefreshMetadataAsync();
            }
            Debug.Log($"[CameraStream] {_source.DisplayName} reconnect -> {_source.BuildUrl()}");
        }

        /// <summary>
        /// /info を取得して Metadata を更新。値が変わった時のみ MetadataUpdated を発火し、
        /// 重複イベント（毎回 Quad 再回転）を避ける。
        /// </summary>
        public async System.Threading.Tasks.Task RefreshMetadataAsync()
        {
            if (_metaInflight) return;
            _metaInflight = true;
            int gen = _endpointGen; // この呼び出しが属するエンドポイント世代
            try
            {
                string url = _source.BuildInfoUrl();
                if (string.IsNullOrEmpty(url)) return;
                var meta = await StreamMetadataFetcher.FetchInfoAsync(url, basicAuthToken: _source.BasicAuthToken);
                if (_disposed || meta == null) return;
                if (gen != _endpointGen) return; // await 中に張替 → 旧エンドポイント由来の結果は破棄

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
                MetadataUpdatedRealtime = Time.realtimeSinceStartup;
                if (!changed) return;

                try { MetadataUpdated?.Invoke(meta); }
                catch (Exception ex) { Debug.LogWarning($"[CameraStream] MetadataUpdated handler threw: {ex.Message}"); }
            }
            finally
            {
                // 張替後の新世代フェッチが走行中なら、その inflight フラグを旧世代の finally が
                // 巻き添えで下ろさない（下ろすと同時 2 本目のフェッチを許してしまう）。
                if (gen == _endpointGen) _metaInflight = false;
            }
        }

        /// <summary>HudDump 等から呼ばれる任意のリフレッシュ。/health は時間経過で値が変わるので明示更新。</summary>
        public async System.Threading.Tasks.Task RefreshHealthAsync()
        {
            if (_healthInflight) return;
            _healthInflight = true;
            int gen = _endpointGen; // この呼び出しが属するエンドポイント世代
            try
            {
                string url = _source.BuildHealthUrl();
                if (string.IsNullOrEmpty(url)) return;
                var h = await StreamMetadataFetcher.FetchHealthAsync(url, basicAuthToken: _source.BasicAuthToken);
                if (_disposed || h == null) return;
                if (gen != _endpointGen) return; // 旧エンドポイント由来の統計は破棄（lag 判定の汚染防止）
                _health = h;
            }
            finally
            {
                if (gen == _endpointGen) _healthInflight = false;
            }
        }

        /// <summary>
        /// メインスレッドから毎フレーム呼ぶ。新フレームがあればテクスチャを更新する。
        /// </summary>
        public void Tick()
        {
            if (_disposed) return;
            float now = Time.realtimeSinceStartup;
            float udt = Time.unscaledDeltaTime;

            // フリーズ明け自己回復（コールバック非依存）:
            // HMD 着脱や OS pause で Update が数秒凍結すると復帰初回フレームの
            // unscaledDeltaTime が巨大になる。Quest/Link は resume コールバック
            // (OnApplicationPause(false)/Focus(true)) を確実には配送しないため、
            // 巨大 dt を「フリーズ明け」と判定し、計測ウィンドウを全リセットし suspend を解除して
            // この 1 フレームをスキップする（A1: BeginTick 内で _suspended=false）。
            if (_watchdog.BeginTick(now, udt))
            {
                _metaRefreshAccum = 0f;
                _healthRefreshAccum = 0f;
                Debug.Log($"[HmdLife] {_source.DisplayName} resume-gap (dt={udt:F2}s) -> reset");
                return;
            }

            // 単一スロット最新フレームを取り出す（既に MjpegStreamReceiver 側で「最新だけ」保持）。
            // バッファは swap で受け渡され、毎フレーム new は発生しない。
            if (_receiver.TryConsumeFrame(ref _scratch, out int len, out var meta) && len > 0 && _scratch != null)
            {
                // markNonReadable=false: 連続 LoadImage 上書きで texture 再利用するため CPU 側を残す。
                // 戻り値で decode 成否を見る。壊れ JPEG は false（texture は前フレームのまま）なので、
                // 成功時のみ受信統計を進める（false を成功として数えると健全性検知が全て沈黙する）。
                bool decoded = _texture.LoadImage(_scratch, markNonReadable: false);
                if (decoded)
                {
                    _watchdog.OnFrameDecoded(now);

                    // 端末内録画。null チェックだけなので非録画時のコストはゼロに近い。
                    // 例外で受信ループを殺さない（録画は体験を止めない）。
                    if (FrameTap != null)
                    {
                        try { FrameTap(_scratch, len); }
                        catch (Exception e) { Debug.LogWarning($"[CameraStream] FrameTap 例外: {e.Message}"); }
                    }

                    if (_lastSeq != 0 && meta.seq > _lastSeq + 1)
                        DroppedFrames += (meta.seq - _lastSeq - 1);
                    _lastSeq = meta.seq;

                    // 遅延の内訳（企画書「視覚遅延は 100ms 程度以内を目標として管理する」）。
                    //   展開ぶん = 受信スレッドの払い出し → メインスレッドの LoadImage 完了
                    //   揺らぎぶん = 撮影時刻付きフレームの到着差（窓内最小からの超過）
                    // **絶対の end-to-end は測っていない**（端末間の時計の基準が違い、引き算できない）。
                    double decodeMs = MjpegStreamReceiver.NowMs() - meta.receivedTickMs;
                    EstimatedLatencyMs = (float)decodeMs;
                    _latency.ObserveDecode((float)decodeMs);
                    if (meta.captureNs != 0)
                    {
                        _latency.ObserveArrival(meta.captureNs / 1_000_000.0, meta.receivedTickMs);
                    }
                }
                else
                {
                    // decode 失敗を数える。連続が枚数 or 時間の閾値を超えたら強制再接続（cooldown 尊重）。
                    if (_watchdog.OnFrameDecodeFailed(now, out int streak, out float sinceSec))
                    {
                        // ログは reconnect 発火時のみ（cooldown で >=5s 間隔）＝毎フレームは出さずスロットル済み。
                        Debug.LogWarning($"[CameraStream] {_source.DisplayName} decode 連続失敗 " +
                                         $"({streak}枚 / {sinceSec:F1}s 壊れ JPEG). reconnecting.");
                        _receiver.RequestReconnect();
                    }
                }
            }

            // /info / /health を内蔵タイマーで定期 refresh（HUD 不在シーンでも動くように）。
            _metaRefreshAccum += udt;
            if (_metaRefreshAccum >= MetadataRefreshInterval)
            {
                _metaRefreshAccum = 0f;
                _ = RefreshMetadataAsync();
            }
            _healthRefreshAccum += udt;
            if (_healthRefreshAccum >= HealthRefreshInterval)
            {
                _healthRefreshAccum = 0f;
                _ = RefreshHealthAsync();
            }

            // recv-fps 窓ロール + lag 検出 + stall watchdog を純ロジックに委譲。
            // 配信端末が熱いあいだは lag 判定を抑止する（張り直しても直らず、悪化させるだけ）。
            // ⚠ streamer v0.11.0 でアプリ側の throttle は消えたが、その先で OS が絞るので抑止は要る。
            float phoneFps = _health?.fps ?? 0f;
            _latency.SetDisplayRate(DisplayRateInfo.CurrentHz);
            // ⚠ `/health` を 1 度も取れていないときも **0 ではなく -1（不明）**。
            //   0 を渡すと「たったいま来た」に化ける（`SetSourceAgeMs` の注記）。
            _latency.SetSourceAgeMs(_health?.latestFrameAgeMs ?? -1f);
            var reason = _watchdog.EndTick(now, udt, phoneFps, _health?.IsHot ?? false);
            if (reason == StreamWatchdogLogic.ReconnectReason.Lag)
            {
                Debug.Log($"[CameraStream] lag detected (recv={_watchdog.ReceivedFps:F1}/phone={phoneFps:F1}). reconnecting.");
                _receiver.RequestReconnect();
            }
            else if (reason == StreamWatchdogLogic.ReconnectReason.Stall)
            {
                Debug.Log($"[CameraStream] {_source.DisplayName} stall detected ({now - _watchdog.LastFrameRealtime:F1}s 無フレーム). reconnecting.");
                _receiver.RequestReconnect();
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
