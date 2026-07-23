#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ビルド時に BuildVariants が焼き込むビルド情報の読み取り（Resources/TableDuoBuildInfo.txt）。
    /// 1 行目 = TableDuoMain.unity の MD5（sceneHash）。host/client でシーンの NetworkObject 構成が
    /// 食い違うと RPC が黙って捨てられる（2026-07-10 実害: 駒が掴めない）ため、
    /// HostBeacon/HostDiscovery がこれを照合して不一致を警告する。
    /// Editor 実行やスタンプ前のビルドでは空文字（照合スキップ＝従来動作）。
    /// </summary>
    public static class TableDuoBuildInfo
    {
        public const string ResourceName = "TableDuoBuildInfo";

        private static string? _sceneHash;

        public static string SceneHash
        {
            get
            {
                if (_sceneHash == null)
                {
                    var asset = Resources.Load<TextAsset>(ResourceName);
                    string raw = asset != null ? asset.text : "";
                    _sceneHash = ParseHash(raw);
                }
                return _sceneHash;
            }
        }

        /// <summary>
        /// 焼き込みテキストから sceneHash を取り出す（BOM 除去 → trim → 先頭行 → trim）。
        /// UTF-8 BOM(U+FEFF) は Trim では落ちないので明示除去（host/client でエンコード差が出ても一致させる）。
        /// </summary>
        public static string ParseHash(string raw) =>
            raw.Replace("﻿", "").Trim().Split('\n')[0].Trim();
    }
}
