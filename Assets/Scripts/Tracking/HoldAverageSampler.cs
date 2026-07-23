#nullable enable
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// A ホールド平均サンプリングの計時・平均・早離し中断を担う純ロジック。
    /// <see cref="CourseRegistrationController"/> の <c>UpdateMarkSampling</c> から分離して EditMode
    /// テスト可能にする（dt は <see cref="Tick"/> の引数で受けるため Time.deltaTime に依存しない）。
    ///
    /// 意味論（旧 UpdateMarkSampling と 1:1 一致）:
    ///   Down エッジ（markDown）でサンプリング開始 → ホールド中（markHeld）は毎フレーム位置を加算 →
    ///   holdSec 経過で平均位置を確定（Captured）。途中で離したら不成立（Aborted・位置は採らない）。
    /// 押下瞬間の 1 サンプルは腕の振り・ボタン押し込みのブレを拾うため、ホールド中の平均で均す。
    /// </summary>
    public sealed class HoldAverageSampler
    {
        public enum Result { None, Captured, Aborted }

        private float _holdSec;

        private bool _active;
        private Vector3 _accum;
        private int _count;
        private float _time;

        public HoldAverageSampler(float holdSec)
        {
            _holdSec = Max0(holdSec);
        }

        /// <summary>ホールド確定に必要な秒数を設定する。</summary>
        public void Configure(float holdSec) => _holdSec = Max0(holdSec);

        /// <summary>サンプリング中か。</summary>
        public bool Active => _active;

        /// <summary>ホールド進捗 [0,1]（非アクティブ or holdSec&lt;=0 で 0）。</summary>
        public float Progress01 => (_active && _holdSec > 0f) ? Clamp01(_time / _holdSec) : 0f;

        /// <summary>計時・累積をクリアする（サンプリング中断）。</summary>
        public void Reset()
        {
            _active = false;
            _accum = Vector3.zero;
            _count = 0;
            _time = 0f;
        }

        /// <summary>
        /// 1 フレーム分の入力を進める。
        ///   非アクティブ: markDown で開始（現フレームを 1 サンプル目に採用）して None、なければ None。
        ///   アクティブ + !markHeld: 早離し＝Aborted（位置は採らない）。
        ///   アクティブ + markHeld: 位置を加算・計時、holdSec 到達で平均を avg へ返し Captured、未達は None。
        /// </summary>
        public Result Tick(bool markDown, bool markHeld, float dt, Vector3 pointer, out Vector3 avg)
        {
            avg = Vector3.zero;

            if (!_active)
            {
                if (!markDown) return Result.None;
                _active = true;
                _accum = pointer; // 押下フレームも 1 サンプル目として使う
                _count = 1;
                _time = 0f;
                return Result.None;
            }

            if (!markHeld)
            {
                // holdSec 未満で離した → マーク不成立（点は採らない）。
                _active = false;
                return Result.Aborted;
            }

            _accum += pointer;
            _count++;
            _time += dt;
            if (_time < _holdSec) return Result.None;

            _active = false;
            avg = _accum / _count;
            return Result.Captured;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Max0(float v) => v < 0f ? 0f : v;
    }
}
