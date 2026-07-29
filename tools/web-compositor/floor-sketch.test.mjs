// floor-sketch.js を node:test で固定する。
//   実行: node --test tools/web-compositor/floor-sketch.test.mjs
//
// 描画そのものの見た目は node では確かめられないので、ここで守るのは 2 つだけ:
//   (A) 座標変換と吸着（地図で指した場所が course 座標として正しいか）
//   (B) 描画が**落ちずに、要素ごとの図形を実際に出している**こと
//       （canvas を偽物に差し替えて呼び出しを記録する。DOM もブラウザも要らない）

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  sketchView, snapToGrid, roomCorners, nearestRoomCorner, hitPoint, drawFloorSketch, floorDims,
} from './floor-sketch.js';

const LAYOUT = {
  floor: { w: 1.8, d: 1.8 },
  grid: { tileM: 0.15, cols: 12, rows: 12, cells: Array(12).fill('111222222222') },
  course: { order: [1, 2] },
  lines: [{ id: 'l1', camera: 1, x1: -0.3, z1: -0.2, x2: 0.3, z2: 0.4, dir: 'both' }],
  hasRoom: true,
  room: {
    floorW: 1.8, floorD: 1.8,
    walls: [{ id: 'w1', x1: -0.4, z1: 0.4, x2: 0.4, z2: 0.4, h: 1, thick: 0.04 }],
    props: [{ id: 'p1', x: 0.6, z: -0.6, w: 0.4, d: 0.4, h: 0.7, yawDeg: 0 }],
  },
};

// ---- (A) 変換・吸着 ------------------------------------------------------------

test('北が上・東が右で、toPx と toCourse が互いの逆になる', () => {
  const view = sketchView(LAYOUT, { width: 300, height: 300 }, 10);
  const north = view.toPx(0, 0.9);
  const south = view.toPx(0, -0.9);
  assert.ok(north.py < south.py, '北（z+）が上');
  const east = view.toPx(0.9, 0);
  const west = view.toPx(-0.9, 0);
  assert.ok(east.px > west.px, '東（x+）が右');

  const back = view.toCourse(north.px, north.py);
  assert.ok(Math.abs(back.x) < 1e-9 && Math.abs(back.z - 0.9) < 1e-9);
});

test('吸着はタイルの角（格子交点）へ寄せる', () => {
  assert.deepEqual(snapToGrid(LAYOUT, 0.31, -0.44), { x: 0.3, z: -0.45 });
  // 格子が無いレイアウトでも落ちない（5cm 刻み）
  assert.deepEqual(snapToGrid({ floor: { w: 2, d: 2 } }, 0.111, 0.089), { x: 0.1, z: 0.1 });
});

test('部屋のプロキシから壁の端と箱の角が出る（未著作なら空）', () => {
  const cs = roomCorners(LAYOUT);
  assert.equal(cs.length, 2 + 4);
  assert.ok(cs.some((c) => c.x === -0.4 && c.z === 0.4));
  assert.deepEqual(roomCorners({ floor: { w: 1.8, d: 1.8 } }), []);
  // 近い角へ吸着する / 遠ければ吸着しない
  assert.deepEqual(nearestRoomCorner(LAYOUT, -0.38, 0.42, 0.12), { x: -0.4, z: 0.4 });
  assert.equal(nearestRoomCorner(LAYOUT, 0, 0, 0.12), null);
});

test('地図上のクリックが最も近い点を拾う（半径外は -1）', () => {
  const view = sketchView(LAYOUT, { width: 300, height: 300 }, 10);
  const pts = [{ x: -0.6, z: 0.6 }, { x: 0.6, z: -0.6 }];
  const q = view.toPx(0.6, -0.6);
  assert.equal(hitPoint(pts, view, q.px + 3, q.py - 2, 14), 1);
  assert.equal(hitPoint(pts, view, q.px + 60, q.py, 14), -1);
});

test('床の大きさは未著作なら 1.8m 四方', () => {
  assert.deepEqual(floorDims(null), { w: 1.8, d: 1.8 });
  assert.deepEqual(floorDims({ floor: { w: 2.4, d: 3 } }), { w: 2.4, d: 3 });
});

// ---- (B) 描画（偽 canvas で呼び出しを記録する）----------------------------------

function fakeCtx(width = 300, height = 300) {
  const calls = [];
  const rec = (name) => (...args) => { calls.push({ name, args }); };
  const ctx = {
    canvas: { width, height },
    calls,
    clearRect: rec('clearRect'), fillRect: rec('fillRect'), strokeRect: rec('strokeRect'),
    beginPath: rec('beginPath'), closePath: rec('closePath'), moveTo: rec('moveTo'),
    lineTo: rec('lineTo'), arc: rec('arc'), stroke: rec('stroke'), fill: rec('fill'),
    setLineDash: rec('setLineDash'), fillText: rec('fillText'),
    save: rec('save'), restore: rec('restore'), translate: rec('translate'), rotate: rec('rotate'),
  };
  return ctx;
}

test('略図は落ちずに、床・壁・ライン・カメラ・点をすべて描く', () => {
  const ctx = fakeCtx();
  const view = sketchView(LAYOUT, ctx.canvas, 10);
  drawFloorSketch(ctx, LAYOUT, view, {
    points: [{ x: -0.6, z: 0.6, done: true }],
    marks: [{ x: 0.3, z: -0.45, key: '0.300,-0.450', kind: 'mark' }],
    activeKey: '0.300,-0.450',
    cameras: [{ index: 0, id: 'A', pose: { x: -0.9, z: 0.9, yawDeg: 135 } }],
  });
  const names = ctx.calls.map((c) => c.name);
  assert.ok(names.includes('strokeRect'), '床の外周');
  assert.ok(names.filter((n) => n === 'arc').length >= 3, 'カメラ・候補・打った点');
  assert.ok(names.includes('setLineDash'), '通過ライン');
  assert.ok(names.includes('fillText'), 'カメラ名と点の番号');
  // 打った点の番号は 1 始まりで振る
  assert.ok(ctx.calls.some((c) => c.name === 'fillText' && c.args[0] === '1'));
});

test('レイアウトが空でも落ちない（現場で layout が未著作のことがある）', () => {
  const ctx = fakeCtx();
  const view = sketchView(null, ctx.canvas, 10);
  drawFloorSketch(ctx, null, view, {});
  assert.ok(ctx.calls.length > 0);
});
