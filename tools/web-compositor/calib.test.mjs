// calib.js（実カメラの較正）を node:test で固定する。
//   実行: node --test tools/web-compositor/calib.test.mjs
//
// 検証は 2 本立て:
//   (A) **往復**: 既知のカメラから床点を投影 → その対応だけから較正 → 元の値が戻るか
//       （較正が「解けている」ことの証明。ノイズ耐性もここで見る）
//   (B) **Unity との一致**: 同じ入力で ShowCgLayer.BuildProjectionMatrix + Unity Camera が出す
//       画素座標と、この JS の projectPoint が一致するか。期待値は両側にハードコードして突き合わせる
//       （C# 側は Assets/Tests/Streaming/CgProjectionTests.cs の Projection_* を見よ）。
//       ここがズレると「卓では合うのに実機が違う」— この現場が何度も踏んだ罠になる。

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  projectPoint, unprojectToFloor, calibrateFromFloorPoints, solveHomography, focalFromHomography,
  matrixToUnityEuler, unityEulerToMatrix, distortNorm, undistortNorm, isDegenerate,
  reprojectionRms, calibQualityLabel, MIN_POINTS_FOR_K1,
} from './calib.js';

// ---- (B) Unity との一致（最重要）--------------------------------------------

test('投影は Unity（BuildProjectionMatrix + Camera）と同じ画素を出す', () => {
  // C# 側 CgProjectionTests.Projection_MatchesPinholeFormula と**同一の入力・同一の期待値**。
  // 片方だけ直すと沈黙して食い違うので、変えるときは必ず両方を直すこと。
  const calib = {
    x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: -30, rollDeg: 0,
    fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
  };
  const q = projectPoint(calib, 0, 0, 0);   // course 原点の床
  assert.ok(q);
  assert.ok(Math.abs(q.u - 320) < 1e-3, `u=${q.u}`);
  assert.ok(Math.abs(q.v - 313.1058) < 1e-3, `v=${q.v}`);
});

test('カメラ後方の点は写らない（null）', () => {
  const calib = {
    x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: -30, rollDeg: 0,
    fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
  };
  assert.equal(projectPoint(calib, 0, 0, -4), null);
});

test('Unity Euler ↔ 回転行列が往復する', () => {
  for (const e of [{ x: 0, y: 0, z: 0 }, { x: 30, y: -45, z: 5 }, { x: -12, y: 170, z: -8 }]) {
    const back = matrixToUnityEuler(unityEulerToMatrix(e.x, e.y, e.z));
    assert.ok(Math.abs(back.x - e.x) < 1e-6, `x ${back.x} != ${e.x}`);
    assert.ok(Math.abs(back.y - e.y) < 1e-6, `y ${back.y} != ${e.y}`);
    assert.ok(Math.abs(back.z - e.z) < 1e-6, `z ${back.z} != ${e.z}`);
  }
});

// ---- レンズ歪み（除算モデル）------------------------------------------------

test('歪みの順・逆が厳密に打ち消し合う（シェーダの逆変換と一致する根拠）', () => {
  for (const k1 of [-0.3, -0.1, 0.15, 0.4]) {
    for (const [nx, ny] of [[0.1, 0.05], [-0.4, 0.3], [0.5, -0.2]]) {
      const d = distortNorm(nx, ny, k1);
      assert.ok(d, `distort failed k1=${k1} n=(${nx},${ny})`);
      const u = undistortNorm(d[0], d[1], k1);
      assert.ok(Math.abs(u[0] - nx) < 1e-9, `x k1=${k1}`);
      assert.ok(Math.abs(u[1] - ny) < 1e-9, `y k1=${k1}`);
    }
  }
});

test('除算モデルで像を結ばない領域は null（無理に外挿しない）', () => {
  // k1 が大きい樽型では、理想座標の遠方が有限半径へ潰れて「そこには写らない」領域ができる。
  // 1 - 4 k1 r_u^2 < 0 がその条件。黙って外挿すると画面外に幽霊の人形が出る。
  assert.equal(distortNorm(0.9, -0.9, 0.4), null);
});

test('歪み 0 は恒等', () => {
  assert.deepEqual(distortNorm(0.3, -0.2, 0), [0.3, -0.2]);
  assert.deepEqual(undistortNorm(0.3, -0.2, 0), [0.3, -0.2]);
});

