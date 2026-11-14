#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 3 周目の長押しの間だけ、人形の下からライブの自分が透ける量 b（<c>_LiveBleed</c>）の純判定。
    /// 台本の旗 <see cref="ShowTakeDef.markBleed"/> が立った演出の、人形が出ているカット（無人の写真＋CG 人形）でだけ働く。
    ///
    /// 押している間は目標 = <see cref="MaxBleed"/> × 長押しの進み具合、離したら目標 0。
    /// 値は時定数 <see cref="TauSec"/> の指数で目標へ寄る（行き過ぎない ＝ 0..<see cref="MaxBleed"/> の外へ出ない）。
    /// 報告が成立したら <see cref="Collapse"/> で即 0 へ畳み、その長押しを離すまで 0 のまま（押し直せばまた透ける）。
    /// 働かない間（<c>armed=false</c>）は即 0。目標 0 で <see cref="SnapEps"/> を割ったら 0 に揃える
    /// （シェーダへ正確な 0 を書く ＝ 旗の無い走行の画を 1 画素も変えない）。
    ///
    /// 観測: 働いていた長押しが終わるたび（離した・働かなくなった）に <see cref="HoldCount"/> が 1 増え、
    /// その長押しで出た最大を <see cref="LastHoldMax"/> に残す。どちらも <see cref="Reset"/> では戻さない（増分で読む）。
    /// </summary>
    public sealed class LiveBleedLogic
    {
        public const float MaxBleed = 0.5f;
        public const float TauSec = 0.15f;
        public const float MaxStepSec = 0.25f;
        public const float SnapEps = 1e-4f;

        private bool _holding;
        private bool _collapsed;
        private float _holdMax;

        /// <summary>いまの b（シェーダへ書く値）。</summary>
        public float Value { get; private set; }

        /// <summary>働いていた長押しが終わった回数（単調増加）。</summary>
        public int HoldCount { get; private set; }

        /// <summary>最後に終わった長押しで出た b の最大。</summary>
        public float LastHoldMax { get; private set; }

        /// <summary>働いている長押しの最中か。</summary>
        public bool Holding => _holding;

        /// <summary>報告の成立で畳んだあと、まだ離していないか。</summary>
        public bool Collapsed => _collapsed;

        /// <summary>b と長押しの状態を落とす（観測の累計は残す）。途中の長押しは数えない。</summary>
        public void Reset()
        {
            Value = 0f;
            _holding = false;
            _collapsed = false;
            _holdMax = 0f;
        }

        /// <summary>報告の成立。b を即 0 にし、この長押しを離すまで透けさせない。</summary>
        public void Collapse()
        {
            Value = 0f;
            _collapsed = true;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="progress01"/> は長押しの進み具合（押していない・受け付けていないなら 0）。
        /// <paramref name="armed"/> は「旗が立った演出の、人形が出ているカットか」。false なら b は即 0。
        /// </summary>
        public float Tick(float dt, float progress01, bool armed)
        {
            if (float.IsNaN(dt) || dt < 0f) dt = 0f;
            if (dt > MaxStepSec) dt = MaxStepSec;
            float x = float.IsNaN(progress01) ? 0f : (progress01 < 0f ? 0f : (progress01 > 1f ? 1f : progress01));
            bool pressing = armed && x > 0f;

            if (!armed) Value = 0f;
            else
            {
                float target = pressing && !_collapsed ? MaxBleed * x : 0f;
                Value += (target - Value) * (1f - (float)Math.Exp(-dt / TauSec));
                if (target <= 0f && Value < SnapEps) Value = 0f;
                if (Value > MaxBleed) Value = MaxBleed;
            }

            if (pressing)
            {
                if (!_holding)
                {
                    _holding = true;
                    _holdMax = 0f;
                }
                if (Value > _holdMax) _holdMax = Value;
            }
            else
            {
                if (_holding) EndHold();
                _collapsed = false;
            }
            return Value;
        }

        private void EndHold()
        {
            _holding = false;
            LastHoldMax = _holdMax;
            HoldCount++;
        }
    }
}
