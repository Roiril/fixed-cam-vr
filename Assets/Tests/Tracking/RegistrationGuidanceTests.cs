#nullable enable
using FixedCamVr.Tracking;
using NUnit.Framework;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// 登録ガイダンス文言の純フォーマッタ <see cref="RegistrationGuidance"/> の検証。
    /// 進捗バーの塗り数・サンプリング行・ずれの行・Review ヘッダのフォーマットを固定する。
    ///
    /// ⚠ 2026-08-15 に**語と書式を HMD 全体へ揃えた**（`FixedCamVr.Diagnostics.HmdTextStyle`）。
    /// それまでこの面は「残差」「再登録」「基準点」といった廃語を使っていて、
    /// **同じ 1 枚の面に 2 つの語彙が並んでいた**（異常の 2 行は規約を守っていた）。
    /// </summary>
    public sealed class RegistrationGuidanceTests
    {
        // ---- ProgressBar ----
        //
        // ゲージは**全塗り 1 種類を明暗で分ける**。空きに ░（網掛け）を使うと、SDF の網目が
        // 1.5° では潰れて「バー」ではなく「文字列」に見える。

        private static string Filled(int n) => $"<color=#{RegistrationGuidance.FilledHex}>" + new string('█', n) + "</color>";
        private static string Empty(int n) => $"<color=#{RegistrationGuidance.EmptyHex}>" + new string('█', n) + "</color>";

        [Test]
        public void ProgressBar_Empty_AtZero()
        {
            Assert.That(RegistrationGuidance.ProgressBar(0f, 0.5f, 5), Is.EqualTo(Empty(5)));
        }

        [Test]
        public void ProgressBar_Full_AtHold()
        {
            Assert.That(RegistrationGuidance.ProgressBar(0.5f, 0.5f, 5), Is.EqualTo(Filled(5)));
        }

        [Test]
        public void ProgressBar_Halfway_FillsHalf()
        {
            // 0.25/0.5 = 0.5 → 5 目盛の半分 = 3 塗り（AwayFromZero 丸め）+ 2 空。
            Assert.That(RegistrationGuidance.ProgressBar(0.25f, 0.5f, 5), Is.EqualTo(Filled(3) + Empty(2)));
        }

        [Test]
        public void ProgressBar_ClampsOverAndUnder()
        {
            Assert.That(RegistrationGuidance.ProgressBar(-1f, 0.5f, 4), Is.EqualTo(Empty(4)));
            Assert.That(RegistrationGuidance.ProgressBar(9f, 0.5f, 4), Is.EqualTo(Filled(4)));
        }

        /// <summary>外形（目盛の総数）は進捗によらず一定 ＝ 1 本の帯として読める。</summary>
        [Test]
        public void ProgressBar_KeepsOutline_AtEveryProgress()
        {
            foreach (float t in new[] { 0f, 0.1f, 0.25f, 0.4f, 0.5f })
            {
                int cells = 0;
                foreach (char c in RegistrationGuidance.ProgressBar(t, 0.5f, 5)) if (c == '█') cells++;
                Assert.That(cells, Is.EqualTo(5), $"t={t}");
            }
        }

        // ---- SamplingLine ----

        [Test]
        public void SamplingLine_ShowsBarWithoutRawSeconds()
        {
            string s = RegistrationGuidance.SamplingLine(0.3f, 0.5f);
            StringAssert.Contains("計測中", s);
            StringAssert.Contains("かざしたまま静止", s);
            // ⚠ `0.3/0.5s` は「経過／目標」なのか「2 つの時間」なのか一瞬では読めない。
            //    進み具合はバーが出しているので数値は要らない。
            Assert.That(s, Does.Not.Contain("0.3"));
            Assert.That(s, Does.Not.Contain("/0.5"));
        }

        // ---- ResidualLine ----

        [Test]
        public void ResidualLine_UsesCentimetersAndPlainWords()
        {
            // 廃語「残差」を使わない。単位のあいだは半角空白（HmdTextStyle の規約）。
            Assert.That(RegistrationGuidance.ResidualLine(0.05f, 0.12f),
                Is.EqualTo("最大のずれ：5 cm（合格 12 cm 以下）"));
        }

        // ---- ReviewHeader ----

        [Test]
        public void ReviewHeader_WithRecord()
        {
            Assert.That(RegistrationGuidance.ReviewHeader("2026-07-21 14:03", 0.05f, 4),
                Is.EqualTo("保存済みの位置合わせ（2026-07-21 14:03／ずれ 5 cm／4 点）"));
        }

        [Test]
        public void ReviewHeader_MissingRecord_ShowsNoRecord()
        {
            // 旧ファイル等: 保存日時 null / ずれ 0 / 点数 0 は全て「記録なし」。
            Assert.That(RegistrationGuidance.ReviewHeader(null, 0f, 0),
                Is.EqualTo("保存済みの位置合わせ（記録なし／ずれ 記録なし／点数 記録なし）"));
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
                Is.EqualTo("床の×印の上、床から 100 cm の高さで"));
            Assert.That(RegistrationGuidance.TouchInstruction("南東", 0.5f),
                Is.EqualTo("「南東」の床の×印の上、床から 50 cm の高さで"));
        }

        // ---- FloorLine ----

        [Test]
        public void FloorLine_ShowsSign()
        {
            Assert.That(RegistrationGuidance.FloorLine(0.08f, 0.02f, 0.06f), Is.EqualTo("床の高さ：+8 cm"));
            Assert.That(RegistrationGuidance.FloorLine(-0.19f, 0.01f, 0.06f), Is.EqualTo("床の高さ：-19 cm"));
        }

        [Test]
        public void FloorLine_WarnsWhenTouchesScatter()
        {
            // 「床に着けていない点がある」を現地で気づけるようにする（不合格にはしない）。
            // ⚠ `⚠` は付けない（HmdTextStyle の規約 — 警告色と出る場所で足りる）。
            Assert.That(RegistrationGuidance.FloorLine(0.08f, 0.12f, 0.06f),
                Is.EqualTo("床の高さ：+8 cm（着けていない点があります 12 cm ばらつき）"));
        }

        // ---- 語と書式の規約 ----

        /// <summary>
        /// ⚠⚠ <b>この面と異常の 2 行は同じ語彙でなければならない。</b>
        /// 2026-08-15 まで <c>RecoveryGuidance</c> だけが規約を守っていて、
        /// **同じ 1 枚の面に「ずれ」と「残差」が同時に出ていた**。
        /// </summary>
        [Test]
        public void NoRetiredWords_Anywhere()
        {
            string[] retired = { "残差", "誤差", "基準点", "再登録", "マーク", "砂嵐", "course", "周回リセット" };
            string all = string.Join("\n", new[]
            {
                RegistrationGuidance.SamplingLine(0.3f, 0.5f),
                RegistrationGuidance.ResidualLine(0.05f, 0.12f),
                RegistrationGuidance.ReviewHeader("2026-07-21 14:03", 0.05f, 4),
                RegistrationGuidance.ReviewHeader(null, 0f, 0),
                RegistrationGuidance.TouchInstruction("北西", 0f),
                RegistrationGuidance.TouchInstruction(null, 0.5f),
                RegistrationGuidance.FloorLine(0.08f, 0.12f, 0.06f),
            });
            foreach (string w in retired)
                Assert.That(all, Does.Not.Contain(w), $"廃語「{w}」が入っている");
        }

        /// <summary>`⚠` は付けない（HmdTextStyle の規約）。目立たせるのは警告色の仕事。</summary>
        [Test]
        public void NoWarningGlyph_Anywhere()
        {
            Assert.That(RegistrationGuidance.FloorLine(0.08f, 0.12f, 0.06f), Does.Not.Contain("⚠"));
            Assert.That(RegistrationGuidance.ResidualLine(0.9f, 0.12f), Does.Not.Contain("⚠"));
        }
    }
}
