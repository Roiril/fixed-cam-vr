#nullable enable
using System.Globalization;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// HMD 内登録ガイダンスの文言を組み立てる純関数群（UnityEngine 非依存・テスト可能）。
    /// <see cref="CourseRegistrationController"/> が StatusHud へ供給する文字列の生成をここへ切り出し、
    /// フォーマット（進捗バー・残差表示・Review ヘッダ）を EditMode テストで固定する。
    /// 数値は <see cref="CultureInfo.InvariantCulture"/> で整形して環境ロケール差を消す。
    /// </summary>
    public static class RegistrationGuidance
    {
        /// <summary>進捗バーの既定目盛数。</summary>
        public const int DefaultSlots = 5;

        // 進捗バーの塗り / 空セル。全角ブロックで HMD でも視認しやすい。
        private const char Filled = '▓';
        private const char Empty = '░';

        /// <summary>進捗 t/hold を <paramref name="slots"/> 目盛のバー "▓▓▓░░" へ。境界は [0,1] にクランプ。</summary>
        public static string ProgressBar(float t, float hold, int slots = DefaultSlots)
        {
            if (slots < 1) slots = 1;
            float p = hold <= 0f ? 1f : t / hold;
            if (p < 0f) p = 0f; else if (p > 1f) p = 1f;
            int filled = (int)System.Math.Round(p * slots, System.MidpointRounding.AwayFromZero);
            if (filled < 0) filled = 0; else if (filled > slots) filled = slots;
            return new string(Filled, filled) + new string(Empty, slots - filled);
        }

        /// <summary>
        /// ホールド平均サンプリング中の 1 行目。例: "計測中 ▓▓▓░░  0.3/0.5s"。毎フレーム呼ぶ想定。
        /// </summary>
        public static string SamplingLine(float t, float hold)
        {
            float shown = t < 0f ? 0f : (t > hold ? hold : t);
            return $"計測中 {ProgressBar(t, hold)}  {Fmt1(shown)}/{Fmt1(hold)}s\nかざしたまま静止";
        }

        /// <summary>Verify の残差 1 行。例: "最大残差 0.05m（合格 ≤0.12m）"。</summary>
        public static string ResidualLine(float maxResidualM, float acceptM)
            => $"最大残差 {Fmt2(maxResidualM)}m（合格 ≤{Fmt2(acceptM)}m）";

        /// <summary>
        /// Review ヘッダ 1 行。保存日時・残差・点数を示す（旧ファイル等で未記録なら「記録なし」）。
        /// 例: "登録済みの位置合わせを表示中（保存: 2026-07-21 14:03 / 残差 0.05m / 4点）"。
        /// </summary>
        public static string ReviewHeader(string? savedAtIso, float maxResidualM, int pointCount)
        {
            string saved = string.IsNullOrEmpty(savedAtIso) ? "記録なし" : savedAtIso;
            string res = maxResidualM > 0f ? $"{Fmt2(maxResidualM)}m" : "記録なし";
            string pts = pointCount > 0 ? $"{pointCount}点" : "記録なし";
            return $"登録済みの位置合わせを表示中（保存: {saved} / 残差 {res} / {pts}）";
        }

        private static string Fmt1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        private static string Fmt2(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
