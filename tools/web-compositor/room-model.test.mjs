// room-model.js の純関数を node:test で固定する（DOM 非依存）。
//   実行: node --test tools/web-compositor/*.test.mjs
//
// ここで守っているのは 3 つ:
//   ① 既定値が Unity（ShowRoomDef / ShowRoomWallDef / ShowRoomBoxDef / ShowRoomLightDef）と一致すること
//      —— 片方だけ変えると、卓で作った部屋と実機が組む部屋が静かに食い違う
//   ② present-flag の AND 規約（flag=false のとき入れ子キーを出さない）
//      —— 破ると端末キャッシュ往復で幽霊の部屋・幽霊の照明が湧く（timeline で実際に起きた事故と同型）
//   ③ footprint が ShowRoomProxy.WallBox / PropBox と同じ軸の取り方であること
//      —— ズレると「卓で見た壁」と「実機で人形を隠す壁」が別物になる

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ROOM_DEFAULT, WALL_DEFAULT, BOX_DEFAULT, defaultLight,
  newRoom, newWall, newBox,
  normalizeRoom, serializeRoom, roomFromLayout, writeRoomToLayout,
  wallsFromLegacyWall, roomOutlineSegments, wallFootprint, boxFootprint, floorRect,
  isWallUsable, isBoxUsable, roomHasData, nextRoomId,
  lightDirCourse, kelvinToRgb, kelvinToCss,
} from './room-model.js';

const near = (a, b, eps = 1e-9) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);

// ---- ① 既定値（Unity C# と一致すること）------------------------------------------

test('既定値は Unity の C# 既定と一致する', () => {
  assert.deepEqual(ROOM_DEFAULT, { floorY: 0, floorW: 1.8, floorD: 1.8 });
  assert.deepEqual(WALL_DEFAULT, { h: 1.0, thick: 0.04 });
  assert.deepEqual(BOX_DEFAULT, { y: 0, w: 0.5, d: 0.5, h: 0.7, yawDeg: 0 });
  assert.deepEqual(defaultLight(), {
    yawDeg: 30, pitchDeg: 55, tempK: 4000, intensity: 1,
    ambient: 0.35, shadowDensity: 0.55, shadowSoftM: 0.12,
  });
});

test('newRoom は床だけの空部屋（照明は未著作）', () => {
  const r = newRoom();
  assert.equal(r.floorW, 1.8);
  assert.equal(r.floorD, 1.8);
  assert.deepEqual(r.walls, []);
  assert.deepEqual(r.props, []);
  assert.equal(r.hasLight, false, '既定で照明を著作済みにすると、触っていない値が実機へ効いてしまう');
  assert.ok(roomHasData(r), '壁ゼロでも床が正なら部屋として成立する（影は落ちる）');
});

test('newWall / newBox は寸法の既定を持つ', () => {
  const w = newWall(-0.5, 0.5, 0.5, 0.5);
  assert.equal(w.h, 1.0);
  assert.equal(w.thick, 0.04);
  const b = newBox(0, 0, 0.6, 0.4);
  assert.equal(b.h, 0.7);
  assert.equal(b.y, 0, 'y は床からの底面高さ。既定は床置き');
  assert.equal(b.yawDeg, 0);
});

// ---- ② 読み書きと present-flag ---------------------------------------------------

test('normalizeRoom は欠けた値を既定で埋め、壊れた要素を落とす', () => {
  const r = normalizeRoom({
    floorW: 2.2,
    walls: [
      { x1: 0, z1: 0, x2: 1, z2: 0 },                 // h / thick 欠け → 既定
      { x1: 'x', z1: 0, x2: 1, z2: 0 },               // 座標が数値でない → 捨てる
      null,
    ],
    props: [{ x: 0.2, z: -0.1 }],                     // 寸法欠け → 既定
  });
  assert.equal(r.floorW, 2.2);
  assert.equal(r.floorD, 1.8, '欠けた軸は既定');
  assert.equal(r.walls.length, 1);
  assert.equal(r.walls[0].h, 1.0);
  assert.equal(r.walls[0].thick, 0.04);
  assert.equal(r.props.length, 1);
  assert.deepEqual(
    [r.props[0].w, r.props[0].d, r.props[0].h],
    [0.5, 0.5, 0.7]);
});

