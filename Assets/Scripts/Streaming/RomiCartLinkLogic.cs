#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕で台車（Romi）へ合図を送る時機を決める（純ロジック）。
    ///
    /// 送るのは 2 つだけ:
    /// <list type="bullet">
    ///   <item><b>Go</b> — 体験者が最後の異常を解除した縁（<see cref="ShowControlClient.CurseReleasedCount"/> が増えた）。
    ///     台車は自分の NVS に保存した「待つ → 回転 → 前進」を始める。動きの中身はここでは持たない</item>
    ///   <item><b>Cut</b> — その後、帰還の結末で終幕（電源断）が始まった縁。台車は時刻を記録し、
    ///     設定によっては止まる。Go を送っていない回では送らない</item>
    /// </list>
    ///
    /// ⚠ Go は 1 ランに 1 回。<see cref="ShowControlClient.CurseReleasedCount"/> は単調に増えるだけで
    /// ラン開始でも 0 へ戻らないので、「増えた縁」で見る。最初の観測は種にするだけで送らない
    /// （起動の途中で生えたときに、前の体験者の解除で台車が走り出さないように）。
    /// </summary>
    public sealed class RomiCartLinkLogic
    {
        public readonly struct Signals
        {
            public readonly bool go, cut;
            public Signals(bool go, bool cut) { this.go = go; this.cut = cut; }
        }

        private bool _seeded;
        private int _lastReleased;
        private ShowPhase _lastPhase;
        private bool _goSent, _cutSent;

        /// <summary>このランで Go を送ったか（Cut の前提）。</summary>
        public bool GoSentThisRun => _goSent;

        public Signals Tick(int releasedCount, ShowPhase phase, ShowEndingOutcome outcome)
        {
            if (!_seeded)
            {
                _seeded = true;
                _lastReleased = releasedCount;
                _lastPhase = phase;
                return default;
            }

            // 終わった相から出た ＝ 次の体験者。合図を送り直せるようにする。
            if (_lastPhase == ShowPhase.Finished && phase != ShowPhase.Finished)
            {
                _goSent = false;
                _cutSent = false;
            }

            bool go = false, cut = false;
            if (releasedCount != _lastReleased)
            {
                _lastReleased = releasedCount;
                if (!_goSent)
                {
                    _goSent = true;
                    go = true;
                }
            }

            if (phase == ShowPhase.Finished && _lastPhase != ShowPhase.Finished
                && _goSent && !_cutSent && outcome == ShowEndingOutcome.Released)
            {
                _cutSent = true;
                cut = true;
            }

            _lastPhase = phase;
            return new Signals(go, cut);
        }
    }
}
