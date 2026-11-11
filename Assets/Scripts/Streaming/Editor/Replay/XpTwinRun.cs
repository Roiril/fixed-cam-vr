#nullable enable
using System;
using System.Globalization;
using System.IO;
using FixedCamVr.Streaming.Replay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.Replay
{
    /// <summary>
    /// <b>ツイン走行</b>（HMD も Quest も要らない本番の通し）。Main シーンの Play に入れて出るだけで、
    /// 流すのは Assembly-CSharp の <c>XpTwinDriver</c>。入口は <c>py -3.11 tools/twin-verify.py</c>。
    ///
    /// CLI: <c>.\tools\unity.ps1 twin-run -Set pack=&lt;束&gt;,out=&lt;フォルダ&gt;</c>。
    /// 値: pack（<c>tools/twin/make_pack.py</c> の出力）・out・speed（m/s。既定 0.25）・dwell（区間の真ん中で止まる秒。既定 6）・
    /// nomark=1（報告しない）・relief=1（ホラー軽減）・calib=show（ツインの配置へ差し替えない）・
    /// marks=1B.2A.3B（周の中でも報告する区間。各区間に入って 2.5 秒後に 1 回）・
    /// capture=eye（走行の Play の中で目線の動画を撮り、out へ eye.mp4 と replay_xp.log を書く）。
    /// capture=eye の撮り方は replay と同じ値で渡せる: fov（縦の度。既定 52）・w・h・fps・msaa・eyecam。
    /// </summary>
    [InitializeOnLoad]
    public static class XpTwinRun
    {
        private const string KeyActive = "fcv.twin.active";
        // XpTwinDriver の定数と同じ値（Assembly-CSharp は Editor の asmdef から参照できない）。
        private const string KeyPack = "fcv.twin.pack";
        private const string KeyOut = "fcv.twin.out";
        private const string KeyDone = "fcv.twin.done";
        private const string KeyResult = "fcv.twin.result";
        private const string KeyCapture = "fcv.twin.capture";
        private const string MainScene = "Assets/Scenes/Main.unity";

        static XpTwinRun()
        {
            if (!SessionState.GetBool(KeyActive, false)) return;
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.playModeStateChanged += OnState;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Twin Run (full show without HMD)", priority = 246)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[Twin] Play 中は始められない");
                Exit(2);
                return;
            }
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string pack = EditorCliArgs.Get("pack") ?? Path.Combine(projectRoot, "Logs", "twin-run", "pack");
            if (!File.Exists(Path.Combine(pack, "pack.json")))
            {
                Debug.LogError($"[Twin] 束が無い: {pack}（py -3.11 tools/twin/make_pack.py）");
                Exit(2);
                return;
            }
            string outDir = EditorCliArgs.Get("out") ?? Path.Combine(projectRoot, "Logs", "twin-run", "last");
            if (!XpCaptureLogic.TryParseTwinCapture(EditorCliArgs.Get("capture"), out XpCaptureLogic.TwinCapture capture, out string why))
            {
                Debug.LogError("[Twin] " + why);
                Exit(2);
                return;
            }

            SessionState.SetString(KeyPack, Path.GetFullPath(pack));
            SessionState.SetString(KeyOut, Path.GetFullPath(outDir));
            SessionState.SetFloat("fcv.twin.speed", ArgFloat("speed", 0.25f));
            SessionState.SetFloat("fcv.twin.dwell", ArgFloat("dwell", 6f));
            SessionState.SetInt("fcv.twin.nomark", ArgFloat("nomark", 0f) != 0f ? 1 : 0);
            SessionState.SetInt("fcv.twin.relief", ArgFloat("relief", 0f) != 0f ? 1 : 0);
            SessionState.SetInt("fcv.twin.fast", ArgFloat("fast", 0f) != 0f ? 1 : 0);
            SessionState.SetString("fcv.twin.marks", EditorCliArgs.Get("marks") ?? "");
            SessionState.SetInt("fcv.twin.showcalib", EditorCliArgs.Get("calib") == "show" ? 1 : 0);
            SessionState.SetString(KeyCapture, capture == XpCaptureLogic.TwinCapture.Eye ? "eye" : "");
            if (capture == XpCaptureLogic.TwinCapture.Eye) SetCaptureArgs(outDir);
            SessionState.EraseBool(KeyDone);
            SessionState.EraseString(KeyResult);
            SessionState.SetBool(KeyActive, true);

            if (EditorCliArgs.IsBatch) EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            else if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { Clear(); return; }
            else EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);

            Debug.Log($"[Twin] 束 {pack} → {outDir}（撮影 {(capture == XpCaptureLogic.TwinCapture.Eye ? "目線を同時に撮る" : "しない")}）。" +
                      $"XR: {ReplayXrGuard.DisableInitOnStart()}");
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.playModeStateChanged += OnState;
            EditorApplication.EnterPlaymode();
        }

        private static void OnState(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OnState;
            bool done = SessionState.GetBool(KeyDone, false);
            string result = SessionState.GetString(KeyResult, "");
            ReplayXrGuard.Restore();
            Clear();
            bool ok = done && result == "ok";
            if (ok) Debug.Log("[Twin] ✓ 走り終えた");
            else Debug.LogError($"[Twin] ✗ 走り切れなかった: {(done ? result : "Play が途中で終わった")}");
            Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// 同時撮影の撮り方。読むのは <c>XpReplayCapture</c> で、キーは replay と同じ（<c>XpReplayRun</c> が書くのと同じ値）。
        /// 全部ここで書き直す（SessionState は Editor のあいだ残るので、前の replay の値を持ち越さない）。
        /// </summary>
        private static void SetCaptureArgs(string outDir)
        {
            SessionState.SetString("fcv.replay.out", Path.GetFullPath(Path.Combine(outDir, "eye.mp4")));
            SessionState.SetString("fcv.replay.view", "eye");
            SessionState.SetFloat("fcv.replay.fov", ArgFloat("fov", XpCaptureLogic.SinglePassEyeFovDeg));
            SessionState.SetInt("fcv.replay.w", ArgInt("w", 1920));
            SessionState.SetInt("fcv.replay.h", ArgInt("h", 1080));
            SessionState.SetInt("fcv.replay.fps", ArgInt("fps", 30));
            SessionState.SetFloat("fcv.replay.maxsec", 0f);   // 走行の終わりまで撮る（終わりは XpTwinDriver.End が閉じる）
            SessionState.SetInt("fcv.replay.eyecam", ArgInt("eyecam", 0));
            SessionState.SetInt("fcv.replay.msaa", ArgInt("msaa", 1));
        }

        private static void Clear()
        {
            SessionState.EraseBool(KeyActive);
            SessionState.EraseString(KeyPack);
            SessionState.EraseBool(KeyDone);
            SessionState.EraseString(KeyResult);
            SessionState.EraseString(KeyCapture);
        }

        private static void Exit(int code)
        {
            if (EditorCliArgs.IsBatch) EditorApplication.Exit(code);
        }

        private static float ArgFloat(string key, float def)
            => float.TryParse(EditorCliArgs.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        private static int ArgInt(string key, int def)
            => int.TryParse(EditorCliArgs.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
    }
}
