#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// スクリーン左上の時刻表示（<c>canon/LEDGER.md</c> 0108）。
    ///
    /// ここが固定するのは**字の決め方と、版との対応**。
    /// <b>どう見えるか</b>（大きさ・位置・管の縁に食われないか）は
    /// <c>.\tools\unity.ps1 menu osd</c> の絵でしか判定できず、
    /// <b>実機で読めるか</b>は被る以外に確かめる手が無い。
    /// </summary>
    public sealed class OsdClockLogicTests
    {
        /// <summary>参考画像（004.jpg）と同じ書式。**すべて半角・秒まで**。</summary>
        [Test]
        public void Format_MatchesTheReferenceShot()
        {
            Assert.AreEqual("2026/09/06 14:23:45",
                            OsdClockLogic.Format(new DateTime(2026, 9, 6, 14, 23, 45)));
        }

        /// <summary>
        /// ⚠ <b>ゼロ詰めを外さない。</b> 幅が変わると、秒が刻むたびに文字列が伸び縮みして
        /// そこだけ生き物のように見える（等幅にしている理由と同じ）。
        /// </summary>
        [Test]
        public void Format_IsAlwaysTheSameWidth()
        {
            var t = new DateTime(2026, 1, 2, 3, 4, 5);
            Assert.AreEqual("2026/01/02 03:04:05", OsdClockLogic.Format(t));
            Assert.AreEqual(OsdClockLogic.TextLength, OsdClockLogic.Format(t).Length);
            Assert.AreEqual(OsdClockLogic.TextLength,
                            OsdClockLogic.Format(new DateTime(2026, 12, 31, 23, 59, 59)).Length);
        }

        /// <summary>
        /// ⚠⚠ <b>版のセルの並びは Python と C# の 2 か所にある。</b>
        /// 片方だけ直すと**実機で別の字が出る**（版は焼き直されるがコードは古い番号を指す）。
        /// ここが食い違いを落とす唯一の門。
        /// </summary>
        [Test]
        public void Glyphs_MatchTheBakingScript()
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string script = Path.Combine(root, "tools", "make-osd-font.py");
            if (!File.Exists(script))
            {
                Assert.Ignore("tools/make-osd-font.py が見つからない");
                return;
            }
            Match m = Regex.Match(File.ReadAllText(script), "^GLYPHS\\s*=\\s*\"([^\"]*)\"",
                                  RegexOptions.Multiline);
            Assert.IsTrue(m.Success, "make-osd-font.py の GLYPHS を読めない");
            Assert.AreEqual(OsdClockLogic.Glyphs, m.Groups[1].Value,
                            "版のセルの並びが C# と Python で食い違っている");
            Assert.AreEqual(OsdClockLogic.GlyphCount, OsdClockLogic.Glyphs.Length,
                            "セル数が並びの長さと合っていない");
        }

        /// <summary>
        /// 書式に出る字は、どれも**その字が焼かれているセル**を指す（＝豆腐も取り違えも出ない）。
        /// ⚠ 日付と時刻の間の空白は<b>空白のセルを指すのが正しい</b>ので、
        /// 「空白セルへ落ちない」ではなく「指した先にその字がある」で見る。
        /// </summary>
        [Test]
        public void EveryCharacterInTheFormat_ResolvesToTheCellThatHoldsIt()
        {
            foreach (char c in OsdClockLogic.Format(new DateTime(2026, 9, 6, 14, 23, 45)))
            {
                int i = OsdClockLogic.GlyphIndex(c);
                Assert.AreEqual(c, OsdClockLogic.Glyphs[i], $"'{c}' が別のセルを指している");
            }
        }

        /// <summary>知らない字は空白のセルへ落とす（版の外を読んで壊れない）。</summary>
        [Test]
        public void UnknownCharacters_FallBackToBlank()
        {
            foreach (char c in new[] { 'あ', 'A', '-', ' ', '\n' })
                Assert.AreEqual(OsdClockLogic.BlankGlyph, OsdClockLogic.GlyphIndex(c));
        }

        /// <summary>
        /// 敷き直すのは**秒が変わった縁だけ**。同じ秒のあいだは何度呼んでも false
        /// （毎フレーム書くと 90Hz で 4 万画素を組み替えることになる）。
        /// </summary>
        [Test]
        public void NeedsRedraw_OnlyOnTheSecondBoundary()
        {
            var t = new DateTime(2026, 9, 6, 14, 23, 45);
            Assert.IsTrue(OsdClockLogic.NeedsRedraw(t, OsdClockLogic.Never, out long s0),
                          "初回は必ず敷く");
            Assert.IsFalse(OsdClockLogic.NeedsRedraw(t.AddMilliseconds(400), s0, out long s1),
                           "同じ秒のあいだは敷き直さない");
            Assert.AreEqual(s0, s1);
            Assert.IsTrue(OsdClockLogic.NeedsRedraw(t.AddSeconds(1), s0, out _), "秒が変われば敷く");
        }

        /// <summary>
        /// ⚠ 秒の**値**ではなく通算秒で比べる。値で比べると
        /// 「ちょうど 60 秒後」「時刻補正で戻った」を取りこぼす。
        /// </summary>
        [Test]
        public void NeedsRedraw_UsesTheAbsoluteSecond_NotTheSecondsField()
        {
            var t = new DateTime(2026, 9, 6, 14, 23, 45);
            OsdClockLogic.NeedsRedraw(t, OsdClockLogic.Never, out long s0);
            Assert.IsTrue(OsdClockLogic.NeedsRedraw(t.AddMinutes(1), s0, out _),
                          "同じ秒の値でも 1 分後なら敷き直す");
        }

        /// <summary>番号列は毎回 new せず、渡した配列へ書く（1 秒に 1 度でも塵は積む）。</summary>
        [Test]
        public void FillGlyphs_WritesIntoTheGivenBuffer()
        {
            var buf = new int[OsdClockLogic.TextLength];
            Assert.IsTrue(OsdClockLogic.FillGlyphs(new DateTime(2026, 9, 6, 14, 23, 45), buf));
            Assert.AreEqual(2, buf[0]);   // '2'
            Assert.AreEqual(11, buf[4]);  // '/'
            Assert.AreEqual(OsdClockLogic.BlankGlyph, buf[10]); // 日付と時刻の間の空白
            Assert.AreEqual(10, buf[13]); // ':'
        }

        /// <summary>短い配列では書かずに false（版の外へはみ出して壊れない）。</summary>
        [Test]
        public void FillGlyphs_RefusesAShortBuffer()
        {
            Assert.IsFalse(OsdClockLogic.FillGlyphs(DateTime.Now,
                                                    new int[OsdClockLogic.TextLength - 1]));
            Assert.IsFalse(OsdClockLogic.FillGlyphs(DateTime.Now, null!));
        }

        /// <summary>
        /// 版が焼かれていて、セル数で割り切れる。
        /// ⚠ <b>割り切れないと <see cref="ScreenOsd"/> は時計を出さない</b>（セル幅が決まらない）。
        /// </summary>
        [Test]
        public void TheBakedAtlas_ExistsAndDividesIntoCells()
        {
            var atlas = Resources.Load<Texture2D>(ScreenOsd.GlyphResourcePath);
            if (atlas == null)
            {
                Assert.Ignore($"版 Resources/{ScreenOsd.GlyphResourcePath} が未生成"
                              + "（py -3.11 tools/make-osd-font.py）");
                return;
            }
            Assert.AreEqual(0, atlas.width % OsdClockLogic.GlyphCount,
                            $"版の幅 {atlas.width} が {OsdClockLogic.GlyphCount} で割り切れない");
            Assert.IsTrue(atlas.isReadable,
                          "版に Read/Write Enabled が要る（CPU で敷き直すため）");
        }
    }
}
