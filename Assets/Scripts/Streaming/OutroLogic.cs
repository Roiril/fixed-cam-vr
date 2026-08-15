#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕の段。<b>導入の逆再生ではない</b>（2026-08-15 に作り替えた・<c>canon/LEDGER.md</c> 0048）。
    /// パススルーへは戻さず、黒のまま装置だけが力尽きる。
    /// </summary>
    public enum OutroStage
    {
        /// <summary>出していない。</summary>
        Off,
        /// <summary>スクリーンが電池切れのようにちかちかしながら暗くなり、消える。</summary>
        Flicker,
        /// <summary>何も無い黒。間。</summary>
        Dark,
        /// <summary>報告の文字が浮かぶ。</summary>
        Report,
        /// <summary>出し切った。<b>文字はここでも出したまま</b>（次のランで <see cref="OutroLogic.Disable"/>）。</summary>
        Done,
    }

    /// <summary>段が終わった合図。</summary>
    public enum OutroEvent { None, Finished }

    /// <summary>
    /// 終幕（本編 → 装置が力尽きて、報告を出して終わる）の判断・計時。UnityEngine 非依存・dt 注入。
    ///
    /// <b>2026-08-15 に骨格ごと作り替えた</b>（<c>canon/LEDGER.md</c> 0048・ユーザー逐語
    /// 「パススルーには戻さず、背景が黒いまま、スクリーンが、電池が切れかけみたいな感じで
    /// だんだんとちかちかしながら消えていき、最後に…書いて終了にしてほしい」）。
    ///
    /// 旧実装は導入を逆に辿ってパススルーへ戻す 5 段（Warm / Unswap / Open / Restore / Hold）だった。
    /// 戻す先が無くなったので、覆い・隔離・パススルーには<b>一切触らない</b> —
    /// 本編の時点で背景は既に黒（パススルーは切れている）なので、<b>何もしないことが「黒のまま」</b>。
    /// 動かすのはスクリーンの電力（<see cref="ScreenPower"/>）と報告の面（<see cref="ReportAlpha"/>）だけ。
    ///
    /// <b>ちらつきの形をシェーダへ持たせない。</b> ここで数値として出せば
    /// (1) 実際に材質へ書いた値をテレメトリに出せる（「段が進んだ」ではなく「画に出た」の観測）、
    /// (2) EditMode テストで固定できる、(3) 実行のたびに同じ絵になる（乱数を使わない）。
    /// </summary>
    public sealed class OutroLogic
    {
        private OutroTiming _t = OutroTiming.Default;
        private OutroStage _stage = OutroStage.Off;
        private float _stageElapsed;
        private float _totalElapsed;

        public OutroStage Stage => _stage;
        public float StageElapsedSec => _stageElapsed;
        public float TotalElapsedSec => _totalElapsed;

        /// <summary>計時が進んでいるか（＝ まだ段が残っている）。</summary>
        public bool Active => _stage != OutroStage.Off && _stage != OutroStage.Done;

        /// <summary>
        /// 画に何かを出している / 出したままか。<b><see cref="OutroStage.Done"/> でも true</b> —
        /// 報告の文字は次のランまで消えないので、Director は <see cref="Active"/> ではなく
        /// こちらで配り続ける。
        /// </summary>
        public bool Presenting => _stage != OutroStage.Off;

        /// <summary>
        /// いまの段の進み 0..1。<b>音が「絵と同じ速さで」細るために要る</b>
        /// （<c>SoundBedLogic</c> が装置の声をこの進みで引く）。
        /// </summary>
        public float StageProgress01
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Flicker: return Progress(_t.flickerSec);
                    case OutroStage.Dark: return Progress(_t.darkSec);
                    case OutroStage.Report: return Progress(_t.reportFadeSec);
                    case OutroStage.Done: return 1f;
                    default: return 0f;
                }
            }
        }

        public void Configure(OutroTiming timing) => _t = timing.Sanitized();

        /// <summary>頭から始める。</summary>
        public void Begin()
        {
            _stage = OutroStage.Flicker;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
        }

        /// <summary>出さない（ラン開始・中止）。</summary>
        public void Disable()
        {
            _stage = OutroStage.Off;
            _stageElapsed = 0f;
            _totalElapsed = 0f;
        }

        public OutroEvent Tick(float dt)
        {
            if (dt < 0f) dt = 0f;
            if (!Active) return OutroEvent.None;

            _stageElapsed += dt;
            _totalElapsed += dt;

            switch (_stage)
            {
                case OutroStage.Flicker:
                    if (_stageElapsed >= _t.flickerSec) Advance(OutroStage.Dark);
                    return OutroEvent.None;

                case OutroStage.Dark:
                    if (_stageElapsed >= _t.darkSec) Advance(OutroStage.Report);
                    return OutroEvent.None;

                case OutroStage.Report:
                    if (_stageElapsed < _t.reportFadeSec) return OutroEvent.None;
                    _stage = OutroStage.Done;
                    return OutroEvent.Finished;

                default:
                    return OutroEvent.None;
            }
        }

        /// <summary>
        /// スクリーンへ掛ける電力 0..1（<c>_ScreenPower</c>）。
        /// <b>出していない間は 1</b>（＝ 本編と同じ見え。0 を返す側へ倒すと画がまるごと消える）。
        /// </summary>
        public float ScreenPower
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Off: return 1f;
                    case OutroStage.Flicker: return FlickerPower(Progress(_t.flickerSec), _stageElapsed);
                    default: return 0f;   // Dark / Report / Done ＝ 消えたまま
                }
            }
        }

        /// <summary>報告の面の不透明度 0..1。</summary>
        public float ReportAlpha
        {
            get
            {
                switch (_stage)
                {
                    case OutroStage.Report: return Progress(_t.reportFadeSec);
                    case OutroStage.Done: return 1f;
                    default: return 0f;
                }
            }
        }

        // --- 電池が切れかけの管 -------------------------------------------------

        /// <summary>ちらつきの刻み (Hz)。序盤は大きく揺れ、終盤ほど細かくなる。</summary>
        private const float FlickerRateLo = 6f;
        private const float FlickerRateHi = 24f;

        /// <summary>1 刻みが「落ちる」確率。終盤ほど高い。</summary>
        private const float DropChanceLo = 0.06f;
        private const float DropChanceHi = 0.82f;

        /// <summary>最後にここから 0 へ落とし切る（切れかけが切れる）。</summary>
        private const float BlackoutFrom = 0.88f;

        /// <summary>
        /// 電池が切れかけの管の明るさ。
        ///
        /// 電池は「暗くなる」だけでも「点滅する」だけでもない。3 つを重ねる:
        ///   ① 供給がじわじわ痩せる（前半はほとんど落ちず、後半で一気に）
        ///   ② ときどき落ちる。落ちる頻度も落ちる深さも終盤ほど大きい
        ///   ③ 最後は 0 まで落とし切る
        ///
        /// ⚠ <b>乱数を使わない。</b> 刻み番号のハッシュなので、同じ版は同じ絵になる
        /// （音の合成と同じ流儀 — 変えていないのに差が出ると、何が効いたのか分からなくなる）。
        /// </summary>
        /// <param name="p">段の進み 0..1。</param>
        /// <param name="t">段に入ってからの秒（刻みの位相）。</param>
        public static float FlickerPower(float p, float t)
        {
            p = Clamp01(p);
            if (t < 0f) t = 0f;

            // ① 供給。p^3 なので前半はほとんど落ちない（p=0.5 で 0.88 / p=0.9 で 0.27）。
            float supply = 1f - p * p * p;

            // ② 落ちる刻み。rate が p で増えるので終盤ほど細かい。
            // ⚠ **頭の 1 刻みは必ず点いている**（`tick > 0`）。始まった瞬間に暗いと
            //   「切れかけ」ではなく「切れた」になり、体験の終わりが事故に見える。
            float rate = FlickerRateLo + (FlickerRateHi - FlickerRateLo) * p;
            int tick = (int)(t * rate);
            float level = supply;
            if (tick > 0 && Hash01(tick) < DropChanceLo + (DropChanceHi - DropChanceLo) * p * p)
            {
                // 落ちた瞬間に残る明るさ。終盤ほど深く落ちる（最後は真っ暗まで）。
                float floorLevel = 0.30f * (1f - p);
                level = supply * floorLevel * (0.4f + 0.6f * Hash01(tick * 7 + 13));
            }

            // ③ 落とし切り。ここが無いと段が変わる瞬間に明るさが飛ぶ。
            float tail = 1f - SmoothStep(Clamp01((p - BlackoutFrom) / (1f - BlackoutFrom)));
            return Clamp01(level * tail);
        }

        /// <summary>
        /// 刻み番号 → 0..1。決定的（同じ番号は必ず同じ値）。
        ///
        /// ⚠ <b>種の足し込み（<c>0x9E3779B9</c>）を外さない。</b> 乗算だけだと
        /// <c>n = 0</c> がそのまま 0 を通って **必ず 0 を返す**（＝ 0 番の刻みが必ず「落ちた」に
        /// なる）。実装した日にテストが捕まえた。
        /// </summary>
        private static float Hash01(int n)
        {
            unchecked
            {
                uint x = (uint)n * 2654435761u + 0x9E3779B9u;
                x ^= x >> 15; x *= 2246822519u;
                x ^= x >> 13; x *= 3266489917u;
                x ^= x >> 16;
                return (x & 0xFFFFFFu) / 16777215f;
            }
        }

        private float Progress(float sec) => sec <= 0f ? 1f : Clamp01(_stageElapsed / sec);

        private void Advance(OutroStage next)
        {
            _stage = next;
            _stageElapsed = 0f;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float SmoothStep(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }

    /// <summary>終幕の尺。<c>show.json</c> の <c>run.outro</c> から作る。</summary>
    [Serializable]
    public struct OutroTiming
    {
        /// <summary>スクリーンがちかちかしながら消えるまで。</summary>
        public float flickerSec;

        /// <summary>消えてから報告が出るまでの、何も無い黒。</summary>
        public float darkSec;

        /// <summary>報告の文字が浮かび上がるまで。<b>出た後は消えない</b>（次のランまで）。</summary>
        public float reportFadeSec;

        /// <summary>
        /// コード既定。合計 8.7s。
        ///
        /// ⚠ <b>ちらつきは短くしない。</b> 「だんだん」と言われているので、
        /// 3 秒程度だと「切れかけ」ではなく「切れた」になる。
        /// </summary>
        public static OutroTiming Default => new OutroTiming
        {
            flickerSec = 6.0f,
            darkSec = 1.2f,
            reportFadeSec = 1.5f,
        };

        /// <summary>0 / 負 / 異常値を既定へ倒す（<c>run.outro</c> が欠けた show.json でも走る）。</summary>
        public OutroTiming Sanitized()
        {
            OutroTiming d = Default;
            return new OutroTiming
            {
                flickerSec = Pick(flickerSec, d.flickerSec),
                darkSec = Pick(darkSec, d.darkSec),
                reportFadeSec = Pick(reportFadeSec, d.reportFadeSec),
            };
        }

        /// <summary>合計（報告を出し切るまで）。</summary>
        public float TotalSec => flickerSec + darkSec + reportFadeSec;

        private static float Pick(float v, float def) => v > 0.01f && v < 60f ? v : def;
    }
}
