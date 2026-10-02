#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class FixedCamContentBuildGuardTests
    {
        [Serializable]
        private sealed class Receipt
        {
            public int schema;
            public string policy = "";
            public string contentId = "";
            public string buildGuid = "";
            public string apkSha256 = "";
            public string builtAt = "";
        }

        private string root = "";
        private byte[] show = Array.Empty<byte>();
        private byte[] asset = Array.Empty<byte>();

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "FixedCamContentBuildGuardTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            show = Encoding.UTF8.GetBytes("{\"cues\":[]}");
            asset = Encoding.UTF8.GetBytes("baked image");
            File.WriteAllBytes(Path.Combine(root, "show.json"), show);
            File.WriteAllBytes(Path.Combine(root, "assets", "image.png"), asset);
            WriteManifest();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [Test]
        public void CompleteContent_Passes() => Validate();

        [Test]
        public void ModifiedShow_Fails()
        {
            File.AppendAllText(Path.Combine(root, "show.json"), " ");
            AssertInvalid();
        }

        [Test]
        public void ModifiedAsset_Fails()
        {
            File.WriteAllBytes(Path.Combine(root, "assets", "image.png"), Encoding.UTF8.GetBytes("baked photo"));
            AssertInvalid();
        }

        [Test]
        public void MissingAsset_Fails()
        {
            File.Delete(Path.Combine(root, "assets", "image.png"));
            AssertInvalid();
        }

        [TestCase("assets/../show.json")]
        [TestCase("/assets/image.png")]
        [TestCase("assets\\image.png")]
        public void EscapingOrAbsoluteAssetPath_Fails(string path)
        {
            WriteManifest(path);
            AssertInvalid();
        }

        [Test]
        public void DuplicateAssetPath_Fails()
        {
            string item = AssetJson("assets/image.png");
            WriteManifest(assets: item + "," + item);
            AssertInvalid();
        }

        [TestCase("")]
        [TestCase("{}")]
        public void EmptyOrDamagedManifest_Fails(string json)
        {
            File.WriteAllText(Path.Combine(root, "manifest.json"), json);
            AssertInvalid();
        }

        [Test]
        public void SuccessfulApk_WritesReceiptForExactBytes()
        {
            string apkPath = Path.Combine(root, "test.apk");
            File.WriteAllBytes(apkPath, Encoding.UTF8.GetBytes("APK bytes"));
            string buildGuid = Guid.NewGuid().ToString("N");
            string contentId = (string)InvokeGuard("Validate", root)!;

            InvokeGuard("WriteReceipt", apkPath, contentId, buildGuid);

            string receiptPath = apkPath + ".content.json";
            Receipt receipt = JsonUtility.FromJson<Receipt>(File.ReadAllText(receiptPath));
            Assert.That(receipt.schema, Is.EqualTo(1));
            Assert.That(receipt.policy, Is.EqualTo("baked-only-v1"));
            Assert.That(receipt.contentId, Is.EqualTo(contentId));
            Assert.That(receipt.buildGuid, Is.EqualTo(buildGuid));
            Assert.That(receipt.apkSha256, Is.EqualTo(Hash(File.ReadAllBytes(apkPath))));
            Assert.That(DateTime.TryParse(receipt.builtAt, out _), Is.True);

            File.AppendAllText(apkPath, "changed");
            Assert.That(receipt.apkSha256, Is.Not.EqualTo(Hash(File.ReadAllBytes(apkPath))));

            InvokeGuard("WriteReceipt", apkPath, contentId, buildGuid);
            Receipt replaced = JsonUtility.FromJson<Receipt>(File.ReadAllText(receiptPath));
            Assert.That(replaced.apkSha256, Is.EqualTo(Hash(File.ReadAllBytes(apkPath))));
            string[] previous = Directory.GetFiles(root, "test.apk.content.json.*.previous");
            Assert.That(previous, Has.Length.EqualTo(1));
            Receipt preserved = JsonUtility.FromJson<Receipt>(File.ReadAllText(previous[0]));
            Assert.That(preserved.apkSha256, Is.EqualTo(receipt.apkSha256));
        }

        [Test]
        public void FailedOrOtherAppBuild_IsIneligibleForReceipt()
        {
            string apkPath = Path.Combine(root, "test.apk");
            Assert.That(InvokeGuard("ShouldWriteReceipt", BuildResult.Failed, BuildTarget.Android,
                "com.roiril.mawarimi", apkPath), Is.EqualTo(false));
            Assert.That(InvokeGuard("ShouldWriteReceipt", BuildResult.Succeeded, BuildTarget.Android,
                "com.roiril.tableduo", apkPath), Is.EqualTo(false));
            Assert.That(InvokeGuard("ShouldWriteReceipt", BuildResult.Succeeded, BuildTarget.Android,
                "com.roiril.mawarimi", apkPath), Is.EqualTo(true));
            Assert.That(File.Exists(apkPath + ".content.json"), Is.False);
        }

        private void WriteManifest(string path = "assets/image.png", string? assets = null)
        {
            string json = "{\"schema\":1,\"policy\":\"baked-only-v1\",\"contentId\":\""
                          + new string('a', 64) + "\",\"configSha256\":\"" + Hash(show)
                          + "\",\"assets\":[" + (assets ?? AssetJson(path)) + "]}";
            File.WriteAllText(Path.Combine(root, "manifest.json"), json);
        }

        private string AssetJson(string path) => "{\"path\":\"" + path.Replace("\\", "\\\\")
                                                 + "\",\"sha256\":\"" + Hash(asset)
                                                 + "\",\"size\":" + asset.Length + "}";

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private void Validate()
            => InvokeGuard("Validate", root);

        private static object? InvokeGuard(string methodName, params object[] arguments)
        {
            Type? guard = Type.GetType("FixedCamVr.EditorTools.FixedCamContentBuildGuard, Assembly-CSharp-Editor");
            Assert.That(guard, Is.Not.Null);
            MethodInfo? method = guard!.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return method!.Invoke(null, arguments);
        }

        private void AssertInvalid()
        {
            var exception = Assert.Throws<TargetInvocationException>(Validate);
            Assert.That(exception!.InnerException, Is.TypeOf<BuildFailedException>());
        }
    }
}
