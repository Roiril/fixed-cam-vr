// 導入演出（run.intro）の既定・合計秒・本番前チェックを固定する。
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  INTRO_DEFAULT, INTRO_STAGE_KEYS, introConfig, introStageSec,
  introDurationLabel, calibratedCameraCount, introPreflightRow, introWireGeometry,
  START_SPOT_DEFAULT, START_RADIUS_MIN, START_RADIUS_MAX,
  normalizeStartSpot, startSpotOf, startSpotZoneIssue,
} from './intro-model.js';

// 段 3 が描く幾何は **layout.room の壁 + layout.floor** から来る（layout.wall は読まない）。
// 既定の L（1m × 1m）から外れた形を「測った壁」として渡す。
const MEASURED_ROOM = () => ({
  floorY: 0, floorW: 1.8, floorD: 1.8,
  walls: [
    { id: 'w1', x1: -0.9, z1: 0.9, x2: 0.9, z2: 0.9, h: 1.8, thick: 0.04 },
    { id: 'w2', x1: -0.9, z1: 0.9, x2: -0.9, z2: -0.9, h: 1.8, thick: 0.04 },
  ],
  props: [],
});
// 「導入が成立している layout」= 床 + 測った壁 + 開始位置あり。開始位置が無いだけで ⚠ が出るので、
// 他の条件を試すテストにはこれを渡す（ノイズで判定が読めなくなるのを防ぐ）。
const measuredLayout = () => ({
  floor: { w: 1.8, d: 1.8 },
  room: MEASURED_ROOM(), hasRoom: true,
  startSpot: { x: 0, z: -0.6 },
});
const calibratedCams = () => ([{ id: 'A', calib: { fxPx: 431.2 } }]);

// ゾーン判定つきの layout。12×12 タイルの上半分（北）をカメラ 1・下半分（南）をカメラ 0 に塗る。
// row0 = 北端（z=+0.9）・col0 = 西端（x=-0.9）。
const zonedLayout = (startSpot, order = [0, 1]) => ({
  room: MEASURED_ROOM(), hasRoom: true,
  floor: { w: 1.8, d: 1.8 },
  course: { order },
  overlapM: 0.08,
  grid: {
    tileM: 0.15, cols: 12, rows: 12,
    cells: [...Array(6).fill('1'.repeat(12)), ...Array(6).fill('0'.repeat(12))],
  },
  startSpot,
});

test('intro が無い show.json はコード既定で成立する（既存データを壊さない）', () => {
  const cfg = introConfig({ totalLaps: 3 });
  assert.deepEqual(cfg, { ...INTRO_DEFAULT });
});

test('段の秒は 0〜20 に丸め、上限秒は 10〜60 に丸める', () => {
  const cfg = introConfig({ intro: { realSec: -3, degradeSec: 99, maxSec: 500 } });
  assert.equal(cfg.realSec, 0);
  assert.equal(cfg.degradeSec, 20);
  assert.equal(cfg.maxSec, 60);
  assert.equal(introConfig({ intro: { maxSec: 1 } }).maxSec, 10);
});

test('乱れの強さは 0〜1 に丸め、数値でない値は既定へ落とす', () => {
  assert.equal(introConfig({ intro: { glitchOnSwap: 5 } }).glitchOnSwap, 1);
  assert.equal(introConfig({ intro: { glitchOnSwap: -1 } }).glitchOnSwap, 0);
  assert.equal(introConfig({ intro: { glitchOnSwap: '' } }).glitchOnSwap, INTRO_DEFAULT.glitchOnSwap);
  assert.equal(introConfig({ intro: { realSec: 'abc' } }).realSec, INTRO_DEFAULT.realSec);
});

test('チェック類は false を明示した時だけ落ちる（欠落は ON）', () => {
  const off = introConfig({ intro: { enabled: false, showCameraMarks: false, showRoomWire: false, raiseHandPrompt: false } });
  assert.deepEqual(
    [off.enabled, off.showCameraMarks, off.showRoomWire, off.raiseHandPrompt],
    [false, false, false, false],
  );
  const on = introConfig({ intro: {} });
  assert.deepEqual([on.enabled, on.showCameraMarks, on.showRoomWire, on.raiseHandPrompt], [true, true, true, true]);
});

