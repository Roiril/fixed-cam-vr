// 廻リ視 ショット・タイムライン — 演出・カットのリボン UI（オーサリングの唯一の面）。
//   show.json の timeline が古い形式（cues[] / insert）でも、読み込み時に演出へ変換して扱う。
//   契約の正本: .claude/plans/2026-07-25_shot-timeline-foundation.md §4（UI）/ §6（スキーマ）。
//
//   描画の確定仕様（§4・変更不可）:
//     - 区間 = 伸縮ブロック（斜線ハッチ）／演出 = 尺に比例した固定幅。
//       この対比が「体験者が決める時間 / こちらが決める時間」の違いをそのまま表す。
//     - ドラッグのスナップは 2 種のみ。区間ブロック内に落とせば「進入 +t 秒」、
//       区間の右境界に磁石で吸着させると「離脱時」。連続的に移り変わる遷移は作らない
//       （enter+t と exit は同じ数直線上にないため、滑らかな遷移は嘘になる）。
//     - 離脱時の演出は区間ブロックの外に、境界線をまたいで描く。
//     - 位置だけに意味を持たせない。ヘッダに常時テキストで「進入 +20s」「離脱時」を出す。
//   UI 文言は「演出 / カット / 映すもの / 素材」。cue / インサートショット / anchor は出さない。
//
//   書き出しは serializeTimelineV3 のみ（v2 キー cues / insert / hasInsert は書かない）。
//   ▶ 検証は 🕹 ショーシミュレーション（show-sim.js）へ委譲する（歩きで実時間検証する方が強く、
//   検証面を 2 つ持たないため。矢印キーの簡易シミュレーションは 2026-07-25 に廃止した）。
//
//   区間ブロックには **実測の平均滞在時間**（capture-server が heartbeat から集計）を出す。
//   「進入 +20s」の演出が実測平均 8s の区間に置かれている、という設計上いちばん危ない状態を
//   作者に見せる唯一の手段（計画 §4 / §8 論点 5）。

import { FX, FX_DEFAULT, camColor, escapeHtml, isVideoUrl } from './common.js';
import { createCueEditor } from './cue-editor.js';
import { durationOf, onDurationResolved } from './media-duration.js';
import { resolveStepDuration } from './show-scenario.js';
import {
  linesFromLayout, cameraAtPoint, zonesFromLayout, lineMid,
  LINE_DIR_FWD, LINE_DIR_BACK,
} from './zone-layout.js';
import {
  TAKE, newTake, newStep, takeId, newSeg, defaultBgm, isV3,
  serializeTimelineV3, normalizeTimelineV3, resolveBgmLane, resolveTakeBgm,
} from './timeline-model.js';

// APK 同梱の既定クリップを指す擬似トラック id（Unity BgmDirector.DefaultTrackId と一致させること）。
const DEFAULT_TRACK_ID = '__default__';

// ---- 描画スケール -----------------------------------------------------------
const PX = 11;          // 1 秒あたり px（演出ブロックの幅 = 尺に比例）
const STEP_MIN = 110;   // カット 1 枚の最小幅（短い尺でも「映すもの」が読める下限）
const MAGNET = 22;      // 区間の右境界への吸着幅（px）= 「離脱時」スナップ
const LANE_PAD = 8;     // 区間ブロック内レーンの左余白（進入 0s の位置）
const LANE_H = 90;      // 演出 1 段の高さ（CSS .rb-seg-lane > .rb-take の height と一致させること）
const LANE_GAP = 4;
const SEG_MIN_W = 152;

const SRC_LABEL = {
  [TAKE.SRC_LIVE]: 'ライブカメラ',
  [TAKE.SRC_INHERIT]: 'そのまま',
  [TAKE.SRC_CLIP]: '事前映像',
  [TAKE.SRC_STILL]: '静止画',
  [TAKE.SRC_REC]: '録画（この体験の）',
};

