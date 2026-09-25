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
        public bool TrappedByLeftHalf { get; private set; }
        public bool ReleaseReported => _releaseReported;
        public bool PromptReadable => _promptReadable;
        public float PromptReadableAt => _promptReadable ? _promptReadableAt : -1f;

        public void ResetRun()
        {
            Outcome = ShowEndingOutcome.Pending;
            _promptReadable = false;
            _releaseReported = false;
            _promptReadableAt = 0f;
            TrappedByLeftHalf = false;
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

        public void Tick(float now, bool segmentKnown, int lap, int camera,
                         bool outsideClosingArea = false, bool endingDollOnLeftHalf = false)
        {
            if (Outcome != ShowEndingOutcome.Pending) return;
            // 報告と人形の左半分到達・離脱・制限時間が同じフレームなら、報告を先に確定する。
            if (_releaseReported) { Outcome = ShowEndingOutcome.Released; return; }
            if (endingDollOnLeftHalf)
            {
                TrappedByLeftHalf = true;
                Outcome = ShowEndingOutcome.Trapped;
                return;
            }
            if (!_promptReadable) return;
            if (outsideClosingArea || (segmentKnown && (lap != 4 || camera != 0))
                || now - _promptReadableAt >= PromptLimitSec)
                Outcome = ShowEndingOutcome.Trapped;
        }

        /// <summary>描画済みの足位置が画面内の左半分にあるか。</summary>
        public static bool IsFootOnLeftHalf(bool projectionValid, float u, float v)
            => projectionValid && IsFinite(u) && IsFinite(v)
               && u >= 0f && u < 0.5f && v >= 0f && v <= 1f;

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        public void Interrupt() => Outcome = ShowEndingOutcome.Interrupted;
    }
}
