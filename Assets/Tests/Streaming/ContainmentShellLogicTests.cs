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

        // ---------------------------------------------------------------- footprint（中と外で共有する境界）

        [Test]
        public void Footprint_MatchesTheAuthorizedFloor()
        {
            // 外から見た箱（SealedBox）と、中から見た許された床は**同じ境界**でなければならない。
            // ずれると「箱の外に立っているのに足元が黒い」が起きる。
            Assert.IsTrue(ContainmentShellLogic.TryFootprint(Layout(1.8f, 2.4f), null, out Vector2 half));
            Assert.AreEqual(0.9f + ContainmentShellLogic.FloorMarginM, half.x, 1e-4f);
            Assert.AreEqual(1.2f + ContainmentShellLogic.FloorMarginM, half.y, 1e-4f);

            List<ContainmentShellLogic.Box> boxes = ContainmentShellLogic.Build(Layout(1.8f, 2.4f), null);
            Assert.AreEqual(half.x, boxes[0].half.x, 1e-4f);
            Assert.AreEqual(half.y, boxes[0].half.z, 1e-4f);
        }

        [Test]
        public void Footprint_WithoutGeometry_IsUnresolved()
        {
            Assert.IsFalse(ContainmentShellLogic.TryFootprint(null, null, out _));
        }

        [Test]
        public void DistanceOutside_IsZeroInside_AndGrowsOutside()
        {
            var half = new Vector2(1.5f, 1.5f);
            Assert.AreEqual(0f, ContainmentShellLogic.DistanceOutsideM(Vector2.zero, half), 1e-4f);
            Assert.AreEqual(0f, ContainmentShellLogic.DistanceOutsideM(new Vector2(1.4f, -1.4f), half), 1e-4f);
            Assert.AreEqual(0.5f, ContainmentShellLogic.DistanceOutsideM(new Vector2(2.0f, 0f), half), 1e-4f);
            // 角の外は 2 軸の合成。片方だけ見ると角で早く開いてしまう。
            Assert.AreEqual(Mathf.Sqrt(0.5f * 0.5f + 0.5f * 0.5f),
                            ContainmentShellLogic.DistanceOutsideM(new Vector2(-2.0f, 2.0f), half), 1e-4f);
        }

        // ---------------------------------------------------------------- 重み（導入・終幕）

        [Test]
        public void Intro_NeverRevealsTheInside_UntilItIsDone()
        {
            // canon/LEDGER.md 0005 の約束。**導入のあいだ壁も床も出さない**（真っ黒）。
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            var inside = new IntroInput { blackCleared = true, outsideBoxM = 0f };
            l.Tick(0.016f, inside);
            Assert.AreEqual(0f, l.Weights.shellReveal, 1e-4f, "段 0 で中を見せている");
            Assert.AreEqual(1f, l.Weights.shell, 1e-4f, "中に居るのに黒が出ていない");

            // 段を進めても shellReveal は 0 のまま。
            l.Tick(0.016f, new IntroInput { blackCleared = true, atStartSpot = true, outsideBoxM = 0f });
            for (int i = 0; i < 2000 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.016f, new IntroInput
                {
                    blackCleared = true, frameCentered = true, liveFresh = true, outsideBoxM = 0f,
                });
                Assert.AreEqual(0f, l.Weights.shellReveal, 1e-4f, $"段 {l.Stage} で中を見せている");
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        [Test]
        public void Intro_InsideTheArea_IsBlackAndOutsideKeepsTheBox()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();

            // 外に居る: 中を隠すのは箱の仕事。殻は要らない。
            l.Tick(0.016f, new IntroInput { blackCleared = true, outsideBoxM = 2f });
            Assert.AreEqual(1f, l.Weights.sealBox, 1e-4f);
            Assert.AreEqual(0f, l.Weights.shell, 1e-4f);

            // 中に入ってしまった: 黒しか見せない。
            l.Tick(0.016f, new IntroInput { blackCleared = true, outsideBoxM = 0f });
            Assert.AreEqual(0f, l.Weights.sealBox, 1e-4f);
            Assert.AreEqual(1f, l.Weights.shell, 1e-4f);
        }

        [Test]
        public void Swap_ShowsNoPassthroughAtAll()
        {
            // 段 5 で現実を 1 画素も出さない（交差の途中に中の様子が透けるのを防ぐ）。
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            l.Tick(0.016f, new IntroInput { blackCleared = true, atStartSpot = true, outsideBoxM = 2f });
            for (int i = 0; i < 3000 && l.Stage != IntroStage.Swap; i++)
                l.Tick(0.016f, new IntroInput
                {
                    blackCleared = true, frameCentered = true, liveFresh = true, outsideBoxM = 2f,
                });
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            for (int i = 0; i < 300 && l.Stage == IntroStage.Swap; i++)
            {
                l.Tick(0.016f, new IntroInput
                {
                    blackCleared = true, frameCentered = true, liveFresh = true, outsideBoxM = 2f,
                });
                Assert.AreEqual(0f, l.Weights.passthrough, 1e-4f, "段 5 でパススルーが出ている");
                Assert.AreEqual(0f, l.Weights.sealBox, 1e-4f, "段 5 で箱が画面を覆っている");
            }
        }

        // ---------------------------------------------------------------- 接近（導入の合図）

        [Test]
        public void Approach_NeedsToHaveBeenAway()
        {
            var a = new ApproachLogic();
            // いきなり近くに居るだけでは始めない（起動時にたまたま近い / 前の体験者が立ったまま）。
            for (int i = 0; i < 60; i++)
                Assert.IsFalse(a.Tick(0.2f, 1.0f, 0.4f, 0.016f, valid: true));

            // 一度離れてから近づけば成立する。
            a.Tick(3.0f, 1.0f, 0.4f, 0.016f, valid: true);
            Assert.IsTrue(a.Armed);
            bool fired = false;
            for (int i = 0; i < 40 && !fired; i++) fired = a.Tick(0.5f, 1.0f, 0.4f, 0.016f, valid: true);
            Assert.IsTrue(fired, "近づいて 0.4 秒留まったのに始まらない");
        }

        [Test]
        public void Approach_DropsArmingWhenThePositionIsNotTrusted()
        {
            var a = new ApproachLogic();
            a.Tick(3.0f, 1.0f, 0.4f, 0.016f, valid: true);
            Assert.IsTrue(a.Armed);
            a.Tick(0.5f, 1.0f, 0.4f, 0.016f, valid: false);   // 位置合わせが外れた等
            Assert.IsFalse(a.Armed, "信用できない間に武装が残ると、復帰した瞬間に発火する");
        }

        [Test]
        public void Intro_ShellNeverSurvivesIntoTheShow()
        {
            // 殻は段 5 で「黒 → 映像」の渡しに使う（canon/LEDGER.md 0005）。
            // **渡し終わったら必ず 0** — 残すと本編の映像まで黒く塗る。
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            l.Tick(0.016f, new IntroInput { blackCleared = true, atStartSpot = true, outsideBoxM = 2f });
            float lastSwapShell = 1f;
            for (int i = 0; i < 3000; i++)
            {
                l.Tick(0.016f, new IntroInput
                {
                    blackCleared = true, frameCentered = true, liveFresh = true, outsideBoxM = 2f,
                });
                if (l.Stage == IntroStage.Swap) lastSwapShell = l.Weights.shell;
                if (l.Stage == IntroStage.Done) break;
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.Less(lastSwapShell, 0.05f, "段 5 の終わりで黒が残っている");
            Assert.AreEqual(0f, l.Weights.shell, 1e-4f);
            Assert.AreEqual(0f, l.Weights.sealBox, 1e-4f);
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
