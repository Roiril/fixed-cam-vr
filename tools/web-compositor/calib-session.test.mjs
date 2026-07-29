// calib-session.js（較正パネルの判断・DOM 非依存）を node:test で固定する。
//   実行: node --test tools/web-compositor/calib-session.test.mjs
//
// ここで守っているのは「較正 UI が現場で嘘をつかない」ための 5 点:
//   (A) 打つ点の候補が **regPoints 優先・重複なし・打った点は消える**（＝対応の入れ違いを減らす）
//   (B) 重ねるワイヤーの幾何が layout そのままである（＝ずれた時に「部屋の記述か較正か」を切り分けられる）
//   (C) 保存する形が Unity の契約どおり（hasCalib の AND 規約 / pose を壊さない / refs を残す）
//   (D) **状態遷移**（開く / フレームを取り直す）が、解像度・縦横比・映像の有無で正しく畳む
//   (E) 実測していない幾何を実測のように扱わない（出典 measured / assumed）
//
// 数学そのもの（解けるか・精度）は calib.test.mjs の担当。ここでは扱わない。

import test from 'node:test';
import assert from 'node:assert/strict';
import { calibrateFromFloorPoints, projectPoint } from './calib.js';
import {
  candidatePoints, wireSegments, pointsFromRefs, lockableFocalPx, hfovFromFocal, calibMatchesSource,
  calibSummaryLines, calibWarnings, calibBadgeText, formatSolvedAt, localIsoNow,
  applyCalibToCameras, clearCalibFromCameras, pointKey, DEFAULT_REG_POINTS, pointQuality, raisedRefCount,
  wallLooksDefault, floorLooksDefault, qualityHeadline, resolvePointLabels, hasMeasuredGeometry,
  openSession, refreshSession, relocateSuspicion, leaveOneOutError, accuracyLine, pixelToMeters,
  focalFromHfov, SOURCE_MEASURED, SOURCE_ASSUMED,
  normalizeLens, lensesOf, resolveLens, lensFocalFor, lensFromSolve, upsertLens, removeLens,
  nextLensId, applyLensToCameras, detachLensFromCameras, lensSummary, lensTrustIssues,
  rescaleCalib,
} from './calib-session.js';

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
    { x: -0.5, z: 0.5, y: 0, u: 160, v: 240 },
    { x: 0.5, z: 0.5, y: 0, u: 480, v: 240 },
  ]);
  assert.deepEqual(pointsFromRefs(refs, 1280, 720)[0], { x: -0.5, z: 0.5, y: 0, u: 320, v: 360 });
  assert.deepEqual(pointsFromRefs(null, 640, 480), []);
  assert.deepEqual(pointsFromRefs([{ u: 0.1 }], 640, 480), []);   // 壊れた要素は落とす
});

test('高さの点（refs の y>0）は復元しても高さを保つ', () => {
  // 復元で y が落ちると、次に解いた時に**平面の外の拘束が黙って消える**（精度が戻る理由が分からなくなる）
  const refs = [{ u: 0.5, v: 0.2, x: -0.5, z: 0.5, y: 1.8 }, { u: 0.5, v: 0.7, x: -0.5, z: 0.5, y: 0 }];
  const pts = pointsFromRefs(refs, 640, 480);
  assert.equal(pts[0].y, 1.8);
  assert.equal(pts[1].y, 0);
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
  // 高さの点を 2 本入れてある解（raisedCount: 2）は、画角の警告だけが出る
  const warn = calibWarnings({ ok: true, focalLocked: false, k1Estimated: true, raisedCount: 2, calib: { pointCount: 8 } });
  assert.equal(warn.length, 1);
  assert.match(warn[0], /画角も推定/);
  assert.deepEqual(calibWarnings({ ok: true, focalLocked: true, k1Estimated: true, calib: { pointCount: 8 } }), []);
});

