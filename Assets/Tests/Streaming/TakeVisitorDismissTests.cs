#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <b>体験者が異変を報告すると、走行中の異常（演出）が消えて現実へ戻る。</b>
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0050 のユーザー逐語
    /// 「左半分に人形が大量にいて、それを異変だと思って**報告したらそれらが消え**」
    /// 「**推したら乱れたのちに元に戻って**終幕で」。
    /// 2026-08-17 に「4 周目 A だけの作り込み」から**仕組み**へ一般化した。
    ///
    /// ここで固定するのは判断（純ロジック）だけ。乱れで返すこと・資源を返すことは
    /// <see cref="TakeRunner"/> 側（<c>TakeSplitLayerTests</c> と同じ流儀）。
    /// </summary>
    public sealed class TakeVisitorDismissTests
    {
        private static TakeRunnerLogic.Def Take(int lap, int cam, bool dismissible,
                                                float offset = 0f, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = false, offsetSec = offset,
                skipWhenMissed = false, once = true, maxDurationSec = 0f,
                dismissible = dismissible,
                stepDurSec = steps.Length > 0 ? steps : new[] { 30f },
            };

        private static TakeRunnerLogic Make(params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            return l;
        }

        // 区間へ入って 1 カット目を走らせた状態を作る。
        private static TakeRunnerLogic Started(TakeRunnerLogic l, int lap, int cam, float now = 0f)
        {
            l.OnZoneCommitted(lap, cam, false, 0, 0, now);
            Assert.That(l.Tick(now, cam).action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "前提: 演出が走っていること");
            return l;
        }

        // ---- 消える ----

        [Test]
        public void Report_EndsTheRunningTake_AndReturnsToTheZoneTheVisitorIsIn()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: true)), 1, 1);

            l.NotifyMarkPressed(3f);
            TakeRunnerLogic.Decision d = l.Tick(3f, latestZoneCam: 1);

            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "報告で演出が終わる");
            Assert.That(d.reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
            Assert.That(d.dismissed, Is.True, "TakeRunner はこれを見て乱れで返す");
            Assert.That(d.forced, Is.False, "watchdog ではない（壊れて止まったのではない）");
            Assert.That(d.returnCamera, Is.EqualTo(1), "復帰先はいま体験者が居るゾーン（再計算）");
            Assert.That(l.IsActive, Is.False);
            Assert.That(l.DismissCount, Is.EqualTo(1));
        }

        /// <summary>
        /// <b>畳むのはカットではなく演出ごと。</b> カットだけ畳むと次のカットが出る ＝
        /// 「消したら別のものが現れた」になり、報告が驚かせる引き金に化ける
        /// （0050「報告ボタンは演出の引き金にはしない」）。
        /// </summary>
        [Test]
        public void Report_FoldsTheWholeTake_NotJustTheCurrentStep()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: true, 0f, 30f, 5f, 5f)), 1, 1);
            Assert.That(l.ActiveStepIndex, Is.EqualTo(0));

            l.NotifyMarkPressed(2f);
            Assert.That(l.Tick(2f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.EndTake),
                "残りの 2 カットは出ない");
            Assert.That(l.IsActive, Is.False);
        }

        /// <summary>
        /// <b>報告で現れてよいのは現実だけ。</b> 連続の渡し（chainNext）を立てると画面が異常のまま
        /// 次の演出へ移り、現実が 1 フレームも出ない ＝ 報告の因果が画から消える。
        /// </summary>
        [Test]
        public void Report_DoesNotHandTheScreenStraightToTheNextTake()
        {
            // 同じ区間に 2 本。A が走り、B は Ready のまま待っている。
            TakeRunnerLogic l = Started(Make(
                Take(1, 1, dismissible: true),
                Take(1, 1, dismissible: false)), 1, 1);

            l.NotifyMarkPressed(2f);
            TakeRunnerLogic.Decision d = l.Tick(2f, 1);
            Assert.That(d.chainNext, Is.False, "画面は必ず一度返す");
        }

        /// <summary>
        /// <b>消した次のフレームに別の異常が噴き出さない。</b> 出てしまうと「消えた」が画に
        /// 一度も出ず、体験者には報告が効かなかったようにしか見えない。
        /// </summary>
        [Test]
        public void Report_DropsTakesThatWereReadyToTakeTheScreen()
        {
            var dropped = new List<(int, TakeRunnerLogic.DropReason)>();
            TakeRunnerLogic l = Make(
                Take(1, 1, dismissible: true),
                Take(1, 1, dismissible: false));
            l.TakeDropped += (i, r) => dropped.Add((i, r));
            Started(l, 1, 1);

            l.NotifyMarkPressed(2f);
            l.Tick(2f, 1);

            Assert.That(dropped, Has.Count.EqualTo(1), "捨てたら必ず報告する（黙って消さない）");
            Assert.That(dropped[0].Item1, Is.EqualTo(1));
            Assert.That(dropped[0].Item2, Is.EqualTo(TakeRunnerLogic.DropReason.VisitorDismissed));

            for (float t = 2.1f; t < 8f; t += 0.5f)
                Assert.That(l.Tick(t, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"消した直後に別の異常が湧かない (t={t})");
        }

        /// <summary>
        /// <b>まだ時刻が来ていない演出は残す。</b> あれは後から別の異常として出るのが自然で、
        /// 消すと著作した内容が黙って減る（捨てるのは「いま画面が空いたら即座に出るもの」だけ）。
        /// </summary>
        [Test]
        public void Report_KeepsTakesThatAreNotDueYet()
        {
            TakeRunnerLogic l = Started(Make(
                Take(1, 1, dismissible: true),
                Take(1, 1, dismissible: false, offset: 10f)), 1, 1);

            l.NotifyMarkPressed(2f);
            Assert.That(l.Tick(2f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));

            Assert.That(l.Tick(9.9f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            TakeRunnerLogic.Decision later = l.Tick(10f, 1);
            Assert.That(later.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep),
                "10 秒後の演出は著作どおり出る");
            Assert.That(later.takeIndex, Is.EqualTo(1));
        }

        // ---- 消えない ----

        [Test]
        public void Report_DoesNothing_WhenTheTakeIsNotDismissible()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: false)), 1, 1);

            l.NotifyMarkPressed(3f);
            Assert.That(l.Tick(3f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.IsActive, Is.True, "旗を立てていない演出は 1 ビットも変わらない");
            Assert.That(l.DismissCount, Is.EqualTo(0));
        }

        /// <summary>
        /// <b>既定は「消えない」。</b> JsonUtility は欠落キーを false で埋めるので、
        /// 既存の show.json・端末キャッシュ・焼き込みは挙動が 1 ビットも変わらない。
        /// ここを逆に倒すと**3 周目の録画（作品の核）が押しボタン 1 つで飛ぶ**。
        /// </summary>
        [Test]
        public void Dismissible_DefaultsToFalse_SoOldShowJsonIsUnchanged()
        {
            var bare = new TakeRunnerLogic.Def
            {
                lap = 1, camera = 1, stepDurSec = new[] { 30f },
            };
            Assert.That(bare.dismissible, Is.False);

            TakeRunnerLogic l = Started(Make(bare), 1, 1);
            l.NotifyMarkPressed(3f);
            Assert.That(l.Tick(3f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.IsActive, Is.True);
        }

        /// <summary>
        /// <b>1 回の押下が 2 つの意味を持たない。</b> 現カットが <c>untilMark</c> なら報告は
        /// そのカットが消費し、演出は畳まれない。畳むと 4 周目 A の締めで
        /// 「現実へ戻る 3 秒」が消え、しかも <c>run.outro.afterTakeId</c> なので終幕が早撃ちされる。
        /// </summary>
        [Test]
        public void UntilMarkStep_ConsumesTheReport_AndTheTakeSurvives()
        {
            TakeRunnerLogic l = Started(Make(
                Take(1, 1, dismissible: true, 0f, TakeRunnerLogic.WaitMark, 3f)), 1, 1);

            l.NotifyMarkPressed(2f);
            TakeRunnerLogic.Decision d = l.Tick(2f, 1);

            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep), "次のカットへ進む");
            Assert.That(d.stepIndex, Is.EqualTo(1));
            Assert.That(l.IsActive, Is.True, "演出は続いている（畳まない）");
            Assert.That(l.DismissCount, Is.EqualTo(0), "これは「消えた」ではない");
        }

        /// <summary>
        /// <b>カットが始まる前の報告は数えない。</b> 直前の区間で押した 1 回が持ち越されて
        /// 次の演出を素通りさせるのを防ぐ（線待ち・untilMark と同じ理由）。
        /// </summary>
        [Test]
        public void Report_FromBeforeTheStepBegan_IsIgnored()
        {
            TakeRunnerLogic l = Make(Take(1, 1, dismissible: true));
            l.OnZoneCommitted(1, 1, false, 0, 0, 10f);
            l.NotifyMarkPressed(9f);            // 区間へ入る前に押していた
            l.Tick(10f, 1);                      // ここで 1 カット目が始まる

            Assert.That(l.Tick(10.1f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            Assert.That(l.IsActive, Is.True);
        }

        // ---- 境界 ----

        /// <summary>
        /// <b>消した演出は戻らない。</b> 戻ると報告が無意味になり、しかも「作者が操作している」と読まれる。
        /// </summary>
        [Test]
        public void DismissedTake_NeverFiresAgain_InTheSameRun()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: true)), 1, 1);
            l.NotifyMarkPressed(2f);
            l.Tick(2f, 1);

            // 別の区間を経て戻ってきても、once なので再武装しない。
            l.OnZoneCommitted(1, 2, true, 1, 1, 5f);
            l.OnZoneCommitted(1, 1, true, 1, 2, 9f);
            for (float t = 9f; t < 15f; t += 0.5f)
                Assert.That(l.Tick(t, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        /// <summary>押されなかった演出は、従来どおり watchdog が必ず畳む（置き去りにしない）。</summary>
        [Test]
        public void DismissibleTake_StillEndsByWatchdog_WhenNobodyReports()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: true, 0f, -2f)), 1, 1);

            TakeRunnerLogic.Decision d = l.Tick(TakeSchema.DefaultMaxDurationSec + 0.1f, 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(d.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Watchdog));
            Assert.That(d.dismissed, Is.False, "乱れでは返さない（壊れて止まったので素直に画面を返す）");
        }

        /// <summary>
        /// 同じフレームで報告と watchdog が揃ったら、<b>体験者の行為の方を理由にする</b>
        /// （画も「乱れて消えた」になり、押した手応えが返る）。
        /// </summary>
        [Test]
        public void Report_WinsOverWatchdog_WhenBothLandOnTheSameFrame()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: true, 0f, -2f)), 1, 1);

            float t = TakeSchema.DefaultMaxDurationSec + 0.1f;
            l.NotifyMarkPressed(t);
            Assert.That(l.Tick(t, 1).reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        /// <summary>
        /// <b>前の体験者が押した 1 回を次のランへ持ち越さない。</b> 持ち越すと 2 人目の演出が
        /// 始まった瞬間に消える（音で 2026-08-15 に踏んだのと同じ型）。
        /// </summary>
        [Test]
        public void ResetRun_ClearsAPendingReport_AndTheDismissCount()
        {
            TakeRunnerLogic l = Started(Make(Take(1, 1, dismissible: true)), 1, 1);
            l.NotifyMarkPressed(2f);
            l.Tick(2f, 1);
            Assert.That(l.DismissCount, Is.EqualTo(1));

            l.ResetRun();
            Assert.That(l.DismissCount, Is.EqualTo(0));

            Started(l, 1, 1, 20f);
            for (float t = 20f; t < 25f; t += 0.5f)
                Assert.That(l.Tick(t, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None),
                    $"2 人目は押していないので消えない (t={t})");
            Assert.That(l.IsActive, Is.True);
        }

        /// <summary>終わり方の理由が観測へ出る（<c>ev=take st=end why=</c>）。混ざると走行の判定ができない。</summary>
        [Test]
        public void EndReason_DistinguishesEveryWayATakeCanEnd()
        {
            // 著作どおり流し切った
            TakeRunnerLogic done = Started(Make(Take(1, 1, dismissible: true, 0f, 2f)), 1, 1);
            Assert.That(done.Tick(2f, 1).reason, Is.EqualTo(TakeRunnerLogic.EndReason.Completed));

            // 体験者が区間を移って打ち切られた（policy=yield）
            var yieldDef = Take(1, 1, dismissible: true);
            yieldDef.yieldOnZoneChange = true;
            TakeRunnerLogic yielded = Started(Make(yieldDef), 1, 1);
            Assert.That(yielded.OnZoneCommitted(1, 2, true, 1, 1, 3f).reason,
                Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
        }
    }
}
