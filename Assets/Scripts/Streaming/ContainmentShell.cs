#nullable enable

using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 隔離殻。<b>現実のうち「見てよいもの」以外を黒で落とす面</b>。
    ///
    /// 世界観の出どころは <c>.claude/canon/LEDGER.md</c> 0002（怪異調査員・呪われた壁・隔離）。
    /// 画としてやることは 1 つだけで、<b>会場（人・机・天井・他の展示）を視界から消し、
    /// 実物の壁と足元の床だけを黒の中に残す</b>。
    ///
    /// ⚠ <b>パススルーには深度が無い。</b>「境界の向こうだけ隠す」は screen-space では原理的に書けない
    /// （手前の実物と奥の会場は同じ画素に重なっていて、区別する情報がフレームバッファに無い）。
    /// だから隠す / 残すは<b>著作した幾何</b>で決める:
    ///   1. <c>layout.room</c>（実物の壁）と <c>layout.floor</c>（足元）の箱を
    ///      <c>ContainmentShellMask</c> で描く。色も深度も書かず、<b>ステンシルに 1 を置くだけ</b>
    ///   2. その外側を <c>ContainmentShell</c> の全画面 1 パスが黒で塗る（ステンシル NotEqual）
    /// 「見える範囲」＝箱の投影された形。眼ごとの投影も縁の MSAA も Unity 側が面倒を見る。
    ///
    /// ⚠ <b>全画面で視線 × 箱の交差を解いてはいけない</b>（2026-08-10 実測）。最初はそう書いたが、
    /// 除算が画素あたり数十回入って導入が <b>90fps → 39fps</b> に落ちた
    /// （`logs/capture/20260810_161524`）。箱をラスタライズすれば同じ形がほぼ無料で出る。
    ///
    /// <b>捏造はしない。</b> 幾何が無ければ殻ごと出さない（<see cref="IntroStructureWire"/> と同じ方針）。
    /// 1.8m 四方の既定値で黒を落とすと、体験者は自分の足元が黒い床の上を歩くことになる。
    ///
    /// 実装の流儀は <see cref="IntroVeil"/> / <see cref="IntroStructureWire"/> と同じ:
    /// 実行時に自前で GameObject を組み、<see cref="Apply"/> で重み（<see cref="IntroWeights.shell"/>）を
    /// 受け、<see cref="SetHidden"/> で畳む。<b>見え方の判断はここに無い</b>（重み 1 本で決まる）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ContainmentShell : MonoBehaviour
    {
        [Tooltip("layout / room の供給元。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("隔離殻を出すか。現場で切り分けるための逃げ道（本番は ON）。")]
        [SerializeField] private bool shellEnabled = true;

        [Tooltip("面までの距離 (m)。判定は世界空間の視線で行うので、ここは視界を覆い切るためだけの値。")]
        [SerializeField, Min(0.05f)] private float distance = 0.3f;

        [Tooltip("面の大きさ (m)。この距離で視界の四隅まで覆い切るサイズにする。")]
        [SerializeField] private Vector2 planeSize = new(2.0f, 2.0f);

        private static readonly int StrengthId = Shader.PropertyToID("_Strength");

        /// <summary>黒を塗る面のシェーダ名。ビルドから剥がれないよう Always Included にも入っている。</summary>
        public const string ShaderName = "FixedCamVr/ContainmentShell";

        /// <summary>「見てよいもの」の印を立てるシェーダ名（色も深度も書かず、ステンシルだけ）。</summary>
        public const string MaskShaderName = "FixedCamVr/ContainmentShellMask";

        private MeshRenderer? _renderer;
        private Material? _mat;
        private Material? _maskMat;
        private Mesh? _mesh;
        private Transform? _maskRoot;
        private readonly List<Transform> _maskBoxes = new();
        private bool _subscribed;
        private bool _dirty = true;
        private bool _warnedUnregistered;
        private bool _warnedNoGeometry;

        private List<ContainmentShellLogic.Box> _course = new();

        /// <summary>
        /// 殻の実体（面 + マテリアル）を組めたか。<b>false なら隔離は一生出ない</b> —
        /// <see cref="ShaderName"/> がビルドから剥がれた状態（2026-07-31 に覆いで踏んだ形）。
        /// テレメトリが <c>shellBuilt=</c> で出す。
        /// </summary>
        public bool IsBuilt => _renderer != null;

        /// <summary>いま黒を書いているか（＝隔離が画に出ているか）。</summary>
        public bool IsActive => _renderer != null && _renderer.enabled;

        /// <summary>直近に書いた強さ (0..1)。診断・テレメトリ用。</summary>
        public float AppliedStrength { get; private set; }

        /// <summary>許された箱の数。<b>0 なら幾何が無い</b>＝ 隔離は出せない。</summary>
        public int BoxCount => _course.Count;

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
            if (_maskMat != null) DestroySafe(_maskMat);
            if (_mesh != null) DestroySafe(_mesh);
            _mat = null;
            _maskMat = null;
            _mesh = null;
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
                // 出せないなら出さない。黒い板を全画面に出す方が危ない。
                Debug.LogWarning($"[ContainmentShell] シェーダ {ShaderName} が見つかりません。隔離は出ません。");
                return;
            }

            var go = new GameObject("ContainmentShellQuad");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, distance);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(planeSize.x, planeSize.y, 1f);

            _mesh = BuildQuad();
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = go.AddComponent<MeshRenderer>();
            _mat = new Material(shader) { name = "ContainmentShell (runtime)" };
            _renderer.sharedMaterial = _mat;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.allowOcclusionWhenDynamic = false;

            // 「見てよいもの」の印を立てるマテリアル。**これが無ければ殻ごと出さない** —
            // 印が 1 つも立たない状態で黒だけ塗ると、視界が丸ごと黒に落ちる。
            var maskShader = Shader.Find(MaskShaderName);
            if (maskShader == null)
            {
                Debug.LogWarning($"[ContainmentShell] シェーダ {MaskShaderName} が見つかりません。隔離は出ません。");
                DestroySafe(_mat);
                DestroySafe(go);
                _mat = null;
                _renderer = null;
                return;
            }
            _maskMat = new Material(maskShader) { name = "ContainmentShellMask (runtime)" };
        }

        /// <summary>
        /// 印の箱を必要な数だけ用意する。<see cref="ShowRoomProxy"/> と同じ作法
        /// （Cube プリミティブ・コライダは外す・毎フレーム world 姿勢を置き直す）。
        /// </summary>
        private void EnsureMaskBoxes(int need)
        {
            if (need > 0 && _maskMat == null) need = 0;

            while (_maskBoxes.Count > need)
            {
                int last = _maskBoxes.Count - 1;
                Transform t = _maskBoxes[last];
                _maskBoxes.RemoveAt(last);
                if (t != null) DestroySafe(t.gameObject);
            }
            while (_maskBoxes.Count < need) _maskBoxes.Add(CreateMaskBox(_maskBoxes.Count));
        }

        private Transform CreateMaskBox(int index)
        {
            if (_maskRoot == null)
            {
                var root = new GameObject("[ContainmentShellMask]");
                root.transform.SetParent(transform, worldPositionStays: false);
                _maskRoot = root.transform;
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"ShellAllow{index}";
            go.transform.SetParent(_maskRoot, worldPositionStays: false);
            go.layer = gameObject.layer;

            // 物理は要らない。付いたままだと体験者のコライダや raycast に引っかかる。
            Collider? col = go.GetComponent<Collider>();
            if (col != null) DestroySafe(col);

            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _maskMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            r.enabled = false;
            return go.transform;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "ContainmentShellQuad" };
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
            // 面はカメラに張り付いて動くので、視錐台カリングで消えないよう十分大きく取る。
            m.bounds = new Bounds(Vector3.zero, new Vector3(10f, 10f, 10f));
            return m;
        }

        /// <summary>導入 / 終幕の重みを殻へ流す。<b>判断はしない</b>（値を書くだけ）。</summary>
        public void Apply(in IntroWeights w)
        {
            float s = shellEnabled ? Mathf.Clamp01(w.shell) : 0f;
            if (_renderer == null || _mat == null || s <= 0.002f) { SetHidden(); return; }

            // 未登録のまま出すと course→world が identity へ落ち、**現実の全然違う所を隠す**。
            // 出さない側へ倒す（登録が入れば次フレームから出る）。
            if (!IsCourseRegistered())
            {
                if (!_warnedUnregistered)
                {
                    _warnedUnregistered = true;
                    Debug.LogWarning("[ContainmentShell] HMD 位置合わせが未完了 → 隔離を出さない（登録すると出る）");
                }
                SetHidden();
                return;
            }
            _warnedUnregistered = false;

            if (_dirty) Rebuild();
            if (_course.Count == 0)
            {
                if (!_warnedNoGeometry)
                {
                    _warnedNoGeometry = true;
                    Debug.LogWarning("[ContainmentShell] layout に床も部屋も無い → 隔離は出ない" +
                                     "（卓の 🧱 部屋 / 較正パネルの床寸法で実測値を入れること）");
                }
                SetHidden();
                return;
            }

            PlaceMaskBoxes();
            _mat.SetFloat(StrengthId, s);
            _renderer.enabled = true;
            AppliedStrength = s;
        }

        /// <summary>殻を完全に外す（本編・終了時・幾何が無いとき）。</summary>
        public void SetHidden()
        {
            AppliedStrength = 0f;
            if (_renderer != null) _renderer.enabled = false;
            // 印も一緒に畳む。残すとステンシルだけ立って、次に黒を塗る誰かの穴になる。
            for (int i = 0; i < _maskBoxes.Count; i++)
            {
                Transform t = _maskBoxes[i];
                if (t == null) continue;
                var r = t.GetComponent<MeshRenderer>();
                if (r != null) r.enabled = false;
            }
        }

        private void Rebuild()
        {
            _dirty = false;
            _warnedNoGeometry = false;
            _course = ContainmentShellLogic.Build(showControl?.Layout, showControl?.Room);
            EnsureMaskBoxes(_course.Count);
        }

        /// <summary>
        /// 印の箱を course 空間から world 姿勢へ置き直す。<b>親子付けでは駄目</b>
        /// （CourseFrame は transform を動かさず originXZ / yawDeg / 床の高さを数値で持つ）ので、
        /// 登録のやり直し・OS recenter に追従させるため毎フレーム置く（数個の Transform 書き込み）。
        /// </summary>
        private void PlaceMaskBoxes()
        {
            float courseYaw = CourseYawDeg();
            int n = Mathf.Min(_maskBoxes.Count, _course.Count);
            for (int i = 0; i < n; i++)
            {
                Transform t = _maskBoxes[i];
                if (t == null) continue;
                ContainmentShellLogic.Box b = _course[i];
                t.position = CourseToWorld(new Vector2(b.center.x, b.center.z), b.center.y);
                t.rotation = Quaternion.Euler(0f, courseYaw + b.yawDeg, 0f);
                t.localScale = b.half * 2f;
                var r = t.GetComponent<MeshRenderer>();
                if (r != null) r.enabled = true;
            }
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

        private bool IsCourseRegistered()
        {
            var f = showControl?.CourseRegisteredProvider;
            return f == null || f();   // 供給元が居ない（Editor プレビュー等）なら止めない
        }

        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }

    /// <summary>
    /// <see cref="ContainmentShell"/> の幾何と交差判定を Unity のシーン API から分離した純ロジック
    /// （<see cref="IntroStructureWireLogic"/> と同じ分け方・EditMode テストで固定する）。
    ///
    /// <see cref="HitsBox"/> / <see cref="IsAuthorized"/> は<b>「見える範囲」の仕様の記述</b>
    /// （眼から出た視線が箱に当たる ⇔ その画素は箱の投影の中）。実機はこの式を走らせず、
    /// 箱をそのままラスタライズしてステンシルで同じ形を作る（そちらの方が桁で速い）。
    /// **幾何の選び方**（床の余白・壁の膨らませ・course の yaw）はここのテストが固定する。
    /// </summary>
    public static class ContainmentShellLogic
    {
        /// <summary>シェーダの <c>SHELL_MAX_BOXES</c> と同じ値。</summary>
        public const int MaxBoxes = 8;

        /// <summary>
        /// 床の矩形へ足す余白 (m)。<c>layout.floor</c> は既定値 (1.8 × 1.8) のまま運用されがちで、
        /// **実際に歩く範囲より狭い**（現行 show.json の通過ラインは z=-1.2 まで伸びている）。
        /// 狭いと歩いた先で足元が黒くなるので、実測が入るまではここで補う（<c>canon/OPEN.md</c> Q7）。
        /// </summary>
        public const float FloorMarginM = 0.6f;

        /// <summary>床の板の厚みの半分 (m)。上面がちょうど床の高さに来るよう中心を下げる。</summary>
        public const float FloorSlabHalfM = 0.03f;

        /// <summary>
        /// 壁・箱を膨らませる量 (m)。位置合わせの残差ぶんだけ黒が実物へ食い込むと「壁が欠けた」に見える。
        /// 少し大きめに許して、**ずれは実物の周りに会場が細く覗く形**へ倒す（そちらの方が壊れて見えない）。
        /// </summary>
        public const float BoxInflateM = 0.08f;

        /// <summary>course 空間の箱 1 個（yaw だけ回る）。</summary>
        public struct Box
        {
            /// <summary>course 空間の中心（y は床からの高さ）。</summary>
            public Vector3 center;
            /// <summary>ローカル半寸（x = 右方向 / y = 上下 / z = 前方向）。</summary>
            public Vector3 half;
            /// <summary>course 空間の yaw（度）。ローカル +X をどちらへ向けるか。</summary>
            public float yawDeg;
        }

        /// <summary>world 空間の箱（軸を明示して持つ）。シェーダへ配る形と 1 対 1。</summary>
        public struct WorldBox
        {
            public Vector3 center;
            public Vector3 right;
            public Vector3 forward;
            public Vector3 half;
        }

        /// <summary>
        /// 「見てよいもの」の箱を全部組む。<b>床の板 → 部屋の壁・箱</b>の順（安定した順序）。
        ///
        /// 床も部屋も未著作なら<b>空を返す</b> — 捏造した 1.8m 四方で黒を落とすと、
        /// 体験者は自分の足元が消えた状態で歩くことになる。
        /// </summary>
        public static List<Box> Build(ShowLayoutDef? layout, ShowRoomDef? room)
        {
            var list = new List<Box>();
            if (!IntroStructureWireLogic.TryFloorExtents(layout, room, out float w, out float d))
                return list;

            float floorY = room != null ? room.floorY : 0f;
            // 足元。上面が床とちょうど一致するよう中心を板の半分だけ下げる
            //（眼が板の中に入ると全視線が「当たり」になって隔離が丸ごと効かなくなる）。
            list.Add(new Box
            {
                center = new Vector3(0f, floorY - FloorSlabHalfM, 0f),
                half = new Vector3(w * 0.5f + FloorMarginM, FloorSlabHalfM, d * 0.5f + FloorMarginM),
                yawDeg = 0f,
            });

            // 実物の壁。**部屋プロキシと同じ幾何**（CG のオクルーダ・段 3 の線と同じ源）を使う。
            // 別経路で組み直すと、人形は壁の裏へ回れるのに隔離だけ壁を無視する、が起きる。
            List<ShowRoomProxyLogic.Box> boxes = ShowRoomProxyLogic.Build(room);
            for (int i = 0; i < boxes.Count && list.Count < MaxBoxes; i++)
            {
                ShowRoomProxyLogic.Box b = boxes[i];
                list.Add(new Box
                {
                    center = b.center,
                    half = new Vector3(b.size.x * 0.5f + BoxInflateM,
                                       b.size.y * 0.5f + BoxInflateM,
                                       b.size.z * 0.5f + BoxInflateM),
                    yawDeg = b.yawDeg,
                });
            }
            return list;
        }

        /// <summary>
        /// course 空間の箱を world 空間へ。回転の規約は <see cref="ShowRoomProxy"/> と同じ
        /// （<c>Quaternion.Euler(0, courseYaw + yaw, 0)</c>）。ここを揃えないと隔離だけ 90° 転ぶ。
        /// </summary>
        public static WorldBox ToWorld(in Box b, System.Func<Vector2, float, Vector3> courseToWorld,
                                       float courseYawDeg)
        {
            Quaternion rot = Quaternion.Euler(0f, courseYawDeg + b.yawDeg, 0f);
            return new WorldBox
            {
                center = courseToWorld(new Vector2(b.center.x, b.center.z), b.center.y),
                right = rot * Vector3.right,
                forward = rot * Vector3.forward,
                half = b.half,
            };
        }

        /// <summary>視線がどれか 1 つの箱に当たるか（＝現実を見せてよい方向か）。</summary>
        public static bool IsAuthorized(Vector3 eye, Vector3 dir, IReadOnlyList<WorldBox> boxes)
        {
            for (int i = 0; i < boxes.Count && i < MaxBoxes; i++)
                if (HitsBox(eye, dir, boxes[i])) return true;
            return false;
        }

        /// <summary>
        /// 視線 × OBB（yaw だけ回る）のスラブ法。<b><c>ContainmentShell.shader</c> の
        /// <c>HitBox</c> と同じ式</b>。眼が箱の中にあるときも当たり扱い（tmin &lt; 0 &lt; tmax）。
        /// </summary>
        public static bool HitsBox(Vector3 eye, Vector3 dir, in WorldBox b)
        {
            Vector3 c = b.center - eye;
            Vector3 up = Vector3.up;
            Vector3 e = new(Vector3.Dot(b.right, c), Vector3.Dot(up, c), Vector3.Dot(b.forward, c));
            Vector3 f = new(Vector3.Dot(b.right, dir), Vector3.Dot(up, dir), Vector3.Dot(b.forward, dir));

            float tmin = -1e6f;
            float tmax = 1e6f;
            for (int k = 0; k < 3; k++)
            {
                float fk = f[k], ek = e[k], hk = b.half[k];
                if (Mathf.Abs(fk) > 1e-6f)
                {
                    float t1 = (ek - hk) / fk;
                    float t2 = (ek + hk) / fk;
                    tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
                    tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));
                }
                else if (Mathf.Abs(ek) > hk)
                {
                    tmax = -1e6f;
                }
            }
            return tmax >= Mathf.Max(tmin, 0f);
        }
    }
}
