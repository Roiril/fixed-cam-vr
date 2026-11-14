#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 3 周目の長押しの間だけ、人形の下からライブの自分が透ける量 b（<c>_LiveBleed</c>）の純判定。
    /// 台本の旗 <see cref="ShowTakeDef.markBleed"/> が立った演出の、人形が出ているカット（無人の写真＋CG 人形）でだけ働く。
    /// 透けるのは体の所だけで、その重み（ライブと素材の差）はシェーダが画素ごとに掛ける。ここが決めるのは全体の量 b。
    ///
    /// 押している間は目標 = <see cref="MaxBleed"/> × s(x)。s は進み具合 x を <see cref="FullAtProgress"/> で 1 に届かせる
    /// smoothstep（進み 6 割で最大）。離したら目標 0。
    /// 値は時定数 <see cref="TauSec"/> の指数で目標へ寄る（行き過ぎない ＝ 0..<see cref="MaxBleed"/> の外へ出ない）。
    /// 報告が成立したら <see cref="Collapse"/> でその時点の値を <see cref="HoldAfterSec"/> のあいだ保ち（押していても離しても）、
    /// 過ぎたら即 0。以後、その長押しを離すまで 0 のまま（押し直せばまた透ける）。
    /// 働かない間（<c>armed=false</c>）は即 0 で、保っている途中でも捨てる。目標 0 で <see cref="SnapEps"/> を割ったら 0 に揃える
    /// （シェーダへ正確な 0 を書く ＝ 旗の無い走行の画を 1 画素も変えない）。
    ///
    /// 観測: 働いていた長押しが終わるたび（離した・働かなくなった）に <see cref="HoldCount"/> が 1 増え、
    /// その長押しで出た最大を <see cref="LastHoldMax"/> に残す。どちらも <see cref="Reset"/> では戻さない（増分で読む）。
    /// 成立の後に保っている間も 1 回の長押しは 1 回と数える（保っている値は成立の前に出た値なので、最大は変わらない）。
    /// </summary>
    public sealed class LiveBleedLogic
    {
        public const float MaxBleed = 0.85f;
        public const float FullAtProgress = 0.6f;
        public const float TauSec = 0.12f;
        public const float HoldAfterSec = 0.5f;
        public const float MaxStepSec = 0.25f;
        public const float SnapEps = 1e-4f;

        private bool _holding;
        private bool _collapsed;
        private float _holdMax;
        // 成立の後に値を保つ残りの秒。0 より大きい間は Value を動かさない。
        private float _holdAfterLeft;

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

        /// <summary>報告の成立の後、値を保っている最中か。</summary>
        public bool HoldingAfterCollapse => _holdAfterLeft > 0f;

        /// <summary>押している間の目標 b。進み具合 x（0..1）を <see cref="FullAtProgress"/> で 1 に届く smoothstep に通す。</summary>
        public static float TargetFor(float progress01)
        {
            float t = progress01 / FullAtProgress;
            if (float.IsNaN(t) || t <= 0f) return 0f;
            if (t >= 1f) return MaxBleed;
            return MaxBleed * t * t * (3f - 2f * t);
        }

        /// <summary>b と長押しの状態を落とす（観測の累計は残す）。途中の長押しは数えない。保っている値も捨てる。</summary>
        public void Reset()
        {
            Value = 0f;
            _holding = false;
            _collapsed = false;
            _holdMax = 0f;
            _holdAfterLeft = 0f;
        }

        /// <summary>
        /// 報告の成立。いまの b を <see cref="HoldAfterSec"/> のあいだ保ち、過ぎたら 0 にする。
        /// この長押しを離すまで、保った後は透けさせない。
        /// </summary>
        public void Collapse()
        {
            _collapsed = true;
            _holdAfterLeft = Value > 0f ? HoldAfterSec : 0f;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="progress01"/> は長押しの進み具合（押していない・受け付けていないなら 0）。
        /// <paramref name="armed"/> は「旗が立った演出の、人形が出ているカットか」。false なら b は即 0（保っている値も捨てる）。
        /// </summary>
        public float Tick(float dt, float progress01, bool armed)
        {
            if (float.IsNaN(dt) || dt < 0f) dt = 0f;
            if (dt > MaxStepSec) dt = MaxStepSec;
            float x = float.IsNaN(progress01) ? 0f : (progress01 < 0f ? 0f : (progress01 > 1f ? 1f : progress01));
            bool pressing = armed && x > 0f;

            if (!armed)
            {
                Value = 0f;
                _holdAfterLeft = 0f;
            }
            else if (_holdAfterLeft > 0f)
            {
                // 成立の後に保っている間は、押していても離しても値を動かさない。過ぎたら即 0。
                _holdAfterLeft -= dt;
                if (_holdAfterLeft <= 0f)
                {
                    _holdAfterLeft = 0f;
                    Value = 0f;
                }
            }
            else
            {
                float target = pressing && !_collapsed ? TargetFor(x) : 0f;
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
                // 保っている値は成立の前に出た値。保っている間に押し直した長押しの最大へは入れない。
                if (_holdAfterLeft <= 0f && Value > _holdMax) _holdMax = Value;
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
