#nullable enable
using System.Reflection;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 憑依の出し方（<c>canon/LEDGER.md</c> 0230）の時計・前線・面の段と、嘘の一文の 1 回きりの門。
    /// 境界は「ぴったり」と「1 コマ手前」を分けて見る（<c>memory/comms_clock_boundaries.md</c>）。
    /// </summary>
    public sealed class CommsPossessionLogicTests
    {
        private const float Dt = 1f / 30f;

        [Test]
        public void ReadSecGrowsWithTheSentence_WithinItsBounds()
        {
            Assert.AreEqual(CommsPossessionLogic.ReadMinSec,
                CommsPossessionLogic.ReadSecFor(0, ShowLang.Ja), 0.001f, "空でも下限");
            Assert.AreEqual(1.6f, CommsPossessionLogic.ReadSecFor(12, ShowLang.Ja), 0.001f,
                "短い真実も1.6秒は読める");
            Assert.AreEqual(1.6f, CommsPossessionLogic.ReadSecFor(24, ShowLang.En), 0.001f,
                "英語でも短文の下限を保つ");
            Assert.Less(CommsPossessionLogic.ReadSecFor(39, ShowLang.Ja), CommsPossessionLogic.ReadMaxSec,
                "いちばん長い文面（①b・改行込み 39 字）でも上限には掛からない");
            Assert.AreEqual(CommsPossessionLogic.ReadMaxSec,
                CommsPossessionLogic.ReadSecFor(200, ShowLang.Fr), 0.001f);
        }

        [Test]
        public void SamplesEveryAuthoredBoundary()
        {
            const float readSec = 1.35f;
            float sweepAt = CommsPossessionLogic.SweepStartSec(readSec);
            float end = CommsPossessionLogic.DurationFor(readSec);

            CommsPossessionSample a = CommsPossessionLogic.Sample(0f, readSec);
            Assert.AreEqual(CommsPossessionPhase.Shown, a.phase);
            Assert.AreEqual(0f, a.show, 1e-6f);
            Assert.AreEqual(0f, a.sweep, 1e-6f);

            CommsPossessionSample shown = CommsPossessionLogic.Sample(CommsPossessionLogic.ShowSec, readSec);
            Assert.AreEqual(CommsPossessionPhase.Shown, shown.phase);
            Assert.AreEqual(1f, shown.show, 1e-6f, "ShowSec で全文が出切る");

            CommsPossessionSample before = CommsPossessionLogic.Sample(sweepAt - Dt, readSec);
            Assert.AreEqual(CommsPossessionPhase.Shown, before.phase, "1 コマ手前はまだ読ませている");
            Assert.AreEqual(0f, before.sweep, 1e-6f);

            CommsPossessionSample at = CommsPossessionLogic.Sample(sweepAt, readSec);
            Assert.AreEqual(CommsPossessionPhase.Sweep, at.phase, "境界ぴったりで前線が降り始める");
            Assert.AreEqual(0f, at.sweep, 1e-5f);

            CommsPossessionSample mid = CommsPossessionLogic.Sample(sweepAt + CommsPossessionLogic.SweepSec * 0.5f, readSec);
            Assert.AreEqual(CommsPossessionPhase.Sweep, mid.phase);
            Assert.AreEqual(0.5f, mid.sweep, 1e-5f);

            CommsPossessionSample done = CommsPossessionLogic.Sample(end, readSec);
            Assert.AreEqual(CommsPossessionPhase.Cursed, done.phase, "境界ぴったりで塗り替わり切る");
            Assert.AreEqual(1f, done.sweep, 1e-6f);
            Assert.AreEqual(CommsPossessionPhase.Cursed, CommsPossessionLogic.Sample(end + 10f, readSec).phase);
            Assert.AreEqual(1.5f, CommsPossessionLogic.SweepSec, 1e-6f,
                "左から人形へ塗り替わる過程を読める尺");
        }

        [Test]
        public void ShowAndSweepNeverRunBackward()
        {
            const float readSec = 1.35f;
            CommsPossessionSample previous = CommsPossessionLogic.Sample(0f, readSec);
            for (int i = 1; i <= 300; i++)
            {
                CommsPossessionSample current = CommsPossessionLogic.Sample(i * 0.01f, readSec);
                Assert.GreaterOrEqual(current.show + 1e-6f, previous.show);
                Assert.GreaterOrEqual(current.sweep + 1e-6f, previous.sweep);
                Assert.GreaterOrEqual((int)current.phase, (int)previous.phase, "段は戻らない");
                previous = current;
            }
        }

        [Test]
        public void Sweep_CursesNothingAtZero_EverythingAtOne_AndMovesLeftToRight()
        {
            const float left = -0.7f, right = 0.3f;
            for (float x = left - 0.05f; x <= right + 0.05f; x += 0.013f)
            for (float y = -0.2f; y <= 0.2f; y += 0.011f)
            {
                Assert.IsFalse(CommsCurseLogic.IsSwept(x, y, 0f, left, right), "進み 0 は 1 画素も呪われない");
                Assert.IsTrue(CommsCurseLogic.IsSwept(x, y, 1f, left, right), "進み 1 は全面");
            }
            for (float p = 0.05f; p < 1f; p += 0.05f)
            {
                float front = left + (right - left) * p;
                for (float x = left; x <= right; x += 0.013f)
                {
                    Assert.AreEqual(x <= front, CommsCurseLogic.IsSwept(x, -0.1f, p, left, right));
                    Assert.AreEqual(x <= front, CommsCurseLogic.IsSwept(x, 0.1f, p, left, right),
                                    "前線は y に依らない");
                }
            }
        }

        [Test]
        public void TearBands_RemainSnappedToTheYGrid()
        {
            const float top = 0.185f, bottom = -0.009f;
            Assert.AreEqual(0, CommsCurseLogic.SweepBandOf(top, top));
            Assert.AreEqual(0, CommsCurseLogic.SweepBandOf(top + 1f, top), "上端より上は最上段");
            Assert.AreEqual(1, CommsCurseLogic.SweepBandOf(top - CommsCurseLogic.TearBandM - 0.001f, top));
            int bands = CommsCurseLogic.SweepBandCount(top, bottom);
            Assert.AreEqual(5, bands, "0.194m / 0.04m = 4.85 -> 5 帯");
        }

        [Test]
        public void ComputeTear_IsSilentAtZero_BoundedAtFull_AndDeterministic()
        {
            var shift = new float[CommsCurseLogic.TearMaxBands];
            var drop = new float[CommsCurseLogic.TearMaxBands];
            CommsCurseLogic.ComputeTear(0f, 1.234f, 5, shift, drop, out float flicker);
            Assert.AreEqual(1f, flicker, 1e-6f, "乱れ 0 は明滅しない");
            for (int i = 0; i < shift.Length; i++)
            {
                Assert.AreEqual(0f, shift[i], 1e-6f);
                Assert.AreEqual(0f, drop[i], 1e-6f);
            }

            bool anyShift = false, anyDrop = false;
            for (int k = 0; k < 40; k++)
            {
                float seed = 0.3f + k * 0.017f;
                CommsCurseLogic.ComputeTear(1f, seed, 5, shift, drop, out flicker);
                Assert.GreaterOrEqual(flicker, 0.78f - 1e-6f);
                Assert.LessOrEqual(flicker, 1f + 1e-6f);
                for (int i = 0; i < shift.Length; i++)
                {
                    Assert.LessOrEqual(System.Math.Abs(shift[i]), CommsCurseLogic.TearShiftM + 1e-6f, "飛びは ±TearShiftM の中");
                    Assert.LessOrEqual(drop[i], 0.5f + 1e-6f, "脱落は 0.5 まで（全面の砂にしない）");
                    if (i >= 5)
                    {
                        Assert.AreEqual(0f, shift[i], 1e-6f, "面の外の帯は触らない");
                        Assert.AreEqual(0f, drop[i], 1e-6f);
                    }
                    if (System.Math.Abs(shift[i]) > 1e-6f) anyShift = true;
                    if (drop[i] > 1e-6f) anyDrop = true;
                }
                // 同じ種は同じ絵（走行ごとに違う絵を作らない）。
                var shift2 = new float[CommsCurseLogic.TearMaxBands];
                var drop2 = new float[CommsCurseLogic.TearMaxBands];
                CommsCurseLogic.ComputeTear(1f, seed, 5, shift2, drop2, out float flicker2);
                Assert.AreEqual(flicker, flicker2, 1e-7f);
                CollectionAssert.AreEqual(shift, shift2);
                CollectionAssert.AreEqual(drop, drop2);
            }
            Assert.IsTrue(anyShift, "強さ 1 なら飛ぶ帯が出る");
            Assert.IsTrue(anyDrop, "強さ 1 なら脱落する帯が出る");
        }

        [Test]
        public void Tear_RisesAtTheSweepHead_HoldsThroughIt_AndTrailsAfterCursed()
        {
            const float readSec = 1.35f;
            float sweepAt = CommsPossessionLogic.SweepStartSec(readSec);
            float end = CommsPossessionLogic.DurationFor(readSec);
            Assert.AreEqual(0f, CommsPossessionLogic.TearFor(0f, readSec), 1e-6f);
            Assert.AreEqual(0f, CommsPossessionLogic.TearFor(sweepAt - Dt, readSec), 1e-6f, "読ませているあいだは乱れない");
            Assert.AreEqual(0f, CommsPossessionLogic.TearFor(sweepAt, readSec), 1e-6f, "頭は 0 から立つ");
            Assert.AreEqual(CommsPossessionLogic.TearPeak * 0.5f,
                CommsPossessionLogic.TearFor(sweepAt + CommsPossessionLogic.TearAttackSec * 0.5f, readSec), 1e-5f);
            Assert.AreEqual(CommsPossessionLogic.TearPeak,
                CommsPossessionLogic.TearFor(sweepAt + CommsPossessionLogic.SweepSec * 0.5f, readSec), 1e-6f, "降りているあいだは頭打ち");
            Assert.AreEqual(CommsPossessionLogic.TearPeak, CommsPossessionLogic.TearFor(end, readSec), 1e-6f, "抜けた瞬間はまだ立っている");
            Assert.AreEqual(CommsPossessionLogic.TearPeak * 0.5f,
                CommsPossessionLogic.TearFor(end + CommsPossessionLogic.TearReleaseSec * 0.5f, readSec), 1e-5f, "尾は線形に引く");
            Assert.AreEqual(0f, CommsPossessionLogic.TearFor(end + CommsPossessionLogic.TearReleaseSec + 0.01f, readSec), 1e-6f);
            Assert.AreEqual(0.6f, CommsPossessionLogic.TearPeak, 1e-6f, "本編の頭打ち（0057）と同じ");

            CommsPossessionSample s = CommsPossessionLogic.Sample(sweepAt + 0.2f, readSec);
            Assert.AreEqual(CommsPossessionLogic.TearPeak, s.tear, 1e-6f, "Sample も同じ値を運ぶ");
            Assert.AreEqual(0f, CommsPossessionLogic.Sample(sweepAt - Dt, readSec).tear, 1e-6f);
        }

        [Test]
        public void PossessedNotice_ShowsAllAtOnce_ThenSweeps_ThenCloses()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.6f);
            l.Begin(12, CommsDelivery.Possessed);
            float readSec = CommsPanelLogic.PossessionReadSecFor(12, ShowLanguage.Current);
            Assert.AreEqual(CommsPossessionLogic.DurationFor(readSec), l.TypeSec, 1e-5f);
            Assert.AreEqual(CommsStage.Intrusion, l.Stage);
            Assert.AreEqual(CommsWeights.Hidden.panel, l.Weights.panel, 1e-6f, "侵入中は既存の面を完全に消す");
            Assert.AreEqual(1f, l.TakeoverErrorOpacity, 1e-6f, "空間エラーだけが先行する");
            Assert.AreEqual(CommsPossessionPhase.Off, l.PossessionSample.phase, "侵入と開いている最中は Off");
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f);

            l.Tick(CommsPanelLogic.IntrusionSec);
            Assert.AreEqual(CommsStage.In, l.Stage);
            l.Tick(CommsPanelLogic.InSec);
            Assert.AreEqual(CommsStage.Type, l.Stage);
            Assert.AreEqual(1f, l.Weights.reveal, 1e-6f, "字は最初から全部そこに在る");
            Assert.AreEqual(0f, l.Weights.glyph, 1e-6f, "濃さだけがこれから上がる");
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f, "出た初めは通常の面");
            Assert.AreEqual(0f, l.Weights.sweep, 1e-6f);

            l.Tick(CommsPossessionLogic.ShowSec);
            Assert.AreEqual(1f, l.Weights.glyph, 1e-5f);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f, "読ませているあいだ斑は 0 のまま（1 秒の立ち上がりは効かない）");

            l.Tick(readSec);
            Assert.AreEqual(CommsPossessionPhase.Sweep, l.PossessionSample.phase);
            Assert.AreEqual(0f, l.Weights.sweep, 1e-5f);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f);

            Assert.AreEqual(0f, l.Weights.tear, 1e-6f, "前線の頭は乱れ 0 から立つ");

            l.Tick(CommsPossessionLogic.SweepSec * 0.5f);
            Assert.AreEqual(0.5f, l.Weights.sweep, 1e-5f);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f, "降りている最中は前線だけ（斑は使わない）");
            Assert.AreEqual(CommsPossessionLogic.TearPeak, l.Weights.tear, 1e-6f, "降りているあいだは乱れが頭打ち（0231）");
            Assert.Greater(l.Weights.tearSeed, 0f, "乱れの種は開いてからの秒");

            l.Tick(CommsPossessionLogic.SweepSec * 0.5f + 0.001f);
            Assert.AreEqual(CommsStage.Hold, l.Stage, "塗り替わり切ったら読ませる段（呪われたまま）");
            Assert.AreEqual(CommsPossessionPhase.Cursed, l.PossessionSample.phase);
            Assert.AreEqual(1f, l.Weights.sweep, 1e-6f);
            Assert.AreEqual(1f, l.Weights.curse, 1e-6f, "塗り替わった後は全面（目標 0.6 ではなく 1）");
            Assert.AreEqual(1f, l.Weights.glyph, 1e-6f);
            Assert.Greater(l.Weights.tear, 0f, "抜けた直後は乱れの尾が残る");
            Assert.AreEqual(CommsPanelLogic.PossessedHoldSec,
                CommsPanelLogic.HoldSecFor(CommsDelivery.Possessed), 1e-6f);
            l.Tick(CommsPanelLogic.PossessedHoldSec);
            Assert.AreEqual(CommsStage.Off, l.Stage);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f);
            Assert.AreEqual(CommsPossessionPhase.Off, l.PossessionSample.phase);
        }

        [Test]
        public void TakeoverBoundaries_ClosePanelAndErrorTogether()
        {
            var l = new CommsPanelLogic();
            l.Begin(12, CommsDelivery.Possessed);
            float readSec = CommsPanelLogic.PossessionReadSecFor(12, ShowLanguage.Current);
            float fillStart = l.TakeoverFillStartSec;
            float failAt = l.FailureAtSec;
            Assert.AreEqual(CommsPanelLogic.IntrusionSec + CommsPanelLogic.InSec +
                CommsPossessionLogic.SweepStartSec(readSec), fillStart, 1e-5f);

            l.Tick(CommsPanelLogic.IntrusionSec - Dt);
            Assert.AreEqual(CommsStage.Intrusion, l.Stage);
            Assert.AreEqual(1f, l.TakeoverErrorOpacity, 1e-6f);
            Assert.AreEqual(0f, l.TakeoverReadFocus);
            float before = l.TakeoverElapsedSec;
            l.Tick(Dt);
            Assert.AreEqual(CommsStage.In, l.Stage);
            Assert.Greater(l.TakeoverElapsedSec, before, "段をまたいでも開始からの時計は戻らない");

            l.Tick(CommsPanelLogic.InSec * 0.5f);
            Assert.AreEqual(0.675f, l.TakeoverErrorOpacity, 1e-5f);
            Assert.AreEqual(0.5f, l.TakeoverReadFocus, 1e-5f);
            l.Tick(CommsPanelLogic.InSec * 0.5f);
            Assert.AreEqual(CommsStage.Type, l.Stage);
            Assert.AreEqual(0.35f, l.TakeoverErrorOpacity, 1e-6f);
            Assert.AreEqual(1f, l.TakeoverReadFocus);

            l.Tick(fillStart - CommsPanelLogic.IntrusionSec - CommsPanelLogic.InSec);
            Assert.AreEqual(CommsPossessionPhase.Sweep, l.PossessionSample.phase);
            Assert.IsFalse(l.TakeoverBlockFailed);
            l.Tick(failAt - fillStart - Dt);
            Assert.IsFalse(l.TakeoverBlockFailed, "破損終了の1コマ手前はまだ遮断中");
            l.Tick(Dt);
            Assert.IsTrue(l.TakeoverBlockFailed, "破損終了で遮断失敗へ変わる");
            Assert.AreEqual(CommsStage.Hold, l.Stage);
            Assert.AreEqual(.35f, l.TakeoverErrorOpacity);
            Assert.AreEqual(1f, l.Weights.panel);
            Assert.AreEqual(1f, l.Weights.glyph);
            Assert.AreEqual(1f, l.Weights.sweep);
            l.Tick(CommsPanelLogic.PossessedHoldSec - CommsPanelLogic.TakeoverBlockBreakSec - Dt);
            Assert.AreEqual(CommsStage.Hold, l.Stage);
            Assert.AreEqual(1f, l.TakeoverReadFocus);
            Assert.AreEqual(.35f, l.TakeoverErrorOpacity);
            l.Tick(Dt);
            Assert.AreEqual(CommsStage.Off, l.Stage);
            Assert.AreEqual(0f, l.TakeoverErrorOpacity);
            Assert.AreEqual(0f, l.TakeoverReadFocus);
            Assert.AreEqual(fillStart + CommsPanelLogic.TakeoverBlockFillSec +
                CommsPanelLogic.PossessedHoldSec, l.TakeoverElapsedSec, 1e-4f);
        }

        [TestCase(30)]
        [TestCase(72)]
        [TestCase(90)]
        [TestCase(8)]
        public void TakeoverClock_PreservesFrameRemainders(int fps)
        {
            var l = new CommsPanelLogic();
            l.Begin(9, CommsDelivery.Possessed);
            float midpoint = l.TakeoverFillStartSec + CommsPanelLogic.TakeoverBlockFillSec * .5f;
            int steps = (int)System.Math.Ceiling(midpoint * fps);
            for (int i = 0; i < steps; i++) l.Tick(1f / fps);
            Assert.IsFalse(l.TakeoverBlockFailed);
            Assert.AreEqual(CommsPossessionPhase.Sweep, l.PossessionSample.phase);
            Assert.AreEqual((l.TakeoverElapsedSec - l.TakeoverFillStartSec) /
                CommsPanelLogic.TakeoverBlockFillSec,
                l.PossessionSample.sweep, 0.00002f);
            while (l.Active) l.Tick(1f / fps);
            float finishAt = l.TakeoverFillStartSec + CommsPanelLogic.TakeoverBlockFillSec +
                CommsPanelLogic.PossessedHoldSec;
            Assert.That(l.TakeoverElapsedSec, Is.InRange(finishAt - .0001f, finishAt + 1f / fps));
        }

        [Test]
        public void TakeoverSweepStartsWithBlockFillAndFailureFollowsBreak()
        {
            Assert.AreEqual(1.5f, CommsPanelLogic.TakeoverBlockFillSec);
            Assert.AreEqual(4.17f, CommsPanelLogic.DefaultTakeoverFillStartSec, 1e-5f);
            Assert.AreEqual(5.92f, CommsPanelLogic.DefaultTakeoverFailureSec, 1e-5f);
            var l = new CommsPanelLogic();
            l.Begin(9, CommsDelivery.Possessed);
            l.Tick(l.TakeoverFillStartSec);
            Assert.IsFalse(l.TakeoverBlockFailed);
            Assert.AreEqual(CommsPossessionPhase.Sweep, l.PossessionSample.phase);
            Assert.AreEqual(0f, l.PossessionSample.sweep, 1e-5f);
            l.Tick(CommsPanelLogic.TakeoverBlockFillSec * .5f);
            Assert.AreEqual(.5f, l.PossessionSample.sweep, 1e-5f);
            l.Tick(CommsPanelLogic.TakeoverBlockFillSec * .5f);
            Assert.AreEqual(1f, l.PossessionSample.sweep);
            Assert.IsFalse(l.TakeoverBlockFailed);
            l.Tick(CommsPanelLogic.TakeoverBlockBreakSec - .001f);
            Assert.IsFalse(l.TakeoverBlockFailed);
            l.Tick(.001f);
            Assert.IsTrue(l.TakeoverBlockFailed);
            Assert.AreEqual(CommsPossessionPhase.Cursed, l.PossessionSample.phase);
        }

        [TestCase(ShowLang.Ja)]
        [TestCase(ShowLang.En)]
        [TestCase(ShowLang.Fr)]
        public void LongTakeoverReadingDelaysFillAndFailureTogether(ShowLang lang)
        {
            ShowLang previous = ShowLanguage.Current;
            try
            {
                ShowLanguage.Select(lang);
                var l = new CommsPanelLogic();
                l.Begin(200, CommsDelivery.Possessed);
                Assert.AreEqual(CommsPossessionLogic.ReadMaxSec,
                    CommsPanelLogic.PossessionReadSecFor(200, lang), 1e-5f);
                Assert.AreEqual(4.97f, l.TakeoverFillStartSec, 1e-5f);
                Assert.AreEqual(6.72f, l.FailureAtSec, 1e-5f);
                l.Tick(CommsPanelLogic.DefaultTakeoverFillStartSec);
                Assert.AreEqual(CommsPossessionPhase.Shown, l.PossessionSample.phase);
                Assert.IsFalse(l.TakeoverBlockFailed);
                l.Tick(l.TakeoverFillStartSec - l.TakeoverElapsedSec);
                Assert.AreEqual(0f, l.PossessionSample.sweep, 1e-5f);
                l.Tick(CommsPanelLogic.TakeoverBlockFillSec * .5f);
                Assert.AreEqual(.5f, l.PossessionSample.sweep, 1e-5f);
                l.Tick(l.FailureAtSec - l.TakeoverElapsedSec);
                Assert.IsTrue(l.TakeoverBlockFailed);
            }
            finally { ShowLanguage.Select(previous); }
        }

        [Test]
        public void PendingCompletion_BlocksBeforeCueTick_ButNotAfterSuppressedCompletion()
        {
            var l = new CommsCueLogic();
            Assert.IsFalse(l.HasPendingTakeover(0));
            Assert.IsTrue(l.HasPendingTakeover(1));
            var input = Input(dollCatchUpCompletedCount: 1);
            input.takeoverSuppressed = true;
            l.Tick(input);
            Assert.IsFalse(l.HasPendingTakeover(1));
            l.ResetRun();
            Assert.IsTrue(l.HasPendingTakeover(1));
            l.NotifyDelivered(CommsNotice.Takeover);
            Assert.IsFalse(l.HasPendingTakeover(1));
        }

        [Test]
        public void BlockedReport_DoesNotCountOrResolveAnything()
        {
            var go = new UnityEngine.GameObject("Blocked report test");
            try
            {
                var control = go.AddComponent<ShowControlClient>();
                control.VisitorMarkBlockedProvider = () => true;
                for (int i = 0; i < 20; i++) Assert.IsFalse(control.RecordVisitorMark());
                Assert.AreEqual(0, control.VisitorMarkCount);
                Assert.AreEqual(0, control.ReportedAnomalyCount);
                Assert.IsFalse(control.LastMarkResolved);
                control.VisitorMarkBlockedProvider = () => false;
                Assert.IsTrue(control.RecordVisitorMark());
                Assert.AreEqual(1, control.VisitorMarkCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ClosingReportIsRejectedUntilThePromptIsFullyShown()
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var go = new UnityEngine.GameObject("Closing report gate test");
            try
            {
                var control = go.AddComponent<ShowControlClient>();
                var timeline = go.AddComponent<TimelineDirector>();
                typeof(TimelineDirector).GetField("_closingAreaEnteredAt", Private)!
                    .SetValue(timeline, UnityEngine.Time.unscaledTime);
                typeof(ShowControlClient).GetField("timelineDirector", Private)!
                    .SetValue(control, timeline);

                Assert.IsFalse(control.RecordVisitorMark());
                Assert.AreEqual(0, control.VisitorMarkCount);

                ShowRunDirector run = control.RunDirector!;
                run.NotifyClosingPromptReadable();
                Assert.IsTrue(control.RecordVisitorMark());
                Assert.AreEqual(1, control.VisitorMarkCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void CourseRegistrationBlocksDirectReports()
        {
            var go = new UnityEngine.GameObject("Registration report gate test");
            try
            {
                var control = go.AddComponent<ShowControlClient>();
                control.CourseRegistrationActiveProvider = () => true;
                Assert.IsTrue(control.IsVisitorMarkBlocked);
                Assert.IsFalse(control.RecordVisitorMark());
                Assert.AreEqual(0, control.VisitorMarkCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void PossessionStartsHiddenFromAnOpenNotice_AndRejectsNotificationInterruption()
        {
            var l = new CommsPanelLogic();
            l.Begin(12);
            l.Tick(CommsPanelLogic.InSec);
            l.Tick(l.TypeSec);
            Assert.AreEqual(CommsStage.Hold, l.Stage);
            Assert.Greater(l.Weights.panel, 0f);

            l.Begin(12, CommsDelivery.Possessed);
            Assert.AreEqual(CommsStage.Intrusion, l.Stage);
            Assert.AreEqual(0f, l.Weights.panel, 1e-6f);
            Assert.AreEqual(0f, l.Weights.glyph, 1e-6f);
            l.Begin(4, CommsDelivery.Typed);
            Assert.AreEqual(CommsStage.Intrusion, l.Stage, "後続通知で乗っ取りを中断しない");
            Assert.AreEqual(CommsDelivery.Possessed, l.Delivery);
        }

        [Test]
        public void TypedNotice_HasNoSweep_AndKeepsTheRamp()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.6f);
            l.Begin(12, CommsDelivery.Typed);
            for (int i = 0; i < 90; i++)
            {
                l.Tick(Dt);
                Assert.AreEqual(0f, l.Weights.sweep, 1e-6f);
                Assert.AreEqual(0f, l.Weights.tear, 1e-6f, "打つ出し方は乱れない");
                Assert.AreEqual(CommsPossessionPhase.Off, l.PossessionSample.phase);
            }
            Assert.AreEqual(0.6f, l.Weights.curse, 1e-4f, "打つ出し方は 0229 の 1 秒の立ち上がりのまま");
        }

        [Test]
        public void CursedNotice_StartsAsTheDoll_FadesTheText_ThenUsesTheNormalHold()
        {
            var l = new CommsPanelLogic();
            l.Begin(12, CommsDelivery.Cursed);
            Assert.AreEqual(CommsPossessionLogic.ShowSec, l.TypeSec, 1e-6f);
            Assert.AreEqual(1f, l.Weights.curse, 1e-6f, "開き始めから人形");
            Assert.AreEqual(1f, l.Weights.sweep, 1e-6f, "通常の面が一瞬も混ざらない");

            l.Tick(CommsPanelLogic.InSec);
            Assert.AreEqual(CommsStage.Type, l.Stage);
            Assert.AreEqual(1f, l.Weights.reveal, 1e-6f, "本文は 1 字ずつ打たない");
            Assert.AreEqual(0f, l.Weights.glyph, 1e-6f);
            Assert.AreEqual(1f, l.Weights.curse, 1e-6f);
            Assert.AreEqual(1f, l.Weights.sweep, 1e-6f);

            l.Tick(CommsPossessionLogic.ShowSec);
            Assert.AreEqual(CommsStage.Hold, l.Stage);
            Assert.AreEqual(1f, l.Weights.glyph, 1e-6f);
            Assert.AreEqual(CommsPanelLogic.HoldSec,
                CommsPanelLogic.HoldSecFor(CommsDelivery.Cursed), 1e-6f);
            l.Tick(CommsPanelLogic.HoldSec + Dt);
            Assert.AreEqual(CommsStage.Out, l.Stage);
        }

        [Test]
        public void HoldingReport_CannotInterruptPossession_OrReopenItAtTheEnd()
        {
            var l = new CommsPanelLogic();
            l.Begin(12, CommsDelivery.Possessed);
            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Intrusion, l.Stage, "侵入中も割り込まない");
            while (l.Stage != CommsStage.Hold)
            {
                l.SetGuideWanted(true);
                l.Tick(Dt);
                Assert.AreNotEqual(CommsStage.Guide, l.Stage,
                    "出る → 読ませる → 塗り替わる は途中で退かない");
            }
            Assert.AreEqual(CommsPossessionPhase.Cursed, l.PossessionSample.phase);
            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Hold, l.Stage, "最終の人形の 1 フレームも奪わない");
            l.Tick(CommsPanelLogic.PossessedHoldSec);
            Assert.AreEqual(CommsStage.Off, l.Stage, "押しっぱなしでも Guide へ戻さず同時消灯する");
            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Off, l.Stage, "押しっぱなしを新しい押下として開き直さない");
            l.SetGuideWanted(false);
            l.SetGuideWanted(true);
            Assert.AreEqual(CommsStage.Guide, l.Stage, "次の押し操作では開く");
        }

        [TestCase(CommsNotice.MarkLogged, 0.25f, CommsDelivery.Typed)]
        [TestCase(CommsNotice.MarkLogged, 0.75f, CommsDelivery.Cursed)]
        [TestCase(CommsNotice.MarkNothing, 1f, CommsDelivery.Cursed)]
        [TestCase(CommsNotice.MarkAnalyzing, 0.75f, CommsDelivery.Cursed)]
        [TestCase(CommsNotice.Takeover, 0f, CommsDelivery.Possessed)]
        [TestCase(CommsNotice.Takeover, 1f, CommsDelivery.Possessed)]
        [TestCase(CommsNotice.Halt, 1f, CommsDelivery.Fade)]
        [TestCase(CommsNotice.Prompt, 1f, CommsDelivery.Typed)]
        [TestCase(CommsNotice.Begin, 0f, CommsDelivery.Typed)]
        [TestCase(CommsNotice.Greeting, 0f, CommsDelivery.Typed)]
        [TestCase(CommsNotice.TutorialAccepted, 1f, CommsDelivery.Fade)]
        public void DeliveryFollowsTheInvasion(CommsNotice notice, float invasion, CommsDelivery expected)
        {
            Assert.AreEqual(expected, CommsCueLogic.DeliveryOf(notice, invasion));
        }

        [Test]
        public void PossessedLevel_IsTheChainedPovLevel()
        {
            Assert.AreEqual(CommsInvasionLogic.ChainedPovLevel, CommsCurseLogic.PossessedLevel);
            Assert.AreEqual(0.75f, CommsCurseLogic.PossessedLevel, 1e-6f, "0230「侵食度0.75以降」");
        }

        [Test]
        public void OrdinaryMissAndDetectionKeepTheirAnswers()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkNothing, logic.Tick(Input(mark: true)));
            Assert.AreEqual(CommsNotice.MarkLogged, logic.Tick(Input(mark: true, detected: true)));
        }

        [Test]
        public void ReportDuringTheLieDoesNotRestartIt()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.None,
                logic.Tick(Input(mark: true, detected: true, invasion: 1f, playing: true)));
        }

        [Test]
        public void DollCatchUpCompletion_FiresOnce_AndWaitsForDeliveryReceipt()
        {
            var logic = new CommsCueLogic();
            Assert.AreNotEqual(CommsNotice.Takeover,
                logic.Tick(Input(invasion: 1f, dollCatchUpCompletedCount: 0)),
                "追いつきの完了前は乗っ取らない");
            Assert.AreEqual(CommsNotice.Takeover,
                logic.Tick(Input(invasion: 1f, dollCatchUpCompletedCount: 1)),
                "追いつきが完了した同じフレームで乗っ取る");
            Assert.AreEqual(CommsNotice.Takeover,
                logic.Tick(Input(invasion: 1f, dollCatchUpCompletedCount: 1)),
                "実際に Deliver されるまでは候補を消費しない");
            logic.NotifyDelivered(CommsNotice.Takeover);
            Assert.AreNotEqual(CommsNotice.Takeover,
                logic.Tick(Input(invasion: 1f, dollCatchUpCompletedCount: 1)));
        }

        [Test]
        public void InvasionLevelAloneNeverFiresTakeover()
        {
            foreach (float invasion in new[] { 0f, 0.25f, 0.75f, 1f })
            {
                var logic = new CommsCueLogic();
                for (int i = 0; i < 30; i++)
                    Assert.AreNotEqual(CommsNotice.Takeover,
                        logic.Tick(Input(invasion: invasion)));
            }
        }

        [TestCase(0.25f)]
        [TestCase(0.75f)]
        public void DetectedReportBeforeFullInvasionIsOrdinary(float invasion)
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkLogged,
                logic.Tick(Input(mark: true, detected: true, invasion: invasion)));
        }

        [Test]
        public void ReportAtFullInvasionDoesNotSubstituteForTheCatchUpEdge()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.MarkLogged,
                logic.Tick(Input(mark: true, detected: true, invasion: 1f)));
        }

        [Test]
        public void AfterTheLieWasDelivered_ReportsAreOrdinaryAgain()
        {
            var logic = new CommsCueLogic();
            logic.NotifyDelivered(CommsNotice.Takeover);
            Assert.AreEqual(CommsNotice.MarkLogged,
                logic.Tick(Input(mark: true, detected: true, invasion: 1f)));
            Assert.AreEqual(CommsNotice.MarkAnalyzing,
                logic.Tick(Input(mark: true, invasion: 1f)),
                "嘘は 1 回だけ。その後の空振りの報告は判定を持たない一文（0232・乗っ取られた装置は正直に「異常なし」を言わない）");
        }

        [TestCase(0f, CommsNotice.MarkNothing)]
        [TestCase(0.25f, CommsNotice.MarkNothing)]
        [TestCase(0.75f, CommsNotice.MarkAnalyzing)]
        public void MissAnswersWithoutAVerdict_OnceThePanelIsPossessed(float invasion, CommsNotice expected)
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(expected, logic.Tick(Input(mark: true, invasion: invasion)));
        }

        [Test]
        public void HaltBoundaryDisablesTheLieButKeepsDetectionAnswer()
        {
            var logic = new CommsCueLogic();
            CommsCueInput input = Input(mark: true, detected: true, invasion: 1f);
            input.takeoverAllowed = false;
            Assert.AreEqual(CommsNotice.MarkLogged, logic.Tick(input));
        }

        [Test]
        public void DollSignalCanRiseAfterTheStepWasApplied()
        {
            Assert.IsFalse(TakeRunner.IsDollReplacementShowing(true, true, false),
                "人形の実体がまだ出ていない段階を表示済みにしない");
            Assert.IsTrue(TakeRunner.IsDollReplacementShowing(true, true, true),
                "次フレームで実体が出たら、カットを適用し直さなくても検出する");
            Assert.IsFalse(TakeRunner.IsDollReplacementShowing(false, true, true));
            Assert.IsFalse(TakeRunner.IsDollReplacementShowing(true, false, true));
        }

        [Test]
        public void DetectionRequiresAnActiveUnsuppressedTake()
        {
            Assert.IsTrue(TakeRunner.IsAnomalyShowing(true, false));
            Assert.IsFalse(TakeRunner.IsAnomalyShowing(false, false));
            Assert.IsFalse(TakeRunner.IsAnomalyShowing(true, true));
        }

        [Test]
        public void LeavingRunResetsTheOneShotForTheNextVisitor()
        {
            var logic = new CommsCueLogic();
            logic.NotifyDelivered(CommsNotice.Takeover);
            logic.Tick(new CommsCueInput { inRun = false, dt = 0.1f });
            Assert.AreNotEqual(CommsNotice.Takeover,
                logic.Tick(Input(invasion: 1f, dollCatchUpCompletedCount: 0)));
            Assert.AreEqual(CommsNotice.Takeover,
                logic.Tick(Input(invasion: 1f, dollCatchUpCompletedCount: 1)));
        }

        private static CommsCueInput Input(bool mark = false, bool detected = false,
            float invasion = 0f, bool playing = false, bool dollCatchUpShowing = false,
            int dollCatchUpCompletedCount = 0)
            => new CommsCueInput
        {
            inRun = true,
            closingSec = -1f,
            panelDoneReading = false,
            markPressed = mark,
            markDetected = detected,
            invasionProgress = invasion,
            takeoverPlaying = playing,
            takeoverAllowed = true,
            dollCatchUpShowing = dollCatchUpShowing,
            dollCatchUpCompletedCount = dollCatchUpCompletedCount,
            dt = 0.1f,
        };
    }
}
