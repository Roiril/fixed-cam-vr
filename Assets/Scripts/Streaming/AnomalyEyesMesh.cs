#nullable enable

using System;
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
        /// <summary>見かけの大きさ ＝ 目の横幅 (度)。</summary>
        public float sizeDeg;
        /// <summary>大きい目か。</summary>
        public bool big;

        // ---- 個体差（参考画像の「全部が同じ形をしていない」を作る）--------------------
        /// <summary>
        /// quad の 縦/横。<b>目そのものの縦横比から逆算した値</b>で、直接いじらない
        /// （<see cref="AnomalyEyesMesh.EyeRatioMin"/> 参照）。
        /// </summary>
        public float aspect;
        /// <summary>上瞼の上がり方（quad 半分に対する比）。</summary>
        public float lidUp;
        /// <summary>下瞼の下がり方。</summary>
        public float lidDown;
        /// <summary>虹彩の半径（quad の横半分を 1 とする）。<b>大きい目ほど白目を食う</b>。</summary>
        public float irisR;
        /// <summary>開き切る量 0..1（1 未満なら半開きのまま止まる）。</summary>
        public float openMax;
        /// <summary>目尻の高さ違い（片方を吊る量）。</summary>
        public float skew;
        /// <summary>瞳孔の横ずれ（虹彩半径に対する比）。</summary>
        public float pupilOffset;
        /// <summary>
        /// 見かけの大きさ 0（いちばん小さい）〜1（視界を埋める）。
        /// 輪郭の強さ・瞳孔の潰れ・<b>欠けの粒の細かさ</b>に効く。
        /// </summary>
        public float sizeNorm;
        /// <summary>面の傾き (度)。</summary>
        public float rollDeg;
    }

    /// <summary>
    /// <b>闇に浮かぶ目の座席表</b>を組む（<see cref="AnomalyEyes"/> が起動時に 1 度だけ張る）。
    ///
    /// 1 つの目 = 中心を向いた独立した 4 頂点の quad。目の形はシェーダが uv の中で描くので、
    /// ここが決めるのは<b>どこに・どれだけの大きさで・どんな形で・どの順に開くか</b>。
    ///
    /// 頂点ストリーム（4 頂点すべてに同じ値を配る）:
    /// <list type="bullet">
    ///   <item><c>POSITION</c> … 群れの中心からの位置 (m)</item>
    ///   <item><c>TEXCOORD0</c> … quad の中の座標 (-1..1, -1..1)</item>
    ///   <item><c>TEXCOORD1</c> … <c>(順位, 大きい目か, 種, 籤)</c></item>
    ///   <item><c>TEXCOORD2</c> … <c>(縦横比, 上瞼, 下瞼, 虹彩半径)</c></item>
    ///   <item><c>TEXCOORD3</c> … <c>(開き切る量, 目尻の傾き, 瞳孔のずれ, 大きさ 0..1)</c></item>
    /// </list>
    ///
    /// ⚠ <b>乱数を使わない</b>（刻み番号のハッシュ）。同じ版は必ず同じ並びになる ＝
    ///   「見え方を変えていないのに前と違う」が起きない（音の合成と同じ規律）。
    ///
    /// ⚠⚠ <b>座席は小さい順に並べる。</b> シェーダが前乗算アルファなので、**後に描いた目が手前**。
    ///   大きい目 ＝ 近い目が後に来るよう並べておかないと、小さい目が大きい目を隠す
    ///   （＝ 遠近が逆に見える）。
    ///
    /// ⚠ <b>目は水平の帯に寄せる</b>（<see cref="HorizonBias"/>）。真上と真下は体験者がまず見ない方向。
    /// </summary>
    public static class AnomalyEyesMesh
    {
        /// <summary>大きい目を除いた数。実機の視界（水平 102°）に入るのは 1/6 ほど。</summary>
        public const int EyeCount = 150;

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
        //    ⚠ 真横に大きく置くと**視界の端で半分切れる**（最初そう置いて切れた）。

        /// <summary>大きい目の水平の向き (度・正面から右)。</summary>
        public const float BigYawDeg = 30f;

        /// <summary>大きい目の高さ (度・正面から上)。スクリーンの上端（+10.4°）より上。</summary>
        public const float BigElevDeg = 21f;

        /// <summary>大きい目の見かけの大きさ (度)。</summary>
        public const float BigSizeDeg = 28f;

        /// <summary>
        /// 大きい目の縁からさらに空ける角度 (度)。要の 1 つが隣と重なって読めなくなるのを防ぐ。
        /// </summary>
        public const float BigClearDeg = 6f;

        /// <summary>
        /// その大きさの目が、大きい目の中心からどれだけ離れていなければならないか (度)。
        /// <b>互いの半幅の和 ＋ 余白</b>。⚠ 定数にすると、視界を埋める大きな目が
        /// 大きい目に覆いかぶさって「白い染み」になる（実際になった）。
        /// </summary>
        public static float ClearDegFor(float sizeDeg)
            => BigSizeDeg * 0.5f + sizeDeg * 0.5f + BigClearDeg;

        // ---- 残りの目 — **大小の差を極端に付ける**（2026-08-17・LEDGER 0076）-----------
        //
        // ⚠ ここが「まだ全然足りていない」の主因だった。参考画像の目は**枠の幅の 18〜29%**
        //    を占めるのに、こちらは 5〜10% しかなく、遠くの光点にしか見えなかった（実測）。
        //    4 段に分けて、視界を埋める目を必ず何個か混ぜる。

        /// <summary>大きさの 4 段（横幅・度）と、その割合。合計 1.0。</summary>
        public static readonly (float min, float max, float share)[] SizeTiers =
        {
            (3.0f, 6.0f, 0.20f),     // 遠くの点。密度と奥行きを作る
            (10f, 18f, 0.50f),       // ふつうの目（参考画像の中心的な大きさ）
            (22f, 34f, 0.24f),       // 近い目
            (42f, 66f, 0.06f),       // **視界を埋める目**。端で切れてよい（me3 の姿）
        };

        /// <summary>面の傾き (度)。ぜんぶ水平だと壁紙の模様に見える。</summary>
        public const float RollDeg = 38f;

        /// <summary>大きい目の傾き (度)。要の 1 つは水平に近く保つ。</summary>
        public const float BigRollDeg = 10f;

        /// <summary>
        /// <b>目そのものの縦横比</b>（高さ ÷ 横幅）。細い目 0.34 〜 丸い目 0.66。
        /// 参考画像（me1 / me2）の目はおよそ 0.4〜0.6。
        /// ⚠ これは quad の比ではない。quad の比は <see cref="QuadAspect"/> が逆算する。
        /// </summary>
        public const float EyeRatioMin = 0.34f;
        public const float EyeRatioMax = 0.66f;

        /// <summary>
        /// 目の縦横比と瞼の開きから、quad の 縦/横 を逆算する。
        /// 目の高さ ＝ (上瞼 ＋ 下瞼) × quad の半分の高さ / 横幅 ＝ 2 × quad の半分の幅、なので
        /// <c>比 = (上 ＋ 下) × aspect / 2</c>。
        /// </summary>
        public static float QuadAspect(float eyeRatio, float lidUp, float lidDown)
            => 2f * eyeRatio / Mathf.Max(lidUp + lidDown, 1e-3f);

        /// <summary>
        /// 目が quad の中で占める割合。<b>残りは暈のための余白</b>。
        /// ⚠ シェーダの <c>SHAPE_SCALE</c> と対 — 片方だけ直すと暈が切れるか、目が縮む。
        /// </summary>
        public const float ShapeScale = 0.74f;

        /// <summary>大きい目を含めた総数。</summary>
        public static int TotalCount => EyeCount + 1;

        /// <summary>大きい目の向き（正面 +Z・上 +Y の座標系）。</summary>
        public static Vector3 BigDir =>
            Quaternion.Euler(-BigElevDeg, BigYawDeg, 0f) * Vector3.forward;

        /// <summary>座席表を組む（メッシュを張らずに数だけ知りたい側 ＝ テスト・観測用）。</summary>
        public static EyeSeat[] BuildSeats()
        {
            var seats = new List<EyeSeat>(TotalCount);
            Vector3 big = BigDir;

            for (int i = 0; i < EyeCount; i++)
            {
                // 球面へ均等に撒いてから、水平の帯へ寄せる。
                float y = 1f - 2f * (i + 0.5f) / EyeCount;
                y = Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y), HorizonBias);
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float phi = i * 2.39996323f;                  // 黄金角
                var dir = new Vector3(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r).normalized;

                float size = SizeFor(i);

                // 大きい目に重なる席は外へ押し出す（消さない — 数が減ると開眼の密度が落ちる）。
                float clear = ClearDegFor(size);
                if (Vector3.Angle(dir, big) < clear)
                {
                    Vector3 axis = Vector3.Cross(big, dir);
                    if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(big, Vector3.up);
                    dir = (Quaternion.AngleAxis(clear, axis.normalized) * big).normalized;
                }

                seats.Add(MakeSeat(i, dir, size, big, isBig: false));
            }

            // 大きい目は最後に足す（＝ いちばん手前に描く）。順位 0 ＝ 波の起点。
            seats.Add(MakeSeat(-1, big, BigSizeDeg, big, isBig: true));

            EyeSeat[] arr = seats.ToArray();
            // ⚠⚠ **小さい順に並べる。** 前乗算アルファでは後に描いた方が手前なので、
            //    大きい目（＝ 近い目）が後ろに来るように並べないと遠近が逆に見える。
            //    大きい目は同率でも最後に来る（安定ソート ＋ 末尾に足してある）。
            Array.Sort(arr, (a, b) => a.sizeDeg.CompareTo(b.sizeDeg));

            return arr;
        }

        /// <summary>4 段の分布から <paramref name="i"/> 番目の目の横幅 (度) を引く。</summary>
        public static float SizeFor(int i)
        {
            float pick = Hash(i * 3 + 2);
            float acc = 0f;
            foreach ((float min, float max, float share) in SizeTiers)
            {
                acc += share;
                if (pick <= acc) return Mathf.Lerp(min, max, Hash(i * 3 + 1));
            }
            (float lo, float hi, float _) = SizeTiers[SizeTiers.Length - 1];
            return Mathf.Lerp(lo, hi, Hash(i * 3 + 1));
        }

        private static EyeSeat MakeSeat(int i, Vector3 dir, float sizeDeg, Vector3 big, bool isBig)
        {
            // ⚠ 大きい目も同じ式で個体差を持つ（別扱いにすると 1 つだけ「作り物」に見える）。
            int k = isBig ? 9973 : i;
            float t = Mathf.InverseLerp(3f, 66f, sizeDeg);   // 大きさの目安（0..1）
            return new EyeSeat
            {
                dir = dir,
                // 大きい目からの角度。**波は大きい目から広がる** —
                // 「1 つが呼んだ」に見せるための順序で、乱数にすると散発的な点滅になる。
                rank = isBig ? 0f : Mathf.Clamp01(Vector3.Angle(dir, big) / 180f),
                presence = isBig ? 0f : Hash(k * 7 + 11),
                sizeDeg = sizeDeg,
                big = isBig,
                // ⚠⚠ quad の縦横比は**目の縦横比から逆算する**（下の EyeRatio* 参照）。
                //    ここへ目の比をそのまま入れると、瞼の開き（lidUp+lidDown ≒ 1）が二重に掛かって
                //    **細長い笹の葉**になる（実際になった）。
                aspect = QuadAspect(Mathf.Lerp(EyeRatioMin, EyeRatioMax, Hash(k * 13 + 5)),
                                    Mathf.Lerp(0.46f, 0.72f, Hash(k * 13 + 6)),
                                    Mathf.Lerp(0.28f, 0.55f, Hash(k * 13 + 7))),
                lidUp = Mathf.Lerp(0.46f, 0.72f, Hash(k * 13 + 6)),
                lidDown = Mathf.Lerp(0.28f, 0.55f, Hash(k * 13 + 7)),
                // 大きい目ほど虹彩が白目を食う（参考 me3 の「巨大な虹彩と黒い内部」）。
                irisR = Mathf.Lerp(0.30f, 0.62f, Mathf.Clamp01(t * 1.15f)) *
                        Mathf.Lerp(0.85f, 1.15f, Hash(k * 13 + 8)),
                // 半開きのまま止まる目を混ぜる（全部が全開だと機械に見える）。
                openMax = Mathf.Lerp(0.62f, 1.0f, Hash(k * 17 + 3)),
                skew = (Hash(k * 17 + 4) - 0.5f) * 0.30f,
                pupilOffset = (Hash(k * 17 + 5) - 0.5f) * 0.34f,
                sizeNorm = t,
                // ⚠ 大きい目だけ傾きを抑える。強く傾くと「こちらを見ている」より「転がっている」に見える。
                rollDeg = (Hash(k * 5 + 3) - 0.5f) * 2f * (isBig ? BigRollDeg : RollDeg),
            };
        }

        /// <summary>座席表からメッシュを張る。</summary>
        public static Mesh Build(EyeSeat[] seats)
        {
            var m = new Mesh { name = "AnomalyEyes" };
            int n = seats.Length;
            var pos = new List<Vector3>(n * 4);
            var uv = new List<Vector2>(n * 4);
            var attr = new List<Vector4>(n * 4);
            var form = new List<Vector4>(n * 4);
            var form2 = new List<Vector4>(n * 4);
            var tris = new List<int>(n * 6);

            for (int i = 0; i < n; i++)
            {
                EyeSeat s = seats[i];
                Vector3 c = s.dir * RadiusM;

                // 面の基底。中心（体験者の頭）を向く ＝ 走行中の向き直しが要らない。
                Vector3 up0 = Mathf.Abs(s.dir.y) > 0.97f ? Vector3.forward : Vector3.up;
                Vector3 right = Vector3.Cross(up0, s.dir).normalized;
                Vector3 upv = Vector3.Cross(s.dir, right).normalized;

                float roll = s.rollDeg * Mathf.Deg2Rad;
                float cr = Mathf.Cos(roll), sr = Mathf.Sin(roll);
                Vector3 rr = right * cr + upv * sr;
                Vector3 uu = upv * cr - right * sr;

                // ⚠ **quad は目より一回り大きく張る**（暈のための余白）。シェーダの SHAPE_SCALE と対で、
                //    ここを割っておくので sizeDeg は「目そのものの見かけの大きさ」のまま。
                float hw = RadiusM * Mathf.Tan(s.sizeDeg * 0.5f * Mathf.Deg2Rad) / ShapeScale;
                float hh = hw * s.aspect;

                var a = new Vector4(s.rank, s.big ? 1f : 0f, Hash(i * 11 + 7), s.presence);
                var f = new Vector4(s.aspect, s.lidUp, s.lidDown, s.irisR);
                var f2 = new Vector4(s.openMax, s.skew, s.pupilOffset, s.sizeNorm);
                int b = pos.Count;
                pos.Add(c - rr * hw - uu * hh); uv.Add(new Vector2(-1f, -1f));
                pos.Add(c + rr * hw - uu * hh); uv.Add(new Vector2(1f, -1f));
                pos.Add(c - rr * hw + uu * hh); uv.Add(new Vector2(-1f, 1f));
                pos.Add(c + rr * hw + uu * hh); uv.Add(new Vector2(1f, 1f));
                for (int k = 0; k < 4; k++) { attr.Add(a); form.Add(f); form2.Add(f2); }
                tris.Add(b + 0); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b + 2); tris.Add(b + 3); tris.Add(b + 1);
            }

            m.SetVertices(pos);
            m.SetUVs(0, uv);
            m.SetUVs(1, attr);
            m.SetUVs(2, form);
            m.SetUVs(3, form2);
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
                if (AnomalyEyesLogic.EyeOpen(field01, s.rank) * s.openMax > AnomalyEyesLogic.OpenEpsilon) n++;
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
