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

        /// <summary>
        /// ⚠⚠ <b>入れ替わりの途中で背景が凹まない</b>（等パワーの対・2026-09-04）。
        /// 初版は劇伴の退き（聴感直線）と風の入り（聴感直線）を別々に走らせていて、
        /// 中央で合成パワーが 0.20（-7dB）まで凹んでいた（走行 20260903_210843 の t=107.4 に
        /// 劇伴 0.21 / 呪い 0.44 の標本）。ユーザー指示は「クロスフェードで入れ替える」（0131）。
        /// </summary>
        [Test]
        public void Otherworld_CrossfadeKeepsTheBackgroundPowerFlat()
        {
            var logic = new SoundBedLogic();
            var s = Run(1, 1);
            Settle(logic, s, 6f);

            s.otherworld = true;
            AssertPowerFlat(logic, s, 1.2f, g => g.score, g => g.wind, "入り");
            s.otherworld = false;
            AssertPowerFlat(logic, s, 1.2f, g => g.score, g => g.wind, "戻り");
        }

        /// <summary>3 周目 B の入れ替わり（劇伴 → 呪いの 2 本）も同じ。</summary>
        [Test]
        public void Curse_CrossfadeKeepsTheBackgroundPowerFlat()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 0), 4f);
            AssertPowerFlat(logic, Run(3, 1), 4f, g => g.score, g => g.beat, "3 周目 B");
        }

        /// <summary>
        /// 呪い → ホワイトノイズの受け渡しのあいだ、<b>劇伴は 0 のまま</b>で、
        /// 2 つのパワーの和は 1 から動かない。⚠ 相手を <c>max</c> で見る実装だと、
        /// 受け渡しの中央で劇伴が 0.7 まで戻る（和で見ること）。
        /// </summary>
        [Test]
        public void Release_HandoffKeepsTheScoreSilent_AndThePowerFlat()
        {
            var logic = new SoundBedLogic();
            Settle(logic, Run(3, 1), 4f);
            var s = Run(4, 0);
            Settle(logic, s, 2f);

            s.curseReleased = true;
            float maxScore = 0f;
            for (int i = 0; i < (int)(4f / Dt); i++)
            {
                var g = logic.Tick(Dt, s);
                maxScore = System.Math.Max(maxScore, g.score);
                float p = g.beat * g.beat + g.white * g.white;
                Assert.That(p, Is.EqualTo(1f).Within(0.03f), $"受け渡しの途中で背景が動いた（{i} tick 目）");
            }
            Assert.Less(maxScore, 0.05f, "受け渡しの途中で劇伴が戻った");
        }

        /// <summary>
        /// 2 つの倍率が入れ替わるあいだ、<b>途中の tick でも</b>二乗の和が 1 のままで、
        /// かつ両方が同時に立つ瞬間がある（＝ 切り替えではなく入れ替え）。
        /// </summary>
        private static void AssertPowerFlat(SoundBedLogic logic, SoundShowState s, float sec,
                                            System.Func<SoundBedGains, float> a,
                                            System.Func<SoundBedGains, float> b, string where)
        {
            bool sawBoth = false;
            for (int i = 0; i < (int)(sec / Dt); i++)
            {
                var g = logic.Tick(Dt, s);
                float x = a(g), y = b(g);
                Assert.That(x * x + y * y, Is.EqualTo(1f).Within(0.03f),
                            $"{where}: {i} tick 目で合成パワーが 1 から外れた（{x:F3} / {y:F3}）");
                if (x > 0.3f && y > 0.3f) sawBoth = true;
            }
            Assert.IsTrue(sawBoth, $"{where}: 両方が立つ瞬間が無い ＝ 入れ替えではなく切り替えになっている");
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
