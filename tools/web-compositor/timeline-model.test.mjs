// timeline-model.js の純関数を node:test で固定する（DOM 非依存）。
//   実行: node --test tools/web-compositor/
//
// **2026-07-25 に演出・カット（schema 3）へ一本化した。**
//   ・書き出しは serializeTimelineV3 だけ（v3 の形と v2→v3 変換の詳細は take-model.test.mjs が固定）
//   ・古い形式（cues[] / insert）は**読み取り変換だけ**残る。ここでは「共有 fixture
//     show_timeline_canonical.json（＝古い形式の標本）を読むと演出になる」ことを確かめる。
//     Unity 側は TimelineFixtureContractTests が同じ fixture を食う（変換の一致はそこで担保）
//   ・BGM 帯の carry-forward（Unity BgmPlanLogic のミラー）もここで固定する

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { normalizeTimelineV3, serializeTimelineV3, resolveBgmLane, TAKE } from './timeline-model.js';

const FIXTURE_URL = new URL('../../Assets/Tests/Fixtures/show_timeline_canonical.json', import.meta.url);
const fixture = JSON.parse(readFileSync(FIXTURE_URL, { encoding: 'utf-8' }));

// ---- 古い形式の読み取り（Unity TimelineMigration と同じ結果）------------------------

test('古い形式の fixture は、読み込むと演出（takes）へ変換される', () => {
  const tl = normalizeTimelineV3(fixture.timeline);
  assert.equal(tl.schema, 3);
  assert.equal(tl.segments.length, 3);
  for (const seg of tl.segments) {
    assert.ok(seg.takes.length > 0, `区間 (${seg.lap},${seg.camera}) に演出が無い`);
    assert.equal('cues' in seg, false, '古いキーは編集モデルへ持ち込まない');
    assert.equal('insert' in seg, false);
  }
});

test('cue は「そのまま + 重ねる素材」のカット 1 枚になる（override は明示値へ）', () => {
  const tl = normalizeTimelineV3(fixture.timeline);
  const seg = tl.segments.find((s) => s.lap === 1 && s.camera === 0);
  const t = seg.takes[0];
  assert.equal(t.at, TAKE.AT_ENTER);
  assert.equal(t.steps.length, 1);
  assert.equal(t.steps[0].source, TAKE.SRC_INHERIT);
  assert.equal(t.steps[0].cueId, 'cue_A_1');
  assert.equal(t.steps[0].durKind, TAKE.DUR_UNTIL_CLIP_END);
  assert.equal(t.steps[0].strength, 0.5);
  assert.equal(t.steps[0].fadeInSec, 1.2);
});

test('インサートショットは「ライブ別カメラ」のカットになる（exit は離脱時の演出）', () => {
  const tl = normalizeTimelineV3(fixture.timeline);
  const segB = tl.segments.find((s) => s.lap === 1 && s.camera === 1);
  const exitTake = segB.takes.find((t) => t.at === TAKE.AT_EXIT);
  assert.ok(exitTake, 'exit インサートが離脱時の演出になっていない');
  assert.equal(exitTake.offsetSec, 0, '離脱時は開始オフセットを持たない');
  assert.equal(exitTake.steps[0].source, TAKE.SRC_LIVE);
  assert.equal(exitTake.steps[0].camera, 2);
  assert.equal(exitTake.steps[0].durKind, TAKE.DUR_SEC);
  assert.equal(exitTake.steps[0].durSec, 3);
});

test('区間の post / bgm は present-flag つきで残る', () => {
  const tl = normalizeTimelineV3(fixture.timeline);
  const segA = tl.segments.find((s) => s.lap === 1 && s.camera === 0);
  const segB = tl.segments.find((s) => s.lap === 1 && s.camera === 1);
  assert.equal(segB.hasPost, true);
  assert.equal(segB.post.exposure, 0.3);
  assert.equal(segB.hasBgm, true);
  assert.equal(segB.bgm.trackId, 'bgm_horror');
  assert.equal(segA.hasBgm, false, '指示の無い区間は BGM を持たない');
});

// ---- BGM 帯の carry-forward（Unity BgmPlanLogic の JS ミラー）--------------------
test('resolveBgmLane carries the current track forward across silent segments', () => {
  const segs = [
    { lap: 1, camera: 0, hasBgm: true, bgm: { action: 'play', trackId: 'bgm_a' } },
    { lap: 1, camera: 2, hasBgm: true, bgm: { action: 'stop' } },
    { lap: 2, camera: 1, hasBgm: true, bgm: { action: 'play', trackId: 'bgm_a' } },
  ];
  const lane = resolveBgmLane(segs, [0, 1, 2], 2, '');
  // 1 周目: cam0 で開始 → cam1 は指示なしで継続 → cam2 で停止
  assert.deepEqual(lane.get('1:0'), { trackId: 'bgm_a', change: 'start' });
  assert.deepEqual(lane.get('1:1'), { trackId: 'bgm_a', change: null });
  assert.deepEqual(lane.get('1:2'), { trackId: '', change: 'stop' });
  // 2 周目: cam0 は無音のまま → cam1 で再開 → cam2 は継続
  assert.deepEqual(lane.get('2:0'), { trackId: '', change: null });
  assert.deepEqual(lane.get('2:1'), { trackId: 'bgm_a', change: 'start' });
  assert.deepEqual(lane.get('2:2'), { trackId: 'bgm_a', change: null });
});