test('高さの点が足りないまま画角も推定しているときは、両方言う', () => {
  // 床だけで解いた場合。**画角と距離が分離しない**ことを実測値つきで言う
  const warn = calibWarnings({ ok: true, focalLocked: false, k1Estimated: true, raisedCount: 0, calib: { pointCount: 8 } });
  assert.equal(warn.length, 2);
  assert.match(warn[1], /高さの点が 0 本/);
  // 画角を固定してあるなら、高さの点が無くても縮退は起きない（余計な警告を出さない）
  assert.deepEqual(
    calibWarnings({ ok: true, focalLocked: true, k1Estimated: true, raisedCount: 0, calib: { pointCount: 8 } }), [],
  );
});

test('保存済みの解からも高さの点の本数を数える（refs の y>0）', () => {
  const calib = { refs: [{ y: 0 }, { y: 1.8 }, { y: 1.8 }, {}] };
  assert.equal(raisedRefCount(calib), 2);
  assert.equal(raisedRefCount(null), 0);
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
  assert.match(free.issues.join(' '), /6 個以上/);
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

test('壁の座標が既定のままなら、それが分かる（点の打ち方より先に疑う所）', () => {
  assert.equal(wallLooksDefault(null), true);
  assert.equal(wallLooksDefault({ wall: { corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] } }), true);
  assert.equal(wallLooksDefault({ wall: { corner: [-0.62, 0.71], endX: [0.55, 0.71], endZ: [-0.62, -0.4] } }), false);
});

// ---- (D) 状態遷移（2026-07-29 追加）--------------------------------------------
//
// ここが空白だったせいで、監査で見つかった実バグ 5 件が全部素通りしていた。
// 「開く」と「フレームを取り直す」は同じ判断をしなければならないのに、旧実装は
// 別々の式で書かれていて片方だけが正しかった。

const MEASURED_LAYOUT = {
  floor: { w: 2.4, d: 2.2 },
  wall: { corner: [-0.62, 0.71], endX: [0.55, 0.71], endZ: [-0.62, -0.4] },
  regPoints: [{ x: -0.5, z: 0.5, label: '玄関側' }, { x: 0.5, z: 0.5, label: '窓側' }],
};

// 保存済みの較正。refs は**その解で実際に投影した位置**にする（手で書いた適当な uv だと
// 「置き直しの疑い」が常に成立してしまい、判定そのものを試験できない）。
const SAVED_POSE = {
  fxPx: 452, fyPx: 452, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
  x: 0.1, y: 1.4, z: -1.2, yawDeg: 5, pitchDeg: -22, rollDeg: 0,
};
const SAVED = {
  ...SAVED_POSE,
  refs: [
    { x: -0.5, z: 0.5, y: 0 },
    { x: 0.5, z: 0.5, y: 0 },
    { x: -0.62, z: -0.4, y: 0 },
    { x: 0.55, z: 0.71, y: 0 },
    { x: -0.5, z: 0.5, y: 1.8 },
  ].map((p) => {
    const s = projectPoint(SAVED_POSE, p.x, p.y, p.z);
    return { u: s.u / 640, v: s.v / 480, x: p.x, z: p.z, y: p.y };
  }),
};

test('開くと前回の対応点が復元され、同じ解像度なら画角を固定できる', () => {
  const s = openSession({ id: 'A', calib: SAVED }, { w: 640, h: 480 }, MEASURED_LAYOUT);
  assert.equal(s.restoredCount, 5);
  assert.equal(s.lockedFocalPx, 452);
  assert.equal(s.lockFocal, true);
  assert.equal(s.needFrame, false);
  assert.equal(s.pts[4].y, 1.8);                      // 高さの拘束が消えない
});

test('復元した点には「何の目印か」が戻る（番号だけだと現場で直せない）', () => {
  const s = openSession({ id: 'A', calib: SAVED }, { w: 640, h: 480 }, MEASURED_LAYOUT);
  assert.match(s.pts[0].label, /位置合わせ点 1・玄関側/);
  assert.match(s.pts[4].label, /の上端$/);            // 高さの点はそれと分かる
  // layout から引けない点（消した印・手入力）は座標そのものを名前にする
  const orphan = resolvePointLabels([{ x: 9, z: 9, y: 0, u: 1, v: 1 }], MEASURED_LAYOUT);
  assert.equal(orphan[0].label, '(9.00, 9.00)');
});

test('映像が来る前に開くと点は空だが、🔄 で取り直したときに復元する', () => {
  // 旧実装は open() でしか復元せず、LIVE 前に開いた作業者は全点を打ち直していた
  const s = openSession({ id: 'A', calib: SAVED }, null, MEASURED_LAYOUT);
  assert.deepEqual(s.pts, []);
  assert.equal(s.needFrame, true);
  assert.equal(s.lockedFocalPx, 0);

  const r = refreshSession({ frame: null, pts: [], lockFocal: false },
    { w: 640, h: 480 }, SAVED, MEASURED_LAYOUT);
  assert.equal(r.restored, 5);
  assert.equal(r.pts.length, 5);
  assert.equal(r.lockedFocalPx, 452);
});

test('🔄 で解像度が変わったら固定画角を捨てる（別解像度の焦点距離で解かせない）', () => {
  // 旧実装は `lockableFocalPx(...) || lockedFocalPx` と書いていたため旧値が残り、
  // 嘘の解が「画角固定済み」として保存されて実機で人形が別の場所に立った
  const before = { frame: { w: 640, h: 480 }, pts: pointsFromRefs(SAVED.refs, 640, 480), lockFocal: true };
  const r = refreshSession(before, { w: 1280, h: 720 }, SAVED, MEASURED_LAYOUT);
  assert.equal(r.lockedFocalPx, 0);
  assert.equal(r.lockFocal, false);
  assert.equal(r.resized, true);
});

test('🔄 で縦横比が同じなら点は比例で移る（画角が同じなら対応は保たれる）', () => {
  const before = { frame: { w: 640, h: 480 }, pts: [{ x: 0, z: 0, y: 0, u: 320, v: 240, label: 'p' }], lockFocal: true };
  const r = refreshSession(before, { w: 1280, h: 960 }, SAVED, MEASURED_LAYOUT);
  assert.equal(r.pts.length, 1);
  assert.equal(r.pts[0].u, 640);
  assert.equal(r.pts[0].v, 480);
  assert.equal(r.dropped, 0);
  assert.equal(r.lockedFocalPx, 0);                   // 解像度が違うので固定は外れる
});

test('🔄 で縦横比が変わったら打っていた点は捨て、保存済み refs から引き直す', () => {
  const before = { frame: { w: 640, h: 480 }, pts: [{ x: 0, z: 0, y: 0, u: 320, v: 240 }], lockFocal: true };
  const r = refreshSession(before, { w: 640, h: 360 }, SAVED, MEASURED_LAYOUT);
  assert.equal(r.dropped, 1);
  assert.equal(r.restored, 5);
  assert.equal(r.pts.length, 5);
});

test('🔄 で同じ寸法なら、打っている途中の点はそのまま残る', () => {
  const mid = [{ x: 0, z: 0, y: 0, u: 100, v: 100, label: 'p' }];
  const r = refreshSession({ frame: { w: 640, h: 480 }, pts: mid, lockFocal: true },
    { w: 640, h: 480 }, SAVED, MEASURED_LAYOUT);
  assert.deepEqual(r.pts, mid);
  assert.equal(r.restored, 0);
  assert.equal(r.resized, false);
});

test('復元した点がいまの映像と大きくずれていれば、置き直しを疑う', () => {
  const pts = pointsFromRefs(SAVED.refs, 640, 480);
  // そのままなら疑わない（保存時の解で投影すればほぼ一致する）
  assert.equal(relocateSuspicion(SAVED, pts).suspect, false);
  // カメラを置き直した後は、復元点が無関係な画素に散らばる
  const moved = pts.map((p) => ({ ...p, u: p.u + 90, v: p.v - 70 }));
  assert.equal(relocateSuspicion(SAVED, moved).suspect, true);
  // 点が少なければ判定しない（誤検知の方が害）
  assert.equal(relocateSuspicion(SAVED, pts.slice(0, 2)).suspect, false);
});

// ---- (E) 出典（実測 / 推測）----------------------------------------------------

test('既定値の壁・床・仮想タイルは「測っていない」、実測した部屋と印は「測った」', () => {
  const def = candidatePoints({
    floor: { w: 1.8, d: 1.8 },
    wall: { corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] },
    grid: { tileM: 0.9, cols: 2, rows: 2, cells: ['0.', '..'] },
  });
  assert.equal(def.every((c) => c.source === SOURCE_ASSUMED), true,
    `推測でない点が混ざった: ${JSON.stringify(def.filter((c) => c.source !== SOURCE_ASSUMED))}`);

  const measured = candidatePoints({
    ...MEASURED_LAYOUT,
    calibPoints: [{ x: 0.3, z: -0.2, label: '棚の脚' }],
  });
  const marks = measured.filter((c) => c.kind === 'mark' || c.kind === 'reg' || c.kind === 'room');
  assert.equal(marks.every((c) => c.source === SOURCE_MEASURED), true);
  assert.equal(measured[0].label, '印・棚の脚');
});

