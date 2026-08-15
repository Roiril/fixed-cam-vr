#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>連絡の面の段。</summary>
    public enum CommsStage
    {
        /// <summary>出していない。</summary>
        Off,
        /// <summary>現れている途中。</summary>
        In,
        /// <summary>出し切って読ませている。</summary>
        Hold,
        /// <summary>引いている途中。</summary>
        Out,
    }

    /// <summary>連絡の面へ配る値（すべて 0..1）。</summary>
    public struct CommsWeights
    {
        /// <summary>地（受信票の面）の不透明度。</summary>
        public float panel;
        /// <summary>文字の不透明度。</summary>
        public float glyph;

        public static CommsWeights Hidden => new CommsWeights();
    }

    /// <summary>
    /// 上司からの連絡（第 2 の面）の状態機械。<b>UnityEngine 非依存・dt 注入</b>。
    ///
    /// 立ち位置は `canon/LEDGER.md` 0043 — <b>本編のスクリーンとは別の面</b>を、
    /// 少し手前・少し外側に立てる。「せっかく VR で立体的なので、スクリーンにつけなくていい」。
    ///
    /// ⚠ <b>本編の進行を既読待ちにしない。</b> 読まなくても体験は進む（時間で引く）。
    /// 既読の操作を作らないのは、体験者が持つ唯一の入力（左 X ＝ 記録）と兼用させないため —
    /// 兼用すると「記録した」と「読んだ」が混ざって、押した時刻の意味が濁る。
    /// </summary>
    public sealed class CommsPanelLogic
    {
        /// <summary>面が現れるまで (秒)。</summary>
        public const float InSec = 0.45f;

        /// <summary>読ませる時間 (秒)。<b>歩きながら読む</b>ので、短い 1 文でも余裕を取る。</summary>
        public const float HoldSec = 7f;

        /// <summary>引くまで (秒)。ぱっと消すと「消えた」ではなく「壊れた」に見える。</summary>
        public const float OutSec = 0.9f;

        /// <summary>文字が出るまでの遅れ (秒)。<b>面が先、文字が後</b>（受信してから表示される）。</summary>
        public const float GlyphDelaySec = 0.25f;

        private CommsStage _stage = CommsStage.Off;
        private float _elapsed;

        public CommsStage Stage => _stage;

        /// <summary>出ているか（実行体が面を描くべきか）。</summary>
        public bool Active => _stage != CommsStage.Off;

        /// <summary>連絡が届いた。<b>すでに出ていれば頭から出し直す</b>（重ねない）。</summary>
        public void Begin()
        {
            _stage = CommsStage.In;
            _elapsed = 0f;
        }

        /// <summary>畳む（ラン開始・本編を出た・中止）。</summary>
        public void Disable()
        {
            _stage = CommsStage.Off;
            _elapsed = 0f;
        }

        /// <summary>時間を進める。</summary>
        public void Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (_stage == CommsStage.Off) return;
            _elapsed += dt;
            switch (_stage)
            {
                case CommsStage.In:
                    if (_elapsed >= InSec) { _stage = CommsStage.Hold; _elapsed = 0f; }
                    break;
                case CommsStage.Hold:
                    if (_elapsed >= HoldSec) { _stage = CommsStage.Out; _elapsed = 0f; }
                    break;
                case CommsStage.Out:
                    if (_elapsed >= OutSec) Disable();
                    break;
            }
        }

        /// <summary>いまの段から面と文字へ配る値。<b>見え方の判断はすべてここ</b>。</summary>
        public CommsWeights Weights
        {
            get
            {
                switch (_stage)
                {
                    case CommsStage.In:
                    {
                        float p = Smooth(Clamp01(_elapsed / InSec));
                        float g = Clamp01((_elapsed - GlyphDelaySec) / System.Math.Max(InSec - GlyphDelaySec, 0.01f));
                        return new CommsWeights { panel = p, glyph = Smooth(g) };
                    }
                    case CommsStage.Hold:
                        return new CommsWeights { panel = 1f, glyph = 1f };
                    case CommsStage.Out:
                    {
                        float t = Smooth(Clamp01(_elapsed / OutSec));
                        // ⚠ **文字が先に消える。** 地より先に消えると「読み終わって畳まれた」に見える。
                        //    同時に消すと「電源が落ちた」に見えて、装置の不調と読まれる。
                        float g = Smooth(Clamp01(_elapsed / (OutSec * 0.6f)));
                        return new CommsWeights { panel = 1f - t, glyph = 1f - g };
                    }
                    default:
                        return CommsWeights.Hidden;
                }
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
