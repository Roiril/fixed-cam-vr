#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>体験者が選ぶ言語</b>（2026-09-03 ユーザー指定・日本語 / English / Français）。
    ///
    /// ⚠ ここが守るのは<b>実機で沈黙して壊れる 2 つ</b>:
    /// 巡る順が壊れて 3 つ目に辿り着けない／体験者が替わっても前の人の言語が残る。
    /// どちらも画には「文字が出ている」ようにしか見えないので、走行の絵では気づけない
    /// （テレメトリの <c>lang</c> / <c>langN</c> と対で読む値）。
    /// </summary>
    public sealed class ShowLanguageTests
    {
        [SetUp]
        public void Reset() => ShowLanguage.Reset();

        [TearDown]
        public void Restore() => ShowLanguage.Reset();

        /// <summary>押さなかった体験者は日本語で読む。</summary>
        [Test]
        public void Default_IsJapanese()
        {
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Default);
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.AreEqual(0, ShowLanguage.ChangeCount);
        }

        /// <summary>
        /// <b>1 つのボタンで 3 つとも選べる</b>（被った体験者に手元は見えないので、
        /// X も Y も同じ「次へ」にしてある ＝ 巡る以外の選び方が無い）。
        /// ⚠ ここが 2 つで止まると、<b>フランス語だけ永久に選べない</b>。
        /// </summary>
        [Test]
        public void Cycle_VisitsEveryLanguage_ThenComesBack()
        {
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.IsTrue(ShowLanguage.Cycle());
            Assert.AreEqual(ShowLang.En, ShowLanguage.Current);
            Assert.IsTrue(ShowLanguage.Cycle());
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.IsTrue(ShowLanguage.Cycle());
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current, "3 回で戻る");
            Assert.AreEqual(3, ShowLanguage.ChangeCount);
        }

        /// <summary><see cref="ShowLanguage.All"/> の順と <see cref="ShowLanguage.Next"/> が食い違わない。</summary>
        [Test]
        public void Next_FollowsTheDeclaredOrder()
        {
            for (int i = 0; i < ShowLanguage.All.Length; i++)
            {
                ShowLang want = ShowLanguage.All[(i + 1) % ShowLanguage.All.Length];
                Assert.AreEqual(want, ShowLanguage.Next(ShowLanguage.All[i]));
            }
        }

        /// <summary>
        /// ⚠⚠ <b>体験者が替わったら既定へ戻る。</b> 戻さないと、フランス語を選んだ人の次に来た人が
        /// フランス語で読むことになる（本人は何も押していないので、直す手が無い）。
        /// 呼ぶのは <c>TitleScreen.BeginTitle</c> ＝ タイトルの出し直し 1 か所。
        /// </summary>
        [Test]
        public void Reset_ForgetsTheVisitorsChoice()
        {
            ShowLanguage.Cycle();
            ShowLanguage.Cycle();
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);

            ShowLanguage.Reset();

            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.AreEqual(0, ShowLanguage.ChangeCount, "回数も走行ごとの値（テレメトリの langN）");
        }

        /// <summary>
        /// <see cref="ShowLanguage.Select"/> は検査用。<b>体験者が押した回数を汚さない</b>
        /// （汚すと <c>ev=sum</c> の <c>langN</c> が「押していないのに変わった」と言う）。
        /// </summary>
        [Test]
        public void Select_DoesNotCountAsTheVisitorPressing()
        {
            ShowLanguage.Select(ShowLang.Fr);
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.AreEqual(0, ShowLanguage.ChangeCount);
        }

        /// <summary>
        /// ログの短い名前は往復する。⚠ <b>ASCII だけ</b> — ここに非 ASCII を入れると
        /// フォントの静的ベイクの収集元に無いファイルへ字が増えて、実機で豆腐になる。
        /// </summary>
        [Test]
        public void Code_RoundTrips_AndStaysAscii()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string code = ShowLanguage.Code(lang);
                Assert.That(code, Is.Not.Empty);
                foreach (char c in code)
                    Assert.Less((int)c, 0x80, $"{lang} の名前に非 ASCII が混ざっている: {code}");
                Assert.AreEqual(lang, ShowLanguage.Parse(code));
            }
        }

        /// <summary>
        /// ⚠⚠ <b>言語が変わるのは単押しだけ</b>（2026-09-05 ユーザー指定・<c>canon/LEDGER.md</c> 0159
        /// 「長押しした後に離すと言語が変わるのが面倒なので、単押し以外で言語は変わらないように」）。
        /// 軽減モードを狙って握って途中でやめた回は、離しても何も起きない。
        /// </summary>
        [Test]
        public void IsTap_TakesOnlyAShortPress()
        {
            Assert.IsTrue(ShowLanguage.IsTap(0f), "押した瞬間に離した回");
            Assert.IsTrue(ShowLanguage.IsTap(ShowLanguage.TapMaxSec * 0.5f));
            Assert.IsTrue(ShowLanguage.IsTap(ShowLanguage.TapMaxSec), "境目は単押しの側");
            Assert.IsFalse(ShowLanguage.IsTap(ShowLanguage.TapMaxSec + 0.01f), "窓を過ぎても通る");
            Assert.IsFalse(ShowLanguage.IsTap(HorrorRelief.HoldSec - 0.1f),
                           "長押しをやめた回で言語が変わる（0159 で直した形）");
            Assert.IsFalse(ShowLanguage.IsTap(-1f), "負の秒");
        }

        /// <summary>
        /// ⚠⚠ <b>単押しの窓は長押しより必ず短い。</b> 逆転すると、長押しが成立する前に
        /// どの押下も単押しになり<b>軽減モードへ入れなくなる</b>（画にも音にも出ない壊れ方）。
        /// </summary>
        [Test]
        public void TheTapWindow_EndsWellBeforeTheHold()
        {
            Assert.Greater(ShowLanguage.TapMaxSec, 0f, "単押しが 1 つも通らない");
            Assert.Less(ShowLanguage.TapMaxSec, HorrorRelief.HoldSec, "長押しへ辿り着けない");
        }

        /// <summary>読めない指定（CLI の打ち間違い）は既定へ倒す — 検査を止めない。</summary>
        [Test]
        public void Parse_FallsBackToTheDefault()
        {
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Parse(null));
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Parse(""));
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Parse("de"));
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Parse("FR"), "大文字小文字は問わない");
        }
    }
}
