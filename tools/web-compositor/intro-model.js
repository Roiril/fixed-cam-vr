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

import { zonesFromLayout, cameraAtPoint } from './zone-layout.js';
import { roomFromLayout, roomHasData, isWallUsable, isBoxUsable, FLOOR_MIN_M } from './room-model.js';

/**
 * show.json `run.intro` が欠けている時の既定（capture-server.py の _default_show と同じ値）。
 *
 * 尺は 2026-07-30 に 33s → 14.5s へ詰めた。**同じ絵の前で待たされる時間は演出ではなく待ち時間**で、
 * 段 1（現実）を 4 秒見せても比較対象としては 1.5 秒で足りる。3 分の予算のうち導入が食う分を
 * 削ると、3 周目の反転を見せ切る余地が残る（設計 §3）。
 */
export const INTRO_DEFAULT = {
  enabled: true,
  maxSec: 20,
  realSec: 1.5,
  degradeSec: 3.5,
  structureSec: 2.5,
  frameSec: 2.5,
  swapSec: 4.5,
  edgeColor: '#ffffff',
  showCameraMarks: true,
  showRoomWire: true,
  glitchOnSwap: 0.8,
  raiseHandPrompt: true,
  // 空 = 開始位置の円（layout.startSpot）で始める。Unity 側 ShowIntroDef.startLineId と対。
  startLineId: '',
};

/**
 * 開始位置（`layout.startSpot`）の既定。**show.json には既定を書かない**
 * — 未設定＝スタッフが手で始める運用、が既定の姿。勝手に (0,0) へ置くと
 * 「立っても始まらない」を現場で初めて知ることになる。
 */
export const START_SPOT_DEFAULT = { radiusM: 0.35, label: 'スタート' };
/** 半径の入力範囲。0.15 未満は踏み外し、1.0 超は部屋（1.8m 四方）の半分を超えて意味を失う。 */
export const START_RADIUS_MIN = 0.15;
export const START_RADIUS_MAX = 1.0;

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
    // 導入を始める通過ライン（layout.lines[].id）。空 = 開始位置の円（layout.startSpot）を使う。
    // 円は「その場所に立つ」＝状態、線は「横切る」＝事象。歩いて入ってくる動きのまま始めたいので
    // 線を選べるようにしてある。実機は Unity の IntroDirector が同じ id を引く。
    startLineId: typeof src.startLineId === 'string' ? src.startLineId : '',
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
/**
 * 段 3（構造）が段 2（格下げ）の後半から始まる比。**Unity の `IntroLogic.StructureOverlapAt` と
 * 同じ値でなければならない** — 片方だけ直すと、卓の表示と実機の尺が沈黙して食い違う。
 */
export const STRUCTURE_OVERLAP_AT = 0.6;
/** 段 3 が段 2 に飲み込まれても、これだけは単独で流れる（`IntroLogic` と同じ下限）。 */
export const STRUCTURE_MIN_OWN_SEC = 0.5;

/**
 * 演出が実際に流れる秒数。
 *
 * ⚠ **単純和ではない。** 段 3 は段 2 の後半から始まるので、重なった分は二度流れない。
 * 実機（`IntroLogic`）が消費するのと同じ式にしてある — ここを単純和にすると
 * 卓が「14.5 秒」と言うのに実機は 13.1 秒で終わり、作者は尺を信じられなくなる。
 */
export function introStageSec(intro) {
  const cfg = intro && intro.realSec !== undefined ? intro : introConfig({ intro });
  const own = Math.max(STRUCTURE_MIN_OWN_SEC,
    cfg.structureSec - cfg.degradeSec * (1 - STRUCTURE_OVERLAP_AT));
  const sum = cfg.realSec + cfg.degradeSec + own + cfg.frameSec + cfg.swapSec;
  return Math.round(sum * 10) / 10;
}

/** 「演出 14.5s / 慣らし 20s」— ⚙ 欄と本番前チェックで同じ文を出す。 */
export function introDurationLabel(intro, introMinSec) {
  const warm = Math.max(0, parseFloat(introMinSec) || 0);
  return `演出 ${introStageSec(intro)}s / 慣らし ${warm}s`;
}

/** 較正済み（＝ CG 人形と段 3 のカメラの印を出せる）カメラの本数。判定は app.js の 🎯 較正 行と同じ。 */
export function calibratedCameraCount(cameras) {
  return (cameras || []).filter((c) => c && c.calib && c.calib.fxPx > 1).length;
}

