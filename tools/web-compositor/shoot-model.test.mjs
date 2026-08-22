// 当日の素材撮りの判定を固定する。
//   実行: node --test tools/web-compositor/
//
// ここが甘いと、当日「撮ったのに実機で出ない」を現場で初めて知ることになる。

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  SHOTS, SHORT_MARGIN_SEC, shotName, neededHeadSec, cutCount, shotStatus,
  allShotStatus, isFromToday, approachPrecheck, adoptName, headWindow,
  setShots, shotsLoaded, countdownSecOf,
} from './shoot-model.js';

// ショット定義は shots.json が正（卓のサーバ・配信スマホも同じファイルを読む）。
// ブラウザは fetch、ここは fs。**同じ実体を食わせる**ことが要点で、定義を写さない。
const SHOTS_JSON = JSON.parse(
  readFileSync(fileURLToPath(new URL('./shots.json', import.meta.url)), 'utf-8'));
setShots(SHOTS_JSON.shots);

// 2 周目 C の著作を模した segments（0104: 人形視点だけの飛び飛び → ライブ + 黒マスク）。
const segments = () => [{
  lap: 2, camera: 2, takes: [{
    id: 'L2C2#0',
    steps: [
      { source: 'clip', cueId: 'pov_1', trimStartSec: -1, durSec: 1.0 },
      { source: 'clip', cueId: 'pov_2', trimStartSec: -1, durSec: 0.9 },
      { source: 'clip', cueId: 'pov_3', trimStartSec: -1, durSec: 0.8 },
      { source: 'clip', cueId: 'pov_4', trimStartSec: -1, durSec: 1.4 },
      { source: 'live', cueId: '', durKind: 'untilZoneChange', durSec: 0, swapHold: true },
    ],
  }],
}];

// 1 本の素材を trim で進めながら複数カットが使う形（機能の検査用・現行の著作には無い）。
const multiCutSegments = () => [{
  lap: 1, camera: 0, takes: [{
    id: 'T#0',
    steps: [
      { source: 'clip', cueId: 'reel', trimStartSec: 0, durSec: 1.2 },
      { source: 'clip', cueId: 'reel', trimStartSec: 1.9, durSec: 1.0 },
      { source: 'clip', cueId: 'reel', trimStartSec: 5.0, durSec: 0.5 },
    ],
  }],
}];

const cues = (over = {}) => ({
  cues: SHOTS.map((s) => ({ id: s.cueId, sourceUrl: over[s.cueId] ?? '' })),
});

test('SHOTS: 5 本・cueId が重複しない（偽ライブは 0104 で廃止）', () => {
  assert.equal(SHOTS.length, 5);
  assert.equal(new Set(SHOTS.map(shotName)).size, 5);
  // 撮る順は onsite-checklist §0 と対（2 周目 B → POV ①〜④）。
  assert.equal(shotName(SHOTS[0]), 'pov_0');
  assert.equal(shotName(SHOTS[1]), 'pov_1');
  assert.ok(!SHOTS.some((s) => shotName(s) === 'fake_live_C'), '偽ライブを復活させない');
});

test('neededHeadSec: 同じ cue を trim を進めながら使うなら max(trim + dur)', () => {
  // 0+1.2 / 1.9+1.0 / 5.0+0.5 → 5.5 が要求
  assert.equal(neededHeadSec(multiCutSegments(), 'reel'), 5.5);
});

test('neededHeadSec: 1 カットの POV はその尺（trim の -1 は 0 扱い）', () => {
  assert.equal(neededHeadSec(segments(), 'pov_1'), 1.0);
  assert.equal(neededHeadSec(segments(), 'pov_4'), 1.4);
});

test('neededHeadSec: 参照が無ければ null（今の著作では使わない素材）', () => {
  assert.equal(neededHeadSec(segments(), 'pov_0'), null);
  assert.equal(neededHeadSec(segments(), ''), null);
  assert.equal(neededHeadSec(null, 'pov_1'), null);
});

test('neededHeadSec: 尺が別条件で決まるカット（durSec<=0）は数えない', () => {
  // untilZoneChange のカットは durSec=0。ここを数えると「0 秒でよい」になる。
  const segs = [{ lap: 1, camera: 0, takes: [{ id: 'T#1', steps: [
    { source: 'plate', cueId: 'plate_X', durKind: 'untilZoneChange', durSec: 0 },
  ] }] }];
  assert.equal(neededHeadSec(segs, 'plate_X'), null);
});

test('cutCount: 同じ cue の複数カットを数える・未使用は 0', () => {
  assert.equal(cutCount(multiCutSegments(), 'reel'), 3);
  assert.equal(cutCount(segments(), 'pov_2'), 1);
  assert.equal(cutCount(segments(), 'pov_0'), 0);
});

// ---- 1 ショットの状態 ----
test('shotStatus: 素材未採用は ng', () => {
  const st = shotStatus(SHOTS[1], cues(), segments(), null, []);
  assert.equal(st.level, 'ng');
  assert.match(st.reason, /未採用/);
});

test('shotStatus: 採用済みでファイルが実在すれば ok', () => {
  const state = cues({ pov_1: '/recordings/pov_1_t01.mp4' });
  const st = shotStatus(SHOTS[1], state, segments(), new Set(['/recordings/pov_1_t01.mp4']), []);
  assert.equal(st.level, 'ok');
});

