#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 劇伴を置き換える 3 つ（<c>canon/LEDGER.md</c> 0131）。
    ///
    /// | どこ | 何が鳴るか |
    /// |---|---|
    /// | 別の場所（バックルームズ）が映っているあいだ | 風だけ（劇伴は切れる） |
    /// | 3 周目 B 〜 呪いが排除されるまで | ビート ＋ ホラーな曲（劇伴と入れ替え） |
    /// | 呪いが排除された後 | ホワイトノイズ |
    ///
    /// ⚠⚠ <b>どれも画に 1 ビットも出ない。</b> 異世界の演出は画としては出るが、
    /// そのとき音が入れ替わったかは録画からは分からない。走行の
    /// <c>sndWind</c> / <c>sndCurse</c> / <c>sndWhite</c> と、ここだけが証拠。
    /// </summary>
    public sealed class SoundLayerSwapTests
    {
        private const float Dt = 1f / 72f;

        private static SoundShowState Run(int lap, int camera)
        {
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            s.lap = lap;
            s.camera = camera;
            return s;
        }

        /// <summary>その状態を <paramref name="sec"/> 秒ぶん進める。</summary>
        private static SoundBedGains Settle(SoundBedLogic logic, SoundShowState s, float sec)
        {
            var g = default(SoundBedGains);
            for (int i = 0; i < (int)(sec / Dt); i++) g = logic.Tick(Dt, s);
            return g;
        }

        // ---- ① 別の場所（バックルームズ）--------------------------------------

        /// <summary>
        /// 異世界が映っているあいだは風だけ。⚠ <b>劇伴は切れる</b>
        /// （ユーザー指定「その時流れてる環境音は切る」）。
        /// </summary>
        [Test]
        public void Otherworld_PlaysWind_AndSilencesTheScore()
        {
            var logic = new SoundBedLogic();
            var s = Run(1, 1);
            var g = Settle(logic, s, 6f);
            Assert.Greater(g.score, 0.9f, "本編では劇伴が鳴っている");
            Assert.That(g.wind, Is.EqualTo(0f).Within(1e-3f));

            s.otherworld = true;
            g = Settle(logic, s, 1.2f);   // 出入りの尺は 0.6 秒
            Assert.Greater(g.wind, 0.9f, "異世界のあいだ風が鳴る");
            Assert.Less(g.score, 0.05f, "そのあいだ劇伴は切れている");
        }

        /// <summary>
        /// ⚠⚠ <b>4 秒の演出に間に合う速さで入れ替わる。</b> 劇伴の既定の退き（2.0 秒）だと
        /// 窓の半分を食う。画は乱れで一瞬に切り替わるので、音もそれに合わせる。
        /// </summary>
        [Test]
        public void Otherworld_SwapsFastEnoughForA4SecondCut()
        {
            var logic = new SoundBedLogic();
            var s = Run(1, 1);
            Settle(logic, s, 6f);

            s.otherworld = true;
            var g = Settle(logic, s, 0.8f);
            Assert.Greater(g.wind, 0.8f, "0.8 秒で風がほぼ立っていること");
            Assert.Less(g.score, 0.2f, "0.8 秒で劇伴がほぼ退いていること");
        }

        /// <summary>抜けたら劇伴が戻る（風は消える）。</summary>
        [Test]
        public void LeavingTheOtherworld_BringsTheScoreBack()
        {
            var logic = new SoundBedLogic();
            var s = Run(1, 1);
            Settle(logic, s, 6f);
            s.otherworld = true;
            Settle(logic, s, 1.2f);

            s.otherworld = false;
            var g = Settle(logic, s, 1.2f);
            Assert.Less(g.wind, 0.05f);
            Assert.Greater(g.score, 0.9f);
        }

        // ---- ② 3 周目 B 〜 呪いの排除 -----------------------------------------

        /// <summary>3 周目 A ではまだ鳴らない（「3-B から」）。</summary>
        [Test]
        public void Curse_DoesNotStartBefore3B()
        {
            var logic = new SoundBedLogic();
            var g = Settle(logic, Run(3, 0), 4f);
            Assert.That(g.beat, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(g.horror2, Is.EqualTo(0f).Within(1e-3f));
            Assert.Greater(g.score, 0.9f, "そこまでは劇伴が鳴っている");
        }

        /// <summary>3 周目 B で 2 本が入り、劇伴と入れ替わる。</summary>
        [Test]
        public void Curse_StartsAt3B_AndCrossfadesWithTheScore()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 0), 4f);
            var g = Settle(logic, Run(3, 1), 4f);
            Assert.Greater(g.beat, 0.9f);
            Assert.That(g.horror2, Is.EqualTo(g.beat).Within(1e-4f), "2 本は常に同じ値");
            Assert.Less(g.score, 0.05f, "劇伴と入れ替わっている");
        }

        /// <summary>
        /// ⚠⚠ <b>引き返しても消えない。</b> 3 周目 C から A へ戻るのは実際に起きる
        /// （<c>rules/show-design.md</c>「体験者は引き返す」）。区間の条件だけで見ていると
        /// <b>戻った瞬間に曲が止まる</b>。
        /// </summary>
        [Test]
        public void Curse_SurvivesBacktracking()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            Settle(logic, Run(3, 2), 2f);
            var g = Settle(logic, Run(3, 0), 4f);   // 引き返した
            Assert.Greater(g.beat, 0.9f, "引き返しで止まらないこと");
        }

        /// <summary>帰りの A（4 周目）でも鳴り続ける（まだ呪いは解けていない）。</summary>
        [Test]
        public void Curse_KeepsPlayingOnTheWayBack()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            var g = Settle(logic, Run(4, 0), 4f);
            Assert.Greater(g.beat, 0.9f);
        }

        // ---- ③ 呪いが排除された後 ---------------------------------------------

        /// <summary>報告が通ると 2 本が退き、ホワイトノイズへ入れ替わる。</summary>
        [Test]
        public void Release_SwapsTheCursePairForWhiteNoise()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            var s = Run(4, 0);
            Settle(logic, s, 2f);

            s.curseReleased = true;
            var g = Settle(logic, s, 4f);
            Assert.Less(g.beat, 0.05f, "呪いの 2 本は退く");
            Assert.Greater(g.white, 0.9f, "ホワイトノイズが鳴る");
            Assert.Less(g.score, 0.05f, "劇伴は戻らない");
        }

        /// <summary>体験が終わったら何も残さない（既存の規約どおり）。</summary>
        [Test]
        public void Finished_LeavesNothingPlaying()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            var s = Run(4, 0);
            s.curseReleased = true;
            Settle(logic, s, 4f);

            s.phase = ShowPhase.Finished;
            var g = Settle(logic, s, 4f);
            Assert.Less(g.white, 0.05f);
            Assert.Less(g.beat, 0.05f);
            Assert.Less(g.wind, 0.05f);
        }

        /// <summary>位置合わせ中は 3 つとも黙る（スタッフの作業音を通す）。</summary>
        [Test]
        public void Registration_SilencesAllThree()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            var s = Run(3, 1);
            s.otherworld = true;
            s.registrationActive = true;
            var g = Settle(logic, s, 4f);
            Assert.Less(g.wind, 0.05f);
            Assert.Less(g.beat, 0.05f);
            Assert.Less(g.white, 0.05f);
        }

        /// <summary>体験者が替わったら持ち越さない。</summary>
        [Test]
        public void ResetRun_ClearsEverything()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            logic.Reset();
            var g = Settle(logic, Run(1, 0), 0.5f);
            Assert.Less(g.beat, 0.05f, "前の体験者の呪いが残らない");
            Assert.Less(g.white, 0.05f);
            Assert.Less(g.wind, 0.05f);
        }
    }
}
