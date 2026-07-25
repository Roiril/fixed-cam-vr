// 廻リ視 タイムラインのデータ純関数（DOM 非依存）。node:test で固定する
//   （MonoBehaviour からの純ロジック分離の Web 版）。
//
//   **書き出しは演出・カット（schema 3）だけ**（2026-07-25 に一本化）。
//   古い形式（cues[] / insert）は **読むときに migrateV2Segment で変換する**だけで、もう書かない。
//   Unity 側も同じで、TimelineMigration.FromV2 が同じ変換をしてから TakeRunner が実行する
//   （変換の一致は共有 fixture show_timeline_canonical.json で機械照合する）。
//
//   present-flag 契約（Web⇄Unity）: Unity は hasPost / hasBgm の bool を宣言値と入れ子の
//   AND で確定する（TimelinePresentFlags.Reconcile）。したがって serializeTimelineV3 は
//   has*=false のとき対応する入れ子キーを show.json から省く。has* bool 自体は常に出力する。

import { FX_DEFAULT } from './common.js';

// 区間 BGM 指示。action は continue（既定＝鳴っている曲がそのまま続く）/ play / stop。
//   -1 は「トラック既定を継承」（Unity ShowBgmDef と同契約）。
export function defaultBgm() {
  return {
    action: 'continue', trackId: '', loop: true, startSec: 0,
    loopStartSec: -1, loopEndSec: -1, volume: -1,
    fadeInSec: 1, fadeOutSec: 1, restart: false,
  };
}
export function newSeg(lap, camera) {
  return {
    lap, camera,
    takes: [],                    // 演出（このモデルの本体）
    post: { ...FX_DEFAULT }, hasPost: false,
    bgm: defaultBgm(), hasBgm: false,
  };
}

// 区間列を course.order 順に走査し、各区間で「鳴っている BGM」を carry-forward で解決する。
//   Unity 側 BgmPlanLogic.Decide の JS ミラー（指示の無い区間は前の曲が続く / 同一トラックは retune）。
//   タイムラインの BGM 帯（どこで曲が変わり、どこで止まるか）と ▶ 検証パネルが共有する。
//   返り値: Map<"lap:camera", { trackId, change }>  change = 'start' | 'retune' | 'stop' | null
export function resolveBgmLane(segments, order, lapCount, rootTrackId = '') {
  const at = new Map();
  const segs = segments || [];
  let cur = rootTrackId || '';
  for (let lap = 1; lap <= lapCount; lap++) {
    for (const cam of order || []) {
      const s = segs.find((x) => x.lap === lap && x.camera === cam);
      let change = null;
      if (s && s.hasBgm && s.bgm) {
        const a = s.bgm.action;
        if (a === 'play' && s.bgm.trackId) {
          change = cur === s.bgm.trackId ? (s.bgm.restart ? 'start' : 'retune') : 'start';
          cur = s.bgm.trackId;
        } else if (a === 'stop') {
          if (cur) change = 'stop';
          cur = '';
        }
      }
      at.set(`${lap}:${cam}`, { trackId: cur, change });
    }
  }
  return at;
}

// ===========================================================================
// v3（演出 Take / カット Step）— 契約の正本は
//   .claude/plans/2026-07-25_shot-timeline-foundation.md §6
// Unity 側の対応: ShowTakeSchema.cs（型・判別子・既定）/ TimelineMigration.cs（v2→v3）。
// 共有 fixture: Assets/Tests/Fixtures/show_timeline_v3_canonical.json（両側から読んで機械照合）。
//
// 設計上の要点: 入れ子を作らずフラットな文字列判別子 + 値にしてある（JsonUtility の
// 「省略キーの入れ子を既定インスタンスで作る」癖に晒される面を最小化するため）。
// present-flag が要るのは post だけで、他の任意値は -1（= 素材定義から継承）で表す。
// ===========================================================================

export const TAKE = {
  AT_ENTER: 'enter', AT_EXIT: 'exit',
  MISSED_FIRE_ON_EXIT: 'fireOnExit', MISSED_SKIP: 'skip',
  POLICY_HOLD: 'hold', POLICY_YIELD: 'yield',
  SRC_LIVE: 'live', SRC_INHERIT: 'inherit', SRC_CLIP: 'clip', SRC_STILL: 'still',
  DUR_SEC: 'sec', DUR_UNTIL_CLIP_END: 'untilClipEnd',
  TRANS_CUT: 'cut', TRANS_DIP: 'dip', TRANS_FADE: 'fade',
  DEFAULT_MAX_DURATION_SEC: 45,
  FALLBACK_STEP_DUR_SEC: 4,
};

