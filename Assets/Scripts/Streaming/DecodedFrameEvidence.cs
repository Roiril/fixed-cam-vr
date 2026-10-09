namespace FixedCamVr.Streaming
{
    /// <summary>正常デコードの証拠。watchdogの復帰時刻補正を成功映像に混ぜない。</summary>
    public sealed class DecodedFrameEvidence
    {
        public const float FreshSec = 1.5f;
        private bool _hasFrame;
        private float _decodedAt;
        private long _connection, _receivedAfter;
        public void Invalidate(long receivedAfter) { _hasFrame = false; _receivedAfter = receivedAfter; }
        public void Decoded(float now, long receivedAt, long connection, long currentConnection, long tickNow)
        {
            if (connection != currentConnection || receivedAt <= _receivedAfter
                || receivedAt > tickNow || tickNow - receivedAt > FreshSec * 1000f) return;
            _hasFrame = true; _decodedAt = now; _connection = connection;
        }
        public bool IsFresh(float now, long connection, bool connected, bool suspended)
            => _hasFrame && connected && !suspended && connection == _connection
                && now >= _decodedAt && now - _decodedAt <= FreshSec;
    }
}
