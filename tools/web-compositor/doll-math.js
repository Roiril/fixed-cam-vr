// 卓で人形を描くための行列と光（DOM / WebGL 非依存・node からテストできる）。
//
// ⚠ ここの投影は **`calib.js` の `projectPoint` と同じ画素を出さなければならない**。
//   ワイヤー（2D で projectPoint で描く）と人形（WebGL で行列で描く）が違う式だと、
//   **ワイヤーは合っているのに人形だけずれる**という最悪の壊れ方をする。
//   `doll-math.test.mjs` が同一点の画素を突き合わせて固定している。
//
// ⚠ 影の落とし方は Unity の `ShadowProjectionLogic.TryProjectToPlane` と同じ式。
//   片方だけ直すと沈黙して食い違う。
import { distortNorm, unityEulerToMatrix } from './calib.js';

export const SHADOW_MIN_LIGHT_UP = 0.02;   // ShadowProjectionLogic.MinLightUp と同値

/** ワールド → カメラ座標系への回転（`projectPoint` の `p_cam = R^T d` の R^T を行で返す）。 */
export function viewRotation(calib) {
  const R = unityEulerToMatrix(-calib.pitchDeg, calib.yawDeg, calib.rollDeg || 0);
  // R^T の行 = R の列
  return [
    [R[0][0], R[1][0], R[2][0]],
    [R[0][1], R[1][1], R[2][1]],
    [R[0][2], R[1][2], R[2][2]],
  ];
}

/**
 * ビュー行列（列優先の 16 要素・WebGL へそのまま渡せる）。
 * カメラ空間は **+Z が前**（`projectPoint` と同じ。GL の -Z 前方ではない）。
 * クリップ座標はシェーダ側で内部パラメータから直に組むので、ここでは射影を含めない。
 */
export function viewMatrix(calib) {
  const Rt = viewRotation(calib);
  const c = [calib.x, calib.y, calib.z];
  const t = [0, 1, 2].map((i) => -(Rt[i][0] * c[0] + Rt[i][1] * c[1] + Rt[i][2] * c[2]));
  // column-major: m[col*4 + row]
  const m = new Float32Array(16);
  for (let r = 0; r < 3; r++) for (let col = 0; col < 3; col++) m[col * 4 + r] = Rt[r][col];
  m[12] = t[0]; m[13] = t[1]; m[14] = t[2]; m[15] = 1;
  return m;
}

/** ワールド点 → カメラ座標（テストと影の計算で使う）。 */
export function toCamera(calib, p) {
  const Rt = viewRotation(calib);
  const d = [p[0] - calib.x, p[1] - calib.y, p[2] - calib.z];
  return [0, 1, 2].map((i) => Rt[i][0] * d[0] + Rt[i][1] * d[1] + Rt[i][2] * d[2]);
}

/**
 * カメラ座標 → 画素。**シェーダが組むクリップ座標と同じ順序**（歪みを掛けてから内部行列）。
 * @returns {{u:number,v:number}|null}
 */
export function camToPixel(calib, pc) {
  if (!(pc[2] > 1e-4)) return null;
  const nd = distortNorm(pc[0] / pc[2], pc[1] / pc[2], calib.k1 || 0);
  if (!nd) return null;
  return { u: calib.cxPx + calib.fxPx * nd[0], v: calib.cyPx - calib.fyPx * nd[1] };
}

/**
 * 光の向き（**光が来る向き**・上向き成分が正）。
 * Unity の `ShowCgLayer.CourseLightDirToWorld` と同じ式。course の yaw は卓では 0。
 */
export function lightDirection(light) {
  const yaw = ((light && light.yawDeg) || 0) * Math.PI / 180;
  const pitch = Math.max(-90, Math.min(90, (light && light.pitchDeg) != null ? light.pitchDeg : 55)) * Math.PI / 180;
  const cp = Math.cos(pitch);
  const v = [Math.sin(yaw) * cp, Math.sin(pitch), Math.cos(yaw) * cp];
  const n = Math.hypot(v[0], v[1], v[2]) || 1;
  return [v[0] / n, v[1] / n, v[2] / n];
}

/**
 * 点を光線方向に沿って高さ `planeY` の水平面へ落とす（平面投影シャドウ）。
 * Unity の `ShadowProjectionLogic.TryProjectToPlane` と同じ式・同じ打ち切り。
 * 光が真横以下なら null（床との交点が無限遠へ飛び、画面いっぱいの黒帯になるため）。
 */
export function projectToPlane(p, planeY, lightDir) {
  const l = lightDir;
  if (!l || !(Math.abs(l[0]) + Math.abs(l[1]) + Math.abs(l[2]) > 1e-8)) return null;
  if (!(l[1] > SHADOW_MIN_LIGHT_UP)) return null;
  const t = (p[1] - planeY) / l[1];
  return [p[0] - l[0] * t, planeY, p[2] - l[2] * t];
}

/** 4x4 の平面投影シャドウ行列（列優先）。頂点シェーダで 1 回掛けるだけで影になる。 */
export function shadowMatrix(planeY, lightDir) {
  const l = lightDir;
  if (!l || !(l[1] > SHADOW_MIN_LIGHT_UP)) return null;
  const m = new Float32Array(16);
  // p' = p - l * (p.y - planeY) / l.y
  const k = 1 / l[1];
  m[0] = 1; m[4] = -l[0] * k; m[8] = 0; m[12] = l[0] * k * planeY;
  m[1] = 0; m[5] = 0; m[9] = 0; m[13] = planeY;
  m[2] = 0; m[6] = -l[2] * k; m[10] = 1; m[14] = l[2] * k * planeY;
  m[3] = 0; m[7] = 0; m[11] = 0; m[15] = 1;
  return m;
}

/** 色温度 (K) → linear RGB 0..1。卓の `kelvinToRgb`（sRGB 0..255）を linear へ落としたもの。 */
export function kelvinLinear(tempK, kelvinToRgb) {
  const c = kelvinToRgb(tempK);
  const lin = (v) => {
    const s = v / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return [lin(c.r), lin(c.g), lin(c.b)];
}
