// 端末内録画（show.json record）と「録画」カットの照合を固定する。
//   実行: node --test tools/web-compositor/
//
// ここが甘いと、実機だけがカットを飛ばして卓には何も出ない（著作者に発見手段が無い）。
// 移植元（**C# が正**）: ShowRecordDef.RecordsLap / TakeRunner.OpenRecording。

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  REC_DEFAULT, REC_DEFAULT_TAIL_SEC, REC_DEFAULT_POST_SEC,
  recordConfig, recordLaps, recordTailSec, recordPostSec, recordLengthSec,
  recStepProblem, recStepIssue, recCutRefs, recordCoverage, missingRecordLaps,
} from './record-model.js';

const recStep = (over = {}) => ({ source: 'rec', camera: 1, recLap: 1, ...over });
const cams = (n) => Array.from({ length: n }, (_, i) => ({ id: 'ABCD'[i] }));

test('recordConfig: 欠落は既定で埋める（record が無い show.json は録画 OFF）', () => {
  assert.deepStrictEqual(recordConfig({}), REC_DEFAULT);
  assert.equal(recordConfig(null).enabled, false, 'record が無い = 1 フレームも録らない');
  assert.deepStrictEqual(recordConfig({ record: { enabled: true, laps: [2] } }),
    { ...REC_DEFAULT, enabled: true, laps: [2] });
});

test('recordTailSec: 0 以下・未指定は既定へ倒す（C# ShowRecordDef.TailSec と同じ判定）', () => {
  assert.equal(recordTailSec({}), REC_DEFAULT_TAIL_SEC);
  assert.equal(recordTailSec({ tailSec: 0 }), REC_DEFAULT_TAIL_SEC, '0 は「無効」ではなく既定');
  assert.equal(recordTailSec({ tailSec: -2 }), REC_DEFAULT_TAIL_SEC);
  assert.equal(recordTailSec({ tailSec: 5.5 }), 5.5);
  // 既定は 3 秒。**C# SegmentRecordWriter.DefaultTailSec と対**なので、片方だけ変えない。
  assert.equal(REC_DEFAULT_TAIL_SEC, 3);
  assert.equal(REC_DEFAULT.tailSec, 3, 'capture-server.py の _default_show とも同じ値');
});

test('recordPostSec: 0 以下・未指定は既定へ倒す（C# ShowRecordDef.PostSec と同じ判定）', () => {
  // ⚠ 0 を「追い録りなし」にしない。JsonUtility はキーの無い show.json でも 0 を書くので、
  //   既存の焼き込み・端末キャッシュでは必ず 0 が入る（0 を無効と読むと機能ごと消える）。
  assert.equal(recordPostSec({}), REC_DEFAULT_POST_SEC);
  assert.equal(recordPostSec({ postSec: 0 }), REC_DEFAULT_POST_SEC, '0 は「無効」ではなく既定');
  assert.equal(recordPostSec({ postSec: -1 }), REC_DEFAULT_POST_SEC);
  assert.equal(recordPostSec({ postSec: 4 }), 4);
  // 既定は 2 秒。**C# SegmentRecordWriter.DefaultPostSec と対**なので、片方だけ変えない。
  assert.equal(REC_DEFAULT_POST_SEC, 2);
  assert.equal(REC_DEFAULT.postSec, 2, 'capture-server.py の _default_show とも同じ値');
});

test('recordLengthSec: 尺は 切り替え前 + 切り替え後。縮むのは前側だけ', () => {
  assert.equal(recordLengthSec({ tailSec: 3, postSec: 2 }), 5);
  // 実測滞在が切り替え前より短ければ、その分しか録れていない。
  assert.equal(recordLengthSec({ tailSec: 3, postSec: 2 }, 1.2), 3.2);
  // 切り替え**後**は次の区間に居るあいだ録るので、前の区間の滞在では縮まない。
  assert.equal(recordLengthSec({ tailSec: 3, postSec: 2 }, 0.1), 2.1);
  // 滞在が長ければ切り替え前は tailSec で頭打ち。
  assert.equal(recordLengthSec({ tailSec: 3, postSec: 2 }, 30), 5);
  assert.equal(recordLengthSec({}), REC_DEFAULT_TAIL_SEC + REC_DEFAULT_POST_SEC);
});

test('recordLaps: 1 始まりの整数だけ拾う', () => {
  assert.deepStrictEqual([...recordLaps({ laps: [1, '2', 0, -3, null, 2] })], [1, 2]);
  assert.deepStrictEqual([...recordLaps({})], []);
});

test('recStepIssue: rec 以外のカットは判定対象外', () => {
  const cfg = recordConfig({});
  assert.equal(recStepIssue(cfg, { source: 'live', camera: 0 }), '');
  assert.equal(recStepIssue(cfg, { source: 'clip', assetUrl: 'a.mp4' }), '');
  assert.equal(recStepIssue(cfg, null), '');
});

