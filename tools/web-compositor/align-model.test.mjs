// 手動カメラ合わせの操作を数値で固定する。
//
// ⚠ ドラッグの符号は**理屈で決めない**。「カーソルの下にあった点が、ドラッグ後もカーソルの
//   下に来る」を assert して定義する。ここを勘で書くと上下左右のどれかが必ず逆になり、
//   しかも実際に触るまで気づけない。
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  initialCalib, dragRotate, dragPan, dolly, cameraBasis, setGroundPos, setLens,
  focalFromHfov, hfovFromFocalPx, toManualCalib, clampCalib, refDistance, summaryLines,
  MAX_PITCH_DEG, DEFAULT_HFOV_DEG,
} from './align-model.js';
import { projectPoint } from './calib.js';

const CAM = { pose: { x: 0, y: 1.4, z: -2.5, yawDeg: 0, pitchDeg: -12, hfovDeg: 77.4 } };
const base = () => initialCalib(CAM, 640, 480);

test('画角と焦点距離は往復する', () => {
  const fx = focalFromHfov(77.4, 640);
  assert.ok(Math.abs(hfovFromFocalPx(fx, 640) - 77.4) < 1e-6);
  // 640px・77.4° の実値（現 show.json の 4 台が持つ画角）を数値で固定する
  assert.ok(Math.abs(fx - 399.46) < 0.05, `fx=${fx}`);
});

test('pose から始める（calib が無いカメラ）', () => {
  const c = base();
  assert.equal(c.y, 1.4);
  assert.equal(c.pitchDeg, -12);
  assert.equal(c.cxPx, 320);
  assert.equal(c.cyPx, 240);
  assert.equal(c.k1, 0);
});

test('calib があればそちらから始める', () => {
  const cam = { pose: CAM.pose, calib: { x: 1, y: 2, z: 3, yawDeg: 10, pitchDeg: -5, rollDeg: 2, fxPx: 400, fyPx: 400, cxPx: 320, cyPx: 240, k1: 0.1, srcW: 640, srcH: 480 } };
  const c = initialCalib(cam, 640, 480);
  assert.equal(c.x, 1);
  assert.equal(c.k1, 0.1);
});

test('解像度が食い違う calib は使わない（pose へ落ちる）', () => {
  const cam = { pose: CAM.pose, calib: { x: 9, y: 9, z: 9, fxPx: 400, srcW: 1280, srcH: 720 } };
  const c = initialCalib(cam, 640, 480);
  assert.equal(c.x, 0, '別解像度の解を持ち込むと黙って別の場所に人形が立つ');
});

// --- ドラッグの定義 -------------------------------------------------------
// 「掴んだ点が付いてくる」。画面中央付近なら数 px で一致するはず。
for (const [dx, dy] of [[40, 0], [-40, 0], [0, 30], [0, -30], [25, 18]]) {
  test(`回すドラッグ (${dx},${dy}) で掴んだ点が付いてくる`, () => {
    const c = base();
    // 画面中央あたりに写る床の点を選ぶ
    const p = [0.3, 0, 1.2];
    const a = projectPoint(c, ...p);
    assert.ok(a, '始点が写っていること');
    const c2 = dragRotate(c, dx, dy);
    const b = projectPoint(c2, ...p);
    assert.ok(b, 'ドラッグ後も写っていること');
    assert.ok(Math.abs(b.u - (a.u + dx)) < 3.0, `u: ${b.u} vs ${a.u + dx}`);
    assert.ok(Math.abs(b.v - (a.v + dy)) < 3.0, `v: ${b.v} vs ${a.v + dy}`);
  });
}

for (const [dx, dy] of [[50, 0], [0, 40], [-30, -20]]) {
  test(`平行移動ドラッグ (${dx},${dy}) で掴んだ点が付いてくる`, () => {
    const c = base();
    const p = [0.0, 0, 0.0];
    const a = projectPoint(c, ...p);
    const dist = Math.hypot(p[0] - c.x, p[1] - c.y, p[2] - c.z);
    const c2 = dragPan(c, dx, dy, dist);
    const b = projectPoint(c2, ...p);
    assert.ok(b);
    // 平行移動は距離に依存するので許容を少し広く取る（基準距離ちょうどの点で評価している）
    assert.ok(Math.abs(b.u - (a.u + dx)) < 6.0, `u: ${b.u} vs ${a.u + dx}`);
    assert.ok(Math.abs(b.v - (a.v + dy)) < 6.0, `v: ${b.v} vs ${a.v + dy}`);
  });
}

