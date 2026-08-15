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

/**
 * 演出（Take）の実行中に鳴っている BGM を解く。
 *   Unity 側の対応: TakeRunner が BgmDirector.BeginTakeOverride / EndTakeOverride を呼ぶ。
 *   指示が無い（hasBgm=false / continue / トラック未指定）演出は**区間の曲がそのまま続く**
 *   （＝「カメラ A の演出なら、A の区間で鳴っている曲のまま」）。
 *   演出が終われば必ずレーン（区間の曲）へ戻るので、レーンの carry-forward は演出に影響されない。
 *
 * 返り値: { trackId, change } — change = 'start'（演出で曲が変わる）/ 'stop'（演出中は無音）/ null（そのまま）
 */
export function resolveTakeBgm(take, laneTrackId = '') {
  const b = take && take.hasBgm ? take.bgm : null;
  if (!b) return { trackId: laneTrackId, change: null };
  if (b.action === 'stop') return { trackId: '', change: laneTrackId ? 'stop' : null };
  if (b.action === 'play' && b.trackId) {
    return { trackId: b.trackId, change: b.trackId === laneTrackId && !b.restart ? null : 'start' };
  }
  return { trackId: laneTrackId, change: null };
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
  // 開始規則: 進入 +t 秒（時刻）/ 離脱時（事象）/ このラインを通過したら（場所・layout.lines の線分）。
  AT_ENTER: 'enter', AT_EXIT: 'exit', AT_LINE: 'line',
  MISSED_FIRE_ON_EXIT: 'fireOnExit', MISSED_SKIP: 'skip',
  POLICY_HOLD: 'hold', POLICY_YIELD: 'yield',
  // 画面が塞がっていて出られなかったときの待ちの寿命。
  //   segment（既定）= 自分の区間まで（従来の挙動）
  //   chain          = 自分を塞いでいた演出が終わるまで、区間を出ても待つ
  WAIT_SEGMENT: 'segment', WAIT_CHAIN: 'chain',
  SRC_LIVE: 'live', SRC_INHERIT: 'inherit', SRC_CLIP: 'clip', SRC_STILL: 'still', SRC_REC: 'rec',
  // そのカメラで撮った無人プレート。still と絵は同じだが CG 人形を重ねられる（パースが一致するため）。
  SRC_PLATE: 'plate',
  CG_FOLLOW: 'follow', CG_FIXED: 'fixed',
  SLOT_SCHEME: 'slot://',
  DUR_SEC: 'sec', DUR_UNTIL_CLIP_END: 'untilClipEnd', DUR_UNTIL_ZONE_CHANGE: 'untilZoneChange',
  // TRANS_GLITCH = 黒ではなく「映像の乱れ」で覆って、その最中に差し替える（企画書 2.3）。
  TRANS_CUT: 'cut', TRANS_DIP: 'dip', TRANS_FADE: 'fade', TRANS_GLITCH: 'glitch',
  DEFAULT_MAX_DURATION_SEC: 45,
  FALLBACK_STEP_DUR_SEC: 4,
  DEFAULT_STEP_GLITCH_SEC: 0.25,
};

