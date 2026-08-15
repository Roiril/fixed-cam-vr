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
                                            float shatter = 0f, float live = 0f)
        {
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = stage;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = shell;
            s.introWeights.shatter = shatter;
            s.introWeights.live = live;
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
        public void RetiredCues_NeverFire()
        {
            // ⚠⚠ 2026-08-15 に段を戻し、`SealClose`（隔離が閉じる）と `Swap`（装置が点く）は
            //    鳴らさなくなった（`canon/LEDGER.md` 0044）。
            //    - 隔離が閉じる段が無くなった
            //    - 「スクリーンが出る瞬間」はユーザー指定の音源を持つ `ScreenOn` が取る
            //    音源は残してあるので、経路が復活すると**黙って 2 本重なる**。ここで止める。
            var l = new SoundCueLogic();
            foreach (IntroStage st in System.Enum.GetValues(typeof(IntroStage)))
            {
                for (int i = 0; i < 8; i++)
                {
                    var s = Intro(st, shell: 1f, shatter: 1f, live: 1f);
                    Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, s), $"段 {st} で SealClose が鳴った");
                    Assert.AreEqual(0, CountOf(l, SoundCue.Swap, Dt, s), $"段 {st} で Swap が鳴った");
                }
            }
        }

        [Test]
        public void Shatter_FiresOnce_WhenTheRealityStartsToBreak()
        {
            // 段 4 — 現実が割れてスクリーンへ吸い込まれ始める。**導入の山。**
            var l = new SoundCueLogic();
            Assert.AreEqual(0, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame)));
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 0.1f)));
            for (int i = 0; i < 60; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 0.9f)),
                                "破砕の音が鳴り続けている");
        }

        [Test]
        public void ScreenOn_FiresOnceWhenTheVideoFillsTheFrame()
        {
            // ⚠ 2026-08-15 に段 4 へ移した。**割れた先がカメラ映像**になったので、
            //    「スクリーンを出すときの音」もその瞬間へ来る（`canon/LEDGER.md` 0045）。
            var l = new SoundCueLogic();
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt,
                                       Intro(IntroStage.Frame, shatter: 0.1f, live: 0.1f)),
                            "割れ始めた瞬間に鳴っている（破砕の音と潰れる）");
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenOn, Dt,
                                       Intro(IntroStage.Frame, shatter: 0.3f,
                                             live: SoundCueLogic.ScreenOnAt)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt,
                                       Intro(IntroStage.Frame, shatter: 0.9f, live: 1f)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Swap, live: 1f)));
        }

        [Test]
        public void Shatter_AndScreenOn_NeverFireInTheSameFrame()
        {
            // 段 4 の頭は `shatter` も `live` も動き出す。**重ねると 1 つの音に潰れる**ので、
            // 映像の満ち具合（ScreenOnAt）で 1 発目から遅らせてある。
            var l = new SoundCueLogic();
            var head = Intro(IntroStage.Frame, shatter: SoundCueLogic.ShatterFireAt, live: 0f);
            var fired = l.Tick(Dt, head, 0f, out int n);
            int sh = 0, on = 0;
            for (int i = 0; i < n; i++)
            {
                if (fired[i] == SoundCue.Shatter) sh++;
                if (fired[i] == SoundCue.ScreenOn) on++;
            }
            Assert.AreEqual(1, sh, "段 4 の頭で割れる音が鳴っていない");
            Assert.AreEqual(0, on, "割れる音と同じフレームでスクリーンの音も鳴っている");
        }

        [Test]
        public void ScreenOn_HasASourceAndDucksLikeAPeak()
        {
            // ⚠ **音源の無い節目を作らない**（rules/sound-design.md §8）。
            Assert.AreEqual("sfx_screen_on", SoundCueLogic.ResourceName(SoundCue.ScreenOn));
            // 導入の山と同じだけ劇伴を退かせる。
            Assert.That(SoundCueLogic.DuckFor(SoundCue.ScreenOn),
                        Is.GreaterThanOrEqualTo(SoundCueLogic.DuckFor(SoundCue.Shatter)));
        }

        [Test]
        public void ScreenNoise_FollowsScreenOn_AtTheHeadOfTheSwap()
        {
            // ユーザー指示（canon/LEDGER.md 0030）「これを最初に出して、その後ノイズを出す」。
            var l = new SoundCueLogic();
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenNoise, Dt,
                                       Intro(IntroStage.Frame, shatter: 1f, live: 1f)),
                            "段 4 で先に鳴っている");
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenNoise, Dt, Intro(IntroStage.Swap, live: 1f)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenNoise, Dt, Intro(IntroStage.Swap, live: 1f)));
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
            var s = Intro(IntroStage.Degrade);
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
            CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f));
            CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f));
            l.ResetRun();
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f)));
        }

        [Test]
        public void SecondVisitor_HearsEverything_WithoutAnyoneCallingResetRun()
        {
            // ⚠⚠ **2026-08-14 に見つけた穴。** ラッチを落とす経路は `ResetRun()` だけで、
            //    **その呼び出し元がどこにも無かった** ＝ アプリを起動してから 1 人目だけ導入の音が鳴り、
            //    2 人目以降は隔離も管の点灯も鈴も終幕も無音だった。展示は 1 日に数十人が続けて
            //    体験するので、**ほぼ全員が無音の側に当たる**。しかも音は録画に映らないので
            //    走行の証拠からは気づけない。
            //
            //    ⚠ 上の `ResetRun_ClearsEveryLatch_...` は**呼ぶ前提を自分で作っていた**ので
            //    この穴を捕まえられなかった。ここでは**誰も呼ばない**まま段 0 へ戻す。
            var l = new SoundCueLogic();
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f)));
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f)));

            // 次の体験者。段 0（開始待ち）へ戻るだけで、リセットの号令は 1 つも来ない。
            Assert.AreEqual(0, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Black)));

            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f)),
                            "2 人目に破砕の音が鳴らない（導入の山）");
            Assert.AreEqual(1, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f)),
                            "2 人目にスクリーンが出る音が鳴らない");
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
            s.introStage = IntroStage.Swap;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.shell = 1f;
            s.introWeights.shatter = 1f;
            s.introWeights.live = 1f;
            l.Tick(Dt, s, 0.9f, out _);
            Assert.AreEqual(0, l.Dropped);
        }
    }
}
