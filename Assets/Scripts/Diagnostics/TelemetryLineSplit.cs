#nullable enable
using System.Collections.Generic;
using System.Text;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// テレメトリの 1 行を、Android の logcat が切らない長さに割る。
    /// <para>
    /// ⚠⚠ Quest の Unity は 1 回の <c>Debug.Log</c> を約 1020 バイトで黙って切る（2026-11-15 実測。
    /// logcat の行頭を含めて 1061 字で全行が止まっていた）。<c>ev=sum</c> は 1 行 1000 字を超えるので、
    /// <c>nzMax</c> 以降（コントローラ・ステータス・第 2 層・カメラごとの欄）が実機のログに 1 度も残っていなかった。
    /// Editor のログは切らないので、ツインの走行では見えない。
    /// </para>
    /// <para>
    /// 割った 2 行目以降は <c>[XP] + </c> で始める。読み手（<c>tools/analyze-xp-log.py</c> の
    /// <c>merge_continuations</c> ほか）は直前の行の続きとして継ぎ直す。割るのは空白の位置だけで、
    /// <c>key=value</c> と <c>|c0</c> の区切りは切らない。
    /// </para>
    /// </summary>
    public static class TelemetryLineSplit
    {
        /// <summary>1 行のバイト数の上限（UTF-8）。切られる実測 ≒1020 に余白を取る。</summary>
        public const int MaxLineBytes = 900;

        /// <summary>続きの行の頭（タグの後）。読み手と対。</summary>
        public const string ContinuationMark = "+";

        public static List<string> Split(string tag, string line, int maxBytes = MaxLineBytes)
        {
            var parts = new List<string>(2);
            if (Encoding.UTF8.GetByteCount(line) <= maxBytes)
            {
                parts.Add(line);
                return parts;
            }
            string head = tag + " " + ContinuationMark;
            var sb = new StringBuilder(maxBytes);
            int bytes = 0;
            foreach (string tok in line.Split(' '))
            {
                if (tok.Length == 0) continue;
                int tb = Encoding.UTF8.GetByteCount(tok);
                // 1 トークンだけで上限を超える行は割れないので、そのまま出す（切られるのは logcat の側）。
                if (sb.Length > 0 && bytes + 1 + tb > maxBytes)
                {
                    parts.Add(sb.ToString());
                    sb.Clear().Append(head);
                    bytes = Encoding.UTF8.GetByteCount(head);
                }
                if (sb.Length > 0) { sb.Append(' '); bytes++; }
                sb.Append(tok);
                bytes += tb;
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }
    }
}
