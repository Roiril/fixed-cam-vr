#nullable enable

namespace FixedCamVr.Input
{
    /// <summary>右手の追跡を失った登録サンプルを中断し、再追跡後の A 解放まで再開を止める。</summary>
    public sealed class RegistrationSampleInputGate
    {
        private bool _needsRelease;

        public bool AcceptInput { get; private set; }

        public bool Tick(bool tracked, bool buttonHeld, bool sampleActive)
        {
            if (!tracked && (buttonHeld || sampleActive)) _needsRelease = true;
            if (_needsRelease && tracked && !buttonHeld) _needsRelease = false;

            AcceptInput = tracked && !_needsRelease;
            return AcceptInput;
        }
    }
}
