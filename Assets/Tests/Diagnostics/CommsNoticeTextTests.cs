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
        // ⓪a。「調査を支援する」と職掌を言う。**異常を遮断する権限は、ここでしか渡していない**
        // （博士は「装置に搭載されたエージェントが解析します」までしか言わない）。
        [TestCase(ShowLang.Ja, "申し遅れました。\n私は調査を支援する\nエージェントのスイです。")]
        [TestCase(ShowLang.En, "My apologies.\nI am Sui, the agent\nsupporting your survey.")]
        [TestCase(ShowLang.Fr, "Veuillez m'excuser.\nJe suis Sui, l'agente\nqui assiste votre enquête.")]
        public void GreetingIntroducesSuiAfterTheTutorial(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.Greeting, lang));
        }

        /// <summary>
        /// ⓪a が<b>職掌（調査を支援する）を言う</b>。0124 の逐語で、そこを落とすと
        /// 「遮断します」がどこから来た権限か、体験の最後まで誰も言わないことになる。
        /// </summary>
        [TestCase(ShowLang.Ja, "調査を支援する")]
        [TestCase(ShowLang.En, "supporting your survey")]
        [TestCase(ShowLang.Fr, "assiste votre enquête")]
        public void Greeting_SaysWhatSuiIsThereToDo(ShowLang lang, string role)
        {
            StringAssert.Contains(role, CommsPanel.NoticeText(CommsNotice.Greeting, lang));
        }

        // ③b。日本語は 0365 の逐語。
        [TestCase(ShowLang.Ja, "異常があなたを\n取り込もうとしています\nボタンを押して遮断を…")]
        [TestCase(ShowLang.En, "The anomaly is trying\nto take you in.\nPress the button to block…")]
        [TestCase(ShowLang.Fr, "L'anomalie essaie\nde vous prendre.\nAppuyez pour la bloquer…")]
        public void ClosingPrompt_UsesTheApprovedWording(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.Prompt, lang));
        }

        /// <summary>
        /// ③b は<b>体験者に遮断させる</b>（0363「私が遮断できないから、あなたにボタンを押して遮断してほしい」）。
        /// スイが「私が遮断します」と言うと、それなら自分でしておけ、になる。最後の行は言い切らずに途切れる。
        /// </summary>
        [TestCase(ShowLang.Ja, "ボタンを押して", "私が")]
        [TestCase(ShowLang.En, "Press the button", "I will")]
        [TestCase(ShowLang.Fr, "Appuyez", "Je la")]
        public void ClosingPrompt_HandsTheBlockToTheVisitor(ShowLang lang, string press, string suiDoesIt)
        {
            string body = CommsPanel.NoticeText(CommsNotice.Prompt, lang);
            StringAssert.Contains(press, body);
            StringAssert.DoesNotContain(suiDoesIt, body);
            StringAssert.EndsWith("…", body);
        }

        /// <summary>
        /// 約束（導入）→ 実行（報告の返事）→ 遮断できない（2 周目 C）→ 体験者に渡す（③b）→ 果たす（④）が、
        /// 言語ごとに同じ動詞で通っている。1 か所だけ別の語にすると、同じことを言っていると読めなくなる。
        /// </summary>
        [TestCase(ShowLang.Ja, "遮断")]
        [TestCase(ShowLang.En, "lock")]
        [TestCase(ShowLang.Fr, "loqu")]
        public void ThePromiseToBlock_UsesOneVerbFromTheTutorialToTheEnd(ShowLang lang, string stem)
        {
            foreach (CommsNotice n in new[]
                     {
                         CommsNotice.TutorialReminder, CommsNotice.MarkLogged, CommsNotice.MarkStuck,
                         CommsNotice.Prompt, CommsNotice.MarkCleared,
                     })
                StringAssert.Contains(stem, CommsPanel.NoticeText(n, lang), $"{n} / {ShowLanguage.Code(lang)}");
        }

        /// <summary>
        /// 正直な空振りの返事と、乗っ取り後の赤い嘘は<b>同じ文</b>（0054 と 0244 の逐語は同じ字）。
        /// 字が 1 つでも違うと、「同じ文が嘘になった」と読めない。
        /// </summary>
        [Test]
        public void TheHonestMissAndTheLie_AreTheSameSentence()
        {
            foreach (ShowLang lang in ShowLanguage.All)
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkNothing, lang),
                                CommsPanel.TakeoverLieText(lang), ShowLanguage.Code(lang));
        }

        [Test]
        public void TutorialAcknowledgement_UsesTheApprovedJapaneseWording()
        {
            Assert.AreEqual("報告できました。\n装置は正常です。",
                            CommsPanel.NoticeText(CommsNotice.TutorialAccepted, ShowLang.Ja));
        }

        [TestCase(ShowLang.En, "Report sent.\nDevice operating normally.")]
        [TestCase(ShowLang.Fr, "Signalement envoyé.\nLe dispositif fonctionne.")]
        public void TutorialAcknowledgement_HasTheSameMeaningInLatinLanguages(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.TutorialAccepted, lang));
        }

        /// <summary>
        /// ⓪c は<b>カメラへの接続</b>を告げ、①は<b>開始</b>を告げる。同じ語を 2 回聞かせない。
        /// ⓪c の直後に起きるのはテレビが点いて観測カメラの映像が来ること（0309）で、体験者が調査を始めるのはその後。
        /// ⚠ 「装置を起動します」とは言わない。装置は導入の練習で「装置は正常です。」と言われたばかりで、もう動いている。
        /// </summary>
        [TestCase(ShowLang.Ja, "接続", "廻り始め")]
        [TestCase(ShowLang.En, "Connecting", "start circling")]
        [TestCase(ShowLang.Fr, "Connexion", "tourner")]
        public void ArrivalConnectsTheCameras_AndTheSurveyStartsSeparately(ShowLang lang, string boot, string start)
        {
            string arrived = CommsPanel.NoticeText(CommsNotice.Arrived, lang);
            string begin = CommsPanel.NoticeText(CommsNotice.Begin, lang);
            StringAssert.Contains(boot, arrived);
            StringAssert.Contains(start, begin);
            StringAssert.DoesNotContain(start, arrived, "⓪c と①が同じ語で始まりを告げている");
        }

        /// <summary>
        /// 本編の押し方（①b）は<b>キー名を出さない</b>（0096 の逐語は「ボタンを長押ししてください」）。
        /// 装置の面に入力機器の名前が出ると、調査の記録ではなくゲームの操作説明に見える。
        /// ⚠ 練習（<c>Tutorial</c>）だけは例外 — 実際に押させるので、どれを押すかが要る。
        /// </summary>
        [Test]
        public void OnlyTheRehearsal_NamesTheButtons()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (CommsNotice n in new[] { CommsNotice.BeginHow, CommsNotice.TutorialReminder })
            {
                string body = CommsPanel.NoticeText(n, lang);
                StringAssert.DoesNotContain("X", body, $"{n} / {ShowLanguage.Code(lang)}");
                StringAssert.DoesNotContain("Y", body, $"{n} / {ShowLanguage.Code(lang)}");
            }
        }

        [TestCase(ShowLang.Ja, "装置の動作を確認します。\nXかYを1秒間、\n押し続けてください。")]
        [TestCase(ShowLang.En, "Checking the device.\nHold X or Y\nfor one second.")]
        [TestCase(ShowLang.Fr, "Vérification du dispositif.\nMaintenez X ou Y\npendant une seconde.")]
        public void Tutorial_ExplainsThatItChecksTheDevice(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.Tutorial, lang));
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
                CommsNotice.Greeting,
            };
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (CommsNotice notice in onboarding)
            foreach (char c in CommsPanel.NoticeText(notice, lang))
            {
                if (char.IsWhiteSpace(c)) continue;
                Assert.IsTrue(font.HasCharacter(c),
                              $"{ShowLanguage.Code(lang)} / {notice}: U+{(int)c:X4} '{c}' が HMD 用フォントに無い");
            }

            // 嘘の一文の書き換え先と、判定を持たない返事（0232）は新しい字を持たないはず（フォントの焼き直し不要の根拠）。
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string body in new[] { CommsPanel.TakeoverLieText(lang), CommsPanel.NoticeText(CommsNotice.MarkAnalyzing, lang) })
            foreach (char c in body)
            {
                if (char.IsWhiteSpace(c)) continue;
                Assert.IsTrue(font.HasCharacter(c),
                              $"{ShowLanguage.Code(lang)} / 0232: U+{(int)c:X4} '{c}' が HMD 用フォントに無い");
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

        [Test]
        public void CursedTextHasABundledHorrorFont()
        {
            TMP_FontAsset horror = Resources.Load<TMP_FontAsset>("Fonts/CommsHorror SDF");
            Assert.NotNull(horror, "怖い本文用フォントを Resources から読めない");
        }

        [TestCase(ShowLang.Ja, "異常を検出しました")]
        [TestCase(ShowLang.En, "An anomaly was detected.")]
        [TestCase(ShowLang.Fr, "Anomalie détectée.")]
        public void TakeoverShowsTheTruthfulDetectionFirst(ShowLang lang, string truth)
        {
            Assert.AreEqual(truth, CommsPanel.NoticeText(CommsNotice.Takeover, lang));
            // 書き換わる前の 1 行は、当たりの返事の 1 行目と 1 字も違わない（「いつもの返事」に見えてから嘘になる）。
            Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkLogged, lang).Split('\n')[0], truth);
        }

        [TestCase(ShowLang.Ja, "異常は検出されませんでした")]
        [TestCase(ShowLang.En, "No anomaly was detected.")]
        [TestCase(ShowLang.Fr, "Aucune anomalie détectée.")]
        public void TakeoverLieUsesTheApprovedWording(ShowLang lang, string lie)
        {
            string truth = CommsPanel.NoticeText(CommsNotice.Takeover, lang);
            Assert.AreEqual(lie, CommsPanel.TakeoverLieText(lang));
            Assert.AreNotEqual(truth, lie);
            Assert.AreEqual(1, lie.Split('\n').Length, "嘘は 1 行（真実と同じ行数で、同じ場所に書き換わる）");
            Assert.LessOrEqual(FullWidth(lie), MaxFullWidthPerLine, "嘘も 1 行に入る");
        }

        [TestCase(ShowLang.Ja, "解析が、\n終わりません。")]
        [TestCase(ShowLang.En, "The analysis\nwill not finish.")]
        [TestCase(ShowLang.Fr, "L'analyse\nne se termine pas.")]
        public void MissAtOrAbovePossessedLevel_AnswersWithoutAVerdict(ShowLang lang, string sentence)
        {
            // 0232: 「異常なし」は乗っ取られた結果なので、乗っ取られた装置は正直に「異常なし」を言わない。
            Assert.AreEqual(sentence, CommsPanel.NoticeText(CommsNotice.MarkAnalyzing, lang));
            // ⚠ 判定（有無）を 1 語も入れない。入れた瞬間、この返事は「異常なし」か「異常あり」になる。
            foreach (string verdict in new[] { "異常", "anomal" })
                StringAssert.DoesNotContain(verdict, sentence.ToLowerInvariant(),
                                            $"侵食中の返事に判定の語「{verdict}」が入っている");
        }

        /// <summary>
        /// <b>侵食中の返事も、スイは自分を「私」で語る。</b> 入れ物の名（装置 / the device）で
        /// 自分を呼ぶと、体験者が受け取るのは判定の保留とも故障とも付かない文になる。
        /// 壊れていることは、一人称のまま「終わらない」と言うことで示す。
        /// </summary>
        [Test]
        public void MissAtOrAbovePossessedLevel_NeverCallsItselfTheDevice()
        {
            foreach (ShowLang lang in ShowLanguage.All)
                StringAssert.DoesNotContain("装置",
                                            CommsPanel.NoticeText(CommsNotice.MarkAnalyzing, lang),
                                            ShowLanguage.Code(lang));
        }

        // 2 行目は「報告すると、スイが遮断する」を返事のたびに見せる。
        [TestCase(ShowLang.Ja, "異常を検出しました\n遮断します")]
        [TestCase(ShowLang.En, "An anomaly was detected.\nBlocking it.")]
        [TestCase(ShowLang.Fr, "Anomalie détectée.\nJe la bloque.")]
        public void SuccessfulReportSaysTheAnomalyWasDetected_AndIsBeingRemoved(ShowLang lang, string sentence)
        {
            Assert.AreEqual(sentence, CommsPanel.NoticeText(CommsNotice.MarkLogged, lang));
        }

        // 報告では消せない演出への返事。ログの行で返す（0363「解析不能、原因不明、外部へ送信」の形）。
        // 3 行目は「遮断不能」— 乗っ取りの「遮断を執行｜失敗」と③b へ続く（ユーザーが「この一式で実装」と選んだ版）。
        [TestCase(ShowLang.Ja, "解析不能\n原因不明\n遮断不能")]
        [TestCase(ShowLang.En, "Analysis failed\nCause unknown\nCannot block")]
        [TestCase(ShowLang.Fr, "Analyse impossible\nCause inconnue\nImpossible de bloquer")]
        public void ReportOnAnAnomalyThatCannotBeRemoved_AnswersInLogLines(ShowLang lang, string sentence)
        {
            Assert.AreEqual(sentence, CommsPanel.NoticeText(CommsNotice.MarkStuck, lang));
            foreach (string line in sentence.Split('\n'))
            {
                StringAssert.DoesNotContain("。", line, "ログの行に句点がある");
                StringAssert.DoesNotContain("、", line, "ログの行が文になっている");
            }
        }

        /// <summary>
        /// <b>スイは画を説明しない</b>（0363「説明しすぎなんだよ」「カメラ以外の映像とか、映像の出所が分からないとか、意味不明」）。
        /// 人形の視点の間に頼まれずに出すのは、言いかけて途切れる 1 行だけ。何が混ざっているかを言わない。
        /// </summary>
        [TestCase(ShowLang.Ja, "何かが……", "カメラ")]
        [TestCase(ShowLang.En, "Something…", "camera")]
        [TestCase(ShowLang.Fr, "Quelque chose…", "caméra")]
        public void TheDollView_IsNotExplained(ShowLang lang, string text, string camera)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.ForeignFeed, lang));
            foreach (CommsNotice n in new[] { CommsNotice.ForeignFeed, CommsNotice.MarkStuck })
                StringAssert.DoesNotContain(camera, CommsPanel.NoticeText(n, lang), n.ToString());
        }

        /// <summary>
        /// <b>周の番号を言わない</b>（0363「○○周目です。と言ってしまったら、開幕の演出があるのに、それに被って
        /// 演出に集中できない」）。開始の合図も番号を持たない。
        /// </summary>
        [TestCase(ShowLang.Ja, "周目")]
        [TestCase(ShowLang.En, " lap")]
        [TestCase(ShowLang.Fr, "Premier tour")]
        public void NoNotice_NamesTheLap(ShowLang lang, string lapWord)
        {
            foreach (string body in CommsPanel.AllBodies(lang))
                StringAssert.DoesNotContain(lapWord, body.Replace('\n', ' '));
        }

        /// <summary>
        /// 乗っ取りの後の報告への返事は、人形の顔と赤い字の 2 行。1 行目は空振りの返事と同じ字（人形がスイを真似る）、
        /// 2 行目は開始の合図の真似で体験者を廻らせ続ける。締めの「もう廻らないで」が打ち消す相手。
        /// 別れの一言だけは、その文のまま赤い字で出る。
        /// </summary>
        [Test]
        public void AReplyAfterTheTakeover_LiesAndKeepsTheVisitorCircling()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string lie = CommsPanel.CursedReplyText(lang);
                string[] lines = lie.Split('\n');
                Assert.AreEqual(2, lines.Length, ShowLanguage.Code(lang));
                Assert.AreEqual(CommsPanel.TakeoverLieText(lang), lines[0]);
                Assert.AreEqual(lie, CommsPanel.CursedBodyFor(CommsNotice.MarkLogged, lang));
                Assert.AreEqual(lie, CommsPanel.CursedBodyFor(CommsNotice.MarkNothing, lang));
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.FarewellFinding, lang),
                                CommsPanel.CursedBodyFor(CommsNotice.FarewellFinding, lang));
            }
        }

        [Test]
        public void TheRedTexts_ExistInTheHorrorFont()
        {
            TMP_FontAsset horror = Resources.Load<TMP_FontAsset>("Fonts/CommsHorror SDF");
            Assert.NotNull(horror, "怖い本文用フォントを Resources から読めない");
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (string text in new[] { CommsPanel.CursedReplyText(lang),
                                             CommsPanel.CursedBodyFor(CommsNotice.FarewellFinding, lang) })
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                Assert.IsTrue(horror.HasCharacter(c),
                              $"{ShowLanguage.Code(lang)}: U+{(int)c:X4} '{c}' が赤い字のフォントに無い（menu comms-horror-font）");
            }
        }

        /// <summary>
        /// <b>体験者が報告するのは「違和感」、スイが判定するのは「異常」。</b> 博士の台詞と同じ分け方。
        /// 「異変」はどちらでもない 3 つ目の語で、混ざると同じ物を指しているのか読めなくなる。
        /// </summary>
        [Test]
        public void TheVisitorReportsUnease_AndSuiNamesAnomalies()
        {
            StringAssert.Contains("違和感", CommsPanel.NoticeText(CommsNotice.TutorialReminder, ShowLang.Ja));
            foreach (string body in CommsPanel.AllBodies(ShowLang.Ja))
                StringAssert.DoesNotContain("異変", body);
        }

        /// <summary>
        /// <b>廻ることを言う動詞は、体験を通して 1 つ</b>（日本語は題字と同じ「廻」）。
        /// 開始「廻り始めてください。」→ 乗っ取りの後の人形の嘘「そのまま、廻ってください」→ 締め「もう廻らないで」。
        /// 嘘はスイの指示の真似になり、締めがそれを取り消す。
        /// </summary>
        [TestCase(ShowLang.Ja, "廻")]
        [TestCase(ShowLang.En, "circl")]
        [TestCase(ShowLang.Fr, "tourne")]
        public void Circling_IsOneVerb_FromTheStartToTheWarning(ShowLang lang, string stem)
        {
            foreach (CommsNotice n in new[] { CommsNotice.Begin, CommsNotice.Halt })
                StringAssert.Contains(stem, CommsPanel.NoticeText(n, lang).ToLowerInvariant(),
                                      $"{n} / {ShowLanguage.Code(lang)}");
            StringAssert.Contains(stem, CommsPanel.CursedReplyText(lang).ToLowerInvariant());
        }

        /// <summary>
        /// 締めの「止まってください！」は、<b>何を止めるのかを同じ 1 通の中で言う</b>（廻ること）。1 行だけでは、博士の教えた
        /// 中断の手順（立ち止まる → 手すりを離す → 装置を外す）の 1 歩目に読めた。
        /// 画に居るのは 1.8 秒ほどなので短い。装置を外すなとは言わない（続けられない人の出口を塞がない）。
        /// ⚠ 日本語は 0365 の逐語で 16 字。旧版（13 字）より長い。
        /// </summary>
        [TestCase(ShowLang.Ja, "止まってください！\nもう廻らないで", "廻", "装置", 16)]
        [TestCase(ShowLang.En, "Please stop!\nNo more circling", "circl", "device", 28)]
        [TestCase(ShowLang.Fr, "Arrêtez !\nNe tournez plus", "tourne", "dispositif", 28)]
        public void TheStopCall_SaysWhatToStop_AndIsShort(ShowLang lang, string text, string circling, string device,
                                                          int maxChars)
        {
            string body = CommsPanel.NoticeText(CommsNotice.Halt, lang);
            Assert.AreEqual(text, body);
            string[] lines = body.Split('\n');
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains(circling, lines[1]);
            StringAssert.DoesNotContain(device, body);
            Assert.LessOrEqual(body.Replace("\n", "").Length, maxChars, "1.8 秒で読み切れる長さを超えている");
        }

        [TestCase(ShowLang.Ja, "廻り始めてください", "廻ってください", "止まって")]
        [TestCase(ShowLang.En, "start circling", "Keep circling", "stop")]
        [TestCase(ShowLang.Fr, "commencez à tourner", "Continuez à tourner", "Arrêtez")]
        public void TheWalkingOrders_StartContinueStop(ShowLang lang, string start, string keep, string stop)
        {
            StringAssert.Contains(start, CommsPanel.NoticeText(CommsNotice.Begin, lang));
            StringAssert.Contains(keep, CommsPanel.CursedReplyText(lang));
            StringAssert.Contains(stop, CommsPanel.NoticeText(CommsNotice.Halt, lang));
        }

        /// <summary>
        /// 句点の決まり（日本語）。話しかける連絡は句点で終わり、報告への返事（記録の行）は句点を持たない。
        /// 返事で句点を持つのは、スイが記録の型を外れて話す「解析が、終わりません。」だけ。
        /// 句点で終わらない連絡は 4 つ: ③a・③b（0365 の逐語。「！」の後で敬語が崩れ、「…」で途切れる）、
        /// 2 周目 C の「何かが……」（言いかけて途切れる）、別れの一言（人形の声）。
        /// </summary>
        [Test]
        public void Notices_EndWithAFullStop_AndLogRepliesDoNot()
        {
            var logReplies = new System.Collections.Generic.HashSet<CommsNotice>
            {
                CommsNotice.MarkLogged, CommsNotice.MarkStuck, CommsNotice.MarkNothing, CommsNotice.Takeover,
                CommsNotice.MarkCleared, CommsNotice.MarkClearedClose,
            };
            var unfinished = new System.Collections.Generic.HashSet<CommsNotice>
            {
                CommsNotice.Halt, CommsNotice.Prompt, CommsNotice.ForeignFeed, CommsNotice.FarewellFinding,
            };
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
            {
                string body = CommsPanel.NoticeText(n, ShowLang.Ja);
                if (string.IsNullOrEmpty(body)) continue;
                char last = body[body.Length - 1];
                if (logReplies.Contains(n) || unfinished.Contains(n))
                    Assert.AreNotEqual('。', last, $"{n}: 句点がある");
                else
                    Assert.IsTrue(last == '。' || last == '！', $"{n}:「{body}」が句点で終わっていない");
            }
            foreach (string line in CommsPanel.CursedReplyText(ShowLang.Ja).Split('\n'))
                StringAssert.DoesNotContain("。", line, "人形の嘘の返事に句点がある（返事は記録の行の真似）");
        }

        /// <summary>
        /// スイの動詞は、乗っ取りの警告が画面に出す語と同じ（「遮断を執行」「遮断失敗」）。
        /// 毎回の返事で通っていたことが、2 周目 C で通らず、乗っ取りで失敗し、締めで体験者の手に渡って通る。
        /// ⚠ 警告の文言は <c>UnauthorizedAccessEffect</c> が持つ。あちらを変えたらここも見る。
        /// </summary>
        [Test]
        public void SuiBlocks_WithTheSameWordAsTheTakeoverWarning()
        {
            foreach (CommsNotice n in new[] { CommsNotice.TutorialReminder, CommsNotice.MarkLogged,
                                              CommsNotice.MarkStuck, CommsNotice.Prompt, CommsNotice.MarkCleared })
                StringAssert.Contains("遮断", CommsPanel.NoticeText(n, ShowLang.Ja), n.ToString());
            foreach (string body in CommsPanel.AllBodies(ShowLang.Ja))
                StringAssert.DoesNotContain("取り除", body, "古い動詞が残っている");
        }

        /// <summary>⓪c が「観測カメラ」と名指す。</summary>
        [TestCase(ShowLang.Ja, "観測カメラ")]
        [TestCase(ShowLang.En, "camera")]
        [TestCase(ShowLang.Fr, "caméra")]
        public void TheCameraIsNamedOnArrival(ShowLang lang, string camera)
        {
            StringAssert.Contains(camera, CommsPanel.NoticeText(CommsNotice.Arrived, lang));
        }

        /// <summary>
        /// ⓪c は、そのカメラが<b>目に見えない異常を映す</b>と言う。ただの室内カメラでは、体験者がなぜ
        /// 自分の目でなくテレビの中の自分を見て歩くのかに答えが無い（2026-11-14 作者の指摘）。
        /// 「室内カメラ」とは呼ばない。
        /// </summary>
        [TestCase(ShowLang.Ja, "目に見えない", "室内")]
        [TestCase(ShowLang.En, "eyes cannot", "room camera")]
        [TestCase(ShowLang.Fr, "invisible", "de la pièce")]
        public void TheObservationCamera_ShowsWhatTheEyeCannot(ShowLang lang, string unseen, string plain)
        {
            string arrived = CommsPanel.NoticeText(CommsNotice.Arrived, lang);
            StringAssert.Contains(unseen, arrived);
            foreach (string body in CommsPanel.AllBodies(lang))
                StringAssert.DoesNotContain(plain, body, "ただの室内のカメラとして呼んでいる");
        }

        /// <summary>
        /// 締めの報告が通った返事は 2 枚。同じ面で入れ替えて出す（0366「2回順番でスクリーンをだして。入れ替えで。」）。
        /// 日本語は 0365 の逐語。
        /// </summary>
        [TestCase(ShowLang.Ja, "遮断できました", "データは十分です\nお疲れ様でした")]
        [TestCase(ShowLang.En, "Block succeeded", "We have enough data\nThank you for your work")]
        [TestCase(ShowLang.Fr, "C'est bloqué", "Les données suffisent\nMerci pour votre travail")]
        public void TheClosingReply_ComesInTwoPanels(ShowLang lang, string first, string second)
        {
            Assert.AreEqual(first, CommsPanel.NoticeText(CommsNotice.MarkCleared, lang));
            Assert.AreEqual(second, CommsPanel.NoticeText(CommsNotice.MarkClearedClose, lang));
        }

        /// <summary>
        /// ⑤ 別れの一言は<b>人形の声</b>（0363「一瞬スイが人形になっていうならわかる」・0365「また、あそぼ」）。
        /// スイが引用するのではないので引用符を付けない。スイの地の文（解析・誰の声か）も付けない。
        /// </summary>
        [TestCase(ShowLang.Ja, "また、あそぼ")]
        [TestCase(ShowLang.En, "Let's play again")]
        [TestCase(ShowLang.Fr, "On rejoue ?")]
        public void FarewellFinding_IsTheDollsOwnVoice(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.FarewellFinding, lang));
            Assert.AreEqual(1, text.Split('\n').Length);
            foreach (string mark in new[] { "「", "」", "“", "”", "«", "»" })
                StringAssert.DoesNotContain(mark, text, "人形の声に引用符を付けている");
            foreach (string log in new[] { "解析", "検出", "声", "nalys", "detect", "voice", "voix" })
                StringAssert.DoesNotContain(log, text, $"⑤にスイの地の文（{log}）が入っている");
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
            // 嘘の書き換え先（0232）と乗っ取りの後の嘘の返事も画へ出る文面。
            foreach (ShowLang lang in ShowLanguage.All)
            {
                yield return (lang, CommsPanel.TakeoverLieText(lang));
                yield return (lang, CommsPanel.CursedReplyText(lang));
            }
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
                    if (CommsCueLogic.DeliveryOf(notice, 0f) != CommsDelivery.Typed) continue;
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
