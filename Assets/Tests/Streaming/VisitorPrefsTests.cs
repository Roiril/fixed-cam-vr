#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>タブレットで選んだ設定を運ぶ箱</b>（2026-09-11・<c>canon/LEDGER.md</c> 0185）。
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

        /// <summary>役が無い機では枠を受け取っても何も起きない（結ばれていない機に他人の設定を書かない）。</summary>
        [Test]
        public void WithoutRole_NothingIsPending()
        {
            VisitorPrefs.Offer(3, "en", true);
            // 役が無くても Offer は受けるが、役を解いた縁で忘れる。ここは役を一度も結んでいない場合。
            VisitorPrefs.SetRole("");
            Assert.AreEqual(0, VisitorPrefs.PendingEpoch);
            Assert.IsFalse(VisitorPrefs.HasUnapplied);
            Assert.IsFalse(VisitorPrefs.ApplyPending());
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
        }

        /// <summary><b>書くのは呼んだときだけ。</b>受け取っただけでは static は動かない（本編の途中で言語が変わらない）。</summary>
        [Test]
        public void Offer_DoesNotWriteUntilApplied()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(1, "fr", true);
            Assert.IsTrue(VisitorPrefs.HasUnapplied);
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current, "受け取っただけで書いている");
            Assert.IsFalse(HorrorRelief.Enabled);

            Assert.IsTrue(VisitorPrefs.ApplyPending());
            Assert.AreEqual(ShowLang.Fr, ShowLanguage.Current);
            Assert.IsTrue(HorrorRelief.Enabled);
            Assert.AreEqual(1, VisitorPrefs.AppliedEpoch);
            Assert.AreEqual(1, VisitorPrefs.ApplyCount);
        }

        /// <summary>
        /// ⚠ <b>体験者が押した回数を動かさない。</b><c>langN</c> / <c>relief=</c> の 2 つ目は
        /// 「押して変わった回数」で、卓の書き込みを混ぜると解析器が「注意書きの外で言語が変わった」と読む。
        /// </summary>
        [Test]
        public void Apply_DoesNotTouchVisitorChangeCounts()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(1, "en", true);
            VisitorPrefs.ApplyPending();
            Assert.AreEqual(0, ShowLanguage.ChangeCount);
            Assert.AreEqual(0, HorrorRelief.ChangeCount);
        }

        /// <summary>同じ世代は二度書かない（毎フレーム呼ばれても static を叩き続けない）。</summary>
        [Test]
        public void SameEpoch_IsNotAppliedTwice()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(1, "en", false);
            Assert.IsTrue(VisitorPrefs.ApplyPending());
            Assert.IsFalse(VisitorPrefs.ApplyPending());
            Assert.AreEqual(1, VisitorPrefs.ApplyCount);
            // 卓が同じ世代を言い直しても（long-poll の再配布）変わらない
            VisitorPrefs.Offer(1, "en", false);
            Assert.IsFalse(VisitorPrefs.HasUnapplied);
        }

        /// <summary>
        /// ⚠⚠ <b>タイトルの出し直しは「戻す → 載せる」。</b>同じ世代でももう一度書く。
        /// これが無いと、スタッフがランをやり直した瞬間にタブレットの設定が消える。
        /// </summary>
        [Test]
        public void ApplyAtTitle_ReappliesTheSameEpoch_AfterReset()
        {
            VisitorPrefs.SetRole("beta");
            VisitorPrefs.Offer(4, "fr", true);
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

        /// <summary>枠が無い（世代 0）機では出し直しで何も載せない ＝ 従来どおり既定のまま。</summary>
        [Test]
        public void ApplyAtTitle_WithoutSlot_LeavesDefaults()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(0, null, false);
            ShowLanguage.Reset();
            HorrorRelief.Reset();
            Assert.IsFalse(VisitorPrefs.ApplyAtTitle());
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.IsFalse(HorrorRelief.Enabled);
        }

        /// <summary>
        /// <b>始めた瞬間の世代を記録する。</b>heartbeat がこれを卓へ返し、卓が枠を既定へ戻す
        /// （次の人へ持ち越さない）。世代が進んでいなければ記録は動かない。
        /// </summary>
        [Test]
        public void Consume_RecordsThePendingEpoch_Once()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(2, "en", false);
            Assert.IsTrue(VisitorPrefs.Consume());
            Assert.AreEqual(2, VisitorPrefs.ConsumedEpoch);
            Assert.IsFalse(VisitorPrefs.Consume(), "同じ世代を二度消費している");
            // 次の人がタブレットで選び直したら、また消費できる
            VisitorPrefs.Offer(3, "fr", true);
            Assert.IsTrue(VisitorPrefs.Consume());
            Assert.AreEqual(3, VisitorPrefs.ConsumedEpoch);
        }

        /// <summary>
        /// 卓が枠を既定へ戻す（世代 +1・日本語・軽減なし）と、それも「書く値」として届く。
        /// 次のタイトルで既定が載る ＝ 前の人の設定が消える経路そのもの。
        /// </summary>
        [Test]
        public void ResetFromDesk_ArrivesAsANewEpochOfDefaults()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(5, "fr", true);
            VisitorPrefs.ApplyPending();
            VisitorPrefs.Consume();
            // 卓が戻した
            VisitorPrefs.Offer(6, "ja", false);
            Assert.IsTrue(VisitorPrefs.HasUnapplied);
            ShowLanguage.Reset(); HorrorRelief.Reset();
            Assert.IsTrue(VisitorPrefs.ApplyAtTitle());
            Assert.AreEqual(ShowLang.Ja, ShowLanguage.Current);
            Assert.IsFalse(HorrorRelief.Enabled);
        }

        /// <summary>役を解いたら枠も忘れる（別の役の値を引きずらない）。</summary>
        [Test]
        public void ClearingRole_ForgetsPending()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(2, "en", true);
            VisitorPrefs.SetRole("");
            Assert.AreEqual(0, VisitorPrefs.PendingEpoch);
            Assert.IsFalse(VisitorPrefs.HasUnapplied);
        }

        /// <summary>読めない言語コードは既定へ倒す（卓の打ち間違いで止めない）。</summary>
        [Test]
        public void UnknownLang_FallsBackToDefault()
        {
            VisitorPrefs.SetRole("alpha");
            VisitorPrefs.Offer(1, "de", false);
            Assert.AreEqual(ShowLang.Ja, VisitorPrefs.PendingLang);
        }
    }
}
