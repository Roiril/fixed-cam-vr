#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FixedCamVr.EditorTools
{
    /// <summary>廻リ視の同梱 show と素材が manifest に一致する場合だけ Android ビルドを通す。</summary>
    public sealed class FixedCamContentBuildGuard : IPreprocessBuildWithReport
    {
        [Serializable]
        private sealed class Manifest
        {
            public int schema;
            public string policy = "";
            public string contentId = "";
            public string configSha256 = "";
            public Asset[]? assets;
        }

        [Serializable]
        private sealed class Asset
        {
            public string path = "";
            public string sha256 = "";
            public long size;
        }

        [Serializable]
        private sealed class Receipt
        {
            public int schema = 1;
            public string policy = "baked-only-v1";
            public string contentId = "";
            public string buildGuid = "";
            public string apkSha256 = "";
            public string builtAt = "";
        }

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android
                || PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android) != "com.roiril.mawarimi")
                return;

            Validate(Path.Combine(Application.streamingAssetsPath, "show"));
        }

        // BuildPlayer が戻る前の postprocess では result が未確定。成功確定後に BuildVariants が呼ぶ。
        public static void RecordSuccessfulBuild(BuildReport report)
        {
            BuildSummary summary = report.summary;
            if (!ShouldWriteReceipt(summary.result, summary.platform,
                    PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android), summary.outputPath))
                return;

            string contentId = Validate(Path.Combine(Application.streamingAssetsPath, "show"));
            WriteReceipt(summary.outputPath, contentId, summary.guid.ToString());
        }

        public static bool ShouldWriteReceipt(BuildResult result, BuildTarget target, string packageId, string outputPath)
            => result == BuildResult.Succeeded && target == BuildTarget.Android
               && packageId == "com.roiril.mawarimi"
               && string.Equals(Path.GetExtension(outputPath), ".apk", StringComparison.OrdinalIgnoreCase);

        public static void WriteReceipt(string apkPath, string contentId, string buildGuid)
        {
            if (!IsSha256(contentId) || string.IsNullOrEmpty(buildGuid))
                Fail("receipt の contentId または buildGuid が不正です");
            if (!File.Exists(apkPath)) Fail($"APK が見つかりません: {apkPath}");

            string receiptPath = apkPath + ".content.json";
            string temporaryPath = receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var receipt = new Receipt
                {
                    contentId = contentId,
                    buildGuid = buildGuid,
                    apkSha256 = HashFile(apkPath).ToLowerInvariant(),
                    builtAt = DateTime.UtcNow.ToString("o"),
                };
                File.WriteAllText(temporaryPath, JsonUtility.ToJson(receipt), new UTF8Encoding(false));
                if (File.Exists(receiptPath)) File.Replace(temporaryPath, receiptPath, null);
                else File.Move(temporaryPath, receiptPath);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException($"receipt の記録に失敗しました: {exception.Message}");
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public static string Validate(string root)
        {
            try
            {
                return ValidateFiles(root);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException($"同梱 show の検証に失敗しました: {exception.Message}");
            }
        }

        private static string ValidateFiles(string root)
        {
            if (string.IsNullOrEmpty(root)) Fail("show ディレクトリが指定されていません");
            string showPath = Path.Combine(root, "show.json");
            string manifestPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(showPath) || !File.Exists(manifestPath))
                Fail("show.json または manifest.json がありません");

            string json = File.ReadAllText(manifestPath);
            if (string.IsNullOrWhiteSpace(json)) Fail("manifest.json が空です");
            Manifest? manifest;
            try { manifest = JsonUtility.FromJson<Manifest>(json); }
            catch (Exception) { throw new BuildFailedException("manifest.json が不正です"); }
            if (manifest == null || manifest.schema != 1 || manifest.policy != "baked-only-v1"
                || !IsSha256(manifest.contentId) || !IsSha256(manifest.configSha256)
                || manifest.assets == null || manifest.assets.Length == 0)
                throw new BuildFailedException("同梱 show の検証に失敗しました: manifest.json の schema、policy、hash または素材一覧が不正です");

            if (!HashMatches(showPath, manifest.configSha256)) Fail("show.json の SHA256 が一致しません");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            foreach (Asset? asset in manifest.assets)
            {
                if (asset == null || asset.size < 0 || !IsSha256(asset.sha256)
                    || !SafeAssetPath(asset.path) || !paths.Add(asset.path))
                    throw new BuildFailedException("同梱 show の検証に失敗しました: manifest.json の素材項目が不正です");

                string fullPath = Path.GetFullPath(Path.Combine(root, asset.path.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(fullPath))
                    Fail($"素材が見つからないか show の外を参照しています: {asset.path}");
                if (new FileInfo(fullPath).Length != asset.size || !HashMatches(fullPath, asset.sha256))
                    Fail($"素材のサイズまたは SHA256 が一致しません: {asset.path}");
            }
            return manifest.contentId;
        }

        private static bool SafeAssetPath(string? path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("assets/", StringComparison.Ordinal)
                || Path.IsPathRooted(path) || path.Contains("\\") || path.Contains(":")
                || path.Contains("?") || path.Contains("#")) return false;
            foreach (string part in path.Split('/'))
                if (part.Length == 0 || part == "." || part == ".."
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return true;
        }

        private static bool IsSha256(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        private static bool HashMatches(string path, string expected)
            => string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase);

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static void Fail(string reason) => throw new BuildFailedException($"同梱 show の検証に失敗しました: {reason}");
    }
}
