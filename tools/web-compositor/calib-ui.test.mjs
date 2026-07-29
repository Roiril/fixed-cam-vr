// calib-ui.js の純関数（DOM 非依存部分）を node:test で固定する。
//   実行: node --test tools/web-compositor/calib-ui.test.mjs
//
// ここで守っているのは「較正 UI が現場で嘘をつかない」ための 3 点:
//   (A) 打つ点の候補が **regPoints 優先・重複なし・打った点は消える**（＝対応の入れ違いを減らす）
//   (B) 重ねるワイヤーの幾何が layout そのままである（＝ずれた時に「部屋の記述か較正か」を切り分けられる）
//   (C) 保存する形が Unity の契約どおり（hasCalib の AND 規約 / pose を壊さない / refs を残す）
//
// 数学そのもの（解けるか・精度）は calib.test.mjs の担当。ここでは扱わない。

import test from 'node:test';
import assert from 'node:assert/strict';
import { calibrateFromFloorPoints, projectPoint } from './calib.js';
import {
  candidatePoints, wireSegments, pointsFromRefs, lockableFocalPx, hfovFromFocal, calibMatchesSource,
  calibSummaryLines, calibWarnings, calibBadgeText, formatSolvedAt, localIsoNow,
  applyCalibToCameras, clearCalibFromCameras, pointKey, DEFAULT_REG_POINTS, pointQuality,
} from './calib-ui.js';

// ---- (A) 候補点 --------------------------------------------------------------

const LAYOUT = {
  floor: { w: 1.8, d: 1.8 },
  wall: { corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] },
  regPoints: [{ x: -0.5, z: 0.5, label: '玄関側' }, { x: 0.5, z: 0.5 }, { x: 0, z: -0.7 }],
  grid: { tileM: 0.9, cols: 2, rows: 2, cells: ['0.', '..'] },
  lines: [{ id: 'line_1', camera: 1, x1: -0.3, z1: -0.7, x2: 0.3, z2: -0.7, dir: 'both' }],
};

test('候補は 位置合わせ点 → 部屋の角 → タイルの角 の順に並ぶ', () => {
  const kinds = candidatePoints(LAYOUT).map((c) => c.kind);
  const firstOf = (k) => kinds.indexOf(k);
  assert.equal(kinds[0], 'reg');
  assert.ok(firstOf('reg') < firstOf('room'), kinds.join(','));
  assert.ok(firstOf('room') < firstOf('grid'), kinds.join(','));
});

test('位置合わせ点はラベルと順番を持つ（HMD のタッチ順と同じ番号）', () => {
  const reg = candidatePoints(LAYOUT).filter((c) => c.kind === 'reg');
  assert.equal(reg.length, 3);
  assert.equal(reg[0].label, '位置合わせ点 1・玄関側');
  assert.equal(reg[1].label, '位置合わせ点 2');
  assert.deepEqual([reg[2].x, reg[2].z], [0, -0.7]);
});

test('regPoints が無ければ既定 2 点（フロアマップのゴーストと同じ値）へフォールバックする', () => {
  const reg = candidatePoints({ floor: { w: 1.8, d: 1.8 } }).filter((c) => c.kind === 'reg');
  assert.equal(reg.length, DEFAULT_REG_POINTS.length);
  assert.deepEqual(reg.map((r) => [r.x, r.z]), DEFAULT_REG_POINTS.map((r) => [r.x, r.z]));
});

test('同じ座標は 1 度しか出さない（位置合わせ点と壁の外角が重なっても二重にしない）', () => {
  const all = candidatePoints(LAYOUT);
  const keys = all.map((c) => c.key);
  assert.equal(new Set(keys).size, keys.length);
  // regPoints[0] と wall.corner はどちらも (-0.5, 0.5) → reg 側が勝って room 側は出ない
  const at = all.filter((c) => c.key === pointKey(-0.5, 0.5));
  assert.equal(at.length, 1);
  assert.equal(at[0].kind, 'reg');
});

test('既に打った点は候補から消える（＝セレクトが次に打つ点へ送られる）', () => {
  const before = candidatePoints(LAYOUT);
  const after = candidatePoints(LAYOUT, [{ x: before[0].x, z: before[0].z }]);
  assert.equal(after.length, before.length - 1);
  assert.ok(!after.some((c) => c.key === before[0].key));
});

