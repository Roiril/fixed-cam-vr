#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// タイトル画面「廻リ視」。<b>体験が始まる前に、黒の中へ立体文字だけを置く。</b>
    ///
    /// 判定はすべて純ロジック <see cref="TitleLogic"/> にあり、ここは<b>観測と配布</b>だけを持つ
    /// （<see cref="IntroDirector"/> と同じ流儀）。
    ///
    /// 立ち位置は<b>導入の段 0（開始待ち）に被さる薄い層</b>。<c>ShowPhase</c> も
    /// <see cref="IntroStage"/> も増やしていないので、ゲート・終了判定・heartbeat・卓・
    /// シミュレータの分岐はどれも変わらない。
    ///
    /// ⚠ <b>壁を一瞬も見せない担保</b>（依頼の要求）。段 0 では <see cref="IntroDirector"/> が毎フレーム
    /// <see cref="SealedBox.Apply"/> / <see cref="ContainmentShell.Apply"/> を呼んでいるので、
    /// タイトルが立っている間ずっと<b>封印の箱は既に描かれている</b>。タイトルの黒は
    /// 封印の箱（queue 4920）より<b>後</b>（4950）に alpha を 1 へ戻しているだけなので、
    /// alpha を 0 へ戻せばその箱がそのまま現れる ＝ 開ける途中に壁が覗くフレームが構造的に無い。
    /// 念のため <see cref="ConcealReady"/> が立つのを待ってから開くが、
    /// <b>待ちは <see cref="TitleLogic.ConcealWaitMaxSec"/> で必ず切れる</b>（ラッチにしない）。
    ///
    /// ⚠ <b>失敗したら素通しに倒す</b>。シェーダやテクスチャが解決できず
    /// <see cref="IsBuilt"/> が false のときは <see cref="IsBlocking"/> も false になり、
    /// 導入は従来どおり始まる。ここをラッチにすると、タイトルが組めない現場で
    /// <b>体験が二度と始まらない</b>（2026-07-31 のシェーダ剥がれと同型の事故）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TitleScreen : MonoBehaviour
    {
        [Header("References（未配線でも実行時に自己解決する）")]
        [SerializeField] private ShowRunDirector? runDirector;
        [SerializeField] private IntroDirector? introDirector;
        [SerializeField] private SealedBox? sealedBox;
        [SerializeField] private ContainmentShell? shell;
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("頭の Transform。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        [Tooltip("タイトルを出すか。現場で切り分けるための逃げ道（本番は ON）。")]
        [SerializeField] private bool titleEnabled = true;

        [Tooltip("文字までの距離 (m)。")]
        [SerializeField, Min(0.5f)] private float distanceM = 2.0f;

        [Tooltip("「廻リ視」の高さ (m)。距離 2m で 0.58m ＝ 横幅 1.51m ＝ 見かけ 41°。")]
        [SerializeField, Min(0.05f)] private float titleHeightM = 0.58f;

        [Tooltip("視線中心からどれだけ下に置くか (度)。少し見下ろすと厚みの天面が見える。")]
        [SerializeField, Range(-20f, 20f)] private float pitchOffsetDeg = 3.0f;

        [Tooltip("副題 MAWARIMI を出すか。")]
        [SerializeField] private bool showSubtitle = true;

        // ---- 構造の値。**const にする**。SerializeField にすると既存シーン YAML に未記載で
        //      0 と読まれ、層が 0 枚 / 厚み 0 になって「立体文字」が黙って平面になる
        //      （unity-prefab-fields の罠。LongPressSec と同じ手当て）。
        /// <summary>厚みを作る層の枚数。</summary>
        private const int LayerCount = 16;
        /// <summary>厚み (m)。距離 2m・高さ 0.46m に対しての 0.10m は「削り出した塊」に見える比。</summary>
        private const float DepthM = 0.10f;
        /// <summary>黒の面までの距離 (m)。文字より手前に置くと文字を隠すので必ず奥。</summary>
        private const float VeilDistanceM = 0.30f;
        /// <summary>黒の面の大きさ (m)。距離 0.3m でこの大きさなら視界を覆い切る（IntroVeil と同値）。</summary>
        private const float VeilSizeM = 2.0f;

        // ---- 追従。頭にわずかに遅れて付いてくると、頭を振るたびに厚みの側面が覗く。
        //      剛体として遅れるので「板が滑る」のではなく「物が浮いている」に見える。
        private const float FollowRateHz = 5.0f;
        private const float MaxLagDeg = 8.0f;
        // 頭が完全に静止していても厚みが分かるよう、ごく遅い揺らぎを足す（0.3°/s 未満）。
        private const float DriftYawDeg = 2.2f;
        private const float DriftPitchDeg = 1.1f;
        private const float DriftYawSec = 13f;
        private const float DriftPitchSec = 17f;

        // ---- 距離場アトラスの帯。**tools/make-title-sdf.py の TITLE_BAND / SUB_BAND と同じ値**。
        //      片方だけ直すと字が伸びる・切れる（沈黙して食い違う）。px は 1024x512・左上原点。
        private const float TexW = 1024f, TexH = 512f;
        private const float TitleX0 = 32f, TitleY0 = 24f, TitleX1 = 992f, TitleY1 = 392f;
        private const float SubX0 = 192f, SubY0 = 424f, SubX1 = 832f, SubY1 = 472f;

        /// <summary>隠すものが「立っている」とみなす不透明度。</summary>
        private const float ConcealMin = 0.9f;

        public const string VeilShaderName = "FixedCamVr/TitleVeil";
        public const string GlyphShaderName = "FixedCamVr/TitleGlyph";
        public const string SdfResourcePath = "Title/MawarimiTitle";

        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int SdfId = Shader.PropertyToID("_Sdf");
        private static readonly int RevealId = Shader.PropertyToID("_Reveal");
        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int FlashPosId = Shader.PropertyToID("_FlashPos");
        private static readonly int FlashAmtId = Shader.PropertyToID("_FlashAmt");
        private static readonly int PhaseSecId = Shader.PropertyToID("_PhaseSec");

        private readonly TitleLogic _logic = new TitleLogic();

        private Transform? _veilQuad;
        private Transform? _lagRoot;
        private Transform? _glyphRoot;
        private float _glyphYOffset;
        private MeshRenderer? _veilRenderer;
        private MeshRenderer? _glyphRenderer;
        private Material? _veilMat;
        private Material? _glyphMat;
        private Mesh? _veilMesh;
        private Mesh? _glyphMesh;
        private bool _subscribed;
        private bool _dismissRequested;
        private Quaternion _lagRot = Quaternion.identity;
        private bool _lagPrimed;
        private float _driftSec;
        private bool _warnedNotBuilt;

        /// <summary>
        /// 実体（面・文字・マテリアル）を組めたか。<b>false ならタイトルは一生出ない</b> —
        /// シェーダがビルドから剥がれた / 距離場テクスチャが見つからない、のどちらか。
        /// テレメトリが <c>titleBuilt=</c> で出す（「重みが動いた」ではなく「画に出た」の側）。
        /// </summary>
        public bool IsBuilt => _veilRenderer != null && _glyphRenderer != null;

        /// <summary>いま画面を持っているか。<b>組めていないときは必ず false</b>（素通しに倒す）。</summary>
        public bool IsBlocking => IsBuilt && titleEnabled && _logic.Active;

        /// <summary>A を待っているか（実行体が触覚・案内を出す判断に使う）。</summary>
        public bool AwaitingInput => IsBlocking && _logic.AwaitingInput;

        public TitleStage Stage => _logic.Stage;

        /// <summary>直近に書いた黒の不透明度。診断・テレメトリ用。</summary>
        public float AppliedVeil { get; private set; }

        /// <summary>直近に書いた文字の不透明度。診断・テレメトリ用。</summary>
        public float AppliedGlyph { get; private set; }

        private void Awake()
        {
            ResolveRefs();
            Build();
            Hide();
        }

        private void OnEnable()
        {
            ResolveRefs();
            if (runDirector != null && !_subscribed)
            {
                runDirector.PhaseChanged += OnPhaseChanged;
                runDirector.RunRestarted += OnRunRestarted;
                _subscribed = true;
                if (runDirector.Phase == ShowPhase.Intro) BeginTitle();
                else _logic.Disable();
            }
        }

        private void OnDisable()
        {
            if (runDirector != null && _subscribed)
            {
                runDirector.PhaseChanged -= OnPhaseChanged;
                runDirector.RunRestarted -= OnRunRestarted;
            }
            _subscribed = false;
            _logic.Disable();
            Hide();
        }

        private void OnDestroy()
        {
            if (_veilMat != null) DestroySafe(_veilMat);
            if (_glyphMat != null) DestroySafe(_glyphMat);
            if (_veilMesh != null) DestroySafe(_veilMesh);
            if (_glyphMesh != null) DestroySafe(_glyphMesh);
            _veilMat = null; _glyphMat = null; _veilMesh = null; _glyphMesh = null;
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (introDirector == null) introDirector = FindObjectOfType<IntroDirector>();
            if (sealedBox == null) sealedBox = FindObjectOfType<SealedBox>();
            if (shell == null) shell = FindObjectOfType<ContainmentShell>();
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
            }
        }

        private void OnPhaseChanged(ShowPhase phase)
        {
            if (phase == ShowPhase.Intro) BeginTitle();
            else { _logic.Disable(); Hide(); }
        }

        // ▶ ラン開始（体験者交代）。相が Intro → Intro だと PhaseChanged が飛ばないので、
        // ここで出し直さないと次の体験者にタイトルが出ない（IntroDirector と同じ手当て）。
        private void OnRunRestarted()
        {
            if (runDirector != null && runDirector.Phase == ShowPhase.Intro) BeginTitle();
        }

        private void BeginTitle()
        {
            _dismissRequested = false;
            _lagPrimed = false;
            _driftSec = 0f;
            if (!titleEnabled || !IsBuilt)
            {
                if (!IsBuilt && !_warnedNotBuilt)
                {
                    _warnedNotBuilt = true;
                    Debug.LogWarning("[Title] 実体を組めていないのでタイトルは出しません（導入はそのまま始まります）");
                }
                _logic.Disable();
                Hide();
                return;
            }
            _logic.Begin();
        }

        /// <summary>
        /// A（右コントローラ）。<b>閉じる演出へ入れたら true</b> を返す。
        /// false は「タイトルが立っていない・実体を組めていない」で、押下は何にも繋がらない
        /// （Normal での A はこれ 1 つだけ。カメラ手動送りは 2026-08-12 に撤去した）。
        /// </summary>
        public bool RequestDismiss()
        {
            if (!IsBlocking || !_logic.AwaitingInput) return false;
            _dismissRequested = true;
            return true;
        }

        /// <summary>演出なしで畳む（卓の ⏭ 等で導入が段 0 を出たとき）。</summary>
        public void ForceClose()
        {
            _logic.ForceClose();
            Hide();
        }

        private void Update()
        {
            if (_logic.Stage == TitleStage.Off || _logic.Stage == TitleStage.Done)
            {
                _dismissRequested = false;
                return;
            }

            // ⚠ 位置合わせ中は譲る。タイトルの黒は 0.3m・queue 4950 で、
            //    登録ガイダンス（StatusHud・1.6m）より手前かつ後に描かれるので、
            //    譲らないと作業中のスタッフに文字が 1 つも見えない（2026-08-07 の実害と同型）。
            bool registering = showControl?.CourseRegistrationActive ?? false;

            // 導入が段 0 を出てしまったら（卓の ⏭ 等）、タイトルは即座に畳む。
            // **コントローラが死んでいる現場での唯一の出口**なので消さないこと。
            if (!registering && introDirector != null && introDirector.Active
                && introDirector.Stage != IntroStage.Black && _logic.Stage != TitleStage.Out)
            {
                ForceClose();
                return;
            }

            _logic.Tick(Time.unscaledDeltaTime, new TitleInput
            {
                dismissRequested = _dismissRequested,
                concealReady = ConcealReady(),
                suspended = registering,
            });
            _dismissRequested = false;

            if (registering) { Hide(); return; }
            Apply(_logic.Weights);
        }

        private void LateUpdate()
        {
            if (!IsBuilt || !_logic.Active) return;
            FollowHead();
        }

        /// <summary>
        /// 体験エリアを隠すものが実際に立っているか。<b>「重みを配った」ではなく「画に出た」の側</b>を見る。
        /// </summary>
        private bool ConcealReady()
        {
            // 導入が走っていないなら、そもそも覆いが alpha 0 を書いていない ＝ パススルーが出ない。
            // 隠すものは要らないので、待たずに開いてよい。
            if (introDirector == null || !introDirector.Active) return true;
            if (sealedBox != null && sealedBox.IsActive && sealedBox.AppliedOpacity >= ConcealMin) return true;
            if (shell != null && shell.IsActive && shell.AppliedStrength >= ConcealMin && !shell.Revealing) return true;
            return false;
        }

        // ---- 組み立て ------------------------------------------------------------

        private void Build()
        {
            if (_veilRenderer != null) return;

            Shader? veilShader = Shader.Find(VeilShaderName);
            Shader? glyphShader = Shader.Find(GlyphShaderName);
            var sdf = Resources.Load<Texture2D>(SdfResourcePath);
            if (veilShader == null || glyphShader == null || sdf == null)
            {
                Debug.LogWarning(
                    $"[Title] 実体を組めません（veil={(veilShader != null ? 1 : 0)} " +
                    $"glyph={(glyphShader != null ? 1 : 0)} sdf={(sdf != null ? 1 : 0)}）。" +
                    "シェーダは Always Included、距離場は Resources/Title に要る。タイトルは出ません");
                return;
            }

            // 黒の面。IntroVeil と同じ「視界を覆い切るだけ」の板。
            var veilGo = new GameObject("TitleVeilQuad");
            veilGo.transform.SetParent(transform, worldPositionStays: false);
            veilGo.transform.localPosition = new Vector3(0f, 0f, VeilDistanceM);
            veilGo.transform.localRotation = Quaternion.identity;
            veilGo.transform.localScale = new Vector3(VeilSizeM, VeilSizeM, 1f);
            _veilMesh = BuildUnitQuad();
            veilGo.AddComponent<MeshFilter>().sharedMesh = _veilMesh;
            _veilRenderer = veilGo.AddComponent<MeshRenderer>();
            _veilMat = new Material(veilShader) { name = "TitleVeil (runtime)" };
            ConfigureRenderer(_veilRenderer, _veilMat);
            _veilQuad = veilGo.transform;

            // ⚠ 遅れて付いてくるのは**文字だけ**。黒の面まで遅らせると、振り向いた瞬間に
            //    覆いの縁が視界へ入って現実が細く覗く（依頼の「一瞬でも壁が見えてはいけない」に反する）。
            var lagGo = new GameObject("TitleLagRoot");
            lagGo.transform.SetParent(transform, worldPositionStays: false);
            lagGo.transform.localPosition = Vector3.zero;
            lagGo.transform.localRotation = Quaternion.identity;
            _lagRoot = lagGo.transform;

            // 立体文字。層を 1 メッシュに畳むので描画は 1 回で済む。
            var glyphGo = new GameObject("TitleGlyphMesh");
            glyphGo.transform.SetParent(lagGo.transform, worldPositionStays: false);
            glyphGo.transform.localPosition = new Vector3(0f, GlyphYOffset(), Mathf.Max(distanceM, 0.5f));
            glyphGo.transform.localRotation = Quaternion.identity;
            _glyphMesh = BuildGlyphMesh();
            glyphGo.AddComponent<MeshFilter>().sharedMesh = _glyphMesh;
            _glyphRenderer = glyphGo.AddComponent<MeshRenderer>();
            _glyphMat = new Material(glyphShader) { name = "TitleGlyph (runtime)" };
            _glyphMat.SetTexture(SdfId, sdf);
            ConfigureRenderer(_glyphRenderer, _glyphMat);
            _glyphRoot = glyphGo.transform;
        }

        private static void ConfigureRenderer(MeshRenderer r, Material m)
        {
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            // 視錐台カリングで消えると視界に穴が空く（覆いと同じ理由）。
            r.allowOcclusionWhenDynamic = false;
            r.enabled = false;
        }

        /// <summary>
        /// 文字の高さ方向の置き場所 (m)。<b>「廻リ視」の中心が視線から
        /// <see cref="pitchOffsetDeg"/> 度だけ下に来る</b>ように置く。
        ///
        /// 少し見下ろす位置にするのは、<b>真正面からでは押し出しの天面が見えない</b>から
        /// （立体を正面から見ると前面しか見えない）。傾けるのではなく下げるのが正しい —
        /// 傾けると面がこちらを向いて、かえって厚みが隠れる。
        ///
        /// アトラス内で主題は中心より上に焼いてあるので、その分を引く。
        /// </summary>
        private float GlyphYOffset()
        {
            float pxPerM = (TitleY1 - TitleY0) / Mathf.Max(titleHeightM, 0.05f);
            float titleCyInMesh = (TexH * 0.5f - (TitleY0 + TitleY1) * 0.5f) / pxPerM;
            float d = Mathf.Max(distanceM, 0.5f);
            _glyphYOffset = -d * Mathf.Tan(pitchOffsetDeg * Mathf.Deg2Rad) - titleCyInMesh;
            return _glyphYOffset;
        }

        private static Mesh BuildUnitQuad()
        {
            var m = new Mesh { name = "TitleVeilQuad" };
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
        /// 立体文字のメッシュ。同じ字を Z 方向へ <see cref="LayerCount"/> 枚重ねる。
        ///
        /// ⚠ <b>奥の層から先に並べる</b>。半透明は深度で並べ替えられない（ZWrite Off / ZTest Always）ので、
        /// メッシュ内の三角形の順序がそのまま描画順になる。手前から並べると奥の層が上書きして
        /// 「厚み」が裏返る。
        ///
        /// 副題は 1 枚だけ（厚みを付けない）。主題と同じ厚みを持たせると、
        /// 目が主題と副題のどちらを見ればよいか決まらなくなる。
        /// </summary>
        private Mesh BuildGlyphMesh()
        {
            // 距離場アトラスの実寸から world 寸法を導く。**PNG のレイアウトがそのまま出る**ので、
            // 主題と副題の位置関係を C# 側で作り直さない（作り直すと必ず食い違う）。
            float titleHpx = TitleY1 - TitleY0;
            float pxPerM = titleHpx / Mathf.Max(titleHeightM, 0.05f);

            int quads = LayerCount + (showSubtitle ? 1 : 0);
            var verts = new Vector3[quads * 4];
            var uv0 = new Vector2[quads * 4];
            var uv1 = new Vector2[quads * 4];
            var cols = new Color[quads * 4];
            var tris = new int[quads * 6];
            int v = 0, t = 0;

            // 主題。奥（i = LayerCount-1）から手前（0）へ積む。
            for (int i = LayerCount - 1; i >= 0; i--)
            {
                float lt = LayerCount > 1 ? i / (float)(LayerCount - 1) : 0f;
                AddQuad(verts, uv0, uv1, cols, tris, ref v, ref t,
                        TitleX0, TitleY0, TitleX1, TitleY1, pxPerM, lt * DepthM,
                        new Color(lt, 0f, 0f, 1f));
            }
            if (showSubtitle)
            {
                AddQuad(verts, uv0, uv1, cols, tris, ref v, ref t,
                        SubX0, SubY0, SubX1, SubY1, pxPerM, 0f,
                        new Color(0f, 1f, 0f, 1f));
            }

            var m = new Mesh { name = "TitleGlyphMesh" };
            m.SetVertices(verts);
            m.SetUVs(0, uv0);
            m.SetUVs(1, uv1);
            m.SetColors(cols);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// アトラスの帯 1 つを、テクセル比を保った矩形として置く。
        /// 帯の中心はテクスチャの中心を原点として決まる（＝ PNG に焼いた通りの配置）。
        /// </summary>
        private void AddQuad(Vector3[] verts, Vector2[] uv0, Vector2[] uv1, Color[] cols, int[] tris,
                             ref int v, ref int t,
                             float x0, float y0, float x1, float y1, float pxPerM, float z, Color col)
        {
            float w = (x1 - x0) / pxPerM;
            float h = (y1 - y0) / pxPerM;
            // px は左上原点、world は下が -Y。テクスチャ中心からの差で置く。
            float cx = ((x0 + x1) * 0.5f - TexW * 0.5f) / pxPerM;
            float cy = (TexH * 0.5f - (y0 + y1) * 0.5f) / pxPerM;

            int b = v;
            verts[v + 0] = new Vector3(cx - w * 0.5f, cy - h * 0.5f, z);
            verts[v + 1] = new Vector3(cx + w * 0.5f, cy - h * 0.5f, z);
            verts[v + 2] = new Vector3(cx - w * 0.5f, cy + h * 0.5f, z);
            verts[v + 3] = new Vector3(cx + w * 0.5f, cy + h * 0.5f, z);

            // UV は左下原点。焼くとき 1 度だけ上下を反しているので、ここでは v = 1 - y/TexH。
            float u0 = x0 / TexW, u1 = x1 / TexW;
            float vv0 = 1f - y1 / TexH, vv1 = 1f - y0 / TexH;
            uv0[v + 0] = new Vector2(u0, vv0);
            uv0[v + 1] = new Vector2(u1, vv0);
            uv0[v + 2] = new Vector2(u0, vv1);
            uv0[v + 3] = new Vector2(u1, vv1);

            uv1[v + 0] = new Vector2(0f, 0f);
            uv1[v + 1] = new Vector2(1f, 0f);
            uv1[v + 2] = new Vector2(0f, 1f);
            uv1[v + 3] = new Vector2(1f, 1f);

            cols[v + 0] = col; cols[v + 1] = col; cols[v + 2] = col; cols[v + 3] = col;

            tris[t + 0] = b + 0; tris[t + 1] = b + 2; tris[t + 2] = b + 1;
            tris[t + 3] = b + 2; tris[t + 4] = b + 3; tris[t + 5] = b + 1;
            v += 4; t += 6;
        }

        // ---- 配布 ----------------------------------------------------------------

        /// <summary>重みを面へ流す。<b>判断はしない</b>（値を書くだけ）。</summary>
        public void Apply(in TitleWeights w) => Apply(w, 0f);

        /// <summary>Editor プレビュー用。<paramref name="phaseSec"/> で光の走りの時刻を外から与える。</summary>
        public void Apply(in TitleWeights w, float phaseSec)
        {
            if (!IsBuilt) return;
            AppliedVeil = Mathf.Clamp01(w.veil);
            AppliedGlyph = Mathf.Clamp01(w.glyph);

            if (_veilMat != null && _veilRenderer != null)
            {
                _veilMat.SetFloat(OpacityId, AppliedVeil);
                _veilMat.SetFloat(PhaseSecId, phaseSec);
                _veilRenderer.enabled = AppliedVeil > 0.002f;
            }
            if (_glyphMat != null && _glyphRenderer != null && _glyphRoot != null)
            {
                _glyphMat.SetFloat(OpacityId, AppliedGlyph);
                _glyphMat.SetFloat(RevealId, Mathf.Clamp01(w.reveal));
                _glyphMat.SetFloat(DissolveId, Mathf.Clamp01(w.dissolve));
                _glyphMat.SetFloat(FlashPosId, w.flashPos);
                _glyphMat.SetFloat(FlashAmtId, Mathf.Clamp01(w.flashAmt));
                _glyphMat.SetFloat(PhaseSecId, phaseSec);
                _glyphRenderer.enabled = AppliedGlyph > 0.002f;
                // 手前が正の push。local z は奥が正なので符号を反す。
                _glyphRoot.localPosition =
                    new Vector3(0f, _glyphYOffset, Mathf.Max(distanceM, 0.5f) - w.pushM);
            }
        }

        /// <summary>畳む（本編・終了・位置合わせ中）。</summary>
        public void Hide()
        {
            AppliedVeil = 0f;
            AppliedGlyph = 0f;
            if (_veilRenderer != null) _veilRenderer.enabled = false;
            if (_glyphRenderer != null) _glyphRenderer.enabled = false;
        }

        /// <summary>
        /// 頭に<b>わずかに遅れて</b>付いてくる。剛体として遅れるので、頭を振るたびに
        /// 厚みの側面が覗く ＝ 立体であることが動きで分かる。
        /// 遅れは <see cref="MaxLagDeg"/> で必ず頭打ちになるので、視界から出ることは無い。
        /// </summary>
        private void FollowHead()
        {
            if (head == null || _lagRoot == null) return;
            float dt = Time.unscaledDeltaTime;
            Quaternion hr = head.rotation;
            if (!_lagPrimed) { _lagRot = hr; _lagPrimed = true; }
            _lagRot = Quaternion.Slerp(_lagRot, hr, 1f - Mathf.Exp(-FollowRateHz * dt));
            // 遅れは必ず頭打ちにする。上限が無いと、速く振り向いたときに文字が視界から出る。
            if (Quaternion.Angle(_lagRot, hr) > MaxLagDeg)
                _lagRot = Quaternion.RotateTowards(hr, _lagRot, MaxLagDeg);

            // 頭が完全に静止していても厚みが分かるよう、ごく遅い揺らぎを足す。
            _driftSec += dt;
            float dy = DriftYawDeg * Mathf.Sin(_driftSec * Mathf.PI * 2f / DriftYawSec);
            float dp = DriftPitchDeg * Mathf.Sin(_driftSec * Mathf.PI * 2f / DriftPitchSec);

            // 親の中での相対姿勢に直して書く。位置も一緒に回るので
            // 「板が滑る」のではなく「物が浮いている」に見える。
            Quaternion parentRot = _lagRoot.parent != null ? _lagRoot.parent.rotation : Quaternion.identity;
            Quaternion want = _lagRot * Quaternion.Euler(dp, dy, 0f);
            _lagRoot.localRotation = Quaternion.Inverse(parentRot) * want;
            _lagRoot.localPosition = Vector3.zero;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

#if UNITY_EDITOR
        /// <summary>Editor プレビューから実体だけ組ませる（Play しないで絵を出すため）。</summary>
        public void EditorBuild()
        {
            Build();
        }
#endif
    }
}
