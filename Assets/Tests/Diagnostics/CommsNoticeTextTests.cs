#nullable enable

using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// AIエージェントからの連絡の文面が、<b>枠にも打鍵にも収まっているか</b>。
    ///
    /// ⚠ ここが守るのは「見た目」ではなく<b>沈黙して壊れる 2 つ</b>:
    /// 行が増えて枠から溢れる／打鍵の間隔が縮んで音が連続音になる。
    /// どちらも <c>menu comms-preview</c> の絵では気づけない
    /// （溢れは枠の外なので写らず、音は録画に映らない）。
    /// </summary>
    public class CommsNoticeTextTests
    {
        [Test]
        public void TutorialAcknowledgement_UsesTheApprovedJapaneseWording()
        {
            Assert.AreEqual("報告を受け取りました",
                            CommsPanel.NoticeText(CommsNotice.TutorialAccepted, ShowLang.Ja));
        }

        [Test]
        public void OnboardingCharacters_ExistInBundledFont()
        {
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Fonts/JapaneseHud SDF");
            Assert.NotNull(font, "HMD 用フォントを Resources から読めない");

            var onboarding = new[]
            {
                CommsNotice.ControllerDisconnected,
                CommsNotice.ControllerUntracked,
                CommsNotice.ControllerStaff,
                CommsNotice.ControllerConfirmed,
                CommsNotice.Tutorial,
                CommsNotice.TutorialShort,
                CommsNotice.TutorialAccepted,
                CommsNotice.TutorialReconnect,
                CommsNotice.TutorialReminder,
            };
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (CommsNotice notice in onboarding)
            foreach (char c in CommsPanel.NoticeText(notice, lang))
            {
                if (char.IsWhiteSpace(c)) continue;
                Assert.IsTrue(font.HasCharacter(c),
                              $"{ShowLanguage.Code(lang)} / {notice}: U+{(int)c:X4} '{c}' が HMD 用フォントに無い");
            }

            foreach (ShowLang lang in ShowLanguage.All)
            foreach (TitleStartGuidance guidance in System.Enum.GetValues(typeof(TitleStartGuidance)))
            foreach (char c in TitleScreen.StartPromptText(guidance, lang))
            {
                if (char.IsWhiteSpace(c)) continue;
                Assert.IsTrue(font.HasCharacter(c),
                              $"{ShowLanguage.Code(lang)} / {guidance}: U+{(int)c:X4} '{c}' が HMD 用フォントに無い");
            }
        }

        [TestCase(ShowLang.Ja, "異常なしと判定しました")]
        [TestCase(ShowLang.En, "No anomaly was detected.")]
        [TestCase(ShowLang.Fr, "Aucune anomalie n’a été\ndétectée.")]
        public void TakeoverAttemptsOnlyOneDenial(ShowLang lang, string sentence)
        {
            Assert.AreEqual(sentence, CommsPanel.NoticeText(CommsNotice.Takeover, lang));
        }

        [TestCase(ShowLang.Ja, "異常なし")]
        [TestCase(ShowLang.En, "No anomaly")]
        [TestCase(ShowLang.Fr, "Aucune anomalie")]
        public void TakeoverRedPrefixCoversOnlyTheDenial(ShowLang lang, string prefix)
        {
            string sentence = CommsPanel.NoticeText(CommsNotice.Takeover, lang);
            int length = CommsPanel.TakeoverDenialPrefixLength(lang);
            Assert.AreEqual(prefix, sentence.Substring(0, length));
            Assert.Less(length, sentence.Length);
        }

        [TestCase(ShowLang.Ja, "異常を検出しました")]
        [TestCase(ShowLang.En, "An anomaly was detected.")]
        [TestCase(ShowLang.Fr, "Anomalie détectée.")]
        public void SuccessfulReportSaysTheAnomalyWasDetected(ShowLang lang, string sentence)
        {
            Assert.AreEqual(sentence, CommsPanel.NoticeText(CommsNotice.MarkLogged, lang));
        }

        /// <summary>
        /// 1 行に入る全角の数（面の幅 0.76m × 0.92 ÷ 1 文字 1.8°）。
        /// ⚠ <c>menu text-audit</c> が実測で測る値の、机上の目安。
        /// </summary>
        private const float MaxFullWidthPerLine = 14f;

        /// <summary>
        /// 上段が想定している最悪の行数。<b><c>CommsPanel.BodyMaxH</c> はこの行数で決まっている</b>ので、
        /// 増やすならあちらも一緒に上げる（上げないと 1 行ぶん枠から溢れる）。
        /// ⚠ 0096 で①が 4 行になり 4 へ上げたが、0097 で①と①b へ割ったので 3 へ戻した。
        /// </summary>
        private const int MaxLines = 3;

        /// <summary>
        /// 行の幅（全角を 1 とする）。物差しは <b>面の折り返し幅を決めるのと同じもの</b>
        /// （<see cref="HmdTextStyle.LineWidth"/>）— 別々に数えると、テストと実物が
        /// 「どの文面がいちばん長いか」で食い違う。
        ///
        /// ⚠⚠ <b>「非 ASCII ＝ 全角」ではない</b>（2026-09-03 に踏んだ）。フランス語の
        /// アクセント付き（é è à ç …）は U+00C0 以降だが<b>字は半角の Latin</b>なので、
        /// <c>c &lt; 0x80</c> で切ると « L'anomalie a été supprimée. » が
        /// 15 文字ぶんと数えられて、実際は収まっているのに落ちる。
        /// </summary>
        private static float FullWidth(string line) => HmdTextStyle.LineWidth(line);

        /// <summary>
        /// 実際に画へ出る文面ぜんぶ。
        /// ⚠⚠ <b><see cref="CommsPanel.LongestNoticeText"/> だけを測ってはいけない</b>
        /// （2026-08-17・<c>canon/LEDGER.md</c> 0079）。あれは<b>いちばん長い行</b>を持つ文面で、
        /// <b>いちばん行数が多い文面とは限らない</b>。①から自己紹介を外して⓪b を足した時、
        /// 最長行は①・最大行数は⓪b になった ＝ 片方しか測らないと溢れを見逃す。
        /// </summary>
        /// <remarks>
        /// ⚠⚠ <b>3 言語ぶん測る</b>（2026-09-03・言語選択）。Latin は 1 文字が半角なので、
        /// 同じ内容でも<b>行の文字数は倍近くになる</b> — 日本語だけ測って通しても、
        /// English / Français のときだけ枠から出る（実機で 1 言語だけ壊れる形）。
        /// </remarks>
        private static System.Collections.Generic.IEnumerable<(ShowLang lang, CommsNotice notice, string text)>
            AllNotices()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
            {
                string t = CommsPanel.NoticeText(n, lang);
                if (!string.IsNullOrEmpty(t)) yield return (lang, n, t);
            }
        }

        private static System.Collections.Generic.IEnumerable<(ShowLang lang, string text)> AllTexts()
        {
            foreach ((ShowLang lang, CommsNotice notice, string t) in AllNotices())
                yield return (lang, t);
        }

        [Test]
        public void EveryLine_FitsTheBand()
        {
            foreach ((ShowLang lang, string body) in AllTexts())
            foreach (string line in body.Split('\n'))
            {
                Assert.LessOrEqual(FullWidth(line), MaxFullWidthPerLine,
                                   $"[{ShowLanguage.Code(lang)}]「{line}」が 1 行に入らない（折り返して行が増える）");
            }
        }

        [Test]
        public void LineCount_StaysWithinTheBandHeight()
        {
            foreach ((ShowLang lang, string body) in AllTexts())
            {
                Assert.LessOrEqual(body.Split('\n').Length, MaxLines,
                                   $"[{ShowLanguage.Code(lang)}]「{body.Replace("\n", "／")}」で行が増えた。"
                                   + "CommsPanel.BodyMaxH も一緒に上げること");
            }
        }

        /// <summary>
        /// <b>どの言語でも 8 通そろっている。</b> 1 通でも空だと、その言語のときだけ
        /// <b>面が開いて何も書かれずに畳まれる</b>（画には「開いて閉じた」しか出ないので、
        /// 走行の絵を開いても抜けに気づけない）。
        /// </summary>
        [Test]
        public void EveryLanguage_HasEveryNotice()
        {
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
            {
                if (string.IsNullOrEmpty(CommsPanel.NoticeText(n, ShowLang.Ja))) continue;
                foreach (ShowLang lang in ShowLanguage.All)
                    Assert.That(CommsPanel.NoticeText(n, lang), Is.Not.Empty,
                                $"{n} の {ShowLanguage.Code(lang)} が無い");
            }
        }

        /// <summary>
        /// <see cref="CommsPanel.LongestNoticeText"/> が本当に最長行を持っているか。
        /// <b>面の折り返し幅はこの 1 本から組まれる</b>ので、ここが嘘になると
        /// 実行時に別の文面だけが枠から出る。
        /// </summary>
        [Test]
        public void LongestNoticeText_ReallyHasTheLongestLine()
        {
            float best = 0f;
            foreach ((ShowLang _, string body) in AllTexts())
            foreach (string line in body.Split('\n')) best = System.Math.Max(best, FullWidth(line));

            float declared = 0f;
            foreach (string line in CommsPanel.LongestNoticeText.Split('\n'))
                declared = System.Math.Max(declared, FullWidth(line));

            Assert.AreEqual(best, declared, 0.01f,
                            "LongestNoticeText より長い行を持つ文面がある（面の折り返し幅がその文面で足りない）");
        }

        /// <summary>
        /// ⚠⚠ <b>打ち終わりの上限（<c>MaxTypeSec</c>）が効くと、打鍵の間隔が <c>CharsPerSec</c> より
        /// 短くなる。</b> その 1 通だけ速く打つ装置になり、切り出した打鍵の尺（60ms）を割れば
        /// <b>1 発ずつが繋がって連続音</b>にもなる（`rules/sound-design.md` §4）。
        /// <b>画は普通に出るので、音を聴くまで気づけない。</b>
        ///
        /// ⚠ <b>全文面を測る</b>（2026-08-19）。<c>LongestNoticeText</c> は<b>いちばん長い行</b>を
        /// 持つ文面で、<b>いちばん文字数が多い文面とは限らない</b>。①が 4 行 48 文字になったとき、
        /// 最長行は⓪a・最多文字は①に分かれた ＝ 1 本だけ測ると上限の圧縮を見逃す。
        /// </summary>
        [Test]
        public void EveryNotice_TypesAtTheDeviceSpeed()
        {
            ShowLang before = ShowLanguage.Current;
            try
            {
                foreach ((ShowLang lang, CommsNotice notice, string body) in AllNotices())
                {
                    // ⚠ すっと浮かぶ連絡（③a）は 1 字も打たないので、速さの物差しを当てる相手ではない
                    //    （`canon/LEDGER.md` 0168）。
                    if (CommsCueLogic.DeliveryOf(notice) != CommsDelivery.Typed) continue;
                    // ⚠ 速さは言語で違う（日本語 12 / Latin 18・0149）。**その言語で測る**。
                    ShowLanguage.Select(lang);
                    var logic = new CommsPanelLogic();
                    logic.Begin(body.Length);
                    float step = logic.TypeSec / body.Length;
                    Assert.AreEqual(1f / CommsPanelLogic.CharsPerSecFor(lang), step, 0.0005f,
                                    $"[{ShowLanguage.Code(lang)}]「{body.Replace("\n", "／")}」"
                                  + $"（{body.Length} 文字）が "
                                  + $"{step * 1000f:0} ms 間隔。装置の打鍵はその言語で 1 つの速さ"
                                  + "（CommsPanelLogic.MaxTypeSec を上げるか文面を短くする）");
                }
            }
            finally { ShowLanguage.Select(before); }
        }
    }
}
