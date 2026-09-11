#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>タブレットで体験前に選んだ設定（言語・ホラー軽減）を、この機へ運ぶ箱。</b>
    /// 2026-09-11・<c>canon/LEDGER.md</c> 0185（クエストα用・β用のタブレット 2 台）。
    ///
    /// 流れは 1 本だけ:
    ///   タブレット（<c>visitor.html</c>）→ 卓 <c>/command setVisitor</c> → <c>control.visitor.&lt;役&gt;</c>
    ///   → long-poll → <see cref="Offer"/>（<c>ShowControlClient</c>）→ 面が出ている段で
    ///   <see cref="ApplyPending"/>（<c>TitleScreen</c>）→ <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/>。
    ///
    /// <b>正は卓が持つ。</b>この機は「いま卓が言っている値（pending）」と「自分がどこまで
    /// 適用したか（applied）」「体験者がどこで始めたか（consumed）」の 3 つの世代番号を持つだけで、
    /// 再起動でぜんぶ 0 へ戻ってよい。体験者が A を押して注意書きを閉じた瞬間に
    /// <see cref="Consume"/> が世代を記録し、heartbeat がそれを卓へ返す。卓は同じ世代を見たら
    /// 枠を既定へ戻す（＝ 次の人に前の人の設定を持ち越さない。<c>capture-server.py</c> の
    /// <c>visitor_prefs.consume</c>）。
    ///
    /// ⚠ <b>static なのは <see cref="ShowLanguage"/> と同じ理由</b>（読む側が asmdef をまたぐ）。
    /// ⚠ <b>ここに表示用の文字列は置かない</b>（フォントの静的ベイク。<see cref="ShowLanguage"/> の doc）。
    /// ⚠ <b>この箱は <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/> を戻さない。</b>
    /// 戻すのは従来どおり <c>TitleScreen.BeginTitle</c> の 1 か所で、そのすぐ後に
    /// <see cref="ApplyAtTitle"/> が「戻した上に、卓の値を載せる」。順序を入れ替えると
    /// タイトルを出し直した瞬間にタブレットの設定が消える。
    /// </summary>
    public static class VisitorPrefs
    {
        /// <summary>この機に割り当てられた役（"alpha" / "beta"）。空 = 卓がまだ結んでいない。</summary>
        public static string Role { get; private set; } = "";

        /// <summary>卓が最後に言った枠の世代。0 = 枠を受け取っていない。</summary>
        public static int PendingEpoch { get; private set; }

        /// <summary>卓が最後に言った言語。</summary>
        public static ShowLang PendingLang { get; private set; } = ShowLanguage.Default;

        /// <summary>卓が最後に言った軽減モード。</summary>
        public static bool PendingRelief { get; private set; }

        /// <summary>この機が <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/> へ書いた最後の世代。</summary>
        public static int AppliedEpoch { get; private set; }

        /// <summary>体験者が注意書きを閉じた（＝ 始めた）ときの枠の世代。heartbeat が卓へ返す。</summary>
        public static int ConsumedEpoch { get; private set; }

        /// <summary>この走行で <see cref="ApplyPending"/> が実際に書いた回数（テレメトリが読む）。</summary>
        public static int ApplyCount { get; private set; }

        /// <summary>卓が言っている値のうち、まだこの機に書いていないものがあるか。</summary>
        public static bool HasUnapplied => PendingEpoch > 0 && PendingEpoch != AppliedEpoch;

        /// <summary>役を結ぶ／解く。解いたら枠も忘れる（別の役の値を引きずらない）。</summary>
        public static void SetRole(string? role)
        {
            string r = role ?? "";
            if (r == Role) return;
            Role = r;
            if (r.Length == 0) ClearPending();
        }

        /// <summary>
        /// 卓の枠を受け取る（long-poll のたびに呼んでよい。同じ世代なら何もしない）。
        /// ⚠ <b>ここでは書かない。</b>書くのは面が出ている段だけ（<see cref="ApplyPending"/>）。
        /// 本編の最中に言語が変わると、連絡の面が途中から別の言語になる。
        /// </summary>
        public static void Offer(int epoch, string? langCode, bool relief)
        {
            // 役が無い機は枠を持たない（結ばれていない機に他人の設定を書かない）。
            if (Role.Length == 0 || epoch <= 0) { ClearPending(); return; }
            PendingEpoch = epoch;
            PendingLang = ShowLanguage.Parse(langCode);
            PendingRelief = relief;
        }

        /// <summary>
        /// 受け取っている枠を <see cref="ShowLanguage"/> / <see cref="HorrorRelief"/> へ書く。
        /// <b>書いたら true。</b>世代が既に書いた値と同じなら何もしない。
        /// ⚠ <c>Select</c> を使うので <c>ChangeCount</c>（体験者が押した回数）は動かない。
        /// </summary>
        public static bool ApplyPending()
        {
            if (!HasUnapplied) return false;
            ShowLanguage.Select(PendingLang);
            HorrorRelief.Select(PendingRelief);
            AppliedEpoch = PendingEpoch;
            ApplyCount++;
            return true;
        }

        /// <summary>
        /// タイトルの出し直し（<c>TitleScreen.BeginTitle</c>）専用。既定へ戻した直後に呼ぶ。
        /// 同じ世代でも<b>もう一度書く</b>（戻した分を載せ直す）。<b>書いたら true。</b>
        /// </summary>
        public static bool ApplyAtTitle()
        {
            AppliedEpoch = 0;
            return ApplyPending();
        }

        /// <summary>
        /// 体験者が注意書きを閉じた（始めた）。いま持っている枠の世代を「消費した」として記録する。
        /// <b>記録が進んだら true。</b>heartbeat がこの値を卓へ返し、卓が枠を既定へ戻す。
        /// </summary>
        public static bool Consume()
        {
            if (PendingEpoch <= ConsumedEpoch) return false;
            ConsumedEpoch = PendingEpoch;
            return true;
        }

        private static void ClearPending()
        {
            PendingEpoch = 0;
            PendingLang = ShowLanguage.Default;
            PendingRelief = false;
        }

        /// <summary>まっさらへ（テスト・Editor のドメインリロード無し Play）。</summary>
        public static void Reset()
        {
            Role = "";
            ClearPending();
            AppliedEpoch = 0;
            ConsumedEpoch = 0;
            ApplyCount = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Reset();
    }
}
