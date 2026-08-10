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
        private const int Width = 1280;
        private const int Height = 960;

        /// <summary>箱の寸法 (m)。show.json の床（1.8 四方）＋ 隔離の余白 0.6 と同じ既定。</summary>
        private const float BoxW = 3.0f;
        private const float BoxD = 3.0f;
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
            mat.SetFloat("_Opacity", 1f);
            mat.SetFloat("_HexSizeM", ParseFloat("hex", 0.45f));
            mat.SetFloat("_MarkDensity", ParseFloat("marks", 0.14f));

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
            Shot(cam, "far", new Vector3(0f, 1.6f, -3.6f), new Vector3(0f, 1.1f, 0f));
            Shot(cam, "near", new Vector3(1.4f, 1.6f, -2.6f), new Vector3(0f, 1.2f, 0f));
            Shot(cam, "surface", new Vector3(0.4f, 1.5f, -2.0f), new Vector3(0.3f, 1.5f, -1.5f));

            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(box);
            Object.DestroyImmediate(mat);
            AssetDatabase.Refresh();
            Debug.Log($"[SealedBoxPreview] 3 枚を {OutDir} へ出しました（模様を見るための絵。現場の見えではない）");
        }

        private static void Shot(Camera cam, string name, Vector3 eye, Vector3 lookAt)
        {
            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation(lookAt - eye, Vector3.up);

            // ⚠ sRGB を明示する。プロジェクトは Linear なので、linear のまま PNG へ書くと
            //    **実際より 2 段暗い絵**になり、明るさの判断を丸ごと誤る。
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB)
            {
                antiAliasing = 4,
            };
            cam.targetTexture = rt;
            cam.Render();

            // ⚠ **MSAA の RT から直接 ReadPixels しない。** 解決前の面を読むので、まだらな
            //    斑点が絵に混じる（2026-08-10 実測。模様の粒だと誤読しかけた）。1 度 Blit して落とす。
            var resolved = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
            Graphics.Blit(rt, resolved);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = resolved;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;

            File.WriteAllBytes(Path.Combine(OutDir, $"sealedbox_{name}.png"), tex.EncodeToPNG());
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
