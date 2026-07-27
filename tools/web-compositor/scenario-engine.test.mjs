// ショーシミュレータ（JS ミラー）が Unity の ShowScenarioRunner と**同じトレース**を出すことを固定する。
//   実行: node --test tools/web-compositor/
//
//   golden fixture（Unity 側 ShowScenarioRunnerTests が生成・照合している正本）:
//     入力 Assets/Tests/Fixtures/scenario_walk.json
//     期待 Assets/Tests/Fixtures/scenario_walk.trace.json
//
//   照合の規約（計画 2026-07-25_show-simulator.md §1）:
//     イベント列（kind / a / b / id / flag）は**完全一致**、時刻 t は **±1 tick（20ms）**まで許容。
//     浮動小数の境界（now - since >= dwell）が C# float と JS double で 1 tick ずれても、
//     セマンティクスの drift だけを検出できるようにするため。
//
//   ⚠ 一致しなかったら**直すのは JS 側**（C# が正）。fixture を書き換えて通すのは禁止。

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  runScenario, parseScenario, createShowRunner, sampleAt,
  pickZone, containsBox, boxAabb, ZoneProgression, SwitchDirector, LapCounter,
  SpotTrigger, spot, spotUndefined, SPOT_EXIT_MARGIN_M, SPOT_MIN_RADIUS_M,
  DEFAULT_TICK_MS,
} from './scenario-engine.js';

const SCENARIO_URL = new URL('../../Assets/Tests/Fixtures/scenario_walk.json', import.meta.url);
const TRACE_URL = new URL('../../Assets/Tests/Fixtures/scenario_walk.trace.json', import.meta.url);
const SPOT_SCENARIO_URL = new URL('../../Assets/Tests/Fixtures/scenario_spot.json', import.meta.url);
const SPOT_TRACE_URL = new URL('../../Assets/Tests/Fixtures/scenario_spot.trace.json', import.meta.url);

const scenario = JSON.parse(readFileSync(SCENARIO_URL, 'utf8'));
const golden = JSON.parse(readFileSync(TRACE_URL, 'utf8'));

const { cfg, samples } = parseScenario(scenario);
const trace = runScenario(cfg, samples);

const shape = (e) => `${e.kind}|a=${e.a}|b=${e.b}|id=${e.id}|flag=${e.flag}`;
const fmt = (e) => `${shape(e)}@${e.t}`;

// ---- golden 一致（S3 の合格条件）---------------------------------------------

test('golden: イベント列が Unity トレースと完全一致する（kind/a/b/id/flag）', () => {
  assert.deepStrictEqual(trace.map(shape), golden.map(shape),
    `トレースが golden と違う。\n JS  : ${trace.map(fmt).join('\n JS  : ')}\n GOLD: ${golden.map(fmt).join('\n GOLD: ')}`);
});

test('golden: 各イベントの時刻が ±1 tick 以内', () => {
  assert.equal(trace.length, golden.length, 'イベント数が違う');
  trace.forEach((e, i) => {
    const d = Math.abs(e.t - golden[i].t);
    assert.ok(d <= DEFAULT_TICK_MS,
      `#${i} ${e.kind} の時刻が ${d}ms ずれている（JS=${e.t} / golden=${golden[i].t}）`);
  });
});

test('golden: 期待トレースが空でない（fixture を読み違えていない）', () => {
  assert.ok(golden.length >= 20, `golden が ${golden.length} 件しかない`);
  assert.ok(golden.some((e) => e.kind === 'take'), 'golden に演出が入っていない');
});

// ---- セマンティクスの単体固定（Unity ShowScenarioRunnerTests のミラー）--------

test('歩き: 確定ゾーンは B,C,A,B,C / 周回は 1 回だけ進む', () => {
  const zones = trace.filter((e) => e.kind === 'zone').map((e) => e.a);
  assert.deepStrictEqual(zones, [1, 2, 0, 1, 2],
    'A から歩き出すので確定は B,C,A,B,C（開始ゾーン A は確定イベントを出さない）');
  const laps = trace.filter((e) => e.kind === 'lap').map((e) => e.a);
  assert.deepStrictEqual(laps, [2], 'C→A の復帰で 2 周目になる（1 回だけ）');
});

test('fireOnExit: 20 秒待てない歩き方でも山場が出る', () => {
  assert.ok(trace.some((e) => e.kind === 'take' && e.id === 't_B_late'));
});

test('skip: 通り過ぎたら出ない', () => {
  const c = parseScenario(scenario);
  c.cfg.takes[0].skipWhenMissed = true;
  const tr = runScenario(c.cfg, c.samples);
  assert.ok(!tr.some((e) => e.kind === 'take' && e.id === 't_B_late'));
});

test('exit 演出は離脱で発火し、尺どおりに終わる', () => {
  const begin = trace.find((e) => e.kind === 'take' && e.id === 't_C_exit');
  const end = trace.find((e) => e.kind === 'end' && e.id === 't_C_exit');
  assert.ok(begin && end);
  assert.ok(Math.abs((end.t - begin.t) - 1000) <= DEFAULT_TICK_MS, '尺 1.0s ぶん表示してから終わる');
  assert.equal(end.flag, false, 'watchdog ではなく尺どおりの終了');
});

