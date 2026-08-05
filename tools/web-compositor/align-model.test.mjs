// 手動カメラ合わせの操作を数値で固定する。
//
// ⚠ ドラッグの符号は**理屈で決めない**。「カーソルの下にあった点が、ドラッグ後もカーソルの
//   下に来る」を assert して定義する。ここを勘で書くと上下左右のどれかが必ず逆になり、
//   しかも実際に触るまで気づけない。
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  initialCalib, dolly, cameraBasis, setGroundPos, setLens,
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
  for (const op of [dragRoom(c, '', 'rotate', 50, 0, {}), dragRoom(c, '', 'move', 50, 50, {}), dolly(c, 1)]) {
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

// --- 軸拘束（Blender / CAD 流）-------------------------------------------
import {
  rotateAboutWorldAxis, translateAlongWorldAxis, axisScreenDir, AXES,
} from './align-model.js';

test('ワールド軸の平行移動はその軸だけ動かす', () => {
  const c = base();
  for (const ax of AXES) {
    const m = translateAlongWorldAxis(c, ax, 0.5);
    const d = { x: m.x - c.x, y: m.y - c.y, z: m.z - c.z };
    for (const k of ['x', 'y', 'z']) {
      assert.ok(Math.abs(d[k] - (k === ax ? 0.5 : 0)) < 1e-9, `${ax} で ${k} が動いた`);
    }
    assert.deepEqual([m.yawDeg, m.pitchDeg, m.rollDeg], [c.yawDeg, c.pitchDeg, c.rollDeg]);
  }
});

test('Y 軸まわりの回転は yaw だけを変える', () => {
  const c = { ...base(), rollDeg: 0 };
  const r = rotateAboutWorldAxis(c, 'y', 20);
  assert.ok(Math.abs(r.yawDeg - (c.yawDeg + 20)) < 1e-6, `${r.yawDeg} vs ${c.yawDeg + 20}`);
  assert.ok(Math.abs(r.pitchDeg - c.pitchDeg) < 1e-6);
  assert.deepEqual([r.x, r.y, r.z], [c.x, c.y, c.z], '回転は位置を動かさない');
});

test('ワールド軸まわりの回転は往復する（合成して Euler へ戻せている）', () => {
  const c = base();
  for (const ax of AXES) {
    const back = rotateAboutWorldAxis(rotateAboutWorldAxis(c, ax, 17), ax, -17);
    for (const k of ['yawDeg', 'pitchDeg', 'rollDeg']) {
      assert.ok(Math.abs(back[k] - c[k]) < 1e-4, `${ax}/${k}: ${back[k]} vs ${c[k]}`);
    }
  }
});

test('X / Z 軸まわりの回転は roll を生む（yaw だけでは表せない）', () => {
  const c = { ...base(), rollDeg: 0 };
  const r = rotateAboutWorldAxis(c, 'z', 15);
  assert.ok(Math.abs(r.rollDeg) > 1, 'Z 軸回転が roll に現れること');
});

test('軸の画面方向は 1m あたりの画素数を返す', () => {
  const c = base();
  const sd = axisScreenDir(c, 'x', [0, 0, 0]);
  assert.ok(sd && sd.pxPerM > 1, `pxPerM=${sd && sd.pxPerM}`);
  assert.ok(Math.abs(Math.hypot(...sd.dir) - 1) < 1e-9);
});

// --- 床と壁を掴む（保存されるのはカメラ）-----------------------------------
import { moveRoom, rotateRoom, dragRoom } from './align-model.js';

test('部屋を回しても掴んだ点（pivot）の投影は動かない', () => {
  const c = base();
  for (const ax of AXES) {
    for (const pivot of [[0, 0, 0], [0.6, 0, 1.1], [-0.4, 0, -0.7]]) {
      const a = projectPoint(c, ...pivot);
      const r = rotateRoom(c, ax, 23, pivot);
      const b = projectPoint(r, ...pivot);
      assert.ok(a && b, `${ax} で pivot が写らなくなった`);
      assert.ok(Math.abs(a.u - b.u) < 1e-6 && Math.abs(a.v - b.v) < 1e-6,
        `${ax}/${pivot}: (${a.u},${a.v}) -> (${b.u},${b.v})`);
    }
  }
});

test('部屋を回すと pivot 以外は動く', () => {
  const c = base();
  const a = projectPoint(c, 1.0, 0, 0);
  const b = projectPoint(rotateRoom(c, 'y', 25, [0, 0, 0]), 1.0, 0, 0);
  assert.ok(Math.hypot(b.u - a.u, b.v - a.v) > 5, '回したのに動かないのはおかしい');
});

test('部屋の平行移動は「部屋がその向きへ動いた」ように見える', () => {
  const c = base();
  const p = [0, 0, 0];
  const a = projectPoint(c, ...p);
  // 部屋を +X へ 0.5m 動かす = 部屋の点 p が p+[0.5,0,0] に来たのと同じ絵になる
  const m = moveRoom(c, [0.5, 0, 0]);
  const b = projectPoint(m, ...p);
  const want = projectPoint(c, 0.5, 0, 0);
  assert.ok(Math.abs(b.u - want.u) < 1e-6 && Math.abs(b.v - want.v) < 1e-6,
    `(${b.u},${b.v}) vs (${want.u},${want.v})`);
});

test('部屋を動かしてもカメラの向きは変わらない / 回しても画角は変わらない', () => {
  const c = base();
  const m = moveRoom(c, [0.3, 0.1, -0.2]);
  assert.deepEqual([m.yawDeg, m.pitchDeg, m.rollDeg], [c.yawDeg, c.pitchDeg, c.rollDeg]);
  const r = rotateRoom(c, 'y', 30, [0, 0, 0]);
  assert.equal(r.fxPx, c.fxPx);
  assert.equal(r.k1, c.k1);
});

test('部屋の回転は往復する', () => {
  const c = base();
  const pivot = [0.3, 0, 0.4];
  for (const ax of AXES) {
    const back = rotateRoom(rotateRoom(c, ax, 19, pivot), ax, -19, pivot);
    for (const k of ['x', 'y', 'z', 'yawDeg', 'pitchDeg', 'rollDeg']) {
      assert.ok(Math.abs(back[k] - c[k]) < 1e-4, `${ax}/${k}: ${back[k]} vs ${c[k]}`);
    }
  }
});

test('自由ドラッグ（移動）で掴んだ床の点がカーソルに付いてくる', () => {
  const c = base();
  const pivot = [0, 0, 0];
  const dist = Math.hypot(pivot[0] - c.x, pivot[1] - c.y, pivot[2] - c.z);
  for (const [dx, dy] of [[50, 0], [0, 40], [-30, 25]]) {
    const a = projectPoint(c, ...pivot);
    const b = projectPoint(dragRoom(c, '', 'move', dx, dy, { pivot, dist }), ...pivot);
    assert.ok(Math.abs(b.u - (a.u + dx)) < 6 && Math.abs(b.v - (a.v + dy)) < 6,
      `(${dx},${dy}): (${b.u},${b.v}) vs (${a.u + dx},${a.v + dy})`);
  }
});

test('軸拘束の移動はその軸だけ（部屋の側で見ても 1 軸）', () => {
  const c = base();
  const pivot = [0, 0, 0];
  const sd = axisScreenDir(c, 'x', pivot);
  const m = dragRoom(c, 'x', 'move', sd.dir[0] * 40, sd.dir[1] * 40, { pivot });
  assert.ok(Math.abs(m.x - c.x) > 1e-3);
  assert.ok(Math.abs(m.y - c.y) < 1e-9 && Math.abs(m.z - c.z) < 1e-9);
});

test('自由回転は Y 軸だけ（横ドラッグで床が回る・縦では回らない）', () => {
  const c = base();
  const pivot = [0, 0, 0];
  const spun = dragRoom(c, '', 'rotate', 40, 0, { pivot });
  assert.ok(Math.abs(spun.yawDeg - c.yawDeg) > 1, '横ドラッグで床が回ること');
  const vert = dragRoom(c, '', 'rotate', 0, 40, { pivot });
  assert.deepEqual([vert.x, vert.y, vert.z, vert.yawDeg], [c.x, c.y, c.z, c.yawDeg],
    '縦ドラッグで勝手に傾かないこと');
});

import { isBelowFloor, setField } from './align-model.js';

test('高さは「人が打つとき」だけ詰める / ドラッグでは詰めない', () => {
  const c = base();
  // 数値欄へ打つ場合は現実的な範囲へ
  assert.equal(setField(c, 'y', -1).y, 0.05);
  assert.equal(setField(c, 'y', 99).y, 6.0);
  // 床を傾ける操作では、等価なカメラが一時的に床下へ回り込んでよい
  const low = rotateRoom(c, 'x', 23, [0.6, 0, 1.1]);
  assert.ok(low.y < 0, `詰めてしまうと掴んだ点が動く: y=${low.y}`);
  assert.ok(isBelowFloor(low), 'UI が警告できるように印は立てる');
  assert.ok(!isBelowFloor(c));
});

// ---- 端末の傾きを取り込む ---------------------------------------------------

import {
  readTilt, applyTilt, tiltMismatchDeg, tiltLockAllows, dragRoom as dragRoomLocked,
} from './align-model.js';

const D = 180 / Math.PI;

test('配信アプリの傾きの式が、卓の姿勢表現と厳密につながっている', () => {
  // fixed-cam-streamer の TiltMath はこの式で上下と傾きを出している（U = ワールド上向き）:
  //     pitch = asin(fwd·U) / roll = atan2(right·U, up·U)
  // course 空間の +Y が重力の上向きなので、U·v は v[1] そのもの。
  // ⚠ 片方だけ直すと沈黙して食い違う。streamer 側は TiltMathTest が同じ置き方を固定している。
  for (const [pitch, roll, yaw] of [[-20, 0, 0], [-12, 3.5, 133], [0, -7, -110], [8, 0, 45]]) {
    const c = { ...base(), pitchDeg: pitch, rollDeg: roll, yawDeg: yaw };
    const { right, up, fwd } = cameraBasis(c);
    const p = Math.asin(Math.max(-1, Math.min(1, fwd[1]))) * D;
    const r = Math.atan2(right[1], up[1]) * D;
    assert.ok(Math.abs(p - pitch) < 1e-9, `pitch ${p} != ${pitch}`);
    assert.ok(Math.abs(r - roll) < 1e-9, `roll ${r} != ${roll}`);
  }
});

test('上下と傾きは左右の向きから独立している（この機能が成り立つ理由）', () => {
  // 同じ傾きのまま左右だけ振っても、重力に対する量は 1 ミリ度も動かない。
  // だから方位が分からなくても上下と傾きを先に焼ける。
  const ref = cameraBasis({ ...base(), pitchDeg: -17, rollDeg: 2.5, yawDeg: 0 });
  for (const yaw of [-175, -90, 0, 37, 133, 179]) {
    const b = cameraBasis({ ...base(), pitchDeg: -17, rollDeg: 2.5, yawDeg: yaw });
    assert.ok(Math.abs(b.fwd[1] - ref.fwd[1]) < 1e-12, `yaw=${yaw} で上下が動いた`);
    assert.ok(Math.abs(b.up[1] - ref.up[1]) < 1e-12, `yaw=${yaw} で傾きが動いた`);
    assert.ok(Math.abs(b.right[1] - ref.right[1]) < 1e-12, `yaw=${yaw} で傾きが動いた`);
  }
});

test('傾きを配らない端末は null（黙って 0 度として扱わない）', () => {
  assert.equal(readTilt(null), null);
  assert.equal(readTilt({}), null, '旧ビルド・IP Camera Lite');
  assert.equal(readTilt({ deviceName: 'iPhone', widthPx: 640 }), null);
  assert.equal(readTilt({ tiltState: 'ok', tiltPitchDeg: 'x', tiltRollDeg: 0 }), null);
});

test('使ってよいのは ok のときだけ', () => {
  const ok = readTilt({ tiltState: 'ok', tiltPitchDeg: -18.34, tiltRollDeg: 0.42 });
  assert.deepEqual(ok, { pitchDeg: -18.34, rollDeg: 0.42, state: 'ok', usable: true });
  for (const s of ['moving', 'steep', 'unknown']) {
    const t = readTilt({ tiltState: s, tiltPitchDeg: -1, tiltRollDeg: 0 });
    assert.equal(t.usable, false, `${s} を取り込ませない`);
    assert.equal(t.state, s, '理由は伝える（画面に出すため）');
  }
});

test('取り込むのは上下と傾きだけ — 位置と左右の向きは触らない', () => {
  const c = base();
  const t = { pitchDeg: -18.3, rollDeg: 0.4 };
  const out = applyTilt(c, t);
  assert.equal(out.pitchDeg, -18.3);
  assert.equal(out.rollDeg, 0.4);
  assert.deepEqual([out.x, out.y, out.z, out.yawDeg], [c.x, c.y, c.z, c.yawDeg]);
  assert.deepEqual([out.fxPx, out.k1, out.srcW], [c.fxPx, c.k1, c.srcW], 'レンズも触らない');
});

test('センサとのずれは上下と傾きの大きい方', () => {
  const c = { ...base(), pitchDeg: -12, rollDeg: 0 };
  assert.ok(Math.abs(tiltMismatchDeg(c, { pitchDeg: -12, rollDeg: 0 })) < 1e-9);
  assert.ok(Math.abs(tiltMismatchDeg(c, { pitchDeg: -15, rollDeg: 0 }) - 3) < 1e-9);
  assert.ok(Math.abs(tiltMismatchDeg(c, { pitchDeg: -12, rollDeg: 5 }) - 5) < 1e-9);
  assert.equal(tiltMismatchDeg(null, { pitchDeg: 0, rollDeg: 0 }), null);
});

test('傾きを保つ設定で止まるのは、傾きを変える操作だけ', () => {
  // 平行移動は全部通す（合わせ作業の本体はそちら）。
  for (const a of ['', 'x', 'y', 'z']) assert.ok(tiltLockAllows('move', a), `move/${a}`);
  // 回転は Y 軸（床を回す＝左右の向き）だけ。
  assert.ok(tiltLockAllows('rotate', ''), '自由回転は Y 軸まわりなので通す');
  assert.ok(tiltLockAllows('rotate', 'y'));
  assert.ok(!tiltLockAllows('rotate', 'x'));
  assert.ok(!tiltLockAllows('rotate', 'z'));
});

test('傾きを保つ設定では、傾けるドラッグが姿勢を動かさない', () => {
  const lockTilt = { pitchDeg: -18.3, rollDeg: 0.4 };
  const c = applyTilt(base(), lockTilt);
  const pivot = [0.4, 0, 1.2];

  // X 軸で傾けようとしても、何も起きない（位置だけ動いて意味不明になるより無視が正しい）。
  // ⚠ 横ドラッグで試す。X 軸は画面でほぼ水平なので、縦ドラッグは**ロックが無くても**効かない
  //   （それで試すとロックの検証にならない）。
  const tilted = dragRoomLocked(c, 'x', 'rotate', 60, 0, { pivot, dist: 3, lockTilt });
  assert.deepEqual(tilted, c);

  // 床を回す（左右の向き）は通り、上下と傾きは 1 ミリ度も動かない。
  const spun = dragRoomLocked(c, '', 'rotate', 40, 0, { pivot, dist: 3, lockTilt });
  assert.ok(Math.abs(spun.yawDeg - c.yawDeg) > 1, '左右は動く');
  assert.equal(spun.pitchDeg, lockTilt.pitchDeg);
  assert.equal(spun.rollDeg, lockTilt.rollDeg);

  // 平行移動も通る。
  const moved = dragRoomLocked(c, '', 'move', 30, 20, { pivot, dist: 3, lockTilt });
  assert.ok(Math.hypot(moved.x - c.x, moved.y - c.y, moved.z - c.z) > 0.01, '位置は動く');
  assert.equal(moved.pitchDeg, lockTilt.pitchDeg);
  assert.equal(moved.rollDeg, lockTilt.rollDeg);
});

test('傾きを保たない設定なら今までどおり全部動く（既定を変えていない）', () => {
  const c = base();
  const pivot = [0.4, 0, 1.2];
  const tilted = dragRoomLocked(c, 'x', 'rotate', 60, 0, { pivot, dist: 3 });
  assert.notDeepEqual(tilted, c, 'ロックしていなければ従来どおり傾く');
  assert.ok(Math.abs(tilted.pitchDeg - c.pitchDeg) > 0.5, '実際に傾いていること');
});
