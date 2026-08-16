#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>乱れは、起きるたびに少しずつ大きくなる。</b>（`canon/LEDGER.md` 0055）
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
        /// ⚠ 小さくしすぎると序盤で天井に張り付いて「少しずつ」が消える。
        /// </summary>
        public const int SaturateAtCount = 12;

        /// <summary>最大時に強さへ掛かる増分（×1.60）。⚠ 1.0 で頭打ちする。</summary>
        public const float LevelGainAtMax = 0.60f;

        /// <summary>最大時に尺へ掛かる増分（×2.00）。**強さは天井があるが、長さには無い。**</summary>
        public const float HoldGainAtMax = 1.00f;

        /// <summary>
        /// 1 回目の乱れの音量（最大時は 1.0）。⚠ <b>幅は 1.94 dB しかない。控えめなのは指定</b>
        /// （2026-08-16 ユーザー「音の上昇は深いじゃない程度に」）。
        ///
        /// ⚠⚠ <b>上へは伸ばせない。</b> <c>SfxPlayer.Play</c> は
        /// <c>Clamp01(gain × masterGain)</c> で、<c>masterGain</c> は 1.0。
        /// 1 を超える倍率は全部 1.0 に潰れて<b>音は 1 ビットも変わらない</b>。
        /// だから天井の内側で下から上げる形にしてある。
        ///
        /// ⚠ 最大が 1.0 ＝ `rules/sound-design.md` §3 の「繰り返す一撃 -23 LUFS」に揃えた高さ。
        /// **終盤が設計どおりで、序盤がそこから 1.94 dB 低い**という関係。
        /// </summary>
        public const float SfxGainAtFirst = 0.80f;

        private int _count;

        /// <summary>このランで乱れが起きた回数。</summary>
        public int Count => _count;

        /// <summary>
        /// 進み（0..1）。<b>1 回目は 0</b>（＝著作どおり）で、<see cref="SaturateAtCount"/> 回目に 1。
        /// 直線で伸ばすのは、1 回ごとの差を「少しずつ」に保つため
        /// （終盤だけ跳ねる曲線にすると、途中まで何も変わらないように見える）。
        /// </summary>
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

        /// <summary>体験 1 回ぶんの状態を落とす。冪等。</summary>
        public void ResetRun() => _count = 0;

        /// <summary>
        /// 乱れが 1 回起きた。<b>強さを計算する前に呼ぶ</b> —
        /// 呼んだ後の <see cref="Count"/> がその回の番号になる（1 回目は据え置き）。
        /// </summary>
        public void Notify()
        {
            if (_count < int.MaxValue) _count++;
        }

        /// <summary>著作された強さに、いまの進みを掛ける（0..1 で頭打ち）。</summary>
        public float ApplyLevel(float level)
        {
            float v = level * (1f + LevelGainAtMax * Progress01);
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        /// <summary>著作された尺に、いまの進みを掛ける（頭打ちなし）。</summary>
        public float ApplyHold(float sec)
        {
            if (sec <= 0f) return sec;
            return sec * (1f + HoldGainAtMax * Progress01);
        }

        /// <summary>
        /// 乱れの音に掛ける倍率。<see cref="SfxGainAtFirst"/> から 1.0 まで。
        /// ⚠ <b>1 を超えない</b>（超えても <c>SfxPlayer</c> が潰すので、超える値は嘘になる）。
        /// </summary>
        public float VolumeGain => SfxGainAtFirst + (1f - SfxGainAtFirst) * Progress01;
    }
}
