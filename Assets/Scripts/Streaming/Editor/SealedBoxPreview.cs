#nullable enable

using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 封印の箱（<see cref="SealedBox"/>）の見た目を PNG で出す。<b>Play も HMD も要らない。</b>
    ///
    /// 何のためか: 箱は<b>体験エリアの外に立っている人にしか見えない</b>ので、端末を机に置いた
    /// 自動走行では出ないことがある（中に置いてあれば背面カリングで消える）。それでも模様の
    /// 粗さ・線の太さ・印の密度・不気味さは<b>絵でしか判定できない</b>ので、ここで見る。
    ///
    /// ⚠ <b>これは模様を見るための絵で、現場の見えではない。</b> 背景はパススルーの代わりの
    /// 一様なグレーで、明るさも実際の部屋とは違う。合否はここで出さない
    /// （<c>.claude/reference/why.md</c>「安い門は落とすためだけに使う」）。
    /// </summary>
    public static class SealedBoxPreview
    {
        private const string OutDir = "Assets/Screenshots/sealedbox";

        /// <summary>光の走りを見るための連番。<b>Assets の外</b>（毎回 100 枚をインポートさせない）。</summary>
        private const string WaveDir = "logs/sealedbox";

        private const int Width = 1280;
        private const int Height = 960;
        private const int WaveWidth = 960;
        private const int WaveHeight = 720;

        /// <summary>連番の枚数と間隔。上下の波 5s と横の波 7.5s が重なるので 10 秒ぶん見る。</summary>
        private const int WaveFrames = 120;
        private const float WaveDt = 1f / 12f;

        /// <summary>
        /// 箱の寸法 (m)。<b>show.json の床そのもの</b>（歩ける範囲 1.8 四方・
        /// <c>ContainmentShellLogic.FloorMarginM</c> は 0）。
        /// ⚠ **実行時の寸法と揃える**。ここだけ古い値（3.0）のままだと、
        /// 絵の中の六角の粗さ・線の太さ・光の走る速さが実機と全部ずれる。
        /// </summary>
        private const float BoxW = 1.8f;
        private const float BoxD = 1.8f;
        private const float BoxH = 2.4f;

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Sealed Box", priority = 236)]
        public static void Run()
        {
            Shader? shader = Shader.Find(SealedBox.ShaderName);
            if (shader == null)
            {
                Debug.LogError($"[SealedBoxPreview] シェーダ {SealedBox.ShaderName} が見つかりません。");
                return;
            }

            Directory.CreateDirectory(OutDir);
            var mat = new Material(shader) { name = "SealedBox (preview)" };
            // ⚠ 地の版は**実行時と同じ経路**で当てる。ここを忘れると「Editor ではのっぺり、
            //    実機では質感あり」になり、絵を見ても質感の良し悪しを判定できない。
            SealedBox.ApplyWearTexture(mat);
            mat.SetFloat("_Opacity", 1f);
            mat.SetFloat("_HexSizeM", ParseFloat("hex", 0.45f));
            mat.SetFloat("_GlowGain", ParseFloat("glow", 1.0f));
            mat.SetFloat("_LineWidth", ParseFloat("line", 0.012f));
            mat.SetVector("_BoxSize", new Vector4(BoxW, BoxH, BoxD, 0f));
            mat.SetFloat("_SeamFadeM", ParseFloat("seam", 0.18f));

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "[SealedBoxPreview]";
            box.hideFlags = HideFlags.HideAndDontSave;
            Object.DestroyImmediate(box.GetComponent<Collider>());
            box.transform.position = new Vector3(0f, BoxH * 0.5f, 0f);
            box.transform.localScale = new Vector3(BoxW, BoxH, BoxD);
            box.GetComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo = new GameObject("[SealedBoxPreviewCam]") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            // パススルーの代わりの地。**明るすぎない灰**（実際の部屋は暗い）。
            cam.backgroundColor = new Color(0.16f, 0.16f, 0.155f, 1f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;

            // 立って見た 3 通り。近い順に「気づく → 近づく → 触れそう」。
            // ⚠⚠ **`far` は「箱が画面に収まる距離」から決める。武装距離から決めない**（2026-08-12 実測）。
            //    武装に要るのは矩形の外 1.35m だが、そこは**面までが 1.35m しかない**。
            //    箱は高さ 2.4m・目線 1.6m なので上下の見かけは 80° になり、画角 60° に入らない
            //    ＝ 面が画面を埋めて「箱が大きすぎて分からない」を絵の側で再現してしまう。
            //    収めるには面まで 1.6/tan(30°) = 2.8m 要る。
            // ⚠ **これは絵の都合であって、現場の立ち位置ではない。** 実際の体験者は武装距離に居るので、
            //    箱は視界を埋める。「1 個の物として読めるか」は `far`、「近づいたときの圧」は `near`。
            float half = BoxW * 0.5f;
            float eye = 1.6f;
            float fit = eye / Mathf.Tan(30f * Mathf.Deg2Rad);   // 上下が画角に収まる、面までの距離
            Shot(cam, "far", new Vector3(0f, eye, -(half + fit)), new Vector3(0f, BoxH * 0.45f, 0f));
            Shot(cam, "near", new Vector3(half * 0.8f, eye, -(half + 1.35f)), new Vector3(0f, 1.2f, 0f));
            Shot(cam, "surface", new Vector3(0.25f, 1.5f, -(half + 0.35f)), new Vector3(0.2f, 1.5f, -half));
            // 継ぎ目を見るための 2 枚。**角と天面の縁は模様が繋がるかどうかの本題**なので、
            // 正面から撮った絵だけでは判定できない。
            Shot(cam, "corner", new Vector3(half + fit * 0.8f, eye, -(half + fit * 0.8f)),
                 new Vector3(half * 0.5f, BoxH * 0.45f, -half * 0.5f));
            // ⚠ 天面と側面の継ぎ目は、**目線 1.6m では高さ 2.4m の箱の上が見えない**。
            //    見上げるのではなく、少し離れて上を向く（現場でも人はこう見る）。
            Shot(cam, "topedge", new Vector3(0.35f, eye, -(half + 1.2f)),
                 new Vector3(0.15f, BoxH + 0.15f, -half * 0.6f));

            // ⚠ **静止画では光の走りを判定できない。** 連番を出して動画にする
            //    （`_PhaseSec` を進める — Edit Mode では `_Time` が走らない）。
            WaveSequence(cam, mat, new Vector3(0f, 1.6f, -3.6f), new Vector3(0f, 1.1f, 0f));

            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(box);
            Object.DestroyImmediate(mat);
            AssetDatabase.Refresh();
            Debug.Log($"[SealedBoxPreview] 3 枚を {OutDir} へ出しました（模様を見るための絵。現場の見えではない）");
        }

        /// <summary>光の走りを連番で出す。<see cref="WaveDir"/> は Assets の外。</summary>
        private static void WaveSequence(Camera cam, Material mat, Vector3 eye, Vector3 lookAt)
        {
            Directory.CreateDirectory(WaveDir);
            foreach (string old in Directory.GetFiles(WaveDir, "wave_*.png")) File.Delete(old);

            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation(lookAt - eye, Vector3.up);
            for (int f = 0; f < WaveFrames; f++)
            {
                mat.SetFloat("_PhaseSec", f * WaveDt);
                Capture(cam, WaveWidth, WaveHeight, Path.Combine(WaveDir, $"wave_{f:D3}.png"));
            }
            mat.SetFloat("_PhaseSec", 0f);
            Debug.Log($"[SealedBoxPreview] 光の走り {WaveFrames} 枚 → {WaveDir}"
                      + $"（{WaveDt:F3}s 刻み・{WaveFrames * WaveDt:F1} 秒ぶん）");
        }

        private static void Shot(Camera cam, string name, Vector3 eye, Vector3 lookAt)
        {
            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation(lookAt - eye, Vector3.up);
            Capture(cam, Width, Height, Path.Combine(OutDir, $"sealedbox_{name}.png"));
        }

        private static void Capture(Camera cam, int w, int h, string path)
        {
            // ⚠ sRGB を明示する。プロジェクトは Linear なので、linear のまま PNG へ書くと
            //    **実際より 2 段暗い絵**になり、明るさの判断を丸ごと誤る。
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB)
            {
                antiAliasing = 4,
            };
            cam.targetTexture = rt;
            cam.Render();

            // ⚠ **MSAA の RT から直接 ReadPixels しない。** 解決前の面を読むので、まだらな
            //    斑点が絵に混じる（2026-08-10 実測。模様の粒だと誤読しかけた）。1 度 Blit して落とす。
            var resolved = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
            Graphics.Blit(rt, resolved);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = resolved;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            resolved.Release();
            Object.DestroyImmediate(resolved);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        private static float ParseFloat(string key, float fallback)
        {
            string? v = EditorCliArgs.Get(key);
            return !string.IsNullOrEmpty(v) && float.TryParse(v, out float f) ? f : fallback;
        }
    }
}
