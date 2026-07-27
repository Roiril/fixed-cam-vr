#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 部屋プロキシ（<c>layout.room</c> → 不可視のオクルーダ幾何）の course 空間計算を固定する。
    ///
    /// ここが狂うと「壁の裏に居るはずの人形が見える / 壁が 90° 転んで別の場所を隠す」になる。
    /// 目で見て気づけるのは実機だけなので、**数値で止める**のがこのテストの役目。
    /// 設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.5
    /// </summary>
    public sealed class ShowRoomProxyLogicTests
    {
        private static ShowRoomWallDef Wall(float x1, float z1, float x2, float z2,
                                            float h = 1f, float thick = 0.04f)
            => new ShowRoomWallDef { x1 = x1, z1 = z1, x2 = x2, z2 = z2, h = h, thick = thick };

        // ---- 壁の線分 → ボックス ----

        [Test]
        public void WallBox_AlongX_CentersAndSizes()
        {
            // (-0.5,0.5)→(0.5,0.5) の壁: 中心は中点、長さ 1m、高さの中心は h/2。
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.WallBox(Wall(-0.5f, 0.5f, 0.5f, 0.5f), 0f);
            Assert.AreEqual(0f, b.center.x, 1e-4f);
            Assert.AreEqual(0.5f, b.center.y, 1e-4f, "床から h/2 の高さに中心が来る");
            Assert.AreEqual(0.5f, b.center.z, 1e-4f);
            Assert.AreEqual(1f, b.size.x, 1e-4f, "ローカル +X が壁の長さ方向");
            Assert.AreEqual(1f, b.size.y, 1e-4f);
            Assert.AreEqual(0.04f, b.size.z, 1e-4f, "ローカル +Z が厚み方向");
            Assert.AreEqual(0f, b.yawDeg, 1e-3f);
        }

        [Test]
        public void WallBox_Yaw_AlignsLocalXWithSegment()
        {
            // **符号を落とすと壁が 90° 転ぶ**箇所。Quaternion.Euler(0,yaw,0)*right が線分方向に一致すること。
            foreach (var seg in new[]
                     {
                         new Vector4(0f, 0f, 1f, 0f),      // +X
                         new Vector4(0f, 0f, 0f, 1f),      // +Z
                         new Vector4(0f, 0f, -1f, 0f),     // -X
                         new Vector4(0f, 0f, 0.6f, -0.8f), // 斜め
                     })
            {
                ShowRoomProxyLogic.Box b =
                    ShowRoomProxyLogic.WallBox(Wall(seg.x, seg.y, seg.z, seg.w), 0f);
                Vector3 axis = Quaternion.Euler(0f, b.yawDeg, 0f) * Vector3.right;
                Vector3 expected = new Vector3(seg.z - seg.x, 0f, seg.w - seg.y).normalized;
                Assert.AreEqual(expected.x, axis.x, 1e-3f, $"seg={seg} x");
                Assert.AreEqual(expected.z, axis.z, 1e-3f, $"seg={seg} z");
            }
        }

        [Test]
        public void WallBox_AlongZ_HasUnitLength()
        {
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.WallBox(Wall(0.5f, -0.5f, 0.5f, 0.5f), 0f);
            Assert.AreEqual(1f, b.size.x, 1e-4f);
            Assert.AreEqual(-90f, b.yawDeg, 1e-3f);
        }

        [Test]
        public void WallBox_FloorY_LiftsWholeWall()
        {
            // 床が上がれば壁も丸ごと上がる（壁は床から生える）。
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.WallBox(Wall(-1f, 0f, 1f, 0f, h: 2f), 0.3f);
            Assert.AreEqual(0.3f + 1f, b.center.y, 1e-4f);
        }

        [Test]
        public void WallBox_ZeroThickness_IsClampedToDrawable()
        {
            // 厚み 0 の箱は深度を書かず（潰れて面積ゼロ）オクルーダとして無意味。最低厚みへ寄せる。
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.WallBox(Wall(-1f, 0f, 1f, 0f, thick: 0f), 0f);
            Assert.Greater(b.size.z, 0f);
        }

        // ---- 箱（机・柱）----

        [Test]
        public void PropBox_BottomSitsOnFloor()
        {
            // y は「床からの**底面**高さ」。中心へ h/2 を足し忘れると箱が床に半分埋まる。
            var def = new ShowRoomBoxDef { x = 0.2f, z = -0.4f, y = 0f, w = 0.6f, d = 0.4f, h = 0.7f };
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.PropBox(def, 0f);
            Assert.AreEqual(0.35f, b.center.y, 1e-4f);
            Assert.AreEqual(0.2f, b.center.x, 1e-4f);
            Assert.AreEqual(-0.4f, b.center.z, 1e-4f);
            Assert.AreEqual(new Vector3(0.6f, 0.7f, 0.4f), b.size);
        }

        [Test]
        public void PropBox_FloatingBox_AddsBothOffsets()
        {
            var def = new ShowRoomBoxDef { y = 0.5f, w = 0.3f, d = 0.3f, h = 0.2f };
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.PropBox(def, 0.1f);
            Assert.AreEqual(0.1f + 0.5f + 0.1f, b.center.y, 1e-4f);
        }

        [Test]
        public void PropBox_KeepsAuthoredYaw()
        {
            var def = new ShowRoomBoxDef { yawDeg = 37f, w = 0.3f, d = 0.3f, h = 0.3f };
            Assert.AreEqual(37f, ShowRoomProxyLogic.PropBox(def, 0f).yawDeg, 1e-4f);
        }

        // ---- 部屋全体 ----

        [Test]
        public void Build_NullRoom_IsEmpty()
        {
            // 部屋が未著作でも例外を出さない（人形と影は room 無しで出す設計）。
            Assert.AreEqual(0, ShowRoomProxyLogic.Build(null).Count);
        }

        [Test]
        public void Build_SkipsDegenerateGeometry()
        {
            // 長さ 0 の壁・高さ 0 の壁・潰れた箱は捨てる（深度を書かないうえ scale 0 で Unity が警告する）。
            var room = new ShowRoomDef
            {
                walls = new[]
                {
                    Wall(0f, 0f, 0f, 0f),               // 長さ 0
                    Wall(-1f, 0f, 1f, 0f, h: 0f),       // 高さ 0
                    Wall(-1f, 0.5f, 1f, 0.5f),          // 正常
                },
                props = new[]
                {
                    new ShowRoomBoxDef { w = 0f, d = 0.3f, h = 0.3f },     // 潰れている
                    new ShowRoomBoxDef { w = 0.3f, d = 0.3f, h = 0.3f },   // 正常
                },
            };
            List<ShowRoomProxyLogic.Box> boxes = ShowRoomProxyLogic.Build(room);
            Assert.AreEqual(2, boxes.Count);
        }

        [Test]
        public void Build_OrdersWallsBeforeProps()
        {
            // 順序が安定していないと、生成済み GameObject の使い回しが毎フレーム崩れる。
            var room = new ShowRoomDef
            {
                walls = new[] { Wall(-1f, 0f, 1f, 0f, h: 1.5f) },
                props = new[] { new ShowRoomBoxDef { w = 0.4f, d = 0.4f, h = 0.4f } },
            };
            List<ShowRoomProxyLogic.Box> boxes = ShowRoomProxyLogic.Build(room);
            Assert.AreEqual(1.5f, boxes[0].size.y, 1e-4f, "先頭は壁");
            Assert.AreEqual(0.4f, boxes[1].size.y, 1e-4f, "次が箱");
        }

        [Test]
        public void Build_NullEntries_DoNotThrow()
        {
            // JsonUtility は配列要素に null を作りうる（手書き show.json も同様）。
            var room = new ShowRoomDef
            {
                walls = new ShowRoomWallDef[] { null! },
                props = new ShowRoomBoxDef[] { null! },
            };
            Assert.AreEqual(0, ShowRoomProxyLogic.Build(room).Count);
        }

        // ---- 床 ----

        [Test]
        public void FloorRect_IsCenteredOnCourseOrigin()
        {
            // layout.grid の CellRect（col0=西 x=-w/2 / row0=北 z=+d/2）と同じ置き方。
            // 揃えないと「塗ったタイルの上に部屋が乗らない」。
            var room = new ShowRoomDef { floorW = 1.8f, floorD = 2.4f };
            ShowRoomProxyLogic.FloorRect(room, out float xLo, out float xHi, out float zLo, out float zHi);
            Assert.AreEqual(-0.9f, xLo, 1e-4f);
            Assert.AreEqual(0.9f, xHi, 1e-4f);
            Assert.AreEqual(-1.2f, zLo, 1e-4f);
            Assert.AreEqual(1.2f, zHi, 1e-4f);
        }

        [Test]
        public void FloorRect_NegativeSize_DoesNotInvert()
        {
            var room = new ShowRoomDef { floorW = -1f, floorD = -1f };
            ShowRoomProxyLogic.FloorRect(room, out float xLo, out float xHi, out float zLo, out float zHi);
            Assert.LessOrEqual(xLo, xHi);
            Assert.LessOrEqual(zLo, zHi);
        }
    }
}
