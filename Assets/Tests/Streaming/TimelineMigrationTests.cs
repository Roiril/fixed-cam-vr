#nullable enable
using System;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// v2（cues[] / insert）→ v3（takes[]）の決定的変換の検証（設計 §6.5）。
    /// Web 側 <c>timeline-model.js</c> の <c>migrateV2Segment</c> と同じ結果を返すことが契約
    /// （JS 側のミラーは段 A で追加し、共有 fixture で突き合わせる）。
    /// </summary>
    public sealed class TimelineMigrationTests
    {
        private static ShowTimelineSegmentDef Seg(int lap, int camera) => new()
        {
            lap = lap,
            camera = camera,
            cues = Array.Empty<ShowSegmentCueDef>(),
            takes = Array.Empty<ShowTakeDef>(),
        };

        [Test]
        public void V2Cue_BecomesInheritStep_UntilClipEnd()
        {
            ShowTimelineSegmentDef seg = Seg(1, 0);
            seg.cues = new[] { new ShowSegmentCueDef { cueId = "cue_A_1", delaySec = 2f, once = true } };

            ShowTakeDef[] takes = TimelineMigration.FromV2(seg);
            Assert.That(takes.Length, Is.EqualTo(1));
            ShowTakeDef t = takes[0];
            Assert.That(t.at, Is.EqualTo(TakeSchema.AtEnter));
            Assert.That(t.offsetSec, Is.EqualTo(2f));
            Assert.That(t.ifMissed, Is.EqualTo(TakeSchema.MissedFireOnExit));
            Assert.That(t.once, Is.True);
            Assert.That(t.steps.Length, Is.EqualTo(1));

            ShowStepDef s = t.steps[0];
            Assert.That(s.source, Is.EqualTo(TakeSchema.SourceInherit), "cue は今映っているものを保つ");
            Assert.That(s.cueId, Is.EqualTo("cue_A_1"));
            Assert.That(s.IsUntilClipEnd, Is.True, "素材が終わったら live へ戻る");
            Assert.That(s.strength, Is.EqualTo(-1f), "override 無しは素材定義から継承（-1）");
            Assert.That(s.fadeInSec, Is.EqualTo(-1f));
        }

        [Test]
        public void V2CueOverride_BecomesExplicitStepValues()
        {
            ShowTimelineSegmentDef seg = Seg(2, 1);
            seg.cues = new[]
            {
                new ShowSegmentCueDef
                {
                    cueId = "cue_B", delaySec = 0f, once = false, hasOverride = true,
                    @override = new ShowCueOverrideDef
                    {
                        strength = 0.5f, fadeIn = 1.2f, fadeOut = 0.8f, trimStart = 1f, trimEnd = 6f,
                    },
                },
            };

            ShowStepDef s = TimelineMigration.FromV2(seg)[0].steps[0];
            Assert.That(s.strength, Is.EqualTo(0.5f));
            Assert.That(s.fadeInSec, Is.EqualTo(1.2f));
            Assert.That(s.fadeOutSec, Is.EqualTo(0.8f));
            Assert.That(s.trimStartSec, Is.EqualTo(1f));
            Assert.That(s.trimEndSec, Is.EqualTo(6f));
            Assert.That(TimelineMigration.FromV2(seg)[0].once, Is.False);
        }

        [Test]
        public void V2EnterInsert_BecomesLiveStep()
        {
            ShowTimelineSegmentDef seg = Seg(2, 2);
            seg.hasInsert = true;
            seg.insert = new ShowInsertDef
            {
                anchor = "enter", camera = 0, delaySec = 1.5f, durationSec = 4f, cueId = "cue_C_scare", once = true,
            };

            ShowTakeDef t = TimelineMigration.FromV2(seg)[0];
            Assert.That(t.at, Is.EqualTo(TakeSchema.AtEnter));
            Assert.That(t.offsetSec, Is.EqualTo(1.5f));
            ShowStepDef s = t.steps[0];
            Assert.That(s.source, Is.EqualTo(TakeSchema.SourceLive));
            Assert.That(s.camera, Is.EqualTo(0));
            Assert.That(s.cueId, Is.EqualTo("cue_C_scare"));
            Assert.That(s.durKind, Is.EqualTo(TakeSchema.DurSec));
            Assert.That(s.durSec, Is.EqualTo(4f));
        }

        [Test]
        public void V2ExitInsert_BecomesExitTake_WithZeroOffset()
        {
            ShowTimelineSegmentDef seg = Seg(1, 1);
            seg.hasInsert = true;
            seg.insert = new ShowInsertDef
            {
                anchor = "exit", camera = 2, delaySec = 9f, durationSec = 3f, once = true,
            };

            ShowTakeDef t = TimelineMigration.FromV2(seg)[0];
            Assert.That(t.IsExit, Is.True);
            Assert.That(t.offsetSec, Is.EqualTo(0f), "exit では delaySec を持ち込まない");
            Assert.That(t.steps[0].camera, Is.EqualTo(2));
        }

        [Test]
        public void V2InsertPost_CarriedWithPresentFlag()
        {
            ShowTimelineSegmentDef seg = Seg(1, 1);
            seg.hasInsert = true;
            seg.insert = new ShowInsertDef
            {
                anchor = "enter", camera = 2, durationSec = 3f,
                post = new PostParams { saturation = 1.5f }, hasPost = true,
            };

            ShowStepDef s = TimelineMigration.FromV2(seg)[0].steps[0];
            Assert.That(s.hasPost, Is.True);
            Assert.That(s.post, Is.Not.Null);
            Assert.That(s.post!.saturation, Is.EqualTo(1.5f));
        }

        [Test]
        public void V2InsertPost_FlagFalse_DropsPost()
        {
            ShowTimelineSegmentDef seg = Seg(1, 1);
            seg.hasInsert = true;
            seg.insert = new ShowInsertDef { anchor = "enter", camera = 2, durationSec = 3f, hasPost = false };

            ShowStepDef s = TimelineMigration.FromV2(seg)[0].steps[0];
            Assert.That(s.hasPost, Is.False);
            Assert.That(s.post, Is.Null);
        }

        [Test]
        public void Order_CuesThenInsert()
        {
            ShowTimelineSegmentDef seg = Seg(1, 0);
            seg.cues = new[]
            {
                new ShowSegmentCueDef { cueId = "c1" },
                new ShowSegmentCueDef { cueId = "c2" },
            };
            seg.hasInsert = true;
            seg.insert = new ShowInsertDef { anchor = "exit", camera = 1, durationSec = 2f };

            ShowTakeDef[] takes = TimelineMigration.FromV2(seg);
            Assert.That(takes.Length, Is.EqualTo(3));
            Assert.That(takes[0].steps[0].cueId, Is.EqualTo("c1"));
            Assert.That(takes[1].steps[0].cueId, Is.EqualTo("c2"));
            Assert.That(takes[2].IsExit, Is.True);
        }

        [Test]
        public void EmptyCueId_Skipped()
        {
            ShowTimelineSegmentDef seg = Seg(1, 0);
            seg.cues = new[] { new ShowSegmentCueDef { cueId = "" } };
            Assert.That(TimelineMigration.FromV2(seg).Length, Is.EqualTo(0));
        }

        [Test]
        public void InsertFlagFalse_NotConverted()
        {
            ShowTimelineSegmentDef seg = Seg(1, 0);
            seg.hasInsert = false;
            seg.insert = new ShowInsertDef { anchor = "enter", camera = 2, durationSec = 3f };
            Assert.That(TimelineMigration.FromV2(seg).Length, Is.EqualTo(0),
                "present-flag が偽なら幽霊インサートは作らない");
        }

        [Test]
        public void Ids_AreDeterministicAndUniqueWithinSegment()
        {
            ShowTimelineSegmentDef seg = Seg(3, 1);
            seg.cues = new[] { new ShowSegmentCueDef { cueId = "a" }, new ShowSegmentCueDef { cueId = "b" } };
            ShowTakeDef[] takes = TimelineMigration.FromV2(seg);
            Assert.That(takes[0].id, Is.EqualTo("L3C1#0"));
            Assert.That(takes[1].id, Is.EqualTo("L3C1#1"));
        }

        // ---- EnsureTakes（タイムライン全体の正規化） ----

        [Test]
        public void EnsureTakes_FillsV2Segments_AndLeavesV3Untouched()
        {
            var v3Seg = Seg(1, 1);
            v3Seg.takes = new[] { new ShowTakeDef { id = "keep", steps = new[] { new ShowStepDef() } } };
            var v2Seg = Seg(1, 0);
            v2Seg.cues = new[] { new ShowSegmentCueDef { cueId = "c" } };

            var tl = new ShowTimelineDef { rev = 1, segments = new[] { v3Seg, v2Seg } };
            TimelineMigration.EnsureTakes(tl);

            Assert.That(tl.segments[0].takes[0].id, Is.EqualTo("keep"), "既に takes を持つ区間は触らない");
            Assert.That(tl.segments[1].takes.Length, Is.EqualTo(1), "v2 区間は変換して埋める");
        }

        [Test]
        public void EnsureTakes_NullSafe()
        {
            Assert.DoesNotThrow(() => TimelineMigration.EnsureTakes(null));
            Assert.DoesNotThrow(() => TimelineMigration.EnsureTakes(new ShowTimelineDef()));
        }

        // ---- v3 判定 ----

        [Test]
        public void IsV3_BySchemaOrByTakes()
        {
            Assert.That(new ShowTimelineDef { rev = 1, schema = 3 }.IsV3(), Is.True);
            var withTakes = new ShowTimelineDef
            {
                rev = 1,
                segments = new[] { new ShowTimelineSegmentDef { takes = new[] { new ShowTakeDef() } } },
            };
            Assert.That(withTakes.IsV3(), Is.True, "schema 未指定でも takes があれば v3");
            var v2 = new ShowTimelineDef
            {
                rev = 1,
                segments = new[] { new ShowTimelineSegmentDef { cues = new[] { new ShowSegmentCueDef() } } },
            };
            Assert.That(v2.IsV3(), Is.False);
        }

        // ---- 判別子の正規化 ----

        [Test]
        public void UnknownDiscriminators_FallBackToDefaults()
        {
            Assert.That(TakeSchema.NormalizeSource("bogus", out bool known), Is.EqualTo(TakeSchema.SourceLive));
            Assert.That(known, Is.False);
            Assert.That(TakeSchema.NormalizeSource(TakeSchema.SourceClip, out known), Is.EqualTo(TakeSchema.SourceClip));
            Assert.That(known, Is.True);
            Assert.That(TakeSchema.IsExit("bogus"), Is.False);
            Assert.That(TakeSchema.SkipWhenMissed("bogus"), Is.False, "未知は fireOnExit 側（安全＝出す）");
            Assert.That(TakeSchema.IsYield("bogus"), Is.False, "未知は hold 側");
        }

        [Test]
        public void ResolveDefaults_ZeroMeansCodeDefault()
        {
            Assert.That(TakeSchema.ResolveMaxDuration(0f), Is.EqualTo(TakeSchema.DefaultMaxDurationSec));
            Assert.That(TakeSchema.ResolveMaxDuration(-3f), Is.EqualTo(TakeSchema.DefaultMaxDurationSec));
            Assert.That(TakeSchema.ResolveMaxDuration(12f), Is.EqualTo(12f));

            Assert.That(TakeSchema.ResolveTransitionMs(TakeSchema.TransCut, 500f), Is.EqualTo(0f), "cut は常に 0");
            Assert.That(TakeSchema.ResolveTransitionMs(TakeSchema.TransDip, 0f), Is.EqualTo(TakeSchema.DefaultDipMs));
            Assert.That(TakeSchema.ResolveTransitionMs(TakeSchema.TransFade, 0f), Is.EqualTo(TakeSchema.DefaultFadeMs));
            Assert.That(TakeSchema.ResolveTransitionMs(TakeSchema.TransDip, 120f), Is.EqualTo(120f));
        }

        [Test]
        public void Inherit_MinusOneTakesCueValue()
        {
            Assert.That(TakeSchema.Inherit(-1f, 0.7f), Is.EqualTo(0.7f));
            Assert.That(TakeSchema.Inherit(0.2f, 0.7f), Is.EqualTo(0.2f));
            Assert.That(TakeSchema.Inherit(0f, 0.7f), Is.EqualTo(0f), "0 は明示指定（継承ではない）");
        }
    }
}
