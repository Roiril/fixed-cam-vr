#nullable enable
namespace TableDuoVr.Net
{
    /// <summary>
    /// 起動時の host/client/discover 決定（純関数）。優先順:
    ///   tdv_mode フラグ &gt; Inspector 焼き込み。client 指定でも IP 無しなら自動発見へ縮退。
    ///   フラグ一切無し + Inspector=None なら自動発見が既定。
    /// <see cref="ConnectionManager.ResolveAutoMode"/> のロジックを切り出す（behavior-preserving）。
    /// enum は <see cref="ConnectionManager"/> ネストのまま参照する（勝手にトップレベル昇格しない）。
    /// </summary>
    public static class AutoModeResolver
    {
        public static (ConnectionManager.AutoMode mode, string? ip) Resolve(
            ConnectionManager.AutoMode inspectorMode, string? modeFlag, string? ipFlag)
        {
            var mode = inspectorMode;
            string? ip = ipFlag;
            if (string.IsNullOrEmpty(ip)) ip = null;

            if (modeFlag == "host") mode = ConnectionManager.AutoMode.Host;
            // client 指定でも IP 無しなら自動発見（IP を調べて打つ必要をなくす）
            else if (modeFlag == "client") mode = ip != null ? ConnectionManager.AutoMode.Client : ConnectionManager.AutoMode.Discover;
            // フラグ一切無し（＝Quest ランチャーから普通に開いた）は自動発見が既定。
            // Inspector で Host/Client を焼き込んだシーン（L0 検証等）は従来どおりそちらが勝つ
            else if (modeFlag == null && mode == ConnectionManager.AutoMode.None) mode = ConnectionManager.AutoMode.Discover;

            return (mode, ip);
        }
    }
}
