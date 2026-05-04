#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Fx.Editor
{
    /// <summary>
    /// Phase 1（CRT FullScreenPassRendererFeature）用のマテリアルとフォルダを準備する。
    ///
    /// URP Renderer Asset と FullScreenPassRendererFeature の追加は
    /// Unity の URP バージョン差・型可視性に左右されやすいため、本スクリプトでは
    ///   - マテリアル `FxCrtMaterial.mat` の生成のみを自動化
    /// に留める。Renderer Asset 自体の作成と Feature 追加は手動手順をログ表示で案内する。
    /// </summary>
    public static class FxRendererSetup
    {
        private const string MatPath = "Assets/Art/Materials/Fx/FxCrtMaterial.mat";
        private const string ShaderPath = "Assets/Art/Shaders/Fx/FxCrtPostFx.shader";

        [MenuItem("Tools/FixedCamVr/Setup/Create CRT Material", priority = 52)]
        public static void CreateCrtMaterial()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                Debug.LogError($"[FxRendererSetup] Shader not found: {ShaderPath}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(MatPath)!);

            var existing = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (existing != null)
            {
                Debug.LogWarning($"[FxRendererSetup] Material already exists: {MatPath}");
                Selection.activeObject = existing;
                return;
            }

            var mat = new Material(shader) { name = "FxCrtMaterial" };
            mat.SetFloat("_ScanlineIntensity", 0.25f);
            mat.SetFloat("_ScanlineCount", 240f);
            mat.SetFloat("_GrainIntensity", 0.08f);
            mat.SetFloat("_VignetteIntensity", 0.6f);

            AssetDatabase.CreateAsset(mat, MatPath);
            AssetDatabase.SaveAssetIfDirty(mat);
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = mat;
            EditorGUIUtility.PingObject(mat);

            Debug.Log(
                "[FxRendererSetup] Created FxCrtMaterial.\n" +
                "Next steps (manual):\n" +
                "  1. Project ペインで Assets/Settings/Fx を選び、右クリック > Create > Rendering > URP Universal Renderer\n" +
                "     名前を 'FxRenderer' にする\n" +
                "  2. FxRenderer asset を選び、Inspector で Add Renderer Feature > Full Screen Pass Renderer Feature\n" +
                "     Pass Material に FxCrtMaterial を割り当て、Injection Point は After Rendering Post Processing\n" +
                "  3. FxSandbox の Main Camera で Renderer 欄に FxRenderer を追加割り当て\n" +
                "     (Project Settings の URP Renderer Pipeline Asset を新規に切ってもよいが、本検証では Camera オーバーライドが安全)");
        }
    }
}
