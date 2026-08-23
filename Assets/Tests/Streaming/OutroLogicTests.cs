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
            Assert.AreEqual(OutroStage.Collapse, l.Stage);
            Assert.IsTrue(l.Active);
            Assert.IsTrue(l.Presenting);
            // 潰れているあいだ電力は 1 のまま（消え方は ScreenCollapse が持つ・二重に暗くしない）。
            Assert.AreEqual(1f, l.ScreenPower, 1e-4f);
            Assert.AreEqual(0f, l.ScreenCollapse, 1e-4f, "始まった瞬間はまだふつうの画");
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f, "報告はまだ出さない");
        }

        [Test]
        public void Stages_CollapseThenDarkThenReport()
        {
            var l = Make();
            l.Begin();
            OutroTiming t = OutroTiming.Default;

            l.Tick(t.collapseSec);
            Assert.AreEqual(OutroStage.Dark, l.Stage);
            Assert.AreEqual(0f, l.ScreenPower, 1e-4f, "消えた後は 0 のまま");
            Assert.AreEqual(0f, l.ScreenCollapse, 1e-4f, "段を出たら潰れの進みは 0 へ戻す");
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f);

            l.Tick(t.darkSec);
            Assert.AreEqual(OutroStage.Report, l.Stage);

            Assert.AreEqual(OutroEvent.Finished, l.Tick(t.reportFadeSec));
            Assert.AreEqual(OutroStage.Done, l.Stage);
        }

        [Test]
        public void Report_StartsWithinTwoSecondsOfTheEnd()
        {
            // ⚠⚠ ユーザー判定「テンポが悪く、終わったかがわかりずらい」（`canon/LEDGER.md` 0111）。
            //    旧構成は 7.2 秒（ちかちか 6.0 ＋ 黒 1.2）だった。ここが伸びたら回帰。
            OutroTiming t = OutroTiming.Default;
            float toFirstChar = t.collapseSec + t.darkSec;
            Assert.LessOrEqual(toFirstChar, 2.2f,
                "終わってから報告が出始めるまでが長い（旧 7.2 秒に戻っていないか）");
        }

        [Test]
        public void Done_KeepsTheReportUpAndTheScreenOff()
        {
            // 報告は次のランまで消えない。Active は落ちるが Presenting は立ったまま
            //（Director がここを見て配り続ける）。
            var l = Make();
            l.Begin();
            OutroTiming t = OutroTiming.Default;
            l.Tick(t.collapseSec);
            l.Tick(t.darkSec);
            l.Tick(t.reportFadeSec);

            Assert.AreEqual(OutroStage.Done, l.Stage);
            Assert.IsFalse(l.Active);
            Assert.IsTrue(l.Presenting, "文字を出したままなので Director は配り続ける");
            Assert.AreEqual(1f, l.ReportAlpha, 1e-4f);
            Assert.AreEqual(0f, l.ScreenPower, 1e-4f);
            Assert.AreEqual(0f, l.ScreenCollapse, 1e-4f);
            Assert.AreEqual(OutroEvent.None, l.Tick(60f), "終わった後は何も起きない");
        }

        [Test]
        public void Report_FadesInOverTheAuthoredTime()
        {
            var l = Make();
            l.Begin();
            OutroTiming t = OutroTiming.Default;
            l.Tick(t.collapseSec);
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
            Assert.AreEqual(0f, l.ScreenCollapse, 1e-4f, "出していない間は潰れも 0（恒等）");
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
            Assert.AreEqual(0f, l.ScreenCollapse, 1e-4f);
            Assert.AreEqual(0f, l.ReportAlpha, 1e-4f);
            Assert.AreEqual(OutroEvent.None, l.Tick(10f));
        }

        // --- ブラウン管の電源断（`canon/LEDGER.md` 0111）---------------------------

        [Test]
        public void Collapse_NeverFlickers()
        {
            // ⚠⚠ **これがこの実装の存在理由**（ユーザー判定「ちかちかする演出は目に悪いのでやめたい」）。
            //   進みは単調に増えるだけで、1 度も戻らない。往復する動きは
            //   (a) 光過敏の危険帯（6〜24Hz）を作り、(b) 原理的に終端に読めない。
            var l = Make();
            l.Begin();
            float prev = -1f;
            for (int i = 0; i < 90; i++)
            {
                l.Tick(OutroTiming.Default.collapseSec / 90f);
                if (l.Stage != OutroStage.Collapse) break;
                Assert.GreaterOrEqual(l.ScreenCollapse, prev, "潰れの進みが戻った（＝ ちかちかしている）");
                prev = l.ScreenCollapse;
            }
            Assert.Greater(prev, 0.9f, "段の終わりまでに潰れ切る");
        }

        [Test]
        public void Collapse_IsDeterministic()
        {
            // 乱数を使っていない（同じ版は同じ絵）。音の合成と同じ流儀。
            var a = Make(); var b = Make();
            a.Begin(); b.Begin();
            for (int i = 0; i < 40; i++)
            {
                a.Tick(0.02f); b.Tick(0.02f);
                Assert.AreEqual(a.ScreenCollapse, b.ScreenCollapse, 0f);
            }
        }

        [Test]
        public void Collapse_KeepsThePowerOnSoTheTubeIsNotDimmedTwice()
        {
            // 「装置が死んだ」を言うスイッチは `ScreenPower` 1 本だけ。潰れているあいだに
            // 電力まで落とすと、線になる前に画が沈んで「潰れた」が読めなくなる。
            var l = Make();
            l.Begin();
            for (int i = 0; i < 8; i++)
            {
                l.Tick(OutroTiming.Default.collapseSec / 10f);
                if (l.Stage != OutroStage.Collapse) break;
                Assert.AreEqual(1f, l.ScreenPower, 1e-4f);
            }
        }

        [Test]
        public void Timing_FallsBackToDefaultsWhenUnset()
        {
            // run.outro が無い / 旧キーしか無い show.json（全部 0）でも走り切る。
            var t = new OutroTiming().Sanitized();
            Assert.AreEqual(OutroTiming.Default.collapseSec, t.collapseSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.darkSec, t.darkSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.reportFadeSec, t.reportFadeSec, 1e-4f);
            Assert.AreEqual(3.6f, t.TotalSec, 1e-3f, "合計が変わったら解析の期待値も直すこと");
        }

        [Test]
        public void Timing_RefusesACollapseThatIsNotAnEvent()
        {
            // ⚠⚠ 旧キーの値（`flickerSec: 6.0`）をそのまま新キーへ写されても通さない。
            //   電源断は一回性の事象で、2 秒を超えたらそれはもう別の演出。
            var slow = new OutroTiming { collapseSec = 6.0f, darkSec = 1.2f, reportFadeSec = 1.5f }.Sanitized();
            Assert.AreEqual(OutroTiming.Default.collapseSec, slow.collapseSec, 1e-4f);

            var tooFast = new OutroTiming { collapseSec = 0.05f, darkSec = 1.2f, reportFadeSec = 1.5f }.Sanitized();
            Assert.AreEqual(OutroTiming.Default.collapseSec, tooFast.collapseSec, 1e-4f);

            // 範囲の内側は著作した値をそのまま使う。
            var ok = new OutroTiming { collapseSec = 1.4f, darkSec = 1.2f, reportFadeSec = 1.5f }.Sanitized();
            Assert.AreEqual(1.4f, ok.collapseSec, 1e-4f);
        }

        [Test]
        public void Def_WithOnlyTheOldKeysStillRunsWithDefaults()
        {
            // 旧 show.json（flickerSec しか持たない / さらに古い unswapSec ほか）は
            // JsonUtility が新キーを 0 で埋める。enabled は生きているので既定で走ること。
            var def = new ShowOutroDef { enabled = true, collapseSec = 0f, darkSec = 0f, reportFadeSec = 0f };
            Assert.IsFalse(def.LooksUnset(), "enabled が立っているので「キーごと無い」ではない");
            OutroTiming t = def.ToTiming();
            Assert.AreEqual(OutroTiming.Default.collapseSec, t.collapseSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.darkSec, t.darkSec, 1e-4f);
            Assert.AreEqual(OutroTiming.Default.reportFadeSec, t.reportFadeSec, 1e-4f);
        }

        [Test]
        public void Def_LooksUnsetWhenJsonUtilityZeroedEverything()
        {
            var def = new ShowOutroDef { enabled = false, collapseSec = 0f, darkSec = 0f, reportFadeSec = 0f };
            Assert.IsTrue(def.LooksUnset());
        }
    }
}
