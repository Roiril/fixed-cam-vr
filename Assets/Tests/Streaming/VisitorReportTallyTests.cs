#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 報告の数え方を固定する（<c>canon/LEDGER.md</c> 0234
    /// 「1つの異変に対して報告したら内部では1だけカウントするようにしてほしい」）。
    ///
    /// ⚠⚠ ここが狂うと終幕の「報告した怪異の数」が押した回数に戻る（同じ人形に 3 回押せば ３）。
    /// 実機では「数字が出た」としか見えず、後々の分岐（報告数によるエンディング）が
    /// 押した回数で分かれてしまう。
    /// </summary>
    public sealed class VisitorReportTallyTests
    {
        [Test]
        public void OnePressOnOneAnomaly_CountsOne()
        {
            var t = new VisitorReportTally();
            Assert.IsTrue(t.Record("L2C0#0", detected: true));
            Assert.AreEqual(1, t.PressCount);
            Assert.AreEqual(1, t.AnomalyCount);
            Assert.AreEqual("L2C0#0", t.LastTakeId);
            Assert.IsTrue(t.LastCounted);
            Assert.IsTrue(t.HasReported("L2C0#0"));
        }

        [Test]
        public void RepeatedPressesOnTheSameAnomaly_CountOnce()
        {
            // 侵食度 1 の嘘の一文「異常を検出しませんでした」（0232）を読んで押し直した形。
            var t = new VisitorReportTally();
            Assert.IsTrue(t.Record("L3C1#0", detected: true));
            Assert.IsFalse(t.Record("L3C1#0", detected: true));
            Assert.IsFalse(t.Record("L3C1#0", detected: true));
            Assert.AreEqual(3, t.PressCount, "押した回数はそのまま残す");
            Assert.AreEqual(1, t.AnomalyCount, "同じ演出は 1 だけ");
            Assert.IsFalse(t.LastCounted);
        }

        [Test]
        public void PressesOnDifferentAnomalies_CountEach()
        {
            var t = new VisitorReportTally();
            t.Record("L1C1#0", detected: true);
            t.Record("L2C0#0", detected: true);
            t.Record("L2C0#0", detected: true);
            t.Record("L2C2#1", detected: true);
            Assert.AreEqual(4, t.PressCount);
            Assert.AreEqual(3, t.AnomalyCount);
        }

        [Test]
        public void PressWithNothingShowing_IsAPressButNotAnAnomaly()
        {
            // スイが「異状は検出されませんでした」と返す押下。押した事実は残し、異変には数えない。
            var t = new VisitorReportTally();
            Assert.IsFalse(t.Record("", detected: false));
            Assert.AreEqual(1, t.PressCount);
            Assert.AreEqual(0, t.AnomalyCount);
            Assert.AreEqual("", t.LastTakeId);
            Assert.IsFalse(t.LastCounted);
        }

        [Test]
        public void SuppressedTake_IsNotCounted()
        {
            // 演出は走っているが卓が画面を取っている（AnomalyShowing=false）。画に出ていないものは数えない。
            var t = new VisitorReportTally();
            Assert.IsFalse(t.Record("L2C1#0", detected: false));
            Assert.AreEqual(0, t.AnomalyCount);
            Assert.IsFalse(t.HasReported("L2C1#0"));
        }

        [Test]
        public void DetectedWithoutTakeId_IsNotCounted()
        {
            // 起きない組み合わせ（検出＝演出が走っている）だが、起きても幽霊を数えない。
            var t = new VisitorReportTally();
            Assert.IsFalse(t.Record(null, detected: true));
            Assert.IsFalse(t.Record("", detected: true));
            Assert.AreEqual(2, t.PressCount);
            Assert.AreEqual(0, t.AnomalyCount);
        }

        [Test]
        public void ClosingCut_ReportThenPressAgainDuringReturn_CountsOnce()
        {
            // 4 周目 A の締め: 報告で解除が通り（Released）、現実へ戻る 3 秒のあいだも同じ演出が走っている。
            // そこで押し直しても「同じ異変」。
            var t = new VisitorReportTally();
            Assert.IsTrue(t.Record("L4C0#0", detected: true));
            Assert.IsFalse(t.Record("L4C0#0", detected: true));
            Assert.AreEqual(1, t.AnomalyCount);
            Assert.AreEqual(2, t.PressCount);
        }

        [Test]
        public void Reset_ClearsEverything_AndTheSameTakeCountsAgainForTheNextVisitor()
        {
            var t = new VisitorReportTally();
            t.Record("L2C0#0", detected: true);
            t.Record("", detected: false);
            t.Reset();
            Assert.AreEqual(0, t.PressCount);
            Assert.AreEqual(0, t.AnomalyCount);
            Assert.AreEqual("", t.LastTakeId);
            Assert.IsFalse(t.LastCounted);
            Assert.IsFalse(t.HasReported("L2C0#0"));
            // 次の体験者は同じ演出を初めて見る。
            Assert.IsTrue(t.Record("L2C0#0", detected: true));
            Assert.AreEqual(1, t.AnomalyCount);
        }

        [Test]
        public void Reset_IsIdempotent()
        {
            var t = new VisitorReportTally();
            t.Reset();
            t.Reset();
            Assert.AreEqual(0, t.PressCount);
            Assert.AreEqual(0, t.AnomalyCount);
        }
    }
}
