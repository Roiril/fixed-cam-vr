#nullable enable

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
            => CountLabel + FullWidth(markCount) + "\n\n" + ClosingLines;
    }
}
