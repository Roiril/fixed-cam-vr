#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// GeisterSnapLogic（ガイスター駒リリース時スナップの純計算）の EditMode テスト。
    /// 盤は 6x6・cellPitch=0.065（実運用値）。GameObject 生成なしの純関数テストのみ。
    /// </summary>
    public class GeisterSnapLogicTests
    {
        private const int CellsPerSide = 6;
        private const float Pitch = 0.065f;

        // 盤中心を原点からずらし、「原点前提」の隠れバグを検出できるようにする
        private static GeisterSnapLogic.Config DefaultConfig() => new GeisterSnapLogic.Config
        {
            boardCenterX = 0.4f,
            boardCenterZ = -0.2f,
            cellPitch = Pitch,
            cellsPerSide = CellsPerSide,
        };

        [Test]
        public void SnapRelease_OnBoardNearCellCenter_SnapsToCellCenterAndUsesGivenYaw()
        {
            var c = DefaultConfig();
            GeisterSnapLogic.CellCenter(2, 4, in c, out float cx, out float cz);
            // セル中心から半ピッチ未満の微小オフセット（実際のリリース位置を想定）
            var pos = new Vector3(cx + 0.005f, 0.1f, cz - 0.004f);
            // 盤上ヒット時は rot は無視され onBoardYawDeg がそのまま返る仕様なので、あえて無関係な回転を渡す
            var rot = Quaternion.Euler(0f, 123f, 0f);

            var r = GeisterSnapLogic.SnapRelease(pos, rot, in c, occupiedMask: 0UL, onBoardYawDeg: 77.5f);

            Assert.IsTrue(r.onBoard);
            Assert.AreEqual(2, r.col);
            Assert.AreEqual(4, r.row);
            Assert.That(r.x, Is.EqualTo(cx).Within(1e-4f));
            Assert.That(r.z, Is.EqualTo(cz).Within(1e-4f));
            Assert.That(r.yawDeg, Is.EqualTo(77.5f).Within(1e-4f), "盤上は onBoardYawDeg をそのまま返すはず");
        }

        [Test]
        public void SnapRelease_OffsetTowardCellBoundary_StillRoundsToNearestCell()
        {
            var c = DefaultConfig();
            GeisterSnapLogic.CellCenter(4, 1, in c, out float cx, out float cz);
            float halfPitch = Pitch * 0.5f;
            // 隣セルとの境界にかなり近いが、まだ (4,1) の方が近い位置
            var pos = new Vector3(cx + halfPitch * 0.9f, 0f, cz);

            var r = GeisterSnapLogic.SnapRelease(pos, Quaternion.identity, in c, occupiedMask: 0UL, onBoardYawDeg: 0f);

            Assert.IsTrue(r.onBoard);
            Assert.AreEqual(4, r.col);
            Assert.AreEqual(1, r.row);
            // 丸め後は入力位置ではなくセル中心ちょうどへ吸着する
            Assert.That(r.x, Is.EqualTo(cx).Within(1e-4f));
            Assert.That(r.z, Is.EqualTo(cz).Within(1e-4f));
        }

        [Test]
        public void SnapRelease_JustOutsideFootprint_KeepsWorldXZAndReportsOffBoard()
        {
            var c = DefaultConfig();
            float half = CellsPerSide * 0.5f * Pitch;
            var pos = new Vector3(c.boardCenterX + half + 0.001f, 0.1f, c.boardCenterZ);

            var r = GeisterSnapLogic.SnapRelease(pos, Quaternion.identity, in c, occupiedMask: 0UL, onBoardYawDeg: 0f);

            Assert.IsFalse(r.onBoard);
            Assert.AreEqual(-1, r.col);
            Assert.AreEqual(-1, r.row);
            Assert.That(r.x, Is.EqualTo(pos.x).Within(1e-5f));
            Assert.That(r.z, Is.EqualTo(pos.z).Within(1e-5f));
        }

        [Test]
        public void SnapRelease_OffBoardWithYaw45_ReturnsApproxYaw45()
        {
            var c = DefaultConfig();
            var pos = new Vector3(c.boardCenterX + 5f, 0.1f, c.boardCenterZ); // 盤から十分離れた盤外
            var rot = Quaternion.Euler(0f, 45f, 0f);

            // onBoardYawDeg にわざと無関係な値を渡し、盤外パスで使われていないことも兼ねて検証
            var r = GeisterSnapLogic.SnapRelease(pos, rot, in c, occupiedMask: 0UL, onBoardYawDeg: 999f);

            Assert.IsFalse(r.onBoard);
            Assert.That(r.yawDeg, Is.EqualTo(45f).Within(0.05f));
        }

        [Test]
        public void SnapRelease_OffBoardWithNearVerticalOrientation_YawIsFiniteNotNaN()
        {
            var c = DefaultConfig();
            var pos = new Vector3(c.boardCenterX + 5f, 0.1f, c.boardCenterZ);
            // 駒が真上向きに近い姿勢で持たれた状態（forward がほぼ鉛直）を再現
            var rot = Quaternion.Euler(90f, 30f, 0f);

            var r = GeisterSnapLogic.SnapRelease(pos, rot, in c, occupiedMask: 0UL, onBoardYawDeg: 0f);

            Assert.IsFalse(r.onBoard);
            Assert.IsFalse(float.IsNaN(r.yawDeg), "forward がほぼ鉛直でも yaw が NaN になってはいけない");
            Assert.IsFalse(float.IsInfinity(r.yawDeg));
        }

        [Test]
        public void SnapRelease_TargetCellOccupied_SnapsToNearestFreeNeighbor()
        {
            var c = DefaultConfig();
            GeisterSnapLogic.CellCenter(3, 3, in c, out float cx, out float cz);
            ulong occupied = 1UL << GeisterSnapLogic.CellIndex(3, 3, in c);
            var pos = new Vector3(cx, 0f, cz);

            var r = GeisterSnapLogic.SnapRelease(pos, Quaternion.identity, in c, occupied, onBoardYawDeg: 0f);

            Assert.IsTrue(r.onBoard);
            // 上下左右4隣接はすべて等距離。走査順（row 昇順→col 昇順）で最初に見つかる (col=3,row=2) が正解
            Assert.AreEqual(3, r.col);
            Assert.AreEqual(2, r.row);
        }

        [Test]
        public void SnapRelease_MultipleEquidistantFreeCells_PicksDeterministicScanOrderWinner()
        {
            var c = DefaultConfig();
            GeisterSnapLogic.CellCenter(3, 3, in c, out float cx, out float cz);
            // 目標セルと北隣を埋め、西・東・南の3方向を等距離の空きセルとして残す
            ulong occupied = 0UL;
            occupied |= 1UL << GeisterSnapLogic.CellIndex(3, 3, in c);
            occupied |= 1UL << GeisterSnapLogic.CellIndex(3, 2, in c); // 北
            var pos = new Vector3(cx, 0f, cz);

            var r1 = GeisterSnapLogic.SnapRelease(pos, Quaternion.identity, in c, occupied, onBoardYawDeg: 0f);
            var r2 = GeisterSnapLogic.SnapRelease(pos, Quaternion.identity, in c, occupied, onBoardYawDeg: 0f);

            // row=3 が row=4 より先に走査されるため、西(col=2,row=3)が勝つ
            Assert.AreEqual(2, r1.col);
            Assert.AreEqual(3, r1.row);
            // 同じ入力なら常に同じ結果（決定的）
            Assert.AreEqual(r1.col, r2.col);
            Assert.AreEqual(r1.row, r2.row);
            Assert.That(r2.x, Is.EqualTo(r1.x).Within(1e-6f));
            Assert.That(r2.z, Is.EqualTo(r1.z).Within(1e-6f));
        }

        [Test]
        public void SnapRelease_AllCellsOccupied_FallsBackToNearestCellEvenIfOccupied()
        {
            // 全占有時は「重なり許容で素の最寄りセル」へフォールバックする仕様
            //（駒 8 体 × 36 セル運用では到達しない経路。スタックせず必ず着地することだけ保証する）
            var c = DefaultConfig();
            GeisterSnapLogic.CellCenter(2, 2, in c, out float cx, out float cz);
            ulong allOccupied = (1UL << (CellsPerSide * CellsPerSide)) - 1UL; // 36 セル全占有
            var pos = new Vector3(cx, 0f, cz);

            var r = GeisterSnapLogic.SnapRelease(pos, Quaternion.identity, in c, allOccupied, onBoardYawDeg: 12f);

            Assert.IsTrue(r.onBoard, "全セル占有でも盤上判定は維持される（フォールバック）");
            Assert.AreEqual(2, r.col);
            Assert.AreEqual(2, r.row);
            Assert.That(r.x, Is.EqualTo(cx).Within(1e-4f));
            Assert.That(r.z, Is.EqualTo(cz).Within(1e-4f));
        }

        [Test]
        public void TryGetCell_InsideFootprint_ReturnsTrueAndCellMatchesFormula()
        {
            var c = DefaultConfig();
            for (int row = 0; row < CellsPerSide; row++)
            {
                for (int col = 0; col < CellsPerSide; col++)
                {
                    float expectedX = c.boardCenterX + (col - 2.5f) * Pitch;
                    float expectedZ = c.boardCenterZ + (row - 2.5f) * Pitch;

                    bool found = GeisterSnapLogic.TryGetCell(expectedX, expectedZ, in c, out int gotCol, out int gotRow);

                    Assert.IsTrue(found, $"col={col},row={row} は盤内のはず");
                    Assert.AreEqual(col, gotCol);
                    Assert.AreEqual(row, gotRow);
                }
            }
        }

        [Test]
        public void TryGetCell_OnFootprintBoundary_IsTreatedAsInside()
        {
            var c = DefaultConfig();
            float half = CellsPerSide * 0.5f * Pitch;

            bool found = GeisterSnapLogic.TryGetCell(c.boardCenterX + half, c.boardCenterZ, in c, out _, out _);

            Assert.IsTrue(found, "境界ちょうどは内側扱いのはず（doc comment 準拠）");
        }

        [Test]
        public void CellCenter_CellIndex_TryGetCell_RoundTripIsConsistent()
        {
            var c = DefaultConfig();
            for (int row = 0; row < CellsPerSide; row++)
            {
                for (int col = 0; col < CellsPerSide; col++)
                {
                    GeisterSnapLogic.CellCenter(col, row, in c, out float x, out float z);
                    Assert.IsTrue(GeisterSnapLogic.TryGetCell(x, z, in c, out int backCol, out int backRow));
                    Assert.AreEqual(col, backCol);
                    Assert.AreEqual(row, backRow);

                    int index = GeisterSnapLogic.CellIndex(col, row, in c);
                    Assert.AreEqual(row * CellsPerSide + col, index);
                }
            }
        }

        [Test]
        public void ExtractYawDeg_HorizontalRotation_MatchesEulerYaw()
        {
            foreach (float yaw in new[] { 0f, 45f, 90f, 135f, 180f, -45f, 270f })
            {
                float got = GeisterSnapLogic.ExtractYawDeg(Quaternion.Euler(0f, yaw, 0f));
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(got, yaw)), Is.LessThan(0.05f),
                    $"yaw={yaw} で抽出結果が一致しない (got={got})");
            }
        }

        [Test]
        public void ExtractYawDeg_ForwardNearVertical_FallsBackToUpAxisWithoutNaN()
        {
            // forward がほぼ真上/真下を向く姿勢（ExtractYawDeg 内の分岐条件 sqrMagnitude<1e-6 を突く）。
            // forward=(0,0,1) は元々 x=0 なので、X 軸回りの純 ±90°回転は回転方向の符号に関わらず
            // 必ず forward を Y 軸上（鉛直）へ落とす。回転の handedness に依存しない安全な構成。
            var noseDown = Quaternion.Euler(90f, 0f, 0f);
            var noseUp = Quaternion.Euler(-90f, 0f, 0f);

            float yawDown = GeisterSnapLogic.ExtractYawDeg(noseDown);
            float yawUp = GeisterSnapLogic.ExtractYawDeg(noseUp);

            Assert.IsFalse(float.IsNaN(yawDown));
            Assert.IsFalse(float.IsInfinity(yawDown));
            Assert.IsFalse(float.IsNaN(yawUp));
            Assert.IsFalse(float.IsInfinity(yawUp));
        }
    }
}
