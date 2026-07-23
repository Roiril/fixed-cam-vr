#nullable enable
using System.Text;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ホスト発見 UDP ビーコンの wire 組立・パース（純関数）。
    /// wire: "TDVB1|&lt;NGOポート&gt;|&lt;sceneHash&gt;"。sceneHash 空 or ローカル空は照合スキップ（一致扱い）。
    /// 送信は <see cref="HostBeacon"/>、受信パースは <see cref="HostDiscovery"/> がこれを使う
    /// （受信スレッドのインライン手続きを純関数化＝テストで wraparound/照合を直接固定）。
    /// </summary>
    public static class HostBeaconMessage
    {
        public const string Magic = "TDVB1";

        /// <summary>ビーコン payload を組み立てる。</summary>
        public static byte[] Build(ushort ngoPort, string sceneHash) =>
            Encoding.UTF8.GetBytes($"{Magic}|{ngoPort}|{sceneHash}");

        /// <summary>
        /// 受信文字列をパースする。Magic 不一致・要素不足・非数値ポートは false。
        /// hashMatches = beacon 側 hash 空 or localSceneHash 空 or 一致。
        /// </summary>
        public static bool TryParse(string text, string localSceneHash, out ushort port, out bool hashMatches)
        {
            port = 0;
            hashMatches = false;
            var parts = text.Split('|');
            if (parts.Length < 2 || parts[0] != Magic) return false;
            if (!ushort.TryParse(parts[1], out port)) return false;
            string hash = parts.Length >= 3 ? parts[2] : "";
            hashMatches = string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(localSceneHash) || hash == localSceneHash;
            return true;
        }
    }
}
