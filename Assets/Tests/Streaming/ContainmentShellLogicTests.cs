#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 隔離殻（会場を黒で落とし、実物の壁と足元の床だけを残す面）の幾何と交差判定を固定する。
    ///
    /// **実機でしか見えない層なので数値で止める。** 幾何を 1 つ間違えると、
    /// 体験者は「足元が黒い」「実物の壁が消えた」「会場が丸見え」のどれかになる。
    ///
    /// 実機は箱をそのままラスタライズしてステンシルで形を作るので、
    /// <see cref="ContainmentShellLogic.HitsBox"/> は**「見える範囲」の仕様の記述**
    /// （眼から出た視線が箱に当たる ⇔ その画素は箱の投影の中）。
    /// ここで固定しているのは<b>幾何の選び方</b> — 床の余白・壁の膨らませ・course の yaw。
    /// 世界観の出どころは .claude/canon/LEDGER.md 0002。
    /// </summary>
    public sealed class ContainmentShellLogicTests
    {
        private static ShowLayoutDef Layout(float w, float d)
            => new ShowLayoutDef { floor = new ShowFloorDef { w = w, d = d } };

        private static ShowRoomDef Room(params ShowRoomWallDef[] walls)
            => new ShowRoomDef { floorY = 0f, floorW = 1.8f, floorD = 1.8f, walls = walls };

        private static ShowRoomWallDef Wall(float x1, float z1, float x2, float z2,
                                            float h = 1.8f, float thick = 0.04f)
            => new ShowRoomWallDef { x1 = x1, z1 = z1, x2 = x2, z2 = z2, h = h, thick = thick };

        /// <summary>登録 identity（course = world）での world 箱へ直す。</summary>
        private static List<ContainmentShellLogic.WorldBox> ToWorld(
            IEnumerable<ContainmentShellLogic.Box> boxes)
        {
            var list = new List<ContainmentShellLogic.WorldBox>();
            foreach (ContainmentShellLogic.Box b in boxes)
                list.Add(ContainmentShellLogic.ToWorld(
                    b, (xz, y) => new Vector3(xz.x, y, xz.y), 0f));
            return list;
        }

        // ---------------------------------------------------------------- 幾何

        [Test]
        public void Build_WithoutGeometry_ReturnsNothing()
        {
            // 捏造しないのが仕様。1.8m 四方を勝手に置くと、体験者は自分の足元が消えた状態で歩く。
            Assert.AreEqual(0, ContainmentShellLogic.Build(null, null).Count);
        }

        [Test]
        public void Build_FloorOnly_IsOneSlabWithMargin()
        {
            List<ContainmentShellLogic.Box> boxes = ContainmentShellLogic.Build(Layout(1.8f, 1.8f), null);
            Assert.AreEqual(1, boxes.Count);
            ContainmentShellLogic.Box floor = boxes[0];
            Assert.AreEqual(0.9f + ContainmentShellLogic.FloorMarginM, floor.half.x, 1e-4f);
            Assert.AreEqual(0.9f + ContainmentShellLogic.FloorMarginM, floor.half.z, 1e-4f);
            // 上面が床（course y=0）とちょうど一致する。中に眼が入ると全視線が「当たり」になる。
            Assert.AreEqual(ContainmentShellLogic.FloorSlabHalfM, floor.half.y, 1e-4f);
            Assert.AreEqual(-ContainmentShellLogic.FloorSlabHalfM, floor.center.y, 1e-4f);
        }

        [Test]
        public void Build_RoomWalls_AreInflatedAndFollowProxyGeometry()
        {
            // 現行 show.json と同じ L 字（w1: 北の腕 / w2: 西の腕）。
            List<ContainmentShellLogic.Box> boxes = ContainmentShellLogic.Build(
                Layout(1.8f, 1.8f),
                Room(Wall(-0.5f, 0.5f, 0.09f, 0.5f), Wall(-0.5f, 0.5f, -0.5f, -0.72f)));

            Assert.AreEqual(3, boxes.Count, "床 1 + 壁 2");
            // 壁 1 は x 方向へ 0.59m。半寸は膨らませた分だけ大きい。
            Assert.AreEqual(0.59f * 0.5f + ContainmentShellLogic.BoxInflateM, boxes[1].half.x, 1e-3f);
            Assert.AreEqual(1.8f * 0.5f + ContainmentShellLogic.BoxInflateM, boxes[1].half.y, 1e-3f);
            // 高さは床から立っている（中心 = 床 + h/2）。
            Assert.AreEqual(0.9f, boxes[1].center.y, 1e-3f);
        }

        [Test]
        public void Build_NeverExceedsShaderArrayLimit()
        {
            var walls = new ShowRoomWallDef[12];
            for (int i = 0; i < walls.Length; i++)
                walls[i] = Wall(-0.5f + i * 0.1f, 0.5f, 0.09f + i * 0.1f, 0.5f);
            List<ContainmentShellLogic.Box> boxes =
                ContainmentShellLogic.Build(Layout(1.8f, 1.8f), Room(walls));
            Assert.LessOrEqual(boxes.Count, ContainmentShellLogic.MaxBoxes,
                "シェーダの SHELL_MAX_BOXES を超えると黙って切り捨てられる");
        }

        // ---------------------------------------------------------------- 交差（シェーダと同じ式）

        [Test]
        public void LookingDown_SeesTheFloor()
        {
            List<ContainmentShellLogic.WorldBox> boxes =
                ToWorld(ContainmentShellLogic.Build(Layout(1.8f, 1.8f), null));
            var eye = new Vector3(0f, 1.6f, 0f);
            Assert.IsTrue(ContainmentShellLogic.IsAuthorized(eye, Vector3.down, boxes),
                "足元が黒くなったら歩けない");
            // 斜め下も床の矩形の中なら見える（下ろした手はこの視線に乗る）。
            Assert.IsTrue(ContainmentShellLogic.IsAuthorized(
                eye, new Vector3(0.4f, -1f, 0.2f).normalized, boxes));
        }

        [Test]
        public void LookingOutward_IsBlackedOut()
        {
            List<ContainmentShellLogic.WorldBox> boxes =
                ToWorld(ContainmentShellLogic.Build(Layout(1.8f, 1.8f), null));
            var eye = new Vector3(0f, 1.6f, 0f);
            // 水平 = 床に当たらない = 会場。ここが false でなければ隔離は成立しない。
            Assert.IsFalse(ContainmentShellLogic.IsAuthorized(eye, Vector3.forward, boxes));
            // 上（天井・照明）も同じ。
            Assert.IsFalse(ContainmentShellLogic.IsAuthorized(eye, Vector3.up, boxes));
            // 遠くの床（矩形の外）は会場の床なので黒。浅い俯角で確かめる。
            Assert.IsFalse(ContainmentShellLogic.IsAuthorized(
                eye, new Vector3(0f, -0.2f, 1f).normalized, boxes),
                "矩形の外の床まで見せると、そこに立っている人の足が残る");
        }

        [Test]
        public void LookingAtTheWall_SeesIt_ButNotOverIt()
        {
            List<ContainmentShellLogic.WorldBox> boxes = ToWorld(ContainmentShellLogic.Build(
                Layout(1.8f, 1.8f), Room(Wall(-0.5f, 0.5f, 0.09f, 0.5f))));
            // 壁は z=+0.5 に立つ（高さ 1.8m）。原点から北を向く。
            var eye = new Vector3(-0.2f, 1.5f, -0.3f);
            Assert.IsTrue(ContainmentShellLogic.IsAuthorized(
                eye, new Vector3(0f, 0f, 1f).normalized, boxes), "実物の壁が消えたら体験が壊れる");
            // 壁の上を越える視線は会場（天井）へ抜けるので黒。
            Assert.IsFalse(ContainmentShellLogic.IsAuthorized(
                eye, new Vector3(0f, 1f, 0.4f).normalized, boxes));
        }

        [Test]
        public void BehindTheEye_IsNotAHit()
        {
            List<ContainmentShellLogic.WorldBox> boxes = ToWorld(ContainmentShellLogic.Build(
                Layout(1.8f, 1.8f), Room(Wall(-0.5f, 0.5f, 0.09f, 0.5f))));
            // 壁は北。南を向いていれば当たらない（t < 0 を当たり扱いにすると視界の半分が抜ける）。
            var eye = new Vector3(-0.2f, 1.5f, -0.3f);
            Assert.IsFalse(ContainmentShellLogic.IsAuthorized(eye, new Vector3(0f, 0f, -1f), boxes));
        }

        [Test]
        public void CourseYaw_RotatesTheWholeShell()
        {
            List<ContainmentShellLogic.Box> spec = ContainmentShellLogic.Build(
                Layout(1.8f, 1.8f), Room(Wall(-0.5f, 0.5f, 0.09f, 0.5f)));
            // course を 90° 回した登録。壁は world の +X 側へ移る。
            var rotated = new List<ContainmentShellLogic.WorldBox>();
            foreach (ContainmentShellLogic.Box b in spec)
                rotated.Add(ContainmentShellLogic.ToWorld(
                    b, (xz, y) => Quaternion.Euler(0f, 90f, 0f) * new Vector3(xz.x, y, xz.y), 90f));

            var eye = new Vector3(0.3f, 1.5f, 0.2f);
            Assert.IsTrue(ContainmentShellLogic.IsAuthorized(eye, Vector3.right, rotated),
                "course→world の yaw を落とすと隔離だけ 90° 転ぶ");
            Assert.IsFalse(ContainmentShellLogic.IsAuthorized(eye, Vector3.forward, rotated));
        }

        // ---------------------------------------------------------------- 重み（導入・終幕）

        [Test]
        public void Intro_SealsAtTheLine_NotBefore()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            // 段 0（開始待ち）＝ まだ入っていない。会場が見えていないと誘導が成立しない。
            Assert.AreEqual(IntroStage.Black, l.Stage);
            Assert.AreEqual(0f, l.Weights.shell, 1e-4f);

            l.Tick(0.016f, new IntroInput { blackCleared = true, atStartSpot = true });
            Assert.AreEqual(IntroStage.Real, l.Stage);
            // 段 1 の頭ではまだ開いていて、段の終わりまでに閉じ切る。
            Assert.Less(l.Weights.shell, 0.2f);
            l.Tick(IntroTiming.Default.realSec * 0.95f, new IntroInput { blackCleared = true });
            Assert.Greater(l.Weights.shell, 0.9f);
        }

        [Test]
        public void Intro_ShellNeverOutlivesPassthrough()
        {
            // 殻は「見えている現実」を潰す層。映像へ移り切った後も残ると画面の映像まで黒く塗る。
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            l.Tick(0.016f, new IntroInput { blackCleared = true, atStartSpot = true });
            for (int i = 0; i < 2000; i++)
            {
                l.Tick(0.016f, new IntroInput
                {
                    blackCleared = true, frameCentered = true, liveFresh = true,
                });
                IntroWeights w = l.Weights;
                Assert.LessOrEqual(w.shell, w.passthrough + 1e-3f,
                    $"段 {l.Stage} で殻がパススルーより濃い");
                if (l.Stage == IntroStage.Done) break;
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.AreEqual(0f, l.Weights.shell, 1e-4f);
        }

        [Test]
        public void Outro_ReleasesTheShellBeforeItEnds()
        {
            var l = new OutroLogic();
            l.Configure(OutroTiming.Default);
            l.Begin();
            var input = new OutroInput { passthroughReady = true };
            l.Tick(0.016f, input);                                  // Warm → Unswap
            l.Tick(OutroTiming.Default.unswapSec, input);           // → Open
            Assert.AreEqual(OutroStage.Open, l.Stage);
            Assert.AreEqual(1f, l.Weights.shell, 1e-3f, "枠が開く間はまだ収容の中");

            l.Tick(OutroTiming.Default.openSec, input);             // → Restore
            l.Tick(OutroTiming.Default.restoreSec * 0.99f, input);
            Assert.Less(l.Weights.shell, 0.05f, "色と一緒に会場が返ってこないと黒い箱で終わる");

            l.Tick(OutroTiming.Default.restoreSec, input);          // → Hold
            Assert.AreEqual(OutroStage.Hold, l.Stage);
            Assert.AreEqual(0f, l.Weights.shell, 1e-4f);
        }
    }
}
