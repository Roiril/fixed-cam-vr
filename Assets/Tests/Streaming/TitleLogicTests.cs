#nullable enable
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// タイトル画面の状態機械を固定する。
    ///
    /// ここで守っているのは主に 2 つで、どちらも<b>壊れても実機の画を見るまで気づけない</b>:
    ///   - <b>黒は最初のフレームから 1</b>（依頼「一瞬でも壁が見えてはいけない」）
    ///   - <b>閉じない状態を作らない</b>。隠すものが立たなくても
    ///     <see cref="TitleLogic.ConcealWaitMaxSec"/> で必ず開く（この codebase が 4 回踏んだ
    ///     「凍結が解けない」の型）
    /// </summary>
    public sealed class TitleLogicTests
    {
        private const float Dt = 1f / 90f;

        private static TitleInput Ready => new TitleInput { concealReady = true };
        private static TitleInput NotReady => new TitleInput { concealReady = false };

        private static void Run(TitleLogic l, float sec, TitleInput input)
        {
            for (float t = 0f; t < sec; t += Dt) l.Tick(Dt, input);
        }

        [Test]
        public void StartGuidance_ExistsInAllThreeLanguages()
        {
            foreach (ShowLang lang in ShowLanguage.All)
            foreach (TitleStartGuidance guidance in System.Enum.GetValues(typeof(TitleStartGuidance)))
            {
                string text = TitleScreen.StartPromptText(guidance, lang);
                if (guidance == TitleStartGuidance.Hidden) Assert.That(text, Is.Empty);
                else Assert.That(text, Is.Not.Empty, $"{lang} / {guidance}");
            }
        }

        [Test]
        public void Idle_IsOff_AndShowsNothing()
        {
            var l = new TitleLogic();
            Assert.AreEqual(TitleStage.Off, l.Stage);
            Assert.IsFalse(l.Active);
            Assert.AreEqual(0f, l.Weights.veil, 1e-5f);
            Assert.AreEqual(0f, l.Weights.glyph, 1e-5f);
        }

        [Test]
        public void Begin_BlacksOutOnTheVeryFirstFrame()
        {
            // ⚠ 立ち上げると起動直後の 1 フレームだけ現実が覗く。依頼の核心なのでここで止める。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Assert.AreEqual(1f, l.Weights.veil, 1e-5f, "黒は最初から 1 でなければならない");
            l.Tick(Dt, Ready);
            Assert.AreEqual(1f, l.Weights.veil, 1e-5f);
        }

        [Test]
        public void Reveal_WaitsThenRisesToOne()
        {
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Run(l, TitleLogic.InDelaySec * 0.5f, Ready);
            Assert.AreEqual(0f, l.Weights.reveal, 1e-3f, "間は字を出さない（起動の黒が明けるのを待つ）");

            Run(l, TitleLogic.InSec * 0.5f, Ready);
            float mid = l.Weights.reveal;
            Assert.Greater(mid, 0.1f);
            Assert.Less(mid, 0.9f);

            Run(l, TitleLogic.InSec, Ready);
            Assert.AreEqual(TitleStage.Hold, l.Stage);
            Assert.AreEqual(1f, l.Weights.reveal, 1e-5f);
            Assert.AreEqual(1f, l.Weights.veil, 1e-5f);
        }

        [Test]
        public void Hold_WaitsUntilTheVisitorIsReady()
        {
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            Run(l, TitleLogic.InSec + 30f, Ready);
            Assert.AreEqual(TitleStage.Hold, l.Stage,
                            "本人の短押し前にタイトルを自動で閉じてはいけない");
            l.RequestDismiss();
            Assert.AreEqual(TitleStage.Out, l.Stage);
        }

        [Test]
        public void Begin_StartsBlack_WithNoLetters_UntilRequested()
        {
            // ⚠ **周回リセット直後は真っ暗で、字は出ていない。**
            //    導入側が呼び出すまで時間では何も起きない。
            var l = new TitleLogic();
            l.Begin();
            for (int i = 0; i < 600; i++) l.Tick(1f / 60f, Ready);    // 10 秒放置
            Assert.AreEqual(TitleStage.Wait, l.Stage);
            Assert.AreEqual(1f, l.Weights.veil, 1e-6f, "黒が張っていない");
            Assert.AreEqual(0f, l.Weights.glyph, 1e-6f, "呼び出す前に字が出ている");
            Assert.IsTrue(l.AwaitingInput);
            Assert.IsFalse(l.GlyphShowing);

            l.RequestAdvance();
            l.Tick(1f / 60f, Ready);
            Assert.AreEqual(TitleStage.In, l.Stage);
            Assert.IsTrue(l.GlyphShowing, "呼び出しても字が立っていない");
        }

        [Test]
        public void A_TwiceInQuickSuccession_DoesNotSkipTheTitle()
        {
            // 呼び出し直後に消去要求が重なっても、題字が一瞬で消えない。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            l.Tick(1f / 120f, Ready);
            l.RequestAdvance();                     // 不感時間の内
            Assert.AreNotEqual(TitleStage.Out, l.Stage);
        }

        [Test]
        public void Dismiss_FlashesImmediately_ThenOpensToPassthrough()
        {
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f, Ready);
            l.RequestDismiss();
            Assert.AreEqual(TitleStage.Out, l.Stage);

            // 押した手応えは**その場で**返す。遅れると押した気がしない。
            l.Tick(Dt, Ready);
            Assert.Greater(l.Weights.flashAmt, 0f, "消去の光は要求直後から出る");

            // 字は黒より先に消え切る（最後に残るのが黒 ＝ 継ぎ目が 1 回で済む）。
            Run(l, TitleLogic.GlyphGoneSec, Ready);
            Assert.AreEqual(0f, l.Weights.glyph, 1e-2f);

            Run(l, TitleLogic.OpenSec + 0.1f, Ready);
            Assert.AreEqual(TitleStage.Done, l.Stage);
            Assert.AreEqual(0f, l.Weights.veil, 1e-5f, "閉じ切ったら黒は 1 画素も残さない");
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Dismiss_MidReveal_IsAccepted()
        {
            // 出現の途中で押されても受ける。待たされる方が不快で、しかも 2 度押しを誘う。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec * 0.4f, Ready);
            l.RequestDismiss();
            Assert.AreEqual(TitleStage.Out, l.Stage);
            // 途中の見えを保ったまま閉じる（押した瞬間に字が完成すると「巻き戻った」ように見える）。
            Assert.Less(l.Weights.reveal, 0.9f);
        }

        [Test]
        public void Open_WaitsForConcealment_ButNeverLatches()
        {
            // 隠すものが立たない現場（シェーダ剥がれ等）でも必ず開く。
            // ⚠ ここをラッチにすると体験が二度と始まらない。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f, Ready);
            l.RequestDismiss();

            // 字が消え切って、さらに上限の手前では、まだ黒を開き始めていない。
            Run(l, TitleLogic.GlyphGoneSec + TitleLogic.ConcealWaitMaxSec * 0.5f, NotReady);
            Assert.AreEqual(1f, l.Weights.veil, 1e-3f, "隠すものが立つまでは開かない");

            Run(l, TitleLogic.ConcealWaitMaxSec + TitleLogic.OpenSec + 0.1f, NotReady);
            Assert.AreEqual(TitleStage.Done, l.Stage, "諦めて開き切る（ラッチにしない）");
            Assert.AreEqual(0f, l.Weights.veil, 1e-5f);
        }

        [Test]
        public void Veil_StaysShut_UntilTheGlyphHasBurnedAway()
        {
            // 2026-08-13 ユーザー指示「タイトルが消えきってから、パススルーへのフェードが
            // 始まるようにしてほしい」。それまでは黒が開きながら焼けていたので、
            // **題字は現実の上で燃えていた**。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f, Ready);
            l.RequestDismiss();

            for (float t = 0f; t < TitleLogic.GlyphGoneSec - 0.05f; t += Dt)
            {
                l.Tick(Dt, Ready);
                Assert.AreEqual(1f, l.Weights.veil, 1e-5f, "字が残っているのに黒が開き始めている");
            }

            Run(l, 0.3f, Ready);
            Assert.Less(l.Weights.veil, 1f, "字が消えても黒が開き始めない");
            Assert.AreEqual(0f, l.Weights.glyph, 1e-3f, "黒が開き始めた時点で字が残っている");
        }

        [Test]
        public void Suspended_FreezesTheClock_AndResumesWhereItStopped()
        {
            // 位置合わせ中に時計が進むと、譲っている間にタイトルが終わってしまう。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec * 0.5f, Ready);
            float before = l.Weights.reveal;

            Run(l, 30f, new TitleInput { concealReady = true, suspended = true });
            Assert.AreEqual(before, l.Weights.reveal, 1e-5f, "譲っている間は進めない");
            Assert.AreEqual(TitleStage.In, l.Stage);

            Run(l, TitleLogic.InSec, Ready);
            Assert.AreEqual(TitleStage.Hold, l.Stage);
        }

        [Test]
        public void ForceClose_EndsImmediately_AndShowsNothing()
        {
            // 卓の ⏭ で導入が段 0 を出たときの逃げ道。コントローラが死んでいても出られる。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            Run(l, 1f, Ready);
            l.ForceClose();
            Assert.AreEqual(TitleStage.Done, l.Stage);
            Assert.AreEqual(0f, l.Weights.veil, 1e-5f);
            Assert.AreEqual(0f, l.Weights.glyph, 1e-5f);
            Assert.IsFalse(l.Active);
        }

        [Test]
        public void Dismiss_IsIgnoredAfterDone()
        {
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();   // 2026-08-12: A で題字を呼び出す段が増えた
            l.ForceClose();
            l.RequestDismiss();
            Assert.AreEqual(TitleStage.Done, l.Stage, "閉じ切った後の要求はタイトルを掘り起こさない");
        }

        [Test]
        public void Push_MovesTowardTheViewer_AndNeverRecedes()
        {
            // ⚠ **2026-08-13 に意味が反転したテスト。** 前は「そのあと奥へ退く」を固定していたが、
            //    ユーザーが「奥へ飛んでいくと箱に吸収されたみたいで変」と言ったので、
            //    いまは**奥へ退かないこと**を固定する。z を引くと題字は視界の中で一様に
            //    小さくなり、消滅ではなく「遠ざかった」と読まれる。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f, Ready);
            l.RequestDismiss();
            Run(l, TitleLogic.FlashSec * 0.5f, Ready);
            Assert.Greater(l.Weights.pushM, 0f, "押した直後は手前へ");

            // ⚠ 閉じ切る前に測る。Done へ入ると重みが全部 0 になり、動きが見えない。
            for (float t = 0f; t < TitleLogic.DissolveDelaySec + TitleLogic.DissolveSec; t += Dt)
            {
                l.Tick(Dt, Ready);
                Assert.GreaterOrEqual(l.Weights.pushM, -1e-5f, "奥へ退いてはいけない");
            }
        }

        [Test]
        public void Swirl_WindsInFromThePress_AndFinishesWithTheBurn()
        {
            // 渦は**押した瞬間から**進み、焼けが終わるころに巻き切る。
            // 焼けと同時に始めると、火が点いてから急にねじれ出して「UI が動いた」に見える。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f, Ready);
            Assert.AreEqual(0f, l.Weights.swirl, 1e-6f, "閉じる前にねじれていてはいけない");

            l.RequestDismiss();
            // ⚠ 隠すものが立たない側で測る。黒が開き切ると段が Done へ抜けて重みが全部 0 になり、
            //    巻き切ったのか畳まれたのかが区別できない（Ready だと Out は 1.35 秒しか無い）。
            Run(l, TitleLogic.DissolveDelaySec * 0.5f, NotReady);
            float early = l.Weights.swirl;
            Assert.Greater(early, 0f, "焼ける前から歪み始めている");
            Assert.Less(early, 0.2f, "頭でねじれ切ってはいけない");

            Run(l, TitleLogic.DissolveDelaySec + TitleLogic.DissolveSec * 0.9f, NotReady);
            Assert.AreEqual(TitleStage.Out, l.Stage);
            Assert.Greater(l.Weights.swirl, 0.85f, "焼け切るころに巻き切っていない");
        }

        [Test]
        public void Burn_KeepsTheInkOpaque_UntilOnlyAshIsLeft()
        {
            // ⚠ 墨は焼け際が食っていくので、**同時に全体を薄くしない**。
            //    頭から不透明度を落とすと、まだ焼けていない所まで半透明になり、
            //    「燃えている」ではなく「フェードアウトしている」に見える。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            Run(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f, Ready);
            l.RequestDismiss();

            // 焼けが 2/3 まで進んでも墨は不透明のまま（食われた所は際が消している）。
            Run(l, TitleLogic.DissolveDelaySec + TitleLogic.DissolveSec * 0.65f, Ready);
            Assert.AreEqual(TitleStage.Out, l.Stage);
            Assert.Greater(l.Weights.dissolve, 0.6f, "焼けが進んでいない");
            Assert.AreEqual(1f, l.Weights.glyph, 1e-5f, "焼けている途中で墨が薄くなっている");
            // 最後に 0 へ落ちることは Dismiss_FlashesImmediately_ThenOpensToPassthrough が固定する。
        }
    }
}
