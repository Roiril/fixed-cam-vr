#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 壁時計。ネットワーク・ファイル I/O の待ち、性能計測など、<b>本物の実時間</b>が要る所だけが読む。
    /// <see cref="SimTime"/> が固定刻みでも実時間を返す。体験の進行に効く読みはここではなく <see cref="SimTime"/>。
    /// </summary>
    public static class WallClock
    {
        /// <summary><c>Time.realtimeSinceStartup</c>（固定刻みの影響を受けない）。</summary>
        public static float Now => Time.realtimeSinceStartup;
    }
}
