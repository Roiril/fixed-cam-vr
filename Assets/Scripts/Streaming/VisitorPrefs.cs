#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>タブレットで体験前に選んだ設定（言語・ホラー軽減）を、この機の中で運ぶ箱。</b>
    /// 2026-09-11・<c>canon/LEDGER.md</c> 0185（クエストα用・β用のタブレット 2 台）／
    /// 0187（卓を経由せず、タブレットと Quest を直接つなぐ）。
    ///
    /// 流れは 1 本だけ、しかも**この機の中で閉じている**:
    ///   タブレット → <see cref="VisitorPortal"/>（この機の HTTP）→ <see cref="Set"/>
    ///   → 題字を待つ段で <see cref="ApplyPending"/>（<c>TitleScreen</c>）
    ///   → <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/>。
    ///
    /// <b>正はこの機。</b>卓も PC も持たない。報告練習後に題字を出し始めた瞬間に
    /// <see cref="Consume"/> が枠を空にする ＝ 次の人に前の人の設定を持ち越さない。
    /// 再起動すればまっさら（永続化しない。持ち越してよいものが 1 つも無い）。
    ///
    /// ⚠ <b>static なのは <see cref="ShowLanguage"/> と同じ理由</b>（読む側が asmdef をまたぐ）。
    /// ⚠ <b>ここに表示用の文字列は置かない</b>（フォントの静的ベイク。<see cref="ShowLanguage"/> の doc）。
    /// ⚠ <b>この箱は <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/> を戻さない。</b>
    /// 戻すのは従来どおり <c>TitleScreen.BeginTitle</c> の 1 か所で、そのすぐ後に
    /// <see cref="ApplyAtTitle"/> が「戻した上に、枠の値を載せる」。順序を入れ替えると
    /// タイトルを出し直した瞬間にタブレットの設定が消える。
    /// ⚠ 呼ぶのはメインスレッドだけ（<see cref="VisitorPortal"/> はキューで渡してくる）。
    /// </summary>
    public static class VisitorPrefs
    {
        /// <summary>いま枠に入っている選択の受理番号。0 = 枠は空。</summary>
        public static int PendingSeq { get; private set; }

        /// <summary>枠の言語。</summary>
        public static ShowLang PendingLang { get; private set; } = ShowLanguage.Default;

        /// <summary>枠の軽減モード。</summary>
        public static bool PendingRelief { get; private set; }

        /// <summary>この機が <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/> へ書いた最後の受理番号。</summary>
        public static int AppliedSeq { get; private set; }

        /// <summary>体験者が始めた（枠を空にした）ときの受理番号。テレメトリが読む。</summary>
        public static int ConsumedSeq { get; private set; }

        /// <summary>この走行で <see cref="ApplyPending"/> が実際に書いた回数（テレメトリが読む）。</summary>
        public static int ApplyCount { get; private set; }

        /// <summary>枠に何か入っているか。</summary>
        public static bool HasPending => PendingSeq > 0;

        /// <summary>枠の値のうち、まだこの機に書いていないものがあるか。</summary>
        public static bool HasUnapplied => PendingSeq > 0 && PendingSeq != AppliedSeq;

        /// <summary>
        /// タブレットの選択を枠へ入れる（受理番号は <see cref="VisitorPortal"/> が振る。単調増加）。
        /// ⚠ <b>ここでは書かない。</b>書くのは面が出ている段だけ（<see cref="ApplyPending"/>）。
        /// 本編の最中に言語が変わると、連絡の面が途中から別の言語になる。
        /// </summary>
        public static void Set(ShowLang lang, bool relief, int seq)
        {
            if (seq <= 0) return;
            PendingSeq = seq;
            PendingLang = lang;
            PendingRelief = relief;
        }

        /// <summary>
        /// 枠の値を <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/> へ書く。<b>書いたら true。</b>
        /// 受理番号が既に書いた値と同じなら何もしない。
        /// ⚠ <c>Select</c> を使うので <c>ChangeCount</c>（体験者が押した回数）は動かない。
        /// </summary>
        public static bool ApplyPending()
        {
            if (!HasUnapplied) return false;
            ShowLanguage.Select(PendingLang);
            HorrorRelief.Select(PendingRelief);
            AppliedSeq = PendingSeq;
            ApplyCount++;
            return true;
        }

        /// <summary>
        /// タイトルの出し直し（<c>TitleScreen.BeginTitle</c>）専用。既定へ戻した直後に呼ぶ。
        /// 同じ受理番号でも<b>もう一度書く</b>（戻した分を載せ直す）。<b>書いたら true。</b>
        /// </summary>
        public static bool ApplyAtTitle()
        {
            AppliedSeq = 0;
            return ApplyPending();
        }

        /// <summary>
        /// 導入が題字の表示へ進んだ。枠を空にする ＝ 次の人へ持ち越さない。
        /// <b>空にしたら true</b>（もともと空なら false）。
        /// </summary>
        public static bool Consume()
        {
            if (!HasPending) return false;
            ConsumedSeq = PendingSeq;
            ClearPending();
            return true;
        }

        /// <summary>スタッフが枠を空にする（送ったまま帰った人の分）。書いた値は戻さない。</summary>
        public static void Clear() => ClearPending();

        private static void ClearPending()
        {
            PendingSeq = 0;
            PendingLang = ShowLanguage.Default;
            PendingRelief = false;
        }

        /// <summary>まっさらへ（テスト・Editor のドメインリロード無し Play）。</summary>
        public static void Reset()
        {
            ClearPending();
            AppliedSeq = 0;
            ConsumedSeq = 0;
            ApplyCount = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Reset();
    }
}