test('既定 2 点へのフォールバックは「測っていない」（置いていないので現場に印が無い）', () => {
  const c = candidatePoints({ floor: { w: 2.4, d: 2.2 } });
  const reg = c.filter((x) => x.kind === 'reg');
  assert.equal(reg.length, 2);
  assert.equal(reg.every((x) => x.source === SOURCE_ASSUMED), true);
});

test('検証ワイヤーは「測った幾何だけ」に絞れる（実在しない部屋の絵を一次証拠にしない）', () => {
  const all = wireSegments(MEASURED_LAYOUT);
  const only = wireSegments(MEASURED_LAYOUT, { includeAssumed: false });
  assert.equal(all.length, only.length);              // 実測済み layout なら全部残る

  const fake = { floor: { w: 1.8, d: 1.8 }, wall: { corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] } };
  assert.ok(wireSegments(fake).length > 4);
  assert.equal(wireSegments(fake, { includeAssumed: false }).length, 0);
  assert.equal(hasMeasuredGeometry(fake), false);
  assert.equal(hasMeasuredGeometry(MEASURED_LAYOUT), true);
  assert.equal(floorLooksDefault({ floor: { w: 1.8, d: 1.8 } }), true);
  assert.equal(floorLooksDefault({ floor: { w: 2.4, d: 2.2 } }), false);
});

