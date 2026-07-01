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

        private static bool _applyMotion = true;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Hand Variants (screenshot)", priority = 211)]
        public static void CapturePosed() { _applyMotion = true; Capture(); }

        // 指を動かさず各モデルの authored rest（バインド）だけを見る診断。崩れが retarget 由来かモデル由来か切り分ける。
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Hand Variants Rest (screenshot)", priority = 212)]
        public static void CaptureRest() { _applyMotion = false; Capture(); }

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
            var frame = data.Frames[PickExpressiveFrame(data)];
            var ovrBind = data.LayoutR; // 送信元の手 bind（= リターゲットの ovrBind）

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath,
                _applyMotion ? "../Temp/HandVariantPreview" : "../Temp/HandVariantPreview/rest"));
            Directory.CreateDirectory(dir);

            GameObject? root = null, camGo = null, lightGo = null;
            try
            {
                root = new GameObject("HandVariantPreview");
                float[] xs = { -0.30f, 0f, 0.30f };
                for (int v = 0; v < Variants.Length; v++)
                {
                    var anchor = new GameObject($"{Variants[v]}").transform;
                    anchor.SetParent(root.transform, false);
                    anchor.localPosition = new Vector3(xs[v], 0f, 0f);
                    anchor.localRotation = frame.WristRotR; // 実機と同じ: 手首の向きを anchor に、bone は相対
                    BuildHand(provider, anchor, Variants[v], frame, ovrBind);
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
                cam.backgroundColor = new Color(0.45f, 0.47f, 0.50f); // 白手も黒ロボットも見えるミッドグレー
                cam.cullingMask = 1 << Layer;

                // 手は手首から +x 方向へ伸びるので aim を少し +x に寄せて 3 体を収める
                var aim = new Vector3(0.06f, -0.02f, 0f);
                Shot(cam, dir, "01_front.png", new Vector3(0.06f, 0.0f, -1.75f), aim);
                Shot(cam, dir, "02_threequarter.png", new Vector3(-0.85f, 0.45f, -1.35f), aim);
                Shot(cam, dir, "03_top.png", new Vector3(0.06f, 1.5f, -0.4f), aim);

                Debug.Log($"[TableDuo] 手バリアントプレビュー保存 → {dir}\n" +
                          "左=Default(白手) / 中=Realistic(人間) / 右=Robot。3種が同じ握り形なら指リターゲットOK。");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (camGo != null) Object.DestroyImmediate(camGo);
                if (lightGo != null) Object.DestroyImmediate(lightGo);
            }
        }

        private static void BuildHand(RemoteHandMeshProvider provider, Transform anchor, HandVariant variant,
            AvatarPose frame, HandSkeletonLayout? ovrBind)
        {
            if (HandVariantTable.IsExternalRig(variant))
            {
                var built = provider.BuildExternalHand(anchor, isRight: true, variant);
                if (built == null) { Debug.LogWarning($"[TableDuo] {variant} の構築に失敗"); return; }
                if (!_applyMotion) return; // rest 診断: retarget せず authored bind のまま
                int n = Mathf.Min(frame.BonesR.Length, built.Bones.Length);
                for (int i = 0; i < n; i++)
                {
                    if (built.Bones[i] == null) continue;
                    var bind = (ovrBind != null && i < ovrBind.BoneCount) ? ovrBind.BindLocalRot[i] : Quaternion.identity;
                    built.Bones[i]!.localRotation = HandRetarget.Solve(frame.BonesR[i], bind, built.VarBind[i]);
                }
                return;
            }

            // Default: Meta 白手を同期 bone で直接駆動（正解基準）
            var prefab = provider.GetPrefab(isRight: true, HandVariant.Default);
            if (prefab == null) { Debug.LogWarning("[TableDuo] Default プレハブ（OVRCustomHandPrefab_R）が無い"); return; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.SetActive(true);
            if (_applyMotion)
            {
                var mapped = RemoteHandMeshProvider.MapHandBonesByName(inst.transform, isRight: true, HandVariant.Default);
                int m = Mathf.Min(frame.BonesR.Length, mapped.Length);
                for (int i = 0; i < m; i++)
                    if (mapped[i] != null) mapped[i]!.localRotation = frame.BonesR[i];
            }
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null && provider.HandMaterial != null) smr.sharedMaterial = provider.HandMaterial;
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
