#nullable enable
using System.Collections.Generic;

namespace TableDuoVr.Net
{
    /// <summary>
    /// リプレイの時刻→フレーム選択カーソル（純アルゴリズム）。
    /// 時刻 t 以下の直近 index を返す。順方向はカーソル前進 O(1)、逆方向シークは二分探索。
    /// <see cref="ReplayViewer"/> の private static Advance をそのまま移植（可視化＝テスト固定のため）。
    /// </summary>
    public static class ReplayCursor
    {
        /// <summary>時刻 t 以下の直近 index。順方向はカーソル前進、逆方向シークは二分探索。</summary>
        public static int Advance(List<long> times, ref int cursor, long t)
        {
            if (times.Count == 0 || t < times[0]) return -1;
            if (cursor >= times.Count) cursor = times.Count - 1;
            if (times[cursor] > t)
            {
                int lo = 0, hi = cursor;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (times[mid] <= t) lo = mid;
                    else hi = mid - 1;
                }
                cursor = lo;
            }
            else
            {
                while (cursor + 1 < times.Count && times[cursor + 1] <= t) cursor++;
            }
            return cursor;
        }
    }
}
