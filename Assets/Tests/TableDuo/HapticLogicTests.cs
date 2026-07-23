#nullable enable
using NUnit.Framework;
using TableDuoVr.Hands;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// 触覚パターンの純ロジック（HapticVocabulary / HapticPatternPlayer / HapticChannel）を時刻注入で固定する。
    /// これは TableDuo 独自系統（HapticChannel = one-shot 無条件差し替え・優先 one-shot&gt;hold&gt;0 の単純合成）で、
    /// fixed-cam-vr 側 ControllerHaptics（ピーク振幅で後着差し替え）とは別実装。両者を混同しないよう
    /// TableDuo の実挙動をそのまま pin する（GameObject/NGO 非依存の純関数テストのみ）。
    /// </summary>
    public class HapticLogicTests
    {
        private const float Tol = 1e-4f;

        // --- HapticVocabulary.HoldAmplitude ---

        [Test]
        public void HoldAmplitude_ClampsAtBoundaries()
        {
            Assert.AreEqual(HapticVocabulary.HoldMinAmp, HapticVocabulary.HoldAmplitude(0f), Tol, "progress=0 は下限");
            Assert.AreEqual(HapticVocabulary.HoldMaxAmp, HapticVocabulary.HoldAmplitude(1f), Tol, "progress=1 は上限");
            Assert.AreEqual(0.20f, HapticVocabulary.HoldAmplitude(0.5f), Tol, "中点は線形補間 0.20");
            Assert.AreEqual(HapticVocabulary.HoldMinAmp, HapticVocabulary.HoldAmplitude(-0.3f), Tol, "負はクランプ下限");
            Assert.AreEqual(HapticVocabulary.HoldMaxAmp, HapticVocabulary.HoldAmplitude(1.5f), Tol, ">1 はクランプ上限");
        }

        // --- HapticPatternPlayer ---

        [Test]
        public void Player_AckSingle_AutoStopsAtExactTotal()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.AckPulses);
            Assert.IsTrue(p.IsPlaying);

            Assert.AreEqual(HapticVocabulary.AckAmp, p.Tick(0.020f), Tol, "総尺内は Ack 振幅");
            Assert.IsTrue(p.IsPlaying);

            // 累計 0.040 >= total 0.040 で自動停止
            Assert.AreEqual(0f, p.Tick(0.020f), Tol, "総尺ちょうどで 0");
            Assert.IsFalse(p.IsPlaying, "総尺到達で自動停止");
        }

        [Test]
        public void Player_Fire_ReturnsZeroInMiddleSilentSegment()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.FirePulses); // 0.08@0.8 / 0.08@0 / 0.08@0.8, total 0.24

            Assert.AreEqual(HapticVocabulary.FireAmp, p.Tick(0.04f), Tol, "第1山 (t=0.04)");
            Assert.AreEqual(0f, p.Tick(0.08f), Tol, "中間無音セグメント (t=0.12)");
            Assert.AreEqual(HapticVocabulary.FireAmp, p.Tick(0.08f), Tol, "第2山 (t=0.20)");
            // 0.04f だと float 累積誤差で _elapsed が _total(0.24) を僅かに下回りフレークするため、明確に総尺を越える dt にする
            Assert.AreEqual(0f, p.Tick(0.05f), Tol, "総尺超 (t=0.25) で停止");
            Assert.IsFalse(p.IsPlaying);
        }

        [Test]
        public void Player_Error_ThreePulsesTwoRests()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.ErrorPulses); // 0.05@0.6 / 0.06@0 / 0.05@0.6 / 0.06@0 / 0.05@0.6

            Assert.AreEqual(HapticVocabulary.ErrorAmp, p.Tick(0.025f), Tol, "山1 (t=0.025)");
            Assert.AreEqual(0f, p.Tick(0.055f), Tol, "休符1 (t=0.08)");
            Assert.AreEqual(HapticVocabulary.ErrorAmp, p.Tick(0.055f), Tol, "山2 (t=0.135)");
            Assert.AreEqual(0f, p.Tick(0.055f), Tol, "休符2 (t=0.19)");
            Assert.AreEqual(HapticVocabulary.ErrorAmp, p.Tick(0.055f), Tol, "山3 (t=0.245)");
        }

        [Test]
        public void Player_Stop_ReturnsZeroAndNotPlaying()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.AckPulses);
            p.Stop();
            Assert.AreEqual(0f, p.Tick(0.010f), Tol);
            Assert.IsFalse(p.IsPlaying);
        }

        [Test]
        public void Player_LargeDtSpanningTotal_StopsImmediately()
        {
            var p = new HapticPatternPlayer();
            p.Play(HapticVocabulary.AckPulses);
            Assert.AreEqual(0f, p.Tick(1f), Tol, "1 フレームで総尺を跨いだら即 0");
            Assert.IsFalse(p.IsPlaying);
        }

        // --- HapticChannel ---

        [Test]
        public void Channel_InitiallyIdle()
        {
            var ch = new HapticChannel();
            Assert.IsTrue(ch.IsIdle);
        }

        [Test]
        public void Channel_OneShotOverridesHold()
        {
            var ch = new HapticChannel();
            ch.PlayOneShot(HapticVocabulary.ActionPulses);
            ch.SetHoldProgress(0.5f);
            // one-shot 振幅 > 0 のとき hold（0.5→0.20）でなく one-shot（ActionAmp 0.5）が優先
            Assert.AreEqual(HapticVocabulary.ActionAmp, ch.Tick(0.020f), Tol);
        }

        [Test]
        public void Channel_HoldRampWithoutOneShot_AndClearHoldGoesIdle()
        {
            var ch = new HapticChannel();
            ch.SetHoldProgress(0.5f);
            Assert.AreEqual(0.20f, ch.Tick(0.020f), Tol, "one-shot 無しなら hold ランプ");
            ch.ClearHold();
            Assert.AreEqual(0f, ch.Tick(0.020f), Tol);
            Assert.IsTrue(ch.IsIdle);
        }

        [Test]
        public void Channel_HoldBleedsThroughOneShotSilentGap_PinsCurrentBehavior()
        {
            // 現行挙動を pin: one-shot の無音セグメント中に hold がアクティブだと hold ランプがブリードする。
            // 呼び出し側 ControllerRecenterWatcher は Fire 前に hold を clear するので実害は無いが、
            // 将来のリファクタで気付けるようここで明示固定する。
            var ch = new HapticChannel();
            ch.PlayOneShot(HapticVocabulary.FirePulses);
            ch.SetHoldProgress(0.5f);

            Assert.AreEqual(HapticVocabulary.FireAmp, ch.Tick(0.04f), Tol, "第1山は one-shot 優先");
            Assert.AreEqual(0.20f, ch.Tick(0.08f), Tol, "中間無音ギャップ中は hold(0.20) がブリードする");
        }
    }
}
