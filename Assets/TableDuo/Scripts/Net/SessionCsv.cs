#nullable enable
namespace TableDuoVr.Net
{
    /// <summary>
    /// セッション CSV の値サニタイズ（純関数）。外部入力 label（curl ?label= 経由）が
    /// カンマ/引用符/改行/CR/タブを含むと CSV 列崩れ＝解析破綻するため無害化する。
    /// <see cref="SessionLogger"/> の private Escape を切り出し（Replace 連鎖順は不変）。
    /// </summary>
    public static class SessionCsv
    {
        // カンマは列区切り、改行は行区切りを壊すので無害化（外部 label に curl ?label= 等で混入しうる）。
        // 二重引用符・タブも RFC4180 パーサで列崩れを起こすので無害化する。
        public static string Escape(string s) =>
            s.Replace(',', ';').Replace('"', '\'').Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
