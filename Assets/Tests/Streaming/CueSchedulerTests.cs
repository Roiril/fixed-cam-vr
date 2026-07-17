#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// CueScheduleLogic（スケジュール発火の純判定ロジック）の検証。
    /// (lap, camera) 一致・once の 1 回制限・activeCue 抑止・delaySec の受け渡しを固定する。
    /// 実際の遅延計時（Update）は MonoBehaviour 側で本テスト対象外。
    /// </summary>
    public sealed class CueSchedulerTests
    {
        private static CueScheduleLogic.Entry Entry(int lap, int camera, string cueId, float delay = 0f, bool once = true)
            => new() { lap = lap, camera = camera, cueId = cueId, delaySec = delay, once = once };

        private static CueScheduleLogic Make(params CueScheduleLogic.Entry[] entries)
        {
            var l = new CueScheduleLogic();
            l.SetEntries(entries);
            return l;
        }

        [Test]
        public void Evaluate_MatchingLapAndCamera_Fires()
        {
            var l = Make(Entry(2, 1, "cue_B_1"));
            var d = l.Evaluate(lap: 2, camera: 1, liveCueActive: false);
            Assert.That(d.fire, Is.True);
            Assert.That(d.cueId, Is.EqualTo("cue_B_1"));
            Assert.That(d.entryIndex, Is.EqualTo(0));
        }

        [Test]
        public void Evaluate_WrongLap_DoesNotFire()
        {
            var l = Make(Entry(2, 1, "cue_B_1"));
            Assert.That(l.Evaluate(1, 1, false).fire, Is.False);
        }

        [Test]
        public void Evaluate_WrongCamera_DoesNotFire()
        {
            var l = Make(Entry(2, 1, "cue_B_1"));
            Assert.That(l.Evaluate(2, 0, false).fire, Is.False);
        }

        [Test]
        public void Evaluate_LiveCueActive_SuppressesFire()
        {
            var l = Make(Entry(2, 1, "cue_B_1"));
            // ライブ手動オーバーライド中は一致していても発火しない。
            Assert.That(l.Evaluate(2, 1, liveCueActive: true).fire, Is.False);
            // 抑止解除後は通常どおり発火する（enter 時点で再評価される前提）。
            Assert.That(l.Evaluate(2, 1, liveCueActive: false).fire, Is.True);
        }

        [Test]
        public void Evaluate_Once_DoesNotRefireAfterMarkFired()
        {
            var l = Make(Entry(2, 1, "cue_B_1", once: true));
            var first = l.Evaluate(2, 1, false);
            Assert.That(first.fire, Is.True);
            l.MarkFired(first.entryIndex);
            // once = true → 2 回目の enter では発火しない。
            Assert.That(l.Evaluate(2, 1, false).fire, Is.False);
        }

        [Test]
        public void Evaluate_NonOnce_RefiresAfterMarkFired()
        {
            var l = Make(Entry(2, 1, "cue_B_1", once: false));
            var first = l.Evaluate(2, 1, false);
            Assert.That(first.fire, Is.True);
            l.MarkFired(first.entryIndex);
            // once = false → 再進入で再発火する。
            Assert.That(l.Evaluate(2, 1, false).fire, Is.True);
        }

        [Test]
        public void Evaluate_CarriesDelaySec()
        {
            var l = Make(Entry(3, 2, "cue_C_1", delay: 1.5f));
            var d = l.Evaluate(3, 2, false);
            Assert.That(d.fire, Is.True);
            Assert.That(d.delaySec, Is.EqualTo(1.5f).Within(1e-4f));
        }

        [Test]
        public void Evaluate_FirstMatchingEntryWins_SkipsFiredOnce()
        {
            // 同じ (lap,camera) に 2 エントリ。先頭 once を発火済みにすると次のエントリが返る。
            var l = Make(
                Entry(2, 1, "first", once: true),
                Entry(2, 1, "second", once: false));
            var a = l.Evaluate(2, 1, false);
            Assert.That(a.cueId, Is.EqualTo("first"));
            l.MarkFired(a.entryIndex);
            var b = l.Evaluate(2, 1, false);
            Assert.That(b.cueId, Is.EqualTo("second"));
        }

        [Test]
        public void SetEntries_ResetsFiredFlags()
        {
            var l = Make(Entry(2, 1, "cue_B_1", once: true));
            var first = l.Evaluate(2, 1, false);
            l.MarkFired(first.entryIndex);
            Assert.That(l.Evaluate(2, 1, false).fire, Is.False);

            // スケジュール差し替えで発火済みフラグはリセットされる。
            l.SetEntries(new[] { Entry(2, 1, "cue_B_1", once: true) });
            Assert.That(l.Evaluate(2, 1, false).fire, Is.True);
        }

        [Test]
        public void ResetFired_ClearsOnceFlags()
        {
            var l = Make(Entry(2, 1, "cue_B_1", once: true));
            var first = l.Evaluate(2, 1, false);
            l.MarkFired(first.entryIndex);
            Assert.That(l.Evaluate(2, 1, false).fire, Is.False);

            l.ResetFired();
            Assert.That(l.Evaluate(2, 1, false).fire, Is.True);
        }

        [Test]
        public void Evaluate_EmptySchedule_DoesNotFire()
        {
            var l = new CueScheduleLogic();
            Assert.That(l.Evaluate(1, 0, false).fire, Is.False);
        }
    }
}
