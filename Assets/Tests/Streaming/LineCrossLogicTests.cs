#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// LineCrossLogic（通過ラインの横断検出）の検証。
    /// 契約は <c>.claude/plans/2026-07-27_position-trigger.md</c> §3.3。
    /// </summary>
    public sealed class LineCrossLogicTests
    {
        private const float Dt = 1f / 60f;

        // 時刻はテスト内で単調に進める（横断の猶予 CrossLatchSec の検査に使う）。
        private float _now;

        [SetUp]
        public void ResetClock() => _now = 0f;

        // 1 フレーム進める（now を dt ぶん進めてから Tick）。
        private void Step(LineCrossLogic l, float x, float z, float dt = Dt)
        {
            _now += dt;
            l.Tick(_now, x, z, dt);
        }

        // x = 0 に立つ長さ 1m の縦ライン（A=(0,-0.5) → B=(0,+0.5)）。法線は +X 側。
        private static LineCrossLogic.Line VerticalLine(int dir = 0, int camera = -1)
            => LineCrossLogic.Line.Between(0f, -0.5f, 0f, 0.5f, dir, camera);

        private static LineCrossLogic Make(params LineCrossLogic.Line[] lines)
        {
            var l = new LineCrossLogic();
            l.SetLines(lines);
            return l;
        }

        // 直線上を等速で歩かせる（1 フレームずつ Tick する）。
        private void Walk(LineCrossLogic l, float x0, float z0, float x1, float z1, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                float t = steps == 0 ? 1f : (float)i / steps;
                Step(l, x0 + (x1 - x0) * t, z0 + (z1 - z0) * t);
            }
        }

        [Test]
        public void CrossingTheLine_FiresExactlyOneFrame()
        {
            var l = Make(VerticalLine());
            int fired = 0;
            for (int i = 0; i <= 20; i++)
            {
                Step(l, -0.3f + i * 0.03f, 0f);   // x: -0.3 → +0.3 を横切る
                if (l.Crossed(0)) fired++;
            }
            Assert.That(fired, Is.EqualTo(1), "**事象**なので横切ったフレームだけ true になる");
        }

        [Test]
        public void MovingWithoutCrossing_NeverFires()
        {
            var l = Make(VerticalLine());
            Walk(l, -0.4f, -0.4f, -0.1f, 0.4f, 20);   // ラインの手前をうろつくだけ
            Assert.That(l.Crossed(0), Is.False);
        }

        [Test]
        public void WalkingAroundTheEnd_DoesNotFire()
        {
            // ラインは z ∈ [-0.5, +0.5] の線分。端の外（z=0.9）を回り込んで反対側へ出る。
            var l = Make(VerticalLine());
            bool any = false;
            void One(float x, float z) { Step(l, x, z); any |= l.Crossed(0); }

            for (int i = 0; i <= 10; i++) One(-0.4f, -0.0f + i * 0.09f);   // 北へ（z 0 → 0.9）
            for (int i = 0; i <= 10; i++) One(-0.4f + i * 0.08f, 0.9f);    // 端の外を東へ
            for (int i = 0; i <= 10; i++) One(0.4f, 0.9f - i * 0.09f);     // 南へ戻る
            Assert.That(any, Is.False, "線分の外を回り込んだら横切っていない（無限直線ではない）");
        }

        [Test]
        public void LingeringOnTheLine_DoesNotFireRepeatedly()
        {
            // 線の上で足踏み・トラッキング揺れ。1 回目だけ発火し、離れるまで再検出しない。
            var l = Make(VerticalLine());
            int fired = 0;
            void One(float x) { Step(l, x, 0f); if (l.Crossed(0)) fired++; }

            One(-0.02f);
            One(0.01f);    // 1 回目の横断
            One(-0.01f);   // 揺れで戻る（まだ線の近く）
            One(0.01f);
            One(-0.01f);
            Assert.That(fired, Is.EqualTo(1), $"線から {LineCrossLogic.RearmMarginM}m 離れるまで再検出しない");

            // いったん離れれば、次の横断は正しく数える。
            One(-0.3f);
            One(0.1f);
            Assert.That(fired, Is.EqualTo(2));
        }

        [Test]
        public void Direction_ForwardOnly_FiresOnlyOneWay()
        {
            // 法線は +X 側（A→B が +Z なら n=(dz,-dx)=(+1,0)）。fwd は「-X → +X」の横断だけ。
            var l = Make(VerticalLine(dir: 1));
            Step(l, -0.3f, 0f, Dt);
            Step(l, 0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.True, "法線向きの横断は発火する");

            Step(l, 0.3f, 0f, Dt);
            Step(l, -0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.False, "逆向きの横断は発火しない");
        }

        [Test]
        public void Direction_BackOnly_FiresOnlyTheOtherWay()
        {
            var l = Make(VerticalLine(dir: -1));
            Step(l, -0.3f, 0f, Dt);
            Step(l, 0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.False);

            Step(l, 0.3f, 0f, Dt);
            Step(l, -0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.True);
        }

        [Test]
        public void DiscontinuousDt_DoesNotCountTheCrossing()
        {
            // HMD 着脱・アプリ復帰でワープしたように見える移動を横断として数えない。
            var l = Make(VerticalLine());
            Step(l, -0.3f, 0f, Dt);
            Step(l, 0.3f, 0f, 3f);
            Assert.That(l.Crossed(0), Is.False);

            // 以後は普通に検出できる（状態が壊れない）。
            Step(l, 0.3f, 0f, Dt);
            Step(l, -0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.True);
        }

        [Test]
        public void TeleportLikeJump_DoesNotCountTheCrossing()
        {
            // recenter で座標系が飛んだ時に幽霊の横断を作らない（1 フレーム 1m 超の移動は無効）。
            var l = Make(VerticalLine());
            Step(l, -1.0f, 0f, Dt);
            Step(l, 0.5f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.False, $"1 フレーム {LineCrossLogic.MaxStepM}m 超は teleport 扱い");
        }

        [Test]
        public void FirstFrame_NeverFires()
        {
            // 直前位置が無い（ラン開始直後 / Reset 直後）フレームは判定しない。
            var l = Make(VerticalLine());
            Step(l, 0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.False);
        }

        [Test]
        public void Reset_ForgetsPreviousPosition()
        {
            var l = Make(VerticalLine());
            Step(l, -0.3f, 0f, Dt);
            l.Reset();
            Step(l, 0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.False, "ラン開始で前の体験者の位置は引き継がない");
        }

        [Test]
        public void UndefinedLine_NeverFires_ButKeepsItsSlot()
        {
            var l = Make(LineCrossLogic.Line.Undefined, VerticalLine());
            Step(l, -0.3f, 0f, Dt);
            Step(l, 0.3f, 0f, Dt);
            Assert.That(l.Crossed(0), Is.False, "layout に実体が無い枠は横切れない");
            Assert.That(l.Crossed(1), Is.True, "index はずれない");
            Assert.That(l.Count, Is.EqualTo(2));
        }

        [Test]
        public void TooShortLine_IsIgnored()
        {
            var l = Make(LineCrossLogic.Line.Between(0f, 0f, 0.01f, 0f, 0, -1));
            Step(l, 0.005f, -0.2f, Dt);
            Step(l, 0.005f, 0.2f, Dt);
            Assert.That(l.Crossed(0), Is.False, $"{LineCrossLogic.MinLengthM}m 未満のラインは誤検出のもとなので無効");
        }

        [Test]
        public void OwnerCamera_IsCarriedInTheState()
        {
            // 区間との照合（別ゾーンのラインを踏んでも発火しない）は TakeRunnerLogic が行うので、
            // ここでは担当カメラが state に載っていることを固定する。
            var l = Make(VerticalLine(camera: 2));
            Step(l, -0.3f, 0f, Dt);
            Step(l, 0.3f, 0f, Dt);
            Assert.That(l.StateView[0].crossed, Is.True);
            Assert.That(l.StateView[0].camera, Is.EqualTo(2));
        }

        [Test]
        public void CrossedAtSec_RemembersWhenItHappened_ForTheLatch()
        {
            // 演出の武装はゾーン確定（dwell 0.5s）で起きるので、横断時刻を覚えておく必要がある
            // （TakeRunnerLogic が CrossLatchSec の猶予で due 判定する）。
            var l = Make(VerticalLine());
            Step(l, -0.3f, 0f);
            Step(l, 0.3f, 0f);
            Assert.That(l.Crossed(0), Is.True);
            float at = l.StateView[0].crossedAtSec;
            Assert.That(at, Is.EqualTo(_now).Within(0.001f), "横断した時刻が残る");

            Step(l, 0.5f, 0f);
            Assert.That(l.Crossed(0), Is.False, "事象そのものは 1 フレーム");
            Assert.That(l.StateView[0].crossedAtSec, Is.EqualTo(at).Within(0.001f), "時刻は残り続ける");
        }

        [Test]
        public void ParseDir_FallsBackToBoth_OnUnknownValues()
        {
            Assert.That(LineCrossLogic.ParseDir(LineCrossLogic.DirBoth, out bool k1), Is.EqualTo(0));
            Assert.That(k1, Is.True);
            Assert.That(LineCrossLogic.ParseDir(LineCrossLogic.DirForward, out _), Is.EqualTo(1));
            Assert.That(LineCrossLogic.ParseDir(LineCrossLogic.DirBack, out _), Is.EqualTo(-1));
            Assert.That(LineCrossLogic.ParseDir("bogus", out bool k2), Is.EqualTo(0), "未知は両方向へ倒す");
            Assert.That(k2, Is.False, "既定へ倒したことは呼び出し側へ伝える（警告 1 回）");
        }

        [Test]
        public void DiagonalLine_IsCrossedByPerpendicularWalk()
        {
            // 斜めのラインでも法線の計算が効く（軸平行に依存していない）。
            var l = Make(LineCrossLogic.Line.Between(-0.5f, -0.5f, 0.5f, 0.5f, 0, -1));
            Step(l, 0.2f, -0.2f, Dt);
            Step(l, -0.2f, 0.2f, Dt);
            Assert.That(l.Crossed(0), Is.True);
        }
    }
}
