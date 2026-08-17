#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>タイトルの直後、体験者を所定の位置まで歩かせる床の誘導</b>（<c>canon/LEDGER.md</c> 0079）。
    /// 東から壁沿いに山形（矢印）が並び、壁の角の近くに同心の弧の輪（指定ポイント）が出る。
    ///
    /// 判断は <see cref="WalkGuideLogic"/>、道筋は <see cref="WalkGuidePath"/>、
    /// 形は <c>WalkGuideArrow.shader</c> / <c>WalkGuideRing.shader</c>。ここは<b>観測と配布</b>だけ。
    ///
    /// ⚠⚠ <b>描く輪と、導入が始まる円は同じもの。</b> <see cref="IntroDirector"/> は誘導が出ている
    /// あいだ <see cref="Arrived"/> だけで段 0 を抜ける（接近・安全網・救済は止まる）。
    /// 別々にすると「指示された所より手前で演出が始まる」＝ 装置の指示が嘘になる。
    ///
    /// ⚠ 座標は course 空間で持ち、<see cref="ShowControlClient.CourseToWorldProvider"/> で毎フレーム
    /// world へ焼く。<b>親子付けでは駄目</b>（CourseFrame は transform を動かさない）。
    /// <b>未登録のあいだは何も描かない</b> — course→world が identity へ落ち、全く違う場所に出る。
    ///
    /// ⚠ 山形は<b>1 メッシュ</b>（quad を並べたもの）。1 個ずつ GameObject にすると
    /// 描画呼び出しが道筋の長さで増える。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WalkGuide : MonoBehaviour
    {
        [Tooltip("layout / course 変換の供給元。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("誘導を出すか。現場で丸ごと切るための逃げ道（既定 ON）。")]
        [SerializeField] private bool guideEnabled = true;

        [Tooltip("線と輪の色。作品の暖色（canon/LEDGER.md 0010）。")]
        [SerializeField] private Color inkColor = new(1f, 0.72f, 0.42f, 1f);

        [Tooltip("床から浮かせる高さ (m)。0 だと実物の床とちらつく。")]
        [SerializeField, Range(0f, 0.05f)] private float liftM = 0.012f;

        /// <summary>山形 1 つの幅 (m)。歩く人の肩幅よりやや広く取ると「道」に見える。</summary>
        public const float ChevronW = 0.36f;

        /// <summary>山形 1 つの奥行き (m)。</summary>
        public const float ChevronL = 0.17f;

        /// <summary>山形の間隔 (m)。</summary>
        public const float ChevronPitchM = 0.20f;

        /// <summary>山形の数の上下限。</summary>
        public const int MinChevrons = 3;
        public const int MaxChevrons = 14;

        /// <summary>
        /// 輪のいちばん外の弧の半径 ÷ 判定の円の半径。
        /// ⚠ <c>WalkGuideRing.shader</c> のいちばん外の弧（1.34 ＋ 幅）を覆う値。
        /// これを使って山形の開始位置を決める — <b>判定の円だけを基準にすると、
        /// 飾りの弧の上に山形が乗る</b>（実測で乗った）。
        /// </summary>
        public const float RingOuterK = 1.40f;

        /// <summary>いちばん手前の山形を、輪のいちばん外の弧からどれだけ離すか (m)。</summary>
        public const float RingGapM = 0.10f;

        /// <summary>
        /// 輪を描く quad の半幅 ÷ 判定の円の半径。
        /// ⚠ <b><c>WalkGuideRing.shader</c> の <c>QUAD_K</c> と同じ値</b>（片方だけ直すと輪がずれる）。
        /// </summary>
        public const float QuadK = 1.42f;

        /// <summary>⚠ 覆い（<c>IntroVeil</c> 4900）より後・5000 以下（<c>rules/unity-vr.md</c>）。</summary>
        private const int ArrowQueue = 4960;
        private const int RingQueue = 4961;

        public const string ArrowShaderName = "FixedCamVr/WalkGuideArrow";
        public const string RingShaderName = "FixedCamVr/WalkGuideRing";

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int RevealId = Shader.PropertyToID("_Reveal");
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private static readonly int FlowId = Shader.PropertyToID("_Flow");
        private static readonly int SpinId = Shader.PropertyToID("_Spin");
        private static readonly int ArriveId = Shader.PropertyToID("_Arrive");

        private readonly WalkGuideLogic _logic = new WalkGuideLogic();

        private Transform? _root;
        private MeshRenderer? _arrowRenderer;
        private MeshRenderer? _ringRenderer;
        private Material? _arrowMat;
        private Material? _ringMat;
        private Mesh? _arrowMesh;
        private Mesh? _ringMesh;
        private bool _subscribed;
        private bool _dirty = true;
        private bool _warnedShader;
        private bool _warnedNoPath;
        private WalkGuidePath.Path _path;
        private int _chevrons;
        private float _spin;

        /// <summary>いまの段（テレメトリ用）。</summary>
        public WalkGuideStage Stage => _logic.Stage;

        /// <summary>体験者へ「円へ行け」と言い切っているか（<see cref="IntroLogic"/> のゲート）。</summary>
        public bool Directing => _logic.Directing;

        /// <summary>円へ着いたか。</summary>
        public bool Arrived => _logic.Arrived;

        /// <summary>着かないまま諦めたか（＝従来の開始判定へ戻した）。</summary>
        public bool TimedOut => _logic.TimedOut;

        /// <summary>道筋が解けているか（＝誘導を出せるか）。</summary>
        public bool HasPath => _path.valid;

        /// <summary>円の中心（course XZ）。<b>導入の開始判定もここ</b>。</summary>
        public Vector2 Spot => _path.spot;

        /// <summary>円の半径 (m)。<b>描いている輪と同じ</b>。</summary>
        public float RadiusM => _path.radiusM;

        /// <summary>円が卓で著作されたものか（false なら壁の角から導出した）。</summary>
        public bool SpotAuthored => _path.spotAuthored;

        /// <summary>
        /// 実体を組めたか。<b>false なら 1 画素も出ない</b>（シェーダがビルドから剥がれた側の症状。
        /// <c>Unlit/Color</c> と同じ穴 — <c>rules/unity-vr.md</c>）。
        /// </summary>
        public bool IsBuilt => _arrowMat != null && _ringMat != null;

        /// <summary>並べた山形の数（0 なら道筋が短すぎて矢印が出ていない）。</summary>
        public int ChevronCount => _chevrons;

        /// <summary>直近に書いた矢印の濃さ（<b>画に出た側</b>の観測）。</summary>
        public float AppliedArrow { get; private set; }

        /// <summary>直近に書いた輪の濃さ（<b>画に出た側</b>の観測）。</summary>
        public float AppliedRing { get; private set; }

        private void OnEnable()
        {
            ResolveRefs();
            Subscribe();
            _dirty = true;
            Build();
            Hide();
        }

        private void OnDisable()
        {
            Unsubscribe();
            Hide();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            TearDown();
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

        /// <summary>体験 1 回ぶんの状態を落とす（ラン開始・導入のやり直し）。冪等。</summary>
        public void ResetRun()
        {
            _logic.Reset();
            Hide();
        }

        /// <summary>
        /// 誘導を 1 フレーム進める。<paramref name="wanted"/> は呼び出し側（<see cref="IntroDirector"/>）が
        /// 「段 0 ＆ タイトルが閉じた ＆ 被っている ＆ 位置合わせ済み」を AND して渡す。
        /// </summary>
        public void Tick(bool wanted)
        {
            float dt = Time.unscaledDeltaTime;
            if (_dirty) SolvePath();

            bool posValid = false;
            float distM = float.MaxValue;
            var head = showControl?.HeadCourseXZProvider;
            if (head != null && _path.valid)
            {
                Vector2 p = head();
                distM = Vector2.Distance(p, _path.spot);
                posValid = true;
            }

            bool want = wanted && guideEnabled && _path.valid && IsBuilt;
            bool changed = _logic.Tick(new WalkGuideInput
            {
                wanted = want,
                posValid = posValid,
                distM = distM,
                radiusM = _path.radiusM,
                dt = dt,
            });
            if (changed) Debug.Log($"[WalkGuide] 段 {_logic.Stage}"
                                   + (_logic.TimedOut ? "（着かないまま上限を超えたので従来の開始判定へ戻す）" : ""));

            if (!_logic.Active) { Hide(); return; }
            _spin += dt;
            Place(_logic.Weights);
        }

        /// <summary>畳む（本編・終幕・位置合わせ中・中止）。</summary>
        public void Hide()
        {
            AppliedArrow = 0f;
            AppliedRing = 0f;
            if (_arrowRenderer != null) _arrowRenderer.enabled = false;
            if (_ringRenderer != null) _ringRenderer.enabled = false;
        }

        // ---- 道筋 ---------------------------------------------------------------

        private void SolvePath()
        {
            _dirty = false;
            WalkGuidePath.Path next = WalkGuidePath.Solve(showControl?.Layout, showControl?.Room);
            bool same = next.valid == _path.valid
                        && next.hasArrow == _path.hasArrow
                        && (next.spot - _path.spot).sqrMagnitude < 1e-6f
                        && (next.from - _path.from).sqrMagnitude < 1e-6f
                        && Mathf.Approximately(next.radiusM, _path.radiusM)
                        && Mathf.Approximately(next.floorY, _path.floorY);
            _path = next;
            if (!next.valid)
            {
                if (!_warnedNoPath)
                {
                    _warnedNoPath = true;
                    Debug.LogWarning("[WalkGuide] 床も壁の角も開始位置も無いので歩行誘導は出しません"
                                     + "（卓の 🧱 部屋で壁を、または 🎬 開始位置で円を置く）。"
                                     + "導入は従来どおり体験エリアへの接近で始まります");
                }
                _chevrons = 0;
                return;
            }
            _warnedNoPath = false;
            if (!same) BuildMeshes();
        }

        // ---- 実体 ---------------------------------------------------------------

        private void Build()
        {
            if (IsBuilt) return;
            Shader? arrow = Shader.Find(ArrowShaderName);
            Shader? ring = Shader.Find(RingShaderName);
            if (arrow == null || ring == null)
            {
                if (!_warnedShader)
                {
                    _warnedShader = true;
                    Debug.LogWarning($"[WalkGuide] シェーダを引けないので歩行誘導は出しません"
                                     + $"（{ArrowShaderName} / {RingShaderName} が Always Included から外れていないか）");
                }
                return;
            }

            var rootGo = new GameObject("[WalkGuideRoot]");
            rootGo.transform.SetParent(transform, worldPositionStays: false);
            _root = rootGo.transform;

            _arrowMat = new Material(arrow) { name = "WalkGuideArrow (runtime)", renderQueue = ArrowQueue };
            _ringMat = new Material(ring) { name = "WalkGuideRing (runtime)", renderQueue = RingQueue };
            _arrowMat.SetColor(ColorId, inkColor);
            _ringMat.SetColor(ColorId, inkColor);

            _arrowRenderer = MakeSurface(_root, "WalkGuideArrow", _arrowMat);
            _ringRenderer = MakeSurface(_root, "WalkGuideRing", _ringMat);
            BuildMeshes();
        }

        private static MeshRenderer MakeSurface(Transform parent, string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.enabled = false;
            return r;
        }

        /// <summary>
        /// 山形の列と輪のメッシュを、<b>円を原点・進行方向を +Z</b> とするローカル空間で組む。
        /// course→world は毎フレーム root の transform が持つので、<b>位置合わせが変わっても
        /// メッシュは組み直さなくてよい</b>。
        /// </summary>
        private void BuildMeshes()
        {
            if (_arrowRenderer == null || _ringRenderer == null) return;

            // ---- 輪（quad 1 枚）----
            float half = Mathf.Max(0.05f, _path.radiusM * QuadK);
            _ringMesh = ReplaceMesh(_ringMesh, "WalkGuideRing");
            var rv = new List<Vector3>(4)
            {
                new(-half, 0f, -half), new(half, 0f, -half), new(half, 0f, half), new(-half, 0f, half),
            };
            var ruv = new List<Vector2>(4) { new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f) };
            _ringMesh.SetVertices(rv);
            _ringMesh.SetUVs(0, ruv);
            _ringMesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            _ringMesh.RecalculateBounds();
            _ringRenderer.GetComponent<MeshFilter>().sharedMesh = _ringMesh;

            // ---- 山形の列 ----
            _arrowMesh = ReplaceMesh(_arrowMesh, "WalkGuideArrow");
            _chevrons = 0;
            if (_path.hasArrow)
            {
                float len = Vector2.Distance(_path.from, _path.spot);
                float first = _path.radiusM * RingOuterK + RingGapM + ChevronL * 0.5f;
                float last = len;
                int n = Mathf.Clamp(Mathf.RoundToInt((last - first) / ChevronPitchM) + 1,
                                    MinChevrons, MaxChevrons);
                if (last - first < ChevronPitchM) n = MinChevrons;
                var verts = new List<Vector3>(n * 4);
                var uv0 = new List<Vector2>(n * 4);
                var uv1 = new List<Vector2>(n * 4);
                var tris = new List<int>(n * 6);
                float hw = ChevronW * 0.5f, hl = ChevronL * 0.5f;
                for (int i = 0; i < n; i++)
                {
                    float t = n <= 1 ? 0f : i / (float)(n - 1);
                    float dist = Mathf.Lerp(first, Mathf.Max(first + ChevronL, last), t);
                    // s = 円からの正規化距離（シェーダの点き方と流れが読む）。
                    float s = len > 0.01f ? Mathf.Clamp01(dist / len) : 0f;
                    int b = verts.Count;
                    verts.Add(new Vector3(-hw, 0f, -dist - hl));
                    verts.Add(new Vector3(hw, 0f, -dist - hl));
                    verts.Add(new Vector3(hw, 0f, -dist + hl));
                    verts.Add(new Vector3(-hw, 0f, -dist + hl));
                    uv0.Add(new Vector2(0f, 0f)); uv0.Add(new Vector2(1f, 0f));
                    uv0.Add(new Vector2(1f, 1f)); uv0.Add(new Vector2(0f, 1f));
                    for (int k = 0; k < 4; k++) uv1.Add(new Vector2(s, 0f));
                    tris.Add(b + 0); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 0); tris.Add(b + 3); tris.Add(b + 2);
                }
                _arrowMesh.SetVertices(verts);
                _arrowMesh.SetUVs(0, uv0);
                _arrowMesh.SetUVs(1, uv1);
                _arrowMesh.SetTriangles(tris, 0);
                _arrowMesh.RecalculateBounds();
                _chevrons = n;
            }
            _arrowRenderer.GetComponent<MeshFilter>().sharedMesh = _arrowMesh;
        }

        private Mesh ReplaceMesh(Mesh? old, string name)
        {
            if (old != null) { old.Clear(); return old; }
            return new Mesh { name = name };
        }

        // ---- 配布 ---------------------------------------------------------------

        private void Place(in WalkGuideWeights w)
        {
            if (_root == null || _arrowRenderer == null || _ringRenderer == null) return;

            var toWorld = showControl?.CourseToWorldProvider;
            float y = _path.floorY + liftM;
            Vector3 center = toWorld != null ? toWorld(_path.spot, y)
                                             : new Vector3(_path.spot.x, y, _path.spot.y);
            // ⚠ **進行方向は 2 点の差から取る。** course→world の中身（yaw・平行移動・床の高さ）に
            //    依存しないので、位置合わせの規約が変わっても矢印の向きだけが黙ってずれることが無い。
            Vector2 travel = _path.hasArrow ? (_path.spot - _path.from) : new Vector2(0f, 1f);
            if (travel.sqrMagnitude < 1e-6f) travel = new Vector2(0f, 1f);
            travel.Normalize();
            Vector3 ahead = toWorld != null ? toWorld(_path.spot + travel * 0.5f, y)
                                            : center + new Vector3(travel.x, 0f, travel.y) * 0.5f;
            Vector3 fwd = ahead - center;
            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.forward;
            _root.SetPositionAndRotation(center, Quaternion.LookRotation(fwd, Vector3.up));

            float arrow = w.arrow * w.spot;
            AppliedArrow = _chevrons > 0 ? arrow : 0f;
            AppliedRing = w.spot;

            if (_arrowMat != null)
            {
                _arrowMat.SetFloat(RevealId, w.reveal);
                _arrowMat.SetFloat(FadeId, arrow);
                _arrowMat.SetFloat(FlowId, w.flow);
            }
            if (_ringMat != null)
            {
                _ringMat.SetFloat(RevealId, w.ring);
                _ringMat.SetFloat(FadeId, w.spot);
                _ringMat.SetFloat(SpinId, _spin);
                _ringMat.SetFloat(ArriveId, w.arrive);
            }
            _arrowRenderer.enabled = _chevrons > 0 && arrow > 0.001f;
            _ringRenderer.enabled = w.spot > 0.001f;
        }

        private void TearDown()
        {
            if (_root != null) DestroySafe(_root.gameObject);
            _root = null;
            _arrowRenderer = null;
            _ringRenderer = null;
            if (_arrowMat != null) DestroySafe(_arrowMat);
            if (_ringMat != null) DestroySafe(_ringMat);
            if (_arrowMesh != null) DestroySafe(_arrowMesh);
            if (_ringMesh != null) DestroySafe(_ringMesh);
            _arrowMat = null; _ringMat = null; _arrowMesh = null; _ringMesh = null;
        }

        // Editor プレビューから破棄される経路があるので分岐する（IntroStructureWire と同じ）。
        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// <summary>
        /// Editor プレビュー（<c>menu walkguide</c>）から、layout と段を差し込んで 1 コマ焼くための口。
        /// ⚠ <b>実機では呼ばない。</b>
        /// </summary>
        public void PreviewFrame(WalkGuidePath.Path path, in WalkGuideWeights w, float spinSec)
        {
            ResolveRefs();
            Build();
            _path = path;
            _dirty = false;
            BuildMeshes();
            _spin = spinSec;
            Place(w);
        }
    }
}
