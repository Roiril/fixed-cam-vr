#nullable enable
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
            Assert.AreEqual(1.35f, CommsPossessionLogic.ReadSecFor(12, ShowLang.Ja), 0.001f,
                "日本語 12 字 = 0.9 + 12/12 × 0.45");
            Assert.AreEqual(1.5f, CommsPossessionLogic.ReadSecFor(24, ShowLang.En), 0.001f,
                "English 24 字 = 0.9 + 24/18 × 0.45");
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
        public void SweepBands_CurseNothingAtZero_EverythingAtOne_AndDescendFromTheTop()
        {
            const float top = 0.185f, bottom = -0.009f;
            int bands = CommsCurseLogic.SweepBandCount(top, bottom);
            Assert.AreEqual(5, bands, "0.194m / 0.04m = 4.85 -> 5 帯（最後の帯は欠けていてよい）");
            for (float x = -0.7f; x <= 0.3f; x += 0.013f)
            for (float y = bottom - 0.05f; y <= top + 0.05f; y += 0.011f)
            {
                Assert.IsFalse(CommsCurseLogic.IsSwept(x, y, 0f, top, bottom), "進み 0 は 1 画素も呪われない");
                Assert.IsTrue(CommsCurseLogic.IsSwept(x, y, 1f, top, bottom), "進み 1 は全面");
            }
            // 進みとともに上の帯から順に反転し、一度反転した帯は戻らない。
            for (float x = -0.7f; x <= 0.3f; x += 0.013f)
            {
                int previous = 0;
                for (float p = 0f; p <= 1f; p += 0.05f)
                {
                    int swept = 0;
                    bool seenClean = false;
                    for (float y = top - 0.001f; y > bottom; y -= 0.004f)
                    {
                        bool cursed = CommsCurseLogic.IsSwept(x, y, p, top, bottom);
                        if (cursed)
                        {
                            Assert.IsFalse(seenClean, "呪われた行の下に通常の行が無い（前線は上から降りる）");
                            swept++;
                        }
                        else seenClean = true;
                    }
                    Assert.GreaterOrEqual(swept, previous, "前線は戻らない");
                    previous = swept;
                }
            }
        }

        [Test]
        public void SweepBands_AreSnappedToTheGrid_NotAContinuousFront()
        {
            const float top = 0.185f, bottom = -0.009f;
            Assert.AreEqual(0, CommsCurseLogic.SweepBandOf(top, top));
            Assert.AreEqual(0, CommsCurseLogic.SweepBandOf(top + 1f, top), "上端より上は最上段");
            Assert.AreEqual(1, CommsCurseLogic.SweepBandOf(top - CommsCurseLogic.TearBandM - 0.001f, top));
            // 同じ帯の中は x にも y にも依らず一斉に反転する（列ごとの段や斜めの前線を作らない）。
            int bands = CommsCurseLogic.SweepBandCount(top, bottom);
            for (int b = 0; b < bands; b++)
            {
                float y0 = top - b * CommsCurseLogic.TearBandM - 0.001f;
                float y1 = System.Math.Max(top - (b + 1) * CommsCurseLogic.TearBandM + 0.001f, bottom + 0.0005f);
                for (float p = 0.05f; p < 1f; p += 0.1f)
                {
                    bool reference = CommsCurseLogic.IsSwept(-0.7f, y0, p, top, bottom);
                    for (float x = -0.7f; x <= 0.3f; x += 0.05f)
                    {
                        Assert.AreEqual(reference, CommsCurseLogic.IsSwept(x, y0, p, top, bottom), $"帯 {b} の上端・x={x}");
                        Assert.AreEqual(reference, CommsCurseLogic.IsSwept(x, y1, p, top, bottom), $"帯 {b} の下端・x={x}");
                    }
                }
            }
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
        public void PossessedNotice_ShowsAllAtOnce_ThenSweeps_ThenHoldsCursed()
        {
            var l = new CommsPanelLogic();
            l.SetCurseTarget(0.6f);
            l.Begin(12, CommsDelivery.Possessed);
            float readSec = CommsPossessionLogic.ReadSecFor(12, ShowLanguage.Current);
            Assert.AreEqual(CommsPossessionLogic.DurationFor(readSec), l.TypeSec, 1e-5f);
            Assert.AreEqual(CommsPossessionPhase.Off, l.PossessionSample.phase, "開いている最中は Off");
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f);

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
            l.Tick(CommsPossessionLogic.TearReleaseSec + 0.01f);
            Assert.AreEqual(0f, l.Weights.tear, 1e-6f, "尾が引いたら呪われた面へ落ち着く");

            l.Tick(CommsPanelLogic.HoldSec + 0.001f);
            Assert.AreEqual(CommsStage.Out, l.Stage);
            Assert.AreEqual(1f, l.Weights.curse, 1e-6f, "引いている最中も呪われたまま");
            l.Tick(CommsPanelLogic.OutSec + 0.001f);
            Assert.AreEqual(CommsStage.Off, l.Stage);
            Assert.AreEqual(0f, l.Weights.curse, 1e-6f);
            Assert.AreEqual(CommsPossessionPhase.Off, l.PossessionSample.phase);
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
        public void HoldingReport_CannotInterruptTheSequence_ButFollowsIt()
        {
            var l = new CommsPanelLogic();
            l.Begin(12, CommsDelivery.Possessed);
            float duration = CommsPanelLogic.InSec + l.TypeSec;
            float t = 0f;
            while (t < duration + Dt)
            {
                l.SetGuideWanted(true);
                l.Tick(Dt);
                t += Dt;
                if (l.Stage == CommsStage.Type)
                    Assert.AreNotEqual(CommsStage.Guide, l.Stage, "出る → 読ませる → 塗り替わる は途中で退かない");
            }
            Assert.AreEqual(CommsStage.Hold, l.Stage);
            Assert.AreEqual(CommsPossessionPhase.Cursed, l.PossessionSample.phase);
            // 押しっぱなしなら、読ませ終わった縁で報告の手元表示へ移る（呪われたまま）。
            l.Tick(CommsPanelLogic.HoldSec + 0.001f);
            Assert.AreEqual(CommsStage.Guide, l.Stage);
            Assert.AreEqual(1f, l.Weights.curse, 1e-6f);
        }

        [TestCase(CommsNotice.MarkLogged, 0.25f, CommsDelivery.Typed)]
        [TestCase(CommsNotice.MarkLogged, 0.75f, CommsDelivery.Possessed)]
        [TestCase(CommsNotice.MarkNothing, 1f, CommsDelivery.Possessed)]
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
        public void FullInvasion_AutoFiresOnlyAfterItsDelay()
        {
            var logic = new CommsCueLogic();
            for (float t = 0f; t < 2f; t += 0.1f)
                Assert.AreNotEqual(CommsNotice.Takeover, logic.Tick(Input(invasion: 0.75f)));
            for (float t = 0f; t < CommsPossessionLogic.AutoDelaySec - 0.1f; t += 0.1f)
                Assert.AreNotEqual(CommsNotice.Takeover, logic.Tick(Input(invasion: 1f)));
            Assert.AreEqual(CommsNotice.Takeover, logic.Tick(Input(invasion: 1f)));
            Assert.AreEqual(CommsNotice.Takeover, logic.Tick(Input(invasion: 1f)),
                "実際に Deliver されるまでは one-shot を消費しない");
            logic.NotifyDelivered(CommsNotice.Takeover);
            Assert.AreNotEqual(CommsNotice.Takeover, logic.Tick(Input(invasion: 1f)));
        }

        [Test]
        public void PartialInvasionNeverAutoFires()
        {
            foreach (float invasion in new[] { 0f, 0.25f, 0.75f })
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
        public void ReportAtFullInvasionFiresImmediately()
        {
            var logic = new CommsCueLogic();
            Assert.AreEqual(CommsNotice.Takeover,
                logic.Tick(Input(mark: true, detected: true, invasion: 1f)));
        }

        [Test]
        public void AfterTheLieWasDelivered_ReportsAreOrdinaryAgain()
        {
            var logic = new CommsCueLogic();
            logic.NotifyDelivered(CommsNotice.Takeover);
            Assert.AreEqual(CommsNotice.MarkLogged,
                logic.Tick(Input(mark: true, detected: true, invasion: 1f)));
            Assert.AreEqual(CommsNotice.MarkNothing,
                logic.Tick(Input(mark: true, invasion: 1f)),
                "嘘は 1 回だけ。その後の報告は普通の返事（出し方は憑依のまま）");
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
            for (int i = 0; i < 6; i++) logic.Tick(Input(invasion: 1f));
            Assert.AreEqual(CommsNotice.Takeover, logic.Tick(Input(invasion: 1f)));
        }

        private static CommsCueInput Input(bool mark = false, bool detected = false,
            float invasion = 0f, bool playing = false) => new CommsCueInput
        {
            inRun = true,
            panelDoneReading = false,
            markPressed = mark,
            markDetected = detected,
            invasionProgress = invasion,
            takeoverPlaying = playing,
            takeoverAllowed = true,
            dt = 0.1f,
        };
    }
}
