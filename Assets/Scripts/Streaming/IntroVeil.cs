#nullable enable

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の全画面の覆いと、静止した実景を運ぶ立体的な破片。
    ///
    /// 基底 Quad は <c>CenterEyeAnchor</c> の子（head-lock）。破片は撮影時の頭位置から
    /// スクリーンの実平面へワールド空間で移動する。静止画像を得られない場合に限り、
    /// <c>Blend Zero SrcAlpha</c> で現実を透かす Passthrough Windows 表示へ戻す。
    ///
    /// ⚠ <b>破片の開始姿勢は、静止画の有無に関わらず割れ始めの 1 回だけ取る</b>（2026-09-14）。
    /// 静止画が取れなかった経路で毎フレームの頭の姿勢を渡していたため、割れた実景が
    /// 頭についてきた（ユーザー報告「フリーズしたパススルーが目の前にずっとついてくる」）。
    /// 割れた実景はその地点に残り、そこからスクリーンへ集まる。どちらの経路でも同じ。
    ///
    /// <b>面そのものは head-lock</b>（視界を必ず覆い切るため。ワールド固定にすると頭を振った瞬間に
    /// 覆いの外が見える）。<b>その上で、開口だけがスクリーンの見かけの形をなぞる</b>
    /// （計画 2026-07-30_intro-passthrough-to-screen.md §4 の「枠は現れるだけで、既にそこにある」）。
    ///
    /// ⚠ <b>「枠は head-lock だから常に正面」は誤りだった</b>（2026-08-01 実害）。本編のスクリーンは
    /// 頭から <c>heightOffset</c> ぶん<b>下</b>（既定 -0.28m / 2.0m ＝ 約 8°）に置かれ、ヨーも緩追従で遅れる。
    /// 開口を uv 中心に固定していたため、<b>枠とスクリーンが縦に 8°（半画角 18.4° に対して 43%）ずれていた</b>。
    ///
    /// 判定は <b>眼とスクリーンの 4 辺を通る平面</b>（<see cref="BuildFramePlanes"/>）で行う。
    /// 覆いの面へ矩形として投影する方式だと、<b>スクリーン面と覆い面が平行なときしか正しくない</b> —
    /// 頭を 20° 下げるとスクリーンは水平のままなので両面が 20° 傾き、四隅が 2.8° ずれる。
    /// 平面 4 枚なら<b>頭の向き・追従の遅れ・首の傾きに関係なく厳密</b>で、除算も特異点も無い。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IntroVeil : MonoBehaviour
    {
        [Tooltip("カメラからの距離 (m)。near clip より大きく、スクリーンより手前に。")]
        [SerializeField, Min(0.05f)] private float distance = 0.3f;

        [Tooltip("覆いの大きさ (m)。この距離で視界を覆い切るサイズにする。")]
        [SerializeField] private Vector2 veilSize = new(2.0f, 2.0f);

        [Tooltip("screenQuad が無いときの開口の半画角 (度)。既定は本編スクリーンの実測 (30.6 / 18.4)。")]
        [SerializeField] private Vector2 fallbackApertureHalfAngleDeg = new(30.6f, 18.4f);

        [Tooltip("本編のスクリーン。開口の中心・大きさ・傾きをここから毎フレーム逆算する。")]
        [SerializeField] private Transform? screenQuad;

        [Tooltip("枠の縁のぼけ。硬い矩形は「UI の窓」に見えるので少しぼかす。")]
        [SerializeField, Range(0.002f, 0.4f)] private float feather = 0.08f;

        private static readonly int PassthroughId = Shader.PropertyToID("_Passthrough");
        /// <summary>スクリーン矩形だけを現実からカメラ映像へ入れ替える量。</summary>
        private static readonly int ScreenFadeId = Shader.PropertyToID("_ScreenFade");
        private static readonly int FractureActiveId = Shader.PropertyToID("_FractureActive");
        private static readonly int ShatterId = Shader.PropertyToID("_Shatter");
        private static readonly int VeilSizeId = Shader.PropertyToID("_VeilSize");
        private static readonly int FeatherAngId = Shader.PropertyToID("_FeatherAng");
        private static readonly int ScreenCenterId = Shader.PropertyToID("_ScreenCenter");
        private static readonly int ScreenRightId = Shader.PropertyToID("_ScreenRight");
        private static readonly int ScreenUpId = Shader.PropertyToID("_ScreenUp");
        private static readonly int ScreenHalfId = Shader.PropertyToID("_ScreenHalf");
        private static readonly int HasFrozenFrameId = Shader.PropertyToID("_HasFrozenFrame");
        private static readonly int FrozenLeftTexId = Shader.PropertyToID("_FrozenLeftTex");
        private static readonly int FrozenRightTexId = Shader.PropertyToID("_FrozenRightTex");
        private static readonly int LeftWorldToUvId = Shader.PropertyToID("_LeftWorldToUv");
        private static readonly int RightWorldToUvId = Shader.PropertyToID("_RightWorldToUv");
        private static readonly int CaptureHeadToWorldId = Shader.PropertyToID("_CaptureHeadToWorld");
        private static readonly int CurrentHeadPositionId = Shader.PropertyToID("_CurrentHeadPosition");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int SrcBlendAlphaId = Shader.PropertyToID("_SrcBlendAlpha");
        private static readonly int DstBlendAlphaId = Shader.PropertyToID("_DstBlendAlpha");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");
        private static readonly int ColorMaskId = Shader.PropertyToID("_ColorMask");
        private static readonly int RevealId = Shader.PropertyToID("_Reveal");
        private static readonly int GapsModeId = Shader.PropertyToID("_GapsMode");
        private static readonly int StencilRefId = Shader.PropertyToID("_StencilRef");
        private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
        private static readonly int StencilPassId = Shader.PropertyToID("_StencilPass");
        private static readonly int StencilWriteMaskId = Shader.PropertyToID("_StencilWriteMask");
        private static readonly int StencilReadMaskId = Shader.PropertyToID("_StencilReadMask");
        /// <summary>破片の深度パスがステンシルに刻むビット（0225）。隔離殻の Ref 1 と被らない。</summary>
        public const int FractureStencilBit = 32;
        private static readonly int[] FramePlaneIds =
        {
            Shader.PropertyToID("_FramePlane0"), Shader.PropertyToID("_FramePlane1"),
            Shader.PropertyToID("_FramePlane2"), Shader.PropertyToID("_FramePlane3"),
        };
        private static readonly int[] ScreenPlaneIds =
        {
            Shader.PropertyToID("_ScreenPlane0"), Shader.PropertyToID("_ScreenPlane1"),
            Shader.PropertyToID("_ScreenPlane2"), Shader.PropertyToID("_ScreenPlane3"),
        };
        // 開口を**他の面へも配る**ための global（`SealedBox.shader` が読む）。封印の箱は覆いより
        // 後に描かれるので、開口で切らないと枠の外へはみ出して「枠が閉じる」が見えなくなる。
        private static readonly int GlobalW2LId = Shader.PropertyToID("_IntroFrameW2L");
        private static readonly int[] GlobalPlaneIds =
        {
            Shader.PropertyToID("_IntroFramePlane0"), Shader.PropertyToID("_IntroFramePlane1"),
            Shader.PropertyToID("_IntroFramePlane2"), Shader.PropertyToID("_IntroFramePlane3"),
        };
        private static readonly int GlobalFeatherId = Shader.PropertyToID("_IntroFrameFeather");

        private MeshRenderer? _renderer;
        private MeshFilter? _filter;
        private Material? _mat;
        private MeshRenderer? _fractureRenderer;
        private MeshFilter? _fractureFilter;
        private Material? _fractureMat;
        private MeshRenderer? _fractureDepthRenderer;
        private MeshFilter? _fractureDepthFilter;
        private Material? _fractureDepthMat;
        // 0225: 破片の隙間だけを黒く塗る面（基底 4900 → 深度 4901 → 隙間 4902 → 色 4903）。
        private MeshFilter? _gapsFilter;
        private MeshRenderer? _gapsRenderer;
        private Material? _gapsMat;
        private MaterialPropertyBlock? _fractureBlock;
        private RenderTexture? _frozenLeft;
        private RenderTexture? _frozenRight;
        private Matrix4x4 _leftWorldToUv = Matrix4x4.identity;
        private Matrix4x4 _rightWorldToUv = Matrix4x4.identity;
        private Matrix4x4 _captureHeadToWorld = Matrix4x4.identity;

        /// <summary>破砕開始時に左右眼の実景を一度だけ借りる。</summary>
        public Func<IntroFrozenFrameSource?>? FrozenFrameProvider { get; set; }

        /// <summary>借りた実景を所有 RenderTexture へ複製できたか。</summary>
        public bool HasFrozenFrame => _frozenLeft != null && _frozenRight != null;

        /// <summary>この走行で所有コピーを作れた回数。</summary>
        public int FrozenFrameCount { get; private set; }

        /// <summary>この走行で provider を試したか。失敗時も再試行しない。</summary>
        public bool FrozenFrameAttempted { get; private set; }

        /// <summary>
        /// 実景の静止画を用意する側（<c>IntroPassthroughCapture</c>）が書く状態。テレメトリが
        /// <c>shatCam=</c> で出す。<c>none</c>=未配線 / <c>ok</c>=取得できた / それ以外は取れなかった理由。
        /// </summary>
        public string FrozenFrameStatus { get; set; } = "none";

        /// <summary>
        /// 割れ始めの頭の姿勢を固定したか。破片はこの姿勢からワールド空間で動く。
        /// 静止画の有無に関わらず、割れ始めの 1 回だけ取る。
        /// </summary>
        public bool ShatterAnchored { get; private set; }

        /// <summary>固定した割れ始めの頭のワールド位置（テスト・診断用）。</summary>
        public Vector3 ShatterAnchorPosition => _captureHeadToWorld.GetColumn(3);

        /// <summary>いま覆いが何かを隠しているか（＝導入演出中か）。</summary>
        public bool IsActive => _renderer != null && _renderer.enabled;

        /// <summary>
        /// 覆いの実体（Quad + マテリアル）を組めたか。<b>false なら枠は一生出ない</b> —
        /// <see cref="Build"/> がシェーダを見つけられずに早期 return した状態で、
        /// 実行時 <c>Shader.Find</c> のシェーダがビルドから剥がれたときにこうなる
        /// （2026-07-31 実害。Editor では通るので実機の画を見るまで気づけない）。
        /// テレメトリが <c>veilBuilt=</c> で出し、解析が「導入の覆いが描画されていない」を名指しする。
        /// </summary>
        public bool IsBuilt => _renderer != null;

        /// <summary>段 4 で単一 quad の開口を実際に描いたか。</summary>
        public bool ApertureDrawn { get; private set; }

        /// <summary>実際に配った開口の閉じ具合の最大値。</summary>
        public float ApertureClosePeak { get; private set; }

        /// <summary>現在描いている覆いの quad 数。組めていれば常に 1。</summary>
        public int ApertureQuads => _filter != null && _filter.sharedMesh == _mesh ? 1 : 0;

        /// <summary>映像との交差判定に使ったスクリーン矩形（半幅,半高,眼からの距離）。</summary>
        public string ApertureRectDesc { get; private set; } = "-";

        /// <summary>破片メッシュがあり、現在その Renderer を実際に描いているか。</summary>
        public bool ShatterDrawn => _fractureRenderer != null && _fractureRenderer.enabled
                                    && _fractureFilter != null && _fractureFilter.sharedMesh != null;

        /// <summary>この走行で Renderer へ実際に配った破砕進行度の最大値。</summary>
        /// <summary>着地した破片の下に映像を残すため、隙間だけを黒く塗る面を出しているか（0225）。</summary>
        public bool GapsDrawn { get; private set; }

        public float ShatterPeak { get; private set; }

        /// <summary>生成された微細破片の実数。</summary>
        public int ShatterPieces { get; private set; }

        /// <summary>破片の行き先に使ったスクリーン矩形（半幅,半高,眼からの距離）。</summary>
        public string ShatterRectDesc { get; private set; } = "-";

        private void Awake()
        {
            Build();
            SetHidden();
        }

        private void Build()
        {
            if (_renderer != null) return;   // 二度組まない（Editor プレビューは Awake を手で叩く）
            var shader = Shader.Find("FixedCamVr/IntroVeil");
            if (shader == null)
            {
                // シェーダが見つからないなら覆いは出さない（黒い板を出して視界を塞ぐ方が危ない）。
                Debug.LogWarning("[IntroVeil] シェーダ FixedCamVr/IntroVeil が見つかりません。導入演出の覆いは出ません。");
                return;
            }

            var go = new GameObject("IntroVeilQuad");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, distance);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(veilSize.x, veilSize.y, 1f);

            _mesh = BuildQuad();
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;
            _filter = mf;

            _quad = go.transform;
            _renderer = go.AddComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "IntroVeil (runtime)" };
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            // 覆いは常に描く（視錐台カリングで消えると視界に穴が空く）。
            _renderer.allowOcclusionWhenDynamic = false;

            // 0225: 破片の隙間だけを黒く塗る面。基底（4900）が RGB を残す区間に、深度パス（4901）が刻んだ
            // ステンシルの外側だけを 4902 で塗る。着地して透明になった破片の下に、その場所の映像が残る。
            var gapsGo = new GameObject("IntroVeilGaps");
            gapsGo.transform.SetParent(transform, worldPositionStays: false);
            gapsGo.transform.localPosition = new Vector3(0f, 0f, distance);
            gapsGo.transform.localRotation = Quaternion.identity;
            gapsGo.transform.localScale = new Vector3(veilSize.x, veilSize.y, 1f);
            _gaps = gapsGo.transform;
            _gapsFilter = gapsGo.AddComponent<MeshFilter>();
            _gapsFilter.sharedMesh = _mesh;
            _gapsRenderer = gapsGo.AddComponent<MeshRenderer>();
            _gapsMat = new Material(shader) { name = "IntroVeilGaps (runtime)" };
            _gapsMat.renderQueue = 4902;
            _gapsMat.SetFloat(GapsModeId, 1f);
            _gapsMat.SetInt(ZWriteId, 0);
            _gapsMat.SetInt(StencilRefId, FractureStencilBit);
            _gapsMat.SetInt(StencilReadMaskId, FractureStencilBit);
            _gapsMat.SetInt(StencilCompId, (int)CompareFunction.NotEqual);
            _gapsRenderer.sharedMaterial = _gapsMat;
            _gapsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _gapsRenderer.receiveShadows = false;
            _gapsRenderer.allowOcclusionWhenDynamic = false;
            _gapsRenderer.enabled = false;

            var fractureShader = Shader.Find("FixedCamVr/IntroFracture");
            if (fractureShader == null)
            {
                Debug.LogWarning("[IntroVeil] シェーダ FixedCamVr/IntroFracture が見つかりません。破砕は出ません。");
                return;
            }

            var fractureGo = new GameObject("IntroVeilFractureColor");
            fractureGo.transform.SetParent(transform, worldPositionStays: false);
            fractureGo.transform.localPosition = new Vector3(0f, 0f, distance);
            fractureGo.transform.localRotation = Quaternion.identity;
            fractureGo.transform.localScale = new Vector3(veilSize.x, veilSize.y, 1f);
            _fracture = fractureGo.transform;

            _fractureMesh = IntroFractureMesh.Build();
            _fractureFilter = fractureGo.AddComponent<MeshFilter>();
            _fractureFilter.sharedMesh = _fractureMesh;
            ShatterPieces = IntroFractureMesh.LastPieceCount;

            _fractureRenderer = fractureGo.AddComponent<MeshRenderer>();
            _fractureMat = new Material(fractureShader) { name = "IntroFracture (runtime)" };
            // 色は隙間の面（4902）の後に描く。着地して透明になった片の下に、その場所の映像が残る。
            _fractureMat.renderQueue = 4903;
            ConfigureColorMaterial(frozen: false);
            _fractureRenderer.sharedMaterial = _fractureMat;
            _fractureRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _fractureRenderer.receiveShadows = false;
            _fractureRenderer.allowOcclusionWhenDynamic = false;
            _fractureRenderer.enabled = false;

            var fractureDepthGo = new GameObject("IntroVeilFractureDepth");
            fractureDepthGo.transform.SetParent(transform, worldPositionStays: false);
            fractureDepthGo.transform.localPosition = new Vector3(0f, 0f, distance);
            fractureDepthGo.transform.localRotation = Quaternion.identity;
            fractureDepthGo.transform.localScale = new Vector3(veilSize.x, veilSize.y, 1f);
            _fractureDepth = fractureDepthGo.transform;
            _fractureDepthFilter = fractureDepthGo.AddComponent<MeshFilter>();
            _fractureDepthFilter.sharedMesh = _fractureMesh;
            _fractureDepthRenderer = fractureDepthGo.AddComponent<MeshRenderer>();
            _fractureDepthMat = new Material(fractureShader) { name = "IntroFractureDepth (runtime)" };
            _fractureDepthMat.renderQueue = 4901;
            _fractureDepthMat.SetInt(ColorMaskId, 0);
            _fractureDepthMat.SetInt(ZWriteId, 1);
            _fractureDepthMat.SetInt(ZTestId, (int)CompareFunction.LessEqual);
            _fractureDepthMat.SetInt(SrcBlendId, (int)BlendMode.One);
            _fractureDepthMat.SetInt(DstBlendId, (int)BlendMode.Zero);
            _fractureDepthMat.SetInt(SrcBlendAlphaId, (int)BlendMode.One);
            _fractureDepthMat.SetInt(DstBlendAlphaId, (int)BlendMode.Zero);
            // 深度と一緒に「破片のある所」をステンシルへ刻む（0225）。隙間の面はこのビットの外側だけを塗る。
            _fractureDepthMat.SetInt(StencilRefId, FractureStencilBit);
            _fractureDepthMat.SetInt(StencilCompId, (int)CompareFunction.Always);
            _fractureDepthMat.SetInt(StencilPassId, (int)StencilOp.Replace);
            _fractureDepthMat.SetInt(StencilWriteMaskId, FractureStencilBit);
            _fractureDepthRenderer.sharedMaterial = _fractureDepthMat;
            _fractureDepthRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _fractureDepthRenderer.receiveShadows = false;
            _fractureDepthRenderer.allowOcclusionWhenDynamic = false;
            _fractureDepthRenderer.enabled = false;
            _fractureBlock = new MaterialPropertyBlock();
        }

        // インスタンスごとに持つ（static で共有すると、片方の Destroy でもう片方のメッシュが消える）。
        private Mesh? _mesh;
        private Transform? _quad;
        private Mesh? _fractureMesh;
        private Transform? _fracture;
        private Transform? _fractureDepth;
        private Transform? _gaps;

        private void ConfigureColorMaterial(bool frozen)
        {
            if (_fractureMat == null) return;
            _fractureMat.SetInt(ColorMaskId, 15);
            _fractureMat.SetInt(ZWriteId, 0);
            _fractureMat.SetInt(ZTestId,
                (int)(frozen ? CompareFunction.Equal : CompareFunction.Always));
            _fractureMat.SetInt(SrcBlendId,
                (int)(frozen ? BlendMode.SrcAlpha : BlendMode.Zero));
            _fractureMat.SetInt(DstBlendId,
                (int)(frozen ? BlendMode.OneMinusSrcAlpha : BlendMode.SrcAlpha));
            _fractureMat.SetInt(SrcBlendAlphaId, (int)BlendMode.Zero);
            _fractureMat.SetInt(DstBlendAlphaId,
                (int)(frozen ? BlendMode.One : BlendMode.SrcAlpha));
        }

        /// <summary>
        /// 覆いの面を <see cref="PlaneDistanceResolved"/> へ運ぶ。**覆う画角は変えない**ので、
        /// 大きさは距離に比例させる（<see cref="distance"/> / <see cref="veilSize"/> はその基準）。
        /// </summary>
        private Vector2 PlaceQuad()
        {
            float d = Mathf.Max(PlaneDistanceResolved, 0.01f);
            float k = d / Mathf.Max(distance, 0.001f);
            Vector2 size = veilSize * k;
            if (_quad != null)
            {
                _quad.localPosition = new Vector3(0f, 0f, d);
                _quad.localScale = new Vector3(size.x, size.y, 1f);
            }
            if (_fracture != null)
            {
                _fracture.localPosition = new Vector3(0f, 0f, d);
                _fracture.localScale = new Vector3(size.x, size.y, 1f);
            }
            if (_fractureDepth != null)
            {
                _fractureDepth.localPosition = new Vector3(0f, 0f, d);
                _fractureDepth.localScale = new Vector3(size.x, size.y, 1f);
            }
            if (_gaps != null)
            {
                _gaps.localPosition = new Vector3(0f, 0f, d);
                _gaps.localScale = new Vector3(size.x, size.y, 1f);
            }
            return size;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "IntroVeilQuad" };
            m.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            });
            m.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            m.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// 枠が開き切っているときの半径 = 眼からの距離 × これ。tan(77.9°) ＝ 旧実装が
        /// 覆いの面いっぱい（1.4 × 半サイズ / 0.3m）に開いていたのと同じ見かけ角。
        /// </summary>
        private const float OpenTan = 4.667f;

        /// <summary>
        /// これ以下の <c>frameClose</c> は「枠が無い」として<b>厳密に全開</b>へ倒す。
        ///
        /// ⚠ <b>有限の矩形で「視界ぜんぶ」を近似しない</b>（2026-08-09 実害）。
        /// 全開でも開口は 77.9° の矩形でしかなく、しかも中心がスクリーン（頭から 8° 下・
        /// ピッチに追従しない）に固定されていたので、<b>頭を 30° 以上下げると下辺が視界へ入り、
        /// 視界の下端に黒帯が出て、下を向くほど広がった</b>（ユーザー報告
        /// 「下を向くとパススルーが途中で途切れており、そこには黒い空間が広がっている」）。
        /// </summary>
        private const float FullyOpenEpsilon = 0.001f;

        /// <summary>
        /// 開口の中心・姿勢を「頭の正面」から「スクリーンの実位置」へ寄せ切る閉じ具合。
        /// ここでの開口は半画角 69°（視界の ±48° よりまだ大きい）＝**枠としてはまだ見えていない**。
        /// これ以降はスクリーンに完全固定なので、「枠はスクリーンの形のまま縮む」は不変。
        /// </summary>
        private const float AnchorK = 0.15f;

        // 枠の 4 辺の平面の法線（この GameObject のローカル空間・**内側で dot(dir, n) < 0**）。
        private readonly Vector4[] _planes = new Vector4[4];
        // 映像とのクロスフェードは現在の開口ではなく、本編スクリーンの実矩形だけに掛ける。
        private readonly Vector4[] _screenPlanes = new Vector4[4];
        private Vector3 _screenCenter;
        private Vector3 _screenRight = Vector3.right;
        private Vector3 _screenUp = Vector3.up;
        private Vector2 _screenHalf;

        /// <summary>
        /// 覆いの面を置く距離 (m)。<b>スクリーンと同じ距離に置く</b>のが要点で、
        /// <see cref="distance"/> は「その距離での大きさ」を決める基準にしか使わない。
        ///
        /// ⚠ <b>近くに置くと両眼視差でずれる</b>（2026-08-01 実害・ユーザーが録画で発見）。
        /// 穴は中央眼から解くが、描画は左右それぞれの眼から行う。覆いの面が 0.3m・スクリーンが 2m だと、
        /// 同じ穴が左眼では右へ、右眼では左へ寄って見え、<b>枠の外にスクリーンがはみ出す</b>
        /// （実測: 左眼は右側だけ、右眼は左側だけに漏れる）。
        /// 面をスクリーンと同じ距離へ置けば、穴の縁の<b>ワールド位置</b>がスクリーンの縁と一致するので、
        /// どちらの眼から見ても合う。
        /// </summary>
        public float PlaneDistanceResolved { get; private set; }

        /// <summary>
        /// 枠の 4 辺（眼と辺を通る平面）を組む。<b>スクリーンの見かけの形そのもの</b>で、
        /// 頭の向き・追従の遅れ・首の傾きに関係なく厳密。
        ///
        /// 平面で持つ理由: 覆いの面へ矩形として投影する方式は<b>両面が平行なときしか正しくない</b>。
        /// スクリーンは水平（ヨーだけ追従）なので、頭を上下に振ると必ず傾く。
        /// また平面 4 枚なら除算が無く、視線がスクリーン面と平行になる特異点も無い。
        /// </summary>
        /// <param name="frameClose">0 = 全開（覆わない） / 1 = スクリーンの形ちょうど。</param>
        /// <returns>縁のぼけ幅（<see cref="SignedDistance"/> と同じ単位）。</returns>
        private float BuildFramePlanes(float frameClose)
        {
            if (!TryResolveScreenRect(out Vector3 c, out Vector3 right, out Vector3 up,
                                      out float hw, out float hh))
            {
                // screenQuad 未配線 / スクリーンが背後。既定の半画角から覆いの面上に矩形を起こす。
                // 半画角で持つのは、覆いの距離 (distance) を変えても見かけの大きさが変わらないため。
                c = new Vector3(0f, 0f, distance);
                right = Vector3.right;
                up = Vector3.up;
                hw = distance * Mathf.Tan(Mathf.Clamp(fallbackApertureHalfAngleDeg.x, 1f, 80f) * Mathf.Deg2Rad);
                hh = distance * Mathf.Tan(Mathf.Clamp(fallbackApertureHalfAngleDeg.y, 1f, 80f) * Mathf.Deg2Rad);
            }

            float dist = Mathf.Max(c.magnitude, 0.01f);
            PlaneDistanceResolved = dist;   // 覆いの面はここへ置く（両眼視差を消すため）
            ApertureRectDesc = $"{hw:F2},{hh:F2},{dist:F2}";
            ShatterRectDesc = ApertureRectDesc;
            _screenCenter = c;
            _screenRight = right;
            _screenUp = up;
            _screenHalf = new Vector2(hw, hh);
            if (!TryBuildPlanes(c, right, up, hw, hh, _screenPlanes))
                SetPlanesFullyOpen(_screenPlanes);
            float k = Mathf.Clamp01(frameClose);

            // 枠が無い段（黒・現実・格下げ・構造）は**厳密に全開**。有限の矩形で近似すると、
            // その矩形の辺がどこかの頭の向きで必ず視界に入る（FullyOpenEpsilon のコメント）。
            if (k <= FullyOpenEpsilon)
            {
                SetPlanesFullyOpen(_planes);
                return Mathf.Max(Mathf.Sin(Mathf.Clamp01(feather) * Mathf.Atan2(hh, dist)), 1e-4f);
            }

            // 開口の**中心と姿勢**も閉じ具合で補間する。閉じ切り (k=1) はスクリーンの実位置・実姿勢
            // ちょうど（2026-08-01 の整合をそのまま保つ）で、開いている間は頭へ寄せる。
            //
            // 中心をスクリーンに固定したまま大きさだけ広げると、開口はスクリーンと同じだけ
            // 下がったまま巨大化するので、**視界の下側だけが枠の外に出る**。
            // 姿勢も要る — スクリーンは水平（ヨーだけ追従）なので、頭を大きく下げるとスクリーンの
            // 上方向が頭から見てほぼ前を向き、**巨大な開口が眼の後ろまで回り込んで退化する**。
            //
            // ⚠ 寄せ切るのは <see cref="AnchorK"/> まで。**そこから先はスクリーンに完全固定**で、
            //    「枠はスクリーンの形のまま縮む」（2026-08-01）を 1 ビットも変えない。
            //    AnchorK での開口は半画角 69° ＝ 視界（±48°）よりまだ十分大きいので、
            //    寄せている区間は体験者に枠として見えていない。
            float ka = Mathf.Clamp01(k / AnchorK);
            c = Vector3.Lerp(new Vector3(0f, 0f, dist), c, ka);
            right = Vector3.Slerp(Vector3.right, right, ka).normalized;
            up = Vector3.Slerp(Vector3.up, up, ka).normalized;

            // 枠は**スクリーンの形のまま**縮む。縦横を別々に補間してはいけない
            // （2026-08-01 実害・ユーザー報告「灰色の迫りが枠とずれている」）。
            // 旧実装は幅と高さを同じ値（dist × OpenTan）から別々に lerp していたため、
            // 枠は**ほぼ正方形のまま迫ってきて、最後にだけ 16:9 へ変形**していた。
            // 「枠は現れるだけで、既にそこにある」（計画 §4）が、閉じている間だけ崩れる。
            //
            // 比を保つので、駆動するのは**縦**（高さの方が閉じ切りの角度が小さく、
            // 全開時に覆いを覆い切れるかの制約もこちらが握る）。角度で線形に閉じる。
            float aOpen = Mathf.Atan(OpenTan);
            float aClosed = Mathf.Atan2(hh, dist);
            float scale = dist * Mathf.Tan(Mathf.Lerp(aOpen, aClosed, k)) / Mathf.Max(hh, 1e-4f);
            float w = hw * scale;
            float h = hh * scale;

            if (!TryBuildPlanes(c, right, up, w, h, _planes)) SetPlanesFullyOpen(_planes);

            // 縁のぼけ。feather は「枠の半分の高さに対する割合」なので、角度へ直してから
            // SignedDistance と同じ単位（辺の平面からの sin）にする。
            //
            // ⚠ 基準は**閉じ切った枠**（hh）で、いまの大きさ（h）ではない。h を使うと開いているとき
            //    ぼけ幅が 6° 以上に膨らみ、**覆いの四隅がぼけ帯に入って半分黒くなる**
            //    （＝現実がそのまま見えているべき段 1〜3 で四隅が翳る）。ぼけの硬さも一定になる。
            float featherAng = Mathf.Sin(Mathf.Clamp01(feather) * Mathf.Atan2(hh, dist));
            return Mathf.Max(featherAng, 1e-4f);
        }

        /// <summary>スクリーンの矩形をこの GameObject のローカル空間で解く。</summary>
        private bool TryResolveScreenRect(out Vector3 center, out Vector3 right, out Vector3 up,
                                          out float halfW, out float halfH)
        {
            center = Vector3.zero; right = Vector3.right; up = Vector3.up; halfW = 0f; halfH = 0f;
            if (screenQuad == null) return false;
            Vector3 s = screenQuad.lossyScale;
            if (!(s.x > 0.001f) || !(s.y > 0.001f)) return false;

            center = transform.InverseTransformPoint(screenQuad.position);
            if (center.z <= 0.01f) return false;   // 真横・背後（枠を出す段では起きない）
            right = transform.InverseTransformDirection(screenQuad.right).normalized;
            up = transform.InverseTransformDirection(screenQuad.up).normalized;
            halfW = s.x * 0.5f;
            halfH = s.y * 0.5f;
            return true;
        }

        // 眼（ローカル原点）と辺 a→b を通る平面。内側（center 側）が負になるよう向きを揃える。
        private static bool TryBuildPlanes(Vector3 center, Vector3 right, Vector3 up,
                                           float halfW, float halfH, Vector4[] target)
        {
            Vector3 p0 = center - right * halfW - up * halfH;
            Vector3 p1 = center + right * halfW - up * halfH;
            Vector3 p2 = center + right * halfW + up * halfH;
            Vector3 p3 = center - right * halfW + up * halfH;
            return TryEdgePlane(p0, p1, center, 0, target)
                && TryEdgePlane(p1, p2, center, 1, target)
                && TryEdgePlane(p2, p3, center, 2, target)
                && TryEdgePlane(p3, p0, center, 3, target);
        }

        private static bool TryEdgePlane(Vector3 a, Vector3 b, Vector3 inside, int index, Vector4[] target)
        {
            Vector3 n = Vector3.Cross(a, b);
            if (n.sqrMagnitude < 1e-12f) return false;   // 辺が眼と一直線 = 枠が退化している
            n.Normalize();
            if (Vector3.Dot(inside, n) > 0f) n = -n;
            target[index] = new Vector4(n.x, n.y, n.z, 0f);
            return true;
        }

        // 退化したときは「何も覆わない」に倒す。覆いは全画面 1 パスなので、
        // 判定が壊れた瞬間に視界が真っ黒になる方が危ない。dir.z > 0 は覆いの面の性質から常に成立。
        private static void SetPlanesFullyOpen(Vector4[] planes)
        {
            for (int i = 0; i < planes.Length; i++) planes[i] = new Vector4(0f, 0f, -1f, 0f);
        }

        private void TryCaptureFrozenFrame()
        {
            FrozenFrameAttempted = true;
            RenderTexture? left = null;
            RenderTexture? right = null;
            try
            {
                IntroFrozenFrameSource? candidate = FrozenFrameProvider?.Invoke();
                if (!candidate.HasValue) return;
                IntroFrozenFrameSource source = candidate.Value;
                if (source.Left == null || source.Right == null
                    || source.Left.width <= 0 || source.Left.height <= 0
                    || source.Right.width <= 0 || source.Right.height <= 0)
                    return;

                left = CopyFrozenTexture(source.Left, "IntroFrozenLeft");
                right = CopyFrozenTexture(source.Right, "IntroFrozenRight");
                _frozenLeft = left;
                _frozenRight = right;
                left = null;
                right = null;
                _leftWorldToUv = source.LeftWorldToUv;
                _rightWorldToUv = source.RightWorldToUv;
                // 開始姿勢は Apply が割れ始めに固定済み（静止画の有無に関わらず同じ行列）。
                if (_fractureMesh != null)
                {
                    IntroFractureMesh.ReselectClosingPieces(
                        _fractureMesh, _captureHeadToWorld, _leftWorldToUv, _rightWorldToUv);
                }
                FrozenFrameCount++;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IntroVeil] 実景の静止画を複製できません: {ex.Message}");
            }
            finally
            {
                DestroyFrozenTexture(left);
                DestroyFrozenTexture(right);
            }
        }

        private static RenderTexture CopyFrozenTexture(Texture source, string name)
        {
            var copy = new RenderTexture(
                source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            try
            {
                copy.Create();
                if (!copy.IsCreated())
                    throw new InvalidOperationException($"{name} RenderTexture の生成に失敗しました。");
                Graphics.Blit(source, copy);
                return copy;
            }
            catch
            {
                DestroyFrozenTexture(copy);
                throw;
            }
        }

        private void ReleaseFrozenFrame()
        {
            DestroyFrozenTexture(_frozenLeft);
            DestroyFrozenTexture(_frozenRight);
            _frozenLeft = null;
            _frozenRight = null;
            _leftWorldToUv = Matrix4x4.identity;
            _rightWorldToUv = Matrix4x4.identity;
        }

        /// <summary>割れ始めの頭の姿勢を捨てる。走行の境界と覆いを畳むときだけ。</summary>
        private void ReleaseShatterAnchor()
        {
            _captureHeadToWorld = Matrix4x4.identity;
            ShatterAnchored = false;
        }

        private static void DestroyFrozenTexture(RenderTexture? texture)
        {
            if (texture == null) return;
            texture.Release();
            if (Application.isPlaying) Destroy(texture);
            else DestroyImmediate(texture);
        }

        /// <summary>導入演出の重みを覆いへ流す。<b>判断はしない</b>（値を書くだけ）。</summary>
        public void Apply(in IntroWeights w)
        {
            if (_renderer == null || _mat == null) return;

            // 新しい導入が全開から始まった時点で、前回走行の到達値を落とす。
            // SetHidden では落とさない。Frame → Swap の遷移ログが閉じ切った実測を読むため。
            if (w.shatter <= FullyOpenEpsilon && w.frame <= FullyOpenEpsilon)
            {
                ReleaseFrozenFrame();
                ReleaseShatterAnchor();
                FrozenFrameAttempted = false;
                FrozenFrameCount = 0;
                ApertureDrawn = false;
                ApertureClosePeak = 0f;
                ShatterPeak = 0f;
            }

            // 割れ始めの頭の姿勢を 1 回だけ固定する。破片はここからワールド空間で動くので、
            // その後に頭を振っても割れた実景はその地点に残る。静止画が取れなくても同じ。
            if (w.shatter > FullyOpenEpsilon && !ShatterAnchored)
            {
                _captureHeadToWorld = transform.localToWorldMatrix;
                ShatterAnchored = true;
            }

            if (w.shatter > FullyOpenEpsilon && !FrozenFrameAttempted)
                TryCaptureFrozenFrame();

            // 何も隠していない状態（枠が開いていて、パススルーも出さない）では描画そのものを止める。
            // 覆いは全画面 1 パスなので、本編中ずっと描くのは無駄。
            // Frame を描いた後の Swap では閉じ切った開口を 1 枚のまま静止して描く。
            // これにより frame=1 は実際の描画経路を通り、終端の観測値も捏造にならない。
            bool needed = w.frame < 0.999f || w.passthrough > 0.001f || ApertureDrawn;
            if (!needed) { SetHidden(); return; }

            _renderer.enabled = true;
            float featherAng = BuildFramePlanes(w.frame);
            // 面をスクリーンと同じ距離へ運び、見かけの大きさ（＝覆う画角）は変えない。
            Vector2 size = PlaceQuad();
            for (int i = 0; i < FramePlaneIds.Length; i++) _mat.SetVector(FramePlaneIds[i], _planes[i]);
            for (int i = 0; i < ScreenPlaneIds.Length; i++) _mat.SetVector(ScreenPlaneIds[i], _screenPlanes[i]);
            _mat.SetFloat(PassthroughId, Mathf.Clamp01(w.passthrough));
            _mat.SetFloat(ScreenFadeId, Mathf.Clamp01(w.live));
            _mat.SetFloat(FractureActiveId, w.shatter > FullyOpenEpsilon ? 1f : 0f);
            // 0225: 着地した破片が映像を見せる区間。静止画が取れなかった経路（旧 alpha 窓）では使わない。
            _mat.SetFloat(RevealId, HasFrozenFrame ? Mathf.Clamp01(w.reveal) : 0f);
            _mat.SetVector(VeilSizeId, new Vector4(size.x, size.y, PlaneDistanceResolved, 0f));
            _mat.SetFloat(FeatherAngId, featherAng);

            // p=.81 で最後の大片がスクリーン矩形を埋める。p=.88 までは実景の面を静止して見せる。
            // Renderer は Frame 終端まで通して実配布の最大値を記録し、Swap へ入った所で止める。
            bool drawShatter = w.shatter > FullyOpenEpsilon
                               && _fractureRenderer != null && _fractureMat != null
                               && _fractureMesh != null;
            bool drawFrozenDepth = drawShatter && HasFrozenFrame
                                   && _fractureDepthRenderer != null && _fractureDepthMat != null;
            _mat.SetInt(ZWriteId, drawFrozenDepth ? 1 : 0);
            ConfigureColorMaterial(HasFrozenFrame);
            if (_fractureRenderer != null) _fractureRenderer.enabled = drawShatter;
            if (_fractureDepthRenderer != null) _fractureDepthRenderer.enabled = drawFrozenDepth;
            // 0225: 着地した破片が映像を見せる区間だけ、隙間を黒く塗る面を出す。全面が映像になったら畳む。
            bool drawGaps = drawFrozenDepth && w.reveal > FullyOpenEpsilon && w.live < 0.999f;
            if (_gapsRenderer != null) _gapsRenderer.enabled = drawGaps;
            GapsDrawn = drawGaps;
            if (drawShatter)
            {
                float shatter = Mathf.Clamp01(w.shatter);
                _fractureBlock ??= new MaterialPropertyBlock();
                _fractureBlock.Clear();
                _fractureBlock.SetFloat(ShatterId, shatter);
                _fractureBlock.SetFloat(ScreenFadeId, Mathf.Clamp01(w.live));
                _fractureBlock.SetFloat(RevealId, HasFrozenFrame ? Mathf.Clamp01(w.reveal) : 0f);
                _fractureBlock.SetFloat(HasFrozenFrameId, HasFrozenFrame ? 1f : 0f);
                _fractureBlock.SetVector(VeilSizeId,
                    new Vector4(size.x, size.y, PlaneDistanceResolved, 0f));
                Vector3 screenCenterWorld = transform.TransformPoint(_screenCenter);
                Vector3 screenRightWorld = transform.TransformDirection(_screenRight).normalized;
                Vector3 screenUpWorld = transform.TransformDirection(_screenUp).normalized;
                _fractureBlock.SetVector(ScreenCenterId,
                    new Vector4(screenCenterWorld.x, screenCenterWorld.y, screenCenterWorld.z, 0f));
                _fractureBlock.SetVector(ScreenRightId,
                    new Vector4(screenRightWorld.x, screenRightWorld.y, screenRightWorld.z, 0f));
                _fractureBlock.SetVector(ScreenUpId,
                    new Vector4(screenUpWorld.x, screenUpWorld.y, screenUpWorld.z, 0f));
                _fractureBlock.SetVector(ScreenHalfId,
                    new Vector4(_screenHalf.x, _screenHalf.y, 0f, 0f));
                Vector3 headPosition = transform.position;
                _fractureBlock.SetVector(CurrentHeadPositionId,
                    new Vector4(headPosition.x, headPosition.y, headPosition.z, 1f));
                // 静止画の有無に関わらず、割れ始めに固定した姿勢を渡す（毎フレームの頭を渡さない）。
                _fractureBlock.SetMatrix(CaptureHeadToWorldId, _captureHeadToWorld);
                _fractureBlock.SetMatrix(LeftWorldToUvId, _leftWorldToUv);
                _fractureBlock.SetMatrix(RightWorldToUvId, _rightWorldToUv);
                if (HasFrozenFrame)
                {
                    _fractureBlock.SetTexture(FrozenLeftTexId, _frozenLeft!);
                    _fractureBlock.SetTexture(FrozenRightTexId, _frozenRight!);
                }
                _fractureRenderer!.SetPropertyBlock(_fractureBlock);
                if (_fractureDepthRenderer != null)
                    _fractureDepthRenderer.SetPropertyBlock(_fractureBlock);
                if (shatter > ShatterPeak) ShatterPeak = shatter;
            }
            float close = Mathf.Clamp01(w.frame);
            if (close > FullyOpenEpsilon)
            {
                ApertureDrawn = true;
                if (close > ApertureClosePeak) ApertureClosePeak = close;
            }
            PublishAperture(featherAng);
        }

        /// <summary>
        /// 開口を global へ配る。<b>覆いより後に描く面（封印の箱）が同じ形で切られる</b>ため。
        /// 平面は覆いのローカル空間なので、世界の点は <c>_IntroFrameW2L</c> で移してから見る。
        /// </summary>
        private void PublishAperture(float featherAng)
        {
            Shader.SetGlobalMatrix(GlobalW2LId, transform.worldToLocalMatrix);
            for (int i = 0; i < GlobalPlaneIds.Length; i++)
                Shader.SetGlobalVector(GlobalPlaneIds[i], _planes[i]);
            Shader.SetGlobalFloat(GlobalFeatherId, featherAng);
        }

        /// <summary>開口を「全開」で配り直す。覆いを畳むときに呼ぶ（古い閉じた開口を残さない）。</summary>
        private void PublishApertureOpen()
        {
            Shader.SetGlobalMatrix(GlobalW2LId, transform.worldToLocalMatrix);
            for (int i = 0; i < GlobalPlaneIds.Length; i++)
                Shader.SetGlobalVector(GlobalPlaneIds[i], new Vector4(0f, 0f, -1f, 0f));
            Shader.SetGlobalFloat(GlobalFeatherId, 0.02f);
        }

        /// <summary>覆いの平面までの距離 (m)。テスト・診断用。</summary>
        public float PlaneDistance => distance;

        /// <summary>覆いの面の大きさ (m)。テスト・診断用。</summary>
        public Vector2 PlaneSize => veilSize;

        /// <summary>
        /// そのワールド点が枠の中かを、シェーダと<b>同じ式</b>で返す（テスト・診断用）。
        /// <b>負 = 枠の中 / 0 = 縁 / 正 = 外</b>。単位は「辺の平面からの角度の sin」。
        ///
        /// 「枠がスクリーンに重なっているか」を Play せずに確かめられる。こことシェーダで
        /// 別の式を書くと、卓や Editor では合うのに実機だけずれる（このリポジトリが何度も踏んだ形）。
        /// </summary>
        public float SignedDistance(Vector3 worldPoint, float frameClose)
        {
            BuildFramePlanes(frameClose);
            Vector3 dir = transform.InverseTransformPoint(worldPoint).normalized;
            float m = float.NegativeInfinity;
            for (int i = 0; i < _planes.Length; i++)
                m = Mathf.Max(m, Vector3.Dot(dir, new Vector3(_planes[i].x, _planes[i].y, _planes[i].z)));
            return m;
        }

        /// <summary>いまの枠の縁のぼけ幅（<see cref="SignedDistance"/> と同じ単位）。テスト用。</summary>
        public float FeatherAngle(float frameClose) => BuildFramePlanes(frameClose);

        /// <summary>
        /// そのワールド点が映像へクロスフェードするスクリーン矩形の内側かを返す。
        /// 負 = 内側 / 0 = 縁 / 正 = 外側。
        /// </summary>
        public float ScreenSignedDistance(Vector3 worldPoint)
        {
            BuildFramePlanes(1f);
            Vector3 dir = transform.InverseTransformPoint(worldPoint).normalized;
            float m = float.NegativeInfinity;
            for (int i = 0; i < _screenPlanes.Length; i++)
                m = Mathf.Max(m, Vector3.Dot(dir,
                    new Vector3(_screenPlanes[i].x, _screenPlanes[i].y, _screenPlanes[i].z)));
            return m;
        }

        /// <summary>覆いを完全に外す（本編・終了時）。</summary>
        public void SetHidden()
        {
            if (_renderer != null) _renderer.enabled = false;
            if (_fractureRenderer != null) _fractureRenderer.enabled = false;
            if (_fractureDepthRenderer != null) _fractureDepthRenderer.enabled = false;
            if (_gapsRenderer != null) _gapsRenderer.enabled = false;
            GapsDrawn = false;
            if (_mat != null) _mat.SetFloat(FractureActiveId, 0f);
            if (_mat != null) _mat.SetInt(ZWriteId, 0);
            if (_filter != null && _mesh != null) _filter.sharedMesh = _mesh;
            ReleaseFrozenFrame();
            ReleaseShatterAnchor();
            // 閉じ切った開口を配ったまま去ると、次に箱を出す誰かが**枠の形に切られる**。
            PublishApertureOpen();
        }

        private void OnDisable() => SetHidden();

        private void OnDestroy()
        {
            ReleaseFrozenFrame();
            if (_mat != null) Destroy(_mat);
            if (_fractureMat != null) Destroy(_fractureMat);
            if (_fractureDepthMat != null) Destroy(_fractureDepthMat);
            if (_gapsMat != null) Destroy(_gapsMat);
            if (_mesh != null) Destroy(_mesh);
            if (_fractureMesh != null) Destroy(_fractureMesh);
        }
    }
}
