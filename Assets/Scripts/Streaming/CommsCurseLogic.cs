#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 連絡の面の呪い（<c>canon/LEDGER.md</c> 0229）の純粋な判断。UnityEngine 非依存。
    ///
    /// <b>呪われた双子の画面を、1 つの斑（まだら）の場で通常の画面に重ねる。</b>
    /// 場は面のローカル座標 (m) の値ノイズで、顔・地・走り書き・文字の切断がすべて同じ式を読む
    /// （GPU 側は <c>Assets/Art/Shaders/Streaming/CommsCurse.hlsl</c>）。
    ///
    /// ⚠⚠ <b><see cref="Field"/> は HLSL の <c>CurseField</c> の写し。</b> 片方だけ直すと、
    /// テレメトリの「切られた字数」（<c>commsCx</c>）が画と食い違う。
    ///
    /// <b>斑の量は侵食度に等号で結ばない。</b> 設計批評（2026-09-18）の指摘 — 2 周目 C で立つ 0.75 を
    /// そのまま閾値に使うと、報告の返事「異常を検出しました」が 3 周目を通して 1 字も読めなくなる。
    /// ここでは<b>覆う面積</b>を決め（0.25 → 22% / 0.75 → 55% / 1 → 全面）、場の分位からその閾値を逆算する。
    /// </summary>
    public static class CommsCurseLogic
    {
        /// <summary>
        /// 面が開いてから斑が目標まで重なるまで (秒)。
        /// スケッチの「出た初めはこれ（通常の画）← 1s ほどで重なる」（0229）。
        /// </summary>
        public const float RampSec = 1.0f;

        /// <summary>斑の基本セル (m・面のローカル)。細かいオクターブはこの半分。</summary>
        public const float CellM = 0.05f;

        /// <summary>細かいオクターブの重み。</summary>
        public const float DetailK = 0.35f;

        /// <summary>境目の柔らかさ。</summary>
        public const float Soft = 0.12f;

        /// <summary>文字を切る閾値（<see cref="K"/> がこれ以上の画素にステンシルが立つ）。</summary>
        public const float CutThreshold = 0.5f;

        /// <summary>これ以下の侵食度は 0 として扱う。</summary>
        public const float OffThreshold = 0.002f;

        /// <summary>2 周目 C の単発人形視点（侵食度 0.25）で斑が覆う面積の割合。</summary>
        public const float CoverageAtFirstPov = 0.22f;

        /// <summary>2 周目 C の連続人形視点（侵食度 0.75）で斑が覆う面積の割合。報告の返事がまだ半分読める。</summary>
        public const float CoverageAtChainedPov = 0.55f;

        // 場の分布を測る矩形（面の実寸に近い・面のローカル m）と刻み。
        private const float SampleX0 = -0.70f, SampleX1 = 0.30f;
        private const float SampleY0 = -0.12f, SampleY1 = 0.22f;
        private const float SampleStep = 0.005f;

        private static float[]? _sortedField;

        /// <summary>侵食度（0 / 0.25 / 0.75 / 1）をそのまま通す。閾値未満は 0。</summary>
        public static float LevelFor(float invasionProgress)
        {
            float level = Clamp01(invasionProgress);
            return level <= OffThreshold ? 0f : level;
        }

        /// <summary>その侵食度で斑が覆う面積の割合（0..1）。折れ線で単調。</summary>
        public static float CoverageFor(float level)
        {
            level = Clamp01(level);
            if (level <= OffThreshold) return 0f;
            if (level <= CommsInvasionLogic.FirstPovLevel)
                return CoverageAtFirstPov * level / CommsInvasionLogic.FirstPovLevel;
            if (level <= CommsInvasionLogic.ChainedPovLevel)
                return CoverageAtFirstPov + (CoverageAtChainedPov - CoverageAtFirstPov)
                    * (level - CommsInvasionLogic.FirstPovLevel)
                    / (CommsInvasionLogic.ChainedPovLevel - CommsInvasionLogic.FirstPovLevel);
            return CoverageAtChainedPov + (1f - CoverageAtChainedPov)
                * (level - CommsInvasionLogic.ChainedPovLevel)
                / (1f - CommsInvasionLogic.ChainedPovLevel);
        }

        /// <summary>その侵食度で面へ書く斑の量（シェーダの <c>_Curse</c>）。</summary>
        public static float MaskFor(float level)
        {
            level = Clamp01(level);
            if (level <= OffThreshold) return 0f;
            if (level >= 0.999f) return 1f;
            return MaskForCoverage(CoverageFor(level));
        }

        /// <summary>
        /// 面積の割合 <paramref name="coverage"/> を覆う斑の量。場の分位から逆算する
        /// （k ≥ cut ⇔ field ≤ mask·(1+soft) − cut·soft）。
        /// </summary>
        public static float MaskForCoverage(float coverage)
        {
            coverage = Clamp01(coverage);
            if (coverage <= 0f) return 0f;
            if (coverage >= 0.999f) return 1f;
            float q = Quantile(coverage);
            return Clamp01((q + CutThreshold * Soft) / (1f + Soft));
        }

        /// <summary>斑の量 <paramref name="mask"/> が実際に覆う面積の割合（サンプル格子で数える）。</summary>
        public static float CoverageOf(float mask)
        {
            float[] sorted = SortedField();
            float limit = mask * (1f + Soft) - CutThreshold * Soft;
            int count = 0;
            for (int i = 0; i < sorted.Length; i++)
                if (sorted[i] <= limit) count++;
            return sorted.Length == 0 ? 0f : count / (float)sorted.Length;
        }

        /// <summary>面のローカル座標 (m) の場 0..1。<b>HLSL の <c>CurseField</c> と同じ式。</b></summary>
        public static float Field(float x, float y)
        {
            float n1 = ValueNoise(x / CellM + 3.7f, y / CellM + 1.9f);
            float n2 = ValueNoise(x / (CellM * 0.5f) + 11.3f, y / (CellM * 0.5f) + 7.1f);
            return (n1 + DetailK * n2) / (1f + DetailK);
        }

        /// <summary>場の値と斑の量から、その点の「呪われている度」0..1（HLSL の <c>CurseK</c>）。</summary>
        public static float K(float field, float mask)
            => Clamp01((mask * (1f + Soft) - field) / Soft);

        /// <summary>その点で文字が切られるか（ステンシルが立つ側か）。</summary>
        public static bool IsCut(float x, float y, float mask)
            => mask > 0f && K(Field(x, y), mask) >= CutThreshold;

        /// <summary>
        /// この侵食度から上は<b>憑依の出し方</b>（<c>canon/LEDGER.md</c> 0230・<see cref="CommsDelivery.Possessed"/>）:
        /// 全文が一気に出て、読ませてから、上から前線が降りて呪われた双子に塗り替わる。
        /// 塗り替わった後は斑が全面（0.75 でも 1 でも同じ）。
        /// </summary>
        public const float PossessedLevel = CommsInvasionLogic.ChainedPovLevel;

        // ---- 塗り替わりの帯と乱れ（HLSL の CommsBandOf / CurseSweepK / CommsPick8 と対・0230 / 0231）----
        //
        // 面を上端から帯（高さ TearBandM）に割り、前線が届いた帯から順に帯ごと一斉に反転する。
        // 帯は乱れと同じ格子で、乱れの帯ごとの値（飛び・脱落）は**ここが 1 か所で作って** uniform と
        // 本文の頂点（CPU）へ配る。HLSL に第二の実装は置かない（設計批評 2026-09-18）。

        /// <summary>帯の高さ (m)。<b>およそ本文 1 行ぶん</b>（字 1.2° に対し帯 1.6°）。細かくすると字が帯で割れて層が食い違う。</summary>
        public const float TearBandM = 0.04f;

        /// <summary>帯の上限（uniform の float4 × 2）。面の丈は最大 0.295m ＝ 7.4 帯。</summary>
        public const int TearMaxBands = 8;

        /// <summary>帯が飛ぶ幅（片側・m）。全幅 0.954m の ±6.3%（見かけ 1.7° ＝ 字 1.4 個ぶん）。本編は枠幅の ±11%。</summary>
        public const float TearShiftM = 0.06f;

        /// <summary>位置ずれが組み替わる速さ (Hz)。本編と同じ。</summary>
        public const float TearHz = 18f;

        /// <summary>脱落と明滅が組み替わる速さ (Hz)。本編と同じ。</summary>
        public const float TearSandHz = 42f;

        /// <summary>帯の数（最後の帯は欠けていてよい）。</summary>
        public static int SweepBandCount(float top, float bottom)
            => (int)MathF.Ceiling(MathF.Max(top - bottom, 0f) / TearBandM);

        /// <summary>上端から数えた帯番号（0 が最上段。上端より上は 0）。</summary>
        public static int SweepBandOf(float y, float top)
            => Math.Max((int)MathF.Floor((top - y) / TearBandM), 0);

        /// <summary>その点の帯が反転しているか（前線の上側 ＝ ステンシルが立つ側）。</summary>
        public static bool IsSwept(float x, float y, float progress, float top, float bottom)
        {
            if (progress <= 0f) return false;
            if (progress >= 1f) return true;
            int cursed = (int)MathF.Floor(Clamp01(progress) * SweepBandCount(top, bottom));
            return SweepBandOf(y, top) < cursed;
        }

        /// <summary>
        /// 乱れの帯ごとの値を作る（<b>唯一の生成元</b>）。<paramref name="shift"/> は帯の中身が右へ動く量 (m)、
        /// <paramref name="drop"/> は帯の脱落 0..1、<paramref name="flicker"/> は全体の明滅 0.78..1。
        /// 種は経過秒（実行時の乱数は使わない ＝ 同じ走行は同じ絵）。
        /// </summary>
        public static void ComputeTear(float strength, float seed, int bandCount,
                                       float[] shift, float[] drop, out float flicker)
        {
            float g = Clamp01(strength);
            float t = MathF.Floor(seed * TearHz);
            float t2 = MathF.Floor(seed * TearSandHz);
            for (int i = 0; i < TearMaxBands; i++)
            {
                shift[i] = 0f;
                drop[i] = 0f;
                if (g <= 0.001f || i >= bandCount) continue;
                // 立った帯だけ飛ぶ（強いほど多くの帯が立つ）。本編の `GlitchUv` と同じ確率。
                if (Hash21(i, t) >= 1f - 0.6f * g)
                    shift[i] = (Hash21(i + 37f, t + 11f) - 0.5f) * 2f * TearShiftM * g;
                // 帯ごとの脱落。本編の「帯が砂になる」と同じ確率・同じ深さ。
                if (Hash21(i + 5f, t2 + 2f) >= 1f - 0.75f * g)
                    drop[i] = g * 0.5f;
            }
            flicker = g <= 0.001f ? 1f : 1f - 0.22f * g * Hash21(t2, 9.3f);
        }

        private static float Quantile(float coverage)
        {
            float[] sorted = SortedField();
            int index = (int)(coverage * (sorted.Length - 1));
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;
            return sorted[index];
        }

        private static float[] SortedField()
        {
            if (_sortedField != null) return _sortedField;
            int nx = (int)((SampleX1 - SampleX0) / SampleStep) + 1;
            int ny = (int)((SampleY1 - SampleY0) / SampleStep) + 1;
            var values = new float[nx * ny];
            int n = 0;
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                values[n++] = Field(SampleX0 + i * SampleStep, SampleY0 + j * SampleStep);
            Array.Sort(values);
            _sortedField = values;
            return values;
        }

        private static float Frac(float v) => v - MathF.Floor(v);

        private static float Hash21(float px, float py)
        {
            px = Frac(px * 123.34f);
            py = Frac(py * 456.21f);
            float d = px * (px + 45.32f) + py * (py + 45.32f);
            px += d;
            py += d;
            return Frac(px * py);
        }

        private static float ValueNoise(float ux, float uy)
        {
            float ix = MathF.Floor(ux), iy = MathF.Floor(uy);
            float fx = ux - ix, fy = uy - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash21(ix, iy);
            float b = Hash21(ix + 1f, iy);
            float c = Hash21(ix, iy + 1f);
            float d = Hash21(ix + 1f, iy + 1f);
            return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