test('shotStatus: 実在しないファイルは ng（消したか名前を変えた）', () => {
  const state = cues({ pov_1: '/recordings/gone.mp4' });
  const st = shotStatus(SHOTS[1], state, segments(), new Set(), []);
  assert.equal(st.level, 'ng');
  assert.match(st.reason, /見つからない/);
});

// trim を進めながら複数カットが使う素材（要求 5.5s）に対する尺の判定。
// 現行の著作に multi-cut は無いが、機能としては残っている（0104 の前はこれが偽ライブだった）。
const reelShot = { cueId: 'reel', label: '検査用', dev: 'pov', hint: '', recSec: 10 };
const reelState = (url) => ({ cues: [{ id: 'reel', sourceUrl: url }] });

test('shotStatus: 尺が要求に足りなければ ng（実機がカットを飛ばす）', () => {
  const url = '/recordings/reel_t01.mp4';
  const takes = [{ url, durSec: 4.0 }];   // 要求 5.5s
  const st = shotStatus(reelShot, reelState(url), multiCutSegments(), new Set([url]), takes);
  assert.equal(st.level, 'ng');
  assert.match(st.reason, /1\.5s 足りない/);
});

test('shotStatus: 余裕が少なければ warn（頭がぶれると足りなくなる）', () => {
  const url = '/recordings/reel_t02.mp4';
  const takes = [{ url, durSec: 5.5 + SHORT_MARGIN_SEC - 0.1 }];
  const st = shotStatus(reelShot, reelState(url), multiCutSegments(), new Set([url]), takes);
  assert.equal(st.level, 'warn');
  assert.equal(st.ok, true, '警告であって不合格ではない');
});

test('shotStatus: 尺が分からないテイクは尺で落とさない', () => {
  const url = '/recordings/x.mp4';
  const state = cues({ pov_1: url });
  const st = shotStatus(SHOTS[1], state, segments(), new Set([url]), [{ url }]);
  assert.equal(st.level, 'ok');
});

test('allShotStatus: 5 本ぶん返す', () => {
  assert.equal(allShotStatus(cues(), segments(), null, {}).length, 5);
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

test('approachPrecheck: ④ 端末が熱い状態で撮った素材は warn', () => {
  const base = okSetup();
  const u = base.state.cues.find((c) => c.id === 'pov_3').sourceUrl;
  base.manifest[u] = { capturedAt: '2026-08-22T10:00:00', throttleStage: 2 };
  const row = approachPrecheck(base);
  assert.equal(row.s, 'warn');
  assert.match(row.detail, /端末が熱い/);
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


// ---- shots.json（3 者が読む単一の正）--------------------------------------
test('読み込む前の判定は ❌「読めていない」（空を「使っていない」と混同しない）', async () => {
  // ⚠ 同じモジュールは 1 度しか初期化されないので、**未読込の状態を作るには読み直す**。
  //   ここを「setShots([]) で空にする」で代用すると、検査したい経路（shotsReady=false）を
  //   1 行も通らない。
  const fresh = await import('./shoot-model.js?unloaded=1');
  assert.equal(fresh.shotsLoaded(), false);
  const row = fresh.approachPrecheck({ state: cues(), segments: segments() });
  assert.equal(row.s, 'ng');
  assert.match(row.detail, /shots\.json/);
  // 読み込んだ側は従来どおり答える（相互に汚さない）。
  assert.equal(shotsLoaded(), true);
});

test('shots.json の各ショットが必須の欄を持つ', () => {
  assert.ok(SHOTS.length >= 1);
  for (const s of SHOTS) {
    assert.match(s.cueId, /^[A-Za-z0-9_-]+$/, `cueId は streamer の shot= に渡す（安全な文字だけ）: ${s.cueId}`);
    assert.ok(s.label && s.hint, `${s.cueId} に label / hint が要る（スマホの撮影指示に出る）`);
    assert.ok(s.recSec > 0, `${s.cueId} の recSec`);
    assert.ok(['pov', 'cam'].includes(s.dev), `${s.cueId} の dev`);
  }
});

test('countdownSecOf は定義値 → 手持ちの既定 3 → 据置き 0', () => {
  assert.equal(countdownSecOf({ dev: 'pov', countdownSec: 5 }), 5);
  assert.equal(countdownSecOf({ dev: 'pov' }), 3);
  assert.equal(countdownSecOf({ dev: 'cam' }), 0);
});


// ---- 移植の一致（node / Python が同じ答えを出す）-----------------------------
// 卓のサーバ（capture-server.py）が同じ式を持つ。**同じフィクスチャを両方が食う**ので、
// 片方だけ直せばどちらかが落ちる。Python 側は test_shoot_plan.py。
test('shoot-fixture.json の期待値と一致する（Python 移植との共通の物差し）', () => {
  const fx = JSON.parse(
    readFileSync(fileURLToPath(new URL('./shoot-fixture.json', import.meta.url)), 'utf-8'));
  for (const [cueId, want] of Object.entries(fx.expect.neededHeadSec)) {
    assert.equal(neededHeadSec(fx.segments, cueId), want, `neededHeadSec(${cueId})`);
  }
  for (const [cueId, want] of Object.entries(fx.expect.cutCount)) {
    assert.equal(cutCount(fx.segments, cueId), want, `cutCount(${cueId})`);
  }
});
