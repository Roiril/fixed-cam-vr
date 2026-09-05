#nullable enable
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// <b>体験前の注意書き</b>。面は上から <b>言語の並び / 操作の説明 / 安全の掲示</b> の 3 枚で、
    /// 並びは 2026-09-05（<c>canon/LEDGER.md</c> 0155）のユーザー指定
    /// 「言語選択を一番上に。その次にホラー軽減モード、その次に注意書き、その次スタッフに声かけて」。
    ///
    /// ⚠ ここが守るのは<b>実機でしか出ない壊れ方</b>:
    /// 行が枠を超えて首を振らないと読めなくなる／選んでいる言語が分からない／
    /// 訳し忘れて 1 言語だけ日本語のまま出る／長押しのゲージで行が左右に揺れる。
    /// どれも <c>menu text-audit</c> の絵に写らない（絵は大きさとはみ出し専用）。
    ///
    /// ⚠ 文言を変えたら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する
    /// （静的ベイクなので、ここに無い字は実機で豆腐になる）。
    /// </summary>
    public sealed class TitleNoticeTextTests
    {
        /// <summary>面が持つ 2 つの状態（ホラー軽減モードの外 / 中）。<b>両方測る</b>。</summary>
        private static readonly bool[] BothReliefStates = { false, true };

        [SetUp]
        public void ResetRelief() => HorrorRelief.Reset();

        [TearDown]
        public void RestoreRelief() => HorrorRelief.Reset();

        /// <summary>
        /// 1 行に入る全角の数（枠 1.70m ÷ 2.6m 先で 1 文字 1.8°）。
        /// ⚠ 机上の目安で、実測は <c>menu text-audit -Set lang=…</c>。
        /// </summary>
        private const float MaxFullWidthPerLine = 20f;

        /// <summary>
        /// 操作の説明（1.5°）の 1 行に入る全角の数。本文より字が小さいぶん多く入る
        /// （20 × 1.8 ÷ 1.5 ＝ 24）。⚠ <b>本文の物差しで測ると理由なく落ちる</b>。
        /// </summary>
        private const float MaxFullWidthPerFooterLine = 24f;

        /// <summary>物差しは全面で 1 つ（<see cref="HmdTextStyle.LineWidth"/>）。
        /// ⚠ 別々に数えると、片方だけアクセント付きを全角と数えて理由なく落ちる。</summary>
        private static float FullWidth(string line) => HmdTextStyle.LineWidth(line);

        // --- 並び（2026-09-05・0155）--------------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>順は 言語 → ホラー軽減 → 掲示 → スタッフ</b>（ユーザー指定）。
        /// この順が崩れると、体験者は「何を選ぶ面なのか」が分からないまま掲示から読むことになる。
        /// </summary>
        [Test]
        public void Compose_PutsTheChooserFirst_AndTheNoticeAfterTheControls()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string all = TitleNotice.ComposeFor(lang, relief: false, hold01: 0f);
                int chooser = all.IndexOf(TitleNotice.ChooserFor(lang), System.StringComparison.Ordinal);
                int relief = all.IndexOf(TitleNotice.ReliefLinesOf(lang, on: false),
                                         System.StringComparison.Ordinal);
                int body = all.IndexOf(TitleNotice.BodyFor(lang), System.StringComparison.Ordinal);
                int ready = all.LastIndexOf(TitleNotice.ReadyLineOf(lang), System.StringComparison.Ordinal);

                Assert.AreEqual(0, chooser, $"[{lang}] 言語の並びが先頭に無い");
                Assert.Less(chooser, relief, $"[{lang}] ホラー軽減が言語の並びより上");
                Assert.Less(relief, body, $"[{lang}] 安全の掲示がホラー軽減より上");
                Assert.Less(body, ready, $"[{lang}] スタッフを呼ぶ行が掲示より上");
            }
        }

        // --- ① 言語の並び -------------------------------------------------------------

        /// <summary>
        /// <b>3 つの名前を常に全部出す。</b> 次の言語だけを出す形にすると、
        /// いま何が選べるのかが分からない（体験者は 1 度も押さずに諦める）。
        /// </summary>
        [Test]
        public void Chooser_ListsAllThreeNames()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string line = TitleNotice.ChooserFor(lang);
                StringAssert.Contains("日本語", line, $"{lang}");
                StringAssert.Contains("English", line, $"{lang}");
                StringAssert.Contains("Français", line, $"{lang}");
                Assert.AreEqual(1, line.Split('\n').Length, $"{lang}: 並びは 1 行");
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerLine, $"{lang}: 並びが枠に入らない");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>いま選んでいるものが 1 つだけ囲まれている。</b> この面は <c>richText</c> を
        /// 切ってあり白 1 色しか出せないので、<b>括弧だけが「選ばれている」の唯一の手掛かり</b>。
        /// ⚠ 数えるのは<b>並びの行だけ</b> — ホラー軽減モードの ［］ は別の面にあり、別の意味を持つ。
        /// </summary>
        [Test]
        public void Chooser_MarksExactlyOneEntry()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string line = TitleNotice.ChooserFor(lang);
                Assert.AreEqual(1, Count(line, '［'), $"{lang}: 囲みの数");
                Assert.AreEqual(1, Count(line, '］'), $"{lang}: 閉じ括弧の数");
            }
        }

        // --- ② 操作の説明 -------------------------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>切り替え方は 3 言語ぶん出す。</b> 選択中の言語だけで書くと、
        /// <b>それを読めない人には切り替え方が届かない</b>（＝ 日本語のまま始めるしかない）。
        /// </summary>
        [Test]
        public void Guide_ExplainsSwitchingInEveryLanguage()
        {
            foreach (bool relief in BothReliefStates)
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string g = TitleNotice.GuideFor(lang, relief, 0f);
                StringAssert.Contains("ボタンを単押し", g, $"[{lang}] 日本語で切り替え方が無い");
                StringAssert.Contains("Press a button once", g, $"[{lang}] English で切り替え方が無い");
                StringAssert.Contains("Appuyez une fois", g, $"[{lang}] Français で切り替え方が無い");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>どちらの押し方の話かを毎行で言う</b>（2026-09-05・0155 の「説明口調で」）。
        /// 同じボタンに 2 つの意味があるので、「ボタンで」では長押しと区別が付かない。
        /// </summary>
        [Test]
        public void Guide_SaysWhichKindOfPressEachLineIsAbout()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string sw = TitleNotice.GuideFor(lang, relief: false, hold01: 0f).Split('\n')[0];
                StringAssert.Contains("単押し", sw, "単押しと言い切っていない");
                string hold = TitleNotice.ReliefLinesOf(lang, on: false);
                bool saysHold = hold.Contains("長押し") || hold.Contains("Hold")
                                || hold.Contains("Maintenez");
                Assert.IsTrue(saysHold, $"[{lang}] 長押しだと分かる語が無い: {hold}");
            }
        }

        /// <summary>
        /// <b>入る前は「どうすれば入れるか」と「何が起きるか」の両方を言う</b>
        /// （ユーザー指定「既存の音が1/2になり、陽気なBGMが流れます と簡単に説明を」）。
        /// </summary>
        [Test]
        public void ReliefLines_Off_SayHowToEnterAndWhatHappens()
        {
            (ShowLang lang, string how, string what)[] cases =
            {
                (ShowLang.Ja, "長押し", "陽気な曲"),
                (ShowLang.En, "Hold a button", "cheery music"),
                (ShowLang.Fr, "Maintenez", "musique joyeuse"),
            };
            foreach ((ShowLang lang, string how, string what) in cases)
            {
                string lines = TitleNotice.ReliefLinesOf(lang, on: false);
                StringAssert.Contains(how, lines, $"[{lang}] 入り方が書かれていない");
                StringAssert.Contains(what, lines, $"[{lang}] 何が起きるかが書かれていない");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>入っているあいだは、入っていると分かる。</b> 白 1 色しか出せないので
        /// <b>囲みだけが状態の手掛かり</b>。⚠ <b>出方も同じ面に書く</b> —
        /// 誤って入った人の出口がここしかない。
        /// </summary>
        [Test]
        public void ReliefLines_On_ShowTheStateAndTheWayOut()
        {
            (ShowLang lang, string mark, string out_)[] cases =
            {
                (ShowLang.Ja, "［", "長押し"),
                (ShowLang.En, "[", "Hold"),
                (ShowLang.Fr, "[", "Maintenez"),
            };
            foreach ((ShowLang lang, string mark, string out_) in cases)
            {
                string on = TitleNotice.ReliefLinesOf(lang, on: true);
                StringAssert.Contains(mark, on, $"[{lang}] 入っている印が無い");
                StringAssert.Contains(out_, on, $"[{lang}] 出方が書かれていない");
                StringAssert.DoesNotContain(mark, TitleNotice.ReliefLinesOf(lang, on: false),
                                            $"[{lang}] 入っていないのに印が出ている");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>入 / 出で行数を変えない</b>（2026-09-05・0155）。変えると切り替えた瞬間に
        /// <b>下の安全の掲示が跳ねる</b>（塊の高さが動くため）。
        /// </summary>
        [Test]
        public void ReliefLines_KeepTheSameRowCount_WhetherOnOrOff()
        {
            foreach (ShowLang lang in ShowLanguage.All)
                Assert.AreEqual(TitleNotice.ReliefLinesOf(lang, on: false).Split('\n').Length,
                                TitleNotice.ReliefLinesOf(lang, on: true).Split('\n').Length,
                                $"[{lang}] 入と出で行数が違う");
        }

        /// <summary>訳し忘れの検出。<b>3 つがどれも違う</b>ことだけを見る。</summary>
        [Test]
        public void ReliefLines_AreWrittenInEveryLanguage()
        {
            foreach (bool on in BothReliefStates)
            {
                Assert.AreNotEqual(TitleNotice.ReliefLinesOf(ShowLang.Ja, on),
                                   TitleNotice.ReliefLinesOf(ShowLang.En, on));
                Assert.AreNotEqual(TitleNotice.ReliefLinesOf(ShowLang.En, on),
                                   TitleNotice.ReliefLinesOf(ShowLang.Fr, on));
                Assert.AreNotEqual(TitleNotice.ReliefLinesOf(ShowLang.Fr, on),
                                   TitleNotice.ReliefLinesOf(ShowLang.Ja, on));
            }
        }

        /// <summary>
        /// <b>操作の説明は 7 行</b>（切り替え方 3 ＋ 空行 ＋ ホラー軽減 2 ＋ ゲージ）。
        /// ⚠ 行が増えると塊が縦に伸びて、上下の端を読むのに首を振ることになる
        /// （縦の実測は <c>TitleNoticeLayoutTests.TheWholeStack_FitsInTheView</c>）。
        /// </summary>
        [Test]
        public void Guide_KeepsItsSevenRows()
        {
            foreach (bool relief in BothReliefStates)
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] rows = TitleNotice.GuideFor(lang, relief, 0f).Split('\n');
                Assert.AreEqual(7, rows.Length, $"[{lang}] 切り替え方 3 ＋ 空行 ＋ 軽減 2 ＋ ゲージ");
                Assert.That(rows[3], Is.Empty, $"[{lang}] 3 言語の塊と 1 言語の塊は空行で離す");
                Assert.That(rows[6], Is.Not.Empty, $"[{lang}] ゲージの行が無い");
            }
        }

        /// <summary>操作の説明の行も枠に入る（本文より小さいので入る数が違う）。</summary>
        [Test]
        public void GuideLines_FitTheFrame()
        {
            foreach (bool relief in BothReliefStates)
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string line in TitleNotice.GuideFor(lang, relief, 1f).Split('\n'))
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerFooterLine,
                                   $"[{ShowLanguage.Code(lang)}/軽減{relief}] 「{line}」が 1 行に入らない");
        }

        // --- ③ 長押しのゲージ ---------------------------------------------------------

        /// <summary>
        /// ⚠⚠ <b>どの進み具合でも幅が 1 ミリも動かない。</b> 半角の <c>-</c> と空白で組むと
        /// 字送りが違って<b>押すほど行が左右に揺れる</b>（このフォントは半角が等幅ではない）。
        /// 全角はどれも送りが 1em なので、それだけが解になる。
        /// </summary>
        [Test]
        public void Gauge_KeepsTheSameWidth_AtEveryProgress()
        {
            float want = FullWidth(TitleNotice.HoldGauge(0f));
            Assert.AreEqual(TitleNotice.GaugeCells, want, 0.001f, "全角 1 マス ＝ 幅 1");
            for (int i = 0; i <= 20; i++)
                Assert.AreEqual(want, FullWidth(TitleNotice.HoldGauge(i / 20f)), 0.001f,
                                $"進み {i / 20f:0.00} で幅が変わった（行が左右に揺れる）");
        }

        /// <summary>マスの数は進み具合に応じて増え、0 と満杯を必ず取る。</summary>
        [Test]
        public void Gauge_FillsFromEmptyToFull()
        {
            Assert.AreEqual(0, TitleNotice.GaugeCellsFor(0f), "押していないのに点いている");
            Assert.AreEqual(TitleNotice.GaugeCells, TitleNotice.GaugeCellsFor(1f), "成立しても満杯にならない");
            Assert.AreEqual(TitleNotice.GaugeCells, TitleNotice.GaugeCellsFor(2f), "1 を超えても満杯で止まる");
            Assert.AreEqual(0, TitleNotice.GaugeCellsFor(-1f), "負でも 0 で止まる");
            // ⚠ 触れただけで点かない（単押しの窓の内側は 0）。
            Assert.AreEqual(0, TitleNotice.GaugeCellsFor(0.09f), "触れただけで点いている");
            Assert.Less(TitleNotice.GaugeCellsFor(0.4f), TitleNotice.GaugeCellsFor(0.8f), "増えていない");
        }

        /// <summary>
        /// ⚠⚠ <b>単押しの窓のあいだは 1 文字も出さない</b>（2026-09-05・<c>canon/LEDGER.md</c> 0159・
        /// ユーザー報告「最初から○○○○が出ていると文字化けしているのか心配になる」）。
        /// 押してもいないうちから同じ字が 10 個並ぶと、体験者は<b>フォントが壊れた</b>と読む。
        ///
        /// ⭐ 出る境目は<b>単押しの上限</b>（<see cref="ShowLanguage.TapMaxSec"/>）に合わせてある ＝
        /// <b>ゲージが出たら、離しても言語は変わらない</b>。
        /// ⚠ <b>行は消さない</b>（全角空白で場所を取る）。消すと行数が変わって塊が上下に跳ねる。
        /// </summary>
        [Test]
        public void Gauge_StaysBlank_WhileThePressCouldStillBeATap()
        {
            foreach (float p in new[] { 0f, 0.1f, TitleNotice.GaugeStart01 })
            {
                string g = TitleNotice.HoldGauge(p);
                Assert.AreEqual(0, Count(g, '●') + Count(g, '○'),
                                $"進み {p:0.00}（まだ単押し）で輪が出ている: 「{g}」");
                Assert.AreEqual(TitleNotice.GaugeCells, g.Length, "行が消えている（塊が跳ねる）");
                Assert.AreEqual(TitleNotice.GaugeCells, FullWidth(g), 0.001f, "幅が変わった");
            }
            Assert.AreEqual(1, TitleNotice.GaugeCellsFor(TitleNotice.GaugeStart01 + 0.001f),
                            "窓を過ぎた最初のフレームで 1 マスも点かない");
        }

        /// <summary>
        /// ⚠⚠ <b>○ だけが並ぶ状態を作らない</b> — それが文字化けに見える形そのもの。
        /// 出るときは必ず ● が 1 つ以上ある。
        /// </summary>
        [Test]
        public void Gauge_NeverShowsRingsWithoutADot()
        {
            for (int i = 0; i <= 200; i++)
            {
                string g = TitleNotice.HoldGauge(i / 200f);
                if (Count(g, '○') > 0)
                    Assert.Greater(Count(g, '●'), 0,
                                   $"進み {i / 200f:0.000} で ○ だけが並んだ: 「{g}」");
            }
        }

        /// <summary>
        /// ⚠ <b>ゲージを ［］ で囲まない。</b> この面の ［］ は「選んでいる言語」と
        /// 「軽減モード中」の 2 つで既に意味を持っている。3 つ目を与えると印が読めなくなる。
        /// </summary>
        [Test]
        public void Gauge_DoesNotUseTheBracketsThatMeanSomethingElse()
        {
            for (int i = 0; i <= TitleNotice.GaugeCells; i++)
            {
                string g = TitleNotice.HoldGauge(i / (float)TitleNotice.GaugeCells);
                foreach (char c in new[] { '［', '］', '[', ']' })
                    Assert.AreEqual(0, Count(g, c), $"ゲージが {c} を使っている");
            }
        }

        // --- ④ 安全の掲示 ------------------------------------------------------------

        [Test]
        public void NoticeLines_FitTheFrame()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string line in TitleNotice.NoticeFor(lang).Split('\n'))
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerLine,
                                   $"[{ShowLanguage.Code(lang)}]「{line}」が 1 行に入らない");
        }

        /// <summary>
        /// 訳し忘れの検出。<b>3 つの本文がどれも違う</b>ことだけを見る
        /// （中身の正しさは人が読むしかないが、<b>丸ごとコピーした</b>のは機械で分かる）。
        /// </summary>
        [Test]
        public void EveryLanguage_HasItsOwnBody()
        {
            Assert.AreNotEqual(TitleNotice.BodyFor(ShowLang.Ja), TitleNotice.BodyFor(ShowLang.En));
            Assert.AreNotEqual(TitleNotice.BodyFor(ShowLang.En), TitleNotice.BodyFor(ShowLang.Fr));
            Assert.AreNotEqual(TitleNotice.BodyFor(ShowLang.Fr), TitleNotice.BodyFor(ShowLang.Ja));
            foreach (ShowLang lang in ShowLanguage.All)
                Assert.That(TitleNotice.BodyFor(lang), Is.Not.Empty, $"{lang}");
        }

        /// <summary>
        /// <b>安全の掲示は言語で内容を変えない。</b> 日本語が言っている 2 つ
        /// （① 怖い表現が入っている ② 気分が悪くなったら外してスタッフへ）は、
        /// どの言語でも<b>空行で分けた 2 つの塊</b>として出る。
        /// ⚠ 片方だけ訳し落とすと、その言語の体験者だけ逃げ方を知らないまま入る。
        /// </summary>
        [Test]
        public void EveryLanguage_KeepsBothHalvesOfTheNotice()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] blocks = TitleNotice.BodyFor(lang).Split(new[] { "\n\n" },
                                                                  System.StringSplitOptions.None);
                Assert.AreEqual(2, blocks.Length, $"{lang}: 空行で分かれた 2 つの塊");
                foreach (string b in blocks) Assert.That(b.Trim(), Is.Not.Empty, $"{lang}");
            }
        }

        /// <summary>
        /// <b>次にすることは、選んでいる言語で 1 行だけ</b>。この行を読むのは言語を選んだ後なので、
        /// そのとき選ばれているのは<b>読める言語</b>。
        /// </summary>
        [Test]
        public void Notice_TellsWhatToDoNext_InTheChosenLanguage()
        {
            (ShowLang lang, string mine, string[] others)[] cases =
            {
                (ShowLang.Ja, "準備ができたら", new[] { "When ready", "Quand vous êtes" }),
                (ShowLang.En, "When ready", new[] { "準備ができたら", "Quand vous êtes" }),
                (ShowLang.Fr, "Quand vous êtes", new[] { "準備ができたら", "When ready" }),
            };
            foreach ((ShowLang lang, string mine, string[] others) in cases)
            {
                string n = TitleNotice.NoticeFor(lang);
                StringAssert.Contains(mine, n, $"[{lang}] 次にすることが自分の言語で無い");
                foreach (string other in others)
                    StringAssert.DoesNotContain(other, n, $"[{lang}] 他の言語の行まで出ている");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>掲示の最後と「次にすること」で動詞を変える</b>（2026-09-05・0155）。
        /// 並びが変わって<b>この 2 行が隣り合った</b>ので、同じ言い回しだと
        /// 2 行続けて同じことを言っているように読める。
        /// </summary>
        [Test]
        public void ReadyLine_DoesNotRepeatTheWordingOfTheNoticesLastLine()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] body = TitleNotice.BodyFor(lang).Split('\n');
                string last = body[body.Length - 1].Trim();
                string ready = TitleNotice.ReadyLineOf(lang).Trim();
                Assert.AreNotEqual(last, ready, $"[{lang}] 掲示の最後と同じ行");
                // 末尾 8 文字（動詞のあたり）が一致していたら、読み手には同じ文に見える。
                string TailOf(string s) => s.Length <= 8 ? s : s.Substring(s.Length - 8);
                Assert.AreNotEqual(TailOf(last), TailOf(ready),
                                   $"[{lang}] 語尾が同じ（「{last}」/「{ready}」）");
            }
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }
    }
}
