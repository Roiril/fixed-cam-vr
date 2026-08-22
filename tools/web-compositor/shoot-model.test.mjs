// 当日の素材撮りの判定を固定する。
//   実行: node --test tools/web-compositor/
//
// ここが甘いと、当日「撮ったのに実機で出ない」を現場で初めて知ることになる。

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  SHOTS, SHORT_MARGIN_SEC, shotName, neededHeadSec, cutCount, shotStatus,
  allShotStatus, isFromToday, approachPrecheck, adoptName, headWindow,
} from './shoot-model.js';

// 2 周目 C の著作を模した segments（偽ライブが 4 カット・trim を進める）。
const segments = () => [{
  lap: 2, camera: 2, takes: [{
    id: 'L2C2#0',
    steps: [
      { source: 'clip', cueId: 'fake_live_C', trimStartSec: 0, durSec: 1.2 },
      { source: 'clip', cueId: 'pov_1', trimStartSec: -1, durSec: 0.7 },
      { source: 'clip', cueId: 'fake_live_C', trimStartSec: 1.9, durSec: 1.0 },
      { source: 'clip', cueId: 'pov_2', trimStartSec: -1, durSec: 0.7 },
      { source: 'clip', cueId: 'fake_live_C', trimStartSec: 3.6, durSec: 0.8 },
      { source: 'clip', cueId: 'pov_3', trimStartSec: -1, durSec: 0.6 },
      { source: 'clip', cueId: 'fake_live_C', trimStartSec: 5.0, durSec: 0.5 },
      { source: 'clip', cueId: 'pov_4', trimStartSec: -1, durSec: 1.2 },
      { source: 'plate', cueId: 'plate_C', durSec: 0 },
    ],
  }],
}];

const cues = (over = {}) => ({
  cues: SHOTS.map((s) => ({ id: s.cueId, sourceUrl: over[s.cueId] ?? '' })),
});

test('SHOTS: 6 本・cueId が重複しない', () => {
  assert.equal(SHOTS.length, 6);
  assert.equal(new Set(SHOTS.map(shotName)).size, 6);
  // 撮る順は onsite-checklist §0 と対（2 周目 B → 偽ライブ → POV）。
  assert.equal(shotName(SHOTS[0]), 'pov_0');
  assert.equal(shotName(SHOTS[1]), 'fake_live_C');
});

test('neededHeadSec: 同じ cue を進めながら使う偽ライブは max(trim + dur)', () => {
  // 0+1.2 / 1.9+1.0 / 3.6+0.8 / 5.0+0.5 → 5.5 が要求
  assert.equal(neededHeadSec(segments(), 'fake_live_C'), 5.5);
});

test('neededHeadSec: 1 カットの POV はその尺（trim の -1 は 0 扱い）', () => {
  assert.equal(neededHeadSec(segments(), 'pov_1'), 0.7);
  assert.equal(neededHeadSec(segments(), 'pov_4'), 1.2);
});

test('neededHeadSec: 参照が無ければ null（今の著作では使わない素材）', () => {
  assert.equal(neededHeadSec(segments(), 'pov_0'), null);
  assert.equal(neededHeadSec(segments(), ''), null);
  assert.equal(neededHeadSec(null, 'pov_1'), null);
});

test('neededHeadSec: 尺が別条件で決まるカット（durSec<=0）は数えない', () => {
  // plate_C は untilZoneChange なので durSec=0。ここを数えると「0 秒でよい」になる。
  assert.equal(neededHeadSec(segments(), 'plate_C'), null);
});

test('cutCount: 偽ライブは 4 カット・POV は 1 カット', () => {
  assert.equal(cutCount(segments(), 'fake_live_C'), 4);
  assert.equal(cutCount(segments(), 'pov_2'), 1);
  assert.equal(cutCount(segments(), 'pov_0'), 0);
});

// ---- 1 ショットの状態 ----
test('shotStatus: 素材未採用は ng', () => {
  const st = shotStatus(SHOTS[2], cues(), segments(), null, []);
  assert.equal(st.level, 'ng');
  assert.match(st.reason, /未採用/);
});

test('shotStatus: 採用済みでファイルが実在すれば ok', () => {
  const state = cues({ pov_1: '/recordings/pov_1_t01.mp4' });
  const st = shotStatus(SHOTS[2], state, segments(), new Set(['/recordings/pov_1_t01.mp4']), []);
  assert.equal(st.level, 'ok');
});

test('shotStatus: 実在しないファイルは ng（消したか名前を変えた）', () => {
  const state = cues({ pov_1: '/recordings/gone.mp4' });
  const st = shotStatus(SHOTS[2], state, segments(), new Set(), []);
  assert.equal(st.level, 'ng');
  assert.match(st.reason, /見つからない/);
});

test('shotStatus: 尺が要求に足りなければ ng（実機がカットを飛ばす）', () => {
  const url = '/recordings/fake_live_C_t01.mp4';
  const state = cues({ fake_live_C: url });
  const takes = [{ url, durSec: 4.0 }];   // 要求 5.5s
  const st = shotStatus(SHOTS[1], state, segments(), new Set([url]), takes);
  assert.equal(st.level, 'ng');
  assert.match(st.reason, /1\.5s 足りない/);
});

