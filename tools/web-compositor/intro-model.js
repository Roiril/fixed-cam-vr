// 導入演出（show.json `run.intro`）の判定。DOM 非依存の純関数だけを置く。
//
// 導入は「現実を固定カメラの映像へ格下げし、格下げが完了した瞬間その映像の中に自分が居る」
// 約 33 秒の遷移（設計 .claude/plans/2026-07-30_intro-passthrough-to-screen.md）。
// **導入は 1 種類でよく、演出（takes）として著作可能にしない** — 自由度を持たせると
// 「導入が壊れている show.json」を作れてしまう（設計 §11）。だから編集面は段ごとの秒と
// on/off だけで、ここに置く判定もそれだけを見る。
//
// 尺の合計・成立条件は 卓の ⚙ 欄 と 本番前チェック の 2 箇所で同じ答えを出さなければならない
// ので、列挙と判定はこの 1 ファイルが単一の正（record-model.js と同じ流儀）。

import { wallLooksDefault } from './calib-session.js';

/** show.json `run.intro` が欠けている時の既定（capture-server.py の _default_show と同じ値）。 */
export const INTRO_DEFAULT = {
  enabled: true,
  maxSec: 40,
  realSec: 4,
  degradeSec: 8,
  structureSec: 6,
  frameSec: 5,
  swapSec: 8,
  edgeColor: '#ffffff',
  showCameraMarks: true,
  showRoomWire: true,
  glitchOnSwap: 0.8,
  raiseHandPrompt: true,
};

/** 段の秒（この 5 つの和が「演出」の尺）。UI の並び順もこの順（体験者が見る順）。 */
export const INTRO_STAGE_KEYS = ['realSec', 'degradeSec', 'structureSec', 'frameSec', 'swapSec'];

/** 段ごとの秒の入力範囲。1 段 20 秒を超える導入は薄さの原則（設計 §3）から外れる。 */
export const STAGE_SEC_MIN = 0;
export const STAGE_SEC_MAX = 20;
/** 打ち切りの上限秒。10 未満だと段を出す前に打ち切られ、60 超は 3 分の予算を壊す。 */
export const MAX_SEC_MIN = 10;
export const MAX_SEC_MAX = 60;

const clamp = (v, lo, hi, fallback) => {
  const n = parseFloat(v);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(hi, Math.max(lo, n));
};

/** show.json `run` → 正規化した導入設定（欠落は既定で埋め、範囲外は丸める）。 */
export function introConfig(run) {
  const src = (run && run.intro) || {};
  const out = {
    enabled: src.enabled !== false,
    maxSec: clamp(src.maxSec, MAX_SEC_MIN, MAX_SEC_MAX, INTRO_DEFAULT.maxSec),
    edgeColor: typeof src.edgeColor === 'string' && src.edgeColor ? src.edgeColor : INTRO_DEFAULT.edgeColor,
    showCameraMarks: src.showCameraMarks !== false,
    showRoomWire: src.showRoomWire !== false,
    glitchOnSwap: clamp(src.glitchOnSwap, 0, 1, INTRO_DEFAULT.glitchOnSwap),
    raiseHandPrompt: src.raiseHandPrompt !== false,
  };
  for (const k of INTRO_STAGE_KEYS) {
    out[k] = clamp(src[k], STAGE_SEC_MIN, STAGE_SEC_MAX, INTRO_DEFAULT[k]);
  }
  return out;
}

/**
 * 演出の尺（段 1〜5 の単純和）。
 *
 * 設計 §4 では段 3（構造）が段 2（格下げ）と時間的に重なるので、実際に流れる秒はこの和より
 * 短くなる。**それでも和で出す** — 上限（maxSec）は打ち切りの保険で、重なりを見込んだ短い方で
 * 判定すると「和が上限を超えているのに卓が黙る」ことになり、打ち切られた段を現場で初めて知る。
 */
export function introStageSec(intro) {
  const cfg = intro && intro.realSec !== undefined ? intro : introConfig({ intro });
  let sum = 0;
  for (const k of INTRO_STAGE_KEYS) sum += cfg[k];
  return Math.round(sum * 10) / 10;
}

/** 「演出 31s / 慣らし 20s」— ⚙ 欄と本番前チェックで同じ文を出す。 */
export function introDurationLabel(intro, introMinSec) {
  const warm = Math.max(0, parseFloat(introMinSec) || 0);
  return `演出 ${introStageSec(intro)}s / 慣らし ${warm}s`;
}

/** 較正済み（＝ CG 人形と段 3 のカメラの印を出せる）カメラの本数。判定は app.js の 🎯 較正 行と同じ。 */
export function calibratedCameraCount(cameras) {
  return (cameras || []).filter((c) => c && c.calib && c.calib.fxPx > 1).length;
}

/**
 * 本番前チェックの `🎬 導入` 行。導入が無効なら **null**（行を出さない = 黙る）。
 *
 * 見るのは設計 §8 の成立条件のうち卓で判定できる 2 つと、尺の打ち切り:
 *   3. 部屋の実寸が入っている — 段 3 は現実に線を直接重ねるので、既定の壁のままでは成立しない（❌）
 *   1. 開始カメラが較正済み  — 段 3 のカメラの印が出せない（⚠。印を消せば導入自体は成立する）
 *   §3 上限                  — 和が maxSec を超えると段を飛ばして枠を出す（⚠）
 */
export function introPreflightRow({ run, layout, cameras } = {}) {
  const intro = introConfig(run);
  if (!intro.enabled) return null;

  const label = '🎬 導入';
  const dur = introDurationLabel(intro, run && run.introMinSec);

  if (wallLooksDefault(layout)) {
    return {
      s: 'ng',
      label,
      detail: '部屋の寸法が卓の既定値のままです — 段 3 の壁・床の線が実物に重なりません'
        + '（［🎯 姿勢を合わせる］の床の寸法入力で測った値を入れる）',
    };
  }

  const warn = [];
  if (calibratedCameraCount(cameras) === 0) {
    warn.push('較正済みのカメラが 1 台もありません（段 3 のカメラの印が出せません）');
  }
  const sec = introStageSec(intro);
  if (sec > intro.maxSec) {
    warn.push(`演出 ${sec}s が上限 ${intro.maxSec}s を超えています（超えた段は飛ばして枠を出します）`);
  }
  if (warn.length) return { s: 'warn', label, detail: `${warn.join(' ・ ')} — ${dur}` };
  return { s: 'ok', label, detail: dur };
}
