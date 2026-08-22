#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーン左上の時刻表示（OSD）の**文字の決め方**（<c>canon/LEDGER.md</c> 0108）。
    ///
    /// 純ロジック（Unity 非依存・時刻は注入）。<see cref="ScreenOsd"/> がこれを回して
    /// 版のセルを敷き直す。<b>「いつ敷き直すか」もここが決める</b> — 秒が変わったときだけで、
    /// 毎フレーム書くと 90Hz で 27,000 画素を組み替えることになる。
    ///
    /// <b>虚構上の位置づけ</b>: この時計は<b>カメラではなく観測装置（画面の側）が打っている</b>
    /// （DVR の OSD と同じ）。だから映像に起きること（切替・差し込み・録画・乱れ・劣化・信号断）は
    /// 表示に及ばず、装置に起きること（切替の黒・終幕の電池切れ）は一緒に受ける。
    /// <b>3 周目に 1 周目の録画が流れても、時計は「いま」のまま進み続ける</b> —
    /// 録画には焼き込まれていないので、これが構造的に成立する。
    ///
    /// ⚠ <b>時刻で嘘をつかない。</b> 巻き戻し・停止・加速の演出は、「装置は正直に映している」
    /// という 3 周目の反転の土台（<c>rules/sound-design.md</c> §1）を壊す。
    /// 装置が死ぬのは終幕の電力（<c>_ScreenPower</c>）だけ。
    /// </summary>
    public static class OsdClockLogic
    {
        /// <summary>
        /// 版のセルの並び。**<c>tools/make-osd-font.py</c> の <c>GLYPHS</c> と対**。
        /// 片方だけ直すと実機で別の字が出る（<c>OsdClockLogicTests</c> が数と並びを固定する）。
        /// 末尾の空白は「何も描かない」セル。
        /// </summary>
        public const string Glyphs = "0123456789:/ ";

        /// <summary>版のセル数。</summary>
        public const int GlyphCount = 13;

        /// <summary>「何も描かない」セルの番号。解決できない文字はここへ落とす。</summary>
        public const int BlankGlyph = 12;

        /// <summary>
        /// 表示する文字数。<c>yyyy/MM/dd HH:mm:ss</c> ＝ 19。**固定長**なので、
        /// 敷き直しでテクスチャの大きさが変わることが無い。
        /// </summary>
        public const int TextLength = 19;

        /// <summary>
        /// 参考画像（004.jpg）と同じ書式。**すべて半角** — 時刻はデータそのもので、
        /// <c>HmdTextStyle</c> の書式 4・5（測った値・データは半角）の側。
        ///
        /// ⚠ <b>秒まで出す。</b> 秒が刻むこと自体が「これはライブだ」という主張になる。
        /// </summary>
        public static string Format(DateTime t)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                             "{0:D4}/{1:D2}/{2:D2} {3:D2}:{4:D2}:{5:D2}",
                             t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);

        /// <summary>文字 → 版のセル番号。知らない文字は空白のセルへ落とす（豆腐を出さない）。</summary>
        public static int GlyphIndex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c == ':') return 10;
            if (c == '/') return 11;
            return BlankGlyph;
        }

        /// <summary>
        /// 敷き直す番号列を <paramref name="into"/> へ書く（配列を毎回 new しない）。
        /// 長さが足りなければ書かずに false。
        /// </summary>
        public static bool FillGlyphs(DateTime t, int[] into)
        {
            if (into == null || into.Length < TextLength) return false;
            string s = Format(t);
            for (int i = 0; i < TextLength; i++) into[i] = GlyphIndex(s[i]);
            return true;
        }

        /// <summary>
        /// 敷き直しが要るか。**秒が変わった縁だけ true**。
        ///
        /// ⚠ 秒の値だけを比べない（60 秒後に同じ秒が来る）。日付をまたぐ走行・
        /// 端末の時刻補正でも取りこぼさないよう、<b>刻んだ通算秒</b>で比べる。
        /// </summary>
        /// <param name="last">前回書いた通算秒（初回は <see cref="Never"/>）</param>
        public static bool NeedsRedraw(DateTime now, long last, out long stamp)
        {
            stamp = now.Ticks / TimeSpan.TicksPerSecond;
            return stamp != last;
        }

        /// <summary>まだ 1 度も敷いていないことを表す値。</summary>
        public const long Never = long.MinValue;
    }
}
