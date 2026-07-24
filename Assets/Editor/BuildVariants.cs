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
        private const string MyCobotHandScene = "Assets/MyCobotHand/Scenes/HandTeleop.unity";

        [MenuItem("Tools/FixedCamVr/Build FixedCam APK（廻リ視）", priority = 20)]
        public static void BuildFixedCam() =>
            BuildVariant("廻リ視", "com.roiril.mawarimi", FixedCamScene, "mawarimi", development: true);

        [MenuItem("Tools/FixedCamVr/Build TableDuo APK", priority = 21)]
        public static void BuildTableDuo() =>
            BuildVariant("TableDuo", "com.roiril.tableduo", TableDuoScene, "tableduo", development: true);

        // ロボットハンド操作 VR（第3のアプリ = mycobot-lab ハンドのテレオペ）。
        // 右手ハンドトラッキングの curl を PC のハンドサーバ（localhost:8001）へストリームする。
        // package: com.mycobot.handteleop / productName: ロボットハンド操作VR
        [MenuItem("Tools/FixedCamVr/Build MyCobotHand APK（ロボットハンド操作VR）", priority = 24)]
        public static void BuildMyCobotHand() =>
            BuildVariant("ロボットハンド操作VR", "com.mycobot.handteleop", MyCobotHandScene, "mycobothand", development: false);

        [MenuItem("Tools/FixedCamVr/Build MyCobotHand APK（ロボットハンド操作VR・Dev）", priority = 25)]
        public static void BuildMyCobotHandDev() =>
            BuildVariant("ロボットハンド操作VR", "com.mycobot.handteleop", MyCobotHandScene, "mycobothand-dev", development: true);

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
            // ⚠ ここで手動 SwitchActiveBuildTarget しない（2026-07-24 実害×2）。
            // 非バッチの Editor では切替が script 再コンパイルを予約し、直後の BuildPlayer が
            // 「Error building Player because scripts are compiling」で必ず失敗する。
            // さらに旧実装は finally で Android へ戻していたため、再実行しても同じ競合を無限に再現した。
            // APK 経路（BuildVariant）と同じく target 切替は BuildPlayer 内部に任せるのが正
            // （BuildPlayer は切替とコンパイルをビルドの一部として同期処理する）。
            // ビルド後の active target は Standalone のまま残る（APK 経路が Android のまま残すのと同じ流儀。
            // EditorUserBuildSettings は Library 管理で git 差分にはならない）。
            if (EditorApplication.isCompiling)
            {
                Debug.LogError("[BuildVariants] DESKTOP 中断: スクリプトコンパイル中。完了後にもう一度実行してください");
                return;
            }
            string prevProduct = PlayerSettings.productName;
            try
            {
                StampTableDuoBuildInfo();
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
            }
        }

        /// <summary>
        /// TableDuo のシーンハッシュを Resources へ焼き込む（ビルド前に呼ぶ）。
        /// HostBeacon/HostDiscovery が host/client のシーン構成一致を照合するのに使う
        /// （不一致だと NetworkObjectId がズレて RPC が黙って捨てられる — 2026-07-10 実害の恒久化）。
        /// APK / desktop の両ビルドが同じシーンから出ていれば同じハッシュになる。
        /// </summary>
        private static void StampTableDuoBuildInfo()
        {
            const string outPath = "Assets/TableDuo/Resources/TableDuoBuildInfo.txt";
            byte[] scene = System.IO.File.ReadAllBytes(TableDuoScene);
            using var md5 = System.Security.Cryptography.MD5.Create();
            string hash = BitConverter.ToString(md5.ComputeHash(scene)).Replace("-", "").Substring(0, 16);
            System.IO.File.WriteAllText(outPath, hash + "\n", new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(outPath);
            Debug.Log($"[BuildVariants] TableDuoBuildInfo stamped: sceneHash={hash}");
        }

        private static void BuildVariant(string productName, string packageId, string scenePath, string outName,
            bool development)
        {
            // ⚠ Android APK は「アクティブプラットフォーム = Android」でしかビルドしない（2026-07-24 実害）。
            // Standalone アクティブのままのクロスターゲット一発ビルドは Oculus XR プラグインの
            // マニフェスト注入（com.oculus.intent.category.VR / focusaware）が落ち、
            // Quest 上で 2D パネルとして起動する APK が焼ける（HMD にシーンが出ない）。
            // 切替 + 直後の BuildPlayer は「scripts are compiling」で必ず失敗する（desktop 経路の教訓）ため、
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
                if (scenePath == TableDuoScene) StampTableDuoBuildInfo();
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
