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
    /// ⚠ <b>括弧は外した</b>（2026-08-15・ユーザー指摘「()で包む必要があるのかとか」）。
    /// 括弧は補足に見えるが、これは<b>体験中ずっと手元に出ている唯一の常設表示</b>で、
    /// 装置の操作銘板として書くのが正しい。書式も他の面と同じ <c>入力：動作</c> に揃えた
    /// （<see cref="ControllerGuidePanel"/> / <see cref="RecoveryGuidance"/> と同じ形）。
    /// 半角読点も <c>／</c> へ — <c>X,Y</c> は「X と Y」に読めるが、実際は<b>どちらでもよい</b>。
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
        /// <summary>待っているときの 1 行。</summary>
        public const string IdleLine = "X／Y：異変を報告";

        /// <summary>長押し中の見出し。<b>ゲージより小さく出す</b>（<see cref="LabelPercent"/>）。</summary>
        public const string HoldingHead = "報告中";

        /// <summary>発火直後の余韻。</summary>
        public const string ConfirmedLine = "報告しました";

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
        /// いま出す文字。優先は 余韻 → 長押し中 → 待ち。
        /// </summary>
        /// <param name="progress01">長押しの進捗 [0,1]。0 なら押していない。</param>
        /// <param name="confirming">発火直後の余韻の最中か。</param>
        public static string Line(float progress01, bool confirming)
        {
            if (confirming) return ConfirmedLine;
            if (progress01 <= 0f) return IdleLine;
            return $"<size={LabelPercent}%>{HoldingHead}</size>\n"
                 + RegistrationGuidance.ProgressBar(progress01, 1f, Slots);
        }
    }
}
