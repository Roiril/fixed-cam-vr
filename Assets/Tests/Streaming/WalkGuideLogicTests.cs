#nullable enable

using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 歩行誘導（<c>canon/LEDGER.md</c> 0079）の段と、道筋の解き方。
    ///
    /// ⚠ ここが守るのは<b>沈黙して壊れる 3 つ</b>:
    /// ①出す前の滞在を数えて勝手に始まる ②着かないまま永久に待つ
    /// ③床も壁も未著作なのに道筋を捏造して、実物に無い所へ歩かせる。
    /// </summary>
    public class WalkGuideLogicTests
    {
        private const float Dt = 1f / 60f;

        private static WalkGuideInput In(bool wanted, float dist, float r = 0.35f, float dt = Dt)
            => new WalkGuideInput
            { wanted = wanted, posValid = true, distM = dist, radiusM = r, dt = dt };

        private static void Run(WalkGuideLogic l, float sec, bool wanted, float dist)
        {
            for (float t = 0f; t < sec; t += Dt) l.Tick(In(wanted, dist));
        }

        // ---- 段 -------------------------------------------------------------

        [Test]
        public void Stages_RunInOrder_WhenWanted()
        {
            var l = new WalkGuideLogic();
            Assert.AreEqual(WalkGuideStage.Off, l.Stage);

            l.Tick(In(true, 9f));
            Assert.AreEqual(WalkGuideStage.SpotIn, l.Stage, "出したいと言われたら円から出る");

            Run(l, WalkGuideLogic.SpotInSec, true, 9f);
            Assert.AreEqual(WalkGuideStage.Trail, l.Stage, "円が出たら矢印が点き始める");

            Run(l, WalkGuideLogic.TrailSec, true, 9f);
            Assert.AreEqual(WalkGuideStage.Hold, l.Stage, "点き切ったら流れだけが回る");
            Assert.IsTrue(l.Directing, "Hold のあいだは『円へ行け』と言い切っている");
        }

        [Test]
        public void Weights_ShowSpotBeforeArrow()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec * 0.5f, true, 9f);
            WalkGuideWeights w = l.Weights;
            Assert.Greater(w.spot, 0f, "円は出ている");
            Assert.AreEqual(0f, w.arrow, 1e-4f, "行き先が決まる前に矢印は出さない");
        }

        [Test]
        public void Arriving_ClosesTheRing_AndPullsTheArrowFirst()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + 0.5f, true, 9f);
            Assert.AreEqual(WalkGuideStage.Hold, l.Stage);

            Run(l, WalkGuideLogic.ArriveHoldSec + Dt, true, 0.05f);
            Assert.IsTrue(l.Arrived);
            Assert.AreEqual(WalkGuideStage.Arrive, l.Stage);
            Assert.IsFalse(l.Directing, "着いたらもう『行け』とは言っていない");

            Run(l, WalkGuideLogic.ArriveSec * 0.6f, true, 0.05f);
            WalkGuideWeights w = l.Weights;
            Assert.Greater(w.arrive, 0f, "輪が閉じ始めている");
            Assert.Less(w.arrow, 1f, "矢印は先に引く");
            Assert.AreEqual(1f, w.spot, 1e-4f, "輪はまだ濃い");
        }

        [Test]
        public void Arrived_NeverGoesBack()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + 0.2f, true, 9f);
            Run(l, WalkGuideLogic.ArriveHoldSec + Dt, true, 0.05f);
            Assert.IsTrue(l.Arrived);
            Run(l, 2f, true, 9f);       // 円から出ても
            Assert.IsTrue(l.Arrived, "着いたことは取り消せない（縁で 1 回だけ導入が始まる）");
        }

        // ---- 着いたの数え方 --------------------------------------------------

        [Test]
        public void Dwell_IsNotCounted_BeforeTheGuideDirects()
        {
            var l = new WalkGuideLogic();
            // 出す前（Off）に円の中に立っていても数えない。
            for (float t = 0f; t < 3f; t += Dt) l.Tick(In(false, 0f));
            Assert.IsFalse(l.Arrived, "言う前の滞在を持ち越すと、円の上に置いた HMD で勝手に始まる");
            Assert.AreEqual(WalkGuideStage.Off, l.Stage);
        }

        [Test]
        public void Dwell_NeedsToBeContinuous()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + 0.2f, true, 9f);
            // 通りすがり: 円の中と外を行き来する
            for (int i = 0; i < 40; i++)
            {
                l.Tick(In(true, 0.05f));
                l.Tick(In(true, 1.2f));
            }
            Assert.IsFalse(l.Arrived, "通りすがりで始めない");
        }

        [Test]
        public void Dwell_HasHysteresis_AtTheEdge()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + 0.2f, true, 9f);
            // 縁でわずかに震える（半径 0.35 に対して 0.34 ↔ 0.40）
            for (float t = 0f; t < WalkGuideLogic.ArriveHoldSec + 0.2f; t += Dt * 2f)
            {
                l.Tick(In(true, 0.34f));
                l.Tick(In(true, 0.40f));
            }
            Assert.IsTrue(l.Arrived, "縁の震えで数えが 0 へ戻ると、体験者は永久に着けない");
        }

        [Test]
        public void Dwell_IgnoresJumpedFrames()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + 0.2f, true, 9f);
            // HMD の着脱・再開で 1 フレームが 3 秒ぶん飛ぶ
            l.Tick(In(true, 0.05f, dt: 3f));
            Assert.IsFalse(l.Arrived, "飛んだフレームで滞在を成立させない");
        }

        // ---- 諦める ---------------------------------------------------------

        [Test]
        public void TimesOut_AndHandsBackToTheOldStartRule()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + WalkGuideLogic.TrailSec + 0.2f, true, 9f);
            Assert.IsTrue(l.Directing);

            Run(l, WalkGuideLogic.HoldMaxSec + 0.2f, true, 9f);
            Assert.IsTrue(l.TimedOut, "着かないまま上限を超えたら諦める");
            Assert.IsFalse(l.Directing, "諦めたら接近・安全網・救済が生き返る");
            Assert.IsFalse(l.Arrived);
        }

        [Test]
        public void Wanted_False_PullsItBack()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + 0.2f, true, 9f);
            l.Tick(In(false, 9f));
            Assert.AreEqual(WalkGuideStage.Out, l.Stage);
            Run(l, WalkGuideLogic.OutSec + 0.2f, false, 9f);
            Assert.AreEqual(WalkGuideStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Done_DoesNotComeBack_WithoutReset()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.SpotInSec + 0.2f, true, 9f);
            Run(l, WalkGuideLogic.OutSec + 0.2f, false, 9f);
            Assert.AreEqual(WalkGuideStage.Done, l.Stage);
            Run(l, 1f, true, 9f);
            Assert.AreEqual(WalkGuideStage.Done, l.Stage, "体験者の目の前で誘導が点滅しない");

            l.Reset();
            l.Tick(In(true, 9f));
            Assert.AreEqual(WalkGuideStage.SpotIn, l.Stage, "ラン開始でやり直せる");
        }

        // ---- 道筋 -----------------------------------------------------------

        private static ShowLayoutDef LWallLayout()
        {
            // 現行 show.json と同じ形（床 1.8m 四方・北の腕と西の腕が (-0.5, +0.5) で交わる L）。
            return new ShowLayoutDef
            {
                floor = new ShowFloorDef { w = 1.8f, d = 1.8f },
                hasRoom = true,
                room = new ShowRoomDef
                {
                    floorY = 0f, floorW = 1.8f, floorD = 1.8f,
                    walls = new[]
                    {
                        new ShowRoomWallDef { id = "w1", x1 = -0.5f, z1 = 0.5f, x2 = 0.09f, z2 = 0.5f, h = 1.8f },
                        new ShowRoomWallDef { id = "w2", x1 = -0.5f, z1 = 0.5f, x2 = -0.5f, z2 = -0.72f, h = 1.8f },
                    },
                },
            };
        }

        [Test]
        public void Path_PutsTheSpot_WhereTheTwoLanesMeet()
        {
            ShowLayoutDef layout = LWallLayout();
            WalkGuidePath.Path p = WalkGuidePath.Solve(layout, layout.room);
            Assert.IsTrue(p.valid);
            Assert.IsFalse(p.spotAuthored, "卓が置いていないので導出");
            // 角 (-0.5, 0.5) から内側へ LaneOffsetM ずつ ＝ 2 本の道の交点。
            Assert.AreEqual(-0.5f + WalkGuidePath.LaneOffsetM, p.spot.x, 1e-3f);
            Assert.AreEqual(0.5f - WalkGuidePath.LaneOffsetM, p.spot.y, 1e-3f);
        }

        [Test]
        public void Path_RunsTheArrowFromTheEast()
        {
            ShowLayoutDef layout = LWallLayout();
            WalkGuidePath.Path p = WalkGuidePath.Solve(layout, layout.room);
            Assert.IsTrue(p.hasArrow);
            Assert.Greater(p.from.x, p.spot.x, "起点は円より東（ユーザー指定「東から壁沿いに」）");
            Assert.AreEqual(p.spot.y, p.from.y, 1e-3f, "壁沿いの 1 本の道（北の腕に平行）");
            Assert.AreEqual(1.8f * 0.5f + WalkGuidePath.StartOutsideM, p.from.x, 1e-3f,
                            "起点は床の縁の少し外（体験者はそこに立っている）");
        }

        [Test]
        public void Path_FabricatesNothing_WithoutFloorOrWalls()
        {
            Assert.IsFalse(WalkGuidePath.Solve(null, null).valid,
                           "床も部屋も無いのに道筋を作ると、実物に無い所へ歩かせる");
            var floorOnly = new ShowLayoutDef { floor = new ShowFloorDef { w = 1.8f, d = 1.8f } };
            Assert.IsFalse(WalkGuidePath.Solve(floorOnly, null).valid,
                           "壁の角も開始位置も無ければ円の置き場が決まらない");
        }

        [Test]
        public void Path_PrefersTheAuthoredSpot()
        {
            ShowLayoutDef layout = LWallLayout();
            layout.hasStartSpot = true;
            layout.startSpot = new ShowStartSpotDef { x = 0.25f, z = -0.3f, radiusM = 0.5f, label = "立つ所" };
            WalkGuidePath.Path p = WalkGuidePath.Solve(layout, layout.room);
            Assert.IsTrue(p.spotAuthored, "人が置いた円を導出で上書きしない");
            Assert.AreEqual(0.25f, p.spot.x, 1e-3f);
            Assert.AreEqual(-0.3f, p.spot.y, 1e-3f);
            Assert.AreEqual(0.5f, p.radiusM, 1e-3f);
        }

        [Test]
        public void InwardNormal_PointsIntoTheFloor()
        {
            // 北の腕（z = +0.5・角から東へ）の内側は -z 側。
            Vector2 n = WalkGuidePath.InwardNormal(new Vector2(-0.5f, 0.5f), new Vector2(0.09f, 0.5f));
            Assert.Less(n.y, -0.9f);
            // 西の腕（x = -0.5・角から南へ）の内側は +x 側。
            Vector2 n2 = WalkGuidePath.InwardNormal(new Vector2(-0.5f, 0.5f), new Vector2(-0.5f, -0.72f));
            Assert.Greater(n2.x, 0.9f);
        }
    }
}
