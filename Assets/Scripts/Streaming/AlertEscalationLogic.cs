#nullable enable

using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>人形視点の切替に乗る警告音は、回を重ねるごとに大きくなる。</b>
    /// （2026-09-04・<c>canon/LEDGER.md</c> 0145・ユーザー逐語
    /// 「人形視点のときの、通常のカメラ切り替え音+警告音だけど、警告音を、
    /// 4回数を重ねるにつれて大きくなるようにしてほしい」）
    ///
    /// UnityEngine 非依存の純ロジック。配るのは <see cref="SwitchAudioCue"/> 1 か所。
    ///
    /// ⚠⚠ <b>大きくなるのは警告音だけ。土台の切替音は 1 ビットも動かない</b>
    /// （0106 / 0134 の「あくまで通常の切り替え音がメイン」）。だから焼く側で
    /// <c>sfx_switch_warn</c>（警告だけ・頭に 35ms の無音つき）を別ファイルにしてある。
    /// **混ぜた 1 本（<c>sfx_switch_alert_*</c>）の音量を動かすと土台ごと大きくなる。**
    ///
    /// ⚠⚠ <b>上へは伸ばせない。</b> <c>AudioSource.volume</c> は 1.0 が天井なので、
    /// 焼いてある高さ（-28.3 LUFS ＝ 0134 が実機で測って決めた値）が<b>最大</b>。
    /// ⇒ <b>下から上がって、最後にちょうど 0dB</b>。天井を上げたいなら焼き直す
    /// （<c>ingest-sounds.py</c> の <c>MIXES</c> の <c>rel</c>）。1 を超える倍率を返しても
    /// 潰れて何も変わらないのに、コードは「大きくした」つもりになる（乱れの音で踏んだ型）。
    /// </summary>
    public sealed class AlertEscalationLogic
    {
        /// <summary>
        /// ここまで数えたら頭打ち（＝ ちょうど 0dB）。
        ///
        /// いまの著作は <b>6 発</b>（2 周目 B のバックルームズ 1 ＋ 2 周目 C の人形視点 5）なので、
        /// <b>最後の 1 発が最大</b>になる。ユーザーが「4回」と言っているのは
        /// 人形視点の連なり（<c>pov_1..pov_4</c>）のことで、そこは 3 発目〜6 発目に当たる。
        ///
        /// ⚠ <b>著作の数と対で決める値。</b> カットを増やしたら早く頭打ちになるだけで壊れないが、
        /// 「最後が最大」を保ちたいならここも直す（走行の <c>swAlert</c> が実際の数を出す）。
        /// </summary>
        public const int RampToCount = 6;

        /// <summary>
        /// 1 発目の倍率（dB）。<b>-8dB</b>。
        ///
        /// ⚠ 下げすぎると 1 発目が<b>聞こえない</b>。警告だけの高さは -28.3 LUFS で、
        /// 下に敷いてある劇伴が -27.7 LUFS。-8dB は -36.3 LUFS ＝ 0134 が
        /// 「実機で他と同じに聞こえる」と否定した旧版とほぼ同じ高さになる。
        /// ⭐ <b>それでよい</b> — 1 発目は「まだほとんど無い」が狙いで、聞こえるのは終盤。
        /// ⚠ 「1 発目から聞こえてほしい」と言われたら -4 へ（幅が半分になる）。
        /// </summary>
        public const float FirstDb = -8f;

        /// <summary>この走行で警告つきが鳴った回数。</summary>
        public int Count { get; private set; }

        /// <summary>直前に使った倍率（テレメトリ用。まだ鳴っていなければ 0）。</summary>
        public float LastGain { get; private set; }

        /// <summary>
        /// ラン開始で落とす。
        /// ⚠⚠ <b>落とさないと 2 人目以降は最初から最大で鳴る</b>（画にも録画にも出ない）。
        /// </summary>
        public void ResetRun()
        {
            Count = 0;
            LastGain = 0f;
        }

        /// <summary>1 回数えて、その回に掛ける倍率を返す。</summary>
        public float Next()
        {
            Count++;
            LastGain = GainFor(Count);
            return LastGain;
        }

        /// <summary>
        /// <paramref name="nth"/> 発目（1 始まり）の倍率。<b>dB で直線</b>。
        ///
        /// ⚠ 乱れの育ち方（<see cref="GlitchEscalationLogic"/>）は 3 乗で「終盤に跳ねる」形だが、
        /// これは<b>直線</b>。ユーザーの言葉が「重ねるにつれて大きくなる」で、
        /// 6 発しかないので跳ねさせると前半が全部同じに聞こえるため
        /// （曲線の選択は<b>シュビーの判断</b>。赤入れが来たら指数を足す）。
        ///
        /// 実際の値: 1 発目 -8.0 / 2 -6.4 / 3 -4.8 / 4 -3.2 / 5 -1.6 / 6 以降 0.0 dB。
        /// </summary>
        public static float GainFor(int nth)
        {
            if (nth <= 1) return DbToLin(FirstDb);
            if (nth >= RampToCount) return 1f;
            float t = (nth - 1) / (float)(RampToCount - 1);   // 0..1
            return DbToLin(FirstDb * (1f - t));
        }

        /// <summary>dB → 倍率。<b>1 を超えない</b>（天井の話は class の但し書き）。</summary>
        private static float DbToLin(float db)
        {
            float g = (float)Math.Pow(10.0, db / 20.0);
            return g < 0f ? 0f : (g > 1f ? 1f : g);
        }
    }
}
