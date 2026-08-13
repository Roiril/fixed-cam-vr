#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 封印の箱が床に落とす影の形（UnityEngine の数学だけを使う純ロジック）。
    ///
    /// 出どころは 2026-08-13 のユーザー判定（<c>canon/LEDGER.md</c> 0024）
    /// 「最初に出る黒い箱が床に影を斜め方向くらいで落とすようにしてほしい。リアル感がなさすぎる」。
    ///
    /// ⚠ <b>パススルーに「影を落とす」とは、パススルーを暗くすることそのもの</b>。
    /// アプリは premultiplied で描かれ、最終画は <c>アプリの rgb + 現実 × (1 - アプリの alpha)</c> なので、
    /// <b>rgb = 0 / alpha = 濃さ</b> の面を床の位置へ描けば、そこの現実だけが暗くなる
    /// （<see cref="ContainmentShell"/> と同じ仕掛け。あちらは 1 まで上げて会場を消す）。
    ///
    /// ⚠⚠ <b>URP のシャドウマップは使えない。</b> 影の落ち先である床は<b>現実</b>なので
    /// シーンに幾何が無く、受け手が存在しない。CG 人形が同じ理由で平面投影シャドウを使っているのと同型
    /// （<c>rules/streaming.md</c>「影・接地・オクルージョン」）。
    ///
    /// <b>形</b>: 方向つき平行光が箱（矩形 × 高さ）を床へ落とすと、影は
    /// 「footprint の矩形を <see cref="Sweep"/> の分だけ掃いた凸六角形」になる。
    /// 距離場は「矩形を線分方向へ動かしながらの最小距離」で、線分への射影を clamp して近似する
    /// （軸に沿う向きでは厳密、斜めでも縁のぼかし幅の内側に収まる）。
    ///
    /// ⚠ <b><see cref="Coverage"/> はシェーダ <c>FixedCamVr/SealedBoxShadow</c> と同じ式</b>。
    /// 片方だけ直すと沈黙して食い違うので、必ず対で直す（<see cref="IntroVeil.SignedDistance"/> と同じ規律）。
    /// </summary>
    public static class SealedBoxShadowLogic
    {
        /// <summary>
        /// 光が差してくる方位（度）。影は<b>この向きへ伸びる</b>（0 = course +Z / 90 = course +X）。
        ///
        /// ⚠ <b>course 空間で持つ</b>。ワールドで持つとトラッキング原点の向き次第で影の向きが変わり、
        /// 同じ現場なのに日によって影が回る。
        ///
        /// ⚠⚠ <b>体験者が立つ側（course -Z）へ倒してある。</b> 影は箱の裏へ落ちると
        /// <b>1 画素も見えない</b>ので、外から箱を見ている人を接地させる、という目的を果たさない。
        /// 最初 -38°（＝ +Z へ伸びる）で焼いたら `menu intro` の絵に影が 1 つも写らなかった。
        /// </summary>
        public const float DefaultYawDeg = 142f;

        /// <summary>
        /// 光の高さ（度）。<b>低いほど影が長い</b>。
        ///
        /// 52° は高さ 2.4m の箱に対して 1.88m の影 ＝ footprint（半 0.9m）の外へ約 1m 出る。
        /// 真上（90°）にすると影が箱の下に隠れて<b>1 画素も見えない</b>ので、斜めであること自体が要件
        /// （ユーザー判定「斜め方向くらい」）。
        /// </summary>
        public const float DefaultElevationDeg = 52f;

        /// <summary>影の最大の濃さ（0..1）。1 にすると現実が完全に消えて「床に空いた穴」になる。</summary>
        public const float DefaultDensity = 0.55f;

        /// <summary>取りうる光の高さ (度)。下限を切らないと影が数十 m へ伸びる。</summary>
        public const float MinElevationDeg = 12f;
        public const float MaxElevationDeg = 85f;

        /// <summary>縁のぼかしの基準幅 (m)。接地点で <see cref="FeatherNear"/> 倍、先端で <see cref="FeatherFar"/> 倍。</summary>
        public const float FeatherM = 0.11f;

        /// <summary>接地点のぼかし倍率。<b>硬い</b>（物が触れている所は半影が細い）。</summary>
        public const float FeatherNear = 0.35f;

        /// <summary>先端のぼかし倍率。<b>柔らかい</b>（遮蔽物から離れるほど半影が広がる）。</summary>
        public const float FeatherFar = 2.0f;

        /// <summary>先端の濃さ（接地点を 1 としたときの比）。</summary>
        public const float FarDensity = 0.42f;

        /// <summary>接地帯の幅 (m)。箱の面のすぐ足元だけを濃くする。</summary>
        public const float ContactM = 0.22f;

        /// <summary>接地帯で足す濃さ。<b>「浮いている」に一番効くのはここ</b>（CG 人形の接地影と同じ）。</summary>
        public const float ContactGain = 0.40f;

        /// <summary>床から浮かせる高さ (m)。深度で解いていないので見た目には効かないが、位置の正しさとして持つ。</summary>
        public const float LiftM = 0.004f;

        /// <summary>
        /// 影が伸びる向きと長さ（箱ローカルの XZ・m）。箱の天面が床へ落ちる先そのもの。
        /// </summary>
        public static Vector2 Sweep(float yawDeg, float elevationDeg, float heightM)
        {
            float e = Mathf.Clamp(elevationDeg, MinElevationDeg, MaxElevationDeg) * Mathf.Deg2Rad;
            float len = Mathf.Max(heightM, 0f) / Mathf.Max(Mathf.Tan(e), 1e-3f);
            float y = yawDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(y), Mathf.Cos(y)) * len;
        }

        /// <summary>
        /// 影を描く床の板の大きさ (m)。<b>掃いた矩形 ＋ ぼかしと接地帯の余白</b>が必ず収まる。
        /// 足りないと影が板の縁でぶつりと切れて、その直線が画に出る。
        /// </summary>
        public static Vector2 QuadSizeM(Vector2 halfXZ, Vector2 sweep)
        {
            float margin = FeatherM * FeatherFar + ContactM + 0.05f;
            return new Vector2(
                2f * (halfXZ.x + margin) + Mathf.Abs(sweep.x),
                2f * (halfXZ.y + margin) + Mathf.Abs(sweep.y));
        }

        /// <summary>板の中心（箱の中心を原点とする箱ローカル XZ）。掃いた分だけ影の側へ寄る。</summary>
        public static Vector2 QuadCenterM(Vector2 sweep) => sweep * 0.5f;

        /// <summary>
        /// 床の点 <paramref name="p"/>（箱の中心を原点とする箱ローカル XZ・m）における影の濃さ（0..1）。
        ///
        /// ⚠ <b>シェーダ <c>SealedBoxShadow.shader</c> の <c>Coverage</c> と同じ式</b>。対で直すこと。
        /// </summary>
        public static float Coverage(Vector2 p, Vector2 halfXZ, Vector2 sweep)
        {
            float ss = Vector2.Dot(sweep, sweep);
            float t = ss > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p, sweep) / ss) : 0f;
            float d = SdBox(p - sweep * t, halfXZ);

            // 半影は遮蔽物から離れるほど広がる。接地点で硬く、先端で柔らかい。
            float f = Mathf.Max(FeatherM * Mathf.Lerp(FeatherNear, FeatherFar, t), 1e-4f);
            // 縁は幾何の縁をまたぐ（内側と外側に半分ずつ）。d = 0 でちょうど半分の濃さ。
            float cov = 1f - IntroLogic.SmoothStep(-f * 0.5f, f * 0.5f, d);

            float dens = Mathf.Lerp(1f, FarDensity, t);
            float contact = 1f - IntroLogic.SmoothStep(0f, ContactM, SdBox(p, halfXZ));
            return Mathf.Clamp01(cov * Mathf.Clamp01(dens + contact * ContactGain));
        }

        /// <summary>矩形の符号つき距離（負 = 内側）。シェーダ側と同じ実装。</summary>
        public static float SdBox(Vector2 p, Vector2 half)
        {
            float dx = Mathf.Abs(p.x) - half.x;
            float dy = Mathf.Abs(p.y) - half.y;
            float outside = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(dx, dy), 0f);
        }
    }
}
