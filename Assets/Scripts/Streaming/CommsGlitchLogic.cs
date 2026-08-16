#nullable enable

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>連絡の面が、周を重ねるごとに壊れていく。</b>UnityEngine 非依存・時刻注入。
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0068 / <b>0069</b>（作り直し）。
    /// 0068 は「壊れを別の層として面の上へ貼る」形で、ユーザーの赤入れは
    /// 「明るすぎる／色が鮮やかすぎる／四角すぎる」だった。**3 つとも同じ 1 つの誤りから出ている** —
    /// <b>この装置は画像を表示していない。1 文字ずつ印字している</b>（打鍵音つき・0056）。
    /// 原色の矩形は**画像の符号化が壊れたとき**の語彙で、印字機には別の壊れ方がある。
    ///
    /// ⇒ **壊れるのは印字そのもの。レイヤは重ねない。**
    ///
    /// | 何 | どこ |
    /// |---|---|
    /// | 字が別の記号へ化ける | <see cref="Corrupt"/> |
    /// | 字が出ない（幅は保つ） | 同上（全角の空白へ置き換える） |
    /// | 面がたまに横へ飛ぶ | <see cref="OffsetXAt"/> |
    /// | 地がわずかに明滅する | <see cref="PanelFlickerAt"/> |
    ///
    /// ⚠⚠ <b>進みは映像とまったく同じものを読む</b>（<see cref="ScreenDecayLogic.Progress"/> ＝
    /// <c>ShowRunDirector.ScreenDecay</c>）。あれは<b>最後の周へ入った瞬間に 1.0 へ着いて以後動かない</b>
    /// ので、「3 周目 A で最大」は<b>別の曲線を書かずにそのまま満たされる</b>。
    /// 独自の周回カウンタを持たせない — 2 つ持つと片方だけ直したときに黙って食い違う。
    ///
    /// ⚠ <b>乱数を使わない。</b> 刻み番号のハッシュで決めるので、同じ版・同じ時刻は同じ絵になる
    /// （<c>OutroLogic.FlickerPower</c> と同じ流儀）。
    /// </summary>
    public sealed class CommsGlitchLogic
    {
        /// <summary>
        /// ⚠⚠ <b>化けた先が意味を持ってはいけない。</b>
        /// 「異常が記録されました」→「異常が記録されません」は<b>装置が嘘をついた</b>ことになり、
        /// 3 周目の反転が乗っている「装置は正直に映している」を壊す（`rules/sound-design.md` §1）。
        /// だから化け先は<b>意味を持たない記号だけ</b>。
        ///
        /// ⚠ <b>四角（■ □）は入れない</b>（0069 の赤入れ「四角すぎる」）。「字が出ていない」は
        /// <see cref="Blank"/>（全角の空白）が担うので、伏せ字の四角は要らない。
        /// ⚠ <b>豆腐（▯）も使わない</b> — 本物のフォント欠落と区別が付かず、
        /// 次の人が「フォントが壊れた」と誤診する。
        ///
        /// ⚠⚠ <b>この 4 字はフォントへ焼かれている必要がある</b>（HMD の日本語は静的ベイク）。
        /// <c>JapaneseHudFontSetup</c> の収集元にこのファイルが入っているので、
        /// **ここに書いてあるだけで拾われる**。字を足したら <c>menu hud-font</c> を再実行する。
        /// </summary>
        public const string Marks = "─│┼※";

        /// <summary>字が出なかったときの置き換え先。<b>全角</b>なので幅が変わらない。</summary>
        public const char Blank = '　';

        /// <summary>
        /// 進み → グリッチの強さ の曲線の指数。<b>2 ＝ 序盤は軽く、後半で効く</b>。
        ///
        /// ⚠ <b>1（線形）にしない。</b> 線形だと 1 周目の途中から目に見え始めるが、
        /// そのとき映像はまだ 1 周目の頭（<c>ScreenDecayLogic</c> のブロックは 800 ＝ ほとんど無変化）で、
        /// <b>連絡の面だけが先に壊れる</b>。同じ装置なのに壊れ方の足並みが揃わない。
        ///
        /// 実際の進み: 2 周目の頭 0.11 / 中ほど 0.25 / 2 周目の終わり 0.44 / <b>3 周目 A 以降 1.00</b>。
        /// </summary>
        public const float Exponent = 2f;

        /// <summary>
        /// 最大時に化ける字の割合。<b>5〜6 字に 1 字</b>。
        /// ⚠ これ以上は文面が読めない。③「異常が検出されました。記録してください。」は
        /// <b>4 周目 A の締め</b>で出て、<b>読まれないと締めのカットが進まない</b>。
        /// </summary>
        public const float MaxCorruptShare = 0.18f;

        /// <summary>化けたうち「出なかった」（空白）になる割合。残りは <see cref="Marks"/> の記号。</summary>
        public const float BlankShare = 0.34f;

        /// <summary>化ける組み合わせが変わる刻み (秒)。短いとちらつき、長いと固まって見える。</summary>
        public const float TickSec = 0.22f;

        /// <summary>発作（面が横へ飛ぶ）が起きる確率の上限。<b>常時ではない</b>ことを守る値。</summary>
        public const float MaxBurstChance = 0.34f;

        /// <summary>
        /// 面が横へ飛ぶ幅の上限 (m)。面の幅は 0.76m なので <b>2.6%</b>。
        /// ⚠ これ以上にすると、飛んでいるあいだ文字が追えなくなる（読める側へ倒す）。
        /// </summary>
        public const float MaxOffsetM = 0.02f;

        /// <summary>地の明滅の深さの上限。<b>沈むだけで、明るくはならない</b>（電源が落ちかけている）。</summary>
        public const float MaxFlickerDepth = 0.35f;

        /// <summary>これ以下は「何も起きていない」（＝ 1 画素も変わらない）。</summary>
        public const float OffThreshold = 0.002f;

        /// <summary>周回の進み 0..1 → 壊れの強さ 0..1。</summary>
        public static float LevelFor(float decayProgress)
        {
            float p = Clamp01(decayProgress);
            if (p <= 0f) return 0f;
            float lv = Pow(p, Exponent);
            return lv <= OffThreshold ? 0f : lv;
        }

        /// <summary>いまの刻み番号。<b>化けの組み合わせも発作もここから引く</b>ので、時刻の写し先は 1 つ。</summary>
        public static int TickAt(float timeSec)
        {
            if (timeSec <= 0f) return 0;
            return (int)(timeSec / TickSec);
        }

        /// <summary>
        /// <b>文面を壊す。</b> 字を別の記号へ置き換える／出さない。
        ///
        /// ⚠⚠ <b>文字数を変えない</b>（置換のみ・挿入も削除もしない）。変えると打鍵の数えと
        /// <c>NoticeChars</c> が食い違う。<see cref="Marks"/> も <see cref="Blank"/> も全角なので
        /// <b>幅も変わらない</b> ＝ レイアウトの組み直しも重心の運び直しも要らない。
        ///
        /// ⚠ <b>改行は壊さない。</b> 壊すと行が繋がって、面からはみ出す。
        /// </summary>
        /// <returns>壊れていなければ <paramref name="src"/> をそのまま返す（割り当てを作らない）。</returns>
        public static string Corrupt(string src, float level, int tick)
        {
            float lv = Clamp01(level);
            if (lv <= OffThreshold || string.IsNullOrEmpty(src)) return src;

            float share = MaxCorruptShare * lv;

            // ⚠⚠ **化ける字数に上限を置く。** 字ごとに独立して確率で決めると二項分布の裾が出て、
            //    まれに **4 割が化ける刻み**が現れる（2026-08-17 にテストが捕まえた。20 字の文面で
            //    実測 8 字）。そうなると③が読めず、締めのカットが進まない。
            //    ⇒ 確率で選びつつ、**上限に達したら打ち切る**。
            int usable = 0;
            for (int i = 0; i < src.Length; i++)
                if (src[i] != '\n' && src[i] != '\r') usable++;
            // ⚠⚠ **端数は切り捨てず、刻みごとの抽選で 1 字ぶん足す。** 切り捨てると
            //    2 周目（進み 0.33 ＝ 期待 0.4 字）が**丸ごと 0 になり、1 字も化けない**
            //    ＝「徐々に」が成立しない（2026-08-17 に絵で見つけた）。
            //    抽選なら 2〜3 刻みに 1 字となり、たまに崩れる装置になる。
            float expected = usable * share;
            int budget = (int)expected;
            if (Hash01((uint)tick * 2246822519u + 99u) < expected - budget) budget++;
            if (budget <= 0) return src;

            // ⚠ 走査の開始位置を刻みごとにずらす。0 から始めると**前半ばかり化ける**
            //    （上限で打ち切るので、後半まで届かない）。
            int start = (int)(Hash01((uint)tick * 3266489917u + 5u) * src.Length) % src.Length;

            char[]? buf = null;
            int done = 0;
            for (int k = 0; k < src.Length && done < budget; k++)
            {
                int i = (start + k) % src.Length;
                char c = src[i];
                if (c == '\n' || c == '\r') continue;
                if (Hash01((uint)tick * 2654435761u + (uint)i * 2246822519u + 374761393u) >= share) continue;

                buf ??= src.ToCharArray();
                done++;
                // ⚠ 「出なかった」か「別の記号」かは**別のハッシュ**で引く。
                //    同じ値を使い回すと、割合の判定と種類の判定が相関して片方へ偏る。
                if (Hash01((uint)tick * 1103515245u + (uint)i * 3266489917u + 668265263u) < BlankShare)
                {
                    buf[i] = Blank;
                }
                else
                {
                    int pick = (int)(Hash01((uint)tick * 22695477u + (uint)i * 668265263u + 1u)
                                     * Marks.Length);
                    buf[i] = Marks[pick % Marks.Length];
                }
            }
            return buf == null ? src : new string(buf);
        }

        /// <summary>この刻みで発作が起きているか。確率は強さに比例（0 → <see cref="MaxBurstChance"/>）。</summary>
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
            float amp = MaxOffsetM * Clamp01(level)
                        * (0.35f + 0.65f * Hash01((uint)tick * 1103515245u + 12345u));
            return Hash01((uint)tick * 22695477u + 1u) < 0.5f ? -amp : amp;
        }

        /// <summary>
        /// 地と文字に掛ける明るさ 0..1。<b>1 = 平常</b>。
        ///
        /// ⚠ <b>沈むだけで、明るくはしない。</b> 電源が落ちかけている装置は暗くなる。
        /// 明るくすると「光った」に見えて、赤入れの「明るすぎる」へ戻る。
        /// ⚠ 刻みより細かく揺らす（印字中のちらつきなので、字の速さより速い）。
        /// </summary>
        public static float PanelFlickerAt(float timeSec, float level)
        {
            float lv = Clamp01(level);
            if (lv <= OffThreshold) return 1f;
            // 刻みの 4 倍の速さ。1 刻みの中でも数回沈む。
            int fine = timeSec <= 0f ? 0 : (int)(timeSec / (TickSec * 0.25f));
            float h = Hash01((uint)fine * 2246822519u + 3266489917u);
            // ⚠ **ほとんどの瞬間は 1 のまま。** 常に揺れていると「点滅する看板」になる。
            if (h > 0.22f) return 1f;
            float depth = MaxFlickerDepth * lv * (0.4f + 0.6f * Hash01((uint)fine * 668265263u + 7u));
            return 1f - depth;
        }

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

        private static float Pow(float b, float e) => (float)System.Math.Pow(b, e);
    }
}
