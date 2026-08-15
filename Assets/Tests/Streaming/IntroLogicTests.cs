#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// IntroLogic（導入演出の段の状態機械）の検証。
    ///
    /// 2026-08-15 に段を旧構成へ戻した（Seal / Dark / Ignite / Live →
    /// <b>Real / Degrade / Structure / Frame / Swap</b>）。設定が「回収された壁の調査」へ変わって
    /// 体験エリアを隠す必要がなくなり、封印の箱を退避したので、
    /// 「箱の中に入ってから固定視点になる」構成は成立しない（<c>canon/LEDGER.md</c> 0044）。
    ///
    /// ここで守るのは 5 つ:
    ///   (A) 段 0 は合図でしか進まない（落ち着いたかは人間か体験者の居場所しか判定できない）
    ///   (B) 段は Real → Degrade → Structure → Frame → Swap の順に流れ、尺の合計は卓と一致する
    ///   (C) <b>カメラが繋がっていなくても段 5 は流れる</b>（砂嵐が出る・LEDGER 0025）
    ///   (D) <b>封印の箱は全段で出ない</b>（退避したものが黙って復活しない）
    ///   (E) <b>管の点灯（ignite）は全期間 1</b>（0 のままだと画がまるごと消える）
    /// </summary>
    public sealed class IntroLogicTests
    {
        // コード既定と同じ尺で試す。独自の長い尺で試すと、条件待ちの上限との関係が実運用とずれる。
        private static readonly IntroTiming T = new IntroTiming
        {
            realSec = 1.5f, degradeSec = 3.5f, structureSec = 2.5f,
            frameSec = 2.5f, swapSec = 4.5f, maxSec = 20f,
        };

        /// <summary>段 3 が単独で流れる秒数（段 2 と重なるぶんを引いたもの）。</summary>
        private static float StructureOwnSec(IntroTiming t)
        {
            float own = t.structureSec - t.degradeSec * (1f - IntroLogic.StructureOverlapAt);
            return own < IntroLogic.StructureMinOwnSec ? IntroLogic.StructureMinOwnSec : own;
        }

        private static IntroLogic Make()
        {
            var l = new IntroLogic();
            l.Configure(T);
            l.Begin();
            return l;
        }

        /// <summary>
        /// 黒が明けていて、映像も届いている観測値。
        /// <b>人が A を押した後（<c>startAuthorized</c>）で、位置も解けている</b>のが既定。
        /// </summary>
        private static IntroInput Ready(bool live = true, float outsideM = 0f) => new IntroInput
        {
            blackCleared = true, headTurnDegPerSec = 0f, frameCentered = true,
            liveFresh = live, recentered = false, outsideBoxM = outsideM,
            startAuthorized = true, outsideValid = true,
        };

        /// <summary>まだ A が押されていない（タイトルが画面を持っている）観測値。</summary>
        private static IntroInput NotAuthorized(float outsideM = 0f)
        {
            var i = Ready(outsideM: outsideM);
            i.startAuthorized = false;
            return i;
        }

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
        private static IntroLogic AtReal()
        {
            var l = Make();
            l.RequestAdvance();
            l.Tick(0.1f, Ready(outsideM: 2f));
            return l;
        }

        /// <summary>目的の段まで、外に立ったまま進める。</summary>
        private static IntroLogic AtStage(IntroStage target)
        {
            var l = AtReal();
            var outside = Ready(outsideM: 2f);
            for (int i = 0; i < 2000 && l.Stage != target && l.Stage != IntroStage.Done; i++)
                l.Tick(0.05f, outside);
            Assert.AreEqual(target, l.Stage, "目的の段まで進めなかった");
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
            Assert.AreEqual(IntroStage.Real, l.Stage);
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
            Assert.AreEqual(IntroStage.Real, l.Stage, "スタッフの合図を待たずに始まる");
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
            // ⚠⚠ **安全網**（2026-08-13）。近づく合図が成立しないまま体験者がエリアへ入ってしまうと、
            //    スタッフの ⏭ 以外に出口が無い。歩いて入ってきたら必ず始める。
            var l = Make();
            Advance(l, 1f, Ready(outsideM: 3f));       // 外に居たことを観測させる
            Assert.AreEqual(IntroStage.Black, l.Stage);
            Assert.IsTrue(l.SawOutsideBox);

            l.Tick(0.1f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Real, l.Stage, "歩いて入ったのに段 0 のまま止まっている");
        }

        [Test]
        public void Black_DoesNotStartForSomeoneWhoWasAlreadyInside()
        {
            // ⚠ **状態ではなく事象**。前の体験者が中に立ったままのリセット・エリア内に置いた HMD で
            //    勝手に走り出してはいけない（canon/LEDGER.md 0005 が禁じた形）。
            //    2026-08-14 から、これを担保するのは**人が A を押したか**（startAuthorized）。
            //    A を押すのは体験者に被せた後なので、置いた HMD では押されない。
            var l = Make();
            Advance(l, 30f, NotAuthorized(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, l.Stage, "中に居ただけで演出が始まった");
            Assert.IsFalse(l.SawOutsideBox);
        }

        [Test]
        public void Black_TheOutsideObservationIsForgottenOnRestart()
        {
            // 体験者交代。前の人の観測が残っていると、中に立ったままのリセットで走り出す。
            var l = Make();
            Advance(l, 1f, NotAuthorized(outsideM: 3f));
            Assert.IsTrue(l.SawOutsideBox);

            l.Begin();
            Assert.IsFalse(l.SawOutsideBox);
            Advance(l, 5f, NotAuthorized(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, l.Stage, "リセット後に前の観測で始まった");
        }

        [Test]
        public void Black_DoesNotStartUntilAPress_EvenWhenTheVisitorApproaches()
        {
            // ⚠⚠ **2026-08-14 に塞いだ穴。** タイトルが立って A を待っているあいだも段 0 は生きていて、
            //    接近も安全網も判定されていた。**スタッフが HMD を持って体験エリアを横切るだけ**で
            //    導入が始まり、`TitleScreen` が「段 0 を出た」を見て自分を強制終了する ＝ 題字が飛ぶ。
            var l = Make();
            var approached = NotAuthorized(outsideM: 3f);
            approached.atStartSpot = true;
            Advance(l, 5f, approached);
            Assert.AreEqual(IntroStage.Black, l.Stage, "A を押す前に接近だけで始まった（題字が飛ぶ）");

            // 外 → 中 と歩いても、A の前なら安全網も動かない。
            Advance(l, 1f, NotAuthorized(outsideM: 3f));
            Advance(l, 2f, NotAuthorized(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, l.Stage, "A を押す前に安全網で始まった");

            // A が押されたら、同じ観測でその場から始まる。
            l.Tick(0.1f, Ready(outsideM: 0f));
            Assert.AreEqual(IntroStage.Real, l.Stage);
        }

        [Test]
        public void Black_StaffOverrideStillWorksBeforeTheAPress()
        {
            // スタッフの ⏭ は人の判断そのもの。「まだ始めるな」を上書きする権利がある
            // （コントローラが死んでいる現場で唯一の出口でもある）。
            var l = Make();
            l.RequestAdvance();
            l.Tick(0.1f, NotAuthorized(outsideM: 3f));
            Assert.AreEqual(IntroStage.Real, l.Stage);
        }

        [Test]
        public void Black_RescuesTheVisitorStandingInsideFromTheStart()
        {
            // ⚠⚠ **2026-08-14 に塞いだ穴。** 最初からエリアの中に立たされた体験者は
            //    「外に居たことがある」を満たせず、接近も「0.35m 縮む」余地が無い。段 0 は maxSec の
            //    対象外なので時間でも抜けない ＝ **スタッフの ⏭ 以外に出口が無い**。
            //    1.8m 四方の現場では、体験者をエリアの縁に立たせるのが普通に起きる。
            var l = Make();
            var inside = Ready(outsideM: 0f);
            Advance(l, IntroLogic.ConcealStartSec * 0.5f, inside);
            Assert.AreEqual(IntroStage.Black, l.Stage, "救済が早すぎる（近づく合図を待つ余地を潰している）");

            Advance(l, IntroLogic.ConcealStartSec, inside);
            Assert.AreEqual(IntroStage.Real, l.Stage, "中に立ったまま詰んでいる");
        }

        [Test]
        public void Black_DoesNotRescueBeforeTheAPress()
        {
            // 救済も「人が始めた」でゲートする。無条件の時間切れにすると、置いた HMD で走り出す。
            var l = Make();
            Advance(l, IntroLogic.ConcealStartSec * 4f, NotAuthorized(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, l.Stage);
        }

        [Test]
        public void Black_DoesNotRescueWhenThePositionIsUnknown()
        {
            // ⚠⚠ **未登録を「中に居る」と読まない**（2026-08-14・Codex 指摘）。旧実装は
            //    位置が解けないときも 0（＝中）を返していたので、区別する材料がロジック側に無かった。
            var l = Make();
            var unknown = Ready(outsideM: 0f);
            unknown.outsideValid = false;
            Advance(l, IntroLogic.ConcealStartSec * 4f, unknown);
            Assert.AreEqual(IntroStage.Black, l.Stage, "位置が解けないのに自動で始まった");
            Assert.IsFalse(l.InsideBox, "解けない観測で中／外の判断を更新した");
        }

        [Test]
        public void InsideBox_HasHysteresis()
        {
            var l = Make();
            l.Tick(0.016f, Ready(outsideM: 1.0f));
            Assert.IsFalse(l.InsideBox);

            l.Tick(0.016f, Ready(outsideM: IntroLogic.InsideEnterM));
            Assert.IsTrue(l.InsideBox);

            // 境界をわずかに超えたくらいでは外へ戻らない（震えで安全網が点滅しない）。
            l.Tick(0.016f, Ready(outsideM: IntroLogic.InsideExitM * 0.5f));
            Assert.IsTrue(l.InsideBox, "ヒステリシスが効いていない");

            l.Tick(0.016f, Ready(outsideM: IntroLogic.InsideExitM));
            Assert.IsFalse(l.InsideBox);
        }

        [Test]
        public void InsideBox_ThresholdsAreOrdered()
        {
            Assert.Greater(IntroLogic.InsideExitM, IntroLogic.InsideEnterM, "ヒステリシスが逆向き");
        }

        // ---- (B) 段の直列と尺 --------------------------------------------------

        [Test]
        public void Stages_RunInOrderAndFinish()
        {
            var l = AtReal();
            Assert.AreEqual(IntroStage.Real, l.Stage);

            RunStage(l, IntroStage.Real, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Degrade, l.Stage);

            RunStage(l, IntroStage.Degrade, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Structure, l.Stage);

            RunStage(l, IntroStage.Structure, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Frame, l.Stage);

            RunStage(l, IntroStage.Frame, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Swap, l.Stage);

            var ev = RunStage(l, IntroStage.Swap, Ready(outsideM: 2f));
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Advance_SkipsTheCurrentStageOnly()
        {
            var l = AtReal();
            l.RequestAdvance(); l.Tick(0.1f, Ready(outsideM: 2f));   // → Degrade
            Assert.AreEqual(IntroStage.Degrade, l.Stage);
            l.RequestAdvance(); l.Tick(0.1f, Ready(outsideM: 2f));   // → Structure
            Assert.AreEqual(IntroStage.Structure, l.Stage);
        }

        [Test]
        public void TotalSec_AccountsForTheStructureOverlap()
        {
            // ⚠ この数字は卓の `intro-model.test.mjs` と**同じ値**にしてある。
            //    1.5 + 3.5 + (2.5 - 3.5×0.4) + 2.5 + 4.5 = 13.1（段 3 は段 2 の後半から重なる）。
            //    片方だけ直すと、卓の表示と実機の尺が沈黙して食い違う。
            Assert.AreEqual(13.1f, T.TotalSec, 0.001f);
            Assert.AreEqual(13.1f, IntroTiming.Default.TotalSec, 0.001f);
            Assert.Less(T.TotalSec, T.realSec + T.degradeSec + T.structureSec + T.frameSec + T.swapSec,
                "重なりが効いていない（単純和になっている）");
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

        [Test]
        public void Default_FitsInTheOpeningBudget()
        {
            var d = IntroTiming.Default;
            Assert.GreaterOrEqual(d.TotalSec, 10f, $"短すぎる: {d.TotalSec}s");
            Assert.LessOrEqual(d.TotalSec, 16f, $"長すぎる: {d.TotalSec}s");
            // クロスフェードの後に「自分だ」と気づく時間が残っていること
            Assert.Greater(d.swapSec - IntroLogic.SwapCrossfadeSec, 1f);
            // 待ちを全部踏んでも打ち切り（maxSec）に掛からないこと。掛かると段が飛ぶ。
            float worst = d.TotalSec + IntroLogic.MaxHoldSec + IntroLogic.MaxHoldSec;
            Assert.Less(worst, d.maxSec, $"最悪ケース {worst}s が上限 {d.maxSec}s を超える");
        }

        [Test]
        public void Default_RunsEndToEndWithinMaxSec()
        {
            var l = new IntroLogic();
            l.Configure(IntroTiming.Default);
            l.Begin();
            l.RequestAdvance();
            l.Tick(0.1f, Ready(outsideM: 2f));

            var ev = IntroEvent.None;
            int ticks = 0;
            var outside = Ready(outsideM: 2f);
            while (ev == IntroEvent.None && ticks < 1000) { ev = l.Tick(0.05f, outside); ticks++; }
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.LessOrEqual(l.TotalElapsedSec, IntroTiming.Default.maxSec,
                $"打ち切りに頼らず自力で終わること（{l.TotalElapsedSec}s）");
        }

        [Test]
        public void Structure_OverlapsTheBackHalfOfDegrade()
        {
            // 段 3 は段 2 の後半から始まる。段の直列ではなく重みで表すので、段 2 の中で
            // structure が立ち上がっていること（立たないと段 3 が唐突に見える）。
            var l = AtStage(IntroStage.Degrade);
            Advance(l, T.degradeSec * (IntroLogic.StructureOverlapAt - 0.1f), Ready(outsideM: 2f));
            Assert.AreEqual(0f, l.Weights.structure, 1e-4f, "重なる前から線が出ている");

            Advance(l, T.degradeSec * 0.35f, Ready(outsideM: 2f));
            Assert.Greater(l.Weights.structure, 0f, "段 2 の後半で線が出始めていない");
        }

        [Test]
        public void Structure_RunsForTheRemainderOnly()
        {
            var l = AtStage(IntroStage.Structure);
            float own = StructureOwnSec(T);
            Advance(l, own * 0.5f, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Structure, l.Stage);

            Advance(l, own, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Frame, l.Stage, $"段 3 が単独ぶん（{own}s）で抜けていない");
        }

        [Test]
        public void Structure_WaitsWhileTheHeadIsTurning()
        {
            // 見ていない方向で枠が閉じ始めると、出来事そのものを見逃す。
            var l = AtStage(IntroStage.Structure);
            var turning = Ready(outsideM: 2f);
            turning.headTurnDegPerSec = IntroLogic.MaxHeadTurnForFrame + 30f;
            Advance(l, StructureOwnSec(T) + 1f, turning);
            Assert.AreEqual(IntroStage.Structure, l.Stage, "頭を振っている間は枠を閉じ始めない");

            Advance(l, 0.3f, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Frame, l.Stage, "止まったら進む");
        }

        // ---- 段ごとの見え ------------------------------------------------------

        [Test]
        public void Black_ShowsThePassthrough_WhereverTheVisitorStands()
        {
            // ⚠ **段 0 は黒くない。** 名前に反して現実が見えている（スタッフが誘導して歩かせる区間）。
            //    箱が無くなったので、中に立っていても外に立っていても見えは同じ。
            var outside = Make();
            outside.Tick(0.016f, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Black, outside.Stage);
            Assert.AreEqual(1f, outside.Weights.passthrough, 1e-4f);
            Assert.AreEqual(0f, outside.Weights.shell, 1e-4f);
            Assert.AreEqual(0f, outside.Weights.sealBox, 1e-4f);

            var inside = Make();
            inside.Tick(0.016f, NotAuthorized(outsideM: 0f));
            Assert.AreEqual(IntroStage.Black, inside.Stage);
            Assert.AreEqual(1f, inside.Weights.passthrough, 1e-4f, "中に立つと黒に落ちている");
            Assert.AreEqual(0f, inside.Weights.shell, 1e-4f);
        }

        [Test]
        public void Degrade_DrainsTheColourBeforeTheEdges()
        {
            // 色 → コントラスト → 輪郭 → 粒 の順に足す。一度に全部動かすと
            // 「質感が落ちた」ではなく「ただ壊れた」に見える。
            var l = AtStage(IntroStage.Degrade);
            Advance(l, T.degradeSec * 0.2f, Ready(outsideM: 2f));
            var early = l.Weights;
            Assert.Greater(early.degrade, 0f, "色が抜け始めていない");
            Assert.AreEqual(0f, early.edge, 1e-4f, "輪郭が色より先に出ている");
            Assert.AreEqual(1f, early.passthrough, 1e-4f, "格下げの段で現実が消えている");
            Assert.AreEqual(0f, early.frame, 1e-4f, "枠はまだ閉じない");

            Advance(l, T.degradeSec * 0.6f, Ready(outsideM: 2f));
            Assert.Greater(l.Weights.edge, 0f, "後半で輪郭が出ていない");
        }

        [Test]
        public void Frame_ShattersFirst_AndClosesTheApertureLater()
        {
            // ⚠ **枠は破砕より遅れて閉じる。** 同時に閉じると、飛んでいる途中の破片が枠の縁で
            //    ぷつりと切れる（覆いを開口で切っているため）。
            var l = AtStage(IntroStage.Frame);
            Advance(l, T.frameSec * (IntroLogic.FrameCloseAt - 0.1f), Ready(outsideM: 2f));
            var early = l.Weights;
            Assert.Greater(early.shatter, 0f, "割れ始めていない");
            Assert.AreEqual(0f, early.frame, 1e-4f, "破砕より先に枠が閉じている");

            Advance(l, T.frameSec * 0.25f, Ready(outsideM: 2f));
            Assert.Greater(l.Weights.frame, 0f, "終わりに向けて枠が閉じていない");
        }

        [Test]
        public void Frame_CrossfadesToVideo_JustBeforeTheApertureCloses()
        {
            // ⚠⚠ ユーザー指摘（2026-08-15・`canon/LEDGER.md` 0045 / 0046）
            //    「割れた先はパススルーのくりぬきではなくカメラ映像に」
            //    「細かくなって集まって、くりぬきが完全になる少し前にフェードで入れ替える感じ」。
            //    前半は現実のまま割れて集まり、閉じ切る少し前に入れ替わる。
            var l = AtStage(IntroStage.Frame);
            Assert.AreEqual(0f, l.Weights.live, 1e-4f, "段の頭から入れ替わっている");

            // 前半（割れて集まるところ）は**まだ現実**。
            Advance(l, T.frameSec * 0.4f, Ready(outsideM: 2f));
            Assert.Greater(l.Weights.shatter, 0f, "割れていない");
            Assert.AreEqual(0f, l.Weights.live, 1e-4f, "集まる前に入れ替わっている");
            Assert.AreEqual(0f, l.Weights.frame, 1e-4f, "破砕より先に枠が閉じている");

            // 枠が閉じ始めるあたりで入れ替えが進んでいる（＝ クロスフェードの最中）。
            Advance(l, T.frameSec * 0.3f, Ready(outsideM: 2f));
            float mid = l.Weights.live;
            Assert.Greater(mid, 0f, "閉じ始めても入れ替わっていない");
            Assert.Less(mid, 1f, "一瞬で入れ替わっている（フェードになっていない）");

            // 閉じ切る前に入れ替え終わっている。
            Advance(l, T.frameSec * 0.2f, Ready(outsideM: 2f));
            Assert.AreEqual(1f, l.Weights.live, 0.02f, "閉じ切るまでに入れ替わっていない");
            Assert.Less(l.Weights.frame, 1f, "もう閉じ切っている（「少し前」になっていない）");
            Assert.AreEqual(1f, l.Weights.passthrough, 1e-4f, "破片が現実を持って飛ばなくなっている");
        }

        [Test]
        public void Swap_KeepsTheVideoOn_AndHidesTheSeamWithGlitch()
        {
            var l = AtStage(IntroStage.Swap);

            Advance(l, IntroLogic.SwapCrossfadeSec * 0.5f, Ready(outsideM: 2f));
            var mid = l.Weights;
            // ⚠ 段 4 で既に映像が出ているので、ここで上げ直さない（出ていた映像が一度消える）。
            Assert.AreEqual(1f, mid.live, 1e-4f, "段 5 で映像を出し直している");
            Assert.Greater(mid.glitch, 0f, "継ぎ目は乱れで隠す");
            Assert.AreEqual(1f, mid.frame, 1e-4f, "枠は既に閉じている");
            Assert.AreEqual(1f, mid.ignite, 1e-4f, "映像が来る段で管が消えている");
            Assert.AreEqual(0f, mid.passthrough, 1e-4f, "段 5 で現実が 1 画素でも出ている");
            // ⚠ 殻を立てると、段 4 で出ていた映像が黒く塗り潰される。
            Assert.AreEqual(0f, mid.shell, 1e-4f, "段 5 で黒を被せている（映像が一度消える）");

            Advance(l, IntroLogic.SwapCrossfadeSec, Ready(outsideM: 2f));
            var after = l.Weights;
            Assert.AreEqual(1f, after.live, 0.01f);
            Assert.Less(after.glitch, 0.2f);
        }

        [Test]
        public void Shell_NeverRunsDuringTheIntro()
        {
            // 殻は全画面の面（queue 4910・ZTest Always）でスクリーンごと黒く塗る。
            // ⚠ **導入では 1 度も立てない**（2026-08-15）。枠の外の黒は覆いが持ち、
            //    枠の中は段 4 から映像。ここで黒を被せると、出ていた映像が一度消える。
            //    終幕（`OutroLogic`）は従来どおり使う。
            var l = AtReal();
            var outside = Ready(outsideM: 2f);
            for (int i = 0; i < 600 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.05f, outside);
                Assert.AreEqual(0f, l.Weights.shell, 1e-5f, $"段 {l.Stage} で殻が立っている");
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        // ---- (D) 封印の箱は全段で出ない -----------------------------------------

        [Test]
        public void SealedBox_NeverAppears_InAnyStage()
        {
            // ⚠⚠ 2026-08-15 に退避した（`canon/LEDGER.md` 0044）。実装は Attic に残してあるので、
            //    重みが 1 フレームでも立つと**黙って箱が戻る**。ここで止める。
            var l = Make();
            var outside = Ready(outsideM: 2f);
            l.Tick(0.05f, outside);
            Assert.AreEqual(0f, l.Weights.sealBox, 1e-5f, "段 0 で箱が出ている");

            l.RequestAdvance();
            l.Tick(0.05f, outside);
            for (int i = 0; i < 600 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.05f, outside);
                Assert.AreEqual(0f, l.Weights.sealBox, 1e-5f, $"段 {l.Stage} で箱が出ている");
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);
            Assert.AreEqual(0f, IntroWeights.Inactive.sealBox, 1e-5f);
        }

        // ---- (C) カメラが無くても本編へ入る -------------------------------------

        [Test]
        public void Swap_StillRunsWithStaticWhenNoCameraFrameArrives()
        {
            // ⚠⚠ **カメラが繋がっていなくても段 5 は流れる**（canon/LEDGER.md 0025）。
            //    旧実装はここで演出ごと畳んで本編へ落としていた ＝ 枠が閉じた次の瞬間に導入が終わり、
            //    体験者から見て「装置が枠になったのに何も起きずに始まった」になっていた。
            var l = AtStage(IntroStage.Frame);
            RunStage(l, IntroStage.Frame, Ready(live: false, outsideM: 2f));
            Assert.AreEqual(IntroStage.Swap, l.Stage, "映像が無いと段 5 が飛ぶ（砂嵐を見せる段が消える）");

            // 段 5 の見えは映像があるときと同じ。**画の中身は SignalLostFx が砂嵐で埋める**。
            var w = l.Weights;
            Assert.AreEqual(1f, w.live, 1e-4f, "砂嵐を出す段で映像の口が閉じている");
            Assert.AreEqual(1f, w.ignite, 1e-4f, "砂嵐を出す段で管が消えている");
            Assert.AreEqual(1f, w.frame, 1e-4f);
            Assert.AreEqual(0f, w.passthrough, 1e-4f);

            var ev = RunStage(l, IntroStage.Swap, Ready(live: false, outsideM: 2f));
            Assert.AreEqual(IntroEvent.Finished, ev, "砂嵐でも段 5 は最後まで流れて終わる");
        }

        [Test]
        public void Swap_WaitsBrieflyForTheCameraBeforeShowingStatic()
        {
            var l = AtStage(IntroStage.Frame);
            var dead = Ready(live: false, outsideM: 2f);
            Advance(l, T.frameSec + 1f, dead);
            Assert.AreEqual(IntroStage.Frame, l.Stage, "少しは待つ（映像が遅れて来ることがある）");

            Advance(l, 1f, Ready(outsideM: 2f));
            Assert.AreEqual(IntroStage.Swap, l.Stage, "来たら進む");
        }

        [Test]
        public void Swap_TheWaitForTheCameraIsBounded()
        {
            // 待ちは上限で必ず切れる。切れないと「カメラが死んだ日は導入が段 4 で固まる」。
            var l = AtStage(IntroStage.Frame);
            Advance(l, T.frameSec + IntroLogic.MaxHoldSec + 0.5f, Ready(live: false, outsideM: 2f));
            Assert.AreEqual(IntroStage.Swap, l.Stage,
                $"段 4 が上限 {IntroLogic.MaxHoldSec}s を過ぎても抜けていない");
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
            stuck.headTurnDegPerSec = 300f;      // ずっと頭を振っている
            var ev = IntroEvent.None;
            for (float x = 0f; x < 30f && ev == IntroEvent.None; x += 0.1f) ev = l.Tick(0.1f, stuck);
            Assert.AreEqual(IntroEvent.Finished, ev);
            Assert.LessOrEqual(l.TotalElapsedSec, 7f);
        }

        [Test]
        public void MaxSec_DoesNotCutTheSwapStageInHalf()
        {
            // 映像が点く最中に打ち切ると、いちばん見せたい一撃が途中で消える。
            var t = T; t.maxSec = 2f;
            var l = new IntroLogic();
            l.Configure(t);
            l.Begin();
            l.RequestAdvance(); l.Tick(0.1f, Ready(outsideM: 2f));
            for (int i = 0; i < 4; i++) { l.RequestAdvance(); l.Tick(0.1f, Ready(outsideM: 2f)); }
            Assert.AreEqual(IntroStage.Swap, l.Stage);

            Advance(l, 2.0f, Ready(outsideM: 2f));
            Assert.Greater(l.TotalElapsedSec, t.maxSec, "maxSec は既に超えている");
            Assert.Less(l.StageElapsedSec, t.swapSec, "Swap はまだ自然終了していない");
            Assert.AreEqual(IntroStage.Swap, l.Stage, "それでも Swap は畳まない");
        }

        // ---- 中止とやり直し ----------------------------------------------------

        [Test]
        public void Recenter_AbortsInsteadOfShowingAMisalignedRoom()
        {
            var l = AtReal();
            Advance(l, T.realSec * 0.5f, Ready(outsideM: 2f));

            var moved = Ready(outsideM: 2f);
            moved.recentered = true;
            Assert.AreEqual(IntroEvent.Aborted, l.Tick(0.1f, moved));
            Assert.AreEqual(IntroStage.Done, l.Stage);
        }

        [Test]
        public void Restart_GoesBackToStageOne_NotToBlack()
        {
            // HMD を被り直された。黒はもう明けているので段 0 へは戻らない。
            var l = AtStage(IntroStage.Degrade);

            l.Restart();
            Assert.AreEqual(IntroStage.Real, l.Stage);
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

        // ---- (E) 管の点灯は全期間 1 ---------------------------------------------

        [Test]
        public void Ignite_IsAlwaysOne()
        {
            // ⚠⚠ **0 を書いたままにすると画がまるごと消える。** 旧構成では段 3 で 0 → 1 と動いたが、
            //    2026-08-15 からは導入のあいだも点いたまま（まだ何も映していないだけ）。
            Assert.AreEqual(1f, IntroWeights.Inactive.ignite, 1e-4f, "既定が 1 でない");

            var l = AtReal();
            var outside = Ready(outsideM: 2f);
            for (int i = 0; i < 600 && l.Stage != IntroStage.Done; i++)
            {
                l.Tick(0.05f, outside);
                Assert.AreEqual(1f, l.Weights.ignite, 1e-4f, $"段 {l.Stage} で管が消えている");
            }
            Assert.AreEqual(IntroStage.Done, l.Stage);

            // 無効化・中止・飛ばしでも 1。
            var off = Make();
            off.Disable();
            Assert.AreEqual(1f, off.Weights.ignite, 1e-4f, "無効化したのに管が消えている");

            var aborted = AtReal();
            var moved = Ready(outsideM: 2f);
            moved.recentered = true;
            Assert.AreEqual(IntroEvent.Aborted, aborted.Tick(0.1f, moved));
            Assert.AreEqual(1f, aborted.Weights.ignite, 1e-4f, "中止したのに管が消えている");

            var skipped = AtReal();
            skipped.RequestSkip();
            skipped.Tick(0.1f, Ready(outsideM: 2f));
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
