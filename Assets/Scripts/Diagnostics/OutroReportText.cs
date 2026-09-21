#nullable enable
using System.Globalization;
using FixedCamVr.Streaming;

namespace FixedCamVr.Diagnostics
{
    /// <summary>終幕の観測記録。押下回数は正解数ではなく、分母を超えても保持する。</summary>
    public static class OutroReportText
    {
        public static string Header(ShowLang lang) => lang switch
        {
            ShowLang.En => "OBSERVATION RECORD",
            ShowLang.Fr => "RELEVÉ D’OBSERVATION",
            _ => "観測記録",
        };

        public static string Title(ShowEndingOutcome outcome, ShowLang lang)
        {
            if (outcome == ShowEndingOutcome.Released)
                return lang == ShowLang.En ? "RETURN CONFIRMED" : lang == ShowLang.Fr ? "RETOUR CONFIRMÉ" : "帰還確認";
            if (outcome == ShowEndingOutcome.Trapped)
                return lang == ShowLang.En ? "RETURN IMPOSSIBLE" : lang == ShowLang.Fr ? "RETOUR IMPOSSIBLE" : "帰還不能";
            return lang == ShowLang.En ? "SURVEY INTERRUPTED" : lang == ShowLang.Fr ? "ENQUÊTE INTERROMPUE" : "調査中断";
        }

        public static string Body(ShowEndingOutcome outcome, ShowLang lang)
        {
            if (outcome == ShowEndingOutcome.Released)
                return lang == ShowLang.En ? "Final report received.\nThe curse has been lifted."
                    : lang == ShowLang.Fr ? "Dernier signalement reçu.\nLa malédiction est levée."
                    : "最後の報告を受信しました。\n呪いの解除を確認しました。";
            if (outcome == ShowEndingOutcome.Trapped)
                return lang == ShowLang.En ? "Final report not received.\nYou remain a doll. There is no return."
                    : lang == ShowLang.Fr ? "Dernier signalement non reçu.\nVous restez une poupée. Sans retour."
                    : "最後の報告を受信できませんでした。\nあなたは人形のまま戻れなくなりました。";
            return lang == ShowLang.En ? "The survey ended before a conclusion.\nReturn status could not be confirmed."
                : lang == ShowLang.Fr ? "L’enquête n’a pas pu aboutir.\nLe retour n’a pas été confirmé."
                : "調査は途中で終了しました。\n帰還の判定は記録されていません。";
        }

        public static string CountLabelOf(ShowLang lang) => lang == ShowLang.En ? "REPORTS / ANOMALIES"
            : lang == ShowLang.Fr ? "SIGNALEMENTS / ANOMALIES" : "報告回数 ／ 異常総数";
        public static string Note(ShowLang lang) => lang == ShowLang.En ? "Repeat reports are included."
            : lang == ShowLang.Fr ? "Les signalements répétés sont inclus."
            : "同じ異常への繰り返しの報告も含みます。";
        public static string Footer(ShowLang lang) => lang == ShowLang.En ? "Experience complete. Please remove the headset."
            : lang == ShowLang.Fr ? "L’expérience est terminée. Retirez le casque."
            : "体験は終了です。装置を外してください。";
        public static string Ratio(int reports, int total) =>
            CountOf(reports, ShowLang.En) + " / " + (total > 0 ? CountOf(total, ShowLang.En) : "—");

        // Diagnostics compatibility. Runtime supplies the captured denominator and ending.
        public static string Compose(int reports) => Compose(reports, ShowLanguage.Current);
        public static string Compose(int reports, ShowLang lang) =>
            Compose(reports, 0, ShowEndingOutcome.Interrupted, lang);
        public static string Compose(int reports, int total, ShowEndingOutcome outcome, ShowLang lang) =>
            string.Join("\n", Header(lang), Title(outcome, lang), Body(outcome, lang),
                CountLabelOf(lang), Ratio(reports, total), Note(lang), Footer(lang));
        public static string CountOf(int n, ShowLang lang) => lang == ShowLang.Ja ? FullWidth(n)
            : System.Math.Max(0, n).ToString(CultureInfo.InvariantCulture);
        public static string FullWidth(int n)
        {
            const string digits = "０１２３４５６７８９";
            string ascii = System.Math.Max(0, n).ToString(CultureInfo.InvariantCulture);
            var chars = ascii.ToCharArray();
            for (int i = 0; i < chars.Length; i++) chars[i] = digits[chars[i] - '0'];
            return new string(chars);
        }
    }
}