// ---- 点の質（高さの点を混ぜない）------------------------------------------------

test('高さの点は一直線の判定に混ぜない（基準点と同じ (x,z) なので必ず一直線になる）', () => {
  // 2026-07-29 監査: 床 4 点 + 高さ 1 点で「3 点が一直線」と誤警告し、正しく打っているのに直せと言った
  const floor4 = [
    { x: -0.6, z: 0.6, y: 0, u: 120, v: 300 }, { x: 0.6, z: 0.6, y: 0, u: 520, v: 300 },
    { x: 0.6, z: -0.6, y: 0, u: 480, v: 430 }, { x: -0.6, z: -0.6, y: 0, u: 160, v: 430 },
  ];
  const withTop = [...floor4, { x: -0.6, z: 0.6, y: 1.8, u: 120, v: 90 }];
  const qy = pointQuality(withTop, 640, 480, false);
  assert.equal(qy.collinear, false);
  assert.equal(qy.degenerate, false);
  assert.equal(qy.ready, true);
  assert.equal(qy.n, 4);                              // 床の点で数える
  assert.equal(qy.nRaised, 1);
});

test('高さの点が 2 本あれば、床 4 点でも歪みまで解ける（必要点数が下がる）', () => {
  const floor4 = [
    { x: -0.6, z: 0.6, y: 0, u: 120, v: 300 }, { x: 0.6, z: 0.6, y: 0, u: 520, v: 300 },
    { x: 0.6, z: -0.6, y: 0, u: 480, v: 430 }, { x: -0.6, z: -0.6, y: 0, u: 160, v: 430 },
  ];
  assert.equal(pointQuality(floor4, 640, 480, false).need, 6);
  const two = [...floor4,
    { x: -0.6, z: 0.6, y: 1.8, u: 120, v: 90 }, { x: 0.6, z: 0.6, y: 1.8, u: 520, v: 95 }];
  assert.equal(pointQuality(two, 640, 480, false).need, 4);
  assert.equal(pointQuality(two, 640, 480, false).issues.some((s) => /床があと/.test(s)), false);
});

