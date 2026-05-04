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

        // ---- ユーザー常用 ----

        [MenuItem(Root + "Open Main Scene %#m", priority = 0)] // Ctrl+Shift+M
        public static void OpenMain() => OpenScene("Assets/Scenes/Main.unity");

        // ---- Diagnostics（シュビー / 検証用） ----

        [MenuItem(Root + "Diagnostics/Open Debug Scene %#d", priority = 200)] // Ctrl+Shift+D
        public static void OpenDebug() => OpenScene("Assets/Scenes/Debug/FlatStreaming.unity");

        [MenuItem(Root + "Diagnostics/Open PlayerZone Sandbox", priority = 201)]
        public static void OpenPlayerZoneSandbox() => OpenScene("Assets/Scenes/Sandbox/PlayerZone.unity");

        [MenuItem(Root + "Diagnostics/Run All Tests", priority = 220)]
        public static void RunAllTests()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[]
                {
                    "FixedCamVr.Streaming.Tests",
                    "FixedCamVr.Tracking.Tests",
                    "FixedCamVr.Fx.Tests"
                }
            }));
            Debug.Log("[FixedCamVr] EditMode tests launched (Streaming / Tracking / Fx). See Test Runner window for results.");
        }

        private static bool _pingInFlight;

        [MenuItem(Root + "Diagnostics/Ping DroidCams", priority = 230)]
        public static async void PingDroidCams()
        {
            // 連打防止（async void なので前回完了前に再入する可能性がある）
            if (_pingInFlight)
            {
                Debug.Log("[FixedCamVr] Ping DroidCams は実行中です。");
                return;
            }

            var sources = AssetDatabase.FindAssets("t:CameraSource")
                .Select(g => AssetDatabase.LoadAssetAtPath<CameraSource>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null)
                .ToArray();

            if (sources.Length == 0)
            {
                Debug.LogWarning("[FixedCamVr] No CameraSource assets found.");
                return;
            }

            _pingInFlight = true;
            try
            {
                using var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(2) };
                foreach (var src in sources)
                {
                    if (src == null) continue;
                    var url = src.BuildUrl();
                    try
                    {
                        // MJPEG エンドポイント (DroidCam / IP Webcam) は HEAD に応えないことが多いので
                        // GET + ResponseHeadersRead でヘッダだけ取得して即破棄する。
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        var ct = resp.Content.Headers.ContentType?.MediaType ?? "(no content-type)";
                        Debug.Log($"[FixedCamVr] OK {src.DisplayName} {url} -> HTTP {(int)resp.StatusCode} ({ct})");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[FixedCamVr] NG {src.DisplayName} {url} -> {ex.Message}");
                    }
                }
            }
            finally
            {
                _pingInFlight = false;
            }
        }

        [MenuItem(Root + "Diagnostics/Reveal Camera Sources Folder", priority = 240)]
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

        [MenuItem(Root + "Diagnostics/Open Test Runner", priority = 221)]
        public static void OpenTestRunner() => EditorApplication.ExecuteMenuItem("Window/General/Test Runner");

        // ---- Editor Layout ----

        private const string LayoutPath = "Assets/Editor/Layouts/FixedCamVr.wlt";

        [MenuItem(Root + "Layout/Setup Recommended Workspace", priority = 100)]
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

        [MenuItem(Root + "Layout/Apply FixedCamVr Layout", priority = 101)]
        public static void ApplyLayout()
        {
            if (!System.IO.File.Exists(LayoutPath))
            {
                Debug.LogWarning($"[FixedCamVr] レイアウト未保存。先に Tools > FixedCamVr > Layout > Save Current Layout を実行してください。期待パス: {LayoutPath}");
                return;
            }
            EditorUtility.LoadWindowLayout(System.IO.Path.GetFullPath(LayoutPath));
        }

        [MenuItem(Root + "Layout/Save Current Layout", priority = 102)]
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
