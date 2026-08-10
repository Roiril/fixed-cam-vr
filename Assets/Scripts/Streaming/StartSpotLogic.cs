#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の開始位置（<c>layout.startSpot</c> の円）を「**歩いて入ってきた**」として判定する。
    /// UnityEngine 非依存の判断だけを持つ（dt 注入・EditMode テスト可）。
    ///
    /// ⚠ <b>円は状態、開始は事象。</b> 旧実装は「いま円の中に 0.5 秒居る」だけを見ていたため、
    /// 起動直後にたまたま条件が揃うと演出が即座に走り出した（2026-08-09 ユーザー報告
    /// 「体験者の位置を基準にトリガーしてほしいが、今はすぐに起動してしまう」）。
    /// 具体的には次の 2 つで踏む:
    /// <list type="bullet">
    ///   <item>頭のポーズがまだ来ておらず、course 原点付近が円の中に入っている</item>
    ///   <item>前の体験者が円の中に立ったまま、スタッフがランをやり直した</item>
    /// </list>
    ///
    /// なので<b>外に居たことを見てから、入ってきた滞在を数える</b>。加えて
    /// <see cref="LineCrossLogic"/> と同じ 2 つの不連続ガードを持つ —
    /// dt が飛んだ（HMD 着脱・アプリ復帰）／1 フレームで大きく飛んだ（トラッキング初期化・recenter）
    /// フレームは軌跡として信用せず、**外に居たことの観測からやり直す**。
    ///
    /// これで「立っても始まらない」が起きうるのは *円の中で武装された* ときだけで、
    /// その場合はスタッフの ⏭（<see cref="IntroLogic.RequestAdvance"/>）が従来どおり効く。
    /// </summary>
    public sealed class StartSpotLogic
    {
        /// <summary>これ以上 dt が飛んだフレームは軌跡として信用しない（秒）。</summary>
        public const float MaxContinuousDtSec = LineCrossLogic.MaxContinuousDtSec;

        /// <summary>1 フレームでこれ以上動いたら軌跡として信用しない（m）。</summary>
        public const float MaxStepM = LineCrossLogic.MaxStepM;

        /// <summary>武装してから軌跡を信用し始めるまでの連続追跡時間（秒）。</summary>
        public const float SettleSec = 0.5f;

        /// <summary>円の外と認めるための余白（m）。境界上のふらつきで「外に出た」にしない。</summary>
        public const float ExitMarginM = 0.10f;

        private bool _sawOutside;
        private float _inSec;
        private float _settledSec;
        private bool _hasPrev;
        private float _prevX, _prevZ;
        private bool _fired;

        /// <summary>円の外に居たことを観測したか（テスト・診断用）。</summary>
        public bool SawOutside => _sawOutside;

        /// <summary>いま円の中に留まっている秒数（テスト・診断用）。</summary>
        public float InsideSec => _inSec;

        /// <summary>一度でも成立したか。段 0 を抜けるまで保持する。</summary>
        public bool Fired => _fired;

        /// <summary>
        /// 武装し直す。<b>ラン開始（体験者の交代）では必ず呼ぶ</b> —
        /// 呼ばないと前の体験者が満たした条件がそのまま次のランへ持ち越される。
        /// </summary>
        public void Rearm()
        {
            _sawOutside = false;
            _inSec = 0f;
            _settledSec = 0f;
            _hasPrev = false;
            _fired = false;
        }

        /// <summary>頭のポーズが取れない・位置合わせが未了などで判定できないフレーム。</summary>
        public void NotifyUnavailable()
        {
            _inSec = 0f;
            _settledSec = 0f;
            _hasPrev = false;
        }

        /// <param name="x">頭の course X (m)。</param>
        /// <param name="z">頭の course Z (m)。</param>
        /// <param name="centerX">円の中心 X (m)。</param>
        /// <param name="centerZ">円の中心 Z (m)。</param>
        /// <param name="radiusM">円の半径 (m)。</param>
        /// <param name="holdSec">円の中に留まる必要のある秒数。</param>
        /// <param name="dt">前フレームからの経過（秒）。</param>
        /// <returns>開始の合図が成立しているか。</returns>
        public bool Tick(float x, float z, float centerX, float centerZ,
                         float radiusM, float holdSec, float dt)
        {
            if (_fired) return true;

            bool continuous = dt > 0f && dt <= MaxContinuousDtSec;
            if (_hasPrev && continuous)
            {
                float dx = x - _prevX, dz = z - _prevZ;
                if (dx * dx + dz * dz > MaxStepM * MaxStepM) continuous = false;
            }
            _prevX = x; _prevZ = z; _hasPrev = true;

            if (!continuous)
            {
                // 軌跡が切れた。ここまでの「外に居た」は当てにならないので観測からやり直す。
                _sawOutside = false;
                _inSec = 0f;
                _settledSec = 0f;
                return false;
            }

            // 武装直後の数フレームは、トラッキングが立ち上がる途中の値が混じる。
            if (_settledSec < SettleSec)
            {
                _settledSec += dt;
                return false;
            }

            float r = Mathf.Max(radiusM, 0.01f);
            float d2 = (x - centerX) * (x - centerX) + (z - centerZ) * (z - centerZ);
            bool inside = d2 <= r * r;

            if (!_sawOutside)
            {
                // 外と認めるのは、半径 + 余白より外に居るとき（境界のふらつきで武装解除しない）。
                float ro = r + ExitMarginM;
                if (d2 > ro * ro) _sawOutside = true;
                _inSec = 0f;
                return false;
            }

            _inSec = inside ? _inSec + dt : 0f;
            if (_inSec >= Mathf.Max(holdSec, 0f)) _fired = true;
            return _fired;
        }
    }
}
