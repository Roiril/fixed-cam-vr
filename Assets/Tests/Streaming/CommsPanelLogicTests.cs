#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 上司からの連絡（第 2 の面）の段。**読まなくても必ず引く**ことと、
    /// **文字が地より先に消える**ことを固定する。
    /// </summary>
    public sealed class CommsPanelLogicTests
    {
        private const float Dt = 1f / 72f;

        private static void Advance(CommsPanelLogic l, float sec)
        {
            int n = (int)(sec / Dt);
            for (int i = 0; i < n; i++) l.Tick(Dt);
        }

        [Test]
        public void ItAlwaysClosesByItself_WithoutAnyRead()
        {
            // ⚠ **本編の進行を既読待ちにしない。** 体験者が持つ入力は左 X（記録）だけで、
            //    既読の操作を作ると「記録した」と「読んだ」が混ざる。
            var l = new CommsPanelLogic();
            l.Begin();
            Assert.IsTrue(l.Active);

            Advance(l, CommsPanelLogic.InSec + CommsPanelLogic.HoldSec + CommsPanelLogic.OutSec + 0.5f);
            Assert.AreEqual(CommsStage.Off, l.Stage, "誰も読まなくても引くこと");
            Assert.AreEqual(0f, l.Weights.panel, 0.001f);
        }

        [Test]
        public void TheTextArrivesAfterThePanel_AndLeavesBeforeIt()
        {
            // 面が先に出て、文字が後から載る（受信してから表示される）。
            // 引くときは逆で、文字が先に消える（同時だと「電源が落ちた」に見える）。
            var l = new CommsPanelLogic();
            l.Begin();
            Advance(l, CommsPanelLogic.GlyphDelaySec * 0.5f);
            Assert.Greater(l.Weights.panel, l.Weights.glyph, "文字が面より先に出ている");

            Advance(l, CommsPanelLogic.InSec + CommsPanelLogic.HoldSec + CommsPanelLogic.OutSec * 0.7f);
            Assert.AreEqual(CommsStage.Out, l.Stage);
            Assert.Less(l.Weights.glyph, l.Weights.panel, "文字が面より後まで残っている");
        }

        [Test]
        public void BeginAgain_RestartsFromTheHead()
        {
            // 2 通目が来たら頭から出し直す（重ねない）。
            var l = new CommsPanelLogic();
            l.Begin();
            Advance(l, CommsPanelLogic.InSec + 1f);
            Assert.AreEqual(CommsStage.Hold, l.Stage);

            l.Begin();
            Assert.AreEqual(CommsStage.In, l.Stage);
            Assert.Less(l.Weights.panel, 1f);
        }
    }
}