test('合計秒は実際に流れる長さ（段 3 は段 2 と重なるので二度足さない・既定は 13.1s）', () => {
  // ⚠ この数字は Unity の IntroLogicTests.TotalSec_CountsTheOverlappingStageOnce と**同じ値**。
  //   own = 2.5 - 3.5 × (1 - 0.6) = 1.1 / 計 = 1.5 + 3.5 + 1.1 + 2.5 + 4.5 = 13.1
  //   片方だけ直すと、卓の表示と実機の尺が沈黙して食い違う。
  assert.equal(introStageSec(introConfig({})), 13.1);
  // 単純和より短い（重なりの分だけ）。ここが同じになったら式が壊れている。
  const naive = INTRO_STAGE_KEYS.reduce((x, k) => x + INTRO_DEFAULT[k], 0);
  assert.ok(introStageSec(introConfig({})) < naive, `${introStageSec(introConfig({}))} < ${naive}`);
});

test('合計秒は正規化前の生データからも出せる（欠落は既定で埋まる）', () => {
  // own = max(0.5, 2.5 - 3.5 × 0.4) = 1.1
  assert.equal(introStageSec(introConfig({ intro: { realSec: 2, swapSec: 10 } })), 2 + 3.5 + 1.1 + 2.5 + 10);
});

test('尺の表示は「演出 Ns / 慣らし Ms」', () => {
  assert.equal(introDurationLabel(introConfig({}), 20), '演出 13.1s / 慣らし 20s');
  assert.equal(introDurationLabel(introConfig({}), undefined), '演出 13.1s / 慣らし 0s');
});

test('較正済みの数え方は fxPx > 1 のカメラ（本番前チェックの 🎯 較正 行と同じ）', () => {
  assert.equal(calibratedCameraCount(null), 0);
  assert.equal(calibratedCameraCount([{ id: 'A' }, { id: 'B', calib: { fxPx: 0 } }]), 0);
  assert.equal(calibratedCameraCount([{ id: 'A', calib: { fxPx: 431 } }, { id: 'B' }]), 1);
});

test('導入を出さない設定なら本番前チェックの行を出さない（黙る）', () => {
  const row = introPreflightRow({ run: { intro: { enabled: false } }, layout: measuredLayout(), cameras: calibratedCams() });
  assert.equal(row, null);
});

test('床の寸法が無ければ ❌（段 3 の線が 1 本も出ない）', () => {
  const row = introPreflightRow({ run: {}, layout: {}, cameras: calibratedCams() });
  assert.equal(row.s, 'ng');
  assert.equal(row.label, '🎬 導入');
  assert.match(row.detail, /床の寸法/);
});

// 段 3 の壁は layout.room だけから起きる（IntroStructureWireLogic.AppendRoom）。
// 旧 layout.wall を見ていた頃は、壁を著作しても ❌ が消えず / room を著作しても ❌ のままだった。
test('壁が著作されていなければ ❌（床の外周とカメラの印だけになる）', () => {
  const row = introPreflightRow({
    run: {}, layout: { floor: { w: 1.8, d: 1.8 }, startSpot: { x: 0, z: -0.6 } }, cameras: calibratedCams(),
  });
  assert.equal(row.s, 'ng');
  assert.match(row.detail, /部屋の壁/);
});

test('段 3 の判定は layout.wall ではなく layout.room を見る', () => {
  const legacyOnly = { floor: { w: 1.8, d: 1.8 }, wall: { corner: [-0.9, 0.9], endX: [0.9, 0.9], endZ: [-0.9, -0.9] } };
  assert.equal(introWireGeometry(legacyOnly).wallCount, 0);
  assert.equal(introWireGeometry(measuredLayout()).wallCount, 2);
});

test('部屋の present-flag が false なら壁は無いものとして数える（幽霊の壁を作らない）', () => {
  const geo = introWireGeometry({ floor: { w: 1.8, d: 1.8 }, room: MEASURED_ROOM(), hasRoom: false });
  assert.equal(geo.wallCount, 0);
});

