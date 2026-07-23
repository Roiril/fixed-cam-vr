#nullable enable

namespace TableDuoVr.Hands
{
    /// <summary>
    /// 手役アバターの見た目バリアント（docs/table-duo/hand-appearance-variants.md）。
    /// - Default : Meta の白い手メッシュ（OVRCustomHandPrefab）。従来どおり同期 bone を直接適用。
    /// - Realistic : 購入パックの Male Hand（人間の手）。別リグ命名なのでバインド差分リターゲットで駆動。
    /// - Robot : 購入パックの Robot Hand（機械の手）。同上。
    /// - FullBody : 手だけでなく人役と同じフル Remy アバター。リモートは Remy IK 経路
    ///   （RemoteAvatarView handsOnly=false）で描き、本人には頭を潰した LocalSelfBody を出し、
    ///   片手モードの左手抑制も解除する（提示仕様は人役と完全共通・<see cref="HandPresentation"/>）。
    /// 自分の見た目＝ローカル選択（<see cref="StudyConfig.SelectedHandVariant"/>）。リモート描画＝相手端末の
    /// 申告値（TableDuoPlayer._studyFlags bit2-3 同期・2026-07-10〜。2bit=4 値でちょうど収まる）。
    /// 手役の見た目切替はホスト卓 FacilitatorPanel のボタンのみで、切替が手役本人・人役の視界・
    /// ホスト観戦の全端末に反映される。
    /// </summary>
    public enum HandVariant : byte
    {
        Default = 0,
        Realistic = 1,
        Robot = 2,
        FullBody = 3,
    }

    /// <summary>手役の見た目バリアントの巡回・ラベル（ホスト卓ボタン用の純ロジック）。</summary>
    public static class HandVariantCycle
    {
        /// <summary>バリアント種類数（Default/Realistic/Robot/FullBody）。</summary>
        public const int Count = 4;

        /// <summary>Default→Realistic→Robot→FullBody→Default の順で次のバリアントを返す。</summary>
        public static HandVariant Next(HandVariant v) => (HandVariant)(((byte)v + 1) % Count);

        /// <summary>ホスト卓ボタン用の日本語ラベル（白手 / リアル / ロボ / Remy）。</summary>
        public static string Label(HandVariant v) => v switch
        {
            HandVariant.Realistic => "リアル",
            HandVariant.Robot => "ロボ",
            HandVariant.FullBody => "Remy",
            _ => "白手",
        };
    }

    /// <summary>
    /// 役割×バリアント → 提示状態（自己ボディ / 左手抑制 / 白手可視 / リモート描画形態）の純ロジック。
    /// FullBody は「人役と同じ提示仕様」を手役へ複製する機能なので、判定を 1 箇所に集約して
    /// owner 側（TableDuoPlayer / LocalVariantHand）と受信側（リモート view 形態）で共有する。
    /// 切替のたびにこの述語群で冪等リコンサイルする＝FullBody から戻せば従来の手だけ挙動へ完全復元。
    /// </summary>
    public static class HandPresentation
    {
        /// <summary>一人称自己ボディ（頭ボーンを潰した Remy・ローカル pose 駆動）を出すか。
        /// 人役は従来どおり ShowSelfBody、手役は FullBody のときだけ同条件で出す
        /// （tdv_selfbody=off なら人役同様に自己ボディ無し＝白手が残る）。</summary>
        public static bool SelfBodyActive(StudyConfig.Role role, HandVariant variant, bool showSelfBody)
            => showSelfBody && (role == StudyConfig.Role.Full
                || (role == StudyConfig.Role.Hand && variant == HandVariant.FullBody));

        /// <summary>片手モードの左手抑制（pose 非送信 + ローカル非表示）。
        /// FullBody 中は人役と同じ両手トラッキングにするため OneHandMode でも解除する。</summary>
        public static bool SuppressLeftHand(StudyConfig.Role role, bool oneHandMode, HandVariant variant)
            => role == StudyConfig.Role.Hand && oneHandMode && variant != HandVariant.FullBody;

        /// <summary>ローカル Meta 白手メッシュを見せるか。Realistic/Robot はパック手が代替、
        /// FullBody は自己ボディの Remy 手が代替（selfBody off のときだけ白手を残す＝人役と同じ規則）。</summary>
        public static bool WhiteHandVisible(HandVariant variant, bool showSelfBody)
            => variant == HandVariant.Default
                || (variant == HandVariant.FullBody && !showSelfBody);

        /// <summary>リモート描画を「手だけ」形態にするか。手役でも FullBody 申告中は
        /// 人役と同じフル（Remy IK）形態で描く。人役は常にフル。</summary>
        public static bool RemoteHandsOnly(StudyConfig.Role role, HandVariant declaredVariant)
            => role == StudyConfig.Role.Hand && declaredVariant != HandVariant.FullBody;
    }
}
