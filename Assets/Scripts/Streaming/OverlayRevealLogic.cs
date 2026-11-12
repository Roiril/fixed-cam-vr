#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 1 層目の差し替えが「滲んで現れる」ときの進み具合（<c>_OverlayReveal</c>・0349）の純判定。
    /// <see cref="ScreenOverlayController"/> が毎フレーム <see cref="Tick"/> し、値をシェーダへ書く。
    ///
    /// <code>
    ///   None ─BeginIn─▶ In ──(revealSec)──▶ Shown（0.985..1 で呼吸）
    ///                    │                    │
    ///                    └──── BeginOut ──────┴──▶ Out ──(outSec)──▶ Done（値 0）
    /// </code>
    ///
    /// シェーダは <c>a = saturate((reveal*(1+soft) - v) / soft)</c> で画素ごとに出す（v は順の画）。
    /// 引っ込みは入りの曲線を逆に辿るので、<b>最後に出た所（v の大きい所）から先に消える</b>。
    /// ⚠ 数値は <c>tools/gen-plate/bleed_preview.py</c> と同じ。片方だけ変えると試し描きが実機と食い違う
    ///   （<c>tools/test-tools.py</c> が突き合わせる）。
    /// </summary>
    public sealed class OverlayRevealTrack
    {
        public enum Phase { None, In, Shown, Out, Done }

        private float _delay;
        private float _sec;
        private float _outSec;
        private float _t;
        private float _outFrom;

        public Phase Current { get; private set; } = Phase.None;

        /// <summary>いまの <c>_OverlayReveal</c>（0..1）。</summary>
        public float Value { get; private set; }

        /// <summary>この cue で出た最大値（観測用。<see cref="BeginIn"/> で 0 へ戻る）。</summary>
        public float Max { get; private set; }

        /// <summary>引っ込みが終わったか（値 0 に着いた）。</summary>
        public bool OutFinished => Current == Phase.Done;

        /// <summary>出始める。delay の間は 0 のまま、sec かけて 1 へ。</summary>
        public void BeginIn(float delaySec, float sec)
        {
            _delay = Math.Max(0f, delaySec);
            _sec = Math.Max(0f, sec);
            _t = 0f;
            Value = 0f;
            Max = 0f;
            Current = Phase.In;
        }

        /// <summary>引っ込める。いまの値から outSec かけて 0 へ。出ていなければ即 Done。</summary>
        public void BeginOut(float outSec)
        {
            if (Current == Phase.None || Current == Phase.Done) { Current = Phase.Done; Value = 0f; return; }
            if (Current == Phase.Out) return;   // 引っ込み中に重ねて呼ばれても速さを変えない
            _outSec = Math.Max(0f, outSec);
            _outFrom = Value;
            _t = 0f;
            Current = Phase.Out;
            if (_outSec <= 0f || _outFrom <= 0f) { Current = Phase.Done; Value = 0f; }
        }

        /// <summary>止める（世代が替わった・中止）。値 0。</summary>
        public void Reset()
        {
            Current = Phase.None;
            Value = 0f;
            Max = 0f;
            _t = 0f;
        }

        public float Tick(float dt)
        {
            if (dt < 0f || float.IsNaN(dt)) dt = 0f;
            switch (Current)
            {
                case Phase.In:
                    _t += dt;
                    float v = OverlayRevealLogic.In(_t, _delay, _sec);
                    Value = v;
                    if (_t >= _delay + _sec) { Current = Phase.Shown; _t = 0f; Value = 1f; }
                    break;
                case Phase.Shown:
                    _t += dt;
                    Value = OverlayRevealLogic.Breath(_t);
                    break;
                case Phase.Out:
                    _t += dt;
                    Value = OverlayRevealLogic.Out(_outFrom, _t, _outSec);
                    if (_t >= _outSec) { Current = Phase.Done; Value = 0f; }
                    break;
            }
            if (Value > Max) Max = Value;
            return Value;
        }
    }

    /// <summary>滲みの式（時間 → <c>_OverlayReveal</c>）と、cue の値の解釈。</summary>
    public static class OverlayRevealLogic
    {
        /// <summary>引っ込みの既定秒（cue の revealOutSec を省略したとき）。</summary>
        public const float DefaultOutSec = 0.45f;

        /// <summary>報告が成立した瞬間にシミを引く秒（膨らみが畳まれ、人形が覗くあいだ）。</summary>
        public const float MarkDoneOutSec = 0.25f;

        /// <summary>前線のぼかし幅（<c>_OverlayRevealSoft</c> の既定）。</summary>
        public const float DefaultSoft = 0.06f;

        /// <summary>進み具合の曲線 1-(1-t)^2.2。出始めが速く、終わりへ向かって落ち着く。</summary>
        public const float CurvePow = 2.2f;

        /// <summary>出切った後の呼吸の下端（上端は 1）と周期。目に止まらない程度。</summary>
        public const float BreathMin = 0.985f;
        public const float BreathPeriodSec = 6f;

        /// <summary>cue の blend が掛け算か（"multiply" だけ。既定・空・未知は差し替え）。</summary>
        public static bool IsMultiplyBlend(string? blend)
            => string.Equals(blend, "multiply", StringComparison.OrdinalIgnoreCase);

        public static float In(float elapsed, float delaySec, float sec)
        {
            if (elapsed < delaySec) return 0f;
            if (sec <= 0f) return 1f;
            float x = (elapsed - delaySec) / sec;
            if (x >= 1f) return 1f;
            return 1f - (float)Math.Pow(1f - x, CurvePow);
        }

        /// <summary>出切ってからの経過秒 → 値。1 から始まり BreathMin まで沈んで戻る。</summary>
        public static float Breath(float sinceFull)
        {
            double k = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * sinceFull / BreathPeriodSec);
            return 1f - (1f - BreathMin) * (float)k;
        }

        /// <summary>引っ込み。from から outSec かけて 0 へ（入りの曲線の裏返し）。</summary>
        public static float Out(float from, float elapsed, float outSec)
        {
            if (outSec <= 0f) return 0f;
            float u = elapsed / outSec;
            if (u >= 1f) return 0f;
            if (u <= 0f) return from;
            return from * (float)Math.Pow(1f - u, CurvePow);
        }
    }
}