test('壁が既定の L と同じ形なら ⚠（測った値かは判定できないと言う）', () => {
  const layout = {
    floor: { w: 1.8, d: 1.8 },
    startSpot: { x: 0, z: -0.6 },
    hasRoom: true,
    room: {
      floorY: 0, floorW: 1.8, floorD: 1.8,
      walls: [
        { id: 'w1', x1: -0.5, z1: -0.5, x2: -0.5, z2: 0.5, h: 1.8, thick: 0.04 },
        { id: 'w2', x1: -0.5, z1: 0.5, x2: 0.5, z2: 0.5, h: 1.8, thick: 0.04 },
      ],
      props: [],
    },
  };
  assert.equal(introWireGeometry(layout).looksDefaultL, true);
  const row = introPreflightRow({ run: {}, layout, cameras: calibratedCams() });
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /既定の L/);
});

test('壁と床の線を出さない設定なら幾何を要求しない（黙って ❌ にしない）', () => {
  const row = introPreflightRow({
    run: { intro: { showRoomWire: false } },
    layout: { startSpot: { x: 0, z: -0.6 } },
    cameras: calibratedCams(),
  });
  assert.equal(row.s, 'ok');
});

test('較正済みのカメラが 1 台も無ければ ⚠（カメラの印が出せない）', () => {
  const row = introPreflightRow({ run: {}, layout: measuredLayout(), cameras: [{ id: 'A' }] });
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /較正済みのカメラ/);
});

test('合計秒が上限を超えたら ⚠（超えた段は実機が飛ばす）', () => {
  const row = introPreflightRow({
    run: { introMinSec: 20, intro: { maxSec: 10 } },
    layout: measuredLayout(),
    cameras: calibratedCams(),
  });
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /上限 10s/);
});

test('未測定と上限超が同時なら ❌ が勝つ（直す順が決まる）', () => {
  const row = introPreflightRow({ run: { intro: { maxSec: 10 } }, layout: {}, cameras: [] });
  assert.equal(row.s, 'ng');
});

test('成立していれば ✅ に尺を出す', () => {
  const row = introPreflightRow({ run: { introMinSec: 20 }, layout: measuredLayout(), cameras: calibratedCams() });
  assert.deepEqual(row, { s: 'ok', label: '🎬 導入', detail: '演出 13.1s / 慣らし 20s' });
});

// ---- 開始位置（layout.startSpot）------------------------------------------------

test('開始位置は座標が無ければ未設定（null）', () => {
  assert.equal(normalizeStartSpot(null), null);
  assert.equal(normalizeStartSpot({}), null);
  assert.equal(normalizeStartSpot({ x: 0 }), null);
  assert.equal(normalizeStartSpot({ x: 'abc', z: 0 }), null);
  assert.equal(startSpotOf({}), null);
  assert.equal(startSpotOf(null), null);
});

test('開始位置は半径を 0.15〜1.0 に丸め、ラベル空欄は「スタート」で埋める', () => {
  assert.deepEqual(normalizeStartSpot({ x: 0.12345, z: -0.6 }),
    { x: 0.123, z: -0.6, radiusM: START_SPOT_DEFAULT.radiusM, label: START_SPOT_DEFAULT.label });
  assert.equal(normalizeStartSpot({ x: 0, z: 0, radiusM: 0.01 }).radiusM, START_RADIUS_MIN);
  assert.equal(normalizeStartSpot({ x: 0, z: 0, radiusM: 9 }).radiusM, START_RADIUS_MAX);
  assert.equal(normalizeStartSpot({ x: 0, z: 0, radiusM: 'x' }).radiusM, START_SPOT_DEFAULT.radiusM);
  assert.equal(normalizeStartSpot({ x: 0, z: 0, label: '  ' }).label, START_SPOT_DEFAULT.label);
  assert.equal(normalizeStartSpot({ x: 0, z: 0, label: '入口' }).label, '入口');
});

test('開始位置がスタート区間の中なら黙る', () => {
  // order[0] = 0（南半分）。z=-0.6 は南 → 一致。
  assert.equal(startSpotZoneIssue(zonedLayout({ x: 0, z: -0.6 })), null);
});

test('開始位置がスタート区間の外なら、そこの担当カメラごと返す', () => {
  // order[0] = 0（南半分）なのに z=+0.6（北半分 = カメラ 1）に置いた。
  assert.deepEqual(startSpotZoneIssue(zonedLayout({ x: 0, z: 0.6 })), { startCamera: 0, spotCamera: 1 });
});

