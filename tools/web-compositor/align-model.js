// カメラを**手で合わせる**ための状態と操作（DOM 非依存・node からテストできる）。
//
// なぜ解くのをやめたか（実測）:
//   show.json の 4 台はすべて `calib` 無し・`pose` 有り。2026-07-29 に較正 UI を全面立て直した
//   あとも 6 日間 1 台も解かれていない。失敗は数値ではなく**入力**にあった — 候補点が
//   「既定値の壁」「仮想の格子」から作られていて、現場の床にその目印が実在しない。
//   実在しない点をどれだけ丁寧に打っても解は歪む。人が絵を見て合わせる方が確実に速い。
//
// ⚠ **掴めるのは外部 6 自由度だけ**（x / y / z / yaw / pitch / roll）。
//   画角 `fxPx` と歪み `k1` はドラッグ対象にしない。「画角を広げて近づける」と
//   「狭めて遠ざける」は画の上でほぼ同じで、`calib.test.mjs` の実測では画角を未知にすると
//   位置が 27cm・固定すれば 2cm。手ドラッグは曖昧さを消さず**残差の表示だけ**を消すので、
//   掴めるようにした瞬間 solver より悪くなる。レンズ単位で一度だけ決める。
//
// ⚠ 動かすのは**カメラ**であって部屋ではない。`layout.room` は 4 台 × 3 用途
//   （オクルーダ / 影の落ち先 / 較正参照）の共有資産で、1 台に合わせて頂点をずらすと
//   他のカメラの合成が黙って狂い、人形の course 座標がカメラごとに食い違う。
import { projectPoint, unprojectToFloor, unityEulerToMatrix, matrixToUnityEuler } from './calib.js';
import { calibMatchesSource } from './calib-session.js';

export const DEFAULT_HFOV_DEG = 77.4;   // 現 show.json の 4 台が持つ値（人が打ったレンズ諸元）
export const MIN_HEIGHT_M = 0.05;
export const MAX_HEIGHT_M = 6.0;
export const MAX_PITCH_DEG = 89.0;
export const DOLLY_STEP_M = 0.05;

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
const num = (v, d) => (Number.isFinite(v) ? v : d);
const wrapDeg = (d) => ((d % 360) + 540) % 360 - 180;

/** 水平画角 (度) → 焦点距離 (px)。正方画素なので fy = fx（actor-proxy と同じ根拠）。 */
export function focalFromHfov(hfovDeg, srcW) {
  const w = srcW > 1 ? srcW : 640;
  const h = clamp(num(hfovDeg, DEFAULT_HFOV_DEG), 10, 170) * Math.PI / 180;
  return (w / 2) / Math.tan(h / 2);
}

export function hfovFromFocalPx(fxPx, srcW) {
  const w = srcW > 1 ? srcW : 640;
  if (!(fxPx > 0)) return DEFAULT_HFOV_DEG;
  return 2 * Math.atan((w / 2) / fxPx) * 180 / Math.PI;
}

/**
 * カメラのローカル軸をワールドで返す。
 * `projectPoint` は `p_cam = R^T d` なので、**R の列がカメラ軸**（0=右 / 1=上 / 2=前）。
 */
export function cameraBasis(calib) {
  const R = unityEulerToMatrix(-calib.pitchDeg, calib.yawDeg, calib.rollDeg || 0);
  return {
    right: [R[0][0], R[1][0], R[2][0]],
    up: [R[0][1], R[1][1], R[2][1]],
    fwd: [R[0][2], R[1][2], R[2][2]],
  };
}

/**
 * このカメラの合わせ始めの姿勢を決める。
 * 既にある `calib`（映像の実寸と一致するもの）> 人が置いた `pose` > 既定、の順。
 */
export function initialCalib(cam, srcW, srcH) {
  const w = srcW > 1 ? srcW : 640, h = srcH > 1 ? srcH : 480;
  const c = cam && cam.calib;
  if (c && c.fxPx > 1 && calibMatchesSource(c, w, h)) {
    return { ...c, srcW: w, srcH: h, fyPx: c.fyPx > 0 ? c.fyPx : c.fxPx };
  }
  const p = (cam && cam.pose) || {};
  const fx = focalFromHfov(p.hfovDeg, w);
  return {
    x: num(p.x, 0), y: num(p.y, 1.2), z: num(p.z, 0),
    yawDeg: num(p.yawDeg, 0), pitchDeg: num(p.pitchDeg, 0), rollDeg: 0,
    fxPx: fx, fyPx: fx, cxPx: w / 2, cyPx: h / 2, k1: num(c && c.k1, 0),
    srcW: w, srcH: h,
  };
}

export function clampCalib(c) {
  return {
    ...c,
    y: clamp(num(c.y, 1.2), MIN_HEIGHT_M, MAX_HEIGHT_M),
    yawDeg: wrapDeg(num(c.yawDeg, 0)),
    pitchDeg: clamp(num(c.pitchDeg, 0), -MAX_PITCH_DEG, MAX_PITCH_DEG),
    rollDeg: clamp(num(c.rollDeg, 0), -45, 45),
  };
}

