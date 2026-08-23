#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>Editor プレビュー（<c>EyesPreview</c>）は、本番（<c>AnomalyEyes</c>）が材質へ書く値を
    /// 1 つ残らず書く</b>。書き漏らすと<b>その値を触っても絵が 1 画素も変わらない計器</b>になり、
    /// 「変えたのに効かない」を絵から判定できなくなる。
    ///
    /// ⚠⚠ <b>この事故は 2 度起きている。</b> どちらも「プレビューは正しく描けている」ように見えて、
    /// 誤った結論まで出した:
    /// <list type="number">
    ///   <item>2026-08-17 <c>_EyeGaze</c>（待機の視線）— プレビューだけ視線が動かず「動きが小さい」と誤診</item>
    ///   <item>2026-08-23 <c>_EyeGain</c>（明るさ）— ユーザー赤入れ「目が明るすぎる」に対して
    ///         明るさを下げたのに、プレビューはシェーダ既定の 1.0 のまま描いていた</item>
    /// </list>
    /// 1 度目のあと <c>EyesPreview.Write</c> に「本番が配る値をここでも全部配る」という
    /// <b>注意書きを置いたが、2 度目は防げなかった</b>。だから機械で守る。
    ///
    /// ⚠ 見ているのは<b>ソースに書かれた uniform 名</b>（<c>SetFloat("_EyeX")</c> /
    /// <c>SetColor</c> / <c>PropertyToID</c>）。実行して比べる手が無いので、
    /// この codebase が prefab の YAML キーを直接見ているのと同じ流儀を採る。
    /// </summary>
    public sealed class AnomalyEyesPreviewParityTests
    {
        // 本番だけが書いてよい uniform（プレビューに対応物が無いもの）。
        // ⚠ ここへ足すのは「プレビューでは原理的に書けない」ときだけ。
        //   「面倒だから」で足すと、このテストが守っているものが空になる。
        private static readonly string[] PreviewExempt =
        {
            // 視界ジャックは別の材質（EyeJack.shader）。EyeJackPreview が受け持つ。
            "_JackTex", "_JackOn", "_JackUv",
        };

        private const string RuntimePath = "Scripts/Streaming/AnomalyEyes.cs";
        private const string PreviewPath = "Scripts/Streaming/Editor/EyesPreview.cs";

        [Test]
        public void Preview_WritesEveryUniform_TheRuntimeWrites()
        {
            HashSet<string> runtime = UniformsIn(RuntimePath);
            HashSet<string> preview = UniformsIn(PreviewPath);

            Assert.That(runtime, Is.Not.Empty,
                        $"{RuntimePath} から uniform 名を 1 つも拾えていない ＝ このテストが何も守っていない");

            foreach (string skip in PreviewExempt) runtime.Remove(skip);

            string[] missing = runtime.Where(u => !preview.Contains(u)).OrderBy(u => u).ToArray();
            Assert.That(missing, Is.Empty,
                        "プレビューが配っていない uniform がある: " + string.Join(", ", missing)
                        + $"\n→ {PreviewPath} の Write() へ足す。"
                        + "\n  足さないと、その値を触ってもプレビューの絵は 1 画素も変わらない"
                        + "（＝ 変更が効いたかを絵で判定できない）。");
        }

        /// <summary>プレビューが渡す既定値は、本番がシーンへ焼いている値と同じ根から来ている。</summary>
        [Test]
        public void PreviewDefaults_ComeFromTheRuntimeConstants()
        {
            string preview = Read(PreviewPath);
            foreach (string c in new[] { "AnomalyEyes.DefaultGain", "AnomalyEyes.DefaultBlink",
                                         "AnomalyEyes.DefaultColor" })
                Assert.That(preview, Does.Contain(c),
                            $"プレビューが {c} ではなく自前の数字を持つと、"
                            + "本番の値を変えたときに黙って食い違う");
        }

        /// <summary>出荷値の正はシーン。const がそこから外れていたら、作り直したときに別の絵になる。</summary>
        [Test]
        public void DefaultGain_MatchesTheValueBakedIntoTheScene()
        {
            string scenePath = Path.Combine(Application.dataPath, "Scenes/Main.unity");
            if (!File.Exists(scenePath)) Assert.Ignore("Main.unity が無い");

            // [Eyes] の AnomalyEyes ブロック（script guid で引く。GameObject 名では引けない）。
            const string Guid = "40a795c26ba089c4a8cc0cc5785d414f";
            string scene = File.ReadAllText(scenePath);
            int at = scene.IndexOf(Guid, System.StringComparison.Ordinal);
            if (at < 0) Assert.Ignore("シーンに [Eyes] が焼かれていない（menu scene 未実行）");

            Match m = Regex.Match(scene.Substring(at), @"^\s*gain:\s*([0-9.]+)\s*$",
                                  RegexOptions.Multiline);
            Assert.That(m.Success, Is.True, "シーンの AnomalyEyes に gain が無い");
            Assert.That(float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                        Is.EqualTo(AnomalyEyes.DefaultGain).Within(0.0001f),
                        "シーンに焼かれた明るさと C# の既定が食い違っている。"
                        + "\n→ 実機に出るのはシーンの値。**両方**直す"
                        + "（menu scene は gain を書き戻さないので、片方だけ直すと黙ってずれ続ける）。");
        }

        // SetFloat("_EyeX") / SetColor("_EyeX") / PropertyToID("_EyeX") から uniform 名を拾う。
        private static HashSet<string> UniformsIn(string relative)
            => new(Regex.Matches(Read(relative),
                                 @"(?:SetFloat|SetColor|SetVector|SetTexture|PropertyToID)\(""(_[A-Za-z0-9_]+)""")
                        .Select(m => m.Groups[1].Value));

        private static string Read(string relative)
        {
            string path = Path.Combine(Application.dataPath, relative);
            if (!File.Exists(path)) Assert.Fail($"読めない: {relative}");
            return File.ReadAllText(path);
        }
    }
}