test('recStepIssue: 周・カメラの未指定は「飛ばされる」', () => {
  const cfg = recordConfig({ record: { enabled: true, laps: [1] } });
  assert.match(recStepIssue(cfg, recStep({ recLap: 0 })), /未指定/);
  assert.match(recStepIssue(cfg, recStep({ camera: -1 })), /未指定/);
  assert.match(recStepIssue(cfg, recStep({ camera: 9 }), 3), /未指定/, 'カメラ数を渡せば範囲外も弾く');
  assert.equal(recStepIssue(cfg, recStep({ camera: 2 }), 3), '', '範囲内なら通る');
});

test('recStepIssue: 録画が OFF / その周を録らない設定は理由ごと返す', () => {
  assert.match(recStepIssue(recordConfig({}), recStep()), /OFF/, '既定は OFF');
  const on1 = recordConfig({ record: { enabled: true, laps: [1] } });
  assert.equal(recStepIssue(on1, recStep({ recLap: 1 })), '');
  assert.match(recStepIssue(on1, recStep({ recLap: 2 })), /2周目は録らない設定/);
  const on12 = recordConfig({ record: { enabled: true, laps: [1, 2] } });
  assert.equal(recStepIssue(on12, recStep({ recLap: 2 })), '', '録る周に足せば通る');
});

test('recStepProblem: 原因と打ち手は分けて返す（一覧が入れ子の括弧にならない）', () => {
  const p = recStepProblem(recordConfig({}), recStep());
  assert.equal(p.cause, '端末内録画が OFF');
  assert.ok(p.fix.includes('ON にする'));
  assert.ok(!p.cause.includes('（'), '一覧に並べる原因に括弧を入れない');
  assert.equal(recStepProblem(recordConfig({ record: { enabled: true, laps: [1] } }), recStep()), null);
});

// 1周目 B / 1周目 C / 2周目 A を録って、3周目 B の離脱演出で 3 本続けて流す構成。
function scenarioSegments() {
  return [{
    lap: 3,
    camera: 1,
    takes: [{
      id: 'L3C1#0',
      name: '回想',
      steps: [
        { source: 'rec', camera: 1, recLap: 1 },
        { source: 'rec', camera: 2, recLap: 1 },
        { source: 'rec', camera: 0, recLap: 2 },
      ],
    }],
  }];
}

test('recCutRefs: 「録画」カットの参照だけを、どの区間の演出かごと集める', () => {
  const refs = recCutRefs(scenarioSegments(), (i) => 'ABC'[i]);
  assert.equal(refs.length, 3);
  assert.deepStrictEqual(refs.map((r) => [r.lap, r.camera]), [[1, 1], [1, 2], [2, 0]]);
  assert.equal(refs[0].where, 'Lap3 / B');
  assert.equal(refs[0].take, '回想');
  assert.deepStrictEqual(recCutRefs([{ lap: 1, camera: 0, takes: [{ steps: [{ source: 'live', camera: 0 }] }] }]), []);
});

test('recordCoverage: 既定（record なし）では 3 本とも実機で飛ばされる', () => {
  const cov = recordCoverage({ cameras: cams(3) }, scenarioSegments(), (i) => 'ABC'[i]);
  assert.equal(cov.refs.length, 3);
  assert.equal(cov.bad.length, 3);
  assert.ok(cov.bad.every((r) => /OFF/.test(r.reason)));
});

test('recordCoverage: 1・2 周目を録る設定なら 3 本とも通る', () => {
  const state = { cameras: cams(3), record: { enabled: true, laps: [1, 2] } };
  const cov = recordCoverage(state, scenarioSegments(), (i) => 'ABC'[i]);
  assert.equal(cov.bad.length, 0);
  assert.deepStrictEqual([...cov.laps], [1, 2]);
});

test('recordCoverage: 1 周目しか録らないと、2周目 A のカットだけが落ちる', () => {
  const state = { cameras: cams(3), record: { enabled: true, laps: [1] } };
  const cov = recordCoverage(state, scenarioSegments(), (i) => 'ABC'[i]);
  assert.equal(cov.bad.length, 1);
  assert.deepStrictEqual([cov.bad[0].lap, cov.bad[0].camera], [2, 0]);
  assert.deepStrictEqual(missingRecordLaps(state, scenarioSegments()), [2], '⟲ で足すべき周');
});

test('missingRecordLaps: 未指定のカットは「足すべき周」に数えない', () => {
  const segs = [{ lap: 3, camera: 1, takes: [{ steps: [{ source: 'rec', camera: -1, recLap: 5 }] }] }];
  assert.deepStrictEqual(missingRecordLaps({ record: { enabled: true, laps: [1] } }, segs), []);
});
