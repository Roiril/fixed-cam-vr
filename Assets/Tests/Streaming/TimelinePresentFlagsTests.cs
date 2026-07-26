#nullable enable
using System;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// B1 契約テスト。Web（timeline.js serialize()）が出す JSON を JsonUtility.FromJson&lt;ShowTimelineDef&gt; で
    /// パース → <see cref="TimelinePresentFlags.Reconcile"/> → consumer が gate する present-flag が honest bool に
    /// 一致することを固定する（旧バグ = object!=null 純代入で全 true に化ける）。
    ///
    /// 旧 wire 形式（flag=false でも既定オブジェクト常時 present）を主 fixture、新 wire 形式（flag=false で
    /// object omit）を従にし、Unity fix が Web の serialize() 変更に非依存であることを証明する。
    /// </summary>
    public sealed class TimelinePresentFlagsTests
    {
        // 旧 Web 形式: flag=false でも post / insert / override の既定オブジェクトを常に非 null で送る。
        //   区間A(lap1,cam0): hasPost/hasInsert=false・cue.hasOverride=false（すべて object は present）
        //   区間B(lap2,cam1): hasPost/hasInsert=true・cue.hasOverride=true + カスタム値
        //   区間B.insert.hasPost=false（insert.post は present）
        private const string OldFormJson =
            "{\"rev\":1,\"segments\":[" +
            "{\"lap\":1,\"camera\":0," +
            "\"cues\":[{\"cueId\":\"cue_A_1\",\"delaySec\":0,\"once\":true," +
            "\"override\":{\"strength\":1,\"fadeIn\":0.5,\"fadeOut\":0.5,\"trimStart\":0,\"trimEnd\":0},\"hasOverride\":false}]," +
            "\"post\":{\"exposure\":0,\"contrast\":1,\"saturation\":1,\"temperature\":0,\"vignette\":0,\"grain\":0,\"scanline\":0},\"hasPost\":false," +
            "\"insert\":{\"anchor\":\"exit\",\"camera\":0,\"delaySec\":0,\"durationSec\":4,\"cueId\":\"\",\"once\":true," +
            "\"post\":{\"exposure\":0,\"contrast\":1,\"saturation\":1,\"temperature\":0,\"vignette\":0,\"grain\":0,\"scanline\":0},\"hasPost\":false},\"hasInsert\":false}," +
            "{\"lap\":2,\"camera\":1," +
            "\"cues\":[{\"cueId\":\"cue_B_1\",\"delaySec\":1.5,\"once\":false," +
            "\"override\":{\"strength\":0.5,\"fadeIn\":1.2,\"fadeOut\":0.8,\"trimStart\":0.3,\"trimEnd\":2},\"hasOverride\":true}]," +
            "\"post\":{\"exposure\":0.4,\"contrast\":1.1,\"saturation\":0.9,\"temperature\":0.2,\"vignette\":0.3,\"grain\":0.1,\"scanline\":0.05},\"hasPost\":true," +
            "\"insert\":{\"anchor\":\"enter\",\"camera\":2,\"delaySec\":0.5,\"durationSec\":3,\"cueId\":\"cue_C_scare\",\"once\":true," +
            "\"post\":{\"exposure\":0.7,\"contrast\":1.2,\"saturation\":0.8,\"temperature\":0.1,\"vignette\":0.2,\"grain\":0.05,\"scanline\":0},\"hasPost\":false},\"hasInsert\":true}" +
            "]}";

        // 区間B.insert.hasPost=true 版（insert.post present）。AND の true 側を単離する。
        private const string OldFormInsertHasPostTrueJson =
            "{\"rev\":1,\"segments\":[" +
            "{\"lap\":2,\"camera\":1," +
            "\"cues\":[]," +
            "\"post\":{\"exposure\":0,\"contrast\":1,\"saturation\":1,\"temperature\":0,\"vignette\":0,\"grain\":0,\"scanline\":0},\"hasPost\":true," +
            "\"insert\":{\"anchor\":\"enter\",\"camera\":2,\"delaySec\":0,\"durationSec\":3,\"cueId\":\"\",\"once\":true," +
            "\"post\":{\"exposure\":0.9,\"contrast\":1,\"saturation\":1,\"temperature\":0,\"vignette\":0,\"grain\":0,\"scanline\":0},\"hasPost\":true},\"hasInsert\":true}" +
            "]}";

        // 新 Web 形式: flag=false のとき post / insert / override のキーを省略する。
        private const string NewFormJson =
            "{\"rev\":1,\"segments\":[" +
            "{\"lap\":1,\"camera\":0," +
            "\"cues\":[{\"cueId\":\"cue_A_1\",\"delaySec\":0,\"once\":true,\"hasOverride\":false}]," +
            "\"hasPost\":false,\"hasInsert\":false}" +
            "]}";

        private static ShowTimelineDef Parse(string json)
        {
            var t = JsonUtility.FromJson<ShowTimelineDef>(json);
            Assert.That(t, Is.Not.Null, "FromJson が null を返した");
            return t!;
        }

        private static ShowTimelineDef ParseAndReconcile(string json)
        {
            var t = Parse(json);
            TimelinePresentFlags.Reconcile(t);
            return t;
        }

        [Test]
        public void OldForm_SegmentA_AllPresentFlagsFalse_DespiteObjectsPresent()
        {
            var t = ParseAndReconcile(OldFormJson);
            var a = t.segments[0];
            // 旧コード（object!=null 純代入）ならここが true になり fail する。
            Assert.That(a.hasPost, Is.False, "post は present でも宣言 false なら false");
            Assert.That(a.hasInsert, Is.False, "insert は present でも宣言 false なら false（致命バグの本丸）");
            Assert.That(a.cues[0].hasOverride, Is.False, "override は present でも宣言 false なら false");
        }

        [Test]
        public void OldForm_SegmentB_AllPresentFlagsTrue()
        {
            var t = ParseAndReconcile(OldFormJson);
            var b = t.segments[1];
            Assert.That(b.hasPost, Is.True);
            Assert.That(b.hasInsert, Is.True);
            Assert.That(b.cues[0].hasOverride, Is.True);
        }

        [Test]
        public void OldForm_SegmentB_CustomValuesReadable_WhenFlagTrue()
        {
            var t = ParseAndReconcile(OldFormJson);
            var b = t.segments[1];
            Assert.That(b.post, Is.Not.Null);
            Assert.That(b.post!.exposure, Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(b.insert, Is.Not.Null);
            Assert.That(b.insert!.camera, Is.EqualTo(2));
            Assert.That(b.cues[0].@override, Is.Not.Null);
            Assert.That(b.cues[0].@override!.strength, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void OldForm_InsertHasPost_AndsDeclaredFlag()
        {
            // 区間B insert.hasPost:false + insert.post present → Reconcile 後 false。
            var falseT = ParseAndReconcile(OldFormJson);
            Assert.That(falseT.segments[1].insert!.hasPost, Is.False);

            // insert.hasPost:true + insert.post present → true。
            var trueT = ParseAndReconcile(OldFormInsertHasPostTrueJson);
            Assert.That(trueT.segments[0].insert!.hasPost, Is.True);
        }

        [Test]
        public void OldForm_OverrideKeyMapsToAtOverrideField()
        {
            // JSON キー "override"（C# 予約語）が @override フィールドへ正しくマップされる。
            var t = ParseAndReconcile(OldFormJson);
            Assert.That(t.segments[1].cues[0].@override, Is.Not.Null,
                "hasOverride:true 区間で @override が非 null（\"override\" キーがマップされている）");
        }

        [Test]
        public void NewForm_ObjectsOmitted_AllFlagsFalse()
        {
            // Unity fix は Web の serialize() 変更（object omit）に非依存 = 新形式でも flag は honest bool のまま。
            var t = ParseAndReconcile(NewFormJson);
            var a = t.segments[0];
            Assert.That(a.hasPost, Is.False);
            Assert.That(a.hasInsert, Is.False);
            Assert.That(a.cues[0].hasOverride, Is.False);
        }

        [Test]
        public void Reconcile_IsDefensive_AgainstNullAndEmpty()
        {
            // null / segments null / segments 空 / null 要素混在 / cues 空 で例外を投げない。
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(null));
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(new ShowTimelineDef { segments = null! }));
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(
                new ShowTimelineDef { segments = Array.Empty<ShowTimelineSegmentDef>() }));

            var withNulls = new ShowTimelineDef
            {
                segments = new ShowTimelineSegmentDef[]
                {
                    null!,
                    new ShowTimelineSegmentDef
                    {
                        lap = 1, camera = 0,
                        cues = new ShowSegmentCueDef[] { null! },
                        hasPost = true, post = null,          // 宣言 true・object null → false（null-deref 回避）
                        hasInsert = true, insert = null,      // 同上
                    },
                },
            };
            Assert.DoesNotThrow(() => TimelinePresentFlags.Reconcile(withNulls));
            var seg = withNulls.segments[1]!;
            Assert.That(seg.hasPost, Is.False, "宣言 true でも object null なら false（AND の安全側 downgrade）");
            Assert.That(seg.hasInsert, Is.False);
        }

        // ---- 演出（Take）の BGM present-flag（2026-07-26）---------------------------

        [Test]
        public void TakeBgm_Flag_IsDeclaredBoolAndObject()
        {
            // 旧 Web 形式（flag=false でも既定 bgm オブジェクトを送る）でも幽霊にならないこと。
            const string json =
                "{\"rev\":1,\"schema\":3,\"segments\":[" +
                "{\"lap\":1,\"camera\":0,\"takes\":[" +
                "{\"id\":\"t0\",\"steps\":[],\"bgm\":{\"action\":\"play\",\"trackId\":\"scare\"},\"hasBgm\":false}," +
                "{\"id\":\"t1\",\"steps\":[],\"bgm\":{\"action\":\"play\",\"trackId\":\"scare\"},\"hasBgm\":true}," +
                "{\"id\":\"t2\",\"steps\":[],\"hasBgm\":true}" +
                "],\"hasPost\":false,\"hasBgm\":false}]}";
            var tl = JsonUtility.FromJson<ShowTimelineDef>(json);
            TimelinePresentFlags.Reconcile(tl);

            ShowTakeDef[] takes = tl.segments[0].takes;
            Assert.That(takes[0].hasBgm, Is.False, "宣言 false は object があっても false（幽霊 BGM を作らない）");
            Assert.That(takes[1].hasBgm, Is.True);
            Assert.That(takes[1].bgm!.trackId, Is.EqualTo("scare"));
            // t2 は bgm キー省略。JsonUtility は入れ子を既定インスタンスで作ることがあるため
            // 「宣言 true ∧ object 存在」の AND で決まる（object が来なければ false へ落ちる）。
            Assert.That(takes[2].hasBgm, Is.EqualTo(takes[2].bgm != null));
        }
    }
}
