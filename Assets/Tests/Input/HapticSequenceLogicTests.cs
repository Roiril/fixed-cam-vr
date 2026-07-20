#nullable enable
using FixedCamVr.Input;
using NUnit.Framework;

namespace FixedCamVr.Input.Tests
{
    /// <summary>
    /// HapticSequenceLogic（振動パターンの「経過時間 → 振幅」）の検証。
    /// Ack/Action/Fire/Error の波形タイミング・HoldTick ランプ・重畳優先度（ピーク優先で差し替え／
    /// 同ピーク・低ピークは再生中なら無視 = 二重発火の畳み込み）・HoldTick との max 合成を固定する。
    /// </summary>
    public sealed class HapticSequenceLogicTests
    {
        private const float Eps = 1e-4f;

        // 単発パターンを鳴らし、絶対経過時間 t での振幅を得る（Tick は advance-then-sample なので
        // fresh から 1 回の Tick(t) で「t 秒時点の振幅」になる）。
        private static float SampleAt(HapticSequenceLogic.Pattern p, float t)
        {
            var l = new HapticSequenceLogic();
            l.Trigger(p);
            return l.Tick(t);
        }

        // ---- 周波数 ----

        [Test]
        public void Frequency_IsHalf()
        {
            Assert.That(HapticSequenceLogic.Frequency, Is.EqualTo(0.5f).Within(Eps));
        }

        // ---- Ack: 40ms・amp 0.25 の単発 ----

        [Test]
        public void Ack_Waveform()
        {
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Ack, 0.02f), Is.EqualTo(0.25f).Within(Eps));
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Ack, 0.041f), Is.EqualTo(0f).Within(Eps));
        }

        // ---- Action: 80ms・amp 0.50 の単発 ----

        [Test]
        public void Action_Waveform()
        {
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Action, 0.04f), Is.EqualTo(0.50f).Within(Eps));
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Action, 0.081f), Is.EqualTo(0f).Within(Eps));
        }

        // ---- Fire: 80ms×2（間 80ms）・amp 0.80 ----

        [Test]
        public void Fire_Waveform_TwoPulsesWithGap()
        {
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Fire, 0.04f), Is.EqualTo(0.80f).Within(Eps), "pulse 1");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Fire, 0.12f), Is.EqualTo(0f).Within(Eps), "gap");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Fire, 0.20f), Is.EqualTo(0.80f).Within(Eps), "pulse 2");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Fire, 0.241f), Is.EqualTo(0f).Within(Eps), "after");
        }

        // ---- Error: 50ms×3（間 60ms）・amp 0.60 ----

        [Test]
        public void Error_Waveform_ThreePulses()
        {
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Error, 0.025f), Is.EqualTo(0.60f).Within(Eps), "pulse 1");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Error, 0.08f), Is.EqualTo(0f).Within(Eps), "gap 1");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Error, 0.135f), Is.EqualTo(0.60f).Within(Eps), "pulse 2");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Error, 0.19f), Is.EqualTo(0f).Within(Eps), "gap 2");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Error, 0.245f), Is.EqualTo(0.60f).Within(Eps), "pulse 3");
            Assert.That(SampleAt(HapticSequenceLogic.Pattern.Error, 0.271f), Is.EqualTo(0f).Within(Eps), "after");
        }

        // ---- HoldTick: 連続・amp 0.10→0.30 の progress 比例ランプ ----

        [Test]
        public void HoldTick_RampProportionalToProgress()
        {
            var l = new HapticSequenceLogic();
            l.SetHoldProgress(0.5f);
            Assert.That(l.Tick(0.016f), Is.EqualTo(0.20f).Within(Eps)); // 0.10 + 0.20*0.5
            l.SetHoldProgress(0.25f);
            Assert.That(l.Tick(0.016f), Is.EqualTo(0.15f).Within(Eps)); // 0.10 + 0.20*0.25
        }

        [Test]
        public void HoldTick_StopsAtZeroAndOne()
        {
            var l = new HapticSequenceLogic();
            l.SetHoldProgress(0f);
            Assert.That(l.Tick(0.016f), Is.EqualTo(0f).Within(Eps), "progress 0 = 停止");
            l.SetHoldProgress(1f);
            Assert.That(l.Tick(0.016f), Is.EqualTo(0f).Within(Eps), "progress 1 = 発火済み・停止");
        }

        // ---- 優先度: ピークが厳密に大きい後着だけ差し替え（Ack→Action の昇格）----

        [Test]
        public void Priority_StrictlyGreaterPeakReplaces_AckToAction()
        {
            var l = new HapticSequenceLogic();
            l.Trigger(HapticSequenceLogic.Pattern.Ack);
            Assert.That(l.Tick(0.01f), Is.EqualTo(0.25f).Within(Eps));
            l.Trigger(HapticSequenceLogic.Pattern.Action); // 0.5 > 0.25 → 差し替え（計時リセット）
            Assert.That(l.Tick(0.01f), Is.EqualTo(0.50f).Within(Eps));
        }

        // ---- 優先度: 低い後着は再生中なら無視 ----

        [Test]
        public void Priority_LowerPeakIgnoredWhilePlaying()
        {
            var l = new HapticSequenceLogic();
            l.Trigger(HapticSequenceLogic.Pattern.Fire);
            Assert.That(l.Tick(0.01f), Is.EqualTo(0.80f).Within(Eps));
            l.Trigger(HapticSequenceLogic.Pattern.Ack); // 0.25 <= 0.80 再生中 → 無視
            Assert.That(l.Tick(0.01f), Is.EqualTo(0.80f).Within(Eps));
        }

        // ---- 優先度: 同ピークの後着は無視（＝二重発火の畳み込み。restart しない）----

        [Test]
        public void Priority_EqualPeakDoesNotRestart()
        {
            var l = new HapticSequenceLogic();
            l.Trigger(HapticSequenceLogic.Pattern.Fire);
            l.Tick(0.04f);                                   // elapsed 0.04（pulse 1）
            l.Trigger(HapticSequenceLogic.Pattern.Fire);     // 同ピーク → 無視（計時は据え置き）
            // 最初の Trigger から総時間 0.24 を超えて終了しているはず（restart なら 0.25 は pulse 2 で 0.8 になる）。
            Assert.That(l.Tick(0.21f), Is.EqualTo(0f).Within(Eps));
        }

        // ---- HoldTick と単発は max 合成 ----

        [Test]
        public void HoldTick_MaxBlendWithOneShot()
        {
            var l = new HapticSequenceLogic();
            l.SetHoldProgress(0.5f);                         // hold 0.20
            l.Trigger(HapticSequenceLogic.Pattern.Fire);     // 0.80
            Assert.That(l.Tick(0.04f), Is.EqualTo(0.80f).Within(Eps), "max(0.8, 0.2)");
            // Fire 終了後は hold が床として残る。
            Assert.That(l.Tick(0.30f), Is.EqualTo(0.20f).Within(Eps), "fire 終了 → hold 0.20");
        }

        // ---- Reset ----

        [Test]
        public void Reset_SilencesEverything()
        {
            var l = new HapticSequenceLogic();
            l.Trigger(HapticSequenceLogic.Pattern.Fire);
            l.SetHoldProgress(0.5f);
            l.Reset();
            Assert.That(l.Tick(0.01f), Is.EqualTo(0f).Within(Eps));
            Assert.That(l.IsPlayingOneShot, Is.False);
        }
    }
}
