// 色統計マッチング（cue へ焼く 6 つの数）の純関数を固定する。
//   実行: node --test tools/web-compositor/
//
// 実機（ScreenComposite の _OverlayGain / _OverlayOffset）は out = src*gain + offset を掛けるだけ。
// ここが狂うと差し替え素材の色が黙って壊れる（しかも実機でしか見えない）。

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  GAIN_MAX, GAIN_MIN, IDENTITY, OFFSET_LIMIT,
  isIdentity, solveMatch, statsFromImageData,
} from './color-match.js';

const stats = (mean, sd) => ({ mean, sd });
const apply = (m, c, v) => v * m.gain[c] + m.offset[c];

test('同じ統計なら恒等', () => {
  const m = solveMatch(stats([0.4, 0.5, 0.6], [0.2, 0.2, 0.2]), stats([0.4, 0.5, 0.6], [0.2, 0.2, 0.2]));
  assert.ok(isIdentity(m), JSON.stringify(m));
});

test('素材の平均と分散が実写へ移る', () => {
  const live = stats([0.30, 0.40, 0.50], [0.10, 0.10, 0.10]);
  const src = stats([0.60, 0.60, 0.60], [0.20, 0.20, 0.20]);
  const m = solveMatch(live, src);
  for (let c = 0; c < 3; c++) {
    // 素材の平均 → 実写の平均
    assert.ok(Math.abs(apply(m, c, src.mean[c]) - live.mean[c]) < 1e-6);
    // 素材の +1σ → 実写の +1σ
    assert.ok(Math.abs(apply(m, c, src.mean[c] + src.sd[c]) - (live.mean[c] + live.sd[c])) < 1e-6);
  }
});

test('向きが逆でないこと（素材を実写へ寄せる）', () => {
  // 実写が暗く、素材が明るい → 素材を暗くする方向でなければならない
  const live = stats([0.2, 0.2, 0.2], [0.1, 0.1, 0.1]);
  const src = stats([0.8, 0.8, 0.8], [0.1, 0.1, 0.1]);
  const m = solveMatch(live, src);
  assert.ok(apply(m, 0, 0.8) < 0.8, '素材の明るさが下がるべき');
  assert.ok(Math.abs(apply(m, 0, 0.8) - 0.2) < 1e-6);
});

test('強度 0 は恒等、0.5 は中間', () => {
  const live = stats([0.2, 0.2, 0.2], [0.1, 0.1, 0.1]);
  const src = stats([0.8, 0.8, 0.8], [0.2, 0.2, 0.2]);
  assert.ok(isIdentity(solveMatch(live, src, 0)));
  const full = solveMatch(live, src, 1);
  const half = solveMatch(live, src, 0.5);
  assert.ok(Math.abs(half.gain[0] - (1 + (full.gain[0] - 1) * 0.5)) < 1e-9);
  assert.ok(Math.abs(half.offset[0] - full.offset[0] * 0.5) < 1e-9);
});

test('ほぼ単色の素材でも発散しない', () => {
  const live = stats([0.5, 0.5, 0.5], [0.3, 0.3, 0.3]);
  const src = stats([0.5, 0.5, 0.5], [0, 0, 0]);   // σ=0
  const m = solveMatch(live, src);
  for (let c = 0; c < 3; c++) {
    assert.ok(Number.isFinite(m.gain[c]) && m.gain[c] <= GAIN_MAX && m.gain[c] >= GAIN_MIN);
    assert.ok(Math.abs(m.offset[c]) <= OFFSET_LIMIT + 1e-9);
  }
});

test('gain / offset は必ず範囲内へ丸められる', () => {
  const live = stats([1, 1, 1], [1, 1, 1]);
  const src = stats([0, 0, 0], [0.001, 0.001, 0.001]);
  const m = solveMatch(live, src);
  for (let c = 0; c < 3; c++) {
    assert.ok(m.gain[c] <= GAIN_MAX + 1e-9);
    assert.ok(Math.abs(m.offset[c]) <= OFFSET_LIMIT + 1e-9);
  }
});

test('片方が無ければ恒等（黙って壊さない）', () => {
  assert.deepStrictEqual(solveMatch(null, stats([0, 0, 0], [1, 1, 1])), { gain: [...IDENTITY.gain], offset: [...IDENTITY.offset] });
  assert.deepStrictEqual(solveMatch(stats([0, 0, 0], [1, 1, 1]), null), { gain: [...IDENTITY.gain], offset: [...IDENTITY.offset] });
});

test('ImageData の統計（透過画素は数えない）', () => {
  // 2 画素だけ不透明（0 と 255）、残りは alpha=0
  const data = new Uint8ClampedArray([
    0, 0, 0, 255,
    255, 255, 255, 255,
    128, 64, 32, 0,     // 透過 → 無視
  ]);
  const s = statsFromImageData(data);
  assert.equal(s.count, 2);
  for (let c = 0; c < 3; c++) {
    assert.ok(Math.abs(s.mean[c] - 0.5) < 1e-6);
    assert.ok(Math.abs(s.sd[c] - 0.5) < 1e-6);
  }
});

test('全部透過なら count 0（呼び出し側が恒等へ倒せる）', () => {
  const s = statsFromImageData(new Uint8ClampedArray([10, 20, 30, 0]));
  assert.equal(s.count, 0);
});
