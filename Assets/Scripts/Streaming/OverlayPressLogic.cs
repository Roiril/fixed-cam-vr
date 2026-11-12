#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 長押しで布が膨らむ量 p（<c>_OverlayPress</c>・0349）の純判定。減衰振動するバネ。
    ///
    /// 押している間は目標 = smoothstep(長押しの進み具合)、離したら目標 0。
    /// 固有振動数 2.4Hz・減衰比 0.38 なので、離すと 0 を少し越えて沈み（布が引っ込む）、揺れて止まる。
    /// p は −0.15..1.1 に収める。報告が成立したら <see cref="Collapse"/> でバネを使わず 0.12 秒で直線に畳む。
    ///
    /// ⚠ フレームの dt は <see cref="MaxStepSec"/> で切り、<see cref="SubstepSec"/> 以下の小刻みで積分する
    ///   （半陰的オイラー）。粗い dt（起動直後・復帰直後・固定刻みの検証）で発散させないため。
    /// ⚠ 数値は <c>tools/gen-plate/bleed_preview.py</c> の PressSpring と同じ（<c>tools/test-tools.py</c> が突き合わせる）。
    /// </summary>
    public sealed class OverlayPressLogic
    {
        public const float NaturalHz = 2.4f;
        public const float DampingRatio = 0.38f;
        public const float Min = -0.15f;
        public const float Max = 1.1f;
        public const float SubstepSec = 1f / 120f;
        public const float MaxStepSec = 0.25f;
        public const float CollapseSec = 0.12f;

        private float _vel;
        private bool _collapsing;
        private float _collapseFrom;
        private float _collapseT;

        /// <summary>いまの p（シェーダへ書く値）。</summary>
        public float P { get; private set; }

        /// <summary><see cref="Reset"/> からの p の最大（観測用）。</summary>
        public float Peak { get; private set; }

        /// <summary>畳み中か（<see cref="Collapse"/> 後、0 に着くまで）。</summary>
        public bool Collapsing => _collapsing;

        /// <summary>畳み終わったか（<see cref="Collapse"/> 後に 0 へ着いた）。</summary>
        public bool Collapsed { get; private set; }

        public void Reset()
        {
            P = 0f;
            _vel = 0f;
            Peak = 0f;
            _collapsing = false;
            Collapsed = false;
            _collapseT = 0f;
        }

        /// <summary>報告の成立。バネを使わず、いまの p から <see cref="CollapseSec"/> で直線に 0 へ。</summary>
        public void Collapse()
        {
            if (_collapsing || Collapsed) return;
            _collapsing = true;
            _collapseFrom = P;
            _collapseT = 0f;
            _vel = 0f;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="progress01"/> は長押しの進み具合（押していない・受け付けていないなら 0）。
        /// </summary>
        public float Tick(float dt, float progress01)
        {
            if (float.IsNaN(dt) || dt < 0f) dt = 0f;
            if (dt > MaxStepSec) dt = MaxStepSec;
            if (_collapsing)
            {
                _collapseT += dt;
                float u = _collapseT / CollapseSec;
                if (u >= 1f)
                {
                    P = 0f;
                    _collapsing = false;
                    Collapsed = true;
                }
                else P = _collapseFrom * (1f - u);
                return P;
            }
            if (Collapsed) return P;

            float x = float.IsNaN(progress01) ? 0f : (progress01 < 0f ? 0f : (progress01 > 1f ? 1f : progress01));
            float target = x * x * (3f - 2f * x);
            float w = 2f * (float)Math.PI * NaturalHz;
            int n = Math.Max(1, (int)Math.Ceiling(dt / SubstepSec));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                float acc = w * w * (target - P) - 2f * DampingRatio * w * _vel;
                _vel += acc * h;
                P += _vel * h;
                if (P < Min || P > Max)
                {
                    P = P < Min ? Min : Max;
                    _vel = 0f;
                }
            }
            if (P > Peak) Peak = P;
            return P;
        }
    }
}
