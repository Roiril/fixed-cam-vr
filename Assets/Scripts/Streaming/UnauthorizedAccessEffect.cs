#nullable enable

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 独立した警告パーツを映像面へ出し、発火位置に数字と干渉光を残す。
    /// 映像面は指定 anchor を追い、空間側は発火した瞬間の世界座標へ固定する。
    /// </summary>
    [ExecuteAlways]
    public sealed class UnauthorizedAccessEffect : MonoBehaviour
    {
        public const float EffectDuration = 7f;

        private const int WarningBandCount = 11;
        private const int SubtitleBandCount = 3;
        private const int SymbolQuadCount = 1;
        private const int DecorationQuadCount = 14;
        private const int GlyphCount = 220;
        private const int FarGlyphCount = 100;
        private const int MiddleGlyphCount = 100;
        private const int NearGlyphCount = 4;
        private const int InterferenceStripCount = 12;
        private const int GlyphVertexCount = GlyphCount * 4;
        private const int WarningVertexCount = WarningBandCount * 4;
        private const int SubtitleVertexCount = SubtitleBandCount * 4;
        private const int SymbolVertexCount = SymbolQuadCount * 4;
        private const int DecorationVertexCount = DecorationQuadCount * 4;
        private const int StripVertexCount = InterferenceStripCount * 4;
        private const int RandomSeed = 0x5A17C0DE;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly Color AlertRed = new Color(1f, .06f, .085f, 1f);

        private static readonly float[] BundlePositions =
        {
            -.88f, -.76f, -.61f, -.43f, -.28f, -.11f,
            .24f, .39f, .53f, .69f, .81f, .94f,
        };

        private static readonly float[] WarningBandEdges =
        {
            0f, .058f, .137f, .224f, .298f, .404f, .487f, .552f, .685f, .752f, .883f, 1f,
        };

        private static readonly float[] SubtitleBandEdges = { 0f, .32f, .68f, 1f };

        private static readonly Vector2[] NearPositions =
        {
            new Vector2(-.67f, -.56f), new Vector2(.76f, .40f),
            new Vector2(.38f, .91f), new Vector2(-.32f, -.96f),
        };

        [SerializeField] private Material? fxMaterial;
        [SerializeField] private Texture2D? warningGraphic;
        [SerializeField] private Texture2D? subtitleGraphic;
        [SerializeField] private Texture2D? symbolGraphic;
        [SerializeField] private Texture2D? interference;
        [SerializeField] private bool autoPlay;

        private Transform? _screenRoot;
        private Transform? _spatialRoot;
        private Transform? _warningTransform;
        private Mesh? _warningMesh;
        private Mesh? _subtitleMesh;
        private Mesh? _symbolMesh;
        private Mesh? _decorationMesh;
        private Mesh? _glyphMesh;
        private Mesh? _interferenceMesh;
        private Material? _warningMaterial;
        private Material? _subtitleMaterial;
        private Material? _symbolMaterial;
        private Material? _decorationMaterial;
        private Material? _glyphMaterial;
        private Material? _interferenceMaterial;

        private readonly Vector3[] _warningVertices = new Vector3[WarningVertexCount];
        private readonly Vector2[] _warningUvs = new Vector2[WarningVertexCount];
        private readonly Color[] _warningColors = new Color[WarningVertexCount];
        private readonly int[] _warningTriangles = new int[WarningBandCount * 6];

        private readonly Vector3[] _subtitleVertices = new Vector3[SubtitleVertexCount];
        private readonly Vector2[] _subtitleUvs = new Vector2[SubtitleVertexCount];
        private readonly Color[] _subtitleColors = new Color[SubtitleVertexCount];
        private readonly int[] _subtitleTriangles = new int[SubtitleBandCount * 6];

        private readonly Vector3[] _symbolVertices = new Vector3[SymbolVertexCount];
        private readonly Vector2[] _symbolUvs = new Vector2[SymbolVertexCount];
        private readonly Color[] _symbolColors = new Color[SymbolVertexCount];
        private readonly int[] _symbolTriangles = new int[SymbolQuadCount * 6];

        private readonly Vector3[] _decorationVertices = new Vector3[DecorationVertexCount];
        private readonly Vector2[] _decorationUvs = new Vector2[DecorationVertexCount];
        private readonly Color[] _decorationColors = new Color[DecorationVertexCount];
        private readonly int[] _decorationTriangles = new int[DecorationQuadCount * 6];

        private readonly Vector3[] _glyphVertices = new Vector3[GlyphVertexCount];
        private readonly Vector2[] _glyphUvs = new Vector2[GlyphVertexCount];
        private readonly Vector2[] _glyphData = new Vector2[GlyphVertexCount];
        private readonly Color[] _glyphColors = new Color[GlyphVertexCount];
        private readonly int[] _glyphTriangles = new int[GlyphCount * 6];
        private readonly GlyphSpec[] _glyphs = new GlyphSpec[GlyphCount];

        private readonly Vector3[] _interferenceVertices = new Vector3[StripVertexCount];
        private readonly Vector2[] _interferenceUvs = new Vector2[StripVertexCount];
        private readonly Color[] _interferenceColors = new Color[StripVertexCount];
        private readonly int[] _interferenceTriangles = new int[InterferenceStripCount * 6];
        private readonly StripSpec[] _strips = new StripSpec[InterferenceStripCount];

        private readonly float[] _bandCollapseStart = new float[WarningBandCount];
        private readonly float[] _bandCollapseDuration = new float[WarningBandCount];
        private readonly float[] _bandCollapseDirection = new float[WarningBandCount];

        private Vector2 _screenSize;
        private float _warningWidth;
        private float _warningHeight;
        private float _subtitleWidth;
        private float _subtitleHeight;
        private float _symbolSize;
        private float _maximumFrontDepth = 1.5f;
        private float _audienceDistance = 3f;

        public float Duration => EffectDuration;
        public bool IsPlaying { get; private set; }
        public float Elapsed { get; private set; }
        public int SpatialElementCount => GlyphCount + InterferenceStripCount;
        public int GlyphCountDiagnostic => GlyphCount;
        public int WarningBandCountDiagnostic => WarningBandCount;
        public int GlyphVertexCountDiagnostic => _glyphMesh != null ? _glyphMesh.vertexCount : 0;
        public int WarningVertexCountDiagnostic => _warningMesh != null ? _warningMesh.vertexCount : 0;
        public int SubtitleVertexCountDiagnostic => _subtitleMesh != null ? _subtitleMesh.vertexCount : 0;
        public int SymbolVertexCountDiagnostic => _symbolMesh != null ? _symbolMesh.vertexCount : 0;
        public Transform? ScreenAnchor { get; private set; }

        public void Configure(Material fxMaterial, Texture2D warningGraphic, Texture2D subtitleGraphic,
            Texture2D symbolGraphic, Texture2D interference)
        {
            bool changed = this.fxMaterial != fxMaterial || this.warningGraphic != warningGraphic ||
                this.subtitleGraphic != subtitleGraphic || this.symbolGraphic != symbolGraphic ||
                this.interference != interference;
            this.fxMaterial = fxMaterial;
            this.warningGraphic = warningGraphic;
            this.subtitleGraphic = subtitleGraphic;
            this.symbolGraphic = symbolGraphic;
            this.interference = interference;
            if (!changed || _screenRoot == null) return;

            bool resume = IsPlaying;
            float elapsed = Elapsed;
            Transform? anchor = ScreenAnchor;
            Vector2 size = _screenSize;
            Vector3 spatialPosition = _spatialRoot != null ? _spatialRoot.position : Vector3.zero;
            Quaternion spatialRotation = _spatialRoot != null ? _spatialRoot.rotation : Quaternion.identity;
            TearDownGenerated();
            if (anchor == null) return;

            ScreenAnchor = anchor;
            _screenSize = size;
            EnsureBuilt();
            AttachScreenRoot(anchor);
            if (_spatialRoot != null)
                _spatialRoot.SetPositionAndRotation(spatialPosition, spatialRotation);
            Layout(size);
            IsPlaying = resume;
            Elapsed = elapsed;
            Render(elapsed);
        }

        public void Play(Transform screenAnchor, Vector2 screenSize)
        {
            if (screenAnchor == null) throw new ArgumentNullException(nameof(screenAnchor));
            if (screenSize.x <= 0f || screenSize.y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(screenSize), "Screen size must be positive.");

            ScreenAnchor = screenAnchor;
            _screenSize = screenSize;
            MeasureAudience(screenAnchor);
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

            BuildMaterials();
            CreateRoots();
            InitializeBandData();
            InitializeScreenPartData();
            InitializeGlyphData();
            InitializeStripData();
            _warningMesh = CreateMesh("Unauthorized Access Warning Bands", _warningVertices,
                _warningUvs, null, _warningColors, _warningTriangles);
            _subtitleMesh = CreateMesh("Unauthorized Access Subtitle Bands", _subtitleVertices,
                _subtitleUvs, null, _subtitleColors, _subtitleTriangles);
            _symbolMesh = CreateMesh("Unauthorized Access Symbol", _symbolVertices,
                _symbolUvs, null, _symbolColors, _symbolTriangles);
            _decorationMesh = CreateMesh("Unauthorized Access Decorations", _decorationVertices,
                _decorationUvs, null, _decorationColors, _decorationTriangles);
            _glyphMesh = CreateMesh("Unauthorized Access Glyphs", _glyphVertices,
                _glyphUvs, _glyphData, _glyphColors, _glyphTriangles);
            _interferenceMesh = CreateMesh("Unauthorized Access Interference", _interferenceVertices,
                _interferenceUvs, null, _interferenceColors, _interferenceTriangles);

            _symbolTransform = CreateMeshObject("WarningSymbol", _screenRoot!, _symbolMesh, _symbolMaterial);
            CreateMeshObject("WarningDecorations", _screenRoot!, _decorationMesh, _decorationMaterial);
            _warningTransform = CreateMeshObject("WarningBands", _screenRoot!, _warningMesh, _warningMaterial);
            _warningTransform.localRotation = Quaternion.Euler(0f, 0f, -.6f);
            CreateMeshObject("SubtitleBands", _screenRoot!, _subtitleMesh, _subtitleMaterial);
            CreateMeshObject("BinaryField", _spatialRoot!, _glyphMesh, _glyphMaterial);
            CreateMeshObject("InterferenceLight", _spatialRoot!, _interferenceMesh, _interferenceMaterial);
            SetRootsVisible(false);
        }

        private Transform? _symbolTransform;

        private void BuildMaterials()
        {
            if (fxMaterial == null) return;

            _symbolMaterial = CreateRuntimeMaterial("Unauthorized Access Symbol", 2f,
                symbolGraphic != null ? symbolGraphic : Texture2D.whiteTexture, 1f, 3448);
            _decorationMaterial = CreateRuntimeMaterial("Unauthorized Access Decorations", 2f,
                Texture2D.whiteTexture, 1f, 3449);
            _warningMaterial = CreateRuntimeMaterial("Unauthorized Access Warning", 2f,
                warningGraphic != null ? warningGraphic : Texture2D.whiteTexture, 1f, 3450);
            _subtitleMaterial = CreateRuntimeMaterial("Unauthorized Access Subtitle", 2f,
                subtitleGraphic != null ? subtitleGraphic : Texture2D.whiteTexture, 1f, 3451);
            _glyphMaterial = CreateRuntimeMaterial("Unauthorized Access Glyph", 1f,
                Texture2D.whiteTexture, 1.18f);
            _interferenceMaterial = CreateRuntimeMaterial("Unauthorized Access Interference", 0f,
                interference != null ? interference : Texture2D.whiteTexture, .82f);
        }

        private Material CreateRuntimeMaterial(string materialName, float mode, Texture texture, float strength,
            int renderQueue = -1)
        {
            var material = new Material(fxMaterial!) { name = materialName + " (runtime)" };
            material.SetTexture(MainTexId, texture);
            material.SetColor(ColorId, Color.white);
            material.SetFloat(ModeId, mode);
            material.SetFloat(StrengthId, strength);
            if (renderQueue >= 0) material.renderQueue = renderQueue;
            return material;
        }

        private void CreateRoots()
        {
            var screenGo = new GameObject("UnauthorizedAccess.Screen");
            if (gameObject.scene.IsValid()) SceneManager.MoveGameObjectToScene(screenGo, gameObject.scene);
            _screenRoot = screenGo.transform;
            _screenRoot.SetParent(transform, false);

            var spatialGo = new GameObject("UnauthorizedAccess.Spatial");
            if (gameObject.scene.IsValid()) SceneManager.MoveGameObjectToScene(spatialGo, gameObject.scene);
            _spatialRoot = spatialGo.transform;
        }

        private static Transform CreateMeshObject(string name, Transform parent, Mesh mesh, Material? material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;
            return go.transform;
        }

        private void InitializeBandData()
        {
            var random = new System.Random(RandomSeed ^ 0x114A);
            for (int i = 0; i < WarningBandCount; i++)
            {
                int vertex = i * 4;
                SetQuadUvs(_warningUvs, vertex, 0f, WarningBandEdges[i], 1f, WarningBandEdges[i + 1]);
                SetQuadTriangles(_warningTriangles, i, vertex);
                _bandCollapseStart[i] = 4.9f + (float)random.NextDouble() * .85f;
                _bandCollapseDuration[i] = .16f + (float)random.NextDouble() * .34f;
                _bandCollapseDuration[i] = Mathf.Min(_bandCollapseDuration[i], 6.18f - _bandCollapseStart[i]);
                _bandCollapseDirection[i] = random.Next(2) == 0 ? -1f : 1f;
            }

            for (int i = 0; i < SubtitleBandCount; i++)
            {
                int vertex = i * 4;
                SetQuadUvs(_subtitleUvs, vertex, 0f, SubtitleBandEdges[i], 1f, SubtitleBandEdges[i + 1]);
                SetQuadTriangles(_subtitleTriangles, i, vertex);
            }
        }

        private void InitializeScreenPartData()
        {
            SetQuadUvs(_symbolUvs, 0, 0f, 0f, 1f, 1f);
            SetQuadTriangles(_symbolTriangles, 0, 0);
            for (int i = 0; i < DecorationQuadCount; i++)
            {
                int vertex = i * 4;
                SetQuadUvs(_decorationUvs, vertex, 0f, 0f, 1f, 1f);
                SetQuadTriangles(_decorationTriangles, i, vertex);
            }
        }

        private void InitializeGlyphData()
        {
            var random = new System.Random(RandomSeed);
            for (int i = 0; i < GlyphCount; i++)
            {
                GlyphKind kind;
                if (i < FarGlyphCount) kind = GlyphKind.Far;
                else if (i < FarGlyphCount + MiddleGlyphCount) kind = GlyphKind.Middle;
                else if (i < FarGlyphCount + MiddleGlyphCount + NearGlyphCount) kind = GlyphKind.Near;
                else kind = GlyphKind.Fragment;

                int cluster = ChooseCluster(random);
                int bundle = kind == GlyphKind.Middle ? (i - FarGlyphCount) % 12 :
                    kind == GlyphKind.Near ? i - FarGlyphCount - MiddleGlyphCount : -1;
                float size = kind switch
                {
                    GlyphKind.Far => Range(random, .035f, .07f),
                    GlyphKind.Middle => Range(random, .075f, .15f),
                    GlyphKind.Near => Range(random, .45f, .65f),
                    _ => Range(random, .055f, .18f),
                };
                float depth = kind switch
                {
                    GlyphKind.Far => Range(random, .2f, .4f),
                    GlyphKind.Middle => Range(random, .55f, 1.1f),
                    GlyphKind.Near => Range(random, 1.38f, 1.5f),
                    _ => Range(random, .32f, 1.28f),
                };
                _glyphs[i] = new GlyphSpec
                {
                    Kind = kind,
                    Cluster = cluster,
                    Bundle = bundle,
                    Digit = random.Next(2),
                    Blur = kind == GlyphKind.Near ? Range(random, .5f, .85f) : Range(random, 0f, .22f),
                    Size = size,
                    Depth = depth,
                    X = Range(random, 0f, 1f),
                    Y = Range(random, 0f, 1f),
                    Phase = Range(random, 0f, 12f),
                    Speed = kind == GlyphKind.Near ? Range(random, .006f, .018f) : Range(random, .1f, .38f),
                    Delay = i < 5 ? 0f : Range(random, 0f, .48f),
                    BaseColor = PickGlyphColor(random, kind),
                };

                int vertex = i * 4;
                SetQuadUvs(_glyphUvs, vertex, 0f, 0f, 1f, 1f);
                Vector2 data = new Vector2(_glyphs[i].Digit, _glyphs[i].Blur);
                _glyphData[vertex] = data;
                _glyphData[vertex + 1] = data;
                _glyphData[vertex + 2] = data;
                _glyphData[vertex + 3] = data;
                SetQuadTriangles(_glyphTriangles, i, vertex);
            }
        }

        private void InitializeStripData()
        {
            var random = new System.Random(RandomSeed ^ 0x71D3);
            for (int i = 0; i < InterferenceStripCount; i++)
            {
                float vMin = Range(random, .30f, .48f);
                float vMax = Range(random, .55f, .68f);
                _strips[i] = new StripSpec
                {
                    X = Range(random, -.82f, .78f),
                    Y = Range(random, -.48f, .52f),
                    Depth = Range(random, .24f, 1.18f),
                    Width = Range(random, .35f, 1.1f),
                    Height = Range(random, .06f, .24f),
                    UMin = Range(random, 0f, .55f),
                    UMax = Range(random, .58f, 1f),
                    VMin = vMin,
                    VMax = vMax,
                    Phase = Range(random, 0f, 8f),
                    Alpha = Range(random, .10f, .30f),
                };
                int vertex = i * 4;
                SetQuadUvs(_interferenceUvs, vertex, _strips[i].UMin, vMin, _strips[i].UMax, vMax);
                SetQuadTriangles(_interferenceTriangles, i, vertex);
            }
        }

        private void Layout(Vector2 size)
        {
            if (_screenRoot != null) SetWorldUnitLocalScale(_screenRoot);
            float warningAspect = TextureAspect(warningGraphic, 1406f / 257f);
            _warningWidth = size.x * 1.17f;
            _warningHeight = _warningWidth / warningAspect;
            float subtitleAspect = TextureAspect(subtitleGraphic, 805f / 107f);
            _subtitleWidth = size.x * .72f;
            _subtitleHeight = _subtitleWidth / subtitleAspect;
            _symbolSize = size.y * 1.32f;
        }

        private static float TextureAspect(Texture2D? texture, float fallback) =>
            texture != null && texture.height > 0 ? texture.width / (float)texture.height : fallback;

        private void Render(float seconds)
        {
            if (seconds < .06f || seconds >= Duration)
            {
                SetRootsVisible(false);
                return;
            }

            SetRootsVisible(true);
            if (_screenRoot != null) SetWorldUnitLocalScale(_screenRoot);
            UpdateSymbolMesh(seconds);
            UpdateDecorationMesh(seconds);
            UpdateWarningMesh(seconds);
            UpdateSubtitleMesh(seconds);
            UpdateGlyphMesh(seconds);
            UpdateInterferenceMesh(seconds);
        }

        private void UpdateWarningMesh(float seconds)
        {
            if (_warningMesh == null) return;

            float expansion = Mathf.Lerp(1f, 1.035f, Smooth01((seconds - .75f) / 4.15f));
            for (int i = 0; i < WarningBandCount; i++)
            {
                float xOffset = WarningBandOffset(seconds, i);
                float widthScale = WarningBandWidthScale(seconds, i);
                float yStep = WarningBandStep(seconds, i);
                float alpha = WarningBandAlpha(seconds, i);
                float width = _warningWidth * expansion * widthScale;
                float y0 = (_warningHeight * (WarningBandEdges[i] - .5f) + yStep) * expansion;
                float y1 = (_warningHeight * (WarningBandEdges[i + 1] - .5f) + yStep) * expansion;
                float x = (-.015f * _screenSize.x + xOffset) * expansion;
                float y = .11f * _screenSize.y + (y0 + y1) * .5f;
                SetQuadVertices(_warningVertices, i * 4, x, y, -.004f, width, y1 - y0);
                SetQuadColor(_warningColors, i * 4, WithAlpha(AlertRed, alpha));
            }
            _warningMesh.SetVertices(_warningVertices);
            _warningMesh.SetColors(_warningColors);
        }

        private void UpdateSubtitleMesh(float seconds)
        {
            if (_subtitleMesh == null) return;

            for (int i = 0; i < SubtitleBandCount; i++)
            {
                float y0 = _subtitleHeight * (SubtitleBandEdges[i] - .5f);
                float y1 = _subtitleHeight * (SubtitleBandEdges[i + 1] - .5f);
                float offset = SubtitleBandOffset(seconds, i);
                float alpha = SubtitleBandAlpha(seconds, i);
                SetQuadVertices(_subtitleVertices, i * 4, .03f * _screenSize.x + offset,
                    -.27f * _screenSize.y + (y0 + y1) * .5f, -.006f,
                    _subtitleWidth, y1 - y0);
                SetQuadColor(_subtitleColors, i * 4, WithAlpha(AlertRed, alpha));
            }
            _subtitleMesh.SetVertices(_subtitleVertices);
            _subtitleMesh.SetColors(_subtitleColors);
        }

        private void UpdateSymbolMesh(float seconds)
        {
            if (_symbolMesh == null) return;

            float reveal = Smooth01((seconds - .06f) / .035f);
            float ending = 1f - Smooth01((seconds - 4.72f) / .72f);
            float alpha = (.34f + Mathf.Sin(seconds * 2.1f) * .04f) * reveal * ending;
            SetQuadVertices(_symbolVertices, 0, -.24f * _screenSize.x, .10f * _screenSize.y,
                .006f, _symbolSize, _symbolSize);
            SetQuadColor(_symbolColors, 0, WithAlpha(AlertRed, alpha));
            _symbolMesh.SetVertices(_symbolVertices);
            _symbolMesh.SetColors(_symbolColors);
        }

        private void UpdateDecorationMesh(float seconds)
        {
            if (_decorationMesh == null) return;

            float reveal = Smooth01((seconds - .075f) / .045f);
            float ending = 1f - Smooth01((seconds - 5.18f) / .82f);
            float symbolX = -.24f * _screenSize.x;
            float symbolY = .10f * _screenSize.y;
            SetQuadVertices(_decorationVertices, 0, symbolX, symbolY + _symbolSize * .035f,
                .005f, _symbolSize * .055f, _symbolSize * .30f);
            SetQuadVertices(_decorationVertices, 4, symbolX, symbolY - _symbolSize * .185f,
                .005f, _symbolSize * .068f, _symbolSize * .068f);
            Color markColor = WithAlpha(AlertRed, .78f * reveal * ending);
            SetQuadColor(_decorationColors, 0, markColor);
            SetQuadColor(_decorationColors, 4, markColor);

            SetHazardGroup(2, .34f * _screenSize.x, .43f * _screenSize.y,
                .28f * _screenSize.x, .07f * _screenSize.y, seconds, .14f);
            SetHazardGroup(8, -.30f * _screenSize.x, -.46f * _screenSize.y,
                .18f * _screenSize.x, .07f * _screenSize.y, seconds, .2f);
            _decorationMesh.SetVertices(_decorationVertices);
            _decorationMesh.SetColors(_decorationColors);
        }

        private void SetHazardGroup(int firstQuad, float centerX, float centerY, float groupWidth,
            float height, float seconds, float phase)
        {
            const int stripeCount = 6;
            float reveal = Smooth01((seconds - .09f - phase * .05f) / .055f);
            float ending = 1f - Smooth01((seconds - 5.35f - phase) / .72f);
            float stripeWidth = groupWidth * .08f;
            float spacing = groupWidth / (stripeCount - 1f);
            float shear = height * .48f;
            Color color = WithAlpha(AlertRed, .68f * reveal * ending);
            for (int i = 0; i < stripeCount; i++)
            {
                float x = centerX - groupWidth * .5f + spacing * i;
                int vertex = (firstQuad + i) * 4;
                SetParallelogramVertices(_decorationVertices, vertex, x, centerY, .003f,
                    stripeWidth, height, shear);
                SetQuadColor(_decorationColors, vertex, color);
            }
        }

        private float WarningBandAlpha(float seconds, int band)
        {
            float hardAppear = Mathf.Clamp01((seconds - .095f) / .03f);
            if (seconds < .2f) return hardAppear;
            if (seconds < .32f)
            {
                int mask = (band * 7 + 3) % 11;
                return mask < 4 ? .03f : (mask < 7 ? .28f : 1f);
            }
            if (seconds < .5f)
            {
                float arrival = .32f + ((band * 5) % WarningBandCount) * .012f;
                return Mathf.Clamp01((seconds - arrival) / .025f);
            }
            if (seconds < .65f)
            {
                int mask = (band * 3 + 1) % 9;
                return mask < 3 ? .05f : (mask == 4 ? .32f : 1f);
            }
            if (seconds < .75f) return Mathf.Clamp01((seconds - .65f) / .055f);
            if (seconds < 4.9f) return 1f;

            float collapse = Mathf.Clamp01((seconds - _bandCollapseStart[band]) / _bandCollapseDuration[band]);
            return 1f - collapse;
        }

        private float SubtitleBandAlpha(float seconds, int band)
        {
            float arrival = .14f + band * .018f;
            float reveal = Mathf.Clamp01((seconds - arrival) / .035f);
            if (seconds >= .75f && seconds <= 2f) return 1f;
            float ending = 1f - Smooth01((seconds - 5.08f - band * .13f) / .56f);
            return reveal * ending;
        }

        private float WarningBandOffset(float seconds, int band)
        {
            if (IsReadableWindow(seconds)) return 0f;
            if (seconds >= 4.9f)
            {
                float collapse = Smooth01((seconds - _bandCollapseStart[band]) / _bandCollapseDuration[band]);
                return _bandCollapseDirection[band] * collapse * _screenSize.x * (.18f + .025f * (band % 4));
            }

            float tear = LargeTear(seconds, 2.05f, 2.18f, band, 2) +
                LargeTear(seconds, 3.65f, 3.79f, band, 7);
            int eventIndex = Mathf.FloorToInt(seconds * 2.65f);
            float eventTime = seconds * 2.65f - eventIndex;
            int selected = PositiveHash(eventIndex * 17 + 5) % WarningBandCount;
            float micro = 0f;
            if (eventTime < .09f && (band == selected || band == (selected + 1) % WarningBandCount))
            {
                float pulse = Mathf.Sin(eventTime / .09f * Mathf.PI);
                float sign = (PositiveHash(eventIndex * 31) & 1) == 0 ? -1f : 1f;
                micro = sign * pulse * _screenSize.x * (.006f + .005f * (band & 1));
            }
            return tear + micro;
        }

        private float SubtitleBandOffset(float seconds, int band)
        {
            if (seconds >= .75f && seconds <= 2f) return 0f;
            float tear = LargeTear(seconds, 2.05f, 2.18f, band, 4) * .72f +
                LargeTear(seconds, 3.65f, 3.79f, band, 9) * .72f;
            if (seconds < 5.08f) return tear;
            float collapse = Smooth01((seconds - 5.08f - band * .13f) / .56f);
            float direction = (band & 1) == 0 ? -1f : 1f;
            return tear + direction * collapse * _screenSize.x * (.08f + band * .025f);
        }

        private float WarningBandWidthScale(float seconds, int band)
        {
            if (IsReadableWindow(seconds)) return 1f;
            if (seconds >= 4.9f)
            {
                float collapse = Smooth01((seconds - _bandCollapseStart[band]) / _bandCollapseDuration[band]);
                return Mathf.Max(.025f, 1f - collapse * (.62f + .08f * (band % 4)));
            }
            int eventIndex = Mathf.FloorToInt(seconds * 2.65f);
            float eventTime = seconds * 2.65f - eventIndex;
            if (eventTime < .09f && band == PositiveHash(eventIndex * 17 + 5) % WarningBandCount)
                return 1f + Mathf.Sin(eventTime / .09f * Mathf.PI) * .06f;
            return 1f;
        }

        private float WarningBandStep(float seconds, int band)
        {
            if (IsReadableWindow(seconds)) return 0f;
            float first = LargeTearPulse(seconds, 2.05f, 2.18f);
            float second = LargeTearPulse(seconds, 3.65f, 3.79f);
            return (first * ((band % 4) - 1.5f) + second * (((band + 2) % 5) - 2f)) * .008f;
        }

        private float LargeTear(float seconds, float start, float end, int band, int phase)
        {
            float pulse = LargeTearPulse(seconds, start, end);
            if (pulse <= 0f || (band + phase) % 3 == 0) return 0f;
            float direction = ((band + phase) & 1) == 0 ? -1f : 1f;
            float amount = .035f + .085f * ((band * 7 + phase) % 10) / 9f;
            return direction * pulse * _screenSize.x * amount;
        }

        private static float LargeTearPulse(float seconds, float start, float end)
        {
            if (seconds <= start || seconds >= end) return 0f;
            return Mathf.Sin((seconds - start) / (end - start) * Mathf.PI);
        }

        private static bool IsReadableWindow(float seconds) => seconds >= .75f && seconds <= 2f;

        private void UpdateGlyphMesh(float seconds)
        {
            if (_glyphMesh == null) return;

            for (int i = 0; i < GlyphCount; i++)
            {
                GlyphSpec glyph = _glyphs[i];
                float disappear = glyph.Kind == GlyphKind.Near ? 5.5f + glyph.X * .45f :
                    5.95f + glyph.X * .6f;
                float ending = 1f - Smooth01((seconds - disappear) / (.15f + glyph.Y * .28f));
                float depth = Mathf.Min(glyph.Depth, _maximumFrontDepth);
                Vector2 center = GlyphCenter(glyph, seconds, depth);
                float reveal = Mathf.Clamp01((seconds - .06f - glyph.Delay) / .035f);
                float edgeFade = GlyphEdgeFade(glyph, center.y);
                float alpha = glyph.BaseColor.a * reveal * ending * edgeFade;
                float size = glyph.Size;
                if (glyph.Kind == GlyphKind.Near)
                    size *= 1f + Mathf.Sin(seconds * .22f + glyph.Phase) * .015f;
                SetQuadVertices(_glyphVertices, i * 4, center.x, center.y, -depth, size, size * 1.34f);
                Color color = glyph.BaseColor;
                color.a = alpha;
                SetQuadColor(_glyphColors, i * 4, color);
            }
            _glyphMesh.SetVertices(_glyphVertices);
            _glyphMesh.SetColors(_glyphColors);
        }

        private Vector2 GlyphCenter(GlyphSpec glyph, float seconds, float depth)
        {
            if (glyph.Kind == GlyphKind.Near)
            {
                float distance = Mathf.Max(.15f, _audienceDistance - depth);
                float halfWidth = distance;
                float halfHeight = Mathf.Tan(29f * Mathf.Deg2Rad) * distance;
                Vector2 position = NearPositions[glyph.Bundle];
                float x = halfWidth * position.x;
                float y = halfHeight * position.y;
                x += Mathf.Sin(seconds * .12f + glyph.Phase) * .025f;
                y += Mathf.Sin(seconds * .09f + glyph.Phase * .7f) * .018f;
                return new Vector2(x, y);
            }

            Vector2 region = ClusterPosition(glyph.Cluster, glyph.X, glyph.Y);
            float xPosition = region.x * _screenSize.x;
            float yPosition = region.y * _screenSize.y;
            if (glyph.Kind == GlyphKind.Middle)
            {
                float bundleX = BundleX(glyph.Bundle) * _screenSize.x;
                xPosition = bundleX + (glyph.X - .5f) * .12f;
                float range = _screenSize.y * 1.38f;
                float phaseY = Mathf.Repeat((glyph.Y * range) - seconds * glyph.Speed + range, range);
                yPosition = phaseY - range * .58f;
            }
            else if (glyph.Kind == GlyphKind.Far)
            {
                yPosition -= seconds * glyph.Speed * .34f;
                float range = _screenSize.y * 1.3f;
                yPosition = Mathf.Repeat(yPosition + range * .62f, range) - range * .62f;
            }
            else
            {
                xPosition += Mathf.Sin(seconds * (.31f + glyph.Digit * .07f) + glyph.Phase) *
                    _screenSize.x * .24f;
                yPosition += Mathf.Sin(seconds * .15f + glyph.Phase * .8f) * .014f;
            }
            return new Vector2(xPosition, yPosition);
        }

        private float GlyphEdgeFade(GlyphSpec glyph, float y)
        {
            if (glyph.Kind == GlyphKind.Near || glyph.Kind == GlyphKind.Fragment) return 1f;
            float halfRange = _screenSize.y * .69f;
            float distance = halfRange - Mathf.Abs(y);
            return Mathf.Clamp01(distance / Mathf.Max(.025f, _screenSize.y * .12f));
        }

        private void UpdateInterferenceMesh(float seconds)
        {
            if (_interferenceMesh == null) return;

            float reveal = Mathf.Clamp01((seconds - .06f) / .05f);
            for (int i = 0; i < InterferenceStripCount; i++)
            {
                StripSpec strip = _strips[i];
                float ending = 1f - Smooth01((seconds - 6.05f - strip.Phase * .065f) / .34f);
                float pulse = .58f + .42f * StepPulse(seconds, strip.Phase);
                float x = strip.X * _screenSize.x;
                float y = strip.Y * _screenSize.y + Mathf.Sin(seconds * .18f + strip.Phase) * .012f;
                float depth = Mathf.Min(strip.Depth, _maximumFrontDepth);
                SetQuadVertices(_interferenceVertices, i * 4, x, y, -depth,
                    strip.Width * _screenSize.x, strip.Height);
                SetQuadColor(_interferenceColors, i * 4,
                    new Color(1f, .06f, .085f, strip.Alpha * reveal * ending * pulse));
            }
            _interferenceMesh.SetVertices(_interferenceVertices);
            _interferenceMesh.SetColors(_interferenceColors);
        }

        private static float StepPulse(float seconds, float phase)
        {
            float cycle = Mathf.Repeat(seconds * .83f + phase, 1f);
            if (cycle < .08f) return 1f;
            if (cycle < .18f) return .16f;
            return .48f;
        }

        private void MeasureAudience(Transform anchor)
        {
            _audienceDistance = 3f;
            if (Camera.main != null)
                _audienceDistance = Vector3.Distance(Camera.main.transform.position, anchor.position);
            _maximumFrontDepth = Mathf.Clamp(_audienceDistance - 1.2f, 0f, 1.5f);
        }

        private void AttachScreenRoot(Transform anchor)
        {
            if (_screenRoot == null) return;
            _screenRoot.SetParent(anchor, false);
            _screenRoot.localPosition = new Vector3(0f, 0f, -.06f * SafeInverse(anchor.lossyScale.z));
            _screenRoot.localRotation = Quaternion.identity;
            SetWorldUnitLocalScale(_screenRoot);
        }

        private void PlaceSpatialRoot(Transform anchor)
        {
            if (_spatialRoot == null) return;
            _spatialRoot.SetParent(null, false);
            if (gameObject.scene.IsValid() && _spatialRoot.gameObject.scene != gameObject.scene)
                SceneManager.MoveGameObjectToScene(_spatialRoot.gameObject, gameObject.scene);
            _spatialRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
            _spatialRoot.localScale = Vector3.one;
        }

        private static Mesh CreateMesh(string name, Vector3[] vertices, Vector2[] uv0, Vector2[]? uv1,
            Color[] colors, int[] triangles)
        {
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv0);
            if (uv1 != null) mesh.SetUVs(1, uv1);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(20f, 12f, 6f));
            return mesh;
        }

        private static void SetQuadVertices(Vector3[] vertices, int index, float x, float y, float z,
            float width, float height)
        {
            float halfWidth = width * .5f;
            float halfHeight = height * .5f;
            vertices[index] = new Vector3(x - halfWidth, y - halfHeight, z);
            vertices[index + 1] = new Vector3(x + halfWidth, y - halfHeight, z);
            vertices[index + 2] = new Vector3(x - halfWidth, y + halfHeight, z);
            vertices[index + 3] = new Vector3(x + halfWidth, y + halfHeight, z);
        }

        private static void SetParallelogramVertices(Vector3[] vertices, int index, float x, float y,
            float z, float width, float height, float shear)
        {
            float halfWidth = width * .5f;
            float halfHeight = height * .5f;
            float halfShear = shear * .5f;
            vertices[index] = new Vector3(x - halfWidth - halfShear, y - halfHeight, z);
            vertices[index + 1] = new Vector3(x + halfWidth - halfShear, y - halfHeight, z);
            vertices[index + 2] = new Vector3(x - halfWidth + halfShear, y + halfHeight, z);
            vertices[index + 3] = new Vector3(x + halfWidth + halfShear, y + halfHeight, z);
        }

        private static void SetQuadUvs(Vector2[] uvs, int index, float u0, float v0, float u1, float v1)
        {
            uvs[index] = new Vector2(u0, v0);
            uvs[index + 1] = new Vector2(u1, v0);
            uvs[index + 2] = new Vector2(u0, v1);
            uvs[index + 3] = new Vector2(u1, v1);
        }

        private static void SetQuadTriangles(int[] triangles, int quad, int vertex)
        {
            int index = quad * 6;
            triangles[index] = vertex;
            triangles[index + 1] = vertex + 2;
            triangles[index + 2] = vertex + 1;
            triangles[index + 3] = vertex + 2;
            triangles[index + 4] = vertex + 3;
            triangles[index + 5] = vertex + 1;
        }

        private static void SetQuadColor(Color[] colors, int index, Color color)
        {
            colors[index] = color;
            colors[index + 1] = color;
            colors[index + 2] = color;
            colors[index + 3] = color;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        private static Vector2 ClusterPosition(int cluster, float x, float y)
        {
            return cluster switch
            {
                0 => new Vector2(Mathf.Lerp(-.92f, -.18f, x), Mathf.Lerp(-.12f, .58f, y)),
                1 => new Vector2(Mathf.Lerp(.18f, .96f, x), Mathf.Lerp(-.62f, .16f, y)),
                _ => new Vector2(Mathf.Lerp(-.58f, .62f, x), Mathf.Lerp(.38f, .78f, y)),
            };
        }

        private static float BundleX(int bundle) =>
            BundlePositions[Mathf.Clamp(bundle, 0, BundlePositions.Length - 1)];

        private static int ChooseCluster(System.Random random)
        {
            int value = random.Next(100);
            if (value < 46) return 0;
            return value < 87 ? 1 : 2;
        }

        private static Color PickGlyphColor(System.Random random, GlyphKind kind)
        {
            float alpha = kind switch
            {
                GlyphKind.Near => Range(random, .12f, .20f),
                GlyphKind.Far => Range(random, .12f, .30f),
                GlyphKind.Middle => Range(random, .22f, .52f),
                _ => Range(random, .16f, .38f),
            };
            int choice = random.Next(10);
            if (choice < 4) return new Color(.93f, .055f, .07f, alpha);
            if (choice < 8) return new Color(.34f, .014f, .022f, alpha);
            return new Color(1f, .38f, .3f, alpha);
        }

        private static float Range(System.Random random, float min, float max) =>
            Mathf.Lerp(min, max, (float)random.NextDouble());

        private static int PositiveHash(int value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x45d9f3b;
                value ^= value >> 16;
                return value & int.MaxValue;
            }
        }

        private static void SetWorldUnitLocalScale(Transform root)
        {
            Transform? parent = root.parent;
            if (parent == null)
            {
                root.localScale = Vector3.one;
                return;
            }
            Vector3 scale = parent.lossyScale;
            root.localScale = new Vector3(SafeInverse(scale.x), SafeInverse(scale.y), SafeInverse(scale.z));
        }

        private static float SafeInverse(float value) => Mathf.Abs(value) > .0001f ? 1f / value : 1f;

        private static float Smooth01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
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
            DestroySafe(_warningMaterial);
            DestroySafe(_subtitleMaterial);
            DestroySafe(_symbolMaterial);
            DestroySafe(_decorationMaterial);
            DestroySafe(_glyphMaterial);
            DestroySafe(_interferenceMaterial);
            DestroySafe(_warningMesh);
            DestroySafe(_subtitleMesh);
            DestroySafe(_symbolMesh);
            DestroySafe(_decorationMesh);
            DestroySafe(_glyphMesh);
            DestroySafe(_interferenceMesh);
            _screenRoot = null;
            _spatialRoot = null;
            _warningTransform = null;
            _symbolTransform = null;
            _warningMaterial = null;
            _subtitleMaterial = null;
            _symbolMaterial = null;
            _decorationMaterial = null;
            _glyphMaterial = null;
            _interferenceMaterial = null;
            _warningMesh = null;
            _subtitleMesh = null;
            _symbolMesh = null;
            _decorationMesh = null;
            _glyphMesh = null;
            _interferenceMesh = null;
        }

        private static void DestroySafe(UnityEngine.Object? value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        private enum GlyphKind
        {
            Far,
            Middle,
            Near,
            Fragment,
        }

        private struct GlyphSpec
        {
            public GlyphKind Kind;
            public int Cluster;
            public int Bundle;
            public int Digit;
            public float Blur;
            public float Size;
            public float Depth;
            public float X;
            public float Y;
            public float Phase;
            public float Speed;
            public float Delay;
            public Color BaseColor;
        }

        private struct StripSpec
        {
            public float X;
            public float Y;
            public float Depth;
            public float Width;
            public float Height;
            public float UMin;
            public float UMax;
            public float VMin;
            public float VMax;
            public float Phase;
            public float Alpha;
        }
    }
}
