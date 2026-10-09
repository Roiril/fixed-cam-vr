namespace FixedCamVr.Input
{
    public enum StaffSetupButtonAction { None, Continue, RecallScreen }
    /// <summary>Bの短押しと画面呼び戻しを分け、持ち越し・長押し解放・二重入力を進行に使わない。</summary>
    public sealed class StaffSetupButtonLogic
    {
        public const float RecallSec = 1f;
        private bool _needsRelease = true, _held, _recalled;
        private bool _canContinue = true;
        private float _sec;
        public StaffSetupButtonAction Tick(bool held, float dt, bool enabled, bool canContinue = true)
        {
            if (_canContinue != canContinue) { _needsRelease = true; _held = false; _sec = 0f; }
            _canContinue = canContinue;
            if (!enabled) { _needsRelease = true; _held = false; _sec = 0f; return StaffSetupButtonAction.None; }
            if (_needsRelease) { if (!held) _needsRelease = false; return StaffSetupButtonAction.None; }
            if (held)
            {
                if (!_held) { _sec = 0f; _recalled = false; }
                _held = true;
                _sec += System.Math.Max(0f, System.Math.Min(dt, 0.25f));
                if (!_recalled && _sec >= RecallSec) { _recalled = true; return StaffSetupButtonAction.RecallScreen; }
                return StaffSetupButtonAction.None;
            }
            bool tap = _held && !_recalled && _sec <= 0.5f;
            _held = false; _sec = 0f;
            return tap && canContinue ? StaffSetupButtonAction.Continue : StaffSetupButtonAction.None;
        }
    }
}
