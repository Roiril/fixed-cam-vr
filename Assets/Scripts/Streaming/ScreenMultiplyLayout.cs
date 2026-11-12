#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>増えるスクリーン 1 枚の席。角度は群れの前方を 0 に測る。</summary>
    public readonly struct ScreenSeat
    {
        public readonly float azDeg, elDeg, widthDeg, distM, tiltYawDeg, tiltPitchDeg, rollDeg;
        /// <summary>席の色。0 = 寒色 / 1 = 白に近い寒色（<see cref="ScreenMultiplyLayout.Tints"/>）。</summary>
        public readonly int tint;
        /// <summary>映す画の遅れ（取り込みの枚数）。</summary>
        public readonly int delay;
        /// <summary>点く順 0..1。</summary>
        public readonly float rank;
        /// <summary>帯の番号（<see cref="ScreenMultiplyLayout.BandEye"/> ほか）。帯ごとにゆっくり回る。</summary>
        public readonly int band;

        public ScreenSeat(float az, float el, float width, float dist, float tiltYaw, float tiltPitch, float roll,
                          int tint, int delay, float rank, int band)
        {
            azDeg = az; elDeg = el; widthDeg = width; distM = dist;
            tiltYawDeg = tiltYaw; tiltPitchDeg = tiltPitch; rollDeg = roll;
            this.tint = tint; this.delay = delay; this.rank = rank; this.band = band;
        }

        public Vector3 Direction => Quaternion.Euler(-elDeg, azDeg, 0f) * Vector3.forward;
        public Vector3 Center => Direction * distM;
        /// <summary>頭の方を向いた姿勢に、席ごとの傾きを足したもの。local +Z が奥。</summary>
        public Quaternion Rotation => Quaternion.Euler(-elDeg + tiltPitchDeg, azDeg + tiltYawDeg, rollDeg);
        public float WidthM => 2f * distM * Mathf.Tan(widthDeg * 0.5f * Mathf.Deg2Rad);
    }

    /// <summary>
    /// <b>目の前にメインスクリーンが増える</b>演出の座席とメッシュ（参考 <c>mul.jpg</c>・走馬灯）。
    /// 表は <c>tools/make-screen-wall.py</c> が焼く。面は頭を巻く帯に並び、帯ごとにゆっくり回る。
    /// </summary>
    public static partial class ScreenMultiplyLayout
    {
        /// <summary>帯の番号。<c>make-screen-wall.py</c> の EYE / UPPER / TOP / FAR と対。</summary>
        public const int BandEye = 0, BandUpper = 1, BandTop = 2, BandFar = 3;
        public const int BandCount = 4;

        /// <summary>
        /// 帯ごとの回る速さ（度/秒。正は右回り ＝ 方位が増える向き）。
        /// ⚠ 隣の帯は必ず逆向き。視野全体が同じ向きに流れると VR で酔う。これより速くしない。
        /// </summary>
        public static float DriftRateDegPerSec(int band) => band switch
        {
            BandEye => 0.9f,
            BandUpper => -1.2f,
            BandTop => 0.7f,
            _ => 0f,
        };

        /// <summary>
        /// 帯 <paramref name="band"/> が出番の頭から <paramref name="sec"/> 秒で回った角度（度）。
        /// 面の頂点（シェーダの <c>_WallDrift</c>）・0 番の方向・音の方角はすべてここを通す。
        /// ホラー軽減（<see cref="HorrorRelief.Enabled"/>）では回さない。
        /// </summary>
        public static float DriftDeg(int band, float sec)
            => HorrorRelief.Enabled ? 0f : DriftRateDegPerSec(band) * sec;

        /// <summary>帯の回転を当てた席の中心（群れの座標）。</summary>
        public static Vector3 DriftedCenter(in ScreenSeat seat, float sec)
            => Quaternion.Euler(0f, DriftDeg(seat.band, sec), 0f) * seat.Center;

        /// <summary>点いた直後の色。<see cref="WarmSettleSec"/> かけて席の色へ落ち着く（波の先頭だけが暖かい）。</summary>
        public static readonly Color WarmTint = new(1.00f, 0.60f, 0.32f, 1f);

        /// <summary>点いた直後の橙から席の色へ落ち着くまでの秒。両シェーダが同じ式で読む。</summary>
        public const float WarmSettleSec = 1.3f;

        /// <summary>電源の切れた管に残る光の点が消えるまでの秒。</summary>
        public const float AfterglowSec = 0.5f;

        /// <summary>
        /// 点いてから <paramref name="secSinceOpen"/> 秒の管の色（シェーダの式と対）。
        /// <c>t = saturate(秒 / WarmSettleSec)</c>、<c>lerp(WarmTint, 席の色, smoothstep)</c>。
        /// </summary>
        public static Color TintAt(int tint, float secSinceOpen)
        {
            float t = Mathf.Clamp01(secSinceOpen / WarmSettleSec);
            return Color.Lerp(WarmTint, Tints[Mathf.Clamp(tint, 0, Tints.Length - 1)], t * t * (3f - 2f * t));
        }

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
        /// <summary>これより遠い席は霧の奥の段（<c>make-screen-wall.py</c> の FAR と対）。</summary>
        public const float FarTierM = 6.6f;
        /// <summary>シェーダへ配る「点いた時刻」の表の大きさ。席の数はこれ以下。</summary>
        public const int MaxSeats = 160;

        public static bool IsFar(in ScreenSeat seat) => seat.distM >= FarTierM;

        /// <summary>
        /// 席 <paramref name="index"/> がどれだけ開いているか。シェーダの式と対（0 番は大きい目の開き、ほかは順位）。
        /// </summary>
        public static float SeatOpen(int index, float big01, float field01, float span)
        {
            if (index == 0) return big01;
            float s = Mathf.Max(0.005f, span);
            return Mathf.Clamp01((field01 * (1f + s) - Seats[index].rank) / s);
        }

        /// <summary>落ち着いた後の席の色。寒色と淡い寒色だけ（暖色は点いた直後の <see cref="WarmTint"/> だけが持つ）。</summary>
        public static readonly Color[] Tints =
        {
            new(0.58f, 0.82f, 1.00f, 1f),
            new(0.86f, 0.95f, 1.00f, 1f),
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
        /// uv1 = (順位, 遅れ, 席の乱数, 0 番か)、uv2 = (席の中心 xyz, 席の番号)、uv3 = (帯の番号, 陰り, 0, 0)、
        /// color = 席の色（a は霧の奥の段で 0）。色は点いた時刻からシェーダが橙 → 席の色へ動かす。
        /// </summary>
        public static Mesh BuildSurfaces()
        {
            var v = new List<Vector3>();
            var uv0 = new List<Vector4>();
            var uv1 = new List<Vector4>();
            var uv2 = new List<Vector4>();
            var uv3 = new List<Vector4>();
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
                var home = new Vector4(p.x, p.y, p.z, s);
                float near = IsFar(seat) ? 0f : 1f;
                tint.a = near;
                float shade = 1f;

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
                    // 上の面は照らされ、下の面は陰る。陰りは uv3.y（色は点いた時刻で動くので焼き込まない）。
                    shade = e == 2 ? 1f : e == 0 ? 0.35f : 0.7f;
                    AddVertex(p + q * f[e], new Vector4(0, 0, 1f, 0f));
                    AddVertex(p + q * f[n], new Vector4(0, 0, 1f, 0f));
                    AddVertex(p + q * b[n], new Vector4(0, 0, 1f, 1f));
                    AddVertex(p + q * b[e], new Vector4(0, 0, 1f, 1f));
                    tri.AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });
                }

                shade = 1f;
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
                    uv2.Add(home);
                    uv3.Add(new Vector4(seat.band, shade, 0f, 0f));
                    c.Add(tint);
                }
            }

            return Assemble("ScreenMultiplySurfaces", v, uv0, uv1, uv2, uv3, c, tri);
        }

        /// <summary>
        /// 暈のメッシュ。面の少し奥へ置くので、面に隠れた所は深度で落ち、周りの環だけが塗られる。
        /// uv0 = (-1..1 の xy, 面の半分の大きさに対する暈の倍率, 未使用)、uv1 / uv2 / uv3 / color は面と同じ。
        /// </summary>
        public static Mesh BuildGlow()
        {
            var v = new List<Vector3>();
            var uv0 = new List<Vector4>();
            var uv1 = new List<Vector4>();
            var uv2 = new List<Vector4>();
            var uv3 = new List<Vector4>();
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
                Vector3 center = seat.Center;
                var home = new Vector4(center.x, center.y, center.z, s);
                tint.a = IsFar(seat) ? 0f : 1f;
                int i = v.Count;
                Vector2[] corners = { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };
                foreach (Vector2 k in corners)
                {
                    v.Add(p + q * new Vector3(k.x * hw, k.y * hh, 0f));
                    uv0.Add(new Vector4(k.x, k.y, GlowScale, 0f));
                    uv1.Add(info);
                    uv2.Add(home);
                    uv3.Add(new Vector4(seat.band, 1f, 0f, 0f));
                    c.Add(tint);
                }
                tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            return Assemble("ScreenMultiplyGlow", v, uv0, uv1, uv2, uv3, c, tri);
        }

        private static Mesh Assemble(string name, List<Vector3> v, List<Vector4> uv0, List<Vector4> uv1,
                                     List<Vector4> uv2, List<Vector4> uv3, List<Color> c, List<int> tri)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetUVs(2, uv2);
            mesh.SetUVs(3, uv3);
            mesh.SetColors(c);
            mesh.SetTriangles(tri, 0);
            // 群れは常に頭の位置にあり、全周に広がる。奥から寄る分と、床に映した分も入れる。
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 40f);
            return mesh;
        }
    }
}
