#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// カットの尺「この床の線を横切るまで」（<c>durKind:"untilLine"</c> ＝ <see cref="TakeRunnerLogic.WaitLine"/>）。
    ///
    /// <b>なぜカット側に要るか</b>: 走行中の演出へ別の演出は割り込めない（武装は Ready で待つ）。
    /// 3 周目 A は「区間へ入った時から画を割っておき、<b>線を越えた瞬間に</b>左半分を凍らせる」なので、
    /// 演出の開始規則（<c>at:"line"</c>）では書けない — 前半の演出が終わるまで後半が始まらないため、
    /// 待っているあいだ画が素へ戻ってしまう（canon/LEDGER.md 0050）。
    /// </summary>
    public sealed class TakeStepUntilLineTests
    {
        private const int Slot = 0;

        // カット 0 が線待ち、カット 1 が 2 秒の演出。
        private static TakeRunnerLogic.Def LineStep(int lap, int cam, int slot) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = 0f,
            stepDurSec = new[] { TakeRunnerLogic.WaitLine, 2f },
            stepLineIndex = new[] { slot, -1 },
        };

        private static TakeRunnerLogic Make(params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            return l;
        }

        private static LineCrossLogic.State[] Line(float crossedAtSec, int camera = 0)
            => new[] { new LineCrossLogic.State { crossed = true, crossedAtSec = crossedAtSec, camera = camera } };

        private static LineCrossLogic.State[] NeverCrossed(int camera = 0)
            => new[] { new LineCrossLogic.State
                       { crossed = false, crossedAtSec = float.NegativeInfinity, camera = camera } };

        // 区間へ入って 1 カット目を走らせた状態を作る。
        private static TakeRunnerLogic Started(out TakeRunnerLogic.Decision begun, float now = 0f)
        {
            TakeRunnerLogic l = Make(LineStep(3, 0, Slot));
            l.OnZoneCommitted(3, 0, false, 0, 0, now);
            begun = l.Tick(now, 0, NeverCrossed());
            Assert.That(begun.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(begun.stepIndex, Is.EqualTo(0));
            return l;
        }

        [Test]
        public void UntilLine_DoesNotAdvance_WhileNotCrossed()
        {
            TakeRunnerLogic l = Started(out _);
            for (float t = 1f; t <= 30f; t += 5f)
                Assert.That(l.Tick(t, 0, NeverCrossed()).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"線を越えていないので進まない (t={t})");
            Assert.That(l.ActiveStepIndex, Is.EqualTo(0));
        }

        [Test]
        public void UntilLine_AdvancesToNextStep_WhenTheLineIsCrossed()
        {
            TakeRunnerLogic l = Started(out _);
            l.Tick(3f, 0, NeverCrossed());

            TakeRunnerLogic.Decision d = l.Tick(5f, 0, Line(crossedAtSec: 5f));
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "線を越えたら次のカットへ");
            Assert.That(d.stepIndex, Is.EqualTo(1));
            Assert.That(d.takeStarted, Is.False, "演出は続いている（画面の占有は保つ）");
        }

        /// <summary>
        /// <b>カットが始まる前の横断では進まない。</b> 横断には <see cref="LineCrossLogic.CrossLatchSec"/> の
        /// 猶予があるので、これが無いと<b>区間へ入る途中で踏んだ線</b>がそのまま効いて 1 カット目が一瞬で飛ぶ。
        /// 3 周目 A の線は区間の入口寄りにあるので、実際に踏む。
        /// </summary>
        [Test]
        public void UntilLine_IgnoresCrossingsFromBeforeTheStepBegan()
        {
            TakeRunnerLogic l = Make(LineStep(3, 0, Slot));
            l.OnZoneCommitted(3, 0, false, 0, 0, 10f);
            // 区間へ入る 0.2 秒前に横切っていた（猶予 0.6s の内側）。
            TakeRunnerLogic.Decision begun = l.Tick(10f, 0, Line(crossedAtSec: 9.8f));
            Assert.That(begun.stepIndex, Is.EqualTo(0));

            Assert.That(l.Tick(10.1f, 0, Line(crossedAtSec: 9.8f)).action,
                Is.EqualTo(TakeRunnerLogic.Action.None), "始まる前の横断は数えない");
            Assert.That(l.ActiveStepIndex, Is.EqualTo(0));

            // 始まった後に越え直せば進む。
            Assert.That(l.Tick(12f, 0, Line(crossedAtSec: 12f)).stepIndex, Is.EqualTo(1));
        }

        /// <summary>線は担当カメラに紐づく（演出の <c>at:"line"</c> と同じ規約）。</summary>
        [Test]
        public void UntilLine_IgnoresLineOfAnotherCamera()
        {
            TakeRunnerLogic l = Started(out _);
            Assert.That(l.Tick(5f, 0, Line(crossedAtSec: 5f, camera: 2)).action,
                Is.EqualTo(TakeRunnerLogic.Action.None), "別の区間の線では進まない");
            Assert.That(l.Tick(6f, 0, Line(crossedAtSec: 6f, camera: 0)).stepIndex, Is.EqualTo(1));
        }

        /// <summary>猶予（0.6 秒）を過ぎた横断では進まない — 事象なので古い記録では動かさない。</summary>
        [Test]
        public void UntilLine_IgnoresStaleCrossing()
        {
            TakeRunnerLogic l = Started(out _);
            Assert.That(l.Tick(10f, 0, Line(crossedAtSec: 5f)).action,
                Is.EqualTo(TakeRunnerLogic.Action.None), "5 秒前の横断では進まない");
        }

        /// <summary>位置が取れない環境（未登録・HMD 参照なし）では線待ちのカットは進まない。</summary>
        [Test]
        public void UntilLine_DoesNotAdvance_WhenPositionIsUnavailable()
        {
            TakeRunnerLogic l = Started(out _);
            Assert.That(l.Tick(5f, 0, null).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.ActiveStepIndex, Is.EqualTo(0));
        }

        /// <summary>
        /// 線待ちでも watchdog は効く（体験者が越えなくても演出は必ず終わる）。
        /// 「終わらないカット」を新しく作らないための番人。
        /// </summary>
        [Test]
        public void UntilLine_StillEndsByWatchdog()
        {
            TakeRunnerLogic.Def def = LineStep(3, 0, Slot);
            def.maxDurationSec = 5f;
            TakeRunnerLogic l = Make(def);
            l.OnZoneCommitted(3, 0, false, 0, 0, 0f);
            l.Tick(0f, 0, NeverCrossed());

            TakeRunnerLogic.Decision d = l.Tick(5.1f, 0, NeverCrossed());
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.forced, Is.True);
        }
    }
}
