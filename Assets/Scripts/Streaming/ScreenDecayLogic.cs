#nullable enable
using System;

namespace FixedCamVr.Streaming
{
    /// <summary>画へ出している劣化の根拠。走行ログと純ロジックテストが読む。</summary>
    public enum ScreenDecayShownMode
    {
        Normal,
        DollPov,
        DollMorph,
        DollHold,
        Release,
    }

    /// <summary>
    /// 音が読む単調な装置の劣化と、主映像へ出す加工を分けて保持する。
    /// 通常mono/粗さは採用初期値で固定。暗部ノイズだけを確定区間位置で進める。
    /// 実際の人形視点ではmono1、人形化後はmono1/noise終端を保持する。
    /// 解除は終了相でもReleaseKを進めて初期画へ戻し、音のProgressは保持する。
    /// </summary>
    public sealed class ScreenDecayLogic
    {
        /// <summary>進み 0 のときの、枠を横切るブロック数。映像の領域は 4:3 が 16:9 の枠へ
        /// letterbox される分だけ狭いので **×0.75 ＝ 600**（ソース 640px に対し 1.07 画素／ブロック
        /// ＝ ほとんど何も起きない）。`canon/LEDGER.md` 0021。</summary>
        public const float FineBlocks = 800f;

        /// <summary>進み 1 のときの、枠を横切るブロック数（**映像の領域で横 200 ブロック**
        /// ＝ 3.2 画素／ブロック）。**最後の周へ入った瞬間にここへ着き、以後は動かない。**
        /// 下げすぎると、最後の周に流れる 1 周目の録画で**人型と「手を上げていない」が読めなくなる**
        /// （2026-08-12 に 110 = 7.8 画素／ブロックまで落として実際に読めなくなった）。</summary>
        public const float EndBlocks = 267f;

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

        /// <summary>
        /// <b>呪いが解けてから、視界が戻り切るまで (秒)。</b>
        /// 出どころは <c>canon/LEDGER.md</c> 0083（「4周目のAで報告して現実世界に戻ったタイミングで、
        /// 視界の悪さも元に戻そう」）。
        ///
        /// ⚠ 締めのカットの次は <c>live</c> 3.0 秒で、その頭に乱れ（0.5〜0.7 秒）が乗る。
        /// 戻しは報告に伴う乱れの中へ収める。終了相へ移っても時計を止めない。
        /// </summary>
        public const float ReleaseSec = 0.5f;

        /// <summary>通常のライブ映像の固定mono。音が読む装置の劣化は1まで進み続ける。</summary>
        public const float NormalMax = 0.5f;

        /// <summary>CG 人形化の乱れの中で 0.5 → 1 へ渡る目安。</summary>
        public const float DollMorphSec = 0.5f;

        private float _progress;
        private int _lastLap = 1;
        private float _lastLapElapsed;
        private float _lapRef = LapRefSec;

        // 呪いが解けた（4 周目 A の締めが報告で進んだ）。
        private bool _released;
        private float _releaseSec;
        private float _releaseFrom;

        // 通常monoは固定。暗部ノイズだけを確定区間位置で決める。
        private float _normalTarget = NormalMax;
        private float _noiseTarget = CrtSignalProfile.NoiseDark;
        private float _releaseNoiseFrom;

        // 画に出る状態は、通常位置と「いま本当に出ている素材」で決まる。
        private float _shown = NormalMax;
        private float _dollMorphSec;
        private bool _dollSeen;
        private bool _dollLatched;
        private ScreenDecayShownMode _shownMode;

        /// <summary>
        /// <b>装置の劣化そのものの進み 0..1</b>（単調・下がらない）。
        ///
        /// ⚠⚠ <b>呪いが解けても、これは下がらない。</b> 読み手が 3 つあり、下げてよいのは画だけ:
        ///   - <c>ShowSoundDirector</c> — 装置の声の痩せ（<c>bed_device → bed_device_worn</c>）。
        ///     <b>下げると音が新品へ戻り、「直った」を音で宣言する</b> ＝ 最も避けたい
        ///     「クリア演出」がまさに音で出る。装置は呪いとは無関係に使い込まれている
        ///   - 連絡の面の斑（<c>CommsPanel.PushCurseTarget</c>）— AI の侵食は表示イベントで進む。
        ///     ⚠ <b>侵食が消えるのは解除の側</b>（<see cref="ReleaseK"/>）であって、ここではない
        ///   - 画 — こちらだけが <see cref="Shown"/> を読む
        /// </summary>
        public float Progress => _progress;

        /// <summary>通常は0.5。人形視点/人形化は1。解除は採用初期値0.5へ戻す。</summary>
        public float Shown => _shown;

