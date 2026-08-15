#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 「見えているものが割れてスクリーンへ入る」破砕（段 4）の契約。
    ///
    /// ⚠⚠ <b>2026-08-15 に導入へ戻した。</b> 2026-08-13〜15 は段ごと眠っていたが、
    /// 封印の箱を退避して「箱の外で現実が割れる」構成に戻ったため
    /// （<c>canon/LEDGER.md</c> 0044）。**割れるのは覆い（パススルー）だけ**で、
    /// 箱の側（<see cref="IntroShatterCurve.BoxShatter"/> / <c>SealedBoxShatterMesh</c>）は
    /// Attic に退避してある。
    ///
    /// 守るのは 5 つ:
    ///   (A) <b>割れるのは段 4 だけ</b>（他の段で現実が勝手に割れない）
    ///   (B) <b>覆いは段 4 の進みをまるごと受ける</b>（箱に半分渡していた分は要らない）
    ///   (C) 進み 1 で<b>全部の破片が閉じる</b>
    ///   (D) スクリーンの上のセルは割らない
    ///   (E) 格子そのものが組める（組めていなければ一生割れない）
    /// </summary>
    public sealed class IntroShatterTests
    {
        // (A) ------------------------------------------------------------------

        [Test]
        public void Shatter_OnlyMoves_DuringTheFrameStage()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            var input = new IntroInput
            {
                blackCleared = true, startAuthorized = true, outsideValid = true, atStartSpot = true,
                frameCentered = true, liveFresh = true, outsideBoxM = 2f,
            };
            Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, "段 0 で割れている");
            bool sawShatter = false;
            for (int i = 0; i < 600 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.05f, input);
                if (l.Stage == IntroStage.Frame)
                {
                    if (l.Weights.shatter > 0f) sawShatter = true;
                }
                else
                {
                    Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, $"段 {l.Stage} で割れている");
                }
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.IsTrue(sawShatter, "段 4 で 1 度も割れていない");
        }

        // (B) ------------------------------------------------------------------

        [Test]
        public void VeilTakesTheWholeProgress()
        {
            // ⚠ 箱が居たころは前半 0.35 だけが覆いの取り分だった。箱を退避したので、
            //   進みをまるごと覆いへ渡さないと**段の 1/3 で割れ終わって残りが無風**になる。
            Assert.AreEqual(0f, IntroShatterCurve.VeilShatter(0f), 1e-5f);
            Assert.AreEqual(0.5f, IntroShatterCurve.VeilShatter(0.5f), 1e-5f);
            Assert.AreEqual(1f, IntroShatterCurve.VeilShatter(1f), 1e-5f);
        }

        // (C) ------------------------------------------------------------------

        [Test]
        public void EveryCell_IsClosed_WhenShatterReachesOne()
        {
            for (int i = 1; i <= 20; i++)
            {
                float far = i / 20f;
                for (int j = 0; j <= 4; j++)
                {
                    float r = j / 4f;
                    Assert.AreEqual(1f, IntroShatterCurve.ClosedAt(far, r, 1f), 1e-4f,
                        $"far={far:F2} r={r:F2} の破片が閉じ切っていない");
                }
            }
        }

        [Test]
        public void Periphery_ClosesBefore_TheCellsNearTheScreen()
        {
            // 前線は**周縁からスクリーンへ**。逆走すると「持っていかれている」が読めない。
            const float mid = 0.6f;
            float outer = IntroShatterCurve.ClosedAt(1.0f, 0.5f, mid);
            float inner = IntroShatterCurve.ClosedAt(0.1f, 0.5f, mid);
            Assert.Greater(outer, inner, "周縁より内側が先に閉じている（前線が逆）");
        }

        // (D) ------------------------------------------------------------------

        [Test]
        public void CellsOnTheScreen_DoNotShatter()
        {
            Assert.IsFalse(IntroShatterCurve.Shatters(0f), "スクリーンの上のセルが割れている");
            Assert.IsTrue(IntroShatterCurve.Shatters(0.01f));
        }

        // (E) 格子そのもの（組めていなければ一生割れない）-------------------------

        [Test]
        public void VeilMesh_HasOneQuadPerCell_PlusStillBorder()
        {
            Mesh m = IntroVeilShatterMesh.Build();
            try
            {
                int cells = IntroVeilShatterMesh.CellCount;
                Assert.AreEqual((cells + 4) * 4, m.vertexCount, "セル数と頂点数が合わない");
                Assert.Less(m.vertexCount, 65535, "16bit index に収まらない");
                var cell = new System.Collections.Generic.List<Vector3>();
                m.GetUVs(1, cell);
                Assert.AreEqual(m.vertexCount, cell.Count, "セル中心のストリームが無い");
                int shattering = 0;
                foreach (Vector3 c in cell) if (c.z > 0.5f) shattering++;
                Assert.AreEqual(cells * 4, shattering, "動かない縁取りの枚数が合わない");
            }
            finally { Object.DestroyImmediate(m); }
        }

        // ⚠ 箱の破片メッシュ（`SealedBoxShatterMesh`）の契約は
        //   `Assets/Tests/Streaming/Attic/` へは移していない — 箱を戻すときに
        //   `.claude/reference/attic-sealed-box.md` の手順で書き直す。

    }
}
