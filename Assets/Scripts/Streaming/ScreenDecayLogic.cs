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
    /// <b>段差を作らない仕掛けは 3 つ。1 つでも外すと階段になる</b>:
    ///   1. <b>周の中も進む</b> — 目標は <c>(lap-1 + 経過/LapRefSec) / totalLaps</c> の連続量。
    ///      周の番号だけで決めると、周の変わり目で必ず段差が出る
    ///   2. <b>単調</b> — 下がらない。下がると「直った」に見えて、装置が壊れていくという筋が崩れる
    ///   3. <b>上げ幅の頭打ち</b>（<see cref="MaxRisePerSec"/>）— 速く歩いて周が飛んでも滑って渡る。
    ///      自然な進みは 1/90 ≒ 0.011/秒 なので、3 倍までは許して段差だけ潰す
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

        /// <summary>進み 1 のときの、枠を横切るブロック数（映像の領域で横 82 ブロック）。
        /// ここを下げすぎると、3 周目に流れる 1 周目の録画で**人型と「手を上げていない」が読めなくなる**
        /// （`rules/streaming.md` が 2026-08-06 に警告していたもの）。読めなければ上げる。</summary>
        public const float EndBlocks = 110f;

        /// <summary>1 周の目安 (秒)。企画書の「各周およそ 30 秒」。周の中の進みを出すのに使う。</summary>
        public const float LapRefSec = 30f;

        /// <summary>進みの上げ幅の頭打ち (1/秒)。周が飛んでも段差にしないための唯一の仕掛け。</summary>
        public const float MaxRisePerSec = 0.035f;

        /// <summary>1 フレームで進める dt の上限 (秒)。ヒッチで一気に飛ばさない。</summary>
        private const float MaxStepSec = 0.25f;

        /// <summary>これ以下は「量子化しない」（＝今までと 1 ビットも変わらない画）。</summary>
        private const float OffThreshold = 0.0005f;

        private float _progress;

        /// <summary>いまの進み 0..1。</summary>
        public float Progress => _progress;

        /// <summary>
        /// シェーダへ書く「枠を横切るブロック数」。<b>0 = 量子化しない。</b>
        /// 対応表をここ 1 箇所にしてあるのは、シェーダ側にも式を置くと
        /// テレメトリと画が黙って食い違うため（シェーダは進み 0..1 を知らない）。
        /// </summary>
        public float Blocks => _progress <= OffThreshold ? 0f : BlocksFor(_progress);

        /// <summary>頭から始め直す（体験者の交代）。</summary>
        public void Reset() => _progress = 0f;

        /// <summary>1 フレーム進める。<paramref name="running"/> が false のあいだは値を保持する。</summary>
        public void Tick(float dt, bool running, int lap, int totalLaps, float lapElapsedSec)
        {
            if (!running || dt <= 0f) return;
            if (dt > MaxStepSec) dt = MaxStepSec;

            float target = TargetFor(lap, totalLaps, lapElapsedSec);
            if (target <= _progress) return;     // 単調（後戻りしない）

            float next = _progress + MaxRisePerSec * dt;
            _progress = next < target ? next : target;
        }

        /// <summary>
        /// その周・その経過における目標の進み 0..1。
        /// <b>帰りの A</b>（<c>lap = totalLaps + 1</c>）は 1 で頭打ちになる。
        /// </summary>
        public static float TargetFor(int lap, int totalLaps, float lapElapsedSec)
        {
            if (totalLaps < 1) totalLaps = ShowRunDefaults.TotalLaps;
            if (lap < 1) lap = 1;
            float within = lapElapsedSec > 0f ? lapElapsedSec / LapRefSec : 0f;
            if (within > 1f) within = 1f;
            return Clamp01((lap - 1 + within) / totalLaps);
        }

        /// <summary>
        /// 進み → 枠を横切るブロック数。<b>等比</b>で下げる。
        /// 等差だと最初の 1 周で見た目が全部落ちて（900→637）、3 周目がほとんど動かない（373→110）。
        /// </summary>
        public static float BlocksFor(float progress)
            => (float)(FineBlocks * Math.Pow(EndBlocks / (double)FineBlocks, Clamp01(progress)));

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
