#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 闇に浮かぶ目の座席表（<c>canon/LEDGER.md</c> 0075）。
    ///
    /// ⚠ ここで押さえたい壊れ方:
    /// ① 大きい目がスクリーンに重なる（0075「スクリーンの外の黒い背景を」に反する）
    /// ② 大きい目が視界の外に置かれる（誰にも気づかれずに 7.5 秒が終わる）
    /// ③ 順位が大きい目からの角度でなくなる（波が広がらず、散発的な点滅になる）
    /// ④ 割合（<c>eyes</c>）が効かない
    /// ⑤ 並びが走行ごとに変わる（同じ版で同じ絵にならない）
    /// </summary>
    public sealed class AnomalyEyesMeshTests
    {
        /// <summary>本編のスクリーンの半画角。<c>IntroVeil.fallbackApertureHalfAngleDeg</c> の実測値。</summary>
        private const float ScreenHalfYawDeg = 30.6f;
        private const float ScreenHalfPitchDeg = 18.4f;

        /// <summary>スクリーンの中心は頭の正面から 8° 下（<c>ScreenAnchor.heightOffset</c> -0.28m / 2.0m）。</summary>
        private const float ScreenDropDeg = 8f;

        /// <summary>Quest 3 の表示画角（片眼・おおよそ）。</summary>
        private const float ViewHalfYawDeg = 52f;
        private const float ViewHalfPitchDeg = 45f;

        [Test]
        public void Seats_HaveExactlyOneBigEye()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            Assert.That(seats.Length, Is.EqualTo(AnomalyEyesMesh.TotalCount));
            int big = 0;
            foreach (EyeSeat s in seats) if (s.big) big++;
            Assert.That(big, Is.EqualTo(1), "気づかせる目は 1 つだけ（増やすと「1 つに見られている」が消える）");
        }

        [Test]
        public void BigEye_SitsOutsideTheScreenButInsideTheView()
        {
            // 目の見かけの大きさ = 横 sizeDeg / 縦はその半分（AspectHeight と瞼の開きから）。
            float halfW = AnomalyEyesMesh.BigSizeDeg * 0.5f;
            float halfH = halfW * 0.5f;
            float left = AnomalyEyesMesh.BigYawDeg - halfW;
            float right = AnomalyEyesMesh.BigYawDeg + halfW;
            float bottom = AnomalyEyesMesh.BigElevDeg - halfH;
            float top = AnomalyEyesMesh.BigElevDeg + halfH;

            // ① スクリーンに重ならない（重なると装置の映像が目に隠される ＝ 0075「スクリーンの外の」に反する）。
            //   横で外れているか、縦で外れているかのどちらかで足りる。
            bool clearSideways = left > ScreenHalfYawDeg + 1f || right < -ScreenHalfYawDeg - 1f;
            bool clearVertically = bottom > ScreenHalfPitchDeg - ScreenDropDeg + 1f;
            Assert.That(clearSideways || clearVertically, Is.True,
                $"大きい目（横 {left:F1}〜{right:F1}° / 縦 {bottom:F1}〜{top:F1}°）が"
                + $"スクリーン（横 ±{ScreenHalfYawDeg}° / 上端 {ScreenHalfPitchDeg - ScreenDropDeg}°）に重なる");

            // ② 表示画角の内側（外に置くと、まっすぐスクリーンを見ている体験者に一生見えない）。
            Assert.That(right, Is.LessThan(ViewHalfYawDeg), "視界の端で切れる");
            Assert.That(top, Is.LessThan(ViewHalfPitchDeg), "視界の上で切れる");
        }

        [Test]
        public void OtherEyes_KeepClearOfTheBigEye()
        {
            // 要の 1 つが隣と重なって「白い染み」に化けない。
            // ⚠ 空ける角度は**相手の大きさで変わる**（定数にすると視界を埋める目が覆いかぶさる）。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            Vector3 big = AnomalyEyesMesh.BigDir;
            foreach (EyeSeat s in seats)
            {
                if (s.big) continue;
                Assert.That(Vector3.Angle(s.dir, big),
                    Is.GreaterThanOrEqualTo(AnomalyEyesMesh.ClearDegFor(s.sizeDeg) - 0.01f),
                    $"大きさ {s.sizeDeg:F1}° の目が大きい目に重なる");
            }
        }

        [Test]
        public void RanksAreDistanceFromTheBigEye()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            Vector3 big = AnomalyEyesMesh.BigDir;
            for (int i = 0; i < seats.Length; i++)
            {
                if (seats[i].big) { Assert.That(seats[i].rank, Is.EqualTo(0f), "波の起点"); continue; }
                float expect = Vector3.Angle(seats[i].dir, big) / 180f;
                Assert.That(seats[i].rank, Is.EqualTo(expect).Within(1e-3f),
                    $"順位は大きい目からの角度（seat {i}）");
                Assert.That(seats[i].rank, Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void Seats_CoverTheWholeSphere()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            // 前後左右に居ること（真後ろが空だと「振り返ったら何も無い」になる）。
            int back = 0, left = 0, right = 0, front = 0, up = 0, down = 0;
            foreach (EyeSeat s in seats)
            {
                if (s.dir.z < -0.5f) back++;
                if (s.dir.z > 0.5f) front++;
                if (s.dir.x < -0.5f) left++;
                if (s.dir.x > 0.5f) right++;
                if (s.dir.y > 0.35f) up++;
                if (s.dir.y < -0.35f) down++;
            }
            Assert.That(back, Is.GreaterThan(8), "真後ろにも目が居る");
            Assert.That(front, Is.GreaterThan(8));
            Assert.That(left, Is.GreaterThan(8));
            Assert.That(right, Is.GreaterThan(8));
            Assert.That(up, Is.GreaterThan(2), "上も空にしない");
            Assert.That(down, Is.GreaterThan(2));
        }

        [Test]
        public void Seats_LeanTowardTheHorizon()
        {
            // 体験者がまず見る帯（水平から ±30°）へ半分以上が集まっている。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            int band = 0;
            foreach (EyeSeat s in seats) if (Mathf.Abs(s.dir.y) < 0.5f) band++;
            Assert.That(band, Is.GreaterThan(seats.Length / 2));
        }

        [Test]
        public void CountOpen_RespectsDensity()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            int full = AnomalyEyesMesh.CountOpen(seats, big01: 1f, field01: 1f, density01: 1f);
            int half = AnomalyEyesMesh.CountOpen(seats, big01: 1f, field01: 1f, density01: 0.5f);
            Assert.That(full, Is.EqualTo(seats.Length), "割合 1 なら全部開く");
            Assert.That(half, Is.LessThan(full).And.GreaterThan(seats.Length / 4),
                "割合を下げると減る（ただし大きい目は必ず残る）");

            int none = AnomalyEyesMesh.CountOpen(seats, big01: 1f, field01: 0f, density01: 1f);
            Assert.That(none, Is.EqualTo(1), "凝視のあいだに開いているのは大きい目 1 つだけ");

            int closed = AnomalyEyesMesh.CountOpen(seats, big01: 0f, field01: 0f, density01: 1f);
            Assert.That(closed, Is.EqualTo(0));
        }

        [Test]
        public void BigEye_IgnoresDensity()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            Assert.That(AnomalyEyesMesh.CountOpen(seats, big01: 1f, field01: 0f, density01: 0f),
                Is.EqualTo(1), "割合 0 でも大きい目は出る（この 1 つが異変の要）");
        }

        [Test]
        public void Seats_AreDeterministic()
        {
            EyeSeat[] a = AnomalyEyesMesh.BuildSeats();
            EyeSeat[] b = AnomalyEyesMesh.BuildSeats();
            for (int i = 0; i < a.Length; i++)
            {
                Assert.That(b[i].dir, Is.EqualTo(a[i].dir));
                Assert.That(b[i].presence, Is.EqualTo(a[i].presence));
                Assert.That(b[i].sizeDeg, Is.EqualTo(a[i].sizeDeg));
            }
        }

        [Test]
        public void Sizes_SpanFourTiers()
        {
            // ⚠⚠ ここが「まだ全然足りていない」の主因だった（0076）。参考画像の目は枠幅の 18〜29% で、
            //    こちらは 5〜10% しか無かった。**視界を埋める目が必ず何個かある**ことを固定する。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            int tiny = 0, mid = 0, near = 0, huge = 0;
            foreach (EyeSeat s in seats)
            {
                if (s.big) continue;
                if (s.sizeDeg < 8f) tiny++;
                else if (s.sizeDeg < 20f) mid++;
                else if (s.sizeDeg < 40f) near++;
                else huge++;
            }
            Assert.That(tiny, Is.GreaterThan(10), "遠くの点（密度と奥行き）");
            Assert.That(mid, Is.GreaterThan(40), "ふつうの目");
            Assert.That(near, Is.GreaterThan(15), "近い目");
            Assert.That(huge, Is.GreaterThan(3), "視界を埋める目（参考画像 me3）");
        }

        [Test]
        public void Seats_AreSortedSmallestFirst()
        {
            // 前乗算アルファは**後に描いた方が手前**。大きい ＝ 近い目を後に置かないと遠近が逆に見える。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            for (int i = 1; i < seats.Length; i++)
                Assert.That(seats[i].sizeDeg, Is.GreaterThanOrEqualTo(seats[i - 1].sizeDeg - 1e-4f));
        }

        [Test]
        public void Shapes_DifferFromEyeToEye()
        {
            // 全部が同じ形だと壁紙の模様に見える（Codex の指摘）。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            float aMin = 9f, aMax = -9f, upMin = 9f, upMax = -9f, irMin = 9f, irMax = -9f, omMin = 9f;
            int halfOpen = 0, tilted = 0;
            foreach (EyeSeat s in seats)
            {
                aMin = Mathf.Min(aMin, s.aspect); aMax = Mathf.Max(aMax, s.aspect);
                upMin = Mathf.Min(upMin, s.lidUp); upMax = Mathf.Max(upMax, s.lidUp);
                irMin = Mathf.Min(irMin, s.irisR); irMax = Mathf.Max(irMax, s.irisR);
                omMin = Mathf.Min(omMin, s.openMax);
                if (s.openMax < 0.85f) halfOpen++;
                if (Mathf.Abs(s.rollDeg) > 20f) tilted++;
            }
            Assert.That(aMax - aMin, Is.GreaterThan(0.30f), "細い目と丸い目が混ざる");
            Assert.That(upMax - upMin, Is.GreaterThan(0.15f), "瞼の上がり方が個体で違う");
            Assert.That(irMax - irMin, Is.GreaterThan(0.20f), "虹彩の大きさが個体で違う");
            Assert.That(omMin, Is.LessThan(0.80f), "半開きのまま止まる目がある");
            Assert.That(halfOpen, Is.GreaterThan(20), "全部が全開にはならない");
            Assert.That(tilted, Is.GreaterThan(30), "傾いた目が混ざる");
        }

        [Test]
        public void BigEyes_HaveBiggerIrises()
        {
            // 大きい目ほど虹彩が白目を食う ＝ 参考 me3 の「巨大な虹彩と黒い内部」が自動的に出る。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            float smallSum = 0f, hugeSum = 0f;
            int smallN = 0, hugeN = 0;
            foreach (EyeSeat s in seats)
            {
                if (s.big) continue;
                if (s.sizeDeg < 8f) { smallSum += s.irisR; smallN++; }
                else if (s.sizeDeg > 40f) { hugeSum += s.irisR; hugeN++; }
            }
            Assert.That(smallN, Is.GreaterThan(0));
            Assert.That(hugeN, Is.GreaterThan(0));
            Assert.That(hugeSum / hugeN, Is.GreaterThan(smallSum / smallN * 1.3f));
        }
    }
}
