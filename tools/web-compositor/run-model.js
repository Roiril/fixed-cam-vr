// 体験の骨格（show.json `run`）の判定。DOM 非依存の純関数だけを置く。
//
// **どの区間が体験中に踏まれるか**は 4 箇所（タイムラインのリボン / 本番前チェック / シミュレータ /
// 実機）で同じ答えを出さなければならない。列挙と判定はこの 1 ファイルが単一の正
// （record-model.js・intro-model.js と同じ流儀）。
//
// 移植元（**C# が正**）:
//   ShowRunReach.IsSegmentReachable … lap <= totalLaps || (lap == totalLaps+1 && camera == order[0])
//   ShowRunLogic.Tick               … 終了は「周を走り切った」次フレーム。grace のあいだは必ず待つ
//   ShowRunDefaults                 … 既定値
//
// ⚠ 同じ式が 3 箇所にある（ここ / C# / tools/analyze-xp-log.py の is_segment_reachable）。
//    期待値を 3 者のテストにハードコードして突き合わせてある。片方だけ直すと沈黙して食い違う。

/** show.json `run` が欠けている時の既定（C# の ShowRunDefaults / capture-server.py と同じ値）。 */
export const RUN_DEFAULT = {
  totalLaps: 3, introEnabled: true, introMinSec: 20, introAutoAdvance: true,
  targetSec: 180, hardLimitSec: 300, endFadeSec: 1.5,
  endGraceSec: 3, endHoldMaxSec: 60,
};

const int = (v, fallback = 0) => {
  const n = parseInt(v, 10);
  return Number.isFinite(n) ? n : fallback;
};

/** show.json → 正規化したラン設定（欠落は既定で埋める）。 */
export function runConfig(state) {
  return { ...RUN_DEFAULT, ...((state && state.run) || {}) };
}

/** 走り切る周数（1 以上。不正値は既定 3）。 */
export function totalLaps(state) {
  const n = int(runConfig(state).totalLaps, RUN_DEFAULT.totalLaps);
  return n > 0 ? n : RUN_DEFAULT.totalLaps;
}

/** 順路（course.order）。未著作なら空配列。 */
export function courseOrder(state) {
  const o = ((state && state.layout && state.layout.course) || {}).order;
  return Array.isArray(o) ? o.map((v) => int(v, 0)) : [];
}

/**
 * 区間 (lap, camera) が体験中に踏まれうるか。
 *
 * 周回は進行ポインタ方式で `order[0]` へ戻った時に上がるので、`lap = totalLaps + 1` の `order[0]`
 * （＝**帰りの A**）は構造的に必ず踏む。体験は「元の位置に戻って終わる」ので、この 1 区間だけは
 * 到達可能として扱う。`totalLaps` を 4 に上げると帰りの B・C まで生きてしまう。
 */
export function isSegmentReachable(lap, camera, laps, order) {
  const t = laps > 0 ? laps : RUN_DEFAULT.totalLaps;
  if (!(lap >= 1)) return false;
  if (lap <= t) return true;
  if (lap !== t + 1) return false;
  if (!order || order.length === 0) return true;   // 順路が未著作なら判定できない → 通す
  return camera === order[0];
}

/** その区間が「帰りの A」か（リボンの見出しを周ではなく「もどり」にする）。 */
export function isReturnSegment(lap, camera, laps, order) {
  const t = laps > 0 ? laps : RUN_DEFAULT.totalLaps;
  if (lap !== t + 1) return false;
  if (!order || order.length === 0) return true;
  return camera === order[0];
}

/**
 * リボンが並べる周の見出し。`totalLaps` 周ぶんの通常の周と、最後の「もどり」の 1 つ。
 * 返すのは `{ lap, label, isReturn }` の配列。
 */
export function lapHeadings(laps) {
  const t = laps > 0 ? laps : RUN_DEFAULT.totalLaps;
  const out = [];
  for (let i = 1; i <= t; i++) out.push({ lap: i, label: `${i}周目`, isReturn: false });
  out.push({ lap: t + 1, label: 'もどり', isReturn: true });
  return out;
}

/**
 * 帰りの区間に置いた演出が、暗転までに始まれるか。
 *
 * 帰りの A に入った瞬間に終了条件が立つので、そこから **`endGraceSec` のあいだしか演出は始まれない**
 * （走り出した演出は `endHoldMaxSec` まで見せ切る）。開始オフセットが grace を超える著作は
 * **実機で必ず出ない**ので、卓が著作の段階で言う。
 */
export function returnTakeTooLate(offsetSec, cfg) {
  const grace = Number((cfg && cfg.endGraceSec) || RUN_DEFAULT.endGraceSec);
  return Number(offsetSec || 0) > grace;
}
