// 廻リ視 ショーシミュレータのエンジン（Unity 純ロジックの JS ミラー・DOM 非依存）。
//   計画: .claude/plans/2026-07-25_show-simulator.md 段 S3
//
// ⚠ **Unity 側（C#）が正**。ここは移植であり、食い違ったら直すのは必ず JS 側。
//   移植元（1 対 1 対応・関数名も揃える）:
//     ZonePickLogic.cs            → containsBox / pickZone
//     ZoneProgressionLogic.cs     → ZoneProgression       （時計 = dwell だけ）
//     SwitchDirectorLogic         → SwitchDirector        （画面 = cooldown / 凍結）
//     LapCounterLogic             → LapCounter            （周回 = 進行ポインタ）
//     TakeRunnerLogic.cs          → TakeRunner            （演出 = 武装 / 発火 / カット / watchdog）
//     ShowScenarioRunner.cs       → createShowRunner / runScenario（束ね + トレース）
//
//   一致は golden fixture で機械固定する:
//     入力 Assets/Tests/Fixtures/scenario_walk.json
//     期待 Assets/Tests/Fixtures/scenario_walk.trace.json
//     照合 scenario-engine.test.mjs（イベント列は厳密・時刻は ±1 tick）
//
// 1 tick の評価順は本番の因果と同じ（**この順序を変えるとトレースが一致しない**）:
//   ① ゾーン判定 → 時計へ要求 ② 時計 Tick（ゾーン確定）③ 既定映し先を画面層へ
//   ④ 画面の commit 判定 ⑤ ゾーン確定の通知（周回 → 区間 → 演出の武装）⑥ 演出 Tick

/**
 * C# の `float`（32bit）演算を JS で再現するための丸め。
 *
 * 時刻の比較（`now - since >= dwell` / `now >= stepEnd`）は C# 側が float で行うため、
 * JS の double のまま計算すると境界がちょうど 1 tick ずれることがある（実測: t_B_late の
 * 終了が 16560 → 16580）。**時刻の生成・加算・差分だけ** fround で float 精度へ丸めると
 * golden とビット単位で揃う。トレース比較の契約は ±1 tick 許容（計画 §1）だが、
 * 許容の中に drift を隠さないため実装は厳密一致を狙う。
 */
const f32 = Math.fround;

export const DEFAULT_TICK_MS = 20;          // ShowScenarioRunner.DefaultTickMs
export const DEFAULT_DWELL_SEC = 0.5;       // ZoneProgressionLogic.DefaultDwellSec
export const DEFAULT_COOLDOWN_SEC = 0.5;    // SwitchDirectorLogic.DefaultCooldownSec
export const DEFAULT_MAX_DURATION_SEC = 45; // TakeSchema.DefaultMaxDurationSec
export const FALLBACK_STEP_DUR_SEC = 4;     // TakeSchema.FallbackStepDurSec

/** TakeSchema.ResolveMaxDuration（0 / 負値 = コード既定）。 */
export const resolveMaxDuration = (v) => (v > 0 ? v : DEFAULT_MAX_DURATION_SEC);

/** SwitchDirectorLogic.ResolveTiming（show.json control の present 判定）。 */
export const resolveTiming = (overrideValue, defaultValue) =>
  (overrideValue > 0 ? overrideValue : defaultValue);

// ---- ゾーン判定（ZonePickLogic）---------------------------------------------

/** 無回転（AABB 互換）の箱。ZonePickLogic.Box.Aabb。 */
export function boxAabb(cx, cy, cz, hx, hy, hz, cameraIndex, priority = 0) {
  return { cx, cy, cz, hx, hy, hz, cos: 1, sin: 0, camera: cameraIndex, priority };
}

/** yaw（度）付きの箱。ZonePickLogic.Box.WithYaw。 */
export function boxWithYaw(cx, cy, cz, hx, hy, hz, yawDeg, cameraIndex, priority = 0) {
  const r = (yawDeg * Math.PI) / 180;
  const b = boxAabb(cx, cy, cz, hx, hy, hz, cameraIndex, priority);
  b.cos = Math.cos(r); b.sin = Math.sin(r);
  return b;
}

