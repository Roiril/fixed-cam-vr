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
        public void Reveal_FadesWithoutChangingTheFaceSize()
        {
            Assert.AreEqual(0.5f, CommsFaceLayout.Reveal(0.5f, BodyW), 1e-5f);
            Assert.AreEqual(0.15f, CommsFaceLayout.CellM, 1e-6f,
                            "出入りで顔そのものを縮めない");
        }

        [Test]
        public void FaceFrame_IsRemoved()
        {
            Assert.AreEqual(0f, CommsFaceLayout.StrokeK);
        }

        [Test]
        public void Divider_IsShortThinAndBetweenFaceAndText()
        {
            float cellRight = CommsFaceLayout.CellCenterX(BodyW) + CommsFaceLayout.CellM * 0.5f;
            float textLeft = -BodyW * 0.5f;
            float divider = CommsFaceLayout.DividerCenterX(BodyW);
            Assert.Greater(divider, cellRight);
            Assert.Less(divider, textLeft);
            Assert.Less(CommsFaceLayout.DividerW, 0.004f);
            Assert.Less(CommsFaceLayout.DividerH, CommsFaceLayout.CellM);
        }

        [Test]
        public void GlyphFade_RisesWithinThirtyToFortyFiveMilliseconds()
        {
            Assert.That(CommsPanel.GlyphFadeSec, Is.InRange(0.030f, 0.045f));
            Assert.AreEqual(0f, CommsPanel.GlyphFadeAlpha(0f), 1e-6f);
            Assert.That(CommsPanel.GlyphFadeAlpha(CommsPanel.GlyphFadeSec * 0.5f),
                        Is.InRange(0.45f, 0.55f));
            Assert.AreEqual(1f, CommsPanel.GlyphFadeAlpha(CommsPanel.GlyphFadeSec), 1e-6f);
        }
    }
}
