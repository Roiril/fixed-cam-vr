// timeline-model.js の純関数を node:test で固定する（DOM 非依存）。
//   実行: node --test tools/web-compositor/
//   fixture 再生成: UPDATE_FIXTURE=1 node --test tools/web-compositor/timeline-model.test.mjs
//     → Assets/Tests/Fixtures/show_timeline_canonical.json を serialize 出力から書き出す。
//   引数なし実行は「serialize 出力 == 現 fixture」を assert する（serialize が変わると落ち再生成を強制）。
//   → Web serialize と Unity 契約テストが共有 fixture 経由で機械的に一致する。

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { newSeg, newAssign, serializeTimeline, normalizeTimeline, migrateFromSchedule } from './timeline-model.js';
import { FX_DEFAULT } from './common.js';

const FIXTURE_URL = new URL('../../Assets/Tests/Fixtures/show_timeline_canonical.json', import.meta.url);

// ---- mixed-flag サンプル timeline（has* の全 true/false 順列を網羅） --------------
//   in-memory 編集モデルを模す（newSeg/newAssign が override/post/insert を常に持つ）。
function buildSample() {
  // seg A (lap1, cam0): cue2 本 — (i) hasOverride:true / (ii) hasOverride:false; hasPost:false; hasInsert:false
  const segA = newSeg(1, 0);
  const a1 = newAssign('cue_A_1'); a1.hasOverride = true;
  a1.override = { strength: 0.5, fadeIn: 1.2, fadeOut: 0.8, trimStart: 0, trimEnd: 0 };
  const a2 = newAssign('cue_A_2'); a2.hasOverride = false;
  segA.cues = [a1, a2];

  // seg B (lap1, cam1): cues 空; hasPost:true; hasInsert:true で insert.hasPost:false
  const segB = newSeg(1, 1);
  segB.hasPost = true; segB.post = { ...FX_DEFAULT, exposure: 0.3 };
  segB.hasInsert = true;
  segB.insert.anchor = 'exit'; segB.insert.camera = 2; segB.insert.durationSec = 3;
  segB.insert.cueId = ''; segB.insert.hasPost = false;

  // seg C (lap2, cam2): cue1 本 hasOverride:false; hasPost:false; hasInsert:true で insert.hasPost:true
  const segC = newSeg(2, 2);
  const c1 = newAssign('cue_C_1'); c1.hasOverride = false;
  segC.cues = [c1];
  segC.hasInsert = true;
  segC.insert.anchor = 'enter'; segC.insert.camera = 0; segC.insert.durationSec = 4;
  segC.insert.cueId = 'cue_C_scare'; segC.insert.hasPost = true;
  segC.insert.post = { ...FX_DEFAULT, saturation: 1.5 };

  // seg D (lap2, cam0): 空（cues 空 && !hasPost && !hasInsert）— filter で除去される検証用
  const segD = newSeg(2, 0);

  return { rev: 3, segments: [segA, segB, segC, segD] };
}

// ---- fixture の静的部（cameras/cues/layout/control/schedule/post）------------------
//   Unity 契約テストが ShowState 全体をパースできるよう、実 show.json と同形にする。
function staticShow() {
  return {
    rev: 3,
    cameras: [
      { id: 'A', sourceId: 'Phone01', host: '', port: 8080, auth: '', post: { ...FX_DEFAULT, exposure: -1.0 } },
      { id: 'B', sourceId: 'Phone02', host: '', port: 8080, auth: '', post: { ...FX_DEFAULT, contrast: 1.2 } },
      { id: 'C', sourceId: 'Phone03', host: '', port: 8080, auth: '', post: { ...FX_DEFAULT } },
    ],
    cues: [
      { id: 'cue_A_1', name: 'A1', camera: 'A', maskUrl: '', sourceUrl: '/captures/a1.jpg', strength: 1, loop: false, fadeIn: 0.3, fadeOut: 0.3 },
      { id: 'cue_A_2', name: 'A2', camera: 'A', maskUrl: '', sourceUrl: '/captures/a2.jpg', strength: 1, loop: false, fadeIn: 0.3, fadeOut: 0.3 },
      { id: 'cue_C_1', name: 'C1', camera: 'C', maskUrl: '', sourceUrl: '/captures/c1.jpg', strength: 1, loop: false, fadeIn: 0.3, fadeOut: 0.3 },
      { id: 'cue_C_scare', name: 'Cscare', camera: 'C', maskUrl: '', sourceUrl: '/captures/scare.mp4', strength: 1, loop: false, fadeIn: 0.3, fadeOut: 0.3 },
    ],
    post: { ...FX_DEFAULT },
    control: { activeCue: null, cameraOverride: '', autoFollow: true },
    layout: {
      rev: 1,
      overlapM: 0.08, hysteresisM: 0.12,
      grid: { tileM: 0.15, cols: 3, rows: 3, cells: ['012', '012', '012'] },
      course: { order: [0, 1, 2] },
    },
    schedule: { rev: 3, entries: [] },
  };
}

function buildFixture() {
  return { ...staticShow(), timeline: serializeTimeline(buildSample()) };
}

