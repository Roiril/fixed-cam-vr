#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 自作シェーダのソースを機械で見る。<b>シェーダの誤りは <c>unity.ps1 test</c> に 1 件も出ない</b>
    /// （コンパイルは走らず、実機では既定のマゼンタか無描画になる）ので、文字列で捕まえられるものだけここで落とす。
    ///
    /// 1. <b>HLSL の予約語を識別子に使わない</b>（<c>line</c> / <c>point</c> / <c>sample</c> …）。
    ///    2026-08-17（WalkGuide）と 2026-09-18（CommsPanelPlate・引数名 <c>line</c>）の 2 回踏んだ。
    ///    どちらも「syntax error: unexpected token」で、プレビューを焼くまで気づけなかった。
    /// 2. <b>1 つのシェーダに LightMode の無いパスを 2 つ置かない</b>。URP は最初の 1 つしか描かない
    ///    （2026-09-18・ステンシルを第 2 パスにして地が 1 画素も出なかった）。別のシェーダ・別の quad に分ける。
    /// </summary>
    public sealed class ShaderSourceLintTests
    {
        private static readonly string[] ReservedWords = { "line", "point", "sample", "matrix", "vector", "texture" };

        private static string[] ShaderFiles()
            => Directory.GetFiles(Path.Combine(Application.dataPath, "Art/Shaders"), "*.*", SearchOption.AllDirectories);

        /// <summary>行コメントと文字列を落とした 1 行（ブロックコメントは行単位で近似する）。</summary>
        private static string StripComments(string line)
        {
            line = Regex.Replace(line, "\"[^\"]*\"", "\"\"");
            int slash = line.IndexOf("//", System.StringComparison.Ordinal);
            return slash >= 0 ? line.Substring(0, slash) : line;
        }

        [Test]
        public void NoHlslReservedWord_IsUsedAsAnIdentifier()
        {
            var offenders = new System.Collections.Generic.List<string>();
            foreach (string path in ShaderFiles())
            {
                if (!path.EndsWith(".shader") && !path.EndsWith(".hlsl")) continue;
                bool inBlock = false;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i];
                    if (inBlock)
                    {
                        int end = code.IndexOf("*/", System.StringComparison.Ordinal);
                        if (end < 0) continue;
                        code = code.Substring(end + 2);
                        inBlock = false;
                    }
                    int start = code.IndexOf("/*", System.StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        int end = code.IndexOf("*/", start + 2, System.StringComparison.Ordinal);
                        if (end < 0) { code = code.Substring(0, start); inBlock = true; }
                        else code = code.Substring(0, start) + code.Substring(end + 2);
                    }
                    code = StripComments(code);
                    foreach (string word in ReservedWords)
                    {
                        // 型名の直後（`float4 line`）、代入の左辺、メンバ参照（`line.x`）のどれかなら識別子。
                        if (Regex.IsMatch(code, $@"\b(float[234]?|half[234]?|int|uint|bool)\s+{word}\b")
                            || Regex.IsMatch(code, $@"(^|[\s(,])\s*{word}\s*(=[^=]|\.[a-z])"))
                            offenders.Add($"{Path.GetFileName(path)}:{i + 1} '{word}' — {lines[i].Trim()}");
                    }
                }
            }
            Assert.IsEmpty(offenders, "HLSL の予約語を識別子に使っている（構文エラーで実機は既定色になる）:\n"
                                      + string.Join("\n", offenders));
        }

        [Test]
        public void NoShader_HasTwoUntaggedPasses()
        {
            var offenders = new System.Collections.Generic.List<string>();
            foreach (string path in ShaderFiles())
            {
                if (!path.EndsWith(".shader")) continue;
                string text = File.ReadAllText(path);
                int passes = Regex.Matches(text, @"(?m)^\s*Pass\s*(\{|$)").Count;
                int tagged = Regex.Matches(text, "\"LightMode\"").Count;
                if (passes - tagged >= 2)
                    offenders.Add($"{Path.GetFileName(path)}: パス {passes} 本のうち LightMode 無しが {passes - tagged} 本");
            }
            Assert.IsEmpty(offenders, "URP は LightMode の無いパスを最初の 1 つしか描かない。別のシェーダ・別の quad に分ける:\n"
                                      + string.Join("\n", offenders));
        }
    }
}
