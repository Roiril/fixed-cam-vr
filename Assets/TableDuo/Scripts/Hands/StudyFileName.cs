#nullable enable
namespace TableDuoVr.Hands
{
    /// <summary>
    /// pid/pair のファイル名サニタイズ（純関数）。英数 . _ - 以外は _ に落とす。
    /// Net(SessionLogger) と Hands.Playback(StreamingPoseRecorder) の両方から使う唯一の位置＝Hands
    /// （Net は Hands を参照するが Hands→Net は不可なので Net 側には置けない）。重複 2 実装の単一化。
    /// </summary>
    public static class StudyFileName
    {
        // ファイル名に使える文字だけに落とす（英数 . _ - 以外は _）。pid/pair の取り違え防止用。
        public static string SafeTag(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
