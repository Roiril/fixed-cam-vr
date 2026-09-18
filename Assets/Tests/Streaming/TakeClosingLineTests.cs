#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 締めの線（3 周目 A の凍結点）を、締めのカットの中で踏んだことの記録
    /// （<see cref="TakeRunnerLogic.ClosingLineCrossed"/>・2026-09-19・<c>canon/LEDGER.md</c> 0233）。
    /// 4 周目 A の③a「止まってください！」の引き金で、読むのは <c>CommsCueLogic</c>。
    ///
    /// ⚠ ここで押さえたい壊れ方は 3 つ。どれも画を見ても気づけない:
    /// ① 締めに入る前（3 周目 A）に踏んだ線が締めの中で効く（③a が入った瞬間に出る）
    /// ② 締めのカット以外で立つ（1〜3 周目の演出中に「止まってください！」）
    /// ③ 2 人目に前の体験者の横断が残る
    /// </summary>
    public sealed class TakeClosingLineTests
    {
        private const int Slot = 0;

        // 締めのカット: 4 秒の動画 → 報告待ち → 3 秒（4 周目 A の形）。
        private static TakeRunnerLogic.Def ClosingTake(int lap = 4, int cam = 0) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = 0f,
            stepDurSec = new[] { 4f, TakeRunnerLogic.WaitMark, 3f },
        };

        // 締めではない演出（線待ち → 2 秒）。3 周目 A の形。
        private static TakeRunnerLogic.Def FreezeTake(int lap = 3, int cam = 0) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = 0f,
            stepDurSec = new[] { TakeRunnerLogic.WaitLine, 2f },
            stepLineIndex = new[] { Slot, -1 },
        };

        private static LineCrossLogic.State[] Line(float crossedAtSec, int camera = 0)
            => new[] { new LineCrossLogic.State { crossed = true, crossedAtSec = crossedAtSec, camera = camera } };

        private static LineCrossLogic.State[] NeverCrossed(int camera = 0)
            => new[] { new LineCrossLogic.State
                       { crossed = false, crossedAtSec = float.NegativeInfinity, camera = camera } };

        private static TakeRunnerLogic Make(params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            l.SetClosingLine(Slot);
            return l;
        }

        // 締めのカットへ入った状態を作る。
        private static TakeRunnerLogic StartedClosing(float now = 10f)
        {
            TakeRunnerLogic l = Make(ClosingTake());
            l.OnZoneCommitted(4, 0, false, 0, 0, now);
            Assert.That(l.Tick(now, 0, NeverCrossed()).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(l.ActiveTakeWaitsForMark, Is.True);
            return l;
        }

        [Test]
        public void NotCrossed_UntilTheVisitorStepsOnTheLine()
        {
            TakeRunnerLogic l = StartedClosing();
            for (float t = 11f; t < 20f; t += 2f)
            {
                l.Tick(t, 0, NeverCrossed());
                Assert.That(l.ClosingLineCrossed, Is.False, $"踏んでいないのに立った (t={t})");
            }
            Assert.That(l.ClosingLineCrossedSec, Is.LessThan(0f));
        }

        [Test]
        public void Crossed_WhenTheLineIsSteppedOn_InsideTheClosingTake()
        {
            TakeRunnerLogic l = StartedClosing(now: 10f);
            l.Tick(12f, 0, NeverCrossed());
            l.Tick(13f, 0, Line(crossedAtSec: 13f));
            Assert.That(l.ClosingLineCrossed, Is.True);
            Assert.That(l.ClosingLineCrossedSec, Is.EqualTo(3f).Within(1e-3f), "締めに入ってからの秒で出す");
        }

        /// <summary>
        /// 記録は<b>締めのあいだ立ちっぱなし</b>。連絡の面が塞がっていて③a をすぐ出せなくても消えない
        /// （0178「止まってください！以降の流れは全員に見せる」）。カットが進んでも（報告して現実へ戻っても）残る。
        /// </summary>
        [Test]
        public void TheRecord_Stays_ForTheRestOfTheClosingTake()
        {
            TakeRunnerLogic l = StartedClosing(now: 0f);
            l.Tick(2f, 0, Line(crossedAtSec: 2f));
            Assert.That(l.ClosingLineCrossed, Is.True);

            l.Tick(9f, 0, NeverCrossed());
            Assert.That(l.ClosingLineCrossed, Is.True, "横断の記録が事象と一緒に消えた");

            // 報告でカットが進んでも（現実へ戻る 3 秒のあいだも）残る。
            l.NotifyMarkPressed(9f);
            Assert.That(l.Tick(9f, 0, NeverCrossed()).stepIndex, Is.EqualTo(2));
            Assert.That(l.ClosingLineCrossed, Is.True);
            Assert.That(l.ClosingLineCrossedSec, Is.EqualTo(2f).Within(1e-3f), "最初の 1 回を保つ");
        }

        /// <summary>締めのカットが終われば下りる（終幕に「止まってください！」を持ち込まない）。</summary>
        [Test]
        public void TheRecord_Drops_WhenTheClosingTakeEnds()
        {
            TakeRunnerLogic l = StartedClosing(now: 0f);
            l.Tick(2f, 0, Line(crossedAtSec: 2f));
            Assert.That(l.Tick(5f, 0, NeverCrossed()).stepIndex, Is.EqualTo(1), "動画が終わって報告待ちへ");
            l.NotifyMarkPressed(9f);
            Assert.That(l.Tick(9f, 0, NeverCrossed()).stepIndex, Is.EqualTo(2), "報告で現実へ戻る 3 秒へ");
            Assert.That(l.ClosingLineCrossed, Is.True, "現実へ戻るあいだも記録は残る");
            TakeRunnerLogic.Decision d = l.Tick(12.1f, 0, NeverCrossed());
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(l.ClosingLineCrossed, Is.False);
        }

        /// <summary>
        /// ⚠⚠ <b>締めに入る直前の横断は数える</b>（at=line の武装と同じ猶予 <see cref="LineCrossLogic.CrossLatchSec"/>）。
        /// 線は区間の入口寄りにあり、ゾーン確定の dwell（0.5 秒）のあいだに踏む人が居る。
        /// 線待ちのカット（<c>untilLine</c>）が始まる前の横断を捨てるのは「同じ区間に線が 2 本ある」ためで、
        /// ここにその問題は無い。
        /// </summary>
        [Test]
        public void ACrossingJustBeforeTheTakeBegan_Counts()
        {
            TakeRunnerLogic l = Make(ClosingTake());
            l.OnZoneCommitted(4, 0, false, 0, 0, 10f);
            // 締めに入る 0.4 秒前に踏んでいた（猶予 0.6 秒の内側）。
            l.Tick(10f, 0, Line(crossedAtSec: 9.6f));
            Assert.That(l.ClosingLineCrossed, Is.True, "入り際の横断が捨てられた");
            Assert.That(l.ClosingLineCrossedSec, Is.EqualTo(-0.4f).Within(1e-3f));
        }

        /// <summary>猶予より前の横断は数えない（3 周目 A で踏んだ線が 4 周目 A の入った瞬間に効かない）。</summary>
        [Test]
        public void ACrossingLongBeforeTheTakeBegan_DoesNotCount()
        {
            TakeRunnerLogic l = Make(ClosingTake());
            l.OnZoneCommitted(4, 0, false, 0, 0, 10f);
            l.Tick(10f, 0, Line(crossedAtSec: 9f));
            Assert.That(l.ClosingLineCrossed, Is.False, "1 秒前の横断が締めの中で効いた");
        }

        /// <summary>古い記録では立たない（事象なので直近のフレームでしか読まない）。</summary>
        [Test]
        public void AStaleCrossing_DoesNotCount()
        {
            TakeRunnerLogic l = StartedClosing(now: 10f);
            l.Tick(20f, 0, Line(crossedAtSec: 15f));
            Assert.That(l.ClosingLineCrossed, Is.False);
        }

        /// <summary>線は担当カメラに紐づく（演出の <c>at:"line"</c>・カットの <c>untilLine</c> と同じ規約）。</summary>
        [Test]
        public void ALineOfAnotherCamera_DoesNotCount()
        {
            TakeRunnerLogic l = StartedClosing(now: 10f);
            l.Tick(12f, 0, Line(crossedAtSec: 12f, camera: 2));
            Assert.That(l.ClosingLineCrossed, Is.False, "別の区間の線で立った");
            l.Tick(13f, 0, Line(crossedAtSec: 13f, camera: 0));
            Assert.That(l.ClosingLineCrossed, Is.True);
        }

        /// <summary>
        /// ⚠⚠ <b>締めのカット以外では立たない。</b> 3 周目 A は同じ線で凍るが、そこで
        /// 「止まってください！」が出たら台本が壊れる。
        /// </summary>
        [Test]
        public void OtherTakes_NeverRecordTheClosingLine()
        {
            TakeRunnerLogic l = Make(FreezeTake());
            l.OnZoneCommitted(3, 0, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 0, NeverCrossed()).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(l.ActiveTakeWaitsForMark, Is.False);

            TakeRunnerLogic.Decision d = l.Tick(3f, 0, Line(crossedAtSec: 3f));
            Assert.That(d.stepIndex, Is.EqualTo(1), "線待ちのカットは進む（凍る）");
            Assert.That(l.ClosingLineCrossed, Is.False, "締めではないのに立った");
        }

        /// <summary>締めの線が無い（-1）なら決して立たない。③a は時計の退避路で出る。</summary>
        [Test]
        public void WithoutAClosingLine_NothingIsRecorded()
        {
            TakeRunnerLogic l = Make(ClosingTake());
            l.SetClosingLine(-1);
            l.OnZoneCommitted(4, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, NeverCrossed());
            l.Tick(2f, 0, Line(crossedAtSec: 2f));
            Assert.That(l.ClosingLineCrossed, Is.False);
        }

        /// <summary>位置が取れない環境（未登録・HMD 参照なし）では立たない（線待ちと同じ）。</summary>
        [Test]
        public void WithoutPosition_NothingIsRecorded()
        {
            TakeRunnerLogic l = StartedClosing(now: 0f);
            l.Tick(2f, 0, null);
            Assert.That(l.ClosingLineCrossed, Is.False);
        }

        /// <summary>2 人目に前の体験者の横断を持ち越さない。</summary>
        [Test]
        public void TheSecondVisitor_StartsWithoutARecord()
        {
            TakeRunnerLogic l = StartedClosing(now: 0f);
            l.Tick(2f, 0, Line(crossedAtSec: 2f));
            Assert.That(l.ClosingLineCrossed, Is.True);

            l.ResetRun();
            Assert.That(l.ClosingLineCrossed, Is.False);

            l.OnZoneCommitted(4, 0, false, 0, 0, 100f);
            l.Tick(100f, 0, NeverCrossed());
            Assert.That(l.ClosingLineCrossed, Is.False, "前の体験者の横断が残っていた");
            l.Tick(101f, 0, Line(crossedAtSec: 101f));
            Assert.That(l.ClosingLineCrossed, Is.True);
        }

        /// <summary>
        /// layout は走行中にも届く（卓が線を動かす）。同じ線のままなら締めの中の記録を落とさない —
        /// 面が塞がっていて③a がまだ出ていない瞬間に落とすと、その 1 通が黙って消える。
        /// </summary>
        [Test]
        public void ReapplyingTheSameLine_KeepsTheRecord()
        {
            TakeRunnerLogic l = StartedClosing(now: 0f);
            l.Tick(2f, 0, Line(crossedAtSec: 2f));
            l.SetClosingLine(Slot);
            Assert.That(l.ClosingLineCrossed, Is.True, "同じ線を貼り直しただけで記録が落ちた");
            l.SetClosingLine(Slot + 1);
            Assert.That(l.ClosingLineCrossed, Is.False, "別の線へ替わったのに記録が残った");
        }

        // ---------------------------------------------------- 締めの線を台本から導く（TakeSchema）

        private static ShowStepDef Step(string durKind = "sec", string lineId = "", bool freeze = false)
            => new() { durKind = durKind, lineId = lineId, splitFreeze = freeze, durSec = 1f };

        private static ShowTakeDef TakeOf(params ShowStepDef[] steps) => new() { id = "t", steps = steps };

        /// <summary>
        /// 締めの線 ＝ <c>splitFreeze</c> のカットの直前まで待っていた <c>untilLine</c> の線（3 周目 A の形）。
        /// show.json に別の口を作らないので、卓で凍結線を据え直せば「止まれ」の場所も一緒に動く。
        /// </summary>
        [Test]
        public void TheClosingLine_IsTheLineTheMirrorWaitedFor_BeforeItFroze()
        {
            ShowTakeDef mirror = TakeOf(
                Step(durKind: TakeSchema.DurUntilLine, lineId: "line_freeze"),
                Step(freeze: true),
                Step(durKind: TakeSchema.DurUntilClipEnd));
            Assert.That(TakeSchema.ResolveClosingLineId(new[] { mirror }), Is.EqualTo("line_freeze"));
        }

        /// <summary>凍るカットと線待ちのあいだにカットが挟まっても、最後に待っていた線を採る。</summary>
        [Test]
        public void TheClosingLine_IsTheLastLineWaitedFor_BeforeTheFreeze()
        {
            ShowTakeDef t = TakeOf(
                Step(durKind: TakeSchema.DurUntilLine, lineId: "line_a"),
                Step(),
                Step(durKind: TakeSchema.DurUntilLine, lineId: "line_b"),
                Step(),
                Step(freeze: true));
            Assert.That(TakeSchema.ResolveClosingLineId(new[] { t }), Is.EqualTo("line_b"));
        }

        /// <summary>線を待たずに凍る台本・凍らない台本では空（③a は時計の退避路で出る）。</summary>
        [Test]
        public void NoClosingLine_WhenNothingFreezesAfterALine()
        {
            Assert.That(TakeSchema.ResolveClosingLineId(new[] { TakeOf(Step(freeze: true)) }), Is.EqualTo(""),
                        "線を待たずに凍るカットから線が出た");
            Assert.That(TakeSchema.ResolveClosingLineId(new[] { TakeOf(Step(durKind: TakeSchema.DurUntilLine, lineId: "x")) }),
                        Is.EqualTo(""), "凍らないのに線が出た");
            Assert.That(TakeSchema.ResolveClosingLineId(new[] { TakeOf(Step(freeze: true), Step(durKind: TakeSchema.DurUntilLine, lineId: "x")) }),
                        Is.EqualTo(""), "凍った後の線を採った");
            Assert.That(TakeSchema.ResolveClosingLineId(null), Is.EqualTo(""));
            Assert.That(TakeSchema.ResolveClosingLineId(new ShowTakeDef?[] { null }), Is.EqualTo(""));
        }

        /// <summary>線待ちに id が無いカット（発火しない枠）は線として数えない。</summary>
        [Test]
        public void AnEmptyLineId_IsNotAClosingLine()
        {
            ShowTakeDef t = TakeOf(Step(durKind: TakeSchema.DurUntilLine, lineId: ""), Step(freeze: true));
            Assert.That(TakeSchema.ResolveClosingLineId(new[] { t }), Is.EqualTo(""));
        }
    }
}
