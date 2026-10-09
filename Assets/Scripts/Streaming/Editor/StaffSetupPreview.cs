#nullable enable
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using FixedCamVr.Input;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>実パネルをEditModeで描画。接続値・シーン・保存済み位置を変更しない。</summary>
    public static class StaffSetupPreview
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        [MenuItem("Tools/FixedCamVr/Preview/Staff Setup")]
        public static void Run()
        {
            if (!Application.isBatchMode) throw new System.InvalidOperationException("スタッフ画面プレビューはCLIから実行してください");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            using var capture = new Capture();
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.name = "MainScreenPreview"; screen.transform.position = new Vector3(0f, 0f, 2f);
            screen.transform.localScale = new Vector3(2.4f, 1.35f, 1f);
            screen.AddComponent<MjpegScreen>();
            var panel = StaffSetupPanel.Ensure()!;
            typeof(StaffSetupPanel).GetField("_previewDeviceIp", Flags)!.SetValue(panel, "192.168.10.31");
            var good = new SetupCameraEvidence { connected = true, freshDecode = true, identityKnown = true, identityMatches = true, connection = 1 };
            var absent = new SetupCameraEvidence { problem = SetupCameraProblem.NoStream };
            var snap = new StaffSetupSnapshot { generation = panel.Logic.Generation,
                cameras = new[] { good, absent, good }, tabletFresh = true, settingsSettled = true, contentReady = true, hasPosition = true };
            string dir = EditorCliArgs.Get("out") ?? "output/staff-flow-20261010/quest-preview";
            Directory.CreateDirectory(dir);
            var regGo = new GameObject("RegistrationPreview");
            var registration = regGo.AddComponent<FixedCamVr.Tracking.CourseRegistrationController>();
            typeof(FixedCamVr.Tracking.CourseRegistrationController).GetMethod("ResolvePoints", Flags)!.Invoke(registration, null);
            typeof(StaffSetupPanel).GetField("_registration", Flags)!.SetValue(panel, registration);
            var phase = typeof(FixedCamVr.Tracking.CourseRegistrationController).GetField("_phase", Flags)!;

            // 初回・位置合わせ前。カメラ B だけ映像が来ないまま 5 秒たった（「確認しています」→ 異常）。
            panel.Logic.Observe(snap, 0f);
            for (int i = 0; i < 24; i++) panel.Logic.Observe(snap, .25f);
            Render(panel, snap, Path.Combine(dir, "00-setup-camera-b-trouble.png"), capture);
            snap.cameras[1] = good;
            Render(panel, snap, Path.Combine(dir, "01-setup-devices-ready.png"), capture);

            // 位置合わせ中（機器確認と並行してよい）。
            panel.SetRightController(true, true);
            snap.registrationActive = true;
            phase.SetValue(registration, System.Enum.Parse(phase.FieldType, "Capture"));
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "02-alignment-capture.png"), capture);
            phase.SetValue(registration, System.Enum.Parse(phase.FieldType, "Verify"));
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "03-alignment-verify.png"), capture);
            phase.SetValue(registration, System.Enum.Parse(phase.FieldType, "Review"));
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "04-alignment-review.png"), capture);
            panel.SetRightController(true, false);
            Render(panel, snap, Path.Combine(dir, "05-alignment-right-untracked.png"), capture);
            panel.SetRightController(true, true);
            snap.registrationActive = false;

            // 位置を確定 → 全部そろった（体験者を迎える段）。
            panel.Logic.ConfirmPosition(snap);
            Render(panel, snap, Path.Combine(dir, "06-all-ready.png"), capture);
            panel.SetResetProgress(.55f);
            Render(panel, snap, Path.Combine(dir, "07-welcome-reset-progress.png"), capture);
            panel.SetResetProgress(0f);
            panel.Logic.VisitorResetCompleted(); snap.generation = panel.Logic.Generation;
            snap.settingsRequested = true; snap.settingsSettled = false;
            Render(panel, snap, Path.Combine(dir, "08-welcome-settings-applying.png"), capture);
            snap.settingsSettled = true; snap.visitorSettingsApplied = true; snap.visitorBriefingCompleted = true;
            Render(panel, snap, Path.Combine(dir, "09-welcome.png"), capture);

            // 引き渡し後にカメラ C が切れて画面が戻る（来場者が読む）。
            panel.Logic.HandOff(snap);
            snap.cameras[2].connected = false; snap.cameras[2].problem = SetupCameraProblem.NoStream;
            typeof(StaffSetupPanel).GetField("_previewSnapshot", Flags)!.SetValue(panel, snap);
            for (int i = 0; i < 8; i++) panel.Refresh(.25f);
            Render(panel, snap, Path.Combine(dir, "10-handed-off-recovery.png"), capture);
            snap.cameras[2] = good;

            // 頭の向きの基準が変わった（位置合わせのやり直し）。
            panel.Logic.NewVisitor(); snap.generation = panel.Logic.Generation;
            snap.positionInvalid = true;
            Render(panel, snap, Path.Combine(dir, "11-recenter-required.png"), capture);
            snap.positionInvalid = false;
            snap.registrationActive = true;
            // 操作の失敗と再試行も実コントローラーの状態機械で作る。保存・実機接続は行わない。
            var frame = regGo.AddComponent<FixedCamVr.Tracking.CourseFrame>();
            var hand = new GameObject("PreviewRightHand");
            Set(registration, "courseFrame", frame);
            Set(registration, "rightHandTransform", hand.transform);
            Set(registration, "_capturedWorld", new Vector3[2]);
            phase.SetValue(registration, System.Enum.Parse(phase.FieldType, "Capture"));
            Feed(registration, true, true, 0f);
            Feed(registration, false, false, 0f);
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "12-point-released-too-soon.png"), capture);
            Feed(registration, true, true, 0f);
            Feed(registration, false, true, .25f);
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "13-point-sampling.png"), capture);
            Feed(registration, false, false, 0f);
            // 点間距離 1.8 m（作品の指定は 1 m）で失敗を起こす。
            Point(registration, hand.transform, new Vector3(-.9f, 0f, .5f));
            Point(registration, hand.transform, new Vector3(.9f, 0f, .5f));
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "14-fit-rejected.png"), capture);
            Point(registration, hand.transform, new Vector3(-.5f, 0f, .5f));
            Point(registration, hand.transform, new Vector3(.5f, 0f, .5f));
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "15-fit-recovered.png"), capture);
            Feed(registration, true, true, 0f);
            Feed(registration, false, true, .6f);
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "16-manual-retry-held.png"), capture);
            Feed(registration, false, false, 0f);
            Guidance(registration);
            Render(panel, snap, Path.Combine(dir, "17-manual-retry-released.png"), capture);
            Debug.Log("[StaffSetupPreview] " + Path.GetFullPath(dir));
        }
        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, Flags)!.SetValue(target, value);
        private static void Guidance(FixedCamVr.Tracking.CourseRegistrationController registration)
            => registration.GetType().GetMethod("UpdateGuidanceText", Flags)!.Invoke(registration, null);
        private static void Feed(FixedCamVr.Tracking.CourseRegistrationController registration, bool mark, bool held, float dt)
            => registration.Feed(new FixedCamVr.Tracking.CourseRegistrationController.RegInput
                { mark = mark, markHeld = held, deltaTime = dt });
        private static void Point(FixedCamVr.Tracking.CourseRegistrationController registration, Transform hand, Vector3 position)
        {
            hand.position = position;
            Feed(registration, false, false, 0f);
            Feed(registration, true, true, 0f);
            Feed(registration, false, true, .5f);
            Feed(registration, false, false, 0f);
        }
        private static void Render(StaffSetupPanel panel, StaffSetupSnapshot snapshot, string path, Capture capture)
        {
            typeof(StaffSetupPanel).GetField("_previewSnapshot", Flags)!.SetValue(panel, snapshot);
            panel.Logic.Observe(snapshot, .25f); panel.Logic.Observe(snapshot, .25f);
            typeof(StaffSetupPanel).GetMethod("LateUpdate", Flags)!.Invoke(panel, null);
            foreach (var text in panel.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                text.havePropertiesChanged = true;
                text.ForceMeshUpdate(true, true);
            }
            var camera = capture.Camera; var rt = capture.Target; var texture = capture.Texture;
            var old = RenderTexture.active;
            try
            {
                camera.targetTexture = rt; GraphicsSettings.useScriptableRenderPipelineBatching = false; camera.Render();
                foreach (var text in panel.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    text.havePropertiesChanged = true;
                    text.ForceMeshUpdate(true);
                }
                GraphicsSettings.useScriptableRenderPipelineBatching = false; camera.Render(); RenderTexture.active = rt;
                foreach (var text in panel.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (!text.enabled) continue;
                    Debug.Log($"[StaffPreviewMesh] {Path.GetFileName(path)} {text.name} font={text.fontSize} scale={text.transform.lossyScale.y} chars={text.textInfo.characterCount} mesh={text.mesh.bounds.size} bounds={text.GetComponent<Renderer>().bounds.size}");
                }
                texture.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0); texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                var pixels = texture.GetPixels32(); var blank = new Color32[pixels.Length];
                var csv = new StringBuilder("field,glyphs,missingPixels,outsidePanel,outsideColumn,widthM,heightM\n");
                int missing = 0, outsidePanel = 0, outsideColumn = 0;
                // はみ出しの物差し：パネルの背景（幅 w・高さ 9/16 w）と、各ラベルの横幅（列）。
                var back = panel.transform.Find("Visual/SetupBackground")!.GetComponent<Renderer>().bounds;
                var corners = new Vector3[4];
                foreach (var text in panel.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (!text.enabled) continue;
                    int count = MissingPixels(text, camera, pixels, out int glyphs);
                    if (MissingPixels(text, camera, blank, out _) != glyphs)
                        throw new System.InvalidOperationException("Blank pixel negative control failed");
                    text.rectTransform.GetWorldCorners(corners);
                    float colMin = Mathf.Min(corners[0].x, corners[2].x), colMax = Mathf.Max(corners[0].x, corners[2].x);
                    int outP = 0, outC = 0;
                    for (int i = 0; i < text.textInfo.characterCount; i++)
                    {
                        var ch = text.textInfo.characterInfo[i]; if (!ch.isVisible) continue;
                        var lo = text.transform.TransformPoint(ch.bottomLeft); var hi = text.transform.TransformPoint(ch.topRight);
                        if (lo.x < back.min.x || hi.x > back.max.x || lo.y < back.min.y || hi.y > back.max.y) outP++;
                        if (lo.x < colMin - 1e-4f || hi.x > colMax + 1e-4f) outC++;
                    }
                    missing += count; outsidePanel += outP; outsideColumn += outC;
                    var bounds = text.GetComponent<Renderer>().bounds;
                    csv.AppendLine($"{text.name},{glyphs},{count},{outP},{outC},{bounds.size.x:F4},{bounds.size.y:F4}");
                }
                File.WriteAllText(Path.ChangeExtension(path, ".csv"), csv.ToString(), new UTF8Encoding(false));
                if (missing != 0) throw new System.InvalidOperationException($"{path}: {missing} glyphs have no rendered pixels");
                if (outsidePanel != 0) throw new System.InvalidOperationException($"{path}: {outsidePanel} glyphs are outside the panel");
                if (outsideColumn != 0) throw new System.InvalidOperationException($"{path}: {outsideColumn} glyphs overflow their column");
            }
            finally
            { RenderTexture.active = old; }
        }
        private static int MissingPixels(TMPro.TMP_Text text, Camera camera, Color32[] pixels, out int glyphs)
        {
            glyphs = 0; int missing = 0;
            for (int i = 0; i < text.textInfo.characterCount; i++)
            {
                var ch = text.textInfo.characterInfo[i]; if (!ch.isVisible) continue;
                glyphs++; bool ink = false;
                var lo = camera.WorldToScreenPoint(text.transform.TransformPoint(ch.bottomLeft));
                var hi = camera.WorldToScreenPoint(text.transform.TransformPoint(ch.topRight));
                for (int y = Mathf.Max(0, Mathf.FloorToInt(lo.y)); y < Mathf.Min(1000, Mathf.CeilToInt(hi.y)) && !ink; y++)
                for (int x = Mathf.Max(0, Mathf.FloorToInt(lo.x)); x < Mathf.Min(1600, Mathf.CeilToInt(hi.x)); x++)
                {
                    var p = pixels[y * 1600 + x]; if (p.r > 30 || p.g > 30 || p.b > 30) { ink = true; break; }
                }
                if (!ink) missing++;
            }
            return missing;
        }
        private sealed class Capture : System.IDisposable
        {
            private readonly bool _batching = GraphicsSettings.useScriptableRenderPipelineBatching;
            private readonly GameObject _go = new GameObject("SetupPreviewCamera");
            public Camera Camera { get; }
            public RenderTexture Target { get; } = new RenderTexture(1600, 1000, 24);
            public Texture2D Texture { get; } = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
            public Capture()
            {
                GraphicsSettings.useScriptableRenderPipelineBatching = false;
                Camera = _go.AddComponent<Camera>(); Camera.transform.position = Vector3.zero;
                Camera.orthographic = true; Camera.orthographicSize = .82f;
                Camera.clearFlags = CameraClearFlags.SolidColor; Camera.backgroundColor = new Color(.04f, .045f, .05f, 1f);
                Camera.targetTexture = Target;
            }
            public void Dispose()
            {
                GraphicsSettings.useScriptableRenderPipelineBatching = _batching;
                Object.DestroyImmediate(Target); Object.DestroyImmediate(Texture); Object.DestroyImmediate(_go);
            }
        }
    }
}