test('品質の見出しは同じことを 2 度言わない（旧: 「6 点まであと 6」と「あと 4 点」が並んだ）', () => {
  const empty = pointQuality([], 640, 480, false);
  assert.equal(qualityHeadline(empty), '床 0 点 + 高さ 0 点 / あと 4 点で解けます');
  const ok = pointQuality([
    { x: -0.6, z: 0.6, y: 0, u: 120, v: 300 }, { x: 0.6, z: 0.6, y: 0, u: 520, v: 300 },
    { x: 0.6, z: -0.6, y: 0, u: 480, v: 430 }, { x: -0.6, z: -0.6, y: 0, u: 160, v: 430 },
  ], 640, 480, true);
  assert.equal(qualityHeadline(ok), '床 4 点 + 高さ 0 点 / 解けます');
});

// ---- レンズ ID の照合（卓と実機で同じ判定にする）--------------------------------

test('レンズが違えば較正は無効（Unity は捨てるのに卓が「較正済み」と言っていた）', () => {
  const calib = { fxPx: 430, srcW: 640, srcH: 480, lensId: 'wide' };
  assert.equal(calibMatchesSource(calib, 640, 480, 'wide'), true);
  assert.equal(calibMatchesSource(calib, 640, 480, 'tele'), false);
  assert.equal(calibMatchesSource(calib, 640, 480, ''), true);      // /info が無い配信アプリは普通にある
  assert.equal(calibMatchesSource({ fxPx: 430, srcW: 640, srcH: 480 }, 640, 480, 'wide'), true);
  assert.match(calibBadgeText({ calib }, 640, 480, 'tele'), /レンズ wide 用/);
});

// ---- 精度（当てはまりと分ける）--------------------------------------------------

const TRUE_CALIB = {
  x: 0.15, y: 1.42, z: -1.25, yawDeg: 8, pitchDeg: -22, rollDeg: 1.5,
  fxPx: 452, fyPx: 452, cxPx: 320, cyPx: 240, k1: 0, srcW: 640, srcH: 480,
};
const FLOOR_GRID = [
  { x: -0.8, z: 0.8 }, { x: 0, z: 0.8 }, { x: 0.8, z: 0.8 },
  { x: -0.8, z: 0 }, { x: 0.8, z: 0 },
  { x: -0.8, z: -0.6 }, { x: 0.8, z: -0.6 },
].map((p) => ({ ...p, y: 0 }));
const observe = (list) => list.map((p) => {
  const s = projectPoint(TRUE_CALIB, p.x, p.y || 0, p.z);
  return { ...p, u: s.u, v: s.v };
});

test('leave-one-out は「知らない点をどれだけ当てられるか」を測る（rms とは別物）', () => {
  const obs = observe(FLOOR_GRID);
  const loo = leaveOneOutError(obs, 640, 480, { fixedFocalPx: TRUE_CALIB.fxPx });
  assert.equal(loo.ok, true);
  assert.ok(loo.samples >= 5, `サンプル ${loo.samples}`);
  // 誤差ゼロの観測なので、伏せた点もほぼ当たる
  assert.ok(loo.medianPx < 1, `medianPx=${loo.medianPx}`);
  assert.ok(loo.medianM < 0.02, `medianM=${loo.medianM}`);
});

