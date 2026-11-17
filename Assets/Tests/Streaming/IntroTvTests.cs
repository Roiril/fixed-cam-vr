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
            // 卓の intro-model.test.mjs と同じ値。1.5 + 1.5 + (3.8 - 1.5×0.4) + 1.3 + 0.6
            Assert.AreEqual(8.1f, IntroTiming.Default.TotalSec, 0.001f);
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
            IntroLogic l = AtStage(IntroStage.Frame);
            Assert.AreEqual(0f, l.Weights.ignite, 0.001f, "段 4 の頭は電源が入る前");
            Assert.AreEqual(1f, l.Weights.tvCase);

            // 電源の音が鳴り始める（予約の先読みの後）までは管は消えたまま。
            l.Tick(IntroLogic.TvOnLeadSec * 0.9f, Ready());
            Assert.AreEqual(0f, l.Weights.ignite, 0.001f, "電源の音より先に管が光っている");

            l.Tick(IntroLogic.TvOnLeadSec * 0.1f + IntroLogic.TvLiveFromSec - 0.02f, Ready());
            Assert.Greater(l.Weights.ignite, 0.5f);
            Assert.AreEqual(0f, l.Weights.live, "管が点く前に映像が出ている");

            l.Tick(IntroLogic.TvLiveFullSec - IntroLogic.TvLiveFromSec + 0.02f, Ready());
            Assert.AreEqual(1f, l.Weights.ignite, 0.001f);
            Assert.AreEqual(1f, l.Weights.live, 0.001f, "電源の音の残響が消える所で映像が出切っていない");
            Assert.AreEqual(l.Weights.live, l.Weights.LiveGate, "テレビ方式の映像は出方そのものを書く");
        }

        /// <summary>
        /// 0387: 電源の画は電源の音（`sfx_tv_on`）の秒で動く。段の尺（frameSec）を変えても音に対する時刻は動かない。
        /// 時刻は焼いた音の波形を測って決めた（`IntroLogic` の表の注釈）。
        /// </summary>
        [Test]
        public void Frame_LightFollowsThePowerOnSound_NotTheStageLength()
        {
            // 尖頭（0.030 秒）で縁が光り切る（シェーダの rim は .30 で頂点）。
            Assert.AreEqual(0.30f, IntroLogic.TvIgniteAt(0.030f), 0.001f);
            // いちばん大きい山（0.19〜0.25 秒）で走査の帯がいちばん明るい所（.58）。
            Assert.AreEqual(0.58f, IntroLogic.TvIgniteAt(0.220f), 0.001f);
            Assert.AreEqual(1f, IntroLogic.TvIgniteAt(0.75f), 0.001f);
            float prev = -1f;
            for (float u = -0.1f; u < 1.5f; u += 0.005f)
            {
                float v = IntroLogic.TvIgniteAt(u);
                Assert.GreaterOrEqual(v, prev, $"電源の進みが {u:0.000} 秒で戻っている");
                Assert.That(v, Is.InRange(0f, 1f));
                prev = v;
            }
            Assert.AreEqual(0f, IntroLogic.TvLiveAt(IntroLogic.TvLiveFromSec), 0.001f);
            Assert.AreEqual(1f, IntroLogic.TvLiveAt(IntroLogic.TvLiveFullSec), 0.001f);

            // 尺の違う 2 つの設定で、段 4 の頭から同じ秒の画が同じ。
            foreach (float frameSec in new[] { 3.0f, 5.0f })
            {
                IntroTiming t = IntroTiming.Default;
                t.frameSec = frameSec;
                var l = new IntroLogic();
                l.Configure(t);
                l.Begin();
                l.Tick(0f, Ready());
                l.Tick(t.realSec, Ready());
                l.Tick(t.degradeSec, Ready());
                l.Tick(StructureOwnSec(t), Ready());
                Assert.AreEqual(IntroStage.Frame, l.Stage);
                l.Tick(IntroLogic.TvOnLeadSec + 0.22f, Ready());
                Assert.AreEqual(0.58f, l.Weights.ignite, 0.002f, $"frameSec={frameSec} で電源の画が音からずれた");
            }
        }

        /// <summary>0387: show.json の frameSec が短くても、映像が出切る前に段 5 へ飛ばない。</summary>
        [Test]
        public void Frame_NeverEndsBeforeThePowerOnFinishes()
        {
            IntroTiming t = IntroTiming.Default;
            t.frameSec = 0.5f;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.Tick(0f, Ready());
            l.Tick(t.realSec, Ready());
            l.Tick(t.degradeSec, Ready());
            l.Tick(StructureOwnSec(t), Ready());
            Assert.AreEqual(IntroStage.Frame, l.Stage);
            for (int i = 0; i < 60; i++) l.Tick(0.02f, Ready());   // 1.2 秒
            Assert.AreEqual(IntroStage.Frame, l.Stage, "映像が出切る前に段 5 へ進んだ");
            Assert.Less(l.Weights.live, 1f);
            // 映像が出切ったら段 5 へ。段 5 の静止 0.6 秒（0398）を飛び越さないよう、抜けた所で止める
            for (int i = 0; i < 120 && l.Stage == IntroStage.Frame; i++) l.Tick(0.02f, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage);
        }

        /// <summary>
        /// 0387: 砂嵐は奥にいるときは小さく、定位置に着いたときに最大値（0383 の半分）。
        /// 振幅は距離に反比例し、テレビが寄ってくる間は戻らずに上がる。
        /// </summary>
        [Test]
        public void TvNoise_IsQuietFarAway_AndReachesHalfAtItsPlace()
        {
            Assert.AreEqual(0.5f, SoundCueLogic.TvNoisePeakGain, 1e-6f, "最大値は 0383 の半分（ユーザー指定）");
            Assert.AreEqual(SoundCueLogic.TvNoisePeakGain, SoundCueLogic.TvNoiseGain(0f), 1e-6f);
            float far = SoundCueLogic.TvNoiseGain(IntroLogic.TvFarM);
            Assert.AreEqual(SoundCueLogic.TvNoisePeakGain * 2f / 9f, far, 1e-5f, "奥の端は 2/9（9m 先）");

            // 段 3 を細かく回して、音量が戻らずに上がり、着いた所で最大値になる。
            IntroLogic l = AtStage(IntroStage.Structure);
            float own = StructureOwnSec(IntroTiming.Default);
            float prev = 0f;
            for (int i = 0; i < 100; i++)
            {
                float g = SoundCueLogic.TvNoiseGain(l.Weights.tvDepthM);
                Assert.GreaterOrEqual(g, prev - 1e-6f, "寄ってくる途中で砂嵐が小さくなった");
                prev = g;
                l.Tick(own / 100f * 0.999f, Ready());
            }
            Assert.AreEqual(SoundCueLogic.TvNoisePeakGain, prev, 0.01f, "定位置に着いても最大値に届いていない");
        }

        [Test]
        public void Swap_KeepsTheCase_AndTheMainShowKeepsTheTvFrame()
        {
            IntroTiming t = IntroTiming.Default;
            IntroLogic l = AtStage(IntroStage.Swap);
            Assert.AreEqual(IntroLogic.MainShowTvCase, l.Weights.tvCase);
            Assert.AreEqual(1f, l.Weights.live);
            l.Tick(t.swapSec * 0.5f, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            Assert.AreEqual(IntroLogic.MainShowTvCase, l.Weights.tvCase, "段 5 の途中で筐体が沈んでいる（0321）");

            IntroEvent ev = l.Tick(t.swapSec * 0.6f, Ready());
            Assert.AreEqual(IntroEvent.Finished, ev);
            IntroWeights done = l.Weights;
            Assert.IsFalse(done.tv);
            Assert.AreEqual(IntroLogic.MainShowTvCase, done.tvCase, "本編でテレビの枠が消えている（0321）");
            Assert.AreEqual(0f, done.tvDark);
            Assert.AreEqual(1f, done.ignite);
            Assert.AreEqual(1f, done.LiveGate);
            Assert.IsFalse(IntroVeil.TvScreenHidden(done), "本編でスクリーンが消える");

            // 導入を通らずに本編にいる（Off）ときも同じ。
            l.Disable();
            Assert.AreEqual(IntroLogic.MainShowTvCase, l.Weights.tvCase, "導入を通らない本編でテレビの枠が出ない");
            Assert.AreEqual(1f, IntroLogic.MainShowTvCase);
        }

        [Test]
        public void Shatter_NeverShowsTheTvCase_InAnyStageOrInTheMainShow()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Shatter);
            Assert.AreEqual(0f, l.Weights.tvCase, "割れる遷移の本編（Off）に筐体が出ている");

            l.Begin();
            bool finished = false;
            for (int i = 0; i < 2000 && !finished; i++)
            {
                finished = l.Tick(0.02f, Ready()) == IntroEvent.Finished;
                Assert.AreEqual(0f, l.Weights.tvCase, $"割れる遷移の段 {l.Stage} に筐体が出ている");
            }
            Assert.IsTrue(finished, "割れる遷移が終わらない");
            Assert.AreEqual(0f, l.Weights.tvCase, "割れる遷移の本編に筐体が出ている");
        }

        [Test]
        public void NoCamera_StillRunsToTheEnd()
        {
            IntroTiming t = IntroTiming.Default;
            IntroLogic l = AtStage(IntroStage.Frame);
            IntroInput noCamera = Ready();
            noCamera.liveFresh = false;
            // 実機と同じ細かい刻みで回す（1 回で大きく進めると、下限を越えた余りが待ちに数えられる）
            float total = System.Math.Max(t.frameSec, IntroLogic.TvPowerOnEndSec) + IntroLogic.MaxHoldSec + 0.1f;
            for (float e = 0f; e < total; e += 0.02f) l.Tick(0.02f, noCamera);
            Assert.AreEqual(IntroStage.Swap, l.Stage, "カメラが無くても砂嵐のまま段 5 へ進む（0025）");
        }

        [Test]
        public void TvNoise_ThenTvOn_EachFireOnce()
        {
            // 0383: テレビが寄ってくる段 3 で砂嵐、目の前に来て電源が入る段 4 の頭で電源の音。
            // 旧スクリーン音（ScreenOn・「ぴこりっ」）は鳴らさない。
            var cues = new SoundCueLogic();
            cues.ResetRun();
            IntroLogic l = AtStage(IntroStage.Structure);
            var s = SoundShowState.Idle;
            s.introActive = true;
            s.introStage = l.Stage;
            s.introWeights = l.Weights;
            System.ReadOnlySpan<SoundCue> noise = cues.Tick(0.01f, s, 0f, out int first);
            Assert.AreEqual(1, first, "テレビが寄ってくる段の頭で砂嵐が鳴っていない");
            Assert.AreEqual(SoundCue.TvNoise, noise[0]);
            cues.Tick(0.01f, s, 0f, out int still);
            Assert.AreEqual(0, still, "砂嵐が 2 回鳴っている");

            l.Tick(StructureOwnSec(IntroTiming.Default), Ready());
            s.introStage = l.Stage;
            s.introWeights = l.Weights;
            System.ReadOnlySpan<SoundCue> fired = cues.Tick(0.01f, s, 0f, out int count);
            Assert.AreEqual(1, count);
            Assert.AreEqual(SoundCue.TvOn, fired[0]);
            cues.Tick(0.01f, s, 0f, out int again);
            Assert.AreEqual(0, again, "電源の音が 2 回鳴っている");
        }
    }
}
