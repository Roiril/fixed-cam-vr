#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 上司からの連絡が<b>いつ・どれで</b>出るか（`canon/LEDGER.md` 0054 のユーザー指定 3 点）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 2 つ。どちらも**画を見ても気づけない**:
    /// ① 締めで報告したのに「異常は検出されませんでした」が返る（意味が真逆）
    /// ② 2 人目の体験者に①③が出ない（ラン単位の状態を落とし忘れる）
    /// </summary>
    public sealed class CommsCueLogicTests
    {
        private const float Dt = 1f / 72f;

        private static CommsCueInput Run(bool waiting = false, bool mark = false, bool hadTake = false) =>
            new CommsCueInput
            {
                inRun = true,
                waitingForMark = waiting,
                markPressed = mark,
                markHadTake = hadTake,
                dt = Dt,
            };

        /// <summary>本編に居るまま <paramref name="sec"/> 秒進め、その間に出た連絡を全部返す。</summary>
        private static System.Collections.Generic.List<CommsNotice> Advance(
            CommsCueLogic l, float sec, bool waiting = false)
        {
            var seen = new System.Collections.Generic.List<CommsNotice>();
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                CommsNotice v = l.Tick(Run(waiting));
                if (v != CommsNotice.None) seen.Add(v);
            }
            return seen;
        }

        // ------------------------------------------------------------------ ① 導入が明けた直後

        [Test]
        public void EnteringTheMainRun_DeliversTheOpeningNotice_Once()
        {
            var l = new CommsCueLogic();
            var seen = Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, seen);

            // そのあといくら経っても二度は来ない。
            CollectionAssert.IsEmpty(Advance(l, 60f));
        }

        [Test]
        public void TheOpeningNotice_WaitsForTheIntroToSettle()
        {
            // ⚠ 0 秒で出すと、導入の締め（枠の中に自分が居る）と文字が重なってどちらも流れる。
            var l = new CommsCueLogic();
            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.BeginDelaySec - 0.2f));
        }

        [Test]
        public void NothingIsDelivered_OutsideTheMainRun()
        {
            // 導入・終幕・中止では出さない（相が Run でない間は 1 通も）。
            var l = new CommsCueLogic();
            for (int i = 0; i < 500; i++)
            {
                var inp = new CommsCueInput { inRun = false, dt = Dt, markPressed = i == 100 };
                Assert.AreEqual(CommsNotice.None, l.Tick(inp), $"i={i} で連絡が出た");
            }
        }

        // ------------------------------------------------------------------ ② 報告した瞬間

        [Test]
        public void ReportingWhileSomethingIsPlaying_SaysItWasLogged()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Assert.AreEqual(CommsNotice.MarkLogged, l.Tick(Run(mark: true, hadTake: true)));
        }

        [Test]
        public void ReportingWithNothingPlaying_SaysNothingWasDetected()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Assert.AreEqual(CommsNotice.MarkNothing, l.Tick(Run(mark: true, hadTake: false)));
        }

        [Test]
        public void EveryReportGetsAnAnswer_NotJustTheFirst()
        {
            // ②は押すたび（①③と違ってラン 1 回の縛りが無い）。返らないと装置が壊れて見える。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            for (int i = 0; i < 5; i++)
                Assert.AreEqual(CommsNotice.MarkNothing, l.Tick(Run(mark: true)), $"{i + 1} 回目");
        }

        [Test]
        public void ReportingAtTheClosingCut_IsAnsweredAsLogged_NotAsNothing()
        {
            // ⚠⚠ **この実装で唯一の危ない所。** 締めのカットは報告でその場で畳まれるので、
            //     「いま演出が走っているか」を後から見ると必ず false になり、意味が真逆の連絡が返る。
            //     ここは押した瞬間の値（markHadTake）を使っていることの固定。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 1f, waiting: true);

            // 締めのカットが待っている ＝ 演出は走っている。押した瞬間の値が渡ってくる。
            Assert.AreEqual(CommsNotice.MarkLogged,
                            l.Tick(Run(waiting: true, mark: true, hadTake: true)));
        }

        [Test]
        public void AReport_OutranksTheOpeningNotice_AndTheOpeningNeverArrivesLate()
        {
            // 同じフレームに 2 つ揃ったら報告が勝つ。⚠ 押しのけた方を**次のフレームへ持ち越さない**
            //    （持ち越すと「報告したのに関係ない連絡が来た」になる）。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec - 0.05f);
            for (int i = 0; i < 10; i++)
            {
                CommsNotice v = l.Tick(Run(mark: true));
                Assert.AreEqual(CommsNotice.MarkNothing, v, $"i={i}");
            }
            CollectionAssert.IsEmpty(Advance(l, 30f), "開始の連絡が遅れて出てきた");
        }

        // ------------------------------------------------------------------ ③ 押さないまま 3 秒

        [Test]
        public void NotReportingAtTheClosingCut_PromptsAfterThreeSeconds()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);

            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.PromptAfterWaitSec - 0.2f, waiting: true),
                                     "3 秒より前に催促した");
            CollectionAssert.AreEqual(new[] { CommsNotice.Prompt }, Advance(l, 0.4f, waiting: true));
            CollectionAssert.IsEmpty(Advance(l, 30f, waiting: true), "催促が二度来た");
        }

        [Test]
        public void ThePrompt_NeedsAnUninterruptedWait()
        {
            // 待ちが途切れたら数え直す（別のカットが挟まった / 一度畳まれた）。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, CommsCueLogic.PromptAfterWaitSec - 0.3f, waiting: true);   // あと 0.3 秒で催促
            Advance(l, 0.5f, waiting: false);                                     // そこで途切れた

            // 再開後は 0 から数え直す ＝ 同じ 2.7 秒ではまだ出ない。
            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.PromptAfterWaitSec - 0.3f, waiting: true),
                                     "途切れる前の待ちを持ち越している");
            // そこからさらに 0.4 秒で 3 秒を越えて、初めて出る。
            CollectionAssert.AreEqual(new[] { CommsNotice.Prompt }, Advance(l, 0.4f, waiting: true));
        }

        [Test]
        public void ThePrompt_NeverFires_WhenNobodyIsWaiting()
        {
            // 締めのカットが無い体験（著作が変わった / 4 周目まで来なかった）では 1 通も出さない。
            var l = new CommsCueLogic();
            var seen = Advance(l, 120f);
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, seen);
        }

        // ------------------------------------------------------------------ 体験 1 回ぶんの状態

        [Test]
        public void TheSecondVisitor_GetsEverythingAgain_AfterLeavingTheRun()
        {
            // 本編を出た縁（ラン開始・中止・終幕）で落ちる。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, CommsCueLogic.PromptAfterWaitSec + 0.4f, waiting: true);

            l.Tick(new CommsCueInput { inRun = false, dt = Dt });

            CollectionAssert.AreEqual(new[] { CommsNotice.Begin },
                                      Advance(l, CommsCueLogic.BeginDelaySec + 0.2f));
            CollectionAssert.AreEqual(new[] { CommsNotice.Prompt },
                                      Advance(l, CommsCueLogic.PromptAfterWaitSec + 0.4f, waiting: true));
        }

        [Test]
        public void TheSecondVisitor_GetsEverythingAgain_WhenOnlyResetRunIsCalled()
        {
            // ⚠ 導入を持たない設定では相が Run のまま次のランが始まりうるので、
            //    号令（ShowRunDirector.RunRestarted）からも落ちること。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);

            l.ResetRun();

            CollectionAssert.AreEqual(new[] { CommsNotice.Begin },
                                      Advance(l, CommsCueLogic.BeginDelaySec + 0.2f));
        }
    }
}
