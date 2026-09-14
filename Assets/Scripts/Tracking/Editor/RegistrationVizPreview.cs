#nullable enable
using System;
using System.IO;
using FixedCamVr.Diagnostics;
using FixedCamVr.Streaming;
using FixedCamVr.Streaming.EditorTools;
using TMPro;
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

        // public なのは CLI（`unity.ps1 menu regviz`）が -executeMethod で直接呼ぶため。
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Registration Viz", priority = 250)]
        public static void Run()
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

                // --- HUD 検証パス（登録ガイダンス StatusHud の位置・サイズ感）---
                // 既存 6 枚（タイル / ワイヤー）は上のループで保存済み。ここは eye アングルのみ +2 枚を足す。
                RenderHudPass(root, cam, rt, tex, outDir, frame, ctrl, savedPaths);
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

        // 登録ガイダンス HUD（StatusHud）の位置・サイズ感を eye アングルで検証する追加パス（+2 枚）。
        // HUD の見た目は本番と同一シーム（<see cref="MainDemoSceneSetup.CreateStatusHudVisual"/>）で組む。
        // head = eye カメラにして、eye アングルへ置いてから SendMessage で内容解決・配置を 1 回駆動する。
        private static void RenderHudPass(GameObject root, Camera cam, RenderTexture rt, Texture2D tex,
            string outDir, CourseFrame frame, CourseRegistrationController ctrl,
            System.Collections.Generic.List<string> savedPaths)
        {
            // head = eye カメラ。CreateStatusHudVisual は本番 CreateStatusHud と同一の見た目を組む
            // （パネル 720x320・スケール 0.001・fontSize 34・distance 1.6 等）。
            StatusHud hud = MainDemoSceneSetup.CreateStatusHudVisual(root.transform, cam.transform, ctrl, frame);
            TMP_Text? hudTmp = hud.GetComponentInChildren<TMP_Text>();

            // 登録モード（Capture）へ入れてガイダンスを非空にする。save:false 経路のため HasRegistration=false
            // → Capture 着地でガイダンス「点 1/N …」が出る。SetActive は [CourseRegViz] を組み直すが、
            // 既存 6 枚は保存済みなので影響しない。
            if (!ctrl.IsActive) ctrl.Toggle();

            Angle eye = Angles[2]; // eye アングルのみ HUD を写す（top/oblique の従来検証は汚さない）

            foreach ((string stateName, Vector2 origin, float yaw) in new[]
                     {
                         ("identity", Vector2.zero, 0f),
                         ("registered", RegisteredOrigin, RegisteredYawDeg),
                     })
            {
                frame.SetRegistration(origin, yaw, save: false);

                // HUD は head（=カメラ）の位置・ヨーに追従するので、先に eye アングルへ置いてから駆動する。
                cam.transform.position = eye.pos;
                cam.transform.LookAt(eye.lookAt, Vector3.up);

                // 組み直された [CourseRegViz] と HUD をプレビューレイヤーへ（cullingMask 隔離）。
                SetLayerRecursive(root, PreviewLayer);
                LayerAllCourseRegViz();

                // Edit Mode では MonoBehaviour の Update/LateUpdate が自動では走らない。SendMessage は private
                // マジックメソッド（StatusHud.Update / LateUpdate）にも届くので、内容解決（RenderContent）と
                // 配置（ApplyPose）を 1 回だけ手動駆動する。head 設定後に駆動 → HUD が視界内の実位置に来る。
                // 先に controller の Update も駆動（GuidanceText がフレームループで組まれる場合に備える）。
                ctrl.gameObject.SendMessage("Update", SendMessageOptions.DontRequireReceiver);
                hud.gameObject.SendMessage("Update", SendMessageOptions.DontRequireReceiver);
                hud.gameObject.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);

                // ⚠ UGUI Canvas（TextMeshProUGUI）は Edit Mode の手動 Camera.Render() に乗らない
                //   （active/enabled/レイヤー/視界内すべて正でも 1 文字も描かれないことを実測確認）。
                //   実 StatusHud（本番シーム）には姿勢決定と内容解決だけをさせ、レンダは同メトリクスの
                //   TMP 3D（TextMeshPro・RectTransform 720x320 / scale 0.001 / fontSize 34 = UGUI と同一単位）
                //   ミラーで行う。位置・サイズ感の検証対象は姿勢とメトリクスなので忠実性は保たれる。
                UpdateHudMirror(root, hud, hudTmp, ctrl);

                Debug.Log($"[RegVizPreview] hud diag: active={ctrl.IsActive} guidanceLen={ctrl.GuidanceText?.Length ?? 0} " +
                          $"hudPos={hud.transform.position} camPos={cam.transform.position} " +
                          $"font={(hudTmp != null && hudTmp.font != null ? hudTmp.font.name : "null")}");

                RenderToTexture(cam, rt, tex);
                string path = Path.Combine(outDir, $"regviz_{stateName}_eye_hud.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                savedPaths.Add(path);
            }

            // パネルの見かけ角を数値ログ（体感サイズの一次証拠）。distance は SerializeField を
            // 読み戻して二重定義を避ける。
            // ⚠ **枠を持つのは面の根ではなく字の RectTransform**（2026-09-14 に WorldSpace Canvas を
            //   やめ、3D の TextMeshPro へ移した）。根は素の Transform なので RectTransform へは落ちない。
            RectTransform? hudRt = hudTmp != null ? (RectTransform)hudTmp.transform : null;
            float dist = new SerializedObject(hud).FindProperty("distance").floatValue;
            float panelW = hudRt != null ? hudRt.rect.width * hudRt.lossyScale.x : 0f;
            float panelH = hudRt != null ? hudRt.rect.height * hudRt.lossyScale.y : 0f;
            // ⚠ 3D の TMP は透視カメラのとき内部で 0.1 を掛ける（HmdTextStyle.MeshWorldEm が唯一の正）。
            float lineH = hudTmp != null && hudRt != null
                ? FixedCamVr.Diagnostics.HmdTextStyle.MeshWorldEm(hudTmp.fontSize, hudRt.lossyScale.y)
                : 0f;
            float panelWDeg = 2f * Mathf.Atan2(panelW * 0.5f, dist) * Mathf.Rad2Deg;
            float panelHDeg = 2f * Mathf.Atan2(panelH * 0.5f, dist) * Mathf.Rad2Deg;
            float lineDeg = 2f * Mathf.Atan2(lineH * 0.5f, dist) * Mathf.Rad2Deg;
            Debug.Log($"[RegVizPreview] hud angular: panel={panelWDeg:F1}°x{panelHDeg:F1}° line={lineDeg:F1}° " +
                      $"(distance={dist:F2}m panel={panelW:F2}x{panelH:F2}m)");
        }

        // 実 StatusHud（UGUI）の姿勢・内容・メトリクスを TMP 3D へ写して手動レンダに乗せる。
        // TextMeshPro(3D) も RectTransform + fontSize の単位系は UGUI と同一なので見かけサイズは等価。
        private static TextMeshPro? _hudMirror;

        private static void UpdateHudMirror(GameObject root, StatusHud hud, TMP_Text? hudTmp,
            CourseRegistrationController ctrl)
        {
            if (_hudMirror == null)
            {
                var go = new GameObject("HudMirror3D");
                go.transform.SetParent(root.transform, false);
                _hudMirror = go.AddComponent<TextMeshPro>();
                _hudMirror.alignment = TextAlignmentOptions.Center;
                _hudMirror.enableWordWrapping = false;
                _hudMirror.richText = true;
                // Edit Mode では StatusHud.Awake（日本語フォント差し替え）が走らないため、ミラー側で同じ
                // ローダを通す（実機と同じ「日本語が読める」見た目を Editor でも検証するため）。
                var jp = FixedCamVr.Diagnostics.JapaneseHudFont.TryGet();
                if (jp != null) _hudMirror.font = jp;
            }
            // TMP は ExecuteAlways で OnEnable がフォント/マテリアルを再解決するため、作成時の 1 回でなく
            // 毎回代入し直す（Edit Mode で CJK が notdef 豆腐に化ける対策）。
            var jpFont = FixedCamVr.Diagnostics.JapaneseHudFont.TryGet();
            if (jpFont != null && _hudMirror.font != jpFont) _hudMirror.font = jpFont;

            // ⚠ 枠と倍率は**字の RectTransform**から取る（面の根は素の Transform・上の注記と同じ理由）。
            RectTransform? hudRt = hudTmp != null ? (RectTransform)hudTmp.transform : null;
            var mirrorRt = (RectTransform)_hudMirror.transform;
            if (hudRt != null)
            {
                mirrorRt.sizeDelta = hudRt.sizeDelta;
                mirrorRt.localScale = hudRt.lossyScale;
            }
            _hudMirror.transform.SetPositionAndRotation(hud.transform.position, hud.transform.rotation);
            _hudMirror.fontSize = hudTmp != null ? hudTmp.fontSize : 34f;
            _hudMirror.color = hudTmp != null ? hudTmp.color : Color.white;
            string content = hudTmp != null && !string.IsNullOrEmpty(hudTmp.text) ? hudTmp.text : ctrl.GuidanceText;
            // 動的アトラスは「メッシュ構築時に無いグリフ」を豆腐で組んでしまうことがある（実測）。
            // 表示文字列のグリフを先に焼いてからテキストを流し込む。
            if (_hudMirror.font != null && !string.IsNullOrEmpty(content))
            {
                bool ok = _hudMirror.font.TryAddCharacters(content, out string missing);
                Debug.Log($"[RegVizPreview] font diag: {_hudMirror.font.name} addOk={ok} " +
                          $"missing='{missing}' has床={_hudMirror.font.HasCharacter('床')}");
            }
            _hudMirror.text = content;
            _hudMirror.gameObject.layer = PreviewLayer;
            _hudMirror.ForceMeshUpdate();
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