test('クリック誤差を入れると精度は落ちる（当てはまりが良くても精度は別）', () => {
  const jitter = [1.5, -1.2, 0.9, -1.6, 1.1, -0.8, 1.4];
  const obs = observe(FLOOR_GRID).map((p, i) => ({ ...p, u: p.u + jitter[i], v: p.v - jitter[i] }));
  const loo = leaveOneOutError(obs, 640, 480, { fixedFocalPx: TRUE_CALIB.fxPx });
  assert.equal(loo.ok, true);
  assert.ok(loo.medianPx > 0.5, `medianPx=${loo.medianPx}`);
});

test('点が足りなければ精度は測れないと言う（黙って 0 を出さない）', () => {
  const loo = leaveOneOutError(observe(FLOOR_GRID.slice(0, 4)), 640, 480, {});
  assert.equal(loo.ok, false);
  assert.match(loo.reason, /5 個以上/);
  assert.match(accuracyLine(loo, 1.2), /精度は測れません/);
});

test('精度の一行は「実際のずれ cm」と「当てはまり px」を分けて言う', () => {
  const line = accuracyLine({ ok: true, medianPx: 1.4, medianM: 0.031, worstPx: 3, samples: 7 }, 1.1);
  assert.match(line, /実際のずれ およそ 3cm/);
  assert.match(line, /当てはまり 1\.1px/);
});

test('画素のずれは、その点の場所の床の上での距離へ直す（遠いほど 1px が長い）', () => {
  const near = projectPoint(TRUE_CALIB, 0, 0, -0.6);
  const far = projectPoint(TRUE_CALIB, 0, 0, 0.8);
  const mNear = pixelToMeters(TRUE_CALIB, near.u, near.v, 10);
  const mFar = pixelToMeters(TRUE_CALIB, far.u, far.v, 10);
  assert.ok(mNear > 0 && mFar > 0);
  assert.ok(mFar > mNear, `遠い点の方が 1px あたり長いはず: near=${mNear} far=${mFar}`);
});

test('水平画角 ↔ 焦点距離は往復する（レンズを画角で登録できるように）', () => {
  assert.ok(Math.abs(focalFromHfov(90, 640) - 320) < 1e-6);
  assert.ok(Math.abs(hfovFromFocal(focalFromHfov(67.5, 640), 640) - 67.5) < 1e-9);
  assert.equal(focalFromHfov(0, 640), 0);
  assert.equal(focalFromHfov(90, 0), 0);
});

// ---- レンズ（内部パラメータ）----------------------------------------------------
//
// 旧実装は画角を固定する値の供給源が「このカメラの前回の解」しか無く、4 点でフリーに解いた
// いい加減な f が翌日「正確な内部パラメータ」として再利用されていた。
// しかも f を固定すると rms はむしろ下がるので、誤りが良い数字に化けて発見できない。

const LENS = {
  id: 'lens_1', name: 'Pixel 7a 超広角', fxPx: 431.2, srcW: 640, srcH: 480, k1: 0.18,
  measuredAtIso: '2026-07-29T20:10:00', pointCount: 9, raisedCount: 3, rmsPx: 0.8, accuracyM: 0.02,
};

test('レンズは壊れていれば読まない（id と焦点距離が要る）', () => {
  assert.equal(normalizeLens(null), null);
  assert.equal(normalizeLens({ name: 'x' }), null);
  assert.equal(normalizeLens({ id: 'a', fxPx: 0 }), null);
  const n = normalizeLens({ id: 'a', fxPx: 400 });
  assert.equal(n.name, 'a');                     // 名前が無ければ id を出す
  assert.equal(n.pointCount, 0);
  assert.deepEqual(lensesOf({ lenses: [LENS, null, { id: 'x' }] }).map((l) => l.id), ['lens_1']);
  assert.deepEqual(lensesOf(null), []);
});

