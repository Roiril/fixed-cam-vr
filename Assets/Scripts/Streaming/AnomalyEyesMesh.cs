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
        /// <summary>目そのものの縦横比（高さ ÷ 横幅）。詰め込みの判定に使う。</summary>
        public float eyeRatio;
        /// <summary>上瞼の弧の半径（小さいほど尖る）。<b>上下で別々</b>にすると同じ型に見えなくなる。</summary>
        public float arcUp;
        /// <summary>下瞼の弧の半径。</summary>
        public float arcDown;
        /// <summary>横の歪み（目頭と目尻で丸み・尖りが変わる）。</summary>
        public float warpX;
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
    ///   <item><c>TEXCOORD4</c> … <c>(上瞼の弧, 下瞼の弧, 横の歪み, -)</c></item>
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
        /// <summary>
        /// 席の候補の数。<b>ここから重ならないものだけを採る</b>ので、実際に出る数はこれより少ない
        /// （<see cref="MaxEyes"/> と球面の詰め方で決まる）。候補が多いほど隙間が埋まる。
        /// </summary>
        public const int CandidateCount = 900;

        /// <summary>採る上限（大きい目を含む）。詰め切れなければこれより少なくなる。</summary>
        public const int MaxEyes = 190;

        /// <summary>
        /// 隣との間隔（互いの半幅の和に対する倍率）。<b>1.0 で「触れる」・それ以上で隙間が空く</b>。
        ///
        /// ⚠⚠ <b>目は重ねない</b>（2026-08-17・ユーザー判定「目が重なってしまってるのは違和感がある」）。
        ///   参考画像でも目は互いに触れておらず、必ず闇の隙間がある。重なると
        ///   ①手前の目が奥の目を切り取って「割れた目」に見える
        ///   ②2 つで 1 つの塊に読める — どちらも「たくさんの目に見られている」を壊す。
        /// </summary>
        public const float SepFactor = 1.02f;

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

        /// <summary>
        /// 大きい目の高さ (度・正面から上)。スクリーンの上端（+10.4°）より上。
        /// ⚠ 2026-09-19 に 21 → 23。目が丸くなった（縦横比 0.80 まで）ので、下端がスクリーンへ掛からないぶん上げた。
        /// </summary>
        public const float BigElevDeg = 23f;

        /// <summary>大きい目の見かけの大きさ (度)。</summary>
        public const float BigSizeDeg = 28f;

        // ---- 残りの目 — **大小の差を極端に付ける**（2026-08-17・LEDGER 0076）-----------
        //
        // ⚠ ここが「まだ全然足りていない」の主因だった。参考画像の目は**枠の幅の 18〜29%**
        //    を占めるのに、こちらは 5〜10% しかなく、遠くの光点にしか見えなかった（実測）。
        //    4 段に分けて、視界を埋める目を必ず何個か混ぜる。

        /// <summary>
        /// 大きさの段（横幅・度）と、その割合。合計 1.0。
        ///
        /// ⚠⚠ <b>中型（10〜22°）が主成分</b>（2026-08-17 の 2 巡目）。参考画像の目は
        /// 枠幅の 12〜13% が中央値で、こちらは 6.5% しかなかった ＝ <b>小さい目で隙間を埋めすぎ</b>。
        /// 小さい目を減らし、中型を増やすと中央値が上がる。
        /// ⚠ 極大（40°超）は 5% だけ。多いと白の面積が跳ね上がる。
        /// </summary>
        public static readonly (float min, float max, float share)[] SizeTiers =
        {
            (4.0f, 7.0f, 0.10f),     // 遠くの点。奥行きを作る（隙間を埋め尽くさない）
            (9f, 15f, 0.32f),        // 中型（小）
            (15f, 23f, 0.36f),       // **中型（大）＝ 参考画像の主成分**
            (25f, 36f, 0.17f),       // 近い目
            (40f, 54f, 0.05f),       // **視界を埋める目**。端で切れてよい（me3 の姿）
        };

        /// <summary>面の傾き (度)。ぜんぶ水平だと壁紙の模様に見える。</summary>
        public const float RollDeg = 38f;

        /// <summary>大きい目の傾き (度)。要の 1 つは水平に近く保つ。</summary>
        public const float BigRollDeg = 10f;

        /// <summary>
        /// <b>目そのものの縦横比</b>（高さ ÷ 横幅）。細い目 0.50 〜 丸い目 0.80。
        /// ⚠ 2026-09-19（<c>canon/LEDGER.md</c> 0237）に 0.34〜0.66 から上げた。参考画像
        /// <c>tools/eyes-ref/dejime.jpg</c> の目は 630×520 ≒ 0.8 と丸く、同心の環（輪郭 → 白目 → 虹彩の縁 →
        /// 内側の環 → 芯）が読めるだけの丈が要る。細い目では環が瞼に切られて帯の束にしか見えない。
        /// ⚠ これは quad の比ではない。quad の比は <see cref="QuadAspect"/> が逆算する。
        /// </summary>
        public const float EyeRatioMin = 0.50f;
        public const float EyeRatioMax = 0.80f;

        /// <summary>虹彩の直径の上限（目の丈に対する比）。参考画像は 0.69。</summary>
        public const float IrisCapOfRatio = 0.72f;

        /// <summary>
        /// 目の縦横比と瞼の開きから、quad の 縦/横 を逆算する。
        /// 目の高さ ＝ (上瞼 ＋ 下瞼) × quad の半分の高さ / 横幅 ＝ 2 × quad の半分の幅、なので
        /// <c>比 = (上 ＋ 下) × aspect / 2</c>。
        /// </summary>
        public static float QuadAspect(float eyeRatio, float lidUp, float lidDown)
            => 2f * eyeRatio / Mathf.Max(lidUp + lidDown, 1e-3f);

        /// <summary>
        /// 目が quad の中で占める割合（横）。
        /// ⚠ シェーダの <c>SHAPE_SCALE</c> と対 — 片方だけ直すと目が縮むか、版が quad からはみ出す。
        /// ⚠⚠ <b>0.74 → 0.90（0239・R057）。</b> 0.74 の余白は版画の白目の暈のためのもので、版の目には要らない。
        ///   0.90 だと quad の横幅 ≒ 版のタイルの横幅（縦は <see cref="RiftExtend"/> 0.8 で ≒ タイルの縦）。
        ///   塗る面積が 32% 減る（89 個が開いた 2 秒で 48 fps → 目標 60）。開きかけの散らばり（0.55）が quad の縁で切れるが 0.27 秒だけ。
        /// </summary>
        public const float ShapeScale = 0.90f;

        /// <summary>
        /// quad の縦の余白（目の丈の単位・2026-09-19・<c>canon/LEDGER.md</c> 0237）。
        /// 目の上下に「ハザマ」の柱が漏れるための余白で、シェーダはこの倍率で uv.y を戻してから目の式へ入れる。
        /// ⚠ シェーダの <c>RIFT_EXTEND</c> と対 — 片方だけ直すと目が縦に潰れるか伸びる。
        /// ⚠⚠ <b>1.5 → 0.8（0239・R057）。</b> 版の柱はタイルの縦（目の丈の ±0.67）にしか無いのに 1.5 まで張っていて、
        ///   塗る面積が倍 ＝ 89 個が開いた 2 秒で実機が 35 fps だった（版を引く前に捨てても戻らなかった ＝ 重いのは面積）。
        /// ⚠ 当たり判定（<see cref="Separated"/>）は目の楕円で測るので、この余白は隣の quad と
        ///   重なってよい（柱は疎らで前乗算なので、重なった所は手前の目が隠すだけ）。
        /// </summary>
        public const float RiftExtend = 0.8f;

        /// <summary>大きい目の向き（正面 +Z・上 +Y の座標系）。</summary>
        public static Vector3 BigDir =>
            Quaternion.Euler(-BigElevDeg, BigYawDeg, 0f) * Vector3.forward;

        /// <summary>
        /// 座席表を組む（メッシュを張らずに数だけ知りたい側 ＝ テスト・観測用）。
        ///
        /// <b>重ならないように詰める</b>（2026-08-17・ユーザー判定「目が重なってしまってるのは違和感がある」）。
        /// 手順は 3 つだけ:
        /// <list type="number">
        ///   <item>候補を球面へ均等に撒く（<see cref="CandidateCount"/>・水平の帯へ寄せる）</item>
        ///   <item><b>大きい順に</b>置いていく（大きい目が先に場所を取る ＝ 参考画像の姿）</item>
        ///   <item>既に置いた目と<b>互いの半幅の和 ×<see cref="SepFactor"/></b> 以上離れていれば採る</item>
        /// </list>
        /// ⚠ 大きい順に置くのは**詰め込みの都合**。後から小さい順へ並べ替えて返す（描画順のため）。
        /// ⚠ 押しのけ（重なった席を外へずらす）はやめた。ずらすと**別の目と重なる**だけで、
        ///   同じ問題が場所を変えて出る。<b>置けないなら置かない</b>方が確実。
        /// </summary>
        public static EyeSeat[] BuildSeats()
        {
            Vector3 big = BigDir;

            // 候補（向き ＋ 望む大きさ）。乱数は使わない。
            var dirs = new Vector3[CandidateCount];
            var sizes = new float[CandidateCount];
            var order = new int[CandidateCount];
            for (int i = 0; i < CandidateCount; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / CandidateCount;
                y = Mathf.Sign(y) * Mathf.Pow(Mathf.Abs(y), HorizonBias);
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float phi = i * 2.39996323f;                  // 黄金角
                dirs[i] = new Vector3(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r).normalized;
                sizes[i] = SizeFor(i);
                order[i] = i;
            }
            // 大きい順（同率は番号順 ＝ 決定的）。
            Array.Sort(order, (a, b) =>
            {
                int c = sizes[b].CompareTo(sizes[a]);
                return c != 0 ? c : a.CompareTo(b);
            });

            // 大きい目が最初に場所を取る（要の 1 つは必ず出る）。
            var seats = new List<EyeSeat>(MaxEyes) { MakeSeat(-1, big, BigSizeDeg, big, isBig: true) };

            for (int k = 0; k < order.Length && seats.Count < MaxEyes; k++)
            {
                int i = order[k];
                EyeSeat cand = MakeSeat(i, dirs[i], sizes[i], big, isBig: false);
                bool fits = true;
                for (int j = 0; j < seats.Count; j++)
                {
                    if (!Separated(cand, seats[j])) { fits = false; break; }
                }
                if (fits) seats.Add(cand);
            }

            EyeSeat[] arr = seats.ToArray();
            // **小さい順に並べる。** 前乗算アルファでは後に描いた方が手前。重ならないので
            // 見た目は変わらないが、順序を大きさで決めておくと万一触れたときも遠近が正しく出る。
            Array.Sort(arr, (a, b) => a.sizeDeg.CompareTo(b.sizeDeg));
            return arr;
        }

        /// <summary>
        /// 2 つの目が重なっていないか。<b>楕円どうしで測る</b>（テスト・詰め込み・診断で同じ式を使う）。
        ///
        /// ⚠ <b>半幅の和で測ると詰まらない。</b> 目は横 2 : 縦 1 の楕円なので、上下に並ぶ相手に対しては
        ///   半幅の和は 2 倍以上の余白を要求する。実際に一度そうして**密度が半分に落ちた**。
        ///   相手の方向へ向けた<b>その楕円の半径</b>どうしを足す。
        /// ⚠ 目（アーモンド）は楕円の内側にあるので、この判定は必ず<b>安全側</b>に外れる
        ///   ＝ 通れば必ず隙間が見える。
        /// </summary>
        public static bool Separated(in EyeSeat a, in EyeSeat b)
        {
            float d = Vector3.Angle(a.dir, b.dir);
            if (d <= 0.001f) return false;
            return d >= (RadiusToward(a, b.dir) + RadiusToward(b, a.dir)) * SepFactor;
        }

        /// <summary>相手の方向へ向けた、その目の楕円の半径 (度)。</summary>
        public static float RadiusToward(in EyeSeat s, Vector3 other)
        {
            Basis(s, out Vector3 right, out Vector3 up);
            // 相手へ向かう方向を、この目の面へ落とす。
            Vector3 t = other - Vector3.Dot(other, s.dir) * s.dir;
            float a = s.sizeDeg * 0.5f;                       // 横の半径
            float b = a * Mathf.Max(s.eyeRatio, 0.05f);       // 縦の半径
            if (t.sqrMagnitude < 1e-10f) return a;
            t.Normalize();
            float c = Vector3.Dot(t, right), q = Vector3.Dot(t, up);
            float den = Mathf.Sqrt(b * b * c * c + a * a * q * q);
            return den < 1e-6f ? a : a * b / den;
        }

        /// <summary>目の面の基底（<see cref="Build"/> と同じ式。ずれると当たり判定と絵が食い違う）。</summary>
        public static void Basis(in EyeSeat s, out Vector3 right, out Vector3 up)
        {
            Vector3 up0 = Mathf.Abs(s.dir.y) > 0.97f ? Vector3.forward : Vector3.up;
            Vector3 r0 = Vector3.Cross(up0, s.dir).normalized;
            Vector3 u0 = Vector3.Cross(s.dir, r0).normalized;
            float roll = s.rollDeg * Mathf.Deg2Rad;
            float cr = Mathf.Cos(roll), sr = Mathf.Sin(roll);
            right = r0 * cr + u0 * sr;
            up = u0 * cr - r0 * sr;
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
                eyeRatio = Mathf.Lerp(EyeRatioMin, EyeRatioMax, Hash(k * 13 + 5)),
                aspect = QuadAspect(Mathf.Lerp(EyeRatioMin, EyeRatioMax, Hash(k * 13 + 5)),
                                    Mathf.Lerp(0.46f, 0.72f, Hash(k * 13 + 6)),
                                    Mathf.Lerp(0.28f, 0.55f, Hash(k * 13 + 7))),
                lidUp = Mathf.Lerp(0.46f, 0.72f, Hash(k * 13 + 6)),
                lidDown = Mathf.Lerp(0.28f, 0.55f, Hash(k * 13 + 7)),
                // 大きい目ほど虹彩が白目を食う。
                // ⚠ 単位は「虹彩の直径 ÷ 目の横幅」。参考画像（0237・dejime.jpg）の虹彩は横幅の 0.55 で、
                //    同心の環がその中に 3 段入る。目が丸くなった（EyeRatio 0.50〜0.80）ので
                //    0.60 でも瞼に切られない（0077 の「暗い葉っぱ」は細い目に 0.78 を入れたときの話）。
                // ⚠ 上限は目の丈の 0.72 倍（`IrisCapOfRatio`）。虹彩が丈いっぱいだと輪郭の線と虹彩の縁の環が
                //    融けて、同心の環が 1 つも読めない（v3 の絵で確認）。参考画像は丈の 0.69 倍。
                irisR = Mathf.Min(Mathf.Lerp(0.32f, 0.62f, Mathf.Clamp01(t * 1.25f)),
                                  IrisCapOfRatio * Mathf.Lerp(EyeRatioMin, EyeRatioMax, Hash(k * 13 + 5))) *
                        Mathf.Lerp(0.88f, 1.12f, Hash(k * 13 + 8)),
                // 半開きのまま止まる目を混ぜる（全部が全開だと機械に見える）。
                openMax = Mathf.Lerp(0.62f, 1.0f, Hash(k * 17 + 3)),
                skew = (Hash(k * 17 + 4) - 0.5f) * 0.30f,
                // 上下の瞼を別の弧にし、横も歪める（Codex 2 巡目「基本テンプレートを崩す」）。
                arcUp = Mathf.Lerp(1.04f, 1.34f, Hash(k * 19 + 2)),
                arcDown = Mathf.Lerp(1.04f, 1.34f, Hash(k * 19 + 3)),
                warpX = (Hash(k * 19 + 4) - 0.5f) * 0.44f,
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
            var form3 = new List<Vector4>(n * 4);
            var tris = new List<int>(n * 6);

            for (int i = 0; i < n; i++)
            {
                EyeSeat s = seats[i];
                Vector3 c = s.dir * RadiusM;

                // 面の基底。中心（体験者の頭）を向く ＝ 走行中の向き直しが要らない。
                // ⚠ **当たり判定と同じ式**（Basis）を使う。別々に書くと絵と隙間が食い違う。
                Basis(s, out Vector3 rr, out Vector3 uu);

                // ⚠ **quad は目より一回り大きく張る**（暈のための余白）。シェーダの SHAPE_SCALE と対で、
                //    ここを割っておくので sizeDeg は「目そのものの見かけの大きさ」のまま。
                float hw = RadiusM * Mathf.Tan(s.sizeDeg * 0.5f * Mathf.Deg2Rad) / ShapeScale;
                // 縦はさらに RiftExtend 倍（目の上下に漏れる帯の柱のぶん）。シェーダが戻す。
                float hh = hw * s.aspect * RiftExtend;

                var a = new Vector4(s.rank, s.big ? 1f : 0f, Hash(i * 11 + 7), s.presence);
                var f = new Vector4(s.aspect, s.lidUp, s.lidDown, s.irisR);
                var f2 = new Vector4(s.openMax, s.skew, s.pupilOffset, s.sizeNorm);
                var f3 = new Vector4(s.arcUp, s.arcDown, s.warpX, 0f);
                int b = pos.Count;
                pos.Add(c - rr * hw - uu * hh); uv.Add(new Vector2(-1f, -1f));
                pos.Add(c + rr * hw - uu * hh); uv.Add(new Vector2(1f, -1f));
                pos.Add(c - rr * hw + uu * hh); uv.Add(new Vector2(-1f, 1f));
                pos.Add(c + rr * hw + uu * hh); uv.Add(new Vector2(1f, 1f));
                for (int k = 0; k < 4; k++) { attr.Add(a); form.Add(f); form2.Add(f2); form3.Add(f3); }
                tris.Add(b + 0); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b + 2); tris.Add(b + 3); tris.Add(b + 1);
            }

            m.SetVertices(pos);
            m.SetUVs(0, uv);
            m.SetUVs(1, attr);
            m.SetUVs(2, form);
            m.SetUVs(3, form2);
            m.SetUVs(4, form3);
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
