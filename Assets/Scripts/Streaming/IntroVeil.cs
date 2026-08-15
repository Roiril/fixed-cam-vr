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
        /// <summary>吸い込み先（スクリーンの上のセル）を現実の窓からカメラ映像へ入れ替える量。</summary>
        private static readonly int ScreenFadeId = Shader.PropertyToID("_ScreenFade");
        private static readonly int VeilSizeId = Shader.PropertyToID("_VeilSize");
        private static readonly int FeatherAngId = Shader.PropertyToID("_FeatherAng");
        private static readonly int[] FramePlaneIds =
        {
            Shader.PropertyToID("_FramePlane0"), Shader.PropertyToID("_FramePlane1"),
            Shader.PropertyToID("_FramePlane2"), Shader.PropertyToID("_FramePlane3"),
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

        private static readonly int GrainId = Shader.PropertyToID("_Grain");
        private static readonly int ScanCountId = Shader.PropertyToID("_ScanlineCount");
        private static readonly int GlitchId = Shader.PropertyToID("_Glitch");
        private static readonly int GlitchSeedId = Shader.PropertyToID("_GlitchSeed");

        // ---- 破砕（段 4）。現実がセルに割れてスクリーンへ入る -----------------------
        // 曲線の数値は `IntroShatterCurve.PushVeil` が配る（マテリアルへ書く場所は 1 箇所だけ）。
        private static readonly int ShatterId = Shader.PropertyToID("_Shatter");

        // 破片の行き先（スクリーン矩形）は **global で配る**。封印の箱も同じ値を読んで、
        // 同じ格子・同じ順番で割れる（片方だけ別の行き先へ飛ぶと 2 つの出来事に見える）。
        private static readonly int GlobalScreenCId = Shader.PropertyToID("_IntroScreenC");
        private static readonly int GlobalScreenRId = Shader.PropertyToID("_IntroScreenR");
        private static readonly int GlobalScreenUId = Shader.PropertyToID("_IntroScreenU");
        private static readonly int GlobalScreenHalfId = Shader.PropertyToID("_IntroScreenHalf");
        private static readonly int GlobalL2WId = Shader.PropertyToID("_IntroFrameL2W");

        private MeshRenderer? _renderer;
        private MeshFilter? _filter;
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

        /// <summary>
        /// 段 4 の破砕で<b>実際にセル格子を張ったか</b>（覆いが畳まれるまで立ちっぱなし）。
        ///
        /// ⚠ 「重みが動いた」ではなく「画に出た」の側の観測。段の遷移は完璧に進んでいるのに
        /// 画には何も出ていなかった、という壊れ方を 2026-07-31 に踏んでいる。
        /// テレメトリが <c>shat=</c> で出し、解析が「破砕が 1 度も張られていない」を名指しする。
        /// </summary>
        public bool ShatterDrawn { get; private set; }

        /// <summary>この段で実際に配った破砕の進みの最大値（0 なら 1 度も割れていない）。</summary>
        public float ShatterPeak { get; private set; }

        /// <summary>張ったセル格子のセル数。<c>0</c> ならメッシュを組めていない ＝ 一生割れない。</summary>
        public int ShatterCells => _cellMesh != null ? IntroVeilShatterMesh.CellCount : 0;

        /// <summary>
        /// 破片の行き先として<b>実際に配った</b>スクリーン矩形（<c>hw,hh,面までの距離,遠さの基準</c>）。
        ///
        /// ⚠ これが無いと「割れなかった」の原因を切り分けられない。破片が動くかどうかは
        /// <c>far = 見かけの隔たり / 遠さの基準</c> 1 本で決まり、基準が大きすぎれば
        /// **全部の破片が「スクリーンのすぐ脇」扱いになって 1 枚も動かない**（2026-08-12 実機で発生）。
        /// C# 側は「重みを配った」までしか知らないので、そこだけ見ると成功に見える。
        /// </summary>
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
            // ⚠ **起動時に組む。** 段 4 で初めて 16,400 頂点を作ると、その 1 フレームだけ
            //    落ちる（継ぎ目の直前なので一番見せたくない場所）。0.4MB 程度なので常時持つ。
            _cellMesh = IntroVeilShatterMesh.Build();
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
        }

        // インスタンスごとに持つ（static で共有すると、片方の Destroy でもう片方のメッシュが消える）。
        private Mesh? _mesh;
        private Mesh? _cellMesh;
        private Transform? _quad;

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
            float k = Mathf.Clamp01(frameClose);

            // 枠が無い段（黒・現実・格下げ・構造）は**厳密に全開**。有限の矩形で近似すると、
            // その矩形の辺がどこかの頭の向きで必ず視界に入る（FullyOpenEpsilon のコメント）。
            if (k <= FullyOpenEpsilon)
            {
                SetPlanesFullyOpen();
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
            // 面をスクリーンと同じ距離へ運び、見かけの大きさ（＝覆う画角）は変えない。
            Vector2 size = PlaceQuad();
            for (int i = 0; i < FramePlaneIds.Length; i++) _mat.SetVector(FramePlaneIds[i], _planes[i]);
            _mat.SetFloat(PassthroughId, Mathf.Clamp01(w.passthrough));
            _mat.SetFloat(ScreenFadeId, Mathf.Clamp01(w.live));
            _mat.SetVector(VeilSizeId, new Vector4(size.x, size.y, PlaneDistanceResolved, 0f));
            _mat.SetFloat(FeatherAngId, featherAng);
            _mat.SetFloat(GrainId, Mathf.Clamp01(w.grain));
            _mat.SetFloat(ScanCountId, scanlineCount);
            _mat.SetFloat(GlitchId, Mathf.Clamp01(w.glitch));
            _mat.SetFloat(GlitchSeedId, _seed);
            ApplyShatter(Mathf.Clamp01(w.shatter), size);
            PublishAperture(featherAng);
        }

        /// <summary>
        /// 破砕（段 4）を配る。<b>覆いのメッシュを差し替えるのはここ 1 箇所だけ</b>。
        ///
        /// ⚠ <c>shatter = 0</c> のときは<b>必ず 1 枚 quad へ戻す</b>。セル格子は 1 セル = 独立した
        /// quad なので、隣り合う辺が浮動小数で 1 ulp ずれると<b>髪の毛ほどの黒い格子</b>が
        /// 現実の上に出る。段 1〜3 でそれが出ると「割れる」という段 4 の合図が先食いされる。
        /// </summary>
        private void ApplyShatter(float shatter, Vector2 veilSizeM)
        {
            if (_mat == null) return;
            // 段 4 の進みは**覆いと箱で分け合う**（パススルーを閉じ切ってから箱を割る）。
            float veilPart = IntroShatterCurve.VeilShatter(shatter);
            IntroShatterCurve.PushVeil(_mat, veilPart);

            if (_filter != null)
            {
                Mesh? want = veilPart > 0f ? _cellMesh : _mesh;
                if (want != null && _filter.sharedMesh != want) _filter.sharedMesh = want;
            }

            // 行き先は**破砕が始まる前から**配る。箱は覆いより後に描かれるが、同じ 1 フレームの
            // 値を読むので、ここで毎フレーム更新しておけば両者がずれない。
            if (!TryResolveScreenRect(out Vector3 c, out Vector3 right, out Vector3 up,
                                      out float hw, out float hh))
            {
                c = new Vector3(0f, 0f, Mathf.Max(PlaneDistanceResolved, 0.01f));
                right = Vector3.right;
                up = Vector3.up;
                hw = c.z * Mathf.Tan(Mathf.Clamp(fallbackApertureHalfAngleDeg.x, 1f, 80f) * Mathf.Deg2Rad);
                hh = c.z * Mathf.Tan(Mathf.Clamp(fallbackApertureHalfAngleDeg.y, 1f, 80f) * Mathf.Deg2Rad);
            }
            float planeZ = Mathf.Max(PlaneDistanceResolved, 0.01f);
            float absorbM = IntroShatterCurve.AbsorbRangeM(hw, hh);
            Shader.SetGlobalVector(GlobalScreenCId, c);
            Shader.SetGlobalVector(GlobalScreenRId, right);
            Shader.SetGlobalVector(GlobalScreenUId, up);
            Shader.SetGlobalVector(GlobalScreenHalfId, new Vector4(hw, hh, planeZ, absorbM));
            ShatterRectDesc = $"{hw:F2},{hh:F2},{planeZ:F2},{absorbM:F2}";
            Shader.SetGlobalMatrix(GlobalL2WId, transform.localToWorldMatrix);

            if (veilPart <= 0f) return;
            ShatterDrawn = _cellMesh != null;
            if (veilPart > ShatterPeak) ShatterPeak = veilPart;
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

        /// <summary>覆いを完全に外す（本編・終了時）。</summary>
        public void SetHidden()
        {
            if (_renderer != null) _renderer.enabled = false;
            // 割れたままのメッシュと進みを残して去らない（次の体験者は割れていない現実から始まる）。
            if (_filter != null && _mesh != null) _filter.sharedMesh = _mesh;
            if (_mat != null) _mat.SetFloat(ShatterId, 0f);
            ShatterDrawn = false;
            ShatterPeak = 0f;
            // 閉じ切った開口を配ったまま去ると、次に箱を出す誰かが**枠の形に切られる**。
            PublishApertureOpen();
        }

        private void OnDisable() => SetHidden();

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            if (_mesh != null) Destroy(_mesh);
            if (_cellMesh != null) Destroy(_cellMesh);
        }
    }
}
