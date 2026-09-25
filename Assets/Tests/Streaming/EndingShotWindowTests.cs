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
            bool swap = false, bool transition = false)
            => TakeRunner.ResolveEndingShotWindow(endingTake, waitingForMark, released, live,
                primary, secondary, cg, primaryActive, secondaryActive, cgActive, swap, transition);

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
        public void ReportImmediatelyClosesTheTrappedWindow()
        {
            Assert.That(Window(waitingForMark: true, released: true,
                primary: true, secondary: true, cg: true), Is.EqualTo(EndingShotWindow.None));
        }
    }
}