test('回すドラッグはカメラを動かさない / 平行移動は向きを変えない', () => {
  const c = base();
  const r = dragRotate(c, 30, 20);
  assert.deepEqual([r.x, r.y, r.z], [c.x, c.y, c.z]);
  const p = dragPan(c, 30, 20, 3);
  assert.deepEqual([p.yawDeg, p.pitchDeg, p.rollDeg], [c.yawDeg, c.pitchDeg, c.rollDeg]);
});

test('前後移動は視線方向へ動く（近づくと物が大きく写る）', () => {
  const c = base();
  const p = [0, 0, 1.0];
  const before = projectPoint(c, 0.5, 0, 1.0).u - projectPoint(c, -0.5, 0, 1.0).u;
  const c2 = dolly(c, 0.5);
  const after = projectPoint(c2, 0.5, 0, 1.0).u - projectPoint(c2, -0.5, 0, 1.0).u;
  assert.ok(after > before, '前へ出たら見かけの幅が広がるはず');
});

test('画角は掴めない（ドラッグは fx を変えない）', () => {
  const c = base();
  for (const op of [dragRotate(c, 50, 50), dragPan(c, 50, 50, 3), dolly(c, 1)]) {
    assert.equal(op.fxPx, c.fxPx, '画角がドラッグで動くと solver より悪くなる');
    assert.equal(op.k1, c.k1);
  }
});

test('上下の向きは真上・真下で止まる', () => {
  const c = clampCalib({ ...base(), pitchDeg: 200 });
  assert.equal(c.pitchDeg, MAX_PITCH_DEG);
});

test('レンズは別枠で差し替える', () => {
  const c = setLens(base(), { hfovDeg: 60, k1: 0.18 });
  assert.ok(Math.abs(hfovFromFocalPx(c.fxPx, 640) - 60) < 1e-6);
  assert.equal(c.k1, 0.18);
  assert.equal(c.fyPx, c.fxPx);
});

test('保存の形は cameras[].calib のスキーマで、手動の印が立つ', () => {
  const out = toManualCalib(base(), { lensId: 'lens_1', nowIso: '2026-08-04T18:00:00' });
  for (const k of ['x', 'y', 'z', 'yawDeg', 'pitchDeg', 'rollDeg', 'fxPx', 'fyPx', 'cxPx', 'cyPx', 'k1', 'srcW', 'srcH']) {
    assert.ok(Number.isFinite(out[k]), `${k} が数値でない`);
  }
  assert.equal(out.method, 'manual');
  assert.equal(out.lensId, 'lens_1');
  // ⚠ 自動で解いたときの指標は書かない（手で置いた値に付けると嘘になる）
  assert.ok(!('rmsPx' in out));
  assert.ok(!('accuracyM' in out));
  assert.ok(!('refs' in out));
});

test('床の上での置き直しは高さと向きを変えない', () => {
  const c = base();
  const g = setGroundPos(c, 1.5, -0.5);
  assert.equal(g.x, 1.5);
  assert.equal(g.z, -0.5);
  assert.equal(g.y, c.y);
  assert.equal(g.yawDeg, c.yawDeg);
});

test('基準距離は見ている床までの距離（下を向いていれば有限）', () => {
  const d = refDistance(base());
  assert.ok(d > 0.2 && d < 50, `d=${d}`);
});

test('地平線より上を向いていても基準距離が壊れない', () => {
  const c = clampCalib({ ...base(), pitchDeg: 45 });
  const d = refDistance(c);
  assert.ok(Number.isFinite(d) && d > 0, '床と交わらない向きでも既定へ落ちること');
});

test('状態の表示は 3 行', () => {
  const lines = summaryLines(base());
  assert.equal(lines.length, 3);
  assert.ok(lines[2].includes('レンズ'), '画角はここでは動かせないと明示する');
});

test('カメラ軸は正規直交', () => {
  const { right, up, fwd } = cameraBasis(clampCalib({ ...base(), rollDeg: 7 }));
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  for (const v of [right, up, fwd]) assert.ok(Math.abs(Math.hypot(...v) - 1) < 1e-9);
  assert.ok(Math.abs(dot(right, up)) < 1e-9);
  assert.ok(Math.abs(dot(right, fwd)) < 1e-9);
});
