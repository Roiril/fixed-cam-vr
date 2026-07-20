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

        // ---- タイムライン由来の cue 上書き（override 運搬） ----

        [Test]
        public void Evaluate_CarriesOverrideToDecision()
        {
            var e = Entry(2, 1, "cue_B_1");
            e.ov = new CueScheduleLogic.CueOverride
            {
                has = true, strength = 0.5f, fadeIn = 1.2f, fadeOut = 0.8f, trimStart = 0.1f, trimEnd = 2f,
            };
            var l = new CueScheduleLogic();
            l.SetEntries(new[] { e });
            var d = l.Evaluate(2, 1, false);
            Assert.That(d.fire, Is.True);
            Assert.That(d.ov.has, Is.True);
            Assert.That(d.ov.strength, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(d.ov.fadeIn, Is.EqualTo(1.2f).Within(1e-4f));
            Assert.That(d.ov.fadeOut, Is.EqualTo(0.8f).Within(1e-4f));
            Assert.That(d.ov.trimStart, Is.EqualTo(0.1f).Within(1e-4f));
            Assert.That(d.ov.trimEnd, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void Evaluate_NoOverride_HasIsFalse()
        {
            // legacy schedule 由来のエントリは override を持たない（has=false）。
            var l = Make(Entry(2, 1, "cue_B_1"));
            var d = l.Evaluate(2, 1, false);
            Assert.That(d.fire, Is.True);
            Assert.That(d.ov.has, Is.False);
        }

        // ---- 次に発火する予定の照会（StatusHud 表示用） ----

        private static readonly int[] Order3 = { 0, 1, 2 }; // A(0)→B(1)→C(2)

        [Test]
        public void TryGetNext_ReturnsNearestUpcomingEntry_ByStep()
        {
            // lap1 の B と lap2 の A。現在 lap1・pos0(A) からは lap1 の B が先。
            var l = Make(Entry(2, 0, "cue_A_lap2"), Entry(1, 1, "cue_B_lap1"));
            Assert.That(l.TryGetNext(currentLap: 1, currentPos: 0, order: Order3, out var next), Is.True);
            Assert.That(next.cueId, Is.EqualTo("cue_B_lap1"));
        }

        [Test]
        public void TryGetNext_IncludesEntryAtCurrentStep()
        {
            // 現在ステップ（lap1・pos1=B）に一致する未発火エントリは「次（＝今まさに）」として返る。
            var l = Make(Entry(1, 1, "cue_B_now"));
            Assert.That(l.TryGetNext(1, 1, Order3, out var next), Is.True);
            Assert.That(next.cueId, Is.EqualTo("cue_B_now"));
        }

        [Test]
        public void TryGetNext_SkipsFiredOnceEntries()
        {
            var l = Make(Entry(1, 1, "cue_B_once", once: true), Entry(2, 2, "cue_C_lap2"));
            var d = l.Evaluate(1, 1, false);
            l.MarkFired(d.entryIndex);
            // 発火済み once はスキップし、次の予定（lap2 C）を返す。
            Assert.That(l.TryGetNext(1, 1, Order3, out var next), Is.True);
            Assert.That(next.cueId, Is.EqualTo("cue_C_lap2"));
        }

        [Test]
        public void TryGetNext_SkipsPastEntries()
        {
            // lap1 の A（既に通過）は現在 lap2・pos0 より前 → 除外され、該当なしで false。
            var l = Make(Entry(1, 0, "cue_A_lap1"));
            Assert.That(l.TryGetNext(2, 0, Order3, out _), Is.False);
        }

        [Test]
        public void TryGetNext_CameraNotInOrder_Excluded()
        {
            // order に居ないカメラ(9)は配置不能 → false。
            var l = Make(Entry(3, 9, "cue_ghost"));
            Assert.That(l.TryGetNext(1, 0, Order3, out _), Is.False);
        }

        [Test]
        public void TryGetNext_EmptyOrder_False()
        {
            var l = Make(Entry(1, 0, "cue"));
            Assert.That(l.TryGetNext(1, 0, System.Array.Empty<int>(), out _), Is.False);
        }
    }
}
