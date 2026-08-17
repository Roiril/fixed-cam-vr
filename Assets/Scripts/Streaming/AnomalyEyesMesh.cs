#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>目 1 つぶんの著作（メッシュを組むときに決まり、走行中は動かない）。</summary>
    public struct EyeSeat
    {
        /// <summary>群れの中心から見た向き（単位ベクトル）。</summary>
        public Vector3 dir;
        /// <summary>開く順位 0..1。<b>大きい目からの角度</b>（0 = 隣 / 1 = 真後ろ）。</summary>
        public float rank;
        /// <summary>出す / 出さないの籤 0..1。カットの <c>eyes</c> がこれ以上なら開く。</summary>
        public float presence;
        /// <summary>見かけの大きさ (度)。</summary>
        public float sizeDeg;
        /// <summary>大きい目か。</summary>
        public bool big;
    }

    /// <summary>
    /// <b>闇に浮かぶ目の座席表</b>を組む（<see cref="AnomalyEyes"/> が起動時に 1 度だけ張る）。
    ///
    /// 1 つの目 = 中心を向いた独立した 4 頂点の quad。目の形はシェーダが uv の中で描くので、
    /// ここが決めるのは<b>どこに・どれだけの大きさで・どの順に開くか</b>だけ。
    ///
    /// 頂点ストリーム（<see cref="IntroVeilShatterMesh"/> と同じ流儀 — 4 頂点すべてに同じ値を配る）:
    /// <list type="bullet">
    ///   <item><c>POSITION</c> … 群れの中心からの位置 (m)</item>
    ///   <item><c>TEXCOORD0</c> … quad の中の座標 (-1..1, -1..1)</item>
    ///   <item><c>TEXCOORD1</c> … <c>(順位, 大きい目か, 種, 籤)</c></item>
    /// </list>
    ///
    /// ⚠ <b>乱数を使わない</b>（刻み番号のハッシュ）。同じ版は必ず同じ並びになる ＝
    ///   「見え方を変えていないのに前と違う」が起きない（音の合成と同じ規律）。
    ///
    /// ⚠ <b>目は水平の帯に寄せる</b>（<see cref="HorizonBias"/>）。真上と真下は体験者がまず見ない方向で、
    ///   均等に撒くと 4 割がそこへ消える。参考画像（me2）も目は水平から少し上に集まっている。
    /// </summary>
    public static class AnomalyEyesMesh
    {
        /// <summary>大きい目を除いた数。実機で視界に入るのは 1/5 ほど。</summary>
        public const int EyeCount = 108;

        /// <summary>群れの半径 (m)。スクリーン（2m）より十分遠く、視差がほぼ出ない距離。</summary>
        public const float RadiusM = 9f;

        /// <summary>
        /// 水平への寄せ（1 = 均等 / 大きいほど水平の帯へ集まる）。
        /// 高さの分布 <c>y</c> を <c>sign(y)·|y|^bias</c> で歪めるだけなので、真上・真下も空にはならない。
        /// </summary>
        public const float HorizonBias = 1.6f;

        // ---- 大きい目（体験者が最初に気づく 1 つ）------------------------------------
        //
        // ⚠ **スクリーンの外・視界の内**。本編のスクリーンは頭の正面から水平 ±30.6° / 上 +10.4°
        //    （中心が 8° 下・半画角 18.4°）。右上の角の外へ斜めに置くと、
        //    ①スクリーンに重ならない ②Quest 3 の表示画角（水平 ±55° / 垂直 ±48° 前後）の内側 の両方を満たす。
        //    ⚠ 真横（38°）に大きく置くと**視界の端で半分切れる**（最初そう置いて切れた）。

        /// <summary>大きい目の水平の向き (度・正面から右)。</summary>
        public const float BigYawDeg = 34f;

        /// <summary>大きい目の高さ (度・正面から上)。スクリーンの上端（+10.4°）より上。</summary>
        public const float BigElevDeg = 18f;

        /// <summary>大きい目の見かけの大きさ (度)。腕を伸ばした手のひらくらい。</summary>
        public const float BigSizeDeg = 24f;

        /// <summary>
        /// 大きい目のまわりに空ける角度 (度)。<b>加算合成では重なった 2 つが 1 つの塊に見える</b>ので、
        /// 近すぎる目は外側へ押し出す。要の 1 つが「白い染み」に化けるのを防ぐ。
        /// </summary>
        public const float BigClearDeg = 21f;

        // ---- 残りの目 ---------------------------------------------------------------

        /// <summary>ふつうの目の見かけの大きさ (度)。</summary>
        public const float SizeMinDeg = 4.2f;
        public const float SizeMaxDeg = 7.8f;

        /// <summary>この籤を超えた目だけ「近くの大きな目」になる（参考画像 me3 の、視界を埋める目）。</summary>
        // ⚠ 大きくしすぎると隣と融合する（加算合成なので**重なった 2 つは 1 つの塊に見える**）。
        public const float NearShare = 0.11f;
        public const float NearScaleMin = 1.5f;
        public const float NearScaleMax = 2.1f;

        /// <summary>
        /// 目の縦横比（横 1 に対する縦）。<b>開き切った目が横 2 : 縦 1</b> になる値
        /// （参考画像 me1 / me2 の目の形）。⚠ シェーダの <c>_EyeAspect</c> と対 —
        /// ここを変えると虹彩が真円でなくなる。
        /// </summary>
        public const float AspectHeight = 1.0f;

        /// <summary>
        /// 目が quad の中で占める割合。<b>残りは暈（glow）のための余白</b>。
        /// ⚠ シェーダの <c>SHAPE_SCALE</c> と対 — 片方だけ直すと暈が切れるか、目が縮む。
        /// </summary>
        public const float ShapeScale = 0.74f;

        /// <summary>面の傾き (度)。ぜんぶ水平だと壁紙の模様に見える。</summary>
        public const float RollDeg = 23f;

        /// <summary>大きい目を含めた総数。</summary>
        public static int TotalCount => EyeCount + 1;

        /// <summary>大きい目の向き（正面 +Z・上 +Y の座標系）。</summary>
        public static Vector3 BigDir =>
            Quaternion.Euler(-BigElevDeg, BigYawDeg, 0f) * Vector3.forward;

        /// <summary>座席表を組む（メッシュを張らずに数だけ知りたい側 ＝ テスト・観測用）。</summary>
        public static EyeSeat[] BuildSeats()
        {
            var seats = new EyeSeat[TotalCount];
            Vector3 big = BigDir;

            // 0 番は大きい目。順位 0 ＝ 波の起点。
            seats[0] = new EyeSeat
            {
                dir = big,
                rank = 0f,
                presence = 0f,      // 籤に関係なく必ず出る（この 1 つが異変の要）
                sizeDeg = BigSizeDeg,
                big = true,
            };

            for (int i = 0; i < EyeCount; i++)
            {
                // 球面へ均等に撒いてから、水平の帯へ寄せる。
                float y = 1f - 2f * (i + 0.5f) / EyeCount;
                y = Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y), HorizonBias);
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float phi = i * 2.39996323f;                  // 黄金角
                var dir = new Vector3(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r).normalized;

                // 大きい目に重なる席は外へ押し出す（消さない — 数が減ると開眼の密度が落ちる）。
                float toBig = Vector3.Angle(dir, big);
                if (toBig < BigClearDeg)
                {
                    Vector3 axis = Vector3.Cross(big, dir);
                    if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(big, Vector3.up);
                    dir = (Quaternion.AngleAxis(BigClearDeg, axis.normalized) * big).normalized;
                }

                float h1 = Hash(i * 3 + 1);
                float h2 = Hash(i * 3 + 2);
                float h3 = Hash(i * 3 + 3);

                float size = Mathf.Lerp(SizeMinDeg, SizeMaxDeg, h1);
                if (h2 > 1f - NearShare) size *= Mathf.Lerp(NearScaleMin, NearScaleMax, h3);

                seats[i + 1] = new EyeSeat
                {
                    dir = dir,
                    // 大きい目からの角度。**波は大きい目から広がる** —
                    // 「1 つが呼んだ」に見せるための順序で、乱数にすると散発的な点滅になる。
                    rank = Mathf.Clamp01(Vector3.Angle(dir, big) / 180f),
                    presence = Hash(i * 7 + 11),
                    sizeDeg = size,
                    big = false,
                };
            }
            return seats;
        }

        /// <summary>座席表からメッシュを張る。</summary>
        public static Mesh Build(EyeSeat[] seats)
        {
            var m = new Mesh { name = "AnomalyEyes" };
            int n = seats.Length;
            var pos = new List<Vector3>(n * 4);
            var uv = new List<Vector2>(n * 4);
            var attr = new List<Vector4>(n * 4);
            var tris = new List<int>(n * 6);

            for (int i = 0; i < n; i++)
            {
                EyeSeat s = seats[i];
                Vector3 c = s.dir * RadiusM;

                // 面の基底。中心（体験者の頭）を向く ＝ 走行中の向き直しが要らない。
                Vector3 up0 = Mathf.Abs(s.dir.y) > 0.97f ? Vector3.forward : Vector3.up;
                Vector3 right = Vector3.Cross(up0, s.dir).normalized;
                Vector3 up = Vector3.Cross(s.dir, right).normalized;

                float roll = (Hash(i * 5 + 3) - 0.5f) * 2f * RollDeg * Mathf.Deg2Rad;
                float cr = Mathf.Cos(roll), sr = Mathf.Sin(roll);
                Vector3 rr = right * cr + up * sr;
                Vector3 uu = up * cr - right * sr;

                // ⚠ **quad は目より一回り大きく張る**（暈のための余白）。シェーダの SHAPE_SCALE と対で、
                //    ここを割っておくので sizeDeg は「目そのものの見かけの大きさ」のまま。
                float hw = RadiusM * Mathf.Tan(s.sizeDeg * 0.5f * Mathf.Deg2Rad) / ShapeScale;
                float hh = hw * AspectHeight;

                var a = new Vector4(s.rank, s.big ? 1f : 0f, Hash(i * 11 + 7), s.presence);
                int b = pos.Count;
                pos.Add(c - rr * hw - uu * hh); uv.Add(new Vector2(-1f, -1f));
                pos.Add(c + rr * hw - uu * hh); uv.Add(new Vector2(1f, -1f));
                pos.Add(c - rr * hw + uu * hh); uv.Add(new Vector2(-1f, 1f));
                pos.Add(c + rr * hw + uu * hh); uv.Add(new Vector2(1f, 1f));
                for (int k = 0; k < 4; k++) attr.Add(a);
                tris.Add(b + 0); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b + 2); tris.Add(b + 3); tris.Add(b + 1);
            }

            m.SetVertices(pos);
            m.SetUVs(0, uv);
            m.SetUVs(1, attr);
            m.SetTriangles(tris, 0);
            // 群れは頭に付いて動くので、視錐台カリングで丸ごと消えないよう球ごと内包させる。
            m.bounds = new Bounds(Vector3.zero, Vector3.one * (RadiusM * 2.4f));
            m.UploadMeshData(markNoLongerReadable: false);
            return m;
        }

        /// <summary>
        /// いま開いている目の数（<b>画に出た側</b>の観測）。
        /// 「重みを配った」ではなく「何個ぶんの目が実際に開いているか」を数える。
        /// </summary>
        public static int CountOpen(EyeSeat[] seats, float big01, float field01, float density01)
        {
            int n = 0;
            for (int i = 0; i < seats.Length; i++)
            {
                EyeSeat s = seats[i];
                if (s.big)
                {
                    if (big01 > AnomalyEyesLogic.OpenEpsilon) n++;
                    continue;
                }
                if (s.presence > density01) continue;
                if (AnomalyEyesLogic.EyeOpen(field01, s.rank) > AnomalyEyesLogic.OpenEpsilon) n++;
            }
            return n;
        }

        /// <summary>0..1 の決定的なハッシュ（整数 1 つから）。</summary>
        public static float Hash(int i)
        {
            uint x = (uint)i * 747796405u + 2891336453u;
            x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
            x = (x >> 22) ^ x;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
