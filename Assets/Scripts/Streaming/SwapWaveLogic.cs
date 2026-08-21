#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>黒い波の形と時間</b>（設計 A / B / C / D。`reports/2026-08-21_swap-wave-design.html`）。
    ///
    /// <see cref="SwapMorphLogic"/> は「段がどこまで進んだか」だけを持ち、この class は
    /// <b>その段の上でどんな波が走るか</b>を持つ。分けてあるのは、波をいくら触っても
    /// 段の進み・差し替えの縁・総尺が 1 ミリも動かないようにするため。
    ///
    /// <b>美学の柱</b>（設計 1 節）:
    ///   1. <b>縦は機械、横は生き物</b> — 帯の格子（48 行の等間隔）は装置の走査線。**縦は絶対に揺らさない**
    ///   2. <b>装置は正直、呪いは応答する</b> — 体験者に応えるのは黒だけ（<see cref="SwapEnergyLogic"/>）
    ///   3. <b>黒だけで組む</b> — 明るい線・色・白は使わない（`canon/LEDGER.md` 0091）。
    ///      かっこよさは**形と時間（緩急）だけ**で作る
    ///
    /// <b>走る波（A）</b>は sin ではなく**非対称パルス** — 前縁が速く立ち上がり、後ろへ exp で尾を引く。
    /// 向きは段の物語と結ぶ:
    /// <list type="bullet">
    /// <item>人 → 人形 … 下へ（前線も人形の現れる足元も下）</item>
    /// <item>人形 → 人 … 上へ（前線も育つ先の背丈も上）</item>
    /// </list>
    /// 1 本の swap で向きは変わらない。ほどける段は前線の**先触れ**（1.5 倍速く）、縮む / 育つ段は
    /// 行き先への**収束**、晴れる段は最後の 1 本が走り抜けて**その後ろから帯が消える**。
    ///
    /// 数値はすべてここに置く（設計 7 節「数値は全部 1 か所」）。
    /// dt 注入の純ロジックなので EditMode テストで固定できる。
    /// </summary>
    public sealed class SwapWaveLogic
    {
        /// <summary>
        /// 山の速さ ÷ 前線の速さ。1 より大きいので、ほどける段の山は**前線を追い越して先を舐める**。
        /// </summary>
        public const float CrestSpeedRatio = 1.5f;

        /// <summary>
        /// ⚠⚠ <b>シェーダの <c>SwapFront</c> の <c>band</c> と対の値。</b>前線は
        /// <c>cover * (1 + band)</c> で進むので、前線の速さを解くのにこの値が要る。
        /// 片方だけ直すと山と前線の関係（1.5 倍）が黙って狂う。
        /// </summary>
        public const float FrontBand = 0.30f;

        /// <summary>
        /// 山の間隔（figure 単位。体高 = 2.0）。設計「体高の 1.2 倍 ＝ 画面にいつも 1 つだけ山がある」。
        /// </summary>
        public const float CrestIntervalFig = 2.4f;

        /// <summary>
        /// 山が体の外から入ってくる余白（figure 単位）。0 にすると、周回のたびに
        /// **山が頭（足元）へ全振幅で湧いて出る**。
        /// </summary>
        public const float EntryMarginFig = 0.30f;

        /// <summary>
        /// 山が棒の端へ足す量（figure 単位）。⚠ 上げると波ではなく「膨らんだ塊」に見える
        /// （`SwapWaveExt` の shift 0.17 / breath 0.20 と同じ桁に収める）。
        /// </summary>
        public const float CrestAmpFig = 0.16f;

        /// <summary>針（設計 B）の全体の強さ。行ごとの長さはシェーダが hash で決める。</summary>
        public const float NeedleGain = 1f;

        /// <summary>稀に跳ぶ帯（設計 C）の全体の強さ。</summary>
        public const float SpikeGain = 1f;

        /// <summary>
        /// キメの一拍（設計 D）の長さ（秒）。**画面が実際に差し替わる 1 フレーム**から数える。
        /// 既存の乱れパルス（<see cref="SwapMorphLogic.VeilSec"/> = 0.20 秒）と同じ縁なので、
        /// 画・乱れ・音がその瞬間に揃う。
        /// </summary>
        public const float BeatSec = 0.25f;

        /// <summary>キメの一拍のあいだに山が走り抜ける距離（figure 単位 ＝ 体高 1.3 倍）。</summary>
        public const float BeatSweepFig = 2.6f;

        /// <summary>
        /// 手のホットスポットの半径（figure 単位）。**実寸の人の figure 空間**で測る
        /// （<c>_SwapRect0</c> 側。縮む人型に貼り付けると、映像の中の腕から離れていく）。
        /// </summary>
        public const float HotRadiusFig = 0.55f;

        /// <summary>1 フレーム分の波。<see cref="SwapMorphFx"/> がそのまま uniform へ流す。</summary>
        public readonly struct Wave
        {
            /// <summary>山の中心（figure 空間の y。-1 = 足元 / +1 = 頭）。</summary>
            public readonly float crestY;

            /// <summary>山の振幅（figure 単位）。</summary>
            public readonly float crestAmp;

            /// <summary>針の強さ（0 = 針なし）。</summary>
            public readonly float needle;

            /// <summary>稀に跳ぶ帯の強さ（0 = 跳ばない）。</summary>
            public readonly float spike;

            /// <summary>入れ替わりが始まってからの秒数（跳びの刻みに使う）。</summary>
            public readonly float elapsed;

            /// <summary>キメの一拍 1..0（画面が差し替わった縁で 1）。</summary>
            public readonly float beat;

            /// <summary>晴れる段の進み 0..1（0 = まだ晴れていない）。</summary>
            public readonly float clear01;

            /// <summary>基本の波（<c>SwapWaveExt</c>）へ掛ける倍率＝全身のエネルギー（設計 E）。</summary>
            public readonly float ampMul;

            public Wave(float crestY, float crestAmp, float needle, float spike,
                        float elapsed, float beat, float clear01, float ampMul)
            {
                this.crestY = crestY;
                this.crestAmp = crestAmp;
                this.needle = needle;
                this.spike = spike;
                this.elapsed = elapsed;
                this.beat = beat;
                this.clear01 = clear01;
                this.ampMul = ampMul;
            }

            /// <summary>波が 1 本も無い状態（入れ替わりが走っていないときの uniform）。</summary>
            public static Wave Idle => new Wave(0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f);
        }

        private bool _active;
        private SwapMorphLogic.Dir _dir = SwapMorphLogic.Dir.ToDoll;
        private float _total = SwapMorphLogic.DefaultTotalSec;
        private float _elapsed;
        private float _travel;      // 山が走った距離（figure 単位・単調増加）
        private float _beatLeft;
        private float _progressPeak;

        /// <summary>山が走る向き（+1 = 上へ / -1 = 下へ）。<b>1 本の swap で変わらない。</b></summary>
        public float Dir01 => _dir == SwapMorphLogic.Dir.ToDoll ? -1f : 1f;

        /// <summary>山が走った距離（figure 単位・テレメトリと機械の門②用）。</summary>
        public float TravelFig => _travel;

        /// <summary>
        /// 前線の速さ（figure 単位 / 秒）。シェーダの <c>SwapFront</c> は
        /// <c>cover * (1 + band)</c> で体（figure 2.0）を掃くので、ほどける段の尺で割る。
        /// </summary>
        public static float FrontSpeedFig(float totalSec)
        {
            float rise = Mathf.Max(SwapMorphLogic.RiseFrac * Mathf.Max(totalSec, 1e-3f), 1e-3f);
            return 2f * (1f + FrontBand) / rise;
        }

        /// <summary>山の速さ（figure 単位 / 秒）。前線の <see cref="CrestSpeedRatio"/> 倍。</summary>
        public static float CrestSpeedFig(float totalSec) => CrestSpeedRatio * FrontSpeedFig(totalSec);

        /// <summary>入れ替わりの始まりで呼ぶ（<see cref="SwapMorphLogic.Begin"/> と対）。</summary>
        /// <param name="keepTravel">
        /// <b>山の走った距離を引き継ぐ</b>（`canon/LEDGER.md` 0102 の持続の覆い → 入れ替わり）。
        /// 0 へ戻すと、包まれたまま走っていた山がその 1 フレームで体の外へ飛んで湧き直す
        /// ＝ 覆いが続いているのに波だけ切れる。⚠ <b>段の進み（<c>_progressPeak</c>）は引き継がない</b> —
        /// 呼び出し側の <see cref="SwapMorphLogic.Progress01"/> が正で、こちらは単調化の器にすぎない。
        /// </param>
        public void Begin(SwapMorphLogic.Dir dir, float totalSec, bool keepTravel = false)
        {
            _active = true;
            _dir = dir;
            _total = Mathf.Clamp(totalSec > 0f ? totalSec : SwapMorphLogic.DefaultTotalSec,
                                 SwapMorphLogic.MinTotalSec, SwapMorphLogic.MaxTotalSec);
            if (!keepTravel)
            {
                _elapsed = 0f;
                _travel = 0f;
            }
            _beatLeft = 0f;
            _progressPeak = 0f;
        }

        /// <summary>畳む（次の <see cref="Tick"/> は <see cref="Wave.Idle"/>）。</summary>
        public void Cancel()
        {
            _active = false;
            _elapsed = 0f;
            _travel = 0f;
            _beatLeft = 0f;
            _progressPeak = 0f;
        }

        /// <summary>
        /// キメの一拍を撃つ（設計 D）。<b>画面が実際に差し替わる 1 フレーム</b>
        /// （<see cref="SwapMorphLogic.Sample.justSwapScreen"/>）で呼ぶ。
        /// **物語のいちばん大きい縁に、視覚のいちばん大きい一拍を置く**のがこの層の仕事。
        /// </summary>
        public void NotifyBeat()
        {
            if (_active) _beatLeft = BeatSec;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="progress01"/> は
        /// <see cref="SwapMorphLogic.Progress01"/>（段の判定を写経しない）。
        /// <paramref name="ampMul"/> / <paramref name="speedMul"/> は
        /// <see cref="SwapEnergyLogic"/> の写像（入力を読まない構成では 1）。
        /// </summary>
        public Wave Tick(float dt, float progress01, float ampMul, float speedMul)
        {
            if (!_active) return Wave.Idle;
            // ⚠ 山の進みだけ dt を切る（1 フレーム落ちで山が体を何度も飛び越えないため）。
            //   段の進み（`progress01`）は切らない — そちらは呼び出し側の時計が正。
            dt = Mathf.Clamp(dt, 0f, SwapEnergyLogic.MaxDtSec);
            _elapsed += dt;
            // ⚠⚠ **走り終わったフレームの `Progress01` は 0 に戻る**（`SwapMorphLogic` の仕様 —
            //   `_active` が落ちてから 1 度読まれる）。生で使うと最後の 1 コマだけ
            //   晴れ方が振り出しへ戻る（帯が全部復活する）。最大値で単調にする。
            _progressPeak = Mathf.Max(_progressPeak, Mathf.Clamp01(progress01));

            float beat = Mathf.Clamp01(_beatLeft / BeatSec);
            // 山は常に前へ進む。キメの一拍のあいだは**全身を 1 度掃く分**を上乗せする
            // （位置を飛ばすのではなく速さを足すので、絵は連続のまま）。
            _travel += CrestSpeedFig(_total) * Mathf.Max(speedMul, 0f) * dt;
            if (_beatLeft > 0f)
            {
                _travel += BeatSweepFig * dt / BeatSec;
                _beatLeft = Mathf.Max(0f, _beatLeft - dt);
            }

            float d = Dir01;
            // 体の外（頭の上 / 足元の下）から入ってきて、反対の外へ抜ける。周回のたびに繰り返す。
            float crestY = -d * (1f + EntryMarginFig) + d * Mod(_travel, CrestIntervalFig);

            float amp = Mathf.Max(ampMul, 0f);
            float clear01 = ClearProgress(_progressPeak);
            return new Wave(
                crestY,
                CrestAmpFig * amp * (1f + beat),
                NeedleGain * amp * (1f + beat),
                // 跳びは晴れる段で引く（晴れながら事故が起きると「壊れた」に読み替わる）。
                SpikeGain * amp * (1f - clear01),
                _elapsed, beat, clear01, amp);
        }

        /// <summary>
        /// 晴れる段の進み 0..1。段の割合は <see cref="SwapMorphLogic"/> のものを使う（写経しない）。
        /// </summary>
        public static float ClearProgress(float progress01)
        {
            float start = SwapMorphLogic.RiseFrac + SwapMorphLogic.MorphFrac;
            float span = Mathf.Max(1f - start, 0.01f);
            return Mathf.Clamp01((progress01 - start) / span);
        }

        /// <summary>常に非負の剰余（<c>Mathf.Repeat</c> と同じだが、この class の中で完結させる）。</summary>
        private static float Mod(float x, float m)
        {
            if (m <= 1e-4f) return 0f;
            float r = x - Mathf.Floor(x / m) * m;
            return r < 0f ? r + m : r;
        }
    }
}