test('shotStatus: 余裕が少なければ warn（頭がぶれると足りなくなる）', () => {
  const url = '/recordings/fake_live_C_t02.mp4';
  const state = cues({ fake_live_C: url });
  const takes = [{ url, durSec: 5.5 + SHORT_MARGIN_SEC - 0.1 }];
  const st = shotStatus(SHOTS[1], state, segments(), new Set([url]), takes);
  assert.equal(st.level, 'warn');
  assert.equal(st.ok, true, '警告であって不合格ではない');
});

test('shotStatus: 尺が分からないテイクは尺で落とさない', () => {
  const url = '/recordings/x.mp4';
  const state = cues({ fake_live_C: url });
  const st = shotStatus(SHOTS[1], state, segments(), new Set([url]), [{ url }]);
  assert.equal(st.level, 'ok');
});

test('allShotStatus: 6 本ぶん返す', () => {
  assert.equal(allShotStatus(cues(), segments(), null, {}).length, 6);
});

// ---- 撮影日 ----
test('isFromToday: 日付だけを見る（時刻は無視）', () => {
  assert.equal(isFromToday('2026-08-22T09:00:00', '2026-08-22T23:59:00'), true);
  assert.equal(isFromToday('2026-08-21T23:59:00', '2026-08-22T00:01:00'), false);
  assert.equal(isFromToday(null, '2026-08-22T00:00:00'), null, '判定できないは false と別');
});

// ---- 本番前チェックの 1 行（効く順） ----
const okSetup = () => {
  const urls = {};
  for (const s of SHOTS) urls[shotName(s)] = `/recordings/${shotName(s)}_t01.mp4`;
  const assets = new Set(Object.values(urls));
  const takesByShot = {};
  const manifest = {};
  for (const s of SHOTS) {
    const u = urls[shotName(s)];
    takesByShot[shotName(s)] = [{ url: u, durSec: 20 }];
    manifest[u] = { capturedAt: '2026-08-22T10:00:00', throttleStage: 0 };
  }
  return { state: cues(urls), segments: segments(), assets, takesByShot, manifest,
           devices: [], nowIso: '2026-08-22T12:00:00' };
};

test('approachPrecheck: すべて揃っていれば ok', () => {
  const row = approachPrecheck(okSetup());
  assert.equal(row.s, 'ok');
});

test('approachPrecheck: この台本が接近を使っていなければ黙る', () => {
  const row = approachPrecheck({ ...okSetup(), segments: [] });
  assert.equal(row.s, 'ok');
  assert.match(row.detail, /使っていない/);
});

test('approachPrecheck: ① 採用漏れが最優先', () => {
  const base = okSetup();
  base.state = cues({ ...Object.fromEntries(SHOTS.map((s) => [shotName(s), `/recordings/${shotName(s)}_t01.mp4`])), pov_2: '' });
  base.devices = [{ id: 'C', recording: true }];   // ② も同時に壊す
  const row = approachPrecheck(base);
  assert.equal(row.s, 'ng');
  assert.match(row.detail, /pov_2/, '採用漏れが録りっぱなしより先に出る');
});

test('approachPrecheck: ② 録りっぱなしの端末（本番中ずっと熱と容量を食う）', () => {
  const row = approachPrecheck({ ...okSetup(), devices: [{ id: 'C', recording: true }] });
  assert.equal(row.s, 'ng');
  assert.match(row.detail, /録画したまま/);
});

test('approachPrecheck: ③ 前の日に撮った素材（照明が違う）', () => {
  const base = okSetup();
  const u = base.state.cues.find((c) => c.id === 'pov_1').sourceUrl;
  base.manifest[u] = { capturedAt: '2026-08-21T10:00:00', throttleStage: 0 };
  const row = approachPrecheck(base);
  assert.equal(row.s, 'ng');
  assert.match(row.detail, /今日撮ったものではない/);
});

test('approachPrecheck: ④ 熱で画質が落ちた素材は warn', () => {
  const base = okSetup();
  const u = base.state.cues.find((c) => c.id === 'pov_3').sourceUrl;
  base.manifest[u] = { capturedAt: '2026-08-22T10:00:00', throttleStage: 2 };
  const row = approachPrecheck(base);
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /熱で画質/);
});

test('approachPrecheck: 台帳が無い素材は日付・熱で落とさない（判定できないだけ）', () => {
  const row = approachPrecheck({ ...okSetup(), manifest: {} });
  assert.equal(row.s, 'ok');
});

// ---- 採用の名前 ----
test('adoptName: テイクごとに一意（Quest のキャッシュ対策）', () => {
  assert.equal(adoptName('pov_2', 1, '20260822_143000'), 'pov_2_t01_20260822_143000.mp4');
  assert.notEqual(adoptName('pov_2', 1, '20260822_143000'),
                  adoptName('pov_2', 2, '20260822_143000'));
});

test('headWindow: 頭の窓は in-point から要求尺ぶん', () => {
  assert.deepStrictEqual(headWindow(5.5, 0), { start: 0, end: 5.5 });
  assert.deepStrictEqual(headWindow(0.7, 0.3), { start: 0.3, end: 1.0 });
  // 要求が無い（今の著作では使わない）ときも試写はできる。
  assert.deepStrictEqual(headWindow(null, 0), { start: 0, end: 2 });
});
