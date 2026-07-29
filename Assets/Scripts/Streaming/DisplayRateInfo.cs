#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// いま実際に出ている表示リフレッシュレート (Hz)。
    ///
    /// Meta XR SDK（OVRManager）を触れるのは Assembly-CSharp 側（<c>Assets/Scripts/OvrBridge/</c>）だけなので、
    /// 実測値をここへ書いてもらい、Streaming 側は読むだけにする（asmdef の向きを増やさない）。
    /// 既定は Quest 3 がアプリの要求なしで走る 72Hz。
    /// </summary>
    public static class DisplayRateInfo
    {
        /// <summary>Quest 3 の既定（アプリが要求しないとこれになる）。</summary>
        public const float DefaultHz = 72f;

        /// <summary>現在の表示レート (Hz)。</summary>
        public static float CurrentHz { get; private set; } = DefaultHz;

        /// <summary>実測 / 適用済みの表示レートを設定する（OvrBridge から）。</summary>
        public static void Report(float hz)
        {
            if (hz > 1f) CurrentHz = hz;
        }
    }
}
