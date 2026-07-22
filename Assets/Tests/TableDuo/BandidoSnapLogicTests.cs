#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;
using UnityEngine;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// BandidoSnapLogic（バンディド札リリース時スナップの純計算）の EditMode テスト。
    /// 格子ピッチ = カード短辺（0.8 倍後 ≈35.2mm）。GameObject 生成なしの純関数テストのみ。
    /// 格子原点を非ゼロにして「原点前提」の隠れバグを検出できるようにする。
    /// </summary>
    public class BandidoSnapLogicTests
    {
        private const float Pitch = 0.0352f;

        private static BandidoSnapLogic.Config DefaultConfig() => new BandidoSnapLogic.Config
        {
            gridOriginX = 0.4f,
            gridOriginZ = -0.2f,
            cellPitch = Pitch,
        };

        [Test]
        public void SnapRelease_VerticalNearIntegerGrid_SnapsToIntegerCellAndYaw0()
        {
            var c = DefaultConfig();
            // 目標: 原点 + (1p, ., 2p)。yaw ~0（縦置き）で整数格子へ丸まるはず
            var pos = new Vector3(c.gridOriginX + Pitch + 0.010f, 0.1f, c.gridOriginZ + 2f * Pitch - 0.008f);
            var rot = Quaternion.Euler(0f, 3f, 0f);

            var r = BandidoSnapLogic.SnapRelease(pos, rot, in c);

            Assert.That(r.yawDeg, Is.EqualTo(0f).Within(1e-4f), "yaw~3° は 0 に丸まる（縦置き）");
            Assert.IsTrue(r.faceUp);
            Assert.That(r.x, Is.EqualTo(c.gridOriginX + Pitch).Within(1e-4f));
            Assert.That(r.z, Is.EqualTo(c.gridOriginZ + 2f * Pitch).Within(1e-4f));
        }

        [Test]
        public void SnapRelease_HorizontalNearHalfGrid_SnapsToHalfCellAndYaw90()
        {
            var c = DefaultConfig();
            // yaw ~90（横置き）→ 半セル格子 (k+0.5)p へ丸まるはず。目標 (1.5p, 0.5p)
            var pos = new Vector3(c.gridOriginX + 1.5f * Pitch + 0.005f, 0.1f,
                c.gridOriginZ + 0.5f * Pitch - 0.004f);
            var rot = Quaternion.Euler(0f, 88f, 0f);

            var r = BandidoSnapLogic.SnapRelease(pos, rot, in c);

            Assert.That(r.yawDeg, Is.EqualTo(90f).Within(1e-4f), "yaw~88° は 90 に丸まる（横置き）");
            Assert.That(r.x, Is.EqualTo(c.gridOriginX + 1.5f * Pitch).Within(1e-4f));
            Assert.That(r.z, Is.EqualTo(c.gridOriginZ + 0.5f * Pitch).Within(1e-4f));
        }

        [Test]
        public void SnapRelease_YawRoundsToNearest90()
        {
            var c = DefaultConfig();
            var pos = new Vector3(c.gridOriginX, 0.1f, c.gridOriginZ);

            // -44° → 0
            var rMinus = BandidoSnapLogic.SnapRelease(pos, Quaternion.Euler(0f, -44f, 0f), in c);
            Assert.That(rMinus.yawDeg, Is.EqualTo(0f).Within(1e-4f));
            // 46° → 90
            var r46 = BandidoSnapLogic.SnapRelease(pos, Quaternion.Euler(0f, 46f, 0f), in c);
            Assert.That(r46.yawDeg, Is.EqualTo(90f).Within(1e-4f));
            // 359° → 0（[0,360) 正規化）
            var r359 = BandidoSnapLogic.SnapRelease(pos, Quaternion.Euler(0f, 359f, 0f), in c);
            Assert.That(r359.yawDeg, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void SnapRelease_FaceDownOrientation_PreservesFaceDown()
        {
            var c = DefaultConfig();
            var pos = new Vector3(c.gridOriginX, 0.1f, c.gridOriginZ);
            // 水平軸まわり 180° 反転（裏向き = up が下向き）。yaw は 180（縦置き）に丸まる
            var rot = Quaternion.Euler(180f, 0f, 0f);

            var r = BandidoSnapLogic.SnapRelease(pos, rot, in c);

            Assert.IsFalse(r.faceUp, "up が下向きなら裏向き保持");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(r.yawDeg, 180f)), Is.LessThan(1e-3f));
        }

        [Test]
        public void SnapRelease_AtOrigin_ReturnsGridOrigin()
        {
            var c = DefaultConfig();
            var r = BandidoSnapLogic.SnapRelease(
                new Vector3(c.gridOriginX, 0.1f, c.gridOriginZ), Quaternion.identity, in c);

            Assert.That(r.x, Is.EqualTo(c.gridOriginX).Within(1e-5f));
            Assert.That(r.z, Is.EqualTo(c.gridOriginZ).Within(1e-5f));
            Assert.That(r.yawDeg, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void SnapRelease_HorizontalAtHalfCellOffset_IsBandyFixedPoint()
        {
            // bandy は原点シフト (cx-p/2, cz-p/2) により原点相対 (0.5p, 0.5p) に yaw90 で置かれる。
            // この点は横置き規則 (k+0.5)p の不動点で、掴んで離しても動かないことの回帰。
            var c = DefaultConfig();
            var pos = new Vector3(c.gridOriginX + 0.5f * Pitch, 0.1f, c.gridOriginZ + 0.5f * Pitch);

            var r = BandidoSnapLogic.SnapRelease(pos, Quaternion.Euler(0f, 90f, 0f), in c);

            Assert.That(r.yawDeg, Is.EqualTo(90f).Within(1e-4f));
            Assert.That(r.x, Is.EqualTo(pos.x).Within(1e-4f), "bandy 位置は横置き格子の不動点（動かない）");
            Assert.That(r.z, Is.EqualTo(pos.z).Within(1e-4f));
        }

        [Test]
        public void SnapRelease_VerticalEdgesMeetHorizontalHalfGrid_TunnelsConnect()
        {
            // 縦置き原点カードの右端 (x = origin + p/2) に、横置きカードの左端が接する構成を検証。
            // 縦置き整数格子と横置き半セル格子が同じ境界線を共有することの回帰。
            var c = DefaultConfig();
            // 横置きカード（幅 2p）を原点右へ。中心 (1.5p, 0.5p) に丸まれば左端は origin + 0.5p = 縦置きカード右端
            var pos = new Vector3(c.gridOriginX + 1.4f * Pitch, 0.1f, c.gridOriginZ + 0.55f * Pitch);
            var r = BandidoSnapLogic.SnapRelease(pos, Quaternion.Euler(0f, 90f, 0f), in c);

            float horizLeftEdge = r.x - Pitch; // 横置きは長辺(2p)が X なので半幅 = p
            Assert.That(horizLeftEdge, Is.EqualTo(c.gridOriginX + 0.5f * Pitch).Within(1e-4f),
                "横置きカードの左端が縦置き原点カードの右端 (origin + p/2) と一致する");
        }
    }
}
