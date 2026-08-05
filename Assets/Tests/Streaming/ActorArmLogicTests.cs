#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験者 → CG 人形への腕の写像と、手が取れない間の idle 合流。
    /// 体験上の要は 3 つ:
    ///   「腕を伸ばし切った」が人形でも伸ばし切りになる（頭身が違っても破綻しない）
    ///   首を振っただけでは手が振り回されない
    ///   手が切れても人形は止まらない・復帰で腕が瞬間移動しない
    /// </summary>
    public sealed class ActorArmLogicTests
    {
        // 体験者の代表値（頭 = HMD = 目の高さ 1.60m → 身長 1.72m）。
        private const float Head = 1.60f;

        [Test]
        public void 腕の比は腕の長さの比になる()
        {
            float playerArm = ActorArmLogic.EstimateArmLengthM(Head);
            Assert.That(ActorArmLogic.ArmScale(playerArm, Head), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(ActorArmLogic.ArmScale(playerArm * 0.5f, Head), Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void 頭高が異常でも比は暴れない()
        {
            // 床に置かれた HMD（頭高 0）でもリーチが発散しない。
            float s = ActorArmLogic.ArmScale(0.46f, 0f);
            Assert.That(s, Is.LessThanOrEqualTo(5f));
            Assert.That(s, Is.GreaterThan(0f));
        }

        [Test]
        public void 体験者の肩は頭の下やや外に推定される()
        {
            Vector3 head = new Vector3(1f, Head, 2f);
            Vector3 right = ActorArmLogic.EstimateShoulder(head, 0f, +1f, Head);
            Vector3 left = ActorArmLogic.EstimateShoulder(head, 0f, -1f, Head);

            float h = ActorArmLogic.EstimateHeightM(Head);          // 1.72
            Assert.That(head.y - right.y, Is.EqualTo(h * 0.15f).Within(1e-4f), "肩は頭より下");
            Assert.That(right.x - head.x, Is.EqualTo(h * 0.104f).Within(1e-4f), "右肩は右へ");
            Assert.That(left.x - head.x, Is.EqualTo(-h * 0.104f).Within(1e-4f), "左肩は左へ");
            Assert.That(right.z, Is.EqualTo(head.z).Within(1e-4f), "前後には出さない");
        }

        [Test]
        public void 体験者の肩は体の向きに合わせて回る()
        {
            Vector3 head = Vector3.up * Head;
            // 体が +90° を向いていれば、右肩はワールドの -Z 側へ出る。
            Vector3 right = ActorArmLogic.EstimateShoulder(head, 90f, +1f, Head);
            float h = ActorArmLogic.EstimateHeightM(Head);
            Assert.That(right.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(right.z, Is.EqualTo(-h * 0.104f).Within(1e-3f));
        }

        [Test]
        public void 同じ向き同じ体格なら肩からの相対がそのまま乗る()
        {
            Vector3 shoulder = new Vector3(1f, 1.4f, 2f);
            Vector3 hand = shoulder + new Vector3(0.3f, -0.2f, 0.4f);
            Vector3 actorShoulder = new Vector3(-3f, 1.3f, 5f);

            Vector3 t = ActorArmLogic.MapHandToActor(hand, shoulder, 30f, actorShoulder, 30f, 1f);

            Assert.That(Vector3.Distance(t, actorShoulder + new Vector3(0.3f, -0.2f, 0.4f)),
                        Is.LessThan(1e-4f));
        }

        /// <summary>
        /// この写像の存在理由。**腕を伸ばし切った体験者は、人形でも伸ばし切りになる**。
        /// 旧実装（頭基準 + 全高比）はここが 1.26 まで外れ、腕が棒のように伸び切っていた。
        /// </summary>
        [Test]
        public void 伸ばし切りは人形でも伸ばし切りになる()
        {
            float playerArm = ActorArmLogic.EstimateArmLengthM(Head);
            Vector3 head = Vector3.up * Head;
            Vector3 shoulder = ActorArmLogic.EstimateShoulder(head, 0f, +1f, Head);
            Vector3 hand = shoulder + Vector3.forward * playerArm;      // 真正面へ伸ばし切り

            // 腕の長さが体験者の 0.6 倍しかない人形。
            float actorArm = playerArm * 0.6f;
            Vector3 actorShoulder = new Vector3(5f, 0.9f, -2f);
            Vector3 t = ActorArmLogic.MapHandToActor(hand, shoulder, 0f, actorShoulder, 0f,
                                                     ActorArmLogic.ArmScale(actorArm, Head));

            Assert.That(Vector3.Distance(actorShoulder, t), Is.EqualTo(actorArm).Within(1e-4f));
        }

        [Test]
        public void 手を上げれば目標も腕長比ぶん上がる()
        {
            Vector3 head = Vector3.up * Head;
            Vector3 sh = ActorArmLogic.EstimateShoulder(head, 0f, -1f, Head);
            Vector3 low = sh + new Vector3(0.2f, -0.4f, 0.1f);
            Vector3 high = sh + new Vector3(0.2f, 0.3f, 0.1f);
            Vector3 actorSh = new Vector3(0f, 0.8f, 0f);

            Vector3 a = ActorArmLogic.MapHandToActor(low, sh, 0f, actorSh, 0f, 0.5f);
            Vector3 b = ActorArmLogic.MapHandToActor(high, sh, 0f, actorSh, 0f, 0.5f);

            Assert.That(b.y - a.y, Is.EqualTo((0.3f - (-0.4f)) * 0.5f).Within(1e-4f));
        }

        [Test]
        public void 人形が別の向きなら相対も回る()
        {
            Vector3 sh = Vector3.up * 1.4f;
            Vector3 hand = sh + new Vector3(0f, 0f, 0.5f);      // 体験者の正面 0.5m 先
            Vector3 actorSh = Vector3.up * 1.4f;

            // 人形が 90° 右を向いていれば、手も人形から見て正面（＝ワールド +X）へ出る。
            Vector3 t = ActorArmLogic.MapHandToActor(hand, sh, 0f, actorSh, 90f, 1f);

            Assert.That(t.x, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(t.z, Is.EqualTo(0f).Within(1e-3f));
        }

        // ---- 体の向き --------------------------------------------------------

        [Test]
        public void 体の向きは頭の向きへ半減期で近づく()
        {
            // 上限を外して遅れだけを見る。
            float body = ActorArmLogic.SmoothYawDeg(0f, 90f, 0.2f, 0.2f, maxTwistDeg: 360f);
            Assert.That(body, Is.EqualTo(45f).Within(0.5f));

            // dt=0 は動かない。
            Assert.That(ActorArmLogic.SmoothYawDeg(10f, 90f, 0f, 0.35f, 360f), Is.EqualTo(10f));
        }

        /// <summary>
        /// 首のねじれ切り。**体ごと回った体験者に人形が付いていく**ための仕掛け。
        /// これが無いと 90° 振り向いた場面で体が置いていかれ、手が背中側へ回る。
        /// </summary>
        [Test]
        public void 首がねじれ切ったら体が引かれる()
        {
            // 頭が 90°、体が 0° は差 90° ＝ 人の首では無理。上限 50° まで引かれる。
            float body = ActorArmLogic.SmoothYawDeg(0f, 90f, 0f, 0.35f, 50f);
            Assert.That(body, Is.EqualTo(40f).Within(0.1f));

            // 可動域の内側なら引かれない（首だけ振った状態を保つ）。
            float small = ActorArmLogic.SmoothYawDeg(0f, 30f, 0f, 0.35f, 50f);
            Assert.That(small, Is.EqualTo(0f).Within(0.1f));
        }

        [Test]
        public void ねじれ切りは逆向きでも効く()
        {
            float body = ActorArmLogic.SmoothYawDeg(0f, -90f, 0f, 0.35f, 50f);
            Assert.That(body, Is.EqualTo(-40f).Within(0.1f));
        }

        [Test]
        public void ねじれ切りは_180_をまたいでも暴れない()
        {
            // 体 170°・頭 -100°（＝ +260 ではなく -90 の差）。短い方へ 50° 制限で寄る。
            float body = ActorArmLogic.SmoothYawDeg(170f, -100f, 0f, 0.35f, 50f);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(body, -100f)), Is.EqualTo(50f).Within(0.1f));
        }

        [Test]
        public void 体の向きは短い方へ回る()
        {
            // 170° から -170°（＝ +20°）へ。360 をまたいで逆走しない。
            float body = ActorArmLogic.SmoothYawDeg(170f, -170f, 1f, 0.1f);
            Assert.That(Mathf.DeltaAngle(body, -170f), Is.EqualTo(0f).Within(1f));
        }

        /// <summary>
        /// **体の向きを使う理由**。手も体も動いていないのに頭だけ 60° 回った場面で、
        /// 頭の向きを渡すと肩の推定がずれて手首目標が飛ぶ。
        /// </summary>
        [Test]
        public void 頭の向きで写すと手が動いていないのに目標が飛ぶ()
        {
            Vector3 head = Vector3.up * Head;
            Vector3 actorSh = new Vector3(2f, 1.3f, 1f);
            Vector3 sh = ActorArmLogic.EstimateShoulder(head, 0f, +1f, Head);
            Vector3 hand = sh + new Vector3(0.02f, -0.5f, 0.02f);   // 手は下ろしたまま動かない

            Vector3 byBody = ActorArmLogic.MapHandToActor(hand, sh, 0f, actorSh, 0f, 1f);

            // 頭だけ 60° 回った瞬間に頭の向きで写すと、肩が回って推定され相対が狂う。
            Vector3 shHead = ActorArmLogic.EstimateShoulder(head, 60f, +1f, Head);
            Vector3 byHead = ActorArmLogic.MapHandToActor(hand, shHead, 60f, actorSh, 60f, 1f);

            Assert.That(Vector3.Distance(byBody, byHead), Is.GreaterThan(0.1f),
                        "この差が「首を振ると腕が振り回される」の正体");
        }

        /// <summary>
        /// 体ごと回ったときは、**人形の体から見た**手の位置が変わらない
        /// （ワールドでは人形ごと回るので、ワールド座標を比べても一致しない）。
        /// </summary>
        [Test]
        public void 体ごと回れば体から見た手の位置は変わらない()
        {
            Vector3 head = Vector3.up * Head;
            Vector3 actorSh = new Vector3(2f, 1.3f, 1f);
            Vector3 local = new Vector3(0.02f, -0.5f, 0.02f);   // 体に対する手の位置

            Vector3 sh0 = ActorArmLogic.EstimateShoulder(head, 0f, +1f, Head);
            Vector3 a = ActorArmLogic.MapHandToActor(sh0 + local, sh0, 0f, actorSh, 0f, 1f);

            Vector3 sh60 = ActorArmLogic.EstimateShoulder(head, 60f, +1f, Head);
            Vector3 hand60 = sh60 + Quaternion.Euler(0f, 60f, 0f) * local;
            Vector3 c = ActorArmLogic.MapHandToActor(hand60, sh60, 60f, actorSh, 60f, 1f);

            // 人形の体の向きで逆回転して比べる。
            Vector3 relA = a - actorSh;
            Vector3 relC = Quaternion.Euler(0f, -60f, 0f) * (c - actorSh);
            Assert.That(Vector3.Distance(relA, relC), Is.LessThan(1e-4f));
        }

        // ---- 合流と平滑化 ----------------------------------------------------

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
