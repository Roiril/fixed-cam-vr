// actor-proxy.js（CG 人形の輪郭プロキシ）を node:test で固定する。
//   実行: node --test tools/web-compositor/actor-proxy.test.mjs
//
// ここで守りたいこと:
//   (A) **クリックした床の点に足元が来る** — `unprojectToFloor` → `actorProxyGeometry` の往復。
//       これが崩れると「映像をクリックして人形を置く」機能そのものが嘘になる
//   (B) **出ないときは出ないと言える** — カメラ後方・画角外・はみ出しを取り違えない。
//       黙って綺麗な輪郭を返すのが最悪（著作者は実機まで気づけない）
//   (C) **較正が無くても概算で置ける** — pose からの擬似 calib が Unity の画角解釈と同じであること

import test from 'node:test';
import assert from 'node:assert/strict';
import { projectPoint, unprojectToFloor } from './calib.js';
import {
  actorProxyGeometry, poseToCalib, resolveProxyCalib, proxyIssueText,
  DEFAULT_HFOV_DEG, FOOT_RADIUS_M,
} from './actor-proxy.js';

// calib.test.mjs / C# CgProjectionTests と同じ既知カメラ（部屋の南から origin を見下ろす）。
const CAM = {
  x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: -30, rollDeg: 0,
  fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
};

// ---- (A) 立ち位置 → 画 --------------------------------------------------------

test('course 原点に立てると足元が画の中央（投影の期待値と一致）へ来る', () => {
  const g = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.6);
  assert.ok(g.visible);
  assert.ok(!g.behind);
  assert.ok(Math.abs(g.foot.u - 320) < 1e-3, `u=${g.foot.u}`);
  assert.ok(Math.abs(g.foot.v - 313.1058) < 1e-3, `v=${g.foot.v}`);
});

test('人形が高いほど頭が上に写る（v は下向きなので小さくなる）', () => {
  const low = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.2);
  const tall = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.9);
  assert.ok(low.head && tall.head);
  assert.ok(tall.head.v < low.head.v, `tall=${tall.head.v} low=${low.head.v}`);
  // 足元は身長で動かない（＝接地点は同じ）。ここが動くと「背を伸ばしたら人形が歩いた」ことになる。
  assert.ok(Math.abs(tall.foot.v - low.foot.v) < 1e-9);
});

test('身長ボックスは 12 辺・足元は閉じた多角形', () => {
  const g = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.6, { segments: 16 });
  assert.equal(g.box.length, 12);
  assert.equal(g.footprint.length, 16);
});

test('床の円は俯瞰カメラで楕円に写る（奥のほうが詰まる）', () => {
  // 円が円のまま写ったら投影を通していない証拠。ここが「接地しているか」の判断材料になる。
  const g = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.6, { segments: 4 });
  const us = g.footprint.map((q) => q.u), vs = g.footprint.map((q) => q.v);
  const wPx = Math.max(...us) - Math.min(...us);
  const hPx = Math.max(...vs) - Math.min(...vs);
  assert.ok(hPx < wPx * 0.85, `w=${wPx} h=${hPx}（俯角 30° なら縦が明確に潰れる）`);
});

test('向きの矢印は yaw の方向へ伸びる（0° = course +Z = 画の奥）', () => {
  const g0 = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.6);
  const g180 = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 180 }, 1.6);
  // +Z（奥）へ向くと矢印の先は画の上（v が小さい）、手前へ向くと下。
  assert.ok(g0.arrow.tip.v < g0.arrow.base.v, `fwd tip v=${g0.arrow.tip.v}`);
  assert.ok(g180.arrow.tip.v > g180.arrow.base.v, `back tip v=${g180.arrow.tip.v}`);
});

// ---- (B) 出ないときは出ないと言う ---------------------------------------------

test('カメラ後方の立ち位置は behind（輪郭を作らない）', () => {
  const g = actorProxyGeometry(CAM, { x: 0, z: -4, yawDeg: 0 }, 1.6);
  assert.equal(g.behind, true);
  assert.equal(g.visible, false);
  assert.equal(g.foot, null);
  assert.equal(g.footprint.length, 0);
  assert.match(proxyIssueText(g, 'calib'), /カメラの後ろ/);
});

test('画角の外に立てると visible=false（黙って端に描かない）', () => {
  const g = actorProxyGeometry(CAM, { x: 5, z: 0, yawDeg: 0 }, 1.6);
  assert.equal(g.behind, false);
  assert.equal(g.visible, false);
  assert.match(proxyIssueText(g, 'calib'), /画角の外/);
});

