#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 心音に同期する画面演出の開始と終了を決める純ロジック。
    /// 警告の自然完了で始まり、3 周目 A の凍結でそのラン中は二度と戻らない。
    /// </summary>
    public sealed class HeartbeatScreenLogic
    {
        private bool _warningCompleted;
        private bool _frozen;

        /// <summary>乗っ取り警告を最後まで読ませた。</summary>
        public void NotifyWarningCompleted() => _warningCompleted = true;

        /// <summary>3 周目 A の凍結へ入った。引き返しても解除しない。</summary>
        public void NotifyFreeze() => _frozen = true;

        /// <summary>次の体験者へ渡す。</summary>
        public void Reset()
        {
            _warningCompleted = false;
            _frozen = false;
        }

        /// <summary>
        /// いま画へ書いてよいか。位置合わせと手動の画面占有は一時停止なのでラッチを変えない。
        /// </summary>
        public bool ShouldApply(bool inRun, bool timelineSuppressed, bool registrationActive)
            => _warningCompleted && !_frozen && inRun && !timelineSuppressed && !registrationActive;
    }
}
