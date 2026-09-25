#nullable enable

namespace FixedCamVr.Input
{
    /// <summary>
    /// コントローラ接続復帰時の入力を、対象ボタンをすべて離した次のフレームまで止める。
    /// 接続復帰フレーム自体は入力に使わない。
    /// </summary>
    public sealed class ControllerConnectionGate
    {
        public readonly struct Result
        {
            public Result(bool acceptInput, bool disconnected, bool reconnected)
            {
                AcceptInput = acceptInput;
                Disconnected = disconnected;
                Reconnected = reconnected;
            }

            public bool AcceptInput { get; }
            public bool Disconnected { get; }
            public bool Reconnected { get; }
        }

        private bool _connected;
        private bool _releaseObserved;
        private bool _acceptInput;

        public Result Tick(bool connected, bool anyTargetButtonHeld)
        {
            bool disconnected = _connected && !connected;
            bool reconnected = !_connected && connected;
            _connected = connected;

            if (!connected)
            {
                _releaseObserved = false;
                _acceptInput = false;
                return new Result(false, disconnected, false);
            }

            if (!_acceptInput)
            {
                if (_releaseObserved)
                {
                    _acceptInput = true;
                }
                else if (!anyTargetButtonHeld)
                {
                    _releaseObserved = true;
                }
            }

            return new Result(_acceptInput, false, reconnected);
        }
    }
}
