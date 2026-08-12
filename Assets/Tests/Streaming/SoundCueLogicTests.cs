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
                                            float shatter = 0f)
        {
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = stage;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = shell;
            s.introWeights.shatter = shatter;
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
        public void SealClose_FiresOnce_WhenTheShellStartsClosing()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Real)));
            Assert.AreEqual(1, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Real, shell: 0.2f)));
            for (int i = 0; i < 60; i++)
            {
                Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Real, shell: 1f)),
                                "隔離の音が鳴り続けている");
            }
        }

        [Test]
        public void Shatter_And_Swap_FireOnce_Each()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 0.1f)));
            Assert.AreEqual(0, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 0.9f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.Swap, Dt, Intro(IntroStage.Swap)));
            Assert.AreEqual(0, CountOf(l, SoundCue.Swap, Dt, Intro(IntroStage.Swap)));
        }

        [Test]
        public void Title_FiresIn_ThenOut()
        {
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.titleVisible = true;
            Assert.AreEqual(1, CountOf(l, SoundCue.TitleIn, Dt, s));
            Assert.AreEqual(0, CountOf(l, SoundCue.TitleIn, Dt, s));
            s.titleVisible = false;
            Assert.AreEqual(1, CountOf(l, SoundCue.TitleOut, Dt, s));
            Assert.AreEqual(0, CountOf(l, SoundCue.TitleOut, Dt, s));
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
            CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Real, shell: 1f));
            CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f));
            l.ResetRun();
            Assert.AreEqual(1, CountOf(l, SoundCue.SealClose, Dt, Intro(IntroStage.Real, shell: 1f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f)));
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
            s.introStage = IntroStage.Frame;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = 1f;
            s.introWeights.shatter = 1f;
            l.Tick(Dt, s, 0.9f, out _);
            Assert.AreEqual(0, l.Dropped);
        }
    }
}
