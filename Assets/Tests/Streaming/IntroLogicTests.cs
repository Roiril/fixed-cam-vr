#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// IntroLogic（導入演出の段の状態機械）の検証。
    ///
    /// 2026-08-13 に段を作り直した（Real / Degrade / Structure / Frame / Swap →
    /// <b>Seal / Dark / Ignite / Live</b>）。体験者は封印の箱の<b>中に入ってから</b>固定視点になるので、
    /// 「箱の外で現実を格下げしていく」旧構成は成立しない。
    ///
    /// ここで守るのは 5 つ:
    ///   (A) 段 0 は合図でしか進まない（落ち着いたかは人間か体験者の居場所しか判定できない）
    ///   (B) 段は Seal → Dark → Ignite → Live の順に流れ、尺の合計は卓と一致する
    ///   (C) <b>段 2 は「中に入った」を待つが、上限 3 秒で必ず抜ける</b>
    ///   (D) <b>「中に居る」はヒステリシスで決める</b>（黒を箱の面より先に立てる）
    ///   (E) <b>管の点灯（ignite）は演出の外で必ず 1</b>（0 のままだと画がまるごと消える）
    /// </summary>
    public sealed class IntroLogicTests
    {
        // コード既定と同じ尺で試す。独自の長い尺で試すと、条件待ちの上限との関係が実運用とずれる。
        private static readonly IntroTiming T = new IntroTiming
        {
            sealSec = 1.4f, darkSec = 0.8f, igniteSec = 1.6f, liveSec = 2.4f, maxSec = 20f,
        };

        private static IntroLogic Make()
        {
            var l = new IntroLogic();
            l.Configure(T);
            l.Begin();
            return l;
        }

        /// <summary>黒が明けていて、体験者が箱の中に居て、映像も届いている観測値。</summary>
        private static IntroInput Ready(bool live = true, float outsideM = 0f) => new IntroInput
        {
            blackCleared = true, headTurnDegPerSec = 0f, frameCentered = true,
            liveFresh = live, recentered = false, outsideBoxM = outsideM,
        };

        /// <summary>秒数ぶん進める（段の途中の見えを確かめたいときだけ使う）。</summary>
        private static void Advance(IntroLogic l, float sec, IntroInput input, float dt = 0.1f)
        {
            int n = (int)System.Math.Round(sec / dt);
            for (int i = 0; i < n; i++) l.Tick(dt, input);
        }

        /// <summary>
        /// その段を抜けるまで進める。**秒数で進めない**のは、0.1 を n 回足した値が尺に 1 tick
        /// 届かず段が変わらないことがあるため（float の累積誤差）。
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
        private static IntroLogic AtSeal()
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
            Advance(l, 30f, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Black, l.Stage, "時間では進まない（近づいたかは体験者が決める）");

            l.RequestAdvance();
            l.Tick(0.1f, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Seal, l.Stage);
        }

        [Test]
        public void Black_DoesNotAdvanceBeforeStartupFaderClears()
        {
            var l = Make();
            l.RequestAdvance();
            l.Tick(0.1f, new IntroInput { blackCleared = false, outsideBoxM = 3f });
            Assert.AreEqual(IntroStage.Black, l.Stage, "黒が明ける前に合図が来ても待つ");
        }

        [Test]
        public void Black_DoesNotBurnTheMaxSecBudget()
        {
            // 合図を待つ時間は演出の尺ではない。ここで maxSec を食うと、待たせた分だけ演出が飛ばされる。
            var l = Make();
            Advance(l, 60f, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Black, l.Stage);
            Assert.AreEqual(0f, l.TotalElapsedSec, 0.001f);
        }

        [Test]
        public void Black_AlsoAdvancesWhenTheVisitorApproaches()
        {
            var l = Make();
            var near = Ready(outsideM: 3f);
            near.atStartSpot = true;
            l.Tick(0.1f, near);
            Assert.AreEqual(IntroStage.Seal, l.Stage, "スタッフの合図を待たずに始まる");
        }

        [Test]
        public void Black_StaysWhenNoSignalArrives()
        {
            var l = Make();
            Advance(l, 20f, Ready(outsideM: 3f));      // Ready() は atStartSpot=false
            Assert.AreEqual(IntroStage.Black, l.Stage);
        }

        [Test]
        public void Black_StartsWhenTheVisitorWalksAllTheWayIn()
        {
            // ⚠⚠ **安全網**（2026-08-13）。段 0 でエリアの中に入ると重みは真っ黒に倒れる。
            //    近づく合図が成立していないと、体験者は**何も起きない黒の中に立ったまま**になる。
            //    開始位置が箱に近い現場ほど踏むので、歩いて入ってきたら必ず始める。
            var l = Make();
            Advance(l, 1f, Ready(outsideM: 3f));       // 外に居たことを観測させる
            Assert.AreEqual(IntroStage.Black, l.Stage);
            Assert.IsTrue(l.SawOutsideBox);

            l.Tick(0.1f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Seal, l.Stage, "歩いて入ったのに黒のまま止まっている");
        }

        [Test]
        public void Black_DoesNotStartForSomeoneWhoWasAlreadyInside()
        {
            // ⚠ **状態ではなく事象**。前の体験者が中に立ったままのリセット・エリア内に置いた HMD で
            //    勝手に走り出してはいけない（canon/LEDGER.md 0005 が禁じた形）。
            var l = Make();
            Advance(l, 30f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, l.Stage, "中に居ただけで演出が始まった");
            Assert.IsFalse(l.SawOutsideBox);
        }

        [Test]
        public void Black_TheOutsideObservationIsForgottenOnRestart()
        {
            // 体験者交代。前の人の観測が残っていると、中に立ったままのリセットで走り出す。
            var l = Make();
            Advance(l, 1f, Ready(outsideM: 3f));
            Assert.IsTrue(l.SawOutsideBox);

            l.Begin();
            Assert.IsFalse(l.SawOutsideBox);
            Advance(l, 5f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, l.Stage, "リセット後に前の観測で始まった");
        }

        // ---- (B) 段の直列と尺 --------------------------------------------------

        [Test]
        public void Stages_RunInOrderAndFinish()
        {
            var l = AtSeal();
            Assert.AreEqual(IntroStage.Seal, l.Stage);

            RunStage(l, IntroStage.Seal, Ready());
            Assert.AreEqual(IntroStage.Dark, l.Stage);

            RunStage(l, IntroStage.Dark, Ready());
            Assert.AreEqual(IntroStage.Ignite, l.Stage);

            RunStage(l, IntroStage.Ignite, Ready());
            Assert.AreEqual(IntroStage.Live, l.Stage);

            var ev = RunStage(l, IntroStage.Live, Ready());
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Advance_SkipsTheCurrentStageOnly()
        {
            var l = AtSeal();
            l.RequestAdvance(); l.Tick(0.1f, Ready());       // → Dark
            Assert.AreEqual(IntroStage.Dark, l.Stage);
            l.RequestAdvance(); l.Tick(0.1f, Ready());       // → Ignite
            Assert.AreEqual(IntroStage.Ignite, l.Stage);
        }

        [Test]
        public void TotalSec_IsTheSumOfTheFourStages()
        {
            // ⚠ この数字は卓の `intro-model.test.mjs` と**同じ値**にしてある。
            //    1.4 + 0.8 + 1.6 + 2.4 = 6.2（段 2 の「中に入るのを待つ」時間は尺に含めない）。
            //    片方だけ直すと、卓の表示と実機の尺が沈黙して食い違う。
            Assert.AreEqual(6.2f, T.TotalSec, 0.001f);
            Assert.AreEqual(6.2f, IntroTiming.Default.TotalSec, 0.001f);
        }

        [Test]
        public void Timing_ZeroOrNegativeFallsBackToCodeDefaults()
        {
            var broken = new IntroTiming();     // 全部 0
            var s = broken.Sanitized();
            Assert.AreEqual(IntroTiming.Default.sealSec, s.sealSec, 0.001f);
            Assert.AreEqual(IntroTiming.Default.maxSec, s.maxSec, 0.001f);
            Assert.Greater(s.TotalSec, 0f, "0 秒の段を黙って作らない");
        }

        [Test]
        public void Default_FitsInTheOpeningBudget()
        {
            var d = IntroTiming.Default;
            Assert.GreaterOrEqual(d.TotalSec, 5f, $"短すぎる: {d.TotalSec}s");
            Assert.LessOrEqual(d.TotalSec, 9f, $"長すぎる: {d.TotalSec}s");
            // クロスフェードの後に「自分だ」と気づく時間が残っていること
            Assert.Greater(d.liveSec - IntroLogic.LiveCrossfadeSec, 1f);
            // 待ちを全部踏んでも打ち切り（maxSec）に掛からないこと。掛かると段が飛ぶ。
            float worst = d.TotalSec + IntroLogic.DarkHoldMaxSec + IntroLogic.MaxHoldSec;
            Assert.Less(worst, d.maxSec, $"最悪ケース {worst}s が上限 {d.maxSec}s を超える");
        }

        [Test]
        public void Default_RunsEndToEndWithinMaxSec()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            l.RequestAdvance();
            l.Tick(0.1f, Ready());

            var ev = IntroEvent.None;
            int ticks = 0;
            while (ev == IntroEvent.None && ticks < 600) { ev = l.Tick(0.05f, Ready()); ticks++; }
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.LessOrEqual(l.TotalElapsedSec, IntroTiming.Default.maxSec,
                $"打ち切りに頼らず自力で終わること（{l.TotalElapsedSec}s）");
        }

        // ---- (C) 段 2 は待つが、必ず抜ける -------------------------------------

        [Test]
        public void Dark_WaitsUntilTheVisitorIsInsideTheBox()
        {
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Dark, l.Stage);

            // まだ外に居る。尺を過ぎても管は点けない。
            Advance(l, T.darkSec + 1f, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Dark, l.Stage, "外に居るのに管が点いた");
            Assert.IsTrue(l.Holding);

            // 中に入ったら進む。
            Advance(l, 0.3f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Ignite, l.Stage, "中に入っても管が点かない");
        }

        [Test]
        public void Dark_GivesUpWaitingAfterTheCap()
        {
            // ⚠ **必ず抜ける。** 位置が解けない現場・端末を机に置いた自動走行でも体験は先へ進む。
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Dark, l.Stage);

            Advance(l, T.darkSec + IntroLogic.DarkHoldMaxSec + 0.5f, Ready(outsideM: 3f));
            Assert.AreEqual(IntroStage.Ignite, l.Stage,
                $"段 2 が上限 {IntroLogic.DarkHoldMaxSec}s を過ぎても抜けていない");
        }

        [Test]
        public void Dark_AlsoWaitsWhileTheHeadIsTurning()
        {
            // 見ていない方向で管が点くと、出現そのものを見逃す。
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            Assert.AreEqual(IntroStage.Dark, l.Stage);

            var turning = Ready();
            turning.headTurnDegPerSec = IntroLogic.MaxHeadTurnForIgnite + 30f;
            Advance(l, T.darkSec + 1f, turning);
            Assert.AreEqual(IntroStage.Dark, l.Stage, "頭を振っている間は管を点けない");

            Advance(l, 0.3f, Ready());
            Assert.AreEqual(IntroStage.Ignite, l.Stage, "止まったら進む");
        }

        // ---- (D) 「中に居る」のヒステリシス -------------------------------------

        [Test]
        public void InsideBox_LeadsTheBoxFace_AndHasHysteresis()
        {
            var l = Make();
            // 外に居る
            l.Tick(0.016f, Ready(outsideM: 1.0f));
            Assert.IsFalse(l.InsideBox);

            // ⚠ **境界の手前で「中」に倒す**（黒を箱の面より先に立てる）。
            l.Tick(0.016f, Ready(outsideM: IntroLogic.InsideEnterM));
            Assert.IsTrue(l.InsideBox, "境界の手前で黒へ倒れていない（③ の一瞬の覗きが再発する）");

            // 入る閾値をわずかに超えたくらいでは外へ戻らない（震えで黒と箱が点滅しない）。
            l.Tick(0.016f, Ready(outsideM: IntroLogic.InsideEnterM + 0.1f));
            Assert.IsTrue(l.InsideBox, "ヒステリシスが効いていない");

            // 抜ける閾値まで離れて初めて外に戻る。
            l.Tick(0.016f, Ready(outsideM: IntroLogic.InsideExitM));
            Assert.IsFalse(l.InsideBox);
        }

        [Test]
        public void InsideBox_ThresholdsKeepTheBlackAhead()
        {
            Assert.Greater(IntroLogic.InsideEnterM, 0f, "境界そのもので判定すると黒が箱より遅れる");
            Assert.Greater(IntroLogic.InsideExitM, IntroLogic.InsideEnterM, "ヒステリシスが逆向き");
        }

        [Test]
        public void Black_ShowsTheBoxOutside_AndBlackInside()
        {
            // ⚠ 別々の個体で見る。**同じ個体で外 → 中と動かすと安全網（歩いて入った）が働いて
            //    段 1 へ進んでしまい、段 0 の見えを測れない**。
            var outside = Make();
            outside.Tick(0.016f, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Black, outside.Stage);
            Assert.AreEqual(1f, outside.Weights.sealBox, 1e-4f);
            Assert.AreEqual(0f, outside.Weights.shell, 1e-4f);

            // 最初から中に居た（前の体験者が残っている・エリア内に置いた HMD）。演出は始まらないが、
            // **中の様子は 1 画素も見せない**（canon/LEDGER.md 0005）。
            var inside = Make();
            inside.Tick(0.016f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, inside.Stage);
            Assert.AreEqual(0f, inside.Weights.sealBox, 1e-4f);
            Assert.AreEqual(1f, inside.Weights.shell, 1e-4f);
            Assert.AreEqual(0f, inside.Weights.shellReveal, 1e-4f);
        }

        // ---- 段ごとの見え ------------------------------------------------------

        [Test]
        public void Seal_ClosesTheApertureAndPullsTheBoxDownTogether()
        {
            var l = AtSeal();
            Advance(l, T.sealSec * 0.5f, Ready(outsideM: 2f));
            var mid = l.Weights;
            Assert.Greater(mid.frame, 0f, "開口が閉じ始めていない");
            Assert.Less(mid.frame, 1f);
            Assert.Less(mid.passthrough, 1f, "現実が閉じ始めていない");
            Assert.Less(mid.sealBox, 1f, "箱が引き始めていない");
            Assert.AreEqual(0f, mid.live, 1e-4f, "映像はまだ出さない");
            Assert.AreEqual(0f, mid.ignite, 1e-4f, "管はまだ点けない");
        }

        [Test]
        public void Dark_IsCompletelyBlack()
        {
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            Assert.AreEqual(IntroStage.Dark, l.Stage);
            var w = l.Weights;
            Assert.AreEqual(0f, w.passthrough, 1e-4f, "全黒のはずが現実が見えている");
            Assert.AreEqual(1f, w.frame, 1e-4f);
            Assert.AreEqual(0f, w.live, 1e-4f);
            Assert.AreEqual(0f, w.ignite, 1e-4f);
            Assert.AreEqual(0f, w.sealBox, 1e-4f);
        }

        [Test]
        public void Ignite_RaisesTheTube_AndLetsTheBlackGo()
        {
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            RunStage(l, IntroStage.Dark, Ready());
            Assert.AreEqual(IntroStage.Ignite, l.Stage);

            Advance(l, T.igniteSec * 0.5f, Ready());
            var mid = l.Weights;
            Assert.Greater(mid.ignite, 0f, "管が点き始めていない");
            Assert.Less(mid.ignite, 1f);
            Assert.AreEqual(0f, mid.live, 1e-4f, "段 3 で映像を出している（管だけのはず）");
            // ⚠ 殻は全画面の面でスクリーンごと黒く塗る。管と入れ替わりに引かないと管が見えない。
            Assert.Less(mid.shell, 1f, "殻が 1 のままだと点いた管が 1 画素も見えない");
        }

        [Test]
        public void Live_CrossfadesTheVideoIn_AndHidesTheSeamWithGlitch()
        {
            var l = AtSeal();
            for (int i = 0; i < 3; i++) { l.RequestAdvance(); l.Tick(0.1f, Ready()); }
            Assert.AreEqual(IntroStage.Live, l.Stage);

            Advance(l, IntroLogic.LiveCrossfadeSec * 0.5f, Ready());
            var mid = l.Weights;
            Assert.Greater(mid.live, 0f);
            Assert.Less(mid.live, 1f);
            Assert.Greater(mid.glitch, 0f, "継ぎ目は乱れで隠す");
            Assert.AreEqual(1f, mid.frame, 1e-4f, "枠は既に閉じている");
            Assert.AreEqual(1f, mid.ignite, 1e-4f, "映像が来る段で管が消えている");
            Assert.AreEqual(0f, mid.passthrough, 1e-4f, "段 4 で現実が 1 画素でも出ている");
            Assert.AreEqual(0f, mid.shell, 1e-4f);

            Advance(l, IntroLogic.LiveCrossfadeSec, Ready());
            var after = l.Weights;
            Assert.AreEqual(1f, after.live, 0.01f);
            Assert.Less(after.glitch, 0.2f);
        }

        [Test]
        public void RetiredWeights_StayAtZero_ButAreStillInTheVocabulary()
        {
            // 段としては廃止したが、語彙からは消していない（終幕 OutroLogic が使う）。
            var l = AtSeal();
            for (int i = 0; i < 200 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.05f, Ready());
                var w = l.Weights;
                Assert.AreEqual(0f, w.shatter, 1e-5f, $"段 {l.Stage} で破砕が動いている");
                Assert.AreEqual(0f, w.degrade, 1e-5f, $"段 {l.Stage} で格下げが動いている");
                Assert.AreEqual(0f, w.edge, 1e-5f, $"段 {l.Stage} で輪郭が動いている");
                Assert.AreEqual(0f, w.structure, 1e-5f, $"段 {l.Stage} で構造の線が動いている");
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        // ---- (C) 必ず本編へ入る -------------------------------------------------

        [Test]
        public void Live_StillRunsWithStaticWhenNoCameraFrameArrives()
        {
            // ⚠⚠ **カメラが繋がっていなくても段 4 は流れる**（canon/LEDGER.md 0025）。
            //    旧実装はここで演出ごと畳んで本編へ落としていた ＝ 管が点いた次の瞬間に導入が終わり、
            //    体験者から見て「装置が点いたのに何も起きずに始まった」になっていた。
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            RunStage(l, IntroStage.Dark, Ready());
            Assert.AreEqual(IntroStage.Ignite, l.Stage);

            RunStage(l, IntroStage.Ignite, Ready(live: false));
            Assert.AreEqual(IntroStage.Live, l.Stage, "映像が無いと段 4 が飛ぶ（砂嵐を見せる段が消える）");

            // 段 4 の見えは映像があるときと同じ。**画の中身は SignalLostFx が砂嵐で埋める**ので、
            // ここは「管が点いたまま、映像の枠が開く」ことだけを担保する。
            var w = l.Weights;
            Assert.AreEqual(1f, w.ignite, 1e-4f, "砂嵐を出す段で管が消えている");
            Assert.AreEqual(1f, w.frame, 1e-4f);
            Assert.AreEqual(0f, w.passthrough, 1e-4f);

            var ev = RunStage(l, IntroStage.Live, Ready(live: false));
            Assert.AreEqual(IntroEvent.Finished, ev, "砂嵐でも段 4 は最後まで流れて終わる");
        }

        [Test]
        public void Live_WaitsBrieflyForTheCameraBeforeShowingStatic()
        {
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            RunStage(l, IntroStage.Dark, Ready());
            var dead = Ready(live: false);
            Advance(l, T.igniteSec + 1f, dead);
            Assert.AreEqual(IntroStage.Ignite, l.Stage, "少しは待つ（映像が遅れて来ることがある）");

            Advance(l, 1f, Ready());
            Assert.AreEqual(IntroStage.Live, l.Stage, "来たら進む");
        }

        [Test]
        public void Live_TheWaitForTheCameraIsBounded()
        {
            // 待ちは上限で必ず切れる。切れないと「カメラが死んだ日は導入が段 3 で固まる」。
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            RunStage(l, IntroStage.Dark, Ready());
            Assert.AreEqual(IntroStage.Ignite, l.Stage);

            Advance(l, T.igniteSec + IntroLogic.MaxHoldSec + 0.5f, Ready(live: false));
            Assert.AreEqual(IntroStage.Live, l.Stage,
                $"段 3 が上限 {IntroLogic.MaxHoldSec}s を過ぎても抜けていない");
        }

        [Test]
        public void MaxSec_ForcesTheShowToStart()
        {
            // 条件待ちで固まっても体験は必ず始まる。
            var t = T; t.maxSec = 6f;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.RequestAdvance(); l.Tick(0.1f, Ready(outsideM: 5f));

            var stuck = Ready(outsideM: 5f);
            stuck.headTurnDegPerSec = 300f;      // ずっと頭を振っていて、しかも箱の外に居る
            var ev = IntroEvent.None;
            for (float x = 0f; x < 30f && ev == IntroEvent.None; x += 0.1f) ev = l.Tick(0.1f, stuck);
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.LessOrEqual(l.TotalElapsedSec, 7f);
        }

        [Test]
        public void MaxSec_DoesNotCutTheLiveStageInHalf()
        {
            // 映像が点く最中に打ち切ると、いちばん見せたい一撃が途中で消える。
            var t = T; t.maxSec = 2f;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.RequestAdvance(); l.Tick(0.1f, Ready());
            for (int i = 0; i < 3; i++) { l.RequestAdvance(); l.Tick(0.1f, Ready()); }
            Assert.AreEqual(IntroStage.Live, l.Stage);

            Advance(l, 2.0f, Ready());
            Assert.Greater(l.TotalElapsedSec, t.maxSec, "maxSec は既に超えている");
            Assert.Less(l.StageElapsedSec, t.liveSec, "Live はまだ自然終了していない");
            Assert.AreEqual(IntroStage.Live, l.Stage, "それでも Live は畳まない");
        }

        // ---- 中止とやり直し ----------------------------------------------------

        [Test]
        public void Recenter_AbortsInsteadOfShowingAMisalignedRoom()
        {
            var l = AtSeal();
            Advance(l, T.sealSec * 0.5f, Ready());

            var moved = Ready();
            moved.recentered = true;
            Assert.AreEqual(IntroEvent.Aborted, l.Tick(0.1f, moved));
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        [Test]
        public void Restart_GoesBackToStageOne_NotToBlack()
        {
            // HMD を被り直された。黒はもう明けているので段 0 へは戻らない。
            var l = AtSeal();
            RunStage(l, IntroStage.Seal, Ready());
            Assert.AreEqual(IntroStage.Dark, l.Stage);

            l.Restart();
            Assert.AreEqual(IntroStage.Seal, l.Stage);
            Assert.AreEqual(0f, l.TotalElapsedSec, 0.001f);
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

        // ---- (E) 管の点灯は演出の外で必ず 1 -------------------------------------

        [Test]
        public void Ignite_IsOneWheneverTheIntroIsNotRunning()
        {
            // ⚠⚠ **0 を書いたままにすると画がまるごと消える。** 演出を出していない全期間で 1。
            Assert.AreEqual(1f, IntroWeights.Inactive.ignite, 1e-4f, "既定が 1 でない");

            // 無効化（演出を出さない設定・本編中・終了後）
            var off = Make();
            off.Disable();
            Assert.AreEqual(1f, off.Weights.ignite, 1e-4f, "無効化したのに管が消えている");

            // 中止（トラッキング原点が変わった）
            var aborted = AtSeal();
            var moved = Ready();
            moved.recentered = true;
            Assert.AreEqual(IntroEvent.Aborted, aborted.Tick(0.1f, moved));
            Assert.AreEqual(1f, aborted.Weights.ignite, 1e-4f, "中止したのに管が消えている");

            // 完走（Done）
            var done = AtSeal();
            for (int i = 0; i < 400 && done.Stage != IntroStage.Done; i++) done.Tick(0.05f, Ready());
            Assert.AreEqual(IntroStage.Done, done.Stage);
            Assert.AreEqual(1f, done.Weights.ignite, 1e-4f, "終わったのに管が消えている");

            // 飛ばした（スタッフ操作）
            var skipped = AtSeal();
            skipped.RequestSkip();
            skipped.Tick(0.1f, Ready());
            Assert.AreEqual(1f, skipped.Weights.ignite, 1e-4f, "飛ばしたのに管が消えている");
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
