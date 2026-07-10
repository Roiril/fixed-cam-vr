#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 手役アバターの手メッシュの見た目バリアント（docs/table-duo/hand-appearance-variants.md）。
    /// - Default : Meta の白い手メッシュ（OVRCustomHandPrefab）。従来どおり同期 bone を直接適用。
    /// - Realistic : 購入パックの Male Hand（人間の手）。別リグ命名なのでバインド差分リターゲットで駆動。
    /// - Robot : 購入パックの Robot Hand（機械の手）。同上。
    /// 自分の手＝ローカル選択（<see cref="StudyConfig.SelectedHandVariant"/>）。リモート描画＝相手端末の
    /// 申告値（TableDuoPlayer._studyFlags bit2-3 同期・2026-07-10〜）で、Y トグルが全端末の見た目に反映される。
    /// </summary>
    public enum HandVariant : byte
    {
        Default = 0,
        Realistic = 1,
        Robot = 2,
    }
}
