using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Streaming
{
    /// <summary>
    /// 終幕の合図（<c>run.outro.afterTakeId</c> が指す演出が終わったら終わる）を固定する。
    ///
    /// ⚠ ここが早撃ちすると **体験が本編の途中で終わる**。撃たないと従来の安全網
    /// （<c>endHoldMaxSec</c>）まで落ちるだけなので、**迷ったら撃たない側**が正しい。
    /// </summary>
    public sealed class EndingCueLogicTests
    {
        private static EndingCueLogic Make(string id = "L4C0#0")
        {
            var l = new EndingCueLogic();
            l.Configure(id);
            return l;
        }

        [Test]
        public void NotConfigured_NeverFires()
        {
            var l = new EndingCueLogic();
            Assert.IsFalse(l.Configured);
            Assert.IsFalse(l.Tick(true, ""));
            Assert.IsFalse(l.Tick(true, "L4C0#0"));
            Assert.IsFalse(l.Tick(true, ""));
            Assert.IsFalse(l.Armed);
        }

        [Test]
        public void FiresWhenTheNamedTakeStopsRunning()
        {
            var l = Make();
            Assert.IsFalse(l.Tick(true, ""), "まだ走っていない");
            Assert.IsFalse(l.Armed);

            Assert.IsFalse(l.Tick(true, "L4C0#0"), "走っている最中は撃たない");
            Assert.IsTrue(l.Armed, "走っているのを見た ＝ 武装");

            Assert.IsTrue(l.Tick(true, ""), "終わった瞬間に撃つ");
            Assert.IsTrue(l.Fired);
        }

        [Test]
        public void DoesNotFireBeforeTheTakeHasEverRun()
        {
            // ⚠ 演出が始まる前も ActiveTakeId は空。空だけを見ると本編に入った瞬間に撃つ。
            var l = Make();
            for (int i = 0; i < 100; i++) Assert.IsFalse(l.Tick(true, ""));
        }

        [Test]
        public void OtherTakesDoNotArmIt()
        {
            var l = Make();
            Assert.IsFalse(l.Tick(true, "L1C1#0"));
            Assert.IsFalse(l.Armed);
            Assert.IsFalse(l.Tick(true, ""), "別の演出が終わっても撃たない");
        }

        [Test]
        public void FiresWhenHandedStraightToAnotherTake()
        {
            // chainNext（復帰の暗転を挟まず次の演出へ渡す）では id が空を経ずに変わる。
            var l = Make();
            l.Tick(true, "L4C0#0");
            Assert.IsTrue(l.Tick(true, "L4C0#1"));
        }

        [Test]
        public void FiresOnlyOnce()
        {
            var l = Make();
            l.Tick(true, "L4C0#0");
            Assert.IsTrue(l.Tick(true, ""));
            Assert.IsFalse(l.Tick(true, ""), "2 回目は撃たない");
            Assert.IsFalse(l.Tick(true, "L4C0#0"), "同じ演出が再武装しない");
            Assert.IsFalse(l.Tick(true, ""));
        }

        [Test]
        public void ResetRun_IsTheOnlyWayBack()
        {
            // ユーザーが名指しした「周回リセットのときにリセットされるフラグ」。
            var l = Make();
            l.Tick(true, "L4C0#0");
            Assert.IsTrue(l.Tick(true, ""));

            l.ResetRun();
            Assert.IsFalse(l.Armed);
            Assert.IsFalse(l.Fired);
            Assert.IsFalse(l.Tick(true, ""), "リセット直後に撃ち返さない");
            l.Tick(true, "L4C0#0");
            Assert.IsTrue(l.Tick(true, ""), "次の体験者でまた効く");
        }

        [Test]
        public void OnlyArmsAndFiresDuringTheMainRun()
        {
            var l = Make();
            Assert.IsFalse(l.Tick(false, "L4C0#0"), "導入では武装しない");
            Assert.IsFalse(l.Armed);
            Assert.IsFalse(l.Tick(false, ""));

            l.Tick(true, "L4C0#0");
            Assert.IsFalse(l.Tick(false, ""), "終了相では撃たない（もう終わっている）");
            Assert.IsTrue(l.Tick(true, ""));
        }

        [Test]
        public void ReconfiguringDropsTheLatch()
        {
            // 卓が show.json を配り直して別の演出を指したら、前の演出で立てた武装は意味を失う。
            var l = Make();
            l.Tick(true, "L4C0#0");
            Assert.IsTrue(l.Armed);

            l.Configure("L3C2#0");
            Assert.IsFalse(l.Armed);
            Assert.IsFalse(l.Tick(true, ""));
        }

        [Test]
        public void ReconfiguringWithTheSameIdKeepsTheLatch()
        {
            // show.json は同じ内容で何度も届く（long-poll）。そのたびに武装が落ちると、
            // 配信のタイミング次第で合図が消える。
            var l = Make();
            l.Tick(true, "L4C0#0");
            l.Configure("L4C0#0");
            Assert.IsTrue(l.Armed);
            Assert.IsTrue(l.Tick(true, ""));
        }
    }
}