// ---- 開始位置（layout.startSpot）------------------------------------------------

/**
 * `layout.startSpot` を正規化する（座標が無ければ **null** = 未設定）。
 * フロアマップの編集も本番前チェックもこの 1 本を通す（2 箇所で別々に丸めない）。
 */
export function normalizeStartSpot(src) {
  if (!src) return null;
  const x = parseFloat(src.x), z = parseFloat(src.z);
  if (!Number.isFinite(x) || !Number.isFinite(z)) return null;
  const label = typeof src.label === 'string' ? src.label.trim() : '';
  return {
    x: +x.toFixed(3),
    z: +z.toFixed(3),
    radiusM: clamp(src.radiusM, START_RADIUS_MIN, START_RADIUS_MAX, START_SPOT_DEFAULT.radiusM),
    label: label || START_SPOT_DEFAULT.label,
  };
}

/** show.json layout → 正規化した開始位置（未設定なら null）。 */
export function startSpotOf(layout) {
  return normalizeStartSpot(layout && layout.startSpot);
}

/**
 * 開始位置が**スタート区間の外**にあるか（あれば `{ startCamera, spotCamera }`・無ければ null）。
 *
 * 外にあると、導入が終わった直後に体験者は本編のスタート区間へ**入り直す**ことになる。
 * 判定は `zone-layout.js` の展開（実機のゾーン判定と同じ）を通す — 生タイルの色で見ると
 * 重なり帯でどちらが勝つかが食い違い、**卓だけが通る / 卓だけが警告する**が起きる。
 *
 * ゾーンが解決できない layout（grid 不在・1 枚も塗っていない・course.order 未設定）では
 * **黙る**。判定できないことを警告にすると、直しようのない ⚠ が出続けて読まれなくなる。
 */
export function startSpotZoneIssue(layout) {
  const spot = startSpotOf(layout);
  if (!spot) return null;
  const order = layout && layout.course && layout.course.order;
  if (!Array.isArray(order) || !order.length) return null;
  const startCamera = order[0];
  if (!Number.isInteger(startCamera) || startCamera < 0) return null;
  const z = zonesFromLayout(layout);
  if (z.source !== 'grid' || !z.boxes.length) return null;
  const spotCamera = cameraAtPoint(z.boxes, spot.x, spot.z);
  if (spotCamera === startCamera) return null;
  return { startCamera, spotCamera };
}

// ---- 段 3（構造）が実際に描く幾何 ------------------------------------------------

/**
 * 卓の既定の L 字壁（1m × 1m）の 3 点。**「測った値かどうかは判定できない」ことを言うためだけ**に持つ。
 * 現場が本当に 1m × 1m なら形が一致するのは正しいので、これは ❌ ではなく ⚠ の材料。
 */
const DEFAULT_L_POINTS = [[-0.5, 0.5], [0.5, 0.5], [-0.5, -0.5]];
const ptKey = (x, z) => `${x.toFixed(3)},${z.toFixed(3)}`;

/** 壁の端点が卓の既定 L と同じ 3 点か（＝寸法を触っていない疑い）。 */
function wallsLookLikeDefaultL(walls) {
  if (!walls.length) return false;
  const keys = new Set();
  for (const w of walls) { keys.add(ptKey(w.x1, w.z1)); keys.add(ptKey(w.x2, w.z2)); }
  if (keys.size !== DEFAULT_L_POINTS.length) return false;
  return DEFAULT_L_POINTS.every(([x, z]) => keys.has(ptKey(x, z)));
}

/**
 * 段 3 が現実の上に重ねる線の内訳。
 *
 * ⚠ **見る鍵は `layout.room` と `layout.floor`。`layout.wall` ではない。**
 * 実機の `IntroStructureWireLogic` は壁と箱を `AppendRoom`（= `layout.room` だけ）から起こし、
 * 床の外周を `TryFloorExtents`（`layout.floor` → `room.floorW/D` の順）から起こす。
 * `layout.wall` は HMD 位置合わせリチュアルのワイヤー専用の旧データで、**段 3 は 1 本も読まない**。
 * ここを取り違えると、卓が指す直し方（床の寸法入力）を実行しても ❌ が消えない。
 */
