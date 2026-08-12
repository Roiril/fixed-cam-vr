#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 周を重ねるごとに映像の解像度が落ちていく進み（UnityEngine 非依存・dt 注入）。
    /// <see cref="ShowRunDirector"/> が回し、<see cref="CameraFeelFx"/> がシェーダへ書く。
    ///
    /// 出どころは <c>canon/LEDGER.md</c> 0012 —
    /// 「1 周目の最初は今くらいの解像度」「3 周目にはだいぶ粗い」
    /// 「急に落ちるではなくばれないように、周を重ねるごとに着実に粗くなっていく」。
    ///
    /// <b>なぜ post ではないのか</b>: post 12 項目は shader / 卓の FS_POST / common.js / pipeline.js の
    /// 4 箇所を手作業で同期していて機械テストが無い。そこへ時間の関数を持ち込むと必ず沈黙して食い違う
    /// （<see cref="CameraFeelLogic"/> と同じ理由で別系統にしてある）。
    ///
    /// <b>落ち切るのは最後の周へ入った瞬間</b>（3 周なら 3 周目の A）。そこから先は動かない
    /// （`canon/LEDGER.md` 0013）。最後の周は**録画が流れる周**なので、その最中に画が動き続けると
    /// 「もう壊れ切った装置で過去を見せられている」ではなく「いま壊れつつある」になる。
    ///
    /// <b>段差を作らない仕掛けは 3 つ。1 つでも外すと階段になる</b>:
    ///   1. <b>周の中も進む</b> — 目標は <c>(lap-1 + 経過/LapRefSec) / (totalLaps-1)</c> の連続量。
    ///      周の番号だけで決めると、周の変わり目で必ず段差が出る
    ///   2. <b>単調</b> — 下がらない。下がると「直った」に見えて、装置が壊れていくという筋が崩れる
    ///   3. <b>上げ幅の頭打ち</b>（<see cref="MaxRisePerSec"/>）— 速く歩いて周が飛んでも滑って渡る。
    ///      自然な進みは 1/60 ≒ 0.017/秒 なので、2 倍までは許して段差だけ潰す
    ///
    /// ⚠ <b>進むのは本編（<see cref="ShowPhase.Run"/>）だけ。</b> 導入と終了では値を**保持する**。
    /// 終了で 0 へ戻すと、走行中の演出を見せ切っている猶予のあいだに
    /// <b>画が急に鮮明になって「直った」ように見える</b>。落とすのは体験者の交代（BeginRun）だけ。
    /// </summary>
    public sealed class ScreenDecayLogic
    {
        /// <summary>進み 0 のときの、枠を横切るブロック数。映像の領域は 4:3 が 16:9 の枠へ
        /// letterbox される分だけ狭いので ×0.75 ＝ 675 で、ソースの 640 とほぼ 1:1 ＝ 何も起きない。</summary>
        public const float FineBlocks = 900f;

        /// <summary>進み 1 のときの、枠を横切るブロック数（映像の領域で横 83 ブロック）。
        /// **最後の周へ入った瞬間にここへ着き、以後は動かない。**
        /// 下げすぎると、最後の周に流れる 1 周目の録画で**人型と「手を上げていない」が読めなくなる**
        /// （`rules/streaming.md` が 2026-08-06 に警告していたもの）。読めなければ上げる。</summary>
        public const float EndBlocks = 110f;

        /// <summary>
        /// 1 周の目安 (秒)。企画書の「各周およそ 30 秒」。<b>1 周目だけこれを使う</b>
        /// （2 周目以降は前の周の実測へ差し替わる — <see cref="Tick"/>）。
        /// </summary>
        public const float LapRefSec = 30f;

        /// <summary>前の周の実測を目安に採るときの下限・上限 (秒)。走り抜け／立ち止まりで暴れさせない。</summary>
        public const float MinLapRefSec = 10f;
        public const float MaxLapRefSec = 60f;

        /// <summary>進みの上げ幅の頭打ち (1/秒)。周が飛んでも段差にしないための唯一の仕掛け。</summary>
        public const float MaxRisePerSec = 0.035f;

        /// <summary>1 フレームで進める dt の上限 (秒)。ヒッチで一気に飛ばさない。</summary>
        private const float MaxStepSec = 0.25f;

        /// <summary>これ以下は「量子化しない」（＝今までと 1 ビットも変わらない画）。</summary>
        private const float OffThreshold = 0.0005f;

        private float _progress;
        private int _lastLap = 1;
        private float _lastLapElapsed;
        private float _lapRef = LapRefSec;

        /// <summary>いまの進み 0..1。</summary>
        public float Progress => _progress;

        /// <summary>いま使っている 1 周の目安 (秒)。診断用。</summary>
        public float LapRef => _lapRef;

        /// <summary>
        /// シェーダへ書く「枠を横切るブロック数」。<b>0 = 量子化しない。</b>
        /// 対応表をここ 1 箇所にしてあるのは、シェーダ側にも式を置くと
        /// テレメトリと画が黙って食い違うため（シェーダは進み 0..1 を知らない）。
        /// </summary>
        public float Blocks => _progress <= OffThreshold ? 0f : BlocksFor(_progress);

        /// <summary>頭から始め直す（体験者の交代）。</summary>
        public void Reset()
        {
            _progress = 0f;
            _lastLap = 1;
            _lastLapElapsed = 0f;
            _lapRef = LapRefSec;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="running"/> が false のあいだは値を保持する。
        ///
        /// ⚠ <b>1 周の目安は前の周の実測へ差し替える。</b> 定数で当てると、実際の周がそれより短いとき
        /// 周の中の進みが 1 に届かず、**不足が最後の境目へ持ち越されて「最後の周に入ってもまだ落ち続ける」**
        /// （2026-08-12 実機: 目安 30 秒に対し実際は 24 秒で、3 周目に入ってから 2 秒ぶん落ち続けた）。
        /// 前の周の実測を使えば周の中で 1 に届き切るので、境目の不足が 0 になる。
        /// </summary>
        public void Tick(float dt, bool running, int lap, int totalLaps, float lapElapsedSec)
        {
            if (!running || dt <= 0f) return;
            if (dt > MaxStepSec) dt = MaxStepSec;

            if (lap != _lastLap)
            {
                // 周が変わった。**直前に見た経過**がその周にかかった秒（lapElapsed は既に 0 へ戻っている）。
                if (_lastLapElapsed > 0f) _lapRef = Clamp(_lastLapElapsed, MinLapRefSec, MaxLapRefSec);
                _lastLap = lap;
            }
            _lastLapElapsed = lapElapsedSec;

            float target = TargetFor(lap, totalLaps, lapElapsedSec, _lapRef);
            if (target <= _progress) return;     // 単調（後戻りしない）

            float next = _progress + MaxRisePerSec * dt;
            _progress = next < target ? next : target;
        }

        /// <summary>
        /// その周・その経過における目標の進み 0..1。
        ///
        /// <b>落ち切るのは最後の周へ入った瞬間</b>（3 周なら <b>3 周目の A</b>）。だから分母は
        /// <c>totalLaps</c> ではなく <b><c>totalLaps - 1</c></b>（`canon/LEDGER.md` 0013 —
        /// 「3周目のAで粗さがマックスになるようにして、そこからは変わらないような感じに」）。
        ///
        /// 最後の周は**録画が流れる周**なので、その最中に画が動き続けると
        /// 「壊れていく装置」ではなく「いま壊れつつある装置」になる。落ちる過程は手前の周に閉じる。
        /// 最後の周と<b>帰りの A</b>（<c>lap = totalLaps + 1</c>）は 1 で頭打ち。
        /// </summary>
        /// <param name="lapRefSec">1 周の目安 (秒)。既定は <see cref="LapRefSec"/>。実行時は
        /// <see cref="Tick"/> が前の周の実測を渡す。</param>
        public static float TargetFor(int lap, int totalLaps, float lapElapsedSec,
                                      float lapRefSec = LapRefSec)
        {
            if (totalLaps < 1) totalLaps = ShowRunDefaults.TotalLaps;
            if (lap < 1) lap = 1;
            if (lapRefSec <= 0f) lapRefSec = LapRefSec;
            float within = lapElapsedSec > 0f ? lapElapsedSec / lapRefSec : 0f;
            if (within > 1f) within = 1f;
            // 1 周しか無い設定では手前の周が存在しないので、その 1 周の中で落とす。
            int span = totalLaps > 1 ? totalLaps - 1 : 1;
            return Clamp01((lap - 1 + within) / span);
        }

        /// <summary>
        /// 進み → 枠を横切るブロック数。<b>等比</b>で下げる。
        /// 等差だと最初の 1 周で見た目が全部落ちて（900→637）、3 周目がほとんど動かない（373→110）。
        /// </summary>
        public static float BlocksFor(float progress)
            => (float)(FineBlocks * Math.Pow(EndBlocks / (double)FineBlocks, Clamp01(progress)));

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
