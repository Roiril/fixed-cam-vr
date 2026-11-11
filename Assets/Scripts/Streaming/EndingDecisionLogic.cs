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

    /// <summary>
    /// 締めの警告を読み切ってからの離脱と時間切れを判定する。
    /// 報告（Released）が最優先。人形が画面の左半分に入る Trapped（<c>endingDollOnLeftHalf</c>）は
    /// 警告を読み切ったか（<c>_promptReadable</c>）の確認より前に立つ。離脱と時間切れは読み切ってからだけ。
    ///
    /// 時間切れの手前 <see cref="SinkSec"/> 秒は主画面の映像が沈む（<see cref="SinkAt"/>）。
    /// 進みは時間切れの判定と同じ時計・同じ値（③b の全文表示からの経過）から引く。時計を 2 つ持たない。
    /// </summary>
    public sealed class EndingDecisionLogic
    {
        public const float PromptLimitSec = 15f;

        /// <summary>時間切れの何秒前から映像が沈み始めるか。</summary>
        public const float SinkSec = 6f;

        /// <summary>沈み切ったときに映像へ掛ける明るさの係数（線形光）。黒へは落とさない — 落ちるのは時間切れの瞬間だけ。</summary>
        public const float SinkFloor = 0.35f;

        private bool _promptReadable;
        private bool _releaseReported;
        private float _promptReadableAt;
        private float _decidedAt;

        public ShowEndingOutcome Outcome { get; private set; } = ShowEndingOutcome.Pending;
        public bool TrappedByLeftHalf { get; private set; }

        /// <summary>Trapped が③b の全文表示から <see cref="PromptLimitSec"/> 経った時間切れで決まったか（離脱・区間の移動・左半分ではない）。</summary>
        public bool TrappedByTimeout { get; private set; }
        public bool ReleaseReported => _releaseReported;
        public bool PromptReadable => _promptReadable;
        public float PromptReadableAt => _promptReadable ? _promptReadableAt : -1f;

        public void ResetRun()
        {
            Outcome = ShowEndingOutcome.Pending;
            _promptReadable = false;
            _releaseReported = false;
            _promptReadableAt = 0f;
            _decidedAt = 0f;
            TrappedByLeftHalf = false;
            TrappedByTimeout = false;
        }

        /// <summary>
        /// ③b の全文表示からの経過秒 → 沈みの進み 0..1。残り <see cref="SinkSec"/> 秒より前は 0、
        /// <see cref="PromptLimitSec"/> で 1、その間は時間に比例する。負（まだ読めていない）と NaN は 0。
        /// </summary>
        public static float SinkAmount(float elapsedSincePromptSec)
        {
            if (float.IsNaN(elapsedSincePromptSec) || elapsedSincePromptSec < 0f) return 0f;
            float start = PromptLimitSec - SinkSec;
            if (elapsedSincePromptSec <= start) return 0f;
            if (elapsedSincePromptSec >= PromptLimitSec) return 1f;
            return (elapsedSincePromptSec - start) / SinkSec;
        }

        /// <summary>
        /// 沈みの進み → 映像に掛ける明るさの係数（1 → <see cref="SinkFloor"/>・線形光）。
        /// 見た目で緩急をつける曲線は <see cref="PerceptualFade.Darken"/>（暗転と同じ正本）。
        /// </summary>
        public static float SinkBrightness(float sink)
            => 1f - (1f - SinkFloor) * PerceptualFade.Darken(sink);

        /// <summary>
        /// 沈みの進み → 周辺の落ち込み（<c>_HeartVignette</c> に書く値）。強さの上限は心音区間の常時の値
        /// （<see cref="CameraFeelFx.HeartVignetteBase"/>）で、新しい強さを作らない。
        /// </summary>
        public static float SinkVignette(float sink)
            => CameraFeelFx.HeartVignetteBase * PerceptualFade.Darken(sink);

        /// <summary>
        /// 報告で呪いが解けた後の沈み。報告の縁の値から、劣化の戻り（<see cref="ScreenDecayLogic.ReleaseK"/>・
        /// <see cref="ScreenDecayLogic.ReleaseSec"/>）と同じ進みで 0 へ戻る。戻りの時計を別に持たない。
        /// </summary>
        public static float SinkAfterRelease(float sinkAtRelease, float releaseK)
        {
            if (float.IsNaN(sinkAtRelease) || float.IsNaN(releaseK)) return 0f;
            float s = sinkAtRelease < 0f ? 0f : sinkAtRelease > 1f ? 1f : sinkAtRelease;
            float k = releaseK < 0f ? 0f : releaseK > 1f ? 1f : releaseK;
            return s * (1f - k);
        }

        /// <summary>
        /// いま画へ出す沈み 0..1。③b を読めてから結末が決まるまでは時計どおりに進む（長押しの途中も止めない）。
        /// 時間切れの Trapped は決まった時点の値（1）で止まる。報告・離脱・区間の移動・左半分・中断では 0。
        /// 報告からの戻りはここでは引かない（呼び出し側が <see cref="ScreenDecayLogic.ReleaseK"/> で引く）。
        /// </summary>
        public float SinkAt(float now)
        {
            if (!_promptReadable) return 0f;
            switch (Outcome)
            {
                case ShowEndingOutcome.Pending:
                    return SinkAmount(now - _promptReadableAt);
                case ShowEndingOutcome.Trapped:
                    return TrappedByTimeout ? SinkAmount(_decidedAt - _promptReadableAt) : 0f;
                default:
                    return 0f;
            }
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
            if (_releaseReported) { Outcome = ShowEndingOutcome.Released; _decidedAt = now; return; }
            if (endingDollOnLeftHalf)
            {
                TrappedByLeftHalf = true;
                Outcome = ShowEndingOutcome.Trapped;
                _decidedAt = now;
                return;
            }
            if (!_promptReadable) return;
            bool left = outsideClosingArea || (segmentKnown && !ShowRunDefaults.IsClosingSegment(lap, camera));
            bool timedOut = now - _promptReadableAt >= PromptLimitSec;
            if (!left && !timedOut) return;
            // 離脱と時間切れが同じフレームなら時間切れとして扱う（画は既に沈み切っている）。
            TrappedByTimeout = timedOut;
            Outcome = ShowEndingOutcome.Trapped;
            _decidedAt = now;
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
