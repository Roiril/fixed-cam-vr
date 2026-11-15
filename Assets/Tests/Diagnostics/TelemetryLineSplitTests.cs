#nullable enable
using System.Linq;
using System.Text;
using FixedCamVr.Diagnostics;
using NUnit.Framework;

namespace FixedCamVr.Diagnostics.Tests
{
    public sealed class TelemetryLineSplitTests
    {
        private static string LongLine(int keys)
        {
            var sb = new StringBuilder("[XP] t=12.34 ev=sum");
            for (int i = 0; i < keys; i++) sb.Append(" key").Append(i).Append("=").Append(i * 0.5f);
            sb.Append(" |c0 con=1 rx=30.0 |c1 con=1 rx=29.5");
            return sb.ToString();
        }

        [Test]
        public void ShortLine_IsLeftAlone()
        {
            var parts = TelemetryLineSplit.Split("[XP]", "[XP] t=1.00 ev=boot");
            Assert.That(parts, Is.EqualTo(new[] { "[XP] t=1.00 ev=boot" }));
        }

        [Test]
        public void LongLine_EveryPartFitsUnderTheLogcatLimit()
        {
            var parts = TelemetryLineSplit.Split("[XP]", LongLine(300));
            Assert.That(parts.Count, Is.GreaterThan(1));
            foreach (string p in parts)
                Assert.That(Encoding.UTF8.GetByteCount(p), Is.LessThanOrEqualTo(TelemetryLineSplit.MaxLineBytes), p);
        }

        [Test]
        public void LongLine_ContinuationsCarryTheMark_AndJoinBackToTheSameTokens()
        {
            string line = LongLine(300);
            var parts = TelemetryLineSplit.Split("[XP]", line);
            Assert.That(parts[0], Does.StartWith("[XP] t=12.34 ev=sum"));
            foreach (string p in parts.Skip(1)) Assert.That(p, Does.StartWith("[XP] + "));
            string joined = parts[0] + string.Concat(parts.Skip(1).Select(p => " " + p.Substring("[XP] + ".Length)));
            Assert.That(joined, Is.EqualTo(line));
        }

        [Test]
        public void JapaneseIsCountedInBytes()
        {
            var sb = new StringBuilder("[XP] t=1.00 ev=note");
            for (int i = 0; i < 200; i++) sb.Append(" 字=あい");
            var parts = TelemetryLineSplit.Split("[XP]", sb.ToString());
            foreach (string p in parts)
                Assert.That(Encoding.UTF8.GetByteCount(p), Is.LessThanOrEqualTo(TelemetryLineSplit.MaxLineBytes));
        }
    }
}
