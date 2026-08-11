#nullable enable

using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 段 4「見えているものが割れてスクリーンへ入る」を PNG で出す。<b>Play も HMD も要らない。</b>
    ///
    /// ⚠⚠ <b>これが破砕の唯一の安い門。</b> 破砕はシェーダの頂点計算なので
    /// <c>unity.ps1 test</c> には 1 件も出ない（コンパイルが通っても全面マゼンタになりうる）。
    /// 書いたら必ずここを通して絵を開くこと（<c>rules/show-design.md</c>「シェーダを書いたら絵を出す」）。
    ///
    /// <b>パススルーの合成まで真似る。</b> 覆いは <c>Blend Zero SrcAlpha</c> で
    /// 「現実を出す＝ alpha を 0 にする」だけなので、素直にカメラで撮ると
    /// <b>現実が出るはずの所が真っ黒</b>になって判断が逆になる。ここでは実機の合成と同じ
    ///   最終 = アプリの rgb + 現実 × (1 - アプリの alpha)
    /// を CPU で解いて、現実の代わりの絵を敷いてある。
    ///
    /// ⚠ <b>これは動きと形を見るための絵で、現場の見えではない。</b> 「現実」は手続きで描いた
    /// 明るい部屋で、実際の会場（暗い）とは違う。合否はここで出さない
    /// （<c>.claude/reference/why.md</c>「安い門は落とすためだけに使う」）。
    /// </summary>
    public static class IntroShatterPreview
    {
        private const string OutDir = "Assets/Screenshots/shatter";

        /// <summary>連番は <b>Assets の外</b>（毎回 60 枚をインポートさせない）。</summary>
        private const string SeqDir = "logs/shatter";

        private const int Width = 1280;
        private const int Height = 960;
        private const int SeqFrames = 60;

        // 実機の段 4 と同じ立ち位置（logs/evidence/20260811_200400 の実測: 箱の面から 0.5m ほど）。
        private const float BoxW = 3.0f;
        private const float BoxD = 3.0f;
        private const float BoxH = 2.4f;
        private const float EyeH = 1.6f;
        private const float StandOffM = 0.6f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Intro Shatter", priority = 237)]
        public static void Run()
        {
            Shader? veilShader = Shader.Find("FixedCamVr/IntroVeil");
            Shader? boxShader = Shader.Find(SealedBox.ShaderName);
            if (veilShader == null || boxShader == null)
            {
                Debug.LogError("[IntroShatterPreview] シェーダが見つかりません " +
                               $"(veil={veilShader != null} box={boxShader != null})");
                return;
            }

            Directory.CreateDirectory(OutDir);
            Texture2D reality = BuildRealityTexture(Width, Height);

            var root = new GameObject("[IntroShatterPreview]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // --- 頭（覆いはこの下にぶら下がる。実機と同じ head-lock）---
                var head = new GameObject("Head").transform;
                head.SetParent(root.transform, false);
                head.position = new Vector3(0f, EyeH, -(BoxD * 0.5f + StandOffM));
                head.rotation = Quaternion.identity;

                var cam = new GameObject("Cam").AddComponent<Camera>();
                cam.transform.SetParent(head, false);
                cam.clearFlags = CameraClearFlags.SolidColor;
                // ⚠ alpha は 1 で始める。**アプリが描かない所は現実が出ない**のが実機の規約で、
                //    覆いが alpha を 0 へ落とした所にだけ現実が出る。
                cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
                cam.fieldOfView = 90f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 50f;

                // --- 本編のスクリーン（破片の行き先）---
                var screen = GameObject.CreatePrimitive(PrimitiveType.Quad).transform;
                Object.DestroyImmediate(screen.GetComponent<Collider>());
                screen.SetParent(head, false);
                screen.localPosition = new Vector3(0f, -0.28f, 2.0f);
                screen.localScale = new Vector3(2.366f, 1.332f, 1f);   // 実測の半画角 30.6°/18.4°
                var screenMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
                { name = "ShatterPreviewScreen" };
                screenMat.SetColor("_BaseColor", new Color(0.05f, 0.06f, 0.08f, 1f));
                screen.GetComponent<MeshRenderer>().sharedMaterial = screenMat;

                // --- 封印の箱（破砕の主役）---
                var boxGo = new GameObject("SealedBox");
                boxGo.transform.SetParent(root.transform, false);
                boxGo.transform.position = new Vector3(0f, BoxH * 0.5f, 0f);
                boxGo.transform.localScale = new Vector3(BoxW, BoxH, BoxD);
                Mesh boxMesh = SealedBoxShatterMesh.Build(new Vector3(BoxW, BoxH, BoxD), out int cells);
                boxGo.AddComponent<MeshFilter>().sharedMesh = boxMesh;
                var boxMat = new Material(boxShader) { name = "SealedBox (preview)" };
                boxMat.SetFloat("_Opacity", 1f);
                boxMat.SetFloat("_HexSizeM", 0.45f);
                boxMat.SetFloat("_GlowGain", 1.0f);
                boxMat.SetVector("_BoxSize", new Vector4(BoxW, BoxH, BoxD, 0f));
                var boxRenderer = boxGo.AddComponent<MeshRenderer>();
                boxRenderer.sharedMaterial = boxMat;
                boxRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // --- 覆い（本物の IntroVeil を使う。開口の平面と global の配布まで実機と同じ）---
                var veilGo = new GameObject("IntroVeil");
                veilGo.transform.SetParent(head, false);
                var veil = veilGo.AddComponent<IntroVeil>();
                if (!veil.IsBuilt) veilGo.SendMessage("Awake", SendMessageOptions.DontRequireReceiver);
                if (!veil.IsBuilt)
                {
                    Debug.LogError("[IntroShatterPreview] 覆いの実体を組めませんでした（Shader.Find が null）");
                    return;
                }
                var so = new SerializedObject(veil);
                so.FindProperty("screenQuad").objectReferenceValue = screen;
                so.ApplyModifiedPropertiesWithoutUndo();

                Debug.Log($"[IntroShatterPreview] 破片: 封印の箱 {cells} 枚 / 覆い " +
                          $"{IntroVeilShatterMesh.CellCount} 枚");

                // --- 段 4 の重みは**本物の状態機械から取る**（プレビュー用の別式を書かない）---
                float[] shots = { 0f, 0.12f, 0.25f, 0.35f, 0.5f, 0.65f, 0.8f, 0.9f, 0.99f };
                foreach (float p in shots)
                {
                    ApplyStage(veil, boxMat, p);
                    Capture(cam, reality, Path.Combine(OutDir, $"shatter_p{Mathf.RoundToInt(p * 100):D2}.png"));
                }

                Directory.CreateDirectory(SeqDir);
                foreach (string old in Directory.GetFiles(SeqDir, "shatter_*.png")) File.Delete(old);
                for (int f = 0; f < SeqFrames; f++)
                {
                    float p = Mathf.Min(f / (SeqFrames - 1f), 0.999f);
                    ApplyStage(veil, boxMat, p);
                    Capture(cam, reality, Path.Combine(SeqDir, $"shatter_{f:D3}.png"));
                }

                Object.DestroyImmediate(boxMesh);
                Object.DestroyImmediate(boxMat);
                Object.DestroyImmediate(screenMat);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(reality);
                AssetDatabase.Refresh();
            }

            Debug.Log($"[IntroShatterPreview] 静止 9 枚 → {OutDir} / 連番 {SeqFrames} 枚 → {SeqDir}"
                      + "（動きと形を見るための絵。現場の見えではない）");
        }

        /// <summary>段 4 の進み <paramref name="p"/> における重みを本物の <see cref="IntroLogic"/> から取る。</summary>
        private static void ApplyStage(IntroVeil veil, Material boxMat, float p)
        {
            IntroWeights w = FrameWeightsAt(p);
            veil.Apply(w);                                   // 開口・global・覆いのセルまで実機と同じ
            IntroShatterCurve.PushBox(boxMat, IntroShatterCurve.BoxShatter(w.shatter));
        }

        /// <summary>
        /// 段 4 を進み <paramref name="p"/> まで走らせた重み。**プレビュー用に式を写さない** —
        /// 写した瞬間に「卓では正しいのに実機が違う」を作る。
        /// </summary>
        private static IntroWeights FrameWeightsAt(float p)
        {
            IntroTiming t = IntroTiming.Default;
            var logic = new IntroLogic();
            logic.Configure(t);
            logic.Begin();
            var input = new IntroInput
            {
                blackCleared = true, headTurnDegPerSec = 0f, frameCentered = true,
                liveFresh = true, recentered = false, outsideBoxM = 1.5f,
            };
            for (int i = 0; i < 4; i++)   // 段 0 → 1 → 2 → 3 → 4
            {
                logic.RequestAdvance();
                logic.Tick(0.001f, input);
            }
            logic.Tick(Mathf.Clamp01(p) * t.frameSec, input);
            return logic.Weights;
        }

        private static void Capture(Camera cam, Texture2D reality, string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB)
            { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();

            // ⚠ MSAA の RT から直接 ReadPixels しない（解決前の面を読むとまだらになる・2026-08-10 実測）。
            var resolved = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
            Graphics.Blit(rt, resolved);

            RenderTexture.active = resolved;
            var app = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            app.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            app.Apply();
            // ⚠ active のまま Release すると Unity が毎回警告を出す（無害だがログが埋まる）。
            RenderTexture.active = null;
            cam.targetTexture = null;

            // ---- パススルーの合成（Passthrough Windows と同じ式）----
            Color32[] a = app.GetPixels32();
            Color32[] r = reality.GetPixels32();
            for (int i = 0; i < a.Length; i++)
            {
                float inv = 1f - a[i].a / 255f;
                a[i] = new Color32(
                    (byte)Mathf.Min(255f, a[i].r + r[i].r * inv),
                    (byte)Mathf.Min(255f, a[i].g + r[i].g * inv),
                    (byte)Mathf.Min(255f, a[i].b + r[i].b * inv),
                    255);
            }
            app.SetPixels32(a);
            app.Apply();

            File.WriteAllBytes(path, app.EncodeToPNG());
            Object.DestroyImmediate(app);
            resolved.Release();
            Object.DestroyImmediate(resolved);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        /// <summary>
        /// パススルーの代わりに敷く「現実」。<b>わざと構造と明暗を持たせる</b> —
        /// 一様な地だと「窓が動いている」のか「破片が飛んでいる」のかを判定できない
        /// （そこがこの方式の唯一の賭けなので、いちばん厳しい地で見る）。
        /// </summary>
        private static Texture2D BuildRealityTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float fy = y / (float)h;
                for (int x = 0; x < w; x++)
                {
                    float fx = x / (float)w;
                    // 床（下 35%）と壁。壁には棚の縦縞、天井近くに照明。
                    float v = fy < 0.35f ? 0.16f + 0.10f * fy : 0.30f;
                    if (fy >= 0.35f)
                    {
                        v += 0.12f * Mathf.Round(Mathf.Repeat(fx * 9f, 1f));   // 縦の棚
                        if (fy > 0.80f && Mathf.Repeat(fx * 3f, 1f) < 0.22f) v = 0.92f;  // 照明
                    }
                    v += (Mathf.PerlinNoise(fx * 40f, fy * 40f) - 0.5f) * 0.06f;
                    v = Mathf.Clamp01(v);
                    px[y * w + x] = new Color(v * 0.98f, v, v * 0.94f, 1f);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