/** ZonePickLogic.Contains（shrink は各軸を内側へ縮める量・負の半長は 0 でクランプ）。 */
export function containsBox(b, x, y, z, shrink = 0) {
  const dx = x - b.cx, dy = y - b.cy, dz = z - b.cz;
  // ワールド差分をゾーンローカル軸へ射影（right = (cos,0,-sin) / forward = (sin,0,cos)）。
  const lx = dx * b.cos - dz * b.sin;
  const lz = dx * b.sin + dz * b.cos;
  const hx = Math.max(0, b.hx - shrink);
  const hy = Math.max(0, b.hy - shrink);
  const hz = Math.max(0, b.hz - shrink);
  return Math.abs(lx) <= hx && Math.abs(dy) <= hy && Math.abs(lz) <= hz;
}

/**
 * ZonePickLogic.Pick。返すのは boxes 内の index（未選択は -1）。
 *   1. 直近ゾーンが shrink 後の箱に入っていれば維持（ヒステリシス）
 *   2. それ以外は包含する中で priority 最大。同値は**配列の先頭が勝つ**（厳密比較）
 *   3. どこにも入っていなければ keepLastWhenOutside の方針
 */
export function pickZone(boxes, x, y, z, currentIndex, hysteresisShrink, keepLastWhenOutside) {
  if (!boxes || boxes.length === 0) return -1;
  if (currentIndex >= 0 && currentIndex < boxes.length
      && containsBox(boxes[currentIndex], x, y, z, hysteresisShrink)) {
    return currentIndex;
  }
  let best = -1;
  let bestPriority = -Infinity;
  for (let i = 0; i < boxes.length; i++) {
    if (!containsBox(boxes[i], x, y, z)) continue;
    if (boxes[i].priority > bestPriority) { best = i; bestPriority = boxes[i].priority; }
  }
  if (best >= 0) return best;
  return keepLastWhenOutside ? currentIndex : -1;
}

// ---- 時計（ZoneProgressionLogic）---------------------------------------------

/**
 * 「体験者がいまどのゾーンに居るか」を確定する。dwell（最小滞在）だけを見る。
 * **画面の状態を一切参照しない** — 画面が凍結していても体験者は歩くので時計は進み続ける。
 */
export class ZoneProgression {
  constructor() {
    this._dwellSec = DEFAULT_DWELL_SEC;
    this._current = -1;
    this._hasPending = false;
    this._pending = 0;
    this._pendingSince = 0;
  }

  get current() { return this._current; }
  get hasPending() { return this._hasPending; }
  get pending() { return this._pending; }

  configure(dwellSec) { this._dwellSec = dwellSec < 0 ? 0 : dwellSec; }

  reset(current) { this._current = current; this._hasPending = false; }

  clearPending() { this._hasPending = false; }

  request(target, now) {
    if (target < 0) return;               // ゾーン外 / 無効 index は保留に触れない
    if (target === this._current) { this._hasPending = false; return; }
    if (!this._hasPending || this._pending !== target) {
      this._hasPending = true;
      this._pending = target;
      this._pendingSince = now;
    }
  }

  /** dwell を満たした保留を確定させる。→ { committed, camera }。 */
  tick(now) {
    if (!this._hasPending) return { committed: false, camera: this._current };
    if (this._pending === this._current) {
      this._hasPending = false;
      return { committed: false, camera: this._current };
    }
    if (f32(now - this._pendingSince) < this._dwellSec) return { committed: false, camera: this._current };
    this._current = this._pending;
    this._hasPending = false;
    return { committed: true, camera: this._current };
  }
}

// ---- 画面（SwitchDirectorLogic）-----------------------------------------------

/**
 * 画面層の切替ガード（クールダウン / cue・演出・override 凍結 / 手動優先）。
 * **最小滞在（dwell）はここには無い**（人の層 = ZoneProgression が持つ）。
 */