test('レンズの焦点距離は同じ解像度でだけ使える（px は解像度に従属する）', () => {
  assert.equal(lensFocalFor(LENS, 640, 480), 431.2);
  assert.equal(lensFocalFor(LENS, 1280, 720), 0);
  assert.equal(lensFocalFor(null, 640, 480), 0);
  // 解像度を記録していない古いレンズは、いまの映像で使わせる（情報が無いだけで矛盾はしていない）
  assert.equal(lensFocalFor({ ...LENS, srcW: 0, srcH: 0 }, 640, 480), 431.2);
});

test('カメラはレンズを参照し、割り当ては付け外しできる', () => {
  const cams = [{ id: 'A' }, { id: 'B', lensRef: 'lens_1' }];
  assert.equal(resolveLens([LENS], cams[1]).name, 'Pixel 7a 超広角');
  assert.equal(resolveLens([LENS], cams[0]), null);
  assert.equal(resolveLens([], cams[1]), null);          // 参照切れは null（例外にしない）

  const on = applyLensToCameras(cams, 'A', 'lens_1');
  assert.equal(on[0].lensRef, 'lens_1');
  assert.equal(cams[0].lensRef, undefined);              // 元配列は書き換えない
  const off = applyLensToCameras(on, 'A', '');
  assert.equal('lensRef' in off[0], false);
});

test('レンズを消したら、参照しているカメラからも外す（参照切れを残さない）', () => {
  const cams = [{ id: 'A', lensRef: 'lens_1' }, { id: 'B', lensRef: 'lens_2' }];
  const next = detachLensFromCameras(cams, 'lens_1');
  assert.equal('lensRef' in next[0], false);
  assert.equal(next[1].lensRef, 'lens_2');
  assert.deepEqual(removeLens([LENS, { ...LENS, id: 'lens_2' }], 'lens_1').map((l) => l.id), ['lens_2']);
  assert.equal(nextLensId([LENS]), 'lens_2');
  assert.equal(nextLensId([]), 'lens_1');
});

test('レンズには測定条件を焼く（素性の分からない画角を同型機へ広めない）', () => {
  const obs = observe(FLOOR_GRID);
  const withTop = [...obs, ...[{ x: -0.8, z: 0.8, y: 1.8 }, { x: 0.8, z: 0.8, y: 1.8 }]
    .map((p) => { const s = projectPoint(TRUE_CALIB, p.x, p.y, p.z); return { ...p, u: s.u, v: s.v }; })];
  const r = calibrateFromFloorPoints(withTop, 640, 480, { solvedAtIso: '2026-07-29T21:00:00' });
  assert.equal(r.ok, true);
  const lens = lensFromSolve(r, { id: 'lens_9', name: 'テスト', accuracyM: 0.012 });
  assert.equal(lens.pointCount, 7);                      // 床の点
  assert.equal(lens.raisedCount, 2);                     // 高さの点
  assert.equal(lens.measuredAtIso, '2026-07-29T21:00:00');
  assert.equal(lens.accuracyM, 0.012);
  assert.ok(Math.abs(lens.fxPx - TRUE_CALIB.fxPx) < 5, `fx=${lens.fxPx}`);
  assert.equal(lensFromSolve({ ok: false }, { id: 'x', name: 'y' }), null);

  const list = upsertLens([LENS], lens);
  assert.equal(list.length, 2);
  assert.equal(upsertLens(list, { ...lens, name: '改名' }).length, 2);
  assert.equal(upsertLens(list, { ...lens, name: '改名' })[1].name, '改名');
});

