#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// SwitchDirectorLogic（カメラ切替の時間軸ガード）の検証。
    /// クールダウン・最小滞在(dwell)・cue 中凍結・手動優先(manualHold)・保留の最新適用・外部同期を固定する。
    /// </summary>
    public sealed class SwitchDirectorLogicTests
    {
        private const float Cooldown = 2f;
        private const float Dwell = 2f;
        private const float ManualHold = 8f;

        private static SwitchDirectorLogic Make(int current = 0,
            float cooldown = Cooldown, float dwell = Dwell, float manualHold = ManualHold)
        {
            var l = new SwitchDirectorLogic();
            l.Configure(cooldown, dwell, manualHold);
            l.Reset(current);
            return l;
        }

        // ---- 手動 ----

        [Test]
        public void Manual_FromFresh_CommitsImmediately()
        {
            var l = Make();
            Assert.That(l.RequestManual(1, 0f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
            Assert.That(l.Current, Is.EqualTo(1));
        }

        [Test]
        public void Manual_SameTarget_DoesNotCommit()
        {
            var l = Make(current: 0);
            Assert.That(l.RequestManual(0, 0f, out _), Is.False);
        }

        [Test]
        public void Manual_WithinCooldown_Rejected()
        {
            var l = Make();
            Assert.That(l.RequestManual(1, 0f, out _), Is.True);
            Assert.That(l.RequestManual(2, 1.0f, out _), Is.False); // 1s < 2s cooldown
        }

        [Test]
        public void Manual_AfterCooldown_Commits()
        {
            var l = Make();
            Assert.That(l.RequestManual(1, 0f, out _), Is.True);
            Assert.That(l.RequestManual(2, 2.0f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(2));
        }

        [Test]
        public void Manual_DuringCue_StillCommits()
        {
            // 手動は cue を無視する（クールダウンのみ尊重）。
            var l = Make();
            l.SetCueActive(true);
            Assert.That(l.RequestManual(1, 0f, out _), Is.True);
        }

        // ---- ゾーン自動 ----

        [Test]
        public void Zone_Pending_AppliesOnlyAfterDwell()
        {
            var l = Make();
            l.RequestZone(1, 0f);
            Assert.That(l.Tick(1.9f, out _), Is.False); // dwell 未達
            Assert.That(l.Tick(2.0f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
        }

        [Test]
        public void Zone_SameAsCurrent_ClearsPending()
        {
            var l = Make(current: 0);
            l.RequestZone(1, 0f);         // 保留 1
            l.RequestZone(0, 0.5f);       // 現在へ戻る = 一瞬の通過 → 保留解消
            Assert.That(l.HasPendingZone, Is.False);
            Assert.That(l.Tick(10f, out _), Is.False);
        }

        [Test]
        public void Zone_CueActive_FreezesUntilCueEnds_ThenAppliesLatest()
        {
            var l = Make();
            l.RequestZone(1, 0f);
            l.SetCueActive(true);
            Assert.That(l.Tick(5f, out _), Is.False); // cue 中は凍結（保留は保つ）
            Assert.That(l.HasPendingZone, Is.True);
            l.SetCueActive(false);
            Assert.That(l.Tick(5f, out int commit), Is.True); // 終了後に適用（dwell は 0 起点で満了済み）
            Assert.That(commit, Is.EqualTo(1));
        }

        [Test]
        public void Zone_LatestTargetWins_DwellFromLatest()
        {
            var l = Make();
            l.RequestZone(1, 0f);
            l.RequestZone(2, 0.5f);                 // 目標変更 → dwell は 0.5 起点
            Assert.That(l.Tick(2.4f, out _), Is.False); // 2.4-0.5=1.9 < 2
            Assert.That(l.Tick(2.5f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(2));
        }

        [Test]
        public void Zone_CooldownBlocks_EvenWhenDwellMet()
        {
            var l = Make(dwell: 0f, manualHold: 0f); // cooldown を単離
            l.NotifyExternalSwitch(0, 1.0f);          // lastSwitch=1.0
            l.RequestZone(1, 1.0f);
            Assert.That(l.Tick(1.5f, out _), Is.False); // 0.5 < 2 cooldown
            Assert.That(l.Tick(3.0f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
        }

        // ---- 手動優先（manualHold） ----

        [Test]
        public void Manual_SuppressesAutoForManualHold_ThenAutoResumes()
        {
            var l = Make();
            Assert.That(l.RequestManual(1, 0f, out _), Is.True); // lastManual=0, current=1
            l.RequestZone(2, 0.1f);
            // cooldown(2)・dwell(2) は満たすが manualHold(8) 未達で抑止。
            Assert.That(l.Tick(2.5f, out _), Is.False);
            // manualHold 経過後に適用。
            Assert.That(l.Tick(8.1f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(2));
        }

        // ---- 外部同期（Web override 等） ----

        [Test]
        public void NotifyExternalSwitch_ResyncsCurrentAndResetsCooldown()
        {
            var l = Make(current: 0);
            l.NotifyExternalSwitch(3, 5.0f);
            Assert.That(l.Current, Is.EqualTo(3));
            Assert.That(l.RequestManual(3, 5.1f, out _), Is.False);  // 同一 = 無切替
            Assert.That(l.RequestManual(4, 5.5f, out _), Is.False);  // 0.5 < 2 cooldown（外部切替でリセット）
            Assert.That(l.RequestManual(4, 7.1f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(4));
        }

        [Test]
        public void NotifyExternalSwitch_ClearsPendingForSameIndex()
        {
            var l = Make(current: 0);
            l.RequestZone(2, 0f);
            l.NotifyExternalSwitch(2, 0.2f); // 外部が保留先へ切替 → 保留解消
            Assert.That(l.HasPendingZone, Is.False);
        }

        // ---- インサート凍結（cue 凍結と同型・独立フラグ） ----

        [Test]
        public void Zone_InsertActive_FreezesUntilCleared_ThenAppliesLatest()
        {
            var l = Make();
            l.RequestZone(1, 0f);
            l.SetInsertActive(true);
            Assert.That(l.Tick(5f, out _), Is.False);   // インサート中は凍結（保留は保つ）
            Assert.That(l.HasPendingZone, Is.True);
            l.SetInsertActive(false);
            Assert.That(l.Tick(5f, out int commit), Is.True); // 解除後に最新を適用
            Assert.That(commit, Is.EqualTo(1));
        }

        [Test]
        public void Zone_InsertActive_FreezesEvenWithoutCue()
        {
            var l = Make();
            l.SetCueActive(false);       // cue は無い
            l.SetInsertActive(true);     // インサート単独で凍結する
            l.RequestZone(1, 0f);
            Assert.That(l.Tick(10f, out _), Is.False);
        }

        [Test]
        public void Zone_InsertAndCue_BothClearedNeededToResume()
        {
            var l = Make();
            l.RequestZone(1, 0f);
            l.SetCueActive(true);
            l.SetInsertActive(true);
            l.SetCueActive(false);       // 片方だけ解除では凍結継続
            Assert.That(l.Tick(5f, out _), Is.False);
            l.SetInsertActive(false);    // 両方解除で再開
            Assert.That(l.Tick(5f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
        }
    }
}