        /// <summary>通常画面の固定mono。テレメトリが <see cref="Shown"/> と照合する。</summary>
        public float NormalTarget => _normalTarget;
        public float NoiseTarget => _noiseTarget;
        public float NoiseDark => _released
            ? _releaseNoiseFrom + (CrtSignalProfile.NoiseDark - _releaseNoiseFrom) * ReleaseK
            : (_dollLatched || _dollSeen ? CrtSignalProfile.EndNoiseDark : _noiseTarget);

        /// <summary>いまの <see cref="Shown"/> を決めた表示状態。</summary>
        public ScreenDecayShownMode ShownMode => _shownMode;

        /// <summary>
        /// <see cref="Shown"/> に対応する「枠を横切るブロック数」。<b>0 = 量子化しない。</b>
        /// 通常と解除後は <see cref="NormalMax"/> に対応する固定値を使う。
        /// </summary>
        public float ShownBlocks => Shown <= OffThreshold ? 0f : BlocksFor(Shown);

        /// <summary>呪いが解けたか（テレメトリ・テスト用）。</summary>
        public bool Released => _released;

        /// <summary>
        /// <b>解除の進み 0..1</b>（smoothstep 済み・0 = まだ呪われている / 1 = 戻り切った）。
        ///
        /// 解像度と色が戻る共通の進み。通信侵食は2-Cと3-Aの表示イベントで進むため、
        /// この値を読まない。
        /// </summary>
        public float ReleaseK => _released ? Smooth(Clamp01(_releaseSec / ReleaseSec)) : 0f;

        /// <summary>
        /// <b>呪いが解けた。</b>ここから <see cref="ReleaseSec"/> かけて視界が元へ戻る
        /// （解像度と色が同じ 1 本の進みを共有しているので、<b>両方が一緒に戻る</b>）。
        ///
        /// ⚠⚠ <b>下がるのは画（<see cref="Shown"/>）だけ。</b> <see cref="Progress"/> は単調のまま —
        /// 音（装置の声の痩せ）が同じ値を読んでいるので、そちらまで戻すと
        /// <b>「直った」を音で宣言する</b>ことになる。
        ///
        /// 通信侵食は独立して保持される。ここで戻すのは主映像だけ。
        ///
        /// ⚠ 冪等。2 度目以降の報告では何も起きない（戻る途中で押し直しても速さが変わらない）。
        /// </summary>
        public void Release()
        {
            if (_released) return;
            _releaseNoiseFrom = NoiseDark;
            _released = true;
            _releaseSec = 0f;
            _releaseFrom = _shown;
            _shownMode = ScreenDecayShownMode.Release;
        }

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
            // ⚠ 解けたラッチも落とす。落とさないと**次の体験者は最初から解けている**
            //（画がいつまでも鮮明なまま）。
            _released = false;
            _releaseSec = 0f;
            _releaseFrom = 0f;
            _normalTarget = NormalMax;
            _noiseTarget = CrtSignalProfile.NoiseDark;
            _releaseNoiseFrom = CrtSignalProfile.NoiseDark;
            _shown = NormalMax;
            _dollMorphSec = 0f;
            _dollSeen = false;
            _dollLatched = false;
            _shownMode = ScreenDecayShownMode.Normal;
        }

