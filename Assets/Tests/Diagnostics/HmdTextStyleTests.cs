#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    /// <summary>
    /// <b>HMD の中の文字は 1 か所（<see cref="HmdTextStyle"/>）から導く</b>という不変条件を機械で守る。
    ///
    /// この不変条件は<b>破れても沈黙する</b> — 面ごとに数字を書いても Editor では読めるし、
    /// ログにも `[XP]` にも出ない。実機を被って初めて「小さすぎる」と分かる。
    /// 実際 2026-08-15 まで 7 面がそれぞれ別の <c>fontSize</c> を持っていて、1 文字の見かけ角は
    /// <b>0.18°〜2.67° と 15 倍ばらついていた</b>（うち 1 面は実機で点にしか見えない大きさだった）。
    /// </summary>
    public sealed class HmdTextStyleTests
    {
        // ---- 段 ----

        /// <summary>VR の日本語は 1 文字 1.5° を下回ると読めない（rules/show-design.md）。</summary>
        [Test]
        public void EveryTier_ClearsTheReadableFloor()
        {
            const float FloorDeg = 1.5f;
            Assert.That(HmdTextStyle.MinorDeg, Is.GreaterThanOrEqualTo(FloorDeg));
            Assert.That(HmdTextStyle.BodyDeg, Is.GreaterThanOrEqualTo(FloorDeg));
            Assert.That(HmdTextStyle.AlertDeg, Is.GreaterThanOrEqualTo(FloorDeg));
        }

        /// <summary>段は 3 つで、順序が入れ替わらない（増やすと微妙なサイズ差が再発する）。</summary>
        [Test]
        public void Tiers_AreOrdered()
        {
            Assert.That(HmdTextStyle.MinorDeg, Is.LessThan(HmdTextStyle.BodyDeg));
            Assert.That(HmdTextStyle.BodyDeg, Is.LessThan(HmdTextStyle.AlertDeg));
        }

        // ---- 逆算 ----

        /// <summary>逆算した fontSize を距離から測り直すと、指定した見かけ角に戻る（Canvas 系）。</summary>
        [Test]
        public void CanvasFontSize_RoundTrips()
        {
            foreach (float d in new[] { 0.3f, 0.45f, 1.6f, 2.6f })
                foreach (float scale in new[] { 0.001f, 0.0005f })
                {
                    float fs = HmdTextStyle.CanvasFontSize(HmdTextStyle.BodyDeg, d, scale);
                    float deg = HmdTextStyle.DegreesOf(HmdTextStyle.CanvasWorldEm(fs, scale), d);
                    Assert.That(deg, Is.EqualTo(HmdTextStyle.BodyDeg).Within(0.001f), $"d={d} scale={scale}");
                }
        }

        /// <summary>
        /// 同じく 3D 系。⚠ <b>透視カメラの TMP は fontSize に 0.1 を掛ける</b> —
        /// この係数を知らずに数字を決めたのが、2 回続けて起きた「実機で読めない」の正体。
        /// </summary>
        [Test]
        public void MeshScale_RoundTrips()
        {
            const float fontSize = 0.07f;   // 3D の面が使っている版の大きさ
            foreach (float d in new[] { 1.5f, 2.6f })
            {
                float scale = HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, d, fontSize);
                float deg = HmdTextStyle.DegreesOf(HmdTextStyle.MeshWorldEm(fontSize, scale), d);
                Assert.That(deg, Is.EqualTo(HmdTextStyle.BodyDeg).Within(0.001f), $"d={d}");
            }
        }

        /// <summary>
        /// ⚠ 2 つの単位系が食い違っていないか（同じ見かけ角なら世界サイズも同じ）。
        /// ここが破れると「Canvas の面と 3D の面で字の大きさが違う」が黙って起きる。
        /// </summary>
        [Test]
        public void BothUnitSystems_LandOnTheSameWorldSize()
        {
            const float d = 1.6f;
            float canvasEm = HmdTextStyle.CanvasWorldEm(
                HmdTextStyle.CanvasFontSize(HmdTextStyle.BodyDeg, d, 0.001f), 0.001f);
            float meshEm = HmdTextStyle.MeshWorldEm(
                0.07f, HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, d, 0.07f));
            Assert.That(meshEm, Is.EqualTo(canvasEm).Within(1e-5f));
        }

        // ---- 色 ----

        /// <summary>リッチテキストの 16 進と <see cref="Color"/> が同じ色であること。</summary>
        [Test]
        public void Hex_MatchesColor()
        {
            Assert.That(Hex(HmdTextStyle.Ink), Is.EqualTo(HmdTextStyle.InkHex));
            Assert.That(Hex(HmdTextStyle.Alert), Is.EqualTo(HmdTextStyle.AlertHex));
            Assert.That(Hex(HmdTextStyle.InkDim), Is.EqualTo(HmdTextStyle.InkDimHex));
        }

        /// <summary>
        /// ⚠⚠ <b>Tracking 側が持つ 16 進が同じ色を指していること。</b>
        /// 位置合わせのガイダンスは Tracking にあり、そちらから Diagnostics は参照できない
        /// （依存の向きが逆）。値の重複そのものは避けられないので、食い違いをここで落とす。
        /// </summary>
        [Test]
        public void TrackingHex_MatchesPalette()
        {
            Assert.That(RegistrationGuidance.FilledHex, Is.EqualTo(HmdTextStyle.InkHex));
            Assert.That(RegistrationGuidance.EmptyHex, Is.EqualTo(HmdTextStyle.InkDimHex));
            Assert.That(RegistrationGuidance.AlertHex, Is.EqualTo(HmdTextStyle.AlertHex));
        }

        /// <summary>ゲージの空きは地の色より暗い（別の色相を作らない）。</summary>
        [Test]
        public void InkDim_IsDarkerInk_NotAnotherHue()
        {
            Color ink = HmdTextStyle.Ink, dim = HmdTextStyle.InkDim;
            Assert.That(dim.r, Is.LessThan(ink.r));
            // 色相（各成分の比）が変わっていない ＝ 同じ色を落としただけ。
            Assert.That(dim.g / dim.r, Is.EqualTo(ink.g / ink.r).Within(1e-4f));
            Assert.That(dim.b / dim.r, Is.EqualTo(ink.b / ink.r).Within(1e-4f));
        }

        /// <summary>報告の面の見出しが補助の段と同じ割合であること。</summary>
        [Test]
        public void LabelPercent_MatchesMinorTier()
        {
            Assert.That(VisitorMarkGuidance.LabelPercent,
                Is.EqualTo(Mathf.RoundToInt(HmdTextStyle.MinorPercent)));
        }

        private static string Hex(Color c) =>
            $"{Mathf.RoundToInt(c.r * 255f):X2}{Mathf.RoundToInt(c.g * 255f):X2}{Mathf.RoundToInt(c.b * 255f):X2}";
    }
}
