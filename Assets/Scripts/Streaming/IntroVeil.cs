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

        [Tooltip("走査線の本数。ScreenComposite の既定と同じ 240 に揃える。")]
        [SerializeField] private float scanlineCount = 240f;

        private static readonly int PassthroughId = Shader.PropertyToID("_Passthrough");
        private static readonly int VeilSizeId = Shader.PropertyToID("_VeilSize");
        private static readonly int FeatherAngId = Shader.PropertyToID("_FeatherAng");
        private static readonly int[] FramePlaneIds =
        {
            Shader.PropertyToID("_FramePlane0"), Shader.PropertyToID("_FramePlane1"),
            Shader.PropertyToID("_FramePlane2"), Shader.PropertyToID("_FramePlane3"),
        };
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
        /// 枠が開き切っているときの半径 = 眼からの距離 × これ。tan(77.9°) ＝ 旧実装が
        /// 覆いの面いっぱい（1.4 × 半サイズ / 0.3m）に開いていたのと同じ見かけ角。
        /// </summary>
        private const float OpenTan = 4.667f;

        // 枠の 4 辺の平面の法線（この GameObject のローカル空間・**内側で dot(dir, n) < 0**）。
        private readonly Vector4[] _planes = new Vector4[4];

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
            float k = Mathf.Clamp01(frameClose);

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

            Vector3 p0 = c - right * w - up * h;
            Vector3 p1 = c + right * w - up * h;
            Vector3 p2 = c + right * w + up * h;
            Vector3 p3 = c - right * w + up * h;
            if (!TryEdgePlane(p0, p1, c, 0) || !TryEdgePlane(p1, p2, c, 1) ||
                !TryEdgePlane(p2, p3, c, 2) || !TryEdgePlane(p3, p0, c, 3))
            {
                SetPlanesFullyOpen();
            }

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
        private bool TryEdgePlane(Vector3 a, Vector3 b, Vector3 inside, int index)
        {
            Vector3 n = Vector3.Cross(a, b);
            if (n.sqrMagnitude < 1e-12f) return false;   // 辺が眼と一直線 = 枠が退化している
            n.Normalize();
            if (Vector3.Dot(inside, n) > 0f) n = -n;
            _planes[index] = new Vector4(n.x, n.y, n.z, 0f);
            return true;
        }

        // 退化したときは「何も覆わない」に倒す。覆いは全画面 1 パスなので、
        // 判定が壊れた瞬間に視界が真っ黒になる方が危ない。dir.z > 0 は覆いの面の性質から常に成立。
        private void SetPlanesFullyOpen()
        {
            for (int i = 0; i < _planes.Length; i++) _planes[i] = new Vector4(0f, 0f, -1f, 0f);
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
            float featherAng = BuildFramePlanes(w.frame);
            for (int i = 0; i < FramePlaneIds.Length; i++) _mat.SetVector(FramePlaneIds[i], _planes[i]);
            _mat.SetFloat(PassthroughId, Mathf.Clamp01(w.passthrough));
            _mat.SetVector(VeilSizeId, new Vector4(veilSize.x, veilSize.y, distance, 0f));
            _mat.SetFloat(FeatherAngId, featherAng);
            _mat.SetFloat(GrainId, Mathf.Clamp01(w.grain));
            _mat.SetFloat(ScanCountId, scanlineCount);
            _mat.SetFloat(GlitchId, Mathf.Clamp01(w.glitch));
            _mat.SetFloat(GlitchSeedId, _seed);
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
