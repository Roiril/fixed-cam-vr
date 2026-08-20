#nullable enable
using FixedCamVr.Tracking;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>体験者の報告ボタンの面</b>に出す文字を組み立てる純関数。UnityEngine 非依存。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0050（ユーザー逐語）:
    /// 「コントローラーの少し上、少し奥に、小さいスクリーンを置いておいて、そこに、
    /// (X,Yで異変を報告) みたいに書いておいてほしい」「報告中 / ゲージ みたいな構成で」。
    ///
    /// ⚠⚠ <b>2026-08-16 に「指示」を全部剥がした</b>（<c>canon/LEDGER.md</c> 0065）。
    /// ここが持つのは<b>状態</b>（いま押している途中であること）だけで、
    /// <b>押し方はAIエージェントの連絡①が本編の入口で 1 度だけ言う</b>（<see cref="CommsPanel"/>）。
    ///
    /// それまでは <c>X／Y：異変を報告</c> を常設の操作銘板として書いていたが、
    /// 面が開いているあいだずっと出る作りだったので、
    /// <b>押している最中にも「押せ」と言い続けていた</b>（ユーザー指摘
    /// 「X/Y を長押しして表示しているのに、X/Y を長押しして記録とかは表示しておかなくていい」）。
    /// 装置の面から<b>入力機器の名前も消えた</b> — キー名が出ると、調査の記録ではなく
    /// ゲームの操作説明に見える。
    ///
    /// ⚠ <b>「報告中」はゲージより小さく、左端を揃える</b>（2026-08-15・ユーザー指摘
    /// 「バーの左上の端っこに少し小さめにした方がかっこいい」）。ゲージが主で、ラベルは添え物。
    ///
    /// ⚠ <b>ゲージは位置合わせと同じ物差しを使う</b>（<see cref="RegistrationGuidance.ProgressBar"/>）。
    /// 2 種類のゲージを作らない — 同じ装置が 2 つの流儀で進捗を出すと、どちらも記号に見える。
    /// <b>出力にリッチテキストが入る</b>ので、出力先の <c>richText</c> を有効にすること。
    ///
    /// ⚠ <b>正誤を返さない。</b> 出すのは「受け取った」だけで、何が異変だったかは装置が判定しない
    /// （この作品の恐怖は「装置は正直に映すだけ」の上に乗っている）。
    ///
    /// ⚠ 文言を変えたら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する
    /// （静的ベイクなので、ここに無い文字は実機で豆腐になる）。
    /// </summary>
    public static class VisitorMarkGuidance
    {
        /// <summary>
        /// 長押し中の見出し。<b>ゲージより小さく出す</b>（<see cref="LabelPercent"/>）。
        /// ⚠ <b>「報告中」→「解析中」</b>（2026-08-19・<c>canon/LEDGER.md</c> 0096）。
        /// ①の連絡が「ボタンを長押ししてください／装置が解析して解呪します」と言うので、
        /// 押しているあいだ画に出る語もそちらへ揃える（<b>同じ装置の言葉に聞こえること</b>）。
        /// </summary>
        public const string HoldingHead = "解析中";

        /// <summary>
        /// 見出しの大きさ（本文に対する %）。<b><see cref="HmdTextStyle.MinorPercent"/> と同じ値</b>で、
        /// <c>HmdTextStyleTests.LabelPercent_MatchesMinorTier</c> が食い違いを落とす。
        /// ここが const なのは、この面の文言をテストで逐語固定しているため。
        /// </summary>
        public const int LabelPercent = 83;

        /// <summary>
        /// ゲージの目盛数。位置合わせ（0.5 秒・5 目盛）より長い 2 秒なので倍にする
        /// （5 目盛だと 1 目盛 0.4 秒で、進んでいるのか止まっているのか分からない）。
        /// </summary>
        public const int Slots = 10;

        /// <summary>
        /// いま出す文字。<b>押している最中だけ</b>（それ以外は空 ＝ 下段に何も出さない）。
        ///
        /// ⚠⚠ <b>指示も余韻も持たない</b>（2026-08-16・<c>canon/LEDGER.md</c> 0065）。捨てた 2 つ:
        /// <list type="bullet">
        /// <item><c>X／Y：異変を報告</c> — 面が開いているあいだ出ていたので、
        ///       <b>押している最中にも「押せ」と言い続けていた</b>。押し方は①の連絡が 1 度だけ言う</item>
        /// <item><c>報告しました</c> — 上段の「異常が記録されました」と<b>同じ瞬間に同じことを言う</b>。
        ///       二重に言う面は、装置が動揺しているように見える</item>
        /// </list>
        /// 残すのは<b>状態</b>だけ ＝ いま押している途中であること。指示ではないので出しっぱなしにならない。
        /// </summary>
        /// <param name="progress01">長押しの進捗 [0,1]。0 なら押していない。</param>
        /// <param name="confirming">発火直後の余韻の最中か（<b>いまは画に出さない</b>）。</param>
        public static string Line(float progress01, bool confirming)
        {
            if (progress01 <= 0f) return "";
            return $"<size={LabelPercent}%>{HoldingHead}</size>\n"
                 + RegistrationGuidance.ProgressBar(progress01, 1f, Slots);
        }
    }
}