test('タイルの角は「塗られたタイルに接する交点」だけ（全交点を並べない）', () => {
  // 2x2 グリッドでタイル (0,0) だけ塗り → その 4 隅だけが候補になる
  const layout = { grid: { tileM: 0.9, cols: 2, rows: 2, cells: ['0.', '..'] } };
  const grid = candidatePoints(layout).filter((c) => c.kind === 'grid');
  const keys = grid.map((c) => c.key).sort();
  // 既定 regPoints (-0.5,0.5)/(0.5,0.5) は格子点と重ならないので 4 隅がそのまま出る
  assert.deepEqual(keys, [
    pointKey(-0.9, 0), pointKey(-0.9, 0.9), pointKey(0, 0), pointKey(0, 0.9),
  ].sort());
});

test('grid の交点は cols×tileM 基準（ZoneLayoutSolver.cellRect と同じ式）で出す', () => {
  // floor が grid と食い違う show.json でも、grid 側の寸法で並べる（半タイルずらさない）
  const layout = { floor: { w: 3, d: 3 }, grid: { tileM: 0.5, cols: 2, rows: 2, cells: ['00', '00'] } };
  const grid = candidatePoints(layout).filter((c) => c.kind === 'grid');
  const xs = [...new Set(grid.map((c) => c.x))].sort((a, b) => a - b);
  assert.deepEqual(xs, [-0.5, 0, 0.5]);
});

// ---- (B) ワイヤー幾何 ---------------------------------------------------------

test('ワイヤーは床の外周・0.3m 格子・L 字壁・通過ラインを course 空間の線分で返す', () => {
  const segs = wireSegments(LAYOUT);
  const count = (k) => segs.filter((s) => s.kind === k).length;
  assert.equal(count('floor'), 4);
  // 1.8m を 0.3m 刻み → 内側の線は各軸 5 本（外周と重ねない）
  assert.equal(count('grid'), 10);
  // L 字 = 床 2 + 上端 2 + 垂直 3
  assert.equal(count('wall'), 7);
  assert.equal(count('line'), 1);
  assert.equal(segs.length, 22);
});

test('壁の垂直線は高さ 1.0m まで立つ（床だけでは高さ方向のずれが見えない）', () => {
  const vert = wireSegments(LAYOUT).filter((s) => s.kind === 'wall' && s.a[0] === s.b[0] && s.a[2] === s.b[2]);
  assert.equal(vert.length, 3);
  for (const v of vert) {
    assert.equal(v.a[1], 0);
    assert.equal(v.b[1], 1);
  }
  assert.equal(wireSegments(LAYOUT, { wallH: 2.4 }).find((s) => s.kind === 'wall' && s.a[0] === s.b[0] && s.a[2] === s.b[2]).b[1], 2.4);
});

test('通過ラインは担当カメラ色を持ち、担当なしは白（フロアマップと同じ規則）', () => {
  const withCam = wireSegments(LAYOUT).find((s) => s.kind === 'line');
  assert.equal(withCam.color, '#5aa8ff');          // camera 1
  const noCam = wireSegments({
    ...LAYOUT, lines: [{ id: 'l', x1: 0, z1: 0, x2: 0.5, z2: 0 }],
  }).find((s) => s.kind === 'line');
  assert.equal(noCam.color, '#fffaf0');
});

test('壁が無い layout でも床と格子は出る（落ちない）', () => {
  const segs = wireSegments({ floor: { w: 1.2, d: 1.2 } });
  assert.equal(segs.filter((s) => s.kind === 'wall').length, 0);
  assert.equal(segs.filter((s) => s.kind === 'floor').length, 4);
  assert.ok(segs.length > 4);
  assert.equal(wireSegments(null).filter((s) => s.kind === 'floor').length, 4);   // 既定 1.8m 床
});

test('ワイヤーは実際に映像へ投影できる（較正 → projectPoint が画素を返す）', () => {
  const calib = {
    x: 0, y: 1.4, z: -1.2, yawDeg: 0, pitchDeg: -25, rollDeg: 0,
    fxPx: 480, fyPx: 480, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
  };
  const projected = wireSegments(LAYOUT)
    .map((s) => [projectPoint(calib, ...s.a), projectPoint(calib, ...s.b)])
    .filter(([a, b]) => a && b);
  assert.ok(projected.length > 10, `投影できた線分 ${projected.length} 本`);
});

// ---- 対応点の復元 / 画角の固定 -------------------------------------------------

