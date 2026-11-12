#nullable enable
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// カットの尺「入ってきた側から見て、この床の線の向こうに居るまで」
    /// （<c>durKind:"untilBeyond"</c> ＝ <see cref="TakeRunnerLogic.WaitBeyond"/>）。
    ///
    /// <b>なぜ要るか</b>: <c>untilLine</c> は「カットが始まった後の横断」という事象で終わる。秒で終わる動画のカットと
    /// 線待ちを交互に並べると、動画の最中に次の線を越えた人は次のカットが永久に終わらない
    /// （2026-11-12 のツイン走行 <c>20261112_232756_posdrv-trail</c> で区間の終わりまで止まった）。
    /// こちらは状態で見るので、始まる前に越えていても最初の Tick で終わる。
    /// </summary>
    public sealed class TakeStepUntilBeyondTests
    {
        private const int Slot = 0;

        // カット 0 が 2 秒の動画、カット 1 が線待ち（wait）、カット 2 が 2 秒。
        private static TakeRunnerLogic.Def VideoThenWait(float wait, int lap = 2, int cam = 0) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = 0f,
            stepDurSec = new[] { 2f, wait, 2f },
            stepLineIndex = new[] { -1, Slot, -1 },
        };

        // カット 0 が側待ち、カット 1 が 2 秒。
        private static TakeRunnerLogic.Def BeyondFirst(int lap = 2, int cam = 0) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = 0f,
            stepDurSec = new[] { TakeRunnerLogic.WaitBeyond, 2f },
            stepLineIndex = new[] { Slot, -1 },
        };

        private static TakeRunnerLogic Make(params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            return l;
        }

        private static LineSideLogic.State[] Side(bool beyond, int camera = 0)
            => new[] { new LineSideLogic.State
                       { beyond = beyond, entrySide = 1, side = beyond ? -1 : 1, camera = camera } };

        private static LineCrossLogic.State[] CrossedAt(float sec, int camera = 0)
            => new[] { new LineCrossLogic.State { crossed = false, crossedAtSec = sec, camera = camera } };

        /// <summary>
        /// ⚠⚠ <b>動画の最中に線を越えた人</b>（カットが始まる前の越え）。untilBeyond は待ちのカットの最初の Tick で終わり、
        /// 同じ状況の untilLine は終わらない（区間を出るまで止まる ＝ 2026-11-12 の実害）。
        /// </summary>
        [Test]
        public void CrossedDuringThePreviousCut_UntilBeyondEndsOnItsFirstTick_UntilLineDoesNot()
        {
            // 体験者は t=1 に線を越え、それ以降ずっと向こう側に居る。
            LineCrossLogic.State[] cross = CrossedAt(1f);
            LineSideLogic.State[] beyond = Side(true);

            TakeRunnerLogic b = Make(VideoThenWait(TakeRunnerLogic.WaitBeyond));
            TakeRunnerLogic u = Make(VideoThenWait(TakeRunnerLogic.WaitLine));
            foreach (TakeRunnerLogic l in new[] { b, u })
            {
                l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
                Assert.That(l.Tick(0f, 0, cross, false, beyond).stepIndex, Is.EqualTo(0));
                Assert.That(l.Tick(1f, 0, cross, false, beyond).action, Is.EqualTo(TakeRunnerLogic.Action.None));
                TakeRunnerLogic.Decision w = l.Tick(2f, 0, cross, false, beyond);
                Assert.That(w.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
                Assert.That(w.stepIndex, Is.EqualTo(1), "動画の後に待ちのカットが始まる");
            }

            TakeRunnerLogic.Decision next = b.Tick(2.02f, 0, cross, false, beyond);
            Assert.That(next.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "untilBeyond: 既に向こう側に居るのに、待ちのカットが最初の Tick で終わらない");
            Assert.That(next.stepIndex, Is.EqualTo(2));

            // 対照: untilLine は始まる前の横断を捨てるので、向こう側に居ても進まない。
            for (float t = 2.02f; t <= 30f; t += 4f)
                Assert.That(u.Tick(t, 0, cross, false, beyond).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"untilLine の挙動が変わった (t={t})");
            Assert.That(u.ActiveStepIndex, Is.EqualTo(1));
        }

        /// <summary>演出の 1 カット目が側待ちでも、区間へ入る前に越えていれば最初の Tick で終わる。</summary>
        [Test]
        public void FirstStepOfTheTake_EndsOnTheNextTick_WhenAlreadyBeyond()
        {
            TakeRunnerLogic l = Make(BeyondFirst());
            l.OnZoneCommitted(2, 0, false, 0, 0, 10f);
            Assert.That(l.Tick(10f, 0, null, false, Side(true)).stepIndex, Is.EqualTo(0));
            TakeRunnerLogic.Decision d = l.Tick(10.02f, 0, null, false, Side(true));
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.stepIndex, Is.EqualTo(1));
            Assert.That(d.takeStarted, Is.False, "演出は続いている（画面の占有は保つ）");
        }

        [Test]
        public void WaitsWhileNotBeyond_ThenAdvancesWhenBeyond()
        {
            TakeRunnerLogic l = Make(BeyondFirst());
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, null, false, Side(false));
            for (float t = 1f; t <= 30f; t += 5f)
                Assert.That(l.Tick(t, 0, null, false, Side(false)).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"手前に居るのに進んだ (t={t})");
            Assert.That(l.IsWaitingForLine(Slot), Is.True, "側待ちを線待ちとして報告しない（自動走行が踏みに行けない）");

            Assert.That(l.Tick(31f, 0, null, false, Side(true)).stepIndex, Is.EqualTo(1));
        }

        /// <summary>線は担当カメラに紐づく（untilLine・at:"line" と同じ規約）。未指定（-1）はどの区間でも効く。</summary>
        [Test]
        public void IgnoresLineOfAnotherCamera()
        {
            TakeRunnerLogic l = Make(BeyondFirst(cam: 0));
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, null, false, Side(true, camera: 2));
            for (float t = 1f; t <= 10f; t += 3f)
                Assert.That(l.Tick(t, 0, null, false, Side(true, camera: 2)).action,
                    Is.EqualTo(TakeRunnerLogic.Action.None), $"別の区間の線で進んだ (t={t})");

            Assert.That(l.Tick(11f, 0, null, false, Side(true, camera: -1)).stepIndex, Is.EqualTo(1),
                "担当カメラ未指定の線で進まない");
        }

        /// <summary>untilLine 用の横断（lines）では進まない — 側待ちが読むのは sides だけ。</summary>
        [Test]
        public void CrossingAloneDoesNotEndABeyondStep()
        {
            TakeRunnerLogic l = Make(BeyondFirst());
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, null, false, Side(false));
            Assert.That(l.Tick(1f, 0, CrossedAt(1f), false, Side(false)).action,
                Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        /// <summary>側が渡されない（線を持つ演出が無い・線が使えない）なら進まず、watchdog が畳む。</summary>
        [Test]
        public void WithoutSides_DoesNotAdvance_AndWatchdogStillEndsIt()
        {
            TakeRunnerLogic.Def def = BeyondFirst();
            def.maxDurationSec = 5f;
            TakeRunnerLogic l = Make(def);
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0);
            Assert.That(l.Tick(3f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));

            TakeRunnerLogic.Decision d = l.Tick(5.1f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.forced, Is.True);
        }

        /// <summary>slot が線の数を超える（線の枠が無い）なら待ち続ける（untilLine と同じ）。</summary>
        [Test]
        public void SlotOutOfRange_DoesNotAdvance()
        {
            TakeRunnerLogic.Def def = BeyondFirst();
            def.stepLineIndex = new[] { 3, -1 };
            TakeRunnerLogic l = Make(def);
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, null, false, Side(true));
            Assert.That(l.Tick(1f, 0, null, false, Side(true)).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        /// <summary>policy=yield なら untilLine と同じく、区間を移れば畳む。</summary>
        [Test]
        public void YieldOnZoneChange_StillApplies()
        {
            TakeRunnerLogic.Def def = BeyondFirst();
            def.yieldOnZoneChange = true;
            TakeRunnerLogic l = Make(def);
            l.OnZoneCommitted(2, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, null, false, Side(false));

            TakeRunnerLogic.Decision d = l.OnZoneCommitted(2, 1, true, 2, 0, 3f, forward: true);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
        }

        // ---- 尺の組み立て（TakeRunner.BuildStepDurations / BuildStepLineIndices）----

        private static float[] BuildDurations(ShowTakeDef take)
        {
            MethodInfo? m = typeof(TakeRunner).GetMethod("BuildStepDurations",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(m, Is.Not.Null, "TakeRunner.BuildStepDurations が見つからない");
            return (float[])m!.Invoke(null, new object[] { take })!;
        }

        private static ShowTakeDef TakeOf(params ShowStepDef[] steps) => new() { id = "t", steps = steps };

        /// <summary>線が指定されていれば側待ちになり、無ければ既定尺で畳む（untilLine と同じ）。</summary>
        [Test]
        public void Durations_UntilBeyondWithLine_WaitsBeyond_WithoutLine_FallsBackToDefault()
        {
            float[] d = BuildDurations(TakeOf(
                new ShowStepDef { durKind = TakeSchema.DurUntilBeyond, lineId = "line_a" },
                new ShowStepDef { durKind = TakeSchema.DurUntilBeyond, lineId = "" },
                new ShowStepDef { durKind = TakeSchema.DurUntilBeyond, lineId = "", durSec = 3f },
                new ShowStepDef { durKind = TakeSchema.DurUntilLine, lineId = "line_a" }));

            Assert.That(d[0], Is.EqualTo(TakeRunnerLogic.WaitBeyond));
            Assert.That(d[1], Is.EqualTo(TakeSchema.FallbackStepDurSec), "線の無い側待ちが既定尺にならない");
            Assert.That(d[2], Is.EqualTo(3f), "線の無い側待ちで durSec を使わない");
            Assert.That(d[3], Is.EqualTo(TakeRunnerLogic.WaitLine), "untilLine の組み立てが変わった");
        }

        [Test]
        public void Schema_UntilBeyondIsALineWait()
        {
            Assert.That(TakeSchema.IsUntilBeyond("untilBeyond"), Is.True);
            Assert.That(TakeSchema.IsLineWait(TakeSchema.DurUntilBeyond), Is.True);
            Assert.That(TakeSchema.IsLineWait(TakeSchema.DurUntilLine), Is.True);
            Assert.That(TakeSchema.IsLineWait(TakeSchema.DurUntilZoneChange), Is.False);
            Assert.That(TakeSchema.IsUntilLine(TakeSchema.DurUntilBeyond), Is.False, "untilLine と混ざった");
            Assert.That(new ShowStepDef { durKind = TakeSchema.DurUntilBeyond }.IsLineWait, Is.True);
        }

        /// <summary>
        /// 実物の側と横断を通した通し: 秒の動画の最中に線を越えた人で、側待ちが終わる
        /// （<see cref="LineSideLogic"/> と <see cref="TakeRunnerLogic"/> を組んだ形。TakeRunner.Update と同じ順）。
        /// </summary>
        [Test]
        public void EndToEnd_WithRealLineSideLogic()
        {
            var line = LineCrossLogic.Line.Between(-1f, 0f, 1f, 0f, 0, 0);
            var side = new LineSideLogic();
            side.SetLines(new[] { line });
            TakeRunnerLogic l = Make(VideoThenWait(TakeRunnerLogic.WaitBeyond));

            float t = 0f;
            float z = -2f;
            // 区間の手前から歩いてきて、0.5 秒の dwell の後に確定。
            for (; z < -1f; z += 0.05f, t += 0.05f) side.Tick(0f, z);
            side.CommitEntry();
            l.OnZoneCommitted(2, 0, false, 0, 0, t);
            Assert.That(l.Tick(t, 0, null, false, side.StateView).stepIndex, Is.EqualTo(0));

            // 動画（2 秒）の最中に線を越える。
            int lastStep = 0;
            float end = t + 3f;
            for (; t < end; t += 0.05f, z += 0.05f)
            {
                side.Tick(0f, z);
                TakeRunnerLogic.Decision d = l.Tick(t, 0, null, false, side.StateView);
                if (d.action == TakeRunnerLogic.Action.BeginStep) lastStep = d.stepIndex;
            }
            Assert.That(side.Beyond(0), Is.True, "前提: 越えている");
            Assert.That(lastStep, Is.EqualTo(2), "動画の最中に越えた人の側待ちが終わらない");
        }
    }
}
