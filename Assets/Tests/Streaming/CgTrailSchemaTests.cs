#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// <c>cgMode:"trail"</c> の台本の読み方を固定する。JsonUtility は欠けたキーを 0 で埋めるので、
    /// 「0 以下は既定」が崩れると、値を書いていない既存のカットで人形が体験者の真後ろ 0m に立つ。
    /// </summary>
    public sealed class CgTrailSchemaTests
    {
        [Test]
        public void trailは既知のcgMode()
        {
            Assert.That(TakeSchema.NormalizeCgMode("trail", out bool known), Is.EqualTo(TakeSchema.CgTrail));
            Assert.IsTrue(known);
            // 既存の 2 つと未知の扱いは変わらない
            Assert.That(TakeSchema.NormalizeCgMode("follow", out known), Is.EqualTo(TakeSchema.CgFollow));
            Assert.IsTrue(known);
            Assert.That(TakeSchema.NormalizeCgMode("fixed", out known), Is.EqualTo(TakeSchema.CgFixed));
            Assert.IsTrue(known);
            Assert.That(TakeSchema.NormalizeCgMode("walk", out known), Is.EqualTo(TakeSchema.CgFollow));
            Assert.IsFalse(known);
        }

        [Test]
        public void 道のりの間隔は0以下で既定_範囲へ丸める()
        {
            Assert.That(TakeSchema.ResolveTrailGapM(0f), Is.EqualTo(1.5f));
            Assert.That(TakeSchema.ResolveTrailGapM(-2f), Is.EqualTo(1.5f));
            Assert.That(TakeSchema.ResolveTrailGapM(float.NaN), Is.EqualTo(1.5f));
            Assert.That(TakeSchema.ResolveTrailGapM(2f), Is.EqualTo(2f));
            Assert.That(TakeSchema.ResolveTrailGapM(0.2f), Is.EqualTo(0.5f));
            Assert.That(TakeSchema.ResolveTrailGapM(9f), Is.EqualTo(4f));
        }

        [Test]
        public void 飛び石の刻みは0以下で既定_範囲へ丸める()
        {
            Assert.That(TakeSchema.ResolveTrailStepM(0f), Is.EqualTo(0.45f));
            Assert.That(TakeSchema.ResolveTrailStepM(-1f), Is.EqualTo(0.45f));
            Assert.That(TakeSchema.ResolveTrailStepM(0.3f), Is.EqualTo(0.3f));
            Assert.That(TakeSchema.ResolveTrailStepM(0.05f), Is.EqualTo(0.1f));
            Assert.That(TakeSchema.ResolveTrailStepM(3f), Is.EqualTo(1.5f));
        }

        [Test]
        public void 描かない近さは0以下で既定_範囲へ丸める()
        {
            Assert.That(TakeSchema.ResolveTrailHideM(0f), Is.EqualTo(0.9f));
            Assert.That(TakeSchema.ResolveTrailHideM(-1f), Is.EqualTo(0.9f));
            Assert.That(TakeSchema.ResolveTrailHideM(0.5f), Is.EqualTo(0.5f));
            Assert.That(TakeSchema.ResolveTrailHideM(5f), Is.EqualTo(3f));
        }

        [Test]
        public void 値を書いていないカットは既定で読む()
        {
            var step = JsonUtility.FromJson<ShowStepDef>("{\"cg\":\"doll\",\"cgMode\":\"trail\"}");
            Assert.That(step.cgMode, Is.EqualTo(TakeSchema.CgTrail));
            Assert.That(TakeSchema.ResolveTrailGapM(step.trailGapM), Is.EqualTo(TakeSchema.DefaultTrailGapM));
            Assert.That(TakeSchema.ResolveTrailStepM(step.trailStepM), Is.EqualTo(TakeSchema.DefaultTrailStepM));
            Assert.That(TakeSchema.ResolveTrailHideM(step.trailHideM), Is.EqualTo(TakeSchema.DefaultTrailHideM));

            var authored = JsonUtility.FromJson<ShowStepDef>(
                "{\"cg\":\"doll\",\"cgMode\":\"trail\",\"trailGapM\":2.2,\"trailStepM\":0.6,\"trailHideM\":1.2}");
            Assert.That(TakeSchema.ResolveTrailGapM(authored.trailGapM), Is.EqualTo(2.2f).Within(1e-6f));
            Assert.That(TakeSchema.ResolveTrailStepM(authored.trailStepM), Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(TakeSchema.ResolveTrailHideM(authored.trailHideM), Is.EqualTo(1.2f).Within(1e-6f));
        }
    }
}
