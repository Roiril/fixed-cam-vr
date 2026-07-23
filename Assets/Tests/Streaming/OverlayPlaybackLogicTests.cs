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

        [Test]
        public void StillImagePath_NoPrepare_NeverPending_NeverTimesOut()
        {
            // 静止画相当: BeginPlay のみで BeginPrepare しない。
            var l = new OverlayPlaybackLogic();
            l.BeginPlay();
            Assert.That(l.PreparePending, Is.False);
            Assert.That(l.TimedOut(1e6f, Timeout), Is.False, "Prepare 未発行なら永遠にタイムアウトしない");
        }
    }
}
