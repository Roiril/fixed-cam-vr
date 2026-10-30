#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// 仮想体験の部屋（<see cref="VirtualWalkRun"/>）。<c>layout.room</c> の床と壁（灰色のカーテンの仕切り）に、
    /// 固定カメラの背景へ写り込む事務室の箱を足す。
    ///
    /// ⚠ <b>置き換えの板</b>。色と明るさは実物を測っていない（カメラごとのプレート写真を見て寄せただけ）。
    /// 見るのは演出の形と時間であって、部屋の見た目ではない。
    /// </summary>
    internal static class VirtualRoom
    {
        public static void Build(ShowLayoutDef layout, float floorY, List<Object> trash)
        {
            var root = new GameObject("VirtualRoom");
            trash.Add(root);
            Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");

            Material carpet = Mat(lit, "Carpet", new Color(0.30f, 0.30f, 0.32f), CarpetTex(trash), trash, 0.15f);
            Material curtain = Mat(lit, "Curtain", new Color(0.36f, 0.35f, 0.34f), CurtainTex(trash), trash, 0.05f);
            Material wall = Mat(lit, "OfficeWall", new Color(0.78f, 0.78f, 0.76f), null, trash, 0.1f);
            Material ceiling = Mat(lit, "Ceiling", new Color(0.86f, 0.86f, 0.85f), null, trash, 0.05f);
            Material desk = Mat(lit, "Desk", new Color(0.55f, 0.52f, 0.48f), null, trash, 0.2f);
            Material dark = Mat(lit, "Dark", new Color(0.10f, 0.10f, 0.11f), null, trash, 0.3f);
            var panel = new Material(unlit) { name = "LightPanel" };
            panel.SetColor("_BaseColor", new Color(1.6f, 1.6f, 1.55f));
            trash.Add(panel);

            // 事務室（カメラの背景）
            const float RoomW = 9f, RoomD = 9f, RoomH = 2.7f;
            Box(root, "Floor", new Vector3(0f, floorY - 0.01f, 0f), new Vector3(RoomW, 0.02f, RoomD), carpet);
            Box(root, "Ceiling", new Vector3(0f, floorY + RoomH, 0f), new Vector3(RoomW, 0.02f, RoomD), ceiling).layer = 28;
            Box(root, "WallN", new Vector3(0f, floorY + RoomH * 0.5f, RoomD * 0.5f), new Vector3(RoomW, RoomH, 0.1f), wall);
            Box(root, "WallS", new Vector3(0f, floorY + RoomH * 0.5f, -RoomD * 0.5f), new Vector3(RoomW, RoomH, 0.1f), wall);
            Box(root, "WallE", new Vector3(RoomW * 0.5f, floorY + RoomH * 0.5f, 0f), new Vector3(0.1f, RoomH, RoomD), wall);
            Box(root, "WallW", new Vector3(-RoomW * 0.5f, floorY + RoomH * 0.5f, 0f), new Vector3(0.1f, RoomH, RoomD), wall);
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                    Box(root, $"Light{i}{j}", new Vector3(i * 2.6f, floorY + RoomH - 0.02f, j * 2.6f),
                        new Vector3(0.3f, 0.01f, 1.2f), panel).layer = 28;
            // 机・棚（背景に奥行きを出す）
            Box(root, "Desk1", new Vector3(2.6f, floorY + 0.36f, 1.6f), new Vector3(1.4f, 0.72f, 0.7f), desk);
            Box(root, "Desk2", new Vector3(-2.4f, floorY + 0.36f, -2.2f), new Vector3(1.4f, 0.72f, 0.7f), desk);
            Box(root, "Shelf1", new Vector3(3.9f, floorY + 0.9f, -1.2f), new Vector3(0.4f, 1.8f, 1.6f), dark);
            Box(root, "Shelf2", new Vector3(-1.2f, floorY + 0.9f, 3.9f), new Vector3(1.8f, 1.8f, 0.4f), dark);
            Box(root, "Board", new Vector3(0.8f, floorY + 1.4f, 4.4f), new Vector3(2.2f, 1.0f, 0.04f), wall);

            // 仕切りのカーテン（layout.room.walls）
            ShowRoomWallDef[]? walls = layout.room?.walls;
            if (walls != null)
            {
                foreach (ShowRoomWallDef? wd in walls)
                {
                    if (wd == null || !wd.IsUsable()) continue;
                    var a = new Vector2(wd.x1, wd.z1);
                    var b = new Vector2(wd.x2, wd.z2);
                    Vector2 mid = (a + b) * 0.5f;
                    float len = Vector2.Distance(a, b);
                    GameObject w = Box(root, "Curtain " + wd.id, new Vector3(mid.x, floorY + wd.h * 0.5f, mid.y),
                        new Vector3(Mathf.Max(wd.thick, 0.02f), wd.h, len), curtain);
                    w.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg, 0f);
                    // 竿
                    GameObject rod = Box(root, "Rod " + wd.id, new Vector3(mid.x, floorY + wd.h + 0.03f, mid.y),
                        new Vector3(0.03f, 0.03f, len + 0.1f), dark);
                    rod.transform.rotation = w.transform.rotation;
                }
            }

            // 著作された備品はナビゲーションと同じ高さ・寸法で描く。
            if (layout.room?.props != null)
                foreach (ShowRoomBoxDef p in layout.room.props)
                {
                    if (p == null || !p.IsUsable()) continue;
                    GameObject prop = Box(root, "Prop " + p.id,
                        new Vector3(p.x, floorY + p.y + p.h * .5f, p.z), new Vector3(p.w, p.h, p.d), desk);
                    prop.transform.rotation = Quaternion.Euler(0f, p.yawDeg, 0f);
                }

            // 光（layout.room.light があれば向き・強さを使う）
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(root.transform);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            ShowRoomDef? room = layout.room;
            float yaw = 30f, pitch = 70f, intensity = 0.9f, ambient = 0.45f;
            if (room != null && room.hasLight && room.light != null)
            {
                yaw = room.light.yawDeg; pitch = room.light.pitchDeg;
                intensity = room.light.intensity; ambient = room.light.ambient;
            }
            sunGo.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            sun.intensity = intensity;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft;
            for (int i = 0; i < 2; i++)
            {
                var pl = new GameObject($"Fluorescent{i}");
                pl.transform.SetParent(root.transform);
                pl.transform.position = new Vector3(i == 0 ? -1.3f : 1.3f, floorY + 2.5f, 0f);
                var l = pl.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = 6f;
                l.intensity = 0.8f;
                l.color = new Color(0.95f, 0.97f, 1f);
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(ambient, ambient, ambient * 0.98f);
        }

        /// <summary>頭に載せる HMD の代役（白い箱）。<b>実機の体験者は必ず被っている</b>ので、映像に写る形を揃える。</summary>
        public static Transform BuildHmd(List<Object> trash)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var white = new Material(lit) { name = "HMD" };
            white.SetColor("_BaseColor", new Color(0.92f, 0.92f, 0.92f));
            trash.Add(white);
            var black = new Material(lit) { name = "Strap" };
            black.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.05f));
            trash.Add(black);
            var root = new GameObject("HMD");
            trash.Add(root);
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.19f, 0.11f, 0.09f);
            body.GetComponent<Renderer>().sharedMaterial = white;
            Object.DestroyImmediate(body.GetComponent<Collider>());
            GameObject strap = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strap.transform.SetParent(root.transform, false);
            strap.transform.localPosition = new Vector3(0f, 0.01f, -0.10f);
            strap.transform.localScale = new Vector3(0.17f, 0.04f, 0.16f);
            strap.GetComponent<Renderer>().sharedMaterial = black;
            Object.DestroyImmediate(strap.GetComponent<Collider>());
            return root.transform;
        }

        private static Material Mat(Shader sh, string name, Color c, Texture2D? tex, List<Object> trash, float smooth)
        {
            var m = new Material(sh) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetColor("_Color", c);
            if (tex != null)
            {
                m.SetTexture("_BaseMap", tex);
                m.SetTexture("_MainTex", tex);
            }
            m.SetFloat("_Smoothness", smooth);
            trash.Add(m);
            return m;
        }

        private static GameObject Box(GameObject parent, string name, Vector3 pos, Vector3 size, Material m)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name;
            g.transform.SetParent(parent.transform, false);
            g.transform.position = pos;
            g.transform.localScale = size;
            g.GetComponent<Renderer>().sharedMaterial = m;
            Object.DestroyImmediate(g.GetComponent<Collider>());
            return g;
        }

        /// <summary>タイルカーペット（細い縞の織り目）。決定的に作る。</summary>
        private static Texture2D CarpetTex(List<Object> trash)
        {
            const int N = 256;
            var t = new Texture2D(N, N, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Repeat, name = "carpet" };
            var rnd = new System.Random(7);
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float stripe = ((x / 3 + y / 12) % 2 == 0) ? 0.08f : -0.04f;
                    float n = (float)rnd.NextDouble() * 0.18f - 0.09f;
                    byte v = (byte)Mathf.Clamp(150 + (stripe + n) * 255f, 0, 255);
                    px[y * N + x] = new Color32(v, v, (byte)Mathf.Min(255, v + 4), 255);
                }
            t.SetPixels32(px);
            t.Apply(true);
            trash.Add(t);
            return t;
        }

        /// <summary>カーテンの縦の襞（明暗の正弦）。</summary>
        private static Texture2D CurtainTex(List<Object> trash)
        {
            const int W = 256, H = 16;
            var t = new Texture2D(W, H, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Repeat, name = "curtain" };
            var px = new Color32[W * H];
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W;
                float fold = 0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 2f * 9f) * (0.8f + 0.2f * Mathf.Sin(u * 37f));
                byte v = (byte)Mathf.Clamp(120 + fold * 110f, 0, 255);
                for (int y = 0; y < H; y++) px[y * W + x] = new Color32(v, v, v, 255);
            }
            t.SetPixels32(px);
            t.Apply(true);
            trash.Add(t);
            return t;
        }
    }
}
