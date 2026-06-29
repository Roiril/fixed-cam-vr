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
            BuildVariant("廻リ視", "com.roiril.mawarimi", FixedCamScene, "mawarimi", development: true);

        [MenuItem("Tools/FixedCamVr/Build TableDuo APK", priority = 21)]
        public static void BuildTableDuo() =>
            BuildVariant("TableDuo", "com.roiril.tableduo", TableDuoScene, "tableduo", development: true);

        // リリース（提出・配布用）: Development Build なし。出力名に -release を付けて区別。
        [MenuItem("Tools/FixedCamVr/Build FixedCam APK（廻リ視・Release）", priority = 22)]
        public static void BuildFixedCamRelease() =>
            BuildVariant("廻リ視", "com.roiril.mawarimi", FixedCamScene, "mawarimi-release", development: false);

        [MenuItem("Tools/FixedCamVr/Build TableDuo APK（Release）", priority = 23)]
        public static void BuildTableDuoRelease() =>
            BuildVariant("TableDuo", "com.roiril.tableduo", TableDuoScene, "tableduo-release", development: false);

        // 実機ゼロ・MCP ゼロの検証用デスクトップビルド（Standalone Windows64）。
        // tdv_l0=on で起動すると HMD/XR 無しのフラット描画 + 合成 pose（FakeHandDriver）で動くので、
        // host/client/spectator を 127.0.0.1 で CLI 起動してマルチプレイ＋観戦を実機なしで検証できる。
        // batchmode から: Unity.exe -batchmode -quit -projectPath <proj> -executeMethod FixedCamVr.EditorTools.BuildVariants.BuildTableDuoDesktop
        [MenuItem("Tools/FixedCamVr/Diagnostics/Build TableDuo Desktop (L0 test)", priority = 240)]
        public static void BuildTableDuoDesktop()
        {
            string prevProduct = PlayerSettings.productName;
            var prevGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            var prevTarget = EditorUserBuildSettings.activeBuildTarget;
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                {
                    EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
                }
                PlayerSettings.productName = "TableDuo";

                var opts = new BuildPlayerOptions
                {
                    scenes = new[] { TableDuoScene },
                    target = BuildTarget.StandaloneWindows64,
                    locationPathName = "Builds/tableduo-desktop/TableDuo.exe",
                    options = BuildOptions.Development,
                };
                BuildReport report = BuildPipeline.BuildPlayer(opts);
                var s = report.summary;
                if (s.result == BuildResult.Succeeded)
                {
                    Debug.Log($"[BuildVariants] DESKTOP OK -> {s.outputPath} " +
                              $"({s.totalSize / (1024 * 1024)}MB, {s.totalTime.TotalSeconds:F0}s)");
                }
                else
                {
                    Debug.LogError($"[BuildVariants] DESKTOP 失敗: result={s.result} errors={s.totalErrors}");
                }
            }
            finally
            {
                PlayerSettings.productName = prevProduct;
                if (EditorUserBuildSettings.activeBuildTarget != prevTarget)
                {
                    EditorUserBuildSettings.SwitchActiveBuildTarget(prevGroup, prevTarget);
                }
            }
        }

        private static void BuildVariant(string productName, string packageId, string scenePath, string outName,
            bool development)
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
                    // 普段は Development Build（logcat 確認用、unity-vr.md）。Release メニューは外す。
                    options = development ? BuildOptions.Development : BuildOptions.None,
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
