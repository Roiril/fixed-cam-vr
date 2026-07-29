#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 「映像の乱れ」の時間包絡（UnityEngine 非依存・dt 注入）。
    ///
    /// 企画書 2.3 は乱れを 2 通りに使う: <b>差し替えの継ぎ目を隠す</b>（遷移のあいだ持続させる）と、
    /// <b>体験者の注意・移動を誘導する</b>（単発で走らせる）。前者を <see cref="SetSustain"/>、
    /// 後者を <see cref="Pulse"/> で受け、**両者を max で合成**する。
    ///
    /// max にするのは、遷移中に単発が重なっても乱れが二重に濃くならないようにするため
    /// （加算にすると 1 を超えて飽和し、遷移の形が潰れる）。
    /// </summary>
    public sealed class GlitchEnvelopeLogic
    {
        /// <summary>単発の立ち上がり (秒)。短くしないと「注意を引く」役に立たない。</summary>
        public const float AttackSec = 0.02f;

        /// <summary>単発の減衰 (秒)。切ると機械的な矩形に見えるので必ず残す。</summary>
        public const float ReleaseSec = 0.14f;

        /// <summary>同時に走る単発の上限。超えたら一番弱いものを置き換える。</summary>
        public const int MaxPulses = 4;

        private struct PulseState
        {
            public bool Active;
            public float Level;
            public float HoldSec;
            public float Elapsed;
        }

        private readonly PulseState[] _pulses = new PulseState[MaxPulses];
        private float _sustain;
        private float _level;

        /// <summary>現在の乱れ強度 (0-1)。</summary>
        public float Level => _level;

        /// <summary>持続成分（遷移中など）。0 で解除。</summary>
        public void SetSustain(float level)
        {
            _sustain = Clamp01(level);
        }

        /// <summary>単発の乱れを 1 つ足す。<paramref name="sec"/> は最大強度を保つ時間。</summary>
        public void Pulse(float level, float sec)
        {
            float lv = Clamp01(level);
            if (lv <= 0.001f) return;

            int slot = -1;
            float weakest = float.MaxValue;
            for (int i = 0; i < _pulses.Length; i++)
            {
                if (!_pulses[i].Active) { slot = i; break; }
                if (_pulses[i].Level < weakest) { weakest = _pulses[i].Level; slot = i; }
            }
            if (slot < 0) return;
            // 空きが無く、既存の方が強いなら新しい方を捨てる（強い乱れを弱いもので上書きしない）。
            if (_pulses[slot].Active && _pulses[slot].Level >= lv) return;

            _pulses[slot] = new PulseState
            {
                Active = true,
                Level = lv,
                HoldSec = sec > 0f ? sec : 0f,
                Elapsed = 0f,
            };
        }

        /// <summary>時間を進めて現在強度を返す。</summary>
        public float Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            float max = _sustain;
            for (int i = 0; i < _pulses.Length; i++)
            {
                if (!_pulses[i].Active) continue;
                _pulses[i].Elapsed += dt;
                float v = Envelope(_pulses[i]);
                if (v <= 0f)
                {
                    _pulses[i] = default;
                    continue;
                }
                if (v > max) max = v;
            }
            _level = Clamp01(max);
            return _level;
        }

        /// <summary>すべて畳む（ラン開始・無効化）。</summary>
        public void Reset()
        {
            Array.Clear(_pulses, 0, _pulses.Length);
            _sustain = 0f;
            _level = 0f;
        }

        private static float Envelope(in PulseState p)
        {
            float t = p.Elapsed;
            if (t < AttackSec) return p.Level * (AttackSec > 0f ? t / AttackSec : 1f);
            float holdEnd = AttackSec + p.HoldSec;
            if (t < holdEnd) return p.Level;
            float rel = t - holdEnd;
            if (rel >= ReleaseSec) return 0f;
            return p.Level * (1f - rel / ReleaseSec);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
