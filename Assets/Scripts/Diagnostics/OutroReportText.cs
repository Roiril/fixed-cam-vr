#nullable enable
using FixedCamVr.Streaming;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>終幕の報告</b>に出す文字を組み立てる純関数。UnityEngine 非依存。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0048（ユーザー逐語）:
    /// 「最後に、報告した怪異の数：○○ / 十分なデータが取れました。/ 調査完了です。/
    /// 装置を外してください。 みたいなことを書いて終了にしてほしい」。
    ///
    /// ⚠ <b>4 行はユーザーが書いたまま出す</b>（句点も含めて）。○○ に入るのは
    /// <c>ShowControlClient.VisitorMarkCount</c>（左 X / Y の 2 秒長押しの回数）。
    ///
    /// ⚠ <b>数は全角。</b> 紙の依頼書が「観測者番号 ０３７」と全角で組んであるので、
    /// 同じ装置が出す数字の形を揃える（<c>canon/LEDGER.md</c> 0041）。
    ///
    /// ⚠ <b>正誤を返さない。</b> 出すのは押された回数だけで、何が異変だったかは装置が判定しない
    /// （<see cref="VisitorMarkGuidance"/> と同じ理由）。
    ///
    /// ⚠ 文言を変えたら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する
    /// （静的ベイクなので、ここに無い文字は実機で豆腐になる）。
    /// </summary>
    public static class OutroReportText
    {
        /// <summary>数の見出し。<b>ユーザーが書いた文字列そのまま</b>（全角コロン）。</summary>
        public const string CountLabel = "報告した怪異の数：";

        /// <summary>結びの 3 行。<b>ユーザーが書いたまま</b>（句点も含む）。</summary>
        public const string ClosingLines =
            "十分なデータが取れました。\n" +
            "調査完了です。\n" +
            "装置を外してください。";

        /// <summary>
        /// 数の見出し（体験者が選んだ言語・2026-09-03）。
        /// ⚠ Latin は<b>数のあとに続けて書く</b>ので末尾に半角空白を持つ。
        /// フランス語はコロンの<b>前にも</b>空白を置く（あちらの組版）。
        /// </summary>
        public static string CountLabelOf(ShowLang lang) => lang switch
        {
            ShowLang.En => "Anomalies reported: ",
            ShowLang.Fr => "Anomalies signalées : ",
            _ => CountLabel,
        };

        /// <summary>
        /// 結びの 3 行（体験者が選んだ言語）。<b>日本語の 4 行と同じことを言う</b> —
        /// 行を足しも減らしもしない（<c>canon/LEDGER.md</c> 0048 が書いた構成そのもの）。
        /// ⚠ 語は連絡の面と揃える（「調査」＝ survey / enquête）。
        /// </summary>
        public static string ClosingLinesOf(ShowLang lang) => lang switch
        {
            ShowLang.En =>
                "We have enough data.\n" +
                "Survey complete.\n" +
                "Please remove the device.",
            ShowLang.Fr =>
                "Données suffisantes.\n" +
                "Enquête terminée.\n" +
                "Veuillez retirer l'appareil.",
            _ => ClosingLines,
        };

        /// <summary>
        /// 全角数字の並び。<b>ここに literal で置いてあるのは、フォントのベイクに拾わせるため</b>
        /// （収集元はソースの非 ASCII 文字なので、書式で組み立てるだけだと 1 文字も焼かれない）。
        /// </summary>
        private const string FullWidthDigits = "０１２３４５６７８９";

        /// <summary>負の数は 0 として扱う（押していない体験者に「−１」を出さない）。</summary>
        public static string FullWidth(int n)
        {
            if (n <= 0) return FullWidthDigits[0].ToString();
            var sb = new System.Text.StringBuilder(4);
            while (n > 0)
            {
                sb.Insert(0, FullWidthDigits[n % 10]);
                n /= 10;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 報告の全文。1 行目（数）と結びのあいだに空行を 1 つ置く
        /// （測った値と、装置が言うことは別のもの。<b>この空行はシュビーが決めた</b>）。
        /// </summary>
        public static string Compose(int markCount)
            => Compose(markCount, ShowLanguage.Current);

        /// <summary>
        /// 言語を明示して組む（テストと <c>menu text-audit</c> 用）。
        ///
        /// ⚠ <b>数字を全角にするのは日本語だけ。</b> 全角は「紙の依頼書（観測者番号 ０３７）と
        /// 同じ装置の数字の形」を狙ったもので（0041）、Latin の文の中に混ぜると
        /// <b>その 1 文字だけ倍の幅になって桁がずれる</b> ＝ 別の装置の出力に見える。
        /// </summary>
        public static string Compose(int markCount, ShowLang lang)
            => CountLabelOf(lang) + CountOf(markCount, lang) + "\n\n" + ClosingLinesOf(lang);

        /// <summary>その言語で書いた回数。負の数は 0 として扱う（<see cref="FullWidth"/> と同じ規律）。</summary>
        public static string CountOf(int markCount, ShowLang lang)
            => lang == ShowLang.Ja
               ? FullWidth(markCount)
               : (markCount <= 0 ? "0" : markCount.ToString(
                     System.Globalization.CultureInfo.InvariantCulture));
    }
}
