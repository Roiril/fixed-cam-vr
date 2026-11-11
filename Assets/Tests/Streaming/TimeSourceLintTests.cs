#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 実時間の時計を直接読んでいないかをソースの文字列で見る。体験の時計は <see cref="SimTime"/>、
    /// 壁時計は <see cref="WallClock"/> から読む。<c>Time.unscaledTime</c> / <c>Time.unscaledDeltaTime</c> /
    /// <c>Time.realtimeSinceStartup</c> を直接読むと、固定刻みで回したときにそこだけ実時間で進み、
    /// 検証の走行が実機と違う順序で起きる（落ちもしないし警告も出ない）。
    ///
    /// コメント・doc コメント・文字列リテラルの中の言及は数えない。補間文字列の <c>{...}</c> の中はコードとして数える。
    /// 置き換えは <c>tools/simtime-migrate.py</c>（同じ字句の切り分けを Python で持つ）。
    /// </summary>
    public sealed class TimeSourceLintTests
    {
        /// <summary>時計そのものを定義するファイル。</summary>
        private static readonly string[] ClockFiles =
        {
            "Streaming/SimTime.cs",
            "Streaming/WallClock.cs",
        };

        /// <summary>
        /// まだ直接読んでいてよいファイル。いまは無い。増やさない（置き換えの道具は tools/simtime-migrate.py）。
        /// </summary>
        private static readonly string[] PendingFiles = { };

        private static readonly Regex DirectRead =
            new Regex(@"(?<![\w.])(?:UnityEngine\.)?Time\.(unscaledTime|unscaledDeltaTime|realtimeSinceStartup)\b",
                RegexOptions.Compiled);

        private static string ScriptsRoot => Path.Combine(Application.dataPath, "Scripts");

        private static string RelativeOf(string path)
        {
            string root = Path.GetFullPath(ScriptsRoot).Replace('\\', '/').TrimEnd('/');
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length + 1)
                : full;
        }

        [Test]
        public void NoScript_ReadsUnityRealtimeClocksDirectly()
        {
            var allowed = new HashSet<string>(ClockFiles);
            allowed.UnionWith(PendingFiles);
            var offenders = new List<string>();
            foreach (string path in Directory.GetFiles(ScriptsRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = RelativeOf(path);
                if (allowed.Contains(rel)) continue;
                string src = File.ReadAllText(path);
                string code = CodeMask(src);
                foreach (Match m in DirectRead.Matches(code))
                {
                    int line = LineOf(src, m.Index);
                    offenders.Add($"{rel}:{line} {m.Value} — {LineText(src, m.Index).Trim()}");
                }
            }
            Assert.IsEmpty(offenders,
                "Unity の実時間を直接読んでいる。体験の進行に効くなら SimTime.Now / Dt / Realtime、" +
                "ネットワーク・ファイル I/O の待ちか性能計測なら WallClock.Now から読む" +
                "（置き換えは py -3.11 tools/simtime-migrate.py --apply）:\n" + string.Join("\n", offenders));
        }

        [Test]
        public void PendingFiles_StillExist()
        {
            // 一覧が腐ると、消えたファイルの名前で別のファイルが素通りしかねない。
            foreach (string rel in PendingFiles)
                Assert.IsTrue(File.Exists(Path.Combine(ScriptsRoot, rel)), $"除外の一覧に無いファイルがある: {rel}");
        }

        [Test]
        public void CodeMask_KeepsOnlyCode()
        {
            // 自分の計器を校正する。数えるべき所と数えてはいけない所を 1 つずつ。
            const string src =
                "// Time.unscaledTime\n" +
                "/* Time.unscaledDeltaTime */ float a = Time.unscaledTime;\n" +
                "string s = \"Time.realtimeSinceStartup\"; string v = @\"Time.unscaledTime \"\" x\";\n" +
                "Debug.Log($\"t={Time.realtimeSinceStartup:F2} {{Time.unscaledTime}}\");\n" +
                "char c = '\"'; float b = Time.unscaledDeltaTime; // Time.unscaledTime\n" +
                "#region Player's Time.unscaledTime\n" +
                "float d = UnityTime.unscaledTime + x.Time.unscaledTime + Time.unscaledTimeAsDouble;\n" +
                "float e = UnityEngine.Time.realtimeSinceStartup;\n";
            string code = CodeMask(src);
            Assert.AreEqual(src.Length, code.Length, "位置が崩れると行番号がずれる");
            var hits = new List<string>();
            foreach (Match m in DirectRead.Matches(code)) hits.Add($"{LineOf(src, m.Index)}:{m.Groups[1].Value}");
            CollectionAssert.AreEqual(
                new[] { "2:unscaledTime", "4:realtimeSinceStartup", "5:unscaledDeltaTime", "8:realtimeSinceStartup" }, hits);
        }

        // ---- 字句の切り分け（tools/simtime-migrate.py の code_mask と同じ規則） ---------------------

        /// <summary>コメント・文字列・文字リテラル・プリプロセッサ行を空白に潰した同じ長さの文字列（改行は残す）。</summary>
        internal static string CodeMask(string src)
        {
            var mask = new Masker(src);
            mask.Code(0, inHole: false);
            return mask.Result;
        }

        private sealed class Masker
        {
            private readonly string _src;
            private readonly StringBuilder _out;
            private readonly int _n;

            public Masker(string src)
            {
                _src = src;
                _out = new StringBuilder(src);
                _n = src.Length;
            }

            public string Result => _out.ToString();

            private char At(int i) => i < _n ? _src[i] : '\0';

            private void Blank(int a, int b)
            {
                for (int k = a; k < b && k < _n; k++)
                    if (_out[k] != '\r' && _out[k] != '\n') _out[k] = ' ';
            }

            private bool AtLineStart(int i)
            {
                int j = i - 1;
                while (j >= 0 && (_src[j] == ' ' || _src[j] == '\t')) j--;
                return j < 0 || _src[j] == '\n';
            }

            private int ToLineEnd(int i)
            {
                int j = _src.IndexOf('\n', i);
                return j < 0 ? _n : j;
            }

            private int Regular(int i)
            {
                int j = i + 1;
                while (j < _n)
                {
                    char c = _src[j];
                    if (c == '\\') { j += 2; continue; }
                    if (c == '"') { j++; break; }
                    if (c == '\n') break;
                    j++;
                }
                Blank(i, j);
                return j;
            }

            private int Verbatim(int start, int i)
            {
                int j = i + 1;
                while (j < _n)
                {
                    if (_src[j] == '"')
                    {
                        if (At(j + 1) == '"') { j += 2; continue; }
                        j++;
                        break;
                    }
                    j++;
                }
                Blank(start, j);
                return j;
            }

            private int CharLiteral(int i)
            {
                int j = i + 1;
                j += At(j) == '\\' ? 2 : 1;
                while (j < _n && _src[j] != '\'' && _src[j] != '\n') j++;
                if (j < _n && _src[j] == '\'') j++;
                Blank(i, j);
                return j;
            }

            private int Interpolated(int start, int i, bool verbatim)
            {
                Blank(start, i + 1);
                int j = i + 1;
                while (j < _n)
                {
                    char c = _src[j];
                    if (!verbatim && c == '\\') { Blank(j, j + 2); j += 2; continue; }
                    if (c == '"')
                    {
                        if (verbatim && At(j + 1) == '"') { Blank(j, j + 2); j += 2; continue; }
                        Blank(j, j + 1);
                        return j + 1;
                    }
                    if (c == '{')
                    {
                        if (At(j + 1) == '{') { Blank(j, j + 2); j += 2; continue; }
                        Blank(j, j + 1);
                        j = Code(j + 1, inHole: true);
                        if (j < _n) { Blank(j, j + 1); j++; }
                        continue;
                    }
                    if (c == '\n' && !verbatim) return j;
                    Blank(j, j + 1);
                    j++;
                }
                return j;
            }

            public int Code(int i, bool inHole)
            {
                int depth = 0;
                while (i < _n)
                {
                    char c = _src[i];
                    char nx = At(i + 1);
                    if (c == '/' && nx == '/') { int j = ToLineEnd(i); Blank(i, j); i = j; continue; }
                    if (c == '/' && nx == '*')
                    {
                        int j = _src.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                        j = j < 0 ? _n : j + 2;
                        Blank(i, j);
                        i = j;
                        continue;
                    }
                    if (c == '#' && !inHole && AtLineStart(i)) { int j = ToLineEnd(i); Blank(i, j); i = j; continue; }
                    if (c == '$' && nx == '"') { i = Interpolated(i, i + 1, verbatim: false); continue; }
                    if (((c == '$' && nx == '@') || (c == '@' && nx == '$')) && At(i + 2) == '"')
                    {
                        i = Interpolated(i, i + 2, verbatim: true);
                        continue;
                    }
                    if (c == '@' && nx == '"') { i = Verbatim(i, i + 1); continue; }
                    if (c == '"') { i = Regular(i); continue; }
                    if (c == '\'') { i = CharLiteral(i); continue; }
                    if (inHole)
                    {
                        if (c == '(' || c == '[' || c == '{') depth++;
                        else if (c == ')' || c == ']') depth--;
                        else if (c == '}')
                        {
                            if (depth == 0) return i;
                            depth--;
                        }
                        else if (c == ':' && depth == 0 && nx != ':')
                        {
                            // 書式指定（{x:F2}）は文字列の一部
                            int j = i;
                            while (j < _n && _src[j] != '}' && _src[j] != '\n') j++;
                            Blank(i, j);
                            i = j;
                            continue;
                        }
                    }
                    i++;
                }
                return i;
            }
        }

        private static int LineOf(string src, int index)
        {
            int line = 1;
            for (int k = 0; k < index && k < src.Length; k++)
                if (src[k] == '\n') line++;
            return line;
        }

        private static string LineText(string src, int index)
        {
            int start = src.LastIndexOf('\n', System.Math.Max(0, index - 1)) + 1;
            int end = src.IndexOf('\n', index);
            return end < 0 ? src.Substring(start) : src.Substring(start, end - start);
        }
    }
}