test('始まった演出は必ず終わる（不変条件 2）', () => {
  const started = trace.filter((e) => e.kind === 'take').map((e) => e.id).sort();
  const ended = trace.filter((e) => e.kind === 'end').map((e) => e.id).sort();
  assert.deepStrictEqual(started, ended);
});

test('動かなければ何も起きない', () => {
  const still = [{ tMs: 0, x: -0.6, z: 0 }, { tMs: 5000, x: -0.6, z: 0 }];
  assert.deepStrictEqual(runScenario(cfg, still), []);
});

test('dwell 未満の踏み込みは確定しない（境界のうろつき）', () => {
  const wobble = [
    { tMs: 0, x: -0.6, z: 0 },
    { tMs: 400, x: 0.0, z: 0 },
    { tMs: 700, x: -0.6, z: 0 },
    { tMs: 2000, x: -0.6, z: 0 },
  ];
  assert.ok(!runScenario(cfg, wobble).some((e) => e.kind === 'zone'));
});

// ---- 位置トリガー（at=spot・golden は Unity 側が生成した正本）--------------------

const spotScenario = JSON.parse(readFileSync(SPOT_SCENARIO_URL, 'utf8'));
const spotGolden = JSON.parse(readFileSync(SPOT_TRACE_URL, 'utf8'));
const spotRun = parseScenario(spotScenario);
const spotTrace = runScenario(spotRun.cfg, spotRun.samples);

test('golden(spot): イベント列が Unity トレースと完全一致する', () => {
  assert.deepStrictEqual(spotTrace.map(shape), spotGolden.map(shape),
    `位置トリガーのトレースが golden と違う。\n JS  : ${spotTrace.map(fmt).join('\n JS  : ')}`
    + `\n GOLD: ${spotGolden.map(fmt).join('\n GOLD: ')}`);
});

test('golden(spot): 各イベントの時刻が ±1 tick 以内', () => {
  assert.equal(spotTrace.length, spotGolden.length, 'イベント数が違う');
  spotTrace.forEach((e, i) => {
    assert.ok(Math.abs(e.t - spotGolden[i].t) <= DEFAULT_TICK_MS,
      `#${i} ${e.kind} の時刻が ${Math.abs(e.t - spotGolden[i].t)}ms ずれている`);
  });
});

test('位置トリガー: 立ち止まれば出る / 長すぎる hold は出ない / 踏んだ瞬間に出る', () => {
  const ids = spotTrace.filter((e) => e.kind === 'take').map((e) => e.id);
  assert.ok(ids.includes('t_C_stand'), '0.5s 立ち止まったので出る');
  assert.ok(!ids.includes('t_C_long'), '3s の立ち止まりは満たさない');
  assert.ok(ids.includes('t_B_pass'), 'hold 0 は踏んだ瞬間に出る');
  assert.ok(ids.includes('t_C_miss'), '踏まないまま離脱 → fireOnExit で出る');
});

test('位置トリガー: 円が無ければ（layout.spots 未著作）沈黙する', () => {
  const c = parseScenario(spotScenario);
  c.cfg.spots = [];
  const ids = runScenario(c.cfg, c.samples).filter((e) => e.kind === 'take').map((e) => e.id);
  assert.deepStrictEqual(ids, ['t_C_miss'], 'fireOnExit の 1 本だけが離脱時に出る');
});

test('SpotTrigger: 入るのは半径 / 出るのはヒステリシス込み', () => {
  const s = new SpotTrigger();
  s.setSpots([spot(0, 0, 0.25)]);
  s.tick(0.28, 0, 0.02);
  assert.equal(s.isInside(0), false, '入る判定は素の半径');
  s.tick(0.2, 0, 0.02);
  assert.equal(s.isInside(0), true);
  s.tick(0.25 + SPOT_EXIT_MARGIN_M - 0.01, 0, 0.02);
  assert.equal(s.isInside(0), true, 'マージンの内はまだ中');
  s.tick(0.25 + SPOT_EXIT_MARGIN_M + 0.01, 0, 0.02);
  assert.equal(s.isInside(0), false);
});

test('SpotTrigger: 滞在秒は連続で数え、出たら 0 / dt 不連続でも数え直す', () => {
  const s = new SpotTrigger();
  s.setSpots([spot(0, 0, 0.25)]);
  s.tick(0, 0, 0.02);
  assert.equal(s.insideSecOf(0), 0, '入った瞬間は 0');
  for (let i = 0; i < 25; i++) s.tick(0, 0, 0.02);
  assert.ok(Math.abs(s.insideSecOf(0) - 0.5) < 0.01);
  s.tick(0, 0, 3);                    // 復帰（HMD 着脱）相当の飛び
  assert.equal(s.isInside(0), true);
  assert.equal(s.insideSecOf(0), 0);
  s.tick(2, 0, 0.02);
  assert.equal(s.insideSecOf(0), 0, '出たら 0');
});

