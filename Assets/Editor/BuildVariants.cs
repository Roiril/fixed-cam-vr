#nullable enable
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FixedCamVr.EditorTools
{
    /// <summary>
    /// 同居 2 アプリ（廻リ視 = FixedCam / TableDuo）を別アプリとしてビルドするためのメニュー。
    /// productName / applicationIdentifier / シーンを変種ごとに切り替えて APK を出し、
    /// 終了後に PlayerSettings を必ず元へ戻す（ProjectSettings をコミットしない運用と整合）。
    ///
    /// パッケージ ID が異なるので Quest 上では 2 つの独立したアプリとして並存する。
    /// 出力: Builds/&lt;name&gt;.apk（gitignore 対象）
    /// </summary>
    public static class BuildVariants
    {
        private const string FixedCamScene = "Assets/Scenes/Main.unity";
        private const string TableDuoScene = "Assets/TableDuo/Scenes/TableDuoMain.unity";

        [MenuItem("Tools/FixedCamVr/Build FixedCam APK（廻リ視）", priority = 20)]
        public static void BuildFixedCam() =>
            BuildVariant("廻リ視", "com.roiril.mawarimi", FixedCamScene, "mawarimi");

        [MenuItem("Tools/FixedCamVr/Build TableDuo APK", priority = 21)]
        public static void BuildTableDuo() =>
            BuildVariant("TableDuo", "com.roiril.tableduo", TableDuoScene, "tableduo");

        private static void BuildVariant(string productName, string packageId, string scenePath, string outName)
        {
            string prevProduct = PlayerSettings.productName;
            string prevId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
            try
            {
                PlayerSettings.productName = productName;
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, packageId);

                var opts = new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    target = BuildTarget.Android,
                    locationPathName = $"Builds/{outName}.apk",
                    // 初期デプロイは Development Build（unity-vr.md）。リリース時はここを外す。
                    options = BuildOptions.Development,
                };
                BuildReport report = BuildPipeline.BuildPlayer(opts);
                var summary = report.summary;
                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log($"[BuildVariants] OK: {productName} ({packageId}) -> {summary.outputPath} " +
                              $"({summary.totalSize / (1024 * 1024)}MB, {summary.totalTime.TotalSeconds:F0}s)\n" +
                              $"install: adb install -r \"{summary.outputPath}\"");
                }
                else
                {
                    Debug.LogError($"[BuildVariants] 失敗: {productName} result={summary.result} " +
                                   $"errors={summary.totalErrors}");
                }
            }
            finally
            {
                // ProjectSettings をビルド前の状態へ戻す（差分を作らない）
                PlayerSettings.productName = prevProduct;
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, prevId);
            }
        }
    }
}
