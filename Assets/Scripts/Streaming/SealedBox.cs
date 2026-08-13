#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 封印の箱。<b>体験エリアの外に立っている人から見た、隔離の外側</b>。
    ///
    /// 出どころは <c>.claude/canon/LEDGER.md</c> 0003（「不気味な感じの黒い箱」「ところどころに模様」
    /// 「何かが封印されてそう」）。<b>中から見た同じ境界が <see cref="ContainmentShell"/></b> で、
    /// footprint は <see cref="ContainmentShellLogic.TryFootprint"/> 1 箇所から取る
    /// （別々に持つと「箱の外に立っているのに足元が黒い」ような食い違いが出る）。
    ///
    /// 出るのは<b>段 0（開始待ち）で、体験エリアの外に居るあいだだけ</b>。
    /// 近づくと薄れて消え、そのまま歩いて中へ入れる。開かないと体験者は黒い壁に向かって歩くことになり、
    /// 足元が見えないまま境界をまたぐ（現場では危ない）。
    ///
    /// 実装の流儀は <see cref="ContainmentShell"/> と同じ: 実行時に自前で GameObject を組み、
    /// <see cref="Apply"/> で重み（<see cref="IntroWeights.sealBox"/>）を受け、<see cref="SetHidden"/> で畳む。
    /// <b>見え方の判断はここに無い</b>（重み 1 本で決まる）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SealedBox : MonoBehaviour
    {
        [Tooltip("layout / room の供給元。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("封印の箱を出すか。現場で切り分けるための逃げ道（本番は ON）。")]
        [SerializeField] private bool boxEnabled = true;

        [Tooltip("箱の高さ (m)。実物の壁 (1.8m) より高く、見上げても上端が視界の端に来る程度。")]
        [SerializeField, Min(0.5f)] private float heightM = 2.4f;

        [Tooltip("六角形の大きさ (m)。模様の粗さ。")]
        [SerializeField, Min(0.05f)] private float hexSizeM = 0.45f;

        [Tooltip("線を走る光の強さ。0 で模様だけ、上げるほど光が強く流れる。")]
        [SerializeField, Range(0f, 3f)] private float glowGain = 1.0f;

        /// <summary>シェーダ名。ビルドから剥がれないよう Always Included にも入っている。</summary>
        public const string ShaderName = "FixedCamVr/SealedBox";

        /// <summary>
        /// 床の影のシェーダ名。<b>こちらも Always Included に入れてある</b>
        /// （実行時 <c>Shader.Find</c> だけのシェーダはビルドで剥がれる・2026-07-31 実害）。
        /// </summary>
        public const string ShadowShaderName = "FixedCamVr/SealedBoxShadow";

        /// <summary>
        /// 使い込まれた地の版（<c>Resources.Load</c> のパス）。焼くのは
        /// <c>tools/make-sealbox-tex.py</c>、チャンネルの意味は <c>SealedBox.shader</c> と対。
        /// </summary>
        public const string WearTexPath = "Intro/SealBoxWear";

        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int HexSizeId = Shader.PropertyToID("_HexSizeM");
        private static readonly int GlowGainId = Shader.PropertyToID("_GlowGain");
        private static readonly int BoxSizeId = Shader.PropertyToID("_BoxSize");
        private static readonly int WearTexId = Shader.PropertyToID("_WearTex");

        // ---- 破砕（段 4）。封印そのものが割れてスクリーンへ入る --------------------
        // 曲線の数値は `IntroShatterCurve.PushBox` が配る（マテリアルへ書く場所は 1 箇所だけ）。
        private static readonly int ShatterId = Shader.PropertyToID("_Shatter");

        // ---- 床の影（`canon/LEDGER.md` 0024）--------------------------------------
        // 形と数値は `SealedBoxShadowLogic` が正（シェーダと**同じ式**を C# 側にも持たせてある）。
        // ⚠ 値は **const**（SerializeField にすると既存シーン YAML に未記載で 0 と読まれ、
        //    影が黙って消える。LongPressSec / TitleScreen の層と同じ手当て）。
        private static readonly int DensityId = Shader.PropertyToID("_Density");
        private static readonly int HalfXZId = Shader.PropertyToID("_HalfXZ");
        private static readonly int SweepId = Shader.PropertyToID("_Sweep");
        private static readonly int QuadSizeId = Shader.PropertyToID("_QuadSizeM");
        private static readonly int QuadCenterId = Shader.PropertyToID("_QuadCenterM");

        private MeshRenderer? _renderer;
        private MeshFilter? _filter;
        private Mesh? _plainMesh;
        private Mesh? _cellMesh;
        private int _cellCount;
        private Vector3 _cellMeshSize;
        private Transform? _box;
        private Material? _mat;
        private bool _subscribed;
        private bool _dirty = true;
        private bool _hasFootprint;
        private Vector2 _half;
        private float _floorY;
        private bool _warnedNoGeometry;

        private Transform? _shadow;
        private MeshRenderer? _shadowRenderer;
        private Material? _shadowMat;
        private Mesh? _shadowMesh;

        /// <summary>箱の実体を組めたか。<c>false</c> なら封印は一生出ない（シェーダがビルドから剥がれた）。</summary>
        public bool IsBuilt => _renderer != null;

        /// <summary>
        /// 床の影の実体を組めたか。<c>false</c> なら影は一生出ない（シェーダがビルドから剥がれた）。
        /// <b>「重みが動いた」ではなく「画に出た」の側の観測</b>（<c>ShowTelemetryHost</c> が読む）。
        /// </summary>
        public bool ShadowIsBuilt => _shadowRenderer != null;

        /// <summary>直近に影へ書いた不透明度 (0..1)。診断・テレメトリ用。</summary>
        public float ShadowAppliedOpacity { get; private set; }

        /// <summary>いま箱が描かれているか。</summary>
        public bool IsActive => _renderer != null && _renderer.enabled;

        /// <summary>直近に書いた不透明度 (0..1)。診断・テレメトリ用。</summary>
        public float AppliedOpacity { get; private set; }

        /// <summary>
        /// この段で実際に配った破砕の進みの最大値。<c>0</c> なら<b>箱は 1 度も割れていない</b>。
        /// ⚠ 「重みが動いた」ではなく「画に出た」の側の観測（2026-07-31 の事故と同じ型を作らない）。
        /// </summary>
        public float ShatterPeak { get; private set; }

        /// <summary>割った破片の数。<c>0</c> なら格子を組めていない ＝ 一生割れない。</summary>
        public int ShatterCells => _cellMesh != null ? _cellCount : 0;

        /// <summary>
        /// いま張っているのが<b>破片の格子か</b>（0 = 1 枚板の Cube のまま）。
        /// ⚠ 進みを配っただけでは画は割れない。板のままなら頂点シェーダは 1 枚を動かすだけになる。
        /// </summary>
        public bool MeshIsCells => _filter != null && _cellMesh != null && _filter.sharedMesh == _cellMesh;

        /// <summary>footprint を解けているか（<c>false</c> なら箱は出しようがない）。</summary>
        public bool HasFootprint
        {
            get
            {
                if (_dirty) Rebuild();
                return _hasFootprint;
            }
        }

        private void Awake()
        {
            ResolveRefs();
            Build();
            SetHidden();
        }

        private void OnEnable()
        {
            ResolveRefs();
            Subscribe();
            _dirty = true;
            SetHidden();
        }

        private void OnDisable()
        {
            Unsubscribe();
            SetHidden();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_mat != null) DestroySafe(_mat);
            _mat = null;
            if (_cellMesh != null) DestroySafe(_cellMesh);
            _cellMesh = null;
            if (_shadowMat != null) DestroySafe(_shadowMat);
            _shadowMat = null;
            if (_shadowMesh != null) DestroySafe(_shadowMesh);
            _shadowMesh = null;
        }

        private void ResolveRefs()
        {
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
        }

        private void Subscribe()
        {
            if (_subscribed || showControl == null) return;
            showControl.LayoutChanged += MarkDirty;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            if (showControl != null) showControl.LayoutChanged -= MarkDirty;
            _subscribed = false;
        }

        private void MarkDirty() => _dirty = true;

        private void Build()
        {
            // ⚠ **影は箱と独立に組む。** 箱のシェーダが剥がれても影だけは出したい、ではなく、
            //    その逆（影のシェーダが剥がれても箱は出す）を成り立たせるため。
            BuildShadow();
            if (_renderer != null) return;
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SealedBox] シェーダ {ShaderName} が見つかりません。封印の箱は出ません。");
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SealedBoxCube";
            go.transform.SetParent(transform, worldPositionStays: false);
            go.layer = gameObject.layer;

            // 物理は要らない。体験者は箱の中へ歩いて入る。
            Collider? col = go.GetComponent<Collider>();
            if (col != null) DestroySafe(col);

            _box = go.transform;
            _filter = go.GetComponent<MeshFilter>();
            _plainMesh = _filter != null ? _filter.sharedMesh : null;
            _renderer = go.GetComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "SealedBox (runtime)" };
            ApplyWearTexture(_mat);
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.allowOcclusionWhenDynamic = false;
            _renderer.enabled = false;
        }

        /// <summary>
        /// 床の影の実体（水平な板 1 枚）を組む。<b>箱とは別の GameObject</b>で、
        /// 描画順も別（影 4915 → 箱 4920）。
        ///
        /// ⚠ <b>姿勢は必ず <c>Euler(90, courseYaw, 0)</c></b>（<see cref="PlaceShadow"/>）。
        /// そう寝かせてあるので<b>板のローカル XY がそのまま箱ローカルの XZ</b> になり、
        /// シェーダへ行列を渡さずに済む。姿勢を変えるなら
        /// <c>SealedBoxShadow.shader</c> の <c>vert</c> も対で直すこと。
        /// </summary>
        private void BuildShadow()
        {
            if (_shadowRenderer != null) return;
            var shader = Shader.Find(ShadowShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SealedBox] シェーダ {ShadowShaderName} が見つかりません。" +
                                 "箱は床に影を落としません（箱が浮いて見える）。" +
                                 "Always Included に入っているか確認すること");
                return;
            }

            var go = new GameObject("SealedBoxShadow");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.layer = gameObject.layer;

            _shadowMesh = BuildUnitQuad();
            go.AddComponent<MeshFilter>().sharedMesh = _shadowMesh;
            _shadowRenderer = go.AddComponent<MeshRenderer>();
            _shadowMat = new Material(shader) { name = "SealedBoxShadow (runtime)" };
            _shadowMat.SetFloat(DensityId, SealedBoxShadowLogic.DefaultDensity);
            _shadowMat.SetFloat("_FeatherM", SealedBoxShadowLogic.FeatherM);
            _shadowMat.SetFloat("_FeatherNear", SealedBoxShadowLogic.FeatherNear);
            _shadowMat.SetFloat("_FeatherFar", SealedBoxShadowLogic.FeatherFar);
            _shadowMat.SetFloat("_FarDensity", SealedBoxShadowLogic.FarDensity);
            _shadowMat.SetFloat("_ContactM", SealedBoxShadowLogic.ContactM);
            _shadowMat.SetFloat("_ContactGain", SealedBoxShadowLogic.ContactGain);
            _shadowRenderer.sharedMaterial = _shadowMat;
            _shadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _shadowRenderer.receiveShadows = false;
            _shadowRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _shadowRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _shadowRenderer.allowOcclusionWhenDynamic = false;
            _shadowRenderer.enabled = false;
            _shadow = go.transform;
        }

        /// <summary>ローカル XY が [-0.5, 0.5] の板。<c>positionOS.xy</c> をそのまま座標に使う。</summary>
        private static Mesh BuildUnitQuad()
        {
            var m = new Mesh { name = "SealedBoxShadowQuad" };
            m.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            });
            m.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// 使い込まれた地の版をマテリアルへ当てる。<b>実行時とプレビューの両方がここを通る</b>
        /// （プレビューは自前でマテリアルを作るので、片方だけに書くと「Editor では地があるのに
        /// 実機はのっぺり」あるいはその逆になり、絵を見ても気づけない）。
        ///
        /// ⚠ 版が見つからなくても落とさない。シェーダの既定が <c>"gray"</c> なので、
        /// のっぺりした面に縮退するだけ。ただし<b>黙らない</b> — 質感が丸ごと消えた状態は
        /// 実機の暗い画では「そういうもの」に見えてしまう（2026-07-31 と同じ型の事故）。
        /// </summary>
        public static void ApplyWearTexture(Material mat)
        {
            var tex = Resources.Load<Texture2D>(WearTexPath);
            if (tex == null)
            {
                Debug.LogWarning($"[SealedBox] 地の版 Resources/{WearTexPath} が見つかりません。" +
                                 "封印の箱はのっぺりした面になります（tools/make-sealbox-tex.py で焼く）");
                return;
            }
            mat.SetTexture(WearTexId, tex);
        }

        /// <summary>導入の重みを箱へ流す。<b>判断はしない</b>（値を書くだけ）。</summary>
        public void Apply(in IntroWeights w)
        {
            float a = boxEnabled ? Mathf.Clamp01(w.sealBox) : 0f;
            if (_renderer == null || _mat == null || _box == null || a <= 0.002f) { SetHidden(); return; }

            if (_dirty) Rebuild();
            if (!_hasFootprint)
            {
                if (!_warnedNoGeometry)
                {
                    _warnedNoGeometry = true;
                    Debug.LogWarning("[SealedBox] layout に床も部屋も無い → 封印の箱は出ない" +
                                     "（卓の 🧱 部屋 / 較正パネルの床寸法で実測値を入れること）");
                }
                SetHidden();
                return;
            }

            Place();
            _mat.SetFloat(OpacityId, a);
            _mat.SetFloat(HexSizeId, hexSizeM);
            _mat.SetFloat(GlowGainId, glowGain);
            // 模様を「1 枚のシートで巻く」ために実寸が要る（周長で六角の周期を丸める）。
            var sizeM = new Vector3(_half.x * 2f, heightM, _half.y * 2f);
            _mat.SetVector(BoxSizeId, new Vector4(sizeM.x, sizeM.y, sizeM.z, 0f));
            ApplyShatter(Mathf.Clamp01(w.shatter), sizeM);
            // 床の影。**箱と同じ重みで生き死にする**（箱が引けば影も引く）。
            ApplyShadow(a, sizeM);
            _renderer.enabled = true;
            AppliedOpacity = a;
        }

        /// <summary>
        /// 床の影を配る。<b>判断はしない</b>（形は <see cref="SealedBoxShadowLogic"/> が持つ）。
        ///
        /// ⚠ <b>破砕中は影を引く。</b> 箱が割れて飛んでいるのに床の影が矩形のまま残ると、
        /// 「影だけ元の箱の形で置き去り」になる。いまの導入は破砕を使わないが、戻したときに
        /// 気づけない類の破れなので、ここで塞いでおく。
        /// </summary>
        private void ApplyShadow(float boxOpacity, Vector3 sizeM)
        {
            if (_shadowMat == null || _shadow == null || _shadowRenderer == null) return;

            float a = boxOpacity * (1f - Mathf.Clamp01(ShatterPeak));
            if (a <= 0.002f) { HideShadow(); return; }

            var half = new Vector2(sizeM.x * 0.5f, sizeM.z * 0.5f);
            Vector2 sweep = SealedBoxShadowLogic.Sweep(
                SealedBoxShadowLogic.DefaultYawDeg, SealedBoxShadowLogic.DefaultElevationDeg, sizeM.y);
            Vector2 quad = SealedBoxShadowLogic.QuadSizeM(half, sweep);
            Vector2 center = SealedBoxShadowLogic.QuadCenterM(sweep);

            PlaceShadow(center, quad);
            _shadowMat.SetFloat(OpacityId, a);
            _shadowMat.SetVector(HalfXZId, new Vector4(half.x, half.y, 0f, 0f));
            _shadowMat.SetVector(SweepId, new Vector4(sweep.x, sweep.y, 0f, 0f));
            _shadowMat.SetVector(QuadSizeId, new Vector4(quad.x, quad.y, 0f, 0f));
            _shadowMat.SetVector(QuadCenterId, new Vector4(center.x, center.y, 0f, 0f));
            _shadowRenderer.enabled = true;
            ShadowAppliedOpacity = a;
        }

        /// <summary>
        /// 影の板を course 空間から world 姿勢へ置き直す。<b>箱と同じ理由で親子付けにしない</b>。
        ///
        /// ⚠ <b>回転は <c>Euler(90, yaw, 0)</c> 固定</b>。Unity の Euler は ZXY 順（R = Ry·Rx·Rz）なので
        /// これは「寝かせてから course の yaw を掛ける」になり、<b>板のローカル XY が箱ローカルの XZ</b>
        /// に一致する。シェーダはその前提で座標を作っている。
        /// </summary>
        private void PlaceShadow(Vector2 centerXZ, Vector2 quadSizeM)
        {
            if (_shadow == null) return;
            _shadow.position = CourseToWorld(centerXZ, _floorY + SealedBoxShadowLogic.LiftM);
            _shadow.rotation = Quaternion.Euler(90f, CourseYawDeg(), 0f);
            _shadow.localScale = new Vector3(quadSizeM.x, quadSizeM.y, 1f);
        }

        private void HideShadow()
        {
            ShadowAppliedOpacity = 0f;
            if (_shadowMat != null) _shadowMat.SetFloat(OpacityId, 0f);
            if (_shadowRenderer != null) _shadowRenderer.enabled = false;
        }

        /// <summary>
        /// 破砕（段 4 の後半）を配る。<b>覆いがパススルーを閉じ切ってから</b>ここが動く
        /// （<see cref="IntroShatterCurve.BoxShatter"/>）。順番の理由は同クラスの注記。
        /// </summary>
        private void ApplyShatter(float shatter, Vector3 sizeM)
        {
            if (_mat == null) return;
            float part = IntroShatterCurve.BoxShatter(shatter);
            IntroShatterCurve.PushBox(_mat, part);
            // ⚠ **格子は箱が出た時点で組む。割れ始めてからでは遅い**（2026-08-12 実測）。
            //    6,720 枚ぶんのメッシュを段 4 の 1 フレーム目で作ると、その 2 秒窓の平均が
            //    90fps → 72fps へ落ちた。継ぎ目のすぐ手前でいちばん落としたくない場所。
            EnsureCellMesh(sizeM);

            if (part > 0f)
            {
                if (_filter != null && _cellMesh != null && _filter.sharedMesh != _cellMesh)
                    _filter.sharedMesh = _cellMesh;
                if (part > ShatterPeak) ShatterPeak = part;
                return;
            }
            // ⚠ 割れていない間は 1 枚板の Cube に戻す。段 0 は 15 秒以上あり、そのあいだ
            //    27,000 頂点を毎フレーム流す理由が無い（破片は段 4 の 1.6 秒しか要らない）。
            if (_filter != null && _plainMesh != null && _filter.sharedMesh != _plainMesh)
                _filter.sharedMesh = _plainMesh;
        }

        /// <summary>
        /// 破片の格子を実寸から組む。<b>寸法が変わったときだけ組み直す</b>
        /// （layout が届くまで既定値で走るので、届いた時点で 1 回だけ作り直る）。
        /// </summary>
        private void EnsureCellMesh(Vector3 sizeM)
        {
            if (_cellMesh != null && (_cellMeshSize - sizeM).sqrMagnitude < 1e-6f) return;
            if (_cellMesh != null) DestroySafe(_cellMesh);
            _cellMesh = SealedBoxShatterMesh.Build(sizeM, out _cellCount);
            _cellMeshSize = sizeM;
        }

        /// <summary>箱を消す（中に入った・段 1 以降・本編）。</summary>
        public void SetHidden()
        {
            AppliedOpacity = 0f;
            ShatterPeak = 0f;
            HideShadow();
            if (_mat != null) _mat.SetFloat(ShatterId, 0f);
            if (_filter != null && _plainMesh != null) _filter.sharedMesh = _plainMesh;
            if (_renderer != null) _renderer.enabled = false;
        }

        private void Rebuild()
        {
            _dirty = false;
            _warnedNoGeometry = false;
            _hasFootprint = ContainmentShellLogic.TryFootprint(
                showControl?.Layout, showControl?.Room, out _half);
            _floorY = showControl?.Room != null ? showControl.Room.floorY : 0f;
        }

        /// <summary>
        /// 箱を course 空間から world 姿勢へ置き直す。<b>親子付けでは駄目</b>
        /// （CourseFrame は transform を動かさず originXZ / yawDeg / 床の高さを数値で持つ）。
        /// </summary>
        private void Place()
        {
            if (_box == null) return;
            _box.position = CourseToWorld(Vector2.zero, _floorY + heightM * 0.5f);
            _box.rotation = Quaternion.Euler(0f, CourseYawDeg(), 0f);
            _box.localScale = new Vector3(_half.x * 2f, heightM, _half.y * 2f);
        }

        private Vector3 CourseToWorld(Vector2 xz, float heightAboveFloor)
        {
            var f = showControl?.CourseToWorldProvider;
            return f != null ? f(xz, heightAboveFloor) : new Vector3(xz.x, heightAboveFloor, xz.y);
        }

        private float CourseYawDeg()
        {
            var f = showControl?.CourseYawProvider;
            return f != null ? f() : 0f;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