export function newStep(over = {}) {
  return {
    source: TAKE.SRC_LIVE, camera: -1, assetUrl: '', recLap: 0,
    cueId: '', strength: -1, fadeInSec: -1, fadeOutSec: -1, trimStartSec: -1, trimEndSec: -1,
    durKind: TAKE.DUR_SEC, durSec: TAKE.FALLBACK_STEP_DUR_SEC,
    transition: TAKE.TRANS_DIP, transitionMs: 0,
    // カット頭で 1 回だけ走らせる乱れ（遷移の glitch とは別物。注意・移動の誘導に使う）。
    glitch: 0, glitchSec: 0,
    // 「装置らしさ」の演出。hold = 画が止まる秒数 / burn = 焼き付きの濃さ（動いたものの跡だけが残る）
    // / aura = 人形のまわりだけ画が荒れる強さ（対象に紐づく乱れ＝機材のせいにできない）。
    hold: 0, burn: 0, burnSec: 0, aura: 0,
    // 左右分割と第 2 の差し替え層（canon/LEDGER.md 0050）。3 周目 A と 4 周目 A だけが使う。
    // ⚠ **卓にまだ編集 UI が無い**（show.json を手で書く）。ここに置いてあるのは
    //    「卓が保存したときに消えない」ようにするため — serializeStep はキーの白名簿なので、
    //    足さないと手で書いた値が 💾 保存のたびに黙って消える。
    splitX: 0, splitFlip: false, splitFreeze: false, overlay2CueId: '',
    cg: '', cgMode: TAKE.CG_FOLLOW,
    // CG 人形の立ち位置（course 空間）。**人形ではなくカットが持つ** — 同じ人形を別のカットで
    // 別の場所に立たせるため。hasPlacement が present-flag（宣言 bool ∧ 実体の AND 規約）。
    placement: { x: 0, z: 0, yawDeg: 0 }, hasPlacement: false,
    post: { ...FX_DEFAULT }, hasPost: false,
    ...over,
  };
}

export function newTake(id, over = {}) {
  return {
    id, name: '',
    at: TAKE.AT_ENTER, offsetSec: 0, ifMissed: TAKE.MISSED_FIRE_ON_EXIT,
    // at=line のとき: どのラインか（layout.lines[].id）。ラインは担当カメラに紐づく。
    lineId: '',
    policy: TAKE.POLICY_HOLD, wait: TAKE.WAIT_SEGMENT, once: true, maxDurationSec: 0,
    steps: [],
    // 演出中だけの BGM（hasBgm=false = 区間で鳴っている曲がそのまま続く）。
    bgm: defaultBgm(), hasBgm: false,
    ...over,
  };
}

/** 演出 id は区間内で一意。Unity TimelineMigration.MakeId と同じ規則。 */
export const takeId = (lap, camera, index) => `L${lap}C${camera}#${index}`;

// 保存用（キー順は schema ドキュメント順。has*=false のとき post キー自体を出さない）。
// 判別子は保存時に既定へ倒す（Unity 側 TakeSchema.Normalize* と同じ倒し先）。
// 未知値をそのまま書き戻すと、実機で毎回警告ログが出るだけの無効値が show.json に残る。
const oneOf = (v, allowed, def) => (allowed.includes(v) ? v : def);

function serializeStep(s) {
  const out = {
    source: oneOf(s.source, [TAKE.SRC_LIVE, TAKE.SRC_INHERIT, TAKE.SRC_CLIP, TAKE.SRC_STILL, TAKE.SRC_REC,
                             TAKE.SRC_PLATE], TAKE.SRC_LIVE),
    camera: Number.isInteger(s.camera) ? s.camera : -1,
    assetUrl: s.assetUrl || '',
    recLap: Number.isInteger(s.recLap) ? s.recLap : 0,
    cueId: s.cueId || '',
    strength: num(s.strength, -1), fadeInSec: num(s.fadeInSec, -1), fadeOutSec: num(s.fadeOutSec, -1),
    trimStartSec: num(s.trimStartSec, -1), trimEndSec: num(s.trimEndSec, -1),
    durKind: oneOf(s.durKind, [TAKE.DUR_SEC, TAKE.DUR_UNTIL_CLIP_END, TAKE.DUR_UNTIL_ZONE_CHANGE], TAKE.DUR_SEC),
    durSec: num(s.durSec, 0),
    transition: oneOf(s.transition,
      [TAKE.TRANS_CUT, TAKE.TRANS_DIP, TAKE.TRANS_FADE, TAKE.TRANS_GLITCH], TAKE.TRANS_DIP),
    transitionMs: num(s.transitionMs, 0),
    glitch: num(s.glitch, 0),
    glitchSec: num(s.glitchSec, 0),
    hold: num(s.hold, 0),
    burn: num(s.burn, 0),
    burnSec: num(s.burnSec, 0),
    aura: num(s.aura, 0),
    // 左右分割と第 2 層（canon/LEDGER.md 0050）。**卓に編集 UI は無いが必ず書き戻す** —
    // 白名簿から漏れると、show.json へ手で書いた台本が 💾 保存の一押しで消える。
    splitX: Math.max(0, Math.min(1, num(s.splitX, 0))),
    splitFlip: !!s.splitFlip,
    splitFreeze: !!s.splitFreeze,
    overlay2CueId: s.overlay2CueId || '',
    cg: s.cg || '',
    cgMode: oneOf(s.cgMode, [TAKE.CG_FOLLOW, TAKE.CG_FIXED], TAKE.CG_FOLLOW),
  };
  if (s.hasPost) out.post = { ...FX_DEFAULT, ...(s.post || {}) };
  out.hasPost = !!s.hasPost;
  // 立ち位置は宣言 bool を正にする（flag=false のとき入れ子キーを出さない = 幽霊 placement を作らない）。
  if (s.hasPlacement && s.placement) {
    out.placement = {
      x: num(s.placement.x, 0), z: num(s.placement.z, 0), yawDeg: num(s.placement.yawDeg, 0),
    };
    out.hasPlacement = true;
  } else {
    out.hasPlacement = false;
  }
  return out;
}

