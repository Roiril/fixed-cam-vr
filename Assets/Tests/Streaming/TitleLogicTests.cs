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
        public void Hold_ClosesItselfAfterTwoSeconds()
        {
            // 2026-08-12 ユーザー指示「2s でタイトルが消え今まで通りのパススルーとしよう」。
            // ⚠ 尺は **A からの通算**（Hold に入ってから 2 秒ではない）。
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();
            for (int i = 0; i < 30; i++) l.Tick(1f / 60f, Ready);     // 0.5 秒
            Assert.AreNotEqual(TitleStage.Out, l.Stage, "まだ消え始めてはいけない");
            for (int i = 0; i < 120; i++) l.Tick(1f / 60f, Ready);    // 通算 2.5 秒
            Assert.AreEqual(TitleStage.Out, l.Stage, "2 秒で消え始めていない");
        }

        [Test]
        public void Begin_StartsBlack_WithNoLetters_UntilA()
        {
            // ⚠ **周回リセット直後は真っ暗で、字は出ていない。**
            //    A を押すまで時間では何も起きない（前は Begin で字が出始めていた）。
            var l = new TitleLogic();
            l.Begin();
            for (int i = 0; i < 600; i++) l.Tick(1f / 60f, Ready);    // 10 秒放置
            Assert.AreEqual(TitleStage.Wait, l.Stage);
            Assert.AreEqual(1f, l.Weights.veil, 1e-6f, "黒が張っていない");
            Assert.AreEqual(0f, l.Weights.glyph, 1e-6f, "A を押す前に字が出ている");
            Assert.IsTrue(l.AwaitingInput);
            Assert.IsFalse(l.GlyphShowing);

            l.RequestAdvance();
            l.Tick(1f / 60f, Ready);
            Assert.AreEqual(TitleStage.In, l.Stage);
            Assert.IsTrue(l.GlyphShowing, "A を押しても字が立っていない");
        }

        [Test]
        public void A_TwiceInQuickSuccession_DoesNotSkipTheTitle()
        {
            // 現場で A を軽く 2 回叩いても、題字が一瞬で消えない。
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
            Assert.Greater(l.Weights.flashAmt, 0f, "A の光は押した直後から出る");

            // 字は黒より先に消え切る（最後に残るのが黒 ＝ 継ぎ目が 1 回で済む）。
            Run(l, TitleLogic.DissolveDelaySec + TitleLogic.DissolveSec, Ready);
            Assert.AreEqual(0f, l.Weights.glyph, 1e-2f);

            Run(l, TitleLogic.OpenSec, Ready);
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

            // 上限より手前では、まだ黒を開き始めていない。
            Run(l, TitleLogic.ConcealWaitMaxSec * 0.5f, NotReady);
            Assert.AreEqual(1f, l.Weights.veil, 1e-3f, "隠すものが立つまでは開かない");

            Run(l, TitleLogic.ConcealWaitMaxSec + TitleLogic.OpenSec
                   + TitleLogic.DissolveDelaySec + TitleLogic.DissolveSec, NotReady);
            Assert.AreEqual(TitleStage.Done, l.Stage, "諦めて開き切る（ラッチにしない）");
            Assert.AreEqual(0f, l.Weights.veil, 1e-5f);
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
            Assert.AreEqual(TitleStage.Done, l.Stage, "閉じ切った後の A はタイトルを掘り起こさない");
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
