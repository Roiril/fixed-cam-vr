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
        [SerializeField, Min(0.05f)] private float hexSizeM = 0.28f;

        [Tooltip("印の付いたセルの割合。0 で模様だけ、上げるほど「何かが居る」感じが増える。")]
        [SerializeField, Range(0f, 0.6f)] private float markDensity = 0.16f;

        /// <summary>シェーダ名。ビルドから剥がれないよう Always Included にも入っている。</summary>
        public const string ShaderName = "FixedCamVr/SealedBox";

        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int HexSizeId = Shader.PropertyToID("_HexSizeM");
        private static readonly int MarkDensityId = Shader.PropertyToID("_MarkDensity");

        private MeshRenderer? _renderer;
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
            _renderer = go.GetComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "SealedBox (runtime)" };
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.allowOcclusionWhenDynamic = false;
            _renderer.enabled = false;
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
            _mat.SetFloat(MarkDensityId, markDensity);
            _renderer.enabled = true;
            AppliedOpacity = a;
        }

        /// <summary>箱を消す（中に入った・段 1 以降・本編）。</summary>
        public void SetHidden()
        {
            AppliedOpacity = 0f;
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
