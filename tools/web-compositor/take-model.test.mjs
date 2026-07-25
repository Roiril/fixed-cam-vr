// v3（演出 Take / カット Step）の純関数を node:test で固定する（DOM 非依存）。
//   実行: node --test tools/web-compositor/
//   fixture 再生成: UPDATE_FIXTURE=1 node --test tools/web-compositor/take-model.test.mjs
//     → Assets/Tests/Fixtures/show_timeline_v3_canonical.json を serialize 出力から書き出す。
//   引数なし実行は「normalize→serialize の往復 == 現 fixture」を assert する。
//   同じ fixture を Unity の TakeFixtureContractTests が読むので、Web と Unity の wire 形式が
//   機械的に一致する（v2 の show_timeline_canonical.json と同じ流儀）。

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import {
  TAKE, newTake, newStep, takeId, isV3,
  serializeTimelineV3, normalizeTimelineV3, migrateV2Segment, newSeg,
} from './timeline-model.js';
import { FX_DEFAULT } from './common.js';

const FIXTURE_URL = new URL('../../Assets/Tests/Fixtures/show_timeline_v3_canonical.json', import.meta.url);
const fixture = JSON.parse(readFileSync(FIXTURE_URL, 'utf8'));

// ---- fixture 契約（Web ⇄ Unity） --------------------------------------------

if (process.env.UPDATE_FIXTURE) {
  test('fixture を serialize 出力から再生成する', () => {
    const out = serializeTimelineV3(normalizeTimelineV3(fixture));
    writeFileSync(FIXTURE_URL, `${JSON.stringify(out, null, 2)}\n`);
  });
} else {
  test('show_timeline_v3_canonical.json == serializeV3(normalizeV3(fixture))', () => {
    assert.deepStrictEqual(serializeTimelineV3(normalizeTimelineV3(fixture)), fixture,
      'normalize→serialize が往復しない（wire 形式が変わったら UPDATE_FIXTURE=1 で再生成し Unity 側テストも確認）');
  });
}

test('fixture は v3 と判定される', () => {
  assert.equal(isV3(fixture), true);
  assert.equal(fixture.schema, 3);
});

test('fixture に要求の 4 カット演出が入っている', () => {
  const seg = fixture.segments.find((s) => s.lap === 3 && s.camera === 1);
  const t = seg.takes[0];
  assert.equal(t.offsetSec, 20);
  assert.equal(t.steps.length, 4);
  assert.equal(t.steps[0].source, TAKE.SRC_LIVE);
  assert.equal(t.steps[0].camera, 3);
  assert.equal(t.steps[1].assetUrl, 'sa://assets/pre_01.mp4');
  assert.equal(t.steps[2].cueId, 'cg_doll_B');
  assert.equal(t.steps[2].hasPost, true);
  assert.equal(t.steps[3].hasPost, false);
});

// ---- serialize の present-flag 規約 ------------------------------------------

test('hasPost=false のとき post キーを出さない（step / segment 両方）', () => {
  const seg = newSeg(1, 0);
  seg.takes = [newTake('t', { steps: [newStep()] })];
  const out = serializeTimelineV3({ rev: 1, segments: [seg] });
  const step = out.segments[0].takes[0].steps[0];
  assert.equal('post' in step, false);
  assert.equal(step.hasPost, false);
  assert.equal('post' in out.segments[0], false);
  assert.equal(out.segments[0].hasPost, false);
});

test('hasPost=true のとき post を FX 既定で埋めて出す', () => {
  const seg = newSeg(1, 0);
  const step = newStep({ hasPost: true, post: { ...FX_DEFAULT, saturation: 0.6 } });
  seg.takes = [newTake('t', { steps: [step] })];
  const out = serializeTimelineV3({ rev: 1, segments: [seg] });
  assert.equal(out.segments[0].takes[0].steps[0].post.saturation, 0.6);
  assert.equal(out.segments[0].takes[0].steps[0].hasPost, true);
});

test('v2 キー（cues / insert）は v3 書き出しに出さない', () => {
  const seg = newSeg(1, 0);
  seg.takes = [newTake('t', { steps: [newStep()] })];
  seg.cues = [{ cueId: 'legacy' }];
  seg.hasInsert = true;
  const out = serializeTimelineV3({ rev: 1, segments: [seg] });
  assert.equal('cues' in out.segments[0], false);
  assert.equal('insert' in out.segments[0], false);
  assert.equal('hasInsert' in out.segments[0], false);
});

test('exit 演出は offsetSec を持ち込まない', () => {
  const seg = newSeg(2, 1);
  seg.takes = [newTake('t', { at: TAKE.AT_EXIT, offsetSec: 9, steps: [newStep()] })];
  const out = serializeTimelineV3({ rev: 1, segments: [seg] });
  assert.equal(out.segments[0].takes[0].offsetSec, 0);
});

test('未知の判別子は既定へ倒す（Unity NormalizeSource と同じ安全側）', () => {
  const seg = newSeg(1, 0);
  seg.takes = [newTake('t', { at: 'bogus', ifMissed: 'bogus', policy: 'bogus', steps: [newStep()] })];
  const t = serializeTimelineV3({ rev: 1, segments: [seg] }).segments[0].takes[0];
  assert.equal(t.at, TAKE.AT_ENTER);
  assert.equal(t.ifMissed, TAKE.MISSED_FIRE_ON_EXIT, '未知は「出す」側');
  assert.equal(t.policy, TAKE.POLICY_HOLD);
});

test('空の区間（takes なし・post/bgm なし）は落とす', () => {
  const out = serializeTimelineV3({ rev: 1, segments: [newSeg(1, 0)] });
  assert.equal(out.segments.length, 0);
});

// ---- v2 → v3 変換（Unity TimelineMigration.FromV2 のミラー） -----------------