        /// <summary>
        /// 1 フレーム進める。<paramref name="running"/> が false のあいだは
        /// <see cref="Progress"/> を保持する。解除中の <see cref="Shown"/> は相に関係なく進む。
        ///
        /// ⚠ <b>1 周の目安は前の周の実測へ差し替える。</b> 定数で当てると、実際の周がそれより短いとき
        /// 周の中の進みが 1 に届かず、**不足が最後の境目へ持ち越されて「最後の周に入ってもまだ落ち続ける」**
        /// （2026-08-12 実機: 目安 30 秒に対し実際は 24 秒で、3 周目に入ってから 2 秒ぶん落ち続けた）。
        /// 前の周の実測を使えば周の中で 1 に届き切るので、境目の不足が 0 になる。
        /// </summary>
        public void Tick(float dt, bool running, int lap, int totalLaps, float lapElapsedSec,
                         bool dollPovShowing = false, bool dollShowing = false,
                         bool dollTransitioning = false, float dollTransition01 = -1f,
                         float normalTarget = -1f, float noiseTarget = -1f)
        {
            if (dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            float visualDt = dt;
            if (dt > MaxStepSec) dt = MaxStepSec;
            _normalTarget = NormalMax;
            if (!float.IsNaN(noiseTarget) && !float.IsInfinity(noiseTarget) && noiseTarget >= 0f)
                _noiseTarget = Clamp(noiseTarget, CrtSignalProfile.NoiseDark, CrtSignalProfile.EndNoiseDark);

            // 解除の進み。
            // ⚠⚠ **running を条件にしない。** 報告 → 現実の 3 秒 → 終幕 は連続した 1 つの出来事で、
            //    その途中で相が Run から Finished へ移る。相を見ると**戻り切る前に凍り**、
            //    「戻った」が体験されないまま画面が死ぬ。
            if (_released && _releaseSec < ReleaseSec)
            {
                _releaseSec += visualDt;
                _shown = _releaseFrom + (NormalMax - _releaseFrom) * ReleaseK;
                _shownMode = ScreenDecayShownMode.Release;
            }

            if (running)
            {
                if (lap != _lastLap)
                {
                    // 周が変わった。**直前に見た経過**がその周にかかった秒（lapElapsed は既に 0 へ戻っている）。
                    if (_lastLapElapsed > 0f) _lapRef = Clamp(_lastLapElapsed, MinLapRefSec, MaxLapRefSec);
                    _lastLap = lap;
                }
                _lastLapElapsed = lapElapsedSec;

                float target = TargetFor(lap, totalLaps, lapElapsedSec, _lapRef);
                if (target > _progress)
                {
                    float next = _progress + MaxRisePerSec * dt;
                    _progress = next < target ? next : target;
                }
            }

            if (!_released)
                TickShown(visualDt, dollPovShowing, dollShowing, dollTransitioning, dollTransition01);
        }

        private void TickShown(float dt, bool dollPovShowing, bool dollShowing,
                               bool dollTransitioning, float dollTransition01)
        {
            float normal = _normalTarget;

            // 2-C の人形視点は、素材が実際に overlay へ混ざったフレームだけ最大にする。
            if (dollPovShowing)
            {
                _shown = 1f;
                _shownMode = ScreenDecayShownMode.DollPov;
                return;
            }

            if (_dollLatched)
            {
                _shown = 1f;
                _shownMode = ScreenDecayShownMode.DollHold;
                return;
            }

            if (dollShowing)
            {
                _dollSeen = true;
                if (dollTransitioning)
                {
                    float k;
                    if (dollTransition01 >= 0f)
                    {
                        k = Clamp01(dollTransition01);
                    }
                    else
                    {
                        _dollMorphSec += dt;
                        k = Clamp01(_dollMorphSec / DollMorphSec);
                    }
                    _shown = NormalMax + (1f - NormalMax) * Smooth(k);
                    _shownMode = ScreenDecayShownMode.DollMorph;
                    return;
                }

                // 遷移が解けた最初の読める CG フレームで 1。以後はカメラや相が変わっても保持する。
                _dollLatched = true;
                _shown = 1f;
                _shownMode = ScreenDecayShownMode.DollHold;
                return;
            }

            // CG が乱れの最中に畳まれた場合も、一度出た事実を失わない。
            if (_dollSeen)
            {
                _dollLatched = true;
                _shown = 1f;
                _shownMode = ScreenDecayShownMode.DollHold;
                return;
            }

            _shown = normal;
            _shownMode = ScreenDecayShownMode.Normal;
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

        /// <summary>通常monoは位置・時間に依存せず固定。既存プレビューの入口も同じ値を返す。</summary>
        public static float NormalTargetFor(int segmentLap, int camera, int[]? courseOrder)
            => NormalMax;

        /// <summary>確定区間の位置だけで暗部ノイズを決める。3周目の頭から終端。逆走は位置へ戻す。</summary>
        public static float NoiseTargetFor(int segmentLap, int camera, int[]? courseOrder)
        {
            if (segmentLap < 1 || courseOrder == null || courseOrder.Length == 0) return CrtSignalProfile.NoiseDark;
            int index = Array.IndexOf(courseOrder, camera);
            if (index < 0) return CrtSignalProfile.NoiseDark;
            float position = Clamp01((float)(((double)segmentLap - 1 + (double)index / courseOrder.Length) / 2d));
            return CrtSignalProfile.NoiseDark + (CrtSignalProfile.EndNoiseDark - CrtSignalProfile.NoiseDark) * position;
        }

        /// <summary>
        /// 進み → 枠を横切るブロック数。<b>線形</b>に下げる（映像の領域で <b>600 → 400 → 200</b>）。
        ///
        /// ⚠ 2026-08-12 まで<b>等比</b>だった。理由は「等差だと最初の 1 周で見た目が全部落ちて、
        /// 後の 2 周が動かない」で、それは 900 → 110（比 8.2 倍）のときは正しい。
        /// いまは 800 → 267（<b>比 3 倍</b>）なので、線形でも 1 ブロックあたりの画素は
        /// 1.07 → 1.6 → 3.2 と素直に伸びる。ユーザーが各周の A の値を 3 点で指定したので
        /// （`canon/LEDGER.md` 0021）、その 3 点をそのまま通す線形にしてある。
        /// <b>比を大きく戻すなら等比へ戻すこと</b>（後半だけが動く画になる）。
        /// </summary>
        public static float BlocksFor(float progress)
            => FineBlocks + (EndBlocks - FineBlocks) * Clamp01(progress);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>smoothstep。戻り始めと戻り終わりの角を取る（直線だと止まる瞬間が見える）。</summary>
        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
