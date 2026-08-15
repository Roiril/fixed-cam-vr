using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Tests.Streaming
{
    /// <summary>
    /// 終幕（装置が力尽きて、報告を出して終わる）の判断・計時を固定する。
    ///
    /// ⚠⚠ ここが狂うと **画がまるごと消えたまま戻らない**（`_ScreenPower` に 0 を書いたまま
    /// 次の体験者が来る）。実機でしか見えないうえ、原因が「終幕」だと気づくのが難しい。
    /// </summary>
    public sealed class OutroLogicTests
    {
        private static OutroLogic Make()
        {
            var l = new OutroLogic();
            l.Configure(OutroTiming.Default);
            return l;
        }

        [Test]
        public void Begin_StartsWithTheScreenStillOn()
        {
            var l = Make();
            l.Begin();
            Assert.AreEqual(OutroStage.Flicker, l.Stage);
            Assert.IsTrue(l.Active);
            Assert.IsTrue(l.Presenting);
            // 始まった瞬間に暗くなると「切れかけ」ではなく「切れた」になる。
            Assert.Greater(l.ScreenPower, 0.5f);
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f, "報告はまだ出さない");
        }

        [Test]
        public void Stages_FlickerThenDarkThenReport()
        {
            var l = Make();
            l.Begin();
            OutroTiming t = OutroTiming.Default;

            l.Tick(t.flickerSec);
            Assert.AreEqual(OutroStage.Dark, l.Stage);
            Assert.AreEqual(0f, l.ScreenPower, 1e-4f, "消えた後は 0 のまま");
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f);

            l.Tick(t.darkSec);
            Assert.AreEqual(OutroStage.Report, l.Stage);

            Assert.AreEqual(OutroEvent.Finished, l.Tick(t.reportFadeSec));
            Assert.AreEqual(OutroStage.Done, l.Stage);
        }

        [Test]
        public void Done_KeepsTheReportUpAndTheScreenOff()
        {
            // 報告は次のランまで消えない。Active は落ちるが Presenting は立ったまま
            //（Director がここを見て配り続ける）。
            var l = Make();
            l.Begin();
            OutroTiming t = OutroTiming.Default;
            l.Tick(t.flickerSec);
            l.Tick(t.darkSec);
            l.Tick(t.reportFadeSec);

            Assert.AreEqual(OutroStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
            Assert.IsTrue(l.Presenting, "文字を出したままなので Director は配り続ける");
            Assert.AreEqual(1f, l.ReportAlpha, 1e-4f);
            Assert.AreEqual(0f, l.ScreenPower, 1e-4f);
            Assert.AreEqual(OutroEvent.None, l.Tick(60f), "終わった後は何も起きない");
        }

        [Test]
        public void Report_FadesInOverTheAuthoredTime()
        {
            var l = Make();
            l.Begin();
            OutroTiming t = OutroTiming.Default;
            l.Tick(t.flickerSec);
            l.Tick(t.darkSec);                       // → Report
            Assert.AreEqual(OutroStage.Report, l.Stage);
            Assert.AreEqual(0f, l.ReportAlpha, 1e-3f);

            l.Tick(t.reportFadeSec * 0.5f);
            Assert.Greater(l.ReportAlpha, 0.3f);
            Assert.Less(l.ReportAlpha, 0.8f);
        }

        [Test]
        public void ScreenPower_IsOneWhenNotRunning()
        {
            // ⚠⚠ ここが 0 を返すと、終幕を出していない全期間で画がまるごと消える。
            var l = Make();
            Assert.AreEqual(OutroStage.Off, l.Stage);
            Assert.AreEqual(1f, l.ScreenPower, 1e-4f);
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f);
            Assert.IsFalse(l.Presenting);
        }

        [Test]
        public void Disable_StopsImmediatelyAndTurnsTheScreenBackOn()
        {
            var l = Make();
            l.Begin();
            l.Tick(1f);
            l.Disable();
            Assert.IsFalse(l.Active);
            Assert.IsFalse(l.Presenting);
            Assert.AreEqual(OutroStage.Off, l.Stage);
            Assert.AreEqual(1f, l.ScreenPower, 1e-4f);
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f);
            Assert.AreEqual(OutroEvent.None, l.Tick(10f));
        }

        // --- 電池が切れかけの管 -------------------------------------------------

        [Test]
        public void Flicker_StartsBrightAndEndsCompletelyDark()
        {
            // ⚠ 頭で落ちると「切れかけ」ではなく「切れた」になり、体験の終わりが事故に見える。
            //   実装した日に、刻み番号のハッシュが 0 番で必ず 0 を返してこれを踏んだ。
            Assert.AreEqual(1f, OutroLogic.FlickerPower(0f, 0f), 1e-4f, "頭は点いている");
            Assert.AreEqual(0f, OutroLogic.FlickerPower(1f, 6f), 1e-4f, "終わりは必ず 0 まで落とし切る");
        }

        [Test]
        public void Flicker_GetsDimmerOnAverageAsTheBatteryDies()
        {
            // 1 サンプルは落ちた瞬間かもしれないので、窓の平均で比べる（それが「だんだん」の意味）。
            float a = MeanPower(0.00f, 0.25f);
            float b = MeanPower(0.35f, 0.60f);
            float c = MeanPower(0.70f, 0.95f);
            Assert.Greater(a, b, "前半 → 中盤で暗くなる");
            Assert.Greater(b, c, "中盤 → 終盤で暗くなる");
            Assert.Greater(a, 0.7f, "序盤はほとんど落ちない（電池の放電曲線）");
            Assert.Less(c, 0.35f, "終盤はほとんど点いていない");
        }

        [Test]
        public void Flicker_ActuallyFlickers()
        {
            // 単調に暗くなるだけでは「ちかちか」にならない。同じ窓の中で明暗が割れていること。
            int lit = 0, dark = 0;
            for (int i = 0; i < 400; i++)
            {
                float p = 0.30f + 0.40f * i / 399f;
                float v = OutroLogic.FlickerPower(p, p * OutroTiming.Default.flickerSec);
                if (v > 0.5f) lit++; else if (v < 0.2f) dark++;
            }
            Assert.Greater(lit, 20, "点いている瞬間がある");
            Assert.Greater(dark, 20, "落ちている瞬間がある");
        }

        [Test]
        public void Flicker_IsDeterministic()
        {
            // 乱数を使っていない（同じ版は同じ絵）。音の合成と同じ流儀。
            for (int i = 0; i < 50; i++)
            {
                float p = i / 49f;
                float t = p * OutroTiming.Default.flickerSec;
                Assert.AreEqual(OutroLogic.FlickerPower(p, t), OutroLogic.FlickerPower(p, t), 0f);
            }
        }

        [Test]
        public void Timing_FallsBackToDefaultsWhenUnset()
        {
            // run.outro が無い / 旧キーしか無い show.json（全部 0）でも走り切る。
            var t = new OutroTiming().Sanitized();
            Assert.AreEqual(OutroTiming.Default.flickerSec, t.flickerSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.darkSec, t.darkSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.reportFadeSec, t.reportFadeSec, 1e-4f);
            Assert.AreEqual(8.7f, t.TotalSec, 1e-3f, "合計が変わったら解析の期待値も直すこと");
        }

        [Test]
        public void Def_WithOnlyTheOldKeysStillRunsWithDefaults()
        {
            // 旧 show.json（unswapSec / openSec / restoreSec / holdSec しか持たない）は
            // JsonUtility が新キーを 0 で埋める。enabled は生きているので既定で走ること。
            var def = new ShowOutroDef { enabled = true, flickerSec = 0f, darkSec = 0f, reportFadeSec = 0f };
            Assert.IsFalse(def.LooksUnset(), "enabled が立っているので「キーごと無い」ではない");
            OutroTiming t = def.ToTiming();
            Assert.AreEqual(OutroTiming.Default.flickerSec, t.flickerSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.darkSec, t.darkSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.reportFadeSec, t.reportFadeSec, 1e-4f);
        }

        [Test]
        public void Def_LooksUnsetWhenJsonUtilityZeroedEverything()
        {
            var def = new ShowOutroDef { enabled = false, flickerSec = 0f, darkSec = 0f, reportFadeSec = 0f };
            Assert.IsTrue(def.LooksUnset());
        }

        private static float MeanPower(float p0, float p1)
        {
            const int n = 200;
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                float p = p0 + (p1 - p0) * i / (n - 1f);
                sum += OutroLogic.FlickerPower(p, p * OutroTiming.Default.flickerSec);
            }
            return sum / n;
        }
    }
}
