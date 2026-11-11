#nullable enable
using System;
using System.Globalization;
using System.IO;
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
    /// marks=1B.2A.3B（周の中でも報告する区間。各区間に入って 2.5 秒後に 1 回）。
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

            SessionState.SetString(KeyPack, Path.GetFullPath(pack));
            SessionState.SetString(KeyOut, Path.GetFullPath(outDir));
            SessionState.SetFloat("fcv.twin.speed", ArgFloat("speed", 0.25f));
            SessionState.SetFloat("fcv.twin.dwell", ArgFloat("dwell", 6f));
            SessionState.SetInt("fcv.twin.nomark", ArgFloat("nomark", 0f) != 0f ? 1 : 0);
            SessionState.SetInt("fcv.twin.relief", ArgFloat("relief", 0f) != 0f ? 1 : 0);
            SessionState.SetString("fcv.twin.marks", EditorCliArgs.Get("marks") ?? "");
            SessionState.SetInt("fcv.twin.showcalib", EditorCliArgs.Get("calib") == "show" ? 1 : 0);
            SessionState.EraseBool(KeyDone);
            SessionState.EraseString(KeyResult);
            SessionState.SetBool(KeyActive, true);

            if (EditorCliArgs.IsBatch) EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            else if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { Clear(); return; }
            else EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);

            Debug.Log($"[Twin] 束 {pack} → {outDir}。XR: {ReplayXrGuard.DisableInitOnStart()}");
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

        private static void Clear()
        {
            SessionState.EraseBool(KeyActive);
            SessionState.EraseString(KeyPack);
            SessionState.EraseBool(KeyDone);
            SessionState.EraseString(KeyResult);
        }

        private static void Exit(int code)
        {
            if (EditorCliArgs.IsBatch) EditorApplication.Exit(code);
        }

        private static float ArgFloat(string key, float def)
            => float.TryParse(EditorCliArgs.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
    }
}
