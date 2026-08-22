#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 映像遅延の推定（UnityEngine 非依存）。企画書 2.3 の
    /// 「映像伝送の視覚遅延は 100 ms 程度以内を目標として管理する」を、**測れるものだけで**支える。
    ///
    /// <b>絶対の end-to-end は測っていない。</b> 配信端末の <c>X-Capture-Ns</c> は端末側の monotonic 時計で、
    /// Unity 側の時計とは基準が違う。両者を結ぶには配信アプリが「同じ基準の現在時刻」を返す口
    /// （例: <c>/clock</c>）を持つ必要があり、それが無い今、差を引き算すると**端末間の時計のずれが
    /// そのまま遅延として出る**。推定の定数を足して「100ms でした」と言うより、測れる 3 つを分けて出す方が正しい。
    ///
    /// 出すもの:
    ///   - <see cref="ArrivalJitterMs"/>: 到着の揺らぎ。<c>受信時刻 − 撮影時刻</c> の窓内最小値からの超過。
    ///     時計のずれは最小値の側に吸われるので**差分だけは正しい**。Wi-Fi の詰まり・熱スロットルで増える。
    ///   - <see cref="DecodeMs"/>: 受信スレッドが 1 枚を出してからメインスレッドで展開し終わるまで。
    ///   - <see cref="PresentMs"/>: 展開してから目に出るまで（表示レートから 1.5 フレーム）。
    ///
    /// これらの和（<see cref="ObservedMs"/>）は「Unity に届いてから目に出るまで＋経路の揺らぎ」であって、
    /// 撮影・エンコード・伝送の下限は含まない。
    /// </summary>
    public sealed class LatencyEstimatorLogic
    {
        /// <summary>到着差の最小値を追う窓 (ms)。長すぎると時計のドリフトを拾い、短すぎると底が上がる。</summary>
        public const double WindowMs = 10000.0;

        /// <summary>窓を刻むバケット数。</summary>
        public const int Buckets = 10;

        /// <summary>デコード所要の平滑化係数（大きいほど新しい値に敏感）。</summary>
        public const float DecodeSmoothing = 0.2f;

        private readonly double[] _bucketMin = new double[Buckets];
        private readonly bool[] _bucketUsed = new bool[Buckets];
        private int _head;
        private double _headStartMs;
        private bool _hasSample;

        private double _lastDeltaMs;
        private float _decodeMs;
        private float _presentMs;
        private float _sourceAgeMs;

        /// <summary>撮影時刻付きのフレームを 1 枚でも観測したか（<c>X-Capture-Ns</c> を送らない配信では false）。</summary>
        public bool HasCaptureStamp => _hasSample;

        /// <summary>到着の揺らぎ (ms)。窓内最小の到着差からの超過。0 が最良。</summary>
        public float ArrivalJitterMs
        {
            get
            {
                if (!_hasSample) return 0f;
                double min = MinInWindow();
                double v = _lastDeltaMs - min;
                return v > 0.0 ? (float)v : 0f;
            }
        }

        /// <summary>デコード所要 (ms)。受信スレッドの払い出し → メインスレッドの展開完了。</summary>
        public float DecodeMs => _decodeMs;

        /// <summary>提示 (ms)。表示レートから 1.5 フレームと置く。</summary>
        public float PresentMs => _presentMs;

        /// <summary>配信側での最新フレームの鮮度 (ms)。<c>/health.latestFrameAgeMs</c>。カメラ stall の指標。</summary>
        public float SourceAgeMs => _sourceAgeMs;

        /// <summary>Unity が観測できる遅延の和 (ms)。撮影・エンコード・伝送の下限は含まない。</summary>
        public float ObservedMs => ArrivalJitterMs + _decodeMs + _presentMs;

        /// <summary>表示レート (Hz) を設定して提示ぶんを更新する。</summary>
        public void SetDisplayRate(float hz)
        {
            float f = hz > 1f ? hz : 72f;
            _presentMs = 1.5f / f * 1000f;
        }

        /// <summary>
        /// 配信側の <c>/health.latestFrameAgeMs</c> を取り込む。
        ///
        /// ⚠⚠ <b><c>-1</c> は「まだ 1 枚も作っていない」の番兵</b>（配信側 <c>FrameStats.toJson</c> が
        /// <c>lastPublishMs == 0</c> のときに返す）。**0 へ潰してはいけない** —
        /// 「たったいま来た」に化けて、**止まっている端末を正常と誤診する**
        /// （2026-08-23 に配信側のセッションが実際に踏んだ。入れ直した直後の端末は
        /// <c>totalFrames=0</c> なので必ずこれになる）。
        /// ⚠ 不正な負値も同じ「不明」へ寄せる（読む側が 1 つの番兵だけ知っていれば済む）。
        /// </summary>
        public void SetSourceAgeMs(float ms) => _sourceAgeMs = ms < 0f ? -1f : ms;

        /// <summary>デコード所要を 1 枚ぶん取り込む（指数平滑）。</summary>
        public void ObserveDecode(float ms)
        {
            if (ms < 0f) ms = 0f;
            _decodeMs = _decodeMs <= 0f ? ms : _decodeMs + (ms - _decodeMs) * DecodeSmoothing;
        }

        /// <summary>
        /// 撮影時刻付きフレームの到着を取り込む。
        /// </summary>
        /// <param name="captureMs">配信端末の時計での撮影時刻 (ms)。</param>
        /// <param name="receivedMs">Unity の時計での受信時刻 (ms)。</param>
        public void ObserveArrival(double captureMs, double receivedMs)
        {
            double delta = receivedMs - captureMs;
            _lastDeltaMs = delta;

            double bucketMs = WindowMs / Buckets;
            if (!_hasSample)
            {
                _hasSample = true;
                _head = 0;
                _headStartMs = receivedMs;
                for (int i = 0; i < Buckets; i++) { _bucketUsed[i] = false; _bucketMin[i] = 0.0; }
                _bucketUsed[0] = true;
                _bucketMin[0] = delta;
                return;
            }

            // 時計が巻き戻った（再接続でリセット等）ら作り直す。
            if (receivedMs < _headStartMs)
            {
                Reset();
                ObserveArrival(captureMs, receivedMs);
                return;
            }

            while (receivedMs - _headStartMs >= bucketMs)
            {
                _headStartMs += bucketMs;
                _head = (_head + 1) % Buckets;
                _bucketUsed[_head] = false;
                _bucketMin[_head] = 0.0;
            }

            if (!_bucketUsed[_head]) { _bucketUsed[_head] = true; _bucketMin[_head] = delta; }
            else if (delta < _bucketMin[_head]) _bucketMin[_head] = delta;
        }

        /// <summary>
        /// 接続の張り替えでリセットする（別端末の値を持ち越さない）。
        ///
        /// ⚠⚠ <b>鮮度は 0 ではなく <c>-1</c>（不明）へ戻す。</b> 張り替えた直後は新しい端末から
        /// <c>/health</c> をまだ 1 度も取れていないので、0 は「たったいま来た」という嘘になる。
        /// カメラの切替は本番中ずっと起きるので、ここが 0 だと<b>切り替わるたびに嘘が出る</b>
        /// （<see cref="SetSourceAgeMs"/> の注記と同じ罠。2026-08-23 に同じ形を 4 か所で踏んだ —
        /// 配信側・Unity の取り込み・卓の表示・ここ）。
        /// </summary>
        public void Reset()
        {
            _hasSample = false;
            _head = 0;
            _headStartMs = 0.0;
            _lastDeltaMs = 0.0;
            _decodeMs = 0f;
            _sourceAgeMs = -1f;
            for (int i = 0; i < Buckets; i++) { _bucketUsed[i] = false; _bucketMin[i] = 0.0; }
        }

        private double MinInWindow()
        {
            double min = double.MaxValue;
            for (int i = 0; i < Buckets; i++)
            {
                if (!_bucketUsed[i]) continue;
                if (_bucketMin[i] < min) min = _bucketMin[i];
            }
            return min == double.MaxValue ? _lastDeltaMs : min;
        }
    }
}
