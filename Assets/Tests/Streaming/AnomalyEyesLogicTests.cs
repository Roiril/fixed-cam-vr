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
        public void Hint_OpensAtTheSoundsOnset_ThenFollowsItsSwell()
        {
            // ⚠⚠ 0076 の主題（**止まっている時間が長く、開くのは一瞬**）は残しつつ、
            //    0135 で「一瞬」の置き場を**音の頭**に移した。音が立つまでは 1 画素も出ない。
            float justBefore = AnomalyEyesLogic.HintCurve((AnomalyEyesLogic.HintOnsetSec - 0.02f) / Hint);
            Assert.That(justBefore, Is.EqualTo(0f), "音が立つ前は闇のまま");

            // 音の頭で一気に（0.04 秒で 0.45 まで）。等速なら 0.04 秒では 0.15 も開かない。
            float atOnset = AnomalyEyesLogic.HintCurve((AnomalyEyesLogic.HintOnsetSec + 0.04f) / Hint);
            Assert.That(atOnset, Is.GreaterThanOrEqualTo(0.40f), "音の頭で一気に開いていない");

            // 音の膨らみのとおりに 0.27 秒で開き切り、以後は戻らない（瞬きは凝視の仕事）。
            float prev = 0f;
            for (float u = 0f; u <= 0.30f; u += 0.005f)
            {
                float v = AnomalyEyesLogic.HintOpenAt(u);
                Assert.That(v, Is.GreaterThanOrEqualTo(prev - 1e-4f), $"音の頭から {u:F3}s で戻っている");
                prev = v;
            }
            Assert.That(AnomalyEyesLogic.HintOpenAt(0.27f), Is.EqualTo(1f).Within(1e-3f), "0.27 秒で開き切る");
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

        /// <summary>
        /// ⚠⚠ <b>凝視はもう「報告を押し切る時間」ではない</b>（2026-08-20・<c>canon/LEDGER.md</c> 0094）。
        /// 0075 は「気づく ＋ 手 ＋ 長押し 1 秒」で 2.8 秒を置いていたが、
        /// 早回しの動画を見たユーザーが 0.5 秒を選んだ。**押し切る前に残りが開き始める。**
        ///
        /// ここは値を固定するだけのテスト —— 戻したくなったら伸ばすのは<b>凝視</b>で、
        /// 開眼（3.0 秒）ではない。目の異変は <c>dismissible</c> を立てていない（0084）ので、
        /// 押し切れるかは体験の成否に効かない。
        /// </summary>
        [Test]
        public void Stare_IsNoLongerSizedForTheReportPress()
        {
            Assert.That(Stare, Is.EqualTo(0.5f).Within(1e-3f));
            const float pressSec = 0.8f + 1.0f + 1.0f;   // 気づく ＋ 手 ＋ 長押し
            Assert.That(Stare, Is.LessThan(pressSec),
                "押し切れる長さへ戻すなら、0094 を覆す判定が要る");
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
            // 10.5（初版）→ 8.4（0076）→ **4.92**（0094 で止まる 3 つを 0.5 秒へ）。
            Assert.That(Hint + Stare + Swarm, Is.EqualTo(4.92f).Within(1e-2f));
            // 止まっている 3 つ ＝ 闇 / 開き切ってからの静止 / 凝視（0135 で断片の静止は無くなった）。
            // 闇は音の頭（0.70）まで。開き切ってからの静止は 0.45。凝視は 0.5。
            Assert.That(AnomalyEyesLogic.HintOnsetSec, Is.EqualTo(0.70f).Within(0.01f));
            Assert.That(Hint - AnomalyEyesLogic.HintFullSec, Is.EqualTo(0.45f).Within(0.01f));
            Assert.That(Stare, Is.EqualTo(0.5f).Within(1e-3f));
            // 動いている所は音の形そのもの（頭から 0.27 秒で開き切る）。
            Assert.That(AnomalyEyesLogic.HintFullSec - AnomalyEyesLogic.HintOnsetSec,
                        Is.EqualTo(0.27f).Within(0.01f));
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
            l.Tick(AnomalyEyesLogic.CloseSec * 0.72f, wanted: false, density: 1f);
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Fading));
            Assert.That(l.Fade, Is.EqualTo(1f), "閉じている最中に薄くしない");
            Assert.That(l.Field, Is.EqualTo(0f), "いちめんは先に閉じ切っている");
            Assert.That(l.Big, Is.EqualTo(1f), "大きい目はまだ開いたまま（ここが間）");
            Assert.That(l.Smile, Is.EqualTo(1f), "そのとき大きい目は笑い切っている");
        }

        /// <summary>
        /// ⚠⚠ <b>閉じる順の幅は開くときより広い</b>（2026-08-18 の赤入れ
        /// 「最後の一つ以外も、目を閉じるようなアニメーションで閉じて」）。
        /// 同じ幅だと 1 つの目が閉じるのに 30fps で 0.6 コマしかかからず、
        /// <b>瞼が下りる過程が 1 コマも描かれない</b>（＝ 消えたようにしか見えない）。
        /// </summary>
        [Test]
        public void Closing_TakesLongEnoughForALidToBeSeenComingDown()
        {
            var l = Run(Hint + Stare + Swarm + 1f);
            Assert.That(l.Span, Is.EqualTo(AnomalyEyesLogic.SwarmSpan), "開いているあいだは開く幅");

            l.Tick(0.05f, wanted: false, density: 1f);
            Assert.That(l.Span, Is.EqualTo(AnomalyEyesLogic.CloseSpan), "閉じ始めたら閉じる幅");

            // 1 つの目が閉じるのにかかる秒数 ＝ 幅 × いちめんが閉じる尺
            float perEye = AnomalyEyesLogic.CloseSpan
                           * AnomalyEyesLogic.CloseSec * AnomalyEyesLogic.CloseFieldAt;
            Assert.That(perEye, Is.GreaterThan(0.08f),
                        $"1 つ {perEye:0.000}s では瞼が下りて見えない（実物の瞬きは 0.1〜0.15 秒）");
        }

        /// <summary>
        /// 開きかけの断片（<c>DROP_EARLY</c>）は<b>闇から現れるときだけ</b>。
        /// 閉じるときも効かせると、瞼が下りるのではなく<b>砕けて散る</b>。
        /// </summary>
        [Test]
        public void Closing_TellsTheShaderToStopFragmenting()
        {
            var l = Run(Hint + Stare + Swarm + 1f);
            Assert.That(l.Closing, Is.EqualTo(0f), "開いているあいだは断片を止めない");
            l.Tick(0.05f, wanted: false, density: 1f);
            Assert.That(l.Closing, Is.EqualTo(1f));
        }

        /// <summary>
        /// <b>最後の 1 つは笑ってから閉じる</b>（ユーザー赤入れ
        /// 「笑っているみたいな感じで、目を細めてから閉じて」）。
        /// ⚠ 細めるのは <c>Smile</c>（下瞼だけ持ち上げる）であって <c>Big</c> ではない —
        /// <c>Big</c> を下げると上下から均等に狭まって<b>眠そうな目</b>になる。
        /// </summary>
        [Test]
        public void TheLastEye_SmilesBeforeItShuts()
        {
            Assert.That(AnomalyEyesLogic.CloseSmileCurve(AnomalyEyesLogic.CloseFieldAt * 0.5f),
                        Is.EqualTo(0f), "いちめんが閉じるあいだはまだ笑わない");
            Assert.That(AnomalyEyesLogic.CloseSmileCurve(AnomalyEyesLogic.CloseSmileAt),
                        Is.EqualTo(1f), "いちめんが閉じ切ったあとに笑い切る");
            Assert.That(AnomalyEyesLogic.CloseSmileCurve(1f), Is.EqualTo(1f),
                        "笑ったまま閉じる（閉じる直前に真顔へ戻らない）");
            // 笑っているあいだ、開き具合そのものは落ちない
            Assert.That(AnomalyEyesLogic.CloseBigCurve(AnomalyEyesLogic.CloseSmileAt), Is.EqualTo(1f));
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
            Assert.That(Run(Hint + Stare * 0.5f).Gaze, Is.EqualTo(0f), "凝視は凝視（動かしたら凝視ではない）");
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

        /// <summary>
        /// <b>追い上げ</b>（2026-08-19・<c>canon/LEDGER.md</c> 0093）。区間の半ばまでに開き切って
        /// いなければ倍速で進む。⚠ <b>飛ばすのではなく速める</b> — 段の並びも緩急も同じものを通る。
        /// </summary>
        [Test]
        public void Rate_SpeedsUpTheSameCurve_WithoutSkippingStages()
        {
            var fast = new AnomalyEyesLogic();
            for (int i = 0; i < 60; i++) fast.Tick(0.05f, wanted: true, density: 1f, rate: 2f);
            var slow = Run(0.05f * 60 * 2f);
            Assert.That(fast.Stage, Is.EqualTo(slow.Stage), "倍速でも同じ段に居る");
            Assert.That(fast.Big, Is.EqualTo(slow.Big).Within(1e-3f));
        }

        /// <summary>
        /// <see cref="AnomalyEyesLogic.Reset"/> は「無かったことにする」。
        /// <c>wanted:false</c>（閉じる演出を始める）とは別物 — 体験者の交代でこちらを通らないと、
        /// 次の人の視界に前の人の目が閉じ残る。
        /// </summary>
        [Test]
        public void Reset_DropsEverythingInOneFrame()
        {
            var l = Run(Hint + Stare + Swarm);
            Assert.That(l.Visible, Is.True);
            l.Reset();
            Assert.That(l.Stage, Is.EqualTo(EyesStage.Off));
            Assert.That(l.Visible, Is.False);
            Assert.That(l.Fade, Is.EqualTo(0f));
            Assert.That(l.Big, Is.EqualTo(0f));
            Assert.That(l.Field, Is.EqualTo(0f));
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
            Assert.That(Run(Hint + Stare * 0.5f).AnchorLocked, Is.False, "凝視も回してよい");
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
    /// <b>これが無いと、体験者が別の方を向いているあいだに兆しと凝視の 1.92 秒が終わる。</b>
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
