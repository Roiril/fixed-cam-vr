#nullable enable

using FixedCamVr.Diagnostics;
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// AIエージェントの顔の枠（<c>canon/LEDGER.md</c> 0071）の寸法を固定する。
    ///
    /// ⚠ ここで守っているのは<b>「文面へ手を出していないこと」</b>が主。
    /// 顔を足すために面の幅を触った瞬間、いちばん長い行（14 文字）が 3 行へ折り返して
    /// 枠の高さの前提（最悪 2 行）ごと崩れる。数値を動かすなら、ここが落ちる形で動かす。
    /// </summary>
    public class CommsFaceLayoutTests
    {
        /// <summary>文面の帯の幅（<c>CommsPanel.PanelW</c> と同じ値。private const なので写す）。</summary>
        private const float BodyW = 0.76f;

        [Test]
        public void RightEdge_IsUnchanged_SoTheTextBandNeverMoves()
        {
            // 面は**左へだけ**伸びる。右端は文面の帯の右端そのままでなければならない。
            float right = CommsFaceLayout.LeftX(BodyW) + CommsFaceLayout.FullW(BodyW);
            Assert.AreEqual(BodyW * 0.5f, right, 1e-5f);
        }

        [Test]
        public void Band_IsCellPlusMarginOnBothSides()
        {
            Assert.AreEqual(CommsFaceLayout.CellM + CommsFaceLayout.MarginM * 2f,
                            CommsFaceLayout.BandW, 1e-6f);
            Assert.AreEqual(CommsFaceLayout.BandW,
                            CommsFaceLayout.FullW(BodyW) - BodyW, 1e-5f);
        }

        [Test]
        public void Cell_SitsInsideItsOwnBand_AndNeverOverlapsTheTextBand()
        {
            float left = CommsFaceLayout.LeftX(BodyW);
            float cx = CommsFaceLayout.CellCenterX(BodyW);
            float cellLeft = cx - CommsFaceLayout.CellM * 0.5f;
            float cellRight = cx + CommsFaceLayout.CellM * 0.5f;

            Assert.AreEqual(left + CommsFaceLayout.MarginM, cellLeft, 1e-5f,
                            "枠の左は面の縁から余白ぶん内側");
            // 文面の帯の左端（-BodyW/2）より左に収まっている ＝ 字に一切かからない。
            Assert.Less(cellRight, -BodyW * 0.5f,
                        "枠が文面の帯へ食い込んでいる（字が隠れる）");
        }

        [Test]
        public void MinHeight_FitsTheCell_WithTheSameMarginAsTheSides()
        {
            Assert.AreEqual(CommsFaceLayout.CellM + CommsFaceLayout.MarginM * 2f,
                            CommsFaceLayout.MinBoxH, 1e-6f);
            Assert.Greater(CommsFaceLayout.MinBoxH, CommsFaceLayout.CellM);
        }

        [Test]
        public void MinHeight_DoesNotStretch_TheTallestAuthoredPanel()
        {
            // いちばん高い姿 ＝ 上段 2 行（`CommsPanel.BodyMaxH` = 0.20）＋ 下段（0.095）。
            // ⚠ ここが逆転すると、**顔のために面がいちばん高い所でも伸びる**ことになり、
            //    0065 の「出ている帯だけを覆う」が意味を失う。
            const float tallest = 0.20f + 0.095f;
            Assert.Less(CommsFaceLayout.MinBoxH, tallest);
        }

        [Test]
        public void Reveal_IsZeroWhileClosed_AndOneWhenFullyOpen()
        {
            Assert.AreEqual(0f, CommsFaceLayout.Reveal(0f, BodyW), 1e-6f);
            Assert.AreEqual(1f, CommsFaceLayout.Reveal(1f, BodyW), 1e-6f);
        }

        [Test]
        public void Reveal_WaitsUntilTheShutterHasPassedTheWholeCell()
        {
            // 枠の右端ちょうどまで開いた時点では、まだ 1 画素も出ない。
            float cellRightFromLeft = CommsFaceLayout.MarginM + CommsFaceLayout.CellM;
            float open = cellRightFromLeft / CommsFaceLayout.FullW(BodyW);
            Assert.AreEqual(0f, CommsFaceLayout.Reveal(open, BodyW), 1e-5f);

            // そこから `RevealSpanM` ぶん開けば出そろう。
            float wider = (cellRightFromLeft + CommsFaceLayout.RevealSpanM)
                          / CommsFaceLayout.FullW(BodyW);
            Assert.AreEqual(1f, CommsFaceLayout.Reveal(wider, BodyW), 1e-5f);
        }

        [Test]
        public void Stroke_IsThickEnoughToSurviveTheHeadset()
        {
            // 1.5m 先の 0.15m 角 ＝ 見かけ 5.7°。Quest 3 は視野中心でおよそ 20 画素/度なので
            // 1 辺 110 画素そこそこ。**枠線が 3 画素を切ると縮小で灰色の靄になる**。
            const float pxPerCell = 110f;
            Assert.GreaterOrEqual(CommsFaceLayout.StrokeK * pxPerCell, 3f);
            // 逆に太すぎると顔が枠に食われる（1 辺の 1 割まで）。
            Assert.Less(CommsFaceLayout.StrokeK, 0.10f);
        }
    }
}
