#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using FixedCamVr.Tracking;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 位置合わせ中の手元案内を実 TMP で描画し、文字と右手先端と床の×印が重ならないことを測る。
    /// シーンと保存済み位置合わせは使わず、立位と屈位を空シーン内の一時オブジェクトで再現する。
    /// </summary>
    public static class ControllerGuideRegistrationPreview
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const int Width = 1600;
        private const int Height = 1200;
        private const float HandDistanceM = .45f;
        private const float PanelWidthM = .28f;
        private const float PanelHeightM = .15f;
        private const float MeshFontSize = .07f;

        [MenuItem("Tools/FixedCamVr/Preview/Registration Hand Guide")]
        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("手元案内プレビューは CLI から実行してください");

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string dir = EditorCliArgs.Get("out") ?? "output/staff-flow-20261010/hand-guide-preview";
            Directory.CreateDirectory(dir);
            var csv = new StringBuilder(
                "pose,state,actionChars,detailChars,controlChars,tmpCharacters,glyphs,inkGlyphs,missingPixels," +
                "outsideLeft,outsideRight,outsideTop,outsideBottom,textTipOverlap,textMarkerOverlap," +
                "textTipGapPx,textMarkerGapPx,tipGeometryDeltaPx,markerGeometryDeltaPx," +
                "textBottomM,tipTopM,markerTopM\n");

            using var capture = new Capture();
            RenderPose("standing", new Vector3(0f, 1.60f, 0f), new Vector3(.25f, 1.15f, .45f),
                dir, capture, csv);
            RenderPose("crouching", new Vector3(0f, .75f, 0f), new Vector3(.25f, .12f, .45f),
                dir, capture, csv);

            string report = Path.Combine(dir, "controller-guide-registration.csv");
            File.WriteAllText(report, csv.ToString(), new UTF8Encoding(false));
            Debug.Log("[ControllerGuideRegistrationPreview] " + Path.GetFullPath(report));
        }

        private static void RenderPose(string pose, Vector3 head, Vector3 hand, string dir, Capture capture,
            StringBuilder csv)
        {
            using var fixture = new Fixture(head, hand);
            RenderState(fixture, pose, "capture", dir, capture, csv);

            fixture.Registration.Feed(new CourseRegistrationController.RegInput { mark = true, markHeld = true });
            fixture.Registration.Feed(new CourseRegistrationController.RegInput { markHeld = true, deltaTime = .25f });
            Guidance(fixture.Registration);
            if (Mathf.Abs(fixture.Registration.SampleHoldProgress01 - .5f) > .001f)
                throw new InvalidOperationException("採取 0.25 秒時点の進捗が 0.5 ではありません");
            RenderState(fixture, pose, "sampling", dir, capture, csv);

            Set(fixture.Registration, "_verifyMaxResidualM", .024f);
            Set(fixture.Registration, "_verifyFloorY", .018f);
            Set(fixture.Registration, "_verifyFloorSpreadM", .012f);
            Phase(fixture.Registration, "Verify");
            RenderState(fixture, pose, "verify", dir, capture, csv);

            fixture.Frame.SetRegistration(new Vector2(.12f, -.08f), 3.5f, .024f, 2, save: false);
            Phase(fixture.Registration, "Review");
            RenderState(fixture, pose, "review", dir, capture, csv);

            fixture.Registration.NotifyRecentered();
            Guidance(fixture.Registration);
            RenderState(fixture, pose, "recenter", dir, capture, csv);
        }

        private static void RenderState(Fixture fixture, string pose, string state, string dir, Capture capture,
            StringBuilder csv)
        {
            if (state == "capture") Phase(fixture.Registration, "Capture");
            ValidateProvider(fixture.Registration);

            // 一度追跡を落としてから復帰させ、実装と同じ「現在位置へスナップ」を通す。
            fixture.Panel.SetControllerState(true, false);
            Invoke(fixture.Panel, "LateUpdate");
            fixture.Panel.SetControllerState(true, true);
            Invoke(fixture.Panel, "LateUpdate");
            if (!fixture.Text.enabled) throw new InvalidOperationException($"{pose}/{state}: 手元案内が非表示です");
            if (fixture.Text.text != fixture.Registration.GuidanceText)
                throw new InvalidOperationException($"{pose}/{state}: 案内元と TMP の本文が一致しません");

            fixture.Text.havePropertiesChanged = true;
            fixture.Text.ForceMeshUpdate(true, true);
            Vector3 markerCenter = new Vector3(fixture.Hand.position.x, .006f, fixture.Hand.position.z);
            capture.LookFrom(fixture.Head.position,
                Vector3.Lerp(fixture.Panel.transform.position, markerCenter, .42f));
            Color32[] pixels = capture.Render(fixture.Text);
            var blank = new Color32[pixels.Length];

            int missing = MissingPixels(fixture.Text, capture.Camera, pixels, out int glyphs);
            int blankMissing = MissingPixels(fixture.Text, capture.Camera, blank, out int blankGlyphs);
            if (blankGlyphs != glyphs || blankMissing != glyphs)
                throw new InvalidOperationException($"{pose}/{state}: 空画像の negative control が失敗しました");

            CountOutside(fixture.Text, out int left, out int right, out int top, out int bottom);
            Rect textRect = TextScreenRect(fixture.Text, capture.Camera);
            // LineRenderer.bounds は EditMode の Camera.Render 直後でも前の AABB を返すことがある。
            // 遮蔽の判定は PNG と同じ実画素からシアンの先端とマゼンタの×印を拾う。
            Rect tipRect = ColoredPixelRect(pixels, ColorProbe.Tip);
            Rect markerRect = ColoredPixelRect(pixels, ColorProbe.Marker);
            Rect tipGeometryRect = RendererScreenRect(fixture.Tip, capture.Camera);
            Rect markerGeometryRect = Union(RendererScreenRect(fixture.MarkerA, capture.Camera),
                RendererScreenRect(fixture.MarkerB, capture.Camera));
            bool tipOverlap = textRect.Overlaps(tipRect);
            bool markerOverlap = textRect.Overlaps(markerRect);
            float panelBottom = fixture.Text.GetComponent<Renderer>().bounds.min.y;
            float tipTop = fixture.Tip.bounds.max.y;
            float markerTop = Mathf.Max(fixture.MarkerA.bounds.max.y, fixture.MarkerB.bounds.max.y);

            csv.Append(pose).Append(',').Append(state).Append(',')
                .Append(fixture.Registration.GuidanceAction.Length).Append(',')
                .Append(fixture.Registration.GuidanceDetails.Length).Append(',')
                .Append(fixture.Registration.GuidanceControls.Length).Append(',')
                .Append(fixture.Text.textInfo.characterCount).Append(',').Append(glyphs).Append(',')
                .Append(glyphs - missing).Append(',').Append(missing).Append(',')
                .Append(left).Append(',').Append(right).Append(',').Append(top).Append(',').Append(bottom).Append(',')
                .Append(tipOverlap ? 1 : 0).Append(',').Append(markerOverlap ? 1 : 0).Append(',')
                .Append(RectGap(textRect, tipRect).ToString("F1")).Append(',')
                .Append(RectGap(textRect, markerRect).ToString("F1")).Append(',')
                .Append(RectEdgeDelta(tipRect, tipGeometryRect).ToString("F1")).Append(',')
                .Append(RectEdgeDelta(markerRect, markerGeometryRect).ToString("F1")).Append(',')
                .Append(panelBottom.ToString("F4")).Append(',').Append(tipTop.ToString("F4")).Append(',')
                .Append(markerTop.ToString("F4")).AppendLine();

            string png = Path.Combine(dir, $"{pose}-{state}.png");
            File.WriteAllBytes(png, capture.Texture.EncodeToPNG());
            if (missing != 0 || left != 0 || right != 0 || top != 0 || bottom != 0)
                throw new InvalidOperationException(
                    $"{pose}/{state}: missing={missing}, outside L/R/T/B={left}/{right}/{top}/{bottom}");
            if (tipOverlap || markerOverlap)
                throw new InvalidOperationException(
                    $"{pose}/{state}: 文字が {(tipOverlap ? "右手先端" : "床の×印")} と重なっています。" +
                    $" text={RectText(textRect)}, tip={RectText(tipRect)}, marker={RectText(markerRect)}");
        }

        private static void ValidateProvider(CourseRegistrationController registration)
        {
            if (string.IsNullOrEmpty(registration.GuidanceAction)
                || string.IsNullOrEmpty(registration.GuidanceDetails)
                || string.IsNullOrEmpty(registration.GuidanceControls))
                throw new InvalidOperationException("Action / Details / Controls のいずれかが空です");
            string expected = registration.GuidanceAction + "\n" + registration.GuidanceDetails
                + "\n" + registration.GuidanceControls;
            if (registration.GuidanceText != expected)
                throw new InvalidOperationException("GuidanceText が 3 区分の単一プロバイダになっていません");
        }

        private static void CountOutside(TMP_Text text, out int left, out int right, out int top, out int bottom)
        {
            left = right = top = bottom = 0;
            Rect rect = text.rectTransform.rect;
            const float tolerance = .0001f;
            for (int i = 0; i < text.textInfo.characterCount; i++)
            {
                TMP_CharacterInfo ch = text.textInfo.characterInfo[i];
                if (!ch.isVisible) continue;
                if (ch.bottomLeft.x < rect.xMin - tolerance) left++;
                if (ch.topRight.x > rect.xMax + tolerance) right++;
                if (ch.topRight.y > rect.yMax + tolerance) top++;
                if (ch.bottomLeft.y < rect.yMin - tolerance) bottom++;
            }
        }

        private static int MissingPixels(TMP_Text text, Camera camera, Color32[] pixels, out int glyphs)
        {
            glyphs = 0;
            int missing = 0;
            for (int i = 0; i < text.textInfo.characterCount; i++)
            {
                TMP_CharacterInfo ch = text.textInfo.characterInfo[i];
                if (!ch.isVisible) continue;
                glyphs++;
                Rect r = CharacterScreenRect(text, ch, camera);
                bool ink = false;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(r.xMin), 0, Width - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(r.xMax), 0, Width);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(r.yMin), 0, Height - 1);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(r.yMax), 0, Height);
                for (int y = y0; y < y1 && !ink; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color32 p = pixels[y * Width + x];
                    if (p.r > 30 || p.g > 30 || p.b > 30) { ink = true; break; }
                }
                if (!ink) missing++;
            }
            return missing;
        }

        private static Rect TextScreenRect(TMP_Text text, Camera camera)
        {
            bool seeded = false;
            Rect result = default;
            for (int i = 0; i < text.textInfo.characterCount; i++)
            {
                TMP_CharacterInfo ch = text.textInfo.characterInfo[i];
                if (!ch.isVisible) continue;
                Rect next = CharacterScreenRect(text, ch, camera);
                result = seeded ? Union(result, next) : next;
                seeded = true;
            }
            if (!seeded) throw new InvalidOperationException("TMP に可視文字がありません");
            return result;
        }

        private static Rect CharacterScreenRect(TMP_Text text, TMP_CharacterInfo ch, Camera camera)
        {
            Vector3 a = camera.WorldToScreenPoint(text.transform.TransformPoint(ch.bottomLeft));
            Vector3 b = camera.WorldToScreenPoint(text.transform.TransformPoint(ch.topRight));
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        private static Rect RendererScreenRect(Renderer renderer, Camera camera)
        {
            Bounds b = renderer.bounds;
            bool seeded = false;
            float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 p = camera.WorldToScreenPoint(b.center + Vector3.Scale(b.extents, new Vector3(x, y, z)));
                if (!seeded) { minX = maxX = p.x; minY = maxY = p.y; seeded = true; }
                else
                {
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private enum ColorProbe { Tip, Marker }

        private static Rect ColoredPixelRect(Color32[] pixels, ColorProbe probe)
        {
            bool seeded = false;
            int minX = 0, minY = 0, maxX = 0, maxY = 0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                Color32 p = pixels[y * Width + x];
                bool match = probe == ColorProbe.Tip
                    ? p.g > 16 && p.b > 16 && p.g > p.r + 4 && p.b > p.r + 4
                    : p.r > 16 && p.b > 16 && p.r > p.g + 4 && p.b > p.g + 4;
                if (!match) continue;
                if (!seeded) { minX = maxX = x; minY = maxY = y; seeded = true; }
                else
                {
                    minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                    minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                }
            }
            if (!seeded) throw new InvalidOperationException($"{probe} の描画画素がありません");
            return Rect.MinMaxRect(minX, minY, maxX + 1, maxY + 1);
        }

        private static Rect Union(Rect a, Rect b)
            => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        private static float RectGap(Rect a, Rect b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(a.xMin - b.xMax, b.xMin - a.xMax));
            float dy = Mathf.Max(0f, Mathf.Max(a.yMin - b.yMax, b.yMin - a.yMax));
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static float RectEdgeDelta(Rect a, Rect b)
            => Mathf.Max(Mathf.Abs(a.xMin - b.xMin), Mathf.Abs(a.yMin - b.yMin),
                Mathf.Abs(a.xMax - b.xMax), Mathf.Abs(a.yMax - b.yMax));

        private static string RectText(Rect r)
            => $"({r.xMin:F1},{r.yMin:F1})-({r.xMax:F1},{r.yMax:F1})";

        private static void Set(object target, string field, object value)
        {
            FieldInfo? f = target.GetType().GetField(field, Flags);
            if (f == null) throw new MissingFieldException(target.GetType().Name, field);
            f.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
        {
            MethodInfo? m = target.GetType().GetMethod(method, Flags);
            if (m == null) throw new MissingMethodException(target.GetType().Name, method);
            m.Invoke(target, null);
        }

        private static void Guidance(CourseRegistrationController registration)
            => Invoke(registration, "UpdateGuidanceText");

        private static void Phase(CourseRegistrationController registration, string phase)
        {
            FieldInfo f = typeof(CourseRegistrationController).GetField("_phase", Flags)!;
            f.SetValue(registration, Enum.Parse(f.FieldType, phase));
            Guidance(registration);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _root = new GameObject("[HandGuidePreview]");
            private readonly Material _markerMaterial;
            private readonly Material _tipMaterial;
            public Transform Head { get; }
            public Transform Hand { get; }
            public CourseFrame Frame { get; }
            public CourseRegistrationController Registration { get; }
            public ControllerGuidePanel Panel { get; }
            public TMP_Text Text { get; }
            public Renderer Tip { get; }
            public LineRenderer MarkerA { get; }
            public LineRenderer MarkerB { get; }

            public Fixture(Vector3 headPosition, Vector3 handPosition)
            {
                Head = Child("Head").transform;
                Head.position = headPosition;
                Hand = Child("RightController").transform;
                Hand.position = handPosition;

                var tracking = Child("Tracking");
                Frame = tracking.AddComponent<CourseFrame>();
                Registration = tracking.AddComponent<CourseRegistrationController>();
                Set(Registration, "courseFrame", Frame);
                Set(Registration, "rightHandTransform", Hand);
                Set(Registration, "headTransform", Head);
                Invoke(Registration, "ResolvePoints");

                var panelGo = Child("ControllerGuide");
                var textGo = Child("GuideText", panelGo.transform);
                var tmp = textGo.AddComponent<TextMeshPro>();
                tmp.fontSize = MeshFontSize;
                tmp.color = HmdTextStyle.Ink;
                tmp.alignment = TextAlignmentOptions.TopLeft;
                tmp.enableWordWrapping = false;
                tmp.richText = false;
                float scale = HmdTextStyle.MeshScale(HmdTextStyle.BodyDeg, HandDistanceM, MeshFontSize);
                tmp.rectTransform.sizeDelta = new Vector2(PanelWidthM / scale, PanelHeightM / scale);
                textGo.transform.localScale = Vector3.one * scale;

                Panel = panelGo.AddComponent<ControllerGuidePanel>();
                Text = tmp;
                Set(Panel, "text", tmp);
                Set(Panel, "controller", Hand);
                Set(Panel, "head", Head);
                Invoke(Panel, "Awake");
                Panel.SetRegistration(Registration);
                Panel.SetMode("REG");

                Shader markerShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                _markerMaterial = new Material(markerShader) { color = new Color(1f, .35f, .9f, 1f) };
                MarkerA = MakeMarker("FloorX-A", new Vector3(-.05f, 0f, -.05f), new Vector3(.05f, 0f, .05f));
                MarkerB = MakeMarker("FloorX-B", new Vector3(-.05f, 0f, .05f), new Vector3(.05f, 0f, -.05f));

                var tipGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tipGo.name = "RightControllerTip";
                tipGo.transform.SetParent(_root.transform, false);
                tipGo.transform.position = Hand.position;
                tipGo.transform.localScale = Vector3.one * .05f;
                Tip = tipGo.GetComponent<Renderer>();
                _tipMaterial = new Material(markerShader) { color = new Color(.2f, .9f, 1f, 1f) };
                Tip.sharedMaterial = _tipMaterial;
            }

            private GameObject Child(string name, Transform? parent = null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent != null ? parent : _root.transform, false);
                return go;
            }

            private LineRenderer MakeMarker(string name, Vector3 a, Vector3 b)
            {
                var line = Child(name).AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = line.endWidth = .008f;
                line.sharedMaterial = _markerMaterial;
                Vector3 center = new Vector3(Hand.position.x, .006f, Hand.position.z);
                line.SetPosition(0, center + a);
                line.SetPosition(1, center + b);
                return line;
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_markerMaterial);
                UnityEngine.Object.DestroyImmediate(_tipMaterial);
                UnityEngine.Object.DestroyImmediate(_root);
            }
        }

        private sealed class Capture : IDisposable
        {
            private readonly GameObject _cameraGo = new GameObject("HandGuidePreviewCamera");
            private readonly bool _batching = GraphicsSettings.useScriptableRenderPipelineBatching;
            public Camera Camera { get; }
            public RenderTexture Target { get; } = new RenderTexture(Width, Height, 24);
            public Texture2D Texture { get; } = new Texture2D(Width, Height, TextureFormat.RGB24, false);

            public Capture()
            {
                GraphicsSettings.useScriptableRenderPipelineBatching = false;
                Camera = _cameraGo.AddComponent<Camera>();
                Camera.fieldOfView = 78f;
                Camera.aspect = Width / (float)Height;
                Camera.nearClipPlane = .01f;
                Camera.farClipPlane = 10f;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = new Color(.035f, .04f, .045f, 1f);
                Camera.targetTexture = Target;
            }

            public void LookFrom(Vector3 position, Vector3 target)
            {
                Camera.transform.position = position;
                Camera.transform.rotation = Quaternion.LookRotation(target - position, Vector3.up);
            }

            public Color32[] Render(TMP_Text text)
            {
                RenderTexture? old = RenderTexture.active;
                try
                {
                    Camera.Render();
                    text.havePropertiesChanged = true;
                    text.ForceMeshUpdate(true, true);
                    Camera.Render();
                    RenderTexture.active = Target;
                    Texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    Texture.Apply();
                    return Texture.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = old;
                }
            }

            public void Dispose()
            {
                GraphicsSettings.useScriptableRenderPipelineBatching = _batching;
                UnityEngine.Object.DestroyImmediate(Target);
                UnityEngine.Object.DestroyImmediate(Texture);
                UnityEngine.Object.DestroyImmediate(_cameraGo);
            }
        }
    }
}
