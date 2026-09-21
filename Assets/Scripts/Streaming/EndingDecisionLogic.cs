#nullable enable

namespace FixedCamVr.Streaming
{
    public enum ShowEndingOutcome
    {
        Pending,
        Released,
        Trapped,
        Interrupted,
    }

    /// <summary>締めの警告を読み切ってからの離脱と時間切れを判定する。</summary>
    public sealed class EndingDecisionLogic
    {
        public const float PromptLimitSec = 15f;

        private bool _promptReadable;
        private bool _releaseReported;
        private float _promptReadableAt;

        public ShowEndingOutcome Outcome { get; private set; } = ShowEndingOutcome.Pending;
        public bool ReleaseReported => _releaseReported;
        public bool PromptReadable => _promptReadable;
        public float PromptReadableAt => _promptReadable ? _promptReadableAt : -1f;

        public void ResetRun()
        {
            Outcome = ShowEndingOutcome.Pending;
            _promptReadable = false;
            _releaseReported = false;
            _promptReadableAt = 0f;
        }

        public void NotifyPromptReadable(float now)
        {
            if (Outcome != ShowEndingOutcome.Pending || _promptReadable) return;
            _promptReadable = true;
            _promptReadableAt = now;
        }

        public void NotifyReleased()
        {
            if (Outcome == ShowEndingOutcome.Pending) _releaseReported = true;
        }

        public void Tick(float now, bool segmentKnown, int lap, int camera, bool outsideClosingArea = false)
        {
            if (Outcome != ShowEndingOutcome.Pending) return;
            // 報告と離脱・制限時間が同じフレームなら、報告を先に確定する。
            if (_releaseReported) { Outcome = ShowEndingOutcome.Released; return; }
            if (!_promptReadable) return;
            if (outsideClosingArea || (segmentKnown && (lap != 4 || camera != 0))
                || now - _promptReadableAt >= PromptLimitSec)
                Outcome = ShowEndingOutcome.Trapped;
        }

        public void Interrupt() => Outcome = ShowEndingOutcome.Interrupted;
    }
}
