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
        private static CommsCueInput Run(float closing = -1f, bool mark = false,
                                         bool detected = false, bool read = false,
                                         bool lineDefined = false, bool lineCrossed = false) =>
            new CommsCueInput
            {
                inRun = true,
                closingSec = closing,
                closingLineDefined = lineDefined,
                closingLineCrossed = lineCrossed,
                markPressed = mark,
                markDetected = detected,
                panelDoneReading = read,
                dt = Dt,
            };

        /// <summary>本編に居るまま <paramref name="sec"/> 秒進め、その間に出た連絡を全部返す。</summary>
        private static System.Collections.Generic.List<CommsNotice> Advance(
            CommsCueLogic l, float sec, bool read = false)
        {
            var seen = new System.Collections.Generic.List<CommsNotice>();
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                CommsNotice v = l.Tick(Run(read: read));
                if (v != CommsNotice.None) seen.Add(v);
            }
            return seen;
        }

        /// <summary>
        /// <b>締めのカットに入ってからの時計を進める</b>（2026-09-06・0178）。報告待ちが立っているかは見ない。
        /// ⚠ 既定は締めの線が無い台本（<see cref="LineDefined"/> = false）。時計はどちらでも有効。
        /// 線がある台本（実機の既定・0233）は <see cref="LineDefined"/> を立て、<see cref="Crossed"/> で踏む。
        /// </summary>
        private sealed class Closing
        {
            public float Sec;
            public bool LineDefined;
            public bool Crossed;
        }

        /// <summary>締めのカットに居るまま <paramref name="sec"/> 秒進める。</summary>
        /// <param name="stopAtFirst">
        /// 1 通出たところで止める。⚠ <b>実機は連絡を出した瞬間に面が塞がる</b>ので、
        /// <c>read: true</c> のまま回し続けると③a の次のフレームに③b が続いてしまう。
        /// </param>
        private static System.Collections.Generic.List<CommsNotice> AdvanceClosing(
            CommsCueLogic l, Closing c, float sec, bool read = false, bool stopAtFirst = false)
        {
            var seen = new System.Collections.Generic.List<CommsNotice>();
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++)
            {
                CommsNotice v = l.Tick(Run(closing: c.Sec, read: read,
                                           lineDefined: c.LineDefined, lineCrossed: c.Crossed));
                c.Sec += Dt;
                if (v == CommsNotice.None) continue;
                seen.Add(v);
                if (stopAtFirst) break;
            }
            return seen;
        }

        /// <summary>
        /// ⚠⚠ <b>①と①b は時間で分ける</b>（2026-08-19・<c>canon/LEDGER.md</c> 0097・ユーザー指定
        /// 「調査を開始してください→異変をみつけたら〜と、表示は時間的に分けて。その間を切り詰める」）。
        /// ①b は<b>①を読ませ終わった縁</b>で、間を置かずに続く（同じ面のまま文面だけ替わる）。
        ///
        /// ⚠⚠ <b>2026-09-06 に中身を入れ替えた</b>（<c>canon/LEDGER.md</c> 0174・ユーザー指定
        /// 「調査を開始してくださいと、異変を見つけたらボタンを長押ししてくださいの順番を逆にしよう」）。
        /// <b>① ＝ 押し方（<c>BeginHow</c>）/ ①b ＝ 開始の合図（<c>Begin</c>）</b>。
        /// enum の名前は文面の名前なので入れ替えていない（走行ログの <c>id=</c> がこの綴り）。
        /// </summary>
        [Test]
        public void TheOpeningNotice_IsDeliveredOnceAfterTheIntroPanelIsClear()
        {
            var l = new CommsCueLogic();
            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.BeginDelaySec + 0.2f),
                                     "前の連絡を読ませている最中に割り込んでいる");
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, Advance(l, 0.1f, read: true));
            Assert.IsTrue(l.BeginFired);
            Assert.IsTrue(l.BeginHowFired, "押し方は導入の練習で完了済みとして扱う");
            // ラン 1 回に 1 度だけ。
            CollectionAssert.IsEmpty(Advance(l, 60f, read: true));
        }

        /// <summary>
        /// ⚠ <b>①b は押しのけられても消える権利が無い</b>（①③と違う）。
        /// ②や③に割り込まれた回では<b>その連絡を読ませ終わってから</b>改めて出す。
        /// ⚠ 0174 で中身が入れ替わったので、いまここが守るのは<b>開始の合図</b>の側。
        /// </summary>
        [Test]
        public void TheOpeningNotice_SurvivesBeingPushedAsideByAReport()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            // 面が空いたフレームに報告が来る ＝ ②が勝つ。
            CollectionAssert.AreEqual(new[] { CommsNotice.MarkLogged },
                                      new System.Collections.Generic.List<CommsNotice>
                                      { l.Tick(Run(mark: true, detected: true, read: true)) });
            Assert.IsFalse(l.BeginFired, "押しのけられた①b を消費している");

            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, Advance(l, 0.1f, read: true));
        }

        /// <summary>2 人目の体験者にも①b が出る（ラン単位のラッチを落とし忘れない）。</summary>
        [Test]
        public void TheSecondVisitor_HearsTheOpeningNoticeAgain()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f, read: true);
            Assert.IsTrue(l.BeginFired);

            l.ResetRun();
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin },
                                      Advance(l, CommsCueLogic.BeginDelaySec + 0.2f, read: true));
        }

        // ------------------------------------------------------------------ ① 導入が明けた直後

        /// <summary>
        /// ⚠⚠ <b>本編で最初に届くのは押し方</b>（0174）。ここが <c>Begin</c> に戻っていたら、
        /// 体験者は「調査を開始してください」を読んでから<b>押し方を知らないまま</b>歩き出す。
        /// </summary>
        [Test]
        public void EnteringTheMainRun_DoesNotRepeatTheHowTo()
        {
            var l = new CommsCueLogic();
            var seen = Advance(l, CommsCueLogic.BeginDelaySec + 0.2f, read: true);
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, seen);
            CollectionAssert.DoesNotContain(seen, CommsNotice.BeginHow);
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
        public void WhenAnAnomalyWasShowing_TheAgentSaysItWasDetected()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Assert.AreEqual(CommsNotice.MarkLogged, l.Tick(Run(mark: true, detected: true)));
        }

        /// <summary>
        /// 異常演出が画面を取っていなければ「検出されませんでした」。
        /// </summary>
        [Test]
        public void WhenNoAnomalyWasShowing_TheAgentSaysNothingWasDetected()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Assert.AreEqual(CommsNotice.MarkNothing, l.Tick(Run(mark: true, detected: false)));
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
            AdvanceClosing(l, new Closing(), 1f);

            Assert.AreEqual(CommsNotice.MarkLogged,
                            l.Tick(Run(closing: 1f, mark: true, detected: true)));
        }

        [Test]
        public void AReport_OutranksTheHowTo_AndTheHowToNeverArrivesLate()
        {
            // 同じフレームに 2 つ揃ったら報告が勝つ。⚠ 押しのけた方を**次のフレームへ持ち越さない**
            //    （持ち越すと「報告したのに関係ない連絡が来た」になる）。
            // ⚠ 0174 で 1 通目が①（押し方）になったので、消費されるのはそちら。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec - 0.05f);
            for (int i = 0; i < 10; i++)
            {
                CommsNotice v = l.Tick(Run(mark: true));
                Assert.AreEqual(CommsNotice.MarkNothing, v, $"i={i}");
            }
            CollectionAssert.IsEmpty(Advance(l, 30f), "押し方の連絡が遅れて出てきた");
        }

        // ------------------------------------------------------------------ ③ 押さないまま 3 秒

        [Test]
        public void NotReportingAtTheClosingCut_CallsStopFirst()
        {
            // ⚠ 締めのカットに入って最初に来るのは③a「止まってください！」（0168 / 0178）。
            //   ③b「異常があなたを…」は③a を読ませ終わってから続く（下のテスト）。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);   // ①
            Advance(l, 0.5f, read: true);                     // ①b

            var c = new Closing();
            CollectionAssert.IsEmpty(
                AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec - 0.2f, read: true),
                "3 秒より前に催促した");
            // ⚠ `read: true` を続けると③b もすぐ続く（実機は③a を読ませているあいだ false）。
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt, CommsNotice.Prompt },
                                      AdvanceClosing(l, c, 0.4f, read: true));
        }

        /// <summary>
        /// ⚠⚠ <b>報告待ちが立つのを待たない</b>（0178）。締めのカットは人形の動画が 8 秒あり、
        /// 報告待ちはそのあとに立つ。待っていた頃は<b>動画のあいだに押した人が③を一度も見なかった</b>。
        /// </summary>
        [Test]
        public void TheStopCall_DoesNotWaitForTheMarkStep()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            // 報告待ちが立っているかは入力に無い。締めの時計だけで出る。
            var c = new Closing();
            var seen = AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec + 0.2f, read: true);
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt, CommsNotice.Prompt }, seen);
        }

        [Test]
        public void TheStopCall_DoesNotWaitForTheClosingTakeToStart()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            // 締めの take が前の演出に塞がれていても、最終 A の確定時計は進む。
            Assert.AreEqual(CommsNotice.Halt,
                            l.Tick(Run(closing: CommsCueLogic.HaltAfterClosingSec,
                                       read: true, lineDefined: true)));
        }

        /// <summary>
        /// ⚠⚠ <b>③a は面が空くまで待つ。押しのけられても消えない</b>（0178・ユーザー指定
        /// 「止まってください！以降の流れは全員に見せる」）。
        /// </summary>
        [Test]
        public void TheStopCall_WaitsForThePanel_AndIsNeverLost()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            // 面が塞がっている（read=false）あいだは出さない — 走っている連絡を上書きしない。
            var c = new Closing();
            CollectionAssert.IsEmpty(
                AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec + 5f, read: false),
                "面が塞がっているのに割り込んだ");

            // 空いた瞬間に出る（消えていない）。⚠ 1 フレームだけ進める（続けると③b も出る）。
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt },
                                      AdvanceClosing(l, c, Dt * 1.5f, read: true));
        }

        /// <summary>
        /// ③a →（読ませ終わった縁）→ ③b。<b>同じ面のまま文面だけが替わる</b>（0096 / 0168）。
        /// ⚠ ここが「間を持つ」形に戻ると、床の演出が走っている最中に面が一度畳まれて開き直す。
        /// </summary>
        [Test]
        public void TheClosingCut_SaysStopFirst_ThenExplainsWhy()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);   // ①（押し方）
            // ⚠ ①b を先に消費しておく。**読ませ終わりは①b の出口でもある**ので、
            //    ここで出しておかないと下の read:true が③b と一緒に①b を連れてくる。
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, Advance(l, 0.5f, read: true));

            // ③a が出るまで面は空いている。出た後は読ませているので塞がる（実機と同じ）。
            var c = new Closing();
            CollectionAssert.AreEqual(
                new[] { CommsNotice.Halt },
                AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec + 0.2f, read: true, stopAtFirst: true));

            // 読ませ終わるまでは続かない。
            CollectionAssert.IsEmpty(AdvanceClosing(l, c, 5f, read: false),
                                     "③a を読ませ終わる前に③b が出た");

            CollectionAssert.AreEqual(new[] { CommsNotice.Prompt },
                                      AdvanceClosing(l, c, 30f, read: true), "③b が二度来た / 来なかった");
        }

        /// <summary>
        /// ⚠⚠ <b>③b は報告しても届く</b>（2026-09-06・0178・ユーザー指定
        /// 「止まってください！以降の流れは全員に見せる」）。
        /// ⚠ 0168 では逆の規律だった（押したら③b を止める）。**覆っている。**
        /// 押せる時間そのものを締めの頭で塞いだ（<c>TakeRunnerLogic.MarkGraceSec</c>）ので、
        /// ③a より前に押し切れる人は居ない。
        /// </summary>
        [Test]
        public void TheExplanation_StillArrives_AfterTheVisitorReports()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);   // ①（押し方）
            Advance(l, 0.5f, read: true);                     // ①b（先に消費）

            var c = new Closing();
            CollectionAssert.AreEqual(
                new[] { CommsNotice.Halt },
                AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec + 0.2f, read: true, stopAtFirst: true));

            // ③a を読ませている最中に報告した。
            Assert.AreEqual(CommsNotice.MarkLogged,
                            l.Tick(Run(closing: c.Sec, mark: true, detected: true)));
            CollectionAssert.AreEqual(new[] { CommsNotice.Prompt },
                                      AdvanceClosing(l, c, 30f, read: true),
                                      "報告したら③b が消えた（全員に見せる約束が守られていない）");
        }

        // ------------------------------------------------------------------ ③a は場所で出る（0233）

        /// <summary>
        /// ⚠⚠ <b>③a「止まってください！」は締めの線（3 周目 A の凍結点）を踏んだ瞬間に出る</b>
        /// （2026-09-19・<c>canon/LEDGER.md</c> 0233・ユーザー指定
        /// 「時間指定で 4s ではなく、場所指定にし、その場所を、左右反転の演出のときのフリーズされる位置に」）。
        /// 線がある台本でも時計は有効。踏めば 3 秒前でも出る。
        /// </summary>
        [Test]
        public void TheStopCall_UsesTheClock_WhenTheDefinedLineIsNotCrossed()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);   // ①
            Advance(l, 0.5f, read: true);                     // ①b

            var c = new Closing { LineDefined = true };
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt },
                                      AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec + 0.2f,
                                                     read: true, stopAtFirst: true),
                                      "線を踏まなくても 3 秒で③a が出る");
        }

        /// <summary>線を早く踏めば、時計の 3 秒より前でも出る。</summary>
        [Test]
        public void TheStopCall_ComesEarly_WhenTheLineIsCrossedEarly()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            var c = new Closing { LineDefined = true, Crossed = true };
            var seen = AdvanceClosing(l, c, 0.5f, read: true, stopAtFirst: true);
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt }, seen);
            Assert.That(c.Sec, Is.LessThan(CommsCueLogic.HaltAfterClosingSec), "時計を待ってから出た");
        }

        /// <summary>
        /// ③a は面が空くまで待って必ず出す（0178「全員に見せる」）。線の記録は締めのあいだ
        /// 立ちっぱなし（<c>TakeRunnerLogic.ClosingLineCrossed</c>）なので、待っても消えない。
        /// </summary>
        [Test]
        public void TheStopCall_FromTheLine_WaitsForThePanel_AndIsNeverLost()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            var c = new Closing { LineDefined = true, Crossed = true };
            CollectionAssert.IsEmpty(AdvanceClosing(l, c, 5f, read: false), "面が塞がっているのに割り込んだ");
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt },
                                      AdvanceClosing(l, c, Dt * 1.5f, read: true));
        }

        /// <summary>③a は 1 度きり。線の上で往復して記録が立ちっぱなしでも 2 度は出ない。</summary>
        [Test]
        public void TheStopCall_FromTheLine_FiresOnlyOnce()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            var c = new Closing { LineDefined = true, Crossed = true };
            var seen = AdvanceClosing(l, c, 30f, read: true);
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt, CommsNotice.Prompt }, seen,
                                      "記録が立ちっぱなしのあいだに③a が繰り返された");
        }

        /// <summary>
        /// 線が無い台本（<c>closingLineDefined</c> = false）でも時計（3 秒）で出る。
        /// </summary>
        [Test]
        public void WithoutAClosingLine_TheClock_StillCallsStop()
        {
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);

            var c = new Closing { LineDefined = false };
            CollectionAssert.IsEmpty(
                AdvanceClosing(l, c, CommsCueLogic.HaltAfterClosingSec - 0.2f, read: true));
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt, CommsNotice.Prompt },
                                      AdvanceClosing(l, c, 0.4f, read: true));
        }

        [Test]
        public void TheClosingNotices_NeverFire_WithoutTheClosingCut()
        {
            // 締めのカットが無い体験（著作が変わった / 4 周目まで来なかった）では 1 通も出さない。
            var l = new CommsCueLogic();
            var seen = Advance(l, 120f, read: true);
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, seen);
        }

        // ------------------------------------------------------------------ 体験 1 回ぶんの状態

        [Test]
        public void TheSecondVisitor_GetsEverythingAgain_AfterLeavingTheRun()
        {
            // 本編を出た縁（ラン開始・中止・終幕）で落ちる。
            var l = new CommsCueLogic();
            Advance(l, CommsCueLogic.BeginDelaySec + 0.2f);
            Advance(l, 0.5f, read: true);
            AdvanceClosing(l, new Closing(), CommsCueLogic.HaltAfterClosingSec + 0.4f, read: true);

            l.Tick(new CommsCueInput { inRun = false, dt = Dt });

            CollectionAssert.IsEmpty(Advance(l, CommsCueLogic.BeginDelaySec + 0.2f));
            // 2 人目にも開始の合図が要る（先に消費してから③を見る）。
            CollectionAssert.AreEqual(new[] { CommsNotice.Begin }, Advance(l, 0.5f, read: true));
            CollectionAssert.AreEqual(new[] { CommsNotice.Halt },
                                      AdvanceClosing(l, new Closing(),
                                                     CommsCueLogic.HaltAfterClosingSec + 0.2f,
                                                     read: true, stopAtFirst: true));
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
                                      Advance(l, CommsCueLogic.BeginDelaySec + 0.2f, read: true));
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
        public void TheTitleClosing_DeliversOnlyTheWalkOrder()
        {
            // 名乗りは報告練習の前に済んでいる。タイトル後は歩行指示だけを出す。
            var l = new CommsCueLogic();
            CollectionAssert.IsEmpty(
                AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false));
            CollectionAssert.AreEqual(new[] { CommsNotice.Walk }, AdvanceIntro(l, 0.1f));
        }

        [Test]
        public void NothingComesOut_WhileTheTitleStillHoldsTheScreen()
        {
            // ⚠ 題字の上に受信票が重なる。
            var l = new CommsCueLogic();
            CollectionAssert.IsEmpty(AdvanceIntro(l, 10f, authorized: false));
            CollectionAssert.IsEmpty(
                AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false));
        }

        [Test]
        public void TheWalkOrder_WaitsForThePanelToBeClear()
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
        public void TheSecondVisitor_DoesNotHearTheGreetingAgainAfterTheTitle()
        {
            var l = new CommsCueLogic();
            AdvanceIntro(l, CommsCueLogic.GreetDelaySec + CommsCueLogic.WalkGapSec + 0.2f);
            AdvanceIntro(l, 1f, waiting: false);      // ⓪c まで出し切る
            l.ResetRun();
            CollectionAssert.IsEmpty(
                AdvanceIntro(l, CommsCueLogic.GreetDelaySec + 0.1f, idle: false));
            CollectionAssert.AreEqual(new[] { CommsNotice.Walk }, AdvanceIntro(l, 0.1f));
        }
    }
}
