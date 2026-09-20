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
        private const int QuadVertexCount = 4;
        private const int RandomSeed = 0x5A17C0DE;
        private const float TakeoverLoopStartSec = 1.6f;
        private const float TakeoverLoopEndSec = 3.8f;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int ReadWorldToLocalId = Shader.PropertyToID("_ReadWorldToLocal");
        private static readonly int ReadRect0Id = Shader.PropertyToID("_ReadRect0");
        private static readonly int ReadRect1Id = Shader.PropertyToID("_ReadRect1");
        private static readonly int ReadRect2Id = Shader.PropertyToID("_ReadRect2");
        private static readonly int ReadRect3Id = Shader.PropertyToID("_ReadRect3");
        private static readonly int ReadMaskStrengthId = Shader.PropertyToID("_ReadMaskStrength");
        private static readonly Color AlertRed = new Color(1f, .06f, .085f, 1f);
        private static readonly Color SubtitleRed = new Color(1f, .13f, .14f, 1f);
        private static readonly Color ContextTint = new Color(.83f, .70f, .64f, 1f);
        private static readonly Color AttemptTint = new Color(1f, .47f, .30f, 1f);
        private static readonly Color FailureTint = new Color(1f, .18f, .19f, 1f);

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
        [SerializeField] private Texture2D? contextGraphic;
        [SerializeField] private Texture2D? attemptGraphic;
        [SerializeField] private Texture2D? failedGraphic;
        [SerializeField] private bool autoPlay;

        private Transform? _screenRoot;
        private Transform? _spatialRoot;
        private Transform? _warningTransform;
        private Transform? _symbolTransform;
        private Mesh? _warningMesh;
        private Mesh? _subtitleMesh;
        private Mesh? _symbolMesh;
        private Mesh? _decorationMesh;
        private Mesh? _contextMesh;
        private Mesh? _statusMesh;
        private Mesh? _glyphMesh;
        private Mesh? _interferenceMesh;
        private Material? _warningMaterial;
        private Material? _subtitleMaterial;
        private Material? _symbolMaterial;
        private Material? _decorationMaterial;
        private Material? _contextMaterial;
        private Material? _statusMaterial;
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
        private readonly Vector3[] _contextVertices = new Vector3[QuadVertexCount];
        private readonly Vector2[] _contextUvs = new Vector2[QuadVertexCount];
        private readonly Color[] _contextColors = new Color[QuadVertexCount];
        private readonly int[] _contextTriangles = new int[6];
        private readonly Vector3[] _statusVertices = new Vector3[QuadVertexCount];
        private readonly Vector2[] _statusUvs = new Vector2[QuadVertexCount];
        private readonly Color[] _statusColors = new Color[QuadVertexCount];
        private readonly int[] _statusTriangles = new int[6];
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

        private Vector2 _screenSize;
        private float _warningWidth;
        private float _warningHeight;
        private float _subtitleWidth;
        private float _subtitleHeight;
        private float _symbolSize;
        private float _contextWidth;
        private float _contextHeight;
        private float _statusWidth;
        private float _statusHeight;
        private float _maximumFrontDepth = 1.5f;
        private float _audienceDistance = 3f;
        private bool _takeoverMode;
        private bool _takeoverBlockFailed;
        private float _takeoverOpacity = 1f;
        private float _takeoverReadFocus;

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
        public int ContextVertexCountDiagnostic => _contextMesh != null ? _contextMesh.vertexCount : 0;
        public int StatusVertexCountDiagnostic => _statusMesh != null ? _statusMesh.vertexCount : 0;
        public Transform? ScreenAnchor { get; private set; }

        public void Configure(Material fxMaterial, Texture2D warningGraphic, Texture2D subtitleGraphic,
            Texture2D symbolGraphic, Texture2D interference, Texture2D? contextGraphic = null,
            Texture2D? attemptGraphic = null, Texture2D? failedGraphic = null)
        {
            bool changed = this.fxMaterial != fxMaterial || this.warningGraphic != warningGraphic ||
                this.subtitleGraphic != subtitleGraphic || this.symbolGraphic != symbolGraphic ||
                this.interference != interference || this.contextGraphic != contextGraphic ||
                this.attemptGraphic != attemptGraphic || this.failedGraphic != failedGraphic;
            this.fxMaterial = fxMaterial;
            this.warningGraphic = warningGraphic;
            this.subtitleGraphic = subtitleGraphic;
            this.symbolGraphic = symbolGraphic;
            this.interference = interference;
            this.contextGraphic = contextGraphic;
            this.attemptGraphic = attemptGraphic;
            this.failedGraphic = failedGraphic;
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
            if (_spatialRoot != null) _spatialRoot.SetPositionAndRotation(spatialPosition, spatialRotation);
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
            ResetTakeoverSample();
            Render(0f);
        }

        public void Stop()
        {
            IsPlaying = false;
            ResetTakeoverSample();
            SetRootsVisible(false);
        }

        /// <summary>指定時刻の見た目だけを適用する。再生状態と経過時刻は変更しない。</summary>
        public void Sample(float seconds)
        {
            ResetTakeoverSample();
            EnsureBuilt();
            if (_screenSize.x > 0f && _screenSize.y > 0f) Layout(_screenSize);
            Render(seconds);
        }

        /// <summary>
        /// Comms の乗っ取り時計で見た目だけを適用する。7 秒版の退場は使わず、警告と空間要素を
        /// <paramref name="opacity"/> で同時に消灯する。<paramref name="readFocus"/> が上がるほど
        /// 通信面を読むため周囲を抑える。再生状態と <see cref="Elapsed"/> は変更しない。
        /// </summary>
        public void SampleTakeover(float elapsed, bool blockFailed, float opacity, float readFocus = 0f)
        {
            _takeoverMode = true;
            _takeoverBlockFailed = blockFailed;
            _takeoverOpacity = Mathf.Clamp01(opacity);
            _takeoverReadFocus = Mathf.Clamp01(readFocus);
            EnsureBuilt();
            if (_screenSize.x > 0f && _screenSize.y > 0f) Layout(_screenSize);
            Render(Mathf.Max(0f, elapsed));
        }

        private void Start()
        {
            if (Application.isPlaying && autoPlay) Play(transform, new Vector2(1.6f, .9f));
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
            _contextMesh = CreateMesh("Unauthorized Access Context", _contextVertices,
                _contextUvs, null, _contextColors, _contextTriangles);
            _statusMesh = CreateMesh("Unauthorized Access Status", _statusVertices,
                _statusUvs, null, _statusColors, _statusTriangles);
            _glyphMesh = CreateMesh("Unauthorized Access Glyphs", _glyphVertices,
                _glyphUvs, _glyphData, _glyphColors, _glyphTriangles);
            _interferenceMesh = CreateMesh("Unauthorized Access Interference", _interferenceVertices,
                _interferenceUvs, null, _interferenceColors, _interferenceTriangles);

            _symbolTransform = CreateMeshObject("WarningSymbol", _screenRoot!, _symbolMesh, _symbolMaterial);
            CreateMeshObject("WarningDecorations", _screenRoot!, _decorationMesh, _decorationMaterial);
            _warningTransform = CreateMeshObject("WarningBands", _screenRoot!, _warningMesh, _warningMaterial);
            _warningTransform.localRotation = Quaternion.Euler(0f, 0f, -.6f);
            CreateMeshObject("SubtitleBands", _screenRoot!, _subtitleMesh, _subtitleMaterial);
            CreateMeshObject("Context", _screenRoot!, _contextMesh, _contextMaterial);
            CreateMeshObject("Status", _screenRoot!, _statusMesh, _statusMaterial);
            CreateMeshObject("BinaryField", _spatialRoot!, _glyphMesh, _glyphMaterial);
            CreateMeshObject("InterferenceLight", _spatialRoot!, _interferenceMesh, _interferenceMaterial);
            SetRootsVisible(false);
        }

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
            _contextMaterial = CreateRuntimeMaterial("Unauthorized Access Context", 2f,
                contextGraphic != null ? contextGraphic : Texture2D.whiteTexture, 1f, 3452);
            _statusMaterial = CreateRuntimeMaterial("Unauthorized Access Status", 2f,
                attemptGraphic != null ? attemptGraphic : Texture2D.whiteTexture, 1f, 3453);
            _glyphMaterial = CreateRuntimeMaterial("Unauthorized Access Glyph", 1f,
                Texture2D.whiteTexture, 1.18f);
            _interferenceMaterial = CreateRuntimeMaterial("Unauthorized Access Interference", 0f,
                interference != null ? interference : Texture2D.whiteTexture, .82f);
            SetTextReadMaskStrength(0f);
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

        private void SetTextReadMaskStrength(float strength)
        {
            SetReadMaskStrength(_warningMaterial, strength);
            SetReadMaskStrength(_subtitleMaterial, strength);
            SetReadMaskStrength(_contextMaterial, strength);
            SetReadMaskStrength(_statusMaterial, strength);
        }

        private static void SetReadMaskStrength(Material? material, float strength)
        {
            if (material != null) material.SetFloat(ReadMaskStrengthId, strength);
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

        private void InitializeScreenPartData()
        {
            for (int i = 0; i < WarningBandCount; i++)
            {
                int vertex = i * 4;
                SetQuadUvs(_warningUvs, vertex, 0f, WarningBandEdges[i], 1f, WarningBandEdges[i + 1]);
                SetQuadTriangles(_warningTriangles, i, vertex);
            }
            for (int i = 0; i < SubtitleBandCount; i++)
            {
                int vertex = i * 4;
                SetQuadUvs(_subtitleUvs, vertex, 0f, SubtitleBandEdges[i], 1f, SubtitleBandEdges[i + 1]);
                SetQuadTriangles(_subtitleTriangles, i, vertex);
            }
            SetQuadUvs(_symbolUvs, 0, 0f, 0f, 1f, 1f);
            SetQuadTriangles(_symbolTriangles, 0, 0);
            SetQuadUvs(_contextUvs, 0, 0f, 0f, 1f, 1f);
            SetQuadTriangles(_contextTriangles, 0, 0);
            SetQuadUvs(_statusUvs, 0, 0f, 0f, 1f, 1f);
            SetQuadTriangles(_statusTriangles, 0, 0);
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
                GlyphKind kind = i < FarGlyphCount ? GlyphKind.Far :
                    i < FarGlyphCount + MiddleGlyphCount ? GlyphKind.Middle :
                    i < FarGlyphCount + MiddleGlyphCount + NearGlyphCount ? GlyphKind.Near : GlyphKind.Fragment;
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
                    Kind = kind, Cluster = cluster, Bundle = bundle, Digit = random.Next(2),
                    Blur = kind == GlyphKind.Near ? Range(random, .5f, .85f) : Range(random, 0f, .22f),
                    Size = size, Depth = depth, X = Range(random, 0f, 1f), Y = Range(random, 0f, 1f),
                    Phase = Range(random, 0f, 12f),
                    Speed = kind == GlyphKind.Near ? Range(random, .006f, .018f) : Range(random, .1f, .38f),
                    Delay = i < 5 ? 0f : Range(random, 0f, .48f), BaseColor = PickGlyphColor(random, kind),
                };
                int vertex = i * 4;
                SetQuadUvs(_glyphUvs, vertex, 0f, 0f, 1f, 1f);
                Vector2 data = new Vector2(_glyphs[i].Digit, _glyphs[i].Blur);
                for (int j = 0; j < 4; j++) _glyphData[vertex + j] = data;
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
                    X = Range(random, -.82f, .78f), Y = Range(random, -.48f, .52f),
                    Depth = Range(random, .24f, 1.18f), Width = Range(random, .35f, 1.1f),
                    Height = Range(random, .06f, .24f), UMin = Range(random, 0f, .55f),
                    UMax = Range(random, .58f, 1f), VMin = vMin, VMax = vMax,
                    Phase = Range(random, 0f, 8f), Alpha = Range(random, .10f, .30f),
                };
                int vertex = i * 4;
                SetQuadUvs(_interferenceUvs, vertex, _strips[i].UMin, vMin, _strips[i].UMax, vMax);
                SetQuadTriangles(_interferenceTriangles, i, vertex);
            }
        }

        private void Layout(Vector2 size)
        {
            if (_screenRoot != null) SetWorldUnitLocalScale(_screenRoot);
            _warningWidth = size.x * 1.06f;
            _warningHeight = _warningWidth / TextureAspect(warningGraphic, 1406f / 257f);
            _subtitleWidth = size.x * .76f;
            _subtitleHeight = _subtitleWidth / TextureAspect(subtitleGraphic, 805f / 107f);
            _symbolSize = size.y * 1.16f;
            _contextWidth = size.x * .70f;
            _contextHeight = _contextWidth / TextureAspect(contextGraphic, 5.4f);
            _statusHeight = size.y * .105f;
            _statusWidth = _statusHeight * TextureAspect(attemptGraphic != null ? attemptGraphic : failedGraphic, 4.2f);
        }

        private static float TextureAspect(Texture2D? texture, float fallback) =>
            texture != null && texture.height > 0 ? texture.width / (float)texture.height : fallback;

        private void Render(float seconds)
        {
            if (seconds < .06f || (!_takeoverMode && seconds >= Duration) ||
                (_takeoverMode && _takeoverOpacity <= 0f))
            {
                SetRootsVisible(false);
                return;
            }
            SetRootsVisible(true);
            if (_screenRoot != null) SetWorldUnitLocalScale(_screenRoot);
            float visualSeconds = TakeoverVisualSeconds(seconds);
            UpdateSymbolMesh(visualSeconds);
            UpdateDecorationMesh(visualSeconds);
            UpdateWarningMesh(visualSeconds);
            UpdateSubtitleMesh(visualSeconds);
            UpdateContextMesh(visualSeconds);
            UpdateStatusMesh(visualSeconds);
            UpdateGlyphMesh(visualSeconds);
            UpdateInterferenceMesh(visualSeconds);
            UpdateReadMasks(visualSeconds);
            if (_takeoverMode) ApplyTakeoverOpacity(_takeoverOpacity);
        }

        private void UpdateWarningMesh(float seconds)
        {
            if (_warningMesh == null) return;
            for (int i = 0; i < WarningBandCount; i++)
            {
                float first = LargeTearPulse(seconds, 2.05f, 2.18f);
                float second = LargeTearPulse(seconds, 3.65f, 3.79f);
                float yStep = (first * ((i % 4) - 1.5f) + second * (((i + 2) % 5) - 2f)) * .008f;
                float y0 = _warningHeight * (WarningBandEdges[i] - .5f) + yStep;
                float y1 = _warningHeight * (WarningBandEdges[i + 1] - .5f) + yStep;
                float x = .10f * _screenSize.x + WarningBandOffset(seconds, i);
                float y = .16f * _screenSize.y + (y0 + y1) * .5f;
                float width = _warningWidth * Mathf.Max(.025f, 1f - WarningCollapse(seconds, i) * (.62f + .08f * (i % 4)));
                SetQuadVertices(_warningVertices, i * 4, x, y, -.004f, width, y1 - y0);
                SetQuadColor(_warningColors, i * 4, WithAlpha(AlertRed, WarningBandAlpha(seconds, i)));
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
                SetQuadVertices(_subtitleVertices, i * 4,
                    .12f * _screenSize.x + SubtitleBandOffset(seconds, i),
                    -.14f * _screenSize.y + (y0 + y1) * .5f, -.006f, _subtitleWidth, y1 - y0);
                SetQuadColor(_subtitleColors, i * 4, WithAlpha(SubtitleRed, SubtitleBandAlpha(seconds, i)));
            }
            _subtitleMesh.SetVertices(_subtitleVertices);
            _subtitleMesh.SetColors(_subtitleColors);
        }

        private void UpdateSymbolMesh(float seconds)
        {
            if (_symbolMesh == null) return;
            float reveal = Smooth01((seconds - .06f) / .035f);
            float ending = _takeoverMode ? 1f : 1f - Smooth01((seconds - 4.95f) / .5f);
            float alpha = (.38f + Mathf.Sin(seconds * 1.7f) * .025f) * reveal * ending;
            SetQuadVertices(_symbolVertices, 0, -.53f * _screenSize.x, .10f * _screenSize.y,
                .006f, _symbolSize, _symbolSize);
            SetQuadColor(_symbolColors, 0, WithAlpha(AlertRed, symbolGraphic != null ? alpha : 0f));
            _symbolMesh.SetVertices(_symbolVertices);
            _symbolMesh.SetColors(_symbolColors);
        }

        private void UpdateDecorationMesh(float seconds)
        {
            if (_decorationMesh == null) return;
            float reveal = Smooth01((seconds - .075f) / .045f);
            float ending = _takeoverMode ? 1f : 1f - Smooth01((seconds - 4.95f) / .5f);
            float symbolX = -.53f * _screenSize.x;
            float symbolY = .10f * _screenSize.y;
            SetQuadVertices(_decorationVertices, 0, symbolX, symbolY + _symbolSize * .035f,
                .005f, _symbolSize * .055f, _symbolSize * .30f);
            SetQuadVertices(_decorationVertices, 4, symbolX, symbolY - _symbolSize * .185f,
                .005f, _symbolSize * .068f, _symbolSize * .068f);
            Color markColor = WithAlpha(AlertRed, .78f * reveal * ending);
            SetQuadColor(_decorationColors, 0, markColor);
            SetQuadColor(_decorationColors, 4, markColor);
            SetHazardGroup(2, .37f * _screenSize.x, .43f * _screenSize.y,
                .25f * _screenSize.x, .055f * _screenSize.y, seconds, 0);
            SetHazardGroup(8, -.46f * _screenSize.x, -.43f * _screenSize.y,
                .18f * _screenSize.x, .065f * _screenSize.y, seconds, 1);
            _decorationMesh.SetVertices(_decorationVertices);
            _decorationMesh.SetColors(_decorationColors);
        }

        private void SetHazardGroup(int firstQuad, float centerX, float centerY, float groupWidth,
            float height, float seconds, int group)
        {
            const int stripeCount = 6;
            float reveal = Smooth01((seconds - .09f - group * .012f) / .055f);
            float ending = _takeoverMode ? 1f : 1f - Smooth01((seconds - 5.92f) / .18f);
            float firstTear = LargeTearPulse(seconds, 2.05f, 2.18f);
            float broken = Smooth01((seconds - 3.65f) / .35f);
            float stripeWidth = groupWidth * .08f;
            float spacing = groupWidth / (stripeCount - 1f);
            float shear = height * .48f;
            for (int i = 0; i < stripeCount; i++)
            {
                bool middle = i == 2 || i == 3;
                float jump = middle ? broken * groupWidth * (i == 2 ? -.18f : .18f) : 0f;
                float x = centerX - groupWidth * .5f + spacing * i + jump;
                float alpha = .58f + firstTear * .30f;
                if (middle) alpha *= 1f - broken * .82f;
                int vertex = (firstQuad + i) * 4;
                SetParallelogramVertices(_decorationVertices, vertex, x, centerY, .003f,
                    stripeWidth, height, shear);
                SetQuadColor(_decorationColors, vertex, WithAlpha(AlertRed, alpha * reveal * ending));
            }
        }

        private void UpdateContextMesh(float seconds)
        {
            if (_contextMesh == null) return;
            float reveal = Smooth01((seconds - .65f) / .20f);
            float ending = _takeoverMode ? 1f : 1f - Smooth01((seconds - 5.6f) / .4f);
            float alpha = contextGraphic != null ? reveal * ending : 0f;
            SetQuadVertices(_contextVertices, 0, .15f * _screenSize.x, -.34f * _screenSize.y,
                -.008f, _contextWidth, _contextHeight);
            SetQuadColor(_contextColors, 0, WithAlpha(ContextTint, alpha));
            _contextMesh.SetVertices(_contextVertices);
            _contextMesh.SetColors(_contextColors);
        }

        private void UpdateStatusMesh(float seconds)
        {
            if (_statusMesh == null) return;
            bool failed = _takeoverMode ? _takeoverBlockFailed : seconds >= 3.82f;
            Texture2D? texture = failed ? failedGraphic : attemptGraphic;
            if (_statusMaterial != null)
                _statusMaterial.SetTexture(MainTexId, texture != null ? texture : Texture2D.whiteTexture);
            float reveal = Smooth01((seconds - 1.35f) / .20f);
            float ending = _takeoverMode ? 1f : 1f - Mathf.Clamp01((seconds - 6.76f) / .08f);
            float alpha = texture != null ? reveal * ending : 0f;
            float aspect = TextureAspect(texture, 4.2f);
            _statusWidth = _statusHeight * aspect;
            SetQuadVertices(_statusVertices, 0, .21f * _screenSize.x, -.50f * _screenSize.y,
                -.01f, _statusWidth, _statusHeight);
            SetQuadColor(_statusColors, 0, WithAlpha(failed ? FailureTint : AttemptTint, alpha));
            _statusMesh.SetVertices(_statusVertices);
            _statusMesh.SetColors(_statusColors);
        }

        private float WarningBandAlpha(float seconds, int band)
        {
            float arrival = .095f + ((band * 5) % WarningBandCount) * .012f;
            float reveal = Mathf.Clamp01((seconds - arrival) / .035f);
            return reveal * (1f - WarningCollapse(seconds, band));
        }

        private float WarningCollapse(float seconds, int band)
        {
            if (_takeoverMode) return 0f;
            float start = 4.9f + .65f * ((band * 7) % 11) / 10f;
            float duration = .19f + .16f * ((band * 5) % 7) / 6f;
            return Smooth01((seconds - start) / duration);
        }

        private float SubtitleBandAlpha(float seconds, int band)
        {
            float arrival = .32f + band * .075f;
            float reveal = Mathf.Clamp01((seconds - arrival) / .08f);
            float ending = _takeoverMode ? 1f : 1f - Smooth01((seconds - 5.75f) / .35f);
            return reveal * ending;
        }

        private float WarningBandOffset(float seconds, int band)
        {
            float direction = (band & 1) == 0 ? -1f : 1f;
            return LargeTear(seconds, 2.05f, 2.18f, band, 2) +
                LargeTear(seconds, 3.65f, 3.79f, band, 7) +
                direction * WarningCollapse(seconds, band) * _screenSize.x * (.18f + .025f * (band % 4));
        }

        private float LargeTear(float seconds, float start, float end, int band, int phase)
        {
            float pulse = LargeTearPulse(seconds, start, end);
            if (pulse <= 0f || (band + phase) % 3 == 0) return 0f;
            float direction = ((band + phase) & 1) == 0 ? -1f : 1f;
            float amount = .035f + .085f * ((band * 7 + phase) % 10) / 9f;
            return direction * pulse * _screenSize.x * amount;
        }

        private float SubtitleBandOffset(float seconds, int band)
        {
            float first = LargeTearPulse(seconds, 2.05f, 2.18f);
            float second = LargeTearPulse(seconds, 3.65f, 3.79f);
            float direction = (band & 1) == 0 ? -1f : 1f;
            return direction * Mathf.Max(first, second) * _screenSize.x * .008f;
        }

        private static float LargeTearPulse(float seconds, float start, float end)
        {
            if (seconds <= start || seconds >= end) return 0f;
            return Mathf.Sin((seconds - start) / (end - start) * Mathf.PI);
        }

        private void UpdateGlyphMesh(float seconds)
        {
            if (_glyphMesh == null) return;
            float firstTear = LargeTearPulse(seconds, 2.05f, 2.18f);
            float secondTear = LargeTearPulse(seconds, 3.65f, 3.79f);
            for (int i = 0; i < GlyphCount; i++)
            {
                GlyphSpec glyph = _glyphs[i];
                float depth = Mathf.Min(glyph.Depth + .08f * Mathf.Max(firstTear, secondTear), _maximumFrontDepth);
                Vector2 center = GlyphCenter(glyph, seconds, depth);
                float direction = ((i + glyph.Digit) & 1) == 0 ? -1f : 1f;
                center.x += direction * Mathf.Max(firstTear, secondTear) * Mathf.Min(.12f, _screenSize.x * .045f);
                float revealStart = glyph.Kind == GlyphKind.Near ? 1.25f : .06f + glyph.Delay;
                float reveal = Mathf.Clamp01((seconds - revealStart) / .06f);
                float edgeFade = GlyphEdgeFade(glyph, center.y);
                float emphasis = 1f + secondTear * .5f + (seconds >= 3.65f ? .18f : 0f);
                float alpha = Mathf.Clamp01(glyph.BaseColor.a * reveal * edgeFade * emphasis);
                SetQuadVertices(_glyphVertices, i * 4, center.x, center.y, -depth,
                    glyph.Size, glyph.Size * 1.34f);
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
                float halfHeight = Mathf.Tan(29f * Mathf.Deg2Rad) * distance;
                Vector2 position = NearPositions[glyph.Bundle];
                return new Vector2(distance * position.x, halfHeight * position.y);
            }
            Vector2 region = ClusterPosition(glyph.Cluster, glyph.X, glyph.Y);
            float xPosition = region.x * _screenSize.x;
            float yPosition = region.y * _screenSize.y;
            float flowTime = GlyphFlowTime(seconds);
            if (glyph.Kind == GlyphKind.Middle)
            {
                xPosition = BundleX(glyph.Bundle) * _screenSize.x + (glyph.X - .5f) * .12f;
                float range = _screenSize.y * 1.38f;
                yPosition = Mathf.Repeat(glyph.Y * range - flowTime * glyph.Speed + range, range) - range * .58f;
            }
            else if (glyph.Kind == GlyphKind.Far)
            {
                float range = _screenSize.y * 1.3f;
                yPosition = Mathf.Repeat(yPosition - flowTime * glyph.Speed * .34f + range * .62f, range) - range * .62f;
            }
            else
            {
                xPosition += Mathf.Sin(flowTime * .18f + glyph.Phase) * _screenSize.x * .055f;
                yPosition += Mathf.Sin(flowTime * .15f + glyph.Phase * .8f) * .014f;
            }
            return new Vector2(xPosition, yPosition);
        }

        private static float GlyphFlowTime(float seconds)
        {
            if (seconds <= 1.55f) return seconds;
            if (seconds <= 2.05f) return 1.55f + (seconds - 1.55f) * .04f;
            float time = 1.57f + (seconds - 2.05f);
            if (seconds > 3.65f) time += (seconds - 3.65f) * .55f;
            return time;
        }

        private float GlyphEdgeFade(GlyphSpec glyph, float y)
        {
            if (glyph.Kind == GlyphKind.Near || glyph.Kind == GlyphKind.Fragment) return 1f;
            float halfRange = _screenSize.y * .69f;
            return Mathf.Clamp01((halfRange - Mathf.Abs(y)) / Mathf.Max(.025f, _screenSize.y * .12f));
        }

        private void UpdateInterferenceMesh(float seconds)
        {
            if (_interferenceMesh == null) return;
            float reveal = Mathf.Clamp01((seconds - .06f) / .05f);
            float firstTear = LargeTearPulse(seconds, 2.05f, 2.18f);
            float secondTear = LargeTearPulse(seconds, 3.65f, 3.79f);
            for (int i = 0; i < InterferenceStripCount; i++)
            {
                StripSpec strip = _strips[i];
                float direction = (i & 1) == 0 ? -1f : 1f;
                float x = strip.X * _screenSize.x + direction * Mathf.Max(firstTear, secondTear) * .12f;
                float y = strip.Y * _screenSize.y;
                float depth = Mathf.Min(strip.Depth + .08f * Mathf.Max(firstTear, secondTear), _maximumFrontDepth);
                SetQuadVertices(_interferenceVertices, i * 4, x, y, -depth,
                    strip.Width * _screenSize.x, strip.Height);
                float pulse = .62f + firstTear * .38f + secondTear * .72f + (seconds >= 3.65f ? .12f : 0f);
                SetQuadColor(_interferenceColors, i * 4,
                    new Color(1f, .06f, .085f, Mathf.Clamp01(strip.Alpha * reveal * pulse)));
            }
            _interferenceMesh.SetVertices(_interferenceVertices);
            _interferenceMesh.SetColors(_interferenceColors);
        }

        private void UpdateReadMasks(float seconds)
        {
            if (_screenRoot == null) return;
            Vector4 warningRect = seconds >= .095f && (_takeoverMode || seconds < 5.9f) ?
                ReadRect(.10f, .16f, _warningWidth, _warningHeight) : Vector4.zero;
            Vector4 subtitleRect = seconds >= .32f && (_takeoverMode || seconds < 6.1f) ?
                ReadRect(.12f, -.14f, _subtitleWidth, _subtitleHeight) : Vector4.zero;
            Vector4 contextRect = contextGraphic != null && seconds >= .65f &&
                (_takeoverMode || seconds < 6f) ?
                ReadRect(.15f, -.34f, _contextWidth, _contextHeight) : Vector4.zero;
            Vector4 statusRect = (attemptGraphic != null || failedGraphic != null) &&
                seconds >= 1.35f && (_takeoverMode || seconds < 6.84f) ?
                ReadRect(.21f, -.50f, _statusWidth, _statusHeight) : Vector4.zero;
            Matrix4x4 worldToLocal = _screenRoot.worldToLocalMatrix;
            SetReadMask(_symbolMaterial, worldToLocal, warningRect, subtitleRect, contextRect, statusRect);
            SetReadMask(_decorationMaterial, worldToLocal, warningRect, subtitleRect, contextRect, statusRect);
            SetReadMask(_glyphMaterial, worldToLocal, warningRect, subtitleRect, contextRect, statusRect);
            SetReadMask(_interferenceMaterial, worldToLocal, warningRect, subtitleRect, contextRect, statusRect);
        }

        private Vector4 ReadRect(float centerX, float centerY, float width, float height) =>
            new Vector4(centerX * _screenSize.x, centerY * _screenSize.y,
                width * .5f + .03f, height * .5f + .03f);

        private static void SetReadMask(Material? material, Matrix4x4 worldToLocal, Vector4 rect0,
            Vector4 rect1, Vector4 rect2, Vector4 rect3)
        {
            if (material == null) return;
            material.SetMatrix(ReadWorldToLocalId, worldToLocal);
            material.SetVector(ReadRect0Id, rect0);
            material.SetVector(ReadRect1Id, rect1);
            material.SetVector(ReadRect2Id, rect2);
            material.SetVector(ReadRect3Id, rect3);
            material.SetFloat(ReadMaskStrengthId, .93f);
        }

        private float TakeoverVisualSeconds(float seconds)
        {
            if (!_takeoverMode || seconds < TakeoverLoopEndSec) return seconds;
            return TakeoverLoopStartSec +
                Mathf.Repeat(seconds - TakeoverLoopStartSec, TakeoverLoopEndSec - TakeoverLoopStartSec);
        }

        private void ApplyTakeoverOpacity(float opacity)
        {
            float readingOpacity = opacity * Mathf.Lerp(1f, .10f / .35f, _takeoverReadFocus);
            float surroundingOpacity = opacity * Mathf.Lerp(1f, .14f / .35f, _takeoverReadFocus);
            float statusTarget = _takeoverBlockFailed ? .55f : .38f;
            float statusOpacity = opacity * Mathf.Lerp(1f, statusTarget / .35f, _takeoverReadFocus);
            ApplyOpacity(_warningColors, _warningMesh, readingOpacity);
            ApplyOpacity(_subtitleColors, _subtitleMesh, readingOpacity);
            ApplyOpacity(_symbolColors, _symbolMesh, surroundingOpacity);
            ApplyOpacity(_decorationColors, _decorationMesh, surroundingOpacity);
            ApplyOpacity(_contextColors, _contextMesh, readingOpacity);
            ApplyOpacity(_statusColors, _statusMesh, statusOpacity);
            ApplyOpacity(_glyphColors, _glyphMesh, surroundingOpacity);
            ApplyOpacity(_interferenceColors, _interferenceMesh, surroundingOpacity);
        }

        private static void ApplyOpacity(Color[] colors, Mesh? mesh, float opacity)
        {
            if (mesh == null) return;
            for (int i = 0; i < colors.Length; i++) colors[i].a = Mathf.Clamp01(colors[i].a * opacity);
            mesh.SetColors(colors);
        }

        private void ResetTakeoverSample()
        {
            _takeoverMode = false;
            _takeoverBlockFailed = false;
            _takeoverOpacity = 1f;
            _takeoverReadFocus = 0f;
        }

        private void MeasureAudience(Transform anchor)
        {
            _audienceDistance = Camera.main != null ?
                Vector3.Distance(Camera.main.transform.position, anchor.position) : 3f;
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
            DestroySafe(_contextMaterial);
            DestroySafe(_statusMaterial);
            DestroySafe(_glyphMaterial);
            DestroySafe(_interferenceMaterial);
            DestroySafe(_warningMesh);
            DestroySafe(_subtitleMesh);
            DestroySafe(_symbolMesh);
            DestroySafe(_decorationMesh);
            DestroySafe(_contextMesh);
            DestroySafe(_statusMesh);
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
            _contextMaterial = null;
            _statusMaterial = null;
            _glyphMaterial = null;
            _interferenceMaterial = null;
            _warningMesh = null;
            _subtitleMesh = null;
            _symbolMesh = null;
            _decorationMesh = null;
            _contextMesh = null;
            _statusMesh = null;
            _glyphMesh = null;
            _interferenceMesh = null;
        }

        private static void DestroySafe(UnityEngine.Object? value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        private enum GlyphKind { Far, Middle, Near, Fragment }

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
