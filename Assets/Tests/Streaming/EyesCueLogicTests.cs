#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 闇の目の開き始め・閉じ始めを体験者の居場所で決める層（<c>canon/LEDGER.md</c> 0093）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 4 つ。どれも実機の画では「なんとなく短い / 出なかった」にしか見えない:
    /// ① 半ばで<b>閉じ切っていない目をいきなり閉じにかかる</b>（開眼が 1 度も起きない）
    /// ② 区間が変わった瞬間に<b>打ち切る</b>（ユーザーが名指しで禁じたもの）
    /// ③ 閉じ切った後、同じ区間で<b>もう一度兆しから始まる</b>
    /// ④ 位置を測れない現場（未登録・layout 不在）で<b>目が一生出ない</b>
    /// </summary>
    public sealed class EyesCueLogicTests
    {
        private static ZoneSpan Span(float p, int camera = 2, int visit = 7)
            => new ZoneSpan(true, camera, visit, p);

        // ---------------------------------------------------------------- ① 入った所 → 半ば

        [Test]
        public void EntersZone_StartsOpening()
        {
            var c = new EyesCueLogic();
            c.Tick(armed: true, EyesStage.Off, Span(0f));
            Assert.That(c.Wanted, Is.True, "入った瞬間から出す");
            Assert.That(c.Rate, Is.EqualTo(1f), "頭から追い上げない（著作した緩急のまま）");
            Assert.That(c.Finishing, Is.False);
        }

        [Test]
        public void BeforeHalf_RunsAtAuthoredSpeed()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Hint, Span(0.49f));
            Assert.That(c.Finishing, Is.False, "半分まで来ていない");
            Assert.That(c.Rate, Is.EqualTo(1f));
        }

        /// <summary>
        /// ⚠⚠ <b>半ばで「閉じろ」ではなく「急いで開き切れ」。</b> 開き切っていない目は閉じられない
        /// （閉じる曲線は開き切った形から逆に辿る）。ここを素直に <c>wanted:false</c> へ落とすと、
        /// 開眼（この異変の山）が 1 度も起きないまま終わる。
        /// </summary>
        [Test]
        public void AtHalf_WhileStillOpening_HurriesInsteadOfClosing()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Stare, Span(0.5f));
            Assert.That(c.Finishing, Is.True, "終了演出へ入った");
            Assert.That(c.Wanted, Is.True, "開くのは止めない");
            Assert.That(c.Rate, Is.EqualTo(EyesCueLogic.HurryRate), "倍速で追い上げる");
        }

        [Test]
        public void AtHalf_WhenAlreadyOpen_StartsClosingAtAuthoredSpeed()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Hold, Span(0.5f));
            Assert.That(c.Wanted, Is.False, "開き切っているなら半ばで閉じ始める");
            Assert.That(c.Rate, Is.EqualTo(1f),
                        "⚠ 閉じは速めない（1 つの瞼が 0.11 秒 → 倍速だと 1.6 コマで消える・0085）");
        }

        [Test]
        public void OnceFinishing_DoesNotGoBack()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Hold, Span(0.6f));
            // 体験者が半ばより手前へ引き返した。閉じ始めたものは戻さない。
            c.Tick(true, EyesStage.Fading, Span(0.2f));
            Assert.That(c.Finishing, Is.True);
            Assert.That(c.Wanted, Is.False);
        }

        // ---------------------------------------------------------------- ② 打ち切らない

        /// <summary>
        /// ユーザー指定（0093）「強制打ち切りではなく流しきってください」。
        /// カットが終わっても（＝ <c>armed</c> が降りても）開き切るまでは出し続ける。
        /// </summary>
        [Test]
        public void CameraSwitchMidOpening_PlaysItOutFast_NeverCuts()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Hint, Span(0.2f));

            c.Tick(armed: false, EyesStage.Hint, Span(0.2f, camera: 0, visit: 8));
            Assert.That(c.Wanted, Is.True, "打ち切らない");
            Assert.That(c.Rate, Is.EqualTo(EyesCueLogic.HurryRate), "倍速で流しきる");

            // 開き切ったら、そこで初めて閉じへ渡す。
            c.Tick(false, EyesStage.Hold, Span(0.3f, camera: 0, visit: 8));
            Assert.That(c.Wanted, Is.False);
            Assert.That(c.Rate, Is.EqualTo(1f));
        }

        [Test]
        public void ZoneChangeToSameCameraByLap_CountsAsLeaving()
        {
            // 一周して同じカメラの区間へ戻ってきた。カメラ index だけでは見分けが付かないので、
            // 通し番号（visit）で「別の滞在」と読む。
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f, camera: 2, visit: 1));
            c.Tick(true, EyesStage.Hint, Span(0.1f, camera: 2, visit: 2));
            Assert.That(c.Finishing, Is.True);
        }

        // ---------------------------------------------------------------- ③ 二度目を始めない

        [Test]
        public void AfterClosing_DoesNotRestartInTheSameZone()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Hold, Span(0.5f));   // 閉じ始める
            c.Tick(true, EyesStage.Off, Span(0.7f));    // 閉じ切った
            Assert.That(c.Spent, Is.True);

            // カットはまだ同じ区間で eyes:1 を言い続けている。ここで兆しから鳴り直させない。
            c.Tick(true, EyesStage.Off, Span(0.8f));
            Assert.That(c.Wanted, Is.False, "同じ滞在で二度目は始めない");

            // 次の区間へ入れば、また出せる。
            c.Tick(true, EyesStage.Off, Span(0.0f, camera: 0, visit: 9));
            Assert.That(c.Wanted, Is.True);
            Assert.That(c.Finishing, Is.False);
        }

        // ---------------------------------------------------------------- ④ 位置が無い現場

        /// <summary>
        /// 位置合わせをしていない機・layout が届いていない現場では
        /// <see cref="ZoneSpan.valid"/> が false。<b>位置を必須条件にしない</b> —
        /// 従来どおりカットの終わりで畳む（そこで初めて流しきりが効く）。
        /// </summary>
        [Test]
        public void WithoutSpan_StillOpens_AndEndsWithTheCut()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, default);
            Assert.That(c.Wanted, Is.True, "位置が無くても出る");

            c.Tick(true, EyesStage.Hold, default);
            Assert.That(c.Finishing, Is.False, "位置が無いので半ばの判定は起きない");

            c.Tick(armed: false, EyesStage.Hold, default);
            Assert.That(c.Finishing, Is.True, "カットの終わりで閉じる");
        }

        [Test]
        public void Reset_ForgetsEverything()
        {
            var c = new EyesCueLogic();
            c.Tick(true, EyesStage.Off, Span(0f));
            c.Tick(true, EyesStage.Hold, Span(0.9f));
            c.Reset();
            Assert.That(c.Running, Is.False);
            Assert.That(c.Finishing, Is.False);
            Assert.That(c.Wanted, Is.False);
            Assert.That(c.Rate, Is.EqualTo(1f));
        }
    }
}
