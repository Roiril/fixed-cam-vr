#nullable enable
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.Replay
{
    /// <summary>
    /// Editor の Play で XR を起動時に初期化させない（再生の間だけ）。
    ///
    /// HMD も Link も無い Editor で OVR を持つ Main.unity を Play すると、XR の起動時初期化が
    /// HMD を待って Editor ごと固まる（`reference/mcp-unity.md`）。再生は頭の姿勢を記録から入れるので
    /// XR は要らない。Standalone の <c>InitManagerOnStart</c> を**メモリ上でだけ**下ろし、終わったら戻す。
    /// ⚠ アセットは保存しない（`Assets/XR/` はビルドの副産物でユーザー所有。ディスクの値を変えない）。
    ///
    /// XR Management の Editor 型は asmdef から参照せず、名前で引く（参照を足すと依存が増える）。
    /// </summary>
    public static class ReplayXrGuard
    {
        private const string Key = "fcv.replayXrPrev";

        /// <summary>下ろした結果（"off (was 1)" / "already off" / "not found"）。</summary>
        public static string DisableInitOnStart()
        {
            object? gs = StandaloneSettings();
            if (gs == null) return "not found";
            PropertyInfo? p = gs.GetType().GetProperty("InitManagerOnStart");
            if (p == null) return "no property";
            bool prev = (bool)p.GetValue(gs)!;
            if (!SessionState.GetBool(Key + ".set", false))
            {
                SessionState.SetBool(Key + ".set", true);
                SessionState.SetBool(Key, prev);
            }
            p.SetValue(gs, false);

            // MRUK は Play の頭（RuntimeInitializeOnLoad）で必ずネイティブを起こし、persistentDataPath を渡す。
            // productName が日本語（廻リ視）だとパスの非 ASCII で文脈の生成に失敗し、続く検査で Editor ごと落ちる。
            // 再生の間だけ ASCII の名前にする（メモリ上だけ。戻す）。
            if (!SessionState.GetBool(Key + ".nameSet", false))
            {
                SessionState.SetBool(Key + ".nameSet", true);
                SessionState.SetString(Key + ".name", PlayerSettings.productName);
            }
            PlayerSettings.productName = "mawarimi-replay";
            return prev ? "off (was 1)" : "already off";
        }

        public static void Restore()
        {
            if (SessionState.GetBool(Key + ".nameSet", false))
            {
                PlayerSettings.productName = SessionState.GetString(Key + ".name", PlayerSettings.productName);
                SessionState.EraseBool(Key + ".nameSet");
                SessionState.EraseString(Key + ".name");
            }
            if (!SessionState.GetBool(Key + ".set", false)) return;
            bool prev = SessionState.GetBool(Key, true);
            SessionState.EraseBool(Key + ".set");
            SessionState.EraseBool(Key);
            object? gs = StandaloneSettings();
            gs?.GetType().GetProperty("InitManagerOnStart")?.SetValue(gs, prev);
        }

        private static object? StandaloneSettings()
        {
            Type? t = Type.GetType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget, Unity.XR.Management.Editor");
            MethodInfo? m = t?.GetMethod("XRGeneralSettingsForBuildTarget", BindingFlags.Public | BindingFlags.Static);
            try { return m?.Invoke(null, new object[] { BuildTargetGroup.Standalone }); }
            catch (Exception e) { Debug.LogWarning("[Replay] XR 設定を引けない: " + e.Message); return null; }
        }
    }
}