export class SwitchDirector {
  constructor() {
    this._cooldownSec = 0;
    this._manualHoldSec = 0;
    this._current = 0;
    this._lastSwitchTime = -Infinity;
    this._lastManualTime = -Infinity;
    this._cueActive = false;
    this._insertActive = false;
    this._overrideActive = false;
    this._hasPendingZone = false;
    this._pendingZone = 0;
  }

  get current() { return this._current; }
  get hasPendingZone() { return this._hasPendingZone; }
  get pendingZone() { return this._pendingZone; }
  get cueActive() { return this._cueActive; }
  get insertActive() { return this._insertActive; }
  get overrideActive() { return this._overrideActive; }

  configure(cooldownSec, manualHoldSec) {
    this._cooldownSec = Math.max(0, cooldownSec);
    this._manualHoldSec = Math.max(0, manualHoldSec);
  }

  reset(current) {
    this._current = current;
    this._lastSwitchTime = -Infinity;
    this._lastManualTime = -Infinity;
    this._hasPendingZone = false;
    this._insertActive = false;
    this._cueActive = false;
    this._overrideActive = false;
  }

  setCueActive(active) { this._cueActive = active; }

  /** override は遷移のたびに stale 保留を無条件クリアする（cue/insert は保持する）。 */
  setOverrideActive(active) { this._overrideActive = active; this._hasPendingZone = false; }

  setInsertActive(active) { this._insertActive = active; }

  notifyExternalSwitch(index, now) {
    this._current = index;
    this._lastSwitchTime = now;
    if (this._hasPendingZone && this._pendingZone === index) this._hasPendingZone = false;
  }

  /** 手動切替（クールダウンのみ尊重）。→ { ok, target }。 */
  requestManual(target, now) {
    if (target === this._current) return { ok: false, target: this._current };
    if (now - this._lastSwitchTime < this._cooldownSec) return { ok: false, target: this._current };
    this._current = target;
    this._lastSwitchTime = now;
    this._lastManualTime = now;
    this._hasPendingZone = false;
    return { ok: true, target };
  }

  /** 「既定として映すべきカメラ」（dwell 済みの確定ゾーン）。即 commit せず保持する。 */
  setAmbient(target) {
    if (target < 0) return;
    if (target === this._current) { this._hasPendingZone = false; return; }
    this._hasPendingZone = true;
    this._pendingZone = target;
  }

  /** 全ゲート（凍結 / クールダウン / manualHold）を通過したら commit。→ { committed, target }。 */
  tick(now) {
    if (!this._hasPendingZone) return { committed: false, target: this._current };
    if (this._pendingZone === this._current) {
      this._hasPendingZone = false;
      return { committed: false, target: this._current };
    }
    if (this._cueActive || this._insertActive || this._overrideActive) {
      return { committed: false, target: this._current };
    }
    if (f32(now - this._lastSwitchTime) < this._cooldownSec) return { committed: false, target: this._current };
    if (f32(now - this._lastManualTime) < this._manualHoldSec) return { committed: false, target: this._current };
    this._current = this._pendingZone;
    this._lastSwitchTime = now;
    this._hasPendingZone = false;
    return { committed: true, target: this._current };
  }
}

// ---- 周回（LapCounterLogic）---------------------------------------------------

/** 進行ポインタ方式。順方向一致でのみ前進し、order[0] へ戻ると lap++（1 始まり）。 */
export class LapCounter {
  constructor() { this._order = []; this._pos = 0; this._lap = 1; }

  get currentLap() { return this._lap; }
  get position() { return this._pos; }
  get order() { return this._order; }

  setOrder(order) { this._order = Array.isArray(order) ? order.slice() : []; this._pos = 0; this._lap = 1; }

  reset() { this._pos = 0; this._lap = 1; }

  /** 戻り値は「この供給で lap が前進したか」。逆走 / 同一 / スキップは前進しない。 */
  feed(camera) {
    const n = this._order.length;
    if (n === 0) return false;
    const nextPos = (this._pos + 1) % n;
    if (this._order[nextPos] !== camera) return false;
    this._pos = nextPos;
    if (this._pos === 0) { this._lap++; return true; }
    return false;
  }
}

