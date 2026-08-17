#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>スクリーンの外の黒い背景に、360 度いちめんの目が開く</b>異変（<c>canon/LEDGER.md</c> 0075）。
    ///
    /// 進み方は <see cref="AnomalyEyesLogic"/>（純ロジック・テストあり）、座席表は
    /// <see cref="AnomalyEyesMesh"/>、目の形は <c>FixedCamVr/AnomalyEyes</c> シェーダ。
    /// ここがやるのは <b>3 つだけ</b> — 実体を組む / 頭に付いて回る / 数を材質へ書く。
    ///
    /// <b>出す・出さないはカットが決める</b>（<c>steps[].eyes</c> → <see cref="TakeRunner"/> →
    /// <see cref="Apply"/>）。演出の仕組みへ乗せてあるので、
    /// <list type="bullet">
    ///   <item>報告で消える（<see cref="ShowTakeDef.dismissible"/>・<c>LEDGER</c> 0050）</item>
    ///   <item>区間を出れば畳まれる（<c>policy:"yield"</c>）</item>
    ///   <item>中止・ラン開始・watchdog で必ず落ちる（<c>TakeRunner.ReleaseStepState</c>）</item>
    /// </list>
    /// が<b>ぜんぶ既存の経路のまま効く</b>。新しい掛けっぱなしの状態を作らない
    /// （この codebase は「凍結が解けない」を 4 回踏んでいる）。
    ///
    /// ⚠ <b>群れは頭の位置に付いて動く</b>（向きは付いてこない）。歩いて群れの外へ出られると
    ///   「目の壁」に見えてしまうので位置だけ追う。向きを追うと HUD に見えるので追わない。
    ///
    /// ⚠ <b>描くのは Background+100（1100）</b>。本編のスクリーン（Geometry / 不透明）が後から
    ///   上書きするので、目は<b>スクリーンの外にしか出ない</b>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnomalyEyes : MonoBehaviour
    {
        /// <summary>目のシェーダ名。ビルドから剥がれないよう Always Included にも入れてある。</summary>
        public const string ShaderName = "FixedCamVr/AnomalyEyes";

        [Tooltip("追従する頭（CenterEyeAnchor）。null なら名前で探し、無ければ Camera.main。")]
        [SerializeField] private Transform? head;

        [Tooltip("位置合わせ中に引っ込めるための ShowControlClient。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("目の明るさ。上げすぎると「発光する記号」に見える。")]
        [SerializeField, Range(0f, 2f)] private float gain = 1.0f;

        [Tooltip("瞬きの量（0 = 瞬きしない）。")]
        [SerializeField, Range(0f, 1f)] private float blink = 1f;

        [Tooltip("目の色。作品の色（暖色）へ寄せた白（参考画像の目は白い）。")]
        [SerializeField] private Color color = new(1.0f, 0.93f, 0.84f, 1f);

        private static readonly int BigId = Shader.PropertyToID("_EyeBig");
        private static readonly int FieldId = Shader.PropertyToID("_EyeField");
        private static readonly int SpanId = Shader.PropertyToID("_EyeSpan");
        private static readonly int DensityId = Shader.PropertyToID("_EyeDensity");
        private static readonly int FadeId = Shader.PropertyToID("_EyeFade");
        private static readonly int IntensityId = Shader.PropertyToID("_EyeIntensity");
        private static readonly int TimeId = Shader.PropertyToID("_EyeTime");
        private static readonly int GainId = Shader.PropertyToID("_EyeGain");
        private static readonly int BlinkId = Shader.PropertyToID("_EyeBlink");
        private static readonly int ColorId = Shader.PropertyToID("_EyeColor");

        private readonly AnomalyEyesLogic _logic = new();
        private readonly EyeAnchorLogic _anchor = new();

        private MeshRenderer? _renderer;
        private Material? _mat;
        private Mesh? _mesh;
        private EyeSeat[] _seats = System.Array.Empty<EyeSeat>();
        private float _clock;

        // カットが言った値（毎フレームではなくカットの縁でだけ書き換わる。左右分割と同じ流儀）。
        private float _wanted;

        /// <summary>
        /// 実体（メッシュ + 材質）を組めたか。<b>false なら目は一生出ない</b> —
        /// <see cref="ShaderName"/> がビルドから剥がれた状態（2026-07-31 に覆いで踏んだ形）。
        /// テレメトリが <c>eyesBuilt=</c> で出す。
        /// </summary>
        public bool IsBuilt => _renderer != null;

        /// <summary>いまの段（診断・テレメトリ用）。</summary>
        public EyesStage Stage => _logic.Stage;

        /// <summary>直近に材質へ書いた「残りの目の広がり」0..1。</summary>
        public float AppliedField { get; private set; }

        /// <summary>直近に材質へ書いた「大きい目の開き」0..1。</summary>
        public float AppliedBig { get; private set; }

        /// <summary>直近に材質へ書いた不透明度 0..1（0 なら 1 画素も出ていない）。</summary>
        public float AppliedFade { get; private set; }

        /// <summary>
        /// いま開いている目の数（<b>画に出た側</b>の観測）。
        /// 「カットが指した」ではなく「何個ぶんの目が実際に開いているか」。
        /// </summary>
        public int OpenCount { get; private set; }

        /// <summary>座席の総数（大きい目を含む）。0 なら座席表を組めていない。</summary>
        public int SeatCount => _seats.Length;

        private void Awake()
        {
            Resolve();
            Build();
            Hide();
        }

        private void OnDisable()
        {
            // 掛けっぱなしにしない。次に有効化されたら必ず兆しから始まる。
            _wanted = 0f;
            _logic.Tick(999f, wanted: false, density: 0f);
            _anchor.Reset();
            Hide();
        }

        private void OnDestroy()
        {
            if (_mat != null) DestroySafe(_mat);
            if (_mesh != null) DestroySafe(_mesh);
            _mat = null;
            _mesh = null;
        }

        private void Resolve()
        {
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
                else if (Camera.main != null) head = Camera.main.transform;
            }
            if (showControl == null) showControl = FindObjectOfType<ShowControlClient>();
        }

        private void Build()
        {
            if (_renderer != null) return;
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                // 出せないなら出さない（黒い板や白い面を視界に出す方が危ない）。
                Debug.LogWarning($"[AnomalyEyes] シェーダ {ShaderName} が見つかりません。目の異変は出ません。");
                return;
            }

            _seats = AnomalyEyesMesh.BuildSeats();
            _mesh = AnomalyEyesMesh.Build(_seats);

            var go = new GameObject("AnomalyEyesShell");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = go.AddComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "AnomalyEyes (runtime)" };
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.allowOcclusionWhenDynamic = false;
        }

        /// <summary>
        /// カットが「目の異変を出す」と言った（<c>steps[].eyes</c>）。
        /// <paramref name="eyes"/> は<b>開く目の割合</b> 0..1（0 = 出さない）。大きい目は割合に関係なく必ず出る。
        ///
        /// ⚠ <b>毎フレームではなくカットの縁で呼ばれる</b>（左右分割・人形と同じ）。
        ///   同じ値を続けて言われても<b>進みは巻き戻らない</b>ので、カットをまたいでも 1 つの出来事として続く。
        /// </summary>
        public void Apply(float eyes) => _wanted = Mathf.Clamp01(eyes);

        /// <summary>畳む（演出の終わり・中止・ラン開始）。消えるまでに <see cref="AnomalyEyesLogic.FadeOutSec"/>。</summary>
        public void Release() => _wanted = 0f;

        private void LateUpdate()
        {
            if (head == null) Resolve();

            float dt = Time.unscaledDeltaTime;
            _clock += dt;

            // 位置合わせ中は引っ込める（現実に線を重ねて合わせる作業を邪魔しない — rules/show-design.md）。
            bool registering = showControl != null && showControl.CourseRegistrationActive;
            bool wanted = _wanted > 0f && !registering;

            _logic.Tick(dt, wanted, _wanted);

            if (_logic.Stage == EyesStage.Off) { Hide(); return; }
            if (_renderer == null || _mat == null) return;

            // 群れは頭の**位置**にだけ付いて動く。向きはワールド固定（頭に張り付くと HUD に見える）。
            if (head != null) transform.position = head.position;

            if (_logic.JustStarted) { FaceHead(); _anchor.Reset(); }
            else if (head != null)
            {
                // 大きい目が視界の外に居続けるなら、群れごと向き直す。
                // **開いているのは大きい目 1 つ（＝視界の外）だけ**の間しか回さないので、回転は見えない。
                Vector3 bigWorld = transform.rotation * AnomalyEyesMesh.BigDir;
                float off = Vector3.Angle(head.forward, bigWorld);
                if (_anchor.Tick(dt, off, _logic.AnchorLocked)) FaceHead();
            }

            _mat.SetFloat(BigId, _logic.Big);
            _mat.SetFloat(FieldId, _logic.Field);
            _mat.SetFloat(SpanId, AnomalyEyesLogic.SwarmSpan);
            _mat.SetFloat(DensityId, _logic.Density);
            _mat.SetFloat(FadeId, _logic.Fade);
            _mat.SetFloat(IntensityId, _logic.Intensity);
            _mat.SetFloat(TimeId, _clock);
            _mat.SetFloat(GainId, gain);
            _mat.SetFloat(BlinkId, blink);
            _mat.SetColor(ColorId, color);
            _renderer.enabled = true;

            AppliedBig = _logic.Big;
            AppliedField = _logic.Field;
            AppliedFade = _logic.Fade;
            OpenCount = _seats.Length == 0
                ? 0
                : AnomalyEyesMesh.CountOpen(_seats, _logic.Big, _logic.Field, _logic.Density);
        }

        /// <summary>群れの向きを頭の水平の向きへ合わせる（大きい目が視界の端に来る）。</summary>
        private void FaceHead()
        {
            if (head == null) return;
            Vector3 f = head.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        private void Hide()
        {
            AppliedBig = 0f;
            AppliedField = 0f;
            AppliedFade = 0f;
            OpenCount = 0;
            if (_renderer != null) _renderer.enabled = false;
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
