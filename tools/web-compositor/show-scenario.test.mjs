// show.json → シミュレータ入力（ゾーン展開 + 演出定義）の純関数を固定する。
//   実行: node --test tools/web-compositor/
//
// ここが Unity と食い違うと「卓では出るのに実機で出ない（逆も）」になる。移植元は
//   ZoneLayoutSolver.SolveGrid / ClampHysteresis / ParseGridCells（zone-layout.js）
//   TakeRunner.SetTakes / BuildStepDurations（show-scenario.js）

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  cellRect, parseGridCells, solveGridZones, clampHysteresis, zonesFromLayout,
} from './zone-layout.js';
import { buildScenarioConfig, resolveStepDuration, stepCamera, serializeScenario } from './show-scenario.js';
import { pickZone, runScenario, parseScenario, FALLBACK_STEP_DUR_SEC } from './scenario-engine.js';
import { TAKE, newTake, newStep, newSeg } from './timeline-model.js';

// ---- grid → 矩形（ZoneLayoutSolver）------------------------------------------

test('cellRect: row0 = 北端（z 最大）/ col0 = 西端（x 最小）', () => {
  const r = cellRect(0, 0, 12, 12, 0.15);
  assert.equal(round(r.xLo), -0.9);
  assert.equal(round(r.xHi), -0.75);
  assert.equal(round(r.zHi), 0.9);
  assert.equal(round(r.zLo), 0.75);
  const last = cellRect(11, 11, 12, 12, 0.15);
  assert.equal(round(last.xHi), 0.9);
  assert.equal(round(last.zLo), -0.9);
});

test('parseGridCells: 未知文字・行長不足は未割当（-1）に倒す', () => {
  const g = parseGridCells(['01x', '2.'], 2, 3);
  assert.deepStrictEqual(g, [0, 1, -1, 2, -1, -1]);
  assert.deepStrictEqual(parseGridCells(null, 1, 2), [-1, -1]);
});

test('solveGridZones: 同一カメラのタイルは 1 矩形へ畳み、overlap/2 だけ広がる', () => {
  const rects = solveGridZones({ rows: 2, cols: 2, tileM: 1, cells: [0, 0, 0, 0], overlapM: 0.2 });
  assert.equal(rects.length, 1);
  assert.equal(round(rects[0].centerX), 0);
  assert.equal(round(rects[0].centerZ), 0);
  assert.equal(round(rects[0].halfX), 1.1);
  assert.equal(round(rects[0].halfZ), 1.1);
  assert.equal(rects[0].label, 'cam0#0');
});

test('solveGridZones: 貪欲分解は「行方向 → 行間」の決定的順序', () => {
  // 0 0
  // 0 .
  const rects = solveGridZones({ rows: 2, cols: 2, tileM: 1, cells: [0, 0, 0, -1], overlapM: 0 });
  assert.equal(rects.length, 2, '下段が欠けるので行間マージできず 2 矩形');
  assert.deepStrictEqual(rects.map((r) => r.label), ['cam0#0', 'cam0#1']);
  assert.equal(round(rects[0].centerZ), 0.5, '1 本目は北の行');
  assert.equal(round(rects[1].centerZ), -0.5);
});

test('solveGridZones: 未割当だけの grid はゾーンを作らない', () => {
  assert.deepStrictEqual(solveGridZones({ rows: 2, cols: 2, tileM: 1, cells: [-1, -1, -1, -1], overlapM: 0 }), []);
});

test('clampHysteresis: overlap/2 を超える shrink はデッドバンドを消すのでクランプ', () => {
  assert.equal(round(clampHysteresis(0.12, 0.08)), 0.04);
  assert.equal(round(clampHysteresis(0.12, 0.24)), 0.12);
  assert.equal(clampHysteresis(-1, 0.08), 0);
});

test('zonesFromLayout: grid があれば箱を作り、cuts だけなら実行しない', () => {
  const layout = {
    floor: { w: 3, d: 3 }, overlapM: 0.2, hysteresisM: 0.5,
    grid: { tileM: 1, cols: 3, rows: 3, cells: ['000', '111', '222'] },
  };
  const z = zonesFromLayout(layout);
  assert.equal(z.source, 'grid');
  assert.equal(z.boxes.length, 3);
  assert.equal(round(z.hysteresisShrink), 0.1, 'overlap 0.2 → shrink は 0.1 へクランプ');
  // 北の行（z=+1）はカメラ 0、南の行（z=-1）はカメラ 2。
  assert.equal(z.boxes[pickZone(z.boxes, 0, 1, 1, -1, z.hysteresisShrink, true)].camera, 0);
  assert.equal(z.boxes[pickZone(z.boxes, 0, 1, -1, -1, z.hysteresisShrink, true)].camera, 2);

  const cutsOnly = zonesFromLayout({ cuts: [{ s: 0.1, camAfter: 1 }] });
  assert.equal(cutsOnly.source, 'cuts');
  assert.equal(cutsOnly.boxes.length, 0);
  assert.match(cutsOnly.warning, /grid/);
});

