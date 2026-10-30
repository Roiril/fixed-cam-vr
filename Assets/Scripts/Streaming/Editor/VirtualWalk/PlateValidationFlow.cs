#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    public static partial class ShowCompositePreview
    {
        // 既存の本番 CommsPanel プレビューを再使用する。署名の変更は診断失敗として表へ出す。
        private static void PlateValidationRelease(string outDir)
        {
            CommsPanel source = UnityEngine.Object.FindObjectOfType<CommsPanel>(true)
                ?? throw new InvalidOperationException("[PlateValidation] Production CommsPanel is missing.");
            GameObject panelRoot = UnityEngine.Object.Instantiate(source.gameObject);
            panelRoot.hideFlags = HideFlags.HideAndDontSave;
            var cameraGo = new GameObject("[PlateValidation release camera]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                CommsPanel panel = panelRoot.GetComponent<CommsPanel>();
                PlateValidationMethod(typeof(CommsPanel), "Awake", Type.EmptyTypes).Invoke(panel, null);
                TMP_Text? text = null;
                foreach (TMP_Text candidate in panel.GetComponentsInChildren<TMP_Text>(true))
                    if (candidate.name == "CommsText") { text = candidate; break; }
                if (text == null) throw new InvalidOperationException("[PlateValidation] CommsText could not be built.");
                FieldInfo field = typeof(CommsPanel).GetField("_logic", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(typeof(CommsPanel).FullName, "_logic");
                object logic = field.GetValue(panel) ?? throw new InvalidOperationException("[PlateValidation] Comms logic is null.");
                MethodInfo apply = PlateValidationMethod(typeof(CommsPanel), "Apply", new[] { typeof(CommsWeights).MakeByRefType() });
                Camera camera = cameraGo.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 1000, 0);
                camera.fieldOfView = 30;
                camera.nearClipPlane = 0.05f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.055f, 0.052f, 0.05f, 1);
                MethodInfo recovery = PlateValidationMethod(typeof(CommsPreview), "ShootRecovery",
                    new[] { typeof(CommsPanel), typeof(object), typeof(MethodInfo), typeof(Transform), typeof(TMP_Text), typeof(Camera), typeof(string), typeof(float) });
                recovery.Invoke(null, new object[] { panel, logic, apply, panel.transform, text, camera, outDir, 1.5f });
                if (!File.Exists(Path.Combine(outDir, "recovery/recovery.tsv")))
                    throw new InvalidOperationException("[PlateValidation] Production release did not write evidence.");
            }
            finally { UnityEngine.Object.DestroyImmediate(panelRoot); UnityEngine.Object.DestroyImmediate(cameraGo); }
        }

        private static MethodInfo PlateValidationMethod(Type type, string name, Type[] parameters)
            => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic, null, parameters, null)
                ?? throw new MissingMethodException(type.FullName, name);

        private static void PlateValidationOutro(Stage stage, Plate plate, PostParams post, RenderTexture captured, string outDir)
        {
            var root = new GameObject("[PlateValidation actual ending report]") { hideFlags = HideFlags.HideAndDontSave };
            var cameraGo = new GameObject("[PlateValidation report camera]") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            var trace = new StringBuilder("timeSec\tstage\tscreenPower\tscreenCollapse\tscreenNoise\treportAlpha\tactualAlpha\tvisibleChars\tphotoVisible\tusesCapturedImage\n");
            Material material = PlateValidationField<Material>(stage, "_compositeMat");
            string[] changedProperties = { "_ScreenPower", "_ScreenCollapse", "_ScreenNoise" };
            var savedProperties = new float[changedProperties.Length];
            for (int i = 0; i < changedProperties.Length; i++)
            {
                if (!material.HasProperty(changedProperties[i]))
                    throw new InvalidOperationException("[PlateValidation] Composite property is missing: " + changedProperties[i]);
                savedProperties[i] = material.GetFloat(changedProperties[i]);
            }
            try
            {
                root.transform.position = new Vector3(0, 2000, 0);
                OutroReport report = root.AddComponent<OutroReport>();
                report.SetEndingImages(captured, null);
                report.PresentPreview(12, 8, ShowEndingOutcome.Released, ShowLang.Ja);
                if (!report.IsBuilt || !ReferenceEquals(report.CurrentEndingImage, captured))
                    throw new InvalidOperationException("[PlateValidation] Actual report did not accept the captured photograph.");
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                Camera camera = cameraGo.AddComponent<Camera>();
                camera.transform.position = root.transform.position;
                camera.transform.rotation = Quaternion.Euler(2, 0, 0);
                camera.fieldOfView = 42;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 31;
                camera.targetTexture = rt;
                MethodInfo alpha = PlateValidationMethod(typeof(OutroReport), "ApplyAlpha", new[] { typeof(float) });
                var logic = new OutroLogic();
                logic.Configure(OutroTiming.Default);
                logic.Begin();
                OutroStage previous = OutroStage.Off;
                int serial = 0;
                for (int frame = 0; frame < 2400; frame++)
                {
                    float time = frame / 30f;
                    stage.Composite(plate, false, post, "actual ending power cut | " + logic.Stage);
                    material.SetFloat("_ScreenPower", logic.ScreenPower);
                    material.SetFloat("_ScreenCollapse", logic.ScreenCollapse);
                    material.SetFloat("_ScreenNoise", logic.ScreenNoise);
                    alpha.Invoke(report, new object[] { logic.ReportAlpha });
                    bool phaseChanged = previous != logic.Stage;
                    if (phaseChanged || frame % 15 == 0 || logic.Closed)
                    {
                        if (logic.ReportAlpha > 0 || logic.Stage == OutroStage.Done)
                        {
                            foreach (TMP_Text text in report.Fields) text.ForceMeshUpdate(true, true);
                            camera.Render();
                            PlateValidationSaveRt(rt, Path.Combine(outDir, "outro_" + serial.ToString("D3") + "_" + logic.Stage + ".png"));
                        }
                        else
                        {
                            if (!EndingFrameCapture.CaptureComposite(material, rt)) throw new InvalidOperationException("[PlateValidation] Power-cut rendering failed.");
                            PlateValidationSaveRt(rt, Path.Combine(outDir, "outro_" + serial.ToString("D3") + "_" + logic.Stage + ".png"));
                        }
                        serial++;
                    }
                    trace.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F3}\t{1}\t{2:F4}\t{3:F4}\t{4:F4}\t{5:F4}\t{6:F4}\t{7}\t{8}\t{9}", time, logic.Stage,
                        logic.ScreenPower, logic.ScreenCollapse, logic.ScreenNoise, logic.ReportAlpha, report.AppliedAlpha,
                        report.VisibleChars, report.PhotoVisible, ReferenceEquals(report.CurrentEndingImage, captured)));
                    previous = logic.Stage;
                    if (logic.Closed) break;
                    logic.Tick(1f / 30f);
                }
                if (!logic.Closed) throw new InvalidOperationException("[PlateValidation] Actual outro did not close.");
            }
            finally
            {
                // Stage は全カメラで共有する。電源断の値を B/C の実写へ持ち越さない。
                for (int i = 0; i < changedProperties.Length; i++) material.SetFloat(changedProperties[i], savedProperties[i]);
                File.WriteAllText(Path.Combine(outDir, "outro.tsv"), trace.ToString(), new UTF8Encoding(false));
                Camera? camera = cameraGo.GetComponent<Camera>();
                if (camera != null) camera.targetTexture = null;
                rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }
    }
}
