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

        /// <summary>
        /// 実際に画へ出る文面ぜんぶ。
        /// ⚠⚠ <b><see cref="CommsPanel.LongestNoticeText"/> だけを測ってはいけない</b>
        /// （2026-08-17・<c>canon/LEDGER.md</c> 0079）。あれは<b>いちばん長い行</b>を持つ文面で、
        /// <b>いちばん行数が多い文面とは限らない</b>。①から自己紹介を外して⓪b を足した時、
        /// 最長行は①・最大行数は⓪b になった ＝ 片方しか測らないと溢れを見逃す。
        /// </summary>
        private static System.Collections.Generic.IEnumerable<string> AllTexts()
        {
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
            {
                string t = CommsPanel.NoticeText(n);
                if (!string.IsNullOrEmpty(t)) yield return t;
            }
        }

        [Test]
        public void EveryLine_FitsTheBand()
        {
            foreach (string body in AllTexts())
            foreach (string line in body.Split('\n'))
            {
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerLine,
                                   $"「{line}」が 1 行に入らない（折り返して行が増える）");
            }
        }

        [Test]
        public void LineCount_StaysWithinTheBandHeight()
        {
            foreach (string body in AllTexts())
            {
                Assert.LessOrEqual(body.Split('\n').Length, MaxLines,
                                   $"「{body.Replace("\n", "／")}」で行が増えた。"
                                   + "CommsPanel.BodyMaxH も一緒に上げること");
            }
        }

        /// <summary>
        /// <see cref="CommsPanel.LongestNoticeText"/> が本当に最長行を持っているか。
        /// <b>面の折り返し幅はこの 1 本から組まれる</b>ので、ここが嘘になると
        /// 実行時に別の文面だけが枠から出る。
        /// </summary>
        [Test]
        public void LongestNoticeText_ReallyHasTheLongestLine()
        {
            float best = 0f;
            foreach (string body in AllTexts())
            foreach (string line in body.Split('\n')) best = System.Math.Max(best, FullWidth(line));

            float declared = 0f;
            foreach (string line in CommsPanel.LongestNoticeText.Split('\n'))
                declared = System.Math.Max(declared, FullWidth(line));

            Assert.AreEqual(best, declared, 0.01f,
                            "LongestNoticeText より長い行を持つ文面がある（面の折り返し幅がその文面で足りない）");
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
            logic.Begin(body.Length);

            float step = logic.TypeSec / body.Length;
            Assert.GreaterOrEqual(step, 0.060f,
                                  $"{body.Length} 文字を {logic.TypeSec:0.00}s で打つと "
                                + $"{step * 1000f:0} ms 間隔。打鍵が重なる"
                                + "（CommsPanelLogic.MaxTypeSec を上げるか文面を短くする）");
        }
    }
}
