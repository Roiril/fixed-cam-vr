#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// スクリーン左上の表示（OSD）の**文字の決め方** — 時刻（<c>canon/LEDGER.md</c> 0108）と、
    /// その右の**周回**（0167）。
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
    ///
    /// <b>周回（0167）</b>: 時刻の右に「1周目」「2周目」「3周目」、帰りの区間は<b>「最後」</b>
    /// （ユーザー逐語「4周目と書くと混乱する」）。出どころは<b>区間の周</b>なので、逆走すれば戻る。
    /// <b>別の場所（バックルームズ）が映っているあいだだけ、時刻も周回も <c>?</c> で埋まる</b> —
    /// 装置が場所を見失っているので、読めないことを読めないと出している（嘘ではない）。
    /// </summary>
    public static class OsdClockLogic
    {
        /// <summary>
        /// 版の**半角**セルの並び（1 字 = 1 セル）。**<c>tools/make-osd-font.py</c> の
        /// <c>GLYPHS</c> と対**。片方だけ直すと実機で別の字が出る
        /// （<c>OsdClockLogicTests</c> が数と並びを固定する）。空白は「何も描かない」セル。
        /// </summary>
        public const string Glyphs = "0123456789:/ ?";

        /// <summary>
        /// 版の**全角**セルの並び（1 字 = <b>2 セル</b>）。**<c>make-osd-font.py</c> の
        /// <c>WIDE_GLYPHS</c> と対**。半角 14 セルの後ろに「左・右」の順で並ぶ。
        ///
        /// ⚠ <b>1 セルに詰めない。</b> 半角セルは 24px で、漢字を入れると潰れて実機で読めない。
        /// 2 セル（48px）なら数字と同じ字高のまま入る（版の側で字高を数字へ合わせている）。
        /// </summary>
        public const string WideGlyphs = "周目最後";

        /// <summary>版のセル数（半角 ＋ 全角 × 2）。</summary>
        public const int GlyphCount = 22;

        /// <summary>「何も描かない」セルの番号。解決できない文字はここへ落とす。</summary>
        public const int BlankGlyph = 12;

        /// <summary>
        /// 時刻の文字数。<c>yyyy/MM/dd HH:mm:ss</c> ＝ 19。**固定長**なので、
        /// 敷き直しでテクスチャの大きさが変わることが無い。
        /// </summary>
        public const int TextLength = 19;

        /// <summary>
        /// 時刻と周回のあいだの空きセル数（<c>canon/LEDGER.md</c> 0167「その右くらいに」）。
        /// </summary>
        public const int GapCells = 2;

        /// <summary>
        /// 周回の欄のセル数。いちばん長い「1周目」（半角 1 ＋ 全角 2 字）がちょうど収まる。
        /// **固定長**で、短い語（「最後」「???」）は右に空白が残る。
        /// </summary>
        public const int LabelCells = 5;

        /// <summary>
        /// 敷き直す 1 行のセル総数。**テクスチャの幅はこれで決まる**（＝ 画に出る全幅）。
        /// </summary>
        public const int CellCount = TextLength + GapCells + LabelCells;

        /// <summary>
        /// <b>異世界が映っているあいだの時刻</b>（<c>canon/LEDGER.md</c> 0167）。
        /// 区切り（<c>/</c> <c>:</c> と空白）は残して数字だけを <c>?</c> にする —
        /// 形が保たれるので「同じ装置が読めなくなっている」と読める。全部を潰すと
        /// ただの記号の列になり、そこに時計があったことすら分からない。
        ///
        /// ⚠ <b>時刻で嘘をつくのとは別のこと。</b> 禁じているのは巻き戻し・停止・加速で、
        /// 装置が場所を見失ったときに「読めない」と出すのは正直な振る舞い。
        /// </summary>
        public const string MaskedTime = "????/??/?? ??:??:??";

        /// <summary>異世界が映っているあいだの周回。</summary>
        public const string MaskedLabel = "???";

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

        /// <summary>半角の文字 → 版のセル番号。知らない文字は空白のセルへ落とす（豆腐を出さない）。</summary>
        public static int GlyphIndex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c == ':') return 10;
            if (c == '/') return 11;
            if (c == '?') return 13;
            return BlankGlyph;
        }

        /// <summary>
        /// 全角の文字 → <b>左のセル</b>の番号（右はその次）。全角でなければ -1。
        /// </summary>
        public static int WideCellIndex(char c)
        {
            int i = WideGlyphs.IndexOf(c);
            return i < 0 ? -1 : Glyphs.Length + i * 2;
        }

        /// <summary>
        /// <b>周回の語</b>（<c>canon/LEDGER.md</c> 0167）。区間がまだ確定していなければ空。
        ///
        /// ⚠ <b>帰りの区間（<c>lap = totalLaps + 1</c>）は「最後」</b>。
        /// ユーザー逐語「4-Aは最後とかの表示で。4周目と書くと混乱する」 —
        /// 3 周と案内している体験で 4 周目と出ると、数が合わなくなる。
        /// </summary>
        /// <param name="lap">いま体験者が居る<b>区間の周</b>（1 始まり・未確定は -1）。</param>
        public static string LapLabel(int lap, int totalLaps)
        {
            if (totalLaps < 1) totalLaps = ShowRunDefaults.TotalLaps;
            if (lap < 1) return "";
            // 版に 1 桁ぶんしか欄が無い。走り切った先はどこであれ「最後」。
            if (lap > totalLaps || lap > 9) return "最後";
            return (char)('0' + lap) + "周目";
        }

        /// <summary>
        /// テレメトリに出す周回のトークン（<c>osdLap=</c>）。
        /// <c>-</c>（区間が未確定）/ <c>1</c> <c>2</c> <c>3</c> / <c>last</c>（帰りの区間）/
        /// <c>mask</c>（異世界）。
        ///
        /// ⚠ <b>語ではなくトークンを出す。</b> ログは機械が読むので、語（「最後」）を出すと
        /// 文言を直した日に解析が黙って落ちる。<c>analyze-xp-log.py</c> と対。
        /// </summary>
        public static string LapToken(int lap, int totalLaps, bool otherworld)
        {
            if (otherworld) return "mask";
            if (totalLaps < 1) totalLaps = ShowRunDefaults.TotalLaps;
            if (lap < 1) return "-";
            if (lap > totalLaps || lap > 9) return "last";
            return ((char)('0' + lap)).ToString();
        }

        /// <summary>
        /// 敷き直す番号列を <paramref name="into"/> へ書く（配列を毎回 new しない）。
        /// 長さが足りなければ書かずに false。**時刻だけ**（<see cref="TextLength"/> セル）。
        /// </summary>
        public static bool FillGlyphs(DateTime t, int[] into)
        {
            if (into == null || into.Length < TextLength) return false;
            string s = Format(t);
            for (int i = 0; i < TextLength; i++) into[i] = GlyphIndex(s[i]);
            return true;
        }

        /// <summary>
        /// <b>1 行ぶん（時刻 ＋ 空き ＋ 周回）のセル番号</b>を <paramref name="into"/> へ書く。
        /// 長さが足りなければ書かずに false。
        ///
        /// <paramref name="otherworld"/> が立っているあいだは<b>時刻も周回も <c>?</c></b>
        /// （<c>canon/LEDGER.md</c> 0167「バックルーム的な演出の最中では、時刻も何周目かも
        /// 全部???になるようにしてほしい」）。
        /// </summary>
        public static bool FillCells(DateTime t, int lap, int totalLaps, bool otherworld, int[] into)
        {
            if (into == null || into.Length < CellCount) return false;

            string time = otherworld ? MaskedTime : Format(t);
            int at = 0;
            for (int i = 0; i < TextLength; i++) into[at++] = GlyphIndex(time[i]);
            for (int i = 0; i < GapCells; i++) into[at++] = BlankGlyph;

            string label = otherworld ? MaskedLabel : LapLabel(lap, totalLaps);
            foreach (char c in label)
            {
                int wide = WideCellIndex(c);
                int need = wide >= 0 ? 2 : 1;
                if (at + need > CellCount) break;   // 欄からはみ出す語は切る（版の外を読まない）
                if (wide >= 0) { into[at++] = wide; into[at++] = wide + 1; }
                else into[at++] = GlyphIndex(c);
            }
            while (at < CellCount) into[at++] = BlankGlyph;
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
