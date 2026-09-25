#nullable enable
using System.Globalization;
using FixedCamVr.Streaming;

namespace FixedCamVr.Diagnostics
{
    /// <summary>エンドロール冒頭に出す結末名と報告数。件数から結末を推測しない。</summary>
    public static class OutroReportText
    {
        public static string Title(ShowEndingOutcome outcome, ShowLang lang)
        {
            if (outcome == ShowEndingOutcome.Released)
                return lang == ShowLang.En ? "Return End" : lang == ShowLang.Fr ? "Fin retour" : "帰還End";
            if (outcome == ShowEndingOutcome.Trapped)
                return lang == ShowLang.En ? "Doll End" : lang == ShowLang.Fr ? "Fin poupée" : "人形End";
            return lang == ShowLang.En ? "Interrupted" : lang == ShowLang.Fr ? "Interrompu" : "中断";
        }

        public static string CountLabelOf(ShowLang lang) => lang == ShowLang.En ? "Reports"
            : lang == ShowLang.Fr ? "Signalements" : "報告数";

        public static string CountLine(int reports, ShowLang lang) =>
            CountLabelOf(lang) + " " + CountOf(reports, lang);

        // Diagnostics compatibility. These strings remain callable but are not placed on the ending lead.
        public static string Header(ShowLang lang) => lang switch
        {
            ShowLang.En => "OBSERVATION RECORD",
            ShowLang.Fr => "RELEVÉ D’OBSERVATION",
            _ => "観測記録",
        };

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

        public static string Note(ShowLang lang) => lang == ShowLang.En ? "Repeat reports are included."
            : lang == ShowLang.Fr ? "Les signalements répétés sont inclus."
            : "同じ異常への繰り返しの報告も含みます。";
        public static string Footer(ShowLang lang) => lang == ShowLang.En ? "Experience complete. Please remove the headset."
            : lang == ShowLang.Fr ? "L’expérience est terminée. Retirez le casque."
            : "体験は終了です。装置を外してください。";
        public static string Ratio(int reports, int total) =>
            CountOf(reports, ShowLang.En) + " / " + (total > 0 ? CountOf(total, ShowLang.En) : "—");
        public static string Compose(int reports) => Compose(reports, ShowLanguage.Current);
        public static string Compose(int reports, ShowLang lang) =>
            Compose(reports, 0, ShowEndingOutcome.Interrupted, lang);
        public static string Compose(int reports, int total, ShowEndingOutcome outcome, ShowLang lang) =>
            string.Join("\n", Title(outcome, lang), CountLine(reports, lang));

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
