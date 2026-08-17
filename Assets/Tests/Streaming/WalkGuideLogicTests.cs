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

        /// <summary>
        /// ⚠ <paramref name="told"/> の既定は true（＝エージェントが説明を始めた後）。
        /// 説明を待つ側の挙動は下の 2 本だけが false を渡す。
        /// </summary>
        private static WalkGuideInput In(bool wanted, float dist, float r = 0.35f, float dt = Dt,
                                         bool told = true)
            => new WalkGuideInput
            { wanted = wanted, told = told, posValid = true, distM = dist, radiusM = r, dt = dt };

        private static void Run(WalkGuideLogic l, float sec, bool wanted, float dist, bool told = true)
        {
            for (float t = 0f; t < sec; t += Dt) l.Tick(In(wanted, dist, told: told));
        }

        /// <summary>矢印が出切って、円も開き切るまで（＝ Hold に居る）。</summary>
        private const float ThroughSpotIn = WalkGuideLogic.TrailSec + WalkGuideLogic.SpotInSec;

        // ---- 段 -------------------------------------------------------------

        [Test]
        public void Stages_RunInOrder_WhenWanted()
        {
            var l = new WalkGuideLogic();
            Assert.AreEqual(WalkGuideStage.Off, l.Stage);

            l.Tick(In(true, 9f));
            Assert.AreEqual(WalkGuideStage.Trail, l.Stage, "説明が始まったら矢印から出る");

            Run(l, WalkGuideLogic.TrailSec, true, 9f);
            Assert.AreEqual(WalkGuideStage.SpotIn, l.Stage, "矢印が全部出たら円が開き始める");

            Run(l, WalkGuideLogic.SpotInSec, true, 9f);
            Assert.AreEqual(WalkGuideStage.Hold, l.Stage, "出し切ったら流れだけが回る");
            Assert.IsTrue(l.Directing, "Hold のあいだは『円へ行け』と言い切っている");
        }

        [Test]
        public void Weights_ShowArrowBeforeSpot()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.TrailSec * 0.5f, true, 9f);
            WalkGuideWeights w = l.Weights;
            Assert.Greater(w.arrow, 0f, "矢印は出ている");
            Assert.Greater(w.reveal, 0f, "山形が手前から点き始めている");
            Assert.Less(w.reveal, 1f, "まだ全部は出ていない");
            Assert.AreEqual(0f, w.spot, 1e-4f,
                            "矢印が全部出るまで円は 1 画素も出さない（0079 の赤入れ 4）");
            Assert.AreEqual(0f, w.ring, 1e-4f);
        }

        [Test]
        public void Spot_OpensOnlyAfterEveryChevronIsOut()
        {
            var l = new WalkGuideLogic();
            // 矢印が出切る手前まで: 円は 1 画素も無い。
            for (float t = 0f; t < WalkGuideLogic.TrailSec - Dt * 2f; t += Dt)
            {
                l.Tick(In(true, 9f));
                Assert.AreEqual(0f, l.Weights.spot, 1e-4f, $"{t:0.00}s で円が出ている");
            }
            Assert.AreEqual(1f, l.Weights.reveal, 0.05f, "この時点で山形はほぼ全部出ている");

            Run(l, WalkGuideLogic.SpotInSec * 0.4f, true, 9f);
            WalkGuideWeights w = l.Weights;
            Assert.AreEqual(WalkGuideStage.SpotIn, l.Stage);
            Assert.AreEqual(1f, w.reveal, 1e-4f, "円が開くあいだ、矢印は出たまま");
            Assert.Greater(w.spot, 0f, "円が開き始めている");
            Assert.Less(w.ring, 1f, "まだ開き切っていない（中心から広がっている途中）");
        }

        // ---- 説明と対で出す ---------------------------------------------------

        [Test]
        public void NothingComesOut_UntilTheAgentStartsExplaining()
        {
            var l = new WalkGuideLogic();
            Run(l, 5f, true, 9f, told: false);
            Assert.AreEqual(WalkGuideStage.Off, l.Stage,
                            "説明の前に矢印が出ると、指示と画が別々の出来事になる");
            Assert.AreEqual(0f, l.Weights.arrow, 1e-4f);

            l.Tick(In(true, 9f, told: true));
            Assert.AreEqual(WalkGuideStage.Trail, l.Stage, "説明が始まったら出る");
        }

        [Test]
        public void ComesOutAnyway_WhenTheAgentNeverSpeaks()
        {
            var l = new WalkGuideLogic();
            Run(l, WalkGuideLogic.TellTimeoutSec - 0.5f, true, 9f, told: false);
            Assert.AreEqual(WalkGuideStage.Off, l.Stage, "保険が先に発火すると順序が崩れる");

            Run(l, 1f, true, 9f, told: false);
            Assert.AreEqual(WalkGuideStage.Trail, l.Stage,
                            "面が組めない現場でも、待たされたら誘導だけは出す");
        }

        [Test]
        public void TheWaitDoesNotCarryOver_WhileTheGuideIsNotWanted()
        {
            var l = new WalkGuideLogic();
            // 出せない状態（タイトルが立っている等）で長く待っても、保険の時計は進まない。
            Run(l, WalkGuideLogic.TellTimeoutSec * 2f, false, 9f, told: false);
            Run(l, 1f, true, 9f, told: false);
            Assert.AreEqual(WalkGuideStage.Off, l.Stage,
                            "出せなかった時間まで数えると、出せるようになった瞬間に飛び出す");
        }

        [Test]
        public void Arriving_ClosesTheRing_AndPullsTheArrowFirst()
        {
            var l = new WalkGuideLogic();
            Run(l, ThroughSpotIn + 0.5f, true, 9f);
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
            Run(l, ThroughSpotIn + 0.2f, true, 9f);
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
            Run(l, ThroughSpotIn + 0.2f, true, 9f);
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
            Run(l, ThroughSpotIn + 0.2f, true, 9f);
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
            Run(l, ThroughSpotIn + 0.2f, true, 9f);
            // HMD の着脱・再開で 1 フレームが 3 秒ぶん飛ぶ
            l.Tick(In(true, 0.05f, dt: 3f));
            Assert.IsFalse(l.Arrived, "飛んだフレームで滞在を成立させない");
        }

        // ---- 諦める ---------------------------------------------------------

        [Test]
        public void TimesOut_AndHandsBackToTheOldStartRule()
        {
            var l = new WalkGuideLogic();
            Run(l, ThroughSpotIn + 0.2f, true, 9f);
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
            Assert.AreEqual(WalkGuideStage.Trail, l.Stage, "ラン開始でやり直せる");
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
            Assert.AreEqual(WalkGuidePath.SpotRadiusM, p.radiusM, 1e-4f,
                            "描く輪と判定の円は同じ半径（別にすると指示が嘘になる）");
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
