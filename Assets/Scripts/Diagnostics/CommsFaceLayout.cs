#nullable enable

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// 連絡の面の左に立つ AI エージェントの顔と、本文との区切りの寸法。UnityEngine 非依存。
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0071（ユーザー指定・2026-08-17）:
    /// 「左に角丸の四角い枠線をつけて、その中にAIの顔を入れれるようにしてほしい」。
    ///
    /// ⚠⚠ <b>文面の帯は 1mm も動かさない。</b> 顔のぶんは<b>面を左へ伸ばして</b>作る。
    /// 面の幅を据え置いて文面を右へ詰める手もあるが、いちばん長い行（14 文字）は
    /// すでに幅の 95% を使っているので、詰めた瞬間に 3 行へ折り返して
    /// <b>枠の高さの前提（最悪 2 行）ごと崩れる</b>。
    /// 置き場所（<c>YawOffsetDeg</c> = -8°）はユーザーが実機で決めた値なので動かさない
    /// （<c>canon/LEDGER.md</c> 0060「もう少し見やすい位置に…もう少し右下に」）。
    ///
    /// ⚠ <b>面の原点は文面の帯の中心のまま。</b> 顔を足しても原点は動かないので、
    /// 文面・下段・打鍵・重心の運びは 1 行も影響を受けない。左へ伸びたぶんは
    /// <see cref="LeftX"/> が持つ（<c>CommsPanel.SetFrame</c> が左端を受け取る形へ変えてある）。
    /// </summary>
    public static class CommsFaceLayout
    {
        /// <summary>
        /// 枠の 1 辺 (m)。1.5m 先で 0.15m ＝ <b>見かけ 5.7°</b>（実機でおよそ 110 画素）。
        /// ⚠ <b>正方形であること</b> — <c>CommsAvatar.shader</c> は距離場を uv 空間で解くので、
        /// 縦横比が 1 でないと角の丸みが楕円になり、枠線の太さも上下と左右で変わる。
        /// </summary>
        public const float CellM = 0.15f;

        /// <summary>顔の外に取る余白 (m)。面の左端と本文の間に同じだけ空ける。</summary>
        public const float MarginM = 0.022f;

        /// <summary>面が左へ伸びる幅 (m)。</summary>
        public const float BandW = CellM + MarginM * 2f;

        /// <summary>
        /// 面の丈の下限 (m)。<b>枠が縦にはみ出さないための最低限</b>。
        ///
        /// ⚠ これは 0065 の「枠は出ている帯だけを覆う」に反しない。あれが禁じたのは
        /// <b>中身の無い空の箱</b>で、いまは左に顔が居るので空ではない。
        /// ⚠ いちばん高い姿（2 行の文面 ＋ 下段 ＝ 約 0.22m）はこの値を超えるので、
        /// <b>伸ばされるのは短い文面のときだけ</b>。
        /// </summary>
        public const float MinBoxH = CellM + MarginM * 2f;

        /// <summary>顔の切り抜きの角の丸み（1 辺に対する割合）。</summary>
        public const float RadiusK = 0.20f;

        /// <summary>顔の外枠は描かない。顔の大きさは <see cref="CellM"/> のまま保つ。</summary>
        public const float StrokeK = 0f;

        /// <summary>顔と本文の間に置く短い縦罫線の幅。</summary>
        public const float DividerW = 0.0025f;

        /// <summary>短い縦罫線が開き切ったときの高さ。</summary>
        public const float DividerH = 0.082f;

        /// <summary>短い縦罫線の中心。</summary>
        public static float DividerCenterX(float bodyW) => -bodyW * 0.5f - MarginM * 0.5f;

        /// <summary>地より手前・文字より奥 (m)。⚠ 面の +Z は体験者から見て奥。</summary>
        public const float DepthM = 0.006f;

        /// <summary>面の左端（面のローカル x）。<paramref name="bodyW"/> は文面の帯の幅。</summary>
        public static float LeftX(float bodyW) => -(bodyW * 0.5f + BandW);

        /// <summary>面の全幅 (m)。</summary>
        public static float FullW(float bodyW) => bodyW + BandW;

        /// <summary>枠の中心（面のローカル x）。</summary>
        public static float CellCenterX(float bodyW) => LeftX(bodyW) + MarginM + CellM * 0.5f;

        /// <summary>
        /// 顔の出方 0..1。面の位置と大きさは固定したまま透明度だけを動かす。
        /// </summary>
        public static float Reveal(float open01, float _)
        {
            float t = Clamp01(open01);
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
