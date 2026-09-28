#nullable enable
using System;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// CLI（<c>tools\unity.ps1 menu &lt;名前&gt;</c> → <c>unity run -- -executeMethod &lt;完全修飾名&gt;</c>）から
    /// Editor の機能を呼ぶときの共通土台。
    ///
    /// <c>-executeMethod</c> は**引数を取れない**ので、値は Unity のコマンドラインへ
    /// <c>-fcv &lt;key&gt;=&lt;value&gt;</c> の形で積んで渡す（Unity は知らない引数を無視して
    /// <see cref="Environment.GetCommandLineArgs"/> に残すので、こちらで拾える）。
    /// <c>unity.ps1 menu ... -Set key=value</c> がこの形に組み立てる。
    ///
    /// ⚠ <c>unity run</c> は <c>-batchmode</c> / <c>-quit</c> / <c>-logFile</c> を自分で管理する。
    /// <c>--</c> の後ろにこれらを書くと CLI が exit 6 で弾く（2026-08-08 実測）。
    ///
    /// このクラスは <c>FixedCamVr.Streaming.Editor</c> asmdef に置いてある。
    /// <c>FixedCamVr.Tracking.Editor</c> はこれを参照済みなので両方から使える。
    /// </summary>
    public static class EditorCliArgs
    {
        /// <summary>コマンドラインで値を積むときの目印。</summary>
        public const string Flag = "-fcv";

        /// <summary>batchmode（＝ CLI から呼ばれた）か。GUI と挙動を分ける唯一の判定。</summary>
        public static bool IsBatch => Application.isBatchMode;

        /// <summary>
        /// <c>-fcv key=value</c> で渡された値を返す。無ければ null。
        /// 同じキーが複数あれば**最後のものが勝つ**（後から上書きできる方が CLI として素直）。
        /// </summary>
        public static string? Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string prefix = key + "=";
            string? found = null;

            string[] argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length - 1; i++)
            {
                if (!string.Equals(argv[i], Flag, StringComparison.Ordinal)) continue;
                string pair = argv[i + 1];
                if (pair.StartsWith(prefix, StringComparison.Ordinal))
                    found = pair.Substring(prefix.Length);
            }
            return string.IsNullOrEmpty(found) ? null : found;
        }

        /// <summary>
        /// CLI から呼ばれていて、かつ指定シーンが開いていなければ開く。
        ///
        /// **GUI では何もしない**（true を返す）。人が開いているシーンを黙って切り替えると、
        /// 未保存の作業を失わせるか、少なくとも「どこを見ていたか」を奪う。
        /// batchmode は起動直後が空シーンなので、開かないと
        /// <c>GameObject.Find</c> 系のプレビューが軒並み「見つからない」で落ちる。
        /// </summary>
        /// <returns>続行してよいか（GUI は常に true / batchmode は開けたか）</returns>
        public static bool EnsureScene(string scenePath)
        {
            if (!IsBatch) return true;

            Scene active = SceneManager.GetActiveScene();
            if (active.path == scenePath) return true;

            Debug.Log($"[EditorCli] batchmode なので {scenePath} を開きます（現在: " +
                      $"{(string.IsNullOrEmpty(active.path) ? "(空シーン)" : active.path)}）");
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            if (SceneManager.GetActiveScene().path == scenePath) return true;

            Debug.LogError($"[EditorCli] {scenePath} を開けませんでした。");
            return false;
        }
    }
}
