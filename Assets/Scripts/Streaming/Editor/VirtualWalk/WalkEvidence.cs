#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Tracking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// 画面内だけでは見えない壁・体幅・実際の移動を第三者の二方向から記録する。
    /// 「立体」（observer）はデジタルツインの部屋を電球の光で撮る斜めの画、「真上」（overhead）は天井と電球を外した平面図。
    /// </summary>
    internal sealed class WalkEvidence : IDisposable
    {
        public const int Layer = 29;
        private readonly TwinLens _observer, _overhead;
        private readonly VirtualRoom _twin;
        private readonly LineRenderer _body, _forward;
        private readonly string _directory;
        private readonly WalkNavigation _navigation;
        private readonly float _floor;
        private readonly ShowRoomDef? _room;
        private readonly List<WalkRouteLogic.Waypoint> _route;
        private readonly Vector3[] _circle = new Vector3[65];

        public WalkEvidence(ShowLayoutDef layout, WalkNavigation navigation,
                            List<WalkRouteLogic.Waypoint> route, string directory, float floor,
                            int width, VirtualRoom twin, List<Object> trash)
        {
            _navigation = navigation; _directory = directory; _floor = floor; _route = route; _room = layout.room; _twin = twin;
            Directory.CreateDirectory(Path.Combine(directory, "observer"));
            Directory.CreateDirectory(Path.Combine(directory, "overhead"));
            float extent = Mathf.Max(navigation.Bounds.width, navigation.Bounds.height);
            // 立体: ブースの開いた側から、電球と L の角を収める。歩行範囲の手前の角でも全身が切れにくい画角
            _observer = new TwinLens("Observer", width, width * 9 / 16, TwinLens.Grade.Cinematic, trash);
            Vector3 focus = twin.Focus;
            Vector3 away = new Vector3(-focus.x, 0f, -focus.z).normalized;
            if (away.sqrMagnitude < 0.5f) away = new Vector3(0.7071f, 0f, -0.7071f);
            // 対角線上には固定カメラ A の三脚が立ち、画の中央を縦に割る。少し回り込む
            away = Quaternion.Euler(0f, -24f, 0f) * away;
            _observer.Aim(focus + away * (extent * 1.62f) + Vector3.up * 0.32f, focus + Vector3.up * 0.18f, 48f);
            _overhead = new TwinLens("Overhead", width, width * 9 / 16, TwinLens.Grade.Plan, trash);
            Camera top = _overhead.Camera;
            top.orthographic = true; top.orthographicSize = extent * 0.58f;
            // 静止画（TwinStill）の平面図と同じ向き: 画の上 = -x（L の奥の壁）、右 = +z。床の札がこの向きで正立する
            top.transform.SetPositionAndRotation(new Vector3(0f, floor + 5f, 0f), Quaternion.Euler(90f, -90f, 0f));
            top.cullingMask = ~((1 << 30) | (1 << VirtualRoom.AtmosphereLayer)); // 天井・電球・光の筋を通して上から見る
            _body = Line("Visitor body radius", new Color(0.05f, 0.8f, 1f), 0.014f, trash);
            _forward = Line("Visitor facing", new Color(1f, 0.7f, 0.15f), 0.025f, trash);
            LineRenderer path = Line("Planned path", new Color(0.1f, 0.7f, 0.8f), 0.012f, trash);
            path.positionCount = route.Count;
            for (int i = 0; i < route.Count; i++)
                path.SetPosition(i, new Vector3(route[i].Course.x, floor + 0.025f, route[i].Course.y));
            Rect b = navigation.Bounds;
            LineRenderer edge = Line("Simulation boundary", new Color(0.9f, 0.6f, 0.2f), 0.012f, trash);
            edge.positionCount = 5;
            edge.SetPositions(new[] { new Vector3(b.xMin, floor + .02f, b.yMin), new Vector3(b.xMin, floor + .02f, b.yMax),
                new Vector3(b.xMax, floor + .02f, b.yMax), new Vector3(b.xMax, floor + .02f, b.yMin),
                new Vector3(b.xMin, floor + .02f, b.yMin) });
        }

        private static LineRenderer Line(string name, Color color, float width, List<Object> trash)
        {
            var go = new GameObject(name) { layer = Layer }; trash.Add(go);
            var line = go.AddComponent<LineRenderer>();
            // 部屋の光とは別の層として加算で重ねる（暗い床でも読める）
            var material = new Material(Shader.Find("FixedCamVr/Twin/Line")); trash.Add(material);
            material.SetColor("_Color", color);
            material.SetFloat("_Intensity", 1.15f);
            line.sharedMaterial = material; line.startWidth = line.endWidth = width;
            line.useWorldSpace = true; line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return line;
        }

        public void Capture(int frame, Vector2 position, float yaw, float time)
        {
            for (int i = 0; i < _circle.Length; i++)
            {
                float a = i * Mathf.PI * 2f / (_circle.Length - 1);
                _circle[i] = new Vector3(position.x + Mathf.Sin(a) * _navigation.BodyRadiusM,
                    _floor + .035f, position.y + Mathf.Cos(a) * _navigation.BodyRadiusM);
            }
            _body.positionCount = _circle.Length; _body.SetPositions(_circle);
            Vector3 origin = new Vector3(position.x, _floor + .04f, position.y);
            _forward.positionCount = 2;
            _forward.SetPosition(0, origin);
            _forward.SetPosition(1, origin + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * .45f);
            _observer.Shoot(_twin, time, frame, Path.Combine(_directory, "observer", $"f{frame:00000}.jpg"));
            _overhead.Shoot(_twin, time, frame, Path.Combine(_directory, "overhead", $"f{frame:00000}.jpg"));
        }

        [Serializable] private sealed class Summary
        {
            public string route = "", routeLog = "", avatar = "";
            public float bodyRadiusM, wallMarginM, distanceM, minWallClearanceM;
            public Rect bounds;
            public int frames;
            public Vector2[] waypoints = Array.Empty<Vector2>();
            public ShowRoomWallDef[] walls = Array.Empty<ShowRoomWallDef>();
            public ShowRoomBoxDef[] props = Array.Empty<ShowRoomBoxDef>();
        }

        public void WriteSummary(string route, string log, float distance, float clearance, string avatar, int frames)
        {
            var summary = new Summary { route = route, routeLog = log, avatar = avatar,
                bodyRadiusM = _navigation.BodyRadiusM, wallMarginM = _navigation.WallMarginM,
                bounds = _navigation.Bounds, distanceM = distance, minWallClearanceM = clearance, frames = frames,
                waypoints = _route.ConvertAll(w => w.Course).ToArray(),
                walls = _room?.walls ?? Array.Empty<ShowRoomWallDef>(), props = _room?.props ?? Array.Empty<ShowRoomBoxDef>() };
            File.WriteAllText(Path.Combine(_directory, "navigation.json"), JsonUtility.ToJson(summary, true));
        }

        public void Dispose() { _observer.Dispose(); _overhead.Dispose(); }
    }
}