// ---- 演出（TakeRunnerLogic）---------------------------------------------------

/** TakeRunnerLogic.Action。 */
export const TAKE_ACTION = { NONE: 'none', BEGIN_STEP: 'beginStep', END_TAKE: 'endTake' };

// 武装中の enter 演出の状態（TakeRunnerLogic.Armed）。
const ARMED_WAITING = 0;
const ARMED_DEFERRED_TO_EXIT = 1;

const noDecision = () => ({
  action: TAKE_ACTION.NONE, takeIndex: -1, stepIndex: -1,
  takeStarted: false, returnCamera: -1, forced: false,
});

/**
 * 演出の純判定・計時。契約（.claude/plans/2026-07-25_shot-timeline-foundation.md §6.3）:
 *   1. 同時に走る演出は 1 本。走行中に別演出の開始条件が来ても**待たせない**
 *   2. 同一区間の順序: at=enter は offsetSec 昇順 → 同値なら配列順 / at=exit は配列順
 *   3. ifMissed=fireOnExit: offsetSec 到達前に離脱したら、その離脱の瞬間に発火
 *   4. 区間を離れた時点で未発火の演出は必ず決着する（発火 or 破棄）
 *   5. once はラン内 1 回 / 6. 抑止中は発火しない / 7. maxDurationSec で強制終了
 *   8. 終了時の復帰先は「いま体験者が居るゾーン」（引数で受ける）
 */
export class TakeRunner {
  constructor() {
    this._defs = [];
    this._fired = [];
    this._armedIndex = [];
    this._armedDue = [];
    this._armedState = [];
    this._hasCurrent = false;
    this._curLap = 0;
    this._curCam = 0;
    this._suppressed = false;
    this._running = false;
    this._activeTake = -1;
    this._activeStep = -1;
    this._stepEnd = 0;
    this._deadline = 0;
    this._baseZoneCam = 0;
  }

  get isActive() { return this._running; }
  get activeTakeIndex() { return this._activeTake; }
  get activeStepIndex() { return this._activeStep; }
  get baseZoneCamera() { return this._baseZoneCam; }
  get armedCount() { return this._armedIndex.length; }

  setDefs(defs) {
    this._defs = defs || [];
    this._fired = new Array(this._defs.length).fill(false);
    this._clearArmed();
    this._running = false;
    this._activeTake = -1;
    this._activeStep = -1;
    this._hasCurrent = false;
  }

  resetRun() {
    this._fired.fill(false);
    this._clearArmed();
    this._running = false;
    this._activeTake = -1;
    this._activeStep = -1;
    this._hasCurrent = false;
  }

  setSuppressed(s) { this._suppressed = s; }

  /**
   * ゾーン確定（ショーの時計）を受ける。離脱区間の決着 → 進入区間の武装。
   * 返すのは最大 1 つの即時 Decision（離脱時発火）。
   */
  onZoneCommitted(newLap, newCam, hadPrev, prevLap, prevCam, now) {
    let result = noDecision();
    const segChanged = !hadPrev || prevLap !== newLap || prevCam !== newCam;
    if (!segChanged) return result;

    // policy=yield: 体験者が区間を移ったら演出を打ち切って画面を返す。
    // このとき離脱区間の exit 演出は発火しない（同時 1 本の原則を保つ）。
    if (this._running && this._defs[this._activeTake].yieldOnZoneChange) {
      const yielded = this._endTakeDecision(newCam, false);
      this._clearArmed();
      this._hasCurrent = true;
      this._curLap = newLap;
      this._curCam = newCam;
      this._armEnterTakes(newLap, newCam, now);
      return yielded;
    }

    if (hadPrev) {
      // 離脱の瞬間: 配列順で最初に該当する 1 本だけ発火し、残りは破棄する。
      if (!this._running && !this._suppressed) {
        const pick = this._findExitCandidate(prevLap, prevCam);
        if (pick >= 0) {
          this._startTake(pick, now, newCam);
          result = this._beginStepDecision(true);
        }
      }
      this._clearArmed();
    }

    this._hasCurrent = true;
    this._curLap = newLap;
    this._curCam = newCam;
    this._armEnterTakes(newLap, newCam, now);
    return result;
  }