/**
 * 画面をドラッグして**世界を掴んで回す**。カーソルの下にあったものがカーソルに付いてくる。
 *
 * 符号は理屈で決めずに `align-model.test.mjs` が「ドラッグ後、その点が (u+dx, v+dy) に来る」で
 * 固定している。ここを勘で書くと、上下左右のどれかが必ず逆になる。
 */
export function dragRotate(calib, dxPx, dyPx) {
  const fx = calib.fxPx > 0 ? calib.fxPx : 1;
  const fy = calib.fyPx > 0 ? calib.fyPx : fx;
  const dYaw = -Math.atan2(dxPx, fx) * 180 / Math.PI;
  // ⚠ 上下だけ符号が逆（`projectPoint` の v = cy - fy*ny で v が下向きなため）。
  //    左右と同じ符号にすると、上下だけ逆に動く道具になる。
  const dPitch = Math.atan2(dyPx, fy) * 180 / Math.PI;
  return clampCalib({ ...calib, yawDeg: calib.yawDeg + dYaw, pitchDeg: calib.pitchDeg + dPitch });
}

/**
 * 画面をドラッグして**カメラを平行移動**する（回さずにずらす）。
 * `refDistM` はドラッグの効き目を決める基準距離＝いま合わせている物までのおよその距離。
 */
export function dragPan(calib, dxPx, dyPx, refDistM = 3.0) {
  const fx = calib.fxPx > 0 ? calib.fxPx : 1;
  const fy = calib.fyPx > 0 ? calib.fyPx : fx;
  const d = clamp(num(refDistM, 3), 0.2, 50);
  const { right, up } = cameraBasis(calib);
  const sx = -(dxPx / fx) * d;      // 世界が右へ動く = カメラが左へ動く
  const sy = (dyPx / fy) * d;       // 世界が下へ動く = カメラが上へ動く
  return clampCalib({
    ...calib,
    x: calib.x + right[0] * sx + up[0] * sy,
    y: calib.y + right[1] * sx + up[1] * sy,
    z: calib.z + right[2] * sx + up[2] * sy,
  });
}

/** 視線方向へ前後する（近づく / 遠ざかる）。画角を変えるのではなく**動く**のが正しい。 */
export function dolly(calib, meters) {
  const { fwd } = cameraBasis(calib);
  return clampCalib({
    ...calib,
    x: calib.x + fwd[0] * meters,
    y: calib.y + fwd[1] * meters,
    z: calib.z + fwd[2] * meters,
  });
}

// ---- 軸拘束（Blender / CAD 流）---------------------------------------------
// 自由ドラッグは速いが、**あと一歩を詰められない**（1 軸だけ直したいのに他の軸が動く）。
// 軸を指定すると、その軸「だけ」が動く。X / Y / Z はワールド（course 空間）の軸。

export const AXES = ['x', 'y', 'z'];
const AXIS_VEC = { x: [1, 0, 0], y: [0, 1, 0], z: [0, 0, 1] };

/** ワールド軸まわりの回転行列（右手・行優先）。 */
function axisRotation(axis, deg) {
  const a = deg * Math.PI / 180, c = Math.cos(a), s = Math.sin(a);
  if (axis === 'x') return [[1, 0, 0], [0, c, -s], [0, s, c]];
  if (axis === 'y') return [[c, 0, s], [0, 1, 0], [-s, 0, c]];
  return [[c, -s, 0], [s, c, 0], [0, 0, 1]];
}

const mul33 = (A, B) => A.map((row) => [0, 1, 2].map((j) =>
  row[0] * B[0][j] + row[1] * B[1][j] + row[2] * B[2][j]));

/**
 * **ワールド軸まわりにカメラを回す。**
 * yaw/pitch/roll のどれか 1 つを足すのではなく、回転行列を合成してから Euler へ戻す
 * （X や Z 軸まわりの回転は yaw/pitch/roll のどれか 1 つでは表せない）。
 */
export function rotateAboutWorldAxis(calib, axis, deg) {
  if (!AXIS_VEC[axis] || !deg) return calib;
  const R = unityEulerToMatrix(-calib.pitchDeg, calib.yawDeg, calib.rollDeg || 0);
  const e = matrixToUnityEuler(mul33(axisRotation(axis, deg), R));
  return clampCalib({ ...calib, pitchDeg: -e.x, yawDeg: e.y, rollDeg: e.z });
}

/** ワールド軸に沿ってカメラを平行移動する。 */
export function translateAlongWorldAxis(calib, axis, meters) {
  const v = AXIS_VEC[axis];
  if (!v || !meters) return calib;
  return clampCalib({
    ...calib,
    x: calib.x + v[0] * meters, y: calib.y + v[1] * meters, z: calib.z + v[2] * meters,
  });
}

/**
 * 軸の**画面上での向き**（単位ベクトル）と、1m あたりの画素数。
 * ドラッグ量をその軸へ落とすのに使う（軸が画面で寝ているほど、同じドラッグで大きく動く）。
 * 軸が画面でほぼ点に潰れている（カメラ正面を向いている）ときは null。
 */
