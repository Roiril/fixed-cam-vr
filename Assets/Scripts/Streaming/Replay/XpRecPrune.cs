#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FixedCamVr.Streaming.Replay
{
    /// <summary>
    /// 体験の記録（<c>files/xprec/&lt;日時&gt;/</c>）を古い順に刈って、合計を上限以内に収める。
    /// <c>xprec/ON</c> の機を朝から夕方まで回すと、1 回 300MB〜1GB の記録が Quest の容量を食い尽くすため。
    ///
    /// 対象は日時名（<see cref="StampFormat"/>）のフォルダだけ。印 <c>ON</c> や想定外の名前は数えず、消さない。
    /// いま書いている記録は上限を超えていても消さない。
    /// 選び方（<see cref="Plan"/>）は純関数、ディスクを触るのは <see cref="Execute"/> だけ（UnityEngine 非依存）。
    /// </summary>
    public static class XpRecPrune
    {
        /// <summary>記録のフォルダ名の書式。<see cref="ExperienceRecorder"/> の録り始めが付ける名前と同じ。</summary>
        public const string StampFormat = "yyyyMMdd_HHmmss";

        /// <summary>記録 1 本（フォルダ名と大きさ）。</summary>
        public readonly struct Entry
        {
            public readonly string Name;
            public readonly long Bytes;

            public Entry(string name, long bytes)
            {
                Name = name;
                Bytes = bytes;
            }
        }

        /// <summary>刈った結果。</summary>
        public readonly struct Result
        {
            /// <summary>消せた記録の数。</summary>
            public readonly int Deleted;
            /// <summary>消せた記録の合計（byte）。</summary>
            public readonly long FreedBytes;
            /// <summary>消そうとして消せなかった記録の数。</summary>
            public readonly int Failed;
            /// <summary>刈った後に残った日時名の記録の合計（byte。いま書いている記録を含む）。</summary>
            public readonly long RemainingBytes;

            public Result(int deleted, long freedBytes, int failed, long remainingBytes)
            {
                Deleted = deleted;
                FreedBytes = freedBytes;
                Failed = failed;
                RemainingBytes = remainingBytes;
            }
        }

        /// <summary>日時名のフォルダか（<see cref="StampFormat"/> にちょうど合う名前だけ）。</summary>
        public static bool IsStampName(string? name)
            => name != null
               && name.Length == StampFormat.Length
               && DateTime.TryParseExact(name, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        /// <summary>
        /// 消す記録を古い順に返す。日時名の記録の合計（<paramref name="current"/> を含む）が
        /// <paramref name="limitBytes"/> 以下になるまで、古いものから必要な分だけ選ぶ。
        /// <paramref name="current"/> と日時名でないものは選ばない（合計にも数えない）。
        /// </summary>
        public static List<string> Plan(IReadOnlyList<Entry> entries, string? current, long limitBytes)
        {
            var candidates = new List<Entry>();
            long total = 0;
            foreach (Entry e in entries)
            {
                if (!IsStampName(e.Name)) continue;
                long bytes = Math.Max(0L, e.Bytes);
                total += bytes;
                if (!string.Equals(e.Name, current, StringComparison.Ordinal)) candidates.Add(new Entry(e.Name, bytes));
            }

            var victims = new List<string>();
            if (total <= limitBytes) return victims;
            // 日時名は桁が固定なので、名前の順が時刻の順。
            candidates.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (Entry e in candidates)
            {
                if (total <= limitBytes) break;
                victims.Add(e.Name);
                total -= e.Bytes;
            }
            return victims;
        }

        /// <summary>
        /// <paramref name="root"/> 直下の記録を数えて刈る。ディスクを列挙・削除するので背景スレッドから呼ぶ。
        /// 1 本の失敗で止めない。例外は投げない（列挙できなければ何も消さない）。
        /// </summary>
        public static Result Execute(string root, string? current, long limitBytes)
        {
            var entries = new List<Entry>();
            try
            {
                if (!Directory.Exists(root)) return new Result(0, 0, 0, 0);
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!IsStampName(name)) continue;
                    entries.Add(new Entry(name, SizeOf(dir)));
                }
            }
            catch
            {
                return new Result(0, 0, 0, 0);
            }

            long remaining = 0;
            var bytesOf = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Entry e in entries)
            {
                remaining += e.Bytes;
                bytesOf[e.Name] = e.Bytes;
            }

            int deleted = 0, failed = 0;
            long freed = 0;
            foreach (string name in Plan(entries, current, limitBytes))
            {
                try
                {
                    Directory.Delete(Path.Combine(root, name), true);
                    deleted++;
                    freed += bytesOf[name];
                    remaining -= bytesOf[name];
                }
                catch
                {
                    failed++;
                }
            }
            return new Result(deleted, freed, failed, remaining);
        }

        private static long SizeOf(string dir)
        {
            long bytes = 0;
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(file).Length; }
                    catch { /* 消えた・読めないファイルは数えない */ }
                }
            }
            catch { /* 読めないフォルダは 0 と数える */ }
            return bytes;
        }
    }
}