  /** 毎フレーム評価。走行中は watchdog / カット進行 / 終了、非走行中は発火判定。 */
  tick(now, latestZoneCam) {
    if (this._running) {
      if (now >= this._deadline) return this._endTakeDecision(latestZoneCam, true);

      this._resolveOverdueWhileBlocked(now);

      if (now >= this._stepEnd) {
        const next = this._activeStep + 1;
        const durs = this._defs[this._activeTake].stepDurSec;
        if (next < durs.length) {
          this._activeStep = next;
          this._stepEnd = stepEndTime(now, durs[next]);
          return this._beginStepDecision(false);
        }
        return this._endTakeDecision(latestZoneCam, false);
      }
      return noDecision();
    }

    if (this._suppressed) {
      // 抑止中に発火時刻を過ぎたものも「待たせない」（離脱時発火 or 破棄へ落とす）。
      this._resolveOverdueWhileBlocked(now);
      return noDecision();
    }

    const pick = this._findDue(now);
    if (pick < 0) {
      this._resolveOverdueWhileBlocked(now);
      return noDecision();
    }

    const takeIndex = this._removeArmedAt(pick);
    this._startTake(takeIndex, now, this._hasCurrent ? this._curCam : 0);
    // 同時に発火時刻を迎えていた他の演出は、この 1 本に譲って決着する。
    this._resolveOverdueWhileBlocked(now);
    return this._beginStepDecision(true);
  }

  /** untilClipEnd のカットで素材の再生が終わったことを通知する。 */
  notifyCurrentStepFinished(now) { this.setCurrentStepEnd(now); }

  /** 現カットの終了時刻を外部から与える（watchdog は別途効き続ける）。 */
  setCurrentStepEnd(endTime) { if (this._running) this._stepEnd = endTime; }

  /** 走行中の演出を外部都合で畳む。 */
  abortActive() { this._running = false; this._activeTake = -1; this._activeStep = -1; }

  // ---- 内部 ----

  _startTake(index, now, baseZoneCam) {
    this._running = true;
    this._activeTake = index;
    this._activeStep = 0;
    this._fired[index] = true;
    this._baseZoneCam = baseZoneCam;
    const durs = this._defs[index].stepDurSec;
    this._stepEnd = stepEndTime(now, durs.length > 0 ? durs[0] : 0);
    this._deadline = f32(now + resolveMaxDuration(this._defs[index].maxDurationSec));
  }

  _beginStepDecision(takeStarted) {
    return {
      action: TAKE_ACTION.BEGIN_STEP,
      takeIndex: this._activeTake,
      stepIndex: this._activeStep,
      takeStarted,
      returnCamera: -1,
      forced: false,
    };
  }

  _endTakeDecision(latestZoneCam, forced) {
    const take = this._activeTake;
    this._running = false;
    this._activeTake = -1;
    this._activeStep = -1;
    return {
      action: TAKE_ACTION.END_TAKE,
      takeIndex: take,
      stepIndex: -1,
      takeStarted: false,
      returnCamera: latestZoneCam,
      forced,
    };
  }

  // 進入区間の enter 演出を武装する（offsetSec 昇順 → 同値は配列順）。
  _armEnterTakes(lap, camera, now) {
    const order = [];
    for (let i = 0; i < this._defs.length; i++) {
      const d = this._defs[i];
      if (d.onExit || d.lap !== lap || d.camera !== camera) continue;
      if (d.once && this._fired[i]) continue;
      if (d.stepDurSec.length === 0) continue;   // カット空の演出は無視
      order.push(i);
    }
    order.sort((a, b) => {
      const c = this._defs[a].offsetSec - this._defs[b].offsetSec;
      return c !== 0 ? Math.sign(c) : a - b;
    });
    for (const i of order) {
      this._armedIndex.push(i);
      this._armedDue.push(f32(now + (this._defs[i].offsetSec < 0 ? 0 : this._defs[i].offsetSec)));
      this._armedState.push(ARMED_WAITING);
    }
  }

