#nullable enable
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 台本の take を異常単位へ対応付ける。分母は来訪者が見た数ではなく、設定された台本の異常数。
    /// 2 周目 C の予備動作と接近は同じ異常として扱う。
    /// </summary>
    public static class ShowAnomalyCatalog
    {
        public static int Count(ShowTimelineSegmentDef[]? segments)
        {
            var anomalies = new HashSet<string>();
            if (segments == null) return 0;

            foreach (ShowTimelineSegmentDef? segment in segments)
            {
                if (segment?.takes == null) continue;
                foreach (ShowTakeDef? take in segment.takes)
                {
                    string? anomalyId = AnomalyId(take?.id);
                    if (anomalyId != null) anomalies.Add(anomalyId);
                }
            }
            return anomalies.Count;
        }

        /// <summary>台本にあるが対応表にない take の本数。Count の 0 と区別して確認できる。</summary>
        public static int CountUnknown(ShowTimelineSegmentDef[]? segments)
        {
            if (segments == null) return 0;
            int count = 0;
            foreach (ShowTimelineSegmentDef? segment in segments)
            {
                if (segment?.takes == null) continue;
                foreach (ShowTakeDef? take in segment.takes)
                    if (AnomalyId(take?.id) == null) count++;
            }
            return count;
        }

        private static string? AnomalyId(string? takeId)
        {
            switch (takeId)
            {
                case "L1C1#0": return "L1C1#0";
                case "L2C0#0": return "L2C0#0";
                case "L2C1#0": return "L2C1#0";
                case "L2C2#0":
                case "L2C2#1": return "L2C2";
                case "L3C0#0": return "L3C0#0";
                case "L3C1#0": return "L3C1#0";
                case "L3C2#0": return "L3C2#0";
                case "L4C0#0": return "L4C0#0";
                default: return null;
            }
        }
    }
}