// JSON 値等価（キー順非依存）。
function jsonEqual(a, b) {
  assert.deepStrictEqual(JSON.parse(JSON.stringify(a)), JSON.parse(JSON.stringify(b)));
}
// show.json 往復を模す（保存 → ネットワーク → 再パース）。
function wire(obj) { return JSON.parse(JSON.stringify(obj)); }

// fixture 再生成モード（テスト実行前に一度だけ）。
if (process.env.UPDATE_FIXTURE) {
  const fx = buildFixture();
  writeFileSync(FIXTURE_URL, JSON.stringify(fx, null, 2) + '\n', { encoding: 'utf-8' });
  console.log('[timeline-model.test] fixture regenerated:', FIXTURE_URL.pathname);
}

// ---- 1. 省略不変条件（B1 直接ガード） ------------------------------------------
test('serialize omits nested keys when has*=false, keeps them when true', () => {
  const out = wire(serializeTimeline(buildSample()));
  const byKey = (lap, camera) => out.segments.find((s) => s.lap === lap && s.camera === camera);

  const segA = byKey(1, 0);
  assert.ok(segA, 'seg A present');
  // cue (i) hasOverride:true → override キーあり
  assert.equal('override' in segA.cues[0], true);
  assert.equal(segA.cues[0].hasOverride, true);
  assert.equal(segA.cues[0].override.strength, 0.5);
  // cue (ii) hasOverride:false → override キーなし
  assert.equal('override' in segA.cues[1], false);
  assert.equal(segA.cues[1].hasOverride, false);
  // seg A: post/insert なし
  assert.equal('post' in segA, false);
  assert.equal(segA.hasPost, false);
  assert.equal('insert' in segA, false);
  assert.equal(segA.hasInsert, false);

  const segB = byKey(1, 1);
  assert.ok(segB, 'seg B present');
  assert.equal('post' in segB, true);
  assert.equal(segB.hasPost, true);
  assert.equal(segB.post.exposure, 0.3);
  assert.equal('insert' in segB, true);
  assert.equal(segB.hasInsert, true);
  // insert.hasPost:false → insert.post キーなし（入れ子の入れ子まで省く）
  assert.equal('post' in segB.insert, false);
  assert.equal(segB.insert.hasPost, false);
  assert.equal(segB.insert.camera, 2);

  const segC = byKey(2, 2);
  assert.ok(segC, 'seg C present');
  assert.equal('override' in segC.cues[0], false);
  assert.equal(segC.cues[0].hasOverride, false);
  assert.equal('post' in segC, false);
  assert.equal(segC.hasPost, false);
  assert.equal('insert' in segC, true);
  // insert.hasPost:true → insert.post キーあり
  assert.equal('post' in segC.insert, true);
  assert.equal(segC.insert.hasPost, true);
  assert.equal(segC.insert.post.saturation, 1.5);
});

// ---- 2. 空 segment の除去 -------------------------------------------------------
test('serialize drops empty segments (no cues && !hasPost && !hasInsert)', () => {
  const out = serializeTimeline(buildSample());
  // seg D (lap2, cam0) は空 → 出力に無い
  assert.equal(out.segments.some((s) => s.lap === 2 && s.camera === 0), false);
  assert.equal(out.segments.length, 3);
});

// ---- 3. round-trip 冪等（has* 保存・値等価） -----------------------------------
test('normalize(serialize(sample)) re-serialized is JSON-equal to first serialize', () => {
  const first = serializeTimeline(buildSample());
  const second = serializeTimeline(normalizeTimeline(wire(first)));
  jsonEqual(second, first);
});

// ---- 4. migrate（(lap,camera) 畳み・hasOverride:false で override 省略）----------
test('migrateFromSchedule folds by (lap,camera) and emits cues without override', () => {
  const tl = migrateFromSchedule([
    { lap: 1, camera: 0, cueId: 'x', delaySec: 1, once: false },
    { lap: 1, camera: 0, cueId: 'y', delaySec: 0, once: true },
    { lap: 2, camera: 1, cueId: 'z', delaySec: 0, once: true },
  ], 1);
  assert.equal(tl.segments.length, 2);
  const seg = tl.segments.find((s) => s.lap === 1 && s.camera === 0);
  assert.equal(seg.cues.length, 2);
  assert.equal(seg.cues[0].cueId, 'x');
  assert.equal(seg.cues[0].delaySec, 1);
  assert.equal(seg.cues[0].once, false);
  // serialize で override キーが出ないこと（hasOverride:false）
  const out = wire(serializeTimeline(tl));
  const outSeg = out.segments.find((s) => s.lap === 1 && s.camera === 0);
  assert.equal('override' in outSeg.cues[0], false);
  assert.equal(outSeg.cues[0].hasOverride, false);
});

// ---- 5. 共有 fixture 整合（serialize 出力に縛る） ------------------------------
test('Assets/Tests/Fixtures/show_timeline_canonical.json .timeline == serializeTimeline(sample)', () => {
  const raw = readFileSync(FIXTURE_URL, { encoding: 'utf-8' });
  const fx = JSON.parse(raw);
  jsonEqual(fx.timeline, serializeTimeline(buildSample()));
});
