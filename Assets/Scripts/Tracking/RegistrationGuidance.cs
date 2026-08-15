#nullable enable
using System.Globalization;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// HMD 内登録ガイダンスの文言を組み立てる純関数群（UnityEngine 非依存・テスト可能）。
    /// <see cref="CourseRegistrationController"/> が StatusHud へ供給する文字列の生成をここへ切り出し、
    /// フォーマット（進捗バー・ずれの行・Review ヘッダ）を EditMode テストで固定する。
    /// 数値は <see cref="CultureInfo.InvariantCulture"/> で整形して環境ロケール差を消す。
    ///
    /// ⚠⚠ <b>語は「位置合わせ」「×印」「点」「ずれ」の 4 語に固定する</b>
    /// （<c>FixedCamVr.Diagnostics.RecoveryGuidance</c> と同じ規約・同じ理由）。
    /// 廃語 = 登録 / 再登録 / 基準点 / 残差 / 誤差 / マーク / 周回リセット / 砂嵐 / course。
    /// 2026-08-15 まで、<b>同じ 1 枚の面に出るのに片方だけが規約を守っていた</b>
    /// （異常の 2 行は守り、位置合わせのガイダンスは廃語だらけ）。
    ///
    /// ⚠ 書式は <c>FixedCamVr.Diagnostics.HmdTextStyle</c> の規約に従う
    /// （操作は `入力：動作` / 括弧は全角 / 数値と単位のあいだに半角空白 / `⚠` は付けない）。
    ///
    /// ⚠ 文言を変えたら <c>.\tools\unity.ps1 menu hud-font</c> を再実行する
    /// （静的ベイクなので、ここに無い字は実機で豆腐になる）。
    /// </summary>
    public static class RegistrationGuidance
    {
        /// <summary>進捗バーの既定目盛数。</summary>
        public const int DefaultSlots = 5;

        // 進捗バーは**全塗りの 1 種類だけ**を並べ、進んだ分と空きを**色で分ける**。
        // ⚠ 2026-08-15 まで空きに ░（網掛け）を使っていたが、SDF の網目が 1.5° では潰れて
        //   「バー」ではなく「文字列」に見えていた（同じ理由で ▓ → █ の変更を 1 度している）。
        //   同じ字を明暗で並べると、外形の残った 1 本の帯になる。
        private const char Cell = '█';

        // ⚠ この 2 つは `FixedCamVr.Diagnostics.HmdTextStyle` の Ink / InkDim と**同じ色**。
        //   Tracking は Diagnostics を参照できない（依存の向きが逆）ので値を持つが、
        //   `HmdTextStyleTests.TrackingHex_MatchesPalette` が食い違いを落とす。
        /// <summary>進んだ分の色（16 進・地の色）。</summary>
        public const string FilledHex = "D1C7B8";

        /// <summary>空きの色（16 進・地の色を落としたもの）。</summary>
        public const string EmptyHex = "3F3C37";

        /// <summary>対応が要る 1 行の色（16 進・<c>HmdTextStyle.Alert</c> と同じ）。</summary>
        public const string AlertHex = "FF8C66";

        /// <summary>
        /// 進捗 t/hold を <paramref name="slots"/> 目盛のバーへ。境界は [0,1] にクランプ。
        /// <b>TMP のリッチテキストを返す</b>ので、出力先の <c>richText</c> を必ず有効にすること。
        /// </summary>
        public static string ProgressBar(float t, float hold, int slots = DefaultSlots)
        {
            if (slots < 1) slots = 1;
            float p = hold <= 0f ? 1f : t / hold;
            if (p < 0f) p = 0f; else if (p > 1f) p = 1f;
            int filled = (int)System.Math.Round(p * slots, System.MidpointRounding.AwayFromZero);
            if (filled < 0) filled = 0; else if (filled > slots) filled = slots;

            var sb = new System.Text.StringBuilder(slots + 40);
            if (filled > 0) Run(sb, FilledHex, filled);
            if (filled < slots) Run(sb, EmptyHex, slots - filled);
            return sb.ToString();

            static void Run(System.Text.StringBuilder sb, string hex, int n)
            {
                sb.Append("<color=#").Append(hex).Append('>');
                sb.Append(Cell, n);
                sb.Append("</color>");
            }
        }

        /// <summary>
        /// ホールド平均サンプリング中の 2 行。バーが進み具合を出すので<b>秒数は出さない</b>
        /// （`0.3/0.5s` は「経過／目標」なのか「2 種類の時間」なのか一瞬では読めない）。
        /// </summary>
        public static string SamplingLine(float t, float hold)
            => $"計測中\n{ProgressBar(t, hold)}\nかざしたまま静止";

        /// <summary>Verify のずれ 1 行。例: "最大のずれ：5 cm（合格 12 cm 以下）"。</summary>
        public static string ResidualLine(float maxResidualM, float acceptM)
            => $"最大のずれ：{Cm(maxResidualM)} cm（合格 {Cm(acceptM)} cm 以下）";

        /// <summary>
        /// タッチする高さの指示。<paramref name="touchHeightM"/> が 0 なら「床に着ける」、
        /// 正なら「床から N cm の高さ」。<paramref name="label"/> は点の名前（空なら省く）。
        /// </summary>
        public static string TouchInstruction(string? label, float touchHeightM)
        {
            string where = string.IsNullOrEmpty(label) ? "床の×印" : $"「{label}」の床の×印";
            if (touchHeightM <= 0f) return $"{where}にコントローラの先を着けて";
            return $"{where}の上、床から {Cm(touchHeightM)} cm の高さで";
        }

        /// <summary>
        /// Verify の床の高さ 1 行。例: "床の高さ：+8 cm"。ばらつきが大きいときは理由も添える。
        /// </summary>
        public static string FloorLine(float floorY, float spreadM, float spreadWarnM)
        {
            string head = $"床の高さ：{(floorY >= 0f ? "+" : "-")}{Cm(System.Math.Abs(floorY))} cm";
            if (spreadM > spreadWarnM)
                return head + $"（着けていない点があります {Cm(spreadM)} cm ばらつき）";
            return head;
        }

        private static string Cm(float m) => (m * 100f).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// Review ヘッダ 1 行。保存日時・ずれ・点数を示す（旧ファイル等で未記録なら「記録なし」）。
        /// 例: "保存済みの位置合わせ（2026-07-21 14:03／ずれ 5 cm／4 点）"。
        /// ⚠ 区切りは全角 <c>／</c>（UI の区切りは全角に統一。半角のままなのは時刻やゾーン名など
        /// <b>データそのもの</b>の中だけ）。
        /// </summary>
        public static string ReviewHeader(string? savedAtIso, float maxResidualM, int pointCount)
        {
            string saved = string.IsNullOrEmpty(savedAtIso) ? "記録なし" : savedAtIso;
            string res = maxResidualM > 0f ? $"ずれ {Cm(maxResidualM)} cm" : "ずれ 記録なし";
            string pts = pointCount > 0 ? $"{pointCount} 点" : "点数 記録なし";
            return $"保存済みの位置合わせ（{saved}／{res}／{pts}）";
        }
    }
}
