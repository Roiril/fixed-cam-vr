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

        /// <summary>箱の実体を組めたか。<c>false</c> なら封印は一生出ない（シェーダがビルドから剥がれた）。</summary>
        public bool IsBuilt => _renderer != null;

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
            _renderer.enabled = true;
            AppliedOpacity = a;
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
