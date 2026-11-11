#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// 再生・ツイン走行の保存先（productName）は木ごとに分かれる。
    /// 同じだと、別の worktree で同時に走る 2 本が同じ xprec/ を消し合う（2026-11-11）。
    /// Editor の asmdef はテストから参照していないので、名前で引く。
    /// </summary>
    public sealed class ReplayProductNameTests
    {
        private static string NameFor(string projectDir)
        {
            Type? t = Type.GetType("FixedCamVr.Streaming.EditorTools.Replay.ReplayXrGuard, FixedCamVr.Streaming.Editor");
            Assert.IsNotNull(t, "ReplayXrGuard が見つからない（asmdef か名前空間が変わった）");
            MethodInfo? m = t!.GetMethod("ProductNameFor", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(m, "ReplayXrGuard.ProductNameFor が無い");
            return (string)m!.Invoke(null, new object[] { projectDir })!;
        }

        [Test]
        public void MainTree_KeepsTheOriginalName()
        {
            Assert.AreEqual("mawarimi-replay", NameFor(@"C:\Users\x\Projects\Unity\fixed-cam-vr"));
            Assert.AreEqual("mawarimi-replay", NameFor("C:/Users/x/Projects/Unity/fixed-cam-vr/"));
        }

        [Test]
        public void AnotherTree_GetsItsOwnName()
        {
            Assert.AreEqual("mawarimi-replay-fcv-twin-tree", NameFor(@"C:\Users\x\Projects\Unity\fcv-twin-tree"));
            Assert.AreNotEqual(NameFor(@"C:\a\agent-1"), NameFor(@"C:\a\agent-2"));
        }

        [Test]
        public void NonAscii_IsReplaced_BecauseMrukFailsOnIt()
        {
            string name = NameFor(@"C:\作業\廻リ視 tree");
            foreach (char c in name) Assert.Less(c, (char)128, name);
            Assert.That(name, Does.StartWith("mawarimi-replay-"));
        }
    }
}
