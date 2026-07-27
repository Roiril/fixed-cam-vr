#nullable enable
using System;
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 演出（Take）の純判定・計時ロジック。「どの演出を・いつ・どのカットで走らせるか」だけを決める。
    /// 実際の画面適用（カメラ切替 / オーバーレイ / post）は <see cref="TakeRunner"/> が行う。
    /// <see cref="InsertLogic"/> の後継で、**1 本の演出が複数カットを持てる**点が本質的な差。
    ///
    /// 契約の正本は <c>.claude/plans/2026-07-25_shot-timeline-foundation.md</c> §6.3。要点:
    ///   1. 同時に走る演出は 1 本。走行中に別演出の開始条件が来ても**待たせない**
    ///   2. 同一区間の順序: at=enter は offsetSec 昇順 → 同値なら配列順 / at=exit は配列順
    ///   3. ifMissed=fireOnExit: offsetSec に達する前に離脱したら、その離脱の瞬間に発火
    ///   4. 区間を離れた時点で未発火の演出は必ず決着する（発火 or 破棄）。**遅れて別区間で発火しない**
    ///   5. once はラン内 1 回（<see cref="ResetRun"/> でクリア）
    ///   6. ライブ卓の抑止中は発火しない
    ///   6b. **開始規則「このラインを通過したら」（at=line）** は時刻ではなく床のラインの横断で due になる
    ///       （<see cref="LineCrossLogic"/> の結果を <see cref="Tick"/> で受ける）。
    ///       ラインは担当カメラに紐づき、区間のカメラと違えば踏んでも発火しない。
    ///       それ以外（武装・決着・once・ifMissed）は enter と完全に同じ扱い
    ///       — 契約は <c>.claude/plans/2026-07-27_position-trigger.md</c> §3
    ///   7. maxDurationSec（既定 45s）を超えたら強制終了する
    ///   8. 終了時の復帰先は「いま体験者が居るゾーン」（引数で受ける・復元ではなく再計算）
    ///
    /// 時刻・現在ゾーンはすべて引数で受ける（UnityEngine 非依存）。
    /// </summary>
    public sealed class TakeRunnerLogic
    {
        /// <summary>演出 1 本の実行定義（区間キー + 開始規則 + カット列）。</summary>
        public struct Def
        {
            public int lap;
            public int camera;          // 区間キーのカメラ
            public bool onExit;         // true=exit アンカー / false=enter or line アンカー
            public float offsetSec;     // enter のみ
            public bool skipWhenMissed; // true=skip / false=fireOnExit
            public bool once;
            public float maxDurationSec;
            public bool yieldOnZoneChange; // true=yield（体験者が区間を移ったら打ち切る）/ false=hold
            public float[] stepDurSec;  // カットごとの尺（<0 = untilClipEnd＝外部通知待ち）

            // 開始規則「このラインを通過したら」（at=line）。onExit=false と併用する。
            public bool onLine;         // true = 時刻ではなく床のラインの横断で発火する
            public int lineIndex;       // LineCrossLogic のスロット（範囲外 = 発火しない）
        }

        public enum Action { None, BeginStep, EndTake }

        /// <summary>1 回の評価結果。action=None なら他フィールドは無効。</summary>
        public struct Decision
        {
            public Action action;
            public int takeIndex;
            public int stepIndex;      // BeginStep
            public bool takeStarted;   // BeginStep: この演出の 1 カット目（＝画面の占有を開始する）
            public int returnCamera;   // EndTake: 復帰先
            public bool forced;        // EndTake: watchdog による強制終了
        }

        // 武装中の enter 演出の状態。
        private enum Armed : byte
        {
            Waiting = 0,        // まだ発火時刻に達していない
            DeferredToExit = 1, // 発火時刻を過ぎたが他が走っていた → 離脱時のみ発火しうる
        }

        private Def[] _defs = Array.Empty<Def>();
        private bool[] _fired = Array.Empty<bool>();

        // 現区間で武装している enter 演出（配列順を保つ）。
        private readonly List<int> _armedIndex = new();
        private readonly List<float> _armedDue = new();
        private readonly List<Armed> _armedState = new();

        private bool _hasCurrent;
        private int _curLap, _curCam;
        private bool _suppressed;

        // 走行中の演出。
        private bool _running;
        private int _activeTake = -1;
        private int _activeStep = -1;
        private float _stepEnd;
        private float _deadline;
        private int _baseZoneCam;

        /// <summary>演出が画面を占有中か。</summary>
        public bool IsActive => _running;

        /// <summary>走行中の演出 index（非走行時は -1）。</summary>
        public int ActiveTakeIndex => _activeTake;

        /// <summary>走行中のカット index（非走行時は -1）。</summary>
        public int ActiveStepIndex => _activeStep;

        /// <summary>演出開始時に体験者が居たゾーンのカメラ（復帰先の最終フォールバック）。</summary>
        public int BaseZoneCamera => _baseZoneCam;

        /// <summary>武装中（発火待ち）の演出数。テスト・診断用。</summary>
        public int ArmedCount => _armedIndex.Count;

        /// <summary>定義を差し替える。once の発火済みと進行はリセットする（新ラン相当）。</summary>
        public void SetDefs(Def[] defs)
        {
            _defs = defs ?? Array.Empty<Def>();
            _fired = new bool[_defs.Length];
            ClearArmed();
            _running = false;
            _activeTake = -1;
            _activeStep = -1;
            _hasCurrent = false;
        }

        /// <summary>ラン開始（体験者交代）。once 発火済みと進行を全消去する。定義は保持。</summary>
        public void ResetRun()
        {
            for (int i = 0; i < _fired.Length; i++) _fired[i] = false;
            ClearArmed();
            _running = false;
            _activeTake = -1;
            _activeStep = -1;
            _hasCurrent = false;
        }

        /// <summary>ライブ卓の抑止（activeCue 非空 / cameraOverride 非 null）を通知する。</summary>
        public void SetSuppressed(bool s) => _suppressed = s;

        /// <summary>
        /// ゾーン確定（ショーの時計）を受ける。離脱区間の決着（exit 演出 / 取り逃した enter 演出）を行い、
        /// 進入区間の enter 演出を武装する。返すのは最大 1 つの即時 Decision（離脱時発火）。
        /// </summary>
        public Decision OnZoneCommitted(int newLap, int newCam, bool hadPrev, int prevLap, int prevCam, float now)
        {
            Decision result = default;
            bool segChanged = !hadPrev || prevLap != newLap || prevCam != newCam;
            if (!segChanged) return result;

            // policy=yield: 体験者が区間を移ったら演出を打ち切って画面を返す（歩行を邪魔しない演出用）。
            // このとき離脱区間の exit 演出は発火しない — 同時 1 本の原則を保ち、打ち切りの直後に
            // 別の演出が始まって「返したのにまた持って行かれる」のを避けるため。
            if (_running && _defs[_activeTake].yieldOnZoneChange)
            {
                Decision yielded = EndTakeDecision(newCam, forced: false);
                ClearArmed();
                _hasCurrent = true;
                _curLap = newLap;
                _curCam = newCam;
                ArmEnterTakes(newLap, newCam, now);
                return yielded;
            }

            if (hadPrev)
            {
                // 離脱の瞬間: 配列順で最初に該当する 1 本だけ発火し、残りは破棄する（不変条件 7）。
                if (!_running && !_suppressed)
                {
                    int pick = FindExitCandidate(prevLap, prevCam);
                    if (pick >= 0)
                    {
                        StartTake(pick, now, newCam);
                        result = BeginStepDecision(takeStarted: true);
                    }
                }
                ClearArmed();
            }

            _hasCurrent = true;
            _curLap = newLap;
            _curCam = newCam;
            ArmEnterTakes(newLap, newCam, now);
            return result;
        }

        /// <summary>
        /// 毎フレーム評価。走行中は watchdog / カット進行 / 終了を、非走行中は発火判定を行う。
        /// <paramref name="latestZoneCam"/> は「いま体験者が居るゾーンのカメラ」（終了時の復帰先）。
        /// <paramref name="lines"/> は通過ラインの今フレームの結果（<see cref="LineCrossLogic.StateView"/>）。
        /// null なら at=line の演出は発火しない（位置が取れない環境＝従来どおりの動き）。
        /// </summary>
        public Decision Tick(float now, int latestZoneCam, LineCrossLogic.State[]? lines = null)
        {
            if (_running)
            {
                if (now >= _deadline)
                    return EndTakeDecision(latestZoneCam, forced: true);

                ResolveOverdueWhileBlocked(now, lines);

                if (now >= _stepEnd)
                {
                    int next = _activeStep + 1;
                    float[] durs = _defs[_activeTake].stepDurSec;
                    if (next < durs.Length)
                    {
                        _activeStep = next;
                        _stepEnd = StepEndTime(now, durs[next]);
                        return BeginStepDecision(takeStarted: false);
                    }
                    return EndTakeDecision(latestZoneCam, forced: false);
                }
                return default;
            }

            if (_suppressed)
            {
                // 抑止中に発火条件を満たしたものも「待たせない」（離脱時発火 or 破棄へ落とす）。
                ResolveOverdueWhileBlocked(now, lines);
                return default;
            }

            int pick = FindDue(now, lines);
            if (pick < 0)
            {
                ResolveOverdueWhileBlocked(now, lines);
                return default;
            }

            RemoveArmedAt(pick, out int takeIndex);
            StartTake(takeIndex, now, _hasCurrent ? _curCam : 0);
            // 同時に発火条件を迎えていた他の演出は、この 1 本に譲って決着する。
            ResolveOverdueWhileBlocked(now, lines);
            return BeginStepDecision(takeStarted: true);
        }

        /// <summary>
        /// 尺が <c>untilClipEnd</c> のカットで、素材の再生が終わったことを通知する
        /// （<see cref="TakeRunner"/> が VideoPlayer / 画像表示の終端で呼ぶ）。次の <see cref="Tick"/> で進行する。
        /// </summary>
        public void NotifyCurrentStepFinished(float now) => SetCurrentStepEnd(now);

        /// <summary>
        /// 現カットの終了時刻を外部から与える（素材が無い <c>untilClipEnd</c> を既定尺で畳む等）。
        /// watchdog は別途効き続けるので、ここで未来を指定しても演出は必ず終わる。
        /// </summary>
        public void SetCurrentStepEnd(float endTime)
        {
            if (_running) _stepEnd = endTime;
        }

        /// <summary>走行中の演出を外部都合で畳む（ラン開始・定義差し替え時の後片付け）。</summary>
        public void AbortActive()
        {
            _running = false;
            _activeTake = -1;
            _activeStep = -1;
        }

        // ---- 内部 ----

        private void StartTake(int index, float now, int baseZoneCam)
        {
            _running = true;
            _activeTake = index;
            _activeStep = 0;
            _fired[index] = true;
            _baseZoneCam = baseZoneCam;
            float[] durs = _defs[index].stepDurSec;
            _stepEnd = StepEndTime(now, durs.Length > 0 ? durs[0] : 0f);
            _deadline = now + TakeSchema.ResolveMaxDuration(_defs[index].maxDurationSec);
        }

        // 尺が負（untilClipEnd）なら外部通知待ち = 無限大。watchdog が上限を保証する。
        private static float StepEndTime(float now, float durSec)
            => durSec < 0f ? float.PositiveInfinity : now + durSec;

        private Decision BeginStepDecision(bool takeStarted) => new()
        {
            action = Action.BeginStep,
            takeIndex = _activeTake,
            stepIndex = _activeStep,
            takeStarted = takeStarted,
        };

        private Decision EndTakeDecision(int latestZoneCam, bool forced)
        {
            int take = _activeTake;
            _running = false;
            _activeTake = -1;
            _activeStep = -1;
            return new Decision
            {
                action = Action.EndTake,
                takeIndex = take,
                returnCamera = latestZoneCam,
                forced = forced,
            };
        }

        // 進入区間の enter / line 演出を武装する（offsetSec 昇順 → 同値は配列順）。
        // ライントリガー（onLine）は offsetSec を持たない（契約上 0）ので、同値タイブレーク＝配列順で並ぶ。
        // 武装さえすれば due 判定（IsDue）が時刻と位置を出し分けるので、ここに分岐は要らない。
        private void ArmEnterTakes(int lap, int camera, float now)
        {
            var order = new List<int>();
            for (int i = 0; i < _defs.Length; i++)
            {
                Def d = _defs[i];
                if (d.onExit || d.lap != lap || d.camera != camera) continue;
                if (d.once && _fired[i]) continue;
                if (d.stepDurSec.Length == 0) continue; // カット空の演出は無視
                order.Add(i);
            }
            order.Sort((a, b) =>
            {
                int c = _defs[a].offsetSec.CompareTo(_defs[b].offsetSec);
                return c != 0 ? c : a.CompareTo(b);
            });
            foreach (int i in order)
            {
                _armedIndex.Add(i);
                _armedDue.Add(now + (_defs[i].offsetSec < 0f ? 0f : _defs[i].offsetSec));
                _armedState.Add(Armed.Waiting);
            }
        }

        // 発火条件を満たした Waiting のうち先頭を返す（無ければ -1）。
        private int FindDue(float now, LineCrossLogic.State[]? lines)
        {
            for (int k = 0; k < _armedIndex.Count; k++)
                if (_armedState[k] == Armed.Waiting && IsDue(k, now, lines)) return k;
            return -1;
        }

        // 武装スロット k の発火条件。時刻トリガーは due 時刻、ライントリガーは今フレームの横断で決まる。
        private bool IsDue(int k, float now, LineCrossLogic.State[]? lines)
        {
            Def d = _defs[_armedIndex[k]];
            if (!d.onLine) return now >= _armedDue[k];
            if (lines == null || d.lineIndex < 0 || d.lineIndex >= lines.Length) return false;
            LineCrossLogic.State s = lines[d.lineIndex];
            // 横断は事象だが、武装がゾーン確定（dwell 0.5s）で起きるため猶予を持たせる
            // （ゾーンの入口すぐの線が永久に発火しないのを防ぐ・LineCrossLogic.CrossLatchSec）。
            if (now - s.crossedAtSec > LineCrossLogic.CrossLatchSec) return false;
            // **ラインは担当カメラに紐づく**（区間紐づけ）。別のゾーンのラインを踏んでも発火させない。
            // 武装自体が区間限定なので通常は一致するが、著作ミス（別ゾーンのラインを選んだ）を
            // ここで構造的に無効化する。camera<0 は未指定 = どの区間でも可。
            return s.camera < 0 || s.camera == d.camera;
        }

        // 走行中 / 抑止中に発火条件を満たしたものを決着させる（待たせない）。
        //   skip        → 武装から外して破棄
        //   fireOnExit  → 離脱時のみ発火しうる状態へ落とす
        private void ResolveOverdueWhileBlocked(float now, LineCrossLogic.State[]? lines)
        {
            for (int k = _armedIndex.Count - 1; k >= 0; k--)
            {
                if (_armedState[k] != Armed.Waiting || !IsDue(k, now, lines)) continue;
                if (_defs[_armedIndex[k]].skipWhenMissed) RemoveArmedAt(k, out _);
                else _armedState[k] = Armed.DeferredToExit;
            }
        }

        // 離脱の瞬間に発火する候補を配列順で 1 つ選ぶ。
        //   - at=exit で未発火のもの
        //   - 武装中の enter / line 演出のうち ifMissed=fireOnExit のもの
        //     （時刻未達・超過どちらでも / ライントリガーなら「そのラインを通過しなかった」場合）
        private int FindExitCandidate(int lap, int camera)
        {
            for (int i = 0; i < _defs.Length; i++)
            {
                Def d = _defs[i];
                if (d.lap != lap || d.camera != camera) continue;
                if (d.stepDurSec.Length == 0) continue;
                if (d.once && _fired[i]) continue;
                if (d.onExit || (!d.skipWhenMissed && IsArmed(i))) return i; // 配列順で先頭が勝つ
            }
            return -1;
        }

        private bool IsArmed(int takeIndex) => _armedIndex.Contains(takeIndex);

        private void RemoveArmedAt(int k, out int takeIndex)
        {
            takeIndex = _armedIndex[k];
            _armedIndex.RemoveAt(k);
            _armedDue.RemoveAt(k);
            _armedState.RemoveAt(k);
        }

        private void ClearArmed()
        {
            _armedIndex.Clear();
            _armedDue.Clear();
            _armedState.Clear();
        }
    }
}
