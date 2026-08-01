// run-model.js の契約を固定する。
//
// ⚠ 期待値は **C# の ShowRunReachTests / tools/analyze-xp-log.py と同じ値をハードコード**してある。
//    3 者が同じ式を持つ設計なので、片方だけ直すとここが落ちる（沈黙して食い違うのを防ぐ）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  RUN_DEFAULT, runConfig, totalLaps, courseOrder,
  isSegmentReachable, isReturnSegment, lapHeadings, returnTakeTooLate,
} from './run-model.js';

const ORDER = [0, 1, 2];

test('到達可能な区間 — 3 周 + 帰りの A', () => {
  // 通常の周はすべて到達可能
  assert.equal(isSegmentReachable(1, 0, 3, ORDER), true);
  assert.equal(isSegmentReachable(3, 1, 3, ORDER), true);
  assert.equal(isSegmentReachable(3, 2, 3, ORDER), true);
  // 帰りの A（lap = totalLaps + 1 の order[0]）だけは到達可能
  assert.equal(isSegmentReachable(4, 0, 3, ORDER), true);
  // 帰りの B・C は踏まない（体験は A で終わる）
  assert.equal(isSegmentReachable(4, 1, 3, ORDER), false);
  assert.equal(isSegmentReachable(4, 2, 3, ORDER), false);
  // さらに先の周は全部踏まない
  assert.equal(isSegmentReachable(5, 0, 3, ORDER), false);
  // 0 周目・負の周は無い
  assert.equal(isSegmentReachable(0, 0, 3, ORDER), false);
});

test('順路が未著作なら到達可能側に倒す（著作を黙って殺さない）', () => {
  assert.equal(isSegmentReachable(4, 2, 3, []), true);
  assert.equal(isSegmentReachable(4, 2, 3, null), true);
});

test('order[0] が 0 でない順路でも帰りの区間は order[0]', () => {
  const order = [2, 0, 1];
  assert.equal(isSegmentReachable(4, 2, 3, order), true);
  assert.equal(isSegmentReachable(4, 0, 3, order), false);
});

test('totalLaps が不正なら既定 3 で判定する', () => {
  assert.equal(isSegmentReachable(4, 0, 0, ORDER), true);
  assert.equal(isSegmentReachable(5, 0, 0, ORDER), false);
});

test('帰りの区間の判定', () => {
  assert.equal(isReturnSegment(4, 0, 3, ORDER), true);
  assert.equal(isReturnSegment(3, 0, 3, ORDER), false);
  assert.equal(isReturnSegment(4, 1, 3, ORDER), false);
});

test('リボンの見出しは 3 周 + もどり', () => {
  const h = lapHeadings(3);
  assert.deepEqual(h.map((x) => x.label), ['1周目', '2周目', '3周目', 'もどり']);
  assert.deepEqual(h.map((x) => x.lap), [1, 2, 3, 4]);
  assert.equal(h[3].isReturn, true);
  assert.equal(h[0].isReturn, false);
});

test('既定値は C# の ShowRunDefaults と同じ', () => {
  assert.equal(RUN_DEFAULT.totalLaps, 3);
  assert.equal(RUN_DEFAULT.endGraceSec, 3);
  assert.equal(RUN_DEFAULT.endHoldMaxSec, 60);
  assert.equal(RUN_DEFAULT.hardLimitSec, 300);
});

test('show.json の欠落は既定で埋める', () => {
  assert.equal(totalLaps({}), 3);
  assert.equal(totalLaps({ run: { totalLaps: 4 } }), 4);
  assert.equal(totalLaps({ run: { totalLaps: 0 } }), 3);
  assert.equal(runConfig({}).endGraceSec, 3);
  assert.equal(runConfig({ run: { endGraceSec: 5 } }).endGraceSec, 5);
});

test('順路の読み出し', () => {
  assert.deepEqual(courseOrder({ layout: { course: { order: [0, 1, 2] } } }), [0, 1, 2]);
  assert.deepEqual(courseOrder({}), []);
});

test('帰りの区間で grace を超える開始オフセットは実機で出ない', () => {
  const cfg = { endGraceSec: 3 };
  assert.equal(returnTakeTooLate(0, cfg), false);
  assert.equal(returnTakeTooLate(3, cfg), false);
  assert.equal(returnTakeTooLate(3.1, cfg), true);
  // 既定でも同じ
  assert.equal(returnTakeTooLate(5, {}), true);
});
