#nullable enable

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 不正アクセスを、スクリーンに残る警告面と、その手前へほどける情報片で見せる。
    /// スクリーン側は指定 anchor を追い、空間側は発火した瞬間の世界座標へ残る。
    /// </summary>
    [ExecuteAlways]
    public sealed class UnauthorizedAccessEffect : MonoBehaviour
    {
        public const float EffectDuration = 7f;

        private const float AppearEndSec = 0.7f;
        private const float ExpandEndSec = 2f;
        private const float FadeStartSec = 5.5f;
        private const float FontSize = 0.07f;
        private const int BinaryColumnCount = 18;
        private const int ErrorLabelCount = 4;
        private const int SpatialLineCount = 4;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private static readonly Color DeepRed = new Color(0.24f, 0.012f, 0.018f, 0.52f);
        private static readonly Color Red = new Color(0.88f, 0.055f, 0.07f, 0.82f);
        private static readonly Color Coral = new Color(1f, 0.48f, 0.38f, 0.88f);
        private static readonly Color DimCoral = new Color(0.72f, 0.20f, 0.17f, 0.54f);

        private const string BinaryA = "0\n1\n0\n0\n1\n1\n0\n1\n0\n1\n1\n0";
        private const string BinaryB = "1\n0\n1\n1\n0\n0\n1\n0\n1\n0\n0\n1";
        private static readonly string[] ErrorLabels =
        {
            "UNAUTHORIZED", "ACCESS DETECTED", "UNKNOWN ORIGIN", "INTRUSION",
        };

        [SerializeField] private Material? fxMaterial;
        [SerializeField] private TMP_FontAsset? font;
        [SerializeField] private Texture2D? interference;
        [SerializeField] private bool autoPlay;

        private readonly List<QuadVisual> _screenQuads = new List<QuadVisual>(24);
        private readonly List<SpatialTextVisual> _spatialTexts = new List<SpatialTextVisual>(22);
        private readonly List<QuadVisual> _spatialLines = new List<QuadVisual>(SpatialLineCount);

        private Transform? _screenRoot;
        private Transform? _spatialRoot;
        private Mesh? _quadMesh;
        private Material? _flatMaterial;
        private Material? _interferenceMaterial;
        private TextMeshPro? _warningText;
        private TextMeshPro? _japaneseText;
        private TextMeshPro? _englishText;
        private Vector2 _screenSize;

        public float Duration => EffectDuration;
        public bool IsPlaying { get; private set; }
        public float Elapsed { get; private set; }
        public int SpatialElementCount => _spatialTexts.Count + _spatialLines.Count;
        public Transform? ScreenAnchor { get; private set; }

        public void Configure(Material fxMaterial, TMP_FontAsset font, Texture2D interference)
        {
            bool changed = this.fxMaterial != fxMaterial || this.font != font || this.interference != interference;
            this.fxMaterial = fxMaterial;
            this.font = font;
            this.interference = interference;
            if (changed && _screenRoot != null)
            {
                bool resume = IsPlaying;
                float elapsed = Elapsed;
                Transform? anchor = ScreenAnchor;
                Vector2 size = _screenSize;
                TearDownGenerated();
                if (anchor != null)
                {
                    ScreenAnchor = anchor;
                    _screenSize = size;
                    EnsureBuilt();
                    AttachScreenRoot(anchor);
                    PlaceSpatialRoot(anchor);
                    Layout(size);
                    IsPlaying = resume;
                    Elapsed = elapsed;
                    Render(elapsed);
                }
            }
        }

        public void Play(Transform screenAnchor, Vector2 screenSize)
        {
            if (screenAnchor == null) throw new ArgumentNullException(nameof(screenAnchor));
            if (screenSize.x <= 0f || screenSize.y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(screenSize), "Screen size must be positive.");

            ScreenAnchor = screenAnchor;
            _screenSize = screenSize;
            EnsureBuilt();
            AttachScreenRoot(screenAnchor);
            PlaceSpatialRoot(screenAnchor);
            Layout(screenSize);
            Elapsed = 0f;
            IsPlaying = true;
            Render(0f);
        }

        public void Stop()
        {
            IsPlaying = false;
            SetRootsVisible(false);
        }

        /// <summary>指定時刻の見た目だけを適用する。再生状態と経過時刻は変更しない。</summary>
        public void Sample(float seconds)
        {
            EnsureBuilt();
            if (_screenSize.x > 0f && _screenSize.y > 0f) Layout(_screenSize);
            Render(seconds);
        }

        private void Start()
        {
            if (Application.isPlaying && autoPlay) Play(transform, new Vector2(1.6f, 0.9f));
        }

        private void Update()
        {
            if (!Application.isPlaying || !IsPlaying) return;
            Elapsed = Mathf.Min(Duration, Elapsed + Time.unscaledDeltaTime);
            Render(Elapsed);
            if (Elapsed >= Duration) IsPlaying = false;
        }

        private void OnDisable() => Stop();

        private void OnDestroy() => TearDownGenerated();

        private void EnsureBuilt()
        {
            if (_screenRoot != null && _spatialRoot != null) return;

            _quadMesh = BuildUnitQuad();
            if (fxMaterial != null)
            {
                _flatMaterial = new Material(fxMaterial) { name = "Unauthorized Access Flat (runtime)" };
                _flatMaterial.SetTexture(MainTexId, Texture2D.whiteTexture);
                _flatMaterial.SetColor(ColorId, Color.white);
                _interferenceMaterial = new Material(fxMaterial) { name = "Unauthorized Access Interference (runtime)" };
                _interferenceMaterial.SetTexture(MainTexId,
                    interference != null ? interference : Texture2D.whiteTexture);
                _interferenceMaterial.SetColor(ColorId, Color.white);
            }

            var screenGo = new GameObject("UnauthorizedAccess.Screen");
            if (gameObject.scene.IsValid())
                SceneManager.MoveGameObjectToScene(screenGo, gameObject.scene);
            _screenRoot = screenGo.transform;
            _screenRoot.SetParent(transform, false);

            var spatialGo = new GameObject("UnauthorizedAccess.Spatial");
            if (gameObject.scene.IsValid())
                SceneManager.MoveGameObjectToScene(spatialGo, gameObject.scene);
            _spatialRoot = spatialGo.transform;

            BuildScreen();
            BuildSpatial();
            SetRootsVisible(false);
        }

        private void BuildScreen()
        {
            Transform root = _screenRoot!;
            AddScreenQuad("Panel", Vector2.zero, new Vector2(0.92f, 0.72f), 0.030f, DeepRed, false);
            AddScreenQuad("Interference", Vector2.zero, new Vector2(0.98f, 0.90f), 0.020f,
                new Color(1f, 0.55f, 0.48f, 0.42f), true);

            AddScreenQuad("TopRule", new Vector2(0f, 0.35f), new Vector2(0.92f, 0.005f), 0.005f, Red, false);
            AddScreenQuad("BottomRule", new Vector2(0f, -0.35f), new Vector2(0.92f, 0.005f), 0.005f, Red, false);
            AddScreenQuad("LeftRule", new Vector2(-0.46f, 0f), new Vector2(0.003f, 0.70f), 0.005f, Red, false);
            AddScreenQuad("RightRule", new Vector2(0.46f, 0f), new Vector2(0.003f, 0.70f), 0.005f, Red, false);
            AddScreenQuad("UpperHairline", new Vector2(0.05f, 0.29f), new Vector2(0.62f, 0.0025f), 0f, Coral, false);
            AddScreenQuad("LowerHairline", new Vector2(-0.05f, -0.29f), new Vector2(0.62f, 0.0025f), 0f, Coral, false);

            // 三角警告記号。細い3本だけで、中央の文字面積を奪わない。
            AddScreenQuad("TriangleLeft", new Vector2(-0.315f, 0.105f), new Vector2(0.075f, 0.005f), -0.005f,
                Coral, false, 60f);
            AddScreenQuad("TriangleRight", new Vector2(-0.265f, 0.105f), new Vector2(0.075f, 0.005f), -0.005f,
                Coral, false, -60f);
            AddScreenQuad("TriangleBase", new Vector2(-0.29f, 0.065f), new Vector2(0.060f, 0.005f), -0.005f,
                Coral, false);
            AddScreenQuad("TriangleStem", Vector2.zero, Vector2.one, -0.006f, Coral, false);
            AddScreenQuad("TriangleDot", Vector2.zero, Vector2.one, -0.006f, Coral, false);

            Vector2[] fragmentCenters =
            {
                new Vector2(-0.35f, 0.23f), new Vector2(0.37f, 0.25f),
                new Vector2(-0.39f, -0.22f), new Vector2(0.34f, -0.24f),
                new Vector2(-0.43f, 0.16f), new Vector2(0.43f, -0.13f),
            };
            for (int i = 0; i < fragmentCenters.Length; i++)
            {
                float width = i % 2 == 0 ? 0.12f : 0.085f;
                AddScreenQuad($"Fragment{i:00}", fragmentCenters[i], new Vector2(width, 0.006f), -0.010f,
                    i % 3 == 0 ? Coral : DimCoral, false, 0f, 0.7f + i * 0.83f);
            }

            _warningText = CreateText("Warning", root, "WARNING", new Color(1f, 0.24f, 0.19f, 1f),
                TextAlignmentOptions.Center, 0.18f);
            _warningText.characterSpacing = 5f;
            _japaneseText = CreateText("Japanese", root, "不正アクセスを検出", new Color(1f, 0.58f, 0.49f, 1f),
                TextAlignmentOptions.Center, 0.11f);
            _englishText = CreateText("English", root, "UNAUTHORIZED ACCESS", new Color(0.94f, 0.36f, 0.30f, 1f),
                TextAlignmentOptions.Center, 0.065f);
        }

        private void BuildSpatial()
        {
            Transform root = _spatialRoot!;
            for (int i = 0; i < BinaryColumnCount; i++)
            {
                TextMeshPro text = CreateText($"Binary{i:00}", root, i % 2 == 0 ? BinaryA : BinaryB,
                    i % 3 == 0 ? Coral : Red, TextAlignmentOptions.TopLeft, 0.045f);
                var digits = new System.Text.StringBuilder();
                var random = new System.Random(41 + i * 131);
                int length = 7 + (i * 7) % 12;
                for (int n = 0; n < length; n++)
                {
                    if (n != 0) digits.Append('\n');
                    digits.Append(random.Next(2) == 0 ? '0' : '1');
                }
                text.text = digits.ToString();
                _spatialTexts.Add(new SpatialTextVisual(text, i * 0.73f));
            }
            for (int i = 0; i < ErrorLabelCount; i++)
            {
                TextMeshPro text = CreateText($"Error{i:00}", root, ErrorLabels[i], Coral,
                    TextAlignmentOptions.Left, 0.055f);
                _spatialTexts.Add(new SpatialTextVisual(text, 2.1f + i * 1.17f));
            }
            for (int i = 0; i < SpatialLineCount; i++)
            {
                QuadVisual line = CreateQuad($"SpatialLine{i:00}", root, _flatMaterial, DimCoral);
                line.Phase = 1.4f + i * 1.31f;
                _spatialLines.Add(line);
            }
        }

        private void Layout(Vector2 size)
        {
            LayoutScreen(size);
            LayoutSpatial(size);
        }

        private void LayoutScreen(Vector2 size)
        {
            if (_screenRoot == null) return;
            SetWorldUnitLocalScale(_screenRoot);
            for (int i = 0; i < _screenQuads.Count; i++)
            {
                QuadVisual q = _screenQuads[i];
                q.Transform.localPosition = new Vector3(q.Center.x * size.x, q.Center.y * size.y, q.Z);
                q.BasePosition = q.Transform.localPosition;
                q.Transform.localScale = new Vector3(q.Size.x * size.x, q.Size.y * size.y, 1f);
                if (q.Transform.name.StartsWith("Triangle", StringComparison.Ordinal))
                {
                    float side = size.y * .19f;
                    float height = side * .8660254f;
                    Vector3 center = new Vector3(-.29f * size.x, .13f * size.y, -.005f);
                    Vector3 scale = new Vector3(side, size.y * .0035f, 1f);
                    if (q.Transform.name == "TriangleLeft") center.x -= side * .25f;
                    else if (q.Transform.name == "TriangleRight") center.x += side * .25f;
                    else if (q.Transform.name == "TriangleBase") center.y -= height * .5f;
                    else if (q.Transform.name == "TriangleStem")
                    {
                        center.y -= height * .06f;
                        scale = new Vector3(size.y * .011f, height * .32f, 1f);
                    }
                    else
                    {
                        center.y -= height * .32f;
                        scale = new Vector3(size.y * .012f, size.y * .012f, 1f);
                    }
                    q.Transform.localPosition = center;
                    q.Transform.localScale = scale;
                    q.BasePosition = center;
                }
            }

            LayoutText(_warningText, new Vector3(0.075f * size.x, 0.115f * size.y, -0.025f),
                0.60f * size.x, 0.22f * size.y, 0.22f);
            LayoutText(_japaneseText, new Vector3(0f, -0.055f * size.y, -0.030f),
                0.74f * size.x, 0.16f * size.y, 0.11f);
            LayoutText(_englishText, new Vector3(0f, -0.205f * size.y, -0.035f),
                0.62f * size.x, 0.11f * size.y, 0.065f);
        }

        private void LayoutSpatial(Vector2 size)
        {
            if (_spatialRoot == null) return;
            for (int i = 0; i < BinaryColumnCount; i++)
            {
                bool left = i < BinaryColumnCount / 2;
                int sideIndex = i % (BinaryColumnCount / 2);
                float edge = Mathf.Lerp(0.38f, 0.88f, sideIndex / 8f);
                float x = (left ? -edge : edge) * size.x;
                float y = Mathf.Lerp(-0.34f, 0.33f, ((sideIndex * 5 + (left ? 1 : 3)) % 9) / 8f) * size.y;
                float depth01 = ((sideIndex * 3 + (left ? 0 : 2)) % 9) / 8f;
                float z = Mathf.Lerp(-0.25f, -1.0f, depth01);
                SpatialTextVisual s = _spatialTexts[i];
                s.BasePosition = new Vector3(x, y, z);
                s.Depth01 = depth01;
                float worldEm = Mathf.Lerp(0.034f, 0.072f, depth01);
                SetTextDimensions(s.Text, 0.22f, size.y * 0.78f, worldEm);
            }

            for (int i = 0; i < ErrorLabelCount; i++)
            {
                SpatialTextVisual s = _spatialTexts[BinaryColumnCount + i];
                bool left = i % 2 == 0;
                float depth01 = 0.18f + i * 0.22f;
                s.BasePosition = new Vector3((left ? -1f : 1f) * size.x * (0.48f + i * 0.02f),
                    size.y * (i < 2 ? .53f : -.54f), Mathf.Lerp(-0.28f, -0.9f, depth01));
                s.Depth01 = depth01;
                SetTextDimensions(s.Text, size.x * 0.34f, 0.12f, Mathf.Lerp(0.050f, 0.038f, depth01));
            }

            for (int i = 0; i < _spatialLines.Count; i++)
            {
                QuadVisual line = _spatialLines[i];
                bool left = i % 2 == 0;
                line.BasePosition = new Vector3((left ? -1f : 1f) * size.x * (0.56f + i * 0.05f),
                    size.y * (0.30f - i * 0.19f), -0.36f - i * 0.16f);
                line.Transform.localPosition = line.BasePosition;
                line.Transform.localScale = new Vector3(size.x * (0.18f + i * 0.025f), 0.006f, 1f);
            }
        }

        private void Render(float seconds)
        {
            bool inRange = seconds >= 0f && seconds < Duration;
            if (!inRange)
            {
                SetRootsVisible(false);
                return;
            }

            SetRootsVisible(true);
            if (_screenRoot != null) SetWorldUnitLocalScale(_screenRoot);
            float appear = Smooth01(seconds / AppearEndSec);
            float expand = Smooth01((seconds - AppearEndSec) / (ExpandEndSec - AppearEndSec));
            float fade = 1f - Smooth01((seconds - FadeStartSec) / (Duration - FadeStartSec));
            float alpha = appear * fade;
            float spread = Mathf.Lerp(0.88f, 1f, expand);

            for (int i = 0; i < _screenQuads.Count; i++)
            {
                QuadVisual q = _screenQuads[i];
                float decoration = q.IsInterference ? .86f + .14f * Mathf.Sin(seconds * 2.3f) : 1f;
                float fragment = q.Phase <= 0f ? 1f : Smooth01((seconds - 0.45f) / 0.9f);
                q.SetAlpha(alpha * decoration * fragment);
                Vector3 p = q.BasePosition;
                if (q.Phase > 0f) p.x += Mathf.Sin(seconds * 1.35f + q.Phase) * 0.012f;
                q.Transform.localPosition = p;
            }

            if (_screenRoot != null)
                _screenRoot.localScale = Vector3.Scale(_screenRoot.localScale, new Vector3(spread, spread, 1f));
            SetTextAlpha(_warningText, alpha * Smooth01((seconds - 0.12f) / 0.42f));
            SetTextAlpha(_japaneseText, alpha * Smooth01((seconds - 0.30f) / 0.48f));
            SetTextAlpha(_englishText, alpha * Smooth01((seconds - 0.50f) / 0.55f));

            float spatialReveal = Smooth01((seconds - 0.12f) / 1.65f);
            for (int i = 0; i < _spatialTexts.Count; i++)
            {
                SpatialTextVisual s = _spatialTexts[i];
                float driftX = Mathf.Sin(seconds * 0.42f + s.Phase) * 0.022f;
                float driftY = Mathf.Sin(seconds * 0.31f + s.Phase * 1.7f) * 0.018f;
                if (i < BinaryColumnCount) driftY -= seconds * (.035f + .035f * s.Depth01);
                s.Text.transform.localPosition = s.BasePosition + new Vector3(driftX, driftY, 0f);
                float depthDim = Mathf.Lerp(0.40f, 0.85f, s.Depth01);
                SetTextAlpha(s.Text, alpha * spatialReveal * depthDim);
            }
            for (int i = 0; i < _spatialLines.Count; i++)
            {
                QuadVisual q = _spatialLines[i];
                Vector3 p = q.BasePosition;
                p.y += Mathf.Sin(seconds * 0.28f + q.Phase) * 0.016f;
                q.Transform.localPosition = p;
                q.SetAlpha(alpha * spatialReveal * Mathf.Lerp(0.55f, 0.28f, i / 3f));
            }
        }

        private void AddScreenQuad(string name, Vector2 center, Vector2 size, float z, Color color,
            bool useInterference, float rotationZ = 0f, float phase = 0f)
        {
            QuadVisual q = CreateQuad(name, _screenRoot!,
                useInterference ? _interferenceMaterial : _flatMaterial, color);
            q.Center = center;
            q.Size = size;
            q.Z = z;
            q.Phase = phase;
            q.IsInterference = useInterference;
            q.Transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);
            _screenQuads.Add(q);
        }

        private QuadVisual CreateQuad(string name, Transform parent, Material? material, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;
            return new QuadVisual(go.transform, renderer, color);
        }

        private TextMeshPro CreateText(string name, Transform parent, string value, Color color,
            TextAlignmentOptions alignment, float worldEm)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            TextMeshPro text = go.AddComponent<TextMeshPro>();
            if (font != null) text.font = font;
            text.text = value;
            text.fontSize = FontSize;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.richText = false;
            text.color = color;
            SetTextDimensions(text, 1f, 1f, worldEm);
            return text;
        }

        private static void LayoutText(TextMeshPro? text, Vector3 position, float width, float height, float worldEm)
        {
            if (text == null) return;
            text.transform.localPosition = position;
            SetTextDimensions(text, width, height, worldEm);
        }

        private static void SetTextDimensions(TextMeshPro text, float width, float height, float worldEm)
        {
            float scale = worldEm / (FontSize * 0.1f);
            RectTransform rect = text.rectTransform;
            rect.sizeDelta = new Vector2(width / scale, height / scale);
            text.transform.localScale = Vector3.one * scale;
            text.transform.localRotation = Quaternion.identity;
        }

        private void AttachScreenRoot(Transform anchor)
        {
            if (_screenRoot == null) return;
            _screenRoot.SetParent(anchor, false);
            _screenRoot.localPosition = new Vector3(0f, 0f, -0.06f * SafeInverse(anchor.lossyScale.z));
            _screenRoot.localRotation = Quaternion.identity;
            SetWorldUnitLocalScale(_screenRoot);
        }

        private void PlaceSpatialRoot(Transform anchor)
        {
            if (_spatialRoot == null) return;
            _spatialRoot.SetParent(null, false);
            _spatialRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
            _spatialRoot.localScale = Vector3.one;
        }

        private static void SetWorldUnitLocalScale(Transform root)
        {
            Transform? parent = root.parent;
            if (parent == null)
            {
                root.localScale = Vector3.one;
                return;
            }
            Vector3 s = parent.lossyScale;
            root.localScale = new Vector3(SafeInverse(s.x), SafeInverse(s.y), SafeInverse(s.z));
        }

        private static float SafeInverse(float value) => Mathf.Abs(value) > 0.0001f ? 1f / value : 1f;

        private static float Smooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }

        private static void SetTextAlpha(TextMeshPro? text, float alpha)
        {
            if (text == null) return;
            Color c = text.color;
            c.a = Mathf.Clamp01(alpha);
            text.color = c;
        }

        private void SetRootsVisible(bool visible)
        {
            if (_screenRoot != null && _screenRoot.gameObject.activeSelf != visible)
                _screenRoot.gameObject.SetActive(visible);
            if (_spatialRoot != null && _spatialRoot.gameObject.activeSelf != visible)
                _spatialRoot.gameObject.SetActive(visible);
        }

        private void TearDownGenerated()
        {
            IsPlaying = false;
            SetRootsVisible(false);
            DestroySafe(_screenRoot != null ? _screenRoot.gameObject : null);
            DestroySafe(_spatialRoot != null ? _spatialRoot.gameObject : null);
            DestroySafe(_flatMaterial);
            DestroySafe(_interferenceMaterial);
            DestroySafe(_quadMesh);
            _screenRoot = null;
            _spatialRoot = null;
            _flatMaterial = null;
            _interferenceMaterial = null;
            _quadMesh = null;
            _warningText = null;
            _japaneseText = null;
            _englishText = null;
            _screenQuads.Clear();
            _spatialTexts.Clear();
            _spatialLines.Clear();
        }

        private static void DestroySafe(UnityEngine.Object? value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        private static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "Unauthorized Access Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            });
            mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            mesh.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private sealed class QuadVisual
        {
            private readonly MeshRenderer _renderer;
            private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
            private readonly Color _color;

            public readonly Transform Transform;
            public Vector2 Center;
            public Vector2 Size;
            public float Z;
            public float Phase;
            public bool IsInterference;
            public Vector3 BasePosition;

            public QuadVisual(Transform transform, MeshRenderer renderer, Color color)
            {
                Transform = transform;
                _renderer = renderer;
                _color = color;
            }

            public void SetAlpha(float alpha)
            {
                Color c = _color;
                c.a *= Mathf.Clamp01(alpha);
                _block.SetColor(ColorId, c);
                _renderer.SetPropertyBlock(_block);
            }
        }

        private sealed class SpatialTextVisual
        {
            public readonly TextMeshPro Text;
            public readonly float Phase;
            public Vector3 BasePosition;
            public float Depth01;

            public SpatialTextVisual(TextMeshPro text, float phase)
            {
                Text = text;
                Phase = phase;
            }
        }
    }
}
