#nullable enable

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>旧メニュー名から現行の通信面プレビューを呼ぶ互換窓口。</summary>
    public static class CommsPossessionPreview
    {
        public static void Run() => CommsRevisionPreview.Run();
    }
}
