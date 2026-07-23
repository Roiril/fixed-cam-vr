#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// MarkLabelRouter（ファシリテータ mark の分類と 200 字トランケート）。
    /// </summary>
    public class MarkLabelRouterTests
    {
        [Test]
        public void Classify_ResetBoard()
        {
            Assert.AreEqual(MarkAction.ResetBoard, MarkLabelRouter.Classify("reset_board", out string id));
            Assert.AreEqual("", id);
        }

        [Test]
        public void Classify_GameSwitch()
        {
            Assert.AreEqual(MarkAction.GameSwitch, MarkLabelRouter.Classify("game_algo", out string id));
            Assert.AreEqual("algo", id);
        }

        [Test]
        public void Classify_GameSwitch_ResetBoardId()
        {
            // game_ 名前空間分離: "game_reset_board" は GameSwitch であって ResetBoard ではない
            Assert.AreEqual(MarkAction.GameSwitch, MarkLabelRouter.Classify("game_reset_board", out string id));
            Assert.AreEqual("reset_board", id);
        }

        [Test]
        public void Classify_AlgoDeal()
        {
            Assert.AreEqual(MarkAction.AlgoDeal, MarkLabelRouter.Classify("algo_deal", out _));
        }

        [Test]
        public void Classify_BandidoDeal()
        {
            Assert.AreEqual(MarkAction.BandidoDeal, MarkLabelRouter.Classify("bandido_deal", out _));
        }

        [Test]
        public void Classify_Unknown_LogOnly()
        {
            Assert.AreEqual(MarkAction.LogOnly, MarkLabelRouter.Classify("phase2", out string id));
            Assert.AreEqual("", id);
        }

        [Test]
        public void TruncateLabel_Over200_ClampsTo200()
        {
            string label = new string('x', 201);
            Assert.AreEqual(200, MarkLabelRouter.TruncateLabel(label).Length);
        }

        [Test]
        public void TruncateLabel_Short_Unchanged()
        {
            Assert.AreEqual("phase2", MarkLabelRouter.TruncateLabel("phase2"));
        }
    }
}