  // 発火時刻に達した Waiting のうち先頭（無ければ -1）。
  _findDue(now) {
    for (let k = 0; k < this._armedIndex.length; k++) {
      if (this._armedState[k] === ARMED_WAITING && now >= this._armedDue[k]) return k;
    }
    return -1;
  }

  // 走行中 / 抑止中に発火時刻を過ぎたものを決着させる（待たせない）。
  //   skip → 破棄 / fireOnExit → 離脱時のみ発火しうる状態へ
  _resolveOverdueWhileBlocked(now) {
    for (let k = this._armedIndex.length - 1; k >= 0; k--) {
      if (this._armedState[k] !== ARMED_WAITING || now < this._armedDue[k]) continue;
      if (this._defs[this._armedIndex[k]].skipWhenMissed) this._removeArmedAt(k);
      else this._armedState[k] = ARMED_DEFERRED_TO_EXIT;
    }
  }

  // 離脱の瞬間に発火する候補を配列順で 1 つ選ぶ。
  _findExitCandidate(lap, camera) {
    for (let i = 0; i < this._defs.length; i++) {
      const d = this._defs[i];
      if (d.lap !== lap || d.camera !== camera) continue;
      if (d.stepDurSec.length === 0) continue;
      if (d.once && this._fired[i]) continue;
      if (d.onExit || (!d.skipWhenMissed && this._isArmed(i))) return i; // 配列順で先頭が勝つ
    }
    return -1;
  }

  _isArmed(takeIndex) { return this._armedIndex.includes(takeIndex); }

  _removeArmedAt(k) {
    const takeIndex = this._armedIndex[k];
    this._armedIndex.splice(k, 1);
    this._armedDue.splice(k, 1);
    this._armedState.splice(k, 1);
    return takeIndex;
  }

  _clearArmed() { this._armedIndex.length = 0; this._armedDue.length = 0; this._armedState.length = 0; }
}

// 尺が負（untilClipEnd）なら外部通知待ち = 無限大。watchdog が上限を保証する。
const stepEndTime = (now, durSec) => (durSec < 0 ? Infinity : f32(now + durSec));

// ---- 束ね（ShowScenarioRunner）------------------------------------------------

/** cfg のデフォルト補完（ShowScenarioRunner.Config 相当）。 */
function normalizeConfig(cfg) {
  const c = cfg || {};
  return {
    zones: c.zones || [],
    hysteresisShrink: num(c.hysteresisShrink, 0),
    keepLastWhenOutside: c.keepLastWhenOutside !== false,
    headY: num(c.headY, 1.6),
    dwellSec: num(c.dwellSec, DEFAULT_DWELL_SEC),
    cooldownSec: num(c.cooldownSec, DEFAULT_COOLDOWN_SEC),
    courseOrder: c.courseOrder || [],
    takes: c.takes || [],
    takeIds: c.takeIds || [],
    stepCameras: c.stepCameras || null,
    startCamera: num(c.startCamera, 0),
    tickMs: c.tickMs > 0 ? c.tickMs : DEFAULT_TICK_MS,
  };
}

const num = (v, def) => (Number.isFinite(v) ? v : def);

// トレース表示用の演出 id（ShowScenarioRunner.TakeId）。
function takeIdOf(cfg, index) {
  return index >= 0 && index < cfg.takeIds.length && cfg.takeIds[index]
    ? cfg.takeIds[index]
    : `#${index}`;
}

// カットが切り替えるカメラ（ShowScenarioRunner.StepCamera）。未設定は -1。
function stepCameraOf(cfg, takeIndex, stepIndex) {
  if (!cfg.stepCameras) return -1;
  if (takeIndex < 0 || takeIndex >= cfg.stepCameras.length) return -1;
  const cams = cfg.stepCameras[takeIndex];
  if (!cams || stepIndex < 0 || stepIndex >= cams.length) return -1;
  return cams[stepIndex];
}

