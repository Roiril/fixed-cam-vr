#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 調査セッションの起動時条件（study-design.md §2）。
    /// ConnectionManager が intent extras / コマンドライン / Inspector から設定し、
    /// 各層はここを読むだけ（書くのは起動時の1回）。
    /// </summary>
    public static class StudyConfig
    {
        public enum Role : byte
        {
            Full = 0,
            Hand = 1,
        }

        /// <summary>起動フラグで指定された自分の役割。null なら従来規則（host=Full / client=Hand）。</summary>
        public static Role? ForcedRole;

        /// <summary>手役アバターの頭マーカー。調査既定は OFF（曖昧さ自体が RQ2/RQ3 の現象）。</summary>
        public static bool ShowHeadMarker;

        /// <summary>片手モード（右手のみ）。調査既定 ON。</summary>
        public static bool OneHandMode = true;

        /// <summary>tdv_* 起動フラグ経由で起動された＝調査セッション（デバッグ GUI を隠す等）。</summary>
        public static bool LaunchedWithStudyFlags;
    }
}