export function createRibbon(container, deps) {
  // deps（app.js が渡す）: getCameras / getCues / getCourseOrder / getGlobalPost /
  //   getBgmTracks / getRootBgm / getLiveImg / getCaptures / refreshCaptures / saveCue / saveTimeline）
  container.innerHTML = `
    <div class="rb-wrap">
      <div class="rb-row">
        <button class="rb-save accent">💾 保存</button>
        <button class="rb-undo" title="直前の編集を取り消す（Ctrl+Z）" disabled>⟲ 元に戻す</button>
        <span class="rb-dirty"></span>
        <span class="rb-schema"></span>
        <span class="spacer"></span>
        <button class="rb-validate" title="🕹 ショーシミュレーションへ移動して、歩き（フロアマップのドット）でショーを実時間検証する">▶ 検証</button>
        <button class="rb-dwell-reset" title="区間に出ている「実測 平均滞在」の集計を消す（会場が変わった / リハをやり直す時）">⟲ 実測クリア</button>
        <button class="rb-lap-add" title="周回を 1 つ増やす">＋ 周回</button>
        <button class="rb-lap-del" title="最後の周回を削除">－ 周回</button>
      </div>
      <div class="rb-hint">区間（斜線）＝ 体験者が決める時間。演出（🎬）＝ こちらが決める時間で、幅は尺に比例する。
        演出はドラッグ（マウス / 指）か <b>← →</b> キーで動かす — 区間の中 = 進入 +t 秒 ／ 右境界に吸着 = 離脱時。</div>
      <div class="rb-note"></div>
      <div class="rb-track-wrap"><div class="rb-track"></div></div>
      <div class="rb-legend">
        <span class="e"><i></i>区間（伸縮・滞在は体験者しだい）</span>
        <span class="r"><i></i>演出（尺に比例した固定幅）</span>
        <span class="x"><i></i>離脱時の演出（区間の外・境界をまたぐ）</span>
        <span class="s"><i></i>📏 ラインの演出（時間軸に乗らない）</span>
      </div>
      <div class="rb-inspector"></div>
    </div>
    <div class="rb-drop-hint" style="display:none"></div>`;

  const q = (s) => container.querySelector(s);
  const track = q('.rb-track');
  const inspectorEl = q('.rb-inspector');
  const dirtyEl = q('.rb-dirty');
  const noteEl = q('.rb-note');
  const dropHint = q('.rb-drop-hint');

  // ---- 状態 ------------------------------------------------------------------
  let timeline = { rev: 1, schema: 3, segments: [] };
  let dirty = false;
  let cameras = [];
  let actors = [];          // 🎭 CG 人形（show.json actors[]）。カットの「CG 人形」選択肢。
  let cues = [];
  let lines = [];           // 📏 通過ライン（layout.lines）。演出の開始規則「このラインを通過したら」で選ぶ。
  let zoneBoxes = [];       // 展開済みゾーン矩形（線がどのカメラのゾーンに置かれているかを実機と同じ判定で出す）
  let order = null;
  let orderIsExplicit = false;
  let lapCount = 1;
  let sel = null;             // { kind:'seg'|'take', lap, camera, id? }
  let convertNote = '';       // 案内（保存で消える）
  let v2Source = false;       // 読み込んだ show.json がまだ v2（保存すると v3 で書き出される）
  let cueEditor = null;
  let editorOpen = false;
  let cueEditorTarget = null; // { step } — 新規素材の保存先

  // ---- undo（スナップショット方式）-------------------------------------------
  const UNDO_MAX = 30;
  let undoStack = [];
  let shadow = null;
  let savedSnap = null;
  const snap = () => JSON.stringify({ tl: timeline, lapCount });
  function resetUndo() { undoStack = []; shadow = snap(); savedSnap = shadow; renderUndo(); }
  function renderUndo() { const b = q('.rb-undo'); if (b) b.disabled = !undoStack.length; }
  function undo() {
    if (!undoStack.length) return;
    const prev = JSON.parse(undoStack.pop());
    timeline = prev.tl; lapCount = prev.lapCount;
    shadow = snap();
    dirty = shadow !== savedSnap;
    sel = null; closeCueEditor();
    renderDirty(); renderUndo(); render(); renderInspector();
  }
  function markDirty() {
    if (shadow) { undoStack.push(shadow); if (undoStack.length > UNDO_MAX) undoStack.shift(); }
    shadow = snap();
    dirty = true; renderDirty(); renderUndo();
  }
  function renderDirty() { dirtyEl.textContent = dirty ? '● 未保存' : ''; dirtyEl.className = 'rb-dirty' + (dirty ? ' on' : ''); }

  // ---- アクセサ ---------------------------------------------------------------
  function rows() {
    const n = cameras.length;
    orderIsExplicit = false;
    if (Array.isArray(order) && order.length) {
      const valid = order.filter((i) => Number.isInteger(i) && i >= 0 && i < n);
      if (valid.length) { orderIsExplicit = true; return valid; }
    }
    return cameras.map((_, i) => i);
  }
  function segAt(lap, camera) { return timeline.segments.find((s) => s.lap === lap && s.camera === camera) || null; }
  function ensureSeg(lap, camera) {
    let s = segAt(lap, camera);
    if (!s) { s = newSeg(lap, camera); timeline.segments.push(s); }
    return s;
  }
  function pruneSeg(s) {
    if (!s) return;
    if ((!s.takes || !s.takes.length) && !s.hasPost && !s.hasBgm) {
      timeline.segments = timeline.segments.filter((x) => x !== s);
    }
  }
  function takeById(lap, camera, id) {
    const s = segAt(lap, camera);
    return s ? (s.takes || []).find((t) => t.id === id) || null : null;
  }
  function selTake() { return sel && sel.kind === 'take' ? takeById(sel.lap, sel.camera, sel.id) : null; }
  function cueById(id) { return cues.find((c) => c.id === id) || null; }
  function cueName(id) { const c = cueById(id); return c ? (c.name || c.id) : id; }
  function cuesForCam(idx) {
    const id = cameras[idx] ? cameras[idx].id : null;
    return id == null ? [] : cues.filter((c) => (c.camera || '') === id);
  }
  // 演出専用カメラ（role:"fx"）は周回に出てこない＝作者が「どれが D か」を選ぶ時の手がかりが要る。
  function camLabel(idx) {
    const c = cameras[idx];
    if (!c) return `#${idx}`;
    return `カメラ ${c.id}${c.role === 'fx' ? '（演出専用）' : ''}`;
  }
  function globalPost() { return (deps.getGlobalPost && deps.getGlobalPost()) || FX_DEFAULT; }
  function captureItems() { return (deps.getCaptures && deps.getCaptures()) || []; }

  // 演出 id は区間内で一意（Unity は空なら L<lap>C<cam>#<i> を補うが、UI 側の選択キーにも使う）。
  function uniqueTakeId(seg, base) {
    const used = new Set((seg.takes || []).map((t) => t.id).filter(Boolean));
    if (base && !used.has(base)) return base;
    const root = base || takeId(seg.lap, seg.camera, seg.takes.length);
    let n = 1; while (used.has(`${root}_${n}`)) n++;
    return `${root}_${n}`;
  }
  function assignIds() {
    for (const s of timeline.segments) {
      const used = new Set();
      (s.takes || []).forEach((t, i) => {
        let id = t.id;
        if (!id || used.has(id)) {
          const root = takeId(s.lap, s.camera, i);
          id = root;
          let n = 1; while (used.has(id)) { id = `${root}_${n}`; n++; }
        }
        t.id = id; used.add(id);
      });
    }
  }

  // ---- 尺の解決 ---------------------------------------------------------------
  //   シミュレータと同じ関数（show-scenario.resolveStepDuration）を通す。「素材の終わりまで」は
  //   素材の実尺をブラウザで測って使い（kind='measured'）、測れない時だけ trim 推定 ≈ に落ちる。
  function stepSeconds(s) {
    const cue = s.cueId ? cueById(s.cueId) : null;
    const d = resolveStepDuration(s, cue, durationOf);
    const approx = d.kind === 'estimated' || d.kind === 'unknown';
    const sec = d.durSec > 0 ? d.durSec : TAKE.FALLBACK_STEP_DUR_SEC;
    return { sec: Math.max(0.2, sec), approx, kind: d.kind };
  }
  function takeSeconds(t) {
    let sec = 0, approx = false;
    for (const s of t.steps || []) { const d = stepSeconds(s); sec += d.sec; approx = approx || d.approx; }
    return { sec, approx };
  }
  const stepWidth = (s) => Math.max(stepSeconds(s).sec * PX, STEP_MIN);
  function takeWidth(t) {
    const w = (t.steps || []).reduce((a, s) => a + stepWidth(s), 0);
    return Math.max(w, STEP_MIN);
  }
  const fmtSec = (v) => (Math.abs(v - Math.round(v)) < 0.05 ? String(Math.round(v)) : v.toFixed(1));
  const maxDurOf = (t) => (t.maxDurationSec > 0 ? t.maxDurationSec : TAKE.DEFAULT_MAX_DURATION_SEC);

  // ---- 通過ライン（開始規則「このラインを通過したら」）----------------------------
  const lineById = (id) => lines.find((l) => l.id === id) || null;
  const lineName = (id) => { const l = lineById(id); return l ? (l.label || l.id) : id; };
  /** そのラインの担当カメラ（**著作値**。実機はこれと区間のカメラを照合する / -1 = 未指定）。 */
  function lineOwner(id) { const l = lineById(id); return l ? l.camera : -1; }
  /** そのラインが実際に置かれているゾーンのカメラ（実機のゾーン判定 / -1 = 未割当・判定不能）。 */
  function lineZoneCamera(id) {
    const l = lineById(id);
    if (!l || !zoneBoxes.length) return -1;
    const m = lineMid(l);
    return cameraAtPoint(zoneBoxes, m.x, m.z);
  }
  const isLineTake = (t) => t.at === TAKE.AT_LINE;
  const dirMark = (dir) => (dir === LINE_DIR_FWD ? ' →' : dir === LINE_DIR_BACK ? ' ←' : '');

  function startLabel(t) {
    if (t.at === TAKE.AT_EXIT) return '離脱時';
    if (isLineTake(t)) {
      if (!t.lineId) return 'ライン 未選択';
      const l = lineById(t.lineId);
      return `ライン「${lineName(t.lineId)}」${l ? dirMark(l.dir) : ''}`;
    }
    return `進入 +${fmtSec(t.offsetSec || 0)}s`;
  }

  // ---- 不正値の検出（§6.4 と同じ判定。保存はできるが警告する）------------------
  function stepIssue(s) {
    if (s.source === TAKE.SRC_LIVE && !(Number.isInteger(s.camera) && s.camera >= 0 && s.camera < cameras.length)) {
      return 'ライブのカメラが未選択（実機ではこのカットは飛ばされます）';
    }
    if (s.source === TAKE.SRC_REC
        && !(Number.isInteger(s.camera) && s.camera >= 0 && s.camera < cameras.length && s.recLap > 0)) {
      return '録画のカメラ／周が未指定（実機ではこのカットは飛ばされます）';
    }
    if ((s.source === TAKE.SRC_CLIP || s.source === TAKE.SRC_STILL) && !s.assetUrl) {
      return '素材が未選択（実機ではこのカットは飛ばされます）';
    }
    if (s.cueId && !cueById(s.cueId)) return `重ねる素材 ${s.cueId} が見つかりません`;
    return null;
  }
  // ライントリガーの不整合（実機で「黙って出ない」状態を作らないため、ここで必ず言う）。
  function lineIssue(t, camera) {
    if (!isLineTake(t)) return null;
    if (!t.lineId) return 'ラインが未選択です（フロアマップの 📏 通過ラインで引いて選んでください）';
    const l = lineById(t.lineId);
    if (!l) return `ライン「${t.lineId}」がフロアマップにありません（発火しません）`;
    if (l.camera >= 0 && l.camera !== camera) {
      return `ライン「${lineName(t.lineId)}」は ${camLabel(l.camera)} 担当です`
        + `（この演出は ${camLabel(camera)} の区間なので出ません。${camLabel(l.camera)} の区間へ移すか、別のラインを選んでください）`;
    }
    if (l.camera < 0) {
      return `ライン「${lineName(t.lineId)}」の担当カメラが未指定です（フロアマップで担当を決めてください）`;
    }
    const zone = lineZoneCamera(t.lineId);
    if (zone >= 0 && zone !== l.camera) {
      return `ライン「${lineName(t.lineId)}」は ${camLabel(l.camera)} 担当ですが、`
        + `線が置かれているのは ${camLabel(zone)} のゾーンです（体験者がそこを通る時の区間と食い違います）`;
    }
    return null;
  }

  function takeIssue(t, camera) {
    if (!t.steps || !t.steps.length) return 'カットが 1 枚もありません（発火しません）';
    const lineBad = lineIssue(t, camera);
    if (lineBad) return lineBad;
    const bad = t.steps.map(stepIssue).filter(Boolean);
    if (bad.length) return bad[0];
    const total = takeSeconds(t);
    if (total.sec > maxDurOf(t)) return `尺の合計 ${fmtSec(total.sec)}s が最大長 ${maxDurOf(t)}s を超えています（超過分は強制終了されます）`;
    return null;
  }

  // ---- 実測滞在時間（capture-server が heartbeat の dwell[] を集計）--------------
  //   「この区間に体験者が実際に何秒居たか」。オーサリングした開始位置が現実的かを判定する材料。
  function dwellStats() { return (deps.getDwellStats && deps.getDwellStats()) || { items: {} }; }
  function dwellFor(lap, camera) {
    const items = dwellStats().items || {};
    const e = items[`${lap}:${camera}`];
    return e && e.n > 0 ? e : null;
  }
  function dwellLabel(lap, camera) {
    const e = dwellFor(lap, camera);
    if (!e) return { text: '滞在は体験者しだい', on: false };
    const n = e.n === 1 ? '1 回' : `${e.n} 回`;
    return { text: `実測 平均 ${fmtSec(e.meanSec)}s（最短 ${fmtSec(e.minSec)}s / ${n}）`, on: true };
  }

  /**
   * 「その演出が体験者の歩速で出ないかもしれない」危険（計画 §8 論点 5）。
   * 実測が無ければ何も言わない（推測で警告しない）。
   */
  function takeTimingRisk(t, lap, camera) {
    if (!t || t.at === TAKE.AT_EXIT) return null;         // 離脱時は必ず出る
    const e = dwellFor(lap, camera);
    if (!e) return null;
    const off = Math.max(0, t.offsetSec || 0);
    if (off <= 0) return null;
    const missed = t.ifMissed === TAKE.MISSED_SKIP
      ? '取り逃したら出ません（設定: 出さない）'
      : '取り逃したら離脱の瞬間に出ます（設定: 離脱時に出す）';
    if (off >= e.meanSec) return `⏱ 開始 +${fmtSec(off)}s は実測の平均滞在 ${fmtSec(e.meanSec)}s を超えています。${missed}`;
    if (off >= e.minSec) return `⏱ 開始 +${fmtSec(off)}s は最短滞在 ${fmtSec(e.minSec)}s を超える周があります。${missed}`;
    return null;
  }

  // ---- BGM 表示ヘルパ（v2 と同じ carry-forward 解決を再利用）--------------------
  function bgmTracks() { return (deps.getBgmTracks && deps.getBgmTracks()) || []; }
  function rootBgmTrackId() {
    const b = deps.getRootBgm && deps.getRootBgm();
    if (!b || !b.action || b.action === 'continue') return DEFAULT_TRACK_ID;
    if (b.action === 'stop') return '';
    return b.trackId || DEFAULT_TRACK_ID;
  }
  function trackName(id) {
    if (id === DEFAULT_TRACK_ID) return '既定（APK 同梱）';
    const t = bgmTracks().find((x) => x.id === id);
    return t ? (t.name || t.id) : `⚠ 未定義（${id}）`;
  }

  // ==========================================================================
  //  リボン描画
  // ==========================================================================
  function render() {
    const rs = rows();
    const schemaEl = q('.rb-schema');
    if (schemaEl) {
      schemaEl.textContent = v2Source ? '古い形式 → 保存で更新' : '演出・カット';
      schemaEl.title = v2Source
        ? 'この show.json は古い形式（cue 割当・インサートショット）。表示は変換したもので、💾 保存で新しい形式になる'
        : 'この show.json は演出・カット形式（schema 3）';
    }
    const msgs = [];
    if (v2Source) {
      msgs.push('この show.json は古い形式（cue 割当・インサートショット）です。演出・カットへ変換して表示しています — '
        + '💾 保存すると新しい形式で書き出されます。実機は読み込み時に同じ変換をするので、'
        + '保存しなくても動きは同じです。');
    }
    if (convertNote) msgs.push(convertNote);
    if (!orderIsExplicit && cameras.length > 0) msgs.push('コース順が未設定です。フロアマップで周回コースを設定してください（今は 0..N-1 の順で表示中）。');
    if (!cameras.length) msgs.push('カメラが 1 台も設定されていません。');
    noteEl.textContent = msgs.join(' / ');
    noteEl.className = 'rb-note' + (msgs.length ? ' on' : '');

    const bgmLane = resolveBgmLane(timeline.segments, rs, lapCount, rootBgmTrackId());
    track.innerHTML = '';
    for (let lap = 1; lap <= lapCount; lap++) {
      const lapEl = document.createElement('div');
      lapEl.className = 'rb-lap';
      const head = document.createElement('div');
      head.className = 'rb-lap-head';
      head.textContent = `${lap}周目`;
      lapEl.appendChild(head);

      const rib = document.createElement('div');
      rib.className = 'rb-ribbon';
      rs.forEach((ci) => {
        rib.appendChild(renderSeg(lap, ci, bgmLane.get(`${lap}:${ci}`)));
        const seg = segAt(lap, ci);
        for (const t of (seg && seg.takes) || []) {
          if (t.at === TAKE.AT_EXIT) rib.appendChild(renderTake(t, lap, ci, null));
        }
      });
      lapEl.appendChild(rib);
      track.appendChild(lapEl);
    }
  }

  function renderSeg(lap, ci, bgmSt) {
    const seg = segAt(lap, ci);
    const enters = ((seg && seg.takes) || []).filter((t) => t.at !== TAKE.AT_EXIT);
    const on = sel && sel.kind === 'seg' && sel.lap === lap && sel.camera === ci;

    const el = document.createElement('div');
    el.className = 'rb-seg' + (on ? ' sel' : '');
    el.style.setProperty('--c', camColor(ci));
    el.dataset.lap = String(lap); el.dataset.cam = String(ci);

    let badges = '';
    if (seg && seg.hasPost) badges += '<span class="rb-badge" title="この区間だけ画像加工を上書き">🎨</span>';
    if (seg && seg.hasBgm) badges += '<span class="rb-badge" title="この区間で BGM 指示あり">🎵</span>';
    const camId = cameras[ci] ? cameras[ci].id : `#${ci}`;
    el.innerHTML = `
      <div class="rb-seg-head">
        <span class="rb-seg-name"><i></i>${escapeHtml(camId)}</span>${badges}
        <span class="spacer"></span>
        <button class="rb-seg-add" title="この区間に演出を足す">＋ 演出</button>
      </div>
      <div class="rb-seg-lane"></div>
      <div class="rb-seg-foot"><span class="rb-seg-dur"></span><span class="rb-seg-bgm"></span></div>`;

    // 実測の平均滞在（Unity heartbeat 由来）。無ければ従来どおり「滞在は体験者しだい」。
    const dl = dwellLabel(lap, ci);
    const durEl = el.querySelector('.rb-seg-dur');
    durEl.textContent = dl.text;
    durEl.className = 'rb-seg-dur' + (dl.on ? ' meas' : '');
    if (dl.on) durEl.title = '実機で歩いた時の実測（体験ごとに更新）。演出の開始位置がこの時間に収まるかを見る';

    // 進入の演出はブロックの中に、進入からの秒数に比例した位置で置く（重なる時は段を分ける）。
    const lane = el.querySelector('.rb-seg-lane');
    const timed = enters.filter((t) => !isLineTake(t));
    const sorted = timed.slice().sort((a, b) => (a.offsetSec || 0) - (b.offsetSec || 0));
    const laneEnds = [];
    let maxRight = 0;
    for (const t of sorted) {
      const left = LANE_PAD + Math.max(0, t.offsetSec || 0) * PX;
      const w = takeWidth(t);
      let row = laneEnds.findIndex((end) => left >= end + 6);
      if (row < 0) { row = laneEnds.length; laneEnds.push(0); }
      laneEnds[row] = left + w;
      const te = renderTake(t, lap, ci, { left, top: row * (LANE_H + LANE_GAP) });
      lane.appendChild(te);
      maxRight = Math.max(maxRight, left + w);
    }
    // ライントリガーの演出は**時間軸に乗らない**（いつ通るかは体験者しだい）。
    // 進入 +t の段の下に、左端揃えで 1 本ずつ置く（位置に意味を持たせない・§4 と同じ理由）。
    let rows = laneEnds.length;
    for (const t of enters.filter(isLineTake)) {
      const te = renderTake(t, lap, ci, { left: LANE_PAD, top: rows * (LANE_H + LANE_GAP) });
      lane.appendChild(te);
      maxRight = Math.max(maxRight, LANE_PAD + takeWidth(t));
      rows++;
    }
    lane.style.height = `${Math.max(1, rows) * (LANE_H + LANE_GAP)}px`;
    el.style.minWidth = `${Math.max(SEG_MIN_W, maxRight + 22)}px`;

    const bgmEl = el.querySelector('.rb-seg-bgm');
    if (bgmSt) {
      const mark = bgmSt.change === 'start' ? '▶' : bgmSt.change === 'retune' ? '≡' : bgmSt.change === 'stop' ? '■' : '🎵';
      const label = bgmSt.trackId ? trackName(bgmSt.trackId) : (bgmSt.change === 'stop' ? '停止' : '無音');
      bgmEl.textContent = `${mark} ${label}`;
      bgmEl.className = 'rb-seg-bgm' + (bgmSt.change ? ' chg' : '');
    }

    el.querySelector('.rb-seg-add').onclick = (e) => { e.stopPropagation(); addTake(lap, ci, TAKE.AT_ENTER); };
    el.onclick = (e) => {
      if (suppressClick) return;
      if (e.target.closest('.rb-take')) return;
      sel = { kind: 'seg', lap, camera: ci }; closeCueEditor(); render(); renderInspector();
    };
    return el;
  }

  function renderTake(t, lap, ci, pos) {
    const el = document.createElement('div');
    const on = sel && sel.kind === 'take' && sel.lap === lap && sel.camera === ci && sel.id === t.id;
    const issue = takeIssue(t, ci);
    const risk = takeTimingRisk(t, lap, ci);
    el.className = 'rb-take' + (t.at === TAKE.AT_EXIT ? ' exit' : '') + (isLineTake(t) ? ' line' : '')
      + (on ? ' sel' : '') + (issue ? ' bad' : '') + (risk ? ' risk' : '');
    el.dataset.lap = String(lap); el.dataset.cam = String(ci); el.dataset.id = t.id;
    el.style.width = `${takeWidth(t)}px`;
    if (pos) { el.style.position = 'absolute'; el.style.left = `${pos.left}px`; el.style.top = `${pos.top}px`; }

    const total = takeSeconds(t);
    const steps = (t.steps || []).map((s) => {
      const d = stepSeconds(s);
      const bad = stepIssue(s);
      return `<div class="rb-step" style="flex:${d.sec} 1 0;--sc:${stepColor(s)}" title="${escapeHtml(stepTitle(s))}">
        <span class="rb-step-src"><i></i>${escapeHtml(stepLabel(s))}</span>
        ${s.cueId ? `<span class="rb-step-ov">+ ${escapeHtml(cueName(s.cueId))}</span>` : ''}
        <span class="rb-step-sec">${bad ? '⚠ ' : ''}${d.approx ? '≈' : ''}${fmtSec(d.sec)}s</span>
      </div>`;
    }).join('') || '<div class="rb-step rb-step-empty">カットなし</div>';

    // この演出のあいだ鳴る曲（区間から変わる時だけバッジを出す。変わらないなら黙っている）。
    const bgmChip = t.hasBgm && t.bgm && t.bgm.action !== 'continue'
      ? `<span class="rb-take-bgm" title="${escapeHtml(takeBgmLabel(t, lap, ci))}">${t.bgm.action === 'stop'
        ? '🔇' : `♪ ${escapeHtml(trackName(t.bgm.trackId))}`}</span>`
      : '';

    const headTitle = isLineTake(t)
      ? 'この演出は「体験者がこのラインを通過したら」始まる（時間軸の上に位置を持たない）。'
        + 'ドラッグで別の区間へ移せる（開始規則はラインのまま）。線の場所はフロアマップの 📏 通過ラインで動かす。'
      : `ドラッグ（マウス / 指）で開始位置を変える（区間の中 = 進入から t 秒 / 右境界に吸着 = 離脱時）。
選んで ← → で 0.5s ずつ（Shift で 2s）、Home = 進入直後、End = 離脱時。`;
    el.innerHTML = `
      <div class="rb-take-head" tabindex="0" role="button" title="${escapeHtml(headTitle)}">
        <span class="rb-take-name">${isLineTake(t) ? '📏' : '🎬'} ${escapeHtml(t.name || '演出')}</span>
        <span class="rb-take-meta">
          ${bgmChip}
          <span class="rb-take-start">${risk ? '⏱ ' : ''}${escapeHtml(startLabel(t))}</span>
          <span class="rb-take-dur">${total.approx ? '≈' : ''}${fmtSec(total.sec)}s</span>
        </span>
      </div>
      <div class="rb-steps">${steps}</div>`;
    if (issue) el.title = `⚠ ${issue}`;
    else if (risk) el.title = risk;

    el.onclick = (e) => {
      e.stopPropagation();
      if (suppressClick) return;
      sel = { kind: 'take', lap, camera: ci, id: t.id }; closeCueEditor(); render(); renderInspector();
    };
    const head = el.querySelector('.rb-take-head');
    head.addEventListener('pointerdown', (e) => beginDrag(e, t, lap, ci, el));
    // キーボード操作（タッチ端末・マウスが使えない場面の代替。数値入力はインスペクタにもある）。
    head.addEventListener('keydown', (e) => onTakeKey(e, t, lap, ci));
    if (on && refocusTakeHead) { refocusTakeHead = false; setTimeout(() => head.focus(), 0); }
    return el;
  }

  // 演出ヘッダのキー操作。スナップは 2 種だけという規約（§4）を保ったまま、
  // 「区間内の t 秒」を矢印で動かし、End で「離脱時」へ、Home で進入直後へ落とす。
  let refocusTakeHead = false;
  function onTakeKey(e, t, lap, ci) {
    const step = e.shiftKey ? 2 : 0.5;
    let handled = true;
    // ライントリガーは時間軸に乗らないので矢印・Home/End で動かさない
    //（黙って「進入 +t 秒」へ化けるとラインの指定が消える）。開始規則の変更はインスペクタで。
    if (isLineTake(t) && e.key !== 'Enter' && e.key !== ' ') return;
    if (e.key === 'ArrowRight') {
      t.at = TAKE.AT_ENTER;
      t.offsetSec = Math.round((Math.max(0, t.offsetSec || 0) + step) * 2) / 2;
    } else if (e.key === 'ArrowLeft') {
      if (t.at === TAKE.AT_EXIT) { t.at = TAKE.AT_ENTER; t.offsetSec = 0; }
      else t.offsetSec = Math.max(0, Math.round((Math.max(0, t.offsetSec || 0) - step) * 2) / 2);
    } else if (e.key === 'Home') {
      t.at = TAKE.AT_ENTER; t.offsetSec = 0;
    } else if (e.key === 'End') {
      t.at = TAKE.AT_EXIT; t.offsetSec = 0;
    } else if (e.key === 'Enter' || e.key === ' ') {
      sel = { kind: 'take', lap, camera: ci, id: t.id };
      closeCueEditor(); render(); renderInspector();
      return;
    } else handled = false;
    if (!handled) return;
    e.preventDefault();
    sel = { kind: 'take', lap, camera: ci, id: t.id };
    refocusTakeHead = true;          // 再描画で要素が作り直されるのでフォーカスを戻す
    markDirty(); render(); renderInspector();
  }

  function stepColor(s) {
    if (s.source === TAKE.SRC_LIVE) return camColor(Number.isInteger(s.camera) && s.camera >= 0 ? s.camera : 0);
    if (s.source === TAKE.SRC_CLIP) return 'var(--accent-color)';
    if (s.source === TAKE.SRC_STILL) return '#c9a6ff';
    return 'rgba(255,250,240,.45)';
  }
  function baseName(u) {
    if (!u) return '';
    const last = String(u).split('/').pop();
    try { return decodeURIComponent(last); } catch { return last; }
  }
  // リボン上のカットは幅が狭い（最短 110px）。長い語で省略記号だらけになるのを避け、
  // 種別は左の色チップで、詳細は title（ツールチップ）で補う。
  function stepLabel(s) {
    if (s.source === TAKE.SRC_LIVE) {
      const id = cameras[s.camera] ? cameras[s.camera].id : '?';
      return `ライブ ${id}`;
    }
    if (s.source === TAKE.SRC_INHERIT) return 'そのまま';
    const kind = s.source === TAKE.SRC_STILL ? '静止画' : '映像';
    return `${kind} ${baseName(s.assetUrl) || '（未選択）'}`;
  }
  const DUR_KIND_LABEL = {
    exact: '尺 秒指定',
    measured: '尺 素材の実尺（実測）',
    estimated: '尺 推定（素材の trim から。実機は素材の実尺で終わる）',
    unknown: '尺 不明（実機は素材の終わりまで。卓では最大長まで走る）',
    fallback: '尺 素材なし → 既定 4s（実機も同じ）',
  };
  function stepTitle(s) {
    const bad = stepIssue(s);
    const tr = s.transition === TAKE.TRANS_CUT ? 'カット' : s.transition === TAKE.TRANS_FADE ? 'フェード' : '暗転';
    const d = stepSeconds(s);
    return `${stepLabel(s)}${s.cueId ? ` + 素材 ${cueName(s.cueId)}` : ''} / 遷移 ${tr}`
      + ` / ${DUR_KIND_LABEL[d.kind] || ''}${bad ? ` / ⚠ ${bad}` : ''}`;
  }

  // ==========================================================================
  //  ドラッグ（スナップ 2 種のみ）
  // ==========================================================================
  let drag = null;
  let suppressClick = false;
  function beginDrag(e, t, lap, ci, el) {
    if (e.button !== 0) return;
    // タッチ / ペンは暗黙のポインタキャプチャが掛かる。掴んだままだと下の区間を
    // elementFromPoint で拾えない（＝ドロップ先が決まらない）ので明示的に外し、
    // ページのスクロール・ズームジェスチャも止める（CSS の touch-action: none と対）。
    if (e.pointerType && e.pointerType !== 'mouse') {
      const tgt = e.currentTarget || e.target;
      try { if (tgt.hasPointerCapture && tgt.hasPointerCapture(e.pointerId)) tgt.releasePointerCapture(e.pointerId); }
      catch { /* 未対応ブラウザは既定挙動のまま */ }
      e.preventDefault();
    }
    drag = { t, lap, ci, el, x0: e.clientX, y0: e.clientY, moved: false, drop: null };
    window.addEventListener('pointermove', onDragMove);
    window.addEventListener('pointerup', onDragEnd);
    window.addEventListener('pointercancel', onDragEnd);   // タッチが割り込みで消えた時
  }
  function onDragMove(e) {
    if (!drag) return;
    if (!drag.moved) {
      if (Math.abs(e.clientX - drag.x0) < 4 && Math.abs(e.clientY - drag.y0) < 4) return;
      drag.moved = true;
      drag.el.classList.add('dragging');   // pointer-events:none（下の区間を拾うため）
      dropHint.style.display = '';
    }
    const info = dropAt(e.clientX, e.clientY);
    drag.drop = info;
    container.querySelectorAll('.rb-seg.drop, .rb-seg.drop-exit').forEach((s) => s.classList.remove('drop', 'drop-exit'));
    if (info) {
      // ライントリガーの演出は区間だけ移す（開始規則はラインのまま。落とした x は意味を持たない）。
      const keepLine = isLineTake(drag.t);
      info.el.classList.add(!keepLine && info.at === TAKE.AT_EXIT ? 'drop-exit' : 'drop');
      dropHint.textContent = keepLine
        ? `${camLabel(info.cam)} ${info.lap}周目 — ${startLabel(drag.t)}（区間を移動）`
        : info.at === TAKE.AT_EXIT
          ? `${camLabel(info.cam)} ${info.lap}周目 — 離脱時`
          : `${camLabel(info.cam)} ${info.lap}周目 — 進入 +${fmtSec(info.offsetSec)}s`;
      dropHint.className = 'rb-drop-hint' + (!keepLine && info.at === TAKE.AT_EXIT ? ' exit' : '');
    } else {
      dropHint.textContent = '区間の上に置いてください';
      dropHint.className = 'rb-drop-hint bad';
    }
    dropHint.style.left = `${e.clientX + 14}px`;
    dropHint.style.top = `${e.clientY + 16}px`;
  }
  function dropAt(cx, cy) {
    const hit = document.elementFromPoint(cx, cy);
    const segEl = hit && hit.closest ? hit.closest('.rb-seg') : null;
    if (!segEl || !container.contains(segEl)) return null;
    const lap = parseInt(segEl.dataset.lap, 10);
    const cam = parseInt(segEl.dataset.cam, 10);
    const r = segEl.getBoundingClientRect();
    if (cx > r.right - MAGNET) return { lap, cam, at: TAKE.AT_EXIT, el: segEl };
    const sec = Math.max(0, Math.round(((cx - r.left - LANE_PAD) / PX) * 2) / 2);
    return { lap, cam, at: TAKE.AT_ENTER, offsetSec: sec, el: segEl };
  }
  function onDragEnd() {
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', onDragEnd);
    window.removeEventListener('pointercancel', onDragEnd);
    const d = drag; drag = null;
    dropHint.style.display = 'none';
    container.querySelectorAll('.rb-seg.drop, .rb-seg.drop-exit').forEach((s) => s.classList.remove('drop', 'drop-exit'));
    if (!d) return;
    d.el.classList.remove('dragging');
    if (!d.moved) return;                     // 動いていなければ通常のクリック（選択）
    suppressClick = true;                     // ドラッグ直後の click で選択が飛ぶのを防ぐ
    setTimeout(() => { suppressClick = false; }, 0);
    if (!d.drop) { render(); return; }
    moveTake(d.t, d.lap, d.ci, d.drop);
  }
  function moveTake(t, fromLap, fromCam, drop) {
    const from = segAt(fromLap, fromCam);
    const to = ensureSeg(drop.lap, drop.cam);
    if (from !== to) {
      if (from) from.takes = (from.takes || []).filter((x) => x !== t);
      // 区間をまたいだら id を移動先のキーで振り直す（show.json を人が読んだとき迷わないように）
      t.id = uniqueTakeId(to, takeId(to.lap, to.camera, to.takes.length));
      to.takes.push(t);
      pruneSeg(from);
    }
    if (isLineTake(t)) { t.offsetSec = 0; }   // ライントリガーは開始規則を保つ（区間だけ移す）
    else if (drop.at === TAKE.AT_EXIT) { t.at = TAKE.AT_EXIT; t.offsetSec = 0; }
    else { t.at = TAKE.AT_ENTER; t.offsetSec = drop.offsetSec; }
    sel = { kind: 'take', lap: to.lap, camera: to.camera, id: t.id };
    markDirty(); render(); renderInspector();
  }

  // ==========================================================================
  //  演出の追加・削除
  // ==========================================================================
  function addTake(lap, camera, at) {
    const seg = ensureSeg(lap, camera);
    // ライントリガーは「この区間のカメラが担当するライン」だけを既定にする（取り違えを構造的に防ぐ）。
    const line = at === TAKE.AT_LINE ? (lines.find((l) => l.camera === camera) || null) : null;
    const t = newTake(uniqueTakeId(seg, takeId(lap, camera, seg.takes.length)), {
      at, offsetSec: 0,
      lineId: line ? line.id : '',
      // ライン指定の演出は「通らなかったら出さない」が既定（時刻トリガーの fireOnExit と違う）。
      ifMissed: at === TAKE.AT_LINE ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT,
      name: at === TAKE.AT_EXIT ? '離脱の演出' : at === TAKE.AT_LINE ? 'ラインの演出' : '演出',
      steps: [newStep({ source: TAKE.SRC_LIVE, camera, durKind: TAKE.DUR_SEC, durSec: 4 })],
    });
    seg.takes.push(t);
    sel = { kind: 'take', lap, camera, id: t.id };
    markDirty(); render(); renderInspector();
  }
  function deleteTake(lap, camera, id) {
    const seg = segAt(lap, camera);
    if (!seg) return;
    seg.takes = (seg.takes || []).filter((t) => t.id !== id);
    pruneSeg(seg);
    sel = { kind: 'seg', lap, camera };
    closeCueEditor(); markDirty(); render(); renderInspector();
  }

  // ==========================================================================
  //  インスペクタ
  // ==========================================================================
  let lastInspKey = '';
  function renderInspector() {
    editorOpen = false;
    // 選択が変わったら BGM 試聴を止める（同じ区間を描き直すだけなら鳴らし続ける）。
    const key = sel ? `${sel.kind}:${sel.lap}:${sel.camera}:${sel.id || ''}` : '';
    if (key !== lastInspKey) stopBgmPreview();
    lastInspKey = key;
    if (!sel) { inspectorEl.innerHTML = ''; return; }
    if (sel.kind === 'take') { const t = selTake(); if (t) return renderTakeInspector(t); sel = { kind: 'seg', lap: sel.lap, camera: sel.camera }; }
    return renderSegInspector();
  }

  // ---- 区間インスペクタ -------------------------------------------------------
  function renderSegInspector() {
    const seg = segAt(sel.lap, sel.camera);
    const dw = dwellFor(sel.lap, sel.camera);
    inspectorEl.innerHTML = `
      <div class="rb-insp-head">区間 — ${escapeHtml(camLabel(sel.camera))} / ${sel.lap}周目
        <span class="rb-insp-sub">滞在時間は体験者が決めます（伸縮）</span>
        <span class="spacer"></span>
        <button class="rb-insp-close">閉じる</button>
      </div>
      <div class="rb-insp-meas" style="display:${dw ? '' : 'none'}">この区間の実測滞在: 平均 ${dw ? fmtSec(dw.meanSec) : '—'}s ／ 最短 ${dw ? fmtSec(dw.minSec) : '—'}s ／ 最長 ${dw ? fmtSec(dw.maxSec) : '—'}s（${dw ? dw.n : 0} 回）— 演出の開始位置はこの中に収める</div>
      <div class="rb-insp-sec">
        <div class="rb-insp-label">演出</div>
        <div class="rb-take-list"></div>
        <div class="row-btns">
          <button class="rb-add-enter accent">＋ 進入で始まる演出</button>
          <button class="rb-add-exit">＋ 離脱時の演出</button>
          <button class="rb-add-line" title="体験者がフロアマップで引いたラインを通過したら始まる演出">📏 ＋ ラインで始まる演出</button>
        </div>
      </div>
      <div class="rb-insp-sec">
        <label class="rb-insp-toggle chk"><input class="rb-post-on" type="checkbox"> 🎨 この区間で画像加工を上書き（区間 &gt; カメラ &gt; 全体）</label>
        <div class="rb-post-fx fx-rows" style="display:none"></div>
      </div>
      <div class="rb-insp-sec">
        <label class="rb-insp-toggle chk"><input class="rb-bgm-on" type="checkbox"> 🎵 この区間で BGM を切り替える / 止める</label>
        <div class="rb-bgm-body" style="display:none"></div>
      </div>`;

    inspectorEl.querySelector('.rb-insp-close').onclick = () => { sel = null; render(); renderInspector(); };
    const list = inspectorEl.querySelector('.rb-take-list');
    const takes = (seg && seg.takes) || [];
    if (!takes.length) list.innerHTML = '<div class="rb-empty">（演出なし＝そのままライブが映る）</div>';
    takes.forEach((t) => {
      const b = document.createElement('button');
      b.className = 'rb-take-pill';
      b.innerHTML = `${isLineTake(t) ? '📏' : '🎬'} ${escapeHtml(t.name || '演出')} <span>${escapeHtml(startLabel(t))} / ${fmtSec(takeSeconds(t).sec)}s</span>`;
      b.onclick = () => { sel = { kind: 'take', lap: sel.lap, camera: sel.camera, id: t.id }; render(); renderInspector(); };
      list.appendChild(b);
    });
    inspectorEl.querySelector('.rb-add-enter').onclick = () => addTake(sel.lap, sel.camera, TAKE.AT_ENTER);
    inspectorEl.querySelector('.rb-add-exit').onclick = () => addTake(sel.lap, sel.camera, TAKE.AT_EXIT);
    const addLineBtn = inspectorEl.querySelector('.rb-add-line');
    const ownLines = lines.filter((l) => l.camera === sel.camera);
    if (!ownLines.length) {
      // この区間のカメラが担当するラインが無い状態で作らせない（作っても発火しない演出が増えるだけ）。
      addLineBtn.disabled = true;
      addLineBtn.title = lines.length
        ? `フロアマップの 📏 通過ラインで ${camLabel(sel.camera)} 担当のラインを引いてから使えます`
        : 'フロアマップの 📏 通過ラインでラインを引いてから使えます';
    } else {
      addLineBtn.onclick = () => addTake(sel.lap, sel.camera, TAKE.AT_LINE);
    }

    renderSegPost();
    renderSegBgm();
  }

  let segPostFx = null;
  function renderSegPost() {
    const onChk = inspectorEl.querySelector('.rb-post-on');
    const fxBox = inspectorEl.querySelector('.rb-post-fx');
    const seg = segAt(sel.lap, sel.camera);
    const has = !!(seg && seg.hasPost);
    onChk.checked = has;
    fxBox.style.display = has ? '' : 'none';
    segPostFx = buildFxSliders(fxBox, (seg && seg.post) || globalPost(), () => {
      const s = ensureSeg(sel.lap, sel.camera);
      s.hasPost = true; s.post = segPostFx.read();
      onChk.checked = true; markDirty(); render();
    });
    onChk.onchange = () => {
      if (onChk.checked) {
        const s = ensureSeg(sel.lap, sel.camera);
        s.hasPost = true; if (!s.post) s.post = { ...globalPost() };
        segPostFx.set(s.post); fxBox.style.display = '';
      } else {
        const s2 = segAt(sel.lap, sel.camera);
        if (s2) { s2.hasPost = false; pruneSeg(s2); }
        fxBox.style.display = 'none';
      }
      markDirty(); render();
    };
  }

  // BGM は「区間で切り替わるレーン」＋「演出が一時的に占有する層」の 2 段。編集面は同じ部品を使う
  //   （host = チェックボックスと本体を持つ DOM、target = 書き込む対象 = 区間 or 演出）。
  let bgmPreview = null;   // <audio>（試聴。区間を離れる / インスペクタを閉じたら止める）
  function stopBgmPreview() { if (bgmPreview) { bgmPreview.pause(); bgmPreview = null; } }

  function renderSegBgm() {
    renderBgmEditor({
      onChk: inspectorEl.querySelector('.rb-bgm-on'),
      body: inspectorEl.querySelector('.rb-bgm-body'),
      get: () => segAt(sel.lap, sel.camera),
      ensure: () => ensureSeg(sel.lap, sel.camera),
      prune: (s) => pruneSeg(s),
      redraw: renderSegBgm,
    });
  }

  // 演出の BGM（区間と同じ部品・書き込む対象だけが違う）。
  function renderTakeBgm(t) {
    updateTakeBgmLabel(t);
    renderBgmEditor({
      onChk: inspectorEl.querySelector('.rb-t-bgm-on'),
      body: inspectorEl.querySelector('.rb-t-bgm-body'),
      get: () => t,
      ensure: () => t,
      prune: () => {},
      redraw: () => renderTakeBgm(t),
      // 値を触るたびに「この演出のあいだ何が鳴るか」を出し直す（インスペクタは作り直さない）。
      onCommit: () => updateTakeBgmLabel(t),
    });
  }

  function updateTakeBgmLabel(t) {
    const el = inspectorEl.querySelector('.rb-bgm-now');
    if (el && sel) el.textContent = takeBgmLabel(t, sel.lap, sel.camera);
  }

  function renderBgmEditor(ctx) {
    const { onChk, body } = ctx;
    if (!onChk || !body) return;
    const target = ctx.get();
    const has = !!(target && target.hasBgm);
    onChk.checked = has;
    body.style.display = has ? '' : 'none';

    const b = (target && target.bgm) || defaultBgm();
    const tracks = bgmTracks();
    let trackOpts = '<option value="">（トラックを選択）</option>';
    for (const t of tracks) trackOpts += `<option value="${escapeHtml(t.id)}"${b.trackId === t.id ? ' selected' : ''}>${escapeHtml(t.name || t.id)}</option>`;
    trackOpts += `<option value="${DEFAULT_TRACK_ID}"${b.trackId === DEFAULT_TRACK_ID ? ' selected' : ''}>既定 BGM（APK 同梱・元の曲へ戻す）</option>`;
    if (b.trackId && b.trackId !== DEFAULT_TRACK_ID && !tracks.some((t) => t.id === b.trackId)) {
      trackOpts += `<option value="${escapeHtml(b.trackId)}" selected>${escapeHtml(b.trackId)}（未定義）</option>`;
    }
    const playing = b.action === 'play';
    body.innerHTML = `
      <div class="rb-grid">
        <label>動作<select class="rb-bgm-action">
          <option value="play"${playing ? ' selected' : ''}>▶ この曲に切り替える</option>
          <option value="stop"${b.action === 'stop' ? ' selected' : ''}>■ BGM を止める（フェードアウト）</option>
          <option value="continue"${b.action === 'continue' ? ' selected' : ''}>― そのまま（指示なし）</option>
        </select></label>
        <label>トラック<select class="rb-bgm-track">${trackOpts}</select></label>
        <label>音量<input class="rb-bgm-vol" type="number" min="-1" max="1" step="0.05" value="${b.volume}"><span class="rb-hint2">-1=既定</span></label>
        <label>IN<input class="rb-bgm-fin" type="number" min="0" step="0.1" value="${b.fadeInSec}">s</label>
        <label>OUT<input class="rb-bgm-fout" type="number" min="0" step="0.1" value="${b.fadeOutSec}">s</label>
      </div>
      <div class="rb-bgm-loop" style="display:${playing ? '' : 'none'}">
        <div class="rb-grid">
          <label>開始<input class="rb-bgm-start" type="number" min="0" step="0.1" value="${b.startSec}">s</label>
          <label>ループ in<input class="rb-bgm-ls" type="number" min="-1" step="0.1" value="${b.loopStartSec}">s</label>
          <label>ループ out<input class="rb-bgm-le" type="number" min="-1" step="0.1" value="${b.loopEndSec}">s</label>
          <label class="chk"><input class="rb-bgm-loop-on" type="checkbox" ${b.loop !== false ? 'checked' : ''}>ループする</label>
          <label class="chk"><input class="rb-bgm-restart" type="checkbox" ${b.restart ? 'checked' : ''}>同じ曲でも頭出し</label>
        </div>
        <div class="rb-bgm-audition">
          <button class="rb-bgm-play" title="この区間の設定で試聴（ブラウザ内・実機には影響しません）">🔊 試聴</button>
          <button class="rb-bgm-stop">■</button>
          <span class="rb-bgm-time">0.0s</span>
          <button class="rb-bgm-mark-in" title="再生中の位置をループ in にする">ここを in</button>
          <button class="rb-bgm-mark-out" title="再生中の位置をループ out にする">ここを out</button>
        </div>
      </div>`;

    const q2 = (s) => body.querySelector(s);
    const commit = () => {
      const s = ctx.ensure();
      s.hasBgm = true;
      if (!s.bgm) s.bgm = defaultBgm();
      s.bgm.action = q2('.rb-bgm-action').value;
      s.bgm.trackId = q2('.rb-bgm-track').value;
      s.bgm.volume = numOr(q2('.rb-bgm-vol').value, -1);
      s.bgm.fadeInSec = Math.max(0, numOr(q2('.rb-bgm-fin').value, 1));
      s.bgm.fadeOutSec = Math.max(0, numOr(q2('.rb-bgm-fout').value, 1));
      s.bgm.startSec = Math.max(0, numOr(q2('.rb-bgm-start').value, 0));
      s.bgm.loopStartSec = numOr(q2('.rb-bgm-ls').value, -1);
      s.bgm.loopEndSec = numOr(q2('.rb-bgm-le').value, -1);
      s.bgm.loop = !!q2('.rb-bgm-loop-on').checked;
      s.bgm.restart = !!q2('.rb-bgm-restart').checked;
      markDirty(); render();
      if (ctx.onCommit) ctx.onCommit();
    };
    body.querySelectorAll('input, select').forEach((el) => { el.onchange = commit; });
    q2('.rb-bgm-action').onchange = () => { commit(); ctx.redraw(); };
    q2('.rb-bgm-track').onchange = () => { commit(); ctx.redraw(); };

    // 試聴（ブラウザ内のみ。show.json も実機も触らない）。ループ範囲を耳で決めて
    // 「ここを in / out」でその位置を書き込む（v2 グリッドから移植）。
    const timeEl = q2('.rb-bgm-time');
    if (timeEl) {
      q2('.rb-bgm-play').onclick = () => {
        const t = tracks.find((x) => x.id === q2('.rb-bgm-track').value);
        if (!t || !t.url) { timeEl.textContent = '音源なし'; return; }
        stopBgmPreview();
        bgmPreview = new Audio(t.url);
        bgmPreview.currentTime = Math.max(0, numOr(q2('.rb-bgm-start').value, 0));
        const volRaw = numOr(q2('.rb-bgm-vol').value, -1);
        bgmPreview.volume = Math.min(1, Math.max(0, volRaw < 0 ? (t.volume ?? 1) : volRaw));
        bgmPreview.ontimeupdate = () => {
          if (!bgmPreview) return;
          timeEl.textContent = `${bgmPreview.currentTime.toFixed(1)}s`;
          const leRaw = numOr(q2('.rb-bgm-le').value, -1);
          const le = leRaw >= 0 ? leRaw : (t.loopEndSec > 0 ? t.loopEndSec : 0);
          const lsRaw = numOr(q2('.rb-bgm-ls').value, -1);
          const ls = lsRaw >= 0 ? lsRaw : (t.loopStartSec || 0);
          if (le > 0 && bgmPreview.currentTime >= le) bgmPreview.currentTime = ls;
        };
        bgmPreview.play().catch(() => { timeEl.textContent = '再生不可'; });
      };
      q2('.rb-bgm-stop').onclick = () => { stopBgmPreview(); timeEl.textContent = '0.0s'; };
      q2('.rb-bgm-mark-in').onclick = () => {
        if (!bgmPreview) return;
        q2('.rb-bgm-ls').value = bgmPreview.currentTime.toFixed(1); commit();
      };
      q2('.rb-bgm-mark-out').onclick = () => {
        if (!bgmPreview) return;
        q2('.rb-bgm-le').value = bgmPreview.currentTime.toFixed(1); commit();
      };
    }

    onChk.onchange = () => {
      if (onChk.checked) {
        const s = ctx.ensure();
        s.hasBgm = true;
        if (!s.bgm) s.bgm = defaultBgm();
        if (s.bgm.action === 'continue') s.bgm.action = 'play';
        body.style.display = '';
      } else {
        stopBgmPreview();
        const s2 = ctx.get();
        if (s2) { s2.hasBgm = false; ctx.prune(s2); }
        body.style.display = 'none';
      }
      markDirty(); render(); ctx.redraw();
      if (ctx.onCommit) ctx.onCommit();
    };
  }

  // ---- 演出インスペクタ -------------------------------------------------------
  function renderTakeInspector(t) {
    const total = takeSeconds(t);
    const issue = takeIssue(t, sel.camera);
    const risk = takeTimingRisk(t, sel.lap, sel.camera);
    const dw = dwellFor(sel.lap, sel.camera);
    const onLine = isLineTake(t);
    const enter = t.at === TAKE.AT_ENTER;
    // ラインの選択肢は **この区間のカメラが担当するものだけ**（他カメラのラインは実機で発火しない）。
    // すでに別担当のラインが入っている show.json は、それも出して警告つきで見せる。
    const ownLines = lines.filter((l) => l.camera === sel.camera);
    const lineOpts = ['<option value="">（ラインを選ぶ）</option>']
      .concat(ownLines.map((l) => `<option value="${escapeHtml(l.id)}"${t.lineId === l.id ? ' selected' : ''}>`
        + `${escapeHtml(l.label || l.id)}${escapeHtml(dirMark(l.dir))}</option>`))
      .concat(t.lineId && !ownLines.some((l) => l.id === t.lineId)
        ? [`<option value="${escapeHtml(t.lineId)}" selected>${escapeHtml(lineName(t.lineId))}`
           + `${lineById(t.lineId) ? `（${escapeHtml(camLabel(lineOwner(t.lineId)))} 担当）` : '（見つかりません）'}</option>`]
        : [])
      .join('');
    inspectorEl.innerHTML = `
      <div class="rb-insp-head">演出 — ${escapeHtml(camLabel(sel.camera))} / ${sel.lap}周目
        <span class="rb-insp-sub">${escapeHtml(startLabel(t))} ／ ${total.approx ? '約' : ''}${fmtSec(total.sec)}s</span>
        <span class="spacer"></span>
        <button class="rb-take-del" title="この演出を削除">🗑 削除</button>
        <button class="rb-insp-close">閉じる</button>
      </div>
      <div class="rb-insp-warn" style="display:${issue ? '' : 'none'}">⚠ ${escapeHtml(issue || '')}</div>
      <div class="rb-insp-risk" style="display:${risk ? '' : 'none'}">${escapeHtml(risk || '')}</div>
      <div class="rb-insp-meas" style="display:${dw ? '' : 'none'}">この区間の実測滞在: 平均 ${dw ? fmtSec(dw.meanSec) : '—'}s ／ 最短 ${dw ? fmtSec(dw.minSec) : '—'}s ／ 最長 ${dw ? fmtSec(dw.maxSec) : '—'}s（${dw ? dw.n : 0} 回）</div>
      <div class="rb-insp-sec">
        <div class="rb-grid">
          <label>名前<input class="rb-t-name" type="text" value="${escapeHtml(t.name || '')}" placeholder="演出名（表示だけ）"></label>
          <label>開始<select class="rb-t-at">
            <option value="${TAKE.AT_ENTER}"${enter ? ' selected' : ''}>進入から</option>
            <option value="${TAKE.AT_EXIT}"${t.at === TAKE.AT_EXIT ? ' selected' : ''}>離脱時（この区間を離れる瞬間）</option>
            <option value="${TAKE.AT_LINE}"${onLine ? ' selected' : ''}${lines.length ? '' : ' disabled'}>このラインを通過したら</option>
          </select></label>
          <label class="rb-t-off-l" style="display:${enter ? '' : 'none'}">+<input class="rb-t-off" type="number" min="0" step="0.5" value="${t.offsetSec || 0}">s</label>
          <label class="rb-t-line-l" style="display:${onLine ? '' : 'none'}"
                 title="フロアマップの 📏 通過ラインで引いた線。**この区間のカメラ担当のライン**だけが選べる（他の領域で踏んでも発火しない）">
            ライン<select class="rb-t-line">${lineOpts}</select></label>
          <label class="rb-t-missed-l" style="display:${t.at === TAKE.AT_EXIT ? 'none' : ''}">${onLine ? '通らなかった時' : '取り逃した時'}<select class="rb-t-missed">
            <option value="${TAKE.MISSED_FIRE_ON_EXIT}"${t.ifMissed !== TAKE.MISSED_SKIP ? ' selected' : ''}>離脱時に出す${onLine ? '' : '（既定）'}</option>
            <option value="${TAKE.MISSED_SKIP}"${t.ifMissed === TAKE.MISSED_SKIP ? ' selected' : ''}>出さない${onLine ? '（既定）' : ''}</option>
          </select></label>
          <label>占有<select class="rb-t-policy">
            <option value="${TAKE.POLICY_HOLD}"${t.policy !== TAKE.POLICY_YIELD ? ' selected' : ''}>見せ切る（歩いても演出のまま）</option>
            <option value="${TAKE.POLICY_YIELD}"${t.policy === TAKE.POLICY_YIELD ? ' selected' : ''}>境界を跨いだら打ち切る</option>
          </select></label>
          <label class="chk"><input class="rb-t-once" type="checkbox" ${t.once !== false ? 'checked' : ''}>ラン内 1 回</label>
          <label>最大長<input class="rb-t-max" type="number" min="0" step="1" value="${t.maxDurationSec || 0}"><span class="rb-hint2">0=既定 ${TAKE.DEFAULT_MAX_DURATION_SEC}s</span></label>
        </div>
      </div>
      <div class="rb-insp-sec">
        <div class="rb-insp-label">カット（上から順に再生）</div>
        <div class="rb-steps-edit"></div>
        <div class="row-btns"><button class="rb-step-add accent">＋ カットを追加</button></div>
      </div>
      <div class="rb-insp-sec">
        <div class="rb-bgm-now">${escapeHtml(takeBgmLabel(t, sel.lap, sel.camera))}</div>
        <label class="rb-insp-toggle chk"><input class="rb-t-bgm-on" type="checkbox"> 🎵 この演出のあいだだけ BGM を変える / 止める</label>
        <div class="rb-t-bgm-body" style="display:none"></div>
        <div class="rb-hint2">指定しなければ、その区間で鳴っている曲がそのまま流れます。演出が終わると、
          <b>いる区間の曲</b>へ中断した位置から戻ります（演出中に歩いて次のゾーンへ入っていれば、その区間の曲）。</div>
      </div>
      <div class="rb-cue-editor-host"></div>`;

    const i = (s) => inspectorEl.querySelector(s);
    i('.rb-insp-close').onclick = () => { sel = { kind: 'seg', lap: sel.lap, camera: sel.camera }; closeCueEditor(); render(); renderInspector(); };
    i('.rb-take-del').onclick = () => {
      if (!confirm(`演出「${t.name || '演出'}」を削除しますか？`)) return;
      deleteTake(sel.lap, sel.camera, t.id);
    };
    const commit = () => {
      t.name = i('.rb-t-name').value;
      const atSel = i('.rb-t-at').value;
      t.at = atSel === TAKE.AT_EXIT ? TAKE.AT_EXIT : atSel === TAKE.AT_LINE ? TAKE.AT_LINE : TAKE.AT_ENTER;
      t.offsetSec = t.at === TAKE.AT_ENTER ? Math.max(0, numOr(i('.rb-t-off').value, 0)) : 0;
      t.lineId = t.at === TAKE.AT_LINE ? i('.rb-t-line').value : '';
      t.ifMissed = i('.rb-t-missed').value === TAKE.MISSED_SKIP ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT;
      t.policy = i('.rb-t-policy').value === TAKE.POLICY_YIELD ? TAKE.POLICY_YIELD : TAKE.POLICY_HOLD;
      t.once = !!i('.rb-t-once').checked;
      t.maxDurationSec = Math.max(0, numOr(i('.rb-t-max').value, 0));
      markDirty(); render();
      // 開始位置を動かしたら「実測滞在に対して遅すぎないか」の警告をその場で更新する
      //（インスペクタごと作り直すと入力中のフォーカスが飛ぶので該当行だけ差し替え）。
      const rk = takeTimingRisk(t, sel.lap, sel.camera);
      const rEl = i('.rb-insp-risk');
      if (rEl) { rEl.textContent = rk || ''; rEl.style.display = rk ? '' : 'none'; }
      // ラインの取り違え（別カメラ担当の線を選んだ 等）もその場で出す。
      const isu = takeIssue(t, sel.camera);
      const wEl = i('.rb-insp-warn');
      if (wEl) { wEl.textContent = isu ? `⚠ ${isu}` : ''; wEl.style.display = isu ? '' : 'none'; }
    };
    inspectorEl.querySelectorAll('.rb-t-name, .rb-t-off, .rb-t-policy, .rb-t-once, .rb-t-max, .rb-t-missed, .rb-t-line')
      .forEach((el) => { el.onchange = commit; });
    i('.rb-t-at').onchange = () => {
      commit();
      // ラインに切り替えた直後に「未選択」で放置しない（この区間のカメラ担当のラインを既定で入れる）。
      if (isLineTake(t) && !t.lineId) {
        const pick = lines.find((l) => l.camera === sel.camera);
        if (pick) { t.lineId = pick.id; markDirty(); render(); }
      }
      renderTakeInspector(t);
    };
    i('.rb-step-add').onclick = () => {
      t.steps.push(newStep({ source: TAKE.SRC_LIVE, camera: sel.camera, durKind: TAKE.DUR_SEC, durSec: 4 }));
      markDirty(); render(); renderTakeInspector(t);
    };
    renderStepRows(t);
    renderTakeBgm(t);
  }

  // 「この演出のあいだ何が鳴るか」。指示が無ければ区間のレーンをそのまま言う（＝黙っていても分かる）。
  function takeBgmLabel(t, lap, camera) {
    const lane = resolveBgmLane(timeline.segments, rows(), lapCount, rootBgmTrackId()).get(`${lap}:${camera}`);
    const laneId = lane ? lane.trackId : '';
    const r = resolveTakeBgm(t, laneId);
    if (!r.trackId) {
      return r.change === 'stop' ? '🎵 この演出のあいだ: 無音（この演出で止める）' : '🎵 この演出のあいだ: 無音';
    }
    const name = trackName(r.trackId);
    return r.change === 'start'
      ? `🎵 この演出のあいだ: ${name}（この演出で切り替え）`
      : `🎵 この演出のあいだ: ${name}（この区間の曲のまま）`;
  }

  function renderStepRows(t) {
    const host = inspectorEl.querySelector('.rb-steps-edit');
    if (!host) return;
    host.innerHTML = '';
    if (!t.steps || !t.steps.length) {
      host.innerHTML = '<div class="rb-empty">（カットなし＝この演出は発火しません）</div>';
      return;
    }
    t.steps.forEach((s, idx) => host.appendChild(buildStepRow(t, s, idx)));
  }

  function buildStepRow(t, s, idx) {
    const row = document.createElement('div');
    row.className = 'rb-step-row';
    const d = stepSeconds(s);
    const bad = stepIssue(s);

    const srcOpts = [TAKE.SRC_LIVE, TAKE.SRC_INHERIT, TAKE.SRC_CLIP, TAKE.SRC_STILL, TAKE.SRC_REC]
      .map((k) => `<option value="${k}"${s.source === k ? ' selected' : ''}>${SRC_LABEL[k]}</option>`).join('');
    const camOpts = cameras.map((c, k) => `<option value="${k}"${s.camera === k ? ' selected' : ''}>${escapeHtml(camLabel(k))}</option>`).join('');
    const wantVideo = s.source !== TAKE.SRC_STILL;
    const assets = captureItems().filter((it) => (wantVideo ? it.type === 'video' || isVideoUrl(it.url) : it.type !== 'video'));
    // 実素材（撮影・合成したもの）と動作確認用のダミー（testassets/）を分けて出す。
    // 混ぜると「いま何を選んでいるのか」が分からなくなる。
    const opt = (it) => `<option value="${escapeHtml(it.url)}"${s.assetUrl === it.url ? ' selected' : ''}>${escapeHtml(it.name)}</option>`;
    let assetOpts = '<option value="">（素材を選ぶ）</option>';
    const real = assets.filter((it) => it.kind !== 'test');
    const test = assets.filter((it) => it.kind === 'test');
    for (const it of real) assetOpts += opt(it);
    if (test.length) assetOpts += `<optgroup label="動作確認用（testassets）">${test.map(opt).join('')}</optgroup>`;
    if (s.assetUrl && !assets.some((it) => it.url === s.assetUrl)) assetOpts += `<option value="${escapeHtml(s.assetUrl)}" selected>${escapeHtml(baseName(s.assetUrl))}</option>`;
    // CG 人形は 🎭 パネルで定義したものから選ぶ（id の手打ちは綴り違いで無言に出なくなる）。
    let cgOpts = '<option value="">（出さない）</option>';
    for (const a of actors) cgOpts += `<option value="${escapeHtml(a.id)}"${s.cg === a.id ? ' selected' : ''}>${escapeHtml(a.name || a.id)}</option>`;
    if (s.cg && !actors.some((a) => a.id === s.cg)) cgOpts += `<option value="${escapeHtml(s.cg)}" selected>${escapeHtml(s.cg)}（未定義）</option>`;
    let cueOpts = '<option value="">（重ねない）</option>';
    for (const c of cues) cueOpts += `<option value="${escapeHtml(c.id)}"${s.cueId === c.id ? ' selected' : ''}>${escapeHtml(c.name || c.id)}</option>`;
    if (s.cueId && !cues.some((c) => c.id === s.cueId)) cueOpts += `<option value="${escapeHtml(s.cueId)}" selected>${escapeHtml(s.cueId)}（未定義）</option>`;

    const isLive = s.source === TAKE.SRC_LIVE;
    const isRec = s.source === TAKE.SRC_REC;
    const isAsset = s.source === TAKE.SRC_CLIP || s.source === TAKE.SRC_STILL;
    const bySec = s.durKind !== TAKE.DUR_UNTIL_CLIP_END;
    row.innerHTML = `
      <div class="rb-step-row-head">
        <span class="rb-step-no" style="--sc:${stepColor(s)}"><i></i>カット ${idx + 1}</span>
        <span class="rb-step-sum">${bad ? '⚠ ' : ''}${d.approx ? '≈' : ''}${fmtSec(d.sec)}s</span>
        <span class="spacer"></span>
        <button class="rb-s-up" title="上へ" ${idx === 0 ? 'disabled' : ''}>↑</button>
        <button class="rb-s-down" title="下へ" ${idx === t.steps.length - 1 ? 'disabled' : ''}>↓</button>
        <button class="rb-s-del" title="このカットを削除">🗑</button>
      </div>
      <div class="rb-grid">
        <label>映すもの<select class="rb-s-src">${srcOpts}</select></label>
        <label class="rb-s-cam-l" style="display:${isLive || isRec ? '' : 'none'}">カメラ<select class="rb-s-cam">${camOpts}</select></label>
        <label class="rb-s-reclap-l" style="display:${isRec ? '' : 'none'}" title="この体験のうち何周目に録った映像か（録れていなければ実機ではこのカットは飛ばされます）">録った周<input class="rb-s-reclap" type="number" min="1" step="1" value="${s.recLap > 0 ? s.recLap : 1}">周目</label>
        <label class="rb-s-asset-l" style="display:${isAsset ? '' : 'none'}">素材<select class="rb-s-asset">${assetOpts}</select></label>
        <button class="rb-s-asset-refresh" style="display:${isAsset ? '' : 'none'}" title="いま撮った素材を読み直す（recordings/ captures/ を再走査）">↻</button>
        <button class="rb-s-asset-dir" style="display:${isAsset ? '' : 'none'}" title="撮影フォルダ（recordings/）を開く。ここに録画・合成した素材を置く">📂</button>
        <label class="rb-s-asseturl-l" style="display:${isAsset ? '' : 'none'}">URL<input class="rb-s-asseturl" type="text" value="${escapeHtml(s.assetUrl || '')}" placeholder="/captures/… または sa://assets/…"></label>
        <label>重ねる素材<select class="rb-s-cue">${cueOpts}</select></label>
        <button class="rb-s-cue-edit" title="重ねる素材（マスク・映像・フェード）を編集">✎ 編集</button>
        <button class="rb-s-cue-new" title="このカメラ向けの素材をその場で作る">＋ 新規素材</button>
      </div>
      <div class="rb-grid">
        <label>尺<select class="rb-s-durkind">
          <option value="${TAKE.DUR_SEC}"${bySec ? ' selected' : ''}>秒で指定</option>
          <option value="${TAKE.DUR_UNTIL_CLIP_END}"${bySec ? '' : ' selected'}>素材の終わりまで</option>
        </select></label>
        <label class="rb-s-dur-l" style="display:${bySec ? '' : 'none'}"><input class="rb-s-dur" type="number" min="0.2" step="0.1" value="${s.durSec > 0 ? s.durSec : TAKE.FALLBACK_STEP_DUR_SEC}">s</label>
        <label>遷移<select class="rb-s-trans">
          <option value="${TAKE.TRANS_CUT}"${s.transition === TAKE.TRANS_CUT ? ' selected' : ''}>カット（瞬時）</option>
          <option value="${TAKE.TRANS_DIP}"${s.transition === TAKE.TRANS_DIP || !s.transition ? ' selected' : ''}>暗転（dip）</option>
          <option value="${TAKE.TRANS_FADE}"${s.transition === TAKE.TRANS_FADE ? ' selected' : ''}>フェード</option>
        </select></label>
        <label><input class="rb-s-transms" type="number" min="0" step="10" value="${s.transitionMs || 0}">ms<span class="rb-hint2">0=既定</span></label>
        <label class="chk"><input class="rb-s-post-on" type="checkbox" ${s.hasPost ? 'checked' : ''}>🎨 画像加工を上書き</label>
        <label title="映像の上に CG の人形を立てる。人形は 🎭 CG 人形パネルで定義する。カメラ姿勢が著作済みのカメラでのみ出る">CG 人形<select class="rb-s-cg">${cgOpts}</select></label>
        <label class="rb-s-cgmode-l" style="display:${s.cg ? '' : 'none'}">立ち位置<select class="rb-s-cgmode">
          <option value="${TAKE.CG_FOLLOW}"${s.cgMode !== TAKE.CG_FIXED ? ' selected' : ''}>体験者の位置</option>
          <option value="${TAKE.CG_FIXED}"${s.cgMode === TAKE.CG_FIXED ? ' selected' : ''}>決めた位置</option>
        </select></label>
      </div>
      <details class="rb-s-ovr"><summary>素材の上書き（-1 = 素材の設定のまま）</summary>
        <div class="rb-grid">
          <label>強度<input class="rb-s-strength" type="number" min="-1" max="1" step="0.05" value="${s.strength}"></label>
          <label>IN<input class="rb-s-fin" type="number" min="-1" step="0.1" value="${s.fadeInSec}"></label>
          <label>OUT<input class="rb-s-fout" type="number" min="-1" step="0.1" value="${s.fadeOutSec}"></label>
          <label>開始<input class="rb-s-tstart" type="number" min="-1" step="0.1" value="${s.trimStartSec}"></label>
          <label>終了<input class="rb-s-tend" type="number" min="-1" step="0.1" value="${s.trimEndSec}"></label>
        </div>
      </details>
      <div class="rb-s-post fx-rows" style="display:${s.hasPost ? '' : 'none'}"></div>
      <div class="rb-step-warn" style="display:${bad ? '' : 'none'}">⚠ ${escapeHtml(bad || '')}</div>`;

    const r = (q2) => row.querySelector(q2);
    const commit = (rebuild) => {
      s.source = r('.rb-s-src').value;
      s.camera = parseInt(r('.rb-s-cam').value, 10);
      if (!Number.isInteger(s.camera)) s.camera = -1;
      if (s.source !== TAKE.SRC_LIVE && s.source !== TAKE.SRC_REC) s.camera = -1;
      s.recLap = s.source === TAKE.SRC_REC ? Math.max(1, Math.round(numOr(r('.rb-s-reclap').value, 1))) : 0;
      s.cg = r('.rb-s-cg').value.trim();
      s.cgMode = r('.rb-s-cgmode').value === TAKE.CG_FIXED ? TAKE.CG_FIXED : TAKE.CG_FOLLOW;
      s.assetUrl = r('.rb-s-asseturl').value.trim();
      if (!isAssetSource(s.source)) s.assetUrl = '';
      s.cueId = r('.rb-s-cue').value;
      s.durKind = r('.rb-s-durkind').value === TAKE.DUR_UNTIL_CLIP_END ? TAKE.DUR_UNTIL_CLIP_END : TAKE.DUR_SEC;
      s.durSec = s.durKind === TAKE.DUR_SEC ? Math.max(0.2, numOr(r('.rb-s-dur').value, TAKE.FALLBACK_STEP_DUR_SEC)) : 0;
      s.transition = r('.rb-s-trans').value;
      s.transitionMs = Math.max(0, numOr(r('.rb-s-transms').value, 0));
      s.strength = numOr(r('.rb-s-strength').value, -1);
      s.fadeInSec = numOr(r('.rb-s-fin').value, -1);
      s.fadeOutSec = numOr(r('.rb-s-fout').value, -1);
      s.trimStartSec = numOr(r('.rb-s-tstart').value, -1);
      s.trimEndSec = numOr(r('.rb-s-tend').value, -1);
      markDirty(); render();
      if (rebuild) renderStepRows(t);
    };
    row.querySelectorAll('.rb-s-cue, .rb-s-transms, .rb-s-trans, .rb-s-strength, .rb-s-fin, .rb-s-fout, .rb-s-tstart, .rb-s-tend')
      .forEach((el) => { el.onchange = () => commit(false); });
    r('.rb-s-src').onchange = () => commit(true);
    r('.rb-s-cam').onchange = () => commit(true);
    r('.rb-s-reclap').onchange = () => commit(true);
    r('.rb-s-cg').onchange = () => commit(true);
    r('.rb-s-cgmode').onchange = () => commit(true);
    r('.rb-s-durkind').onchange = () => commit(true);
    r('.rb-s-dur').onchange = () => commit(true);
    // 素材 select → URL 欄へ流し込む（sa:// 等の手入力も残せるように 2 段構え）
    r('.rb-s-asset').onchange = () => { r('.rb-s-asseturl').value = r('.rb-s-asset').value; commit(true); };
    r('.rb-s-asseturl').onchange = () => commit(true);
    // 撮る → 合成する → ここで選ぶ、の導線。撮った直後に一覧へ出ないと素材が使えない。
    r('.rb-s-asset-refresh').onclick = async () => {
      if (deps.refreshCaptures) await deps.refreshCaptures();
      renderStepRows(t);
    };
    r('.rb-s-asset-dir').onclick = () => { if (deps.openCaptureDir) deps.openCaptureDir(); };

    r('.rb-s-up').onclick = () => { if (idx > 0) { const a = t.steps; [a[idx - 1], a[idx]] = [a[idx], a[idx - 1]]; markDirty(); render(); renderStepRows(t); } };
    r('.rb-s-down').onclick = () => { const a = t.steps; if (idx < a.length - 1) { [a[idx + 1], a[idx]] = [a[idx], a[idx + 1]]; markDirty(); render(); renderStepRows(t); } };
    r('.rb-s-del').onclick = () => { t.steps.splice(idx, 1); markDirty(); render(); renderStepRows(t); };

    r('.rb-s-cue-edit').onclick = () => openCueEditor(s, s.cueId ? cueById(s.cueId) : null);
    r('.rb-s-cue-new').onclick = () => openCueEditor(s, null);

    const postBox = r('.rb-s-post');
    const postOn = r('.rb-s-post-on');
    const fx = buildFxSliders(postBox, s.post || globalPost(), () => {
      s.hasPost = true; s.post = fx.read(); postOn.checked = true; markDirty();
    });
    postOn.onchange = () => {
      s.hasPost = postOn.checked;
      if (s.hasPost && !s.post) s.post = { ...globalPost() };
      if (s.hasPost) fx.set(s.post);
      postBox.style.display = s.hasPost ? '' : 'none';
      markDirty();
    };
    return row;
  }

  const isAssetSource = (src) => src === TAKE.SRC_CLIP || src === TAKE.SRC_STILL;
  function numOr(v, def) { const n = parseFloat(v); return Number.isFinite(n) ? n : def; }

  // ---- FX スライダ（区間 post / カット post 共用）------------------------------
  function buildFxSliders(containerEl, initial, onChange) {
    containerEl.innerHTML = '';
    const inputs = {}, vals = {};
    for (const [key, label, min, max, step] of FX) {
      const row = document.createElement('label');
      row.className = 'sld fx-row';
      row.append(`${label} `);
      const inp = document.createElement('input');
      inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step;
      inp.value = (initial && key in initial) ? initial[key] : FX_DEFAULT[key];
      const val = document.createElement('span'); val.textContent = inp.value;
      inp.oninput = () => { val.textContent = inp.value; onChange(); };
      row.append(inp, val);
      containerEl.appendChild(row);
      inputs[key] = inp; vals[key] = val;
    }
    return {
      read() { const o = {}; for (const [k] of FX) o[k] = parseFloat(inputs[k].value); return o; },
      set(vv) { for (const [k] of FX) { const v = (vv && k in vv) ? vv[k] : FX_DEFAULT[k]; inputs[k].value = v; vals[k].textContent = String(v); } },
    };
  }

  // ---- 埋め込み素材（cue）エディタ ---------------------------------------------
  function ensureCueEditor() {
    if (cueEditor) return cueEditor;
    cueEditor = createCueEditor({
      getLiveImg: (camId) => (deps.getLiveImg ? deps.getLiveImg(camId) : null),
      getCamPost: (camId) => { const c = cameras.find((x) => x.id === camId); return (c && c.post) || globalPost(); },
      getCaptures: () => captureItems(),
      refreshCaptures: () => deps.refreshCaptures && deps.refreshCaptures(),
      getAllCues: () => cues,
      saveCue: (cue) => deps.saveCue(cue),
      onSaved: (cue) => {
        if (cueEditorTarget && cueEditorTarget.step) cueEditorTarget.step.cueId = cue.id;
        const k = cues.findIndex((c) => c.id === cue.id);
        if (k >= 0) cues[k] = cue; else cues.push(cue);
        markDirty(); render();
        const t = selTake(); if (t) renderStepRows(t);
      },
    });
    cueEditor.el.addEventListener('ce-close', closeCueEditor);
    return cueEditor;
  }
  function openCueEditor(step, cue) {
    const host = inspectorEl.querySelector('.rb-cue-editor-host');
    if (!host) return;
    ensureCueEditor();
    if (cueEditor.el.parentNode !== host) { host.innerHTML = ''; host.appendChild(cueEditor.el); }
    cueEditorTarget = { step };
    // 素材は「その画のカメラ」に紐づく（ライブなら映すカメラ、それ以外は区間のカメラ）。
    const camIdx = (step.source === TAKE.SRC_LIVE && step.camera >= 0) ? step.camera : sel.camera;
    cueEditor.open(cameras[camIdx] ? cameras[camIdx].id : null, cue);
    cueEditor.el.style.display = '';
    editorOpen = true;
  }
  function closeCueEditor() { if (cueEditor) cueEditor.el.style.display = 'none'; editorOpen = false; cueEditorTarget = null; }

  // ---- 周回の増減 / 保存 -------------------------------------------------------
  q('.rb-lap-add').onclick = () => { lapCount++; markDirty(); render(); };
  q('.rb-lap-del').onclick = () => {
    if (lapCount <= 1) return;
    const removed = lapCount;
    const lost = timeline.segments.filter((s) => s.lap >= removed).length;
    if (lost && !confirm(`${removed} 周目には編集済みの区間が ${lost} 個あります。周ごと削除しますか？`)) return;
    timeline.segments = timeline.segments.filter((s) => s.lap < removed);
    lapCount--;
    if (sel && sel.lap >= lapCount + 1) { sel = null; closeCueEditor(); }
    markDirty(); render(); renderInspector();
  };
  q('.rb-undo').onclick = undo;
  // ▶ 検証 = 🕹 ショーシミュレーション（歩きで実時間検証）。卓の検証面はこれ 1 つ。
  q('.rb-validate').onclick = () => { if (deps.openSimulator) deps.openSimulator(); };
  q('.rb-dwell-reset').onclick = async () => {
    if (!deps.resetDwell) return;
    if (!confirm('区間に出ている「実測 平均滞在」の集計を消します。よろしいですか？')) return;
    await deps.resetDwell();
    render();
    if (sel && !editorOpen) renderInspector();
  };
  q('.rb-save').onclick = async () => {
    timeline.rev = (parseInt(timeline.rev, 10) || 0) + 1;
    const res = await deps.saveTimeline(serializeTimelineV3(timeline));
    if (res && res.ok !== false) {
      dirty = false; convertNote = ''; v2Source = false;
      renderDirty(); resetUndo(); render();
    }
    else { dirtyEl.textContent = '✕ 保存失敗'; dirtyEl.className = 'rb-dirty on'; }
  };

  // Ctrl+Z（入力欄では横取りしない。リボンが画面に出ている時だけ効く）
  function onKeyDown(e) {
    if (!(e.ctrlKey || e.metaKey) || (e.key !== 'z' && e.key !== 'Z')) return;
    const t = e.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) return;
    if (!container.offsetParent) return;
    e.preventDefault(); undo();
  }
  document.addEventListener('keydown', onKeyDown);

  // ---- 取り込み ---------------------------------------------------------------
  function adoptState(state) {
    const tl = (state && state.timeline) || null;
    timeline = normalizeTimelineV3(tl && Array.isArray(tl.segments) ? tl : { rev: (tl && tl.rev) || 1, segments: [] });
    assignIds();
  }
  function recomputeLapCount() {
    const maxLap = timeline.segments.reduce((m, s) => Math.max(m, s.lap || 1), 0);
    lapCount = Math.max(1, maxLap, lapCount);
  }

  // カメラ / 素材 / コース順は deps 側（app.js の state）が正。描画の前に必ず引き直す。
  function pullDeps(state) {
    cameras = deps.getCameras ? deps.getCameras() : ((state && state.cameras) || []);
    cues = deps.getCues ? deps.getCues() : ((state && state.cues) || []);
    actors = deps.getActors ? deps.getActors() : ((state && state.actors) || []);
    const layout = deps.getLayout ? deps.getLayout() : (state && state.layout) || null;
    order = deps.getCourseOrder ? deps.getCourseOrder() : (layout && layout.course ? layout.course.order : null);
    // 通過ライン（線分）と、その線がどのカメラのゾーンに置かれているかを判定するための展開済みゾーン。
    lines = linesFromLayout(layout);
    zoneBoxes = lines.length ? zonesFromLayout(layout).boxes : [];
  }

  function onState(state) {
    // 版はデータから自分で判定する（呼び元は版を知らなくていい）。
    v2Source = !isV3(state && state.timeline);
    pullDeps(state);
    if (!dirty) { adoptState(state); recomputeLapCount(); resetUndo(); }
    if (sel && sel.camera >= cameras.length) { sel = null; closeCueEditor(); }
    render();
    if (sel && !editorOpen) renderInspector();
    else if (!sel) inspectorEl.innerHTML = '';
  }

  render();
  renderDirty();
  resetUndo();
  return {
    onState,
    /** 実測滞在時間が更新された（app.js の /dwell/stats ポーリング由来）。区間表示だけ描き直す。 */
    onDwell() { render(); },
    isDirty: () => dirty,
    destroy() {
      document.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('pointermove', onDragMove);
      window.removeEventListener('pointerup', onDragEnd);
      window.removeEventListener('pointercancel', onDragEnd);
      if (unsubDuration) unsubDuration();
      stopBgmPreview();
      if (cueEditor) cueEditor.destroy();
    },
  };
}
