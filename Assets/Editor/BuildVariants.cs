#nullable enable
using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FixedCamVr.EditorTools
{
    /// <summary>
    /// 廻リ視の APK をビルドするメニュー。
    /// productName / applicationIdentifier をビルド時だけ廻リ視のものに切り替え、
    /// 終了後に PlayerSettings を必ず元へ戻す（ProjectSettings をコミットしない運用と整合）。
    ///
    /// 2026-09-28 まで TableDuo / MyCobotHand が同居しており、変種ごとに切り替える形はその名残。
    /// 手のアプリは table-duo-vr リポジトリへ分離した。
    /// 出力: Builds/&lt;name&gt;.apk（gitignore 対象）
    /// </summary>
    public static class BuildVariants
    {
        private const string FixedCamScene = "Assets/Scenes/Main.unity";

        [MenuItem("Tools/FixedCamVr/Build FixedCam APK（廻リ視）", priority = 20)]
        public static void BuildFixedCam() =>
            BuildVariant("廻リ視", "com.roiril.mawarimi", FixedCamScene, "mawarimi", development: true);

        // リリース（提出・配布用）: Development Build なし。出力名に -release を付けて区別。
        [MenuItem("Tools/FixedCamVr/Build FixedCam APK（廻リ視・Release）", priority = 22)]
        public static void BuildFixedCamRelease() =>
            BuildVariant("廻リ視", "com.roiril.mawarimi", FixedCamScene, "mawarimi-release", development: false);

        private static void BuildVariant(string productName, string packageId, string scenePath, string outName,
            bool development)
        {
            // ⚠ Android APK は「アクティブプラットフォーム = Android」でしかビルドしない（2026-07-24 実害）。
            // Standalone アクティブのままのクロスターゲット一発ビルドは Oculus XR プラグインの
            // マニフェスト注入（com.oculus.intent.category.VR / focusaware）が落ち、
            // Quest 上で 2D パネルとして起動する APK が焼ける（HMD にシーンが出ない）。
            // 切替 + 直後の BuildPlayer は「scripts are compiling」で必ず失敗する（2026-07-24 実害）ため、
            // ここでは切替の開始だけ行い中断する。切替完了後にもう一度メニューを実行すること。
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.LogError("[BuildVariants] 中断: アクティブプラットフォームが Android ではありません" +
                               $"（現在: {EditorUserBuildSettings.activeBuildTarget}）。Android への切替を開始しました — " +
                               "切替完了後にもう一度このメニューを実行してください");
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                return;
            }
            if (EditorApplication.isCompiling)
            {
                Debug.LogError("[BuildVariants] 中断: スクリプトコンパイル中。完了後にもう一度実行してください");
                return;
            }

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
                    FixedCamContentBuildGuard.RecordSuccessfulBuild(report);
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
