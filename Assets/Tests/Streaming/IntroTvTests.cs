#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// テレビ方式の導入（<c>canon/LEDGER.md</c> 0309）の重み。
    /// パススルー → 暗転 → テレビが出る → 電源 → 映像 の順と、畳んだ後が本編と同じことを守る。
    /// 見た目は <c>unity.ps1 menu intro</c> の PNG でしか分からない。
    /// </summary>
    public sealed class IntroTvTests
    {
        private static IntroInput Ready() => new IntroInput
        {
            blackCleared = true, startAuthorized = true, atStartSpot = true, outsideValid = true,
            outsideBoxM = 2f, frameCentered = true, liveFresh = true,
        };

        private static float StructureOwnSec(IntroTiming t)
            => t.structureSec - t.degradeSec * (1f - IntroLogic.StructureOverlapAt);

        /// <summary>目的の段の頭まで本物の状態機械を回す。</summary>
        private static IntroLogic AtStage(IntroStage target)
        {
            IntroTiming t = IntroTiming.Default;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.Tick(0f, Ready());
            Assert.AreEqual(IntroStage.Real, l.Stage);
            if (target == IntroStage.Real) return l;
            l.Tick(t.realSec, Ready());
            if (target == IntroStage.Degrade) return l;
            l.Tick(t.degradeSec, Ready());
            if (target == IntroStage.Structure) return l;
            l.Tick(StructureOwnSec(t), Ready());
            if (target == IntroStage.Frame) return l;
            l.Tick(t.frameSec, Ready());
            // 段 4 の尺の間に中央の保持も満ちるので、ここで段 5 の頭に居る。
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            return l;
        }

        [Test]
        public void TotalSec_MatchesTheTable()
        {
            // 卓の intro-model.test.mjs と同じ値。1.5 + 1.5 + (3.8 - 1.5×0.4) + 3.0 + 1.6
            Assert.AreEqual(10.8f, IntroTiming.Default.TotalSec, 0.001f);
            Assert.AreEqual(3.2f, StructureOwnSec(IntroTiming.Default), 0.001f);
        }

        [Test]
        public void DefaultIsTv_AndShatterKeepsItsChoreographedClock()
        {
            Assert.AreEqual(IntroTransition.Tv, IntroTiming.Default.transition);
            Assert.AreEqual(IntroTransition.Tv, new ShowIntroDef().ToTiming().transition);
            IntroTiming old = new ShowIntroDef { transition = "shatter", frameSec = 3f }.ToTiming();
            Assert.AreEqual(IntroTransition.Shatter, old.transition);
            Assert.AreEqual(5f, old.frameSec, 0.001f, "割れる遷移の振り付けは 5.0 秒に合わせてある");
        }

        [Test]
        public void ShowIntroDef_FieldDefaults_MatchTheCodeDefault()
        {
            IntroTiming d = IntroTiming.Default;
            var def = new ShowIntroDef();
            Assert.AreEqual(d.realSec, def.realSec, 0.001f);
            Assert.AreEqual(d.degradeSec, def.degradeSec, 0.001f);
            Assert.AreEqual(d.structureSec, def.structureSec, 0.001f);
            Assert.AreEqual(d.frameSec, def.frameSec, 0.001f);
            Assert.AreEqual(d.swapSec, def.swapSec, 0.001f);
        }

        [Test]
        public void Real_ShowsThePassthrough_AndNoTv()
        {
            IntroWeights w = AtStage(IntroStage.Real).Weights;
            Assert.IsTrue(w.tv);
            Assert.AreEqual(1f, w.passthrough);
            Assert.AreEqual(0f, w.tvCase);
            Assert.AreEqual(0f, w.ignite);
        }

        [Test]
        public void Darken_FallsFromRealityToBlack_WithoutBreaking()
        {
            IntroLogic l = AtStage(IntroStage.Degrade);
            float prev = 1.01f;
            for (int i = 0; i < 14; i++)
            {
                IntroWeights w = l.Weights;
                Assert.Less(w.passthrough, prev, "暗転が単調に進んでいない");
                prev = w.passthrough;
                Assert.AreEqual(0f, w.shatter, "テレビ方式で破片を描いている");
                Assert.AreEqual(0f, w.degrade, "暗転で色を抜いている（0207 の案は外した）");
                Assert.AreEqual(0f, w.frame, "暗転の途中で開口が閉じている");
                l.Tick(IntroTiming.Default.degradeSec / 15f, Ready());
            }
            Assert.AreEqual(IntroStage.Degrade, l.Stage);
            Assert.Less(prev, 0.25f);
        }

        [Test]
        public void Structure_HoldsBlack_ThenTheTvComesOutOfTheDark()
        {
            IntroTiming t = IntroTiming.Default;
            float own = StructureOwnSec(t);
            IntroLogic l = AtStage(IntroStage.Structure);
            IntroWeights head = l.Weights;
            Assert.AreEqual(0f, head.passthrough);
            Assert.AreEqual(0f, head.tvCase, "黒が無い");
            Assert.AreEqual(1f, head.tvDark);

            l.Tick(own * (IntroLogic.TvBlackHoldShare - 0.02f), Ready());
            Assert.AreEqual(0f, l.Weights.tvCase, "黒の保持の途中でテレビが出ている");

            l.Tick(own * 0.5f, Ready());
            IntroWeights mid = l.Weights;
            Assert.Greater(mid.tvCase, 0f);
            Assert.Less(mid.tvCase, 1f);
            Assert.AreEqual(1f, mid.tvCase + mid.tvDark, 0.001f, "筐体とガラスの灯りが同じ量で上がっていない");
            Assert.AreEqual(0f, mid.ignite, "電源の前に管が光っている");
            Assert.AreEqual(0f, mid.live, "電源の前に映像が出ている");
        }

        [Test]
        public void Structure_TheTvComesFromTheDepth_AndStopsAtItsPlace()
        {
            IntroTiming t = IntroTiming.Default;
            float own = StructureOwnSec(t);
            IntroLogic l = AtStage(IntroStage.Structure);
            Assert.AreEqual(IntroLogic.TvFarM, l.Weights.tvDepthM, 0.001f, "黒の保持の間に奥へ下がっていない");

            // 寄ってくる間、奥行きは単調に減り、明るさは単調に増える（霧の奥ほど暗い）。
            l.Tick(own * IntroLogic.TvBlackHoldShare, Ready());
            float prevDepth = l.Weights.tvDepthM, prevCase = l.Weights.tvCase;
            float firstSeenDepth = -1f;
            for (int i = 0; i < 40; i++)
            {
                l.Tick(own * (1f - IntroLogic.TvBlackHoldShare) / 42f, Ready());
                IntroWeights w = l.Weights;
                Assert.AreEqual(IntroStage.Structure, l.Stage);
                Assert.LessOrEqual(w.tvDepthM, prevDepth + 1e-4f, "奥へ戻っている");
                Assert.GreaterOrEqual(w.tvCase, prevCase - 1e-4f, "寄ってくるのに暗くなっている");
                if (firstSeenDepth < 0f && w.tvCase > 0.02f) firstSeenDepth = w.tvDepthM;
                prevDepth = w.tvDepthM;
                prevCase = w.tvCase;
            }
            // 見えはじめの距離は定位置の 3 倍より遠い（見かけの幅が 3 倍以上になる）。
            Assert.Greater(IntroLogic.TvRestM + firstSeenDepth, IntroLogic.TvRestM * 3f);
            Assert.Less(prevDepth, 0.05f, "段 3 の終わりで定位置に着いていない");
            Assert.Greater(prevCase, 0.95f);

            // 電源が入るときは定位置。
            Assert.AreEqual(0f, AtStage(IntroStage.Frame).Weights.tvDepthM);
            Assert.AreEqual(0f, AtStage(IntroStage.Swap).Weights.tvDepthM);
            Assert.AreEqual(0f, IntroWeights.Inactive.tvDepthM);
        }

        [Test]
        public void TheScreenIsNotDrawn_UntilTheTvComesOut()
        {
            Assert.IsTrue(IntroVeil.TvScreenHidden(AtStage(IntroStage.Real).Weights), "素通しの間");
            Assert.IsTrue(IntroVeil.TvScreenHidden(AtStage(IntroStage.Degrade).Weights), "暗転の途中");
            IntroLogic l = AtStage(IntroStage.Structure);
            Assert.IsTrue(IntroVeil.TvScreenHidden(l.Weights), "黒の保持");
            l.Tick(StructureOwnSec(IntroTiming.Default) * 0.6f, Ready());
            Assert.IsFalse(IntroVeil.TvScreenHidden(l.Weights), "テレビが出ているのに描いていない");
            Assert.IsFalse(IntroVeil.TvScreenHidden(IntroWeights.Inactive), "本編でスクリーンが消える");
        }

        [Test]
        public void Frame_PowersOnFirst_ThenTheImageComes()
        {
            IntroTiming t = IntroTiming.Default;
            IntroLogic l = AtStage(IntroStage.Frame);
            Assert.AreEqual(0f, l.Weights.ignite, 0.001f, "段 4 の頭は電源が入る前");
            Assert.AreEqual(1f, l.Weights.tvCase);

            l.Tick(t.frameSec * (IntroLogic.TvLiveFrom - 0.02f), Ready());
            Assert.Greater(l.Weights.ignite, 0.5f);
            Assert.AreEqual(0f, l.Weights.live, "管が点く前に映像が出ている");

            l.Tick(t.frameSec * 0.35f, Ready());
            Assert.AreEqual(1f, l.Weights.ignite, 0.001f);
            Assert.Greater(l.Weights.live, 0.5f);
            Assert.AreEqual(l.Weights.live, l.Weights.LiveGate, "テレビ方式の映像は出方そのものを書く");
        }

        [Test]
        public void Swap_SinksTheCase_AndDoneIsTheMainShowLook()
        {
            IntroTiming t = IntroTiming.Default;
            IntroLogic l = AtStage(IntroStage.Swap);
            Assert.AreEqual(1f, l.Weights.tvCase, 0.05f);
            Assert.AreEqual(1f, l.Weights.live);
            l.Tick(t.swapSec * 0.5f, Ready());
            Assert.Less(l.Weights.tvCase, 1f);
            Assert.Greater(l.Weights.tvCase, 0f);

            IntroEvent ev = l.Tick(t.swapSec * 0.6f, Ready());
            Assert.AreEqual(IntroEvent.Finished, ev);
            IntroWeights done = l.Weights;
            Assert.IsFalse(done.tv);
            Assert.AreEqual(0f, done.tvCase, "本編に筐体が残る（0302 の枠なしの形が崩れる）");
            Assert.AreEqual(0f, done.tvDark);
            Assert.AreEqual(1f, done.ignite);
            Assert.AreEqual(1f, done.LiveGate);
        }

        [Test]
        public void NoCamera_StillRunsToTheEnd()
        {
            IntroTiming t = IntroTiming.Default;
            IntroLogic l = AtStage(IntroStage.Frame);
            IntroInput noCamera = Ready();
            noCamera.liveFresh = false;
            l.Tick(t.frameSec, noCamera);
            l.Tick(IntroLogic.MaxHoldSec + 0.1f, noCamera);
            Assert.AreEqual(IntroStage.Swap, l.Stage, "カメラが無くても砂嵐のまま段 5 へ進む（0025）");
        }

        [Test]
        public void ScreenOn_FiresOnce_WhenThePowerComesOn()
        {
            var cues = new SoundCueLogic();
            cues.ResetRun();
            IntroLogic l = AtStage(IntroStage.Structure);
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = l.Stage;
            s.introWeights = l.Weights;
            cues.Tick(0.01f, s, 0f, out int before);
            Assert.AreEqual(0, before, "テレビが出る段で音が鳴っている");

            l.Tick(StructureOwnSec(IntroTiming.Default), Ready());
            s.introStage = l.Stage;
            s.introWeights = l.Weights;
            System.ReadOnlySpan<SoundCue> fired = cues.Tick(0.01f, s, 0f, out int count);
            Assert.AreEqual(1, count);
            Assert.AreEqual(SoundCue.ScreenOn, fired[0]);
            cues.Tick(0.01f, s, 0f, out int again);
            Assert.AreEqual(0, again, "電源の音が 2 回鳴っている");
        }
    }
}