test('保存済み refs（正規化 uv）は解像度が変わっても画素へ戻せる', () => {
  const refs = [{ u: 0.25, v: 0.5, x: -0.5, z: 0.5, y: 0 }, { u: 0.75, v: 0.5, x: 0.5, z: 0.5, y: 0 }];
  assert.deepEqual(pointsFromRefs(refs, 640, 480), [
    { x: -0.5, z: 0.5, u: 160, v: 240 },
    { x: 0.5, z: 0.5, u: 480, v: 240 },
  ]);
  assert.deepEqual(pointsFromRefs(refs, 1280, 720)[0], { x: -0.5, z: 0.5, u: 320, v: 360 });
  assert.deepEqual(pointsFromRefs(null, 640, 480), []);
  assert.deepEqual(pointsFromRefs([{ u: 0.1 }], 640, 480), []);   // 壊れた要素は落とす
});

test('画角の固定は同じ解像度のときだけ許す（px 焦点距離は解像度に従属する）', () => {
  const calib = { fxPx: 430, srcW: 640, srcH: 480 };
  assert.equal(lockableFocalPx(calib, 640, 480), 430);
  assert.equal(lockableFocalPx(calib, 1280, 720), 0);
  assert.equal(lockableFocalPx(calib, 640, 360), 0);
  assert.equal(lockableFocalPx(null, 640, 480), 0);
  assert.equal(lockableFocalPx({ fxPx: 0, srcW: 640, srcH: 480 }, 640, 480), 0);
});

test('較正が今の映像に有効か（Unity の MatchesSource と同じ判定）', () => {
  const calib = { fxPx: 430, srcW: 640, srcH: 480 };
  assert.equal(calibMatchesSource(calib, 640, 480), true);
  assert.equal(calibMatchesSource(calib, 1280, 720), false);
  assert.equal(calibMatchesSource(calib, 0, 0), true);      // 実寸が分からないうちは邪魔しない
  assert.equal(calibMatchesSource(null, 640, 480), false);
});

test('焦点距離 → 水平画角（卓の 📐 欄・フロアマップの扇と同じ「水平」）', () => {
  assert.ok(Math.abs(hfovFromFocal(320, 640) - 90) < 1e-6);
  assert.ok(Math.abs(hfovFromFocal(430, 640) - 73.33) < 0.05, String(hfovFromFocal(430, 640)));
  assert.equal(hfovFromFocal(0, 640), 0);
});

// ---- 表示文字列 ---------------------------------------------------------------

const CALIB_SAMPLE = {
  x: -0.8, y: 1.5, z: 0.8, yawDeg: 135, pitchDeg: -38, rollDeg: 0.3,
  fxPx: 430, fyPx: 430, cxPx: 320, cyPx: 240, k1: 0.18,
  srcW: 640, srcH: 480, lensId: '0', rmsPx: 1.24, pointCount: 7,
  solvedAtIso: '2026-07-27T20:15:03', refs: [],
};

test('結果の本文は位置・高さ・水平画角・俯角・歪みを人の言葉で出す', () => {
  const lines = calibSummaryLines(CALIB_SAMPLE);
  assert.equal(lines[0], 'カメラ位置 (-0.80, 0.80) 高さ 1.50m');
  assert.match(lines[1], /^水平画角 73\.3° \/ 俯角 -38\.0°（下向きが負） \/ 傾き 0\.3°$/);
  assert.equal(lines[2], '歪み k1 +0.18 / 焦点距離 430px / 点 7 個');
  assert.deepEqual(calibSummaryLines(null), []);
});

test('画角を推定したときは「位置が数十 cm ずれうる」と必ず言う', () => {
  const warn = calibWarnings({ ok: true, focalLocked: false, k1Estimated: true, calib: { pointCount: 8 } });
  assert.equal(warn.length, 1);
  assert.match(warn[0], /画角も推定/);
  assert.deepEqual(calibWarnings({ ok: true, focalLocked: true, k1Estimated: true, calib: { pointCount: 8 } }), []);
});

test('点が足りず歪みを推定していないときも黙らない', () => {
  const warn = calibWarnings({ ok: true, focalLocked: true, k1Estimated: false, calib: { pointCount: 5 } });
  assert.equal(warn.length, 1);
  assert.match(warn[0], /点が 5 個なのでレンズ歪みは推定していません/);
  assert.deepEqual(calibWarnings({ ok: false, reason: 'x' }), []);
});

