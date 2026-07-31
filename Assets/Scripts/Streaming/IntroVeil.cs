#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の「覆い」。<b>現実を枠の中へ閉じ込める面</b>。
    ///
    /// 配置は <c>CenterEyeAnchor</c> の子（head-lock）。起動時の黒（<c>StartupFader</c>）と同じ流儀だが、
    /// UI Canvas ではなく <b>Quad + 専用シェーダ</b>にしている — Passthrough Windows 方式が
    /// <c>Blend Zero SrcAlpha</c> という特殊なブレンドを要求するため（UI の Image では書けない）。
    ///
    /// <b>枠は head-lock で常に正面</b>。段 4・段 5 は体験者が静止している前提（段 4 の開始条件で
    /// 頭の角速度を見ている）なので、本編のスクリーン（yaw 緩追従）とのずれは起きない。
    /// ワールド固定にすると、頭を振ったときに覆いの外が見えてしまう。
    ///
    /// 開口の大きさは<b>本編のスクリーンの見かけの角</b>に合わせる。合わせておくと、枠が閉じ切った
    /// 瞬間の枠と、本編で映像が出る枠が同じ大きさになり、「枠を運ぶ」処理が要らない
    /// （計画 2026-07-30_intro-passthrough-to-screen.md §4 の「枠は現れるだけで、既にそこにある」）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IntroVeil : MonoBehaviour
    {
        [Tooltip("カメラからの距離 (m)。near clip より大きく、スクリーンより手前に。")]
        [SerializeField, Min(0.05f)] private float distance = 0.3f;

        [Tooltip("覆いの大きさ (m)。この距離で視界を覆い切るサイズにする。")]
        [SerializeField] private Vector2 veilSize = new(2.0f, 2.0f);

        [Tooltip("閉じ切ったときの開口の半径（uv 中心からの比）。screenAnchor があれば実測で上書きする。")]
        [SerializeField] private Vector2 apertureUv = new(0.42f, 0.24f);

        [Tooltip("本編のスクリーン。開口の大きさをここから逆算する（null なら apertureUv をそのまま使う）。")]
        [SerializeField] private Transform? screenQuad;

        [Tooltip("枠の縁のぼけ。硬い矩形は「UI の窓」に見えるので少しぼかす。")]
        [SerializeField, Range(0.002f, 0.4f)] private float feather = 0.08f;

        [Tooltip("走査線の本数。ScreenComposite の既定と同じ 240 に揃える。")]
        [SerializeField] private float scanlineCount = 240f;

        private static readonly int FrameId = Shader.PropertyToID("_Frame");
        private static readonly int PassthroughId = Shader.PropertyToID("_Passthrough");
        private static readonly int ApertureId = Shader.PropertyToID("_Aperture");
        private static readonly int FeatherId = Shader.PropertyToID("_Feather");
        private static readonly int GrainId = Shader.PropertyToID("_Grain");
        private static readonly int ScanCountId = Shader.PropertyToID("_ScanlineCount");
        private static readonly int GlitchId = Shader.PropertyToID("_Glitch");
        private static readonly int GlitchSeedId = Shader.PropertyToID("_GlitchSeed");

        private MeshRenderer? _renderer;
        private Material? _mat;
        private float _seed;

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

        private void Awake()
        {
            Build();
            SetHidden();
        }

        private void Build()
        {
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

            _renderer = go.AddComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "IntroVeil (runtime)" };
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            // 覆いは常に描く（視錐台カリングで消えると視界に穴が空く）。
            _renderer.allowOcclusionWhenDynamic = false;
        }

        // インスタンスごとに持つ（static で共有すると、片方の Destroy でもう片方のメッシュが消える）。
        private Mesh? _mesh;

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
        /// 開口の半径（uv）を、本編のスクリーンの見かけの角から逆算する。
        /// 同じ見かけ角になる覆い上の幅 = スクリーンの幅 × (覆いの距離 / スクリーンの距離)。
        /// </summary>
        private Vector2 ResolveAperture()
        {
            if (screenQuad == null) return apertureUv;
            var s = screenQuad.lossyScale;
            // ⚠ **眼からスクリーンまでの距離**。`screenQuad.localPosition.magnitude` は
            // 「親（原点の空グループ）からの距離」で、ScreenAnchor が `transform.position` を
            // ワールドで書く以上まったく別の値になる（実測: 真値 2.02m に対し 2.40m ＝ 開口が 16% 小さい）。
            // 開口が小さいと段 4 で「枠の縁とスクリーンの縁の間」に映像の帯が露出し、
            // 「枠は現れるだけで、既にそこにある」という設計が崩れる。この覆いは
            // CenterEyeAnchor 直下なので自分の位置がそのまま眼の位置。
            float dist = Vector3.Distance(transform.position, screenQuad.position);
            if (!(dist > 0.01f) || !(s.x > 0.001f) || !(s.y > 0.001f)) return apertureUv;
            float k = distance / dist;
            float x = s.x * k / Mathf.Max(veilSize.x, 0.001f);
            float y = s.y * k / Mathf.Max(veilSize.y, 0.001f);
            return new Vector2(Mathf.Clamp(x, 0.02f, 1f), Mathf.Clamp(y, 0.02f, 1f));
        }

        /// <summary>導入演出の重みを覆いへ流す。<b>判断はしない</b>（値を書くだけ）。</summary>
        public void Apply(in IntroWeights w)
        {
            if (_renderer == null || _mat == null) return;

            // 何も隠していない状態（枠が開いていて、パススルーも出さない）では描画そのものを止める。
            // 覆いは全画面 1 パスなので、本編中ずっと描くのは無駄。
            bool needed = w.frame < 0.999f || w.passthrough > 0.001f || w.grain > 0.001f || w.glitch > 0.001f;
            if (!needed) { SetHidden(); return; }

            _renderer.enabled = true;
            _seed += Time.unscaledDeltaTime;
            var ap = ResolveAperture();
            _mat.SetFloat(FrameId, Mathf.Clamp01(w.frame));
            _mat.SetFloat(PassthroughId, Mathf.Clamp01(w.passthrough));
            _mat.SetVector(ApertureId, new Vector4(ap.x, ap.y, 0f, 0f));
            _mat.SetFloat(FeatherId, feather);
            _mat.SetFloat(GrainId, Mathf.Clamp01(w.grain));
            _mat.SetFloat(ScanCountId, scanlineCount);
            _mat.SetFloat(GlitchId, Mathf.Clamp01(w.glitch));
            _mat.SetFloat(GlitchSeedId, _seed);
        }

        /// <summary>覆いを完全に外す（本編・終了時）。</summary>
        public void SetHidden()
        {
            if (_renderer != null) _renderer.enabled = false;
        }

        private void OnDisable() => SetHidden();

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
