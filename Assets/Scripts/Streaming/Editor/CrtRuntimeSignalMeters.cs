#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class CrtReferencePreview
    {
        [Serializable] private sealed class RuntimeSignalMeters
        {
            public float filterIdentityMaxError, historicalFilterMaxError, colorDcMaxError, gainRatioMaxError, uvMaxError, uvWrongYError;
            public long auxiliaryCameraUpdates, mainCameraUpdates, repeatedMainUpdates, reenabledUpdates;
            public bool disabledDisposed, reenabledRebuilt;
        }

        private static void ValidateRuntimeSignalMeters(Camera camera, Renderer renderer, GameObject screen, Mesh curved, string output, RuntimeSignalMeters result, List<UnityEngine.Object> objects)
        {
            var runtime = new Material(CrtSignalResources.Load().SignalFilter); objects.Add(runtime);
            var old = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/CrtSignalFilter.shader")); objects.Add(old);
            var source = LensTarget(128, 64, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
            var target = LensTarget(128, 64, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
            for (int pattern = 0; pattern < 3; pattern++)
            {
                var texture = new Texture2D(128, 64, TextureFormat.RGBAFloat, false, true) { wrapMode = TextureWrapMode.Clamp }; objects.Add(texture);
                var pixels = new Color[128 * 64];
                for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++)
                {
                    Color display = x < 64 ? new Color(.65f, .35f, .18f, 1) : new Color(.24f, .5f, .62f, 1);
                    if (pattern == 0) display = new Color(.65f, .35f, .18f, 1);
                    if (pattern == 2) display = new Color(.45f + (x % 4 < 2 ? .2f : -.2f), .45f - (x % 4 < 2 ? .2f : -.2f) * (.299f / .587f), .45f, 1);
                    if (y > 48 && x < 16) display = new Color(.12f, .27f, .72f, 1);
                    pixels[y * 128 + x] = display.linear;
                }
                texture.SetPixels(pixels); texture.Apply(); Graphics.Blit(texture, source); Color[] input = ReadSignalPixels(source);
                old.SetTexture("_SignalSource", source);
                foreach (float gain in new[] { 1f, .85f }) foreach (float bandwidth in new[] { 0f, 1f })
                {
                    runtime.SetFloat("_Gain", gain); runtime.SetFloat("_Bandwidth", bandwidth); Graphics.Blit(source, target, runtime); Color[] actual = ReadSignalPixels(target);
                    if (gain == 1 && bandwidth == 0) result.filterIdentityMaxError = Mathf.Max(result.filterIdentityMaxError, PsfMaxDifference(input, actual));
                    old.SetFloat("_Gain", gain); old.SetFloat("_Bandwidth", bandwidth); camera.allowHDR = true;
                    Color[] expected = RenderSignalFilter(old, camera, renderer, target);
                    result.historicalFilterMaxError = Mathf.Max(result.historicalFilterMaxError, PsfMaxDifference(expected, actual));
                    if (pattern != 2) foreach (int x in new[] { 32, 96 })
                    {
                        int i = 32 * 128 + x; Vector3 before = SignalDisplay(input[i]), after = SignalDisplay(actual[i]);
                        result.colorDcMaxError = Mathf.Max(result.colorDcMaxError, SignalMax(after - before * gain));
                        for (int c = 0; c < 3; c++) result.gainRatioMaxError = Mathf.Max(result.gainRatioMaxError, Mathf.Abs(after[c] / before[c] - gain));
                    }
                }
            }
            if (result.filterIdentityMaxError != 0 || result.historicalFilterMaxError > .001f || result.colorDcMaxError > .001f || result.gainRatioMaxError > .002f) throw new InvalidOperationException("Stable filter identity/color/gain differs from historical calibrated filter: " + JsonUtility.ToJson(result));

            var ramp = new Texture2D(1280, 1030, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; objects.Add(ramp);
            var gradient = new Color[1280 * 1030]; for (int y = 0; y < 1030; y++) for (int x = 0; x < 1280; x++) gradient[y * 1280 + x] = new Color((x + .5f) / 1280, (y + .5f) / 1030, .25f, 1);
            ramp.SetPixels(gradient); ramp.Apply();
            var reference = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Art/Shaders/Streaming/CrtRuntimePsfReference.shader")); objects.Add(reference); reference.SetFloat("_ProbeUV", 1);
            var surfaceDisplay = new Material(CrtSignalResources.Load().Composite); objects.Add(surfaceDisplay); surfaceDisplay.SetFloat("_CrtSignalRuntime", 1); surfaceDisplay.SetFloat("_CrtSignalReady", 1); surfaceDisplay.SetTexture("_CrtProcessedSignalTex", ramp);
            surfaceDisplay.EnableKeyword("CRT_SIGNAL_RUNTIME");
            CompilePsf(surfaceDisplay); CompilePsf(reference);
            var uvTarget = LensTarget(1280, 1030, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear, objects);
            screen.GetComponent<MeshFilter>().sharedMesh = curved; screen.transform.localScale = new Vector3(CrtReferenceScreen.CaseAspect, 1, 1); camera.aspect = CrtReferenceScreen.CaseAspect;
            for (int angle = 0; angle < 2; angle++)
            {
                camera.transform.position = new Vector3(1000 + angle * .3f, 0, -2); camera.transform.LookAt(screen.transform.position); camera.targetTexture = uvTarget; camera.allowHDR = true;
                renderer.sharedMaterial = reference; camera.Render(); Color[] expected = ReadSignalPixels(uvTarget);
                renderer.sharedMaterial = surfaceDisplay; camera.Render(); Color[] actual = ReadSignalPixels(uvTarget);
                result.uvMaxError = Mathf.Max(result.uvMaxError, PsfMaxDifference(expected, actual)); Save(uvTarget, Path.Combine(output, "calibration/surface_uv_" + angle + ".png"));
            }
            for (int y = 0; y < 1030; y++) for (int x = 0; x < 1280; x++) gradient[y * 1280 + x].g = 1 - gradient[y * 1280 + x].g;
            ramp.SetPixels(gradient); ramp.Apply(); renderer.sharedMaterial = reference; camera.Render(); Color[] correct = ReadSignalPixels(uvTarget); renderer.sharedMaterial = surfaceDisplay; camera.Render(); result.uvWrongYError = PsfMaxDifference(correct, ReadSignalPixels(uvTarget));
            if (result.uvMaxError > .003f || result.uvWrongYError < .5f) throw new InvalidOperationException("Canonical surface UV positive/negative meter failed: " + JsonUtility.ToJson(result)
                + "; keywords=" + string.Join(";", surfaceDisplay.shaderKeywords) + "; ready=" + surfaceDisplay.GetFloat("_CrtSignalReady") + "; power=" + surfaceDisplay.GetFloat("_ScreenPower")
                + "; reference=" + surfaceDisplay.GetFloat("_CrtReference") + "; texture=" + surfaceDisplay.GetTexture("_CrtProcessedSignalTex").name);
            camera.transform.SetPositionAndRotation(new Vector3(1000, 0, -2), Quaternion.identity);
        }

        private static void ValidateRuntimeLifecycle(string output, RuntimeSignalMeters result, List<UnityEngine.Object> objects)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Quad); objects.Add(root); root.name = "RuntimeLifecycleScreen"; root.SetActive(false); root.transform.position = new Vector3(2000, 0, 0); root.layer = 2;
            var original = new Material(CrtSignalResources.Load().Composite); objects.Add(original); root.GetComponent<Renderer>().sharedMaterial = original; root.AddComponent<CrtScreenMesh>();
            var registry = root.AddComponent<CameraStreamRegistry>(); var screen = root.AddComponent<MjpegScreen>();
            var serialized = new SerializedObject(screen); serialized.FindProperty("registry").objectReferenceValue = registry; serialized.ApplyModifiedPropertiesWithoutUndo();
            screen.EnableReferenceSurface(); Material owned = root.GetComponent<Renderer>().sharedMaterial;
            owned.SetTexture("_LiveTex", Texture2D.whiteTexture); root.SetActive(true);
            MethodInfo enable = typeof(MjpegScreen).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo disable = typeof(MjpegScreen).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            enable.Invoke(screen, null);
            var cameraObject = new GameObject("RuntimeLifecycleMain"); objects.Add(cameraObject); var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            camera.transform.SetPositionAndRotation(new Vector3(2000, 0, -2), Quaternion.identity); camera.orthographic = true; camera.orthographicSize = .5f; camera.cullingMask = 1 << 2;
            camera.targetTexture = LensTarget(64, 64, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, objects);
            string oldTag = cameraObject.tag;
            GameObject[] previousMain = GameObject.FindGameObjectsWithTag("MainCamera");
            try
            {
                // An Editor may reopen the saved Main scene. Do not let its camera choose the meter's HMD.
                foreach (GameObject existing in previousMain) existing.tag = "Untagged";
                camera.Render(); result.auxiliaryCameraUpdates = screen.SignalProcessor?.ProcessedFrames ?? 0;
                camera.enabled = true; cameraObject.tag = "MainCamera"; camera.Render(); var processor = screen.SignalProcessor ?? throw new InvalidOperationException("HMD camera callback did not prepare signal");
                result.mainCameraUpdates = processor.ProcessedFrames; camera.Render(); result.repeatedMainUpdates = processor.ProcessedFrames;
                disable.Invoke(screen, null); result.disabledDisposed = processor.IsDisposed && owned.GetFloat("_CrtSignalReady") == 0 && owned.GetTexture("_CrtProcessedSignalTex") == null;
                enable.Invoke(screen, null); camera.Render(); result.reenabledUpdates = screen.SignalProcessor?.ProcessedFrames ?? 0;
                result.reenabledRebuilt = screen.SignalProcessor != null && screen.SignalProcessor != processor && owned.GetFloat("_CrtSignalReady") == 1;
            }
            finally { camera.enabled = false; cameraObject.tag = oldTag; foreach (GameObject existing in previousMain) if (existing != null) existing.tag = "MainCamera"; disable.Invoke(screen, null); }
            if (result.auxiliaryCameraUpdates != 0 || result.mainCameraUpdates != 1 || result.repeatedMainUpdates != 1 || !result.disabledDisposed || result.reenabledUpdates != 1 || !result.reenabledRebuilt) throw new InvalidOperationException("Runtime callback/lifecycle meter failed: " + JsonUtility.ToJson(result));
            File.WriteAllText(Path.Combine(output, "calibration/runtime-meters.json"), JsonUtility.ToJson(result, true));
        }
    }
}
