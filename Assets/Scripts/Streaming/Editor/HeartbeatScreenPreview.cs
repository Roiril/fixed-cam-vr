#nullable enable
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>実シェーダで静止画と心音同期フレームを描画する。シーンは保存しない。</summary>
    public static class HeartbeatScreenPreview
    {
        [Serializable]
        private sealed class PreviewShow
        {
            public PostParams? post;
        }

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Heartbeat Screen")]
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dir = Path.Combine(root, "Logs", "heartbeat-ripple", "render");
            Directory.CreateDirectory(dir);
            string showPath = Path.Combine(root, "tools", "web-compositor", "show.json");
            var show = JsonUtility.FromJson<PreviewShow>(File.ReadAllText(showPath));
            PostParams post = show?.post ?? throw new InvalidOperationException("show.json の全体画像加工がありません: " + showPath);
            string source = Directory.GetFiles(Path.Combine(root, "tools", "web-compositor", "captures"), "plate_A*.jpg")
                .OrderByDescending(File.GetLastWriteTimeUtc).First();
            var plate = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!plate.LoadImage(File.ReadAllBytes(source))) throw new InvalidOperationException(source);
            Shader shader = Shader.Find("FixedCamVr/ScreenComposite");
            if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("ScreenComposite shader is unavailable or has errors");
            var mat = new Material(shader);
            var cameraObject = new GameObject("Heartbeat preview camera");
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var rt = new RenderTexture(960, 540, 24);
            var read = new Texture2D(960, 540, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                mat.SetTexture("_LiveTex", plate);
                // ShowControlClient.ApplyPostForActive と同じ項目を実シェーダへ渡す。
                // 2-C と3-Aは個別postを持たないため、この全体設定が体験中の値になる。
                mat.SetFloat("_Exposure", post.exposure);
                mat.SetFloat("_Contrast", post.contrast);
                mat.SetFloat("_Saturation", post.saturation);
                mat.SetFloat("_Temperature", post.temperature);
                mat.SetFloat("_Vignette", post.vignette);
                mat.SetFloat("_Grain", post.grain);
                mat.SetFloat("_Scanline", post.scanline);
                mat.SetFloat("_Lift", post.lift);
                mat.SetFloat("_Tint", post.tint);
                mat.SetFloat("_Aberration", post.aberration);
                mat.SetFloat("_Pixelate", post.pixelate);
                mat.SetFloat("_ScanlineCount", post.ResolveScanlineCount());
                mat.SetFloat("_FrameAspect", 16f / 9f);
                float sourceAspect = plate.width / (float)plate.height;
                mat.SetVector("_LiveScale", sourceAspect > 16f / 9f
                    ? new Vector4(1f, (16f / 9f) / sourceAspect, 0f, 0f)
                    : new Vector4(sourceAspect / (16f / 9f), 1f, 0f, 0f));
                quad.transform.position = new Vector3(0f, 2000f, 0f);
                quad.transform.localScale = new Vector3(16f / 9f, 1f, 1f);
                quad.GetComponent<Renderer>().sharedMaterial = mat;
                var cam = cameraObject.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = .5f;
                cam.nearClipPlane = .01f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.position = new Vector3(0f, 2000f, -1f);
                cam.targetTexture = rt;
                byte[] Draw(float time, float pulse, float vignette, string name)
                {
                    mat.SetFloat("_HeartTime", HeartbeatPulseLogic.Age(time));
                    mat.SetFloat("_HeartRipple", pulse);
                    mat.SetFloat("_HeartVignette", vignette);
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
                    read.Apply();
                    File.WriteAllBytes(Path.Combine(dir, name + ".png"), read.EncodeToPNG());
                    return read.GetRawTextureData<byte>().ToArray();
                }
                byte[] baseline = Draw(0f, 0f, 0f, "before");
                const float crest = .13f - .045f + HeartbeatPulseLogic.RiseSec;
                byte[] ripple = Draw(crest, 1f, 0f, "ripple-only");
                byte[] vignette = Draw(crest, 0f, CameraFeelFx.HeartVignetteBase, "vignette-only");
                Draw(crest, 1f, CameraFeelFx.HeartVignetteBase + CameraFeelFx.HeartVignettePulse, "peak");
                byte[] restored = Draw(2.11f, 0f, 0f, "after-freeze");
                if (!baseline.SequenceEqual(restored)) throw new InvalidOperationException("Restored image differs from baseline");
                if (baseline.SequenceEqual(ripple) || baseline.SequenceEqual(vignette))
                    throw new InvalidOperationException("Heartbeat effect did not change rendered pixels");
                // 最初の1秒は通常。次の8秒は追加加工。最後の1秒は凍結後の解除を示す。
                for (int frame = 0; frame < 300; frame++)
                {
                    float time = frame / 30f;
                    bool active = frame >= 30 && frame < 270;
                    float pulse = active ? HeartbeatPulseLogic.Evaluate(time) : 0f;
                    Draw(time, pulse, active ? CameraFeelFx.HeartVignetteBase
                        + CameraFeelFx.HeartVignettePulse * pulse : 0f, $"f{frame:0000}");
                }
                File.WriteAllText(Path.Combine(dir, "verification.txt"),
                    $"source={source}\npost=show.json global\nexposure={post.exposure}\ncontrast={post.contrast}\n"
                    + $"saturation={post.saturation}\ntemperature={post.temperature}\nvignette={post.vignette}\n"
                    + "frames=300\nripplePixelsChanged=true\nvignettePixelsChanged=true\nrestoredPixelsIdentical=true\n");
                Debug.Log("[HeartbeatPreview] 300 frames; ripple and vignette change pixels; reset is pixel-identical: " + dir);
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(quad);
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(read);
                UnityEngine.Object.DestroyImmediate(mat);
                UnityEngine.Object.DestroyImmediate(plate);
            }
        }
    }
}