export function axisScreenDir(calib, axis, atPoint) {
  const v = AXIS_VEC[axis];
  if (!v) return null;
  const p = atPoint || [calib.x + 0, 0, calib.z + 0];
  const eps = 0.05;
  const a = projectPoint(calib, p[0], p[1], p[2]);
  const b = projectPoint(calib, p[0] + v[0] * eps, p[1] + v[1] * eps, p[2] + v[2] * eps);
  if (!a || !b) return null;
  const du = b.u - a.u, dv = b.v - a.v;
  const len = Math.hypot(du, dv);
  if (!(len > 1e-3)) return null;              // 画面で点に潰れている＝この軸では動かせない
  return { dir: [du / len, dv / len], pxPerM: len / eps };
}

/**
 * 軸拘束つきのドラッグ。ドラッグを軸の画面方向へ**射影**してから、その軸だけ動かす。
 * `kind` は 'move'（平行移動）か 'rotate'（回転）。
 */
export function dragConstrained(calib, axis, kind, dxPx, dyPx, opts = {}) {
  const at = opts.at || [0, 0, 0];
  const sd = axisScreenDir(calib, axis, at);
  if (!sd) return calib;
  const along = dxPx * sd.dir[0] + dyPx * sd.dir[1];     // 画面上で軸に沿った成分だけ拾う
  if (kind === 'rotate') {
    // 回転は「画面 100px のドラッグで 30°」を目安にする（細かく詰められる速さ）
    return rotateAboutWorldAxis(calib, axis, -along * 0.30);
  }
  // ⚠ 世界が along だけ動いて見えるように、カメラは**逆へ**動かす
  return translateAlongWorldAxis(calib, axis, -along / sd.pxPerM);
}

/** 床の上でカメラを置き直す（上から見た図のドラッグ用）。高さと向きは変えない。 */
export function setGroundPos(calib, x, z) {
  return clampCalib({ ...calib, x: num(x, calib.x), z: num(z, calib.z) });
}

export function setField(calib, key, value) {
  if (!(key in calib) && !['x', 'y', 'z', 'yawDeg', 'pitchDeg', 'rollDeg'].includes(key)) return calib;
  return clampCalib({ ...calib, [key]: num(Number(value), calib[key]) });
}

/** レンズ（画角・歪み）を差し替える。**現場では触らない**想定の、別枠の操作。 */
export function setLens(calib, { hfovDeg, k1 } = {}) {
  const fx = hfovDeg != null ? focalFromHfov(hfovDeg, calib.srcW) : calib.fxPx;
  return { ...calib, fxPx: fx, fyPx: fx, k1: k1 != null ? clamp(num(Number(k1), 0), -0.6, 0.6) : calib.k1 };
}

/**
 * 保存する形。**`cameras[].calib` のスキーマそのまま**（Unity 側は 1 行も変えない）。
 *
 * ⚠ `rmsPx` / `accuracyM` / `refs` は**書かない**。あれは自動で解いたときの当てはまりと
 *   精度の指標で、手で置いた値に付けると嘘になる。代わりに `method:"manual"` を立てて、
 *   卓のバッジと本番前チェックが「手で合わせた（誤差の数値は無し）」と別扱いできるようにする。
 */
export function toManualCalib(calib, { lensId = '', nowIso = '' } = {}) {
  const c = clampCalib(calib);
  return {
    x: r3(c.x), y: r3(c.y), z: r3(c.z),
    yawDeg: r3(c.yawDeg), pitchDeg: r3(c.pitchDeg), rollDeg: r3(c.rollDeg),
    fxPx: r3(c.fxPx), fyPx: r3(c.fyPx > 0 ? c.fyPx : c.fxPx),
    cxPx: r3(c.cxPx), cyPx: r3(c.cyPx), k1: r3(c.k1 || 0),
    srcW: Math.round(c.srcW), srcH: Math.round(c.srcH),
    lensId: lensId || '',
    method: 'manual',
    solvedAtIso: nowIso || '',
  };
}

export const isManual = (c) => !!c && c.method === 'manual';

const r3 = (v) => Math.round(num(v, 0) * 1000) / 1000;

/** 画面に出す 1 行ずつの状態（数値を読みながら合わせられるように）。 */
export function summaryLines(calib) {
  return [
    `位置  X ${calib.x.toFixed(2)}  Z ${calib.z.toFixed(2)}  高さ ${calib.y.toFixed(2)} m`,
    `向き  左右 ${calib.yawDeg.toFixed(1)}°  上下 ${calib.pitchDeg.toFixed(1)}°  傾き ${(calib.rollDeg || 0).toFixed(1)}°`,
    `画角  ${hfovFromFocalPx(calib.fxPx, calib.srcW).toFixed(1)}°（レンズ・ここでは動かさない）`,
  ];
}

/**
 * いま合わせている物までのおよその距離。ドラッグの効き目に使う。
 * 画面中央のレイが床と交わる点までの距離＝「見ている床までの距離」。
 */
export function refDistance(calib) {
  const hit = unprojectToFloor(calib, calib.cxPx, calib.cyPx, 0);
  return hit && hit.t > 0.2 ? clamp(hit.t, 0.2, 50) : 3.0;
}

export { projectPoint, unprojectToFloor };
