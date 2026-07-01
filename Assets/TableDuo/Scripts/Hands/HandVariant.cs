#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 手役アバターの手メッシュの見た目バリアント（docs/table-duo/hand-appearance-variants.md）。
    /// - Default : Meta の白い手メッシュ（OVRCustomHandPrefab）。従来どおり同期 bone を直接適用。
    /// - Realistic : 購入パックの Male Hand（人間の手）。別リグ命名なのでバインド差分リターゲットで駆動。
    /// - Robot : 購入パックの Robot Hand（機械の手）。同上。
    /// 見た目はネット同期しない＝各クライアントのローカル表示選択（<see cref="StudyConfig.SelectedHandVariant"/>）。
    /// </summary>
    public enum HandVariant : byte
    {
        Default = 0,
        Realistic = 1,
        Robot = 2,
    }
}
