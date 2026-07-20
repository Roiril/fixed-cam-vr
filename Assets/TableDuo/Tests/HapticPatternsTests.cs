#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Hands.Tests
{
    /// <summary>
    /// 触覚パターンの純ロジック（<see cref="HapticVocabulary"/> / <see cref="HapticPatternPlayer"/> /
    /// <see cref="HapticChannel"/>）の EditMode テスト。MonoBehaviour / OVRInput 非依存。
    /// </summary>
    public sealed class HapticPatternsTests
    {
        private const float Eps = 1e-4f;

        [Test]
        public void Ack_IsSinglePulse_ThenStops()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.AckPulses);

            Assert.AreEqual(HapticVocabulary.AckAmp, p.Tick(0.020f), Eps, "受理直後は Ack 振幅");
            Assert.IsTrue(p.IsPlaying);
            Assert.AreEqual(0f, p.Tick(0.030f), Eps, "40ms 超で停止し 0");
            Assert.IsFalse(p.IsPlaying);
        }

        [Test]
        public void Action_HasExpectedAmplitudeAndDuration()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.ActionPulses);

            Assert.AreEqual(HapticVocabulary.ActionAmp, p.Tick(0.040f), Eps);
            Assert.AreEqual(HapticVocabulary.ActionAmp, p.Tick(0.030f), Eps, "80ms 手前はまだ鳴る");
            Assert.AreEqual(0f, p.Tick(0.020f), Eps, "80ms 超で停止");
            Assert.IsFalse(p.IsPlaying);
        }

        [Test]
        public void Fire_IsTwoPulsesWithGap()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.FirePulses);

            Assert.AreEqual(HapticVocabulary.FireAmp, p.Tick(0.040f), Eps, "1 発目");
            Assert.AreEqual(0f, p.Tick(0.080f), Eps, "間の無音（t=0.12）");
            Assert.AreEqual(HapticVocabulary.FireAmp, p.Tick(0.080f), Eps, "2 発目（t=0.20）");
            Assert.AreEqual(0f, p.Tick(0.060f), Eps, "総尺 0.24 超で停止");
            Assert.IsFalse(p.IsPlaying);
        }

        [Test]
        public void Error_IsThreePulses()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.ErrorPulses);

            // セグメント境界: 0.05 / 0.11 / 0.16 / 0.22 / 0.27（総尺 0.27）
            Assert.AreEqual(HapticVocabulary.ErrorAmp, p.Tick(0.025f), Eps, "1 発目");
            Assert.AreEqual(0f, p.Tick(0.055f), Eps, "間1（t=0.08）");
            Assert.AreEqual(HapticVocabulary.ErrorAmp, p.Tick(0.055f), Eps, "2 発目（t=0.135）");
            Assert.AreEqual(0f, p.Tick(0.055f), Eps, "間2（t=0.19）");
            Assert.AreEqual(HapticVocabulary.ErrorAmp, p.Tick(0.055f), Eps, "3 発目（t=0.245）");
            Assert.AreEqual(0f, p.Tick(0.050f), Eps, "総尺 0.27 超で停止");
            Assert.IsFalse(p.IsPlaying);
        }

        [Test]
        public void HoldAmplitude_ClampsAndInterpolates()
        {
            Assert.AreEqual(HapticVocabulary.HoldMinAmp, HapticVocabulary.HoldAmplitude(-1f), Eps, "負はクランプ");
            Assert.AreEqual(HapticVocabulary.HoldMinAmp, HapticVocabulary.HoldAmplitude(0f), Eps);
            Assert.AreEqual(HapticVocabulary.HoldMaxAmp, HapticVocabulary.HoldAmplitude(1f), Eps);
            Assert.AreEqual(HapticVocabulary.HoldMaxAmp, HapticVocabulary.HoldAmplitude(2f), Eps, "超過はクランプ");

            float mid = HapticVocabulary.HoldAmplitude(0.5f);
            float expectedMid = (HapticVocabulary.HoldMinAmp + HapticVocabulary.HoldMaxAmp) * 0.5f;
            Assert.AreEqual(expectedMid, mid, Eps, "中間は線形補間");
        }

        [Test]
        public void Channel_OneShotTakesPrecedenceOverHold()
        {
            var ch = new HapticChannel();
            ch.SetHoldProgress(0f);                 // ランプ最小（0.10）を有効化
            ch.PlayOneShot(HapticVocabulary.AckPulses); // その上に Ack（0.25）

            Assert.AreEqual(HapticVocabulary.AckAmp, ch.Tick(0.020f), Eps, "単発が優先");
            // Ack（40ms）が終わると、まだ有効なランプ振幅へフォールバック
            Assert.AreEqual(HapticVocabulary.HoldMinAmp, ch.Tick(0.030f), Eps, "単発終了後はランプへ");
        }

        [Test]
        public void Channel_ClearHold_SilencesRamp()
        {
            var ch = new HapticChannel();
            ch.SetHoldProgress(1f);
            Assert.AreEqual(HapticVocabulary.HoldMaxAmp, ch.Tick(0.016f), Eps);

            ch.SetHoldProgress(-1f); // 解除
            Assert.AreEqual(0f, ch.Tick(0.016f), Eps, "解除で無音");
            Assert.IsTrue(ch.IsIdle);
        }

        [Test]
        public void Channel_IsIdle_WhenNothingActive()
        {
            var ch = new HapticChannel();
            Assert.IsTrue(ch.IsIdle);

            ch.PlayOneShot(HapticVocabulary.ActionPulses);
            Assert.IsFalse(ch.IsIdle);
            ch.Tick(0.200f); // 80ms 超で単発終了
            Assert.IsTrue(ch.IsIdle);
        }
    }
}
