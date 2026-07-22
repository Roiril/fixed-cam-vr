#nullable enable
using System;
using System.IO;
using FixedCamVr.Streaming;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Tracking.EditorTools
{
    /// <summary>
    /// 位置合わせ検証ビュー（<see cref="ZoneGridFootprint"/> の grid 生タイル + 登録ワイヤーフレーム）を
    /// Play なしに Editor で組んで多角度 PNG に焼く診断ツール。親エージェント（シュビー）が実機ビルドを
    /// 待たずにビュー崩れ・南北反転・変換ズレを机上検証するための一次証拠を作る。
    ///
    /// アクティブシーンへ一時ルート <c>[RegVizPreview]</c> を作り、実運用と同一のコードパス
    /// （<see cref="CourseFrame"/> + <see cref="ShowControlClient"/> + <see cref="CourseRegistrationController.PreviewBuildViz"/>）で
    /// viz を組む。show.json layout を注入して 2 状態（identity / 登録後相当）× 3 アングル（真上・斜め俯瞰・目線）を
    /// レンダし、finally でルートを DestroyImmediate する（シーンは汚さない・保存しない）。
    /// registration.json も show.json も一切書かない（SetRegistration は save:false、layout は SetLayoutForPreview 注入）。
    /// </summary>
    internal static class RegistrationVizPreview
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const string OutDirRel = "Screenshots/regviz";

        // 登録後相当の剛体変換（origin XZ + yaw）。identity と対で「変換が効いているか」を見る。
        private static readonly Vector2 RegisteredOrigin = new(0.4f, 0.25f);
        private const float RegisteredYawDeg = 25f;

        // 開いているシーンの写り込み隔離用レイヤー（組み込みの TransparentFX を一時借用。恒久変更はしない）。
        private const int PreviewLayer = 1;

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        // [CourseRegViz] は CourseRegistrationController がシーンルートに生成する。プレビューでは
        // 実行前（前回の残骸掃除）と finally（今回分の掃除）で全て消し、シーンを汚さない。
        private static void DestroyAllCourseRegViz()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (GameObject go in scene.GetRootGameObjects())
                if (go != null && go.name == "[CourseRegViz]")
                    UnityEngine.Object.DestroyImmediate(go);
        }

        // 現存する全 [CourseRegViz]（今状態で作られた分）をプレビューレイヤーへ。
        private static void LayerAllCourseRegViz()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (GameObject go in scene.GetRootGameObjects())
                if (go != null && go.name == "[CourseRegViz]")
                    SetLayerRecursive(go, PreviewLayer);
        }

        private struct Angle
        {
            public string name;
            public Vector3 pos;
            public Vector3 lookAt;
        }

        private static readonly Angle[] Angles =
        {
            new() { name = "top",     pos = new Vector3(0f, 3.2f, 0.001f), lookAt = Vector3.zero },
            new() { name = "oblique", pos = new Vector3(2.2f, 2.4f, -2.2f), lookAt = new Vector3(0f, 0.15f, 0.2f) },
            new() { name = "eye",     pos = new Vector3(0f, 1.6f, -2.4f),   lookAt = new Vector3(0f, 0.2f, 0.6f) },
        };

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Registration Viz", priority = 250)]
        private static void Run()
        {
            GameObject? root = null;
            RenderTexture? rt = null;
            Texture2D? tex = null;
            var savedPaths = new System.Collections.Generic.List<string>();
            try
            {
                DestroyAllCourseRegViz(); // 過去実行の残骸を先に掃除（累積するとレイヤー載せ替えが残骸に当たる）

                ShowLayoutDef layout = LoadShowLayout();

                root = new GameObject("[RegVizPreview]");

                // --- 供給元コンポーネント（Edit Mode では Awake/OnEnable が走らないためネットワーク・IO は起きない）---
                var frameGo = new GameObject("CourseFrame");
                frameGo.transform.SetParent(root.transform, false);
                var frame = frameGo.AddComponent<CourseFrame>();

                var showGo = new GameObject("ShowControlClient");
                showGo.transform.SetParent(root.transform, false);
                var show = showGo.AddComponent<ShowControlClient>();
                show.SetLayoutForPreview(layout);

                var ctrlGo = new GameObject("CourseRegistrationController");
                ctrlGo.transform.SetParent(root.transform, false);
                var ctrl = ctrlGo.AddComponent<CourseRegistrationController>();
                // private [SerializeField] courseFrame / showControl を SerializedObject で注入する。
                var so = new SerializedObject(ctrl);
                so.FindProperty("courseFrame").objectReferenceValue = frame;
                so.FindProperty("showControl").objectReferenceValue = show;
                so.ApplyModifiedPropertiesWithoutUndo();

                // --- 基準ジオメトリ（床グリッド + 北マーカー）。状態をまたいで不変なので 1 回だけ作る ---
                BuildStageProps(root.transform);

                // --- カメラ + RT ---
                var camGo = new GameObject("RegVizCamera");
                camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.10f, 0.12f, 1f);
                cam.fieldOfView = 55f;
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 50f;
                // ⚠ 開いているシーン（Main / TableDuoMain 等）の机・壁が写り込んで対象を隠すため、
                // プレビュー対象物だけを専用レイヤーに載せて cullingMask で隔離する（シーンは閉じない）。
                cam.cullingMask = 1 << PreviewLayer;

                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "RegVizRT" };
                tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);

                string outDir = Path.Combine(Application.dataPath, OutDirRel);
                Directory.CreateDirectory(outDir);

                foreach ((string stateName, Vector2 origin, float yaw) in new[]
                         {
                             ("identity", Vector2.zero, 0f),
                             ("registered", RegisteredOrigin, RegisteredYawDeg),
                         })
                {
                    // save:false ＝ registration.json は書かない。Changed 発火で購読 viz が追従する。
                    frame.SetRegistration(origin, yaw, save: false);
                    ctrl.PreviewBuildViz(); // 実運用と同一の viz 構築（grid 生タイル + ワイヤーフレーム）

                    // viz は [CourseRegViz]（シーンルート・状態ごとに作り直される）に生成されるため、
                    // 状態を組むたびにプレビューレイヤーへ載せ替える。
                    SetLayerRecursive(root, PreviewLayer);
                    LayerAllCourseRegViz();

                    foreach (Angle a in Angles)
                    {
                        cam.transform.position = a.pos;
                        if (a.name == "top")
                            // 真下視は LookAt(up=Vector3.up) が縮退してロールが不定になる。
                            // 北(+z)=画像上 と固定する。
                            cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                        else
                            cam.transform.LookAt(a.lookAt, Vector3.up);
                        RenderToTexture(cam, rt, tex);
                        string path = Path.Combine(outDir, $"regviz_{stateName}_{a.name}.png");
                        File.WriteAllBytes(path, tex.EncodeToPNG());
                        savedPaths.Add(path);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[RegVizPreview] レンダ失敗: {e}");
            }
            finally
            {
                if (rt != null)
                {
                    RenderTexture.active = null;
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                DestroyAllCourseRegViz(); // 最終状態の viz もシーンに残さない
            }

            if (savedPaths.Count > 0)
            {
                AssetDatabase.Refresh();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[RegVizPreview] {savedPaths.Count} 枚を保存:");
                foreach (string p in savedPaths) sb.AppendLine("  " + p.Replace('\\', '/'));
                Debug.Log(sb.ToString());
            }
        }

        // <repo>/tools/web-compositor/show.json の layout を読む。無い / 壊れているなら合成 grid にフォールバック。
        private static ShowLayoutDef LoadShowLayout()
        {
            try
            {
                string? repoRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (repoRoot != null)
                {
                    string showPath = Path.Combine(repoRoot, "tools", "web-compositor", "show.json");
                    if (File.Exists(showPath))
                    {
                        string json = File.ReadAllText(showPath);
                        var wrap = JsonUtility.FromJson<LayoutWrapper>(json);
                        if (wrap != null && wrap.layout != null && wrap.layout.HasData())
                        {
                            Debug.Log($"[RegVizPreview] show.json layout を使用: {showPath.Replace('\\', '/')}");
                            return wrap.layout;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RegVizPreview] show.json 読込失敗（合成 grid にフォールバック）: {e.Message}");
            }
            Debug.Log("[RegVizPreview] 合成 grid（12x12）にフォールバック");
            return RegVizSampleLayout.Build();
        }

        // JsonUtility 用の最小ラッパ（show.json の layout セクションだけ取り出す。他フィールドは無視される）。
        [Serializable] private sealed class LayoutWrapper { public ShowLayoutDef? layout; }

        // 灰色フロア（3x3m・y=0）と北マーカー（+Z 側の赤い細棒）。南北反転の検出用。
        private static void BuildStageProps(Transform parent)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            floor.name = "RegVizFloor";
            floor.transform.SetParent(parent, false);
            floor.transform.position = new Vector3(0f, 0f, 0f);
            floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            floor.transform.localScale = new Vector3(3f, 3f, 1f);
            var floorCol = floor.GetComponent<Collider>();
            if (floorCol != null) UnityEngine.Object.DestroyImmediate(floorCol);
            // 床は不透明ジオメトリなので URP/Unlit（URP で確実に描ける不透明シェーダ）を使う。
            var floorMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            floorMat.SetColor("_BaseColor", new Color(0.28f, 0.28f, 0.30f, 1f));
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            var north = GameObject.CreatePrimitive(PrimitiveType.Cube);
            north.name = "RegVizNorthMarker(+Z)";
            north.transform.SetParent(parent, false);
            north.transform.position = new Vector3(0f, 0.03f, 1.2f);
            north.transform.localScale = new Vector3(0.12f, 0.04f, 0.5f);
            var northCol = north.GetComponent<Collider>();
            if (northCol != null) UnityEngine.Object.DestroyImmediate(northCol);
            north.GetComponent<Renderer>().sharedMaterial =
                new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 0.25f, 0.2f, 1f) };
        }

        private static void RenderToTexture(Camera cam, RenderTexture rt, Texture2D tex)
        {
            cam.targetTexture = rt;
            // URP 2022.3 では StandardRequest が Edit Mode で destination に書かないことを実測確認
            // （背景 SolidColor が反映されず未初期化 RT を読んだ）。Camera.Render() は URP でも
            // 単発カメラの同期レンダとして機能するのでこちらを正とする。
            bool usedRequest = false;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            // 診断: レンダ経路と平均色（背景 SolidColor(0.10,0.10,0.12) に近い暗色でなければレンダ不発を疑う）。
            Color avg = AverageColor(tex);
            Debug.Log($"[RegVizPreview] render path={(usedRequest ? "StandardRequest" : "Camera.Render")} avg=({avg.r:F3},{avg.g:F3},{avg.b:F3})");
        }

        private static Color AverageColor(Texture2D tex)
        {
            Color[] px = tex.GetPixels(0, 0, Width, Height, 0);
            float r = 0f, g = 0f, b = 0f;
            for (int i = 0; i < px.Length; i += 97) { r += px[i].r; g += px[i].g; b += px[i].b; }
            float n = Mathf.Ceil(px.Length / 97f);
            return new Color(r / n, g / n, b / n);
        }
    }
}