test('素性の悪いレンズは、選ぶ前に理由を言う', () => {
  assert.deepEqual(lensTrustIssues(LENS), []);           // 床 9 点 + 高さ 3 本 + ずれ 2cm なら文句なし
  const thin = lensTrustIssues({ ...LENS, pointCount: 4, raisedCount: 0, accuracyM: 0.28 });
  assert.equal(thin.length, 3);
  assert.match(thin.join(' '), /高さの点が 0 本/);
  assert.match(thin.join(' '), /床の点が 4 個/);
  assert.match(thin.join(' '), /28cm/);
  assert.match(lensSummary(LENS), /640×480/);
  assert.match(lensSummary(LENS), /床 9 点 \+ 高さ 3 本/);
  assert.match(lensSummary(LENS), /ずれ 2cm/);
});

// ---- 解像度をまたぐ（現場で撮っておいた映像から解く）--------------------------

test('同じ縦横比なら較正は別の解像度へ移せる（投影が一致する）', () => {
  // 現場で撮っておいた映像（Quest 経由の記録は 480×360）で解いて、配信（640×480）で使う。
  const small = { ...TRUE_CALIB, srcW: 480, srcH: 360, fxPx: 339, fyPx: 339, cxPx: 240, cyPx: 180, rmsPx: 1.2 };
  const big = rescaleCalib(small, 640, 480);
  assert.equal(big.srcW, 640);
  assert.ok(Math.abs(big.fxPx - 339 * 640 / 480) < 1e-9);
  assert.ok(Math.abs(big.cxPx - 320) < 1e-9);
  assert.ok(Math.abs(big.rmsPx - 1.6) < 1e-9);
  // 同じ床点は、同じ相対位置へ写る（＝画角も姿勢も変わっていない）
  for (const p of [{ x: 0, z: 0 }, { x: -0.8, z: 0.8 }, { x: 0.8, z: -0.6 }]) {
    const a = projectPoint(small, p.x, 0, p.z);
    const b = projectPoint(big, p.x, 0, p.z);
    assert.ok(Math.abs(a.u / 480 - b.u / 640) < 1e-9, `u ${a.u / 480} vs ${b.u / 640}`);
    assert.ok(Math.abs(a.v / 360 - b.v / 480) < 1e-9);
  }
});

test('縦横比が違えば移さない（切り取られた映像で嘘の較正を作らない）', () => {
  const c = { ...TRUE_CALIB, srcW: 640, srcH: 480 };
  assert.equal(rescaleCalib(c, 640, 360), null);
  assert.equal(rescaleCalib(null, 640, 480), null);
  assert.equal(rescaleCalib({ fxPx: 0 }, 640, 480), null);
  assert.deepEqual(rescaleCalib(c, 640, 480), { ...c });      // 同寸法はそのまま複製
});

test('固定する画角はレンズが優先（前回の解より素性が分かっているため）', () => {
  const cam = { id: 'A', lensRef: 'lens_1', calib: SAVED };
  const withLens = openSession(cam, { w: 640, h: 480 }, MEASURED_LAYOUT, LENS);
  assert.equal(withLens.lockedFocalPx, 431.2);
  assert.equal(withLens.focalFrom, 'lens');
  // レンズが無ければ従来どおり前回の解へ落ちる
  const noLens = openSession({ id: 'A', calib: SAVED }, { w: 640, h: 480 }, MEASURED_LAYOUT, null);
  assert.equal(noLens.lockedFocalPx, 452);
  assert.equal(noLens.focalFrom, 'last');
  // 解像度が違うレンズは使わない（前回の解も使えないので固定なし）
  const other = openSession(cam, { w: 1280, h: 720 }, MEASURED_LAYOUT, LENS);
  assert.equal(other.lockedFocalPx, 0);
  assert.equal(other.focalFrom, '');
  assert.equal(refreshSession({ frame: { w: 640, h: 480 }, pts: [], lockFocal: true },
    { w: 640, h: 480 }, SAVED, MEASURED_LAYOUT, LENS).lockedFocalPx, 431.2);
});
