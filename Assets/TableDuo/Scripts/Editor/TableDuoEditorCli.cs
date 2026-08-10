#nullable enable
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// CLI（<c>tools\unity.ps1 menu &lt;名前&gt;</c>）から TableDuo の Editor ツールを呼ぶための土台。
    ///
    /// batchmode は起動直後が空シーンなので、<c>GameObject.Find("[TableDuo]/...")</c> や
    /// <c>FindObjectOfType&lt;RemoteHandMeshProvider&gt;()</c> に頼るプレビューは
    /// **シーンを開かないと軒並み「見つからない」で落ちる**。
    ///
    /// ⚠ 廻リ視側の <c>FixedCamVr.Streaming.EditorTools.EditorCliArgs</c> と同じ処理を持つが、
    /// **参照しない**。アプリ間の asmdef 相互参照は禁止で、<c>AppIsolationTests</c> が機械で落とす
    /// （<c>rules/parallel-projects.md</c>）。重複より分離を採る。
    /// </summary>
    internal static class TableDuoEditorCli
    {
        public const string ScenePath = "Assets/TableDuo/Scenes/TableDuoMain.unity";

        /// <summary>batchmode（＝ CLI から呼ばれた）か。</summary>
        public static bool IsBatch => Application.isBatchMode;

        /// <summary>
        /// CLI から呼ばれていて TableDuoMain が開いていなければ開く。
        /// **GUI では何もしない**（人が開いているシーンを黙って切り替えない）。
        /// </summary>
        /// <returns>続行してよいか</returns>
        public static bool EnsureScene()
        {
            if (!IsBatch) return true;

            Scene active = SceneManager.GetActiveScene();
            if (active.path == ScenePath) return true;

            Debug.Log($"[TableDuoCli] batchmode なので {ScenePath} を開きます（現在: " +
                      $"{(string.IsNullOrEmpty(active.path) ? "(空シーン)" : active.path)}）");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            if (SceneManager.GetActiveScene().path == ScenePath) return true;

            Debug.LogError($"[TableDuoCli] {ScenePath} を開けませんでした。" +
                           "Tools/FixedCamVr/Setup/Setup TableDuo Scene を先に実行してください。");
            return false;
        }
    }
}
