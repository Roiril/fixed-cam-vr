using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Streaming
{
    /// <summary>
    /// 終幕（本編 → パススルーへ戻して終わる）の判断・計時を固定する。
    ///
    /// ここが狂うと「終幕が黒で始まる」「終わったのに終わらない」が実機でしか見えない。
    /// </summary>
    public sealed class OutroLogicTests
    {
        private static OutroLogic Make()
        {
            var l = new OutroLogic();
            l.Configure(OutroTiming.Default);
            return l;
        }

        private static OutroInput Ready(bool ready) => new OutroInput { passthroughReady = ready };

        [Test]
        public void Begin_StartsSilentAndLooksLikeTheShow()
        {
            var l = Make();
            l.Begin();
            Assert.AreEqual(OutroStage.Warm, l.Stage);
            Assert.IsTrue(l.Active);
            Assert.IsTrue(l.Silent, "点火待ちの段は画に出ていない");

            // **本編と同じ見え**。ここで画が変わると「パススルーの点火待ち」が体験者に見える。
            IntroWeights w = l.Weights;
            Assert.AreEqual(IntroWeights.Inactive.passthrough, w.passthrough, 1e-4f);
            Assert.AreEqual(IntroWeights.Inactive.frame, w.frame, 1e-4f);
            Assert.AreEqual(IntroWeights.Inactive.live, w.live, 1e-4f);
        }

        [Test]
        public void Warm_WaitsForPassthroughActuallyComposited()
        {
            var l = Make();
            l.Begin();
            // 「有効化を要求した」では進まない。実際に出るまで待つ。
            l.Tick(0.5f, Ready(false));
            Assert.AreEqual(OutroStage.Warm, l.Stage);
            l.Tick(0.1f, Ready(true));
            Assert.AreEqual(OutroStage.Unswap, l.Stage);
        }

        [Test]
        public void Warm_GivesUpAfterLimit()
        {
            // 待ち続けて本編のまま固まる方が悪い。上限で諦めて進む。
            var l = Make();
            l.Begin();
            l.Tick(OutroLogic.WarmMaxSec + 0.01f, Ready(false));
            Assert.AreEqual(OutroStage.Unswap, l.Stage);
        }

        [Test]
        public void Stages_RunInReverseOfTheIntro()
        {
            var l = Make();
            l.Begin();
            l.Tick(0.1f, Ready(true));
            OutroTiming t = OutroTiming.Default;

            Assert.AreEqual(OutroStage.Unswap, l.Stage);
            l.Tick(t.unswapSec, Ready(true));
            Assert.AreEqual(OutroStage.Open, l.Stage);
            l.Tick(t.openSec, Ready(true));
            Assert.AreEqual(OutroStage.Restore, l.Stage);
            l.Tick(t.restoreSec, Ready(true));
            Assert.AreEqual(OutroStage.Hold, l.Stage);
            Assert.AreEqual(OutroEvent.Finished, l.Tick(t.holdSec, Ready(true)));
            Assert.AreEqual(OutroStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Unswap_TradesLiveForPassthroughInsideTheClosedFrame()
        {
            var l = Make();
            l.Begin();
            l.Tick(0.1f, Ready(true));                       // → Unswap
            l.Tick(OutroTiming.Default.unswapSec * 0.5f, Ready(true));

            IntroWeights w = l.Weights;
            Assert.AreEqual(1f, w.frame, 1e-3f, "枠はまだ閉じ切っている（開くのは次の段）");
            Assert.Greater(w.passthrough, 0.1f, "現実が戻り始めている");
            Assert.Less(w.live, 0.9f, "映像が抜け始めている");
            Assert.AreEqual(1f, w.degrade, 1e-3f, "色はまだ戻っていない");
        }

        [Test]
        public void Open_OpensTheFrameWhilePassthroughStaysUp()
        {
            var l = Make();
            l.Begin();
            l.Tick(0.1f, Ready(true));
            l.Tick(OutroTiming.Default.unswapSec, Ready(true));   // → Open
            Assert.AreEqual(OutroStage.Open, l.Stage);

            l.Tick(OutroTiming.Default.openSec * 0.5f, Ready(true));
            IntroWeights w = l.Weights;
            Assert.AreEqual(1f, w.passthrough, 1e-3f, "枠を開けている間もパススルーは切らない");
            Assert.Less(w.frame, 1f);
            Assert.Greater(w.frame, 0f);
            Assert.AreEqual(0f, w.live, 1e-3f);
        }

        [Test]
        public void Restore_BringsColourBackLast()
        {
            var l = Make();
            l.Begin();
            l.Tick(0.1f, Ready(true));
            OutroTiming t = OutroTiming.Default;
            l.Tick(t.unswapSec, Ready(true));
            l.Tick(t.openSec, Ready(true));                  // → Restore
            Assert.AreEqual(OutroStage.Restore, l.Stage);

            IntroWeights mid = l.Weights;
            Assert.AreEqual(0f, mid.frame, 1e-3f, "枠は既に開き切っている");
            l.Tick(t.restoreSec * 0.5f, Ready(true));
            Assert.Less(l.Weights.degrade, mid.degrade, "色が戻っていく");
        }

        [Test]
        public void Hold_EndsAtPlainPassthrough()
        {
            var l = Make();
            l.Begin();
            l.Tick(0.1f, Ready(true));
            OutroTiming t = OutroTiming.Default;
            l.Tick(t.unswapSec, Ready(true));
            l.Tick(t.openSec, Ready(true));
            l.Tick(t.restoreSec, Ready(true));

            IntroWeights w = l.Weights;
            Assert.AreEqual(1f, w.passthrough, 1e-3f);
            Assert.AreEqual(0f, w.degrade, 1e-3f);
            Assert.AreEqual(0f, w.edge, 1e-3f);
            Assert.AreEqual(0f, w.frame, 1e-3f);
            Assert.AreEqual(0f, w.live, 1e-3f);
        }

        [Test]
        public void Disable_StopsImmediately()
        {
            var l = Make();
            l.Begin();
            l.Tick(0.1f, Ready(true));
            l.Disable();
            Assert.IsFalse(l.Active);
            Assert.AreEqual(OutroStage.Off, l.Stage);
            Assert.AreEqual(OutroEvent.None, l.Tick(10f, Ready(true)));
        }

        [Test]
        public void Timing_FallsBackToDefaultsWhenUnset()
        {
            // run.outro が無い show.json（全部 0）でも走り切る。
            var t = new OutroTiming().Sanitized();
            Assert.AreEqual(OutroTiming.Default.unswapSec, t.unswapSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.openSec, t.openSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.restoreSec, t.restoreSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.holdSec, t.holdSec, 1e-4f);
            Assert.AreEqual(7.5f, t.TotalSec, 1e-3f, "合計が変わったら卓の表示と解析の期待値も直すこと");
        }
    }
}
