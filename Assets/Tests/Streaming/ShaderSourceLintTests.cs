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

        // ---- 滲んで現れる・長押しで膨らむ（0349）--------------------------------------------

        private static string SharedHlsl()
            => File.ReadAllText(Path.Combine(Application.dataPath, "Art/Shaders/Streaming/CrtCompositeShared.hlsl"));

        private static string CompositeShader()
            => File.ReadAllText(Path.Combine(Application.dataPath, "Art/Shaders/Streaming/ScreenComposite.shader"));

        private static readonly string[] BleedTextures = { "_RevealTex", "_PressMaskTex", "_PressTex" };
        private static readonly string[] BleedScalars =
            { "_OverlayRevealOn", "_OverlayReveal", "_OverlayRevealSoft", "_OverlayMul", "_OverlayPress", "_PressCenter", "_PressScale" };

        /// <summary>
        /// Properties に無い uniform は、マテリアルを作り直したときに既定値が入らない
        /// （<c>_OverlayRevealOn</c> が 0 でないと全 cue の画が順の画で欠ける）。CBUFFER に無ければ SRP Batcher が外れる。
        /// </summary>
        [Test]
        public void BleedUniforms_AreDeclaredInPropertiesAndCbuffer()
        {
            string shader = CompositeShader();
            string hlsl = SharedHlsl();
            foreach (string name in BleedTextures)
            {
                StringAssert.IsMatch($@"(?m)^\s*{name}\(""[^""]*"",\s*2D\)", shader, $"Properties に {name} が無い");
                StringAssert.IsMatch($@"TEXTURE2D\({name}\);", hlsl, $"HLSL に {name} が無い");
            }
            foreach (string name in BleedScalars)
            {
                StringAssert.IsMatch($@"(?m)^\s*{name}\(""", shader, $"Properties に {name} が無い");
                StringAssert.IsMatch($@"(?m)^\s*(float|float4|half)\s+{name};", hlsl, $"CBUFFER に {name} が無い");
            }
            StringAssert.IsMatch(@"(?m)^\s*_OverlayRevealOn\(""[^""]*"",\s*Float\)\s*=\s*0\s*$", shader,
                                 "_OverlayRevealOn の既定は 0（滲まない cue は従来の画のまま）");
            StringAssert.IsMatch(@"(?m)^\s*_OverlayPress\(""[^""]*"",\s*Range\([^)]*\)\)\s*=\s*0\s*$", shader,
                                 "_OverlayPress の既定は 0（膨らみの画は読まない）");
        }

        /// <summary>
        /// 新しい sampler を足さない。Quest（GLES3 / Vulkan）の sampler の数には上限があり、ScreenComposite は既に多い。
        /// 順の画と press マスクはマスクの sampler、膨らみは差し替えの sampler を借りる。
        /// </summary>
        [Test]
        public void BleedTextures_BorrowExistingSamplers()
        {
            string hlsl = SharedHlsl();
            foreach (string name in BleedTextures)
                StringAssert.DoesNotContain($"sampler{name}", hlsl, $"{name} 専用の sampler を宣言している");
            StringAssert.Contains("SAMPLE_TEXTURE2D_LOD(_RevealTex, sampler_MaskTex", hlsl);
            StringAssert.Contains("SAMPLE_TEXTURE2D_LOD(_PressMaskTex, sampler_MaskTex", hlsl);
            StringAssert.Contains("SAMPLE_TEXTURE2D_LOD(_PressTex, sampler_OverlayTex", hlsl);
        }

        /// <summary>
        /// 順の画・膨らみの画は、uniform の分岐の中でだけ読む。分岐の外で読むと、使わない cue でも毎画素 2〜3 枚を
        /// 余計に引く（90Hz の両眼で効く）。読む行の直前の <c>[branch]</c> が、その uniform を見ていること。
        /// </summary>
        [Test]
        public void BleedTextures_AreReadOnlyInsideTheirBranch()
        {
            string[] lines = SharedHlsl().Split('\n');
            var offenders = new System.Collections.Generic.List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string gate = lines[i].Contains("SAMPLE_TEXTURE2D_LOD(_RevealTex") ? "_OverlayRevealOn"
                            : lines[i].Contains("SAMPLE_TEXTURE2D_LOD(_PressMaskTex")
                              || lines[i].Contains("SAMPLE_TEXTURE2D_LOD(_PressTex") ? "_OverlayPress"
                            : "";
                if (gate.Length == 0) continue;
                int j = i;
                while (j >= 0 && !lines[j].Contains("[branch]")) j--;
                if (j < 0 || !lines[j].Contains(gate) || i - j > 12)
                    offenders.Add($"{i + 1}: {lines[i].Trim()}（直前の [branch] が {gate} を見ていない）");
            }
            Assert.IsEmpty(offenders, string.Join("\n", offenders));
        }

        /// <summary>
        /// シェーダの数値は試し描き（<c>tools/gen-plate/bleed_preview.py</c>）と同じ。Python 側の突き合わせは
        /// <c>tools/test_bleed_log.py</c>。ここは HLSL の式が消えていないことだけを見る。
        /// </summary>
        [Test]
        public void BleedFormulas_MatchThePreview()
        {
            string hlsl = SharedHlsl();
            StringAssert.Contains("revealEdge = revealA * (1.0 - revealA) * 4.0;", hlsl);
            StringAssert.Contains("ratio * (1.0 - 0.22 * revealEdge)", hlsl);
            StringAssert.Contains("overlay *= (1.0 - 0.30 * revealEdge);", hlsl);
            StringAssert.Contains("(0.12 * _OverlayPress * pressM)", hlsl);
            StringAssert.Contains("smoothstep(0.30, 0.62, saturate(_OverlayPress))", hlsl);
        }

        // ---- 長押しでライブが透ける（markBleed）---------------------------------------------

        /// <summary>
        /// <c>_LiveBleed</c>（既定 0・0..1）と体の重みの閾値 <c>_LiveBleedLo</c> / <c>_LiveBleedHi</c>（既定 0.05 / 0.16）は
        /// Properties と CBUFFER の両方にある。掛かるのは隠す層 2 つ（差し替えの 1 層目・2 層目）の不透明度だけで、
        /// 通常の 1 層目と 2 層目は体の所だけ（ライブと素材の差の重み）、掛け算の 1 層目は一様に薄める。
        /// CG 人形の合成（<c>s</c>）には掛けない — 人形は消さず、下のライブだけが透ける。
        /// </summary>
        [Test]
        public void LiveBleed_IsDeclared_DefaultsToZero_AndOnlyThinsTheHidingLayers()
        {
            string shader = CompositeShader();
            string hlsl = SharedHlsl();
            StringAssert.IsMatch(@"(?m)^\s*_LiveBleed\(""[^""]*"",\s*Range\(0,\s*1\)\)\s*=\s*0\s*$", shader,
                                 "_LiveBleed の既定は 0（旗の無い台本の画は変わらない）");
            StringAssert.IsMatch(@"(?m)^\s*_LiveBleedLo\(""[^""]*"",\s*Range\([^)]*\)\)\s*=\s*0\.05\s*$", shader,
                                 "体の重みの下の閾値は既定 0.05");
            StringAssert.IsMatch(@"(?m)^\s*_LiveBleedHi\(""[^""]*"",\s*Range\([^)]*\)\)\s*=\s*0\.16\s*$", shader,
                                 "体の重みの上の閾値は既定 0.16");
            foreach (string name in new[] { "_LiveBleed", "_LiveBleedLo", "_LiveBleedHi" })
                StringAssert.IsMatch($@"(?m)^\s*float\s+{name};", hlsl, $"CBUFFER に {name} が無い");

            StringAssert.Contains("half alpha = saturate(mask * revealA * _OverlayStrength);", hlsl);
            StringAssert.Contains("alpha *= (1.0 - _LiveBleed);", hlsl, "掛け算の 1 層目は一様に薄める");
            StringAssert.Contains("alpha *= (1.0 - _LiveBleed * LiveBleedBodyW(liveC, coverC));", hlsl,
                                  "通常の 1 層目は体の所だけ透ける");
            StringAssert.Contains("bleed2 = _LiveBleed * LiveBleedBodyW(liveC, coverC);", hlsl, "2 層目も体の所だけ透ける");
            StringAssert.Contains("col = lerp(col, ov2, saturate(mask2 * _Overlay2Strength) * (1.0 - bleed2));", hlsl);
            StringAssert.Contains("float s = saturate(_CgStrength) * cgIn;", hlsl);
            StringAssert.Contains("smoothstep(_LiveBleedLo, max(_LiveBleedHi, _LiveBleedLo + 1e-3), max(d.r, max(d.g, d.b)))", hlsl,
                                  "体の重みは rgb の差の最大成分を Lo..Hi で写す");

            int uniform = Regex.Matches(hlsl, @"\(1\.0 - _LiveBleed\)").Count;
            Assert.That(uniform, Is.EqualTo(1), "一様に薄めるのは掛け算の 1 層目だけ");
            int bodyW = Regex.Matches(hlsl, @"_LiveBleed \* LiveBleedBodyW\(").Count;
            Assert.That(bodyW, Is.EqualTo(2), "体の重みを掛けるのは通常の 1 層目と 2 層目の 2 つだけ");
        }

        /// <summary>
        /// 体の重みのための読み直し（<c>lod + LIVE_BLEED_LOD_STEP</c>）は <c>_LiveBleed</c> の分岐の中だけ。
        /// 外で読むと、旗の無い走行でも毎画素 2〜3 枚を余計に引く（90Hz の両眼で効く）。
        /// 段は今の lod より 2 段粗い（粒と符号化のゆらぎを差として拾わない）。
        /// </summary>
        [Test]
        public void LiveBleed_CoarseRereads_AreOnlyInsideTheBleedBranch()
        {
            string hlsl = SharedHlsl();
            StringAssert.IsMatch(@"(?m)^#define\s+LIVE_BLEED_LOD_STEP\s+2\.0\b", hlsl);
            string[] lines = hlsl.Split('\n');
            var offenders = new System.Collections.Generic.List<string>();
            int reads = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("SAMPLE_TEXTURE2D_LOD(") || !lines[i].Contains("bleedLod")) continue;
                reads++;
                int j = i;
                while (j >= 0 && !lines[j].Contains("[branch]")) j--;
                if (j < 0 || !lines[j].Contains("_LiveBleed > 0.001") || i - j > 12)
                    offenders.Add($"{i + 1}: {lines[i].Trim()}（直前の [branch] が _LiveBleed を見ていない）");
            }
            Assert.That(reads, Is.GreaterThanOrEqualTo(4), "ライブと素材を 1 層目・2 層目でそれぞれ読み直している");
            Assert.IsEmpty(offenders, string.Join("\n", offenders));
            Assert.That(Regex.Matches(hlsl, @"float bleedLod = lod \+ LIVE_BLEED_LOD_STEP;").Count, Is.EqualTo(2));
        }
    }
}
