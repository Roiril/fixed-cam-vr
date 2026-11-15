#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 題字が渦の奥へ吸い込まれる（2026-10-14・<c>canon/LEDGER.md</c> 0261）。
    /// ⚠ 消える音は 2026-11-15 にユーザー提供の音へ替えた（0383）。合成の一撃を画の時刻へ置く
    /// 突き合わせ（旧 <c>CollapseTime_MatchesTheSound</c>）は対象が無くなったので外した。
    /// </summary>
    public sealed class TitleSuckTests
    {
        [Test]
        public void ClosingTheTitle_DoesNotDuckTheScore()
        {
            // rules/sound-design.md「劇伴はそのまま続く」。引くと、押した瞬間に沈んで一撃の前に戻ってきた。
            Assert.AreEqual(0f, SoundCueLogic.DuckFor(SoundCue.TitleOut), 1e-6f);
        }
    }
}
