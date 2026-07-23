#nullable enable
namespace FixedCamVr.Streaming
{
    /// <summary>
    /// オーバーレイ cue の世代（stale 破棄）と動画 Prepare ライフサイクル（保留/タイムアウト/
    /// 受理可否/エラー時中止）の純判定。MonoBehaviour（ScreenOverlayController）から分離して EditMode テスト可能に。
    /// 単一 VideoPlayer 前提で _prepareGen は常に最新の Prepare を指す（stale 世代の完了/エラーは無視する）。
    /// </summary>
    public sealed class OverlayPlaybackLogic
    {
        private int _gen;
        private int _prepareGen = -1;
        private bool _preparePending;
        private float _prepareStart;

        public int Generation => _gen;
        public bool PreparePending => _preparePending;

        /// 新しい再生要求。世代を進め、進行中 Prepare 保留を無効化して返す。
        public int BeginPlay() { _preparePending = false; return ++_gen; }

        /// 動画 Prepare 発行を記録（gen=BeginPlay の戻り、now=unscaled realtime）。
        public void BeginPrepare(int gen, float now) { _prepareGen = gen; _preparePending = true; _prepareStart = now; }

        /// Prepare 完了を現行世代として受理してよいか。受理時は保留を下ろす。
        public bool AcceptPrepared() { if (_prepareGen != _gen) return false; _preparePending = false; return true; }

        /// 動画エラーが現行世代の準備/再生に対するものか（true で呼び出し側が cue を畳む）。
        public bool ShouldAbortOnError() { if (_prepareGen != _gen) return false; _preparePending = false; return true; }

        /// prepare 保留が timeout 超過か（現行世代のみ）。true で保留を下ろす。
        public bool TimedOut(float now, float timeoutSec)
        {
            if (!_preparePending || _prepareGen != _gen) return false;
            if (now - _prepareStart <= timeoutSec) return false;
            _preparePending = false;
            return true;
        }

        /// 停止で世代を進め保留を無効化。
        public void Stop() { _preparePending = false; ++_gen; }
    }
}
