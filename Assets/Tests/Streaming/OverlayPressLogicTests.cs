#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 長押しで布が膨らむバネ（<see cref="OverlayPressLogic"/>・0349）。押し続ければ 1 へ寄り、
    /// 離せば 0 を越えて沈み（布が引っ込む）、粗い dt でも発散しない。成立は 0.12 秒で直線に畳む。
    /// </summary>
    public sealed class OverlayPressLogicTests
    {
        private const float Dt = 1f / 72f;

        [Test]
        public void Holding_ConvergesToOne()
        {
            var p = new OverlayPressLogic();
            for (int i = 0; i < 72 * 3; i++) p.Tick(Dt, 1f);
            Assert.That(p.P, Is.EqualTo(1f).Within(0.02f));
            Assert.That(p.Peak, Is.LessThanOrEqualTo(OverlayPressLogic.Max));
        }

        [Test]
        public void OneSecondHold_ReachesTheBulgeBeforeTheMarkFires()
        {
            // 1 秒の長押し（VisitorMarkHoldLogic.DefaultHoldSec）で進み具合が 0→1。成立の瞬間に膨らみが出ている
            // （解析器の合格線 0.6 と同じ）。
            var p = new OverlayPressLogic();
            for (int i = 1; i <= 72; i++) p.Tick(Dt, i / 72f);
            Assert.That(p.Peak, Is.GreaterThanOrEqualTo(0.6f));
        }

        [Test]
        public void Releasing_DipsBelowZero_ThenSettles()
        {
            var p = new OverlayPressLogic();
            for (int i = 0; i < 72 * 3; i++) p.Tick(Dt, 1f);
            float low = 1f;
            for (int i = 0; i < 72; i++) low = System.Math.Min(low, p.Tick(Dt, 0f));
            Assert.That(low, Is.LessThan(0f), "離すと 0 を越えて沈む（布が引っ込む）");
            Assert.That(low, Is.GreaterThanOrEqualTo(OverlayPressLogic.Min));
            for (int i = 0; i < 72 * 4; i++) p.Tick(Dt, 0f);
            Assert.That(p.P, Is.EqualTo(0f).Within(0.01f), "揺れて止まる");
        }

        [Test]
        public void CoarseDt_DoesNotDiverge()
        {
            var p = new OverlayPressLogic();
            for (int i = 0; i < 40; i++)
            {
                float v = p.Tick(i % 2 == 0 ? 1f : 0.5f, i < 20 ? 1f : 0f);
                Assert.That(v, Is.InRange(OverlayPressLogic.Min, OverlayPressLogic.Max));
                Assert.That(float.IsNaN(v), Is.False);
            }
            for (int i = 0; i < 72 * 4; i++) p.Tick(Dt, 0f);
            Assert.That(p.P, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void BadInputs_AreTreatedAsZero()
        {
            var p = new OverlayPressLogic();
            p.Tick(float.NaN, float.NaN);
            p.Tick(-1f, -3f);
            Assert.That(p.P, Is.EqualTo(0f));
        }

        [Test]
        public void Collapse_IsLinearToZeroIn012Sec_AndIgnoresProgress()
        {
            var p = new OverlayPressLogic();
            for (int i = 0; i < 72 * 3; i++) p.Tick(Dt, 1f);
            float from = p.P;
            float peak = p.Peak;
            p.Collapse();
            Assert.That(p.Collapsing, Is.True);
            float half = p.Tick(OverlayPressLogic.CollapseSec * 0.5f, 1f);
            Assert.That(half, Is.EqualTo(from * 0.5f).Within(1e-4f), "バネを使わず直線");
            p.Tick(OverlayPressLogic.CollapseSec, 1f);
            Assert.That(p.Collapsed, Is.True);
            Assert.That(p.P, Is.EqualTo(0f));
            Assert.That(p.Tick(Dt, 1f), Is.EqualTo(0f), "畳んだ後は押し続けても膨らまない");
            Assert.That(p.Peak, Is.EqualTo(peak).Within(1e-4f), "観測用の最大は残る（押し切ると行き過ぎるので、畳む直前の p より大きい）");
        }

        [Test]
        public void Reset_ClearsEverything()
        {
            var p = new OverlayPressLogic();
            for (int i = 0; i < 72; i++) p.Tick(Dt, 1f);
            p.Collapse();
            p.Tick(1f, 0f);
            p.Reset();
            Assert.That(p.P, Is.EqualTo(0f));
            Assert.That(p.Peak, Is.EqualTo(0f));
            Assert.That(p.Collapsed, Is.False);
            for (int i = 0; i < 72; i++) p.Tick(Dt, 1f);
            Assert.That(p.P, Is.GreaterThan(0.5f), "リセット後はまた膨らむ");
        }
    }
}
