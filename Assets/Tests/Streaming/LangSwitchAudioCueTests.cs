#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Streaming
{
    /// <summary>
    /// 言語を切り替えたときの音（2026-09-04・<c>canon/LEDGER.md</c> 0153・ユーザー指定
    /// 「以下のを<b>交互</b>になるようにしてほしい」）。
    ///
    /// ⚠ <b>音は録画に映らない</b>ので、順序が壊れても画からは分からない。だから機械が持つ。
    /// </summary>
    public sealed class LangSwitchAudioCueTests
    {
        /// <summary>
        /// <b>交互</b>。1 本目 → 2 本目 → 1 本目 …。
        /// ⚠ 乱数で選ぶと「同じ音が 2 回続いた ＝ 切り替わらなかった」と読まれる。
        /// </summary>
        [Test]
        public void Alternates()
        {
            int[] want = { 0, 1, 0, 1, 0, 1 };
            for (int played = 0; played < want.Length; played++)
            {
                Assert.AreEqual(want[played],
                                LangSwitchAudioCue.IndexFor(played, LangSwitchAudioCue.VariantCount),
                                $"{played + 1} 回目");
            }
        }

        /// <summary>
        /// 1 本しか掴めなかった現場でも<b>黙らない</b>（残った 1 本を鳴らし続ける）。
        /// ⚠ 番号で引くと、片方が焼かれていない日に「1 回おきに無音」という
        /// 気づけない壊れ方をする。
        /// </summary>
        [Test]
        public void WithOneClip_KeepsPlayingIt()
        {
            for (int played = 0; played < 4; played++)
                Assert.AreEqual(0, LangSwitchAudioCue.IndexFor(played, 1), $"{played + 1} 回目");
        }

        /// <summary>体験者が替わったら 1 本目から（次の人には同じ順で聞こえる）。</summary>
        [Test]
        public void AfterReset_StartsFromTheFirstAgain()
        {
            Assert.AreEqual(0, LangSwitchAudioCue.IndexFor(0, LangSwitchAudioCue.VariantCount));
        }

        /// <summary>
        /// 焼く本数（<c>tools/ingest-sounds.py</c> の <c>PLAN</c>）と対。
        /// ⚠ 増やすと「交互」が「順ぐり」になる ＝ ユーザーの指定と別のものになるので、
        /// ここが変わるときは台帳を見る。
        /// </summary>
        [Test]
        public void KeepsTwoClips()
        {
            Assert.AreEqual(2, LangSwitchAudioCue.VariantCount);
        }
    }
}
