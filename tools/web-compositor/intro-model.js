// 導入演出（show.json `run.intro`）の判定。DOM 非依存の純関数だけを置く。
//
// 導入は「素通しの現実が格下げされ、割れて、スクリーンの枠へ吸い込まれ、その中に自分が居る」
// 12.7 秒の遷移（2026-08-15 に旧構成へ戻した。封印の箱を退避したので「箱の中に入ってから
// 固定視点」が成立しない — canon/LEDGER.md 0044）。
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
  // 段 1 素通し / 段 2 格下げ / 段 3 輪郭（段 2 と重なる）/ 段 4 割れる / 段 5 映像
  realSec: 1.5,
  degradeSec: 3.5,
  structureSec: 2.5,
  frameSec: 5.0,
  swapSec: 1.6,
  // 生成り。純白は蛍光灯の下の点検作業に見える（LEDGER 0010「全体的に暖色に」）。
  // Unity 側 ShowIntroDef.edgeColor / capture-server.py の _default_show と対。
  edgeColor: '#ffcf9e',
  // ⚠ 既定は**どちらも false**（2026-08-01）。細い寒色の線が現実に重なると計測器に見え、
  //    「現実がそのまま格下げされていく」という筋を切る。Unity 側 ShowIntroDef の既定と対。
  //    ⚠ 2026-08-15 に段 3「構造」が戻ったので、on にすれば**また画に出る**（既定は off のまま）。
  showCameraMarks: false,
  showRoomWire: false,
  glitchOnSwap: 0,
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

/** 段の秒。UI の並び順もこの順（体験者が見る順）。⚠ 単純和は尺ではない（下の `introStageSec`）。 */
export const INTRO_STAGE_KEYS = ['realSec', 'degradeSec', 'structureSec', 'frameSec', 'swapSec'];

/**
 * 段 3（構造）が段 2（格下げ）と重なり始める進み。
 * ⚠ Unity の `IntroLogic.StructureOverlapAt` と**同じ値**。片方だけ直すと尺が食い違う。
 */
export const STRUCTURE_OVERLAP_AT = 0.6;
/** 段 3 が段 2 に飲み込まれても、これだけは単独で流れる（`IntroLogic.StructureMinOwnSec`）。 */
export const STRUCTURE_MIN_OWN_SEC = 0.5;

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
 * 演出が実際に流れる秒数。
 *
 * ⚠ **単純和ではない。** 段 3 は段 2 の後半から始まるので、重なった分は二度流れない。
 * 実機（`IntroTiming.TotalSec`）と**同じ式**にしてある — 片方だけ直すと、
 * 卓の表示と実機の尺が沈黙して食い違う。
 */
export function introStageSec(intro) {
  const cfg = intro && intro.realSec !== undefined ? intro : introConfig({ intro });
  let own = cfg.structureSec - cfg.degradeSec * (1 - STRUCTURE_OVERLAP_AT);
  if (own < STRUCTURE_MIN_OWN_SEC) own = STRUCTURE_MIN_OWN_SEC;
  const sum = cfg.realSec + cfg.degradeSec + own + cfg.frameSec + cfg.swapSec;
  return Math.round(sum * 10) / 10;
}

/** 「演出 12.7s / 慣らし 20s」— ⚙ 欄と本番前チェックで同じ文を出す。 */
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
 * 段 3 が現実の上に重ねる線の内訳。
 *
 * ⚠ **線は既定 off**（2026-08-01 ユーザー判断）。on にしたときだけ画に出るので、
 * 本番前チェックは幾何を要求しない（起きようのない不備を直させない）。
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
 *   構造の線の設定 — on なら重ねる相手（実物の寸法）が要る（⚠）
 *   上限           — 尺が maxSec を超えると段を飛ばして映像を出す（⚠）
 *   開始の合図     — **実機と同じ優先順位**（接近 > 通過ライン > 円）で、
 *                    使われないものが著作されたままなら名指しする（⚠）
 *
 * ⚠ **線が off なら幾何は要求しない**（起きようのない不備を直させない）。
 * 幾何そのものは隔離殻と CG が別に要求する。
 */
export function introPreflightRow({ run, layout, cameras } = {}) {
  const intro = introConfig(run);
  if (!intro.enabled) return null;

  const label = '🎬 導入';
  const dur = introDurationLabel(intro, run && run.introMinSec);

  const warn = [];
  // ⚠ 線を on にしたなら、重ねる相手（実物の寸法）が要る。著作していないと
  //    既定値の壁が現実の全然違う所へ重なる。
  if (intro.showRoomWire || intro.showCameraMarks) {
    const g = introWireGeometry(layout);
    if (!g.hasFloor) {
      warn.push('壁の線 / カメラの印を on にしていますが、床の実寸が未著作です'
        + '（較正パネルで床の幅×奥行を入れる）');
    } else if (intro.showRoomWire && g.wallCount === 0) {
      warn.push('壁の線を on にしていますが、layout.room に壁が 1 本もありません'
        + '（フロアマップの 🧱 部屋で引く）');
    } else if (g.looksDefaultL) {
      warn.push('壁が卓の既定（1m × 1m の L）のままです — 実測した値か判定できません');
    }
  }
  // ---- 開始の合図。**実機と同じ優先順位で見る**（IntroDirector.IsAtStartSpot）------------
  //   1. 接近（体験エリアへ近づいてくる動き）… 床の実寸が解ける限りこれが正
  //   2. 通過ライン run.intro.startLineId … 1 が解けないときだけ
  //   3. 開始位置の円 layout.startSpot   … 2 も空のときだけ
  // ⚠ ここは 2026-08-13 に接近が入るまで「円が無ければ手動運用」と言い切っていた。
  //   接近が効く現場でそれを言うと**嘘の警告**になり、しかも直し方（円を置け）まで間違っている。
  const startsByApproach = introWireGeometry(layout).hasFloor;
  const lineId = (intro.startLineId || '').trim();
  if (startsByApproach) {
    // 円も線も見られない。**著作してあるのに使われないものだけ**を名指しする
    //   （消してあるものについては黙る — 消したのに警告が残るのが一番たちが悪い）。
    if (lineId) {
      warn.push(`開始の通過ライン「${lineId}」は使われません`
        + ' — 床の実寸が著作されているので、体験エリアへの接近で始まります');
    }
    if (startSpotOf(layout)) {
      warn.push('開始位置の円は使われません — 同上（消しても体験は変わりません）');
    }
  } else if (lineId) {
    if (startSpotOf(layout)) {
      warn.push('開始位置の円は使われません — 開始の通過ラインが指定されています');
    }
  } else if (!startSpotOf(layout)) {
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
  //   演出 12.7s に対し introMinSec が 4s だと、演出の途中で本編へ移って**演出が切り落とされる**。
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