export function newStep(over = {}) {
  return {
    source: TAKE.SRC_LIVE, camera: -1, assetUrl: '',
    cueId: '', strength: -1, fadeInSec: -1, fadeOutSec: -1, trimStartSec: -1, trimEndSec: -1,
    durKind: TAKE.DUR_SEC, durSec: TAKE.FALLBACK_STEP_DUR_SEC,
    transition: TAKE.TRANS_DIP, transitionMs: 0,
    post: { ...FX_DEFAULT }, hasPost: false,
    ...over,
  };
}

export function newTake(id, over = {}) {
  return {
    id, name: '',
    at: TAKE.AT_ENTER, offsetSec: 0, ifMissed: TAKE.MISSED_FIRE_ON_EXIT,
    policy: TAKE.POLICY_HOLD, once: true, maxDurationSec: 0,
    steps: [],
    ...over,
  };
}

/** 演出 id は区間内で一意。Unity TimelineMigration.MakeId と同じ規則。 */
export const takeId = (lap, camera, index) => `L${lap}C${camera}#${index}`;

// 保存用（キー順は schema ドキュメント順。has*=false のとき post キー自体を出さない）。
function serializeStep(s) {
  const out = {
    source: s.source || TAKE.SRC_LIVE,
    camera: Number.isInteger(s.camera) ? s.camera : -1,
    assetUrl: s.assetUrl || '',
    cueId: s.cueId || '',
    strength: num(s.strength, -1), fadeInSec: num(s.fadeInSec, -1), fadeOutSec: num(s.fadeOutSec, -1),
    trimStartSec: num(s.trimStartSec, -1), trimEndSec: num(s.trimEndSec, -1),
    durKind: s.durKind || TAKE.DUR_SEC,
    durSec: num(s.durSec, 0),
    transition: s.transition || TAKE.TRANS_DIP,
    transitionMs: num(s.transitionMs, 0),
  };
  if (s.hasPost) out.post = { ...FX_DEFAULT, ...(s.post || {}) };
  out.hasPost = !!s.hasPost;
  return out;
}

function serializeTake(t) {
  return {
    id: t.id || '',
    name: t.name || '',
    at: t.at === TAKE.AT_EXIT ? TAKE.AT_EXIT : TAKE.AT_ENTER,
    offsetSec: t.at === TAKE.AT_EXIT ? 0 : num(t.offsetSec, 0),   // exit は offset を持ち込まない
    ifMissed: t.ifMissed === TAKE.MISSED_SKIP ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT,
    policy: t.policy === TAKE.POLICY_YIELD ? TAKE.POLICY_YIELD : TAKE.POLICY_HOLD,
    once: t.once !== false,
    maxDurationSec: num(t.maxDurationSec, 0),
    steps: (t.steps || []).map(serializeStep),
  };
}

const num = (v, def) => (Number.isFinite(v) ? v : def);

/**
 * v3 として保存する。**v2 キー（cues / insert）は書き出さない** — 「どちらが正か」が現場で分岐して
 * 事故るのを防ぐため（present-flag の幽霊インサート事故と同型）。区間 post / bgm は従来どおり。
 */
export function serializeTimelineV3(timeline) {
  const segs = (timeline.segments || [])
    .filter((s) => (s.takes && s.takes.length) || s.hasPost || s.hasBgm)
    .map((s) => {
      const out = { lap: s.lap, camera: s.camera };
      out.takes = (s.takes || []).map(serializeTake);
      if (s.hasPost) out.post = { ...FX_DEFAULT, ...(s.post || {}) };
      out.hasPost = !!s.hasPost;
      if (s.hasBgm) out.bgm = { ...defaultBgm(), ...(s.bgm || {}) };
      out.hasBgm = !!s.hasBgm;
      return out;
    });
  return { rev: timeline.rev, schema: 3, segments: segs };
}

