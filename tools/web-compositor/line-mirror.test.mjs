// 床の線を画像空間の鏡へ写す純関数（`canon/LEDGER.md` 0102）を node:test で固定する。
//   実行: node --test tools/web-compositor/
//
// ここで守るのは 3 つ。どれも外すと「録画が凍結より外から始まる」が黙って戻る:
//   1. 画像空間の鏡は **course 空間の鏡と別物**（カメラが対称軸の真上に無い限り一致しない）
//   2. 2 回写すと元へ戻る（対合）
//   3. 較正が無い / 写っていないときは**答えを返さない**（推測で線を置かない）

import test from 'node:test';
import assert from 'node:assert/strict';
import { projectPoint } from './calib.js';
import {
  mirrorLine, mirrorFloorPoint, mirrorResidualM, mirrorSideCheck,
  calibUsableForMirror, MIRROR_TOLERANCE_M,
} from './line-mirror.js';

// 部屋の南西寄りに置いて北東を向く、ふつうの広角カメラ。x 軸に対して対称ではない
// （＝ course 空間の鏡と画像空間の鏡が一致しない配置）。
const CAM = {
  x: -0.62, y: 1.34, z: -0.78, yawDeg: 34, pitchDeg: -12, rollDeg: 0,
  fxPx: 431.2, fyPx: 431.2, cxPx: 320, cyPx: 240, k1: 0.18, srcW: 640, srcH: 480,
};

test('較正が無ければ答えを返さない', () => {
  assert.equal(calibUsableForMirror(null), false);
  assert.equal(calibUsableForMirror({ fxPx: 0, srcW: 640, srcH: 480 }), false);
  const r = mirrorLine(null, { x1: 0, z1: 0, x2: 0.5, z2: 0 });
  assert.ok(r.error, '較正が無いのに座標を返してはいけない');
});

test('鏡へ写した点は画像の横中央に対して対称', () => {
  const p = { x: 0.35, z: 0.20 };
  const m = mirrorFloorPoint(CAM, p.x, p.z);
  assert.ok(m, '写るはずの点');
  const a = projectPoint(CAM, p.x, 0, p.z);
  const b = projectPoint(CAM, m.x, 0, m.z);
  assert.ok(a && b);
  // u が中央の反対側へ移り、v は動かない。
  assert.ok(Math.abs((a.u + b.u) / 2 - CAM.srcW / 2) < 0.5,
    `u の中点が画像の中央でない: ${(a.u + b.u) / 2}`);
  assert.ok(Math.abs(a.v - b.v) < 0.5, 'v は動かない（横の反転だけ）');
});

test('2 回写すと元へ戻る（対合）', () => {
  const p = { x: 0.42, z: -0.11 };
  const m1 = mirrorFloorPoint(CAM, p.x, p.z);
  const m2 = mirrorFloorPoint(CAM, m1.x, m1.z);
  assert.ok(Math.hypot(m2.x - p.x, m2.z - p.z) < 1e-3,
    `対合が壊れている: (${m2.x}, ${m2.z}) != (${p.x}, ${p.z})`);
});

test('画像空間の鏡は course 空間の鏡と一致しない（この機構が要る理由）', () => {
  // course 空間で「対角軸に対する鏡像」＝ (x, z) → (-z, -x)。現行の line_rec_start の作り方。
  const p = { x: 0.60, z: 0.30 };
  const img = mirrorFloorPoint(CAM, p.x, p.z);
  const course = { x: -p.z, z: -p.x };
  const gap = Math.hypot(img.x - course.x, img.z - course.z);
  assert.ok(gap > MIRROR_TOLERANCE_M,
    `2 つの鏡が一致してしまっている（ずれ ${gap.toFixed(3)}m）。`
    + 'この配置では機構の意味が示せないので、テストのカメラ姿勢を非対称にし直す');
});

test('線をまるごと写す — 端点の順序は保つ', () => {
  const line = { x1: 0.30, z1: 0.10, x2: 0.75, z2: 0.10 };
  const m = mirrorLine(CAM, line);
  assert.ok(!m.error, m.error);
  // 端点 1 の鏡は端点 1 に対応する（入れ替わると dir の意味が反転する）。
  const e1 = mirrorFloorPoint(CAM, line.x1, line.z1);
  assert.ok(Math.hypot(m.x1 - e1.x, m.z1 - e1.z) < 1e-3);
});

test('残差は「いま置かれている線が鏡像か」を測る', () => {
  const src = { x1: 0.30, z1: 0.10, x2: 0.75, z2: 0.10 };
  const good = mirrorLine(CAM, src);
  assert.ok(mirrorResidualM(CAM, src, good) < 1e-3, '作り直した直後は 0');

  const moved = { ...good, x1: good.x1 + 0.25 };
  assert.ok(mirrorResidualM(CAM, src, moved) > MIRROR_TOLERANCE_M,
    '片方を動かしたら残差で分かる');

  assert.equal(mirrorResidualM(null, src, good), null, '較正が無ければ判定しない');
});

test('凍結の線が左半分に写っていたら側を教える', () => {
  // カメラの右手側（画面の右）に来る点で作った線。
  const right = { x1: 0.55, z1: -0.35, x2: 0.80, z2: -0.10 };
  const left = { x1: -0.80, z1: 0.35, x2: -0.55, z2: 0.60 };
  const rMid = projectPoint(CAM, (right.x1 + right.x2) / 2, 0, (right.z1 + right.z2) / 2);
  const lMid = projectPoint(CAM, (left.x1 + left.x2) / 2, 0, (left.z1 + left.z2) / 2);
  assert.ok(rMid && lMid, 'どちらも写っている前提のテスト');
  // どちらが右かはこのカメラ姿勢が決めるので、実測値から期待を作る（数値をハードコードしない）。
  const rOk = rMid.u / CAM.srcW >= 0.5;
  assert.equal(mirrorSideCheck(CAM, right).ok, rOk);
  assert.equal(mirrorSideCheck(CAM, left).ok, lMid.u / CAM.srcW >= 0.5);
  // 左半分と判定された側は必ず理由を持つ。
  const bad = rOk ? left : right;
  const res = mirrorSideCheck(CAM, bad);
  if (!res.ok) assert.ok(res.detail.includes('左半分'));
});
