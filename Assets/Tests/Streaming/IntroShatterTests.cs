#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 「見えているものが割れてスクリーンへ入る」破砕の契約。
    ///
    /// ⚠⚠ <b>2026-08-13 に導入からは外れた。</b> 体験者が封印の箱の中に入ってから固定視点になる
    /// 運用へ変わり、「箱の外で現実が割れる」段が成立しなくなったため。
    /// <see cref="IntroShatterCurve"/> と 2 つの破片メッシュは<b>眠らせてあるだけ</b>で消していない
    /// （赤入れが返ってから消す）ので、曲線とメッシュの契約はここで守り続ける。
    ///
    /// 守るのは 5 つ。うち (A) だけが新しい:
    ///   (A) <b>導入のどの段でも破砕は 0</b>（眠っている ＝ 現実が勝手に割れない）
    ///   (B) <b>覆い（パススルー）を閉じ切ってから箱を割る</b> — 重なると箱の割れ目から
    ///       体験エリアの中が覗ける（canon/LEDGER.md 0005 違反）
    ///   (C) 進み 1 で<b>全部の破片が閉じる</b>
    ///   (D) スクリーンの上のセルは割らない
    ///   (E) 格子そのものが組める（組めていなければ一生割れない）
    /// </summary>
    public sealed class IntroShatterTests
    {
        // (A) ------------------------------------------------------------------

        [Test]
        public void Shatter_IsAsleep_InEveryIntroStage()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            var input = new IntroInput
            {
                blackCleared = true, startAuthorized = true, outsideValid = true, atStartSpot = true, frameCentered = true,
                liveFresh = true, outsideBoxM = 0f,
            };
            Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, "段 0 で割れている");
            for (int i = 0; i < 400 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.05f, input);
                Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, $"段 {l.Stage} で割れている");
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        // (B) ------------------------------------------------------------------

        [Test]
        public void VeilPhase_FinishesBefore_BoxPhaseStarts()
        {
            // 重なると、箱の割れ目からその裏のパススルーが覗く（＝ 体験エリアの中）。
            for (float s = 0f; s <= 1.0001f; s += 0.01f)
            {
                float veil = IntroShatterCurve.VeilShatter(s);
                float box = IntroShatterCurve.BoxShatter(s);
                if (box > 0f)
                    Assert.AreEqual(1f, veil, 1e-5f,
                        $"箱が割れ始めた時点（shatter={s:F2}）で覆いが閉じ切っていない");
            }
            Assert.AreEqual(1f, IntroShatterCurve.VeilShatter(IntroShatterCurve.VeilPhaseEnd), 1e-5f);
            Assert.AreEqual(0f, IntroShatterCurve.BoxShatter(IntroShatterCurve.VeilPhaseEnd), 1e-5f);
            Assert.AreEqual(1f, IntroShatterCurve.BoxShatter(1f), 1e-5f);
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

        [Test]
        public void BoxMesh_CoversFiveFaces_AndFitsIn16BitIndex()
        {
            Mesh m = SealedBoxShatterMesh.Build(new Vector3(3f, 2.4f, 3f), out int cells);
            try
            {
                Assert.Greater(cells, 1000, "破片が粗すぎる（0.5m の距離では 1 枚が大きすぎる）");
                Assert.AreEqual(cells * 4, m.vertexCount);
                Assert.Less(m.vertexCount, 65535, "16bit index に収まらない");
                // 底面は作らない（Cull Back で外から見るので、床に接した底の外側は見えない）。
                var nrm = new System.Collections.Generic.List<Vector3>();
                m.GetNormals(nrm);
                foreach (Vector3 n in nrm)
                    Assert.Greater(n.y, -0.5f, "底面の破片がある（見えないので作らない約束）");
            }
            finally { Object.DestroyImmediate(m); }
        }
    }
}
