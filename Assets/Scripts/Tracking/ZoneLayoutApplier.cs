#nullable enable
using System.Collections.Generic;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// show.json layout（<see cref="ShowControlClient"/> 経由）× <see cref="CourseFrame"/> を
    /// <see cref="ZoneLayoutSolver"/> で矩形へ展開し、生成した <see cref="PlayerZone"/> 群を
    /// <see cref="PlayerZoneTracker"/> へ流し込むコンポーネント。layout 変更・frame 変更のどちらでも再適用する。
    ///
    /// また heartbeat 用に「HMD の course space XZ」「現在ゾーンラベル」を Streaming 側へ Func 注入する
    /// （Streaming → Tracking の参照を作らないため依存方向を守る手段）。
    ///
    /// layout / frame 変更で本 applier が [GeneratedZones] 配下のゾーンを作り直し、tracker.zones を
    /// 差し替える。位置合わせは <see cref="CourseFrame"/>（HMD 2 点登録）を通す。
    /// layout 不在時は rebuildFromDefaultOnStart=false なら何もせず、シーンの静的ゾーンを維持する。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneLayoutApplier : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ShowControlClient? showControl;
        [SerializeField] private CourseFrame? courseFrame;
        [SerializeField] private PlayerZoneTracker? tracker;

        [Tooltip("生成ゾーンをぶら下げる親 Transform。null なら本コンポーネントの Transform。")]
        [SerializeField] private Transform? zonesContainer;

        [Tooltip("heartbeat のプレイヤードット用 HMD Transform（CenterEyeAnchor）。")]
        [SerializeField] private Transform? headTransform;

        [Header("Zone geometry")]
        [Tooltip("生成ゾーンの中心 y (m)。HMD 高さ ≈1.6m を含むよう Transform.y に置く。")]
        [SerializeField] private float zoneCenterY = 1f;

        [Tooltip("生成ゾーンの半高 (m)。全身を含める。")]
        [SerializeField] private float zoneHalfHeight = 2f;

        [Header("Fallback")]
        [Tooltip("show.json layout が無い時、内蔵の既定 layout（正準 cuts）でゾーンを生成する。" +
                 "既定 false（= layout が来るまで既存の静的ゾーンを触らない）。")]
        [SerializeField] private bool rebuildFromDefaultOnStart = false;

        private bool _subscribed;

        private Transform Container => zonesContainer != null ? zonesContainer : transform;

        private void OnEnable()
        {
            if (showControl != null) showControl.LayoutChanged += OnLayoutChanged;
            if (courseFrame != null) courseFrame.Changed += OnFrameChanged;
            if (tracker != null) tracker.ZoneChanged += OnZoneChanged;
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (!_subscribed) return;
            if (showControl != null) showControl.LayoutChanged -= OnLayoutChanged;
            if (courseFrame != null) courseFrame.Changed -= OnFrameChanged;
            if (tracker != null) tracker.ZoneChanged -= OnZoneChanged;
            _spanValid = false;
            _subscribed = false;
        }

        private void Start()
        {
            InjectHeartbeatProviders();
            Rebuild();
        }

        private void OnLayoutChanged() => Rebuild();
        private void OnFrameChanged() => Rebuild();

        private void InjectHeartbeatProviders()
        {
            if (showControl == null) return;
            if (courseFrame != null)
            {
                var frame = courseFrame;
                if (headTransform != null)
                {
                    var head = headTransform;
                    showControl.HeadCourseXZProvider = () => frame.WorldToCourse(head.position);
                }
                // CG レイヤ（仮想カメラ・人形）を位置合わせ済みの実空間へ置くための course→world。
                // CourseFrame は transform を動かさない（originXZ/yawDeg を数値で持つ）ので、
                // 親子付けではなくこの変換を通すのが唯一正しい経路。
                showControl.CourseToWorldProvider = (xz, y) => frame.CourseToWorld(xz, y);
                showControl.CourseYawProvider = () => frame.YawDeg;
                // 未登録のまま CG を出すと course→world が identity へ落ち、人形が全く違う場所に立つ
                // （2026-07-27 監査 HIGH 4）。ShowCgLayer がこれを見て「出さない」へ倒す。
                showControl.CourseRegisteredProvider = () => frame.HasRegistration;
                // OS recenter でズレた状態のまま体験を始めさせない。HMD 内の警告は体験者が
                // 被っている間スタッフに見えないので、卓の本番前チェックへ届ける（2026-07-28）。
                showControl.CourseNeedsReRegProvider = () => frame.NeedsReRegistration;
                // 「B で確定した」を事象として検出する口。IntroDirector が中止からの復帰を
                // 1 段（位置合わせを撃ち直すだけ）にするのに使う。プレビューでは変わらない値なので、
                // 確定前に導入が再開して登録ビューと演出が混ざることがない。
                showControl.CourseRegistrationStampProvider = () => frame.SavedAtIso;
            }
            var trk = tracker;
            showControl.CurrentZoneLabelProvider = () => trk != null && trk.CurrentZone != null ? trk.CurrentZone.Label : "";
            // 区間の進み（canon/LEDGER.md 0093）。**その場で測る** — 進みは頭の位置の関数でしかないので、
            // 毎フレーム更新して持ち回るより、読まれた時に 1 回内積を取る方が食い違いようが無い。
            showControl.ZoneSpanProvider = SampleZoneSpan;
        }

        // ---- 区間の進み（canon/LEDGER.md 0093）------------------------------------------------
        // 「入った瞬間の立ち位置」と「奥の端」だけを覚えておき、進みは読まれるたびに測る。
        // 入った瞬間は PlayerZoneTracker.ZoneChanged（生の判定）で拾う。ショーの確定（dwell 0.5s）
        // より少し早いが、**幾何としては生の方が正しい**（体験者が線を跨いだのはその瞬間）。
        private bool _spanValid;
        private Vector3 _spanAxis;
        private float _spanEntryU, _spanFarU;
        private int _spanCamera = -1;
        private int _spanVisit;

        private void OnZoneChanged(PlayerZone? _, PlayerZone entered)
        {
            _spanValid = false;
            _spanVisit++;
            if (entered == null || tracker == null || headTransform == null) return;

            PlayerZone[] zones = tracker.Zones;
            var boxes = new SpanBox[zones.Length];
            int index = -1;
            for (int i = 0; i < zones.Length; i++)
            {
                PlayerZone z = zones[i];
                if (z == null) continue;
                boxes[i] = new SpanBox(z.Center, z.Rotation, z.HalfExtents, z.CameraIndex);
                if (z == entered) index = i;
            }
            if (index < 0) return;

            _spanCamera = entered.CameraIndex;
            _spanValid = ZoneSpanMath.Solve(boxes, index, headTransform.position,
                                            out _spanAxis, out _spanEntryU, out _spanFarU);
        }

        private ZoneSpan SampleZoneSpan()
        {
            if (!_spanValid || headTransform == null) return default;
            float p = ZoneSpanMath.Progress01(headTransform.position, _spanAxis, _spanEntryU, _spanFarU);
            return new ZoneSpan(true, _spanCamera, _spanVisit, p);
        }

        /// <summary>layout を解決し、ゾーンを作り直して tracker へ流し込む。layout が無ければ何もしない。</summary>
        public void Rebuild()
        {
            if (courseFrame == null || tracker == null) return;

            if (!TryResolveRects(out List<ZoneLayoutSolver.ZoneRect> rects, out float hyst, out string src))
                return;

            PlayerZone[] zones = BuildZones(rects);
            // 箱を作り直したら、覚えていた「入った所 → 奥の端」はもう別の空間の値。次の進入で測り直す。
            _spanValid = false;
            tracker.SetZonesRuntime(zones);
            tracker.SetHysteresisShrink(hyst);
            Debug.Log($"[ZoneLayoutApplier] rebuilt zones={zones.Length} (src={src}, hyst={hyst:F3})");
        }

        // layout（grid 優先→cuts→内蔵既定）を矩形群 + hysteresis へ解決する。解決できなければ false。
        private bool TryResolveRects(out List<ZoneLayoutSolver.ZoneRect> rects, out float hyst, out string src)
        {
            rects = null!;
            hyst = 0.12f;
            src = "none";

            ShowLayoutDef? lay = showControl != null ? showControl.Layout : null;
            if (lay != null)
            {
                bool hasGrid = lay.grid != null && lay.grid.HasData();
                bool hasCuts = lay.cuts != null && lay.cuts.Length > 0;
                switch (ZoneLayoutSolver.ChooseSource(hasGrid, hasCuts))
                {
                    case ZoneLayoutSolver.LayoutSource.Grid:
                        rects = SolveGridFrom(lay);
                        hyst = ClampHyst(lay.hysteresisM, lay.overlapM);
                        src = $"grid {lay.grid!.cols}x{lay.grid.rows}";
                        return true;
                    case ZoneLayoutSolver.LayoutSource.Cuts:
                        ZoneLayoutSolver.ZoneLayoutInput ci = FromShowLayout(lay);
                        rects = ZoneLayoutSolver.Solve(ci);
                        hyst = ClampHyst(ci.hysteresisM, ci.overlapM);
                        src = $"cuts={lay.cuts!.Length}";
                        return true;
                }
            }

            if (rebuildFromDefaultOnStart)
            {
                ZoneLayoutSolver.ZoneLayoutInput di = BuildDefaultInput();
                rects = ZoneLayoutSolver.Solve(di);
                hyst = ClampHyst(di.hysteresisM, di.overlapM);
                src = "default-cuts";
                return true;
            }
            return false;
        }

        // ヒステリシスを overlapM/2 でクランプし、逆転（hyst > overlap/2）でデッドバンドが消える構成なら
        // 1 度だけ警告する。純関数 ZoneLayoutSolver.ClampHysteresis を全経路（grid/cuts/default）で共有する。
        private bool _hystClampWarned;
        private float ClampHyst(float hysteresisM, float overlapM)
        {
            float clamped = ZoneLayoutSolver.ClampHysteresis(hysteresisM, overlapM);
            if (clamped < hysteresisM - 1e-4f && !_hystClampWarned)
            {
                _hystClampWarned = true;
                Debug.LogWarning($"[ZoneLayoutApplier] hysteresisM {hysteresisM:F3} > overlapM/2 {overlapM * 0.5f:F3} → " +
                                 $"デッドバンド消失を防ぐため {clamped:F3} にクランプ（show.json の overlapM/hysteresisM 逆転を確認）。");
            }
            return clamped;
        }

        private static List<ZoneLayoutSolver.ZoneRect> SolveGridFrom(ShowLayoutDef lay)
        {
            ShowGridDef gd = lay.grid!;
            float w = lay.floor != null && lay.floor.w > 0f ? lay.floor.w : 1.8f;
            float d = lay.floor != null && lay.floor.d > 0f ? lay.floor.d : 1.8f;

            // 検証: cols·tileM が floor.w と大きくズレていれば警告（NW 角アンカーでそのまま展開する）。
            if (Mathf.Abs(gd.cols * gd.tileM - w) > 0.01f || Mathf.Abs(gd.rows * gd.tileM - d) > 0.01f)
                Debug.LogWarning($"[ZoneLayoutApplier] grid 寸法 {gd.cols}x{gd.rows}·{gd.tileM:F3}m " +
                                 $"= {gd.cols * gd.tileM:F2}x{gd.rows * gd.tileM:F2}m が floor {w:F2}x{d:F2}m と不一致。NW 角アンカーで展開。");

            int[] cells = ZoneLayoutSolver.ParseGridCells(gd.cells, gd.rows, gd.cols);
            var g = new ZoneLayoutSolver.GridLayoutInput
            {
                floorW = w,
                floorD = d,
                overlapM = lay.overlapM,
                hysteresisM = lay.hysteresisM,
                tileM = gd.tileM,
                cols = gd.cols,
                rows = gd.rows,
                cells = cells,
            };
            return ZoneLayoutSolver.SolveGrid(g);
        }

        private static ZoneLayoutSolver.ZoneLayoutInput FromShowLayout(ShowLayoutDef lay)
        {
            var cuts = new ZoneLayoutSolver.CutInput[lay.cuts.Length];
            for (int i = 0; i < cuts.Length; i++)
                cuts[i] = new ZoneLayoutSolver.CutInput { s = lay.cuts[i].s, camAfter = lay.cuts[i].camAfter };
            float w = lay.floor != null && lay.floor.w > 0f ? lay.floor.w : 1.8f;
            float d = lay.floor != null && lay.floor.d > 0f ? lay.floor.d : 1.8f;
            return new ZoneLayoutSolver.ZoneLayoutInput
            {
                floorW = w,
                floorD = d,
                overlapM = lay.overlapM,
                hysteresisM = lay.hysteresisM,
                cuts = cuts,
            };
        }

        // 内蔵既定（show.json 不在時のフォールバック）。現行 A:South / B:East / C:North+West に相当する正準 cuts。
        private static ZoneLayoutSolver.ZoneLayoutInput BuildDefaultInput()
        {
            return new ZoneLayoutSolver.ZoneLayoutInput
            {
                floorW = 1.8f,
                floorD = 1.8f,
                overlapM = 0.08f,
                hysteresisM = 0.12f,
                cuts = new[]
                {
                    new ZoneLayoutSolver.CutInput { s = 0.125f, camAfter = 1 },
                    new ZoneLayoutSolver.CutInput { s = 0.375f, camAfter = 2 },
                    new ZoneLayoutSolver.CutInput { s = 0.875f, camAfter = 0 },
                },
            };
        }

        private PlayerZone[] BuildZones(List<ZoneLayoutSolver.ZoneRect> rects)
        {
            ClearContainer();
            var frame = courseFrame!;
            var zones = new PlayerZone[rects.Count];
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                var go = new GameObject($"GenZone_{i}_{r.label}");
                go.transform.SetParent(Container, worldPositionStays: false);

                var z = go.AddComponent<PlayerZone>();
                z.SetRuntimeConfig(r.cameraIndex, r.priority, r.label);
                Vector3 worldCenter = frame.CourseToWorld(new Vector2(r.centerX, r.centerZ), zoneCenterY);
                z.SetRuntimeBounds(worldCenter, new Vector3(r.halfX, zoneHalfHeight, r.halfZ));
                z.SetRuntimeRotation(frame.Rotation);
                zones[i] = z;
            }
            return zones;
        }

        private void ClearContainer()
        {
            Transform c = Container;
            for (int i = c.childCount - 1; i >= 0; i--)
            {
                var child = c.GetChild(i);
                if (child.GetComponent<PlayerZone>() != null) Destroy(child.gameObject);
            }
        }
    }
}
