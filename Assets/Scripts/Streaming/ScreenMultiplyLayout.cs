#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>増えるスクリーン 1 枚の席。角度は群れの前方を 0 に測る。</summary>
    public readonly struct ScreenSeat
    {
        public readonly float azDeg, elDeg, widthDeg, distM, tiltYawDeg, tiltPitchDeg, rollDeg;
        /// <summary>0 = 寒色 / 1 = 白に近い寒色 / 2 = 暖色。</summary>
        public readonly int tint;
        /// <summary>映す画の遅れ（取り込みの枚数）。</summary>
        public readonly int delay;
        /// <summary>点く順 0..1。</summary>
        public readonly float rank;

        public ScreenSeat(float az, float el, float width, float dist, float tiltYaw, float tiltPitch, float roll,
                          int tint, int delay, float rank)
        {
            azDeg = az; elDeg = el; widthDeg = width; distM = dist;
            tiltYawDeg = tiltYaw; tiltPitchDeg = tiltPitch; rollDeg = roll;
            this.tint = tint; this.delay = delay; this.rank = rank;
        }

        public Vector3 Direction => Quaternion.Euler(-elDeg, azDeg, 0f) * Vector3.forward;
        public Vector3 Center => Direction * distM;
        /// <summary>頭の方を向いた姿勢に、席ごとの傾きを足したもの。local +Z が奥。</summary>
        public Quaternion Rotation => Quaternion.Euler(-elDeg + tiltPitchDeg, azDeg + tiltYawDeg, rollDeg);
        public float WidthM => 2f * distM * Mathf.Tan(widthDeg * 0.5f * Mathf.Deg2Rad);
    }

    /// <summary>
    /// <b>目の前にメインスクリーンが増える</b>演出の座席とメッシュ（参考 <c>mul.jpg</c>）。
    /// 表は <c>tools/make-screen-wall.py</c> が焼く。
    /// </summary>
    public static partial class ScreenMultiplyLayout
    {
        /// <summary>取り込みのリングの枚数。<c>make-screen-wall.py</c> の DELAY_SLOTS と対。</summary>
        public const int DelaySlots = 16;

        /// <summary>奥行き（幅に対する比）。ブラウン管の奥へ細る箱。</summary>
        public const float BodyDepth = 0.72f;
        /// <summary>箱の奥の面の大きさ（前面に対する比）。</summary>
        public const float BodyTaper = 0.78f;
        /// <summary>箱の前の縁を外形より内へ入れる比。外形の丸い角から箱の角がはみ出さない。</summary>
        public const float BodyInset = 0.94f;
        /// <summary>光の暈の大きさ（面に対する比）。</summary>
        public const float GlowScale = 1.9f;

        public static readonly Color[] Tints =
        {
            new(0.58f, 0.82f, 1.00f, 1f),
            new(0.86f, 0.95f, 1.00f, 1f),
            new(1.00f, 0.60f, 0.32f, 1f),
        };

        /// <summary>
        /// いま点いている枚数（画に出た側の観測）。
        /// 0 番は <paramref name="big01"/>、ほかは <see cref="AnomalyEyesLogic.EyeOpen"/>（シェーダの式と対）。
        /// </summary>
        public static int CountOpen(float big01, float field01)
        {
            int n = big01 > AnomalyEyesLogic.OpenEpsilon ? 1 : 0;
            for (int i = 1; i < Seats.Length; i++)
                if (AnomalyEyesLogic.EyeOpen(field01, Seats[i].rank) > AnomalyEyesLogic.OpenEpsilon) n++;
            return n;
        }

        /// <summary>いちばん最後に点いた席。音の方角に使う。</summary>
        public static int NewestOpen(float field01)
        {
            int best = 0;
            float rank = -1f;
            for (int i = 1; i < Seats.Length; i++)
            {
                float r = Seats[i].rank;
                if (r <= rank || AnomalyEyesLogic.EyeOpen(field01, r) <= AnomalyEyesLogic.OpenEpsilon) continue;
                rank = r;
                best = i;
            }
            return best;
        }

        /// <summary>
        /// 面と箱のメッシュ。uv0 = (面の u, 面の v, 部位 0 面 / 1 箱, 箱の奥行き 0..1)、
        /// uv1 = (順位, 遅れ, 席の乱数, 0 番か)、color = 色。
        /// </summary>
        public static Mesh BuildSurfaces()
        {
            var v = new List<Vector3>();
            var uv0 = new List<Vector4>();
            var uv1 = new List<Vector4>();
            var c = new List<Color>();
            var tri = new List<int>();

            for (int s = 0; s < Seats.Length; s++)
            {
                ScreenSeat seat = Seats[s];
                Vector3 p = seat.Center;
                Quaternion q = seat.Rotation;
                float hw = seat.WidthM * 0.5f;
                float hh = hw / CrtReferenceScreen.CaseAspect;
                Color tint = Tints[Mathf.Clamp(seat.tint, 0, Tints.Length - 1)];
                var info = new Vector4(seat.rank, seat.delay, AnomalyEyesMesh.Hash(s + 311), s == 0 ? 1f : 0f);

                // 箱を先に積む。奥へ細る 4 枚（前面の裏から見えないのでふさがない）。
                float front = BodyInset, back = BodyTaper * BodyInset;
                float depth = seat.WidthM * BodyDepth;
                Vector3[] f =
                {
                    new(-hw * front, -hh * front, 0.002f), new(hw * front, -hh * front, 0.002f),
                    new(hw * front, hh * front, 0.002f), new(-hw * front, hh * front, 0.002f),
                };
                Vector3[] b =
                {
                    new(-hw * back, -hh * back, depth), new(hw * back, -hh * back, depth),
                    new(hw * back, hh * back, depth), new(-hw * back, hh * back, depth),
                };
                for (int e = 0; e < 4; e++)
                {
                    int n = (e + 1) % 4;
                    int i0 = v.Count;
                    // 上の面は照らされ、下の面は陰る。
                    float shade = e == 2 ? 1f : e == 0 ? 0.35f : 0.7f;
                    AddVertex(p + q * f[e], new Vector4(0, 0, 1f, 0f));
                    AddVertex(p + q * f[n], new Vector4(0, 0, 1f, 0f));
                    AddVertex(p + q * b[n], new Vector4(0, 0, 1f, 1f));
                    AddVertex(p + q * b[e], new Vector4(0, 0, 1f, 1f));
                    for (int k = i0; k < i0 + 4; k++) c[k] = new Color(tint.r * shade, tint.g * shade, tint.b * shade, 1f);
                    tri.AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });
                }

                int i = v.Count;
                AddVertex(p + q * new Vector3(-hw, -hh, 0f), new Vector4(0, 0, 0, 0));
                AddVertex(p + q * new Vector3(hw, -hh, 0f), new Vector4(1, 0, 0, 0));
                AddVertex(p + q * new Vector3(hw, hh, 0f), new Vector4(1, 1, 0, 0));
                AddVertex(p + q * new Vector3(-hw, hh, 0f), new Vector4(0, 1, 0, 0));
                tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });

                void AddVertex(Vector3 pos, Vector4 uv)
                {
                    v.Add(pos);
                    uv0.Add(uv);
                    uv1.Add(info);
                    c.Add(tint);
                }
            }

            return Assemble("ScreenMultiplySurfaces", v, uv0, uv1, c, tri);
        }

        /// <summary>
        /// 暈のメッシュ。面の少し奥へ置くので、面に隠れた所は深度で落ち、周りの環だけが塗られる。
        /// uv0 = (-1..1 の xy, 面の半分の大きさに対する暈の倍率, 未使用)、uv1 / color は面と同じ。
        /// </summary>
        public static Mesh BuildGlow()
        {
            var v = new List<Vector3>();
            var uv0 = new List<Vector4>();
            var uv1 = new List<Vector4>();
            var c = new List<Color>();
            var tri = new List<int>();

            for (int s = 0; s < Seats.Length; s++)
            {
                ScreenSeat seat = Seats[s];
                Vector3 p = seat.Center + seat.Rotation * Vector3.forward * (seat.WidthM * 0.02f);
                Quaternion q = seat.Rotation;
                float hw = seat.WidthM * 0.5f * GlowScale;
                float hh = seat.WidthM * 0.5f / CrtReferenceScreen.CaseAspect * GlowScale;
                Color tint = Tints[Mathf.Clamp(seat.tint, 0, Tints.Length - 1)];
                var info = new Vector4(seat.rank, seat.delay, AnomalyEyesMesh.Hash(s + 311), s == 0 ? 1f : 0f);
                int i = v.Count;
                Vector2[] corners = { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };
                foreach (Vector2 k in corners)
                {
                    v.Add(p + q * new Vector3(k.x * hw, k.y * hh, 0f));
                    uv0.Add(new Vector4(k.x, k.y, GlowScale, 0f));
                    uv1.Add(info);
                    c.Add(tint);
                }
                tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            return Assemble("ScreenMultiplyGlow", v, uv0, uv1, c, tri);
        }

        private static Mesh Assemble(string name, List<Vector3> v, List<Vector4> uv0, List<Vector4> uv1,
                                     List<Color> c, List<int> tri)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetColors(c);
            mesh.SetTriangles(tri, 0);
            // 群れは常に頭の位置にあり、全周に広がる。
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 16f);
            return mesh;
        }
    }
}
