#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>憑依の出し方（<see cref="CommsDelivery.Possessed"/>）の段。</summary>
    public enum CommsPossessionPhase
    {
        /// <summary>憑依の出し方ではない（打つ／浮かぶ）、または面が畳まれている。</summary>
        Off,
        /// <summary>全文が一気に出て、読ませている。斑は 0（通常の面そのもの）。</summary>
        Shown,
        /// <summary>上端から前線が降りて、呪われた双子に塗り替わっている最中。</summary>
        Sweep,
        /// <summary>塗り替わり切った。以後は畳むまで全面が呪われた双子。</summary>
        Cursed,
    }

    public struct CommsPossessionSample
    {
        public CommsPossessionPhase phase;
        /// <summary>全文の濃さ 0..1（<see cref="CommsPossessionLogic.ShowSec"/> でふっと出る）。</summary>
        public float show;
        /// <summary>前線の進み 0..1（0 = まだ通常 / 1 = 全面が呪われた双子）。</summary>
        public float sweep;
        /// <summary>乱れの強さ 0..1（<c>canon/LEDGER.md</c> 0231）。塗り替わりのあいだだけ立つ。</summary>
        public float tear;
    }

    /// <summary>
    /// 侵食度 0.75 以降の連絡の出し方（<c>canon/LEDGER.md</c> 0230）の純粋な時計。UnityEngine 非依存。
    ///
    /// ユーザー逐語「侵食度0.75以降ではスイの文章を、一文字ずつではなくぱっと一気に出してしまい、
    /// スイの文章を全部見せた後に上からデジタルが侵食されるようなアニメーションで急に塗り替わる」。
    ///
    /// <b>出る（一気に）→ 読ませる → 塗り替わる（上から）</b> の 3 段。塗り替わった先は 0229 の呪われた双子
    /// （人形の顔・走り書き・毛羽立った地）で、塗り替わった後は斑が全面。段の秒数はシュビーの案
    /// （<c>canon/OPEN.md</c>「0.75 以降は一気に出して、上から塗り替わる」）。
    ///
    /// ⚠ 旧「印字へ侵食が追いつく（引き延ばし → 抵抗 → 崩壊）」の弧（2026-09-14）は 0230 で捨てた —
    /// 初見の人には「装置の不調」にしか見えない、というのがユーザーの判定の芯。
    /// ⚠ 読ませる時間は字数で伸びる（<see cref="ReadSecFor"/>）。打鍵の速さ（<see cref="CommsPanelLogic.CharsPerSecFor"/>）
    /// を物差しに使うので、言語ごとの読む速さの違いはそのまま乗る。
    /// </summary>
    public static class CommsPossessionLogic
    {
        /// <summary>侵食度 1 で報告が無いとき、嘘の一文を自動で出すまで (秒)。</summary>
        public const float AutoDelaySec = 0.6f;

        /// <summary>
        /// 全文がふっと出るまで (秒)。「ぱっと一気に」だが 1 フレームでは出さない —
        /// 点いたのではなく浮かんだ、に見える最短（<see cref="CommsPanelLogic.FadeInSec"/> の半分）。
        /// </summary>
        public const float ShowSec = 0.12f;

        /// <summary>読ませる時間の土台 (秒)。ここに字数ぶんを足す。</summary>
        public const float ReadBaseSec = 0.9f;

        /// <summary>
        /// 字数ぶんの読ませる時間の倍率。「その文面を打つのに掛かる秒 × これ」を <see cref="ReadBaseSec"/> に足す。
        /// 打つ出し方では打っているあいだに読み終わっている（0092）ので、その半分弱で足りる。
        /// </summary>
        public const float ReadPerTypeK = 0.45f;

        /// <summary>読ませる時間の下限 (秒)。短い返事（1 行）でも一瞬で塗り替えない。</summary>
        public const float ReadMinSec = 1.0f;

        /// <summary>読ませる時間の上限 (秒)。3 行の文面でも 2.4 秒で塗り替わりへ進む。</summary>
        public const float ReadMaxSec = 2.4f;

        /// <summary>
        /// 上端から下端まで塗り替わるまで (秒)。「急に塗り替わる」の速さ。
        /// 遅くすると「ゆっくり暗くなる」に見え、1〜2 コマだと「点いた」に見える（前線が画に出ない）。
        /// </summary>
        public const float SweepSec = 0.45f;

        // ---- 乱れ（`canon/LEDGER.md` 0231・「メインスクリーンをまねした…乱れ演出を…使う」）----

        /// <summary>
        /// 乱れの強さの頭打ち。<b>本編と同じ 0.6</b>（<c>canon/LEDGER.md</c> 0057）。
        /// 全面が砂で埋まると「乱れ」ではなく「信号断」に見えるので、1 にしない。
        /// </summary>
        public const float TearPeak = 0.6f;

        /// <summary>乱れの立ち上がり (秒)。塗り替わりの頭で一気に立つ。</summary>
        public const float TearAttackSec = 0.06f;

        /// <summary>
        /// 乱れの尾 (秒)。<b>塗り替わり切った後</b>に引く — 前線が下端へ抜けた瞬間に乱れも消すと、
        /// 「装置が直った」に見える。呪われた面へ落ち着くまでを乱れが覆う。
        /// </summary>
        public const float TearReleaseSec = 0.12f;

        // Mono の中間計算と float の戻り値の丸め差だけを吸収する。描画 1 コマより十分小さい。
        private const float BoundaryToleranceSec = 0.000001f;

        /// <summary>その文面（字数・言語）を読ませる時間 (秒)。</summary>
        public static float ReadSecFor(int charCount, ShowLang lang)
        {
            int n = charCount < 0 ? 0 : charCount;
            float typeSec = n / CommsPanelLogic.CharsPerSecFor(lang);
            return Clamp(ReadBaseSec + typeSec * ReadPerTypeK, ReadMinSec, ReadMaxSec);
        }

        /// <summary>塗り替わりが始まる時刻（文字の段に入ってから・秒）。</summary>
        public static float SweepStartSec(float readSec) => ShowSec + Positive(readSec);

        /// <summary>文字の段の長さ（出る → 読ませる → 塗り替わる）。終わったら読ませる段（呪われたまま）へ。</summary>
        public static float DurationFor(float readSec) => SweepStartSec(readSec) + SweepSec;

        /// <summary>文字の段に入ってからの秒から、いまの段と値を返す。</summary>
        public static CommsPossessionSample Sample(float elapsedSinceTypeStart, float readSec)
        {
            float t = elapsedSinceTypeStart < 0f ? 0f : elapsedSinceTypeStart;
            float sweepAt = SweepStartSec(readSec);
            if (t + BoundaryToleranceSec >= DurationFor(readSec))
                return At(CommsPossessionPhase.Cursed, 1f, 1f, TearFor(t, readSec));
            if (t + BoundaryToleranceSec >= sweepAt)
                return At(CommsPossessionPhase.Sweep, 1f, Clamp01((t - sweepAt) / SweepSec),
                          TearFor(t, readSec));
            return At(CommsPossessionPhase.Shown, Smooth(Clamp01(t / ShowSec)), 0f, 0f);
        }

        /// <summary>
        /// その時刻の乱れの強さ（<c>canon/LEDGER.md</c> 0231）。読ませているあいだは 0、
        /// 塗り替わりの頭で <see cref="TearAttackSec"/> で立ち、塗り替わり切ってから
        /// <see cref="TearReleaseSec"/> で引く。
        /// </summary>
        public static float TearFor(float elapsedSinceTypeStart, float readSec)
        {
            float t = elapsedSinceTypeStart < 0f ? 0f : elapsedSinceTypeStart;
            float sweepAt = SweepStartSec(readSec);
            float end = DurationFor(readSec);
            if (t <= sweepAt) return 0f;
            if (t < sweepAt + TearAttackSec)
                return TearPeak * Clamp01((t - sweepAt) / TearAttackSec);
            if (t < end) return TearPeak;
            float k = Clamp01((t - end) / TearReleaseSec);
            return TearPeak * (1f - k);
        }

        private static CommsPossessionSample At(CommsPossessionPhase phase, float show, float sweep,
                                                float tear)
            => new CommsPossessionSample { phase = phase, show = show, sweep = sweep, tear = tear };

        private static float Positive(float v) => v > 0f ? v : ReadMinSec;

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
