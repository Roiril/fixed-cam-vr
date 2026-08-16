#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>連絡の面が、周を重ねるごとに壊れていく。</b>UnityEngine 非依存・時刻注入。
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0068（ユーザー逐語）:
    /// 「周回を重ねるごとに、大きい方のカメラが映ってるスクリーンの画質が粗くなるじゃないですか。
    /// それに合わせて、AIエージェントのスクリーンもバグるような演出を徐々につけていってほしい。
    /// 3 周目 A で演出最大に」。
    ///
    /// ⚠⚠ <b>進みは映像とまったく同じものを読む</b>（<see cref="ScreenDecayLogic.Progress"/> ＝
    /// <c>ShowRunDirector.ScreenDecay</c>）。あれは<b>最後の周へ入った瞬間に 1.0 へ着いて以後動かない</b>
    /// （`canon/LEDGER.md` 0013）ので、「3 周目 A で最大」は<b>別の曲線を書かずにそのまま満たされる</b>。
    /// 独自の周回カウンタを持たせない — 2 つ持つと、片方だけ直したときに黙って食い違う。
    ///
    /// <b>層は 2 つ</b>:
    ///   1. <b>常時</b> — 走査線と微かな滲み。<see cref="LevelFor"/> に比例して濃くなる
    ///   2. <b>発作</b> — 矩形のブロックと帯が一気に出て、面が横へ飛ぶ。
    ///      <see cref="BurstTickSec"/> ごとに起きるか決まり、確率と振れ幅が進みで増える
    ///
    /// ⚠ <b>乱数を使わない。</b> 刻み番号のハッシュで決めるので、同じ版・同じ時刻は同じ絵になる
    /// （<c>OutroLogic.FlickerPower</c> と同じ流儀）。走行を並べて比べられなくなるのを防ぐ。
    ///
    /// ⚠⚠ <b>文字は最後まで読める側へ倒す。</b> ③「異常が検出されました。記録してください。」は
    /// <b>4 周目 A の締め</b>（進み 1.0）で出て、<b>読まれないと締めのカットが進まない</b>
    /// （体験者が押すまで待っている）。だから被覆にも横飛びにも上限がある。
    /// </summary>
    public sealed class CommsGlitchLogic
    {
        /// <summary>
        /// 進み → グリッチの強さ の曲線の指数。<b>2 ＝ 序盤は軽く、後半で効く</b>。
        ///
        /// ⚠ <b>1（線形）にしない。</b> 線形だと 1 周目の途中から目に見え始めるが、
        /// そのとき映像はまだ 1 周目の頭（<c>ScreenDecayLogic</c> のブロックは 800 ＝ ほとんど無変化）で、
        /// <b>連絡の面だけが先に壊れる</b>。同じ装置なのに壊れ方の足並みが揃わない。
        ///
        /// 実際の進み: 2 周目の頭 0.11 / 中ほど 0.25 / 2 周目の終わり 0.44 / <b>3 周目 A 以降 1.00</b>。
        ///
        /// ⚠ 乱れの育ち（<see cref="GlitchEscalationLogic.Exponent"/> = 3）とは<b>別の軸</b>。
        /// あちらは「起きた回数」、こちらは「周の進み」。値を揃える理由は無い。
        /// </summary>
        public const float Exponent = 2f;

        /// <summary>発作を起こすか決める刻み (秒)。短いとちらつき、長いと「たまに」が粗くなる。</summary>
        public const float BurstTickSec = 0.22f;

        /// <summary>発作が起きる確率の上限（進み 1.0 のとき）。<b>常時ではない</b>ことを守る値。</summary>
        public const float MaxBurstChance = 0.34f;

        /// <summary>
        /// 面が横へ飛ぶ幅の上限 (m)。面の幅は 0.76m なので <b>2.6%</b>。
        /// ⚠ これ以上にすると、飛んでいるあいだ文字が追えなくなる（読める側へ倒す）。
        /// </summary>
        public const float MaxOffsetM = 0.02f;

        /// <summary>これ以下は「何も起きていない」（＝ 1 画素も変わらない）。</summary>
        public const float OffThreshold = 0.002f;

        /// <summary>
        /// 周回の進み 0..1 → グリッチの強さ 0..1。<b>シェーダへ渡すのはこの 1 本だけ</b>
        /// （被覆率・帯の数・色はシェーダが強さから解く。テレメトリに出すのは強さなので、
        /// <c>ScreenDecayLogic.BlocksFor</c> のように対応表を C# へ持つ必要が無い）。
        /// </summary>
        public static float LevelFor(float decayProgress)
        {
            float p = Clamp01(decayProgress);
            if (p <= 0f) return 0f;
            float lv = Pow(p, Exponent);
            return lv <= OffThreshold ? 0f : lv;
        }

        /// <summary>いまの刻み番号。<b>発作の抽選も種もここから引く</b>ので、時刻の写し先はここ 1 つ。</summary>
        public static int TickAt(float timeSec)
        {
            if (timeSec <= 0f) return 0;
            return (int)(timeSec / BurstTickSec);
        }

        /// <summary>
        /// シェーダへ渡す種 0..1。<b>刻みごとに変わる</b>ので、同じ刻みのあいだブロックの位置は動かない
        /// （毎フレーム動かすと砂嵐になり、「矩形が貼り付いている」というデジタル破損の顔が消える）。
        /// </summary>
        public static float SeedAt(float timeSec) => Hash01((uint)TickAt(timeSec) * 2654435761u);

        /// <summary>
        /// この刻みで発作が起きているか。確率は強さに比例（0 → <see cref="MaxBurstChance"/>）。
        /// </summary>
        public static bool BurstAt(float timeSec, float level)
        {
            float lv = Clamp01(level);
            if (lv <= OffThreshold) return false;
            return Hash01((uint)TickAt(timeSec) * 374761393u + 668265263u) < MaxBurstChance * lv;
        }

        /// <summary>
        /// 面の横飛び (m)。<b>発作の刻みだけ</b>飛び、そうでなければ 0。
        /// 幅は強さに比例し、向きは刻みごとに変わる。
        /// </summary>
        public static float OffsetXAt(float timeSec, float level)
        {
            if (!BurstAt(timeSec, level)) return 0f;
            int tick = TickAt(timeSec);
            float amp = MaxOffsetM * Clamp01(level) * (0.35f + 0.65f * Hash01((uint)tick * 1103515245u + 12345u));
            return Hash01((uint)tick * 22695477u + 1u) < 0.5f ? -amp : amp;
        }

        // ⚠⚠ **強さ 1 本にまとめない**（2026-08-17 に絵で分かった）。
        //    初版は「常時 0.42 / 発作 1.0」を 1 本の強さへ畳んでシェーダへ渡していたが、
        //    シェーダはその値で**ブロックの被覆率を解く**ので、**常時でもブロックが出ていた**
        //    ＝「たまに壊れる」が作れない。いまは `_Level`（常時の濃さ）と
        //    `_Burst`（発作が立っているか）を**別々に**渡す。

        private static float Hash01(uint n)
        {
            unchecked
            {
                uint x = n * 747796405u + 2891336453u;
                x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
                x = (x >> 22) ^ x;
                return (x & 0xFFFFFFu) / 16777215f;
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>整数でない指数も通すが、使うのは <see cref="Exponent"/> だけ。</summary>
        private static float Pow(float b, float e) => (float)System.Math.Pow(b, e);
    }
}
