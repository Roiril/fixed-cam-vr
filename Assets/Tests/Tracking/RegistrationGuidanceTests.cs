#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 登録ガイダンス文言の純フォーマッタ <see cref="RegistrationGuidance"/> の検証。
    /// 進捗バーの塗り数・サンプリング行・残差行・Review ヘッダ（記録あり/なし）のフォーマットを固定する。
    /// </summary>
    public sealed class RegistrationGuidanceTests
    {
        // ---- ProgressBar ----

        [Test]
        public void ProgressBar_Empty_AtZero()
        {
            Assert.That(RegistrationGuidance.ProgressBar(0f, 0.5f, 5), Is.EqualTo("░░░░░"));
        }

        [Test]
        public void ProgressBar_Full_AtHold()
        {
            Assert.That(RegistrationGuidance.ProgressBar(0.5f, 0.5f, 5), Is.EqualTo("█████"));
        }

        [Test]
        public void ProgressBar_Halfway_FillsHalf()
        {
            // 0.25/0.5 = 0.5 → 5 目盛の半分 = 3 塗り（AwayFromZero 丸め）+ 2 空。
            Assert.That(RegistrationGuidance.ProgressBar(0.25f, 0.5f, 5), Is.EqualTo("███░░"));
        }

        [Test]
        public void ProgressBar_ClampsOverAndUnder()
        {
            Assert.That(RegistrationGuidance.ProgressBar(-1f, 0.5f, 4), Is.EqualTo("░░░░"));
            Assert.That(RegistrationGuidance.ProgressBar(9f, 0.5f, 4), Is.EqualTo("████"));
        }

        // ---- SamplingLine ----

        [Test]
        public void SamplingLine_ContainsBarAndClampedTime()
        {
            string s = RegistrationGuidance.SamplingLine(0.3f, 0.5f);
            StringAssert.Contains("計測中", s);
            StringAssert.Contains("0.3/0.5s", s);
            StringAssert.Contains("かざしたまま静止", s);
        }

        [Test]
        public void SamplingLine_ClampsShownTimeToHold()
        {
            // 経過が hold を超えても表示は hold 止まり。
            StringAssert.Contains("0.5/0.5s", RegistrationGuidance.SamplingLine(0.9f, 0.5f));
        }

        // ---- ResidualLine ----

        [Test]
        public void ResidualLine_FormatsTwoDecimals()
        {
            Assert.That(RegistrationGuidance.ResidualLine(0.05f, 0.12f),
                Is.EqualTo("最大残差 0.05m（合格 ≤0.12m）"));
        }

        // ---- ReviewHeader ----

        [Test]
        public void ReviewHeader_WithRecord()
        {
            Assert.That(RegistrationGuidance.ReviewHeader("2026-07-21 14:03", 0.05f, 4),
                Is.EqualTo("登録済みの位置合わせを表示中（保存: 2026-07-21 14:03 / 残差 0.05m / 4点）"));
        }

        [Test]
        public void ReviewHeader_MissingRecord_ShowsNoRecord()
        {
            // 旧ファイル等: 保存日時 null / 残差 0 / 点数 0 は全て「記録なし」。
            Assert.That(RegistrationGuidance.ReviewHeader(null, 0f, 0),
                Is.EqualTo("登録済みの位置合わせを表示中（保存: 記録なし / 残差 記録なし / 記録なし）"));
        }

        // ---- TouchInstruction（床の高さも測るようになったので指示が変わった）----

        [Test]
        public void TouchInstruction_Zero_MeansPutItOnTheFloor()
        {
            Assert.That(RegistrationGuidance.TouchInstruction(null, 0f),
                Is.EqualTo("床の×印にコントローラの先を着けて"));
        }

        [Test]
        public void TouchInstruction_WithLabel()
        {
            Assert.That(RegistrationGuidance.TouchInstruction("北西", 0f),
                Is.EqualTo("「北西」の床の×印にコントローラの先を着けて"));
        }

        [Test]
        public void TouchInstruction_WithHeight_ShowsCentimeters()
        {
            Assert.That(RegistrationGuidance.TouchInstruction(null, 1.0f),
                Is.EqualTo("床の×印の上、床から 100cm の高さで"));
            Assert.That(RegistrationGuidance.TouchInstruction("南東", 0.5f),
                Is.EqualTo("「南東」の床の×印の上、床から 50cm の高さで"));
        }

        // ---- FloorLine ----

        [Test]
        public void FloorLine_ShowsSign()
        {
            Assert.That(RegistrationGuidance.FloorLine(0.08f, 0.02f, 0.06f), Is.EqualTo("床の高さ +0.08m"));
            Assert.That(RegistrationGuidance.FloorLine(-0.19f, 0.01f, 0.06f), Is.EqualTo("床の高さ -0.19m"));
        }

        [Test]
        public void FloorLine_WarnsWhenTouchesScatter()
        {
            // 「床に着けていない点がある」を現地で気づけるようにする（不合格にはしない）。
            Assert.That(RegistrationGuidance.FloorLine(0.08f, 0.12f, 0.06f),
                Is.EqualTo("床の高さ +0.08m（⚠ タッチ高さが 0.12m ばらついています）"));
        }
    }
}