export function introWireGeometry(layout) {
  const { room, hasRoom } = roomFromLayout(layout);
  const walls = hasRoom ? (room.walls || []).filter(isWallUsable) : [];
  const props = hasRoom ? (room.props || []).filter(isBoxUsable) : [];
  const f = (layout && layout.floor) || null;
  const hasFloor = !!(f && f.w > FLOOR_MIN_M && f.d > FLOOR_MIN_M) || (hasRoom && roomHasData(room));
  return {
    hasFloor,
    wallCount: walls.length,
    propCount: props.length,
    looksDefaultL: wallsLookLikeDefaultL(walls),
  };
}

/** カメラ index → 表示名（cameras が無ければ index で呼ぶ）。 */
function cameraName(cameras, i) {
  if (i < 0) return '未割当';
  const c = (cameras || [])[i];
  return c && c.id ? `カメラ ${c.id}` : `#${i}`;
}

/**
 * 本番前チェックの `🎬 導入` 行。導入が無効なら **null**（行を出さない = 黙る）。
 *
 * 見るのは設計 §8 の成立条件のうち卓で判定できる 2 つと、尺の打ち切り:
 *   3. 部屋の幾何が入っている — 段 3 は現実に線を直接重ねるので、線が 1 本も無ければ成立しない（❌）
 *   1. 開始カメラが較正済み  — 段 3 のカメラの印が出せない（⚠。印を消せば導入自体は成立する）
 *   §3 上限                  — 和が maxSec を超えると段を飛ばして枠を出す（⚠）
 *   開始位置（startSpot）    — 未設定なら手動開始・スタート区間の外なら入り直しになる（⚠）
 */
export function introPreflightRow({ run, layout, cameras } = {}) {
  const intro = introConfig(run);
  if (!intro.enabled) return null;

  const label = '🎬 導入';
  const dur = introDurationLabel(intro, run && run.introMinSec);
  const geo = introWireGeometry(layout);

  if (intro.showRoomWire && !geo.hasFloor) {
    return {
      s: 'ng',
      label,
      detail: '床の寸法が入っていません — 段 3 の線が 1 本も出ません'
        + '（［🎯 姿勢を合わせる］の床の寸法入力で測った値を入れる）',
    };
  }
  if (intro.showRoomWire && geo.wallCount === 0 && geo.propCount === 0) {
    return {
      s: 'ng',
      label,
      detail: '部屋の壁が著作されていません — 段 3 は床の外周とカメラの印だけになり、'
        + '線が実物に重なるかを確かめられません（フロアマップの 🧱 部屋 で壁を引く）',
    };
  }

  const warn = [];
  if (geo.looksDefaultL) {
    warn.push('壁の形が卓の既定の L（1m × 1m）と同じです'
      + ' — 実際に測った値かどうかは判定できません（現場でメジャーを当てて確かめる）');
  }
  if (calibratedCameraCount(cameras) === 0) {
    warn.push('較正済みのカメラが 1 台もありません（段 3 のカメラの印が出せません）');
  }
  if (!startSpotOf(layout)) {
    warn.push('開始位置が未設定です — スタッフが手で始める運用になります'
      + '（フロアマップの 🎬 開始位置 で床に 1 点置く）');
  } else {
    const issue = startSpotZoneIssue(layout);
    if (issue) {
      warn.push(`開始位置がスタート区間（${cameraName(cameras, issue.startCamera)}）の外`
        + `（いまは ${cameraName(cameras, issue.spotCamera)} の領域）`
        + ' — 演出が終わった直後に本編のスタート区間へ入り直すことになります');
    }
  }
  // 慣らし歩行（introMinSec）は**導入相全体の下限**なので、演出がそれを食う。
  //   演出 13.1s に対し introMinSec が 10s だと、演出の途中で本編へ移って**演出が切り落とされる**。
  //   RestartIntroClock は演出が終わった時にしか打たれないので、実機は黙って途中で切り替わる。
  const warmSec = Math.max(0, parseFloat(run && run.introMinSec) || 0);
  const stageSec = introStageSec(intro);
  if (warmSec > 0 && warmSec < stageSec) {
    warn.push(`慣らし歩行 ${warmSec}s が演出 ${stageSec}s より短いので、演出が途中で切り落とされます`
      + '（⚙ 欄の「導入（慣らし歩行）」を演出より長くする）');
  }

  const sec = introStageSec(intro);
  if (sec > intro.maxSec) {
    warn.push(`演出 ${sec}s が上限 ${intro.maxSec}s を超えています（超えた段は飛ばして枠を出します）`);
  }
  if (warn.length) return { s: 'warn', label, detail: `${warn.join(' ・ ')} — ${dur}` };
  return { s: 'ok', label, detail: dur };
}
