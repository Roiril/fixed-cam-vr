#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// B3 純ロジック。オーバーレイ cue の世代（stale 破棄）と動画 Prepare ライフサイクル
    /// （保留 / 受理可否 / エラー時中止 / タイムアウト / 停止）のセマンティクスを時間非依存で固定する。
    /// </summary>
    public sealed class OverlayPlaybackLogicTests
    {
        private const float Timeout = 6f;

        [Test]
        public void BeginPlay_AdvancesGeneration_AndClearsPending()
        {
            var l = new OverlayPlaybackLogic();
            int g0 = l.Generation;
            l.BeginPrepare(l.BeginPlay(), 0f);
            Assert.That(l.PreparePending, Is.True);
            int g1 = l.BeginPlay();
            Assert.That(g1, Is.EqualTo(g0 + 2));           // 2 回 BeginPlay 分進む
            Assert.That(l.PreparePending, Is.False, "新しい再生要求で進行中 Prepare 保留を無効化する");
        }

        [Test]
        public void AcceptPrepared_TrueForCurrentGen_FalseForStale()
        {
            var l = new OverlayPlaybackLogic();
            int gen = l.BeginPlay();
            l.BeginPrepare(gen, 0f);
            Assert.That(l.AcceptPrepared(), Is.True, "現行世代の Prepare 完了は受理");
            Assert.That(l.PreparePending, Is.False, "受理で保留を下ろす");

            // 別 cue が来て世代が進んだ後の stale prepare 完了は受理しない。
            l.BeginPrepare(gen, 0f);
            l.BeginPlay();                                 // 世代前進（新 cue）
            Assert.That(l.AcceptPrepared(), Is.False);
        }

        [Test]
        public void ShouldAbortOnError_TrueForCurrentGen_FalseForStale()
        {
            var l = new OverlayPlaybackLogic();
            int gen = l.BeginPlay();
            l.BeginPrepare(gen, 0f);
            Assert.That(l.ShouldAbortOnError(), Is.True, "現行世代の準備/再生に対するエラーは中止");
            Assert.That(l.PreparePending, Is.False, "中止判定で保留を下ろす");

            l.BeginPrepare(gen, 0f);
            l.BeginPlay();                                 // supersede
            Assert.That(l.ShouldAbortOnError(), Is.False, "stale 世代のエラーは新 cue を殺さない");
        }

        [Test]
        public void TimedOut_OnlyWhenPendingAndCurrentGenAndExceeded()
        {
            var l = new OverlayPlaybackLogic();
            int gen = l.BeginPlay();
            l.BeginPrepare(gen, 0f);
            Assert.That(l.TimedOut(Timeout - 0.01f, Timeout), Is.False, "timeout 未満は false");
            Assert.That(l.TimedOut(Timeout + 0.01f, Timeout), Is.True, "超過で true");
            Assert.That(l.PreparePending, Is.False, "true で保留を下ろす");

            // 保留なしは false。
            Assert.That(l.TimedOut(1000f, Timeout), Is.False);

            // stale 世代は false（別 cue が来て世代が進んでいる）。
            var l2 = new OverlayPlaybackLogic();
            int g = l2.BeginPlay();
            l2.BeginPrepare(g, 0f);
            l2.BeginPlay();
            Assert.That(l2.TimedOut(1000f, Timeout), Is.False);
        }

        [Test]
        public void TimedOut_DoesNotDoubleFire()
        {
            var l = new OverlayPlaybackLogic();
            l.BeginPrepare(l.BeginPlay(), 0f);
            Assert.That(l.TimedOut(1000f, Timeout), Is.True);
            Assert.That(l.TimedOut(1000f, Timeout), Is.False, "一度下ろしたら再発火しない");
        }

        [Test]
        public void Stop_AdvancesGeneration_AndClearsPending()
        {
            var l = new OverlayPlaybackLogic();
            int gen = l.BeginPlay();
            l.BeginPrepare(gen, 0f);
            int before = l.Generation;
            l.Stop();
            Assert.That(l.Generation, Is.EqualTo(before + 1), "停止で世代を進める（stale prepare 完了を破棄）");
            Assert.That(l.PreparePending, Is.False);
            // Stop 後に届いた元 Prepare 完了は受理しない。
            Assert.That(l.AcceptPrepared(), Is.False);
        }

        // ---- ロード中フラグ（untilClipEnd の終端誤判定を防ぐ・2026-07-26 監査 HIGH）----

        [Test]
        public void BeginPlay_MarksLoading_UntilEndLoad()
        {
            var l = new OverlayPlaybackLogic();
            int g = l.BeginPlay();
            Assert.That(l.IsLoading(g), Is.True, "発火直後はロード中（＝まだ終わっていない）");
            l.EndLoad(g);
            Assert.That(l.IsLoading(g), Is.False);
        }

        [Test]
        public void EndLoad_FromStaleGeneration_DoesNotClearCurrentLoading()
        {
            var l = new OverlayPlaybackLogic();
            int g0 = l.BeginPlay();
            int g1 = l.BeginPlay();          // 置き換え発火
            l.EndLoad(g0);                   // 古い発火のロードが遅れて完了
            Assert.That(l.IsLoading(g1), Is.True, "現行世代のロード中を古い完了で下ろさない");
            Assert.That(l.IsLoading(g0), Is.False, "古い世代はそもそもロード中とみなさない");
        }

        [Test]
        public void Stop_ClearsLoading()
        {
            var l = new OverlayPlaybackLogic();
            int g = l.BeginPlay();
            l.Stop();
            Assert.That(l.IsLoading(g), Is.False, "停止でロード中は消える（終端判定が固まらない）");
        }

        [Test]
        public void StillImagePath_NoPrepare_NeverPending_NeverTimesOut()
        {
            // 静止画相当: BeginPlay のみで BeginPrepare しない。
            var l = new OverlayPlaybackLogic();
            l.BeginPlay();
            Assert.That(l.PreparePending, Is.False);
            Assert.That(l.TimedOut(1e6f, Timeout), Is.False, "Prepare 未発行なら永遠にタイムアウトしない");
        }

        // ---- 滲んで現れる（OverlayRevealTrack・0349）--------------------------------------

        private static float Run(OverlayRevealTrack t, float sec, float dt = 1f / 72f)
        {
            float v = t.Value;
            for (float e = 0f; e < sec - 1e-5f; e += dt) v = t.Tick(dt);
            return v;
        }

        [Test]
        public void Reveal_StaysZeroDuringDelay_ThenReachesOne()
        {
            var t = new OverlayRevealTrack();
            t.BeginIn(0.6f, 4.2f);
            Assert.That(Run(t, 0.5f), Is.EqualTo(0f), "遅れの間は 0（シミはまだ出ない）");
            Assert.That(Run(t, 2.0f), Is.InRange(0.3f, 0.99f), "途中は 0 と 1 の間");
            Run(t, 2.4f);
            Assert.That(t.Current, Is.EqualTo(OverlayRevealTrack.Phase.Shown));
            Assert.That(t.Max, Is.EqualTo(1f).Within(1e-4f), "出切った瞬間は 1");
        }

        [Test]
        public void Reveal_CurveIsFastFirst()
        {
            // 1-(1-t)^2.2: 半分の時間で 0.78 を越える（出始めが速く、終わりへ向けて落ち着く）
            Assert.That(OverlayRevealLogic.In(0.5f, 0f, 1f), Is.EqualTo(1f - System.MathF.Pow(0.5f, 2.2f)).Within(1e-5f));
            Assert.That(OverlayRevealLogic.In(0.5f, 0f, 1f), Is.GreaterThan(0.75f));
        }

        [Test]
        public void Reveal_BreathesBetweenBreathMinAndOne()
        {
            var t = new OverlayRevealTrack();
            t.BeginIn(0f, 0.1f);
            Run(t, 0.2f);
            float lo = 1f, hi = 0f;
            for (int i = 0; i < 72 * 12; i++)
            {
                float v = t.Tick(1f / 72f);
                lo = System.Math.Min(lo, v);
                hi = System.Math.Max(hi, v);
            }
            Assert.That(lo, Is.EqualTo(OverlayRevealLogic.BreathMin).Within(1e-3f));
            Assert.That(hi, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void Reveal_OutGoesToZeroWithinOutSec_AndKeepsMax()
        {
            var t = new OverlayRevealTrack();
            t.BeginIn(0f, 1f);
            Run(t, 0.5f);
            float from = t.Value;
            t.BeginOut(0.25f);
            Assert.That(t.Current, Is.EqualTo(OverlayRevealTrack.Phase.Out));
            Run(t, 0.3f);
            Assert.That(t.OutFinished, Is.True);
            Assert.That(t.Value, Is.EqualTo(0f));
            Assert.That(t.Max, Is.EqualTo(from).Within(1e-4f), "観測用の最大は引っ込みで消えない");
        }

        [Test]
        public void Reveal_SecondBeginOutDoesNotChangeTheSpeed()
        {
            var t = new OverlayRevealTrack();
            t.BeginIn(0f, 0.1f);
            Run(t, 0.2f);
            t.BeginOut(1f);
            Run(t, 0.5f);
            float mid = t.Value;
            t.BeginOut(0.01f);
            Assert.That(t.Value, Is.EqualTo(mid), "引っ込み中に重ねて呼んでも跳ばない");
            Assert.That(t.Current, Is.EqualTo(OverlayRevealTrack.Phase.Out));
        }

        [Test]
        public void Reveal_BeginOutBeforeBeginIn_IsDoneImmediately()
        {
            var t = new OverlayRevealTrack();
            t.BeginOut(0.45f);
            Assert.That(t.OutFinished, Is.True);
            Assert.That(t.Value, Is.EqualTo(0f));
        }

        [Test]
        public void Blend_OnlyMultiplyIsMultiply()
        {
            Assert.That(OverlayRevealLogic.IsMultiplyBlend("multiply"), Is.True);
            Assert.That(OverlayRevealLogic.IsMultiplyBlend("Multiply"), Is.True);
            Assert.That(OverlayRevealLogic.IsMultiplyBlend("replace"), Is.False);
            Assert.That(OverlayRevealLogic.IsMultiplyBlend(""), Is.False);
            Assert.That(OverlayRevealLogic.IsMultiplyBlend(null), Is.False);
            Assert.That(OverlayRevealLogic.IsMultiplyBlend("screen"), Is.False, "未知の値は差し替え");
        }
    }
}
