#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>節目の検出。**1 回だけ鳴ること**と**ランをまたがないこと**を固定する。</summary>
    public class SoundCueLogicTests
    {
        private const float Dt = 1f / 72f;

        private static SoundShowState Intro(IntroStage stage, float shell = 0f,
                                            float shatter = 0f, float ignite = 1f)
        {
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = stage;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = shell;
            s.introWeights.shatter = shatter;
            // ⚠ 既定は Inactive の 1（＝ 管が点いている）。**段 3 の途中を作るときだけ下げる。**
            s.introWeights.ignite = ignite;
            return s;
        }

        private static int CountOf(SoundCueLogic l, SoundCue want, float dt,
                                   in SoundShowState s, float glitch = 0f)
        {
            var fired = l.Tick(dt, s, glitch, out int n);
            int c = 0;
            for (int i = 0; i < n; i++)
            {
                if (fired[i] == want) c++;
            }
            return c;
        }

        [Test]
        public void SealClose_DoesNotFireWhileWaitingInStageZero()
        {
            // ⚠⚠ **起動直後に鳴っていた**（2026-08-14 実機・canon/LEDGER.md 0035）。
            //    段 0 で位置が解けていないと `outsideBoxM` が 0（中に居る扱い）へ倒れ、
            //    `OutsideWeights` が `shell = 1` を返す。重みだけを見ると「閉じた」に見えるが、
            //    体験者はまだ何もしていない。しかもラッチなので**本当に閉じる段で鳴らなくなる**。
            var l = new SoundCueLogic();
            for (int i = 0; i < 120; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Black, shell: 1f)),
                                "段 0（開始待ち）で隔離の音が鳴った");

            // 本当に閉じる段へ来たら鳴る。
            Assert.AreEqual(1, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Seal, shell: 1f)));
        }

        [Test]
        public void SealClose_FiresOnce_WhenTheShellStartsClosing()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Seal)));
            Assert.AreEqual(1, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Seal, shell: 0.2f)));
            for (int i = 0; i < 60; i++)
            {
                Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Seal, shell: 1f)),
                                "隔離の音が鳴り続けている");
            }
        }

        [Test]
        public void Shatter_And_Swap_FireOnce_Each()
        {
            // ⚠ Shatter は 2026-08-13 に**導入から鳴らなくなった**（段 4「破砕」を廃止し
            //    重み shatter を眠らせた）。検出の仕掛け自体は残してあるので、ここでは
            //    重みを直接与えて経路が生きていることだけを固定する。
            var l = new SoundCueLogic();
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Dark, shatter: 0.1f)));
            Assert.AreEqual(0, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Dark, shatter: 0.9f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.Swap, Dt, Intro(IntroStage.Live)));
            Assert.AreEqual(0, CountOf(l, SoundCue.Swap, Dt, Intro(IntroStage.Live)));
        }

        [Test]
        public void ScreenOn_FiresOnceWhenTheTubeIgnites()
        {
            // 破砕を廃したので、**導入の山はここ**（闇の中で管に電源が入る）。
            // 段の頭で 1 回だけ鳴る — 鳴り続けると効果音になる。
            var l = new SoundCueLogic();
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Dark)),
                            "闇の段で先に鳴っている");
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Ignite)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Ignite)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Live)));
        }

        [Test]
        public void ScreenOn_HasASourceAndDucksLikeAPeak()
        {
            // ⚠ **音源の無い節目を作らない**（rules/sound-design.md §8）。
            Assert.AreEqual("sfx_screen_on", SoundCueLogic.ResourceName(SoundCue.ScreenOn));
            // 導入の山なので、劇伴は隔離が閉じる音と同じくらい深く退く。
            Assert.That(SoundCueLogic.DuckFor(SoundCue.ScreenOn),
                        Is.GreaterThanOrEqualTo(SoundCueLogic.DuckFor(SoundCue.SealClose)));
        }

        [Test]
        public void ScreenNoise_FollowsScreenOn_WhenTheTubeFaceFills()
        {
            // ユーザー指示（canon/LEDGER.md 0030）「これを最初に出して、その後ノイズを出す」。
            // **同じフレームで 2 本鳴らさない** — 重ねると 1 つの音に潰れて「その後」にならない。
            var l = new SoundCueLogic();
            var early = Intro(IntroStage.Ignite, ignite: 0.1f);
            var fired = l.Tick(Dt, early, 0f, out int n);
            int on = 0, noise = 0;
            for (int i = 0; i < n; i++)
            {
                if (fired[i] == SoundCue.ScreenOn) on++;
                if (fired[i] == SoundCue.ScreenNoise) noise++;
            }
            Assert.AreEqual(1, on, "段 3 の頭で一撃が鳴っていない");
            Assert.AreEqual(0, noise, "一撃と同じフレームでノイズも鳴っている");

            // 面が満ち始める所で 2 本目。**1 回だけ。**
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenNoise, Dt,
                                       Intro(IntroStage.Ignite, ignite: SoundCueLogic.ScreenNoiseAt - 0.05f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenNoise, Dt,
                                       Intro(IntroStage.Ignite, ignite: SoundCueLogic.ScreenNoiseAt)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenNoise, Dt, Intro(IntroStage.Ignite, ignite: 1f)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenNoise, Dt, Intro(IntroStage.Live)));
        }

        [Test]
        public void ScreenNoise_HasASourceAndStaysUnderTheHit()
        {
            // ⚠ **音源の無い節目を作らない**（rules/sound-design.md §8）。
            Assert.AreEqual("sfx_screen_noise", SoundCueLogic.ResourceName(SoundCue.ScreenNoise));
            // 山の尾なので、劇伴は退かせたまま。ただし一撃より深くはしない。
            Assert.That(SoundCueLogic.DuckFor(SoundCue.ScreenNoise),
                        Is.LessThan(SoundCueLogic.DuckFor(SoundCue.ScreenOn)));
            Assert.That(SoundCueLogic.DuckFor(SoundCue.ScreenNoise), Is.GreaterThan(0f));
        }

        [Test]
        public void Title_IsSilentWhileWaitingInTheDark_ThenFiresWhenTheGlyphStands()
        {
            // ⚠ 2026-08-12 にタイトルの流れが変わった。周回リセット直後は**真っ暗で A を待つ**
            //    だけなので、そこで音を鳴らすと「まだ何も始まっていない」と食い違う。
            //    鳴らす縁は「画面を持った」ではなく「**字が立った**」。
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.titleVisible = true;                       // 真っ暗で A 待ち
            for (int i = 0; i < 60; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.TitleIn, Dt, s), "真っ暗の間に鳴っている");

            s.titleGlyphShowing = true;                  // A を押した
            Assert.AreEqual(1, CountOf(l, SoundCue.TitleIn, Dt, s));
            Assert.AreEqual(0, CountOf(l, SoundCue.TitleIn, Dt, s));

            s.titleGlyphShowing = false;                 // 2 秒後に消え始める
            Assert.AreEqual(1, CountOf(l, SoundCue.TitleOut, Dt, s));
            Assert.AreEqual(0, CountOf(l, SoundCue.TitleOut, Dt, s));
        }

        [Test]
        public void Creak_FiresSparsely_AndNeverAtAFixedInterval()
        {
            // 家鳴りは**等間隔にしない**。規則正しいと建物ではなく機械に聞こえる。
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            var gaps = new System.Collections.Generic.List<float>();
            float since = 0f;
            for (int i = 0; i < 60 * 300; i++)           // 5 分
            {
                since += Dt;
                if (CountOf(l, SoundCue.Creak, Dt, s) > 0) { gaps.Add(since); since = 0f; }
            }
            Assert.Greater(gaps.Count, 8, "5 分で家鳴りが少なすぎる");
            Assert.Less(gaps.Count, 40, "5 分で家鳴りが多すぎる（にぎやかになる）");
            foreach (float g in gaps)
            {
                Assert.GreaterOrEqual(g, SoundCueLogic.CreakMinSec - 0.5f);
                Assert.LessOrEqual(g, SoundCueLogic.CreakMaxSec + 0.5f);
            }
            var uniq = new System.Collections.Generic.HashSet<int>();
            foreach (float g in gaps) uniq.Add((int)(g * 4));
            Assert.Greater(uniq.Count, 3, "間隔がほぼ一定（機械に聞こえる）");
        }

        [Test]
        public void Creak_IsSilentDuringTheIntroStages_AndDuringRegistration()
        {
            // 導入の演出中に家鳴りが割り込むと、段の出来事が薄まる。
            var l = new SoundCueLogic();
            var s = Intro(IntroStage.Dark);
            for (int i = 0; i < 60 * 120; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Creak, Dt, s), "導入の途中で鳴っている");

            var r = SoundShowState.Idle;
            r.phase = ShowPhase.Run;
            r.registrationActive = true;
            for (int i = 0; i < 60 * 120; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Creak, Dt, r), "位置合わせ中に鳴っている");
        }

        [Test]
        public void Bell_RingsOnce_InStageZero()
        {
            var l = new SoundCueLogic();
            var s = Intro(IntroStage.Black);
            int total = 0;
            for (int i = 0; i < 60 * 120; i++) total += CountOf(l, SoundCue.Bell, Dt, s);
            Assert.AreEqual(1, total, "鈴は 1 回だけ");

            l.ResetRun();
            total = 0;
            for (int i = 0; i < 60 * 120; i++) total += CountOf(l, SoundCue.Bell, Dt, s);
            Assert.AreEqual(1, total, "次の体験者にはもう一度鳴る");
        }

        [Test]
        public void Bell_DoesNotRingImmediately()
        {
            // 段 0 に入った瞬間に鳴ると「押したから鳴った」に読まれる。
            var l = new SoundCueLogic();
            var s = Intro(IntroStage.Black);
            for (int i = 0; i < (int)(SoundCueLogic.BellAtSec * 60) - 30; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Bell, 1f / 60f, s));
        }

        [Test]
        public void Cues_AreDeterministic_AcrossRuns()
        {
            // **走行のたびに違う音が出ると、何が効いたのか分からなくなる。**
            var s = SoundShowState.Idle;
            s.phase = ShowPhase.Run;
            var first = new System.Collections.Generic.List<int>();
            var second = new System.Collections.Generic.List<int>();
            foreach (var list in new[] { first, second })
            {
                var l = new SoundCueLogic();
                for (int i = 0; i < 60 * 200; i++)
                    if (CountOf(l, SoundCue.Creak, Dt, s) > 0) list.Add(i);
            }
            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void Glitch_Rearms_ButRespectsMinInterval()
        {
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            Assert.AreEqual(1, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0.5f));
            // 下がって上がっても、最短間隔の内は鳴らない（連射は「効果音」に聞こえる）
            Assert.AreEqual(0, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0f));
            Assert.AreEqual(0, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0.5f));
            // 間隔を空ければ鳴る
            for (int i = 0; i < 40; i++) l.Tick(Dt, s, 0f, out _);
            Assert.AreEqual(1, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0.5f));
        }

        [Test]
        public void Glitch_DoesNotRearm_WhileStillLoud()
        {
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            Assert.AreEqual(1, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0.9f));
            for (int i = 0; i < 200; i++)
            {
                Assert.AreEqual(0, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0.9f),
                                "強い乱れが続く間に連射している");
            }
        }

        [Test]
        public void ResetRun_ClearsEveryLatch_SoTheNextVisitorHearsItAll()
        {
            var l = new SoundCueLogic();
            CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Seal, shell: 1f));
            CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Dark, shatter: 1f));
            l.ResetRun();
            Assert.AreEqual(1, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Seal, shell: 1f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Dark, shatter: 1f)));
        }

        [Test]
        public void Outro_Open_FiresOnce()
        {
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.outroActive = true;
            s.outroStage = OutroStage.Unswap;
            Assert.AreEqual(0, CountOf(l, SoundCue.ShellOpen, Dt, s));
            s.outroStage = OutroStage.Open;
            Assert.AreEqual(1, CountOf(l, SoundCue.ShellOpen, Dt, s));
            Assert.AreEqual(0, CountOf(l, SoundCue.ShellOpen, Dt, s));
        }

        [Test]
        public void EveryCue_HasAResourceName_AndAVariantCount()
        {
            // ⚠ **音源が無い節目を作らない。** 名前が空だと実機で黙って何も鳴らず、
            //    ログにも出ない（画と違って録画にも映らないので、絶対に気づけない）。
            foreach (SoundCue c in System.Enum.GetValues(typeof(SoundCue)))
            {
                if (c == SoundCue.None) continue;
                Assert.IsNotEmpty(SoundCueLogic.ResourceName(c), $"{c} に音源名が無い");
                Assert.GreaterOrEqual(SoundCueLogic.VariantCount(c), 1);
            }
        }

        [Test]
        public void DuckDepth_IsOrdered_ByHowBigTheMomentIs()
        {
            Assert.Greater(SoundCueLogic.DuckFor(SoundCue.Shatter),
                           SoundCueLogic.DuckFor(SoundCue.Swap));
            Assert.Greater(SoundCueLogic.DuckFor(SoundCue.SealClose),
                           SoundCueLogic.DuckFor(SoundCue.ShellOpen));
            Assert.AreEqual(0f, SoundCueLogic.DuckFor(SoundCue.None), 1e-6f);
        }

        [Test]
        public void NothingIsDropped_EvenWhenEverythingLandsOnOneFrame()
        {
            // ⚠ **ありえないほど重なるフレームでも 1 本も落とさない。**
            //    「たぶん重ならない」を根拠に上限を切ると、重なった日に黙って音が消える。
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.titleVisible = true;
            l.Tick(Dt, s, 0f, out _);
            s.titleVisible = false;
            s.introActive = true;
            s.introStage = IntroStage.Live;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = 1f;
            s.introWeights.shatter = 1f;
            l.Tick(Dt, s, 0.9f, out _);
            Assert.AreEqual(0, l.Dropped);
        }
    }
}