test('バッジは誤差と日時を出し、解像度が違えば警告する', () => {
  const cam = { id: 'A', calib: CALIB_SAMPLE };
  assert.equal(calibBadgeText(cam), '🎯 較正済み（誤差 1.2px・7/27 20:15）');
  assert.equal(calibBadgeText(cam, 640, 480), '🎯 較正済み（誤差 1.2px・7/27 20:15）');
  assert.match(calibBadgeText(cam, 1280, 720), /⚠ 640×480 用（いまは 1280×720）/);
  assert.equal(calibBadgeText({ id: 'B' }), '');
  assert.equal(formatSolvedAt('こわれた値'), '');
});

test('solvedAtIso はローカル時刻のタイムゾーン無し ISO（登録リチュアルと同じ流儀）', () => {
  const iso = localIsoNow(new Date(2026, 6, 27, 20, 15, 3));
  assert.equal(iso, '2026-07-27T20:15:03');
  assert.match(localIsoNow(), /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$/);
});

// ---- (C) 保存する形 -----------------------------------------------------------

const CAMS = () => ([
  { id: 'A', sourceId: 'Phone01', host: '10.0.0.1', port: 8080 },
  { id: 'D', sourceId: 'Phone04', role: 'fx', pose: { x: -0.5, z: 0.69, y: 1.35, yawDeg: 91, pitchDeg: -8, hfovDeg: 62 } },
]);

test('保存は calib + hasCalib を立て、pose も他カメラも壊さない', () => {
  const src = CAMS();
  const next = applyCalibToCameras(src, 'D', CALIB_SAMPLE);
  const d = next.find((c) => c.id === 'D');
  assert.equal(d.hasCalib, true);
  assert.equal(d.calib.fxPx, 430);
  assert.deepEqual(d.pose, { x: -0.5, z: 0.69, y: 1.35, yawDeg: 91, pitchDeg: -8, hfovDeg: 62 });
  assert.equal(d.role, 'fx');
  assert.deepEqual(next.find((c) => c.id === 'A'), src[0]);
  // 元配列は書き換えない（postState 前に UI が state を壊さない）
  assert.equal(src.find((c) => c.id === 'D').hasCalib, undefined);
  assert.notEqual(d.calib, CALIB_SAMPLE);          // 参照を共有しない（後の解き直しで汚染しない）
});

test('較正を捨てると calib は消え hasCalib=false が明示される（AND 規約）', () => {
  const withCalib = applyCalibToCameras(CAMS(), 'D', CALIB_SAMPLE);
  const cleared = clearCalibFromCameras(withCalib, 'D');
  const d = cleared.find((c) => c.id === 'D');
  assert.equal('calib' in d, false);
  assert.equal(d.hasCalib, false);
  assert.deepEqual(d.pose, { x: -0.5, z: 0.69, y: 1.35, yawDeg: 91, pitchDeg: -8, hfovDeg: 62 });
  assert.equal(withCalib.find((c) => c.id === 'D').hasCalib, true);   // 元は不変
});

test('解いた結果をそのまま保存すると Unity 契約のキーが揃う（再解決用の refs 込み）', () => {
  // 既知のカメラで床点を投影 → その対応だけから解く（calib.test.mjs の往復と同じ手口）
  const truth = {
    x: -0.85, y: 1.6, z: -0.85, yawDeg: 45, pitchDeg: -45, rollDeg: 0,
    fxPx: 430, fyPx: 430, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
  };
  const floor = [[-0.5, 0.5], [0.5, 0.5], [0.5, -0.5], [-0.5, -0.5], [0, 0], [0.3, -0.7]];
  const pts = floor.map(([x, z]) => {
    const q = projectPoint(truth, x, 0, z);
    // 部屋の隅から見下ろした構図なので 6 点とも画角に入る（＝人が実際にクリックできる状況）
    assert.ok(q.u >= 0 && q.u <= 640 && q.v >= 0 && q.v <= 480, `(${x},${z}) が画角の外`);
    return { x, z, u: q.u, v: q.v };
  });
  const r = calibrateFromFloorPoints(pts, 640, 480, {
    lensId: '0', solvedAtIso: localIsoNow(new Date(2026, 6, 27, 20, 15, 3)), fixedFocalPx: 430,
  });
  assert.ok(r.ok, r.reason);

  const saved = applyCalibToCameras(CAMS(), 'D', r.calib).find((c) => c.id === 'D').calib;
  for (const k of ['x', 'y', 'z', 'yawDeg', 'pitchDeg', 'rollDeg', 'fxPx', 'fyPx', 'cxPx', 'cyPx',
    'k1', 'srcW', 'srcH', 'lensId', 'rmsPx', 'pointCount', 'solvedAtIso', 'refs']) {
    assert.ok(k in saved, `キー ${k} が無い（Unity ShowCameraCalibDef と食い違う）`);
  }
  assert.equal(saved.srcW, 640);
  assert.equal(saved.srcH, 480);
  assert.equal(saved.pointCount, 6);
  assert.equal(saved.refs.length, 6);
  // refs は正規化 uv。次に開いた時この点が復元される
  assert.ok(saved.refs.every((f) => f.u >= 0 && f.u <= 1 && f.v >= 0 && f.v <= 1));
  assert.deepEqual(pointsFromRefs(saved.refs, 640, 480).map((p) => [p.x, p.z]), floor);
  // 固定した画角のまま解けている（＝次回も同じ値で固定できる）
  assert.equal(lockableFocalPx(saved, 640, 480), 430);
});

