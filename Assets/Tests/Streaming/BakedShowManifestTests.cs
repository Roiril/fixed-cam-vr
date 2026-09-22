#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class BakedShowManifestTests
    {
        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        [Test]
        public void RawShowBytesMustMatchManifest()
        {
            byte[] config = Encoding.UTF8.GetBytes("{\"cameras\":[]}");
            var manifest = new BakedShowManifest
            {
                schema = 1,
                policy = BakedShowManifest.Policy,
                contentId = new string('a', 64),
                configSha256 = Hash(config),
                assets = new[] { new BakedShowManifest.Asset { path = "assets/photo.png", sha256 = new string('b', 64), size = 12 } },
            };
            string json = JsonUtility.ToJson(manifest);
            Assert.That(BakedShowManifest.TryVerify(config, json, out string id), Is.True);
            Assert.That(id, Is.EqualTo(manifest.contentId));
            Assert.That(BakedShowManifest.TryVerify(Encoding.UTF8.GetBytes("{\"cameras\":[ ]}"), json, out _), Is.False);
            manifest.assets![0].path = "assets/写真 1.png";
            Assert.That(BakedShowManifest.TryVerify(config, JsonUtility.ToJson(manifest), out _), Is.True);
            manifest.policy = "live";
            Assert.That(BakedShowManifest.TryVerify(config, JsonUtility.ToJson(manifest), out _), Is.False);
            manifest.policy = BakedShowManifest.Policy;
            manifest.assets![0].path = "assets/../secret.png";
            Assert.That(BakedShowManifest.TryVerify(config, JsonUtility.ToJson(manifest), out _), Is.False);
        }

        [Test]
        public void ExternalAndEscapingAssetsAreRejected()
        {
            Assert.That(ShowAssetResolver.ResolveBakedOnly("https://example.com/a.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("/masks/a.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("file:///tmp/a.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("slot://photo"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets/../secret.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets/%2e%2e/secret.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets%2fsecret.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets/%252e%252e/secret.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets\\photo.png"), Is.Empty);
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets/photo.png"), Does.EndWith("/show/assets/photo.png"));
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets/%E5%86%99%E7%9C%9F%201.png"),
                Does.EndWith("/show/assets/%E5%86%99%E7%9C%9F%201.png"));
            Assert.That(ShowAssetResolver.ResolveBakedOnly("sa://assets/写真 1.png"),
                Does.EndWith("/show/assets/%E5%86%99%E7%9C%9F%201.png"));
        }
    }
}