const ev = (kind, t, a = -1, b = -1, id = '', flag = false) => ({ kind, t, a, b, id, flag });

// Decision → トレースイベント（ShowScenarioRunner.Emit）。
function emit(trace, cfg, d, tMs) {
  if (d.action === TAKE_ACTION.BEGIN_STEP) {
    const id = takeIdOf(cfg, d.takeIndex);
    if (d.takeStarted) trace.push(ev('take', tMs, -1, -1, id));
    trace.push(ev('step', tMs, d.stepIndex, stepCameraOf(cfg, d.takeIndex, d.stepIndex), id));
  } else if (d.action === TAKE_ACTION.END_TAKE) {
    trace.push(ev('end', tMs, d.returnCamera, -1, takeIdOf(cfg, d.takeIndex), d.forced));
  }
}

/**
 * ショーを 1 tick ずつ進める実行器。**golden 一致のテストと卓 UI が同じコードを通る**ように、
 * ShowScenarioRunner.Run のループ本体をこの step() に切り出してある
 * （テストだけ通って UI が別物、という drift を作らないため）。
 */
export function createShowRunner(cfg) {
  const c = normalizeConfig(cfg);

  const progress = new ZoneProgression();
  progress.configure(c.dwellSec);
  progress.reset(c.startCamera);

  const screen = new SwitchDirector();
  screen.configure(c.cooldownSec, 0);
  screen.reset(c.startCamera);

  const lap = new LapCounter();
  lap.setOrder(c.courseOrder);

  const takes = new TakeRunner();
  takes.setDefs(c.takes);

  let zoneIndex = -1;                 // pickZone の直近選択
  let curCam = c.startCamera;         // 時計が確定しているゾーンのカメラ
  let hasSeg = false;
  let segLap = 1;
  let segCam = c.startCamera;

  /** 1 tick 進める。返すのはこの tick で発生したイベント配列（発生順）。 */
  function step(tMs, x, z) {
    const now = f32(tMs / 1000);   // C# は `float now = tMs / 1000f`
    const out = [];

    // ① ゾーン判定 → 時計へ要求
    zoneIndex = pickZone(c.zones, x, c.headY, z, zoneIndex, c.hysteresisShrink, c.keepLastWhenOutside);
    if (zoneIndex >= 0) progress.request(c.zones[zoneIndex].camera, now);

    // ② 時計 Tick（ゾーン確定）
    const zoneStep = progress.tick(now);
    if (zoneStep.committed) {
      curCam = zoneStep.camera;
      screen.setAmbient(zoneStep.camera);   // ③ 既定映し先を画面層へ
    }

    // ④ 画面（凍結・クールダウンを通過したら commit）
    const shown = screen.tick(now);
    if (shown.committed) out.push(ev('screen', tMs, shown.target));

    // ⑤ ゾーン確定の通知（周回 → 区間 → 演出の武装）
    if (zoneStep.committed) {
      const zoneCam = zoneStep.camera;
      out.push(ev('zone', tMs, zoneCam));

      if (lap.feed(zoneCam)) out.push(ev('lap', tMs, lap.currentLap));

      const newLap = lap.currentLap;
      out.push(ev('seg', tMs, newLap, zoneCam));

      emit(out, c, takes.onZoneCommitted(newLap, zoneCam, hasSeg, segLap, segCam, now), tMs);

      hasSeg = true;
      segLap = newLap;
      segCam = zoneCam;
    }

    // ⑥ 演出 Tick（カット進行 / 終了 / 発火）
    emit(out, c, takes.tick(now, curCam), tMs);

    // 演出が画面を占有しているかを画面層へ反映する（本番の insert 凍結と同じ役割）。
    screen.setInsertActive(takes.isActive);

    return out;
  }

  return {
    config: c,
    step,
    // 観測（UI 用。判定には使わない）
    get shownCamera() { return screen.current; },
    get zoneCamera() { return curCam; },
    get zoneIndex() { return zoneIndex; },
    get lap() { return lap.currentLap; },
    get position() { return lap.position; },
    get segment() { return hasSeg ? { lap: segLap, camera: segCam } : null; },
    get takeActive() { return takes.isActive; },
    get activeTakeIndex() { return takes.activeTakeIndex; },
    get activeStepIndex() { return takes.activeStepIndex; },
    get armedCount() { return takes.armedCount; },
    get pendingZone() { return progress.hasPending ? progress.pending : -1; },
  };
}

