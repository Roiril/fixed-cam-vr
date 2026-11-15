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
                                            float shatter = 0f, float live = 0f, bool tv = false)
        {
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = stage;
            s.introWeights = IntroWeights.Inactive;
            s.introWeights.tv = tv;
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
            //    - 2026-09-25 以降は生成動画の1本に帰還音も含め、ScreenOn を重ねない。
            //    音源は残してあるので、経路が復活すると2本重なる。ここで止める。
            var l = new SoundCueLogic();
            foreach (IntroStage st in System.Enum.GetValues(typeof(IntroStage)))
            {
                for (int i = 0; i < 8; i++)
                {
                    var s = Intro(st, shell: 1f, shatter: 1f, live: 1f);
                    Assert.AreEqual(0, CountOf(l, SoundCue.SealClose, Dt, s), $"段 {st} で SealClose が鳴った");
                    Assert.AreEqual(0, CountOf(l, SoundCue.Swap, Dt, s), $"段 {st} で Swap が鳴った");
                    Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, s), $"段 {st} で ScreenOn が鳴った");
                    // ⚠ 2026-08-16 追加（`canon/LEDGER.md` 0057「ノイズは鳴らさない」）。
                    Assert.AreEqual(0, CountOf(l, SoundCue.ScreenNoise, Dt, s),
                                    $"段 {st} で ScreenNoise が鳴った");
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
        public void ScreenOn_RetainsItsSourceWithoutFiring()
        {
            // 旧音源は保持し、入口での発火だけ止める。
            Assert.AreEqual("sfx_screen_on", SoundCueLogic.ResourceName(SoundCue.ScreenOn));
        }

        [Test]
        public void ScreenNoise_KeepsItsSourceEvenThoughItNeverFires()
        {
            // ⚠ 2026-08-16 に鳴らさなくなった（`canon/LEDGER.md` 0057）。**戻すときのために
            //    音源と退き量は残す** — 発火しないことは `RetiredCues_NeverFire` が固定する。
            Assert.AreEqual("sfx_screen_noise", SoundCueLogic.ResourceName(SoundCue.ScreenNoise));
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

        /// <summary>段 5 に入ってから <paramref name="sec"/> 秒ぶん進めて、鳴った鈴を数える。</summary>
        private static int RingsWithin(SoundCueLogic l, float sec, bool registering = false)
        {
            var s = Intro(IntroStage.Swap, live: 1f);
            s.registrationActive = registering;
            int c = 0;
            for (int i = 0; i < (int)(sec / Dt); i++) c += CountOf(l, SoundCue.Bell, Dt, s);
            return c;
        }

        [Test]
        public void Bell_RingsOnce_WhenTheScreenHasFullyTakenOver()
        {
            // ⚠ 2026-08-16 に段 3 の頭からここへ移した（`canon/LEDGER.md` 0057・
            //    「鈴は、完全にスクリーンになったときになるようにしてほしい」）。
            var l = new SoundCueLogic();

            // 段 0〜4 では鳴らない（段 4 は割れている最中で、まだスクリーンになっていない）。
            foreach (IntroStage st in new[] { IntroStage.Black, IntroStage.Real,
                                              IntroStage.Degrade, IntroStage.Structure,
                                              IntroStage.Frame })
            {
                var q = Intro(st, shatter: 1f, live: 1f);
                for (int i = 0; i < 60 * 30; i++)
                    Assert.AreEqual(0, CountOf(l, SoundCue.Bell, Dt, q), $"段 {st} で鈴が鳴った");
            }

            // 段 5 の頭ではまだ鳴らない（最後の破片が消えていく最中）。
            Assert.AreEqual(0, RingsWithin(l, SoundCueLogic.BellAfterSwapSec - 0.1f),
                            "すり替えの最中に鳴っている");
            // クロスフェードが終わったら 1 回だけ。
            Assert.AreEqual(1, RingsWithin(l, 0.2f), "完全にスクリーンになった所で鳴らない");
            Assert.AreEqual(0, RingsWithin(l, 30f), "鈴は 1 回だけ");
        }

        [Test]
        public void Bell_WaitsUntilTheGeneratedGlassSoundHasFinished()
        {
            // 破砕開始0.135秒 + 6.016秒は、段4の5秒 + 段5の1.2秒より前。
            Assert.That(SoundCueLogic.BellAfterSwapSec, Is.GreaterThanOrEqualTo(1.16f),
                        "鈴が生成音の尾に重なる");
            Assert.AreEqual(IntroLogic.SwapCrossfadeSec, SoundCueLogic.BellAfterSwapSec,
                            "すり替えのクロスフェードと同じ尺にしておく");
        }

        [Test]
        public void Bell_RingsAgain_ForTheNextVisitor()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(1, RingsWithin(l, SoundCueLogic.BellAfterSwapSec + 0.2f));

            // 段 0 へ入った縁でラッチが落ちる（号令を配る側の実装に依存しない）。
            var black = Intro(IntroStage.Black);
            for (int i = 0; i < 4; i++) CountOf(l, SoundCue.Bell, Dt, black);
            Assert.AreEqual(1, RingsWithin(l, SoundCueLogic.BellAfterSwapSec + 0.2f),
                            "次の体験者に鈴が鳴らない");
        }

        [Test]
        public void Bell_IsSilentDuringRegistration()
        {
            // 位置合わせはスタッフの作業。世界の音を割り込ませない。
            var l = new SoundCueLogic();
            Assert.AreEqual(0, RingsWithin(l, 30f, registering: true));
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
        public void ResetRun_ClearsTheShatterLatch_ForTheNextVisitor()
        {
            var l = new SoundCueLogic();
            CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f));
            l.ResetRun();
            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f)));
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f)));
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
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f)));

            // 次の体験者。段 0（開始待ち）へ戻るだけで、リセットの号令は 1 つも来ない。
            Assert.AreEqual(0, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Black)));

            Assert.AreEqual(1, CountOf(l, SoundCue.Shatter, Dt, Intro(IntroStage.Frame, shatter: 1f)),
                            "2 人目に破砕の音が鳴らない（導入の山）");
            Assert.AreEqual(0, CountOf(l, SoundCue.ScreenOn, Dt, Intro(IntroStage.Frame, live: 1f)),
                            "2 人目に旧スクリーン音が重なった");
        }

        [Test]
        public void Outro_FiresOnlyThePowerOff_AndOnlyOnce()
        {
            // ⚠⚠ **終幕に鳴るのは電源断 1 本だけ**（2026-08-23・canon/LEDGER.md 0125）。
            //    0048 の「終幕に足す音は 1 本も無い」はユーザー指定で覆った。
            //    装置が引いた後に残るのは部屋の音だけ、という設計は変わっていないので、
            //    **2 本目を足すならそれは世界観の判定が要る**（ここが止める）。
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.outroActive = true;
            s.outroStage = OutroStage.Collapse;
            s.outroElapsedSec = 0f;

            var fired = l.Tick(Dt, s, 0f, out int n);
            Assert.AreEqual(1, n, "終幕の頭で鳴ったのが 1 本ではない");
            Assert.AreEqual(SoundCue.PowerOff, fired[0]);

            foreach (OutroStage st in System.Enum.GetValues(typeof(OutroStage)))
            {
                s.outroStage = st;
                s.outroElapsedSec = 0.5f;
                for (int i = 0; i < 8; i++)
                {
                    l.Tick(Dt, s, 0f, out int m);
                    Assert.AreEqual(0, m, $"終幕 {st} で音が鳴り続けている");
                }
            }
        }

        [Test]
        public void TrappedOutro_FiresTheFallInsteadOfPowerOff_OnlyOnce()
        {
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.outroActive = true;
            s.outroTrapped = true;
            s.outroStage = OutroStage.Dark;

            var fired = l.Tick(Dt, s, 0f, out int n);
            Assert.AreEqual(1, n);
            Assert.AreEqual(SoundCue.DollEndFall, fired[0]);
            Assert.AreEqual("sfx_doll_end_fall", SoundCueLogic.ResourceName(SoundCue.DollEndFall));

            s.outroElapsedSec = 1f;
            l.Tick(Dt, s, 0f, out int again);
            Assert.AreEqual(0, again, "人形 End の音が 2 度鳴っている");
        }

        [Test]
        public void PowerOff_WaitsForTheTubeToStartCollapsing()
        {
            // 2026-10-21 から、終幕の頭は砂嵐（0.95 秒）で、管が潰れるのはその後。
            // 電源断の音は潰れ始める所（outroPowerCutSec）まで待つ。
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.outroActive = true;
            s.outroStage = OutroStage.Noise;
            s.outroPowerCutSec = 0.95f;
            s.outroElapsedSec = 0.5f;
            Assert.AreEqual(0, CountOf(l, SoundCue.PowerOff, Dt, s), "砂嵐のあいだに電源断が鳴った");
            s.outroStage = OutroStage.Collapse;
            s.outroElapsedSec = 0.96f;
            Assert.AreEqual(1, CountOf(l, SoundCue.PowerOff, Dt, s), "潰れ始めで鳴らない");
            s.outroElapsedSec = 1.1f;
            Assert.AreEqual(0, CountOf(l, SoundCue.PowerOff, Dt, s), "2 度鳴った");
        }

        [Test]
        public void PowerOff_RearmsForTheNextVisitor()
        {
            // ⚠⚠ **導入のラッチ（`ResetIntroLatches`）に相乗りさせない。** あれが落ちるのは
            //    導入の段 0 なので、そこへ乗せると「終幕 → 次の人の導入」の順でしか戻らない。
            //    ここは**終幕を抜けたフレーム**で自分から再武装する。
            //    （2026-08-14 に導入の音で「1 人目だけ鳴る」を踏んだのと同じ形。）
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            s.outroActive = true;
            s.outroStage = OutroStage.Collapse;
            Assert.AreEqual(1, CountOf(l, SoundCue.PowerOff, Dt, s));

            s.outroActive = false;
            s.outroStage = OutroStage.Off;
            for (int i = 0; i < 8; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.PowerOff, Dt, s), "終幕の外で鳴った");

            s.outroActive = true;
            s.outroStage = OutroStage.Collapse;
            Assert.AreEqual(1, CountOf(l, SoundCue.PowerOff, Dt, s), "2 人目の終幕が無音");
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

        // ---------------------------------------------------------------- 0388: 音と画の頭を揃える

        /// <summary>段 4（テレビ方式）に入ってから鈴が鳴った秒を返す（鳴らなければ -1）。</summary>
        private static float TvBellAt(SoundCueLogic l, float frameSec, bool registering = false)
        {
            var s = Intro(IntroStage.Frame, tv: true);
            s.registrationActive = registering;
            for (int i = 0; i * Dt <= frameSec; i++)
                if (CountOf(l, SoundCue.Bell, Dt, s) > 0) return i * Dt;
            return -1f;
        }

        [Test]
        public void TvBell_PeaksWhenThePictureHasFullyComeIn()
        {
            // 鈴の山 ＝ 予約の遅れ ＋ 音源の山。それが映像が出切る秒（段 4 の頭から）に来る。
            Assert.AreEqual(IntroLogic.TvPowerOnEndSec,
                            SoundCueLogic.BellAfterTvOnSec + IntroLogic.TvOnLeadSec + SoundCueLogic.BellPeakSec,
                            1e-4f);
            var l = new SoundCueLogic();
            float at = TvBellAt(l, 3f);
            Assert.AreEqual(SoundCueLogic.BellAfterTvOnSec, at, Dt + 1e-4f,
                            "テレビ方式の鈴が映像の出切りに合っていない");

            // 段 5 ではもう鳴らない（1 回だけ）。
            var swap = Intro(IntroStage.Swap, live: 1f, tv: true);
            for (int i = 0; i < 60 * 10; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Bell, Dt, swap), "鈴が 2 回鳴った");
        }

        [Test]
        public void TvBell_FitsInsideTheShortestFrameStage()
        {
            // 段 4 は TvPowerOnEndSec を割らない（IntroLogic.FrameMinSec）ので、段 4 の中で必ず鳴る。
            Assert.Less(SoundCueLogic.BellAfterTvOnSec, IntroLogic.TvPowerOnEndSec);
            Assert.Greater(SoundCueLogic.BellAfterTvOnSec, IntroLogic.TvLiveFromSec,
                           "映像が出始める前に鈴が鳴る");
        }

        [Test]
        public void TvBell_RingsAtTheSwapEdge_WhenTheFrameStageWasCutShort()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(-1f, TvBellAt(l, 0.3f));
            var swap = Intro(IntroStage.Swap, live: 1f, tv: true);
            Assert.AreEqual(1, CountOf(l, SoundCue.Bell, Dt, swap), "段 4 を送ったら鈴が鳴らない");
        }

        [Test]
        public void TvBell_IsSilentDuringRegistration_AndRingsForTheNextVisitor()
        {
            var l = new SoundCueLogic();
            Assert.AreEqual(-1f, TvBellAt(l, 3f, registering: true));
            var black = Intro(IntroStage.Black, tv: true);
            for (int i = 0; i < 4; i++) CountOf(l, SoundCue.Bell, Dt, black);
            Assert.Greater(TvBellAt(l, 3f), 0f, "次の体験者に鈴が鳴らない");
        }

        [Test]
        public void ShatterBell_StillWaitsForTheCrossfade()
        {
            // 割れる遷移（tv=false）は段 4 では鳴らさない。
            var l = new SoundCueLogic();
            var frame = Intro(IntroStage.Frame, shatter: 1f, live: 1f);
            for (int i = 0; i < 60 * 6; i++)
                Assert.AreEqual(0, CountOf(l, SoundCue.Bell, Dt, frame));
            Assert.AreEqual(0, RingsWithin(l, SoundCueLogic.BellAfterSwapSec - 0.1f));
            Assert.AreEqual(1, RingsWithin(l, 0.2f));
        }

        [Test]
        public void BellPeakSec_MatchesTheBakedBell()
        {
            string path = System.IO.Path.Combine(UnityEngine.Application.dataPath,
                                                 "Resources", "Sound", "amb_bell.wav");
            Assert.IsTrue(System.IO.File.Exists(path), path);
            float peak = WavPeakSec(System.IO.File.ReadAllBytes(path), 0.010f);
            Assert.AreEqual(SoundCueLogic.BellPeakSec, peak, 0.03f,
                            "amb_bell の山の秒が変わった。BellPeakSec を測り直す");
        }

        /// <summary>RIFF/WAVE（PCM 16/24・float 32）の 10ms 実効値が最大になる窓の頭の秒。</summary>
        private static float WavPeakSec(byte[] b, float win)
        {
            int pos = 12, fmt = 0, ch = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = System.BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    fmt = System.BitConverter.ToUInt16(b, pos + 8);
                    ch = System.BitConverter.ToInt16(b, pos + 10);
                    rate = System.BitConverter.ToInt32(b, pos + 12);
                    bits = System.BitConverter.ToInt16(b, pos + 22);
                    if (fmt == 0xFFFE) fmt = System.BitConverter.ToUInt16(b, pos + 32);
                }
                else if (id == "data")
                {
                    dataAt = pos + 8;
                    dataLen = System.Math.Min(len, b.Length - dataAt);
                    break;
                }
                pos += 8 + len + (len & 1);
            }
            Assert.Greater(dataAt, 0, "data が無い");
            int bps = bits / 8, frames = dataLen / (bps * ch), hop = (int)(rate * win);
            double best = -1;
            int bestI = 0;
            for (int w = 0; (w + 1) * hop <= frames; w++)
            {
                double acc = 0;
                for (int f = w * hop; f < (w + 1) * hop; f++)
                {
                    double m = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int o = dataAt + (f * ch + c) * bps;
                        double v = fmt == 3 ? System.BitConverter.ToSingle(b, o)
                                 : bps == 2 ? System.BitConverter.ToInt16(b, o) / 32768.0
                                 : ((b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)) << 8 >> 8) / 8388608.0;
                        m += v;
                    }
                    m /= ch;
                    acc += m * m;
                }
                if (acc > best) { best = acc; bestI = w; }
            }
            return bestI * win;
        }

        [Test]
        public void GlitchOnset_FiresOnTheLineThatPulses_AndTheThresholdPathDoesNotDoubleIt()
        {
            var l = new SoundCueLogic();
            var s = SoundShowState.Idle;
            Assert.IsTrue(l.TryFireGlitchOnset(0.5f, 0f), "画の乱れの頭で鳴らない");
            // 同じ乱れが閾値を越えても 2 発目は鳴らない。
            Assert.AreEqual(0, CountOf(l, SoundCue.Glitch, Dt, s, glitch: 0.5f));
            // 最短間隔の内は鳴らない。
            Assert.IsFalse(l.TryFireGlitchOnset(0.5f, 0f));
            for (int i = 0; i < 40; i++) l.Tick(Dt, s, 0f, out _);
            Assert.IsTrue(l.TryFireGlitchOnset(0.5f, 0f));
        }

        [Test]
        public void GlitchOnset_KeepsTheThresholdRules()
        {
            var l = new SoundCueLogic();
            // 閾値に届かない弱い乱れは鳴らさない（閾値の経路と同じ）。
            Assert.IsFalse(l.TryFireGlitchOnset(SoundCueLogic.GlitchFireAt - 0.01f, 0f));
            Assert.IsTrue(l.TryFireGlitchOnset(0.9f, 0f));
            for (int i = 0; i < 40; i++) l.Tick(Dt, SoundShowState.Idle, 0.9f, out _);
            // 強い乱れが続いている間は再武装しない。
            Assert.IsFalse(l.TryFireGlitchOnset(0.9f, 0.9f), "強い乱れが続く間に連射している");
            Assert.IsTrue(l.TryFireGlitchOnset(0.9f, 0f));
        }
    }
}
