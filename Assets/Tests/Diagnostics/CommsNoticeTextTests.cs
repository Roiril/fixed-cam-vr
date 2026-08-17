#nullable enable

using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// AIエージェントからの連絡の文面が、<b>枠にも打鍵にも収まっているか</b>。
    ///
    /// ⚠ ここが守るのは「見た目」ではなく<b>沈黙して壊れる 2 つ</b>:
    /// 行が増えて枠から溢れる／打鍵の間隔が縮んで音が連続音になる。
    /// どちらも <c>menu comms-preview</c> の絵では気づけない
    /// （溢れは枠の外なので写らず、音は録画に映らない）。
    /// </summary>
    public class CommsNoticeTextTests
    {
        /// <summary>
        /// 1 行に入る全角の数（面の幅 0.76m × 0.92 ÷ 1 文字 1.8°）。
        /// ⚠ <c>menu text-audit</c> が実測で測る値の、机上の目安。
        /// </summary>
        private const float MaxFullWidthPerLine = 14f;

        /// <summary>
        /// 上段が想定している最悪の行数。<b><c>CommsPanel.BodyMaxH</c> はこの行数で決まっている</b>ので、
        /// 増やすならあちらも一緒に上げる（上げないと 1 行ぶん枠から溢れる）。
        /// </summary>
        private const int MaxLines = 3;

        private static float FullWidth(string line)
        {
            float w = 0f;
            foreach (char c in line) w += c < 0x80 ? 0.5f : 1f;
            return w;
        }

        [Test]
        public void EveryLine_FitsTheBand()
        {
            foreach (string line in CommsPanel.LongestNoticeText.Split('\n'))
            {
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerLine,
                                   $"「{line}」が 1 行に入らない（折り返して行が増える）");
            }
        }

        [Test]
        public void LineCount_StaysWithinTheBandHeight()
        {
            Assert.LessOrEqual(CommsPanel.LongestNoticeText.Split('\n').Length, MaxLines,
                               "行が増えた。CommsPanel.BodyMaxH も一緒に上げること");
        }

        /// <summary>
        /// ⚠⚠ <b>打ち終わりの上限が効くと、打鍵の間隔が <c>CharsPerSec</c> より短くなる。</b>
        /// 切り出した打鍵は 1 発 60ms あるので、そこを割ると<b>1 発ずつが繋がって連続音</b>になる
        /// （`rules/sound-design.md` §4）。<b>画は普通に出るので、音を聴くまで気づけない。</b>
        ///
        /// ⚠ <c>CommsPanelLogicTests.TypingSpeed_KeepsKeystrokesApart</c> は
        /// <c>CharsPerSec</c> しか見ていないので、この経路（上限による圧縮）を捕まえられない。
        /// </summary>
        [Test]
        public void LongestNotice_TypesSlowEnough_ForTheKeystrokeClips()
        {
            string body = CommsPanel.LongestNoticeText;
            var logic = new CommsPanelLogic();
            logic.Begin(body.Length, CommsPanelLogic.HoldBriefSec);

            float step = logic.TypeSec / body.Length;
            Assert.GreaterOrEqual(step, 0.060f,
                                  $"{body.Length} 文字を {logic.TypeSec:0.00}s で打つと "
                                + $"{step * 1000f:0} ms 間隔。打鍵が重なる"
                                + "（CommsPanelLogic.MaxTypeSec を上げるか文面を短くする）");
        }
    }
}