/** 指定時刻の位置を線形補間で求める（ShowScenarioRunner.SampleAt）。 */
export function sampleAt(samples, tMs) {
  const last = samples.length - 1;
  if (tMs <= samples[0].tMs) return { x: samples[0].x, z: samples[0].z };
  if (tMs >= samples[last].tMs) return { x: samples[last].x, z: samples[last].z };
  for (let i = 1; i <= last; i++) {
    if (samples[i].tMs < tMs) continue;
    const span = samples[i].tMs - samples[i - 1].tMs;
    const u = span <= 0 ? 0 : (tMs - samples[i - 1].tMs) / span;
    return {
      x: samples[i - 1].x + (samples[i].x - samples[i - 1].x) * u,
      z: samples[i - 1].z + (samples[i].z - samples[i - 1].z) * u,
    };
  }
  return { x: samples[last].x, z: samples[last].z };
}

/** シナリオを回してトレースを返す（ShowScenarioRunner.Run）。 */
export function runScenario(cfg, samples) {
  const trace = [];
  if (!samples || samples.length === 0) return trace;
  const runner = createShowRunner(cfg);
  const tick = runner.config.tickMs;
  const endMs = samples[samples.length - 1].tMs;
  for (let tMs = samples[0].tMs; tMs <= endMs; tMs += tick) {
    const p = sampleAt(samples, tMs);
    for (const e of runner.step(tMs, p.x, p.z)) trace.push(e);
  }
  return trace;
}

/**
 * scenario JSON（Assets/Tests/Fixtures/scenario_walk.json と同形式）を
 * runScenario の引数へ変換する。→ { cfg, samples }
 */
export function parseScenario(json) {
  const j = json || {};
  const zones = (j.zones || []).map((zn) => boxWithYaw(
    num(zn.cx, 0), num(zn.cy, 0), num(zn.cz, 0),
    num(zn.hx, 0), num(zn.hy, 0), num(zn.hz, 0),
    num(zn.yawDeg, 0), Number.isInteger(zn.camera) ? zn.camera : 0, num(zn.priority, 0)));

  const takesSrc = j.takes || [];
  const cfg = {
    zones,
    hysteresisShrink: num(j.hysteresisShrink, 0),
    keepLastWhenOutside: j.keepLastWhenOutside !== false,
    headY: num(j.headY, 1.6),
    dwellSec: num(j.dwellSec, DEFAULT_DWELL_SEC),
    cooldownSec: num(j.cooldownSec, DEFAULT_COOLDOWN_SEC),
    courseOrder: j.courseOrder || [],
    takes: takesSrc.map((t) => ({
      lap: num(t.lap, 1),
      camera: num(t.camera, 0),
      onExit: !!t.onExit,
      offsetSec: num(t.offsetSec, 0),
      skipWhenMissed: !!t.skipWhenMissed,
      once: t.once !== false,
      maxDurationSec: num(t.maxDurationSec, 0),
      yieldOnZoneChange: !!t.yieldOnZoneChange,
      stepDurSec: (t.stepDurSec || []).map((v) => num(v, 0)),
    })),
    takeIds: takesSrc.map((t) => t.id || ''),
    stepCameras: takesSrc.map((t) => (t.stepCameras || []).map((v) => num(v, -1))),
    startCamera: num(j.startCamera, 0),
    tickMs: num(j.tickMs, DEFAULT_TICK_MS),
  };
  const samples = (j.samples || []).map((s) => ({ tMs: num(s.tMs, 0), x: num(s.x, 0), z: num(s.z, 0) }));
  return { cfg, samples };
}