// ---- (A) 較正の往復 ---------------------------------------------------------

const W = 640, H = 480;

// 現場に近い配置: 1.8m 四方の部屋の隅、高さ 1.5m、部屋の中心へ向けた広角（水平 73°）。
const TRUTH = {
  x: -0.8, y: 1.5, z: 0.8, yawDeg: 135, pitchDeg: -38, rollDeg: 0,
  fxPx: 430, fyPx: 430, cxPx: W / 2, cyPx: H / 2, k1: 0, srcW: W, srcH: H,
};

// 床の × 印（layout.regPoints を流用する想定）。**カメラから見える範囲**に打つ
// （カメラ足元の床は画角に入らない — 現場でも同じ）。
const FLOOR = [
  { x: -0.6, z: -0.6 }, { x: 0.6, z: -0.6 }, { x: 0.6, z: 0.6 },
  { x: 0.0, z: 0.0 }, { x: -0.2, z: -0.3 },
];

// 歪みまで解くための密な点（3x3 グリッド）。画面に入るものだけを使う。
const FLOOR_DENSE = [-0.6, 0, 0.6].flatMap((x) => [-0.6, 0, 0.6].map((z) => ({ x, z })));

function synth(truth, floor = FLOOR, noisePx = 0) {
  const pts = [];
  let seed = 12345;
  const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return (seed / 0x7fffffff) * 2 - 1; };
  for (const f of floor) {
    const q = projectPoint(truth, f.x, 0, f.z);
    if (!q) continue;
    if (q.u < 0 || q.u > W || q.v < 0 || q.v > H) continue;   // 画面外の点は人がクリックできない
    pts.push({ x: f.x, z: f.z, u: q.u + rnd() * noisePx, v: q.v + rnd() * noisePx });
  }
  return pts;
}

test('テストの床点はすべて画面内に写る（人がクリックできる前提）', () => {
  assert.equal(synth(TRUTH).length, FLOOR.length);
  assert.ok(synth(TRUTH, FLOOR_DENSE).length >= 8);
});

function assertClose(got, truth, tol) {
  assert.ok(Math.abs(got.x - truth.x) < tol.pos, `x ${got.x.toFixed(3)} vs ${truth.x}`);
  assert.ok(Math.abs(got.y - truth.y) < tol.pos, `y ${got.y.toFixed(3)} vs ${truth.y}`);
  assert.ok(Math.abs(got.z - truth.z) < tol.pos, `z ${got.z.toFixed(3)} vs ${truth.z}`);
  assert.ok(Math.abs(got.yawDeg - truth.yawDeg) < tol.ang, `yaw ${got.yawDeg.toFixed(2)} vs ${truth.yawDeg}`);
  assert.ok(Math.abs(got.pitchDeg - truth.pitchDeg) < tol.ang, `pitch ${got.pitchDeg.toFixed(2)} vs ${truth.pitchDeg}`);
  assert.ok(Math.abs(got.fxPx / truth.fxPx - 1) < tol.fRel, `f ${got.fxPx.toFixed(1)} vs ${truth.fxPx}`);
}

test('床 5 点から姿勢と焦点距離が復元される（歪み無し・誤差無し）', () => {
  const r = calibrateFromFloorPoints(synth(TRUTH), W, H, { estimateK1: false });
  assert.equal(r.ok, true, r.reason);
  assertClose(r.calib, TRUTH, { pos: 0.01, ang: 0.2, fRel: 0.01 });
  assert.ok(r.calib.rmsPx < 0.1, `rms=${r.calib.rmsPx}`);
});

test('画角が既知なら床 4 点（最小構成）で解ける', () => {
  // 4 点ちょうどのときは **どの 3 点も一直線に並んでいないこと**が要る（DLT が正方系になるため）。
  const quad = [FLOOR[0], FLOOR[1], FLOOR[2], FLOOR[4]];
  const r = calibrateFromFloorPoints(synth(TRUTH, quad), W, H, { fixedFocalPx: TRUTH.fxPx });
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.focalLocked, true);
  assertClose(r.calib, TRUTH, { pos: 0.01, ang: 0.2, fRel: 1e-6 });
});

