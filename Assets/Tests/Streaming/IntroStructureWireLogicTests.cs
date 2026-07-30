#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 導入演出 段 3（構造の線）の course 空間計算を固定する。
    ///
    /// この線は<b>現実に直接重なる</b>ので、幾何が狂うと体験者には「位置合わせがズレている」に見える
    /// （スタッフはそこで中止して再登録する — 計画 §8-6）。実機の目でしか気づけないので数値で止める。
    /// 設計の正本: .claude/plans/2026-07-30_intro-passthrough-to-screen.md §4
    /// </summary>
    public sealed class IntroStructureWireLogicTests
    {
        private static ShowLayoutDef Layout(float w, float d)
            => new ShowLayoutDef { floor = new ShowFloorDef { w = w, d = d } };

        private static ShowRoomDef Room(params ShowRoomWallDef[] walls)
            => new ShowRoomDef { floorW = 1.8f, floorD = 1.8f, walls = walls };

        private static ShowRoomWallDef Wall(float x1, float z1, float x2, float z2,
                                           float h = 1f, float thick = 0.04f)
            => new ShowRoomWallDef { x1 = x1, z1 = z1, x2 = x2, z2 = z2, h = h, thick = thick };

        private static List<IntroStructureWireLogic.CameraMark> Marks(
            params IntroStructureWireLogic.CameraMark[] m) => new(m);

        private static IntroStructureWireLogic.CameraMark Mark(
            float x, float y, float z, float yawDeg = 0f, float pitchDeg = 0f)
            => new IntroStructureWireLogic.CameraMark
            {
                coursePos = new Vector3(x, y, z), yawDeg = yawDeg, pitchDeg = pitchDeg,
            };

        // ---- 何も著作されていないときは何も描かない ----

        [Test]
        public void Build_NoLayoutNoRoom_DrawsNothing()
        {
            var lines = IntroStructureWireLogic.Build(null, null, null,
                includeRoom: true, includeCameras: true);
            Assert.AreEqual(0, lines.Count,
                "床も部屋も無いなら 1.8m 四方を捏造せず何も描かない（較正 UI で潰した捏造を作り直さない）");
        }

        [Test]
        public void Build_FloorTooSmall_DrawsNothing()
        {
            var lines = IntroStructureWireLogic.Build(Layout(0.01f, 0.01f), null, null,
                includeRoom: true, includeCameras: false);
            Assert.AreEqual(0, lines.Count);
        }

        // ---- 床の外周 ----

        [Test]
        public void AppendFloor_FromLayoutFloor_CentersOnCourseOrigin()
        {
            var lines = IntroStructureWireLogic.Build(Layout(2f, 3f), null, null,
                includeRoom: true, includeCameras: false);
            Assert.AreEqual(1, lines.Count, "床は閉ループ 1 本");
            Assert.IsTrue(lines[0].loop);
            Vector3[] p = lines[0].points;
            Assert.AreEqual(4, p.Length);
            Assert.AreEqual(-1f, p[0].x, 1e-4f);
            Assert.AreEqual(-1.5f, p[0].z, 1e-4f);
            Assert.AreEqual(1f, p[2].x, 1e-4f);
            Assert.AreEqual(1.5f, p[2].z, 1e-4f);
            foreach (Vector3 v in p) Assert.AreEqual(0f, v.y, 1e-4f, "床の高さは course y=0");
        }

        [Test]
        public void AppendFloor_FallsBackToRoomExtents_AndFloorY()
        {
            ShowRoomDef room = Room();
            room.floorW = 2.4f; room.floorD = 1.2f; room.floorY = 0.05f;
            var lines = IntroStructureWireLogic.Build(new ShowLayoutDef(), room, null,
                includeRoom: true, includeCameras: false);
            Assert.AreEqual(1, lines.Count, "壁ゼロの部屋なら床だけ");
            Assert.AreEqual(1.2f, lines[0].points[2].x, 1e-4f);
            Assert.AreEqual(0.6f, lines[0].points[2].z, 1e-4f);
            Assert.AreEqual(0.05f, lines[0].points[0].y, 1e-4f, "床の高さは room.floorY");
        }

        [Test]
        public void TryFloorExtents_PrefersLayoutFloorOverRoom()
        {
            ShowRoomDef room = Room();
            bool ok = IntroStructureWireLogic.TryFloorExtents(Layout(2f, 2f), room,
                out float w, out float d);
            Assert.IsTrue(ok);
            Assert.AreEqual(2f, w, 1e-4f);
            Assert.AreEqual(2f, d, 1e-4f);
        }

        // ---- 壁・箱の輪郭 ----

        [Test]
        public void AppendRoom_WallBecomesSixPolylines()
        {
            var lines = IntroStructureWireLogic.Build(new ShowLayoutDef(), Room(Wall(-0.5f, 0.5f, 0.5f, 0.5f)),
                null, includeRoom: true, includeCameras: false);
            // 床 1 本 + 壁 1 枚 = 6 本（床輪郭・上端輪郭・縦の稜線 4 本）
            Assert.AreEqual(7, lines.Count);
            Assert.IsTrue(lines[1].loop, "箱の床の輪郭は閉ループ");
            Assert.IsTrue(lines[2].loop, "箱の上端の輪郭は閉ループ");
            for (int i = 3; i < 7; i++)
            {
                Assert.IsFalse(lines[i].loop, "縦の稜線は開いた 2 点");
                Assert.AreEqual(2, lines[i].points.Length);
            }
        }

        [Test]
        public void AppendRoom_DegenerateWallSkipped()
        {
            var lines = IntroStructureWireLogic.Build(new ShowLayoutDef(),
                Room(Wall(0f, 0f, 0f, 0f), Wall(-0.5f, 0.5f, 0.5f, 0.5f)),
                null, includeRoom: true, includeCameras: false);
            Assert.AreEqual(7, lines.Count, "潰れた壁は ShowRoomProxyLogic と同じく捨てる");
        }

        [Test]
        public void AppendBox_BottomAtFloor_TopAtHeight()
        {
            var into = new List<IntroStructureWireLogic.Polyline>();
            ShowRoomProxyLogic.Box b = ShowRoomProxyLogic.WallBox(Wall(-0.5f, 0.5f, 0.5f, 0.5f, h: 1.2f), 0f);
            IntroStructureWireLogic.AppendBox(into, b);

            Assert.AreEqual(6, into.Count);
            foreach (Vector3 v in into[0].points)
                Assert.AreEqual(0f, v.y, 1e-4f, "床の輪郭は floorY の高さ");
            foreach (Vector3 v in into[1].points)
                Assert.AreEqual(1.2f, v.y, 1e-4f, "上端の輪郭は floorY + h");

            // 長さ方向（ローカル +X）は x = ±0.5、厚み方向は z = 0.5 ± 0.02。
            Assert.AreEqual(-0.5f, into[0].points[0].x, 1e-4f);
            Assert.AreEqual(0.5f, into[0].points[1].x, 1e-4f);
            Assert.AreEqual(0.48f, into[0].points[0].z, 1e-4f);
            Assert.AreEqual(0.52f, into[0].points[2].z, 1e-4f);
        }

        [Test]
        public void BoxCorner_Yaw90_MatchesProxyRotationConvention()
        {
            // ShowRoomProxyLogic と同じ規約: Euler(0,yaw,0)*right == (cos yaw, 0, -sin yaw)。
            // yaw=90° ならローカル +X は course -Z を向く。
            var b = new ShowRoomProxyLogic.Box
            {
                center = new Vector3(1f, 0.5f, 2f), size = new Vector3(2f, 1f, 0.1f), yawDeg = 90f,
            };
            Vector3 c = IntroStructureWireLogic.BoxCorner(b, 1f, 0f, 0f);
            Assert.AreEqual(1f, c.x, 1e-3f);
            Assert.AreEqual(1f, c.z, 1e-3f, "ローカル +X が course -Z へ回る");

            // Unity の実回転と一致することも確かめる（規約を文章でなく数値で縛る）。
            Vector3 expect = b.center + Quaternion.Euler(0f, b.yawDeg, 0f) * new Vector3(1f, 0f, 0f);
            Assert.AreEqual(expect.x, c.x, 1e-3f);
            Assert.AreEqual(expect.y, c.y, 1e-3f);
            Assert.AreEqual(expect.z, c.z, 1e-3f);
        }

        // ---- カメラの印 ----

        [Test]
        public void AppendCameraMark_CrossAndDirection()
        {
            var into = new List<IntroStructureWireLogic.Polyline>();
            IntroStructureWireLogic.AppendCameraMark(into, Mark(0.5f, 1.2f, -0.3f, yawDeg: 90f));

            Assert.AreEqual(4, into.Count, "3 軸の十字 + 向きの線 1 本");
            float a = IntroStructureWireLogic.CameraMarkArmM;
            Assert.AreEqual(2f * a, (into[0].points[1] - into[0].points[0]).magnitude, 1e-4f);
            Assert.AreEqual(2f * a, (into[1].points[1] - into[1].points[0]).magnitude, 1e-4f);
            Assert.AreEqual(2f * a, (into[2].points[1] - into[2].points[0]).magnitude, 1e-4f);

            Vector3 tip = into[3].points[1];
            Assert.AreEqual(0.5f + IntroStructureWireLogic.CameraMarkDirM, tip.x, 1e-4f,
                "yaw=90° は course +X を向く");
            Assert.AreEqual(1.2f, tip.y, 1e-4f);
            Assert.AreEqual(-0.3f, tip.z, 1e-4f);
        }

        [Test]
        public void Forward_MatchesShowCgLayerRotationConvention()
        {
            // ShowCgLayer.ApplyCameraPose の Quaternion.Euler(-pitch, yaw, 0) * forward と一致させる。
            foreach ((float yaw, float pitch) in new[] { (0f, 0f), (90f, 0f), (-35f, -20f), (200f, 15f) })
            {
                Vector3 got = IntroStructureWireLogic.Forward(yaw, pitch);
                Vector3 expect = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
                Assert.AreEqual(expect.x, got.x, 1e-3f, $"yaw={yaw} pitch={pitch}");
                Assert.AreEqual(expect.y, got.y, 1e-3f, $"yaw={yaw} pitch={pitch}");
                Assert.AreEqual(expect.z, got.z, 1e-3f, $"yaw={yaw} pitch={pitch}");
            }
        }

        [Test]
        public void Build_CamerasOnly_SkipsRoom()
        {
            var lines = IntroStructureWireLogic.Build(Layout(2f, 2f), Room(Wall(-0.5f, 0.5f, 0.5f, 0.5f)),
                Marks(Mark(0f, 1.2f, 1f)), includeRoom: false, includeCameras: true);
            Assert.AreEqual(4, lines.Count, "床・壁は出さずカメラの印だけ");
        }

        [Test]
        public void Build_RoomOnly_SkipsCameras()
        {
            var lines = IntroStructureWireLogic.Build(Layout(2f, 2f), null,
                Marks(Mark(0f, 1.2f, 1f)), includeRoom: true, includeCameras: false);
            Assert.AreEqual(1, lines.Count, "カメラの印は出さず床だけ");
        }

        // ---- 再構築の要否判定 ----

        [Test]
        public void Same_DetectsMovedOrTurnedCamera()
        {
            IntroStructureWireLogic.CameraMark a = Mark(0f, 1.2f, 1f, yawDeg: 10f);
            Assert.IsTrue(IntroStructureWireLogic.Same(a, Mark(0f, 1.2f, 1f, yawDeg: 10f)));
            Assert.IsFalse(IntroStructureWireLogic.Same(a, Mark(0.02f, 1.2f, 1f, yawDeg: 10f)),
                "2cm 動いたら組み直す");
            Assert.IsFalse(IntroStructureWireLogic.Same(a, Mark(0f, 1.2f, 1f, yawDeg: 11f)));
            Assert.IsFalse(IntroStructureWireLogic.Same(a, Mark(0f, 1.2f, 1f, yawDeg: 10f, pitchDeg: -5f)));
        }
    }
}
