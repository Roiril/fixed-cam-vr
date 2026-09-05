#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// AIエージェントからの連絡が<b>いつ・どれで</b>出るか（`canon/LEDGER.md` 0054 のユーザー指定 3 点）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 2 つ。どちらも**画を見ても気づけない**:
    /// ① 締めで報告したのに「異常は検出されませんでした」が返る（意味が真逆）
    /// ② 2 人目の体験者に①③が出ない（ラン単位の状態を落とし忘れる）
    /// </summary>
    public sealed class CommsCueLogicTests
    {
        private const float Dt = 1f / 72f;

        /// <summary>
        /// 本編 1 フレーム分。
        /// ⚠ <paramref name="read"/>（前の連絡を読ませ終わったか）は<b>既定 false</b> —
        /// 既存のテストは①だけを見たいので、①b が続かない状態を既定にしてある。
        /// </summary>
        private static CommsCueInput Run(bool waiting = false, bool mark = false,
                                         bool resolved = false, bool read = false) =>
            new CommsCueInput
            {
                inRun = true,
                waitingForMark = waiting,
                markPressed = mark,
                markResolved = resolved,
                panelDoneReading = read,
                dt = Dt,
            };

        /// <summary>本編に居るまま <paramref name="sec"/> 秒進め、その間に出た連絡を全部返す。</summary>
        private static System.Collections.Generic.List<CommsNotice> Advance(
            CommsCueLogic l, float sec, bool waiting = false, bool read = false)
        {
            var seen = new System.Collections.Generic.List<CommsNotice>();
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                CommsNotice v = l.Tick(Run(waiting, read: read));
                if (v != CommsNotice.None) seen.Add(v);
            }
            return seen;
        }

        /// <summary>
        /// ⚠⚠ <b>①と①b は時間で分ける</b>（2026-08-19・<c>canon/LEDGER.md</c> 0097・ユーザー指定
        /// 「調査を開始してください→異変をみつけたら〜と、表示は時間的に分けて。その間を切り詰める」）。
        /// ①b は<b>①を読ませ終わった縁</b>で、間を置かずに続く（同じ面のまま文面だけ替わる）。
        /// </summary>
        [Test]
        public void TheOpeningNotice_IsFollowedByTheHowTo_AsSoonAsItIsRead()
        {
            var l = new CommsCueLogic();
            // ①が出て、読ませているあいだ（read:false）は次が来ない。
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin },
                                      Advance(l, CommsCueLogic.BeginDelaySec + 0.2f));
            CollectionAssert.IsEmpty(Advance(l, 30f), "読ませている最中に①b が割り込んでいる");

            // 読ませ終わったら、次のフレームで①b。
            CollectionAssert.AreEqual(new[] { CommsNotice.BeginHow }, Advance(l, 0.1f, read: true));
            Assert.IsTrue(l.BeginHowFired);
            // ラン 1 回に 1 度だけ。
            CollectionAssert.IsEmpty(Advance(l, 60f, read: true));
        }

        /// <summary>
        /// ⚠ <b>①b は押しのけられても消える権利が無い</b>（①③と違う）。押し方の説明なので、
        /// ②や③に割り込まれた回では<b>その連絡を読ませ終わってから</b>改めて出す。
        /// </summary>
        [Test]
        public void TheHowTo_SurvivesBeingPushedAsideByAReport()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);          // ①
            // 読ませ終わったフレームに報告が来る ＝ ②が勝つ。
            CollectionAssert.AreEqual(new[] { CommsNotice.MarkLogged },
                                      new System.Collections.Generic.List<CommsNotice>
                                      { l.Tick(Run(mark: true, resolved: true, read: true)) });
            Assert.IsFalse(l.BeginHowFired, "押しのけられた①b を消費している");

            CollectionAssert.AreEqual(new[] { CommsNotice.BeginHow }, Advance(l, 0.1f, read: true));
        }

        /// <summary>2 人目の体験者にも①b が出る（ラン単位のラッチを落とし忘れない）。</summary>
        [Test]
        public void TheSecondVisitor_HearsTheHowToAgain()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.2f, read: true);
            Assert.IsTrue(l.BeginHowFired);

            l.ResetRun();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            CollectionAssert.AreEqual(new[] { CommsNotice.BeginHow }, Advance(l, 0.1f, read: true));
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
        public void WhenTheAnomalyIsCleared_TheAgentSaysItWasLogged()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Assert.AreEqual(CommsNotice.MarkLogged, l.Tick(Run(mark: true, resolved: true)));
        }

        /// <summary>
        /// ⚠⚠ <b>解除が通らなければ「検出されませんでした」。</b>
        /// 3 周目の入れ替わり（<c>dismissible</c> でない演出）に押したときがこれで、
        /// **演出は走っているのに** false が返る（`canon/LEDGER.md` 0082）。
        /// 2026-08-17 まではここが「演出が走っていたか」だったので、消えていないのに
        /// 「異常が記録されました」と認めた顔をしていた。
        /// </summary>
        [Test]
        public void WhenNothingIsCleared_TheAgentSaysNothingWasDetected()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Assert.AreEqual(CommsNotice.MarkNothing, l.Tick(Run(mark: true, resolved: false)));
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
            // ⚠⚠ **締めのカット（untilMark）は報告を消費する ＝ 解除が通った側。**
            //     供給は TimelineDirector.NotifyVisitorMark の戻り値なので、
            //     「畳んだ後に演出の有無を見る」ことによる真逆の連絡は構造的に起きない。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 1f, waiting: true);

            Assert.AreEqual(CommsNotice.MarkLogged,
                            l.Tick(Run(waiting: true, mark: true, resolved: true)));
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
        public void NotReportingAtTheClosingCut_CallsStopFirst()
        {
            // ⚠ 待って最初に来るのは③a「止まってください！」（2026-09-06・0168）。
            //   ③b「異常があなたを…」は③a を読ませ終わってから続く（下のテスト）。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);

            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.PromptAfterWaitSec - 0.2f, waiting: true),
                                     "2 秒より前に催促した");
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt }, Advance(l, 0.4f, waiting: true));
            CollectionAssert.IsEmpty(Advance(l, 30f, waiting: true), "催促が二度来た");
        }

        /// <summary>
        /// ③a →（読ませ終わった縁）→ ③b。<b>同じ面のまま文面だけが替わる</b>（0096 / 0168）。
        /// ⚠ ここが「間を持つ」形に戻ると、床の演出が走っている最中に面が一度畳まれて開き直す。
        /// </summary>
        [Test]
        public void TheClosingCut_SaysStopFirst_ThenExplainsWhy()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);   // ①
            // ⚠ ①b を先に消費しておく。**読ませ終わりは①b の出口でもある**ので、
            //    ここで出しておかないと下の read:true が③b と一緒に①b を連れてくる。
            CollectionAssert.AreEqual(new[] { CommsNotice.BeginHow }, Advance(l, 0.5f, read: true));

            CollectionAssert.AreEqual(new[] { CommsNotice.Halt },
                                      Advance(l, CommsCueLogic.PromptAfterWaitSec + 0.2f, waiting: true));

            // 読ませ終わるまでは続かない（read=false のあいだ 1 通も出ない）。
            CollectionAssert.IsEmpty(Advance(l, 5f, waiting: true),
                                     "③a を読ませ終わる前に③b が出た");

            CollectionAssert.AreEqual(new[] { CommsNotice.Prompt },
                                      Advance(l, Dt * 3f, waiting: true, read: true));
            CollectionAssert.IsEmpty(Advance(l, 30f, waiting: true, read: true), "③b が二度来た");
        }

        /// <summary>
        /// ⚠⚠ <b>③a のあとに報告したら、③b は二度と届かない。</b>
        /// 届くと「排除しました」の直後に「排除してください」が来る ＝ 意味が真逆になる
        /// （②が真逆になる事故と同じ型・0082）。
        /// </summary>
        [Test]
        public void TheExplanation_NeverArrives_AfterTheVisitorReports()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);                       // ①
            Advance(l, 0.5f, read: true);                                         // ①b（先に消費）
            Advance(l, CommsCueLogic.PromptAfterWaitSec + 0.2f, waiting: true);   // ③a

            Assert.AreEqual(CommsNotice.MarkLogged,
                            l.Tick(Run(waiting: true, mark: true, resolved: true)));
            CollectionAssert.IsEmpty(Advance(l, 30f, waiting: false, read: true),
                                     "報告した後に③b が届いた");
        }

        [Test]
        public void TheStopCall_NeedsAnUninterruptedWait()
        {
            // 待ちが途切れたら数え直す（別のカットが挟まった / 一度畳まれた）。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, CommsCueLogic.PromptAfterWaitSec - 0.3f, waiting: true);   // あと 0.3 秒で催促
            Advance(l, 0.5f, waiting: false);                                     // そこで途切れた

            // 再開後は 0 から数え直す ＝ 同じ 2.7 秒ではまだ出ない。
            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.PromptAfterWaitSec - 0.3f, waiting: true),
                                     "途切れる前の待ちを持ち越している");
            // そこからさらに 0.4 秒で 2 秒を越えて、初めて出る。
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt }, Advance(l, 0.4f, waiting: true));
        }

        [Test]
        public void TheClosingNotices_NeverFire_WhenNobodyIsWaiting()
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
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt },
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

        // ------------------------------------------------------------------ ⓪ タイトルの直後

        private static CommsCueInput Intro(bool authorized = true, bool waiting = true, bool idle = true) =>
            new CommsCueInput
            {
                inIntro = true,
                startAuthorized = authorized,
                introWaiting = waiting,
                panelDoneReading = idle,
                dt = Dt,
            };

        private static System.Collections.Generic.List<CommsNotice> AdvanceIntro(
            CommsCueLogic l, float sec, bool authorized = true, bool waiting = true, bool idle = true)
        {
            var seen = new System.Collections.Generic.List<CommsNotice>();
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                CommsNotice v = l.Tick(Intro(authorized, waiting, idle));
                if (v != CommsNotice.None) seen.Add(v);
            }
            return seen;
        }

        [Test]
        public void TheTitleClosing_DeliversTheGreeting_ThenTheWalkOrder()
        {
            // ⚠ 名乗りを読ませているあいだ（idle:false）は次を出さない。読ませ終わった縁で
            //   **間を置かずに**指示が来る（2026-08-19・`canon/LEDGER.md` 0096・同じ面のまま繋ぐ）。
            var l = new CommsCueLogic();
            CollectionAssert.AreEqual(new[] { CommsNotice.Greeting },
                                      AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false));
            CollectionAssert.AreEqual(new[] { CommsNotice.Walk }, AdvanceIntro(l, 0.1f));
        }

        [Test]
        public void NothingComesOut_WhileTheTitleStillHoldsTheScreen()
        {
            // ⚠ 題字の上に受信票が重なる。
            var l = new CommsCueLogic();
            CollectionAssert.IsEmpty(AdvanceIntro(l, 10f, authorized: false));
            CollectionAssert.AreEqual(new[] { CommsNotice.Greeting },
                                      AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false));
        }

        [Test]
        public void TheWalkOrder_WaitsForTheGreetingToBeRead()
        {
            // ⚠ 面は 1 つしか無い。重ねると自己紹介が読まれないまま上書きされる。
            // ⚠ 待つのは**読ませ終わり**であって畳み終わりではない（0096）。
            var l = new CommsCueLogic();
            AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false);
            CollectionAssert.IsEmpty(AdvanceIntro(l, 30f, idle: false));
            CollectionAssert.AreEqual(new[] { CommsNotice.Walk },
                                      AdvanceIntro(l, CommsCueLogic.WalkGapSec + 0.1f));
        }

        [Test]
        public void TheWalkOrder_RepeatsButNotForever()
        {
            var l = new CommsCueLogic();
            AdvanceIntro(l, CommsCueLogic.GreetDelaySec + CommsCueLogic.WalkGapSec + 0.2f);
            for (int i = 0; i < CommsCueLogic.WalkRepeatMax; i++)
            {
                CollectionAssert.AreEqual(new[] { CommsNotice.Walk },
                                          AdvanceIntro(l, CommsCueLogic.WalkRepeatSec + 0.1f),
                                          $"{i + 1} 回目の出し直し");
            }
            CollectionAssert.IsEmpty(AdvanceIntro(l, CommsCueLogic.WalkRepeatSec * 3f),
                                     "同じ文が何度も来ると装置が壊れているように見える");
        }

        [Test]
        public void LeavingTheWaitingStage_AnnouncesTheArrival_Once()
        {
            // ⓪c「ポイントに到着しました。観測装置を起動します」は**段 0 を抜けた縁**で 1 度だけ。
            // ⚠ 演出が始まってからも延々と何か出す、ではない（そこから先は画が主役）。
            var l = new CommsCueLogic();
            AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f);
            CollectionAssert.AreEqual(new[] { CommsNotice.Arrived },
                                      AdvanceIntro(l, 30f, waiting: false));
            Assert.IsTrue(l.ArrivedFired);
        }

        [Test]
        public void TheArrival_IsAnnouncedEvenIfTheGuideNeverSpoke()
        {
            // スタッフの ⏭ で題字から一気に演出へ入った場合も、装置は起動を告げる。
            var l = new CommsCueLogic();
            CollectionAssert.AreEqual(new[] { CommsNotice.Arrived },
                                      AdvanceIntro(l, 1f, waiting: false));
        }

        [Test]
        public void TheArrival_IsNotAnnouncedWhileTheTitleHoldsTheScreen()
        {
            var l = new CommsCueLogic();
            CollectionAssert.IsEmpty(AdvanceIntro(l, 5f, authorized: false, waiting: false));
        }

        [Test]
        public void TheIntroLatches_SurviveInsideTheIntro()
        {
            // ⚠⚠ 旧実装は「本編に居なければ ResetRun」だったので、導入では毎フレーム落ちて
            //    ⓪が延々と出続ける（この構造を壊さないための固定）。
            var l = new CommsCueLogic();
            AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f);
            Assert.IsTrue(l.GreetFired);
            AdvanceIntro(l, 5f);
            Assert.IsTrue(l.WalkFired);
            CollectionAssert.DoesNotContain(AdvanceIntro(l, 5f), CommsNotice.Greeting);
        }

        [Test]
        public void TheSecondVisitor_HearsTheGreetingAgain()
        {
            var l = new CommsCueLogic();
            AdvanceIntro(l, CommsCueLogic.GreetDelaySec + CommsCueLogic.WalkGapSec + 0.2f);
            AdvanceIntro(l, 1f, waiting: false);      // ⓪c まで出し切る
            l.ResetRun();
            CollectionAssert.AreEqual(new[] { CommsNotice.Greeting },
                                      AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false));
        }
    }
}