test('4 点のうち 3 点が一直線なら、理由を言って断る', () => {
  // (-0.6,-0.6) (0.6,0.6) (0,0) は y=x 上に並ぶ。黙って「解けません」ではなく何が悪いか言う。
  const collinear = [FLOOR[0], FLOOR[1], FLOOR[2], FLOOR[3]];
  const r = calibrateFromFloorPoints(synth(TRUTH, collinear), W, H, { fixedFocalPx: TRUTH.fxPx });
  assert.equal(r.ok, false);
  assert.match(r.reason, /一直線/);
});

// ---- 画角を固定するかどうかが精度を一桁変える（運用設計の根拠）------------------

test('⭐ 画角を固定すると、人のクリック誤差に対する位置精度が一桁良くなる', () => {
  // **この体験の較正運用（内部は一度だけ / 外部は毎回）の数値的根拠**。
  // 平面 1 枚から焦点距離まで推定すると、f の誤差がそのまま奥行きスケールの誤差になる。
  const pts = synth(TRUTH, FLOOR_DENSE, 1.5);
  const est = calibrateFromFloorPoints(pts, W, H, { estimateK1: false });
  const fixed = calibrateFromFloorPoints(pts, W, H, { estimateK1: false, fixedFocalPx: TRUTH.fxPx });
  assert.equal(est.ok, true);
  assert.equal(fixed.ok, true);

  const err = (c) => Math.hypot(c.x - TRUTH.x, c.y - TRUTH.y, c.z - TRUTH.z);
  const eEst = err(est.calib), eFixed = err(fixed.calib);
  assert.ok(eFixed < 0.05, `画角固定なら 5cm 以内に入るはず（実測 ${(eFixed * 100).toFixed(1)}cm）`);
  assert.ok(eFixed * 3 < eEst,
    `固定 ${(eFixed * 100).toFixed(1)}cm が推定 ${(eEst * 100).toFixed(1)}cm より明確に良いはず`);
});

test('画角を固定すれば、点が少なくても歪みを推定できる（縮退しないため）', () => {
  const truth = { ...TRUTH, k1: 0.18 };
  const r = calibrateFromFloorPoints(synth(truth, FLOOR), W, H, { fixedFocalPx: truth.fxPx });
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.k1Estimated, true);
  assert.ok(Math.abs(r.calib.k1 - truth.k1) < 0.03, `k1=${r.calib.k1}`);
  assertClose(r.calib, truth, { pos: 0.02, ang: 0.3, fRel: 1e-6 });
});

test('樽型歪みのある広角でも、点が十分あれば k1 ごと復元される', () => {
  // 超広角スマホを想定した樽型（除算モデルの k1 は正で樽型）。
  const truth = { ...TRUTH, k1: 0.18 };
  const r = calibrateFromFloorPoints(synth(truth, FLOOR_DENSE), W, H);
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.k1Estimated, true);
  assert.ok(Math.abs(r.calib.k1 - truth.k1) < 0.03, `k1=${r.calib.k1}`);
  assertClose(r.calib, truth, { pos: 0.02, ang: 0.3, fRel: 0.02 });
});

test('k1 を 0 に固定すると、歪んだ映像では誤差が明確に大きくなる（推定する価値の証明）', () => {
  const truth = { ...TRUTH, k1: 0.18 };
  const pts = synth(truth, FLOOR_DENSE);
  const withK1 = calibrateFromFloorPoints(pts, W, H);
  const withoutK1 = calibrateFromFloorPoints(pts, W, H, { estimateK1: false });
  assert.equal(withK1.ok, true);
  assert.equal(withoutK1.ok, true);
  assert.ok(withK1.calib.rmsPx * 10 < withoutK1.calib.rmsPx,
    `k1 推定 ${withK1.calib.rmsPx} が固定 ${withoutK1.calib.rmsPx} より桁で良いはず`);
});

test('画角も未知で点が少ないときは、歪みの推定を諦める（縮退で姿勢が壊れるため）', () => {
  // 5 点・画角未知で k1 まで解こうとすると、歪みを焦点距離に吸わせた偽の解
  //（f 430→300 / 高さ 1.5m→1.14m）に落ちる。だから推定しない。
  const truth = { ...TRUTH, k1: 0.18 };
  const pts = synth(truth, FLOOR);
  assert.ok(pts.length < MIN_POINTS_FOR_K1);
  const r = calibrateFromFloorPoints(pts, W, H);
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.k1Estimated, false, '点が足りないのに歪みを推定してはいけない');
  assert.equal(r.calib.k1, 0);
});

