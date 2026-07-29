#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 演出の「連続」の検証 — 待ちの持ち越し（wait="chain"）と、繋ぎ目で画面を返さないこと（chainNext）。
    ///
    /// 直したかった実害:
    ///   前の区間の離脱時演出が 8 秒あり、次の区間の滞在が 5 秒しかないと、
    ///   次の区間の演出は Ready のまま待たされたあげく区間を出て捨てられる。
    ///   「待つのは正しい（ユーザーの要望）が、待った結果出ないまま消えるのが事故」。
    ///
    /// 持ち越しは**因果がはっきりしている場合だけ**にする:
    ///   離脱の瞬間に実際に画面が塞がっていて、かつ開始条件は既に満たしていた演出に限る。
    ///   「歩くのが速くて時刻に届かなかった」ものは持ち越さない（それは ifMissed の担当）。
    /// </summary>
    public sealed class TakeChainWaitTests
    {
        private static TakeRunnerLogic.Def Enter(int lap, int cam, float offset, bool chain, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = false, offsetSec = offset,
                skipWhenMissed = false, once = true, maxDurationSec = 0f,
                chainWait = chain, stepDurSec = steps,
            };

        private static TakeRunnerLogic.Def Exit(int lap, int cam, params float[] steps)
            => new()
            {
                lap = lap, camera = cam, onExit = true, offsetSec = 0f,
                skipWhenMissed = false, once = true, maxDurationSec = 0f, stepDurSec = steps,
            };

        private static TakeRunnerLogic Make(List<(int, TakeRunnerLogic.DropReason)> drops,
                                            params TakeRunnerLogic.Def[] defs)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(defs);
            l.TakeDropped = (i, r) => drops.Add((i, r));
            return l;
        }

        // 「B の離脱時演出が走っているあいだに C へ入り、C の滞在が足りずに D へ移る」状況を作る。
        //   defs: 0 = B の離脱時演出（8 秒）/ 1 = C の進入演出（3 秒）
        private static TakeRunnerLogic Scenario(List<(int, TakeRunnerLogic.DropReason)> drops, bool chain)
            => Make(drops, Exit(1, 1, 8f), Enter(1, 2, 0f, chain, 3f));

        [Test]
        public void Chain_CarriesAcrossSegment_AndFiresWhenScreenFrees()
        {
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Scenario(drops, chain: true);

            l.OnZoneCommitted(1, 1, false, 0, 0, 0f);                 // B へ進入
            var d = l.OnZoneCommitted(1, 2, true, 1, 1, 5f);          // B → C: 離脱時演出が発火
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.takeIndex, Is.EqualTo(0));

            l.Tick(5.02f, 2);                                        // C の演出は Ready で待つ
            Assert.That(l.IsActive, Is.True);

            // 8 秒の演出が終わる前に C を出る（滞在 5 秒）。
            l.OnZoneCommitted(1, 0, true, 1, 2, 10f);
            Assert.That(l.CarryCount, Is.EqualTo(1), "画面が塞がっていたせいで出られなかったので持ち越す");
            Assert.That(drops, Is.Empty, "持ち越したものは捨てていないので報告しない");

            // 離脱時演出が終わる（開始 5s + 尺 8s）。
            var end = l.Tick(13.1f, 0);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.chainNext, Is.True, "次に出せるものが控えている＝画面を返さない");

            var fired = l.Tick(13.12f, 0);
            Assert.That(fired.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(fired.takeIndex, Is.EqualTo(1), "持ち越した C の演出が出る");
            Assert.That(l.CarryCount, Is.EqualTo(0));
        }

        [Test]
        public void Segment_IsUnchanged_StillDroppedAtExit()
        {
            // 既定（wait="segment"）は 1 ビットも変えない — これが「既存データの挙動不変」の証拠。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Scenario(drops, chain: false);

            l.OnZoneCommitted(1, 1, false, 0, 0, 0f);
            l.OnZoneCommitted(1, 2, true, 1, 1, 5f);
            l.Tick(5.02f, 2);
            l.OnZoneCommitted(1, 0, true, 1, 2, 10f);

            Assert.That(l.CarryCount, Is.EqualTo(0));
            Assert.That(drops, Has.Count.EqualTo(1));
            Assert.That(drops[0].Item1, Is.EqualTo(1));
            Assert.That(drops[0].Item2, Is.EqualTo(TakeRunnerLogic.DropReason.ScreenBusyAtExit));
        }

        [Test]
        public void Chain_DoesNotCarry_WhenScreenWasFree()
        {
            // 画面が空いていたのに出なかった＝「歩くのが速くて時刻に届かなかった」。因果が無いので持ち越さない。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Make(drops, Enter(1, 2, 20f, true, 3f));

            l.OnZoneCommitted(1, 2, false, 0, 0, 0f);
            l.Tick(1f, 2);
            l.OnZoneCommitted(1, 0, true, 1, 2, 5f);   // 20 秒待たずに区間を出た

            Assert.That(l.CarryCount, Is.EqualTo(0), "時刻未達は持ち越しの対象外（ifMissed の担当）");
        }

        [Test]
        public void Chain_ExitTakeNeverCarries()
        {
            // 離脱時の演出を別の区間で出すと文脈が最も壊れる。chainWait を立てても持ち越さない。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var exitChain = Exit(1, 1, 3f);
            exitChain.chainWait = true;
            var l = Make(drops, Enter(1, 1, 0f, false, 8f), exitChain);

            l.OnZoneCommitted(1, 1, false, 0, 0, 0f);
            l.Tick(0.02f, 1);                              // 進入演出（8 秒）が走る
            l.OnZoneCommitted(1, 2, true, 1, 1, 3f);       // 走行中に区間を出る
            Assert.That(l.CarryCount, Is.EqualTo(0));
        }

        [Test]
        public void Chain_CapDropsOldestAndReports()
        {
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var defs = new List<TakeRunnerLogic.Def> { Exit(1, 9, 60f) };   // 長い演出で画面を塞ぐ
            for (int i = 0; i < TakeRunnerLogic.MaxCarrySlots + 1; i++)
                defs.Add(Enter(1, i, 0f, true, 2f));
            var l = Make(drops, defs.ToArray());

            l.OnZoneCommitted(1, 9, false, 0, 0, 0f);
            l.OnZoneCommitted(1, 0, true, 1, 9, 1f);       // 塞ぐ演出が発火
            Assert.That(l.IsActive, Is.True);

            float t = 1f;
            for (int i = 0; i < TakeRunnerLogic.MaxCarrySlots + 1; i++)
            {
                l.Tick(t += 0.5f, i);
                l.OnZoneCommitted(1, i + 1, true, 1, i, t += 0.5f);
            }
            Assert.That(l.CarryCount, Is.EqualTo(TakeRunnerLogic.MaxCarrySlots));
            Assert.That(drops, Has.Some.Matches<(int, TakeRunnerLogic.DropReason)>(
                x => x.Item2 == TakeRunnerLogic.DropReason.CarryExpired), "溢れた分は必ず報告する");
        }

        [Test]
        public void Chain_ExpiresAndReports()
        {
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Scenario(drops, chain: true);
            l.OnZoneCommitted(1, 1, false, 0, 0, 0f);
            l.OnZoneCommitted(1, 2, true, 1, 1, 5f);
            l.Tick(5.02f, 2);
            l.OnZoneCommitted(1, 0, true, 1, 2, 10f);
            Assert.That(l.CarryCount, Is.EqualTo(1));

            l.Tick(10f + TakeRunnerLogic.CarryMaxWaitSec + 0.1f, 0);
            Assert.That(l.CarryCount, Is.EqualTo(0));
            Assert.That(drops, Has.Exactly(1).Matches<(int, TakeRunnerLogic.DropReason)>(
                x => x.Item1 == 1 && x.Item2 == TakeRunnerLogic.DropReason.CarryExpired));
        }

        [Test]
        public void Chain_AbortActiveDropsCarryAndReports()
        {
            // 卓の「■ 画面を取り返す」で止めた直後に、溜まっていた演出が噴き出さないこと。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Scenario(drops, chain: true);
            l.OnZoneCommitted(1, 1, false, 0, 0, 0f);
            l.OnZoneCommitted(1, 2, true, 1, 1, 5f);
            l.Tick(5.02f, 2);
            l.OnZoneCommitted(1, 0, true, 1, 2, 10f);

            l.AbortActive();
            Assert.That(l.CarryCount, Is.EqualTo(0));
            Assert.That(drops, Has.Exactly(1).Matches<(int, TakeRunnerLogic.DropReason)>(
                x => x.Item2 == TakeRunnerLogic.DropReason.BlockerGone));
            Assert.That(l.Tick(10.1f, 0).action, Is.EqualTo(TakeRunnerLogic.Action.None));
        }

        [Test]
        public void Chain_SuppressedDropsCarryAndReports()
        {
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Scenario(drops, chain: true);
            l.OnZoneCommitted(1, 1, false, 0, 0, 0f);
            l.OnZoneCommitted(1, 2, true, 1, 1, 5f);
            l.Tick(5.02f, 2);
            l.OnZoneCommitted(1, 0, true, 1, 2, 10f);

            l.AbortActive();                 // 走行中を畳む（卓の介入と同じ）
            drops.Clear();
            l.OnZoneCommitted(1, 1, true, 1, 0, 11f);
            l.SetSuppressed(true);
            l.Tick(11.1f, 1);
            Assert.That(l.CarryCount, Is.EqualTo(0), "人が画面を握ったら溜め込まない");
        }

        [Test]
        public void ChainNext_IsFalseWhenNothingWaits()
        {
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Make(drops, Enter(1, 0, 0f, false, 1f));
            l.OnZoneCommitted(1, 0, false, 0, 0, 0f);
            l.Tick(0.02f, 0);
            var end = l.Tick(1.1f, 0);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.chainNext, Is.False, "次が無ければ普通に画面を返す");
        }

        [Test]
        public void ChainNext_IsTrueForConsecutiveTakesInSameSegment()
        {
            // 同じ区間に演出を 2 本並べる（＝「連続でつける」）。繋ぎ目で画面を返さない。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var l = Make(drops, Enter(1, 0, 0f, false, 1f), Enter(1, 0, 0f, false, 1f));
            l.OnZoneCommitted(1, 0, false, 0, 0, 0f);
            var first = l.Tick(0.02f, 0);
            Assert.That(first.takeIndex, Is.EqualTo(0));

            var end = l.Tick(1.1f, 0);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.chainNext, Is.True);

            var second = l.Tick(1.12f, 0);
            Assert.That(second.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(second.takeIndex, Is.EqualTo(1));
            Assert.That(second.takeStarted, Is.True);
        }

        [Test]
        public void ChainNext_NotSetOnWatchdog()
        {
            // 壊れて止まったときは素直に画面を返す（仕切り直し）。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var slow = Enter(1, 0, 0f, false, -1f);   // untilClipEnd（外部通知待ち）
            slow.maxDurationSec = 1f;
            var l = Make(drops, slow, Enter(1, 0, 0f, false, 1f));
            l.OnZoneCommitted(1, 0, false, 0, 0, 0f);
            l.Tick(0.02f, 0);
            var end = l.Tick(1.2f, 0);
            Assert.That(end.forced, Is.True);
            Assert.That(end.chainNext, Is.False);
        }

        [Test]
        public void Yield_NowReportsDroppedArmedTakes()
        {
            // policy=yield の打ち切りで消える武装は、旧実装だけが報告を漏らしていた。
            var drops = new List<(int, TakeRunnerLogic.DropReason)>();
            var yielding = Enter(1, 0, 0f, false, 10f);
            yielding.yieldOnZoneChange = true;
            var l = Make(drops, yielding, Enter(1, 0, 5f, false, 2f));

            l.OnZoneCommitted(1, 0, false, 0, 0, 0f);
            l.Tick(0.02f, 0);                          // 1 本目が走る（10 秒）
            l.OnZoneCommitted(1, 1, true, 1, 0, 1f);   // 区間を移って打ち切り

            Assert.That(drops, Has.Exactly(1).Matches<(int, TakeRunnerLogic.DropReason)>(x => x.Item1 == 1));
        }
    }
}
