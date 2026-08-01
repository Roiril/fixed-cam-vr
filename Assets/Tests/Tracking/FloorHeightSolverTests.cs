#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 位置合わせのタッチ位置から床の高さを解く契約。
    ///
    /// 「壁や床のワイヤーが地面より下に出る」を直すための測定で、XZ + yaw の剛体フィットとは別系統。
    /// トラッキング原点は FloorLevel 設定だが実測とは食い違うので、実測して合わせる。
    /// </summary>
    public sealed class FloorHeightSolverTests
    {
        [Test]
        public void FloorIsMedianMinusTouchHeight()
        {
            // 床に着ける運用（構え 0）: タッチ位置の中央値がそのまま床
            var r = FloorHeightSolver.Solve(new[] { 0.30f, 0.32f, 0.31f }, 0f);
            Assert.That(r.Ok, Is.True);
            Assert.That(r.FloorY, Is.EqualTo(0.31f).Within(1e-4f));
        }

        [Test]
        public void TouchHeightIsSubtracted()
        {
            // 床から 1m に構える運用: 実測 1.35m なら床は 0.35m
            var r = FloorHeightSolver.Solve(new[] { 1.35f, 1.35f, 1.35f }, 1.0f);
            Assert.That(r.FloorY, Is.EqualTo(0.35f).Within(1e-4f));
        }

        [Test]
        public void OneBadPointDoesNotDragTheFloor()
        {
            // 1 点だけ床に着け損ねた（30cm 浮いた）。平均だと 10cm ずれるが、中央値なら動かない。
            var r = FloorHeightSolver.Solve(new[] { 0.30f, 0.60f, 0.30f }, 0f);
            Assert.That(r.FloorY, Is.EqualTo(0.30f).Within(1e-4f),
                "中央値なので 3 点中 1 点の失敗は床に効かない");
            Assert.That(r.Suspicious, Is.True, "ばらつきは警告として出る");
        }

        [Test]
        public void EvenCountUsesTheMiddleTwo()
        {
            var r = FloorHeightSolver.Solve(new[] { 0.10f, 0.20f, 0.30f, 0.40f }, 0f);
            Assert.That(r.FloorY, Is.EqualTo(0.25f).Within(1e-4f));
        }

        [Test]
        public void SpreadIsMaxMinusMin()
        {
            var r = FloorHeightSolver.Solve(new[] { 0.10f, 0.14f, 0.12f }, 0f);
            Assert.That(r.SpreadM, Is.EqualTo(0.04f).Within(1e-4f));
            Assert.That(r.Suspicious, Is.False, "6cm 以内は正常");
        }

        [Test]
        public void EmptyInputDoesNotSolve()
        {
            Assert.That(FloorHeightSolver.Solve(null, 0f).Ok, Is.False);
            Assert.That(FloorHeightSolver.Solve(new float[0], 0f).Ok, Is.False);
        }

        [Test]
        public void SinglePointStillSolves()
        {
            // 基準点が 2 点未満の登録は成立しないが、ソルバ自体は 1 点でも答えを返す
            var r = FloorHeightSolver.Solve(new[] { 0.42f }, 0f);
            Assert.That(r.Ok, Is.True);
            Assert.That(r.FloorY, Is.EqualTo(0.42f).Within(1e-4f));
            Assert.That(r.SpreadM, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void NegativeFloorIsAllowed()
        {
            // トラッキング原点が実際の床より上にあるケース（床が下に沈んで見えるのがこれ）
            var r = FloorHeightSolver.Solve(new[] { -0.18f, -0.20f, -0.19f }, 0f);
            Assert.That(r.FloorY, Is.EqualTo(-0.19f).Within(1e-4f));
        }
    }
}
