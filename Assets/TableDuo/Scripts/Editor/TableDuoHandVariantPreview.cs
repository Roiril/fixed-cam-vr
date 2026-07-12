#nullable enable
using System.IO;
using TableDuoVr.Hands;
using TableDuoVr.Hands.Playback;
using TableDuoVr.Net;
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 手の見た目 3 バリアント（Default / Realistic / Robot）を **Play / 実機なし**で並べて撮るスクショツール。
    /// 実トラッキング録画（TestData/tdv_handrec_real_*.bin）の 1 フレームを 3 種すべてに同じように当て、
    /// - Default = Meta 白手（同期 bone を直接代入 = 正解基準）
    /// - Realistic/Robot = バインド差分リターゲット（RemoteAvatarView 実機と同じ経路）
    /// を左→右に並べる。Default と同じ握り/伸ばしになっていれば指のリターゲットが効いている。
    /// 材質のマゼンタ化・スケール・配置・指の曲がり軸を Editor だけで確認できる。
    ///
    /// 出力: &lt;project&gt;/Temp/HandVariantPreview/（gitignore・Read で確認）。シーンは保存しない。
    /// </summary>
    public static class TableDuoHandVariantPreview
    {
        private const int Size = 900;
        private const int Layer = 31;
        private static readonly HandVariant[] Variants = { HandVariant.Default, HandVariant.Realistic, HandVariant.Robot };

        // rest（bind）と fist（最曲がり録画フレーム）の 2 ポーズを回し、各バリアントを白手リファレンスと
        // 指ごとに数値比較 + スクショ（Temp/HandVariantPreview/rest・fist/）。実機と同一の参照コピー式経路。
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Hand Variants (screenshot)", priority = 211)]
        public static void Capture()
        {
            var provider = Object.FindObjectOfType<RemoteHandMeshProvider>();
            if (provider == null)
            {
                Debug.LogError("[TableDuo] RemoteHandMeshProvider がシーンに無い。TableDuoMain を開いて Setup 済みか確認。");
                return;
            }

            var data = LoadRecording();
            if (data == null || data.Frames.Count == 0)
            {
                Debug.LogError("[TableDuo] 手録画が読めない（TestData/tdv_handrec_real_*.bin）。");
                return;
            }
            var ovrBind = data.LayoutR; // 送信元の手 bind（= リターゲットの ovrBind）
            if (ovrBind == null) { Debug.LogError("[TableDuo] 録画に手 layout（bind）が無い。"); return; }
            var fistFrame = data.Frames[PickExpressiveFrame(data)];

            // rest = bind（静止・開き手）と fist（最も曲がった録画フレーム）の 2 ポーズを回す。
            // 実機指摘（静止で指が曲がる／握りで指が交わる）は rest と fist を別々に見ないと切り分かない。
            var poses = new (string name, Quaternion[] bonesR, Quaternion wristRot)[]
            {
                ("rest", ovrBind.BindLocalRot, fistFrame.WristRotR),
                ("fist", fistFrame.BonesR, fistFrame.WristRotR),
            };

            var sb = new System.Text.StringBuilder("[TableDuo] 手バリアント忠実度診断（pack vs 同コンテナ白手・指ごと世界方向差）:\n");
            foreach (var (poseName, bonesR, wristRot) in poses)
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, $"../Temp/HandVariantPreview/{poseName}"));
                Directory.CreateDirectory(dir);
                sb.AppendLine($"[{poseName}]");

                GameObject? root = null, camGo = null, lightGo = null;
                try
                {
                    root = new GameObject("HandVariantPreview");
                    var vlist = Variants;
                    float[] xs = { -0.30f, 0f, 0.30f };
                    for (int v = 0; v < vlist.Length; v++)
                    {
                        var anchor = new GameObject($"{vlist[v]}").transform;
                        anchor.SetParent(root.transform, false);
                        anchor.localPosition = new Vector3(xs[v], 0f, 0f);
                        anchor.localRotation = wristRot; // 実機と同じ: 手首の向きを anchor に、bone は相対
                        var built = BuildHand(provider, anchor, vlist[v], bonesR, ovrBind);
                        if (built != null) AppendFingerFidelity(sb, vlist[v], built);
                    }

                    foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        smr.forceMatrixRecalculationPerRender = true;
                    SetLayerRecursive(root, Layer);

                    lightGo = new GameObject("PreviewLight");
                    var light = lightGo.AddComponent<Light>();
                    light.type = LightType.Directional;
                    light.intensity = 1.15f;
                    light.transform.rotation = Quaternion.Euler(35f, -20f, 0f);

                    camGo = new GameObject("PreviewCam");
                    var cam = camGo.AddComponent<Camera>();
                    cam.fieldOfView = 30f;
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 20f;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.45f, 0.47f, 0.50f);
                    cam.cullingMask = 1 << Layer;

                    var aim = new Vector3(0.06f, -0.02f, 0f);
                    Shot(cam, dir, "01_front.png", new Vector3(0.06f, 0.0f, -1.75f), aim);
                    Shot(cam, dir, "02_threequarter.png", new Vector3(-0.85f, 0.45f, -1.35f), aim);
                    Shot(cam, dir, "03_top.png", new Vector3(0.06f, 1.5f, -0.4f), aim);
                }
                finally
                {
                    if (root != null) Object.DestroyImmediate(root);
                    if (camGo != null) Object.DestroyImmediate(camGo);
                    if (lightGo != null) Object.DestroyImmediate(lightGo);
                }
            }

            Debug.Log(sb.ToString());
            File.WriteAllText(Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Temp/HandVariantPreview/directions.txt")), sb.ToString());
            Debug.Log("[TableDuo] 忠実度診断 → Temp/HandVariantPreview/directions.txt / rest・fist の各スクショ");
        }

        // 指ごとの (mcp→tip) BoneId。pack と白手リファレンスを同 BoneId で比較する。
        private static readonly (string name, int mcp, int tip)[] Fingers =
        {
            ("thumb", 3, 5), ("index", 6, 8), ("middle", 9, 11), ("ring", 12, 14), ("pinky", 16, 18),
        };

        /// <summary>pack 各指の (mcp→tip) 世界方向を、同コンテナの白手リファレンス（正解）と比較して角度差を出す。
        /// pack は白手を「なぞる」設計なので、全指 &lt;~15° なら忠実。大きい指＝その指のリターゲット/マッピング不良。
        /// サイズも wrist→middle3 の長さ比で白手と比較（1.00 なら一致）。</summary>
        private static void AppendFingerFidelity(System.Text.StringBuilder sb, HandVariant variant,
            RemoteHandMeshProvider.BuiltHand built)
        {
            var pack = built.Bones; var white = built.MetaBones;
            float sizeRatio = -1f;
            if (pack[0] != null && pack[11] != null && white[0] != null && white[11] != null)
            {
                float wl = Vector3.Distance(white[0]!.position, white[11]!.position);
                float pl = Vector3.Distance(pack[0]!.position, pack[11]!.position);
                if (wl > 1e-5f) sizeRatio = pl / wl;
            }
            sb.Append($"  {variant}: sizeRatio={sizeRatio:F2}");
            foreach (var (fname, mcp, tip) in Fingers)
            {
                if (mcp >= pack.Length || tip >= pack.Length ||
                    pack[mcp] == null || pack[tip] == null || white[mcp] == null || white[tip] == null)
                { sb.Append($" {fname}=NA"); continue; }
                Vector3 pd = (pack[tip]!.position - pack[mcp]!.position).normalized;
                Vector3 wd = (white[tip]!.position - white[mcp]!.position).normalized;
                sb.Append($" {fname}={Vector3.Angle(pd, wd):F0}°");
            }
            sb.AppendLine();
        }

        /// <summary>anchor 下に variant 手を建て、bonesR で駆動する。外部リグは BuiltHand を返す
        /// （忠実度診断が MetaBones/Bones を読む）。Default は白手直接駆動で null を返す。</summary>
        private static RemoteHandMeshProvider.BuiltHand? BuildHand(RemoteHandMeshProvider provider,
            Transform anchor, HandVariant variant, Quaternion[] bonesR, HandSkeletonLayout ovrBind)
        {
            if (HandVariantTable.IsExternalRig(variant))
            {
                // 実機（LocalVariantHand / RemoteHandView）と同じ参照コピー式で駆動。restPose=bind で
                // オフセット捕捉を rest 基準にする（実機と同一経路）
                var built = provider.BuildExternalHand(anchor, isRight: true, variant, ovrBind);
                if (built == null) { Debug.LogWarning($"[TableDuo] {variant} の構築に失敗"); return null; }
                HandRetarget.ApplyFromReference(bonesR, built.MetaBones, built.Bones, built.BoneOffsets, smooth: 1f);
                return built;
            }

            // Default: Meta 白手を同期 bone で直接駆動（正解基準）
            var prefab = provider.GetPrefab(isRight: true, HandVariant.Default);
            if (prefab == null) { Debug.LogWarning("[TableDuo] Default プレハブ（OVRCustomHandPrefab_R）が無い"); return null; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.SetActive(true);
            var mapped = RemoteHandMeshProvider.MapHandBonesByName(inst.transform, isRight: true, HandVariant.Default);
            int m = Mathf.Min(bonesR.Length, mapped.Length);
            for (int i = 0; i < m; i++)
                if (mapped[i] != null) mapped[i]!.localRotation = bonesR[i];
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null && provider.HandMaterial != null) smr.sharedMaterial = provider.HandMaterial;
            return null;
        }

        /// <summary>指がよく曲がっている（bind から角度が大きい）フレームを選ぶ＝表情のある一枚。</summary>
        private static int PickExpressiveFrame(PoseRecordingFile.Data data)
        {
            var bind = data.LayoutR;
            int best = data.Frames.Count / 2;
            if (bind == null) return best;
            float bestScore = -1f;
            for (int f = 0; f < data.Frames.Count; f++)
            {
                var pose = data.Frames[f];
                if (!pose.TrackedR) continue;
                float s = 0f;
                for (int i = 2; i <= 18 && i < bind.BoneCount; i++)
                    s += Quaternion.Angle(pose.BonesR[i], bind.BindLocalRot[i]);
                if (s > bestScore) { bestScore = s; best = f; }
            }
            return best;
        }

        private static PoseRecordingFile.Data? LoadRecording()
        {
            string proj = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (var rel in new[] { "TestData/tdv_handrec_real_20260610.bin" })
            {
                var d = PoseRecordingFile.Load(Path.Combine(proj, rel));
                if (d != null && d.Frames.Count > 0) return d;
            }
            // フォールバック: TestData 内の任意の手録画
            var td = Path.Combine(proj, "TestData");
            if (Directory.Exists(td))
                foreach (var file in Directory.GetFiles(td, "tdv_handrec*.bin"))
                {
                    var d = PoseRecordingFile.Load(file);
                    if (d != null && d.Frames.Count > 0) return d;
                }
            return null;
        }

        private static void Shot(Camera cam, string dir, string file, Vector3 camPos, Vector3 aim)
        {
            cam.transform.position = camPos;
            cam.transform.rotation = Quaternion.LookRotation(aim - camPos, Vector3.up);

            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(Path.Combine(dir, file), tex.EncodeToPNG());

            cam.targetTexture = null;
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }
    }
}