// 俯角のゆるいカメラ。CAM（俯角 30°）は 1.8m 四方の部屋では**どこに立っても頭が枠から出る**ので、
// 「収まる / はみ出す」の対比が作れない（それ自体が現場で起きる事実なので CAM は変えない）。
const CAM_SOFT = { ...CAM, pitchDeg: -12 };

test('画角に収まっていれば警告は出ない', () => {
  const g = actorProxyGeometry(CAM_SOFT, { x: 0, z: 1, yawDeg: 0 }, 1.6);
  assert.equal(g.visible, true);
  assert.equal(g.clipped, false);
  assert.equal(proxyIssueText(g, 'calib'), '');
});

test('枠にかかると clipped（実機でも体が切れる、と言えるようにする）', () => {
  // 同じ距離のまま横へずらしていくと、どこかで身長ボックスが左端を割る。
  let found = null;
  for (let x = -0.2; x >= -2.5; x -= 0.02) {
    const g = actorProxyGeometry(CAM_SOFT, { x, z: 1, yawDeg: 0 }, 1.6);
    if (g.visible && g.clipped) { found = g; break; }
  }
  assert.ok(found, 'はみ出す立ち位置が見つからない（投影かフラグの計算が壊れている）');
  assert.ok(found.bbox.u0 < 0, `左端を割っていない bbox.u0=${found.bbox.u0}`);
  assert.match(proxyIssueText(found, 'calib'), /はみ出/);
});

test('俯角の強いカメラでは近くに立つと必ず頭が切れる（現場の事実を握りつぶさない）', () => {
  const g = actorProxyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.6);
  assert.equal(g.visible, true);
  assert.equal(g.clipped, true);
});

test('較正が無ければ何も返さない（原点に幽霊を立てない）', () => {
  const g = actorProxyGeometry(null, { x: 0, z: 0, yawDeg: 0 }, 1.6);
  assert.equal(g.visible, false);
  assert.equal(g.foot, null);
});

// ---- (A) 往復: クリックした点に足元が来る -------------------------------------

test('unprojectToFloor → actorProxyGeometry で、クリックした画素に足元が戻る', () => {
  // 歪みと roll を入れて、経路のどこかで補正を飛ばしていたら落ちるようにする。
  const calib = { ...CAM, yawDeg: 12, pitchDeg: -22, rollDeg: 4, k1: 0.14 };
  for (const [u, v] of [[320, 400], [180, 330], [500, 460], [400, 300]]) {
    const p = unprojectToFloor(calib, u, v);
    assert.ok(p, `床に落ちない (${u},${v})`);
    const g = actorProxyGeometry(calib, { x: p.x, z: p.z, yawDeg: 0 }, 1.6);
    assert.ok(g.foot, `足元が写らない (${u},${v})`);
    assert.ok(Math.abs(g.foot.u - u) < 1e-6, `u ${g.foot.u} != ${u}`);
    assert.ok(Math.abs(g.foot.v - v) < 1e-6, `v ${g.foot.v} != ${v}`);
  }
});

test('足元マーカーの実寸は 0.2m（画の大きさではなく床の大きさで決まる）', () => {
  // 近くと遠くで画素半径は変わるが、逆写像した course 半径は同じ。
  const near = actorProxyGeometry(CAM, { x: 0, z: -0.5, yawDeg: 0 }, 1.6, { segments: 4 });
  const far = actorProxyGeometry(CAM, { x: 0, z: 0.9, yawDeg: 0 }, 1.6, { segments: 4 });
  const radiusOf = (g, cz) => {
    const p = unprojectToFloor(CAM, g.footprint[0].u, g.footprint[0].v);
    return Math.hypot(p.x - 0, p.z - cz);
  };
  assert.ok(Math.abs(radiusOf(near, -0.5) - FOOT_RADIUS_M) < 1e-6);
  assert.ok(Math.abs(radiusOf(far, 0.9) - FOOT_RADIUS_M) < 1e-6);
  // 近いほうが画では大きい（＝遠近が効いている）。
  const span = (g) => Math.max(...g.footprint.map((q) => q.u)) - Math.min(...g.footprint.map((q) => q.u));
  assert.ok(span(near) > span(far));
});

// ---- (C) 概算 pose からの擬似 calib -------------------------------------------

