#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>優先カメラ以外へ、1フレームにつき1台だけ順番に展開の機会を渡す。</summary>
    public sealed class StreamDecodeScheduler
    {
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
