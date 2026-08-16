#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>連絡の面の段。</summary>
    public enum CommsStage
    {
        /// <summary>出していない。</summary>
        Off,
        /// <summary>枠が左から右へ開いている途中。</summary>
        In,
        /// <summary>文字が 1 字ずつ打たれている途中。</summary>
        Type,
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
        /// <summary>文字の不透明度。<b>何文字出ているかは <see cref="reveal"/> が持つ。</b></summary>
        public float glyph;
        /// <summary>枠の開き。0 = 左端に畳まれている / 1 = 開き切り。</summary>
        public float open;
        /// <summary>文字の出た割合（0 = 1 字も出ていない / 1 = 全部出た）。</summary>
        public float reveal;

        public static CommsWeights Hidden => new CommsWeights();
    }

    /// <summary>
    /// 上司からの連絡（第 2 の面）の状態機械。<b>UnityEngine 非依存・dt 注入</b>。
    ///
    /// 立ち位置は `canon/LEDGER.md` 0043 — <b>本編のスクリーンとは別の面</b>を、
    /// 少し手前・少し外側に立てる。「せっかく VR で立体的なので、スクリーンにつけなくていい」。
    ///
    /// 出方は `canon/LEDGER.md` 0053（2026-08-16）:
    /// <b>枠が左端から右へ開き、開き切ってから文字が 1 字ずつ打たれる。</b>
    /// 引くときは逆で、文字が消えてから枠が左へ畳まれる。
    /// 装置が受信して、印字して、片づける — という順序がそのまま画になる。
    ///
    /// ⚠ <b>本編の進行を既読待ちにしない。</b> 読まなくても体験は進む（時間で引く）。
    /// 既読の操作を作らないのは、体験者が持つ唯一の入力（左 X ＝ 記録）と兼用させないため —
    /// 兼用すると「記録した」と「読んだ」が混ざって、押した時刻の意味が濁る。
    /// </summary>
    public sealed class CommsPanelLogic
    {
        /// <summary>枠が開き切るまで (秒)。</summary>
        public const float InSec = 0.45f;

        /// <summary>
        /// 1 秒あたり何文字打つか。速すぎると「一気に出た」に見え、遅いと読み終わる前に焦れる。
        ///
        /// ⚠⚠ <b>22 → 12 に落とした</b>（2026-08-16・<c>canon/LEDGER.md</c> 0056）。
        /// 1 文字 = 1 発の打鍵音を足したので、<b>この値がそのまま打鍵の間隔になる</b>。
        /// 22 文字/秒 ＝ 45ms 間隔では 1 発ずつが分かれて聞こえず、カタカタではなく
        /// 連続音になる（人が個々の打鍵を分けて聞けるのは 15〜20 発/秒あたりまで）。
        /// 12 ＝ 83ms は、実物のテレタイプ（10 文字/秒）と、
        /// もらった素材の押し込み→戻りの間隔（実測 80ms）の両方に近い。
        /// ⚠ 音の側は <see cref="CommsPanelLogic"/> の刻みを**そのまま数える**ので、
        /// ここを変えると打鍵の密度も一緒に変わる（対で直す必要は無い ＝ ずれようがない）。
        /// </summary>
        public const float CharsPerSec = 12f;

        /// <summary>打ち終わるまでの下限・上限 (秒)。文面が伸びても間延びさせない。</summary>
        public const float MinTypeSec = 0.15f;
        public const float MaxTypeSec = 2.5f;

        /// <summary>読ませる時間 (秒)。<b>打ち終わってから</b>数える。歩きながら読むので余裕を取る。</summary>
        public const float HoldSec = 7f;

        /// <summary>引くまで (秒)。ぱっと消すと「消えた」ではなく「壊れた」に見える。</summary>
        public const float OutSec = 0.9f;

        /// <summary>
        /// 引くとき、文字が消え切るまで（<see cref="OutSec"/> に対する割合）。
        /// <b>枠が畳まれ始めるより先に消え切る</b> — 畳む枠から文字がはみ出さない。
        /// </summary>
        public const float GlyphOutAt = 0.35f;

        /// <summary>引くとき、枠が畳まれ始める時点（<see cref="OutSec"/> に対する割合）。</summary>
        public const float FoldStartAt = 0.35f;

        /// <summary>地の濃さが乗り切る時点（<see cref="InSec"/> に対する割合）。開き切る前に濃さは決まる。</summary>
        public const float PanelInkAt = 0.35f;

        private CommsStage _stage = CommsStage.Off;
        private float _elapsed;
        private float _typeSec = MinTypeSec;

        public CommsStage Stage => _stage;

        /// <summary>出ているか（実行体が面を描くべきか）。</summary>
        public bool Active => _stage != CommsStage.Off;

        /// <summary>打ち終わるまでの秒（この文面での実測値。プレビューと卓が読む）。</summary>
        public float TypeSec => _typeSec;

        /// <summary>
        /// 連絡が届いた。<b>すでに出ていれば頭から出し直す</b>（重ねない）。
        /// </summary>
        /// <param name="charCount">
        /// 打つ文字数。<b>尺はここから決まる</b>（文面が伸びれば打つ時間も伸びる）。
        /// 0 以下なら文字の段を飛ばす。
        /// </param>
        public void Begin(int charCount)
        {
            _stage = CommsStage.In;
            _elapsed = 0f;
            _typeSec = charCount <= 0
                ? 0f
                : Clamp(charCount / CharsPerSec, MinTypeSec, MaxTypeSec);
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
                    if (_elapsed >= InSec) { _stage = CommsStage.Type; _elapsed = 0f; }
                    break;
                case CommsStage.Type:
                    if (_elapsed >= _typeSec) { _stage = CommsStage.Hold; _elapsed = 0f; }
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
                        // 枠は左端から右へ開く。⚠ **地の濃さは先に決まる**（開きながら明るくなると
                        //    「2 つのことが起きている」に見える。動いているのは幅だけにする）。
                        float k = Clamp01(_elapsed / InSec);
                        return new CommsWeights
                        {
                            panel = Smooth(Clamp01(k / PanelInkAt)),
                            glyph = 1f,
                            open = Smooth(k),
                            reveal = 0f,
                        };
                    }
                    case CommsStage.Type:
                    {
                        // ⚠ **打つところは滑らかにしない。** ここを smoothstep で均すと
                        //    打鍵の間隔が伸び縮みして「機械が打っている」に見えない。
                        float p = _typeSec <= 0f ? 1f : Clamp01(_elapsed / _typeSec);
                        return new CommsWeights { panel = 1f, glyph = 1f, open = 1f, reveal = p };
                    }
                    case CommsStage.Hold:
                        return new CommsWeights { panel = 1f, glyph = 1f, open = 1f, reveal = 1f };
                    case CommsStage.Out:
                    {
                        float t = Clamp01(_elapsed / OutSec);
                        // ⚠ **文字が先に消えてから枠が畳まれる。** 逆にすると、畳む枠から
                        //    文字がはみ出して「潰された」に見える。
                        float g = Smooth(Clamp01(t / GlyphOutAt));
                        float fold = Smooth(Clamp01((t - FoldStartAt) / (1f - FoldStartAt)));
                        return new CommsWeights
                        {
                            panel = 1f,
                            glyph = 1f - g,
                            open = 1f - fold,
                            reveal = 1f,
                        };
                    }
                    default:
                        return CommsWeights.Hidden;
                }
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
