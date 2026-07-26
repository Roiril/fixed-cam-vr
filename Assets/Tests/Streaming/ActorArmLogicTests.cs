#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験者 → CG 人形への腕の写像と、手が取れない間の idle 合流。
    /// 体験上の要は「手が切れても人形は止まらない」「復帰で腕が瞬間移動しない」の 2 つ。
    /// </summary>
    public sealed class ActorArmLogicTests
    {
        [Test]
        public void 体格比は身長比になる()
        {
            // 体験者の頭 1.60m → 推定身長 1.72m。人形 1.72m なら等倍。
            Assert.That(ActorArmLogic.BodyScale(1.60f + ActorArmLogic.HeadToTopM, 1.60f),
                        Is.EqualTo(1f).Within(1e-4f));
            // 半分の背丈の人形なら半分のリーチ。
            Assert.That(ActorArmLogic.BodyScale((1.60f + ActorArmLogic.HeadToTopM) * 0.5f, 1.60f),
                        Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void 頭高が異常でも体格比は暴れない()
        {
            // 床に置かれた HMD（頭高 0）でもリーチが発散しない。
            float s = ActorArmLogic.BodyScale(1.6f, 0f);
            Assert.That(s, Is.LessThanOrEqualTo(3f));
            Assert.That(s, Is.GreaterThan(0f));
        }

        [Test]
        public void 同じ向きなら手の相対がそのまま乗る()
        {
            Vector3 head = new Vector3(1f, 1.6f, 2f);
            Vector3 hand = head + new Vector3(0.3f, 0.2f, 0.4f);
            Vector3 actorHead = new Vector3(-3f, 1.5f, 5f);

            Vector3 t = ActorArmLogic.MapHandToActor(hand, head, 30f, actorHead, 30f, 1f);

            Assert.That(Vector3.Distance(t, actorHead + new Vector3(0.3f, 0.2f, 0.4f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 手を上げれば目標も体格比ぶん上がる()
        {
            Vector3 head = Vector3.up * 1.6f;
            Vector3 low = head + new Vector3(0.2f, -0.4f, 0.1f);
            Vector3 high = head + new Vector3(0.2f, 0.3f, 0.1f);
            Vector3 actorHead = new Vector3(0f, 0.8f, 0f);

            Vector3 a = ActorArmLogic.MapHandToActor(low, head, 0f, actorHead, 0f, 0.5f);
            Vector3 b = ActorArmLogic.MapHandToActor(high, head, 0f, actorHead, 0f, 0.5f);

            Assert.That(b.y - a.y, Is.EqualTo((0.3f - (-0.4f)) * 0.5f).Within(1e-4f));
        }

        [Test]
        public void 人形が別の向きなら相対も回る()
        {
            Vector3 head = Vector3.up * 1.6f;
            Vector3 hand = head + new Vector3(0f, 0f, 0.5f);      // 体験者の正面 0.5m 先
            Vector3 actorHead = Vector3.up * 1.6f;

            // 人形が 90° 右を向いていれば、手も人形から見て正面（＝ワールド +X）へ出る。
            Vector3 t = ActorArmLogic.MapHandToActor(hand, head, 0f, actorHead, 90f, 1f);

            Assert.That(t.x, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(t.z, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void 追従の重みは_blendSec_で立ち上がり同じ時間で戻る()
        {
            float w = 0f;
            for (int i = 0; i < 10; i++) w = ActorArmLogic.Blend(w, true, 0.05f, 0.5f);
            Assert.That(w, Is.EqualTo(1f).Within(1e-4f));

            for (int i = 0; i < 5; i++) w = ActorArmLogic.Blend(w, false, 0.05f, 0.5f);
            Assert.That(w, Is.EqualTo(0.5f).Within(1e-3f));   // 半分だけ戻る（瞬間で落ちない）
        }

        [Test]
        public void 重みは_0_と_1_の外へ出ない()
        {
            Assert.That(ActorArmLogic.Blend(1f, true, 10f, 0.3f), Is.EqualTo(1f));
            Assert.That(ActorArmLogic.Blend(0f, false, 10f, 0.3f), Is.EqualTo(0f));
            Assert.That(ActorArmLogic.Blend(0.5f, true, -1f, 0.3f), Is.EqualTo(0.5f)); // 負 dt は無変化
        }

        [Test]
        public void 平滑化は半減期で半分近づく()
        {
            Vector3 cur = Vector3.zero;
            Vector3 tgt = new Vector3(1f, 0f, 0f);

            Vector3 half = ActorArmLogic.Smooth(cur, tgt, 0.1f, 0.1f);
            Assert.That(half.x, Is.EqualTo(0.5f).Within(1e-3f));

            // dt=0 は動かない（フレームが進んでいないのに追従してはいけない）。
            Assert.That(ActorArmLogic.Smooth(cur, tgt, 0f).x, Is.EqualTo(0f));
        }

        [Test]
        public void 平滑化は目標を追い越さない()
        {
            Vector3 cur = Vector3.zero;
            Vector3 tgt = new Vector3(1f, 0f, 0f);

            for (int i = 0; i < 100; i++) cur = ActorArmLogic.Smooth(cur, tgt, 0.5f, 0.05f);

            Assert.That(cur.x, Is.LessThanOrEqualTo(1f + 1e-4f));
            Assert.That(cur.x, Is.GreaterThan(0.99f));
        }
    }
}
