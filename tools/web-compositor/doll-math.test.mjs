// 人形（WebGL）とワイヤー（2D）が**同じ画素**に落ちることを固定する。
//
// これが無いと「ワイヤーは実物に合っているのに人形だけずれる」が起き、しかも作業者には
// 原因がカメラなのか人形なのか切り分けられない。
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  viewMatrix, viewRotation, toCamera, camToPixel, lightDirection, projectToPlane,
  shadowMatrix, kelvinLinear, SHADOW_MIN_LIGHT_UP,
} from './doll-math.js';
import { projectPoint } from './calib.js';
import { initialCalib } from './align-model.js';
import { kelvinToRgb } from './room-model.js';

const CAM = { pose: { x: -1.05, y: 1.35, z: 1.05, yawDeg: 133, pitchDeg: -14, hfovDeg: 77.4 } };
const base = (over = {}) => ({ ...initialCalib(CAM, 640, 480), ...over });

const PTS = [
  [0, 0, 0], [0.4, 0, 0.3], [-0.6, 0.2, -0.4], [0.2, 0.41, 0.9], [1.1, 0.1, -1.2],
];

for (const k1 of [0, 0.18, -0.12]) {
  test(`カメラ経路と projectPoint が一致する (k1=${k1})`, () => {
    const c = base({ k1 });
    for (const p of PTS) {
      const a = projectPoint(c, p[0], p[1], p[2]);
      const b = camToPixel(c, toCamera(c, p));
      if (!a) { assert.equal(b, null); continue; }
      assert.ok(b, `${p} が camToPixel で落ちた`);
      assert.ok(Math.abs(a.u - b.u) < 1e-6, `u ${a.u} vs ${b.u}`);
      assert.ok(Math.abs(a.v - b.v) < 1e-6, `v ${a.v} vs ${b.v}`);
    }
  });
}

test('ビュー行列は toCamera と同じ変換（列優先で WebGL へ渡せる形）', () => {
  const c = base();
  const m = viewMatrix(c);
  for (const p of PTS) {
    const want = toCamera(c, p);
    // column-major: out[r] = sum_col m[col*4+r] * v[col] + m[12+r]
    const got = [0, 1, 2].map((r) =>
      m[0 * 4 + r] * p[0] + m[1 * 4 + r] * p[1] + m[2 * 4 + r] * p[2] + m[12 + r]);
    for (let i = 0; i < 3; i++) assert.ok(Math.abs(want[i] - got[i]) < 1e-5, `${i}: ${want[i]} vs ${got[i]}`);
  }
});

test('カメラ軸は正規直交（回転成分が正しい）', () => {
  const Rt = viewRotation(base({ rollDeg: 9 }));
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  for (const r of Rt) assert.ok(Math.abs(Math.hypot(...r) - 1) < 1e-9);
  assert.ok(Math.abs(dot(Rt[0], Rt[1])) < 1e-9);
  assert.ok(Math.abs(dot(Rt[1], Rt[2])) < 1e-9);
});

// --- 光と影 ---------------------------------------------------------------
test('光の向きは上から来る（既定 pitch 55°）', () => {
  const d = lightDirection({ yawDeg: 30, pitchDeg: 55 });
  assert.ok(Math.abs(Math.hypot(...d) - 1) < 1e-9);
  assert.ok(d[1] > 0.8, '上向き成分が正＝光が上から来る');
});

test('真上の光なら影は足元に落ちる', () => {
  const d = lightDirection({ yawDeg: 0, pitchDeg: 90 });
  const p = projectToPlane([0.3, 0.4, -0.2], 0, d);
  assert.ok(Math.abs(p[0] - 0.3) < 1e-9 && Math.abs(p[2] - (-0.2)) < 1e-9);
  assert.equal(p[1], 0);
});

test('斜めの光は影を光と反対側へ伸ばす', () => {
  const d = lightDirection({ yawDeg: 0, pitchDeg: 45 });   // +Z 側の上から
  const p = projectToPlane([0, 1, 0], 0, d);
  assert.ok(p[2] < -0.9, `光が +Z から来るなら影は -Z へ伸びる: ${p[2]}`);
});

test('光が真横以下なら影を出さない（画面いっぱいの黒帯を防ぐ）', () => {
  for (const pitch of [0, -20, 1]) {
    const d = lightDirection({ yawDeg: 0, pitchDeg: pitch });
    if (d[1] > SHADOW_MIN_LIGHT_UP) continue;
    assert.equal(projectToPlane([0, 1, 0], 0, d), null);
    assert.equal(shadowMatrix(0, d), null);
  }
});

test('影の行列は projectToPlane と同じ結果を出す', () => {
  const d = lightDirection({ yawDeg: 30, pitchDeg: 55 });
  const m = shadowMatrix(0, d);
  assert.ok(m);
  for (const p of PTS) {
    const want = projectToPlane(p, 0, d);
    const w = m[3] * p[0] + m[7] * p[1] + m[11] * p[2] + m[15];
    const got = [0, 1, 2].map((r) =>
      (m[0 * 4 + r] * p[0] + m[1 * 4 + r] * p[1] + m[2 * 4 + r] * p[2] + m[12 + r]) / w);
    for (let i = 0; i < 3; i++) assert.ok(Math.abs(want[i] - got[i]) < 1e-6, `${i}: ${want[i]} vs ${got[i]}`);
  }
});

test('色温度は linear へ落ちる（4000K の実値を固定）', () => {
  const c = kelvinLinear(4000, kelvinToRgb);
  // 卓のスウォッチ（sRGB）と同じ近似式から落としたもの。Unity の KelvinToLinearColor と対。
  assert.ok(c[0] > c[1] && c[1] > c[2], '4000K は赤寄り');
  assert.ok(c[0] > 0.95, `r=${c[0]}`);
  assert.ok(c[2] > 0.1 && c[2] < 0.5, `b=${c[2]}`);
});