test('v2 cue → inherit カット / untilClipEnd / -1 継承', () => {
  const seg = newSeg(1, 0);
  seg.cues = [{ cueId: 'cue_A_1', delaySec: 2, once: true, hasOverride: false }];
  const takes = migrateV2Segment(seg);
  assert.equal(takes.length, 1);
  assert.equal(takes[0].id, 'L1C0#0');
  assert.equal(takes[0].at, TAKE.AT_ENTER);
  assert.equal(takes[0].offsetSec, 2);
  assert.equal(takes[0].ifMissed, TAKE.MISSED_FIRE_ON_EXIT);
  const s = takes[0].steps[0];
  assert.equal(s.source, TAKE.SRC_INHERIT);
  assert.equal(s.cueId, 'cue_A_1');
  assert.equal(s.durKind, TAKE.DUR_UNTIL_CLIP_END);
  assert.equal(s.strength, -1);
});

test('v2 cue override → 明示値になる', () => {
  const seg = newSeg(2, 1);
  seg.cues = [{
    cueId: 'cue_B', delaySec: 0, once: false, hasOverride: true,
    override: { strength: 0.5, fadeIn: 1.2, fadeOut: 0.8, trimStart: 1, trimEnd: 6 },
  }];
  const t = migrateV2Segment(seg)[0];
  assert.equal(t.once, false);
  assert.equal(t.steps[0].strength, 0.5);
  assert.equal(t.steps[0].fadeInSec, 1.2);
  assert.equal(t.steps[0].trimEndSec, 6);
});

test('v2 enter insert → live カット', () => {
  const seg = newSeg(2, 2);
  seg.hasInsert = true;
  seg.insert = { anchor: 'enter', camera: 0, delaySec: 1.5, durationSec: 4, cueId: 'cue_C_scare', once: true, hasPost: false };
  const t = migrateV2Segment(seg)[0];
  assert.equal(t.at, TAKE.AT_ENTER);
  assert.equal(t.offsetSec, 1.5);
  assert.equal(t.steps[0].source, TAKE.SRC_LIVE);
  assert.equal(t.steps[0].camera, 0);
  assert.equal(t.steps[0].durSec, 4);
  assert.equal(t.steps[0].cueId, 'cue_C_scare');
});

test('v2 exit insert → exit 演出（offset は 0）', () => {
  const seg = newSeg(1, 1);
  seg.hasInsert = true;
  seg.insert = { anchor: 'exit', camera: 2, delaySec: 9, durationSec: 3, once: true, hasPost: false };
  const t = migrateV2Segment(seg)[0];
  assert.equal(t.at, TAKE.AT_EXIT);
  assert.equal(t.offsetSec, 0);
  assert.equal(t.steps[0].camera, 2);
});

test('v2 insert の post は present-flag つきで引き継ぐ', () => {
  const seg = newSeg(1, 1);
  seg.hasInsert = true;
  seg.insert = { anchor: 'enter', camera: 2, durationSec: 3, hasPost: true, post: { ...FX_DEFAULT, saturation: 1.5 } };
  const s = migrateV2Segment(seg)[0].steps[0];
  assert.equal(s.hasPost, true);
  assert.equal(s.post.saturation, 1.5);
});

test('v2 変換の並びは cues 順 → 最後に insert', () => {
  const seg = newSeg(1, 0);
  seg.cues = [{ cueId: 'c1' }, { cueId: 'c2' }];
  seg.hasInsert = true;
  seg.insert = { anchor: 'exit', camera: 1, durationSec: 2 };
  const takes = migrateV2Segment(seg);
  assert.deepStrictEqual(takes.map((t) => t.steps[0].cueId), ['c1', 'c2', '']);
  assert.equal(takes[2].at, TAKE.AT_EXIT);
  assert.deepStrictEqual(takes.map((t) => t.id), ['L1C0#0', 'L1C0#1', 'L1C0#2']);
});

test('hasInsert=false の insert は変換しない（幽霊インサートを作らない）', () => {
  const seg = newSeg(1, 0);
  seg.hasInsert = false;
  seg.insert = { anchor: 'enter', camera: 2, durationSec: 3 };
  assert.equal(migrateV2Segment(seg).length, 0);
});

test('cueId 空の v2 cue は落とす', () => {
  const seg = newSeg(1, 0);
  seg.cues = [{ cueId: '' }];
  assert.equal(migrateV2Segment(seg).length, 0);
});

test('normalizeTimelineV3 は takes を持たない v2 区間を変換して埋める', () => {
  const tl = {
    rev: 2,
    segments: [
      { lap: 1, camera: 0, cues: [{ cueId: 'c' }], hasPost: false, hasBgm: false },
      { lap: 1, camera: 1, takes: [{ id: 'keep', steps: [{ source: 'live', camera: 2 }] }], hasPost: false, hasBgm: false },
    ],
  };
  const out = normalizeTimelineV3(tl);
  assert.equal(out.segments[0].takes.length, 1, 'v2 区間は変換される');
  assert.equal(out.segments[0].takes[0].steps[0].source, TAKE.SRC_INHERIT);
  assert.equal(out.segments[1].takes[0].id, 'keep', '既に takes を持つ区間は触らない');
});

// ---- 補助 -------------------------------------------------------------------

test('takeId は Unity MakeId と同じ規則', () => {
  assert.equal(takeId(3, 1, 0), 'L3C1#0');
});

test('isV3 は schema か takes の存在で判定する', () => {
  assert.equal(isV3({ schema: 3, segments: [] }), true);
  assert.equal(isV3({ segments: [{ takes: [{}] }] }), true);
  assert.equal(isV3({ segments: [{ cues: [{}] }] }), false);
  assert.equal(isV3(null), false);
});
