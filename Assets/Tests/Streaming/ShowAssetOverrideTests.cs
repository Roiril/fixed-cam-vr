#nullable enable
using System.IO;
using FixedCamVr.Streaming;
using NUnit.Framework;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// Editor の検証だけが使う素材の差し替え（<see cref="ShowAssetResolver.OverrideDirEnv"/>）。
    /// ツインの部屋で作り直した合成素材を、本番の show.json を書き換えずに流す。
    /// </summary>
    public class ShowAssetOverrideTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "fixedcam-override-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(Path.Combine(_dir, "gen_handsB.png"), new byte[] { 1 });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Test]
        public void 同じ名前のファイルがあればそれを読む()
        {
            Assert.IsTrue(ShowAssetResolver.TryOverride(_dir, "gen_handsB.png", out string uri));
            StringAssert.StartsWith("file://", uri);
            StringAssert.EndsWith("/gen_handsB.png", uri);
            Assert.IsFalse(uri.Contains("\\"));
        }

        [Test]
        public void 無い名前と空のフォルダは差し替えない()
        {
            Assert.IsFalse(ShowAssetResolver.TryOverride(_dir, "stain.png", out _));
            Assert.IsFalse(ShowAssetResolver.TryOverride("", "gen_handsB.png", out _));
            Assert.IsFalse(ShowAssetResolver.TryOverride(null, "gen_handsB.png", out _));
        }

        /// <summary>差し替え先の外へ出る名前は読まない（sa:// の名前に区切りが混ざっても、フォルダの外を開かない）。</summary>
        [Test]
        public void 区切りを含む名前は読まない()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "sub"));
            File.WriteAllBytes(Path.Combine(_dir, "sub", "x.png"), new byte[] { 1 });
            Assert.IsFalse(ShowAssetResolver.TryOverride(_dir, "sub/x.png", out _));
            Assert.IsFalse(ShowAssetResolver.TryOverride(Path.Combine(_dir, "sub"), "../gen_handsB.png", out _));
        }
    }
}
