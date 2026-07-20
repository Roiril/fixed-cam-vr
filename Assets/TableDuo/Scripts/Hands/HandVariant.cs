#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 手役アバターの手メッシュの見た目バリアント（docs/table-duo/hand-appearance-variants.md）。
    /// - Default : Meta の白い手メッシュ（OVRCustomHandPrefab）。従来どおり同期 bone を直接適用。
    /// - Realistic : 購入パックの Male Hand（人間の手）。別リグ命名なのでバインド差分リターゲットで駆動。
    /// - Robot : 購入パックの Robot Hand（機械の手）。同上。
    /// 自分の手＝ローカル選択（<see cref="StudyConfig.SelectedHandVariant"/>）。リモート描画＝相手端末の
    /// 申告値（TableDuoPlayer._studyFlags bit2-3 同期・2026-07-10〜）。手役の見た目切替はホスト卓 FacilitatorPanel の
    /// 巡回ボタンのみで、切替が手役本人・人役の視界・ホスト観戦の全端末に反映される。
    /// </summary>
    public enum HandVariant : byte
    {
        Default = 0,
        Realistic = 1,
        Robot = 2,
    }

    /// <summary>手役の手の見た目バリアントの巡回（ホスト卓の単一ボタン用の純ロジック）。</summary>
    public static class HandVariantCycle
    {
        /// <summary>バリアント種類数（Default/Realistic/Robot）。</summary>
        public const int Count = 3;

        /// <summary>Default→Realistic→Robot→Default の順で次のバリアントを返す。</summary>
        public static HandVariant Next(HandVariant v) => (HandVariant)(((byte)v + 1) % Count);

        /// <summary>ホスト卓ボタン用の日本語ラベル（白手 / リアル / ロボ）。</summary>
        public static string Label(HandVariant v) => v switch
        {
            HandVariant.Realistic => "リアル",
            HandVariant.Robot => "ロボ",
            _ => "白手",
        };
    }
}
