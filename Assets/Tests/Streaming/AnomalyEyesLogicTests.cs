#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 闇に目が開く異変の進み方（<c>canon/LEDGER.md</c> 0075）。
    ///
    /// ⚠ ここで押さえたい壊れ方は 5 つ。どれも実機の画を眺めても気づきにくい:
    /// ① 兆しが直線で開く（最初から見えてしまう ＝「気づかれにくい」が消える）
    /// ② 凝視のあいだに残りが開き始める（報告する時間が無くなる）
    /// ③ 畳んでいる最中に進みが増える（押した結果が「消えた」ではなく「増えた」に見える）
    /// ④ 次の体験者へ進みが持ち越される（2 人目が途中から始まる）
    /// ⑤ 残りが開いた後に群れが回る（世界が回って見える）
    /// </summary>
    public sealed class AnomalyEyesLogicTests
    {
        private const float Hint = AnomalyEyesLogic.HintSec;
        private const float Stare = AnomalyEyesLogic.StareSec;
        private const float Swarm = AnomalyEyesLogic.SwarmSec;

        private static AnomalyEyesLogic Run(float sec, float dt = 0.05f, float density = 1f)
        {
            var l = new AnomalyEyesLogic();
            Advance(l, sec, dt, density);
            return l;
        }

        private static void Advance(AnomalyEyesLogic l, float sec, float dt = 0.05f, float density = 1f)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(sec / dt));
            for (int i = 0; i < steps; i++) l.Tick(dt, wanted: true, density: density);
        }

        // ---------------------------------------------------------------- ① 兆し

        [Test]
        public void Hint_StartsInDarkness()
        {
            // 最初のしばらくは 1 画素も出ない（闇のまま）。
            var l = Run(Hint * 0.2f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Hint));
            Assert.That(l.Big, Is.EqualTo(0f), "頭は闇のまま");
            Assert.That(l.Field, Is.EqualTo(0f), "兆しでは大きい目 1 つだけ");
        }

        [Test]
        public void Hint_HoldsAsAFragmentThenSnapsOpen()
        {
            // ⚠⚠ ここが 0076 の主題。**止まっている時間が長く、開くのは一瞬**。
            //    初版は等速で開いていて、判定は「ゆっくり過ぎて怖くない」だった。
            float mid1 = AnomalyEyesLogic.HintCurve(0.45f);
            float mid2 = AnomalyEyesLogic.HintCurve(0.80f);
            Assert.That(mid1, Is.EqualTo(AnomalyEyesLogic.HintCrackOpen).Within(1e-3f));
            Assert.That(mid2, Is.EqualTo(mid1).Within(1e-3f), "断片のまま止まっている（長い間）");
            Assert.That(mid1, Is.LessThan(0.25f), "止まっている間は気づかれない大きさ");

            // 見開きは 0.2 秒以内（＝ 尺の 8% 未満）で終わる。
            float snapSec = (AnomalyEyesLogic.HintSnapAt - AnomalyEyesLogic.HintHoldAt) * Hint;
            Assert.That(snapSec, Is.LessThan(0.20f), "見開くのは一瞬でなければ怖くない");
            Assert.That(AnomalyEyesLogic.HintCurve(1f), Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void Hint_IsNotLinear()
        {
            // 直線なら半分の時刻でちょうど 0.5。
            float half = AnomalyEyesLogic.HintCurve(0.5f);
            Assert.That(half, Is.LessThan(0.30f), "兆しは直線で開いてはいけない");
        }

        [Test]
        public void Hint_EndsFullyOpen()
        {
            var l = Run(Hint);
            Assert.That(l.Big, Is.EqualTo(1f).Within(1e-3f));
        }

        // ---------------------------------------------------------------- ② 凝視

        [Test]
        public void Stare_KeepsTheRestClosed()
        {
            // 「気づいて報告ボタンを押すくらいの時間」— ここで残りが開いたら beat が消える。
            var l = Run(Hint + Stare - 0.10f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Stare));
            Assert.That(l.Big, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(l.Field, Is.EqualTo(0f));
            Assert.That(l.Intensity, Is.EqualTo(0f));
        }

        [Test]
        public void Stare_BlinksOnceInTheMiddle()
        {
            // 静止のただ中で 1 度だけ落ちる。**閉じ切らない**（閉じると「消えた」に見える）。
            float at = AnomalyEyesLogic.StareCurve(AnomalyEyesLogic.StareBlinkAt);
            Assert.That(at, Is.EqualTo(AnomalyEyesLogic.StareBlinkFloor).Within(1e-3f));
            Assert.That(at, Is.GreaterThan(0.02f), "閉じ切らない");
            Assert.That(AnomalyEyesLogic.StareCurve(0.05f), Is.EqualTo(1f).Within(1e-3f));
            Assert.That(AnomalyEyesLogic.StareCurve(0.95f), Is.EqualTo(1f).Within(1e-3f));
            float blinkSec = 2f * AnomalyEyesLogic.StareBlinkHalf * Stare;
            Assert.That(blinkSec, Is.LessThan(0.45f), "瞬きは一瞬（長いと「眠い目」に見える）");
        }

        [Test]
        public void Stare_IsLongEnoughToPressTheReportButton()
        {
            // 長押し 1 秒（VisitorMarkHoldLogic.DefaultHoldSec）＋ 気づいて手を動かす時間。
            Assert.That(Stare, Is.GreaterThanOrEqualTo(2.5f),
                "凝視が 2.5 秒を切ると、気づいて長押しし切る前に全部開く");
        }

        // ---------------------------------------------------------------- ③ 開眼

        [Test]
        public void Swarm_OpensEveryEyeWithinThreeSeconds()
        {
            var l = Run(Hint + Stare + Swarm);
            Assert.That(l.Field, Is.EqualTo(1f).Within(1e-3f));
            // 真後ろ（順位 1）の目まで開き切っている。
            Assert.That(AnomalyEyesLogic.EyeOpen(l.Field, 1f), Is.EqualTo(1f).Within(1e-3f));
            Assert.That(AnomalyEyesLogic.EyeOpen(l.Field, 0f), Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void Swarm_RipplesThenPausesThenRushes()
        {
            // ⚠⚠ 等速で回すと「波が通り過ぎるのを眺める」になる。**間**が驚きを作る（0076）。
            float ripple = AnomalyEyesLogic.SwarmCurve(AnomalyEyesLogic.SwarmRippleAt);
            float pause = AnomalyEyesLogic.SwarmCurve(AnomalyEyesLogic.SwarmPauseAt - 0.01f);
            Assert.That(ripple, Is.EqualTo(AnomalyEyesLogic.SwarmRippleField).Within(1e-3f));
            Assert.That(pause, Is.EqualTo(ripple).Within(1e-3f), "さざめきの後は止まっている");
            float pauseSec = (AnomalyEyesLogic.SwarmPauseAt - AnomalyEyesLogic.SwarmRippleAt) * Swarm;
            Assert.That(pauseSec, Is.GreaterThan(0.25f), "間が短いと落差にならない");
            Assert.That(AnomalyEyesLogic.SwarmCurve(AnomalyEyesLogic.SwarmRushAt),
                Is.EqualTo(1f).Within(1e-3f), "一気に 360 度まで届く");
        }

        [Test]
        public void SingleEye_OpensInAboutOneTenthOfASecond()
        {
            // ⚠ ここが「ゆっくり過ぎて怖くない」の主因だった（初版は 0.84 秒）。
            float sec = AnomalyEyesLogic.SwarmSpan * Swarm;
            Assert.That(sec, Is.LessThan(0.15f), "1 つの目は瞬時に開く（実物の目の速さ）");
        }

        [Test]
        public void Swarm_SpreadsOutwardFromTheBigEye()
        {
            // 波は大きい目のまわりから広がる（乱数の点滅にしない）。
            var l = Run(Hint + Stare + Swarm * 0.5f);
            float near = AnomalyEyesLogic.EyeOpen(l.Field, 0.05f);
            float far = AnomalyEyesLogic.EyeOpen(l.Field, 0.95f);
            Assert.That(near, Is.GreaterThan(far), "近い目から先に開く");
            Assert.That(far, Is.EqualTo(0f), "真後ろはまだ開いていない");
        }

        [Test]
        public void TotalToFullOpen_FitsInASegment()
        {
            // 設計値の固定。ここを動かすと「区間の滞在に収まる」前提が崩れる。
            Assert.That(Hint + Stare + Swarm, Is.EqualTo(8.4f).Within(1e-3f));
        }

        // ---------------------------------------------------------------- ④ 畳む

        [Test]
        public void Release_ClosesWithoutAdvancing()
        {
            var l = Run(Hint + Stare + Swarm * 0.4f);
            float field = l.Field;
            l.Tick(0.05f, wanted: false, density: 1f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Fading));
            Assert.That(l.Field, Is.LessThan(field), "閉じ始めたら広がりは下がる");
            Assert.That(l.Field, Is.GreaterThan(0f), "0.05 秒で全部消えるのは速すぎる");
        }

        /// <summary>
        /// ⚠⚠ <b>閉じるのは形であって不透明度ではない</b>（<c>canon/LEDGER.md</c> 0084）。
        /// 一様に薄くすると「閉じた」ではなく「電源が落ちた」に見える。
        /// </summary>
        [Test]
        public void Closing_KeepsFullOpacity_AndClosesByShape()
        {
            var l = Run(Hint + Stare + Swarm + 1f);
            l.Tick(AnomalyEyesLogic.CloseSec * 0.5f, wanted: false, density: 1f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Fading));
            Assert.That(l.Fade, Is.EqualTo(1f), "閉じている最中に薄くしない");
            Assert.That(l.Field, Is.EqualTo(0f), "いちめんは先に閉じ切っている");
            Assert.That(l.Big, Is.EqualTo(1f), "大きい目はまだ開いたまま（ここが間）");
        }

        /// <summary>
        /// <b>開いた順の逆で閉じる</b> — いちめん → 間 → 大きい目。
        /// 兆し（闇 → 断片 → 静止 → 見開く）と対になる形。
        /// </summary>
        [Test]
        public void Closing_HasThePauseBeforeTheLastEyeShuts()
        {
            // いちめんが閉じ切る所
            Assert.That(AnomalyEyesLogic.CloseFieldCurve(AnomalyEyesLogic.CloseFieldAt),
                        Is.EqualTo(0f).Within(0.001f));
            // 間のあいだ、大きい目は 1 のまま
            float mid = 0.5f * (AnomalyEyesLogic.CloseFieldAt + AnomalyEyesLogic.CloseHoldAt);
            Assert.That(AnomalyEyesLogic.CloseBigCurve(mid), Is.EqualTo(1f));
            Assert.That(AnomalyEyesLogic.CloseBigCurve(AnomalyEyesLogic.CloseHoldAt), Is.EqualTo(1f));
            // そのあと落ちる
            Assert.That(AnomalyEyesLogic.CloseBigCurve(1f), Is.EqualTo(0f).Within(0.001f));
            Assert.That(AnomalyEyesLogic.CloseHoldAt - AnomalyEyesLogic.CloseFieldAt,
                        Is.GreaterThan(1f - AnomalyEyesLogic.CloseHoldAt),
                        "間は最後の一閉じより長いこと（そこが効く）");
        }

        // ---------------------------------------------------------------- ④b 待機中の視線

        /// <summary>
        /// <b>開く動きと視線を重ねない</b>（0076「動かすものは 1 つに絞る」）。
        /// 開き切って待機に入ってから動き出す。
        /// </summary>
        [Test]
        public void Gaze_StaysStillUntilEveryEyeHasOpened()
        {
            Assert.That(Run(1.0f).Gaze, Is.EqualTo(0f), "兆しでは動かない");
            Assert.That(Run(Hint + 1.0f).Gaze, Is.EqualTo(0f), "凝視は凝視（動かしたら凝視ではない）");
            Assert.That(Run(Hint + Stare + Swarm * 0.5f).Gaze, Is.EqualTo(0f), "開いている最中も動かない");
        }

        [Test]
        public void Gaze_RisesDuringTheHold()
        {
            var l = Run(Hint + Stare + Swarm + AnomalyEyesLogic.GazeRiseSec * 0.5f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Hold));
            Assert.That(l.Gaze, Is.GreaterThan(0f).And.LessThan(1f), "一拍おいてから動き出す");
            Assert.That(Run(Hint + Stare + Swarm + AnomalyEyesLogic.GazeRiseSec + 0.2f).Gaze,
                        Is.EqualTo(1f));
        }

        /// <summary>閉じ始めたら視線は止まる（閉じる動きが主）。</summary>
        [Test]
        public void Gaze_StopsWhenTheEyesStartClosing()
        {
            var l = Run(Hint + Stare + Swarm + 2f);
            Assert.That(l.Gaze, Is.EqualTo(1f));
            l.Tick(AnomalyEyesLogic.GazeFallSec, wanted: false, density: 1f);
            Assert.That(l.Gaze, Is.EqualTo(0f));
        }

        [Test]
        public void Release_ResetsSoTheNextVisitorStartsFromTheHint()
        {
            var l = Run(Hint + Stare + Swarm);
            for (int i = 0; i < 20; i++) l.Tick(0.1f, wanted: false, density: 1f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Off));
            Assert.That(l.Fade, Is.EqualTo(0f));

            l.Tick(0.05f, wanted: true, density: 1f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Hint));
            Assert.That(l.JustStarted, Is.True, "始まった縁は 1 度だけ立つ（向きを合わせ直す合図）");
            Assert.That(l.Big, Is.LessThan(0.01f), "2 人目も必ず兆しから");
        }

        [Test]
        public void Reapply_DoesNotRestartWithinTheSameEvent()
        {
            // カットが変わって同じ値を言い直しても、1 つの出来事として続く。
            var l = Run(Hint + 1.0f);
            EyesStage stage = l.Stage;
            l.Tick(0.05f, wanted: true, density: 1f);
            Assert.That(l.JustStarted, Is.False);
            Assert.That(l.Stage, Is.EqualTo(stage));
        }

        // ---------------------------------------------------------------- ⑤ 向き

        [Test]
        public void Anchor_IsLockedOnceTheSwarmBegins()
        {
            Assert.That(Run(1.0f).AnchorLocked, Is.False, "兆しは回してよい（大きい目は視界の外）");
            Assert.That(Run(Hint + 1.0f).AnchorLocked, Is.False, "凝視も回してよい");
            Assert.That(Run(Hint + Stare + 0.5f).AnchorLocked, Is.True, "開き始めたら二度と回さない");
            Assert.That(Run(Hint + Stare + Swarm + 1f).AnchorLocked, Is.True);
        }

        [Test]
        public void Density_IsLatchedFromTheCut()
        {
            var l = Run(1.0f, density: 0.4f);
            Assert.That(l.Density, Is.EqualTo(0.4f).Within(1e-4f));
        }
    }

    /// <summary>
    /// 大きい目を視界へ入れ直す判断（<see cref="EyeAnchorLogic"/>）。
    /// <b>これが無いと、体験者が別の方を向いているあいだに兆しと凝視の 7.5 秒が終わる。</b>
    /// </summary>
    public sealed class EyeAnchorLogicTests
    {
        [Test]
        public void InView_NeverReanchors()
        {
            var a = new EyeAnchorLogic();
            for (int i = 0; i < 100; i++)
                Assert.That(a.Tick(0.1f, offAxisDeg: 40f, locked: false), Is.False);
        }

        [Test]
        public void OutOfView_ReanchorsAfterTheHold()
        {
            var a = new EyeAnchorLogic();
            float t = 0f;
            bool fired = false;
            for (int i = 0; i < 40 && !fired; i++)
            {
                fired = a.Tick(0.1f, offAxisDeg: 140f, locked: false);
                t += 0.1f;
            }
            Assert.That(fired, Is.True);
            Assert.That(t, Is.EqualTo(EyeAnchorLogic.LostHoldSec).Within(0.15f),
                "首を振っただけで動かないよう、外に居続けた時間で判定する");
        }

        [Test]
        public void GlancingAway_DoesNotReanchor()
        {
            var a = new EyeAnchorLogic();
            // 0.4 秒だけ外れて戻る、を繰り返しても発火しない。
            for (int k = 0; k < 10; k++)
            {
                for (int i = 0; i < 4; i++)
                    Assert.That(a.Tick(0.1f, offAxisDeg: 120f, locked: false), Is.False);
                Assert.That(a.Tick(0.1f, offAxisDeg: 20f, locked: false), Is.False);
            }
        }

        [Test]
        public void Locked_NeverReanchors()
        {
            var a = new EyeAnchorLogic();
            for (int i = 0; i < 100; i++)
                Assert.That(a.Tick(0.1f, offAxisDeg: 179f, locked: true), Is.False,
                    "残りが開いた後に回すと、世界が回って見える");
        }
    }
}
