#nullable enable
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 題字が渦の奥へ吸い込まれる（2026-10-14・<c>canon/LEDGER.md</c> 0261）。画と音が同じ時刻を持つこと。
    /// </summary>
    public sealed class TitleSuckTests
    {
        /// <summary>
        /// 音の潰れる一撃（<c>tools/make-sounds.py</c> の <c>TITLE_OUT_CORE</c>）は、
        /// 墨が灰だけになって引き始めてから、字が消え切るまでの間に置く。画の尺を変えたらここが落ちる。
        /// </summary>
        [Test]
        public void CollapseTime_MatchesTheSound()
        {
            string py = Path.Combine(Application.dataPath, "..", "tools", "make-sounds.py");
            Assert.IsTrue(File.Exists(py), py);
            Match m = Regex.Match(File.ReadAllText(py), @"^TITLE_OUT_CORE\s*=\s*([0-9.]+)", RegexOptions.Multiline);
            Assert.IsTrue(m.Success, "make-sounds.py に TITLE_OUT_CORE が無い");
            float core = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            float ashFrom = TitleLogic.DissolveDelaySec + TitleLogic.AshFadeFrom * TitleLogic.DissolveSec;
            Assert.That(core, Is.InRange(ashFrom, TitleLogic.GlyphGoneSec),
                $"潰れる一撃 {core:0.00}s が、灰が引き始める {ashFrom:0.00}s と消え切る {TitleLogic.GlyphGoneSec:0.00}s の外");
        }

        [Test]
        public void ClosingTheTitle_DoesNotDuckTheScore()
        {
            // rules/sound-design.md「劇伴はそのまま続く」。引くと、押した瞬間に沈んで一撃の前に戻ってきた。
            Assert.AreEqual(0f, SoundCueLogic.DuckFor(SoundCue.TitleOut), 1e-6f);
        }
    }
}