test('歪んだ映像を歪み無しとして解くと高さが大きく狂う（歪みを無視できない根拠）', () => {
  // **これが「k1 を後回しにしてはいけない」理由**。広角の樽型を無視すると、
  // 再投影誤差はそこそこでも高さが 4 割ずれる = 人形が床にめり込む / 浮く。
  const truth = { ...TRUTH, k1: 0.18 };
  const naive = calibrateFromFloorPoints(synth(truth, FLOOR), W, H, { estimateK1: false });
  assert.equal(naive.ok, true);
  assert.ok(Math.abs(naive.calib.y - truth.y) > 0.3,
    `歪み無視だと高さが大きく狂うはず（実測 ${naive.calib.y.toFixed(2)}m / 真値 ${truth.y}m）`);

  // 画角を固定すれば、同じ 5 点でも歪みごと解けて高さが戻る。
  const good = calibrateFromFloorPoints(synth(truth, FLOOR), W, H, { fixedFocalPx: truth.fxPx });
  assert.ok(Math.abs(good.calib.y - truth.y) < 0.03, `y=${good.calib.y}`);
});

test('画角が既知なら、クリック誤差 1.5px でも人形が床に立つ精度で解ける', () => {
  const r = calibrateFromFloorPoints(synth(TRUTH, FLOOR, 1.5), W, H, { fixedFocalPx: TRUTH.fxPx });
  assert.equal(r.ok, true, r.reason);
  // 1.5px のクリックずれ ≒ 現場で人が打つ精度。位置 5cm・角度 1° 以内。
  assertClose(r.calib, TRUTH, { pos: 0.05, ang: 1.0, fRel: 1e-6 });
});

test('roll（三脚の傾き）も復元される', () => {
  const truth = { ...TRUTH, rollDeg: 7 };
  const r = calibrateFromFloorPoints(synth(truth), W, H, { estimateK1: false });
  assert.equal(r.ok, true, r.reason);
  assert.ok(Math.abs(r.calib.rollDeg - 7) < 0.5, `roll=${r.calib.rollDeg}`);
});

test('解いた較正で再投影すると元の画素に戻る', () => {
  const pts = synth(TRUTH);
  const r = calibrateFromFloorPoints(pts, W, H, { estimateK1: false });
  assert.ok(reprojectionRms(r.calib, pts) < 0.1);
});

test('refs は正規化座標で残る（再解決に使う）', () => {
  const pts = synth(TRUTH);
  const r = calibrateFromFloorPoints(pts, W, H, { estimateK1: false });
  assert.equal(r.calib.refs.length, pts.length);
  for (const ref of r.calib.refs) {
    assert.ok(ref.u >= 0 && ref.u <= 1, `u=${ref.u}`);
    assert.ok(ref.v >= 0 && ref.v <= 1, `v=${ref.v}`);
    assert.equal(ref.y, 0);
  }
});

// ---- 失敗のしかた（現場で「なぜ解けないか」が分かること）----------------------

test('点が足りなければ理由を返す', () => {
  const r = calibrateFromFloorPoints(synth(TRUTH, FLOOR.slice(0, 3)), W, H);
  assert.equal(r.ok, false);
  assert.match(r.reason, /4 点/);
});

test('一直線に並んだ点は退化として弾く', () => {
  const line = [{ x: -0.6, z: 0 }, { x: -0.2, z: 0 }, { x: 0.2, z: 0 }, { x: 0.6, z: 0 }];
  assert.equal(isDegenerate(line.map((p) => ({ ...p, u: 0, v: 0 }))), true);
  const r = calibrateFromFloorPoints(synth(TRUTH, line), W, H);
  assert.equal(r.ok, false);
  assert.match(r.reason, /一直線/);
});

test('映像の実寸が不明なら解かない', () => {
  const r = calibrateFromFloorPoints(synth(TRUTH), 0, 0);
  assert.equal(r.ok, false);
  assert.match(r.reason, /実寸/);
});

test('品質ラベルは誤差に応じて 3 段階', () => {
  assert.equal(calibQualityLabel(1.2).level, 'good');
  assert.equal(calibQualityLabel(4).level, 'ok');
  assert.equal(calibQualityLabel(12).level, 'bad');
  assert.equal(calibQualityLabel(Number.POSITIVE_INFINITY).level, 'bad');
});

