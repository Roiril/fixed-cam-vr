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

            // 床の影（canon/LEDGER.md 0024）。**数値は SealedBoxShadowLogic が唯一の正**で、
            // ここは置き方だけを実行時（SealedBox.PlaceShadow）と同じ形に真似る。
            // ⚠ 背景は一様な灰（パススルーの代わり）なので、影は**そこが暗くなる**形で出る。
            //   実機は同じ式で現実を (1 - a) 倍にする。
            GameObject? shadow = BuildShadow(out Material? shadowMat);

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
            // ⚠ **床の影は目線の高さからは画に入らない。** 目 1.6m で水平に見ると、いちばん手前に
            //   見える床は 2.8m 先 ＝ 影が落ちる範囲（面から 1m ほど）は視界の下に外れる。
            //   形そのものを判定するための 1 枚を上から撮る（現場の見えではない）。
            Shot(cam, "shadow", new Vector3(2.4f, 2.8f, -3.2f), new Vector3(0.5f, 0.1f, -0.7f));

            // ⚠ **静止画では光の走りを判定できない。** 連番を出して動画にする
            //    （`_PhaseSec` を進める — Edit Mode では `_Time` が走らない）。
            WaveSequence(cam, mat, new Vector3(0f, 1.6f, -3.6f), new Vector3(0f, 1.1f, 0f));

            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(box);
            if (shadow != null) Object.DestroyImmediate(shadow);
            Object.DestroyImmediate(mat);
            if (shadowMat != null) Object.DestroyImmediate(shadowMat);
            AssetDatabase.Refresh();
            Debug.Log($"[SealedBoxPreview] 5 枚 + 連番を {OutDir} へ出しました"
                      + "（模様を見るための絵。現場の見えではない）");
        }

        /// <summary>
        /// 床の影の板。<b>置き方は <see cref="SealedBox"/> の実行時と同じ</b>
        /// （<c>Euler(90, 0, 0)</c> で寝かせ、板のローカル XY を箱ローカルの XZ に一致させる）。
        /// ここが食い違うと、絵で見た向きと実機の向きが別になる。
        /// </summary>
        private static GameObject? BuildShadow(out Material? mat)
        {
            mat = null;
            Shader? shader = Shader.Find(SealedBox.ShadowShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SealedBoxPreview] シェーダ {SealedBox.ShadowShaderName} が見つかりません。"
                                 + "床の影は絵に出ません");
                return null;
            }

            var half = new Vector2(BoxW * 0.5f, BoxD * 0.5f);
            Vector2 sweep = SealedBoxShadowLogic.Sweep(
                SealedBoxShadowLogic.DefaultYawDeg, SealedBoxShadowLogic.DefaultElevationDeg, BoxH);
            Vector2 quad = SealedBoxShadowLogic.QuadSizeM(half, sweep);
            Vector2 center = SealedBoxShadowLogic.QuadCenterM(sweep);

            mat = new Material(shader) { name = "SealedBoxShadow (preview)" };
            mat.SetFloat("_Opacity", 1f);
            mat.SetFloat("_Density", SealedBoxShadowLogic.DefaultDensity);
            mat.SetVector("_HalfXZ", new Vector4(half.x, half.y, 0f, 0f));
            mat.SetVector("_Sweep", new Vector4(sweep.x, sweep.y, 0f, 0f));
            mat.SetVector("_QuadSizeM", new Vector4(quad.x, quad.y, 0f, 0f));
            mat.SetVector("_QuadCenterM", new Vector4(center.x, center.y, 0f, 0f));
            mat.SetFloat("_FeatherM", SealedBoxShadowLogic.FeatherM);
            mat.SetFloat("_FeatherNear", SealedBoxShadowLogic.FeatherNear);
            mat.SetFloat("_FeatherFar", SealedBoxShadowLogic.FeatherFar);
            mat.SetFloat("_FarDensity", SealedBoxShadowLogic.FarDensity);
            mat.SetFloat("_ContactM", SealedBoxShadowLogic.ContactM);
            mat.SetFloat("_ContactGain", SealedBoxShadowLogic.ContactGain);

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "[SealedBoxShadowPreview]";
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = new Vector3(center.x, SealedBoxShadowLogic.LiftM, center.y);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(quad.x, quad.y, 1f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
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
