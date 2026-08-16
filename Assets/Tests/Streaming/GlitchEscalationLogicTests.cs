#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 乱れが起きるたびに大きくなること（`canon/LEDGER.md` 0055）。
    /// <b>序盤〜中盤は軽く、終盤で跳ねる</b>（2026-08-16 にユーザーが直線を却下した）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 4 つ。どれも走行の画を見ても気づきにくい:
    /// ① 曲線が直線に戻る（却下された形）
    /// ② 序盤が重い（体験の前半が最初から荒い）
    /// ③ 天井を超えて音が潰れる（コードは「大きくした」つもりになる）
    /// ④ 次の体験者へ持ち越される
    /// </summary>
    public sealed class GlitchEscalationLogicTests
    {
        private const int Sat = GlitchEscalationLogic.SaturateAtCount;

        private static GlitchEscalationLogic After(int n)
        {
            var l = new GlitchEscalationLogic();
            for (int i = 0; i < n; i++) l.Notify();
            return l;
        }

        // ---------------------------------------------------------------- 曲線の形

        [Test]
        public void ItIsNotLinear_TheFirstHalfBarelyMoves()
        {
            // ⚠ ユーザーが直線を却下した（「指数関数的に最後にかけて粗く」）。
            //    半分まで来た時点で、まだ全体の 2 割も進んでいないこと。
            var half = After(1 + (Sat - 1) / 2);
            Assert.Less(half.Curve01, 0.20f,
                        $"中盤で既に {half.Curve01:F2} まで来ている（直線に戻っていないか）");
            Assert.AreEqual(0.5f, half.Progress01, 0.06f, "回数の進み自体は半分のはず");
        }

        [Test]
        public void ItRampsHardAtTheEnd()
        {
            // 最後の 3 回で、全体の半分以上を一気に進む。
            float atMinus3 = After(Sat - 3).Curve01;
            Assert.Less(atMinus3, 0.5f);
            Assert.AreEqual(1f, After(Sat).Curve01, 1e-6f);
            Assert.Greater(1f - atMinus3, 0.5f, "終盤の跳ね上がりが足りない");
        }

        [Test]
        public void EveryStepIsBiggerThanTheOneBefore()
        {
            float prev = -1f;
            for (int n = 1; n <= Sat; n++)
            {
                float v = After(n).Curve01;
                Assert.Greater(v, prev, $"{n} 回目で増えていない");
                prev = v;
            }
        }

        // ---------------------------------------------------------------- 序盤は軽い

        [Test]
        public void TheFirstGlitchIsLighterThanTheScript()
        {
            // ⚠ ユーザー指定「最初〜中盤はもっと軽くていい」。台本どおりではなく**下回る**。
            var l = After(1);
            Assert.AreEqual(0f, l.Curve01, 1e-6f);
            Assert.Less(l.ApplyLevel(0.70f), 0.70f, "1 回目が台本より軽くない");
            Assert.Less(l.ApplyHold(0.40f), 0.40f, "1 回目の尺が台本より短くない");
        }

        [Test]
        public void TheMiddleIsStillLighterThanTheScript()
        {
            // 中盤（全 12 回の 6 回目）でも台本の値を超えないこと。
            var l = After(6);
            Assert.Less(l.ApplyLevel(0.70f), 0.70f, $"6 回目で既に台本を超えている");
        }

        // ---------------------------------------------------------------- 終盤

        [Test]
        public void ByTheEndItIsClearlyBigger_InPictureAndInSound()
        {
            var first = After(1);
            var last = After(Sat);
            Assert.AreEqual(GlitchEscalationLogic.MaxLevel, last.ApplyLevel(0.70f), 1e-3f,
                            "台本 0.70 が強さの上限まで上がる");
            Assert.AreEqual(GlitchEscalationLogic.MaxHoldSec, last.ApplyHold(0.40f), 1e-3f,
                            "尺が上限まで伸びる");
            Assert.AreEqual(1f, last.VolumeGain, 1e-6f, "音は天井（設計どおりの高さ）まで上がる");
            Assert.Greater(last.ApplyHold(0.40f) / first.ApplyHold(0.40f), 2.4f,
                           "最初と最後で尺が 2.5 倍ほどちがうこと");
        }

        // ---------------------------------------------------------------- 尺の上限

        [Test]
        public void OneGlitchNeverLastsLongerThanTheCap()
        {
            // ⚠ ユーザー指定「乱れが起こる時間は長くても 0.6s」。
            //    台本の尺はカットごとに違う（0.40s / 0.50s）ので、**倍率だけでは守れない**。
            foreach (float authored in new[] { 0.10f, 0.35f, 0.40f, 0.50f, 0.59f })
            {
                for (int n = 1; n <= Sat + 10; n++)
                {
                    float v = After(n).ApplyHold(authored);
                    Assert.LessOrEqual(v, GlitchEscalationLogic.MaxHoldSec + 1e-4f,
                                       $"台本 {authored}s の {n} 回目が上限を超えた（{v:F3}s）");
                }
            }
        }

        [Test]
        public void TheCapHasNoExceptions_EvenForALongAuthoredValue()
        {
            // ⚠ 一度「著作が上限を超えていたら著作を優先する」と書いたが、それは**上限に穴を開ける**
            //    だけだった（台本 0.59s が 0.537s まで伸びた）。例外は作らない。
            foreach (float authored in new[] { 0.59f, 0.90f, 2.0f })
            {
                for (int n = 1; n <= Sat + 5; n++)
                {
                    Assert.LessOrEqual(After(n).ApplyHold(authored),
                                       GlitchEscalationLogic.MaxHoldSec + 1e-4f,
                                       $"台本 {authored}s の {n} 回目が上限を超えた");
                }
            }
        }

        [Test]
        public void ItStopsGrowing_SoTheLastOnesAreNotAbsurd()
        {
            var at = After(Sat);
            var far = After(Sat + 40);
            Assert.AreEqual(at.Curve01, far.Curve01, 1e-6f);
            Assert.AreEqual(at.ApplyHold(0.4f), far.ApplyHold(0.4f), 1e-6f);
            Assert.AreEqual(at.VolumeGain, far.VolumeGain, 1e-6f);
        }

        // ---------------------------------------------------------------- 台本との関係

        [Test]
        public void TheRelativeStrengthOfTheScriptIsKept()
        {
            // ⚠ 弱く置いた切替の乱れ（0.25）は、最後まで強い乱れ（0.7）より弱いままであること。
            //    置き換えではなく掛け算にしている理由がこれ。
            var l = After(Sat);
            Assert.Less(l.ApplyLevel(0.25f), l.ApplyLevel(0.70f));
        }

        [Test]
        public void ZeroStaysZero()
        {
            Assert.AreEqual(0f, After(Sat).ApplyLevel(0f), 1e-6f);
        }

        [Test]
        public void OneGlitchNeverGetsStrongerThanTheCap()
        {
            // ⚠ ユーザー指定「強さの上限も、0.83 にして」。台本の強さはカットごとに違う
            //    （0.25 / 0.70 / 0.80）ので、**倍率だけでは守れない**。
            foreach (float authored in new[] { 0.25f, 0.50f, 0.70f, 0.80f, 0.83f })
            {
                for (int n = 1; n <= Sat + 10; n++)
                {
                    float v = After(n).ApplyLevel(authored);
                    Assert.LessOrEqual(v, GlitchEscalationLogic.MaxLevel + 1e-4f,
                                       $"台本 {authored} の {n} 回目が上限を超えた（{v:F3}）");
                }
            }
        }

        [Test]
        public void TheLevelCapHasNoExceptions_EvenForAStrongAuthoredValue()
        {
            foreach (float authored in new[] { 0.90f, 0.95f, 1.0f })
            {
                for (int n = 1; n <= Sat + 5; n++)
                {
                    Assert.LessOrEqual(After(n).ApplyLevel(authored),
                                       GlitchEscalationLogic.MaxLevel + 1e-4f,
                                       $"台本 {authored} の {n} 回目が上限を超えた");
                }
            }
        }

        [Test]
        public void TheScreenNeverFillsCompletelyWithNoise()
        {
            // ⚠ 全面が砂で埋まると「乱れ」ではなく「信号断」に見える。上限はそれを避けるためにある。
            Assert.Less(GlitchEscalationLogic.MaxLevel, 1f);
        }

        [Test]
        public void ZeroLengthStaysZero_SoSustainOnlyTransitionsAreUntouched()
        {
            // transition:"glitch" のカットは尺 0 で持続成分だけを使う。ここを伸ばすと意味が変わる。
            var l = After(Sat);
            Assert.AreEqual(0f, l.ApplyHold(0f), 1e-6f);
        }

        // ---------------------------------------------------------------- 音

        [Test]
        public void TheSoundNeverExceedsFull_BecauseThePlayerWouldClampIt()
        {
            // ⚠⚠ SfxPlayer.Play は Clamp01(gain × masterGain) で masterGain は 1.0。
            //    1 を超える倍率を返すと**音は 1 ビットも変わらない**のに、コードは
            //    「大きくした」つもりになる。ここで超えないことを固定する。
            for (int n = 0; n <= Sat + 20; n++)
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
            var last = After(Sat);
            double db = 20.0 * System.Math.Log10(last.VolumeGain / first.VolumeGain);
            Assert.Less(db, 3.0, $"音の上がり幅が大きすぎる（{db:F1} dB）");
            Assert.Greater(db, 1.0, $"上がり幅が小さすぎて聞き分けられない（{db:F1} dB）");
        }

        // ---------------------------------------------------------------- ラン単位

        [Test]
        public void TheNextVisitorStartsFromTheLightestAgain()
        {
            var l = After(Sat + 5);
            l.ResetRun();
            Assert.AreEqual(0, l.Count);
            l.Notify();
            Assert.AreEqual(0f, l.Curve01, 1e-6f, "前の体験者の分が残っている");
        }
    }
}