test('poseToCalib は水平画角から焦点距離を出す（Unity の hfov 解釈と同じ）', () => {
  const c = poseToCalib({ x: 0, z: -1.5, y: 1.2, yawDeg: 0, pitchDeg: -30, hfovDeg: 70 }, 640, 480);
  const want = 320 / Math.tan(35 * Math.PI / 180);
  assert.ok(Math.abs(c.fxPx - want) < 1e-9, `fx=${c.fxPx}`);
  assert.equal(c.fyPx, c.fxPx);      // 正方画素（Unity の垂直 FOV 変換を展開すると fy = fx）
  assert.equal(c.cxPx, 320);
  assert.equal(c.cyPx, 240);
  assert.equal(c.k1, 0);
  assert.equal(c.approx, true);
});

test('poseToCalib は画角未著作でも既定の水平画角で解ける', () => {
  const c = poseToCalib({ x: 0, z: 0 }, 640, 480);
  const want = 320 / Math.tan(DEFAULT_HFOV_DEG * Math.PI / 360);
  assert.ok(Math.abs(c.fxPx - want) < 1e-9);
  assert.equal(c.y, 1.2);            // Unity ShowCameraPoseDef.y の既定
});

test('画角が広いほど同じ立ち位置が画の中央寄りに写る', () => {
  const wide = poseToCalib({ x: 0, z: -1.5, y: 1.2, pitchDeg: -30, hfovDeg: 110 }, 640, 480);
  const tele = poseToCalib({ x: 0, z: -1.5, y: 1.2, pitchDeg: -30, hfovDeg: 40 }, 640, 480);
  const qw = projectPoint(wide, 0.6, 0, 0), qt = projectPoint(tele, 0.6, 0, 0);
  assert.ok(Math.abs(qw.u - 320) < Math.abs(qt.u - 320));
});

test('resolveProxyCalib は 較正 > 概算 pose > 無し の順で決める', () => {
  const calib = { ...CAM, fxPx: 480, srcW: 640, srcH: 480 };
  const pose = { x: 1, z: 1, y: 1.2, yawDeg: 90, pitchDeg: -10, hfovDeg: 70 };

  const a = resolveProxyCalib({ calib, pose }, 640, 480);
  assert.equal(a.source, 'calib');
  assert.equal(a.note, '');

  const b = resolveProxyCalib({ pose }, 640, 480);
  assert.equal(b.source, 'pose');
  assert.match(b.note, /較正していない/);
  assert.ok(Math.abs(b.calib.x - 1) < 1e-9);

  const c = resolveProxyCalib({}, 640, 480);
  assert.equal(c.source, 'none');
  assert.equal(c.calib, null);
  assert.match(c.note, /未著作/);
});

// ---- 描画ヘルパ（ribbon と show-sim が共有する）--------------------------------
//   Canvas2D は node に無いので、呼び出しを記録するだけの偽 ctx で「落ちない・座標が写像を通る」を見る。
//   ⚠ 見た目そのものはここでは検証できない（ブラウザで見るしかない）。ここで守るのは
//     「toPx を通し忘れて生の画素で描く」型の事故だけ。

function fakeCtx() {
  const calls = [], pts = [];
  const rec = (name) => (...a) => { calls.push(name); if (name === 'moveTo' || name === 'lineTo' || name === 'arc') pts.push([a[0], a[1]]); };
  return {
    calls, pts,
    save: rec('save'), restore: rec('restore'), beginPath: rec('beginPath'), closePath: rec('closePath'),
    moveTo: rec('moveTo'), lineTo: rec('lineTo'), arc: rec('arc'), stroke: rec('stroke'), fill: rec('fill'),
    fillRect: rec('fillRect'), fillText: rec('fillText'),
    measureText: () => ({ width: 30 }),
    strokeStyle: '', fillStyle: '', lineWidth: 1, font: '', textAlign: '', textBaseline: '',
  };
}

test('drawActorProxy は toPx を通した座標だけを使う（レターボックスを無視しない）', async () => {
  const { drawActorProxy } = await import('./actor-proxy.js');
  const g = actorProxyGeometry(CAM_SOFT, { x: 0, z: 1, yawDeg: 30 }, 1.6);
  const ctx = fakeCtx();
  // 画素 → キャンバスの写像として「半分に縮めて (1000, 2000) ずらす」を渡す。
  drawActorProxy(ctx, g, { toPx: (p) => ({ x: p.u / 2 + 1000, y: p.v / 2 + 2000 }), scale: 1, label: '人形' });
  assert.ok(ctx.calls.includes('stroke'));
  // 1 点でも生の画素のまま描いていたら、この範囲から外れる。
  for (const [x, y] of ctx.pts) {
    assert.ok(x >= 900 && x <= 1400, `x=${x} が写像を通っていない`);
    assert.ok(y >= 1900 && y <= 2400, `y=${y} が写像を通っていない`);
  }
});

