#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    [Serializable] public sealed class BakedShowManifest
    {
        [Serializable] public sealed class Asset
        {
            public string path = "";
            public string sha256 = "";
            public long size;
        }

        public int schema;
        public string policy = "";
        public string contentId = "";
        public string configSha256 = "";
        public Asset[]? assets;

        public const string Policy = "baked-only-v1";

        public static bool TryVerify(byte[] configBytes, string manifestJson, out string contentId)
        {
            contentId = "";
            if (configBytes == null || string.IsNullOrEmpty(manifestJson)) return false;
            BakedShowManifest? manifest;
            try { manifest = JsonUtility.FromJson<BakedShowManifest>(manifestJson); }
            catch { return false; }
            if (manifest == null || manifest.schema != 1 || manifest.policy != Policy
                || !IsSha256(manifest.contentId) || !IsSha256(manifest.configSha256)
                || manifest.assets == null) return false;
            using var sha = SHA256.Create();
            string actual = BitConverter.ToString(sha.ComputeHash(configBytes)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, manifest.configSha256, StringComparison.OrdinalIgnoreCase)) return false;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (Asset? asset in manifest.assets)
            {
                if (asset == null || asset.size < 0 || !IsSha256(asset.sha256)
                    || string.IsNullOrEmpty(asset.path) || !asset.path.StartsWith("assets/", StringComparison.Ordinal)
                    || string.IsNullOrEmpty(ShowAssetResolver.ResolveBakedOnly("sa://" + asset.path))
                    || !paths.Add(asset.path)) return false;
            }
            contentId = manifest.contentId;
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
    }
}