test('resolveBgmLane starts from the run default and marks same-track as retune', () => {
  const segs = [
    { lap: 1, camera: 1, hasBgm: true, bgm: { action: 'play', trackId: 'bgm_root', loopStartSec: 10 } },
    { lap: 1, camera: 2, hasBgm: true, bgm: { action: 'play', trackId: 'bgm_root', restart: true } },
  ];
  const lane = resolveBgmLane(segs, [0, 1, 2], 1, 'bgm_root');
  assert.deepEqual(lane.get('1:0'), { trackId: 'bgm_root', change: null });   // ラン既定が鳴っている
  assert.deepEqual(lane.get('1:1'), { trackId: 'bgm_root', change: 'retune' }); // 同一トラック → 位置維持
  assert.deepEqual(lane.get('1:2'), { trackId: 'bgm_root', change: 'start' });  // restart → 頭出し
});

// ---- CG 人形の立ち位置（steps[].placement）------------------------------------
//   位置は「人形」ではなく「カット」が持つ。同じ人形を別のカットで別の場所に立たせるため
//   （2026-07-27 の作り直し）。present-flag は宣言 bool ∧ 実体の AND 規約。

test('立ち位置は宣言 bool が false なら書き出さない（幽霊 placement を作らない）', () => {
  const tl = normalizeTimelineV3({
    rev: 1,
    segments: [{
      lap: 1, camera: 0,
      takes: [{ id: 'L1C0#0', steps: [{ source: 'live', camera: 0, cg: 'doll', cgMode: 'fixed' }] }],
    }],
  });
  const step = tl.segments[0].takes[0].steps[0];
  assert.equal(step.hasPlacement, false);

  const out = serializeTimelineV3(tl);
  const s = out.segments[0].takes[0].steps[0];
  assert.equal(s.hasPlacement, false);
  assert.equal('placement' in s, false, 'flag=false のとき入れ子キー自体を出さない');
});

test('立ち位置は読み込み → 書き出しで値が保たれる', () => {
  const tl = normalizeTimelineV3({
    rev: 1,
    segments: [{
      lap: 2, camera: 1,
      takes: [{
        id: 'L2C1#0',
        steps: [{
          source: 'live', camera: 1, cg: 'doll', cgMode: 'fixed',
          placement: { x: 0.35, z: -0.4, yawDeg: 180 }, hasPlacement: true,
        }],
      }],
    }],
  });
  const step = tl.segments[0].takes[0].steps[0];
  assert.equal(step.hasPlacement, true);
  assert.deepEqual(step.placement, { x: 0.35, z: -0.4, yawDeg: 180 });

  const s = serializeTimelineV3(tl).segments[0].takes[0].steps[0];
  assert.equal(s.hasPlacement, true);
  assert.deepEqual(s.placement, { x: 0.35, z: -0.4, yawDeg: 180 });
});

test('立ち位置の欠けた軸は 0 で埋まる（NaN を書き出さない）', () => {
  const tl = normalizeTimelineV3({
    rev: 1,
    segments: [{
      lap: 1, camera: 0,
      takes: [{
        id: 'L1C0#0',
        steps: [{ source: 'live', cg: 'doll', placement: { x: 0.2 }, hasPlacement: true }],
      }],
    }],
  });
  const s = serializeTimelineV3(tl).segments[0].takes[0].steps[0];
  assert.deepEqual(s.placement, { x: 0.2, z: 0, yawDeg: 0 });
});

// ---- 左右分割と第 2 の差し替え層（canon/LEDGER.md 0050）------------------------------

// ⚠⚠ 卓にはまだ編集 UI が無く、3 周目 A / 4 周目 A の分割は show.json を**手で書く**。
//    serializeStep はキーの白名簿なので、そこから漏れると 💾 保存の一押しで台本が消える。
//    この 2 本はその往復だけを固定する（UI が付いても意味は変わらない）。
test('左右分割と第 2 層は読み込み → 書き出しで往復する', () => {
  const tl = normalizeTimelineV3({
    rev: 1,
    segments: [{
      lap: 3, camera: 0,
      takes: [{
        id: 'L3C0#0',
        steps: [{
          source: 'live', camera: 0,
          splitX: 0.5, splitFlip: true, splitFreeze: true, overlay2CueId: 'plate_A',
        }],
      }],
    }],
  });
  const s = serializeTimelineV3(tl).segments[0].takes[0].steps[0];
  assert.equal(s.splitX, 0.5);
  assert.equal(s.splitFlip, true);
  assert.equal(s.splitFreeze, true);
  assert.equal(s.overlay2CueId, 'plate_A');
});

test('分割を指定していないカットは 0 / false / 空で書き出す（幽霊の分割を作らない）', () => {
  const tl = normalizeTimelineV3({
    rev: 1,
    segments: [{ lap: 1, camera: 0, takes: [{ id: 'L1C0#0', steps: [{ source: 'live', camera: 0 }] }] }],
  });
  const s = serializeTimelineV3(tl).segments[0].takes[0].steps[0];
  assert.equal(s.splitX, 0);
  assert.equal(s.splitFlip, false);
  assert.equal(s.splitFreeze, false);
  assert.equal(s.overlay2CueId, '');
});

test('splitX は 0..1 へ丸める（枠の外を指す台本を実機へ配らない）', () => {
  const tl = normalizeTimelineV3({
    rev: 1,
    segments: [{
      lap: 3, camera: 0,
      takes: [{ id: 'L3C0#0', steps: [{ source: 'live', camera: 0, splitX: 1.8 }] }],
    }],
  });
  assert.equal(serializeTimelineV3(tl).segments[0].takes[0].steps[0].splitX, 1);
});
