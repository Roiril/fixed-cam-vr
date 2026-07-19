#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// InsertLogic（インサートショットの純判定・計時ロジック）の検証。
    /// enter の delay 発火 / exit の即時発火 / duration 復帰 / 復帰先=最新ゾーン / once / 抑止 / 窓一致を固定する。
    /// dip 演出・cue・post の実適用は InsertController 側でテスト対象外。
    /// </summary>
    public sealed class InsertLogicTests
    {
        private static InsertLogic.Def Enter(int lap, int camera, int insertCam,
            float delay = 0f, float duration = 4f, string cueId = "", bool once = true)
            => new()
            {
                lap = lap, camera = camera, onExit = false, insertCamera = insertCam,
                delaySec = delay, durationSec = duration, cueId = cueId, once = once,
            };

        private static InsertLogic.Def Exit(int lap, int camera, int insertCam,
            float duration = 4f, string cueId = "", bool once = true)
            => new()
            {
                lap = lap, camera = camera, onExit = true, insertCamera = insertCam,
                delaySec = 0f, durationSec = duration, cueId = cueId, once = once,
            };

        private static InsertLogic Make(params InsertLogic.Def[] defs)
        {
            var l = new InsertLogic();
            l.SetDefs(defs);
            return l;
        }

        // ---- enter アンカー ----

        [Test]
        public void Enter_ArmsThenFiresAfterDelay()
        {
            var l = Make(Enter(2, 1, insertCam: 3, delay: 1f, duration: 4f, cueId: "cue_x"));
            // 区間 (2,1) 進入で武装（即時 Decision は None）。
            var arm = l.OnZoneCommitted(2, 1, hadPrev: false, 0, 0, now: 0f);
            Assert.That(arm.action, Is.EqualTo(InsertLogic.Action.None));
            Assert.That(l.IsActive, Is.True);
            // delay 未達では発火しない。
            Assert.That(l.Tick(0.9f, latestZoneCam: 1).action, Is.EqualTo(InsertLogic.Action.None));
            // delay 満了で BeginEnter。
            var d = l.Tick(1.0f, latestZoneCam: 1);
            Assert.That(d.action, Is.EqualTo(InsertLogic.Action.BeginEnter));
            Assert.That(d.camera, Is.EqualTo(3));
            Assert.That(d.cueId, Is.EqualTo("cue_x"));
        }

        [Test]
        public void Enter_DurationExpires_EndsToLatestZone()
        {
            var l = Make(Enter(2, 1, insertCam: 3, delay: 0f, duration: 4f));
            l.OnZoneCommitted(2, 1, hadPrev: false, 0, 0, now: 0f);
            Assert.That(l.Tick(0f, 1).action, Is.EqualTo(InsertLogic.Action.BeginEnter)); // delay 0 で即
            Assert.That(l.Tick(3.9f, 1).action, Is.EqualTo(InsertLogic.Action.None));
            // 表示中に体験者が別ゾーン(=5)へ移動 → 復帰先は最新ゾーン。
            var end = l.Tick(4.0f, latestZoneCam: 5);
            Assert.That(end.action, Is.EqualTo(InsertLogic.Action.End));
            Assert.That(end.camera, Is.EqualTo(5));
            Assert.That(l.IsActive, Is.False);
        }

        // ---- exit アンカー ----

        [Test]
        public void Exit_FiresImmediatelyOnLeavingSegment()
        {
            var l = Make(Exit(2, 1, insertCam: 3, duration: 4f, cueId: "scare"));
            // 区間 (2,1) から (2,0) へ離脱 → 即 BeginExit。
            var d = l.OnZoneCommitted(newLap: 2, newCam: 0, hadPrev: true, prevLap: 2, prevCam: 1, now: 0f);
            Assert.That(d.action, Is.EqualTo(InsertLogic.Action.BeginExit));
            Assert.That(d.camera, Is.EqualTo(3));
            Assert.That(d.cueId, Is.EqualTo("scare"));
            // 復帰先の既定 = 実際に入ったゾーン(newCam=0)。保留が無ければこれへ戻る。
            Assert.That(l.BaseZoneCamera, Is.EqualTo(0));
            var end = l.Tick(4.0f, latestZoneCam: 0);
            Assert.That(end.action, Is.EqualTo(InsertLogic.Action.End));
            Assert.That(end.camera, Is.EqualTo(0));
        }

        [Test]
        public void Exit_DoesNotFireWhenNotLeavingThatSegment()
        {
            var l = Make(Exit(2, 1, insertCam: 3));
            // 離脱元が (2,0) なので (2,1) の exit は発火しない。
            var d = l.OnZoneCommitted(2, 2, hadPrev: true, prevLap: 2, prevCam: 0, now: 0f);
            Assert.That(d.action, Is.EqualTo(InsertLogic.Action.None));
            Assert.That(l.IsActive, Is.False);
        }

        [Test]
        public void Exit_NoFireWithoutPrevSegment()
        {
            var l = Make(Exit(2, 1, insertCam: 3));
            // seed（hadPrev=false）では離脱が無い → exit 発火しない。
            var d = l.OnZoneCommitted(2, 1, hadPrev: false, 0, 0, now: 0f);
            Assert.That(d.action, Is.EqualTo(InsertLogic.Action.None));
        }

        // ---- once ----

        [Test]
        public void Once_DoesNotRefireUntilReset()
        {
            var l = Make(Enter(2, 1, insertCam: 3, delay: 0f, duration: 1f, once: true));
            l.OnZoneCommitted(2, 1, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 1).action, Is.EqualTo(InsertLogic.Action.BeginEnter));
            Assert.That(l.Tick(1f, 1).action, Is.EqualTo(InsertLogic.Action.End));
            // 再進入では発火しない（once）。
            l.OnZoneCommitted(2, 1, false, 0, 0, 2f);
            Assert.That(l.IsActive, Is.False);
            // リセット後は再発火する。
            l.ResetRun();
            l.OnZoneCommitted(2, 1, false, 0, 0, 3f);
            Assert.That(l.Tick(3f, 1).action, Is.EqualTo(InsertLogic.Action.BeginEnter));
        }

        // ---- 抑止 ----

        [Test]
        public void Suppressed_BlocksExitFire()
        {
            var l = Make(Exit(2, 1, insertCam: 3));
            l.SetSuppressed(true);
            var d = l.OnZoneCommitted(2, 0, hadPrev: true, prevLap: 2, prevCam: 1, now: 0f);
            Assert.That(d.action, Is.EqualTo(InsertLogic.Action.None));
        }

        [Test]
        public void Suppressed_BlocksEnterArm()
        {
            var l = Make(Enter(2, 1, insertCam: 3, delay: 1f));
            l.SetSuppressed(true);
            l.OnZoneCommitted(2, 1, false, 0, 0, 0f);
            Assert.That(l.IsActive, Is.False);
        }

        [Test]
        public void Suppressed_DuringDelay_CancelsPendingEnter()
        {
            var l = Make(Enter(2, 1, insertCam: 3, delay: 2f));
            l.OnZoneCommitted(2, 1, false, 0, 0, 0f);
            Assert.That(l.IsActive, Is.True);
            l.SetSuppressed(true);
            // delay 待ち中に抑止が入ったらキャンセル（表示に入らない）。
            Assert.That(l.Tick(1f, 1).action, Is.EqualTo(InsertLogic.Action.None));
            Assert.That(l.IsActive, Is.False);
        }

        // ---- 同時 1 本 / 未一致 ----

        [Test]
        public void OnlyOneInsertAtATime_ArmingBlockedWhileActive()
        {
            var l = Make(
                Enter(2, 1, insertCam: 3, delay: 0f, duration: 5f),
                Enter(2, 2, insertCam: 4, delay: 0f, duration: 5f));
            l.OnZoneCommitted(2, 1, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 1).action, Is.EqualTo(InsertLogic.Action.BeginEnter));
            // 表示中に別区間の進入が来ても新規武装しない（同時 1 本）。
            l.OnZoneCommitted(2, 2, hadPrev: true, prevLap: 2, prevCam: 1, now: 1f);
            Assert.That(l.ActiveDefIndex, Is.EqualTo(0)); // 依然 def0 が進行中
        }

        [Test]
        public void NonMatching_NeverFires()
        {
            var l = Make(Enter(2, 1, insertCam: 3), Exit(3, 0, insertCam: 4));
            var d = l.OnZoneCommitted(1, 0, hadPrev: true, prevLap: 1, prevCam: 2, now: 0f);
            Assert.That(d.action, Is.EqualTo(InsertLogic.Action.None));
            Assert.That(l.Tick(10f, 0).action, Is.EqualTo(InsertLogic.Action.None));
            Assert.That(l.IsActive, Is.False);
        }
    }
}
