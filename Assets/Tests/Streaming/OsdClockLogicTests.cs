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
            string src = File.ReadAllText(script);
            Match m = Regex.Match(src, "^GLYPHS\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);
            Assert.IsTrue(m.Success, "make-osd-font.py の GLYPHS を読めない");
            Assert.AreEqual(OsdClockLogic.Glyphs, m.Groups[1].Value,
                            "半角セルの並びが C# と Python で食い違っている");

            Match w = Regex.Match(src, "^WIDE_GLYPHS\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);
            Assert.IsTrue(w.Success, "make-osd-font.py の WIDE_GLYPHS を読めない");
            Assert.AreEqual(OsdClockLogic.WideGlyphs, w.Groups[1].Value,
                            "全角セルの並びが C# と Python で食い違っている");

            Assert.AreEqual(OsdClockLogic.GlyphCount,
                            OsdClockLogic.Glyphs.Length + OsdClockLogic.WideGlyphs.Length * 2,
                            "セル数が並びの長さと合っていない（全角は 2 セル）");
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
            foreach (char c in new[] { 'あ', 'Z', '-', ' ', '\n' })
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

        // ---- 周回（canon/LEDGER.md 0167）----

        /// <summary>
        /// ⚠ <b>帰りの区間は「最後」</b>。ユーザー逐語「4-Aは最後とかの表示で。
        /// 4周目と書くと混乱する」— 待機者の資料に「3 周します」と書いてある（0165）。
        /// </summary>
        [Test]
        public void LapLabel_CountsFromOne_AndCallsTheReturnSegmentTheLast()
        {
            Assert.AreEqual("1周目", OsdClockLogic.LapLabel(1, 3));
            Assert.AreEqual("2周目", OsdClockLogic.LapLabel(2, 3));
            Assert.AreEqual("3周目", OsdClockLogic.LapLabel(3, 3));
            Assert.AreEqual("最後", OsdClockLogic.LapLabel(4, 3), "帰りの A は「最後」");
            Assert.AreEqual("最後", OsdClockLogic.LapLabel(9, 3));
        }

        /// <summary>区間がまだ確定していなければ何も出さない（導入のあいだ）。</summary>
        [Test]
        public void LapLabel_IsEmptyBeforeTheFirstSegment()
        {
            Assert.AreEqual("", OsdClockLogic.LapLabel(-1, 3));
            Assert.AreEqual("", OsdClockLogic.LapLabel(0, 3));
        }

        /// <summary>周数が渡されなくても既定（3 周）で答える。</summary>
        [Test]
        public void LapLabel_FallsBackToTheDefaultTotal()
        {
            Assert.AreEqual("3周目", OsdClockLogic.LapLabel(3, 0));
            Assert.AreEqual("最後", OsdClockLogic.LapLabel(4, 0));
        }

        /// <summary>全角は版の「その字が焼かれている 2 セル」を指す。</summary>
        [Test]
        public void WideCellIndex_PointsAtTheCellThatHoldsIt()
        {
            for (int i = 0; i < OsdClockLogic.WideGlyphs.Length; i++)
            {
                int at = OsdClockLogic.WideCellIndex(OsdClockLogic.WideGlyphs[i]);
                Assert.AreEqual(OsdClockLogic.Glyphs.Length + i * 2, at);
                Assert.Less(at + 1, OsdClockLogic.GlyphCount, "右のセルが版の外を指している");
            }
            Assert.AreEqual(-1, OsdClockLogic.WideCellIndex('0'), "半角は全角のセルを指さない");
        }

        /// <summary>
        /// 周回は<b>時刻の右</b>に、空きを 2 セル置いて出る（0167「その右くらいに」）。
        /// 全角は 2 セルを使うので「1周目」で欄（5 セル）がちょうど埋まる。
        /// </summary>
        [Test]
        public void FillCells_PutsTheLapToTheRightOfTheClock()
        {
            var buf = new int[OsdClockLogic.CellCount];
            Assert.IsTrue(OsdClockLogic.FillCells(new DateTime(2026, 9, 6, 14, 23, 45),
                                                  lap: 1, totalLaps: 3, otherworld: false, buf));
            for (int i = 0; i < OsdClockLogic.TextLength; i++)
                Assert.AreEqual(OsdClockLogic.GlyphIndex("2026/09/06 14:23:45"[i]), buf[i]);
            Assert.AreEqual(OsdClockLogic.BlankGlyph, buf[19], "時刻と周回のあいだは空き");
            Assert.AreEqual(OsdClockLogic.BlankGlyph, buf[20]);
            Assert.AreEqual(1, buf[21], "'1'");
            Assert.AreEqual(OsdClockLogic.WideCellIndex('周'), buf[22]);
            Assert.AreEqual(OsdClockLogic.WideCellIndex('周') + 1, buf[23]);
            Assert.AreEqual(OsdClockLogic.WideCellIndex('目'), buf[24]);
            Assert.AreEqual(OsdClockLogic.WideCellIndex('目') + 1, buf[25]);
        }

        /// <summary>短い語（「最後」＝ 4 セル）の右は空白で埋まる（欄の幅は変わらない）。</summary>
        [Test]
        public void FillCells_PadsTheShorterLabel()
        {
            var buf = new int[OsdClockLogic.CellCount];
            Assert.IsTrue(OsdClockLogic.FillCells(new DateTime(2026, 9, 6, 14, 23, 45),
                                                  lap: 4, totalLaps: 3, otherworld: false, buf));
            Assert.AreEqual(OsdClockLogic.WideCellIndex('最'), buf[21]);
            Assert.AreEqual(OsdClockLogic.WideCellIndex('後'), buf[23]);
            Assert.AreEqual(OsdClockLogic.BlankGlyph, buf[25]);
        }

        /// <summary>区間が未確定なら周回の欄は空のまま（時刻だけが出る）。</summary>
        [Test]
        public void FillCells_LeavesTheLabelEmptyBeforeTheFirstSegment()
        {
            var buf = new int[OsdClockLogic.CellCount];
            Assert.IsTrue(OsdClockLogic.FillCells(new DateTime(2026, 9, 6, 14, 23, 45),
                                                  lap: -1, totalLaps: 3, otherworld: false, buf));
            for (int i = OsdClockLogic.TextLength; i < OsdClockLogic.CellCount; i++)
                Assert.AreEqual(OsdClockLogic.BlankGlyph, buf[i]);
        }

        /// <summary>
        /// ⚠⚠ <b>別の場所が映っているあいだは、時刻も周回も <c>?</c></b>
        /// （0167「時刻も何周目かも全部???になるようにしてほしい」）。
        /// 数字は 1 つも残らない。区切り（<c>/</c> <c>:</c>）は形として残す。
        /// </summary>
        [Test]
        public void FillCells_MasksBothTheClockAndTheLapInTheOtherworld()
        {
            var buf = new int[OsdClockLogic.CellCount];
            Assert.IsTrue(OsdClockLogic.FillCells(new DateTime(2026, 9, 6, 14, 23, 45),
                                                  lap: 2, totalLaps: 3, otherworld: true, buf));
            int q = OsdClockLogic.GlyphIndex('?');
            for (int i = 0; i < OsdClockLogic.CellCount; i++)
                Assert.IsTrue(buf[i] == q || buf[i] == OsdClockLogic.BlankGlyph
                              || buf[i] == OsdClockLogic.GlyphIndex('/')
                              || buf[i] == OsdClockLogic.GlyphIndex(':'),
                              $"セル {i} に数字か語が残っている");
            Assert.AreEqual(OsdClockLogic.GlyphIndex('/'), buf[4], "区切りは形として残す");
            Assert.AreEqual(q, buf[21]);
            Assert.AreEqual(q, buf[22]);
            Assert.AreEqual(q, buf[23]);
            Assert.AreEqual(OsdClockLogic.BlankGlyph, buf[24]);
        }

        /// <summary>マスクした時刻も**同じ幅**（欄がずれない）。</summary>
        [Test]
        public void MaskedTime_HasTheSameWidthAsTheClock()
        {
            Assert.AreEqual(OsdClockLogic.TextLength, OsdClockLogic.MaskedTime.Length);
        }

        /// <summary>短い配列では書かずに false（版の外へはみ出して壊れない）。</summary>
        [Test]
        public void FillCells_RefusesAShortBuffer()
        {
            Assert.IsFalse(OsdClockLogic.FillCells(DateTime.Now, 1, 3, false,
                                                   new int[OsdClockLogic.CellCount - 1]));
            Assert.IsFalse(OsdClockLogic.FillCells(DateTime.Now, 1, 3, false, null!));
        }

        /// <summary>
        /// 書いた番号はどれも版の中を指す（版の外を読んで壊れない）。
        /// ⚠ 全角は 2 セルを使うので、<b>右のセルまで</b>版に無いといけない。
        /// </summary>
        [Test]
        public void FillCells_NeverPointsOutsideTheAtlas()
        {
            var buf = new int[OsdClockLogic.CellCount];
            foreach (int lap in new[] { -1, 1, 2, 3, 4 })
                foreach (bool other in new[] { false, true })
                {
                    Assert.IsTrue(OsdClockLogic.FillCells(DateTime.Now, lap, 3, other, buf));
                    foreach (int cell in buf)
                    {
                        Assert.GreaterOrEqual(cell, 0);
                        Assert.Less(cell, OsdClockLogic.GlyphCount);
                    }
                }
        }

        // ---- 言語（canon/LEDGER.md 0127 の表に周回を足した）----

        /// <summary>周回は体験者が選んだ言語で出る（時刻は数字なので訳さない）。</summary>
        [Test]
        public void LapLabel_IsTranslated()
        {
            Assert.AreEqual("LAP 1", OsdClockLogic.LapLabel(1, 3, ShowLang.En));
            Assert.AreEqual("TOUR 1", OsdClockLogic.LapLabel(1, 3, ShowLang.Fr));
            Assert.AreEqual("1周目", OsdClockLogic.LapLabel(1, 3, ShowLang.Ja));
            Assert.AreEqual("LAST", OsdClockLogic.LapLabel(4, 3, ShowLang.En));
            Assert.AreEqual("FIN", OsdClockLogic.LapLabel(4, 3, ShowLang.Fr));
            Assert.AreEqual("最後", OsdClockLogic.LapLabel(4, 3, ShowLang.Ja));
        }

        /// <summary>
        /// ⚠⚠ <b>3 言語のどの語も欄（<see cref="OsdClockLogic.LabelCells"/>）に収まる。</b>
        /// はみ出した語は <see cref="OsdClockLogic.FillCells"/> が黙って切る
        /// （画では「TOUR」で終わって数字が消える）。ここが落ちたら欄を広げる。
        /// </summary>
        [Test]
        public void EveryLabel_FitsTheField()
        {
            foreach (ShowLang lang in ShowLanguage.All)
                foreach (int lap in new[] { 1, 2, 3, 4 })
                {
                    string s = OsdClockLogic.LapLabel(lap, 3, lang);
                    int cells = 0;
                    foreach (char c in s) cells += OsdClockLogic.WideCellIndex(c) >= 0 ? 2 : 1;
                    Assert.LessOrEqual(cells, OsdClockLogic.LabelCells,
                                       $"{lang} lap{lap}: 「{s}」が欄（{OsdClockLogic.LabelCells} セル）に入らない");
                }
            Assert.LessOrEqual(OsdClockLogic.MaskedLabel.Length, OsdClockLogic.LabelCells);
        }

        /// <summary>
        /// ⚠⚠ <b>語に使う字が版に焼かれている。</b> 無い字は空白のセルへ落ちるので、
        /// <b>画では字が消えるだけ</b>（豆腐も警告も出ない）。訳語を変えたらここが落ちる。
        /// </summary>
        [Test]
        public void EveryLabelCharacter_IsInTheAtlas()
        {
            foreach (ShowLang lang in ShowLanguage.All)
                foreach (int lap in new[] { 1, 2, 3, 4 })
                    foreach (char c in OsdClockLogic.LapLabel(lap, 3, lang))
                    {
                        if (c == ' ') continue;
                        bool ok = OsdClockLogic.WideCellIndex(c) >= 0
                               || OsdClockLogic.GlyphIndex(c) != OsdClockLogic.BlankGlyph;
                        Assert.IsTrue(ok, $"'{c}'（{lang}）が版に無い — make-osd-font.py の"
                                        + " GLYPHS / WIDE_GLYPHS に足す");
                    }
        }

        /// <summary>言語を変えても時刻の欄は 1 セルも動かない（数字は訳さない）。</summary>
        [Test]
        public void TheClockDoesNotMoveWhenTheLanguageChanges()
        {
            var t = new DateTime(2026, 9, 6, 14, 23, 45);
            var ja = new int[OsdClockLogic.CellCount];
            var fr = new int[OsdClockLogic.CellCount];
            Assert.IsTrue(OsdClockLogic.FillCells(t, 1, 3, false, ja, ShowLang.Ja));
            Assert.IsTrue(OsdClockLogic.FillCells(t, 1, 3, false, fr, ShowLang.Fr));
            for (int i = 0; i < OsdClockLogic.TextLength + OsdClockLogic.GapCells; i++)
                Assert.AreEqual(ja[i], fr[i], $"セル {i} が言語で動いた");
            Assert.AreNotEqual(ja[21], fr[21], "周回の欄は言語で変わる");
        }

        /// <summary>
        /// ⚠ <b>異世界の判定は 1 本</b>（<see cref="TakeRunner.IsOtherworldCue"/>）。
        /// 音（風・劇伴）と時計が同じものを読む。
        /// </summary>
        [Test]
        public void TheOtherworldTest_IsSharedWithTheSound()
        {
            Assert.AreEqual(TakeRunner.OtherworldCuePrefix,
                            ShowSoundDirector.OtherworldCuePrefix);
            Assert.IsTrue(TakeRunner.IsOtherworldCue("backrooms_B"));
            Assert.IsFalse(TakeRunner.IsOtherworldCue("plate_A"));
            Assert.IsFalse(TakeRunner.IsOtherworldCue(""));
        }
    }
}