test('SpotTrigger: 実体の無い枠は常に外 / 半径 0 は下限へクランプ', () => {
  const s = new SpotTrigger();
  s.setSpots([spotUndefined(), spot(0, 0, 0)]);
  s.tick(0, 0, 0.02);
  assert.equal(s.isInside(0), false);
  assert.equal(s.isInside(1), true, `半径 0 は ${SPOT_MIN_RADIUS_M}m へクランプ`);
  assert.equal(s.count, 2, 'スロットは保たれる（index が動かない）');
});

// ---- 部品の単体（ZonePickLogic / 時計 / 画面 / 周回）---------------------------

test('pickZone: shrink 後の箱に居る限り直近ゾーンを維持する', () => {
  const boxes = [boxAabb(-0.6, 1, 0, 0.34, 2, 0.9, 0), boxAabb(0, 1, 0, 0.34, 2, 0.9, 1)];
  // 重なり帯（x=-0.3）は両方に含まれるが、shrink 後の box0 にも入るので維持される。
  assert.equal(pickZone(boxes, -0.3, 1, 0, 0, 0.04, true), 0);
  // 直近が無ければ配列先頭が勝つ（同 priority のタイブレーク）。
  assert.equal(pickZone(boxes, -0.3, 1, 0, -1, 0.04, true), 0);
  // shrink 後の箱を出たら再評価され、隣が勝つ。
  assert.equal(pickZone(boxes, -0.2, 1, 0, 0, 0.04, true), 1);
});

test('pickZone: 外に出たら keepLastWhenOutside の方針に従う', () => {
  const boxes = [boxAabb(0, 1, 0, 0.3, 2, 0.3, 0)];
  assert.equal(pickZone(boxes, 5, 1, 5, 0, 0, true), 0);
  assert.equal(pickZone(boxes, 5, 1, 5, 0, 0, false), -1);
});

test('containsBox: yaw 付き箱はローカル軸で判定する', () => {
  const b = boxAabb(0, 0, 0, 1, 1, 0.1, 0);
  assert.equal(containsBox(b, 0.9, 0, 0, 0), true);
  assert.equal(containsBox(b, 0, 0, 0.9, 0), false);
});

test('時計: dwell を満たすまで確定しない / 戻ったら保留は消える', () => {
  const p = new ZoneProgression();
  p.configure(0.5);
  p.reset(0);
  p.request(1, 0);
  assert.equal(p.tick(0.4).committed, false);
  p.request(0, 0.45);                 // 元のゾーンへ戻った = 移動しなかった
  assert.equal(p.hasPending, false);
  p.request(1, 1.0);
  assert.equal(p.tick(1.4).committed, false);
  const r = p.tick(1.5);
  assert.equal(r.committed, true);
  assert.equal(r.camera, 1);
});

test('画面: クールダウン中は commit しない / 凍結中も commit しない', () => {
  const s = new SwitchDirector();
  s.configure(0.5, 0);
  s.reset(0);
  s.setAmbient(1);
  assert.equal(s.tick(0).committed, true, '初期状態は即切替できる');
  s.setAmbient(2);
  assert.equal(s.tick(0.2).committed, false, 'クールダウン中');
  s.setInsertActive(true);
  assert.equal(s.tick(1.0).committed, false, '演出が画面を占有中は凍結');
  s.setInsertActive(false);
  assert.equal(s.tick(1.0).committed, true, '解除で最新の保留が適用される');
  assert.equal(s.current, 2);
});

test('周回: 順方向一致でのみ前進し、逆走・スキップは数えない', () => {
  const l = new LapCounter();
  l.setOrder([0, 1, 2]);
  assert.equal(l.feed(2), false, 'スキップ（0→2）は前進しない');
  assert.equal(l.feed(1), false);
  assert.equal(l.feed(0), false, '逆走（1→0）は前進しない');
  assert.equal(l.feed(2), false);
  assert.equal(l.feed(0), true, '一周して lap++');
  assert.equal(l.currentLap, 2);
});

test('sampleAt: サンプル間は等速で補間し、両端は張り付く', () => {
  const s = [{ tMs: 0, x: 0, z: 0 }, { tMs: 1000, x: 1, z: -2 }];
  assert.deepStrictEqual(sampleAt(s, -5), { x: 0, z: 0 });
  assert.deepStrictEqual(sampleAt(s, 500), { x: 0.5, z: -1 });
  assert.deepStrictEqual(sampleAt(s, 5000), { x: 1, z: -2 });
});

test('createShowRunner: UI 用の観測値が step と整合する（テストと UI が同じ経路）', () => {
  const r = createShowRunner(cfg);
  const tick = r.config.tickMs;
  const end = samples[samples.length - 1].tMs;
  const seen = [];
  for (let t = samples[0].tMs; t <= end; t += tick) {
    const p = sampleAt(samples, t);
    for (const e of r.step(t, p.x, p.z)) seen.push(shape(e));
  }
  assert.deepStrictEqual(seen, trace.map(shape), 'runScenario と step ループの結果が一致する');
  assert.equal(r.lap, 2);
  assert.equal(r.shownCamera, 2);
  assert.deepStrictEqual(r.segment, { lap: 2, camera: 2 });
});