// ---- 内部部品 ---------------------------------------------------------------

test('ホモグラフィは対応点を厳密に写す', () => {
  const pts = synth(TRUTH);
  const H3 = solveHomography(pts);
  assert.ok(H3);
  for (const p of pts) {
    const w = H3[2][0] * p.x + H3[2][1] * p.z + H3[2][2];
    const u = (H3[0][0] * p.x + H3[0][1] * p.z + H3[0][2]) / w;
    const v = (H3[1][0] * p.x + H3[1][1] * p.z + H3[1][2]) / w;
    assert.ok(Math.abs(u - p.u) < 1e-6, `u ${u} vs ${p.u}`);
    assert.ok(Math.abs(v - p.v) < 1e-6, `v ${v} vs ${p.v}`);
  }
});

test('焦点距離は 2 つの直交拘束から一致して出る', () => {
  const H3 = solveHomography(synth(TRUTH));
  const f = focalFromHomography(H3, W / 2, H / 2);
  assert.ok(Math.abs(f - TRUTH.fxPx) < 2, `f=${f}`);
});

// ---- 逆写像（映像クリック → 床の点）------------------------------------------
//   「映像の床をクリックして人形を置く」の土台。projectPoint の**厳密な逆**でなければ、
//   クリックした場所と人形の立つ場所が食い違い、しかも誰もその理由に辿り着けない。

test('unprojectToFloor は projectPoint の厳密な逆（歪み・roll 込み）', () => {
  const cams = [
    TRUTH,
    { ...TRUTH, k1: 0.22 },                       // 樽型の広角
    { ...TRUTH, k1: -0.18, rollDeg: 7 },          // 糸巻き + 傾き
    { x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: -30, rollDeg: 0, fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: W, srcH: H },
  ];
  for (const c of cams) {
    for (const f of [{ x: -0.6, z: -0.6 }, { x: 0.6, z: 0.6 }, { x: 0, z: 0 }, { x: 0.3, z: -0.45 }]) {
      const q = projectPoint(c, f.x, 0, f.z);
      if (!q) continue;
      const p = unprojectToFloor(c, q.u, q.v);
      assert.ok(p, `逆写像できない (${f.x},${f.z}) k1=${c.k1}`);
      assert.ok(Math.abs(p.x - f.x) < 1e-9, `x ${p.x} vs ${f.x} (k1=${c.k1})`);
      assert.ok(Math.abs(p.z - f.z) < 1e-9, `z ${p.z} vs ${f.z} (k1=${c.k1})`);
    }
  }
});

test('地平線より上（床と交わらない方向）は null', () => {
  // 俯角 10°・垂直半画角 26.6° なので、画の上端のレイは 16.6° 上向き＝床と永遠に交わらない。
  // ⚠ 俯角が半画角より深いカメラでは画の上端すら床に当たる（ただし遥か遠く）。
  //    「地平線が画に写っているか」はカメラ次第なので、卓は距離でも歯止めをかけること。
  const c = { x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: -10, rollDeg: 0, fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: W, srcH: H };
  assert.equal(unprojectToFloor(c, 320, 0), null);
  // 真下寄り（画の下端）は必ず床に当たる。
  assert.ok(unprojectToFloor(c, 320, 470));
});

test('水平に構えたカメラの水平線ちょうども null（t が発散する所を弾く）', () => {
  const c = { x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: 0, rollDeg: 0, fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: W, srcH: H };
  assert.equal(unprojectToFloor(c, 320, 240), null);
});

test('床以外の高さの平面にも落とせる（planeY）', () => {
  const c = { x: 0, y: 1.2, z: -1.5, yawDeg: 0, pitchDeg: -30, rollDeg: 0, fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: W, srcH: H };
  const q = projectPoint(c, 0.4, 0.7, 0.2);
  const p = unprojectToFloor(c, q.u, q.v, 0.7);
  assert.ok(Math.abs(p.x - 0.4) < 1e-9 && Math.abs(p.z - 0.2) < 1e-9);
});

test('較正が無ければ null（原点を返して黙って置かない）', () => {
  assert.equal(unprojectToFloor(null, 100, 100), null);
  assert.equal(unprojectToFloor({ fxPx: 0 }, 100, 100), null);
});
