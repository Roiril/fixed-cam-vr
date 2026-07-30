#nullable enable

using System.Collections.Generic;
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 導入演出の段 3。<b>現実の上に部屋の構造の線とカメラの位置の印を重ねる</b>層。
    ///
    /// 設計の正本は <c>.claude/plans/2026-07-30_intro-passthrough-to-screen.md</c> §4。要点:
    ///   - ここは「自分がどこから見られているか」を知る段で、<b>情報表示ではない</b>。
    ///     だから<b>数字も文字も出さない</b>（出すと較正画面に見える）
    ///   - 線は現実に<b>直接</b>重なる。位置合わせの残差がそのまま目に触れるので、
    ///     この段は同時に「登録がズレていないか」の現地検証にもなる（§8-6）
    ///
    /// <see cref="IntroVeil"/> と同じ流儀で、実行時に自前で GameObject（LineRenderer）を組み、
    /// <see cref="Apply"/> で重み（<see cref="IntroWeights.structure"/>）を受け、<see cref="SetHidden"/> で畳む。
    /// on/off の判断（<c>run.intro.showRoomWire</c> / <c>showCameraMarks</c>）は<b>呼び出し側が持つ</b>ので、
    /// この実装は「重み 1 本で見え方が決まる」形にしてある（SerializeField のトグルは現場の切り分け用）。
    ///
    /// 座標は course 空間で持ち、毎フレーム <see cref="ShowControlClient.CourseToWorldProvider"/> で
    /// world へ焼く。<b>親子付けでは駄目</b>（CourseFrame は transform を動かさず originXZ/yawDeg を数値で持つ）。
    /// <b>未登録のあいだは何も描かない</b> — course→world が identity へ落ち、全く違う場所に線が出るため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IntroStructureWire : MonoBehaviour
    {
        /// <summary>カメラ index を走査する上限。<c>layout.grid</c> のセル文字 '0'..'8' と同じ 9 台。</summary>
        public const int MaxCameraScan = 9;

        [Tooltip("layout / cameras の供給元。null なら実行時に探す。")]
        [SerializeField] private ShowControlClient? showControl;

        [Tooltip("壁・箱・床の線を出すか（呼び出し側の on/off とは別の、現場の切り分け用）。")]
        [SerializeField] private bool showRoomWire = true;

        [Tooltip("カメラの位置の印を出すか。")]
        [SerializeField] private bool showCameraMarks = true;

        [Tooltip("線の太さ (m)。登録リチュアルの検証ワイヤーと同じ 0.01 に揃える。")]
        [SerializeField, Range(0.002f, 0.05f)] private float lineWidth = 0.01f;

        [Tooltip("線の色。薄い寒色 1 色（alpha は structure の重みで最大この値まで）。")]
        [SerializeField] private Color lineColor = new(0.45f, 0.78f, 1f, 0.8f);

        [Tooltip("カメラ姿勢の変化を見に行く間隔 (秒)。0 なら毎フレーム。")]
        [SerializeField, Min(0f)] private float markPollSec = 0.25f;

        private readonly List<LineRenderer> _renderers = new();
        private readonly List<IntroStructureWireLogic.CameraMark> _marks = new();
        private readonly List<IntroStructureWireLogic.CameraMark> _builtMarks = new();
        private List<IntroStructureWireLogic.Polyline> _lines = new();
        private GameObject? _root;
        private Material? _mat;
        private bool _dirty = true;
        private bool _visible;
        private bool _subscribed;
        private float _sinceMarkPoll;
        private bool _warnedUnregistered;
        private bool _warnedNoGeometry;

        /// <summary>いま線を出しているか（＝段 3 の重みが乗っているか）。</summary>
        public bool IsActive => _visible;

        /// <summary>組んである線（ポリライン）の本数。診断・テスト用。</summary>
        public int LineCount => _renderers.Count;

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

        /// <summary>導入演出の重みを線へ流す。<b>判断はしない</b>（重み 1 本で見え方が決まる）。</summary>
        public void Apply(in IntroWeights w)
        {
            float a = Mathf.Clamp01(w.structure);
            if (a <= 0.001f) { SetHidden(); return; }

            // 未登録のまま出すと course→world が identity へ落ち、線が全く違う場所に出る。
            // 「出さない」で止める（黙って嘘の位置に重ねない）。登録が入れば次フレームから出る。
            if (!IsCourseRegistered())
            {
                if (!_warnedUnregistered)
                {
                    _warnedUnregistered = true;
                    Debug.LogWarning("[IntroStructureWire] HMD 位置合わせが未完了 → 構造の線を出さない（登録すると出る）");
                }
                SetHidden();
                return;
            }
            _warnedUnregistered = false;

            PollMarks();
            if (_dirty) Rebuild();

            if (_renderers.Count == 0)
            {
                // 床も部屋も未著作。1.8m 四方を捏造して線を引くと、実物と重ならない線を
                // 「位置合わせのズレ」と誤診させる（較正 UI で同じ捏造を潰したばかり）。
                if (!_warnedNoGeometry)
                {
                    _warnedNoGeometry = true;
                    Debug.LogWarning("[IntroStructureWire] layout に床も部屋も無い → 構造の線は出ない" +
                                     "（卓の 🧱 部屋 / 較正パネルの床寸法で実測値を入れること）");
                }
                SetHidden();
                return;
            }

            Place(a);
        }

        /// <summary>線を完全に消す（段 4 以降・本編・終了時）。</summary>
        public void SetHidden()
        {
            _visible = false;
            for (int i = 0; i < _renderers.Count; i++)
            {
                LineRenderer lr = _renderers[i];
                if (lr != null) lr.enabled = false;
            }
        }

        private bool IsCourseRegistered()
        {
            var f = showControl?.CourseRegisteredProvider;
            return f == null || f();   // 供給元が居ない（Editor プレビュー等）なら止めない
        }

        // ---- カメラの印（姿勢の変化に追従する）----

        // 較正の解 → 概算 pose の順で解く（ShowCgLayer と同じ優先順位）。
        // 較正は「実測した姿勢」そのものなので、人がドラッグした pose より常に上。
        private void PollMarks()
        {
            _sinceMarkPoll += Time.unscaledDeltaTime;
            if (_builtMarks.Count > 0 && _sinceMarkPoll < markPollSec) return;
            _sinceMarkPoll = 0f;

            _marks.Clear();
            if (showControl != null && showCameraMarks)
            {
                for (int i = 0; i < MaxCameraScan; i++)
                {
                    // Try* は範囲外を false で返すので、台数を別途知らずに走査できる。
                    if (showControl.TryGetCameraCalib(i, out ShowCameraCalibDef calib))
                    {
                        _marks.Add(new IntroStructureWireLogic.CameraMark
                        {
                            coursePos = new Vector3(calib.x, calib.y, calib.z),
                            yawDeg = calib.yawDeg,
                            pitchDeg = calib.pitchDeg,
                        });
                    }
                    else if (showControl.TryGetCameraPose(i, out ShowCameraPoseDef pose))
                    {
                        _marks.Add(new IntroStructureWireLogic.CameraMark
                        {
                            coursePos = new Vector3(pose.x, pose.y, pose.z),
                            yawDeg = pose.yawDeg,
                            pitchDeg = pose.pitchDeg,
                        });
                    }
                }
            }

            if (!SameAsBuilt(_marks)) _dirty = true;
        }

        private bool SameAsBuilt(List<IntroStructureWireLogic.CameraMark> now)
        {
            if (now.Count != _builtMarks.Count) return false;
            for (int i = 0; i < now.Count; i++)
                if (!IntroStructureWireLogic.Same(now[i], _builtMarks[i])) return false;
            return true;
        }

        // ---- 幾何の組み立て ----

        private void Rebuild()
        {
            _dirty = false;
            _warnedNoGeometry = false;

            _lines = IntroStructureWireLogic.Build(
                showControl?.Layout,
                showControl?.Room,
                _marks,
                includeRoom: showRoomWire,
                includeCameras: showCameraMarks);

            _builtMarks.Clear();
            _builtMarks.AddRange(_marks);

            EnsureRenderers(_lines.Count);
            for (int i = 0; i < _renderers.Count; i++)
            {
                LineRenderer lr = _renderers[i];
                if (lr == null) continue;
                IntroStructureWireLogic.Polyline p = _lines[i];
                lr.loop = p.loop;
                lr.positionCount = p.points.Length;
            }
        }

        private void EnsureRenderers(int need)
        {
            if (need > 0 && !EnsureMaterial()) need = 0;

            while (_renderers.Count > need)
            {
                int last = _renderers.Count - 1;
                LineRenderer lr = _renderers[last];
                _renderers.RemoveAt(last);
                if (lr != null) DestroySafe(lr.gameObject);
            }
            while (_renderers.Count < need)
                _renderers.Add(CreateLine(_renderers.Count));
        }

        private LineRenderer CreateLine(int index)
        {
            if (_root == null)
            {
                _root = new GameObject("[IntroStructureWire]");
                _root.transform.SetParent(transform, worldPositionStays: false);
            }
            var go = new GameObject($"WireLine_{index}");
            go.transform.SetParent(_root.transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _mat;
            lr.useWorldSpace = true;            // course→world を毎フレーム焼くので world 固定
            lr.widthMultiplier = lineWidth;
            lr.numCapVertices = 2;
            lr.textureMode = LineTextureMode.Stretch;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        // マテリアルは 1 個を全線で共有する（色は 1 色なので、線ごとに複製する理由が無い）。
        private bool EnsureMaterial()
        {
            if (_mat != null) return true;
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogWarning("[IntroStructureWire] シェーダ Sprites/Default が見つからない → 構造の線は出ません");
                return false;
            }
            _mat = new Material(shader) { name = "IntroStructureWire (runtime)" };

            // ⚠ **覆い（IntroVeil）より後に描く。** 覆いは Passthrough Windows 方式で
            //   `Blend Zero SrcAlpha`（結果 rgb = srcAlpha × 背景）を Queue 5000 で全画面に掛ける。
            //   段 2 / 段 3 は枠がまだ開いていて `_Frame=0` → `inside=1` → `alpha = 1 - _Passthrough = 0`
            //   なので、**先に描いた線は rgb ごと 0 に潰される**（＝段 3 が丸ごと画面に出ない）。
            //   線を後に置けば、線の画素だけフレームバッファの alpha が 1 に戻り、
            //   そこだけパススルーが隠れて線が見える。線は細いので実物の視認を妨げない。
            //   2026-07-30 の設計批評で「段 3 は原理的に見えない」と指摘されて判明。ブレンド式から
            //   決まる話なので実機を待たずに直せる。
            _mat.renderQueue = VeilRenderQueue + 100;
            return true;
        }

        /// <summary>覆い（<see cref="IntroVeil"/>）の描画順。線はこれより後に描かなければ潰される。</summary>
        private const int VeilRenderQueue = 5000;   // IntroVeil.shader の "Queue" = "Overlay+1000"

        // ---- 追従（course → world）----

        private void Place(float alpha)
        {
            if (_mat != null)
                _mat.color = new Color(lineColor.r, lineColor.g, lineColor.b, lineColor.a * alpha);

            int n = Mathf.Min(_renderers.Count, _lines.Count);
            for (int i = 0; i < n; i++)
            {
                LineRenderer lr = _renderers[i];
                if (lr == null) continue;
                Vector3[] pts = _lines[i].points;
                if (lr.positionCount != pts.Length) lr.positionCount = pts.Length;
                for (int j = 0; j < pts.Length; j++)
                    lr.SetPosition(j, CourseToWorld(pts[j]));
                lr.widthMultiplier = lineWidth;
                lr.enabled = true;
            }
            _visible = n > 0;
        }

        private Vector3 CourseToWorld(Vector3 course)
        {
            var f = showControl?.CourseToWorldProvider;
            return f != null ? f(new Vector2(course.x, course.z), course.y) : course;
        }

        private void TearDown()
        {
            for (int i = 0; i < _renderers.Count; i++)
                if (_renderers[i] != null) DestroySafe(_renderers[i].gameObject);
            _renderers.Clear();
            if (_root != null) DestroySafe(_root);
            _root = null;
            if (_mat != null) DestroySafe(_mat);
            _mat = null;
            _visible = false;
        }

        // Edit Mode（Editor プレビューツール）から破棄される経路があるため分岐する。
        private static void DestroySafe(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }

    /// <summary>
    /// <see cref="IntroStructureWire"/> の幾何計算を Unity のシーン API から分離した純ロジック
    /// （<see cref="ShowRoomProxyLogic"/> と同じ分け方・EditMode テストで固定する）。
    ///
    /// 出すのは course 空間のポリライン列だけ。course→world 変換と LineRenderer の生成は呼び出し側に残す。
    /// 壁・箱は <see cref="ShowRoomProxyLogic.Build"/> の結果（＝部屋プロキシと<b>同じ幾何</b>）を線に
    /// 起こす。別経路で組み直すと、線と CG のオクルーダがズレて「線は合っているのに人形が壁を抜ける」になる。
    /// </summary>
    public static class IntroStructureWireLogic
    {
        /// <summary>カメラの印の腕の長さ (m)。一辺 = この 2 倍。</summary>
        public const float CameraMarkArmM = 0.06f;

        /// <summary>カメラが向いている方向へ引く線の長さ (m)。</summary>
        public const float CameraMarkDirM = 0.25f;

        /// <summary>床・部屋として成立する最小寸法 (m)。これ未満は「未著作」とみなす。</summary>
        public const float MinExtentM = 0.05f;

        /// <summary>course 空間のポリライン 1 本。</summary>
        public struct Polyline
        {
            /// <summary>course 空間の点列（x, y, z。y は course の絶対高さ）。</summary>
            public Vector3[] points;
            /// <summary>末尾と先頭を閉じるか。</summary>
            public bool loop;
        }

        /// <summary>カメラ 1 台の印（course 空間の位置と向き）。calib / pose のどちらから来てもこの形。</summary>
        public struct CameraMark
        {
            public Vector3 coursePos;
            /// <summary>course +Z を 0 とする水平角。</summary>
            public float yawDeg;
            /// <summary>下向きが負。</summary>
            public float pitchDeg;
        }

        /// <summary>印が実質同じか（再構築の要否判定）。</summary>
        public static bool Same(in CameraMark a, in CameraMark b)
            => (a.coursePos - b.coursePos).sqrMagnitude < 1e-6f
               && Mathf.Abs(a.yawDeg - b.yawDeg) < 0.05f
               && Mathf.Abs(a.pitchDeg - b.pitchDeg) < 0.05f;

        /// <summary>
        /// 段 3 で描く線を全部組む。
        ///
        /// <paramref name="room"/> が未著作なら壁・箱は描かない（床だけ）。床も無ければ<b>何も描かない</b> —
        /// 既定の 1.8m 四方を捏造すると、実物と重ならない線を「位置合わせのズレ」と誤診させる。
        /// </summary>
        public static List<Polyline> Build(ShowLayoutDef? layout, ShowRoomDef? room,
                                           IReadOnlyList<CameraMark>? cameras,
                                           bool includeRoom, bool includeCameras)
        {
            var list = new List<Polyline>();
            if (includeRoom)
            {
                AppendFloor(list, layout, room);
                AppendRoom(list, room);
            }
            if (includeCameras && cameras != null)
                for (int i = 0; i < cameras.Count; i++) AppendCameraMark(list, cameras[i]);
            return list;
        }

        /// <summary>
        /// 床の外周（1 本の閉ループ）。<c>layout.floor</c> を正とし、無ければ <c>layout.room</c> の床寸法へ落ちる
        /// （登録リチュアルのワイヤーと同じ優先順位）。どちらも無ければ何も足さない。
        /// </summary>
        public static void AppendFloor(List<Polyline> into, ShowLayoutDef? layout, ShowRoomDef? room)
        {
            if (!TryFloorExtents(layout, room, out float w, out float d)) return;
            float y = room != null ? room.floorY : 0f;
            float hx = w * 0.5f, hz = d * 0.5f;
            into.Add(new Polyline
            {
                loop = true,
                points = new[]
                {
                    new Vector3(-hx, y, -hz), new Vector3(hx, y, -hz),
                    new Vector3(hx, y, hz), new Vector3(-hx, y, hz),
                },
            });
        }

        /// <summary>床の実寸（course 原点中心）。捏造はしない（無ければ false）。</summary>
        public static bool TryFloorExtents(ShowLayoutDef? layout, ShowRoomDef? room,
                                           out float w, out float d)
        {
            if (layout?.floor != null && layout.floor.w > MinExtentM && layout.floor.d > MinExtentM)
            {
                w = layout.floor.w; d = layout.floor.d;
                return true;
            }
            if (room != null && room.HasData())
            {
                w = room.floorW; d = room.floorD;
                return true;
            }
            w = 0f; d = 0f;
            return false;
        }

        /// <summary>壁と箱の輪郭。<see cref="ShowRoomProxyLogic.Build"/> と同じ幾何を線に起こす。</summary>
        public static void AppendRoom(List<Polyline> into, ShowRoomDef? room)
        {
            if (room == null) return;
            List<ShowRoomProxyLogic.Box> boxes = ShowRoomProxyLogic.Build(room);
            for (int i = 0; i < boxes.Count; i++) AppendBox(into, boxes[i]);
        }

        /// <summary>
        /// 箱 1 個の輪郭 = 床の輪郭（閉ループ）+ 上端の輪郭（閉ループ）+ 縦の稜線 4 本 = 6 本。
        /// 12 本の独立線分にせずポリラインにまとめるのは、LineRenderer が 1 本 = 1 GameObject だから。
        /// </summary>
        public static void AppendBox(List<Polyline> into, in ShowRoomProxyLogic.Box b)
        {
            float hx = b.size.x * 0.5f, hy = b.size.y * 0.5f, hz = b.size.z * 0.5f;

            Vector3 b0 = BoxCorner(b, -hx, -hy, -hz), b1 = BoxCorner(b, hx, -hy, -hz);
            Vector3 b2 = BoxCorner(b, hx, -hy, hz), b3 = BoxCorner(b, -hx, -hy, hz);
            Vector3 t0 = BoxCorner(b, -hx, hy, -hz), t1 = BoxCorner(b, hx, hy, -hz);
            Vector3 t2 = BoxCorner(b, hx, hy, hz), t3 = BoxCorner(b, -hx, hy, hz);

            into.Add(new Polyline { loop = true, points = new[] { b0, b1, b2, b3 } });
            into.Add(new Polyline { loop = true, points = new[] { t0, t1, t2, t3 } });
            into.Add(new Polyline { loop = false, points = new[] { b0, t0 } });
            into.Add(new Polyline { loop = false, points = new[] { b1, t1 } });
            into.Add(new Polyline { loop = false, points = new[] { b2, t2 } });
            into.Add(new Polyline { loop = false, points = new[] { b3, t3 } });
        }

        /// <summary>
        /// 箱ローカル (lx, ly, lz) → course 空間。回転は <see cref="ShowRoomProxyLogic"/> と同じ規約
        /// （<c>Quaternion.Euler(0, yaw, 0) * right == (cos yaw, 0, -sin yaw)</c>）。
        /// ここの符号を落とすと線だけが 90° 転び、プロキシとズレる。
        /// </summary>
        public static Vector3 BoxCorner(in ShowRoomProxyLogic.Box b, float lx, float ly, float lz)
        {
            float yaw = b.yawDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            return new Vector3(
                b.center.x + lx * c + lz * s,
                b.center.y + ly,
                b.center.z - lx * s + lz * c);
        }

        /// <summary>
        /// カメラの印 = 3 軸の十字（一辺 <see cref="CameraMarkArmM"/> × 2）+ 向きの線 1 本 = 4 本。
        /// <b>数字も文字も出さない</b>（出すと較正画面に見える。計画 §4）。
        /// </summary>
        public static void AppendCameraMark(List<Polyline> into, in CameraMark m)
        {
            Vector3 p = m.coursePos;
            float a = CameraMarkArmM;
            into.Add(new Polyline { loop = false, points = new[] { p + Vector3.left * a, p + Vector3.right * a } });
            into.Add(new Polyline { loop = false, points = new[] { p + Vector3.down * a, p + Vector3.up * a } });
            into.Add(new Polyline { loop = false, points = new[] { p + Vector3.back * a, p + Vector3.forward * a } });
            into.Add(new Polyline
            {
                loop = false,
                points = new[] { p, p + Forward(m.yawDeg, m.pitchDeg) * CameraMarkDirM },
            });
        }

        /// <summary>
        /// course 空間でカメラが向いている方向（正規化）。<c>Quaternion.Euler(-pitch, yaw, 0) * forward</c> と
        /// 一致させる（<c>ShowCgLayer.ApplyCameraPose</c> の規約。ここが食い違うと印の向きだけが嘘になる）。
        /// </summary>
        public static Vector3 Forward(float yawDeg, float pitchDeg)
        {
            float y = yawDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
            float cp = Mathf.Cos(p);
            return new Vector3(cp * Mathf.Sin(y), Mathf.Sin(p), cp * Mathf.Cos(y));
        }
    }
}