test('normalizeRoom は id の欠け・重複を一意化する（選択・削除のキーになる）', () => {
  const r = normalizeRoom({
    walls: [
      { id: 'w1', x1: 0, z1: 0, x2: 1, z2: 0 },
      { id: 'w1', x1: 0, z1: 1, x2: 1, z2: 1 },   // 重複
      { x1: 0, z1: 2, x2: 1, z2: 2 },             // 欠け
    ],
  });
  assert.deepEqual(r.walls.map((w) => w.id), ['w1', 'w2', 'w3']);
});

test('照明の present-flag は宣言 bool ∧ 実体の AND', () => {
  assert.equal(normalizeRoom({ hasLight: true }).hasLight, false, '実体が無ければ未著作');
  assert.equal(normalizeRoom({ light: { yawDeg: 10 } }).hasLight, false, '宣言が無ければ未著作');
  const on = normalizeRoom({ hasLight: true, light: { yawDeg: 10 } });
  assert.equal(on.hasLight, true);
  assert.equal(on.light.yawDeg, 10);
  assert.equal(on.light.tempK, 4000, '書かれていない項目は既定で埋まる');
});

test('serializeRoom は hasLight=false のとき light キー自体を出さない', () => {
  const out = serializeRoom(newRoom());
  assert.equal(out.hasLight, false);
  assert.equal('light' in out, false, 'flag=false で入れ子を書くと幽霊の照明が湧く');
});

test('serializeRoom は退化した壁・箱を書かない（Unity が黙って捨てるものを残さない）', () => {
  const r = newRoom({
    walls: [
      newWall(0, 0, 1, 0),                    // ok
      newWall(0, 0, 0, 0),                    // 長さ 0
      newWall(0, 0, 1, 0, { h: 0 }),          // 高さ 0
    ],
    props: [
      newBox(0, 0, 0.6, 0.4),                 // ok
      newBox(0, 0, 0, 0.4),                   // 幅 0
      newBox(0, 0, 0.6, 0.4, { h: 0 }),       // 高さ 0
    ],
  });
  const out = serializeRoom(r);
  assert.equal(out.walls.length, 1);
  assert.equal(out.props.length, 1);
});

test('normalize → serialize の往復で値が保たれる', () => {
  const raw = {
    floorY: 0, floorW: 1.8, floorD: 1.8,
    walls: [{ id: 'w1', x1: -0.5, z1: 0.5, x2: 0.5, z2: 0.5, h: 1.0, thick: 0.04 }],
    props: [{ id: 'p1', x: 0, z: 0, y: 0, w: 0.6, d: 0.4, h: 0.7, yawDeg: 0 }],
    light: { yawDeg: 30, pitchDeg: 55, tempK: 4000, intensity: 1, ambient: 0.35, shadowDensity: 0.55, shadowSoftM: 0.12 },
    hasLight: true,
  };
  assert.deepEqual(serializeRoom(normalizeRoom(raw)), raw);
});

test('roomFromLayout は AND 規約で判定し、未著作なら layout.floor を種にする', () => {
  assert.equal(roomFromLayout({ room: { floorW: 2, floorD: 2 } }).hasRoom, false, '宣言なし');
  assert.equal(roomFromLayout({ hasRoom: true }).hasRoom, false, '実体なし');
  assert.equal(
    roomFromLayout({ hasRoom: true, room: { floorW: 0.02, floorD: 2 } }).hasRoom, false,
    '床が退化していれば Unity が丸ごと無視するので、卓も未著作として扱う');

  const seeded = roomFromLayout({ floor: { w: 2.4, d: 2.0 } });
  assert.equal(seeded.hasRoom, false);
  assert.equal(seeded.room.floorW, 2.4, '部屋を作り始めた瞬間に床が 1.8m へ化けない');
  assert.equal(seeded.room.floorD, 2.0);
});

test('writeRoomToLayout は flag を常に書き、false なら room キーを消す', () => {
  const lay = { rev: 1, room: { floorW: 1.8 }, hasRoom: true };
  writeRoomToLayout(lay, newRoom(), false);
  assert.equal(lay.hasRoom, false);
  assert.equal('room' in lay, false);

  writeRoomToLayout(lay, newRoom({ walls: [newWall(0, 0, 1, 0)] }), true);
  assert.equal(lay.hasRoom, true);
  assert.equal(lay.room.walls.length, 1);

  // 床が退化していたら true で頼まれても書かない（Unity が読まないデータを残さない）
  writeRoomToLayout(lay, newRoom({ floorW: 0.01 }), true);
  assert.equal(lay.hasRoom, false);
  assert.equal('room' in lay, false);
});

