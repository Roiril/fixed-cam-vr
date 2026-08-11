#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 段 4「見えているものが割れてスクリーンへ入る」の契約。
    ///
    /// 守るのは 4 つ。どれも<b>破れると実機の画でしか気づけない</b>:
    ///   (A) 破砕は段 4 だけで動く（他の段に漏れると現実が勝手に割れる）
    ///   (B) <b>覆い（パススルー）を閉じ切ってから箱を割る</b> — 重なると箱の割れ目から
    ///       体験エリアの中が覗ける（canon/LEDGER.md 0005 違反）
    ///   (C) 進み 1 で<b>全部の破片が閉じる</b>（開いたままだと段 5 の映像に現実が混ざる）
    ///   (D) スクリーンの上のセルは割らない（枠がどこにあるか分からないまま映像が点く）
    /// </summary>
    public sealed class IntroShatterTests
    {
        private static readonly IntroTiming T = IntroTiming.Default;

        private static IntroInput Ready() => new IntroInput
        {
            blackCleared = true, headTurnDegPerSec = 0f, frameCentered = true,
            liveFresh = true, recentered = false, outsideBoxM = 1.5f,
        };

        private static IntroLogic AtFrame()
        {
            var l = new IntroLogic();
            l.Configure(T);
            l.Begin();
            for (int i = 0; i < 4; i++) { l.RequestAdvance(); l.Tick(0.001f, Ready()); }
            Assert.AreEqual(IntroStage.Frame, l.Stage, "段 4 まで進めていない");
            return l;
        }

        // (A) ------------------------------------------------------------------

        [Test]
        public void Shatter_IsZero_OutsideFrameStage()
        {
            var l = new IntroLogic();
            l.Configure(T);
            l.Begin();
            Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, "段 0 で割れている");
            for (int i = 0; i < 3; i++)
            {
                l.RequestAdvance();
                l.Tick(0.001f, Ready());
                Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, $"段 {l.Stage} で割れている");
            }
            // 段 4 を通り過ぎたら 0 に戻る（段 5 は黒 → 映像で、割るものが無い）。
            l.RequestAdvance();
            l.Tick(0.001f, Ready());
            l.RequestAdvance();
            l.Tick(0.001f, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            Assert.AreEqual(0f, l.Weights.shatter, 1e-5f, "段 5 で割れている");
        }

        [Test]
        public void Shatter_RisesMonotonically_InFrameStage()
        {
            var l = AtFrame();
            float last = -1f;
            for (int i = 0; i < 10; i++)
            {
                l.Tick(T.frameSec / 12f, Ready());
                if (l.Stage != IntroStage.Frame) break;
                float s = l.Weights.shatter;
                Assert.GreaterOrEqual(s, last, "破砕が戻った");
                Assert.Greater(s, 0f, "段 4 に居るのに割れていない");
                last = s;
            }
            Assert.Greater(last, 0.5f, "段 4 の後半まで進んでいない");
        }

        [Test]
        public void Shatter_NeverOutlivesPassthrough()
        {
            // 割るものは現実。パススルーが 0 の段で割ると、黒を割って黒を出すだけになる。
            var l = AtFrame();
            for (int i = 0; i < 12; i++)
            {
                l.Tick(T.frameSec / 10f, Ready());
                IntroWeights w = l.Weights;
                if (w.shatter > 0f)
                    Assert.Greater(w.passthrough, 0f, "パススルーが無いのに割れている");
            }
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

        [Test]
        public void Frame_StaysFullyOpen_WhileMostOfTheShatterRuns()
        {
            // 開口は覆いのセルも箱の破片も切る。飛んでいる最中に閉じると通り道で消える。
            var l = AtFrame();
            float step = T.frameSec / 20f;
            for (int i = 0; i < 20; i++)
            {
                l.Tick(step, Ready());
                if (l.Stage != IntroStage.Frame) break;
                IntroWeights w = l.Weights;
                if (w.shatter <= IntroLogic.FrameCloseAt)
                    Assert.AreEqual(0f, w.frame, 1e-5f,
                        $"破砕が {w.shatter:F2} の時点で枠が閉じ始めている");
            }
        }

        // ---- 格子そのもの（組めていなければ一生割れない）-----------------------

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
