#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FixedCamVr.Tracking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>画面内だけでは見えない壁・体幅・実際の移動を第三者の二方向から記録する。</summary>
    internal sealed class WalkEvidence : IDisposable
    {
        public const int Layer = 29;
        private readonly Camera _observer, _overhead;
        private readonly RenderTexture _rt;
        private readonly Texture2D _pixels;
        private readonly LineRenderer _body, _forward;
        private readonly string _directory;
        private readonly WalkNavigation _navigation;
        private readonly float _floor;
        private readonly ShowRoomDef? _room;
        private readonly List<WalkRouteLogic.Waypoint> _route;
        private readonly Vector3[] _circle = new Vector3[65];

        public WalkEvidence(ShowLayoutDef layout, WalkNavigation navigation,
                            List<WalkRouteLogic.Waypoint> route, string directory, float floor,
                            int width, List<Object> trash)
        {
            _navigation = navigation; _directory = directory; _floor = floor; _route = route; _room = layout.room;
            Directory.CreateDirectory(Path.Combine(directory, "observer"));
            Directory.CreateDirectory(Path.Combine(directory, "overhead"));
            _rt = new RenderTexture(width, width * 9 / 16, 24) { antiAliasing = 4 };
            _rt.Create(); trash.Add(_rt);
            _pixels = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
            trash.Add(_pixels);
            float extent = Mathf.Max(navigation.Bounds.width, navigation.Bounds.height);
            _observer = CameraAt("Observer", new Vector3(2.8f, 3.0f, -3.2f), extent * 0.63f, trash);
            _overhead = CameraAt("Overhead", new Vector3(0f, 5f, 0f), extent * 0.58f, trash);
            _overhead.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
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

        private Camera CameraAt(string name, Vector3 offset, float size, List<Object> trash)
        {
            var go = new GameObject(name); trash.Add(go);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false; cam.orthographic = true; cam.orthographicSize = size;
            cam.transform.position = offset + Vector3.up * _floor;
            cam.transform.LookAt(new Vector3(0f, _floor + .65f, 0f));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.16f, .17f, .19f);
            cam.cullingMask = ~((1 << 30) | (1 << 28)); // 天井を通して上から見る。固定カメラには天井を残す。
            cam.nearClipPlane = .05f; cam.farClipPlane = 20f;
            return cam;
        }

        private static LineRenderer Line(string name, Color color, float width, List<Object> trash)
        {
            var go = new GameObject(name) { layer = Layer }; trash.Add(go);
            var line = go.AddComponent<LineRenderer>();
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); trash.Add(material);
            material.SetColor("_BaseColor", color);
            line.sharedMaterial = material; line.startWidth = line.endWidth = width;
            line.useWorldSpace = true; line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return line;
        }

        public void Capture(int frame, Vector2 position, float yaw)
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
            Shoot(_observer, Path.Combine(_directory, "observer", $"f{frame:00000}.jpg"));
            Shoot(_overhead, Path.Combine(_directory, "overhead", $"f{frame:00000}.jpg"));
        }

        private void Shoot(Camera camera, string path)
        {
            RenderTexture? old = RenderTexture.active;
            try
            {
                camera.targetTexture = _rt; camera.Render();
                RenderTexture.active = _rt;
                _pixels.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0); _pixels.Apply();
                File.WriteAllBytes(path, _pixels.EncodeToJPG(90));
            }
            finally { camera.targetTexture = null; RenderTexture.active = old; }
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

        public void Dispose() { _observer.targetTexture = null; _overhead.targetTexture = null; }
    }
}