// ---- 旧 L 字壁の取り込み ------------------------------------------------------------

test('wallsFromLegacyWall は L 字 3 点から壁 2 本を作る', () => {
  const walls = wallsFromLegacyWall({ corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] });
  assert.equal(walls.length, 2);
  // 西の腕: endZ → corner
  assert.deepEqual([walls[0].x1, walls[0].z1, walls[0].x2, walls[0].z2], [-0.5, -0.5, -0.5, 0.5]);
  // 北の腕: corner → endX
  assert.deepEqual([walls[1].x1, walls[1].z1, walls[1].x2, walls[1].z2], [-0.5, 0.5, 0.5, 0.5]);
  assert.deepEqual(walls.map((w) => w.id), ['w1', 'w2']);
  assert.equal(walls[0].h, 1.0);
});

test('wallsFromLegacyWall は壊れた / 退化した入力で空を返す', () => {
  assert.deepEqual(wallsFromLegacyWall(null), []);
  assert.deepEqual(wallsFromLegacyWall({ corner: [0, 0] }), [], '腕が無ければ壁も無い');
  assert.deepEqual(
    wallsFromLegacyWall({ corner: [0, 0], endX: [0, 0], endZ: [0, 0] }), [],
    '長さ 0 の腕は壁にしない');
});

// ---- ③ footprint（ShowRoomProxy と同じ軸）------------------------------------------

test('壁の footprint は線分方向に長さ・直交方向に厚みを持つ', () => {
  // 東西に伸びる壁（+X 方向）。厚みは (-dz, dx)/len = (0, +1) 方向 = +Z 側。
  const fp = wallFootprint({ x1: -0.5, z1: 0.5, x2: 0.5, z2: 0.5, h: 1, thick: 0.04 });
  assert.equal(fp.length, 4);
  const xs = fp.map((p) => p.x), zs = fp.map((p) => p.z);
  near(Math.min(...xs), -0.5); near(Math.max(...xs), 0.5);
  near(Math.min(...zs), 0.48); near(Math.max(...zs), 0.52);
});

test('壁の厚みは最低 5mm 確保する（潰れた板は深度を書かずオクルーダにならない）', () => {
  const fp = wallFootprint({ x1: 0, z1: 0, x2: 1, z2: 0, h: 1, thick: 0 });
  const zs = fp.map((p) => p.z);
  near(Math.max(...zs) - Math.min(...zs), 0.005);
});

test('箱の footprint は yawDeg で回る（Unity の Euler Y と同じ向き）', () => {
  // yaw=90: ローカル +X（幅 w=0.6）→ course (0,-1) = Z 方向 / ローカル +Z（奥行 d=0.4）→ (+1,0) = X 方向
  const fp = boxFootprint({ x: 0, z: 0, w: 0.6, d: 0.4, h: 0.7, yawDeg: 90 });
  const xs = fp.map((p) => p.x), zs = fp.map((p) => p.z);
  near(Math.max(...xs) - Math.min(...xs), 0.4, 1e-9);
  near(Math.max(...zs) - Math.min(...zs), 0.6, 1e-9);
});

test('floorRect は course 原点中心（タイル塗りと同じ置き方）', () => {
  assert.deepEqual(floorRect(newRoom()), { xLo: -0.9, xHi: 0.9, zLo: -0.9, zHi: 0.9 });
});

test('roomOutlineSegments は床 → 壁 → 箱の順で、退化したものを含まない', () => {
  const r = newRoom({
    walls: [newWall(-0.5, 0.5, 0.5, 0.5), newWall(0, 0, 0, 0)],
    props: [newBox(0.2, -0.2, 0.6, 0.4), newBox(0, 0, 0.6, 0.4, { h: 0 })],
  });
  const segs = roomOutlineSegments(r);
  assert.equal(segs.length, 4 * 3, '床 1 + 壁 1 + 箱 1 の矩形 = 12 線分');
  assert.deepEqual([...new Set(segs.map((s) => s.kind))], ['floor', 'wall', 'prop']);
});

