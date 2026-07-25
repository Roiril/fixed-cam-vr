#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 「体験者がいまどのゾーンに居るか」を確定する純ロジック（＝ショーの時計の入力）。
    /// dwell（最小滞在）だけを見る。**画面の状態を一切参照しない** — cue / インサート / Web override の凍結も、
    /// dip 演出も、最小ショット長（クールダウン）も見ない。画面が止まっていても体験者は歩くので、
    /// 時計は進み続けなければならない（設計 <c>.claude/plans/2026-07-25_shot-timeline-foundation.md</c> 不変条件 4）。
    ///
    /// 旧実装ではこの dwell 判定が <see cref="SwitchDirectorLogic"/> の中で凍結・クールダウンと同居しており、
    /// 演出中は周回カウントごと止まっていた。そのため <c>InsertController</c> が復帰時に
    /// 「実は Zone だった」と偽装発火して周回の辻褄を合わせていた（その偽装は本分離で廃止）。
    ///
    /// ⚠ **所在は暫定**。段 C で <c>ShotDirector</c> を新設する際に、画面層（<see cref="CameraSwitchDirector"/>）から
    ///   人の層へ移す。今はシーン / prefab の配線変更による実機事故（SerializeField 欠落の実績あり）を
    ///   避けるため画面層のコンポーネントに同居させている。
    /// </summary>
    public sealed class ZoneProgressionLogic
    {
        /// <summary>最小滞在の既定 (秒)。1.8m 四方・帯幅 ~0.45m を歩行 0.6〜1.5s で抜ける想定。</summary>
        public const float DefaultDwellSec = 0.5f;

        private float _dwellSec = DefaultDwellSec;

        private int _current = -1;
        private bool _hasPending;
        private int _pending;
        private float _pendingSince;

        /// <summary>確定済みのゾーンカメラ index（未確定は -1）。</summary>
        public int Current => _current;

        /// <summary>dwell 待ちの目標があるか。</summary>
        public bool HasPending => _hasPending;

        /// <summary>dwell 待ちの目標カメラ index（<see cref="HasPending"/> が false のとき無効）。</summary>
        public int Pending => _pending;

        public void Configure(float dwellSec) => _dwellSec = dwellSec < 0f ? 0f : dwellSec;

        /// <summary>現在ゾーンを与えて初期化する（保留は捨てる）。</summary>
        public void Reset(int current)
        {
            _current = current;
            _hasPending = false;
        }

        /// <summary>
        /// 未確定の保留を捨てる。**Web cameraOverride の適用時にだけ**使う — override 中は
        /// <c>ShowControlClient</c> が <c>PlayerZoneTracker</c> ごと無効化する＝入力そのものが切れるため、
        /// 直前に積まれた未確定の目標は根拠を失う。画面の凍結（cue / インサート）では呼ばない。
        /// </summary>
        public void ClearPending() => _hasPending = false;

        /// <summary>
        /// ゾーン判定（ヒステリシス通過後）の目標を受ける。現在ゾーンと同じなら保留を解消する
        /// （境界でうろついて戻った＝移動しなかった）。目標が変われば dwell を測り直す。
        /// </summary>
        public void Request(int target, float now)
        {
            if (target < 0) return; // ゾーン外 / 無効 index は無視（保留に触れない）
            if (target == _current)
            {
                _hasPending = false;
                return;
            }
            if (!_hasPending || _pending != target)
            {
                _hasPending = true;
                _pending = target;
                _pendingSince = now;
            }
        }

        /// <summary>毎フレーム評価。dwell を満たした保留を確定させる。確定したら true。</summary>
        public bool Tick(float now, out int committed)
        {
            committed = _current;
            if (!_hasPending) return false;
            if (_pending == _current) { _hasPending = false; return false; }
            if (now - _pendingSince < _dwellSec) return false;
            _current = _pending;
            _hasPending = false;
            committed = _current;
            return true;
        }
    }
}
