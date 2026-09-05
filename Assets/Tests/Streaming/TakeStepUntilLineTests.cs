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
        // ---- 「体験者が報告するまで」（untilMark・4 周目 A の締め）----

        private static TakeRunnerLogic.Def MarkStep(int lap, int cam, float maxSec = 0f) => new()
        {
            lap = lap, camera = cam, onExit = false, offsetSec = 0f,
            skipWhenMissed = false, once = true, maxDurationSec = maxSec,
            stepDurSec = new[] { TakeRunnerLogic.WaitMark, 3f },
        };

        private static TakeRunnerLogic StartedMark(float maxSec = 0f, float now = 0f)
        {
            TakeRunnerLogic l = Make(MarkStep(4, 0, maxSec));
            l.OnZoneCommitted(4, 0, false, 0, 0, now);
            Assert.That(l.Tick(now, 0).stepIndex, Is.EqualTo(0));
            return l;
        }

        [Test]
        public void UntilMark_WaitsUntilTheVisitorReports()
        {
            TakeRunnerLogic l = StartedMark();
            for (float t = 1f; t <= 20f; t += 5f)
                Assert.That(l.Tick(t, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"押していないので進まない (t={t})");

            l.NotifyMarkPressed(21f);
            TakeRunnerLogic.Decision d = l.Tick(21f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.stepIndex, Is.EqualTo(1), "報告で次のカット（全部ライブへ戻る）へ");
        }

        /// <summary>
        /// カットが始まる前の報告では進まない。前の区間で押した 1 回が持ち越されて
        /// 締めのカットを素通りするのを防ぐ（線待ちと同じ理由）。
        /// </summary>
        [Test]
        public void UntilMark_IgnoresReportsFromBeforeTheStepBegan()
        {
            TakeRunnerLogic l = Make(MarkStep(4, 0));
            l.OnZoneCommitted(4, 0, false, 0, 0, 10f);
            l.NotifyMarkPressed(9f);            // 区間へ入る前に押していた
            Assert.That(l.Tick(10f, 0).stepIndex, Is.EqualTo(0));
            Assert.That(l.Tick(11f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));

            // ⚠ 猶予（MarkGraceSec）を過ぎてから押す（0178 で 4 秒は受け付けなくなった）。
            float t = 10f + TakeRunnerLogic.MarkGraceSec + 1f;
            l.NotifyMarkPressed(t);
            Assert.That(l.Tick(t, 0).stepIndex, Is.EqualTo(1));
        }

        // ---------------------------------------------------- 締めの受け付け猶予（0178）

        /// <summary>
        /// ⚠⚠ <b>締めのカットに入って最初の数秒は、押しても受け付けない</b>
        /// （2026-09-06・<c>canon/LEDGER.md</c> 0178・ユーザー指定
        /// 「4-A に入ってから 4s は、押しても反応しないようにしてほしい」）。
        /// </summary>
        [Test]
        public void ClosingTake_IgnoresTheReport_ForTheFirstFewSeconds()
        {
            TakeRunnerLogic l = Make(MarkStep(4, 0));
            l.OnZoneCommitted(4, 0, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 0).stepIndex, Is.EqualTo(0));

            Assert.That(l.IsMarkTooEarly(0f), Is.True, "入った瞬間は受け付けない");
            Assert.That(l.IsMarkTooEarly(TakeRunnerLogic.MarkGraceSec - 0.1f), Is.True);
            Assert.That(l.NotifyMarkPressed(TakeRunnerLogic.MarkGraceSec - 0.1f),
                        Is.EqualTo(TakeRunnerLogic.MarkResult.None), "猶予の中で受け付けた");
            Assert.That(l.Tick(TakeRunnerLogic.MarkGraceSec - 0.1f, 0).action,
                        Is.EqualTo(TakeRunnerLogic.Action.None), "猶予の中の報告でカットが進んだ");

            Assert.That(l.IsMarkTooEarly(TakeRunnerLogic.MarkGraceSec + 0.1f), Is.False);
            Assert.That(l.NotifyMarkPressed(TakeRunnerLogic.MarkGraceSec + 0.1f),
                        Is.EqualTo(TakeRunnerLogic.MarkResult.Released), "猶予を過ぎても受け付けない");
        }

        /// <summary>
        /// ⚠ <b>猶予がかかるのは締めのカットだけ。</b> 1〜3 周目の演出はいままでどおり
        /// 押した瞬間に効く（<c>dismissible</c>）。ここを取り違えると体験の大半で報告が死ぬ。
        /// </summary>
        [Test]
        public void OtherTakes_AreNotAffectedByTheClosingGrace()
        {
            TakeRunnerLogic l = Make(new TakeRunnerLogic.Def
            {
                lap = 4, camera = 0, onExit = false, offsetSec = 0f,
                skipWhenMissed = false, once = true, dismissible = true,
                stepDurSec = new[] { 5f },
            });
            l.OnZoneCommitted(4, 0, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 0).stepIndex, Is.EqualTo(0));

            Assert.That(l.ActiveTakeWaitsForMark, Is.False, "締めのカットではない");
            Assert.That(l.IsMarkTooEarly(0.1f), Is.False, "締め以外にも猶予が掛かっている");
            Assert.That(l.NotifyMarkPressed(0.1f),
                        Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
        }

        /// <summary>③の時計（<c>ClosingTakeSec</c>）は締めのカットに居るあいだだけ進む。</summary>
        [Test]
        public void ClosingTakeSec_IsNegative_OutsideTheClosingTake()
        {
            TakeRunnerLogic l = Make(MarkStep(4, 0));
            Assert.That(l.ClosingTakeSec(0f), Is.LessThan(0f), "走っていないのに正の値");

            l.OnZoneCommitted(4, 0, false, 0, 0, 3f);
            l.Tick(3f, 0);
            Assert.That(l.ClosingTakeSec(3f), Is.EqualTo(0f).Within(1e-3f));
            Assert.That(l.ClosingTakeSec(8f), Is.EqualTo(5f).Within(1e-3f));
        }

        /// <summary>報告しない体験者でも必ず終わる（押さなかった人を置き去りにしない）。</summary>
        [Test]
        public void UntilMark_StillEndsByWatchdog()
        {
            TakeRunnerLogic l = StartedMark(maxSec: 45f);
            TakeRunnerLogic.Decision d = l.Tick(45.1f, 0);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.forced, Is.True);
        }

        /// <summary>
        /// <b>報告は他の尺のカットを「進めない」。</b> 早送りボタンにならない、が要点
        /// （3 周目の演出は 2〜4 カットあるので、進むと台本が押しボタンで飛ばされる）。
        ///
        /// ⚠ 2026-08-17 から、演出に <c>dismissible</c> が立っていれば報告で**演出ごと畳まれる**。
        ///   ここで確かめているのは<b>旗を立てていない演出</b>（＝ 既定）なので従来どおり何も起きない。
        ///   畳む側の契約は <see cref="TakeVisitorDismissTests"/>。
        /// </summary>
        [Test]
        public void MarkPress_DoesNotAffectOtherStepKinds()
        {
            TakeRunnerLogic.Def def = MarkStep(4, 0);
            def.stepDurSec = new[] { 10f, 3f };          // ふつうの秒指定
            TakeRunnerLogic l = Make(def);
            l.OnZoneCommitted(4, 0, false, 0, 0, 0f);
            l.Tick(0f, 0);

            l.NotifyMarkPressed(1f);
            Assert.That(l.Tick(1f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                "秒で指定したカットは報告では進まない");
        }
    }
}
