#nullable enable
using System;
using UnityEngine;

namespace TableDuoVr.Hands
{
    /// <summary>
    /// tdv_* 起動フラグ（Android intent extras / PC コマンドライン）の一元パース。
    ///
    /// 設定の優先順（ここが唯一の定義。study-design.md §2）:
    ///   ConnectionManager の Inspector 既定（Editor 検証用）
    ///     &lt; 起動フラグ（本クラス。実機 adb / CLI — 調査セッションの正）
    /// StudyConfig への書き込みは ConnectionManager.Awake が「Inspector → Apply()」の順で 1 回だけ行う。
    /// 各層は StudyConfig を読むだけ（フラグを直接読まない）。
    /// </summary>
    public static class StudyLaunchFlags
    {
        /// <summary>intent extra（実機）/ コマンドライン引数（PC）を読む。無ければ null。</summary>
        public static string? Get(string extraKey, string cmdKey)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", extraKey);
            }
            catch (Exception)
            {
                return null;
            }
#else
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == cmdKey) return args[i + 1];
            }
            return null;
#endif
        }

        /// <summary>
        /// tdv_role / tdv_marker / tdv_hands / tdv_pid / tdv_pair / tdv_preplace / tdv_hand を
        /// StudyConfig へ上書き適用する（無指定の項目は既存値＝Inspector 既定を保持）。
        /// </summary>
        public static void Apply()
        {
            string? role = Get("tdv_role", "-tdvRole");
            if (role == "full") StudyConfig.ForcedRole = StudyConfig.Role.Full;
            else if (role == "hand") StudyConfig.ForcedRole = StudyConfig.Role.Hand;
            else if (role == "spectator") StudyConfig.ForcedRole = StudyConfig.Role.Spectator;
            if (role != null) StudyConfig.LaunchedWithStudyFlags = true;

            string? marker = Get("tdv_marker", "-tdvMarker");
            if (marker == "on") StudyConfig.ShowHeadMarker = true;
            else if (marker == "off") StudyConfig.ShowHeadMarker = false;

            string? hands = Get("tdv_hands", "-tdvHands");
            if (hands == "one") StudyConfig.OneHandMode = true;
            else if (hands == "two") StudyConfig.OneHandMode = false;

            // 参加者ID / ペアID（紙記録と機械的に突合・取り違え防止）。指定があれば調査セッション扱い。
            string? pid = Get("tdv_pid", "-tdvPid");
            if (!string.IsNullOrEmpty(pid)) { StudyConfig.ParticipantId = pid!; StudyConfig.LaunchedWithStudyFlags = true; }
            string? pair = Get("tdv_pair", "-tdvPair");
            if (!string.IsNullOrEmpty(pair)) { StudyConfig.PairId = pair!; StudyConfig.LaunchedWithStudyFlags = true; }

            // 診断: 各席に静的アバターを先置き（描画/疎通/トラッキングの段階切り分け）
            string? preplace = Get("tdv_preplace", "-tdvPreplace");
            if (preplace == "on") StudyConfig.PreplaceAvatars = true;
            else if (preplace == "off") StudyConfig.PreplaceAvatars = false;

            // 手メッシュの見た目（default=Meta白手 / realistic=人間の手 / robot=機械の手）。
            // 別名も受ける（male/human/skin→realistic、meta/simple→default）。指定で調査セッション扱い。
            string? hand = Get("tdv_hand", "-tdvHand");
            if (hand != null)
            {
                StudyConfig.SelectedHandVariant = hand switch
                {
                    "realistic" or "male" or "human" or "skin" => HandVariant.Realistic,
                    "robot" => HandVariant.Robot,
                    _ => HandVariant.Default,
                };
                StudyConfig.LaunchedWithStudyFlags = true;
            }

            if (StudyConfig.LaunchedWithStudyFlags)
            {
                Debug.Log($"[TableDuo] StudyConfig: role={StudyConfig.ForcedRole} marker={StudyConfig.ShowHeadMarker} oneHand={StudyConfig.OneHandMode} hand={StudyConfig.SelectedHandVariant} pid={StudyConfig.ParticipantId} pair={StudyConfig.PairId}");
            }
        }
    }
}
