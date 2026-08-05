// 色統計マッチング（cue へ焼く 6 つの数）の純関数を固定する。
//   実行: node --test tools/web-compositor/
//
// 実機（ScreenComposite の _OverlayGain / _OverlayOffset）は out = src*gain + offset を掛けるだけ。
// ここが狂うと差し替え素材の色が黙って壊れる（しかも実機でしか見えない）。

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  GAIN_MAX, GAIN_MIN, IDENTITY, OFFSET_LIMIT,
  averageStats, isIdentity, solveMatch, statsFromImageData, maskRectForSource,
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

// ---- averageStats（動画を尺全体からサンプルして畳む）------------------------

const stat = (m, sd, count = 100) => ({ mean: [m, m, m], sd: [sd, sd, sd], count });

test('プールした sd は群平均のばらつきを含む（単純平均ではない）', () => {
  // 暗い前半（平均 0.2）と明るい後半（平均 0.6）。どちらも群内 sd は 0.1。
  const a = averageStats([stat(0.2, 0.1), stat(0.6, 0.1)]);
  assert.ok(Math.abs(a.mean[0] - 0.4) < 1e-9);
  // 全体分散 = 群内分散の平均 0.01 + 群平均の分散 0.04 = 0.05
  assert.ok(Math.abs(a.sd[0] - Math.sqrt(0.05)) < 1e-9);
  // 単純平均（0.1）で済ませると、明暗の振れ幅がまるごと落ちる
  assert.ok(a.sd[0] > 0.2);
  assert.equal(a.count, 200);
});

test('1 フレームだけで解くと gain が倍以上ずれる（尺全体で取る理由）', () => {
  const live = stat(0.4, Math.sqrt(0.05));
  const whole = averageStats([stat(0.2, 0.1), stat(0.6, 0.1)]);
  const oneFrame = stat(0.2, 0.1);          // 暗い頭のフレームだけを見た場合
  assert.ok(Math.abs(solveMatch(live, whole).gain[0] - 1) < 1e-6);
  assert.ok(solveMatch(live, oneFrame).gain[0] > 2);
});

test('空・count 0 の群は無視する', () => {
  assert.equal(averageStats([]).count, 0);
  assert.equal(averageStats(null).count, 0);
  const a = averageStats([stat(0.5, 0.1), { mean: [0, 0, 0], sd: [0, 0, 0], count: 0 }]);
  assert.ok(Math.abs(a.mean[0] - 0.5) < 1e-9);
  assert.ok(Math.abs(a.sd[0] - 0.1) < 1e-9);
});

// ---- マスクを素材と同じ格子で読むための矩形 ----------------------------------
//   実機は overlay を contain-fit で枠へ収め、**マスクだけは生 uv で読む**。
//   統計をマスクで絞るときは、その対応を逆にたどらないと別の場所を見ることになる。

test('maskRectForSource: 4:3 の素材は 16:9 マスクの中央 75% に対応する', () => {
  const r = maskRectForSource(640, 480, 640, 360);
  assert.deepStrictEqual(r, { sx: 80, sy: 0, sw: 480, sh: 360 });
});

test('maskRectForSource: 素材と枠のアスペクトが同じなら全面', () => {
  assert.deepStrictEqual(maskRectForSource(1920, 1080, 640, 360),
    { sx: 0, sy: 0, sw: 640, sh: 360 });
});

test('maskRectForSource: 素材の方が横長なら上下に余白が付く', () => {
  const r = maskRectForSource(1000, 250, 640, 360);   // 4:1
  assert.equal(r.sw, 640);
  assert.equal(r.sh, 160);
  assert.equal(r.sx, 0);
  assert.equal(r.sy, 100);
});

test('maskRectForSource: 寸法が取れないときは枠いっぱいに倒す', () => {
  assert.deepStrictEqual(maskRectForSource(0, 0, 640, 360),
    { sx: 0, sy: 0, sw: 640, sh: 360 });
});
