#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 乱れが起きるたびに大きくなること（`canon/LEDGER.md` 0055）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 3 つ。どれも走行の画を見ても気づきにくい:
    /// ① 1 回目から既に大きい（著作した値が無視される）
    /// ② 天井に早く張り付いて「少しずつ」が消える
    /// ③ 次の体験者に持ち越される（ラン単位の状態を落とし忘れる）
    /// </summary>
    public sealed class GlitchEscalationLogicTests
    {
        private static GlitchEscalationLogic After(int n)
        {
            var l = new GlitchEscalationLogic();
            for (int i = 0; i < n; i++) l.Notify();
            return l;
        }

        [Test]
        public void TheFirstGlitchIsExactlyWhatTheScriptAuthored()
        {
            // ⚠ 1 回目を持ち上げると、台本の 0.7 が実機で 0.7 でなくなる。
            var l = After(1);
            Assert.AreEqual(0f, l.Progress01, 1e-6f);
            Assert.AreEqual(0.7f, l.ApplyLevel(0.7f), 1e-6f);
            Assert.AreEqual(0.40f, l.ApplyHold(0.40f), 1e-6f);
            Assert.AreEqual(GlitchEscalationLogic.SfxGainAtFirst, l.VolumeGain, 1e-6f);
        }

        [Test]
        public void TheSoundNeverExceedsFull_BecauseThePlayerWouldClampIt()
        {
            // ⚠⚠ SfxPlayer.Play は Clamp01(gain × masterGain) で masterGain は 1.0。
            //    1 を超える倍率を返すと**音は 1 ビットも変わらない**のに、コードは
            //    「大きくした」つもりになる。ここで超えないことを固定する。
            for (int n = 0; n <= GlitchEscalationLogic.SaturateAtCount + 20; n++)
            {
                float g = After(n).VolumeGain;
                Assert.LessOrEqual(g, 1f, $"{n} 回目で 1 を超えた（潰れて効かない）");
                Assert.GreaterOrEqual(g, GlitchEscalationLogic.SfxGainAtFirst);
            }
        }

        [Test]
        public void TheSoundRiseIsGentle_AsAsked()
        {
            // ユーザー指定「音の上昇は深いじゃない程度に」。端から端で 2 dB ほどに収める。
            var first = After(1);
            var last = After(GlitchEscalationLogic.SaturateAtCount);
            double db = 20.0 * System.Math.Log10(last.VolumeGain / first.VolumeGain);
            Assert.Less(db, 3.0, $"音の上がり幅が大きすぎる（{db:F1} dB）");
            Assert.Greater(db, 1.0, $"上がり幅が小さすぎて聞き分けられない（{db:F1} dB）");
        }

        [Test]
        public void ItGrowsALittleEachTime_NotAllAtTheEnd()
        {
            // 「少しずつ大きくなる」＝ 隣り合う回で必ず増え、しかも 1 回の増分が小さい。
            float prev = -1f;
            for (int n = 1; n <= GlitchEscalationLogic.SaturateAtCount; n++)
            {
                float v = After(n).ApplyHold(0.40f);
                Assert.Greater(v, prev, $"{n} 回目で増えていない");
                if (prev > 0f) Assert.Less(v - prev, 0.08f, $"{n} 回目の増分が大きすぎる");
                prev = v;
            }
        }

        [Test]
        public void ByTheEndItIsClearlyBigger_InPictureAndInSound()
        {
            var l = After(GlitchEscalationLogic.SaturateAtCount);
            Assert.AreEqual(1f, l.Progress01, 1e-6f);
            // 台本の 0.7 は天井（1.0）まで上がる ＝ いまの著作には無い強さになる。
            Assert.AreEqual(1f, l.ApplyLevel(0.7f), 1e-6f);
            Assert.AreEqual(0.80f, l.ApplyHold(0.40f), 1e-6f, "尺が 2 倍になる");
            Assert.AreEqual(1f, l.VolumeGain, 1e-6f, "音は天井（設計どおりの高さ）まで上がる");
        }

        [Test]
        public void ItStopsGrowing_SoTheLastOnesAreNotAbsurd()
        {
            var at = After(GlitchEscalationLogic.SaturateAtCount);
            var far = After(GlitchEscalationLogic.SaturateAtCount + 40);
            Assert.AreEqual(at.Progress01, far.Progress01, 1e-6f);
            Assert.AreEqual(at.ApplyHold(0.4f), far.ApplyHold(0.4f), 1e-6f);
            Assert.AreEqual(at.VolumeGain, far.VolumeGain, 1e-6f);
        }

        [Test]
        public void TheRelativeStrengthOfTheScriptIsKept()
        {
            // ⚠ 弱く置いた切替の乱れ（0.25）は、最後まで強い乱れ（0.7）より弱いままであること。
            //    置き換えではなく掛け算にしている理由がこれ。
            var l = After(GlitchEscalationLogic.SaturateAtCount);
            Assert.Less(l.ApplyLevel(0.25f), l.ApplyLevel(0.70f));
        }

        [Test]
        public void ItNeverGoesPastFull_EvenWhenTheScriptAlreadyAsksForFull()
        {
            var l = After(GlitchEscalationLogic.SaturateAtCount);
            Assert.AreEqual(1f, l.ApplyLevel(1f), 1e-6f);
            Assert.AreEqual(0f, l.ApplyLevel(0f), 1e-6f);
        }

        [Test]
        public void ZeroLengthStaysZero_SoSustainOnlyTransitionsAreUntouched()
        {
            // transition:"glitch" のカットは尺 0 で持続成分だけを使う。ここを伸ばすと意味が変わる。
            var l = After(GlitchEscalationLogic.SaturateAtCount);
            Assert.AreEqual(0f, l.ApplyHold(0f), 1e-6f);
        }

        [Test]
        public void TheNextVisitorStartsFromTheScriptAgain()
        {
            var l = After(GlitchEscalationLogic.SaturateAtCount + 5);
            l.ResetRun();
            Assert.AreEqual(0, l.Count);
            l.Notify();
            Assert.AreEqual(0.7f, l.ApplyLevel(0.7f), 1e-6f, "前の体験者の分が残っている");
        }
    }
}