test('開始位置が未割当タイルの上でもスタート区間の外として返す（spotCamera=-1）', () => {
  const lay = zonedLayout({ x: 0, z: 0.6 });
  lay.grid.cells = [...Array(6).fill('.'.repeat(12)), ...Array(6).fill('0'.repeat(12))];
  assert.deepEqual(startSpotZoneIssue(lay), { startCamera: 0, spotCamera: -1 });
});

test('ゾーンが解決できない layout では黙る（直しようのない ⚠ を出さない）', () => {
  // 開始位置が無い / course.order が無い / grid が無い / 1 枚も塗っていない
  assert.equal(startSpotZoneIssue(zonedLayout(undefined)), null);
  assert.equal(startSpotZoneIssue({ ...zonedLayout({ x: 0, z: 0.6 }), course: {} }), null);
  assert.equal(startSpotZoneIssue({ startSpot: { x: 0, z: 0.6 }, course: { order: [0] } }), null);
  const blank = zonedLayout({ x: 0, z: 0.6 });
  blank.grid.cells = Array(12).fill('.'.repeat(12));
  assert.equal(startSpotZoneIssue(blank), null);
});

test('開始位置が未設定なら ⚠（スタッフが手で始める運用になる）', () => {
  const lay = measuredLayout(); delete lay.startSpot;
  const row = introPreflightRow({ run: {}, layout: lay, cameras: calibratedCams() });
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /開始位置が未設定/);
});

test('開始位置がスタート区間の外なら ⚠ にカメラ名を出す', () => {
  const row = introPreflightRow({
    run: { introMinSec: 20 },
    layout: zonedLayout({ x: 0, z: 0.6 }),
    cameras: [{ id: 'A', calib: { fxPx: 431.2 } }, { id: 'B' }],
  });
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /スタート区間（カメラ A）の外/);
  assert.match(row.detail, /カメラ B/);
});

test('導入が無効なら開始位置が未設定でも黙る', () => {
  const lay = measuredLayout(); delete lay.startSpot;
  assert.equal(introPreflightRow({ run: { intro: { enabled: false } }, layout: lay, cameras: calibratedCams() }), null);
});

test('卓の既定値と capture-server.py の _default_show が一致している', async () => {
  const { readFile } = await import('node:fs/promises');
  const py = await readFile(new URL('./capture-server.py', import.meta.url), 'utf8');
  const m = py.match(/'intro':\s*\{([\s\S]*?)\}\}/);
  assert.ok(m, "_default_show に run.intro が無い");
  const body = m[1];
  for (const k of INTRO_STAGE_KEYS) {
    assert.match(body, new RegExp(`'${k}':\\s*${INTRO_DEFAULT[k]}\\b`), `${k} が食い違っている`);
  }
  assert.match(body, new RegExp(`'maxSec':\\s*${INTRO_DEFAULT.maxSec}\\b`));
  assert.match(body, new RegExp(`'glitchOnSwap':\\s*${INTRO_DEFAULT.glitchOnSwap}`));
  assert.match(body, /'edgeColor':\s*'#ffffff'/);
  for (const k of ['enabled', 'showCameraMarks', 'showRoomWire', 'raiseHandPrompt']) {
    assert.match(body, new RegExp(`'${k}':\\s*True`), `${k} が食い違っている`);
  }
});

test('慣らし歩行が演出より短いと切り落とされることを警告する', () => {
  // introMinSec は導入相全体の下限。演出 13.1s に対し 10s だと演出の途中で本編へ移る。
  // RestartIntroClock は演出の終わりにしか打たれないので、実機は黙って切り替わる。
  const row = introPreflightRow({
    run: { introEnabled: true, introMinSec: 10, intro: { enabled: true } },
    layout: measuredLayout(), cameras: calibratedCams(),
  });
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /慣らし歩行 10s が演出 13\.1s より短い/);
});

test('慣らし歩行が演出より長ければその警告は出ない', () => {
  const row = introPreflightRow({
    run: { introEnabled: true, introMinSec: 20, intro: { enabled: true } },
    layout: measuredLayout(), cameras: calibratedCams(),
  });
  assert.ok(!/慣らし歩行/.test(row ? row.detail : ''));
});
