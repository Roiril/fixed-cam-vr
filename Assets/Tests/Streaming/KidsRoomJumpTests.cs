#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 2-B の子供部屋で、押し始めたら人形の落ちる所へ飛び、報告しても最後のコマまで見せ切る（<c>canon/LEDGER.md</c> 0375）。
    ///
    /// ここで見るのは判定と尺と音の口。動画の seek と重ねる画（<see cref="ScreenOverlayController.BeginSeekJump"/>）は
    /// VideoPlayer が要るので EditMode では動かせない。走行ログの <c>ev=kidsjump</c> / <c>ev=kidsland</c>
    /// （<c>tools/analyze-xp-log.py</c> の <c>kids_jump_findings</c>）が受け持つ。
    /// </summary>
    public sealed class KidsRoomJumpTests
    {
        private const string Res = "Sound/sfx_kidsroom";
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        /// <summary>人形が画の上から入る最初のコマ（112 コマ目 / 24fps）。<see cref="TakeRunner.KidsRoomFallSec"/> の測り方を参照。</summary>
        private const float DollEntersSec = 112f / 24f;

        private readonly List<UnityEngine.Object> _spawned = new();
        private float _now;

        [SetUp]
        public void SetUp() => _now = 0f;

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object item in _spawned)
                if (item != null) UnityEngine.Object.DestroyImmediate(item);
            _spawned.Clear();
        }

        // ------------------------------------------------------------------ 落ちる所と飛ぶ判定

        /// <summary>重ね終わってから人形が入る（重ねている途中に落ち始めると前の画と二重に見える）。</summary>
        [Test]
        public void FallPoint_FadeFinishesBeforeTheDollEnters()
        {
            Assert.That(TakeRunner.KidsRoomJumpFadeSec, Is.EqualTo(0.4f).Within(1e-6f));
            Assert.That(TakeRunner.KidsRoomFallSec, Is.EqualTo(4.2f).Within(1e-6f));
            Assert.That(TakeRunner.KidsRoomFallSec + TakeRunner.KidsRoomJumpFadeSec, Is.LessThan(DollEntersSec));
        }

        [Test]
        public void ShouldJump_OnlyBeforeTheFallPoint_OncePerTake_WhileTheVideoShows()
        {
            const float fall = TakeRunner.KidsRoomFallSec;
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, 0f, fall), Is.True, "準備中（0 秒）も飛ぶ");
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, 1.3f, fall), Is.True);
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, 4.19f, fall), Is.True);
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, 4.2f, fall), Is.False, "落ちる所以後は何もしない");
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, 5.5f, fall), Is.False);
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, true, 1.3f, fall), Is.False, "1 回の演出で 1 度だけ");
            Assert.That(TakeRunner.ShouldJumpKidsRoom(false, false, 1.3f, fall), Is.False, "子供部屋のカットが出ていない");
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, float.NaN, fall), Is.False, "動画が出ていない");
            Assert.That(TakeRunner.ShouldJumpKidsRoom(true, false, -0.1f, fall), Is.False);
        }

        /// <summary>落ちる所の線（0380）は、向こう側に居るときだけ押し始めと同じ条件で飛ぶ。</summary>
        [Test]
        public void ShouldJumpByLine_OnlyBeyondTheLine_WithTheSameConditionsAsAPress()
        {
            const float fall = TakeRunner.KidsRoomFallSec;
            Assert.That(TakeRunner.ShouldJumpKidsRoomByLine(true, true, false, 0.6f, fall), Is.True);
            Assert.That(TakeRunner.ShouldJumpKidsRoomByLine(false, true, false, 0.6f, fall), Is.False, "線の手前では飛ばない");
            Assert.That(TakeRunner.ShouldJumpKidsRoomByLine(true, true, true, 0.6f, fall), Is.False, "押して飛んだ後は線でもう飛ばない");
            Assert.That(TakeRunner.ShouldJumpKidsRoomByLine(true, true, false, 4.3f, fall), Is.False, "落ちる所以後に越えたら何もしない");
            Assert.That(TakeRunner.ShouldJumpKidsRoomByLine(true, true, false, float.NaN, fall), Is.False, "動画が出るまで待つ");
            Assert.That(TakeRunner.ShouldJumpKidsRoomByLine(true, false, false, 0.6f, fall), Is.False, "子供部屋のカットでなければ飛ばない");
        }

        /// <summary>台本に子供部屋のカットがあるときだけ落ちる所の線の枠を取る（位置の追跡も回る）。無ければ警告する。</summary>
        [Test]
        public void Runner_TakesTheFallLineSlot_OnlyWithAKidsRoomCut_AndWarnsWhenTheLineIsMissing()
        {
            Rig rig = MakeRig();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex($"layout.lines に無いライン.*{TakeRunner.KidsRoomFallLineId}"));
            rig.Timeline.SetTimeline(new[] { Segment(KidsTake()) });
            Assert.That(rig.Runner.KidsRoomFallLineSlot, Is.GreaterThanOrEqualTo(0));
            Assert.That((bool)GetField(rig.Runner, "_hasLineTakes")!, Is.True, "線の側を見るために位置の追跡を回す");

            rig.Timeline.SetTimeline(new[] { Segment(EnterTake("other", LiveStep(2f))) });
            Assert.That(rig.Runner.KidsRoomFallLineSlot, Is.EqualTo(-1));
        }

        [Test]
        public void SkipSec_IsWhatTheJumpCutsOff()
        {
            Assert.That(TakeRunner.KidsRoomJumpSkipSec(1.3f, 4.2f), Is.EqualTo(2.9f).Within(1e-5f));
            Assert.That(TakeRunner.KidsRoomJumpSkipSec(0f, 4.2f), Is.EqualTo(4.2f).Within(1e-5f));
            Assert.That(TakeRunner.KidsRoomJumpSkipSec(5f, 4.2f), Is.Zero);
            Assert.That(TakeRunner.KidsRoomJumpSkipSec(float.NaN, 4.2f), Is.Zero);
        }

        /// <summary>
        /// 報告を保つのは動画のカットと、その次の最後のコマ（ライブではないカット）のあいだだけ。
        /// 動画の前のカット（人形が後ろを付いて来る所）とライブへ戻ったカットでは従来どおり即畳む。
        /// </summary>
        [Test]
        public void DismissAfterStep_HoldsThroughTheLastFrame()
        {
            ShowStepDef[] steps =
            {
                Step(TakeSchema.SourceInherit, ""),
                Step("plate", TakeRunner.KidsRoomCueId),
                Step("plate", "backrooms_B"),
                Step(TakeSchema.SourceLive, ""),
            };
            Assert.That(TakeRunner.FindKidsRoomStep(steps), Is.EqualTo(1));
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(steps, 0), Is.EqualTo(-1), "動画の前は即畳む（従来どおり）");
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(steps, 1), Is.EqualTo(2), "動画のカット → 最後のコマまで");
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(steps, 2), Is.EqualTo(2), "最後のコマ → その終わりまで");
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(steps, 3), Is.EqualTo(-1), "ライブへ戻った後は即畳む");

            ShowStepDef[] noLastFrame = { Step("plate", TakeRunner.KidsRoomCueId), Step(TakeSchema.SourceLive, "") };
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(noLastFrame, 0), Is.EqualTo(0), "最後のコマが無ければ動画の終わりまで");

            ShowStepDef[] other = { Step("plate", "backrooms_B"), Step("plate", "pov_x") };
            Assert.That(TakeRunner.FindKidsRoomStep(other), Is.EqualTo(-1));
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(other, 0), Is.EqualTo(-1), "子供部屋の演出でなければ保たない");
            Assert.That(TakeRunner.ResolveKidsRoomDismissAfterStep(null, 0), Is.EqualTo(-1));
        }

        // ------------------------------------------------------------------ 純ロジック（尺と畳む縁）

        private static TakeRunnerLogic.Def KidsDef(bool yieldOnZoneChange = true) => new()
        {
            lap = 2, camera = 1, onExit = false, offsetSec = 0f,
            skipWhenMissed = true, once = true, maxDurationSec = 0f, dismissible = true,
            yieldOnZoneChange = yieldOnZoneChange,
            stepDurSec = new[] { 2f, 5.9f, 1.625f, TakeRunnerLogic.WaitZoneChange },
        };

        private static TakeRunnerLogic StartKids(out float now)
        {
            var l = new TakeRunnerLogic();
            l.SetDefs(new[] { KidsDef() });
            l.OnZoneCommitted(2, 1, false, 0, 0, 0f);
            Assert.That(l.Tick(0f, 1).stepIndex, Is.EqualTo(0));
            TakeRunnerLogic.Decision d = l.Tick(2f, 1);
            Assert.That(d.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(d.stepIndex, Is.EqualTo(1), "子供部屋の動画のカット");
            now = 2f;
            return l;
        }

        [Test]
        public void ReportDuringTheVideo_IsCounted_ButFoldsOnlyAfterTheLastFrame()
        {
            TakeRunnerLogic l = StartKids(out _);
            Assert.That(l.NotifyMarkPressed(3f, dismissAfterStep: 2), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed),
                        "報告はこの時点で成立する");
            Assert.That(l.DismissAfterStep, Is.EqualTo(2));
            Assert.That(l.Tick(3.1f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "動画の途中で畳まない");

            TakeRunnerLogic.Decision last = l.Tick(7.9f, 1);
            Assert.That(last.action, Is.EqualTo(TakeRunnerLogic.Action.BeginStep));
            Assert.That(last.stepIndex, Is.EqualTo(2), "人形がこちらを向く最後のコマへ進む");

            Assert.That(l.Tick(9.0f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None), "最後のコマも見せ切る");
            TakeRunnerLogic.Decision end = l.Tick(9.525f, 1);
            Assert.That(end.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake));
            Assert.That(end.reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed), "ライブへは報告として戻る（乱れ）");
            Assert.That(end.dismissed, Is.True);
            Assert.That(l.DismissCount, Is.EqualTo(1));
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled));
        }

        [Test]
        public void ReportDuringTheLastFrame_FoldsAtItsEnd()
        {
            TakeRunnerLogic l = StartKids(out _);
            Assert.That(l.Tick(7.9f, 1).stepIndex, Is.EqualTo(2));
            Assert.That(l.NotifyMarkPressed(8.5f, dismissAfterStep: 2), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(l.Tick(8.6f, 1).action, Is.EqualTo(TakeRunnerLogic.Action.None));
            TakeRunnerLogic.Decision end = l.Tick(9.525f, 1);
            Assert.That(end.reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        [Test]
        public void HeldReport_StillFoldsAtOnceWhenTheVisitorWalksOut()
        {
            TakeRunnerLogic l = StartKids(out _);
            l.NotifyMarkPressed(3f, dismissAfterStep: 2);
            TakeRunnerLogic.Decision cut = l.OnZoneCommitted(2, 2, true, 2, 1, 3.5f, forward: true);
            Assert.That(cut.action, Is.EqualTo(TakeRunnerLogic.Action.EndTake), "区間を出たら即畳む（従来どおり）");
            Assert.That(cut.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Yielded));
            Assert.That(l.OutcomeOf(0), Is.EqualTo(TakeRunnerLogic.Outcome.Settled), "報告済みなので再演しない");
            Assert.That(l.DismissAfterStep, Is.EqualTo(-1), "次の演出へ持ち越さない");
        }

        [Test]
        public void HeldReport_StillFoldsAtOnceOnWatchdogAndAbort()
        {
            var l = new TakeRunnerLogic();
            TakeRunnerLogic.Def def = KidsDef();
            def.maxDurationSec = 4f;
            l.SetDefs(new[] { def });
            l.OnZoneCommitted(2, 1, false, 0, 0, 0f);
            l.Tick(0f, 1);
            l.Tick(2f, 1);
            l.NotifyMarkPressed(3f, dismissAfterStep: 2);
            TakeRunnerLogic.Decision wd = l.Tick(4.1f, 1);
            Assert.That(wd.reason, Is.EqualTo(TakeRunnerLogic.EndReason.Watchdog), "watchdog は従来どおり即畳む");

            TakeRunnerLogic a = StartKids(out _);
            a.NotifyMarkPressed(3f, dismissAfterStep: 2);
            a.AbortActive();
            Assert.That(a.IsActive, Is.False, "卓の中止は即畳む");
            Assert.That(a.DismissAfterStep, Is.EqualTo(-1));
        }

        [Test]
        public void WithoutHold_ReportFoldsAtOnce_AsBefore()
        {
            TakeRunnerLogic l = StartKids(out _);
            Assert.That(l.NotifyMarkPressed(3f), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            TakeRunnerLogic.Decision end = l.Tick(3.01f, 1);
            Assert.That(end.reason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        /// <summary>飛んだ分だけ縮め、seek を待った分だけ延ばす。終わりは今より前へは行かない。待ちのカットは触らない。</summary>
        [Test]
        public void ShortenCurrentStep_MovesOnlyFiniteEnds()
        {
            TakeRunnerLogic l = StartKids(out _);
            Assert.That(l.CurrentStepEnd, Is.EqualTo(7.9f).Within(1e-5f));
            l.ShortenCurrentStep(3.3f, TakeRunner.KidsRoomJumpSkipSec(1.3f, TakeRunner.KidsRoomFallSec));
            Assert.That(l.CurrentStepEnd, Is.EqualTo(5.0f).Within(1e-5f), "落ちる所から動画の終わりまで");
            l.ShortenCurrentStep(3.4f, -0.08f);
            Assert.That(l.CurrentStepEnd, Is.EqualTo(5.08f).Within(1e-5f), "seek を待った分を延ばす");
            l.ShortenCurrentStep(3.5f, 100f);
            Assert.That(l.CurrentStepEnd, Is.EqualTo(3.5f).Within(1e-5f), "今より前へは行かない");

            Assert.That(l.Tick(3.5f, 1).stepIndex, Is.EqualTo(2));
            Assert.That(l.Tick(5.125f, 1).stepIndex, Is.EqualTo(3), "最後のコマの尺は変えない");
            l.ShortenCurrentStep(5.2f, 1f);
            Assert.That(float.IsPositiveInfinity(l.CurrentStepEnd), Is.True, "区間の切り替わり待ちは触らない");
        }

        // ------------------------------------------------------------------ TakeRunner の配線

        /// <summary>動画のカットで報告しても畳まず、最後のコマが終わってから報告として戻る。報告は数える。</summary>
        [Test]
        public void Runner_ReportDuringTheVideo_HoldsUntilTheLastFrameEnds()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[] { Segment(KidsTake()) });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            Assert.That(rig.Runner.ActiveStepCueId, Is.EqualTo(TakeRunner.KidsRoomCueId));

            _now = 2f;
            Assert.That(rig.Runner.NotifyVisitorMark(), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(rig.Runner.KidsRoomHeldReportCount, Is.EqualTo(1));
            Assert.That(rig.Runner.LastKidsRoomHeldStep, Is.EqualTo(1));
            Assert.That(rig.Runner.LastKidsRoomHeldUntilStep, Is.EqualTo(2));
            Assert.That(rig.Runner.LastKidsRoomHeldTakeId, Is.EqualTo("kids"));
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.True, "報告しても動画の途中で畳まない");
            Assert.That(rig.Runner.KidsRoomStopCount, Is.Zero, "音も退かせない");

            _now = 6.9f;
            Frame(rig);
            Assert.That(rig.Runner.ActiveStepCueId, Is.EqualTo("backrooms_B"), "最後のコマへ進む");
            Assert.That(rig.Runner.IsActive, Is.True);

            _now = 8.525f;
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False);
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed),
                        "最後のコマが終わってから報告として畳む");
        }

        /// <summary>動画の前のカット（人形が後ろを付いて来る所）で報告したら、従来どおり即畳む。</summary>
        [Test]
        public void Runner_ReportBeforeTheVideo_FoldsAtOnce()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[] { Segment(KidsTake()) });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 0.5f;
            Assert.That(rig.Runner.NotifyVisitorMark(), Is.EqualTo(TakeRunnerLogic.MarkResult.Dismissed));
            Assert.That(rig.Runner.KidsRoomHeldReportCount, Is.Zero);
            Frame(rig);
            Assert.That(rig.Runner.IsActive, Is.False);
            Assert.That(rig.Timeline.LastEndReason, Is.EqualTo(TakeRunnerLogic.EndReason.VisitorDismissed));
        }

        /// <summary>画面（オーバーレイ）が無い構成では押し始めても飛ばない（飛ぶ画が無い）。数え直しはラン開始で。</summary>
        [Test]
        public void Runner_WithoutOverlay_DoesNotJump_AndResetRunClearsCounts()
        {
            Rig rig = MakeRig();
            rig.Timeline.SetTimeline(new[] { Segment(KidsTake()) });
            EnterZone(rig, 0);
            Frame(rig);
            _now = 1f;
            Frame(rig);
            _now = 1.5f;
            rig.Runner.NotifyVisitorMarkStarted();
            Assert.That(rig.Runner.KidsRoomJumpCount, Is.Zero);
            rig.Runner.NotifyVisitorMark();
            Assert.That(rig.Runner.KidsRoomHeldReportCount, Is.EqualTo(1));

            rig.Timeline.ResetRun();
            Assert.That(rig.Runner.KidsRoomHeldReportCount, Is.Zero);
            Assert.That(rig.Runner.KidsRoomJumpCount, Is.Zero);
            Assert.That(rig.Runner.KidsRoomLandCount, Is.Zero);
            Assert.That(rig.Runner.LastKidsRoomHeldStep, Is.EqualTo(-1));
        }

        // ------------------------------------------------------------------ 音を同じ秒から鳴らし直す口

        /// <summary>
        /// 途中から鳴らし直しても「鳴った回数」（<c>ev=sfx</c> の引き金）は増えない。立ち上げ中の声を退かせたら、
        /// いまの音量から退かせる。音源の外の秒は鳴らさない。
        /// </summary>
        [Test]
        public void SfxPlayer_PlayFrom_DoesNotCountAsANewHit()
        {
            var go = new GameObject("kidsroom-playfrom-test");
            _spawned.Add(go);
            var sfx = go.AddComponent<SfxPlayer>();
            Invoke(sfx, "Awake");
            var clip = AudioClip.Create("kids", 48000 * 6, 2, 48000, false);
            _spawned.Add(clip);

            Assert.IsTrue(sfx.Play(clip, pitchSpread: 0f, gainSpreadDb: 0f));
            int played = sfx.PlayedCount;
            Assert.That(sfx.FadeOut(clip, TakeRunner.KidsRoomJumpFadeSec), Is.EqualTo(1));
            Assert.IsTrue(sfx.PlayFrom(clip, TakeRunner.KidsRoomFallSec, TakeRunner.KidsRoomJumpFadeSec));
            Assert.That(sfx.PlayedCount, Is.EqualTo(played), "鳴らし直しは数えない");
            Assert.That(sfx.FadingVoices, Is.EqualTo(1), "前の声は退かせる");
            Assert.That(sfx.RisingVoices, Is.EqualTo(1), "新しい声は立ち上げる");
            Assert.That(sfx.BusyVoices, Is.EqualTo(2), "0.4 秒は重なる");

            Assert.IsFalse(sfx.PlayFrom(clip, 6.5f, 0.4f), "音源の外");
            Assert.IsFalse(sfx.PlayFrom(clip, -1f, 0.4f));
            Assert.IsFalse(sfx.PlayFrom(clip, float.NaN, 0.4f));
            Assert.IsFalse(sfx.PlayFrom(null, 1f, 0.4f));

            Assert.That(sfx.FadeOut(clip, 0.2f), Is.EqualTo(2));
            Assert.That(sfx.RisingVoices, Is.Zero, "立ち上げ中の声も退かせへ移る");
            Assert.That(sfx.FadingVoices, Is.EqualTo(2));
            sfx.StopAll();
            Assert.That(sfx.RisingVoices + sfx.FadingVoices, Is.Zero);
        }

        /// <summary>ShowSoundDirector の口は <c>LastCue</c> と一撃の累計を動かさない（<c>ev=sfx</c> が 2 行に見えない）。</summary>
        [Test]
        public void RestartSpotAt_DoesNotLookLikeASecondHit()
        {
            var go = new GameObject("kidsroom-restart-test");
            _spawned.Add(go);
            var sfx = go.AddComponent<SfxPlayer>();
            Invoke(sfx, "Awake");
            var dir = go.AddComponent<ShowSoundDirector>();
            Invoke(dir, "Awake");
            if (Resources.Load<AudioClip>(Res) == null)
                Assert.Ignore("音源が取り込まれていない（KidsRoomAudioTests.Clip_IsInResources を先に見る）");

            Assert.IsTrue(dir.PlaySpot(SoundCue.KidsRoom));
            var other = AudioClip.Create("other", 48000, 1, 48000, false);
            _spawned.Add(other);
            sfx.Play(other, pitchSpread: 0f, gainSpreadDb: 0f);
            int spots = dir.SpotCount;
            Assert.IsTrue(dir.RestartSpotAt(SoundCue.KidsRoom, TakeRunner.KidsRoomFallSec, TakeRunner.KidsRoomJumpFadeSec));
            Assert.That(dir.SpotCount, Is.EqualTo(spots), "一撃の累計は動かない");
            Assert.That(sfx.FadingVoices, Is.EqualTo(1), "子供部屋の声だけを退かせる");
            Assert.That(sfx.RisingVoices, Is.EqualTo(1));
            Assert.That(sfx.BusyVoices, Is.EqualTo(3), "他の音は残る");
        }

        // ------------------------------------------------------------------ rig（KidsRoomAudioTests と同じ組み方）

        private sealed class Rig
        {
            public CameraSwitchDirector Director = null!;
            public TakeRunner Runner = null!;
            public TimelineDirector Timeline = null!;
            public CueScheduler Scheduler = null!;
        }

        private static ShowStepDef Step(string source, string cueId) => new() { source = source, cueId = cueId };

        // 本番の 2-B（後ろを付いて来る 1 秒 → 子供部屋 5.9 秒 → 最後のコマ 1.625 秒）。最後のコマは素材の要らない inherit で代える。
        private static ShowTakeDef KidsTake()
        {
            ShowTakeDef take = EnterTake("kids",
                LiveStep(1f),
                LiveStep(5.9f, cueId: TakeRunner.KidsRoomCueId),
                new ShowStepDef
                {
                    source = TakeSchema.SourceInherit,
                    camera = 1,
                    cueId = "backrooms_B",
                    durKind = TakeSchema.DurSec,
                    durSec = 1.625f,
                    transition = TakeSchema.TransCut,
                },
                LiveStep(5f));
            take.dismissible = true;
            return take;
        }

        private Rig MakeRig()
        {
            var registryGo = new GameObject("Registry");
            _spawned.Add(registryGo);
            var registry = registryGo.AddComponent<CameraStreamRegistry>();
            FieldInfo streams = typeof(CameraStreamRegistry).GetField("_streams", BF)!;
            streams.SetValue(registry, Array.CreateInstance(streams.FieldType.GetElementType()!, 4));
            SetField(registry, "_activeIndex", 0);

            var host = new GameObject("Screen");
            _spawned.Add(host);
            var director = host.AddComponent<CameraSwitchDirector>();
            SetField(director, "registry", registry);
            SetField(director, "dipDownSec", 0f);
            SetField(director, "dipUpSec", 0f);
            SetField(director, "switchCooldownSec", 0f);
            SetField(director, "minDwellSec", 0f);
            director.SetDeltaSource(() => 1f);
            Invoke(director, "Awake");
            Invoke(director, "OnEnable");

            var soundGo = new GameObject("[Sound]");
            _spawned.Add(soundGo);
            var sfx = soundGo.AddComponent<SfxPlayer>();
            Invoke(sfx, "Awake");
            var sound = soundGo.AddComponent<ShowSoundDirector>();
            Invoke(sound, "Awake");

            var scheduler = host.AddComponent<CueScheduler>();
            var runner = host.AddComponent<TakeRunner>();
            var timeline = host.AddComponent<TimelineDirector>();
            SetField(runner, "director", director);
            SetField(runner, "overlay", null);
            SetField(runner, "showControl", null);
            SetField(runner, "_sound", sound);
            runner.SetTimeSource(() => _now);
            runner.SetCueResolver(id => new OverlayCueData { id = id });
            SetField(timeline, "cueScheduler", scheduler);
            SetField(timeline, "takeRunner", runner);
            SetField(timeline, "showControl", null);
            Invoke(scheduler, "Awake");
            Invoke(timeline, "Awake");
            Invoke(timeline, "OnEnable");

            return new Rig { Director = director, Runner = runner, Timeline = timeline, Scheduler = scheduler };
        }

        private static ShowStepDef LiveStep(float durSec, string cueId = "", int camera = 1) => new()
        {
            source = TakeSchema.SourceLive,
            camera = camera,
            cueId = cueId,
            durKind = TakeSchema.DurSec,
            durSec = durSec,
            transition = TakeSchema.TransCut,
        };

        private static ShowTakeDef EnterTake(string id, params ShowStepDef[] steps) => new()
        {
            id = id,
            at = TakeSchema.AtEnter,
            offsetSec = 0f,
            ifMissed = TakeSchema.MissedFireOnExit,
            policy = TakeSchema.PolicyHold,
            once = true,
            maxDurationSec = 0f,
            steps = steps,
        };

        private static ShowTimelineSegmentDef Segment(ShowTakeDef take) => new()
        {
            lap = 1,
            camera = 0,
            takes = new[] { take },
            cues = Array.Empty<ShowSegmentCueDef>(),
        };

        private static void EnterZone(Rig rig, int camera)
            => rig.Scheduler.NotifyCameraEntered(new SegmentEntry(camera, segmentLap: 1, progressLap: 1, forward: false));

        private static void Frame(Rig rig)
        {
            Invoke(rig.Runner, "Update");
            for (int i = 0; i < 4; i++) Invoke(rig.Director, "Update");
        }

        private static void SetField(object target, string name, object? value)
        {
            FieldInfo? field = target.GetType().GetField(name, BF);
            Assert.That(field, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            field!.SetValue(target, value);
        }

        private static object? GetField(object target, string name)
        {
            FieldInfo? field = target.GetType().GetField(name, BF);
            Assert.That(field, Is.Not.Null, $"field '{name}' not found on {target.GetType().Name}");
            return field!.GetValue(target);
        }

        private static void Invoke(object target, string method)
        {
            MethodInfo? member = target.GetType().GetMethod(method, BF);
            Assert.That(member, Is.Not.Null, $"method '{method}' not found on {target.GetType().Name}");
            member!.Invoke(target, null);
        }
    }
}
