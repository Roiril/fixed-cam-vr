#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>タブレットで選んだ設定を運ぶ箱</b>（2026-09-11・<c>canon/LEDGER.md</c> 0185 / 0187）。
    ///
    /// ⚠ ここが守るのは<b>画にも音にも出ない壊れ方</b>:
    /// タイトルを出し直した瞬間にタブレットの設定が消える／本編の途中で言語が変わる／
    /// 前の人の設定が次の人へ持ち越す／体験者が押した回数（<c>ChangeCount</c>）が卓の書き込みで動く。
    /// </summary>
    public sealed class VisitorPrefsTests
    {
        [SetUp]
        public void Reset()
        {
            VisitorPrefs.Reset();
            ShowLanguage.Reset();
            HorrorRelief.Reset();
        }

        [TearDown]
        public void Restore() => Reset();

        /// <summary><b>書くのは呼んだときだけ。</b>受け取っただけでは static は動かない（本編の途中で言語が変わらない）。</summary>
        [Test]
        public void Set_DoesNotWriteUntilApplied()
        {
            VisitorPrefs.Set(ShowLang.Fr, true, 1);
            Assert.IsTrue(VisitorPrefs.HasPending);
            Assert.IsTrue(VisitorPrefs.HasUnapplied);
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current, "受け取っただけで書いている");
            Assert.IsFalse(HorrorRelief.Enabled);

            Assert.IsTrue(VisitorPrefs.ApplyPending());
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.IsTrue(HorrorRelief.Enabled);
            Assert.AreEqual(1, VisitorPrefs.AppliedSeq);
            Assert.AreEqual(1, VisitorPrefs.ApplyCount);
        }

        /// <summary>
        /// ⚠ <b>体験者が押した回数を動かさない。</b><c>langN</c> / <c>relief=</c> の 2 つ目は
        /// 「押して変わった回数」で、タブレットの書き込みを混ぜると解析器が「注意書きの外で言語が変わった」と読む。
        /// </summary>
        [Test]
        public void Apply_DoesNotTouchVisitorChangeCounts()
        {
            VisitorPrefs.Set(ShowLang.En, true, 1);
            VisitorPrefs.ApplyPending();
            Assert.AreEqual(0, ShowLanguage.ChangeCount);
            Assert.AreEqual(0, HorrorRelief.ChangeCount);
        }

        /// <summary>同じ受理番号は二度書かない（毎フレーム呼ばれても static を叩き続けない）。</summary>
        [Test]
        public void SameSeq_IsNotAppliedTwice()
        {
            VisitorPrefs.Set(ShowLang.En, false, 1);
            Assert.IsTrue(VisitorPrefs.ApplyPending());
            Assert.IsFalse(VisitorPrefs.ApplyPending());
            Assert.AreEqual(1, VisitorPrefs.ApplyCount);
        }

        /// <summary>選び直しは新しい受理番号で届き、また書ける。</summary>
        [Test]
        public void NewerSeq_IsAppliedAgain()
        {
            VisitorPrefs.Set(ShowLang.En, false, 1);
            VisitorPrefs.ApplyPending();
            VisitorPrefs.Set(ShowLang.Fr, true, 2);
            Assert.IsTrue(VisitorPrefs.HasUnapplied);
            Assert.IsTrue(VisitorPrefs.ApplyPending());
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.AreEqual(2, VisitorPrefs.ApplyCount);
        }

        /// <summary>
        /// ⚠⚠ <b>タイトルの出し直しは「戻す → 載せる」。</b>同じ受理番号でももう一度書く。
        /// これが無いと、スタッフがランをやり直した瞬間にタブレットの設定が消える。
        /// </summary>
        [Test]
        public void ApplyAtTitle_ReappliesTheSameSeq_AfterReset()
        {
            VisitorPrefs.Set(ShowLang.Fr, true, 4);
            VisitorPrefs.ApplyPending();
            // TitleScreen.BeginTitle と同じ順
            ShowLanguage.Reset();
            HorrorRelief.Reset();
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.IsTrue(VisitorPrefs.ApplyAtTitle(), "出し直しで載せ直していない");
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.IsTrue(HorrorRelief.Enabled);
            Assert.AreEqual(2, VisitorPrefs.ApplyCount);
        }

        /// <summary>枠が空の機では出し直しで何も載せない ＝ 従来どおり既定のまま。</summary>
        [Test]
        public void ApplyAtTitle_WithEmptySlot_LeavesDefaults()
        {
            ShowLanguage.Reset();
            HorrorRelief.Reset();
            Assert.IsFalse(VisitorPrefs.ApplyAtTitle());
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.IsFalse(HorrorRelief.Enabled);
        }

        /// <summary>
        /// ⚠⚠ <b>体験者が始めたら枠は空になる ＝ 次の人へ持ち越さない。</b>
        /// 次のタイトルでは既定のまま（<c>BeginTitle</c> が戻した値がそのまま残る）。
        /// </summary>
        [Test]
        public void Consume_EmptiesTheSlot_SoTheNextTitleStaysDefault()
        {
            VisitorPrefs.Set(ShowLang.En, true, 2);
            VisitorPrefs.ApplyPending();
            Assert.IsTrue(VisitorPrefs.Consume());
            Assert.AreEqual(2, VisitorPrefs.ConsumedSeq);
            Assert.IsFalse(VisitorPrefs.HasPending);
            Assert.IsFalse(VisitorPrefs.Consume(), "空の枠をもう一度空にしている");
            // 次の人のタイトル
            ShowLanguage.Reset(); HorrorRelief.Reset();
            Assert.IsFalse(VisitorPrefs.ApplyAtTitle());
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.IsFalse(HorrorRelief.Enabled);
        }

        /// <summary>本編中に届いた次の人の選択は、いまの人には書かず、次のタイトルで載る。</summary>
        [Test]
        public void SetDuringRun_WaitsForTheNextTitle()
        {
            VisitorPrefs.Set(ShowLang.En, false, 1);
            VisitorPrefs.ApplyPending();
            VisitorPrefs.Consume();                       // いまの人が始めた
            VisitorPrefs.Set(ShowLang.Fr, true, 2);       // 次の人がタブレットで選んだ（本編中）
            Assert.AreEqual(ShowLang.En, ShowLanguage.Current, "本編中に書いている");
            ShowLanguage.Reset(); HorrorRelief.Reset();   // 次のタイトル
            Assert.IsTrue(VisitorPrefs.ApplyAtTitle());
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.IsTrue(HorrorRelief.Enabled);
        }

        /// <summary>スタッフの「枠を空にする」は枠だけを空にし、書いた値は戻さない（戻すのは BeginTitle）。</summary>
        [Test]
        public void Clear_EmptiesTheSlot_WithoutTouchingStatics()
        {
            VisitorPrefs.Set(ShowLang.Fr, true, 1);
            VisitorPrefs.ApplyPending();
            VisitorPrefs.Clear();
            Assert.IsFalse(VisitorPrefs.HasPending);
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
        }

        /// <summary>受理番号 0 以下は受けない（口が振る番号は 1 から）。</summary>
        [Test]
        public void ZeroSeq_IsIgnored()
        {
            VisitorPrefs.Set(ShowLang.En, true, 0);
            Assert.IsFalse(VisitorPrefs.HasPending);
        }
    }
}
