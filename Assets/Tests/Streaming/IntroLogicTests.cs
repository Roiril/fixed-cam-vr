#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// IntroLogic（導入演出の段の状態機械）の検証。
    /// 設計の正本は <c>.claude/plans/2026-07-30_intro-passthrough-to-screen.md</c>。
    ///
    /// ここで守るのは 4 つ:
    ///   (A) 段 0 はスタッフの合図でしか進まない（落ち着いたかは人間しか判定できない）
    ///   (B) 段 2 と段 3 が**重なる**（構造の線は段 2 の後半から出る）
    ///   (C) 条件待ちで固まっても**必ず本編へ入る**（maxSec の打ち切り・映像不在の分岐）
    ///   (D) パススルーは段 5 の最後まで生きている（切ると数百 ms の黒が出るため）
    /// </summary>
    public sealed class IntroLogicTests
    {
        private static readonly IntroTiming T = new IntroTiming
        {
            realSec = 4f, degradeSec = 8f, structureSec = 6f,
            frameSec = 5f, swapSec = 8f, maxSec = 40f,
        };

        private static IntroLogic Make()
        {
            var l = new IntroLogic();
            l.Configure(T);
            l.Begin();
            return l;
        }

        /// <summary>黒が明けていてスタッフが合図した状態の観測値。</summary>
        private static IntroInput Ready(bool live = true) => new IntroInput
        {
            blackCleared = true, headTurnDegPerSec = 0f, frameCentered = true,
            liveFresh = live, recentered = false,
        };

        /// <summary>秒数ぶん進める（段の途中の見えを確かめたいときだけ使う）。</summary>
        private static void Advance(IntroLogic l, float sec, IntroInput input, float dt = 0.1f)
        {
            int n = (int)System.Math.Round(sec / dt);
            for (int i = 0; i < n; i++) l.Tick(dt, input);
        }

        /// <summary>
        /// その段を抜けるまで進める。**秒数で進めない**のは、0.1 を n 回足した値が尺に 1 tick
        /// 届かず段が変わらないことがあるため（float の累積誤差）。テストの意図も
        /// 「この段を抜けたら」なので、そのまま書ける方が正しい。
        /// </summary>
        private static IntroEvent RunStage(IntroLogic l, IntroStage stage, IntroInput input,
                                           float limitSec = 30f, float dt = 0.1f)
        {
            int n = (int)(limitSec / dt);
            for (int i = 0; i < n && l.Stage == stage; i++)
            {
                var ev = l.Tick(dt, input);
                if (ev != IntroEvent.None) return ev;
            }
            return IntroEvent.None;
        }

        /// <summary>段 0 を抜けて段 1 に居る状態を作る。</summary>
        private static IntroLogic AtReal()
        {
            var l = Make();
            l.RequestAdvance();
            l.Tick(0.1f, Ready());
            return l;
        }

        // ---- (A) 段 0 ----------------------------------------------------------

        [Test]
        public void Black_WaitsForStaff_NotForTime()
        {
            var l = Make();
            Advance(l, 30f, Ready());
            Assert.AreEqual(IntroStage.Black, l.Stage, "時間では進まない（落ち着いたかは人間が判定する）");

            l.RequestAdvance();
            l.Tick(0.1f, Ready());
            Assert.AreEqual(IntroStage.Real, l.Stage);
        }

        [Test]
        public void Black_DoesNotAdvanceBeforeStartupFaderClears()
        {
            var l = Make();
            l.RequestAdvance();
            l.Tick(0.1f, new IntroInput { blackCleared = false });
            Assert.AreEqual(IntroStage.Black, l.Stage, "黒が明ける前に合図が来ても待つ");
        }

        [Test]
        public void Black_DoesNotBurnTheMaxSecBudget()
        {
            // スタッフを待つ時間は演出の尺ではない。ここで maxSec を食うと、
            // 待たせた分だけ演出が飛ばされる。
            var l = Make();
            Advance(l, 60f, Ready());
            Assert.AreEqual(IntroStage.Black, l.Stage);
            Assert.AreEqual(0f, l.TotalElapsedSec, 0.001f);
        }

        // ---- 段の直列 ----------------------------------------------------------

        [Test]
        public void Stages_RunInOrderAndFinish()
        {
            var l = AtReal();
            Assert.AreEqual(IntroStage.Real, l.Stage);

            RunStage(l, IntroStage.Real, Ready());
            Assert.AreEqual(IntroStage.Degrade, l.Stage);

            RunStage(l, IntroStage.Degrade, Ready());
            Assert.AreEqual(IntroStage.Structure, l.Stage);

            RunStage(l, IntroStage.Structure, Ready());
            Assert.AreEqual(IntroStage.Frame, l.Stage);

            RunStage(l, IntroStage.Frame, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage);

            var ev = RunStage(l, IntroStage.Swap, Ready());
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Advance_SkipsTheCurrentStageOnly()
        {
            var l = AtReal();
            l.RequestAdvance(); l.Tick(0.1f, Ready());       // → Degrade
            Assert.AreEqual(IntroStage.Degrade, l.Stage);
            l.RequestAdvance(); l.Tick(0.1f, Ready());       // → Structure
            Assert.AreEqual(IntroStage.Structure, l.Stage);
        }

        [Test]
        public void Skip_JumpsStraightToTheMainShowLook()
        {
            var l = Make();
            l.RequestSkip();
            Assert.AreEqual(IntroEvent.Finished, l.Tick(0.1f, Ready()));
            Assert.AreEqual(IntroStage.Done, l.Stage);
            var w = l.Weights;
            Assert.AreEqual(0f, w.passthrough, 0.001f, "本編は黒背景");
            Assert.AreEqual(1f, w.live, 0.001f);
            Assert.AreEqual(1f, w.frame, 0.001f);
        }

        // ---- (B) 段 2 と段 3 の重なり -------------------------------------------

        [Test]
        public void Structure_StartsDuringDegrade_NotAfterIt()
        {
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());
            Assert.AreEqual(IntroStage.Degrade, l.Stage);

            // 段 2 の前半では構造の線は出ていない
            Advance(l, T.degradeSec * 0.4f, Ready());
            Assert.AreEqual(0f, l.Weights.structure, 0.001f);

            // 後半に入ると出始める（段が変わらないまま）
            Advance(l, T.degradeSec * 0.4f, Ready());
            Assert.AreEqual(IntroStage.Degrade, l.Stage);
            Assert.Greater(l.Weights.structure, 0f, "段 2 の後半から構造の線が出る");
            Assert.Less(l.Weights.structure, 1f);
        }

        [Test]
        public void Degrade_AddsEffectsInOrder_ColorThenEdgeThenGrain()
        {
            // 一度に全部動かすと「質感が落ちた」ではなく「ただ壊れた」に見える。
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());

            Advance(l, T.degradeSec * 0.2f, Ready());
            var early = l.Weights;
            Assert.Greater(early.degrade, 0f, "色は最初から動く");
            Assert.AreEqual(0f, early.edge, 0.001f, "輪郭はまだ");
            Assert.AreEqual(0f, early.grain, 0.001f, "粒はまだ");

            Advance(l, T.degradeSec * 0.3f, Ready());
            Assert.Greater(l.Weights.edge, 0f, "輪郭が先に出る");

            Advance(l, T.degradeSec * 0.3f, Ready());
            Assert.Greater(l.Weights.grain, 0f, "粒は最後");
        }

        // ---- 段 4 の保留 --------------------------------------------------------

        [Test]
        public void Frame_WaitsWhileTheHeadIsTurning()
        {
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());
            RunStage(l, IntroStage.Degrade, Ready());
            Assert.AreEqual(IntroStage.Structure, l.Stage);

            var turning = Ready();
            turning.headTurnDegPerSec = IntroLogic.MaxHeadTurnForFrame + 30f;
            Advance(l, T.structureSec + 1f, turning);
            Assert.AreEqual(IntroStage.Structure, l.Stage, "頭を振っている間は枠を出さない");
            Assert.IsTrue(l.Holding);

            Advance(l, 1f, Ready());
            Assert.AreEqual(IntroStage.Frame, l.Stage, "止まったら進む");
        }

        [Test]
        public void Frame_GivesUpWaitingAfterMaxHold()
        {
            // 待ち続けて体験が止まるより、枠を出して先へ進む方がまし。
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());
            RunStage(l, IntroStage.Degrade, Ready());
            var turning = Ready();
            turning.headTurnDegPerSec = 200f;
            Advance(l, T.structureSec + IntroLogic.MaxHoldSec + 1f, turning);
            Assert.AreEqual(IntroStage.Frame, l.Stage);
        }

        // ---- (C) 必ず本編へ入る -------------------------------------------------

        [Test]
        public void Swap_IsSkippedWhenNoCameraFrameArrives()
        {
            // 映像が来ていないなら段 5 は無意味（枠の中に自分が映らない）。砂嵐を見せずに畳む。
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());
            RunStage(l, IntroStage.Degrade, Ready());
            RunStage(l, IntroStage.Structure, Ready());
            Assert.AreEqual(IntroStage.Frame, l.Stage);

            var ev = RunStage(l, IntroStage.Frame, Ready(live: false));
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        [Test]
        public void Swap_WaitsBrieflyForTheCameraBeforeGivingUp()
        {
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());
            RunStage(l, IntroStage.Degrade, Ready());
            RunStage(l, IntroStage.Structure, Ready());
            var dead = Ready(live: false);
            Advance(l, T.frameSec + 1f, dead);
            Assert.AreEqual(IntroStage.Frame, l.Stage, "少しは待つ（映像が遅れて来ることがある）");

            Advance(l, 1f, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage, "来たら進む");
        }

        [Test]
        public void MaxSec_ForcesTheShowToStart()
        {
            // 条件待ちで固まっても体験は必ず始まる。
            var t = T; t.maxSec = 12f;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.RequestAdvance(); l.Tick(0.1f, Ready());

            var stuck = Ready();
            stuck.headTurnDegPerSec = 300f;      // ずっと頭を振っている
            var ev = IntroEvent.None;
            for (float x = 0f; x < 30f && ev == IntroEvent.None; x += 0.1f) ev = l.Tick(0.1f, stuck);
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.LessOrEqual(l.TotalElapsedSec, 13f);
        }

        [Test]
        public void MaxSec_DoesNotCutTheSwapInHalf()
        {
            // すり替えの最中に打ち切ると、いちばん見せたい一撃が途中で消える。
            var t = T; t.maxSec = 5f;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.RequestAdvance(); l.Tick(0.1f, Ready());
            // 段を手で送って Swap まで持っていく
            for (int i = 0; i < 4; i++) { l.RequestAdvance(); l.Tick(0.1f, Ready()); }
            Assert.AreEqual(IntroStage.Swap, l.Stage);

            Advance(l, 6f, Ready());
            Assert.Greater(l.TotalElapsedSec, t.maxSec, "maxSec は既に超えている");
            Assert.AreEqual(IntroStage.Swap, l.Stage, "それでも Swap は畳まない");
        }

        // ---- 中止とやり直し ----------------------------------------------------

        [Test]
        public void Recenter_AbortsInsteadOfShowingAMisalignedRoom()
        {
            var l = AtReal();
            Advance(l, T.realSec + 1f, Ready());

            var moved = Ready();
            moved.recentered = true;
            Assert.AreEqual(IntroEvent.Aborted, l.Tick(0.1f, moved));
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        [Test]
        public void Restart_GoesBackToStageOne_NotToBlack()
        {
            // HMD を被り直された。黒はもう明けているので段 0 へは戻らない。
            var l = AtReal();
            RunStage(l, IntroStage.Real, Ready());
            RunStage(l, IntroStage.Degrade, Ready());
            Assert.AreEqual(IntroStage.Structure, l.Stage);

            l.Restart();
            Assert.AreEqual(IntroStage.Real, l.Stage);
            Assert.AreEqual(0f, l.TotalElapsedSec, 0.001f);
            Assert.AreEqual(0f, l.Weights.structure, 0.001f);
        }

        [Test]
        public void Disable_LooksLikeTheMainShow()
        {
            var l = Make();
            l.Disable();
            Assert.IsFalse(l.Active);
            Assert.AreEqual(IntroEvent.None, l.Tick(1f, Ready()));
            var w = l.Weights;
            Assert.AreEqual(0f, w.passthrough, 0.001f);
            Assert.AreEqual(1f, w.live, 0.001f);
        }

        // ---- (D) パススルーは最後まで生きている ---------------------------------

        [Test]
        public void Passthrough_StaysOnUntilTheSwapCrossfade()
        {
            // 切ると数百 ms の黒が出る（公式に "black flicker"）。切るのは本編に入ってから。
            var l = AtReal();
            Assert.AreEqual(1f, l.Weights.passthrough, 0.001f);
            RunStage(l, IntroStage.Real, Ready());
            Assert.AreEqual(1f, l.Weights.passthrough, 0.001f, "格下げ中も現実は見えている");
            RunStage(l, IntroStage.Degrade, Ready());
            Assert.AreEqual(1f, l.Weights.passthrough, 0.001f, "構造の段でも見えている");
            RunStage(l, IntroStage.Structure, Ready());
            Assert.AreEqual(IntroStage.Frame, l.Stage);
            Assert.AreEqual(1f, l.Weights.passthrough, 0.001f, "枠になっても中身は現実のまま");

            RunStage(l, IntroStage.Frame, Ready());
            Assert.AreEqual(IntroStage.Swap, l.Stage);
            Advance(l, IntroLogic.SwapCrossfadeSec * 0.5f, Ready());
            Assert.Less(l.Weights.passthrough, 0.9f, "ここで初めて薄くなる");
        }

        [Test]
        public void Swap_CrossfadesLiveIn_AndHidesTheSeamWithGlitch()
        {
            var l = AtReal();
            for (int i = 0; i < 4; i++) { l.RequestAdvance(); l.Tick(0.1f, Ready()); }
            Assert.AreEqual(IntroStage.Swap, l.Stage);

            // 入り口: 映像はまだ薄く、乱れが立っている
            Advance(l, IntroLogic.SwapCrossfadeSec * 0.5f, Ready());
            var mid = l.Weights;
            Assert.Greater(mid.live, 0f);
            Assert.Less(mid.live, 1f);
            Assert.Greater(mid.glitch, 0f, "継ぎ目は乱れで隠す");
            Assert.AreEqual(1f, mid.frame, 0.001f, "枠は既に閉じている");

            // 抜けた後: 映像だけ。乱れは引く（以後は演出側の乱れが持つ）
            Advance(l, IntroLogic.SwapCrossfadeSec, Ready());
            var after = l.Weights;
            Assert.AreEqual(1f, after.live, 0.01f);
            Assert.AreEqual(0f, after.passthrough, 0.01f);
            Assert.Less(after.glitch, 0.2f);
        }

        // ---- 尺の計算（卓の表示と一致させる）------------------------------------

        [Test]
        public void TotalSec_CountsTheOverlappingStageOnce()
        {
            // 段 3 は段 2 と重なるので、合計に二重で足さない（卓の「演出 31s」と一致させる）。
            Assert.AreEqual(4f + 8f + 5f + 8f, T.TotalSec, 0.001f);
        }

        [Test]
        public void Timing_ZeroOrNegativeFallsBackToCodeDefaults()
        {
            var broken = new IntroTiming();     // 全部 0
            var s = broken.Sanitized();
            Assert.AreEqual(IntroTiming.Default.realSec, s.realSec, 0.001f);
            Assert.AreEqual(IntroTiming.Default.maxSec, s.maxSec, 0.001f);
            Assert.Greater(s.TotalSec, 0f, "0 秒の段を黙って作らない");
        }

        // ---- 補助関数 ----------------------------------------------------------

        [Test]
        public void SmoothStep_And_Bump_StayInRange()
        {
            Assert.AreEqual(0f, IntroLogic.SmoothStep(0.2f, 0.8f, 0.1f), 0.001f);
            Assert.AreEqual(1f, IntroLogic.SmoothStep(0.2f, 0.8f, 0.9f), 0.001f);
            Assert.AreEqual(0.5f, IntroLogic.SmoothStep(0f, 1f, 0.5f), 0.001f);
            Assert.AreEqual(0f, IntroLogic.Bump(0f), 0.001f);
            Assert.AreEqual(1f, IntroLogic.Bump(0.5f), 0.001f);
            Assert.AreEqual(0f, IntroLogic.Bump(1f), 0.001f);
        }
    }
}
