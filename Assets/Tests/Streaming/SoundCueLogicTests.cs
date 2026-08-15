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
        public void Creak_NeverFires_Anywhere()
        {
            // ⚠⚠ 2026-08-15 に全廃した（`canon/LEDGER.md` 0049・ユーザー逐語
            //    「導入の家鳴りは無くす」「それ以降も、家鳴りは無くしてほしい」）。
            //    音源も enum も残してあるので、経路が復活すると**黙って鳴り出す**。ここで止める。
            var l = new SoundCueLogic();

            var run = SoundShowState.Idle;
            run.phase = ShowPhase.Run;
            for (int i = 0; i < 60 * 300; i++)          // 本編を 5 分
                Assert.AreEqual(0, CountOf(l, SoundCue.Creak, Dt, run), "本編で家鳴りが鳴った");

            foreach (IntroStage st in System.Enum.GetValues(typeof(IntroStage)))
            {
                var s = Intro(st);
                for (int i = 0; i < 60 * 60; i++)       // 各段を 1 分
                    Assert.AreEqual(0, CountOf(l, SoundCue.Creak, Dt, s), $"段 {st} で家鳴りが鳴った");
            }
        }

        [Test]
        public void Bell_RingsOnce_AtTheStartOfStructure()
        {
            // 段 3（輪郭だけの世界）の頭で 1 回だけ（`canon/LEDGER.md` 0049）。
            var l = new SoundCueLogic();

            // 段 0〜2 では鳴らない。
            foreach (IntroStage st in new[] { IntroStage.Black, IntroStage.Real, IntroStage.Degrade })
            {
                var q = Intro(st);
                for (int i = 0; i < 60 * 30; i++)
                    Assert.AreEqual(0, CountOf(l, SoundCue.Bell, Dt, q), $"段 {st} で鈴が鳴った");
            }

            var s = Intro(IntroStage.Structure);
            Assert.AreEqual(1, CountOf(l, SoundCue.Bell, Dt, s), "段 3 の頭で鳴らない");
            int extra = 0;
            for (int i = 0; i < 60 * 30; i++) extra += CountOf(l, SoundCue.Bell, Dt, s);
            Assert.AreEqual(0, extra, "鈴は 1 回だけ");

            // ⚠ 段 3 は実尺 1.1 秒しかない。**待つ形にすると 1 度も鳴らない。**
            var l2 = new SoundCueLogic();
            var short3 = Intro(IntroStage.Structure);
            int inShortStage = 0;
            for (int i = 0; i < (int)(1.1f / Dt); i++) inShortStage += CountOf(l2, SoundCue.Bell, Dt, short3);
            Assert.AreEqual(1, inShortStage, "段 3 の実尺 1.1 秒の中で鳴っていない");
        }

        [Test]
        public void Bell_RingsAgain_ForTheNextVisitor()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(1, CountOf(l, SoundCue.Bell, Dt, Intro(IntroStage.Structure)));

            // 段 0 へ入った縁でラッチが落ちる（号令を配る側の実装に依存しない）。
            var black = Intro(IntroStage.Black);
            for (int i = 0; i < 4; i++) CountOf(l, SoundCue.Bell, Dt, black);
            Assert.AreEqual(1, CountOf(l, SoundCue.Bell, Dt, Intro(IntroStage.Structure)),
                            "次の体験者に鈴が鳴らない");
        }

        [Test]
        public void Bell_IsSilentDuringRegistration()
        {
            // 位置合わせはスタッフの作業。世界の音を割り込ませない。
            var l = new SoundCueLogic();
            var s = Intro(IntroStage.Structure);
            s.registrationActive = true;
            for (int i = 0; i < 60 * 30; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Bell, Dt, s));
        }

        [Test]
        public void Cues_AreDeterministic_AcrossRuns()
        {
            // **走行のたびに違う音が出ると、何が効いたのか分からなくなる。**
            // ⚠ 乱数を持っていた家鳴りは廃止したので、いまは導入の並びで確かめる。
            var first = new System.Collections.Generic.List<string>();
            var second = new System.Collections.Generic.List<string>();
            foreach (var list in new[] { first, second })
            {
                var l = new SoundCueLogic();
                foreach (IntroStage st in new[] { IntroStage.Black, IntroStage.Real, IntroStage.Degrade,
                                                  IntroStage.Structure, IntroStage.Frame, IntroStage.Swap })
                {
                    var s = Intro(st, shatter: st == IntroStage.Frame ? 1f : 0f,
                                  live: st == IntroStage.Frame ? 1f : 0f);
                    for (int i = 0; i < 30; i++)
                    {
                        var fired = l.Tick(Dt, s, 0f, out int n);
                        for (int k = 0; k < n; k++) list.Add($"{st}:{fired[k]}");
                    }
                }
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
        public void Outro_AddsNoSoundAtAll()
        {
            // ⚠ **終幕に足す音は 1 本も無い**（canon/LEDGER.md 0048）。装置が引いた後に残るのは
            //    部屋の音だけで、それも Done で無音へ落ちる（`rules/sound-design.md`
            //    「終わりに音を残さない」）。ここに一撃を戻すなら、それは世界観の判定が要る。
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.outroActive = true;
            foreach (OutroStage st in System.Enum.GetValues(typeof(OutroStage)))
            {
                s.outroStage = st;
                s.outroProgress01 = 0.5f;
                for (int i = 0; i < 8; i++)
                {
                    l.Tick(Dt, s, 0f, out int n);
                    Assert.AreEqual(0, n, $"終幕 {st} で音が鳴った");
                }
            }
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
