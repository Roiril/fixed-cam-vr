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

        // ③b。1・2 行目は 0124 / 0168 の逐語。3 行目は体験者が知っている操作の名前（報告）で頼み、
        // 導入の約束を言い直す（シュビーの案・未判定。canon/OPEN.md）。
        [TestCase(ShowLang.Ja, "廻り続ければ、あなたも私も\n人形になります。\n報告を。私が遮断します。")]
        [TestCase(ShowLang.En, "If you keep circling, you\nand I will become dolls.\nReport it. I will block it.")]
        [TestCase(ShowLang.Fr, "Si vous tournez encore,\nvous et moi serons poupées.\nSignalez. Je la bloquerai.")]
        public void ClosingPromptAsksForAReportAndRenewsThePromise(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.Prompt, lang));
        }

        /// <summary>
        /// ③b が<b>スイ自身も取り込まれかけていると言う</b>（0124 の逐語「異常があなたと私を」）。
        ///
        /// ⚠⚠ <b>ここがスイの侵食を言葉で言う唯一の場所。</b> 画（乗っ取り・顔に残る人形・赤の残り）が
        /// 言っていることを、文面側はこの 1 語でしか言わない。2026-09 に「あなたを」へ縮められて
        /// 2 か月のあいだ空白になっていたので、機械で守る。
        /// </summary>
        [TestCase(ShowLang.Ja, "あなたも私も", "人形")]
        [TestCase(ShowLang.En, "you and I", "dolls")]
        [TestCase(ShowLang.Fr, "vous et moi", "poupées")]
        public void ClosingPrompt_PutsSuiInsideTheDanger(ShowLang lang, string both, string doll)
        {
            // 行をまたいでいてもよい（English は 1 行目の末尾と 2 行目の頭に分かれる）。
            string flat = CommsPanel.NoticeText(CommsNotice.Prompt, lang).Replace('\n', ' ');
            StringAssert.Contains(both, flat);
            // 何になるのかを言う。「取り込む」だけでは、何に取り込まれるのかが読み手に伝わらなかった。
            StringAssert.Contains(doll, flat);
        }

        /// <summary>
        /// 約束（導入）→ 実行（報告の返事）→ 言い直し（③b）→ 果たす（④）が、言語ごとに同じ動詞で通っている。
        /// 1 か所だけ別の語にすると、同じことを言っていると読めなくなる。
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

        // 2 周目の頭。廻ることと異常を結ぶ（開始の合図と同じ動詞で言う）。
        // 体験者は同じ所を廻っているので「近づく」とは言わない。変わるのは廻った回数。
        // ⚠ 推し量る形（ようです / seems / semble）。乗っ取りの前のスイは、根拠の足りないことを断定しない。
        [TestCase(ShowLang.Ja, "二周目です。\n廻るたびに、異常は\n強まっているようです。", "ようです")]
        [TestCase(ShowLang.En, "Second lap.\nThe anomaly seems to grow\neach time you circle.", "seems")]
        [TestCase(ShowLang.Fr, "Deuxième tour.\nPlus vous tournez, plus\nl'anomalie semble croître.", "semble")]
        public void SecondLap_TiesTheAnomalyToCircling(ShowLang lang, string text, string hedge)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.SecondLap, lang));
            StringAssert.Contains(hedge, text);
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

        // 報告では消せない演出への返事。1 行目は当たりの返事と同じ。2 行目が理由（カメラの映像ではない）、
        // 3 行目が結果。理由があるので、直後の乗っ取り（不正アクセス）がその続きとして読める。
        [TestCase(ShowLang.Ja, "異常を検出しました\n観測カメラの映像ではなく、\n遮断できません", "カメラ")]
        [TestCase(ShowLang.En, "An anomaly was detected.\nIt is not from our cameras.\nI cannot block it.", "camera")]
        [TestCase(ShowLang.Fr, "Anomalie détectée.\nAucune caméra ne la filme.\nJe ne peux pas la bloquer.", "caméra")]
        public void ReportOnAnAnomalyThatCannotBeRemoved_SaysWhy(ShowLang lang, string sentence, string camera)
        {
            Assert.AreEqual(sentence, CommsPanel.NoticeText(CommsNotice.MarkStuck, lang));
            Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkLogged, lang).Split('\n')[0],
                            sentence.Split('\n')[0], "1 行目は当たりの返事と同じ");
            StringAssert.Contains(camera, sentence.Split('\n')[1], "2 行目が理由を言っていない");
        }

        /// <summary>
        /// 周の節目は 3 回、同じ型で告げる（一周目 ＝ 開始の合図・二周目・三周目）。
        /// 3 通目は乗っ取りの後に来る。型を 2 回聞かせておかないと、3 通目が嘘だと分からない。
        /// </summary>
        [TestCase(ShowLang.Ja, "一周目です。", "二周目です。", "三周目です。")]
        [TestCase(ShowLang.En, "First lap.", "Second lap.", "Third lap.")]
        [TestCase(ShowLang.Fr, "Premier tour.", "Deuxième tour.", "Troisième tour.")]
        public void EveryLap_IsAnnouncedInTheSameForm(ShowLang lang, string first, string second, string third)
        {
            Assert.AreEqual(first, CommsPanel.NoticeText(CommsNotice.Begin, lang).Split('\n')[0]);
            Assert.AreEqual(second, CommsPanel.NoticeText(CommsNotice.SecondLap, lang).Split('\n')[0]);
            Assert.AreEqual(third, CommsPanel.NoticeText(CommsNotice.ThirdLap, lang).Split('\n')[0]);
            // 嘘も同じ 1 行目で始まる。違うのは中身だけ。
            Assert.AreEqual(third, CommsPanel.ThirdLapLieText(lang).Split('\n')[0]);
            Assert.AreNotEqual(CommsPanel.NoticeText(CommsNotice.ThirdLap, lang), CommsPanel.ThirdLapLieText(lang));
        }

        /// <summary>
        /// 乗っ取りの後の 3 周目の頭は、連絡の文ではなく専用の嘘が赤い字で出る。ほかの連絡は嘘の一文のまま。
        /// 嘘は「異常は無い」と言い切り、歩き続けさせる。締めの「止まってください！」が打ち消す相手。
        /// </summary>
        [Test]
        public void ThirdLapAfterTheTakeover_LiesAndKeepsTheVisitorWalking()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string lie = CommsPanel.ThirdLapLieText(lang);
                Assert.AreEqual(lie, CommsPanel.CursedBodyFor(CommsNotice.ThirdLap, lang));
                Assert.AreEqual(CommsPanel.TakeoverLieText(lang),
                                CommsPanel.CursedBodyFor(CommsNotice.MarkLogged, lang));
                Assert.AreEqual(CommsPanel.TakeoverLieText(lang),
                                CommsPanel.CursedBodyFor(CommsNotice.MarkNothing, lang));
                Assert.AreEqual(3, lie.Split('\n').Length, ShowLanguage.Code(lang));
            }
        }

        [Test]
        public void ThirdLapLie_ExistsInTheHorrorFont()
        {
            TMP_FontAsset horror = Resources.Load<TMP_FontAsset>("Fonts/CommsHorror SDF");
            Assert.NotNull(horror, "怖い本文用フォントを Resources から読めない");
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (char c in CommsPanel.ThirdLapLieText(lang))
            {
                if (char.IsWhiteSpace(c)) continue;
                Assert.IsTrue(horror.HasCharacter(c),
                              $"{ShowLanguage.Code(lang)}: U+{(int)c:X4} '{c}' が赤い字のフォントに無い（menu comms-horror-font）");
            }
        }

        /// <summary>
        /// 当たりの返事は、報告が乗った演出が分かるとき<b>解析の中身を 1 行挟む</b>
        /// （博士「何を見たかは、装置に搭載されたエージェントが解析します」）。
        /// 1 行目と最後の行は元の返事のまま。知らない演出では元の 2 行へ戻る。
        /// </summary>
        [Test]
        public void SuccessfulReport_SaysWhatTheAnalysisFound()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] plain = CommsPanel.NoticeText(CommsNotice.MarkLogged, lang).Split('\n');
                foreach (string takeId in CommsPanel.FindingTakeIds)
                {
                    string? finding = CommsPanel.FindingLine(takeId, lang);
                    Assert.That(finding, Is.Not.Null.And.Not.Empty, $"{takeId} / {ShowLanguage.Code(lang)}");
                    string[] lines = CommsPanel.BodyWithFinding(CommsNotice.MarkLogged, lang, takeId).Split('\n');
                    Assert.AreEqual(3, lines.Length, takeId);
                    Assert.AreEqual(plain[0], lines[0], takeId);
                    Assert.AreEqual(finding, lines[1], takeId);
                    Assert.AreEqual(plain[1], lines[2], takeId);
                }
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkLogged, lang),
                                CommsPanel.BodyWithFinding(CommsNotice.MarkLogged, lang, "L9C9#9"));
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkLogged, lang),
                                CommsPanel.BodyWithFinding(CommsNotice.MarkLogged, lang, ""));
                // ほかの連絡には挟まない（空振りに解析の中身が付いたら嘘になる）。
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkNothing, lang),
                                CommsPanel.BodyWithFinding(CommsNotice.MarkNothing, lang, "L1C1#0"));
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
        /// 開始「廻り始めてください。」→ 2 周目「廻るたびに」→ 乗っ取りの後の嘘「そのまま、廻ってください。」→
        /// 締め「廻り続ければ、異常が…取り込みます。」。嘘はスイの指示の真似になり、締めがそれを取り消す。
        /// 途中で別の語（歩く・周を重ねる）に替えると、締めの「止まってください！」が何を止めるのか分からなくなる。
        /// </summary>
        [TestCase(ShowLang.Ja, "廻")]
        [TestCase(ShowLang.En, "circl")]
        [TestCase(ShowLang.Fr, "tourne")]
        public void Circling_IsOneVerb_FromTheStartToTheWarning(ShowLang lang, string stem)
        {
            foreach (CommsNotice n in new[] { CommsNotice.Begin, CommsNotice.SecondLap,
                                              CommsNotice.ThirdLap, CommsNotice.Halt, CommsNotice.Prompt })
                StringAssert.Contains(stem, CommsPanel.NoticeText(n, lang).ToLowerInvariant(),
                                      $"{n} / {ShowLanguage.Code(lang)}");
            StringAssert.Contains(stem, CommsPanel.ThirdLapLieText(lang).ToLowerInvariant());
        }

        /// <summary>
        /// 歩くことへの指示は、始める（スイ）→ 続けろ（嘘）→ 止まれ（スイ）の順で 1 本になる。
        /// 正直な 3 周目の連絡は、続けろとは言わない（歩かせ続けるのは嘘の側だけ）。
        /// </summary>
        /// <summary>
        /// 締めの「止まって」は、<b>何を止めるのかを同じ 1 通の中で言う</b>（廻ること）。1 行だけでは、博士の教えた
        /// 中断の手順（立ち止まる → 手すりを離す → 装置を外す）の 1 歩目に読めた。
        /// 画に居るのは 1.8 秒ほどなので短い。装置を外すなとは言わない（続けられない人の出口を塞がない）。
        /// </summary>
        [TestCase(ShowLang.Ja, "廻", "装置", 14)]
        [TestCase(ShowLang.En, "circle", "device", 28)]
        [TestCase(ShowLang.Fr, "tourne", "dispositif", 28)]
        public void TheStopCall_SaysWhatToStop_AndIsShort(ShowLang lang, string circling, string device, int maxChars)
        {
            string body = CommsPanel.NoticeText(CommsNotice.Halt, lang);
            string[] lines = body.Split('\n');
            Assert.AreEqual(2, lines.Length);
            StringAssert.Contains(circling, lines[1]);
            StringAssert.DoesNotContain(device, body);
            Assert.LessOrEqual(body.Replace("\n", "").Length, maxChars, "1.8 秒で読み切れる長さを超えている");
        }

        [TestCase(ShowLang.Ja, "廻り始めてください", "廻ってください", "止まって")]
        [TestCase(ShowLang.En, "start circling", "Keep circling", "Stop")]
        [TestCase(ShowLang.Fr, "commencez à tourner", "Continuez à tourner", "Arrêtez")]
        public void TheWalkingOrders_StartContinueStop(ShowLang lang, string start, string keep, string stop)
        {
            StringAssert.Contains(start, CommsPanel.NoticeText(CommsNotice.Begin, lang));
            StringAssert.Contains(keep, CommsPanel.ThirdLapLieText(lang));
            StringAssert.Contains(stop, CommsPanel.NoticeText(CommsNotice.Halt, lang));
            StringAssert.DoesNotContain(keep, CommsPanel.NoticeText(CommsNotice.ThirdLap, lang));
        }

        /// <summary>
        /// 頼まれずに出す警告（2 周目 C）と、報告で消せない返事は、同じことを言う
        /// （観測カメラの映像ではない・遮断できない）。警告は話しかける文、返事は記録の行。
        /// </summary>
        [TestCase(ShowLang.Ja, "観測カメラ", "遮断できません")]
        [TestCase(ShowLang.En, "cameras", "cannot block")]
        [TestCase(ShowLang.Fr, "caméra", "ne peux pas")]
        public void TheUnaskedWarning_SaysWhatTheStuckReplySays(ShowLang lang, string camera, string cannot)
        {
            foreach (CommsNotice n in new[] { CommsNotice.ForeignFeed, CommsNotice.MarkStuck })
            {
                StringAssert.Contains(camera, CommsPanel.NoticeText(n, lang), n.ToString());
                StringAssert.Contains(cannot, CommsPanel.NoticeText(n, lang), n.ToString());
            }
        }

        /// <summary>
        /// 理由（観測カメラの映像ではない）を既に言った後の返事は、同じ文を繰り返さず、新しいことを 1 つ言う
        /// （映像の出どころが分からない。直後の乗っ取りの警告「接続元不明」の先触れ）。
        /// </summary>
        [Test]
        public void TheStuckReply_DropsTheReason_OnceItWasSaid()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            {
                string[] full = CommsPanel.NoticeText(CommsNotice.MarkStuck, lang).Split('\n');
                Assert.AreEqual(3, full.Length);
                string brief = CommsPanel.BodyWithFinding(CommsNotice.MarkStuck, lang, "", stuckReasonAlreadySaid: true);
                // 1 行目は同じ。2 行目は、まだ言っていないこと（出どころが分からない）を 1 つ言う。
                Assert.AreEqual(full[0] + "\n" + CommsPanel.StuckFollowUpLine(lang), brief, ShowLanguage.Code(lang));
                StringAssert.DoesNotContain(full[1], brief, "同じ理由を繰り返している");
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkStuck, lang),
                                CommsPanel.BodyWithFinding(CommsNotice.MarkStuck, lang, "", stuckReasonAlreadySaid: false));
                // ほかの返事は変わらない。
                Assert.AreEqual(CommsPanel.NoticeText(CommsNotice.MarkNothing, lang),
                                CommsPanel.BodyWithFinding(CommsNotice.MarkNothing, lang, "", stuckReasonAlreadySaid: true));
            }
        }

        /// <summary>
        /// 句点の決まり（日本語）。話しかける連絡は句点で終わり、報告への返事（記録の行）は句点を持たない。
        /// 返事で句点を持つのは、スイが記録の型を外れて話す 2 通だけ。
        /// </summary>
        [Test]
        public void Notices_EndWithAFullStop_AndLogRepliesDoNot()
        {
            var logReplies = new System.Collections.Generic.HashSet<CommsNotice>
            {
                CommsNotice.MarkLogged, CommsNotice.MarkStuck, CommsNotice.MarkNothing, CommsNotice.Takeover,
            };
            foreach (CommsNotice n in System.Enum.GetValues(typeof(CommsNotice)))
            {
                string body = CommsPanel.NoticeText(n, ShowLang.Ja);
                if (string.IsNullOrEmpty(body)) continue;
                char last = body[body.Length - 1];
                if (logReplies.Contains(n))
                    Assert.AreNotEqual('。', last, $"{n}: 返事（記録の行）に句点がある");
                else
                    Assert.IsTrue(last == '。' || last == '！' || last == '」', $"{n}:「{body}」が句点で終わっていない");
            }
            foreach (string takeId in CommsPanel.FindingTakeIds)
            {
                string? f = CommsPanel.FindingLine(takeId, ShowLang.Ja);
                StringAssert.DoesNotContain("。", f!, $"{takeId}: 解析の行に句点がある（返事は記録の行）");
            }
            Assert.AreNotEqual('。', CommsPanel.TakeoverLieText(ShowLang.Ja)[CommsPanel.TakeoverLieText(ShowLang.Ja).Length - 1]);
        }

        /// <summary>
        /// スイの動詞は、乗っ取りの警告が画面に出す語と同じ（「遮断を執行」「遮断失敗」）。
        /// 毎回の返事で通っていたことが、乗っ取りで初めて通らず、締めの報告でもう一度通る。
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

        /// <summary>
        /// ⓪c が「観測カメラ」と名指し、報告で消せない返事と頼まれない警告が同じ名で否定する。
        /// 同じ語なので、後の 2 通は 1 通目の裏返しとして読める（装置が繋いだ先ではない所から、映像が来ている）。
        /// </summary>
        [TestCase(ShowLang.Ja, "観測カメラ")]
        [TestCase(ShowLang.En, "camera")]
        [TestCase(ShowLang.Fr, "caméra")]
        public void TheCameraIsNamedOnArrival_AndDeniedInTheDollView(ShowLang lang, string camera)
        {
            StringAssert.Contains(camera, CommsPanel.NoticeText(CommsNotice.Arrived, lang));
            StringAssert.Contains(camera, CommsPanel.NoticeText(CommsNotice.MarkStuck, lang));
            StringAssert.Contains(camera, CommsPanel.NoticeText(CommsNotice.ForeignFeed, lang));
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
        /// 締めの報告が通った返事は、約束と同じ語（異常・遮断）で果たし、戻ってきたことを言う。
        /// 結果画面の題は「帰還End」。
        /// </summary>
        [TestCase(ShowLang.Ja, "異常を遮断しました。\nおかえりなさい。")]
        [TestCase(ShowLang.En, "The anomaly is blocked.\nWelcome back.")]
        [TestCase(ShowLang.Fr, "Anomalie bloquée.\nBon retour parmi nous.")]
        public void TheClosingReply_WelcomesTheVisitorBack(ShowLang lang, string text)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.MarkCleared, lang));
        }

        // ⑤ 別れの一言。<b>引用だけ</b>。スイの地の文（誰の声か・解析が済んだ・あの子が言っている）を付けない。
        // 説明の文を 5 通り試して、5 通りとも初見の読み手に「白けた・唐突・説明的」と書かれた。
        // 引用は 2 周目 C で聞いた声の言い直しではなく、去っていく人形がいま言っている言葉（「また、」）。
        // ⚠ シュビーの案で未判定（canon/OPEN.md）。文面を変えたら menu hud-font で焼き直す。
        [TestCase(ShowLang.Ja, "「また、あーそぼー」", "また")]
        [TestCase(ShowLang.En, "“Come and play again.”", "again")]
        [TestCase(ShowLang.Fr, "« Reviens jouer. »", "Reviens")]
        public void FarewellFinding_QuotesWhatTheDollSaysAsItLeaves(ShowLang lang, string text, string again)
        {
            Assert.AreEqual(text, CommsPanel.NoticeText(CommsNotice.FarewellFinding, lang));
            string[] lines = text.Split('\n');
            Assert.AreEqual(1, lines.Length, "引用の 1 行だけ（スイの地の文を付けない）");
            string quote = lines[0];
            // 聞いた声の言い直しにしない。「また」が無いと、締めに新しいことが 1 つも無い。
            StringAssert.Contains(again, quote);
            // 3 行目は引用だけ。スイの判定（検出・敵意・detected・détecté）を足さない —
            // 乗っ取られた装置の判定は、体験者に信じてもらえない。
            foreach (string verdict in new[] { "検出", "敵意", "detect", "hostil", "détect" })
                StringAssert.DoesNotContain(verdict, text, $"⑤に判定の語「{verdict}」が入っている");
            // 記録の口調へ戻らない・誰の声かを説明しない。去っていく人形とオルゴールの場面で、どちらも読み手が冷めた。
            foreach (string log in new[] { "解析", "調査対象", "0514", "声", "あの子", "言って",
                                           "nalys", "subject", "objet", "voice", "voix", "child", "enfant" })
                StringAssert.DoesNotContain(log, text, $"⑤にスイの地の文（{log}）が入っている");
            char open = quote[0], close = quote[quote.Length - 1];
            Assert.IsTrue((open == '「' && close == '」') || (open == '“' && close == '”') || (open == '«' && close == '»'),
                          $"最後の行「{quote}」が引用の形になっていない");
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
            // 嘘の書き換え先（0232）・3 周目の嘘・解析の中身つきの返事も画へ出る文面。
            foreach (ShowLang lang in ShowLanguage.All)
            {
                yield return (lang, CommsPanel.TakeoverLieText(lang));
                yield return (lang, CommsPanel.ThirdLapLieText(lang));
                foreach (string takeId in CommsPanel.FindingTakeIds)
                    yield return (lang, CommsPanel.BodyWithFinding(CommsNotice.MarkLogged, lang, takeId));
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