test('roomOutlineSegments は床が退化した部屋で床を描かない', () => {
  const segs = roomOutlineSegments(newRoom({ floorW: 0.01, floorD: 0.01 }));
  assert.deepEqual(segs, []);
});

// ---- 退化の判定（Unity の IsUsable と同じしきい値）------------------------------------

test('isWallUsable / isBoxUsable は Unity の IsUsable と同じしきい値', () => {
  assert.equal(isWallUsable(newWall(0, 0, 0.02, 0)), true);
  assert.equal(isWallUsable(newWall(0, 0, 0.005, 0)), false, '長さ 1cm 以下は無効');
  assert.equal(isWallUsable(newWall(0, 0, 1, 0, { h: 0.005 })), false, '高さ 1cm 以下は無効');
  assert.equal(isBoxUsable(newBox(0, 0, 0.02, 0.02)), true);
  assert.equal(isBoxUsable(newBox(0, 0, 0.005, 0.5)), false);
});

test('nextRoomId は既存 id と衝突しない（削除して番号が飛んでも安全）', () => {
  assert.equal(nextRoomId([{ id: 'w1' }, { id: 'w3' }], 'w'), 'w2');
  assert.equal(nextRoomId([], 'p'), 'p1');
  assert.equal(nextRoomId(null, 'w'), 'w1');
});

// ---- 照明 --------------------------------------------------------------------------

test('lightDirCourse は「光が来る向き」（Unity CourseLightDirToWorld の course 部分）', () => {
  const north = lightDirCourse(0, 0);
  near(north.x, 0); near(north.y, 0); near(north.z, 1, 1e-12);
  const east = lightDirCourse(90, 0);
  near(east.x, 1, 1e-12); near(east.z, 0, 1e-15);
  const up = lightDirCourse(30, 90);
  near(up.y, 1, 1e-12);
  // 仰角は ±90 に畳む（真上を超えて裏返らない）
  near(lightDirCourse(0, 200).y, 1, 1e-12);
});

test('kelvinToRgb は暖色 → 白 → 寒色へ単調に変わる', () => {
  assert.deepEqual(kelvinToRgb(2000), { r: 255, g: 137, b: 14 });
  assert.deepEqual(kelvinToRgb(4000), { r: 255, g: 206, b: 166 });
  assert.deepEqual(kelvinToRgb(6500), { r: 255, g: 254, b: 250 });
  assert.deepEqual(kelvinToRgb(8000), { r: 221, g: 230, b: 255 });
  const warm = kelvinToRgb(2000), cool = kelvinToRgb(8000);
  assert.ok(warm.r > warm.b, '低色温度は赤寄り');
  assert.ok(cool.b > cool.r, '高色温度は青寄り');
});

test('kelvinToRgb は壊れた入力・範囲外でも 0..255 に収まる', () => {
  for (const v of [NaN, undefined, null, -1, 1e9]) {
    const c = kelvinToRgb(v);
    for (const ch of ['r', 'g', 'b']) {
      assert.ok(Number.isInteger(c[ch]) && c[ch] >= 0 && c[ch] <= 255, `${v} → ${JSON.stringify(c)}`);
    }
  }
  assert.equal(kelvinToCss(6500), 'rgb(255, 254, 250)');
});

// ---- 色温度は Unity と同じ値を出すこと ----------------------------------------
//   ⚠ C# 側 CgProjectionTests.Kelvin_MatchesConsoleFormula と**同一の期待値**。
//   卓のスウォッチと実機の人形の色が食い違うと、著作者は何を信じればいいのか分からなくなる。
//   片方だけ直すと沈黙して食い違うので、変えるときは必ず両方を直すこと。

test('kelvinToRgb は Unity と同じ値を出す（4000K）', () => {
  const c = kelvinToRgb(4000);
  assert.ok(Math.abs(c.r - 255) < 0.5, `R=${c.r}`);
  assert.ok(Math.abs(c.g - 205.8) < 0.5, `G=${c.g}`);
  assert.ok(Math.abs(c.b - 166.1) < 0.5, `B=${c.b}`);
});

test('低い色温度ほど赤が青より強い', () => {
  const warm = kelvinToRgb(2500), cool = kelvinToRgb(7500);
  assert.ok(warm.r / Math.max(1, warm.b) > cool.r / Math.max(1, cool.b));
});
