#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 闇に浮かぶ目の座席表（<c>canon/LEDGER.md</c> 0072）。
    ///
    /// ⚠ ここで押さえたい壊れ方:
    /// ① 大きい目がスクリーンに重なる（0072「スクリーンの外の黒い背景を」に反する）
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

            // ① スクリーンに重ならない（重なると装置の映像が目に隠される ＝ 0072「スクリーンの外の」に反する）。
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
            // 加算合成では重なった 2 つが 1 つの塊に見える。要の 1 つが「白い染み」に化けない。
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            Vector3 big = AnomalyEyesMesh.BigDir;
            for (int i = 1; i < seats.Length; i++)
            {
                Assert.That(Vector3.Angle(seats[i].dir, big),
                    Is.GreaterThanOrEqualTo(AnomalyEyesMesh.BigClearDeg - 0.01f),
                    $"seat {i} が大きい目に重なる");
            }
        }

        [Test]
        public void BigEye_IsFirstAndRanksAreDistanceFromIt()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            Assert.That(seats[0].big, Is.True);
            Assert.That(seats[0].rank, Is.EqualTo(0f), "波の起点");

            Vector3 big = AnomalyEyesMesh.BigDir;
            for (int i = 1; i < seats.Length; i++)
            {
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
        public void Sizes_StayInRange()
        {
            EyeSeat[] seats = AnomalyEyesMesh.BuildSeats();
            int near = 0;
            for (int i = 1; i < seats.Length; i++)
            {
                Assert.That(seats[i].sizeDeg, Is.GreaterThanOrEqualTo(AnomalyEyesMesh.SizeMinDeg - 1e-3f));
                Assert.That(seats[i].sizeDeg, Is.LessThan(30f), "ふつうの目が大きい目より大きくならない");
                if (seats[i].sizeDeg > AnomalyEyesMesh.SizeMaxDeg) near++;
            }
            Assert.That(near, Is.GreaterThan(4), "視界を埋める近い目が何個かある（参考画像 me3）");
        }
    }
}
