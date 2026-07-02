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

        /// <summary>手役アバターの手メッシュの見た目（Default=Meta白手 / Realistic=人間の手 / Robot=機械の手）。
        /// **正式な調査条件（within-pair 因子・2026-07-02 決定）**: ブロックごとに tdv_hand 起動フラグで固定し、
        /// セッション中の切替は禁止（HandVariantWatcher が調査フラグ起動時にトグルを無効化。切替は CSV に刻まれる）。
        /// ネット非同期＝ローカル表示選択。変更は <see cref="SetHandVariant"/> 経由にすること
        /// （描画側が <see cref="HandVariantChanged"/> で再構築する）。</summary>
        public static HandVariant SelectedHandVariant;

        /// <summary>手バリアントが切り替わった。ローカル手 / リモート手の描画側がメッシュを作り直すために購読する。</summary>
        public static event System.Action? HandVariantChanged;

        /// <summary>手バリアントを設定し、変わった時だけ購読者へ通知する（同値なら再構築しない）。</summary>
        public static void SetHandVariant(HandVariant v)
        {
            if (v == SelectedHandVariant) return;
            SelectedHandVariant = v;
            HandVariantChanged?.Invoke();
        }

        /// <summary>Default→Realistic→Robot→Default と巡回（実機トグル用）。</summary>
        public static void CycleHandVariant()
        {
            var next = SelectedHandVariant switch
            {
                HandVariant.Default => HandVariant.Realistic,
                HandVariant.Realistic => HandVariant.Robot,
                _ => HandVariant.Default,
            };
            SetHandVariant(next);
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
            SelectedHandVariant = HandVariant.Default;
            HandVariantChanged = null;
        }
    }
}
