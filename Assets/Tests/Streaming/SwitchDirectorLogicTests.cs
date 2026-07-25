#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// SwitchDirectorLogic（**画面層**の切替ガード）の検証。
    /// クールダウン・cue / インサート / override 凍結・手動優先(manualHold)・最新の既定映し先の適用・外部同期を固定する。
    ///
    /// 最小滞在(dwell)は段 B で人の層（<see cref="ZoneProgressionLogic"/>）へ分離したため**ここには無い**。
    /// dwell の計時は ZoneProgressionLogicTests が固定する。本ロジックが受け取る
    /// <c>SetAmbient</c> の目標は「dwell 済みの確定ゾーンのカメラ」である。
    /// </summary>
    public sealed class SwitchDirectorLogicTests
    {
        private const float Cooldown = 2f;
        private const float ManualHold = 8f;

        private static SwitchDirectorLogic Make(int current = 0,
            float cooldown = Cooldown, float manualHold = ManualHold)
        {
            var l = new SwitchDirectorLogic();
            l.Configure(cooldown, manualHold);
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

        // ---- 既定映し先（確定ゾーン）の適用 ----

        [Test]
        public void Ambient_CommitsOnNextTick_WhenGatesOpen()
        {
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetAmbient(1);
            Assert.That(l.Tick(0f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
            Assert.That(l.Current, Is.EqualTo(1));
        }

        [Test]
        public void Ambient_SameAsCurrent_ClearsPending()
        {
            var l = Make(current: 0);
            l.SetAmbient(1);          // 保持 1
            l.SetAmbient(0);          // 現在へ戻る → 保持解消
            Assert.That(l.HasPendingZone, Is.False);
            Assert.That(l.Tick(10f, out _), Is.False);
        }

        [Test]
        public void Ambient_InvalidTarget_DoesNothing()
        {
            var l = Make(current: 0, cooldown: 0f, manualHold: 0f);
            l.SetAmbient(-1);
            Assert.That(l.HasPendingZone, Is.False);
            Assert.That(l.Tick(10f, out _), Is.False);
            Assert.That(l.Current, Is.EqualTo(0));
        }

        [Test]
        public void Ambient_CueActive_FreezesUntilCueEnds_ThenAppliesLatest()
        {
            var l = Make();
            l.SetAmbient(1);
            l.SetCueActive(true);
            Assert.That(l.Tick(5f, out _), Is.False); // cue 中は凍結（保持は保つ）
            Assert.That(l.HasPendingZone, Is.True);
            l.SetCueActive(false);
            Assert.That(l.Tick(5f, out int commit), Is.True); // 終了後に最新を適用
            Assert.That(commit, Is.EqualTo(1));
        }

        [Test]
        public void Ambient_FrozenThenNewerTarget_AppliesNewestOnly()
        {
            // 凍結中に体験者が更に移動した（時計は進み続けて SetAmbient が更新される）→ 解除後は最新だけを映す。
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetCueActive(true);
            l.SetAmbient(1);
            l.SetAmbient(2);
            Assert.That(l.Tick(5f, out _), Is.False);
            l.SetCueActive(false);
            Assert.That(l.Tick(5f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(2), "途中の 1 は飛ばして最新の 2 を映す（復元でなく再計算）");
        }

        [Test]
        public void Ambient_CooldownBlocks_AndCurrentUnchanged()
        {
            var l = Make(manualHold: 0f); // cooldown=2 を単離
            l.NotifyExternalSwitch(0, 1.0f);            // lastSwitch=1.0, current=0
            l.SetAmbient(1);
            Assert.That(l.Tick(1.5f, out _), Is.False); // 0.5 < 2 cooldown
            Assert.That(l.Current, Is.EqualTo(0));      // 表示カメラ不変
            Assert.That(l.Tick(3.0f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
        }

        // ---- 手動優先（manualHold） ----

        [Test]
        public void Manual_SuppressesAutoForManualHold_ThenAutoResumes()
        {
            var l = Make();
            Assert.That(l.RequestManual(1, 0f, out _), Is.True); // lastManual=0, current=1
            l.SetAmbient(2);
            // cooldown(2) は満たすが manualHold(8) 未達で抑止。
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
            l.SetAmbient(2);
            l.NotifyExternalSwitch(2, 0.2f); // 外部が保持先へ切替 → 保持解消
            Assert.That(l.HasPendingZone, Is.False);
        }

        // ---- インサート凍結（cue 凍結と同型・独立フラグ） ----

        [Test]
        public void Ambient_InsertActive_FreezesUntilCleared_ThenAppliesLatest()
        {
            var l = Make();
            l.SetAmbient(1);
            l.SetInsertActive(true);
            Assert.That(l.Tick(5f, out _), Is.False);   // インサート中は凍結（保持は保つ）
            Assert.That(l.HasPendingZone, Is.True);
            l.SetInsertActive(false);
            Assert.That(l.Tick(5f, out int commit), Is.True); // 解除後に最新を適用
            Assert.That(commit, Is.EqualTo(1));
        }

        [Test]
        public void Ambient_InsertActive_FreezesEvenWithoutCue()
        {
            var l = Make();
            l.SetCueActive(false);       // cue は無い
            l.SetInsertActive(true);     // インサート単独で凍結する
            l.SetAmbient(1);
            Assert.That(l.Tick(10f, out _), Is.False);
        }

        [Test]
        public void Ambient_InsertAndCue_BothClearedNeededToResume()
        {
            var l = Make();
            l.SetAmbient(1);
            l.SetCueActive(true);
            l.SetInsertActive(true);
            l.SetCueActive(false);       // 片方だけ解除では凍結継続
            Assert.That(l.Tick(5f, out _), Is.False);
            l.SetInsertActive(false);    // 両方解除で再開
            Assert.That(l.Tick(5f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
        }

        // ---- override 第一級凍結（Web cameraOverride） ----

        [Test]
        public void Override_Enter_ClearsStalePending()
        {
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetAmbient(1);                           // override 前に保持を積む
            Assert.That(l.HasPendingZone, Is.True);
            l.SetOverrideActive(true);
            Assert.That(l.HasPendingZone, Is.False, "override enter で stale 保持を無条件クリアする");
            Assert.That(l.OverrideActive, Is.True);
        }

        [Test]
        public void Override_FreezesTick_EvenWhenTimingGatesOpen()
        {
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetOverrideActive(true);
            l.SetAmbient(1);                           // override 中に保持が積まれても
            Assert.That(l.Tick(10f, out _), Is.False, "cooldown 0 でも override 中は commit しない");
            Assert.That(l.Current, Is.EqualTo(0));
        }

        [Test]
        public void Override_Exit_LeavesCleanSlate_NewRequestCommits()
        {
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetAmbient(2);                           // override 前の stale 保持
            l.SetOverrideActive(true);                 // クリア
            l.SetOverrideActive(false);                // 解除（保持は残らない）
            Assert.That(l.HasPendingZone, Is.False, "解除後も stale 保持は蘇らない");
            Assert.That(l.OverrideActive, Is.False);
            l.SetAmbient(1);                           // 新規要求は通常経路で commit
            Assert.That(l.Tick(1f, out int commit), Is.True);
            Assert.That(commit, Is.EqualTo(1));
        }

        [Test]
        public void Override_FreezesIndependentlyOfCueAndInsert()
        {
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetCueActive(false);
            l.SetInsertActive(false);
            l.SetOverrideActive(true);                 // override 単独で凍結する
            l.SetAmbient(1);
            Assert.That(l.Tick(10f, out _), Is.False);
        }

        [Test]
        public void Reset_ClearsOverrideActive()
        {
            var l = Make(cooldown: 0f, manualHold: 0f);
            l.SetOverrideActive(true);
            l.Reset(0);                                // OnEnable 経路で override 凍結が残らない
            Assert.That(l.OverrideActive, Is.False);
            l.SetAmbient(1);
            Assert.That(l.Tick(1f, out int commit), Is.True, "Reset 後は凍結が解けて commit する");
            Assert.That(commit, Is.EqualTo(1));
        }

        // ---- show.json control の present 判定（Director の ApplyTimingOverride が使う純関数） ----

        [Test]
        public void ResolveTiming_PresentJudgement()
        {
            Assert.That(SwitchDirectorLogic.ResolveTiming(0f, 0.5f), Is.EqualTo(0.5f));   // 未指定 → 既定
            Assert.That(SwitchDirectorLogic.ResolveTiming(-1f, 0.5f), Is.EqualTo(0.5f));  // 負値 → 既定
            Assert.That(SwitchDirectorLogic.ResolveTiming(1.5f, 0.5f), Is.EqualTo(1.5f)); // >0 → 採用
        }

        [Test]
        public void DefaultCooldown_IsHalfSecond()
        {
            Assert.That(SwitchDirectorLogic.DefaultCooldownSec, Is.EqualTo(0.5f));
        }
    }
}
