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
            PlayerSettings.productName = ProductNameFor(System.IO.Path.GetDirectoryName(Application.dataPath) ?? "");
            return prev ? "off (was 1)" : "already off";
        }

        /// <summary>主の木のフォルダ名。ここで開いたときだけ、保存先は従来の <c>mawarimi-replay</c>。</summary>
        public const string MainFolder = "fixed-cam-vr";
        public const string BaseName = "mawarimi-replay";

        /// <summary>
        /// 再生・ツイン走行の間の productName（＝ persistentDataPath の末尾）。**木ごとに分ける。**
        ///
        /// ⚠ 同じ名前だと、別の worktree で同時に走る 2 本が同じ <c>xprec/</c> を使う。<c>XpTwinDriver.Load</c> は
        /// 走り始めに <c>xprec/</c> を丸ごと消すので、後から始めた方が先の走行の記録を消しにいく
        /// （2026-11-11、相手が掴んでいた <c>cam_Phone01.mjr</c> で IOException になり、後の走行が 49 秒で落ちた。
        /// 掴まれていなければ黙って消えていた）。
        /// 非 ASCII は使わない（上の MRUK の罠）。
        /// </summary>
        public static string ProductNameFor(string projectDir)
        {
            string folder = System.IO.Path.GetFileName(projectDir.TrimEnd('/', '\\'));
            if (string.Equals(folder, MainFolder, StringComparison.OrdinalIgnoreCase)) return BaseName;
            var sb = new System.Text.StringBuilder(BaseName).Append('-');
            foreach (char c in folder)
                sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_') ? char.ToLowerInvariant(c) : '_');
            return sb.ToString();
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
