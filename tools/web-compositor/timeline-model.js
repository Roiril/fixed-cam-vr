// 廻リ視 タイムラインのデータ純関数（DOM 非依存）。timeline.js の serialize/normalize/migrate と
//   in-memory ファクトリを切り出し、node:test で固定する（MonoBehaviour からの純ロジック分離の Web 版）。
//
//   present-flag 契約（Web⇄Unity）: Unity は hasPost/hasInsert/hasOverride/insert.hasPost の bool を
//   信用せず、ライブ受信パース直後に入れ子オブジェクトの null 有無から再導出する
//   （ShowControlClient.NormalizeTimelinePresentFlags → TimelinePresentFlags.Reconcile）。したがって
//   serializeTimeline は has*=false のとき対応する入れ子キー（override/post/insert/insert.post）を
//   show.json から省く（CameraDef の delete cam.post と同流儀）。has* bool 自体は常に出力する。

import { FX_DEFAULT } from './common.js';

export function defaultOverride() { return { strength: 1, fadeIn: 0.5, fadeOut: 0.5, trimStart: 0, trimEnd: 0 }; }
export function newAssign(cueId) { return { cueId, delaySec: 0, once: true, override: defaultOverride(), hasOverride: false }; }
export function defaultInsert() {
  return { anchor: 'exit', camera: 0, delaySec: 0, durationSec: 4, cueId: '', once: true, post: { ...FX_DEFAULT }, hasPost: false };
}
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
    lap, camera, cues: [], post: { ...FX_DEFAULT }, hasPost: false,
    insert: defaultInsert(), hasInsert: false, bgm: defaultBgm(), hasBgm: false,
  };
}

// 保存用に正規化（present-flag を確定・空セグメントを落とす・has*=false の入れ子キーを省く）。
//   in-memory 状態（s.insert/s.post/a.override が常在）と serialize 出力を分離する。
//   キー挿入順は show.json の可読性のため schema ドキュメント順（object → その直後に flag）。
export function serializeTimeline(timeline) {
  const segs = (timeline.segments || [])
    .filter((s) => (s.cues && s.cues.length) || s.hasPost || s.hasInsert || s.hasBgm)
    .map((s) => {
      const out = { lap: s.lap, camera: s.camera };
      out.cues = (s.cues || []).map((a) => {
        const c = { cueId: a.cueId, delaySec: a.delaySec || 0, once: a.once !== false };
        if (a.hasOverride) c.override = { ...defaultOverride(), ...(a.override || {}) }; // false 時は override キーを出さない
        c.hasOverride = !!a.hasOverride;
        return c;
      });
      if (s.hasPost) out.post = { ...FX_DEFAULT, ...(s.post || {}) };   // false 時は post キーを出さない
      out.hasPost = !!s.hasPost;
      if (s.hasInsert) {
        const ins = {
          anchor: (s.insert && s.insert.anchor) || 'exit',
          camera: (s.insert && s.insert.camera) || 0,
          delaySec: (s.insert && s.insert.delaySec) || 0,
          durationSec: (s.insert && s.insert.durationSec) || 4,
          cueId: (s.insert && s.insert.cueId) || '',
          once: !(s.insert && s.insert.once === false),
        };
        const insHasPost = !!(s.insert && s.insert.hasPost);
        if (insHasPost) ins.post = { ...FX_DEFAULT, ...((s.insert && s.insert.post) || {}) }; // false 時は insert.post キーを出さない
        ins.hasPost = insHasPost;
        out.insert = ins;                                              // false 時は insert キーごと出さない
      }
      out.hasInsert = !!s.hasInsert;
      if (s.hasBgm) out.bgm = { ...defaultBgm(), ...(s.bgm || {}) };   // false 時は bgm キーを出さない
      out.hasBgm = !!s.hasBgm;
      return out;
    });
  return { rev: timeline.rev, segments: segs };
}

// load 時の正規化（既定を再充填し in-memory 編集モデルへ戻す）。null 安全。
export function normalizeTimeline(tl) {
  const segs = (tl.segments || []).map((s) => {
    const seg = newSeg(s.lap, s.camera);
    seg.cues = Array.isArray(s.cues) ? s.cues.map((a) => ({
      cueId: a.cueId, delaySec: a.delaySec || 0, once: a.once !== false,
      override: { ...defaultOverride(), ...(a.override || {}) }, hasOverride: !!a.hasOverride,
    })) : [];
    seg.hasPost = !!s.hasPost; if (s.post) seg.post = { ...FX_DEFAULT, ...s.post };
    seg.hasInsert = !!s.hasInsert;
    if (s.insert) seg.insert = { ...defaultInsert(), ...s.insert, post: { ...FX_DEFAULT, ...(s.insert.post || {}) } };
    seg.hasBgm = !!s.hasBgm;
    if (s.bgm) seg.bgm = { ...defaultBgm(), ...s.bgm };
    return seg;
  });
  return { rev: Number.isInteger(tl.rev) ? tl.rev : 1, segments: segs };
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

// schedule.entries → timeline（編集開始・(lap,camera) で cue を畳む）。
export function migrateFromSchedule(entries, rev) {
  const out = { rev: Number.isInteger(rev) ? rev : 1, segments: [] };
  for (const e of entries) {
    if (!Number.isInteger(e.lap) || !Number.isInteger(e.camera) || !e.cueId) continue;
    let s = out.segments.find((x) => x.lap === e.lap && x.camera === e.camera);
    if (!s) { s = newSeg(e.lap, e.camera); out.segments.push(s); }
    const a = newAssign(e.cueId); a.delaySec = e.delaySec || 0; a.once = e.once !== false;
    s.cues.push(a);
  }
  return out;
}