// ---- (A2) 自分で置いた印 / 部屋のプロキシの角 ---------------------------------
//   地図から置いた印（layout.calibPoints）は**候補の先頭**に出す。regPoints へコピーはしない
//   （2 コピーはいつか必ずずれる）。部屋のプロキシ（🧱 の壁・箱）の角は映像で見つけやすいので候補に混ぜる。

test('自分で置いた印は候補の先頭に出て、位置合わせ点を置き換えない', () => {
  const lay = { ...LAYOUT, calibPoints: [{ x: 0.2, z: 0.3, label: '棚の脚' }] };
  const list = candidatePoints(lay);
  assert.equal(list[0].kind, 'mark');
  assert.match(list[0].label, /棚の脚/);
  // regPoints は消えない（別の集合として両方出る）
  assert.ok(list.some((c) => c.kind === 'reg'));
});

test('部屋のプロキシの壁の端・箱の角が候補に入る', () => {
  const lay = {
    ...LAYOUT,
    hasRoom: true,
    room: {
      floorW: 1.8, floorD: 1.8,
      walls: [{ id: 'w1', x1: -0.4, z1: 0.4, x2: 0.4, z2: 0.4, h: 1, thick: 0.04 }],
      props: [{ id: 'p1', x: 0.6, z: -0.6, w: 0.4, d: 0.4, h: 0.7, yawDeg: 0 }],
    },
  };
  const room = candidatePoints(lay).filter((c) => c.kind === 'room');
  assert.ok(room.some((c) => Math.abs(c.x + 0.4) < 1e-6 && Math.abs(c.z - 0.4) < 1e-6), '壁の端');
  assert.ok(room.some((c) => Math.abs(c.x - 0.4) < 1e-6 && Math.abs(c.z + 0.4) < 1e-6), '箱の角');
});

// ---- (A3) 打つ前に言える点の質 ------------------------------------------------
//   実測（calib.test.mjs）で分かっている効き方を、解く前に作者へ返す。

const SQUARE = [
  { x: -0.6, z: 0.6, u: 60, v: 90 }, { x: 0.6, z: 0.6, u: 560, v: 100 },
  { x: 0.6, z: -0.6, u: 590, v: 400 }, { x: -0.6, z: -0.6, u: 40, v: 390 },
];

test('画角を固定するなら 4 点で解ける / 推定するなら 6 点要ると言う', () => {
  const locked = pointQuality(SQUARE, 640, 480, true);
  assert.equal(locked.ready, true);
  assert.deepEqual(locked.issues, []);
  const free = pointQuality(SQUARE, 640, 480, false);
  assert.equal(free.need, 6);
  assert.match(free.issues.join(' '), /6 点/);
});

test('一直線に近い点・画面の隅に固まった点を、解く前に指摘する', () => {
  const line = [
    { x: -0.6, z: 0, u: 50, v: 240 }, { x: -0.2, z: 0, u: 200, v: 240 },
    { x: 0.2, z: 0, u: 350, v: 240 }, { x: 0.6, z: 0, u: 500, v: 240 },
  ];
  const qy = pointQuality(line, 640, 480, true);
  assert.equal(qy.degenerate, true);
  assert.equal(qy.ready, false);

  const clustered = [
    { x: -0.6, z: 0.6, u: 300, v: 240 }, { x: 0.6, z: 0.6, u: 320, v: 245 },
    { x: 0.6, z: -0.6, u: 330, v: 260 }, { x: -0.6, z: -0.6, u: 305, v: 262 },
  ];
  assert.match(pointQuality(clustered, 640, 480, true).issues.join(' '), /画面の中で点が固まって/);
});
