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
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Heartbeat Screen")]
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string dir = Path.Combine(root, "Logs", "heartbeat-ripple", "render");
            Directory.CreateDirectory(dir);
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
                mat.SetFloat("_Vignette", .25f);
                mat.SetFloat("_Contrast", 1.05f);
                mat.SetFloat("_Exposure", .5f);
                mat.SetFloat("_Saturation", .65f);
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
                byte[] vignette = Draw(crest, 0f, .62f, "vignette-only");
                Draw(crest, 1f, .62f, "peak");
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
                    Draw(time, pulse, active ? .48f + .14f * pulse : 0f, $"f{frame:0000}");
                }
                File.WriteAllText(Path.Combine(dir, "verification.txt"),
                    $"source={source}\nframes=300\nripplePixelsChanged=true\nvignettePixelsChanged=true\nrestoredPixelsIdentical=true\n");
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
