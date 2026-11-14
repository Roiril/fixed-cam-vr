#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Streaming.Replay;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 体験の記録（<c>xprec/&lt;日時&gt;/</c>）の刈り込み。上限以内なら何も消さない・超えたら古い順に必要な分だけ・
    /// いま書いている記録は消さない・日時名でないもの（印 ON など）は数えず触らない、を固定する。
    /// </summary>
    public sealed class XpRecPruneTests
    {
        private const long Mb = 1024L * 1024;

        private static XpRecPrune.Entry E(string name, long mb) => new XpRecPrune.Entry(name, mb * Mb);

        [Test]
        public void WithinLimit_DeletesNothing()
        {
            var entries = new[] { E("20261101_100000", 1000), E("20261101_110000", 1000), E("20261101_120000", 1000) };
            Assert.That(XpRecPrune.Plan(entries, "20261101_120000", 4096 * Mb), Is.Empty);
            Assert.That(XpRecPrune.Plan(entries, null, 3000 * Mb), Is.Empty, "ちょうど上限は超えていない");
        }

        [Test]
        public void OverLimit_DeletesOldestFirst_OnlyAsManyAsNeeded()
        {
            // 並びは古い順でなくてよい。名前（日時）の順で古いものから選ぶ。
            var entries = new[]
            {
                E("20261101_120000", 1000), E("20261101_090000", 1500), E("20261101_100000", 1000),
                E("20261101_110000", 1000), E("20261101_130000", 10),
            };
            // 合計 4510MB。上限 3000MB へは 09:00（1500）と 10:00（1000）を消せば 2010MB で足りる。
            List<string> victims = XpRecPrune.Plan(entries, "20261101_130000", 3000 * Mb);
            Assert.That(victims, Is.EqualTo(new[] { "20261101_090000", "20261101_100000" }));
        }

        [Test]
        public void CurrentRecording_IsNeverDeleted_EvenOverLimit()
        {
            var entries = new[] { E("20261101_090000", 500), E("20261101_100000", 5000) };
            List<string> victims = XpRecPrune.Plan(entries, "20261101_100000", 4096 * Mb);
            Assert.That(victims, Is.EqualTo(new[] { "20261101_090000" }), "古いものは全部消しても、書いている記録は残す");
        }

        [Test]
        public void NonStampNames_AreNeitherCountedNorDeleted()
        {
            var entries = new[]
            {
                E("ON", 9000), E("notes", 9000), E("20261101_10000", 9000), E("2026110x_100000", 9000),
                E("20261301_100000", 9000), E("20261101_100000_old", 9000), E("20261101_100000", 100),
            };
            Assert.That(XpRecPrune.Plan(entries, null, 4096 * Mb), Is.Empty, "日時名でないものは合計に数えない");
            Assert.That(XpRecPrune.Plan(entries, null, 0), Is.EqualTo(new[] { "20261101_100000" }));
            Assert.IsTrue(XpRecPrune.IsStampName("20261023_183012"));
            Assert.IsFalse(XpRecPrune.IsStampName("ON"));
            Assert.IsFalse(XpRecPrune.IsStampName(null));
        }

        [Test]
        public void Execute_DeletesOldFoldersOnDisk_AndLeavesTheMarkerAndUnknownNames()
        {
            string root = Path.Combine(Path.GetTempPath(), "fcv-xprec-prune-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "ON"), "on");
                Write(root, "keep_me", 4000);
                Write(root, "20261101_090000", 3000);
                Write(root, "20261101_100000", 3000);
                Write(root, "20261101_110000", 1000);

                // 日時名の合計 7000 byte（keep_me は数えない）。上限 3000 へは 09:00 と 10:00 の 2 本を消す。
                XpRecPrune.Result r = XpRecPrune.Execute(root, "20261101_110000", 3000);

                Assert.That(r.Deleted, Is.EqualTo(2));
                Assert.That(r.FreedBytes, Is.EqualTo(6000));
                Assert.That(r.Failed, Is.Zero);
                Assert.That(r.RemainingBytes, Is.EqualTo(1000));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "20261101_090000")));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "20261101_100000")));
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "20261101_110000")));
                Assert.IsTrue(Directory.Exists(Path.Combine(root, "keep_me")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "ON")));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* 一時フォルダ */ }
            }
        }

        [Test]
        public void Execute_WithoutRoot_DoesNothing()
        {
            string root = Path.Combine(Path.GetTempPath(), "fcv-xprec-missing-" + Guid.NewGuid().ToString("N"));
            XpRecPrune.Result r = XpRecPrune.Execute(root, null, 0);
            Assert.That(r.Deleted, Is.Zero);
            Assert.That(r.FreedBytes, Is.Zero);
        }

        private static void Write(string root, string name, int bytes)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "cam_Phone01.mjr"), new byte[bytes]);
        }
    }
}
