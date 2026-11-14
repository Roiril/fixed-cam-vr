#nullable enable
using FixedCamVr.Streaming.Cg;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// trail の人形がカットの指定点（<c>trailFromX</c> / <c>trailFromZ</c>）から歩き出すことを固定する。
    /// 指定点に立つ → hop ごとに 1 歩 → gap に届いたら gap の飛び石へ渡す → 遠すぎる点では始めない。
    /// ここが崩れると、人形が指定点に出ない・床を滑る・引き渡しで後ろへ戻る、のどれかが起きる。
    /// </summary>
    public sealed class TrailApproachTests
    {
        private const float Gap = 1.5f;
        private const float Step = 0.45f;
        // 拍は 2 進で割り切れる値にする（0.55 だと k·hop と足し算の拍が 1ulp ずれて境目の判定が揺れる）。
        private const float Hop = 0.5f;

        // +X へ 0.1m 刻みで歩く（from を含まない・to を含む）。
        private static void WalkX(FootprintTrail t, float z, int fromTenths, int toTenths)
        {
            for (int i = fromTenths + 1; i <= toTenths; i++)
                t.Add(new Vector2(i * 0.1f, z));
        }

        // 0m から 6m まで +X へ歩いた跡（道のり 6m）。
        private static FootprintTrail StraightTrail(int toTenths = 60)
        {
            var t = new FootprintTrail();
            t.Add(Vector2.zero);
            WalkX(t, 0f, 0, toTenths);
            return t;
        }

        // ---- FootprintTrail.TryNearest / TryPointAt ----

        [Test]
        public void いちばん近い跡の点の道のりと距離を返す()
        {
            var t = StraightTrail();
            Assert.IsTrue(t.TryNearest(new Vector2(1.23f, 0.4f), out float s, out float d));
            Assert.That(s, Is.EqualTo(1.23f).Within(1e-4f), "点の間も射影する");
            Assert.That(d, Is.EqualTo(0.4f).Within(1e-4f));

            // 端より外は端の点に寄る
            Assert.IsTrue(t.TryNearest(new Vector2(-0.3f, 0f), out s, out d));
            Assert.That(s, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(d, Is.EqualTo(0.3f).Within(1e-4f));
        }

        [Test]
        public void 折り返した跡では近い方の道のりを返し_同じ近さなら新しい方()
        {
            // 2 進で割り切れる点だけで組む（同じ近さを浮動小数の誤差なしに作るため）。
            var t = new FootprintTrail();
            t.Add(new Vector2(0f, 0f));
            t.Add(new Vector2(1f, 0f));
            t.Add(new Vector2(2f, 0f));      // 往き（道のり 0..2）
            t.Add(new Vector2(2f, 0.5f));    // 横へ（道のり 2..2.5）
            t.Add(new Vector2(1f, 0.5f));
            t.Add(new Vector2(0f, 0.5f));    // 帰り（道のり 2.5..4.5）

            Assert.IsTrue(t.TryNearest(new Vector2(0.5f, 0.45f), out float s, out float d));
            Assert.That(d, Is.EqualTo(0.05f).Within(1e-4f));
            Assert.That(s, Is.EqualTo(4.0f).Within(1e-5f), "帰り道の方が近い");

            // 往きと帰りのちょうど真ん中なら新しい（道のりの長い）方
            Assert.IsTrue(t.TryNearest(new Vector2(1.0f, 0.25f), out s, out d));
            Assert.That(d, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(s, Is.EqualTo(3.5f).Within(1e-5f));
        }

        [Test]
        public void 跡が無いか非数なら近い点は無い()
        {
            var t = new FootprintTrail();
            Assert.IsFalse(t.TryNearest(Vector2.zero, out _, out float d));
            Assert.That(float.IsPositiveInfinity(d));
            t.Add(Vector2.zero);
            Assert.IsTrue(t.TryNearest(new Vector2(0f, 0.2f), out float s, out d), "1 点だけでもその点を返す");
            Assert.That(s, Is.EqualTo(0f));
            Assert.That(d, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.IsFalse(t.TryNearest(new Vector2(float.NaN, 0f), out _, out _));
        }

        [Test]
        public void 道のりの位置を引く()
        {
            var t = StraightTrail();
            Assert.IsTrue(t.TryPointAt(2.25f, out Vector2 p));
            Assert.That(p.x, Is.EqualTo(2.25f).Within(1e-4f));
            Assert.IsTrue(t.TryPointAt(99f, out p), "跡の先は最新の点");
            Assert.That(p.x, Is.EqualTo(6f).Within(1e-4f));
            Assert.IsFalse(t.TryPointAt(-0.5f, out _), "捨てた区間（最古の点より前）は引けない");
        }

        // ---- TrailApproach ----

        [Test]
        public void 始まりは指定点そのものに立つ()
        {
            var t = StraightTrail();
            var a = new TrailApproach();
            var from = new Vector2(1.0f, 0.3f);
            Assert.IsTrue(a.Begin(t, from, 10f, Step, Gap, Hop, out float near));
            Assert.That(near, Is.EqualTo(0.3f).Within(1e-4f));
            Assert.IsTrue(a.Tick(t, 10f, out Vector2 xz, out int hop));
            Assert.That(hop, Is.EqualTo(0));
            Assert.That(xz, Is.EqualTo(from), "射影した跡の点ではなく指定点に立つ");
            Assert.That(a.Target, Is.EqualTo(1.0f).Within(1e-4f), "目標は指定点にいちばん近い跡の点の道のり");

            // 拍の前は動かない
            Assert.IsTrue(a.Tick(t, 10f + Hop - 0.01f, out xz, out hop));
            Assert.That(hop, Is.EqualTo(0));
            Assert.That(xz, Is.EqualTo(from));
        }

        [Test]
        public void 拍ごとに1歩ずつ跡の上を跳ぶ()
        {
            var t = StraightTrail();
            var a = new TrailApproach();
            Assert.IsTrue(a.Begin(t, new Vector2(1.0f, 0.3f), 0f, Step, Gap, Hop, out _));

            for (int k = 1; k <= 4; k++)
            {
                float now = k * Hop;
                Assert.IsTrue(a.Tick(t, now - 0.001f, out _, out int before));
                Assert.That(before, Is.EqualTo(k - 1), $"拍 {k} の直前はまだ跳ばない");
                Assert.IsTrue(a.Tick(t, now, out Vector2 xz, out int hop));
                Assert.That(hop, Is.EqualTo(k));
                Assert.That(xz.x, Is.EqualTo(1.0f + k * Step).Within(1e-4f), "道のり s0 + k·step の点");
                Assert.That(xz.y, Is.EqualTo(0f).Within(1e-5f), "跳んだ先は跡の上");
            }
        }

        [Test]
        public void 遅れたフレームでも1回に1歩しか跳ばない()
        {
            var t = StraightTrail();
            var a = new TrailApproach();
            Assert.IsTrue(a.Begin(t, new Vector2(0.5f, 0f), 0f, Step, Gap, Hop, out _));
            Assert.IsTrue(a.Tick(t, 3f * Hop, out _, out int hop));
            Assert.That(hop, Is.EqualTo(1), "3 拍ぶん遅れても 1 歩");
            Assert.IsTrue(a.Tick(t, 3f * Hop + 0.01f, out _, out hop));
            Assert.That(hop, Is.EqualTo(1), "続けざまに跳ばない（拍を今から数え直す）");
            Assert.IsTrue(a.Tick(t, 4f * Hop + 0.01f, out _, out hop));
            Assert.That(hop, Is.EqualTo(2));
        }

        [Test]
        public void gapに届いたら飛び石へ渡し_後ろへは戻らない()
        {
            // 道のり 6m・gap 1.5 → 渡すのは目標が 4.5m に届いたとき。s0 = 2.0 から 0.45 ずつ。
            var t = StraightTrail();
            var a = new TrailApproach();
            Assert.IsTrue(a.Begin(t, new Vector2(2.0f, 0f), 0f, Step, Gap, Hop, out _));
            int lastHop = 0;
            float lastTarget = a.Target;
            float now = 0f;
            bool handed = false;
            for (int k = 1; k <= 20; k++)
            {
                now = k * Hop;
                if (!a.Tick(t, now, out _, out int hop)) { handed = a.LastEnd == TrailApproach.End.Handover; break; }
                Assert.That(hop, Is.GreaterThanOrEqualTo(lastHop));
                Assert.That(a.Target, Is.LessThan(t.Length - Gap), "目標は gap の手前まで");
                lastHop = hop;
                lastTarget = a.Target;
            }
            // 2.0 + 5·0.45 = 4.25 が最後の石（次の 4.70 は 4.5 を越える）
            Assert.That(lastHop, Is.EqualTo(5));
            Assert.That(lastTarget, Is.EqualTo(4.25f).Within(1e-4f));
            Assert.IsTrue(handed, "目標が gap に届いて飛び石へ渡した");
            Assert.IsFalse(a.Active);
            // 渡した先の gap の飛び石は、最後に立った石より前（後ろへ戻らない）
            Assert.IsTrue(t.TryPointBehind(Gap, Step, out _, out int station));
            Assert.That(station * Step, Is.GreaterThanOrEqualTo(lastTarget - 1e-4f));
        }

        [Test]
        public void gapの飛び石が手前にあるうちは今の石で待つ()
        {
            // 道のり 4.4m・gap 1.5 → gap の目標 2.9・飛び石は floor(2.9/0.45)=6 → 2.70m。
            // s0 = 2.8 の目標は飛び石より先にあるので、渡すと後ろへ戻る → 待つ。
            var t = StraightTrail(44);
            var a = new TrailApproach();
            Assert.IsTrue(a.Begin(t, new Vector2(2.8f, 0f), 0f, Step, Gap, Hop, out _));
            Assert.IsTrue(a.Tick(t, 2f * Hop, out Vector2 xz, out int hop), "渡さずに待つ");
            Assert.That(hop, Is.EqualTo(0));
            Assert.That(xz, Is.EqualTo(new Vector2(2.8f, 0f)));

            // 体験者が 4.7m まで歩く → gap の目標 3.2・飛び石 7 → 3.15m ≥ 2.8 で渡す。
            WalkX(t, 0f, 44, 47);
            Assert.IsFalse(a.Tick(t, 2f * Hop + 0.02f, out _, out _));
            Assert.That(a.LastEnd, Is.EqualTo(TrailApproach.End.Handover));
        }

        [Test]
        public void 指定点のそばに跡が無ければ始めない()
        {
            var t = StraightTrail();
            var a = new TrailApproach();
            Assert.IsFalse(a.Begin(t, new Vector2(2.0f, 0.61f), 0f, Step, Gap, Hop, out float near));
            Assert.That(near, Is.EqualTo(0.61f).Within(1e-4f));
            Assert.IsFalse(a.Active, "0.6m より遠い点では歩き出さない（gap の立ち方へ落とす）");
            Assert.IsFalse(a.Tick(t, 1f, out _, out int hop));
            Assert.That(hop, Is.EqualTo(-1));

            Assert.IsTrue(a.Begin(t, new Vector2(2.0f, 0.59f), 0f, Step, Gap, Hop, out _), "0.6m 以内なら歩き出す");
            Assert.IsFalse(new TrailApproach().Begin(new FootprintTrail(), Vector2.zero, 0f, Step, Gap, Hop, out near),
                           "跡がまだ無い");
            Assert.That(float.IsPositiveInfinity(near));
        }

        [Test]
        public void 跡が数え直されたら打ち切る()
        {
            var t = StraightTrail();
            var a = new TrailApproach();
            Assert.IsTrue(a.Begin(t, new Vector2(1.0f, 0f), 0f, Step, Gap, Hop, out _));
            t.Add(new Vector2(20f, 0f)); // 追跡の跳び → 数え直し
            Assert.IsFalse(a.Tick(t, 0.1f, out _, out _));
            Assert.That(a.LastEnd, Is.EqualTo(TrailApproach.End.Lost));
            Assert.That(a.Hop, Is.EqualTo(-1));
        }

        [Test]
        public void 止めたら止まる()
        {
            var t = StraightTrail();
            var a = new TrailApproach();
            Assert.IsTrue(a.Begin(t, new Vector2(1.0f, 0f), 0f, Step, Gap, Hop, out _));
            a.Cancel();
            Assert.IsFalse(a.Active);
            Assert.That(a.LastEnd, Is.EqualTo(TrailApproach.End.Cancelled));
        }

        // ---- 台本の読み方 ----

        [Test]
        public void 指定点はキーが無ければ非数で無効_両方あるときだけ有効()
        {
            var none = JsonUtility.FromJson<ShowStepDef>("{\"cg\":\"doll\",\"cgMode\":\"trail\"}");
            Assert.That(float.IsNaN(none.trailFromX) && float.IsNaN(none.trailFromZ), "キーが無ければ初期値の NaN のまま");
            Assert.IsFalse(TakeSchema.HasTrailFrom(none.trailFromX, none.trailFromZ));

            var half = JsonUtility.FromJson<ShowStepDef>("{\"trailFromX\":0.4}");
            Assert.IsFalse(TakeSchema.HasTrailFrom(half.trailFromX, half.trailFromZ), "片方だけでは無効");

            var both = JsonUtility.FromJson<ShowStepDef>("{\"trailFromX\":0.4,\"trailFromZ\":0}");
            Assert.IsTrue(TakeSchema.HasTrailFrom(both.trailFromX, both.trailFromZ), "0 も有効な座標");
            Assert.That(both.trailFromZ, Is.EqualTo(0f));
        }

        [Test]
        public void 配列の中のカットでもキーが無ければ非数()
        {
            var take = JsonUtility.FromJson<ShowTakeDef>(
                "{\"id\":\"x\",\"steps\":[{\"cg\":\"doll\"},{\"trailFromX\":0.4,\"trailFromZ\":1.1}]}");
            Assert.That(take.steps.Length, Is.EqualTo(2));
            Assert.That(float.IsNaN(take.steps[0].trailFromX), "配列要素でも初期値が残る");
            Assert.That(float.IsNaN(take.steps[0].trailFromZ));
            Assert.IsTrue(TakeSchema.HasTrailFrom(take.steps[1].trailFromX, take.steps[1].trailFromZ));
        }

        [Test]
        public void 非数は書き出して読み直しても無効のまま()
        {
            var take = JsonUtility.FromJson<ShowTakeDef>("{\"id\":\"x\",\"steps\":[{\"cg\":\"doll\"}]}");
            var again = JsonUtility.FromJson<ShowTakeDef>(JsonUtility.ToJson(take));
            Assert.IsFalse(TakeSchema.HasTrailFrom(again.steps[0].trailFromX, again.steps[0].trailFromZ),
                           "ToJson → FromJson の往復で 0 へ化けない（0 は有効な座標なので化けると人形が原点から歩き出す）");
        }

        [Test]
        public void 拍は0以下で既定_範囲へ丸める()
        {
            Assert.That(TakeSchema.ResolveTrailHopSec(0f), Is.EqualTo(0.55f));
            Assert.That(TakeSchema.ResolveTrailHopSec(-1f), Is.EqualTo(0.55f));
            Assert.That(TakeSchema.ResolveTrailHopSec(float.NaN), Is.EqualTo(0.55f));
            Assert.That(TakeSchema.ResolveTrailHopSec(0.8f), Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(TakeSchema.ResolveTrailHopSec(0.01f), Is.EqualTo(TakeSchema.MinTrailHopSec));
            Assert.That(TakeSchema.ResolveTrailHopSec(60f), Is.EqualTo(TakeSchema.MaxTrailHopSec));
        }
    }
}
