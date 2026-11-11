#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class EndingShotWindowTests
    {
        private static EndingShotWindow Window(
            bool endingTake = true, bool waitingForMark = false, bool released = false,
            bool live = false, bool primary = false, bool secondary = false, bool cg = false,
            bool primaryActive = false, bool secondaryActive = false, bool cgActive = false,
            bool swap = false, bool transition = false, bool farewellFacing = false, bool plainAllowed = true)
            => TakeRunner.ResolveEndingShotWindow(endingTake, waitingForMark, released, live,
                primary, secondary, cg, primaryActive, secondaryActive, cgActive, swap, transition,
                farewellFacing, plainAllowed);

        [Test]
        public void Trapped_RequiresTheEndingTakeAndEveryVisibleLayer()
        {
            Assert.That(Window(waitingForMark: true, primary: true, secondary: true, cg: true),
                Is.EqualTo(EndingShotWindow.Trapped));
            Assert.That(Window(endingTake: false, waitingForMark: true,
                primary: true, secondary: true, cg: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(waitingForMark: true, primary: false, secondary: true, cg: true),
                Is.EqualTo(EndingShotWindow.None), "最終素材が material へ届く前は撮らない");
            Assert.That(Window(waitingForMark: true, primary: true, secondary: false, cg: true),
                Is.EqualTo(EndingShotWindow.None), "右側の実写素材が material へ届く前は撮らない");
            Assert.That(Window(waitingForMark: true, primary: true, secondary: true, cg: false),
                Is.EqualTo(EndingShotWindow.None), "体験者位置の CG が描けていなければ撮らない");
        }

        [Test]
        public void TheAppliedOverlay_IsMatchedByTheStepKey_NotByTheAssetName()
        {
            // 2026-11-11: 素材名（gen_dolls_A_left）と比べていたため primary が常に偽で、
            // 人形 End の写真が 1 枚も撮れなかった。画面に届いた id は take と段の番号で作られる。
            string applied = TakeRunner.StepCueKey("L4C0#0", 1);
            Assert.That(applied, Is.EqualTo("L4C0#0#1"));
            Assert.That(TakeRunner.IsStepCueKey(applied, "L4C0#0", 1), Is.True);
            Assert.That(TakeRunner.IsStepCueKey("gen_dolls_A_left", "L4C0#0", 1), Is.False);
            Assert.That(TakeRunner.IsStepCueKey(applied, "L4C0#0", 0), Is.False, "前の段の素材が残っている間は撮らない");
            Assert.That(TakeRunner.IsStepCueKey(TakeRunner.StepCueKey("L4C0#0", 12), "L4C0#0", 1), Is.False);
            Assert.That(TakeRunner.IsStepCueKey(TakeRunner.StepCueKey("L4C0#0", 12), "L4C0#0", 12), Is.True);
            Assert.That(TakeRunner.IsStepCueKey(TakeRunner.StepCueKey("L3C0#0", 1), "L4C0#0", 1), Is.False);
            Assert.That(TakeRunner.IsStepCueKey(TakeRunner.StepCueKey("#3", 0), "#3", 0), Is.True, "id の無い take");
            Assert.That(TakeRunner.IsStepCueKey("L4C0#0#01", "L4C0#0", 1), Is.False);
            Assert.That(TakeRunner.IsStepCueKey("L4C0#0#", "L4C0#0", 0), Is.False);
            Assert.That(TakeRunner.IsStepCueKey(null, "L4C0#0", 1), Is.False);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Trapped_DoesNotOpenDuringATransition(bool swap, bool transition)
        {
            Assert.That(Window(waitingForMark: true, primary: true, secondary: true, cg: true,
                swap: swap, transition: transition), Is.EqualTo(EndingShotWindow.None));
        }

        [Test]
        public void Released_RequiresReportLiveAndAllCompositeLayersGone()
        {
            Assert.That(Window(released: true, live: true), Is.EqualTo(EndingShotWindow.Released));
            Assert.That(Window(released: false, live: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: true, live: false), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: true, live: true, primaryActive: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: true, live: true, secondaryActive: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: true, live: true, cgActive: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: true, live: true, swap: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: true, live: true, transition: true), Is.EqualTo(EndingShotWindow.None));
        }

        [Test]
        public void Released_IsTakenWhileTheFarewellDollFacesTheVisitor()
        {
            // 0291: 右に体験者・左に別れの人形が体験者を向いて立つ画を帰還の写真にする。
            Assert.That(Window(released: true, live: true, cgActive: true, farewellFacing: true, plainAllowed: false),
                Is.EqualTo(EndingShotWindow.Released));
            Assert.That(Window(released: true, live: true, cgActive: true, farewellFacing: false, plainAllowed: false),
                Is.EqualTo(EndingShotWindow.None), "後ろを向いた後・下がっている間は撮らない");
            Assert.That(Window(released: true, live: true, cgActive: false, farewellFacing: false, plainAllowed: false),
                Is.EqualTo(EndingShotWindow.None), "消えた後に人形なしの画で上書きしない");
            Assert.That(Window(released: true, live: true, cgActive: false, farewellFacing: false, plainAllowed: true),
                Is.EqualTo(EndingShotWindow.Released), "人形を出せなかったときは人形なしの帰還を撮る");
            Assert.That(Window(released: true, live: true, cgActive: true, farewellFacing: true, plainAllowed: false,
                swap: true), Is.EqualTo(EndingShotWindow.None));
            Assert.That(Window(released: false, live: true, cgActive: true, farewellFacing: true),
                Is.EqualTo(EndingShotWindow.None), "報告前は帰還の写真を撮らない");
        }

        [Test]
        public void ReportImmediatelyClosesTheTrappedWindow()
        {
            Assert.That(Window(waitingForMark: true, released: true,
                primary: true, secondary: true, cg: true), Is.EqualTo(EndingShotWindow.None));
        }

        [TestCase(true, false, true, false, true)]
        [TestCase(false, false, true, false, false)]
        [TestCase(true, true, true, false, false)]
        [TestCase(true, false, false, false, false)]
        [TestCase(true, false, true, true, false)]
        public void EndingDollFootExcludesOtherTakesReportsHiddenDollsAndTransitions(
            bool endingTake, bool released, bool dollShowing, bool transition, bool expected)
        {
            Assert.That(TakeRunner.CanUseEndingDollFoot(
                endingTake, released, dollShowing, transition), Is.EqualTo(expected));
        }
    }
}