/** load 時の正規化（既定を再充填して in-memory 編集モデルへ戻す）。v2 区間はここで takes へ変換する。 */
export function normalizeTimelineV3(tl) {
  const segs = (tl.segments || []).map((s) => {
    const seg = newSeg(s.lap, s.camera);
    seg.takes = Array.isArray(s.takes) && s.takes.length
      ? s.takes.map((t) => normalizeTake(t))
      : migrateV2Segment(s);
    seg.hasPost = !!s.hasPost; if (s.post) seg.post = { ...FX_DEFAULT, ...s.post };
    seg.hasBgm = !!s.hasBgm; if (s.bgm) seg.bgm = { ...defaultBgm(), ...s.bgm };
    return seg;
  });
  return { rev: Number.isInteger(tl.rev) ? tl.rev : 1, schema: 3, segments: segs };
}

function normalizeTake(t) {
  const out = newTake(t.id || '', {
    name: t.name || '',
    at: t.at === TAKE.AT_EXIT ? TAKE.AT_EXIT : TAKE.AT_ENTER,
    offsetSec: num(t.offsetSec, 0),
    ifMissed: t.ifMissed === TAKE.MISSED_SKIP ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT,
    policy: t.policy === TAKE.POLICY_YIELD ? TAKE.POLICY_YIELD : TAKE.POLICY_HOLD,
    once: t.once !== false,
    maxDurationSec: num(t.maxDurationSec, 0),
  });
  out.steps = (t.steps || []).map((s) => newStep({
    ...s,
    post: { ...FX_DEFAULT, ...(s.post || {}) },
    hasPost: !!s.hasPost,
  }));
  return out;
}

/**
 * v2 区間（cues[] / insert）→ takes[] の決定的変換。
 * **Unity の TimelineMigration.FromV2 と同じ結果を返すこと**（並びは cues の順 → 最後に insert）。
 */
export function migrateV2Segment(seg) {
  const takes = [];
  const cues = Array.isArray(seg.cues) ? seg.cues : [];
  cues.forEach((c) => {
    if (!c || !c.cueId) return;
    const ov = c.hasOverride ? (c.override || {}) : null;
    takes.push(newTake(takeId(seg.lap, seg.camera, takes.length), {
      name: c.cueId,
      at: TAKE.AT_ENTER,
      offsetSec: num(c.delaySec, 0),
      once: c.once !== false,
      steps: [newStep({
        source: TAKE.SRC_INHERIT, camera: -1,
        cueId: c.cueId,
        strength: ov ? num(ov.strength, -1) : -1,
        fadeInSec: ov ? num(ov.fadeIn, -1) : -1,
        fadeOutSec: ov ? num(ov.fadeOut, -1) : -1,
        trimStartSec: ov ? num(ov.trimStart, -1) : -1,
        trimEndSec: ov ? num(ov.trimEnd, -1) : -1,
        durKind: TAKE.DUR_UNTIL_CLIP_END, durSec: 0,
        transition: TAKE.TRANS_FADE,
      })],
    }));
  });

  if (seg.hasInsert && seg.insert) {
    const ins = seg.insert;
    const exit = ins.anchor === TAKE.AT_EXIT;
    takes.push(newTake(takeId(seg.lap, seg.camera, takes.length), {
      name: ins.cueId || 'insert',
      at: exit ? TAKE.AT_EXIT : TAKE.AT_ENTER,
      offsetSec: exit ? 0 : num(ins.delaySec, 0),
      once: ins.once !== false,
      steps: [newStep({
        source: TAKE.SRC_LIVE, camera: Number.isInteger(ins.camera) ? ins.camera : 0,
        cueId: ins.cueId || '',
        durKind: TAKE.DUR_SEC, durSec: num(ins.durationSec, TAKE.FALLBACK_STEP_DUR_SEC),
        transition: TAKE.TRANS_DIP,
        post: { ...FX_DEFAULT, ...(ins.post || {}) },
        hasPost: !!(ins.hasPost && ins.post),
      })],
    }));
  }
  return takes;
}

/** タイムラインが v3 か（明示 schema か、takes を持つ区間が 1 つでもあれば v3）。 */
export function isV3(tl) {
  if (!tl) return false;
  if (Number.isInteger(tl.schema) && tl.schema >= 3) return true;
  return (tl.segments || []).some((s) => s && Array.isArray(s.takes) && s.takes.length > 0);
}