// ---- 演出定義の変換（TakeRunner.SetTakes / BuildStepDurations）----------------

function sampleState() {
  const seg1 = newSeg(1, 0);
  seg1.takes = [newTake('t_enter', {
    at: TAKE.AT_ENTER, offsetSec: 2, ifMissed: TAKE.MISSED_SKIP, policy: TAKE.POLICY_YIELD, once: false,
    steps: [newStep({ source: TAKE.SRC_LIVE, camera: 2, durKind: TAKE.DUR_SEC, durSec: 3 })],
  })];
  const seg2 = newSeg(2, 1);
  seg2.takes = [newTake('t_exit', {
    at: TAKE.AT_EXIT,
    steps: [
      newStep({ source: TAKE.SRC_CLIP, assetUrl: 'x.mp4', durKind: TAKE.DUR_SEC, durSec: 0 }),
      newStep({ source: TAKE.SRC_INHERIT, cueId: 'cue_B', durKind: TAKE.DUR_UNTIL_CLIP_END }),
    ],
  })];
  return {
    cameras: [{ id: 'A' }, { id: 'B' }, { id: 'C' }],
    cues: [{ id: 'cue_B', sourceUrl: '/captures/01.mp4', trimStart: 1, trimEnd: 6 }],
    control: { minDwellSec: 0.8, switchCooldownSec: 0 },
    layout: {
      floor: { w: 3, d: 3 }, overlapM: 0.2, hysteresisM: 0.1,
      grid: { tileM: 1, cols: 3, rows: 3, cells: ['000', '111', '222'] },
      course: { order: [0, 1, 2] },
    },
    timeline: { rev: 3, schema: 3, segments: [seg1, seg2] },
  };
}

test('buildScenarioConfig: 区間 take → Def（at/ifMissed/policy/once）を写す', () => {
  const { cfg } = buildScenarioConfig(sampleState());
  assert.equal(cfg.takes.length, 2);
  const [a, b] = cfg.takes;
  assert.deepStrictEqual(
    { lap: a.lap, camera: a.camera, onExit: a.onExit, offsetSec: a.offsetSec, skip: a.skipWhenMissed, once: a.once, yield: a.yieldOnZoneChange },
    { lap: 1, camera: 0, onExit: false, offsetSec: 2, skip: true, once: false, yield: true });
  assert.equal(b.onExit, true);
  assert.equal(b.skipWhenMissed, false, 'ifMissed 既定は fireOnExit（出す側）');
  assert.deepStrictEqual(cfg.takeIds, ['t_enter', 't_exit']);
});

test('buildScenarioConfig: カメラを動かすのは live カットだけ', () => {
  const { cfg } = buildScenarioConfig(sampleState());
  assert.deepStrictEqual(cfg.stepCameras, [[2], [-1, -1]]);
});

test('buildScenarioConfig: control の 0 / 未指定はコード既定へ戻る', () => {
  const { cfg } = buildScenarioConfig(sampleState());
  assert.equal(cfg.dwellSec, 0.8, '正値は現場調整値を採用');
  assert.equal(cfg.cooldownSec, 0.5, '0 は未指定 → 既定 0.5');
  assert.equal(cfg.startCamera, 0, 'course.order[0]');
});

test('resolveStepDuration: 秒指定 / 素材なし untilClipEnd / trim 推定 / 不明', () => {
  assert.deepStrictEqual(resolveStepDuration(newStep({ durKind: TAKE.DUR_SEC, durSec: 3 }), null),
    { durSec: 3, kind: 'exact' });
  assert.deepStrictEqual(resolveStepDuration(newStep({ durKind: TAKE.DUR_SEC, durSec: 0 }), null),
    { durSec: FALLBACK_STEP_DUR_SEC, kind: 'exact' }, '0 は既定尺');
  assert.deepStrictEqual(resolveStepDuration(newStep({ durKind: TAKE.DUR_UNTIL_CLIP_END }), null),
    { durSec: FALLBACK_STEP_DUR_SEC, kind: 'fallback' }, '素材が無ければ Unity と同じ 4s で畳む');
  assert.deepStrictEqual(
    resolveStepDuration(newStep({ durKind: TAKE.DUR_UNTIL_CLIP_END, cueId: 'c' }), { sourceUrl: 'v.mp4', trimStart: 1, trimEnd: 6 }),
    { durSec: 5, kind: 'estimated' });
  assert.deepStrictEqual(
    resolveStepDuration(newStep({ durKind: TAKE.DUR_UNTIL_CLIP_END, cueId: 'c' }), { sourceUrl: 'v.mp4' }),
    { durSec: -1, kind: 'unknown' }, 'trim が無ければ尺は分からない（watchdog 任せ）');
});

