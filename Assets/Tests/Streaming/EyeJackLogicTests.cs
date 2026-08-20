#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 目の視界ジャック（<c>canon/LEDGER.md</c> 0099）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 5 つ。どれも実機では「出なかった / 消えない」にしか見えない:
    /// ① 歩き続ける体験者（多数派）に一度も出ない — 全開だけを発火条件にすると、実測の滞在では
    ///    半分（約 3.5 秒）が全開（4.92 秒）より先に来る
    /// ② 写真が 0 枚の日に何かが出る（空の板・黒い板）
    /// ③ カットが終わったのに視界を返さない（凍結の型 — この codebase が 4 回踏んだ）
    /// ④ カット 1 → 2 の縁で二度目が始まる
    /// ⑤ 写真が尽きたのに目が閉じない（「元に戻して目も消えて終わる」の後半が欠ける）
    /// </summary>
    public sealed class EyeJackLogicTests
    {
        private const float Dt = 1f / 30f;

        private static EyeJackLogic FiredAtHold(int photos)
        {
            var j = new EyeJackLogic();
            j.Tick(Dt, armed: true, EyesStage.Hold, halfReached: false, photos);
            for (float t = 0f; t < EyeJackLogic.HoldBeatSec + Dt; t += Dt)
                j.Tick(Dt, true, EyesStage.Hold, false, photos);
            Assert.That(j.Active, Is.True, "前提: 全開 + 1 拍で発火している");
            return j;
        }

        // ---------------------------------------------------------------- 発火の 2 つの縁

        [Test]
        public void Hold_FiresAfterBeat_NotInstantly()
        {
            var j = new EyeJackLogic();
            j.Tick(Dt, true, EyesStage.Hold, false, photoCount: 4);
            Assert.That(j.Active, Is.False, "全開の瞬間には乗っ取らない（見られる 1 拍を置く）");

            for (float t = 0f; t < EyeJackLogic.HoldBeatSec + Dt; t += Dt)
                j.Tick(Dt, true, EyesStage.Hold, false, 4);
            Assert.That(j.Active, Is.True);
            Assert.That(j.PhotoIndex, Is.EqualTo(0), "1 枚目から");
        }

        /// <summary>① 歩き続ける体験者は半分の縁で発火する（全開を待たない）。</summary>
        [Test]
        public void HalfReached_FiresImmediately_EvenWhileStillOpening()
        {
            var j = new EyeJackLogic();
            j.Tick(Dt, true, EyesStage.Swarm, halfReached: true, photoCount: 4);
            Assert.That(j.Active, Is.True, "実測の滞在では全開より半分が先に来る（設計批評 2026-08-21）");
            Assert.That(j.JustStarted, Is.True);
        }

        [Test]
        public void WhileEyesClosing_DoesNotFire()
        {
            var j = new EyeJackLogic();
            j.Tick(Dt, true, EyesStage.Fading, halfReached: true, photoCount: 4);
            Assert.That(j.Active, Is.False, "閉じ始めた後に乗っ取ると「元に戻して目も消えて」の順が壊れる");
        }

        // ---------------------------------------------------------------- ② 写真が 0 枚

        [Test]
        public void ZeroPhotos_NeverFires()
        {
            var j = new EyeJackLogic();
            for (float t = 0f; t < 10f; t += Dt)
                j.Tick(Dt, true, EyesStage.Hold, halfReached: true, photoCount: 0);
            Assert.That(j.Active, Is.False, "当日フォルダが空でも体験は壊れない（目は従来どおり）");
        }

        // ---------------------------------------------------------------- 尺の決まり方

        [Test]
        public void FewPhotos_ClampToPerMax()
        {
            var j = new EyeJackLogic();
            j.Tick(Dt, true, EyesStage.Swarm, true, photoCount: 3);
            Assert.That(j.ShowCount, Is.EqualTo(3));
            Assert.That(j.PerSec, Is.EqualTo(EyeJackLogic.PerMaxSec), "3 枚なら 1 枚 0.5 秒（居座らせない）");
        }

        [Test]
        public void ManyPhotos_DropTail_TotalStaysComfortable()
        {
            var j = new EyeJackLogic();
            j.Tick(Dt, true, EyesStage.Swarm, true, photoCount: 20);
            Assert.That(j.ShowCount, Is.EqualTo(12), "総尺 2.4 秒 ÷ 下限 0.2 秒 = 12 枚まで");
            Assert.That(j.PerSec, Is.EqualTo(EyeJackLogic.PerMinSec));
        }

        [Test]
        public void PhotoIndex_AdvancesInOrder_NoSkipBackwards()
        {
            var j = FiredAtHold(photos: 6);
            int last = -1;
            while (j.Active)
            {
                Assert.That(j.PhotoIndex, Is.GreaterThanOrEqualTo(last), "戻らない");
                Assert.That(j.PhotoIndex, Is.LessThan(j.ShowCount));
                last = j.PhotoIndex;
                j.Tick(Dt, true, EyesStage.Hold, false, 6);
            }
            Assert.That(last, Is.EqualTo(5), "全部の写真が出た");
        }

        // ---------------------------------------------------------------- ⑤ 写真が尽きた側の分岐

        [Test]
        public void PhotosDone_ReturnsVision_AndAsksEyesToClose()
        {
            var j = FiredAtHold(photos: 4);
            bool asked = false;
            string why = "";
            for (float t = 0f; t < EyeJackLogic.TotalSec + 1f && j.Active; t += Dt)
            {
                j.Tick(Dt, true, EyesStage.Hold, false, 4);
                asked |= j.FinishEyesRequested;
                if (j.EndedWhy != "") why = j.EndedWhy;
            }
            Assert.That(j.Active, Is.False, "写真が尽きたら視界を返す");
            Assert.That(asked, Is.True, "目も閉じさせる（0099「元に戻して目も消えて終わる」）");
            Assert.That(why, Is.EqualTo("done"));
        }

        [Test]
        public void AfterDone_DoesNotRefire_WhileSameCutKeepsAsking()
        {
            var j = FiredAtHold(photos: 4);
            while (j.Active) j.Tick(Dt, true, EyesStage.Hold, false, 4);

            // ④ カット 2 も eyeJack:true を言い続ける。二度目を始めない。
            for (float t = 0f; t < 5f; t += Dt)
                j.Tick(Dt, true, EyesStage.Hold, halfReached: true, 4);
            Assert.That(j.Active, Is.False, "同じ出番で二度乗っ取らない");
        }

        // ---------------------------------------------------------------- ③ 必ず返す

        [Test]
        public void CutEnd_ReturnsVisionImmediately_WithoutClosingEyesItself()
        {
            var j = FiredAtHold(photos: 8);
            j.Tick(Dt, armed: false, EyesStage.Hold, false, 8);
            Assert.That(j.Active, Is.False, "「今設定してるところで止める」— 区間の畳みで即座に返す");
            Assert.That(j.EndedWhy, Is.EqualTo("cut"));
            Assert.That(j.FinishEyesRequested, Is.False, "目の畳み方は従来の流しきりに任せる");
        }

        [Test]
        public void ActiveNeverOutlivesMaxActiveSec()
        {
            var j = FiredAtHold(photos: 4);
            for (float t = 0f; t < EyeJackLogic.MaxActiveSec + 1f && j.Active; t += Dt)
                j.Tick(Dt, true, EyesStage.Hold, false, 4);
            Assert.That(j.Active, Is.False, "安全網。乗っ取りは必ず終わる（通常は done、壊れても wd）");
        }

        /// <summary>
        /// ⚠⚠ <b>覆いの裏で目を閉じさせない</b>（<c>canon/LEDGER.md</c> 0099 / 0084）。
        /// 歩き続ける体験者は「区間の半分」で終了演出に入るのと<b>同じ縁</b>でジャックが出るので、
        /// 素直に閉じさせると 1.6 秒の閉じが 1 フレームも見えないまま終わる
        /// （0084 の赤入れ「閉じるときは、開くときと同じように緩急つけて」が消える）。
        ///
        /// 実行体（<c>AnomalyEyes.LateUpdate</c>）は <c>_cue.Wanted || _jack.Active</c> を渡す。
        /// ここではその式が「ジャック中は必ず開いたまま・返した瞬間に閉じへ渡る」ことを固定する。
        /// </summary>
        [Test]
        public void WhileJackCovers_EyesAreHeldOpen_AndCloseOnlyAfterItReturns()
        {
            var eyes = new AnomalyEyesLogic();
            var cue = new EyesCueLogic();
            var jack = new EyeJackLogic();
            float progress = 0f;
            bool closedWhileCovered = false;
            bool closedAfter = false;

            for (float t = 0f; t < 12f; t += Dt)
            {
                progress = System.Math.Min(1f, progress + Dt * (0.5f / 3.5f));  // 歩き続ける
                var span = new ZoneSpan(true, 2, 7, progress);
                cue.Tick(armed: true, eyes.Stage, span);
                // 実行体と同じ式。
                eyes.Tick(Dt, cue.Wanted || jack.Active, 1f, cue.Rate);
                jack.Tick(Dt, armed: true, eyes.Stage, cue.HalfReached, 5);
                if (jack.FinishEyesRequested) cue.RequestFinish();

                if (jack.Active && (eyes.Stage == EyesStage.Fading || eyes.Stage == EyesStage.Off))
                    closedWhileCovered = true;
                if (!jack.Active && jack.Spent && eyes.Stage == EyesStage.Fading)
                    closedAfter = true;
            }

            Assert.That(closedWhileCovered, Is.False, "覆いの裏で閉じていない");
            Assert.That(closedAfter, Is.True, "覆いが上がってから閉じている");
            Assert.That(eyes.Stage, Is.EqualTo(EyesStage.Off), "最後は閉じ切る（掛けっぱなしにしない）");
        }

        [Test]
        public void Reset_KillsInOneFrame()
        {
            var j = FiredAtHold(photos: 4);
            j.Reset();
            Assert.That(j.Active, Is.False);
            Assert.That(j.Spent, Is.False);
        }

        [Test]
        public void AfterCutEnd_NextRunFiresAgain()
        {
            // 引き返して頭から再演される場合（rules/streaming.md「引き返したら…頭から出し直す」）。
            var j = FiredAtHold(photos: 4);
            j.Tick(Dt, false, EyesStage.Fading, false, 4);
            j.Tick(Dt, true, EyesStage.Swarm, halfReached: true, 4);
            Assert.That(j.Active, Is.True, "新しい出番では再び出る");
        }
    }
}