test('drawActorProxy は空の輪郭では何もしない（黒い画に幽霊の線を出さない）', async () => {
  const { drawActorProxy } = await import('./actor-proxy.js');
  const ctx = fakeCtx();
  drawActorProxy(ctx, actorProxyGeometry(CAM, { x: 0, z: -4, yawDeg: 0 }, 1.6), {});
  assert.equal(ctx.calls.length, 0);
});

test('解像度の食い違う較正は使わず概算 pose へ落ちる（実機と同じ判定）', () => {
  const calib = { ...CAM, srcW: 1280, srcH: 720 };
  const pose = { x: 0, z: -1.5, y: 1.2, pitchDeg: -30, hfovDeg: 70 };
  const r = resolveProxyCalib({ calib, pose }, 640, 480);
  assert.equal(r.source, 'pose');
  assert.match(r.note, /1280×720 用/);
});

// ---- (D) 人型シルエット（2026-07-28）-----------------------------------------
//   箱のワイヤーでは事前オーサリングで構図を判断できなかったので人型を足した。
//   ⚠ 写実性は求めない（実描画の正は Unity）。**位置・大きさ・距離感が正しいこと**だけを守る。

test('人型の骨格は 13 本（胴 + 肩腕 ×2 + 脚 ×2）で、頭が付く', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  const b = actorBodyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.6);
  assert.equal(b.bones.length, 13);
  assert.ok(b.head && b.head.rPx > 0);
});

test('頭の直径は身長の 12〜15%（人体比率。ここが崩れると「人に見えない」）', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  // ⚠ 近距離では頭がカメラに近い分だけ透視で大きく写る（実測: 1.5m で 18%・4.5m で 13%）。
  //    これは正しい挙動なので、人体比率は**正射影に近い遠距離**で見る。
  const at = (z) => {
    const g = actorProxyGeometry(CAM, { x: 0, z, yawDeg: 0 }, 1.6);
    const b = actorBodyGeometry(CAM, { x: 0, z, yawDeg: 0 }, 1.6);
    return (b.head.rPx * 2) / Math.abs(g.foot.v - g.head.v);
  };
  const far = at(3);
  assert.ok(far > 0.12 && far < 0.15, `遠距離での 頭/身長 = ${far.toFixed(3)}`);
  assert.ok(at(0) > far, '近いほど頭が大きく写る（透視が効いている）');
});

test('遠いほど細く描かれる（太さが画素で解かれている＝距離感が出る）', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  const near = actorBodyGeometry(CAM, { x: 0, z: -0.6, yawDeg: 0 }, 1.6);
  const far = actorBodyGeometry(CAM, { x: 0, z: 2.5, yawDeg: 0 }, 1.6);
  assert.ok(near.bones[0].wPx > far.bones[0].wPx * 1.8,
    `near=${near.bones[0].wPx.toFixed(1)} far=${far.bones[0].wPx.toFixed(1)}`);
  assert.ok(near.head.rPx > far.head.rPx);
});

test('身長を変えると骨格全体が比例する（actors[].heightM が効く）', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  const small = actorBodyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 1.0);
  const tall = actorBodyGeometry(CAM, { x: 0, z: 0, yawDeg: 0 }, 2.0);
  assert.ok(tall.head.v < small.head.v, '背が高いほど頭は画の上へ来る');
  assert.ok(tall.head.rPx > small.head.rPx);
});

test('向きを 90° 変えると肩の投影幅が変わる（yaw が骨格に効いている）', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  const width = (yaw) => {
    const b = actorBodyGeometry(CAM, { x: 0, z: 0, yawDeg: yaw }, 1.6);
    const us = b.bones.flatMap((x) => [x.a.u, x.b.u]);
    return Math.max(...us) - Math.min(...us);
  };
  const front = width(0), side = width(90);
  assert.ok(Math.abs(front - side) > 4, `正面 ${front.toFixed(1)} / 横 ${side.toFixed(1)}`);
});

test('カメラ後方では骨格を返さない（幽霊の人形を描かない）', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  const b = actorBodyGeometry(CAM, { x: 0, z: -4, yawDeg: 0 }, 1.6);
  assert.equal(b.bones.length, 0);
  assert.equal(b.head, null);
});

test('較正が無ければ何も返さない（姿勢未著作のカメラでは実機も出さない）', async () => {
  const { actorBodyGeometry } = await import('./actor-proxy.js');
  assert.deepEqual(actorBodyGeometry(null, { x: 0, z: 0, yawDeg: 0 }, 1.6), { head: null, bones: [] });
});
