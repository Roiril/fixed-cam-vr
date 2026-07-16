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
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (!_subscribed) return;
            if (showControl != null) showControl.LayoutChanged -= OnLayoutChanged;
            if (courseFrame != null) courseFrame.Changed -= OnFrameChanged;
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
            if (courseFrame != null && headTransform != null)
            {
                var frame = courseFrame;
                var head = headTransform;
                showControl.HeadCourseXZProvider = () => frame.WorldToCourse(head.position);
            }
            var trk = tracker;
            showControl.CurrentZoneLabelProvider = () => trk != null && trk.CurrentZone != null ? trk.CurrentZone.Label : "";
        }

        /// <summary>layout を解決し、ゾーンを作り直して tracker へ流し込む。layout が無ければ何もしない。</summary>
        public void Rebuild()
        {
            if (courseFrame == null || tracker == null) return;

            if (!TryResolveRects(out List<ZoneLayoutSolver.ZoneRect> rects, out float hyst, out string src))
                return;

            PlayerZone[] zones = BuildZones(rects);
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
                        hyst = lay.hysteresisM;
                        src = $"grid {lay.grid!.cols}x{lay.grid.rows}";
                        return true;
                    case ZoneLayoutSolver.LayoutSource.Cuts:
                        ZoneLayoutSolver.ZoneLayoutInput ci = FromShowLayout(lay);
                        rects = ZoneLayoutSolver.Solve(ci);
                        hyst = ci.hysteresisM;
                        src = $"cuts={lay.cuts!.Length}";
                        return true;
                }
            }

            if (rebuildFromDefaultOnStart)
            {
                ZoneLayoutSolver.ZoneLayoutInput di = BuildDefaultInput();
                rects = ZoneLayoutSolver.Solve(di);
                hyst = di.hysteresisM;
                src = "default-cuts";
                return true;
            }
            return false;
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
