#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 床の線の「どちら側に居るか」（<see cref="LineSideLogic"/>・カットの尺 <c>durKind:"untilBeyond"</c>）。
    ///
    /// 線は x 軸に沿った (-1,0)→(1,0)。法線は A→B の左 90° なので z &lt; 0 が +1、z &gt; 0 が -1。
    /// テストは符号の値ではなく「入ってきた側の反対か」を見る。
    /// </summary>
    public sealed class LineSideLogicTests
    {
        private const int Slot = 0;

        private static LineSideLogic Make()
        {
            var l = new LineSideLogic();
            l.SetLines(new[] { LineCrossLogic.Line.Between(-1f, 0f, 1f, 0f, 0, 0) });
            return l;
        }

        // z を from から to まで 5cm 刻みで歩く（x は 0）。
        private static void WalkZ(LineSideLogic l, float from, float to, float x = 0f)
        {
            float step = to >= from ? 0.05f : -0.05f;
            int n = (int)System.Math.Round((to - from) / step);
            for (int i = 0; i <= n; i++) l.Tick(x, from + step * i);
        }

        [Test]
        public void Beyond_WhenOnTheOppositeSideFromWhereTheVisitorEntered()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -1f);
            l.CommitEntry();
            Assert.That(l.EntrySide(Slot), Is.Not.EqualTo(0), "入ってきた側が決まっていない");
            Assert.That(l.Beyond(Slot), Is.False, "まだ手前に居るのに向こう側と答えた");

            WalkZ(l, -1f, 0.5f);
            Assert.That(l.Beyond(Slot), Is.True, "線の向こうへ出たのに向こう側と答えない");
            Assert.That(l.StateView[Slot].beyond, Is.True, "StateView と Beyond が食い違う");
            Assert.That(l.StateView[Slot].camera, Is.EqualTo(0));
        }

        /// <summary>線の上で揺れても（不感帯 6cm の中）側は反転しない。</summary>
        [Test]
        public void JitterInsideTheDeadband_DoesNotFlipTheSide()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -0.5f);
            l.CommitEntry();
            int side = l.Side(Slot);

            for (int i = 0; i < 40; i++)
            {
                l.Tick(0f, (i % 2 == 0) ? 0.05f : -0.05f);
                Assert.That(l.Side(Slot), Is.EqualTo(side), $"不感帯の中の揺れで側が変わった (i={i})");
                Assert.That(l.Beyond(Slot), Is.False, $"不感帯の中の揺れで向こう側と答えた (i={i})");
            }

            l.Tick(0f, LineSideLogic.DeadbandM + 0.01f);
            Assert.That(l.Beyond(Slot), Is.True, "不感帯を出たのに向こう側にならない");
        }

        /// <summary>
        /// ⚠ <b>入ってきた側の基準は約 0.5m 手前の位置。</b> 区間の確定は dwell 0.5 秒の後なので、
        /// 入口に近い線は確定の時点で既に越えている。今の位置で決めると「向こう側」が手前側になり、
        /// 永久に終わらない。
        /// </summary>
        [Test]
        public void EntryIsTakenFromAboutHalfAMetreBack_SoAlreadyCrossedLineIsBeyond()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, 0.2f);                // 確定の時点で既に 0.2m 越えている
            l.CommitEntry();
            Assert.That(l.Beyond(Slot), Is.True, "確定の前に越えた線が向こう側にならない（今の位置で決めている）");

            // 対照: 足跡が揃わない（その場で確定した）なら今の位置が基準になる。
            LineSideLogic fresh = Make();
            fresh.Tick(0f, 0.2f);
            fresh.CommitEntry();
            Assert.That(fresh.Beyond(Slot), Is.False, "足跡が無いのに今の位置以外を基準にした");
        }

        /// <summary>反対の端から入った人は向きが逆になる（線は向きを持たない）。</summary>
        [Test]
        public void VisitorFromTheOtherSide_GetsTheOppositeDirection()
        {
            LineSideLogic a = Make();
            WalkZ(a, -2f, -1f);
            a.CommitEntry();

            LineSideLogic b = Make();
            WalkZ(b, 2f, 1f);
            b.CommitEntry();
            Assert.That(b.EntrySide(Slot), Is.EqualTo(-a.EntrySide(Slot)), "反対から入ったのに同じ向き");

            WalkZ(b, 1f, 0.5f);
            Assert.That(b.Beyond(Slot), Is.False, "まだ手前（z>0）なのに向こう側と答えた");
            WalkZ(b, 0.5f, -0.5f);
            Assert.That(b.Beyond(Slot), Is.True, "z<0 へ出たのに向こう側にならない");
        }

        /// <summary>位置が取れない間（Tick を呼ばない）は、側も向こう側も前の値のまま。</summary>
        [Test]
        public void WhilePositionIsUnavailable_NothingChanges()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -1f);
            l.CommitEntry();
            WalkZ(l, -1f, 0.5f);
            Assert.That(l.Beyond(Slot), Is.True);

            // 位置が取れないフレームは Tick しない。値は保たれる。
            for (int i = 0; i < 10; i++)
                Assert.That(l.Beyond(Slot), Is.True, "位置が取れない間に向こう側が落ちた");

            // 戻ってきた位置が 1m を超えて跳んでいても、側はその位置で取り直すだけ。
            l.Tick(0f, -1.6f);
            Assert.That(l.Beyond(Slot), Is.False, "手前に戻ったのに向こう側のまま");
        }

        /// <summary>
        /// 1 フレームで 1m を超える跳び（recenter・テレポート）は足跡を捨てて数え直す。
        /// 跳ぶ前の足跡で入ってきた側を決めない。
        /// </summary>
        [Test]
        public void JumpOverAMetre_DiscardsTheTrail()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -0.6f);           // 足跡は手前側に揃っている
            l.Tick(0f, 0.6f);               // 1.2m 跳んで向こうへ
            l.CommitEntry();
            Assert.That(l.Beyond(Slot), Is.False, "跳ぶ前の足跡を基準にした");
        }

        /// <summary>基準が不感帯の中なら未確定のまま置き、不感帯を出た最初の側を入ってきた側にする。</summary>
        [Test]
        public void EntryInsideTheDeadband_IsDecidedByTheFirstSideOutside()
        {
            LineSideLogic l = Make();
            for (float x = -0.9f; x <= 0.9f; x += 0.05f) l.Tick(x, 0f);   // 線の上を歩いてきた
            l.CommitEntry();
            Assert.That(l.EntrySide(Slot), Is.EqualTo(0), "不感帯の中で入ってきた側を決めた");
            Assert.That(l.Beyond(Slot), Is.False);

            l.Tick(0.9f, 0.3f);
            int entry = l.EntrySide(Slot);
            Assert.That(entry, Is.Not.EqualTo(0), "不感帯を出たのに決まらない");
            Assert.That(entry, Is.EqualTo(l.Side(Slot)), "不感帯を出た最初の側になっていない");
            Assert.That(l.Beyond(Slot), Is.False);

            l.Tick(0.9f, -0.3f);
            Assert.That(l.Beyond(Slot), Is.True, "最初の側の反対へ出たのに向こう側にならない");
        }

        /// <summary>区間が確定するたびに入ってきた側を決め直す（前の区間の向きを持ち越さない）。</summary>
        [Test]
        public void CommitEntry_RedecidesForTheNewSegment()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -1f);
            l.CommitEntry();
            WalkZ(l, -1f, 1f);
            Assert.That(l.Beyond(Slot), Is.True);

            l.CommitEntry();   // 向こう側で次の区間へ入った
            Assert.That(l.Beyond(Slot), Is.False, "前の区間の向きを持ち越した");
            WalkZ(l, 1f, -1f);
            Assert.That(l.Beyond(Slot), Is.True, "新しい向きで向こうへ出たのに答えない");
        }

        [Test]
        public void Reset_DropsTrailSideAndEntry()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -1f);
            l.CommitEntry();
            WalkZ(l, -1f, 0.5f);
            Assert.That(l.Beyond(Slot), Is.True);

            l.Reset();
            Assert.That(l.Beyond(Slot), Is.False, "リセットで向こう側が残った");
            Assert.That(l.EntrySide(Slot), Is.EqualTo(0));
            Assert.That(l.Side(Slot), Is.EqualTo(0));

            // リセットの後は確定が来るまで入ってきた側が無い（動いても向こう側にならない）。
            WalkZ(l, 0.5f, -1f);
            Assert.That(l.Beyond(Slot), Is.False, "確定の無いまま向こう側と答えた");
            // 足跡も落ちている: その場で確定すれば今の位置が基準になる。
            l.Reset();
            l.Tick(0f, 0.2f);
            l.CommitEntry();
            Assert.That(l.Beyond(Slot), Is.False, "リセット前の足跡を基準にした");
        }

        /// <summary>
        /// layout は走行中にも届く。形が変わらない線は入ってきた側を保つ（落とすとカットが区間の終わりまで止まる）。
        /// 形が変わった線は未確定から始め直す。
        /// </summary>
        [Test]
        public void SetLines_KeepsStateOfUnchangedLines()
        {
            LineSideLogic l = Make();
            WalkZ(l, -2f, -1f);
            l.CommitEntry();
            int entry = l.EntrySide(Slot);

            l.SetLines(new[] { LineCrossLogic.Line.Between(-1f, 0f, 1f, 0f, 0, 2) });
            Assert.That(l.EntrySide(Slot), Is.EqualTo(entry), "同じ形の線で入ってきた側を落とした");
            Assert.That(l.StateView[Slot].camera, Is.EqualTo(2), "担当カメラが更新されない");

            l.SetLines(new[] { LineCrossLogic.Line.Between(-1f, 0.5f, 1f, 0.5f, 0, 2) });
            Assert.That(l.EntrySide(Slot), Is.EqualTo(0), "形が変わった線の入ってきた側を持ち越した");
        }

        /// <summary>位置が一度も取れていなければ、確定しても全部未確定（向こう側にならない）。</summary>
        [Test]
        public void CommitWithoutAnyPosition_LeavesEverythingUndecided()
        {
            LineSideLogic l = Make();
            l.CommitEntry();
            Assert.That(l.EntrySide(Slot), Is.EqualTo(0));
            Assert.That(l.Beyond(Slot), Is.False);
        }

        /// <summary>実体の無い線（layout に無い id の枠）は決して向こう側にならない。</summary>
        [Test]
        public void UndefinedLine_IsNeverBeyond()
        {
            var l = new LineSideLogic();
            l.SetLines(new[] { LineCrossLogic.Line.Undefined });
            WalkZ(l, -2f, -1f);
            l.CommitEntry();
            WalkZ(l, -1f, 1f);
            Assert.That(l.Beyond(Slot), Is.False);
            Assert.That(l.Beyond(5), Is.False, "範囲外の slot");
        }
    }
}
