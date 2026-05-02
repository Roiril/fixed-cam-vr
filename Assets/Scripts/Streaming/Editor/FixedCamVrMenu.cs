#nullable enable
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FixedCamVr.Streaming;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// Tools > FixedCamVr メニュー。シーン切替・テスト実行・DroidCam 接続確認の導線を集約。
    /// </summary>
    public static class FixedCamVrMenu
    {
        private const string Root = "Tools/FixedCamVr/";

        [MenuItem(Root + "Open Main Scene %#m")] // Ctrl+Shift+M
        public static void OpenMain() => OpenScene("Assets/Scenes/Main.unity");

        [MenuItem(Root + "Open Debug Scene %#d")] // Ctrl+Shift+D
        public static void OpenDebug() => OpenScene("Assets/Scenes/Debug.unity");

        [MenuItem(Root + "Run Streaming Tests")]
        public static void RunStreamingTests()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "FixedCamVr.Streaming.Tests" }
            }));
            Debug.Log("[FixedCamVr] EditMode tests launched. See Test Runner window for results.");
        }

        [MenuItem(Root + "Ping DroidCams")]
        public static async void PingDroidCams()
        {
            var sources = AssetDatabase.FindAssets("t:CameraSource")
                .Select(g => AssetDatabase.LoadAssetAtPath<CameraSource>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null)
                .ToArray();

            if (sources.Length == 0)
            {
                Debug.LogWarning("[FixedCamVr] No CameraSource assets found.");
                return;
            }

            using var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(2) };
            foreach (var src in sources)
            {
                var url = src.BuildUrl();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Head, url);
                    var resp = await http.SendAsync(req);
                    Debug.Log($"[FixedCamVr] ✅ {src.DisplayName} {url} → HTTP {(int)resp.StatusCode}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[FixedCamVr] ❌ {src.DisplayName} {url} → {ex.Message}");
                }
            }
        }

        [MenuItem(Root + "Reveal Camera Sources Folder")]
        public static void RevealCamerasFolder()
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>("Assets/Settings/Cameras");
            if (obj != null)
            {
                EditorUtility.FocusProjectWindow();
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
        }

        [MenuItem(Root + "Open Test Runner")]
        public static void OpenTestRunner() => EditorApplication.ExecuteMenuItem("Window/General/Test Runner");

        // ---- Editor Layout ----

        private const string LayoutPath = "Assets/Editor/Layouts/FixedCamVr.wlt";

        [MenuItem(Root + "Layout/Setup Recommended Workspace")]
        public static void SetupRecommendedWorkspace()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            // 1. Main シーンをロード
            if (System.IO.File.Exists("Assets/Scenes/Main.unity"))
            {
                EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            }

            // 2. 開発で常用するウィンドウを開く
            EditorApplication.ExecuteMenuItem("Window/General/Hierarchy");
            EditorApplication.ExecuteMenuItem("Window/General/Inspector");
            EditorApplication.ExecuteMenuItem("Window/General/Project");
            EditorApplication.ExecuteMenuItem("Window/General/Console");
            EditorApplication.ExecuteMenuItem("Window/General/Scene");
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.ExecuteMenuItem("Window/General/Test Runner");

            // 3. [Streaming] を選択（Inspector に Registry の live state が出る位置）
            var streaming = GameObject.Find("[Streaming]");
            if (streaming != null) Selection.activeGameObject = streaming;

            // 4. Scene View を Screen にフレーム
            var screen = GameObject.Find("Screen");
            if (screen != null && SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.Frame(new Bounds(screen.transform.position, Vector3.one * 3f), instant: true);
            }

            Debug.Log(
                "[FixedCamVr] 推奨ワークスペースを準備しました。\n" +
                "次の手順（一度だけ手動でドラッグ）:\n" +
                "  ・Hierarchy を左カラムに / Project をその下に\n" +
                "  ・Scene と Game を中央上タブに\n" +
                "  ・Console を中央下に\n" +
                "  ・Inspector を右カラムに / Test Runner をその下 or タブで\n" +
                "完了したら Tools > FixedCamVr > Layout > Save Current Layout でコミット可能な .wlt として保存される。\n" +
                "次回以降は Tools > FixedCamVr > Layout > Apply FixedCamVr Layout 一発で復元。"
            );
        }

        [MenuItem(Root + "Layout/Apply FixedCamVr Layout")]
        public static void ApplyLayout()
        {
            if (!System.IO.File.Exists(LayoutPath))
            {
                Debug.LogWarning($"[FixedCamVr] レイアウト未保存。先に Tools > FixedCamVr > Layout > Save Current Layout を実行してください。期待パス: {LayoutPath}");
                return;
            }
            EditorUtility.LoadWindowLayout(System.IO.Path.GetFullPath(LayoutPath));
        }

        [MenuItem(Root + "Layout/Save Current Layout")]
        public static void SaveLayout()
        {
            var dir = System.IO.Path.GetDirectoryName(LayoutPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            // SaveWindowLayout は internal なので reflection で呼ぶ（API 安定）
            var saveFn = typeof(EditorUtility).GetMethod(
                "SaveWindowLayout",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);

            if (saveFn == null)
            {
                // フォールバック: WindowLayout クラス
                var wlType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
                saveFn = wlType?.GetMethod(
                    "SaveWindowLayout",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                    null, new[] { typeof(string) }, null);
            }

            if (saveFn == null)
            {
                Debug.LogError("[FixedCamVr] SaveWindowLayout が見つかりません。`Window > Layouts > Save Layout...` から手動で Assets/Editor/Layouts/FixedCamVr.wlt として保存してください。");
                return;
            }

            saveFn.Invoke(null, new object[] { System.IO.Path.GetFullPath(LayoutPath) });
            AssetDatabase.Refresh();
            Debug.Log($"[FixedCamVr] レイアウトを保存しました: {LayoutPath}（コミットすれば全員に共有可）");
        }

        private static void OpenScene(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                Debug.LogError($"[FixedCamVr] scene not found: {path}");
                return;
            }
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            }
        }
    }
}
