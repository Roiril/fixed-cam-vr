#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>優先カメラ以外へ、1フレームにつき1台だけ順番に展開の機会を渡す。</summary>
    public sealed class StreamDecodeScheduler
    {
        /// <summary>
        /// 背景カメラの展開間隔。背景の Texture はどこにも描かれず、切替は公開前に最新を展開するので、
        /// ここで要るのは「映像として読める」の証拠だけ。1.5 秒の鮮度判定と 2 秒の壊れ検出に間に合う値にする。
        /// </summary>
        public const float BackgroundIntervalSec = 1f / 3f;

        /// <summary>前回の機会から間隔が空いたか。<paramref name="fullRate"/> は記録中・再生中で、全数を展開する。</summary>
        public static bool BackgroundDue(float now, float nextDueAt, bool fullRate) => fullRate || now >= nextDueAt;

        private int _next;

        /// <summary>優先/無効の要素はtrue。背景が無ければ-1。配列は呼び手のキャッシュを読むだけ。</summary>
        public int NextBackground(bool[] priority)
        {
            int count = priority.Length;
            if (count == 0) { _next = 0; return -1; }
            if (_next >= count) _next = 0;
            for (int offset = 0; offset < count; offset++)
            {
                int index = (_next + offset) % count;
                if (priority[index]) continue;
                _next = (index + 1) % count;
                return index;
            }
            return -1;
        }
    }
}