function serializeTake(t) {
  const at = oneOf(t.at, [TAKE.AT_ENTER, TAKE.AT_EXIT, TAKE.AT_LINE], TAKE.AT_ENTER);
  const out = {
    id: t.id || '',
    name: t.name || '',
    at,
    // 進入 +t 秒だけが offsetSec を持つ（exit は事象・line は場所なので 0 で書き出す）。
    offsetSec: at === TAKE.AT_ENTER ? num(t.offsetSec, 0) : 0,
    ifMissed: t.ifMissed === TAKE.MISSED_SKIP ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT,
    lineId: at === TAKE.AT_LINE ? (t.lineId || '') : '',
    policy: t.policy === TAKE.POLICY_YIELD ? TAKE.POLICY_YIELD : TAKE.POLICY_HOLD,
    // 離脱時の演出は持ち越せない（別の区間で出すと文脈が最も壊れる）。
    wait: (t.wait === TAKE.WAIT_CHAIN && t.at !== TAKE.AT_EXIT) ? TAKE.WAIT_CHAIN : TAKE.WAIT_SEGMENT,
    once: t.once !== false,
    maxDurationSec: num(t.maxDurationSec, 0),
    steps: (t.steps || []).map(serializeStep),
  };
  // present-flag 規約: false のときは入れ子キー自体を出さない（幽霊指示を作らない）。
  if (t.hasBgm) out.bgm = { ...defaultBgm(), ...(t.bgm || {}) };
  out.hasBgm = !!t.hasBgm;
  return out;
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
    at: oneOf(t.at, [TAKE.AT_ENTER, TAKE.AT_EXIT, TAKE.AT_LINE], TAKE.AT_ENTER),
    offsetSec: num(t.offsetSec, 0),
    lineId: typeof t.lineId === 'string' ? t.lineId : '',
    ifMissed: t.ifMissed === TAKE.MISSED_SKIP ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT,
    policy: t.policy === TAKE.POLICY_YIELD ? TAKE.POLICY_YIELD : TAKE.POLICY_HOLD,
    // 離脱時の演出は持ち越せない（別の区間で出すと文脈が最も壊れる）。
    wait: (t.wait === TAKE.WAIT_CHAIN && t.at !== TAKE.AT_EXIT) ? TAKE.WAIT_CHAIN : TAKE.WAIT_SEGMENT,
    once: t.once !== false,
    maxDurationSec: num(t.maxDurationSec, 0),
    hasBgm: !!t.hasBgm,
    bgm: { ...defaultBgm(), ...(t.bgm || {}) },
  });
  out.steps = (t.steps || []).map((s) => newStep({
    ...s,
    post: { ...FX_DEFAULT, ...(s.post || {}) },
    hasPost: !!s.hasPost,
    placement: { x: 0, z: 0, yawDeg: 0, ...(s.placement || {}) },
    hasPlacement: !!s.hasPlacement,
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
