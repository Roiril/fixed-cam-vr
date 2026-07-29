#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 「映像の乱れ」の時間包絡の検証。企画書 2.3 は乱れを 2 通りに使う —
    /// 差し替えの継ぎ目を覆う（持続）と、注意・移動を誘導する（単発）。両者は max で合成する。
    /// </summary>
    public sealed class GlitchEnvelopeLogicTests
    {
        [Test]
        public void Idle_IsZero()
        {
            var g = new GlitchEnvelopeLogic();
            Assert.That(g.Tick(1f), Is.EqualTo(0f));
        }

        [Test]
        public void Sustain_HoldsUntilCleared()
        {
            var g = new GlitchEnvelopeLogic();
            g.SetSustain(0.6f);
            Assert.That(g.Tick(0.1f), Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(g.Tick(5f), Is.EqualTo(0.6f).Within(1e-4f), "持続成分は時間で減らない");
            g.SetSustain(0f);
            Assert.That(g.Tick(0.01f), Is.EqualTo(0f));
        }

        [Test]
        public void Pulse_RisesThenHoldsThenDecaysToZero()
        {
            var g = new GlitchEnvelopeLogic();
            g.Pulse(1f, 0.2f);

            // 立ち上がり途中
            float mid = g.Tick(GlitchEnvelopeLogic.AttackSec * 0.5f);
            Assert.That(mid, Is.GreaterThan(0f).And.LessThan(1f));

            // 保持
            Assert.That(g.Tick(GlitchEnvelopeLogic.AttackSec * 0.5f + 0.1f), Is.EqualTo(1f).Within(1e-4f));

            // 減衰しきる
            g.Tick(0.2f);
            g.Tick(GlitchEnvelopeLogic.ReleaseSec + 0.01f);
            Assert.That(g.Level, Is.EqualTo(0f));
        }

        [Test]
        public void Pulse_AndSustain_CombineWithMax_NotSum()
        {
            var g = new GlitchEnvelopeLogic();
            g.SetSustain(0.5f);
            g.Pulse(0.8f, 0.2f);
            g.Tick(GlitchEnvelopeLogic.AttackSec);
            Assert.That(g.Level, Is.EqualTo(0.8f).Within(1e-3f), "加算だと 1.3 で飽和して遷移の形が潰れる");

            // 弱い単発は持続成分に埋もれる
            var h = new GlitchEnvelopeLogic();
            h.SetSustain(0.7f);
            h.Pulse(0.2f, 0.2f);
            h.Tick(GlitchEnvelopeLogic.AttackSec);
            Assert.That(h.Level, Is.EqualTo(0.7f).Within(1e-3f));
        }

        [Test]
        public void Pulse_IgnoresZeroAndNegative()
        {
            var g = new GlitchEnvelopeLogic();
            g.Pulse(0f, 1f);
            g.Pulse(-1f, 1f);
            Assert.That(g.Tick(0.1f), Is.EqualTo(0f));
        }

        [Test]
        public void Pulse_ClampsAboveOne()
        {
            var g = new GlitchEnvelopeLogic();
            g.Pulse(5f, 0.2f);
            Assert.That(g.Tick(GlitchEnvelopeLogic.AttackSec), Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void Pulse_OverflowKeepsTheStrongest()
        {
            var g = new GlitchEnvelopeLogic();
            for (int i = 0; i < GlitchEnvelopeLogic.MaxPulses; i++) g.Pulse(0.9f, 1f);
            g.Pulse(0.1f, 1f);   // 弱い後着は捨てられる
            g.Tick(GlitchEnvelopeLogic.AttackSec);
            Assert.That(g.Level, Is.EqualTo(0.9f).Within(1e-3f));
        }

        [Test]
        public void Reset_ClearsEverything()
        {
            var g = new GlitchEnvelopeLogic();
            g.SetSustain(1f);
            g.Pulse(1f, 5f);
            g.Tick(0.1f);
            g.Reset();
            Assert.That(g.Tick(0.01f), Is.EqualTo(0f));
        }

        [Test]
        public void NegativeDeltaIsTreatedAsZero()
        {
            var g = new GlitchEnvelopeLogic();
            g.Pulse(1f, 0.2f);
            g.Tick(GlitchEnvelopeLogic.AttackSec);
            float before = g.Level;
            Assert.That(g.Tick(-5f), Is.EqualTo(before).Within(1e-4f));
        }
    }
}
