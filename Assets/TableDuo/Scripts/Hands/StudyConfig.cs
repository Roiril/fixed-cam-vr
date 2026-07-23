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
            // 観戦者（第三者視点）。席を持たず・アバター無し・pose 非送信。PC（Editor Play）で
            // 両プレイヤーを俯瞰観察するためのロール。tdv_role=spectator で起動。
            Spectator = 2,
        }

        /// <summary>起動フラグで指定された自分の役割。null なら従来規則（host=Full / client=Hand）。</summary>
        public static Role? ForcedRole;

        /// <summary>手役アバターの頭マーカー。調査既定は OFF（曖昧さ自体が RQ2/RQ3 の現象）。</summary>
        public static bool ShowHeadMarker;

        /// <summary>片手モード（右手のみ）。調査既定 ON。</summary>
        public static bool OneHandMode = true;

        /// <summary>tdv_* 起動フラグ経由で起動された＝調査セッション（デバッグ GUI を隠す等）。</summary>
        public static bool LaunchedWithStudyFlags;

        /// <summary>参加者ID（tdv_pid）。CSV/リプレイのヘッダに刻み、紙記録との突合・取り違え防止に使う。空=未指定。</summary>
        public static string ParticipantId = "";

        /// <summary>ペアID（tdv_pair）。2台の記録を機械的に紐づける。空=未指定。</summary>
        public static string PairId = "";

        /// <summary>診断: 各席に静的アバターを先置きする（tdv_preplace=on）。描画/疎通/トラッキングの段階切り分け用。
        /// 接続したら静的→ライブに差し替わる。研究本番は false（相手不在時にアバターが居ると体験が変わるため）。</summary>
        public static bool PreplaceAvatars;

        /// <summary>人役の一人称自己アバター表示（tdv_selfbody、既定 on）。自分のローカル pose で駆動する Remy を
        /// 席下にもう1体出し、頭ボーンを潰して視界を塞がず胴/腕/手だけ見せる（身体所有感）。
        /// **既定 on**（2026-07-10 ユーザー採択）: 人役は自分の体が見えるのを標準体験とする。
        /// `tdv_selfbody=off` で明示的に切れる（交絡比較のパイロット用）。
        /// ローカル描画専用＝相手に見える自分（ネット越しの Remy）は不変。条件は _studyFlags bit4 で同期・CSV 記録。</summary>
        public static bool ShowSelfBody = true;

        /// <summary>手役アバターの見た目（Default=Meta白手 / Realistic=人間の手 / Robot=機械の手 /
        /// FullBody=人役と同じフル Remy 化）。
        /// **正式な調査条件（within-pair 因子・2026-07-02 決定）**: ブロックごとに tdv_hand 起動フラグで固定する。
        /// セッション中の変更はホスト（実験者卓）強制のみ（<see cref="ApplyForcedVariant"/>）。参加者トグルは撤去済み（2026-07-18）。
        /// 自分の手＝この値。リモート描画＝相手の申告値（_studyFlags 同期）が優先される。
        /// 変更は <see cref="ApplyForcedVariant"/> 経由にすること（描画側が <see cref="HandVariantChanged"/> で
        /// 再構築し、owner の TableDuoPlayer が申告値を書き直して相手端末の描画も追従する）。
        /// 手役の手の見た目を変える唯一の導線はホスト卓 FacilitatorPanel の巡回ボタン（参加者トグルは撤去済み）。</summary>
        public static HandVariant SelectedHandVariant;

        /// <summary>協調配置課題（Phase 4）の目標配置パネルを手役に表示する（tdv_pattern=on、既定 OFF）。
        /// 課題を使わない通常の設営/体験では「空中にカラフルな板が浮いてる」と誤認されるため既定で出さない
        /// （2026-07-12 実機指摘）。</summary>
        public static bool ShowPatternPanel;

        /// <summary>手バリアントが tdv_hand 起動フラグで固定された（＝調査条件として指定された）ことを表す申告フラグ。
        /// かつては参加者の Y トグル（HandVariantWatcher）をこの時だけ無効化していたが、
        /// トグル自体を撤去したため現在は gating に使われない（ホスト強制のみ・2026-07-18）。
        /// CSV/リプレイに「条件固定で起動したか」を残す記録目的で保持している。</summary>
        public static bool HandVariantLockedByFlag;

        /// <summary>手バリアントが切り替わった。ローカル手 / リモート手の描画側がメッシュを作り直すために購読する。</summary>
        public static event System.Action? HandVariantChanged;

        /// <summary>ホスト（実験者）強制で手バリアントを設定する。
        /// <see cref="HandVariantLockedByFlag"/> に**関係なく**貫通して適用する
        /// （ホスト強制は参加者ロックより優先。参加者トグルはロック中に無効化されるが、実験者卓の指示は通す）。
        /// TableDuoPlayer の _forcedVariant（server→owner 指示チャネル）から呼ばれる。</summary>
        public static void ApplyForcedVariant(HandVariant v)
        {
            if (v == SelectedHandVariant) return;
            SelectedHandVariant = v;
            HandVariantChanged?.Invoke();
        }

        // domain-reload を切った Play では static が前回 Play の条件を引き継ぐ。Editor で役割/条件を
        // 変えて再生したのに古い値で走る事故を防ぐため毎 Play 既定へ戻す（ConnectionManager.Awake が再設定）。
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ForcedRole = null;
            ShowHeadMarker = false;
            OneHandMode = true;
            LaunchedWithStudyFlags = false;
            ParticipantId = "";
            PairId = "";
            PreplaceAvatars = false;
            ShowSelfBody = true;
            SelectedHandVariant = HandVariant.Default;
            HandVariantLockedByFlag = false;
            ShowPatternPanel = false;
            HandVariantChanged = null;
        }
    }
}
