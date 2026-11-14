#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// <c>cgMode:"trail"</c> の人形を、カットが指定した点（<c>trailFromX</c> / <c>trailFromZ</c>）から歩き出させる。
    ///
    /// 始まりは指定点そのものに立つ（hop 0）。指定点にいちばん近い跡の点の道のり s0 から、
    /// <c>trailHopSec</c> ごとに目標を <c>trailStepM</c> ずつ進め、その道のりの点へ跳ぶ（滑らせない）。
    /// 次の目標が <c>Length − trailGapM</c> に届いたら、既存の「gap 後ろの飛び石に立つ」へ引き渡す。
    ///
    /// ⚠ 引き渡しで後ろへ戻らない。gap の飛び石（<see cref="FootprintTrail.TryPointBehind"/>）は
    ///   stepM 刻みに切り下げるので、届いた瞬間には今の目標より手前にあることがある。
    ///   そのときは今の石で待ち、gap の石が今の目標に追いついてから渡す。
    ///
    /// 跡の数え直し（<see cref="FootprintTrail.Epoch"/>）と、目標が捨てた区間に入ったときは打ち切る（<see cref="LastEnd"/>）。
    /// 純ロジック（時刻と跡は呼び出し側が渡す）なので EditMode テストで固定できる。
    /// </summary>
    public sealed class TrailApproach
    {
        /// <summary>指定点からいちばん近い跡の点がこれより遠ければ、歩き出しを使わない (m)。</summary>
        public const float MaxSnapM = 0.6f;

        /// <summary>歩き出しが終わった理由。</summary>
        public enum End
        {
            None = 0,
            /// <summary>目標が gap に届き、gap の飛び石へ渡した。</summary>
            Handover = 1,
            /// <summary>跡が数え直された／目標の区間が捨てられた。</summary>
            Lost = 2,
            /// <summary>呼び出し側が止めた（カットの切り替え・交代）。</summary>
            Cancelled = 3,
        }

        private bool _active;
        private int _epoch;
        private float _target;
        private float _stepM;
        private float _gapM;
        private float _hopSec;
        private float _nextHopT;
        private int _hop;
        private Vector2 _xz;

        /// <summary>歩き出しの最中か。</summary>
        public bool Active => _active;

        /// <summary>何回跳んだか（0 = 指定点に立っている）。<see cref="Active"/> でなければ -1。</summary>
        public int Hop => _active ? _hop : -1;

        /// <summary>いまの目標の道のり（通算）。hop 0 では指定点にいちばん近い跡の点の道のり s0。</summary>
        public float Target => _target;

        /// <summary>いま立っている位置（course XZ）。<see cref="Active"/> のときだけ意味がある。</summary>
        public Vector2 StandXz => _xz;

        /// <summary>最後に終わった理由。<see cref="Begin"/> で None に戻る。</summary>
        public End LastEnd { get; private set; }

        /// <summary>
        /// 歩き出しを始める。指定点 <paramref name="from"/> にいちばん近い跡の点を探し、
        /// <see cref="MaxSnapM"/> 以内なら始めて true。遠い・跡が無いなら false（呼び出し側は gap の立ち方へ落とす）。
        /// <paramref name="nearestDistM"/> は見つかった点までの距離（跡が無ければ +∞）。
        /// stepM / gapM / hopSec は解決済みの値を渡す（<see cref="TakeSchema.ResolveTrailStepM"/> ほか）。
        /// </summary>
        public bool Begin(FootprintTrail trail, Vector2 from, float now, float stepM, float gapM, float hopSec,
                          out float nearestDistM)
        {
            _active = false;
            LastEnd = End.None;
            if (!trail.TryNearest(from, out float s0, out nearestDistM)) return false;
            if (!(nearestDistM <= MaxSnapM)) return false;
            if (!(stepM > 0f) || !(hopSec > 0f) || float.IsNaN(gapM)) return false;
            _active = true;
            _epoch = trail.Epoch;
            _target = s0;
            _stepM = stepM;
            _gapM = gapM;
            _hopSec = hopSec;
            _nextHopT = now + hopSec;
            _hop = 0;
            _xz = from;
            return true;
        }

        /// <summary>呼び出し側の都合で止める。</summary>
        public void Cancel()
        {
            if (!_active) return;
            _active = false;
            LastEnd = End.Cancelled;
        }

        /// <summary>
        /// 1 フレーム進める。歩き出しを続けるなら true と立つ位置・hop を返す。
        /// 終わった（引き渡し・打ち切り）なら false（以後 <see cref="Active"/> は false）。
        /// 1 回の呼び出しで跳ぶのは高々 1 歩（遅れたフレームでも続けざまに跳ばない＝滑って見えない）。
        /// </summary>
        public bool Tick(FootprintTrail trail, float now, out Vector2 xz, out int hop)
        {
            xz = default;
            hop = -1;
            if (!_active) return false;
            if (trail.Epoch != _epoch) { Finish(End.Lost); return false; }
            if (now >= _nextHopT)
            {
                float next = _target + _stepM;
                if (next < trail.Length - _gapM - 1e-5f)
                {
                    if (!trail.TryPointAt(next, out Vector2 p)) { Finish(End.Lost); return false; }
                    _target = next;
                    _hop++;
                    _xz = p;
                    _nextHopT += _hopSec;
                    // 1 拍以上遅れていたら拍を今から数え直す（取り戻そうとして毎フレーム跳ばない）。
                    if (_nextHopT <= now) _nextHopT = now + _hopSec;
                }
                else if (trail.TryPointBehind(_gapM, _stepM, out _, out int station)
                         && station * _stepM >= _target - 1e-4f)
                {
                    Finish(End.Handover);
                    return false;
                }
                // それ以外は今の石で待つ（gap の石が今の目標より手前＝渡すと後ろへ戻る）。
            }
            xz = _xz;
            hop = _hop;
            return true;
        }

        private void Finish(End why)
        {
            _active = false;
            LastEnd = why;
        }
    }
}
