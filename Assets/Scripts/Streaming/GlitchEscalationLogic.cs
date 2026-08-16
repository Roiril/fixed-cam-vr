#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>乱れは、起きるたびに大きくなる。序盤〜中盤は軽く、終盤で跳ねる。</b>
    /// （`canon/LEDGER.md` 0055）
    ///
    /// UnityEngine 非依存の純ロジック。配るのは <see cref="GlitchFx"/>（画）と
    /// <c>ShowSoundDirector</c>（音）で、<b>両方が同じ 1 つの進みを読む</b> —
    /// 別々に数えると「画は激しいのに音は同じ」が沈黙して起きる。
    ///
    /// ⚠ <b>著作した値を置き換えない。掛ける。</b> 台本が書いた強さ・尺の比は保つので、
    /// 弱く置いた切替の乱れは最後まで相対的に弱いまま（強弱の設計が壊れない）。
    ///
    /// ⚠ <b>数えるのは「起きた回数」であって周や経過時間ではない</b>（ユーザー指定）。
    /// 歩くのが速い体験者では乱れの回数も減るので、体験の長さに自然に追従する。
    /// </summary>
    public sealed class GlitchEscalationLogic
    {
        /// <summary>
        /// ここまで数えたら頭打ち。現行の台本は 1 ランでおよそ 16〜17 回起きる
        /// （著作 7 ＋ ゾーン切替 9〜10）ので、<b>終盤の 5 回ほどが最大</b>になる。
        /// </summary>
        public const int SaturateAtCount = 12;

        /// <summary>
        /// 曲線の指数。<b>3 = 序盤〜中盤はほとんど動かず、終盤で跳ねる</b>
        /// （2026-08-16 ユーザー指定「線形的に上がるのではなく、指数関数的に最後にかけて粗く」
        /// 「最初〜中盤はもっと軽くていい」）。
        ///
        /// 実際の進み: 4 回目 0.02 / 6 回目 0.09 / 9 回目 0.39 / 11 回目 0.75 / 12 回目 1.00。
        /// ⚠ <b>1 に戻すと線形</b>＝ユーザーが却下した形。下げるなら理由を書く。
        /// </summary>
        public const float Exponent = 3f;

        /// <summary>1 回目に強さへ掛かる倍率。<b>台本より軽い</b>（0.70 → 0.32）。</summary>
        public const float LevelAtFirst = 0.45f;

        /// <summary>最大時に強さへ掛かる倍率。台本の 0.70 が <see cref="MaxLevel"/> に届く値。</summary>
        public const float LevelAtMax = 1.19f;

        /// <summary>
        /// <b>強さの上限。</b>（2026-08-16 ユーザー指定「強さの上限も、0.83 にして」）
        ///
        /// ⚠ シェーダの天井（1.0）より手前で止める。全面が砂で埋まると
        /// <b>何が映っていたか分からなくなり、乱れではなく信号断に見える</b>。
        /// ⚠ <b>倍率だけでは守れない</b> — 台本の強さはカットごとに違う（0.25 / 0.70 / 0.80）ので、
        /// 同じ倍率を掛けると強い方が上限を超える。
        /// ⚠ <b>例外を作らない。</b> 一度「著作がこれを超えていたら著作を優先する」と書いたが、
        /// それは<b>上限に穴を開ける</b>だけだった（台本 0.59s が 0.537s まで伸びた）。
        /// いまの台本の最大は強さ 0.80 / 尺 0.50s で、どちらも上限を超えないので例外は要らない。
        /// </summary>
        public const float MaxLevel = 0.83f;

        /// <summary>1 回目に尺へ掛かる倍率（0.40s → 0.20s）。</summary>
        public const float HoldAtFirst = 0.50f;

        /// <summary>
        /// 最大時に尺へ掛かる倍率。台本の 0.40s がちょうど <see cref="MaxHoldSec"/> に届く値。
        /// 強さは 9 回目あたりで天井に着くので、<b>終盤の「粗さ」を実際に担うのはここ</b>。
        /// </summary>
        public const float HoldAtMax = 1.25f;

        /// <summary>
        /// <b>1 回の乱れが続く上限 (秒)。</b>（2026-08-16 ユーザー指定
        /// 「尺の上限は 0.5s にして」）
        ///
        /// ⚠ <b>倍率だけでは守れない。</b> 台本の尺はカットごとに違い（0.40s / 0.50s）、
        /// 同じ倍率を掛けると長い方が上限を超える。だから頭打ちが要る。
        /// ⚠ <b>例外を作らない</b>（<see cref="MaxLevel"/> の注記と同じ理由）。
        /// </summary>
        public const float MaxHoldSec = 0.50f;

        /// <summary>
        /// 1 回目の乱れの音量（最大時は 1.0）。⚠ <b>幅は 1.94 dB しかない。控えめなのは指定</b>
        /// （2026-08-16 ユーザー「音の上昇は深いじゃない程度に」）。
        /// 曲線は画と同じなので、<b>音も終盤まで上がらない</b>。
        ///
        /// ⚠⚠ <b>上へは伸ばせない。</b> <c>SfxPlayer.Play</c> は
        /// <c>Clamp01(gain × masterGain)</c> で、<c>masterGain</c> は 1.0。
        /// 1 を超える倍率は全部 1.0 に潰れて<b>音は 1 ビットも変わらない</b>。
        ///
        /// ⚠ 最大が 1.0 ＝ `rules/sound-design.md` §3 の「繰り返す一撃 -23 LUFS」に揃えた高さ。
        /// **終盤が設計どおりで、序盤がそこから 1.94 dB 低い**という関係。
        /// </summary>
        public const float SfxGainAtFirst = 0.80f;

        private int _count;

        /// <summary>このランで乱れが起きた回数。</summary>
        public int Count => _count;

        /// <summary>回数の進み（0..1・直線）。<b>掛かるのは <see cref="Curve01"/> の方</b>。</summary>
        public float Progress01
        {
            get
            {
                if (_count <= 1) return 0f;
                if (SaturateAtCount <= 1) return 1f;
                float p = (_count - 1) / (float)(SaturateAtCount - 1);
                return p > 1f ? 1f : p;
            }
        }

        /// <summary>
        /// 実際に掛かる進み（0..1）。<see cref="Progress01"/> を <see cref="Exponent"/> 乗したもの。
        /// <b>これが「いまどれだけ粗いか」</b>で、テレメトリの <c>glE</c> もこの値。
        /// </summary>
        public float Curve01
        {
            get
            {
                float p = Progress01;
                if (p <= 0f) return 0f;
                if (p >= 1f) return 1f;
                // 指数は const なので展開して書く（Mathf を持ち込まない ＝ 純ロジックのまま）。
                return p * p * p;
            }
        }

        /// <summary>体験 1 回ぶんの状態を落とす。冪等。</summary>
        public void ResetRun() => _count = 0;

        /// <summary>
        /// 乱れが 1 回起きた。<b>強さを計算する前に呼ぶ</b> —
        /// 呼んだ後の <see cref="Count"/> がその回の番号になる。
        /// </summary>
        public void Notify()
        {
            if (_count < int.MaxValue) _count++;
        }

        /// <summary>著作された強さに、いまの進みを掛ける。<see cref="MaxLevel"/> で頭打ち（例外なし）。</summary>
        public float ApplyLevel(float level)
        {
            if (level <= 0f) return 0f;
            float v = level * Lerp(LevelAtFirst, LevelAtMax, Curve01);
            return v > MaxLevel ? MaxLevel : v;
        }

        /// <summary>著作された尺に、いまの進みを掛ける。<see cref="MaxHoldSec"/> で頭打ち（例外なし）。</summary>
        public float ApplyHold(float sec)
        {
            if (sec <= 0f) return sec;
            float v = sec * Lerp(HoldAtFirst, HoldAtMax, Curve01);
            return v > MaxHoldSec ? MaxHoldSec : v;
        }

        /// <summary>
        /// 乱れの音に掛ける倍率。<see cref="SfxGainAtFirst"/> から 1.0 まで。
        /// ⚠ <b>1 を超えない</b>（超えても <c>SfxPlayer</c> が潰すので、超える値は嘘になる）。
        /// </summary>
        public float VolumeGain => Lerp(SfxGainAtFirst, 1f, Curve01);

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
