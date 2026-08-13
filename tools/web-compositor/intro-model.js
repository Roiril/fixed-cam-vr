// 導入演出（show.json `run.intro`）の判定。DOM 非依存の純関数だけを置く。
//
// 導入は「開口が閉じて闇になり、その闇の中でスクリーンの管が点いて、映った先に自分が居る」
// 6.2 秒の遷移（2026-08-13 に作り直し。体験者は封印の箱の**中に入ってから**固定視点になる）。
// **導入は 1 種類でよく、演出（takes）として著作可能にしない** — 自由度を持たせると
// 「導入が壊れている show.json」を作れてしまう。だから編集面は段ごとの秒と
// on/off だけで、ここに置く判定もそれだけを見る。
//
// 尺の合計・成立条件は 卓の ⚙ 欄 と 本番前チェック の 2 箇所で同じ答えを出さなければならない
// ので、列挙と判定はこの 1 ファイルが単一の正（record-model.js と同じ流儀）。

import { zonesFromLayout, cameraAtPoint } from './zone-layout.js';
import { roomFromLayout, roomHasData, isWallUsable, isBoxUsable, FLOOR_MIN_M } from './room-model.js';

/**
 * show.json `run.intro` が欠けている時の既定。
 *
 * ⚠ この値は 4 箇所に現れる。**全部一致していること**:
 *   ここ / Unity の `IntroTiming.Default` / Unity の `ShowIntroDef` / capture-server.py の
 *   `_default_show`。片方だけ直すと沈黙して食い違う（下のテストが突き合わせる）。
 */
export const INTRO_DEFAULT = {
  enabled: true,
  maxSec: 20,
  // 段 1 開口が閉じ切る / 段 2 全黒（＋中に入るのを待つ）/ 段 3 管が点く / 段 4 映像
  sealSec: 1.4,
  darkSec: 0.8,
  igniteSec: 1.6,
  liveSec: 2.4,
  // 生成り。純白は蛍光灯の下の点検作業に見える（LEDGER 0010「全体的に暖色に」）。
  // Unity 側 ShowIntroDef.edgeColor / capture-server.py の _default_show と対。
  edgeColor: '#ffcf9e',
  // ⚠ 既定は**どちらも false**（2026-08-01）。細い寒色の線が現実に重なると計測器に見え、
  //    「現実がそのまま格下げされていく」という筋を切る。Unity 側 ShowIntroDef の既定と対。
  //    ⚠⚠ **2026-08-13 以降はどちらを on にしても画に出ない**（線を出していた段 3「構造」を
  //    廃止したため）。キーは残してあるが、`introPreflightRow` が「出ません」と言う。
  showCameraMarks: false,
  showRoomWire: false,
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

/** 段の秒（この 4 つの和が「演出」の尺）。UI の並び順もこの順（体験者が見る順）。 */
export const INTRO_STAGE_KEYS = ['sealSec', 'darkSec', 'igniteSec', 'liveSec'];

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
    // ⚠ 既定 false なので **=== true** で読む（`!== false` のままだと未指定が true に化け、
    //    JsonUtility が false で埋める実機と食い違う）。
    showCameraMarks: src.showCameraMarks === true,
    showRoomWire: src.showRoomWire === true,
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
 * 演出が実際に流れる秒数（4 段の和）。
 *
 * ⚠ **段 2 の「体験者が中に入るのを待つ」時間は含まない**（最大 3 秒・`IntroLogic.DarkHoldMaxSec`）。
 * 待ちは演出ではないので尺に足さない。実機（`IntroTiming.TotalSec`）と**同じ式**にしてある —
 * 片方だけ直すと、卓の表示と実機の尺が沈黙して食い違う。
 */
export function introStageSec(intro) {
  const cfg = intro && intro.sealSec !== undefined ? intro : introConfig({ intro });
  const sum = INTRO_STAGE_KEYS.reduce((x, k) => x + cfg[k], 0);
  return Math.round(sum * 10) / 10;
}

/** 「演出 6.2s / 慣らし 20s」— ⚙ 欄と本番前チェックで同じ文を出す。 */
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

// ---- 旧・段 3（構造）が描いていた幾何（判定だけ残す）--------------------------------

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
 * 旧・段 3 が現実の上に重ねていた線の内訳。
 *
 * ⚠ **2026-08-13 に段 3「構造」を廃止したので、いまこの線は 1 本も出ない。**
 * 判定そのものは残してある（`layout.room` / `layout.floor` の著作状況を見る手段として
 * 他所からも使われる）が、本番前チェックは幾何を要求しない。
 *
 * ⚠ 見る鍵は `layout.room` と `layout.floor`。`layout.wall` ではない
 * （あれは HMD 位置合わせリチュアルのワイヤー専用の旧データ）。
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
 * 見るのは 3 つ:
 *   構造の線の設定 — 段 3「構造」を廃止したので、on にしても画に出ない（⚠）
 *   上限           — 和が maxSec を超えると段を飛ばして映像を出す（⚠）
 *   開始位置       — 未設定なら手動開始・スタート区間の外なら入り直しになる（⚠）
 *
 * ⚠ **幾何（床の寸法・部屋の壁）はもう要求しない。** 要求していたのは段 3 が現実に線を
 * 直接重ねていたからで、その段が無くなった以上、直しようのある不備ではなくなった
 * （起きようのない不備を直させない）。幾何そのものは隔離殻と CG が別に要求する。
 */
export function introPreflightRow({ run, layout, cameras } = {}) {
  const intro = introConfig(run);
  if (!intro.enabled) return null;

  const label = '🎬 導入';
  const dur = introDurationLabel(intro, run && run.introMinSec);

  const warn = [];
  // ⚠ 段 3「構造」の廃止で、この 2 つは画に出なくなった。**設定が残っているのに効かない**のが
  //    いちばん分かりにくいので、on のときだけ名指しで言う。
  if (intro.showRoomWire || intro.showCameraMarks) {
    warn.push('壁の線 / カメラの印は導入から外れたので画に出ません'
      + '（段 3「構造」を廃止した。設定は off にしてよい）');
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
  //   演出 6.2s に対し introMinSec が 4s だと、演出の途中で本編へ移って**演出が切り落とされる**。
  //   RestartIntroClock は演出が終わった時にしか打たれないので、実機は黙って途中で切り替わる。
  const warmSec = Math.max(0, parseFloat(run && run.introMinSec) || 0);
  const stageSec = introStageSec(intro);
  if (warmSec > 0 && warmSec < stageSec) {
    warn.push(`慣らし歩行 ${warmSec}s が演出 ${stageSec}s より短いので、演出が途中で切り落とされます`
      + '（⚙ 欄の「導入（慣らし歩行）」を演出より長くする）');
  }

  const sec = introStageSec(intro);
  if (sec > intro.maxSec) {
    warn.push(`演出 ${sec}s が上限 ${intro.maxSec}s を超えています（超えた段は飛ばして映像を出します）`);
  }
  if (warn.length) return { s: 'warn', label, detail: `${warn.join(' ・ ')} — ${dur}` };
  return { s: 'ok', label, detail: dur };
}
