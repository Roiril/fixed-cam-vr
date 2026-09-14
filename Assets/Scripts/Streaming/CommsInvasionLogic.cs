#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 通信画面の侵食度を、実際に表示された演出の観測から段階的に進める。
    /// 進んだ侵食度は明示的にリセットされるまで保持する。
    /// </summary>
    public sealed class CommsInvasionLogic
    {
        public const float FirstPovLevel = 0.25f;
        public const float ChainedPovLevel = 0.75f;
        public const float DollReplacementLevel = 1f;

        public float Level { get; private set; }

        /// <summary>観測によって侵食度が上がったときだけ true を返す。</summary>
        public bool Observe(int lap, int cameraIndex, string? activeStepCueId,
                            bool dollReplacementShowing)
        {
            float observed = 0f;

            if (lap == 3 && cameraIndex == 0 && dollReplacementShowing)
            {
                observed = DollReplacementLevel;
            }
            else if (lap == 2 && cameraIndex == 2)
            {
                if (IsChainedPov(activeStepCueId))
                    observed = ChainedPovLevel;
                else if (activeStepCueId == "pov_0")
                    observed = FirstPovLevel;
            }

            if (observed <= Level) return false;
            Level = observed;
            return true;
        }

        public void Reset() => Level = 0f;

        private static bool IsChainedPov(string? cueId)
            => cueId == "pov_1"
               || cueId == "pov_2"
               || cueId == "pov_3"
               || cueId == "pov_4";
    }
}
