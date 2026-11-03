#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// デジタルツインの部屋（<see cref="VirtualRoom"/>）の形を手続きで作る。UV はすべてメートル
    /// （材質の <c>_TexScale</c> が 1m あたりの繰り返し数）。頂点色の r に焼いた遮蔽（AO）を入れる。
    /// </summary>
    internal static class TwinMeshes
    {
        private sealed class Builder
        {
            public readonly List<Vector3> V = new();
            public readonly List<Vector3> N = new();
            public readonly List<Vector2> Uv = new();
            public readonly List<Color> C = new();
            public readonly List<int> T = new();

            public Mesh ToMesh(string name, List<Object> trash, bool recalcNormals = false)
            {
                var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                m.SetVertices(V);
                m.SetUVs(0, Uv);
                m.SetColors(C);
                m.SetTriangles(T, 0);
                if (recalcNormals || N.Count != V.Count) m.RecalculateNormals();
                else m.SetNormals(N);
                m.RecalculateTangents();
                m.RecalculateBounds();
                trash.Add(m);
                return m;
            }
        }

        /// <summary>平面の格子（内向き）。<paramref name="ao"/> は世界座標 → 遮蔽 0..1。</summary>
        public static Mesh Plane(Vector3 origin, Vector3 axisU, Vector3 axisV, float lenU, float lenV, float step,
                                 Func<Vector3, float> ao, List<Object> trash, string name)
        {
            var b = new Builder();
            int nu = Mathf.Max(1, Mathf.CeilToInt(lenU / step)), nv = Mathf.Max(1, Mathf.CeilToInt(lenV / step));
            Vector3 normal = Vector3.Cross(axisU, axisV).normalized;
            for (int j = 0; j <= nv; j++)
                for (int i = 0; i <= nu; i++)
                {
                    float u = lenU * i / nu, v = lenV * j / nv;
                    Vector3 p = origin + axisU * u + axisV * v;
                    b.V.Add(p); b.N.Add(normal); b.Uv.Add(new Vector2(u, v));
                    float a = ao(p);
                    b.C.Add(new Color(a, a, a, 1f));
                }
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    int k = j * (nu + 1) + i;
                    b.T.Add(k); b.T.Add(k + 1); b.T.Add(k + nu + 1);
                    b.T.Add(k + 1); b.T.Add(k + nu + 2); b.T.Add(k + nu + 1);
                }
            return b.ToMesh(name, trash);
        }

        /// <summary>
        /// ひだのあるカーテン 1 枚（<paramref name="a"/>→<paramref name="b"/> の線上に垂れる。表裏 2 面）。
        /// 実物は黒いパイプの枠に吊った灰色の厚手のカーテン（plate_A）。襞の幅は 1 本ずつ 6〜14cm、
        /// 上はフックでつまんで小さな山になり、下へ行くほど襞が開いて揺らぎ、裾は床に 3〜4cm 溜まる。
        /// </summary>
        public static Mesh Curtain(Vector2 a, Vector2 b, float floorY, float height, int seed, List<Object> trash, string name,
                                   List<float>? hooks = null)
        {
            var mb = new Builder();
            float len = Vector2.Distance(a, b);
            Vector2 t2 = (b - a) / Mathf.Max(len, 1e-4f);
            var along = new Vector3(t2.x, 0f, t2.y);
            var side = new Vector3(-t2.y, 0f, t2.x);
            var rnd = new System.Random(seed);
            float phase0 = (float)rnd.NextDouble() * 10f;

            // 襞 1 本ずつの幅と深さ
            var starts = new List<float>();
            var widths = new List<float>();
            var depths = new List<float>();
            for (float acc = -(float)rnd.NextDouble() * 0.08f; acc < len + 0.16f;)
            {
                float w = rnd.NextDouble() < 0.25 ? Mathf.Lerp(0.03f, 0.06f, (float)rnd.NextDouble()) : Mathf.Lerp(0.08f, 0.22f, (float)rnd.NextDouble());
                starts.Add(acc); widths.Add(w); depths.Add(Mathf.Lerp(0.45f, 1.4f, (float)rnd.NextDouble()));
                if (acc > 0.02f && acc < len - 0.02f) hooks?.Add(acc);
                acc += w;
            }
            int Fold(float u)
            {
                int k = 0;
                while (k + 1 < starts.Count && starts[k + 1] <= u) k++;
                return k;
            }
            float Phase(float u)
            {
                int k = Fold(u);
                return Mathf.PI * 2f * (k + (u - starts[k]) / widths[k]);
            }
            float DepthAt(float u)
            {
                int k = Fold(u);
                float f = Mathf.Clamp01((u - starts[k]) / widths[k]);
                float d1 = k + 1 < depths.Count ? depths[k + 1] : depths[k];
                return Mathf.Lerp(depths[k], d1, f * f * (3f - 2f * f));
            }

            int nu = Mathf.CeilToInt(len / 0.005f), nv = 84;
            const int Pool = 4;             // 床に溜まる段
            const float Thick = 0.004f;
            int row = nu + 1;
            for (int sideSign = 1; sideSign >= -1; sideSign -= 2)
            {
                int start = mb.V.Count;
                for (int j = -Pool; j <= nv; j++)
                {
                    float v = Mathf.Max(0, j) / (float)nv;          // 0 = 裾, 1 = 上
                    float drift = (1f - v) * (1f - v);
                    for (int i = 0; i <= nu; i++)
                    {
                        float u = len * i / nu;
                        float uw = u + 0.03f * Mathf.Sin(u * 2.1f + v * 3.1f + phase0) * drift;
                        float ph = Phase(uw);
                        float amp = Mathf.Lerp(0.032f, 0.022f, v) * DepthAt(uw);
                        amp *= Mathf.Lerp(1f, 0.55f, Mathf.SmoothStep(0.93f, 1f, v));     // フックでつまむ
                        // 断面は非対称: フックの所（ph = 0）は鋭く折れ、襞の中ほどは丸く膨らむ
                        float fold = 1f - 2f * Mathf.Pow(Mathf.Abs(Mathf.Sin(ph * 0.5f)), 0.7f);
                        float bulge = 0.012f * Mathf.Sin(u * 2.6f + phase0 * 0.7f) * drift;
                        float endTaper = Mathf.Clamp01(Mathf.Min(u, len - u) / 0.03f);
                        float off = (amp * fold + bulge) * Mathf.Lerp(0.35f, 1f, endTaper);
                        // 上端の山はフック（襞の境）の位置で竿のすぐ下、山と山の間は 2cm 前後たわむ（1 つずつ ±40%）
                        float y = floorY + 0.0015f + v * (height - 0.0035f);
                        float sag = 0.02f * Mathf.Lerp(0.3f, 1.7f, Mathf.InverseLerp(0.45f, 1.4f, DepthAt(u)));
                        if (v > 0.94f) y -= (v - 0.94f) / 0.06f * sag * Mathf.Pow(Mathf.Sin(ph * 0.5f), 2f);
                        if (j < 0)
                        {
                            // 裾が床に溜まる: 襞の向きへ寝かせて広げる
                            float k = -j;
                            off += 0.0135f * k * Mathf.Clamp(off / 0.012f, -1f, 1f) * Mathf.Lerp(0.4f, 1f, endTaper);
                            y = floorY + 0.0015f + 0.016f * (Pool - k) / Pool * (0.45f + 0.55f * Mathf.Abs(fold));
                        }
                        Vector3 p = new Vector3(a.x, y, a.y) + along * u + side * (off + sideSign * Thick * 0.5f);
                        mb.V.Add(p);
                        mb.Uv.Add(new Vector2(u, j < 0 ? j * 0.012f : y - floorY));
                        float valley = 0.4f + 0.6f * (0.5f + 0.5f * fold * sideSign);
                        float ao = valley * Mathf.Lerp(0.62f, 1f, Mathf.Clamp01((y - floorY) / 0.3f));
                        mb.C.Add(new Color(ao, ao, ao, 1f));
                    }
                }
                int rows = nv + Pool;
                for (int r = 0; r < rows; r++)
                    for (int i = 0; i < nu; i++)
                    {
                        int k = start + r * row + i;
                        if (sideSign > 0)
                        {
                            mb.T.Add(k); mb.T.Add(k + 1); mb.T.Add(k + row);
                            mb.T.Add(k + 1); mb.T.Add(k + row + 1); mb.T.Add(k + row);
                        }
                        else
                        {
                            mb.T.Add(k); mb.T.Add(k + row); mb.T.Add(k + 1);
                            mb.T.Add(k + 1); mb.T.Add(k + row); mb.T.Add(k + row + 1);
                        }
                    }
            }
            return mb.ToMesh(name, trash, recalcNormals: true);
        }

        /// <summary>円柱（パイプ・コード・ソケット）。端に蓋。</summary>
        public static void AddCylinder(List<CombineInstance> into, Vector3 p0, Vector3 p1, float radius, int sides, List<Object> trash)
        {
            var b = new Builder();
            Vector3 axis = p1 - p0;
            float len = axis.magnitude;
            Vector3 dir = axis / Mathf.Max(len, 1e-5f);
            Vector3 u = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 w = Vector3.Cross(dir, u);
            for (int r = 0; r <= 1; r++)
                for (int s = 0; s <= sides; s++)
                {
                    float ang = s / (float)sides * Mathf.PI * 2f;
                    Vector3 n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                    b.V.Add((r == 0 ? p0 : p1) + n * radius);
                    b.N.Add(n);
                    b.Uv.Add(new Vector2(ang * radius, r * len));
                    b.C.Add(Color.white);
                }
            for (int s = 0; s < sides; s++)
            {
                int k = s, k2 = s + sides + 1;
                b.T.Add(k); b.T.Add(k + 1); b.T.Add(k2);
                b.T.Add(k + 1); b.T.Add(k2 + 1); b.T.Add(k2);
            }
            foreach ((Vector3 c, Vector3 n) in new[] { (p0, -dir), (p1, dir) })
            {
                int center = b.V.Count;
                b.V.Add(c); b.N.Add(n); b.Uv.Add(Vector2.zero); b.C.Add(Color.white);
                for (int s = 0; s <= sides; s++)
                {
                    float ang = s / (float)sides * Mathf.PI * 2f;
                    b.V.Add(c + (u * Mathf.Cos(ang) + w * Mathf.Sin(ang)) * radius);
                    b.N.Add(n); b.Uv.Add(Vector2.zero); b.C.Add(Color.white);
                }
                for (int s = 0; s < sides; s++)
                {
                    if (n == dir) { b.T.Add(center); b.T.Add(center + 1 + s); b.T.Add(center + 2 + s); }
                    else { b.T.Add(center); b.T.Add(center + 2 + s); b.T.Add(center + 1 + s); }
                }
            }
            into.Add(new CombineInstance { mesh = b.ToMesh("cyl", trash), transform = Matrix4x4.identity });
        }

        /// <summary>楕円体（電球のガラス）。<paramref name="squareness"/> &lt; 1 で角の丸い箱へ寄る（HMD の前面）。</summary>
        public static Mesh Ellipsoid(Vector3 radii, int seg, List<Object> trash, string name, float squareness = 1f)
        {
            var b = new Builder();
            for (int j = 0; j <= seg; j++)
            {
                float th = Mathf.PI * j / seg;
                for (int i = 0; i <= seg * 2; i++)
                {
                    float ph = Mathf.PI * i / seg;
                    var n = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    var q = new Vector3(Pow(n.x, squareness), Pow(n.y, squareness), Pow(n.z, squareness));
                    b.V.Add(Vector3.Scale(q, radii));
                    b.N.Add(new Vector3(n.x / radii.x, n.y / radii.y, n.z / radii.z).normalized);
                    b.Uv.Add(new Vector2(i / (2f * seg), j / (float)seg));
                    b.C.Add(Color.white);
                }
            }
            int row = seg * 2 + 1;
            for (int j = 0; j < seg; j++)
                for (int i = 0; i < seg * 2; i++)
                {
                    int k = j * row + i;
                    b.T.Add(k); b.T.Add(k + 1); b.T.Add(k + row);
                    b.T.Add(k + 1); b.T.Add(k + row + 1); b.T.Add(k + row);
                }
            return b.ToMesh(name, trash, recalcNormals: squareness < 0.99f);
        }

        private static float Pow(float v, float e) => Mathf.Sign(v) * Mathf.Pow(Mathf.Abs(v), e);

        /// <summary>直方体（中心・半径・回転）。</summary>
        public static void AddBox(List<CombineInstance> into, Vector3 center, Vector3 half, Quaternion rot, List<Object> trash)
        {
            var b = new Builder();
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            for (int f = 0; f < 6; f++)
            {
                int ax = f / 2;
                float sgn = f % 2 == 0 ? 1f : -1f;
                Vector3 n = axes[ax] * sgn;
                Vector3 u = axes[(ax + 1) % 3], v = axes[(ax + 2) % 3];
                if (sgn < 0) (u, v) = (v, u);
                int k = b.V.Count;
                for (int c = 0; c < 4; c++)
                {
                    float su = (c & 1) == 0 ? -1f : 1f, sv = (c & 2) == 0 ? -1f : 1f;
                    Vector3 local = Vector3.Scale(n + u * su + v * sv, half);
                    b.V.Add(center + rot * local);
                    b.N.Add(rot * n);
                    b.Uv.Add(new Vector2(su * Vector3.Scale(u, half).magnitude, sv * Vector3.Scale(v, half).magnitude));
                    b.C.Add(Color.white);
                }
                b.T.Add(k); b.T.Add(k + 1); b.T.Add(k + 2);
                b.T.Add(k + 1); b.T.Add(k + 3); b.T.Add(k + 2);
            }
            into.Add(new CombineInstance { mesh = b.ToMesh("box", trash), transform = Matrix4x4.identity });
        }

        public static Mesh Combine(List<CombineInstance> parts, List<Object> trash, string name)
        {
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.CombineMeshes(parts.ToArray(), true, true);
            m.RecalculateBounds();
            trash.Add(m);
            return m;
        }

        /// <summary>線分の集まり（MeshTopology.Lines）。頂点色の a が濃さ。</summary>
        public static Mesh Lines(List<(Vector3 a, Vector3 b, float alpha)> segs, List<Object> trash, string name)
            => Lines(segs.ConvertAll(s => (s.a, s.b, s.alpha, s.alpha)), trash, name);

        /// <summary>線分の集まり。端ごとに濃さを変えられる（先へ行くほど消える線）。</summary>
        public static Mesh Lines(List<(Vector3 a, Vector3 b, float alphaA, float alphaB)> segs, List<Object> trash, string name)
        {
            var v = new List<Vector3>(segs.Count * 2);
            var c = new List<Color>(segs.Count * 2);
            var idx = new List<int>(segs.Count * 2);
            foreach ((Vector3 a, Vector3 b, float alphaA, float alphaB) in segs)
            {
                idx.Add(v.Count); v.Add(a); c.Add(new Color(1, 1, 1, alphaA));
                idx.Add(v.Count); v.Add(b); c.Add(new Color(1, 1, 1, alphaB));
            }
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v); m.SetColors(c);
            m.SetIndices(idx, MeshTopology.Lines, 0);
            m.RecalculateBounds();
            trash.Add(m);
            return m;
        }

        /// <summary>太さのある線（札の字）。<paramref name="normal"/> の面に寝かせた細い四角形の集まり。</summary>
        public static Mesh Strokes(List<(Vector3 a, Vector3 b, float alpha)> segs, Vector3 normal, float width, List<Object> trash, string name)
        {
            var v = new List<Vector3>(); var c = new List<Color>(); var t = new List<int>();
            foreach ((Vector3 a, Vector3 b, float alpha) in segs)
            {
                Vector3 dir = (b - a).normalized;
                Vector3 side = Vector3.Cross(normal, dir).normalized * width * 0.5f;
                Vector3 a0 = a - dir * width * 0.5f, b0 = b + dir * width * 0.5f;   // 角を埋める
                int k = v.Count;
                v.Add(a0 - side); v.Add(a0 + side); v.Add(b0 - side); v.Add(b0 + side);
                for (int i = 0; i < 4; i++) c.Add(new Color(1, 1, 1, alpha));
                t.Add(k); t.Add(k + 2); t.Add(k + 1); t.Add(k + 1); t.Add(k + 2); t.Add(k + 3);
            }
            var m = new Mesh { name = name };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            trash.Add(m);
            return m;
        }

        /// <summary>凸多角形の塗り（視野が床に落ちる面）。頂点色の a が濃さ。</summary>
        public static Mesh Fan(IReadOnlyList<Vector3> pts, float alpha, List<Object> trash, string name)
        {
            var v = new List<Vector3>(pts); var c = new List<Color>(); var t = new List<int>();
            for (int i = 0; i < v.Count; i++) c.Add(new Color(1, 1, 1, alpha));
            for (int i = 1; i + 1 < v.Count; i++) { t.Add(0); t.Add(i); t.Add(i + 1); }
            var m = new Mesh { name = name };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            trash.Add(m);
            return m;
        }

        /// <summary>塗りの四角形の集まり（床のゾーン）。頂点色の a が濃さ。</summary>
        public static Mesh Quads(List<(Vector3 c, Vector2 half, float alpha)> quads, List<Object> trash, string name)
        {
            var v = new List<Vector3>(); var c = new List<Color>(); var t = new List<int>();
            foreach ((Vector3 p, Vector2 h, float alpha) in quads)
            {
                int k = v.Count;
                v.Add(p + new Vector3(-h.x, 0, -h.y)); v.Add(p + new Vector3(h.x, 0, -h.y));
                v.Add(p + new Vector3(-h.x, 0, h.y)); v.Add(p + new Vector3(h.x, 0, h.y));
                for (int i = 0; i < 4; i++) c.Add(new Color(1, 1, 1, alpha));
                t.Add(k); t.Add(k + 2); t.Add(k + 1); t.Add(k + 1); t.Add(k + 2); t.Add(k + 3);
            }
            var m = new Mesh { name = name };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            trash.Add(m);
            return m;
        }

        /// <summary>視線へ向く粒の集まり（<c>FixedCamVr/Twin/Sprites</c>）。</summary>
        public static Mesh Sprites(List<(Vector3 p, float size, float seed)> items, List<Object> trash, string name)
        {
            var v = new List<Vector3>(); var uv0 = new List<Vector2>(); var uv1 = new List<Vector2>(); var t = new List<int>();
            Vector2[] corners = { new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };
            foreach ((Vector3 p, float size, float seed) in items)
            {
                int k = v.Count;
                foreach (Vector2 c in corners) { v.Add(p); uv0.Add(c); uv1.Add(new Vector2(size, seed)); }
                t.Add(k); t.Add(k + 2); t.Add(k + 1); t.Add(k + 1); t.Add(k + 2); t.Add(k + 3);
            }
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v); m.SetUVs(0, uv0); m.SetUVs(1, uv1); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            // 板は頂点シェーダで広がるので、境界を粒の最大径ぶん広げる
            Bounds bb = m.bounds; bb.Expand(0.6f); m.bounds = bb;
            trash.Add(m);
            return m;
        }
    }
}
