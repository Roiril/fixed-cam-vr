// 導入演出（run.intro）の既定・合計秒・本番前チェックを固定する。
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  INTRO_DEFAULT, INTRO_STAGE_KEYS, introConfig, introStageSec,
  introDurationLabel, calibratedCameraCount, introPreflightRow,
} from './intro-model.js';

// 測った壁（既定 1m×1m の L から外れている）。これが無いと段 3 が成立しない。
const MEASURED_WALL = { corner: [-0.9, 0.9], endX: [0.9, 0.9], endZ: [-0.9, -0.9] };
const measuredLayout = () => ({ wall: { ...MEASURED_WALL } });
const calibratedCams = () => ([{ id: 'A', calib: { fxPx: 431.2 } }]);

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

test('合計秒は段 1〜5 の単純和（既定は 31s）', () => {
  assert.equal(introStageSec(introConfig({})), 31);
  const sum = INTRO_STAGE_KEYS.reduce((a, k) => a + INTRO_DEFAULT[k], 0);
  assert.equal(introStageSec(introConfig({})), sum);
});

test('合計秒は正規化前の生データからも出せる（欠落は既定で埋まる）', () => {
  assert.equal(introStageSec(introConfig({ intro: { realSec: 2, swapSec: 10 } })), 2 + 8 + 6 + 5 + 10);
});

test('尺の表示は「演出 Ns / 慣らし Ms」', () => {
  assert.equal(introDurationLabel(introConfig({}), 20), '演出 31s / 慣らし 20s');
  assert.equal(introDurationLabel(introConfig({}), undefined), '演出 31s / 慣らし 0s');
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

test('部屋の寸法が既定値のままなら ❌（段 3 の線が実物に重ならない）', () => {
  const row = introPreflightRow({ run: {}, layout: {}, cameras: calibratedCams() });
  assert.equal(row.s, 'ng');
  assert.equal(row.label, '🎬 導入');
  assert.match(row.detail, /寸法/);
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
  assert.deepEqual(row, { s: 'ok', label: '🎬 導入', detail: '演出 31s / 慣らし 20s' });
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
