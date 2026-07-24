#nullable enable
using System;
using System.IO;
using System.Linq;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// Web⇄Unity present-flag 契約テスト。共有 fixture（Web の serializeTimeline 出力 =
    /// Assets/Tests/Fixtures/show_timeline_canonical.json）を JsonUtility でパースし、
    /// TimelinePresentFlags.Reconcile 後の has* が JSON の honest bool に一致することを固定する。
    ///
    /// fixture は has*=false のとき入れ子キー（override/post/insert/insert.post）を省く（新 wire 形式）。
    /// 注意: JsonUtility.FromJson は省略キーの入れ子 [Serializable] クラスを null でなく既定インスタンスで
    /// 生成しうる（null 非対応の Unity シリアライズ規則）。契約は「Reconcile 後の has* が honest bool と一致し、
    /// flag=false の入れ子を consumer が読まない」ことであり、null 性は実装詳細なので assert しない。
    /// fixture の再生成は node 側 `UPDATE_FIXTURE=1 node --test tools/web-compositor/timeline-model.test.mjs`。
    /// （Unity 側の旧バグ = object 常時 present での全 true 化は TimelinePresentFlagsTests が別途カバーする。）
    /// </summary>
    public sealed class TimelineFixtureContractTests
    {
        // fixture の timeline セクションだけを受ける薄いラッパ（他トップレベルキーは JsonUtility が無視）。
        [Serializable] private sealed class Wrapper { public ShowTimelineDef? timeline; }

        private static ShowTimelineDef LoadTimeline()
        {
            var path = Path.Combine(Application.dataPath, "Tests/Fixtures/show_timeline_canonical.json");
            Assert.IsTrue(File.Exists(path), $"fixture が見つからない: {path}");
            var json = File.ReadAllText(path);
            var w = JsonUtility.FromJson<Wrapper>(json);
            Assert.IsNotNull(w?.timeline, "timeline セクションのパースに失敗");
            return w!.timeline!;
        }

        private static ShowTimelineSegmentDef Seg(ShowTimelineDef t, int lap, int camera)
        {
            var s = t.segments.FirstOrDefault(x => x != null && x.lap == lap && x.camera == camera);
            Assert.IsNotNull(s, $"区間 (lap={lap}, camera={camera}) が fixture に無い");
            return s!;
        }

        [Test]
        public void Reconcile_PreservesHonestBools_AcrossAllLevels()
        {
            var t = LoadTimeline();

            // Reconcile 前の honest bool（JSON が書いた値）を捕捉する。
            var pre = t.segments.Select(s => new
            {
                s.lap, s.camera,
                hasPost = s.hasPost,
                hasInsert = s.hasInsert,
                hasBgm = s.hasBgm,
                insertHasPost = s.insert != null && s.insert.hasPost,
                cueFlags = (s.cues ?? Array.Empty<ShowSegmentCueDef>()).Select(c => c != null && c.hasOverride).ToArray(),
            }).ToArray();

            TimelinePresentFlags.Reconcile(t);

            // Web が honest bool と入れ子キー有無を一致させているため、Reconcile は honest bool を保つ。
            foreach (var p in pre)
            {
                var s = Seg(t, p.lap, p.camera);
                Assert.AreEqual(p.hasPost, s.hasPost, $"seg({p.lap},{p.camera}).hasPost");
                Assert.AreEqual(p.hasInsert, s.hasInsert, $"seg({p.lap},{p.camera}).hasInsert");
                Assert.AreEqual(p.hasBgm, s.hasBgm, $"seg({p.lap},{p.camera}).hasBgm");
                if (s.insert != null)
                    Assert.AreEqual(p.insertHasPost, s.insert.hasPost, $"seg({p.lap},{p.camera}).insert.hasPost");
                var cues = s.cues ?? Array.Empty<ShowSegmentCueDef>();
                Assert.AreEqual(p.cueFlags.Length, cues.Length, $"seg({p.lap},{p.camera}).cues.Length");
                for (int i = 0; i < cues.Length; i++)
                    Assert.AreEqual(p.cueFlags[i], cues[i].hasOverride, $"seg({p.lap},{p.camera}).cues[{i}].hasOverride");
            }
        }

        [Test]
        public void Reconcile_FalseFlags_StayFalse_AndTrueFlagsHaveObjects()
        {
            var t = LoadTimeline();
            TimelinePresentFlags.Reconcile(t);

            // seg A (1,0): cue0 override あり / cue1 override なし・post/insert なし。
            // flag=false 側の入れ子は JsonUtility が既定インスタンスで埋めることがあるため null 性は見ない
            // （consumer は has* で gate するので flag=false が守られていれば十分）。
            var a = Seg(t, 1, 0);
            Assert.AreEqual(2, a.cues.Length, "seg A の cue は 2 本");
            Assert.IsTrue(a.cues[0].hasOverride, "seg A cue0 は override あり");
            Assert.IsNotNull(a.cues[0].@override, "override=true なら @override 非 null");
            Assert.IsFalse(a.cues[1].hasOverride, "seg A cue1 は override なし（キー省略でも false のまま）");
            Assert.IsFalse(a.hasPost, "seg A は post なし");
            Assert.IsFalse(a.hasInsert, "seg A は insert なし");

            // seg B (1,1): post あり / insert あり・insert.post なし。
            var b = Seg(t, 1, 1);
            Assert.IsTrue(b.hasPost, "seg B は post あり");
            Assert.IsNotNull(b.post, "post=true なら post 非 null");
            Assert.IsTrue(b.hasInsert, "seg B は insert あり");
            Assert.IsNotNull(b.insert, "insert=true なら insert 非 null");
            Assert.IsFalse(b.insert!.hasPost, "seg B insert.post は省略（false のまま）");

            // seg C (2,2): post なし / insert あり・insert.post あり。
            var c = Seg(t, 2, 2);
            Assert.IsFalse(c.hasPost, "seg C は post なし");
            Assert.IsTrue(c.hasInsert, "seg C は insert あり");
            Assert.IsTrue(c.insert!.hasPost, "seg C insert.post あり");
            Assert.IsNotNull(c.insert!.post, "insert.post=true なら非 null");

            // BGM も同じ present-flag 契約: A=指示なし / B=play（ループ範囲つき）/ C=stop。
            Assert.IsFalse(a.hasBgm, "seg A は BGM 指示なし");
            Assert.IsTrue(b.hasBgm, "seg B は BGM 指示あり");
            Assert.IsNotNull(b.bgm, "hasBgm=true なら bgm 非 null");
            Assert.AreEqual(BgmPlanLogic.ActionPlay, b.bgm!.action, "seg B は play");
            Assert.AreEqual("bgm_horror", b.bgm!.trackId);
            Assert.AreEqual(12f, b.bgm!.loopStartSec, 1e-4f);
            Assert.AreEqual(48f, b.bgm!.loopEndSec, 1e-4f);
            Assert.IsTrue(c.hasBgm, "seg C は BGM 指示あり");
            Assert.AreEqual(BgmPlanLogic.ActionStop, c.bgm!.action, "seg C は stop");

            // seg D (2,0) は空区間 → serialize で除去され fixture に存在しない。
            Assert.IsFalse(t.segments.Any(s => s != null && s.lap == 2 && s.camera == 0), "空区間は fixture に無い");
        }

        [Test]
        public void Reconcile_OverrideKeyMapsToAtOverrideField_AndValuesReadable()
        {
            var t = LoadTimeline();
            TimelinePresentFlags.Reconcile(t);

            // JSON キー "override" が @override へマップされ値が読める。
            var a0 = Seg(t, 1, 0).cues[0];
            Assert.IsNotNull(a0.@override);
            Assert.AreEqual(0.5f, a0.@override!.strength, 1e-4f, "override.strength");

            // insert.camera / insert.post の値も可用（flag=true 時）。
            var b = Seg(t, 1, 1);
            Assert.AreEqual(2, b.insert!.camera, "seg B insert.camera");
        }

        [Test]
        public void Reconcile_NullAndEmpty_DoNotThrow()
        {
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(null));
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(new ShowTimelineDef()));
            var t = new ShowTimelineDef { segments = new ShowTimelineSegmentDef[] { null! } };
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(t));
        }
    }
}
