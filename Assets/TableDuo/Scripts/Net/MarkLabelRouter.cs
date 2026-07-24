#nullable enable
using System;

namespace TableDuoVr.Net
{
    /// <summary>ファシリテータ mark の分類結果（遠隔ディスパッチ先）。</summary>
    public enum MarkAction
    {
        LogOnly,
        ResetBoard,
        GameSwitch,
        AlgoDeal,
        BandidoDeal,
        RecToggle,
    }

    /// <summary>
    /// ファシリテータ mark（curl ?label=）のルーティング（純関数）。分類と 200 字トランケートだけを担う。
    /// dispatch 先（BoardReset/GameSwitcher/AlgoDealer/BandidoDealer）の実行は
    /// <see cref="FacilitatorMarkServer"/> に残す（scope 外の MonoBehaviour）。
    /// </summary>
    public static class MarkLabelRouter
    {
        /// <summary>label を分類する。game_ 接頭時は gameId=label.Substring(5)、それ以外は空文字。</summary>
        public static MarkAction Classify(string label, out string gameId)
        {
            gameId = "";
            // 特別ラベル: 盤面リセット（ラウンド/ブロック跨ぎで卓上を初期配置へ）
            if (label == "reset_board") return MarkAction.ResetBoard;
            // 特別ラベル: ボドゲ切替（reset_board と同じ遠隔導線）。curl ?label=game_algo
            if (label.StartsWith("game_", StringComparison.Ordinal))
            {
                gameId = label.Substring(5);
                return MarkAction.GameSwitch;
            }
            // 特別ラベル: アルゴ完全ランダム配り直し
            if (label == "algo_deal") return MarkAction.AlgoDeal;
            // 特別ラベル: バンディド完全ランダム配り直し
            if (label == "bandido_deal") return MarkAction.BandidoDeal;
            // 特別ラベル: 映像記録（俯瞰+人役 FPV）の開始/停止トグル
            if (label == "rec_toggle") return MarkAction.RecToggle;
            return MarkAction.LogOnly;
        }

        /// <summary>巨大 label による1行肥大化・破損を防ぐ（200 字上限）。</summary>
        public static string TruncateLabel(string label) =>
            label.Length > 200 ? label.Substring(0, 200) : label;
    }
}
