#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.Replay
{
    /// <summary>
    /// <b>体験の再生と撮り直し</b>（紹介動画用。2026-10-23 ユーザー依頼）。
    /// Quest の記録（<c>py -3.11 tools/quest-xprec.py pull</c> → <c>Logs/xprec/&lt;日時&gt;/</c>）を、
    /// 本番の Main シーンへ流し直して広い画角で mp4 にする。実体は Assembly-CSharp の
    /// <c>XpReplayDriver</c>（流す）と <c>XpReplayCapture</c>（撮る）。ここは Play に入れて出るだけ。
    ///
    /// CLI: <c>.\tools\unity.ps1 replay -Set rec=latest,view=eye</c>（batchmode で Play し、終わると閉じる）。
    /// GUI: メニューから呼ぶと Play に入り、終わると Play を出る（Editor は開いたまま）。
    ///
    /// 値（<c>-Set key=value</c>）: rec（フォルダ / latest）・view（eye / follow）・fov（縦の度）・w・h・fps・
    /// maxsec（撮る長さの上限）・out（mp4 のパス）。
    ///
    /// HMD 無しの Play は XR の起動時初期化で固まり、MRUK は日本語の productName で落ちる。
    /// <see cref="ReplayXrGuard"/> が Play の間だけ両方を外す（ディスクの設定は変えない）。
    /// </summary>
    [InitializeOnLoad]
    public static class XpReplayRun
    {
        private const string KeyActive = "fcv.replay.active";
        private const string MainScene = "Assets/Scenes/Main.unity";

        static XpReplayRun()
        {
            if (!SessionState.GetBool(KeyActive, false)) return;
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.playModeStateChanged += OnState;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Replay Recording (wide capture)", priority = 245)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[Replay] Play 中は始められない");
                Exit(2);
                return;
            }
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string? rec = ResolveRecording(projectRoot, EditorCliArgs.Get("rec") ?? "latest");
            if (rec == null)
            {
                Debug.LogError("[Replay] 記録が見つからない（Logs/xprec/<日時>/ に meta.json と track.bin が要る。" +
                               "py -3.11 tools/quest-xprec.py pull）");
                Exit(2);
                return;
            }

            string view = (EditorCliArgs.Get("view") ?? "eye").ToLowerInvariant();
            if (view != "eye" && view != "follow")
            {
                Debug.LogError($"[Replay] view は eye / follow（受け取ったもの: {view}）");
                Exit(2);
                return;
            }
            string outPath = EditorCliArgs.Get("out") ?? DefaultOut(projectRoot, rec, view);

            SessionState.SetString(KeyDirName, rec);
            SessionState.SetInt("fcv.replay.fps", ArgInt("fps", 30));
            SessionState.SetString("fcv.replay.out", Path.GetFullPath(outPath));
            SessionState.SetString("fcv.replay.view", view);
            SessionState.SetFloat("fcv.replay.fov", ArgFloat("fov", view == "follow" ? 62f : 72f));
            SessionState.SetInt("fcv.replay.w", ArgInt("w", 1920));
            SessionState.SetInt("fcv.replay.h", ArgInt("h", 1080));
            SessionState.SetFloat("fcv.replay.maxsec", ArgFloat("maxsec", 0f));
            SessionState.SetInt("fcv.replay.eyecam", ArgInt("eyecam", 0));
            SessionState.SetInt("fcv.replay.msaa", ArgInt("msaa", 1));
            SessionState.EraseBool("fcv.replay.done");
            SessionState.EraseString("fcv.replay.result");
            SessionState.SetBool(KeyActive, true);

            if (EditorCliArgs.IsBatch) EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            else if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { Clear(); return; }
            else EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);

            Debug.Log($"[Replay] 記録 {rec} → {outPath}（view={view}）。XR: {ReplayXrGuard.DisableInitOnStart()}");
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.playModeStateChanged += OnState;
            EditorApplication.EnterPlaymode();
        }

        // XpReplayDriver.KeyDir と同じ値（Assembly-CSharp は Editor の asmdef から参照できない）。
        private const string KeyDirName = "fcv.replay.dir";

        private static void OnState(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OnState;
            bool done = SessionState.GetBool("fcv.replay.done", false);
            string result = SessionState.GetString("fcv.replay.result", "");
            ReplayXrGuard.Restore();
            Clear();
            bool ok = done && !result.StartsWith("error", StringComparison.Ordinal) && File.Exists(result);
            if (ok) Debug.Log($"[Replay] ✓ 書き出した: {result}");
            else Debug.LogError($"[Replay] ✗ 撮れなかった: {(done ? result : "Play が途中で終わった")}");
            Exit(ok ? 0 : 1);
        }

        private static void Clear()
        {
            SessionState.EraseBool(KeyActive);
            SessionState.EraseString(KeyDirName);
            SessionState.EraseBool("fcv.replay.done");
            SessionState.EraseString("fcv.replay.result");
        }

        private static void Exit(int code)
        {
            if (EditorCliArgs.IsBatch) EditorApplication.Exit(code);
        }

        /// <summary>rec の値からフォルダを決める。latest は Logs/xprec/ の中でいちばん新しいもの。</summary>
        public static string? ResolveRecording(string projectRoot, string rec)
        {
            bool IsRec(string d) => File.Exists(Path.Combine(d, "meta.json")) && File.Exists(Path.Combine(d, "track.bin"));
            if (!string.Equals(rec, "latest", StringComparison.OrdinalIgnoreCase))
            {
                string p = Path.IsPathRooted(rec) ? rec : Path.Combine(projectRoot, rec);
                if (IsRec(p)) return Path.GetFullPath(p);
                string q = Path.Combine(projectRoot, "Logs", "xprec", rec);
                return IsRec(q) ? Path.GetFullPath(q) : null;
            }
            string root = Path.Combine(projectRoot, "Logs", "xprec");
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Where(IsRec)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>
        /// 既定の出力先。人が後で見る動画なので Drive へ直接書く（`~/.claude/rules/drive-offload.md`）。
        /// Drive が無い機では Logs/replay/。
        /// </summary>
        private static string DefaultOut(string projectRoot, string rec, string view)
        {
            string name = $"{Path.GetFileName(rec)}_{view}.mp4";
            string drive = @"G:\マイドライブ\shubie\renders\mawarimi-replay";
            try
            {
                if (Directory.Exists(Path.GetDirectoryName(drive))) return Path.Combine(drive, name);
            }
            catch { }
            return Path.Combine(projectRoot, "Logs", "replay", name);
        }

        private static int ArgInt(string key, int def)
            => int.TryParse(EditorCliArgs.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

        private static float ArgFloat(string key, float def)
            => float.TryParse(EditorCliArgs.Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
    }
}
