#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 警告音は回を重ねて大きくなる（<c>canon/LEDGER.md</c> 0145）。
    ///
    /// ⚠⚠ <b>画にも録画にも出ない。</b> 実機で聴いても「鳴ってはいる」ので、
    /// 育っていないことに気づけるのはここと走行ログの <c>swAlert</c> の 2 つ目だけ。
    /// </summary>
    public sealed class AlertEscalationLogicTests
    {
        [Test]
        public void FirstIsQuietest_AndTheLastIsExactlyFull()
        {
            Assert.AreEqual(0.398f, AlertEscalationLogic.GainFor(1), 0.005f,
                            "1 発目は -8dB（＝ 0.398 倍）");
            Assert.AreEqual(1f, AlertEscalationLogic.GainFor(AlertEscalationLogic.RampToCount), 1e-4f,
                            "頭打ちはちょうど 1.0（焼いてある高さが天井）");
        }

        /// <summary>
        /// ⚠ <b>1 を超えない。</b> <c>AudioSource.volume</c> は 1.0 が天井なので、
        /// 超える値を返しても潰れて何も変わらないのにコードは「大きくした」つもりになる
        /// （乱れの音で踏んだ型）。
        /// </summary>
        [Test]
        public void NeverExceedsOne_EvenAfterTheRamp()
        {
            for (int n = AlertEscalationLogic.RampToCount; n < AlertEscalationLogic.RampToCount + 20; n++)
            {
                Assert.AreEqual(1f, AlertEscalationLogic.GainFor(n), 1e-4f,
                                $"{n} 発目が 1.0 を離れた");
            }
        }

        [Test]
        public void RisesEveryTime_UntilItSaturates()
        {
            float prev = 0f;
            for (int n = 1; n <= AlertEscalationLogic.RampToCount; n++)
            {
                float g = AlertEscalationLogic.GainFor(n);
                Assert.Greater(g, prev, $"{n} 発目が前より大きくなっていない");
                prev = g;
            }
        }

        /// <summary>
        /// ⚠⚠ <b>1 段ぶんの差は、土台に掛かる散らし（±1.5dB）より大きくなければ意味が無い。</b>
        /// 警告そのものには散らしを掛けていないが、段が細かすぎると人には同じに聞こえる。
        /// </summary>
        [Test]
        public void EachStepIsAudible()
        {
            for (int n = 2; n <= AlertEscalationLogic.RampToCount; n++)
            {
                float lo = AlertEscalationLogic.GainFor(n - 1);
                float hi = AlertEscalationLogic.GainFor(n);
                float db = 20f * Mathf_Log10(hi / lo);
                Assert.GreaterOrEqual(db, 1.0f, $"{n} 発目の段が {db:F2}dB しかない");
            }
        }

        /// <summary>
        /// ⚠⚠ <b>落とさないと 2 人目が最初から最大で鳴る。</b>
        /// 落とす場所は <c>ShowRunDirector.BeginRun</c> 1 か所（乱れの育ちと同じ）。
        /// </summary>
        [Test]
        public void ResetRun_PutsTheNextVisitorBackToTheStart()
        {
            var esc = new AlertEscalationLogic();
            for (int i = 0; i < AlertEscalationLogic.RampToCount + 3; i++) esc.Next();
            Assert.AreEqual(1f, esc.LastGain, 1e-4f, "頭打ちまで育っていない");

            esc.ResetRun();
            Assert.AreEqual(0, esc.Count, "回数が落ちていない");
            Assert.AreEqual(0f, esc.LastGain, 1e-4f, "倍率が落ちていない");
            Assert.AreEqual(AlertEscalationLogic.GainFor(1), esc.Next(), 1e-4f,
                            "次の体験者が 1 発目から始まっていない");
        }

        [Test]
        public void Next_CountsAndReturnsTheSameCurve()
        {
            var esc = new AlertEscalationLogic();
            for (int n = 1; n <= AlertEscalationLogic.RampToCount; n++)
            {
                Assert.AreEqual(AlertEscalationLogic.GainFor(n), esc.Next(), 1e-4f,
                                $"{n} 発目が表と食い違う");
                Assert.AreEqual(n, esc.Count);
            }
        }

        private static float Mathf_Log10(float x) => (float)System.Math.Log10(x);
    }
}