test('stepCamera: camera<0 の live はカメラを動かさない', () => {
  assert.equal(stepCamera(newStep({ source: TAKE.SRC_LIVE, camera: 1 })), 1);
  assert.equal(stepCamera(newStep({ source: TAKE.SRC_LIVE, camera: -1 })), -1);
  assert.equal(stepCamera(newStep({ source: TAKE.SRC_CLIP, camera: 1 })), -1);
});

test('buildScenarioConfig: 推定・不明の尺と未解決 cue は警告に出る（黙って捏造しない）', () => {
  // (a) trim があるので推定できる → 「推定」警告
  const est = buildScenarioConfig(sampleState()).meta;
  assert.ok(est.warnings.some((w) => w.includes('推定')), '推定した尺は必ず告知する');

  // (b) cue を消す → 未解決の警告。尺は素材が無い扱いで Unity と同じ 4s フォールバック
  const st = sampleState();
  st.cues = [];
  const dropped = buildScenarioConfig(st);
  assert.ok(dropped.meta.warnings.some((w) => w.includes('cue 未解決')));
  assert.equal(dropped.cfg.takes[1].stepDurSec[1], FALLBACK_STEP_DUR_SEC);

  // (c) 素材はあるが trim が無い → 尺不明（watchdog 任せ）の警告
  const noTrim = sampleState();
  noTrim.cues = [{ id: 'cue_B', sourceUrl: '/captures/01.mp4' }];
  const unknown = buildScenarioConfig(noTrim);
  assert.equal(unknown.cfg.takes[1].stepDurSec[1], -1);
  assert.ok(unknown.meta.warnings.some((w) => w.includes('素材の終わりまで') && w.includes('watchdog')));
});

// ---- 通し（設定 → 実行 → 保存形式）-------------------------------------------

test('通し: 組んだ設定で歩かせるとゾーン確定と演出が出る', () => {
  const { cfg } = buildScenarioConfig(sampleState());
  // 北(cam0) → 中(cam1) へ歩き、2 秒立ち止まる（dwell 0.8s）。
  const samples = [
    { tMs: 0, x: 0, z: 1 }, { tMs: 1000, x: 0, z: 1 },
    { tMs: 2000, x: 0, z: 0 }, { tMs: 6000, x: 0, z: 0 },
  ];
  const tr = runScenario(cfg, samples);
  assert.deepStrictEqual(tr.filter((e) => e.kind === 'zone').map((e) => e.a), [1]);
  assert.ok(tr.some((e) => e.kind === 'screen' && e.a === 1), '画面もカメラ 1 へ切り替わる');
});

test('保存形式は golden fixture と同じ形（parseScenario で往復できる）', () => {
  const { cfg } = buildScenarioConfig(sampleState());
  const samples = [{ tMs: 0, x: 0, z: 1 }, { tMs: 3000, x: 0, z: 0 }];
  const file = serializeScenario(cfg, samples, { name: 'test' });
  const back = parseScenario(JSON.parse(JSON.stringify(file)));
  assert.equal(back.cfg.takes.length, cfg.takes.length);
  assert.deepStrictEqual(back.cfg.takeIds, cfg.takeIds);
  assert.deepStrictEqual(back.cfg.courseOrder, cfg.courseOrder);
  assert.equal(back.cfg.zones.length, cfg.zones.length);
  assert.deepStrictEqual(back.samples, samples);
  // fixture と同じキーが揃っている（C# ShowScenarioRunner へそのまま食わせられる）。
  for (const k of ['tickMs', 'dwellSec', 'cooldownSec', 'hysteresisShrink', 'headY', 'startCamera',
    'courseOrder', 'zones', 'takes', 'samples']) {
    assert.ok(k in file, `保存形式に ${k} が無い`);
  }
});

test('卓の実 show.json からも設定を組める（構造の smoke）', () => {
  const show = JSON.parse(readFileSync(new URL('./show.json', import.meta.url), 'utf8'));
  const { cfg, meta } = buildScenarioConfig(show);
  assert.ok(Array.isArray(cfg.zones));
  assert.ok(Array.isArray(cfg.takes));
  assert.ok(cfg.tickMs > 0);
  assert.ok(['grid', 'cuts', 'none'].includes(meta.zoneSource));
});

const round = (v) => Math.round(v * 1e6) / 1e6;
