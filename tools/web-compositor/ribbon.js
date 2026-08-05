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

import {
  actorProxyGeometry, actorBodyGeometry, drawActorProxy, proxyIssueText, resolveProxyCalib, DEFAULT_HEIGHT_M,
} from './actor-proxy.js';
import { projectPoint, unprojectToFloor } from './calib.js';
// 較正 UI の純関数（部屋のワイヤー）だけ借りる。**同じ線を 2 度書かない** —
// 較正で見た絵と人形を置く時の絵が食い違うと、どちらがずれているのか判断できなくなる。
import { wireSegments } from './calib-session.js';
import { FX, FX_DEFAULT, camColor, escapeHtml, isVideoUrl } from './common.js';
import { recStepIssue } from './record-model.js';
// 周の並び（3 周 ＋ もどり）と「その区間を踏むか」の判定は run-model.js が単一の正
// （本番前チェック・シミュレータ・実機と同じ答えを出さないと、卓だけが嘘をつく）。
import {
  lapHeadings, totalLaps as runTotalLaps, isSegmentReachable, isReturnSegment,
  returnTakeTooLate, RUN_DEFAULT,
} from './run-model.js';
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

/**
 * そのカットが**自分でカメラを指す**か。live は画面ごとそのカメラへ動き、rec / plate は画面は
 * 動かないが「そのカメラで撮った画」なので較正が効く（＝ CG 人形を重ねられる）。
 * TakeSchema 側の `SourceLive || MatchesCameraPerspective` と同じ集合。
 */
const usesStepCamera = (src) =>
  src === TAKE.SRC_LIVE || src === TAKE.SRC_REC || src === TAKE.SRC_PLATE;

const SRC_LABEL = {
  [TAKE.SRC_LIVE]: 'ライブカメラ',
  [TAKE.SRC_INHERIT]: 'そのまま',
  [TAKE.SRC_CLIP]: '事前映像',
  [TAKE.SRC_STILL]: '静止画',
  [TAKE.SRC_REC]: '録画（この体験の）',
  [TAKE.SRC_PLATE]: '無人の部屋（このカメラ）',
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
        <span class="rb-laps"></span>
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
  let record = null;        // ⏺ 端末内録画の設定（show.json record）。「録画」カットが本当に録れるかの照合に使う。
  let zoneBoxes = [];       // 展開済みゾーン矩形（線がどのカメラのゾーンに置かれているかを実機と同じ判定で出す）
  let order = null;
  let orderIsExplicit = false;
  // 走り切る周数は **show.json run.totalLaps が単一の正**（「ライブ運用」の走り切る周数で決める）。
  // リボン側で別に増減できると、卓に出ている周数と実機が走る周数が食い違ったまま著作できてしまう。
  let run = null;
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
  const snap = () => JSON.stringify({ tl: timeline });
  function resetUndo() { undoStack = []; shadow = snap(); savedSnap = shadow; renderUndo(); }
  function renderUndo() { const b = q('.rb-undo'); if (b) b.disabled = !undoStack.length; }
  function undo() {
    if (!undoStack.length) return;
    const prev = JSON.parse(undoStack.pop());
    timeline = prev.tl;
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
  /** 走り切る周数（show.json run.totalLaps）。もどりの区間はこの外側に 1 つ足される。 */
  function lapsOf() { return runTotalLaps({ run }); }
  /** リボンが並べる周（1..周数 ＋ もどり）。 */
  function lapRows() { return lapHeadings(lapsOf()); }
  /** その周で並べるカメラ。もどりの周はスタート領域だけ（ほかは踏まないので出さない）。 */
  function camsForLap(lap) {
    const rs = rows();
    const laps = lapsOf();
    if (lap <= laps) return rs;
    return rs.filter((ci) => isSegmentReachable(lap, ci, laps, rs));
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
  // 重ねる素材（cue）のマスクは**そのカメラの構図に対して**焼かれる（素材工房が種フレームとの差分を
  // PNG にする）。別カメラの cue を重ねると実機で位置が合わないので、選ばせない
  // （2026-07-27「作れない組み合わせは卓が選ばせない」をラインから素材へ延長・2026-07-28）。
  // カメラ未指定の cue は場所に依らない汎用素材として通す。
  function cueFitsCam(c, idx) {
    if (!c.camera) return true;
    const id = cameras[idx] ? cameras[idx].id : null;
    return id != null && c.camera === id;
  }
  // このカットで画面の下地になるカメラ。live / rec は自分で指し、それ以外は区間のカメラを継ぐ。
  //
  // ⚠ 継ぐ先は**そのカットが属する区間**で、選択中の区間ではない。`segCam` を渡さないと
  //    リボンを描いている最中（まだ何も選んでいない = sel が null）は -1 に落ち、
  //    「そのまま」カットの下地が毎回「未割当」と判定されて、正しく著作された
  //    オーバーレイに「マスクの位置が実機でずれます」の ⚠ が出る（実際に出ていた）。
  function stepScreenCam(s, segCam) {
    if (usesStepCamera(s.source) && Number.isInteger(s.camera) && s.camera >= 0) {
      return s.camera;
    }
    if (Number.isInteger(segCam)) return segCam;
    return sel ? sel.camera : -1;
  }
  // 演出専用カメラ（role:"fx"）は周回に出てこない＝作者が「どれが D か」を選ぶ時の手がかりが要る。
  function camLabel(idx) {
    const c = cameras[idx];
    if (!c) return `#${idx}`;
    return `カメラ ${c.id}${c.role === 'fx' ? '（演出専用）' : ''}`;
  }
  function globalPost() { return (deps.getGlobalPost && deps.getGlobalPost()) || FX_DEFAULT; }
  function captureItems() { return (deps.getCaptures && deps.getCaptures()) || []; }

  // ---- CG 人形の立ち位置 -------------------------------------------------------
  //   位置は**人形ではなくカット**が持つ（同じ人形を別のカットで別の場所に立たせるため。
  //   設計 2026-07-27_cg-compositing-rebuild.md §2.6）。actor 側の fixedX/Z/YawDeg は
  //   「まだカットに置いていない時の初期値」としてだけ読む（旧データの後方互換）。
  function actorById(id) { return actors.find((a) => a.id === id) || null; }
  function actorHeight(id) {
    const a = actorById(id);
    return a && a.heightM > 0 ? a.heightM : DEFAULT_HEIGHT_M;
  }
  function placementOf(s) {
    // hasPlacement は**書き出しの**present-flag であって「編集中に値を覚えているか」ではない。
    // 「体験者の位置」へ切り替えると flag は false になるが、値は残す — 戻した時に
    // 苦労して置いた立ち位置が 0 に戻るのは事故に見える。
    // 値が丸ごと 0 のときだけ actor の既定へ落とす（正規化が入れる空の placement と、
    // 著作された placement を、これ以外に見分ける手段が無い。actor 既定も普通は 0 なので実害は出ない）。
    const p = s.placement;
    if (p && (numOr(p.x, 0) || numOr(p.z, 0) || numOr(p.yawDeg, 0))) {
      return { x: numOr(p.x, 0), z: numOr(p.z, 0), yawDeg: numOr(p.yawDeg, 0) };
    }
    const a = actorById(s.cg);
    return {
      x: a ? numOr(a.fixedX, 0) : 0, z: a ? numOr(a.fixedZ, 0) : 0, yawDeg: a ? numOr(a.fixedYawDeg, 0) : 0,
    };
  }
  function setPlacement(s, p) {
    s.placement = { x: p.x, z: p.z, yawDeg: p.yawDeg };
    s.hasPlacement = !!s.cg && s.cgMode === TAKE.CG_FIXED;
  }
  /** 人形の投影に使うカメラ。ライブ／録画／無人プレートはカットのカメラ、素材カットは区間のカメラ。 */
  function stepCameraIndex(s) {
    if (usesStepCamera(s.source) && s.camera >= 0) return s.camera;
    return sel ? sel.camera : -1;
  }
  /**
   * 人形が**黙って出ない**条件をここで言い切る。実機のガードと同じ判定
   * （姿勢も較正も無いカメラでは ShowCgLayer が人形を出さない）。
   */
  function cgIssue(s) {
    if (!s.cg) return null;
    if (!actorById(s.cg)) return `人形「${s.cg}」は 🎭 CG 人形に定義がありません（出ません）`;
    // 素材（動画・静止画）はいつどこで撮ったか分からない画で、どのカメラの較正を当てても
    // 人形のパースが合わない。**実機（TakeRunner）も素材カットでは人形を出さない。**
    if (s.source === 'clip' || s.source === 'still') {
      return '素材（動画・静止画）のカットなので**実機でも人形は出ません**'
        + '（素材の構図と人形のパースが合わないため）。ライブか録画のカットへ置いてください';
    }
    const cam = cameras[stepCameraIndex(s)];
    if (!cam) return 'このカットが映すカメラが決まっていません（人形も出ません）';
    if (!(cam.calib && cam.calib.fxPx > 1) && !cam.pose) {
      return `${camLabel(stepCameraIndex(s))} は姿勢が未著作です（実機では人形が出ません。`
        + '📐 カメラ姿勢 で置くか 🎯 姿勢を合わせる で較正してください）';
    }
    return null;
  }

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
    const d = resolveStepDuration(s, cue, durationOf, recSecondsOf);
    // 「次にカメラが切り替わるまで」は著作時に秒が決まらない（体験者が歩いた分だけ続く）。
    // 絵の幅は仮置きし、必ず ≈ を付ける — 確定した秒に見せると、リボンの絵が嘘になる。
    const approx = d.kind === 'estimated' || d.kind === 'unknown' || d.kind === 'untilZone';
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
  // `segCam` = このカットが属する区間のカメラ。「そのまま」「素材」カットの下地の判定に使う。
  function stepIssue(s, segCam) {
    if (s.source === TAKE.SRC_LIVE && !(Number.isInteger(s.camera) && s.camera >= 0 && s.camera < cameras.length)) {
      return 'ライブのカメラが未選択（実機ではこのカットは飛ばされます）';
    }
    // 「録画」カットは録画設定との照合まで見る（判定は record-model が単一の正）。
    // ここが食い違うと実機は録っていない映像を探して、そのカットを黙って飛ばす。
    const recBad = recStepIssue(record, s, cameras.length);
    if (recBad) return recBad;
    if ((s.source === TAKE.SRC_CLIP || s.source === TAKE.SRC_STILL) && !s.assetUrl) {
      return '素材が未選択（実機ではこのカットは飛ばされます）';
    }
    // 無人プレートは素材を直に指しても、重ねる素材（cue）から取ってもよい。どちらも無ければ出ない。
    if (s.source === TAKE.SRC_PLATE && !s.assetUrl && !s.cueId) {
      return '無人の部屋の画が未選択（実機ではこのカットは飛ばされます）';
    }
    if (s.source === TAKE.SRC_PLATE && !(Number.isInteger(s.camera) && s.camera >= 0)) {
      return 'どのカメラの部屋かが未選択（人形を重ねる構図が決まりません）';
    }
    if (s.cueId && !cueById(s.cueId)) return `重ねる素材 ${s.cueId} が見つかりません`;
    // 既存データが別カメラの素材を指しているケース（選択肢は塞いだが、過去の割当は残りうる）。
    // マスクはカメラの構図に対して焼かれるので、実機では位置がずれたまま出る＝黙って壊れる。
    if (s.cueId) {
      const c = cueById(s.cueId);
      const scam = stepScreenCam(s, segCam);
      if (c && !cueFitsCam(c, scam)) {
        return `重ねる素材「${cueName(s.cueId)}」は ${camLabel(cameras.findIndex((x) => x.id === c.camera))} 用です`
          + `（このカットの下地は ${camLabel(scam)}。マスクの位置が実機でずれます）`;
      }
    }
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

  // ---- 「作っても実機が honour できない組み合わせ」を作らせないための判定 -------------
  //   実行側は「画面は 1 つ・同時に走る演出は 1 本」なので、構造的に必ず死ぬ組み合わせがある。
  //   それらは警告ではなく **UI で選べなくする**（ユーザー指示 2026-07-27）。

  /** その区間のカメラが担当する通過ライン（これが無ければ at=line は発火しようがない）。 */
  function ownLinesOf(camera) { return lines.filter((l) => l.camera === camera); }

  /** その区間に既にある「離脱時」の演出（except を除く）。離脱時に出られるのは 1 本だけ。 */
  function exitTakeOf(lap, camera, except) {
    const seg = segAt(lap, camera);
    return ((seg && seg.takes) || []).find((t) => t.at === TAKE.AT_EXIT && t !== except) || null;
  }

  function takeIssue(t, camera) {
    if (!t.steps || !t.steps.length) return 'カットが 1 枚もありません（発火しません）';
    const lineBad = lineIssue(t, camera);
    if (lineBad) return lineBad;
    const bad = t.steps.map((s) => stepIssue(s, camera)).filter(Boolean);
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
  /**
   * 「録画」カットの尺。録画の長さ = 録った区間に体験者が居た時間なので、実測滞在の平均で見積もる
   * （最短ではなく平均。最短で見ると尺を過小に見せて「最大長に収まる」と誤解させる）。
   * 実測が無ければ null → 既定尺で仮置きされ、表示は ≈ のまま。
   */
  function recSecondsOf(s) {
    if (!s || s.source !== TAKE.SRC_REC) return null;
    const e = dwellFor(s.recLap, s.camera);
    return e && e.meanSec > 0 ? e.meanSec : null;
  }
  /** 「録画」カットの尺の説明（著作時には確定しないので、根拠ごと言う）。 */
  function recLenNote(s) {
    const sec = recSecondsOf(s);
    if (sec) return `長さ ≈${fmtSec(sec)}s（この区間の実測 平均滞在）`;
    return '長さ = その人がこの区間に居た時間（実測が出ると出ます）';
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
  // ひとつ前の区間（周回コース順）。1 周目の先頭には前が無い。
  function prevSegKey(lap, camera) {
    const rs = rows();
    const i = rs.indexOf(camera);
    if (i < 0) return null;
    if (i > 0) return { lap, camera: rs[i - 1] };
    return lap > 1 ? { lap: lap - 1, camera: rs[rs.length - 1] } : null;
  }

  /**
   * 「ひとつ前の区間の離脱時演出が、この区間の滞在を食い尽くす」危険。
   *
   * 実行側は**画面が空くまで待つ**（それ自体は正しい）。問題は、待っているあいだに体験者が
   * この区間を出てしまうと、著作した演出が一度も出ないまま終わること。
   * 卓はこれを一度も言っていなかったので、シミュレータを回さないと気づけなかった。
   */
  function blockedByPrevExit(t, lap, camera) {
    if (!t || t.at === TAKE.AT_EXIT) return null;
    const p = prevSegKey(lap, camera);
    if (!p) return null;
    const ex = exitTakeOf(p.lap, p.camera, null);
    if (!ex) return null;
    const exSec = takeSeconds(ex).sec;
    if (!(exSec > 0)) return null;
    const waits = t.wait === TAKE.WAIT_CHAIN;
    const e = dwellFor(lap, camera);
    const head = `⏳ ひとつ前（${p.lap}周目 ${camLabel(p.camera)}）の離脱時の演出が ${fmtSec(exSec)}s あります`;
    if (waits) {
      return `${head}。この演出は「前の演出が終わるまで待つ」ので、`
        + `${e ? `実測 ${fmtSec(e.meanSec)}s のこの区間を出たあとでも` : 'この区間を出たあとでも'}続けて出ます。`;
    }
    if (e && exSec >= e.minSec) {
      return `${head}。実測の最短滞在 ${fmtSec(e.minSec)}s を超えるので、`
        + `この演出は出ないまま終わる周があります — インスペクタの「前の演出が終わるまで待つ」を入れてください。`;
    }
    return `${head}。この区間の滞在がそれより短いと、この演出は出ないまま終わります`
      + `（「前の演出が終わるまで待つ」で救えます）。`;
  }

  function takeTimingRisk(t, lap, camera) {
    const blocked = blockedByPrevExit(t, lap, camera);
    if (blocked) return blocked;
    if (!t || t.at === TAKE.AT_EXIT) return null;         // 離脱時は必ず出る
    // もどりの区間は入った瞬間に終了へ向かうので、猶予を過ぎた開始位置の演出は**実機で必ず出ない**。
    // 本番前チェックだけでなくここでも言う（作れない組み合わせは選ばせない、の流儀）。
    if (isReturnSegment(lap, camera, lapsOf(), rows())
        && t.at !== TAKE.AT_LINE && returnTakeTooLate(t.offsetSec, run)) {
      const grace = Number((run && run.endGraceSec) || RUN_DEFAULT.endGraceSec);
      return `⏱ もどりの区間は入った直後に体験が終わります。開始 +${fmtSec(t.offsetSec || 0)}s は`
           + ` 猶予 ${fmtSec(grace)}s を超えるので実機では出ません`;
    }
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
    // 走り切る周数を超えた区間は、この画面に出ないまま show.json に残る。黙って隠すと
    // 「著作したのに出ない」に現場で初めて気づくので、残っていることをここで言う。
    const laps = lapsOf();
    const hidden = timeline.segments.filter((s) => (s.takes || []).length
      && !isSegmentReachable(s.lap || 0, s.camera, laps, rs));
    if (hidden.length) {
      const n = hidden.reduce((m, s) => m + s.takes.length, 0);
      msgs.push(`踏まない区間に演出が ${n} 本残っています`
        + `（${[...new Set(hidden.map((s) => `${s.lap}周目`))].join(' / ')}）。`
        + '出したいなら「ライブ運用」の走り切る周数を増やしてください。');
    }
    noteEl.textContent = msgs.join(' / ');
    noteEl.className = 'rb-note' + (msgs.length ? ' on' : '');
    renderLapsLabel();

    // BGM は「指示が無い区間は前の曲が続く」ので、もどりの区間まで通して解く。
    const bgmLane = resolveBgmLane(timeline.segments, rs, laps + 1, rootBgmTrackId());
    track.innerHTML = '';
    for (const h of lapRows()) {
      const lapEl = document.createElement('div');
      lapEl.className = 'rb-lap' + (h.isReturn ? ' ret' : '');
      const head = document.createElement('div');
      head.className = 'rb-lap-head';
      head.textContent = h.label;
      if (h.isReturn) head.title = '元の位置に戻って体験が終わる区間';
      lapEl.appendChild(head);

      const rib = document.createElement('div');
      rib.className = 'rb-ribbon';
      camsForLap(h.lap).forEach((ci) => {
        rib.appendChild(renderSeg(h.lap, ci, bgmLane.get(`${h.lap}:${ci}`)));
        const seg = segAt(h.lap, ci);
        for (const t of (seg && seg.takes) || []) {
          if (t.at === TAKE.AT_EXIT) rib.appendChild(renderTake(t, h.lap, ci, null));
        }
      });
      lapEl.appendChild(rib);
      if (h.isReturn) {
        const note = document.createElement('div');
        note.className = 'rb-lap-note';
        note.textContent = '元の位置に戻って体験が終わります。ここに置いた演出は入った直後にしか始まりません。';
        lapEl.appendChild(note);
      }
      track.appendChild(lapEl);
    }
  }

  /** 周数の出どころを常に見せる（リボンからは増減できない＝食い違いを作らない）。 */
  function renderLapsLabel() {
    const el = q('.rb-laps');
    if (!el) return;
    el.textContent = `${lapsOf()} 周 ＋ もどり`;
    el.title = '走り切る周数は「ライブ運用」タブの走り切る周数（show.json run.totalLaps）で決まります';
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
      const bad = stepIssue(s, ci);
      return `<div class="rb-step" style="flex:${d.sec} 1 0;--sc:${stepColor(s)}" title="${escapeHtml(stepTitle(s, ci))}">
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
    // 無人プレートは「そのカメラで撮った画」なので、そのカメラの色を淡く使う（録画と同じ考え方）。
    if (s.source === TAKE.SRC_PLATE) return camColor(Number.isInteger(s.camera) && s.camera >= 0 ? s.camera : 0);
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
    // 「録画」カットは assetUrl を持たない（素材は体験中に端末が作る）。ここを素材カットと
    // 同じ扱いにすると「映像（未選択）」と出て、正しく著作された 3 周目のカットが
    // 未完成に見える（実際にそう見えていた）。指しているのは (録った周, カメラ)。
    if (s.source === TAKE.SRC_REC) {
      const id = cameras[s.camera] ? cameras[s.camera].id : '?';
      return `録画 ${s.recLap > 0 ? s.recLap : 1}周目 ${id}`;
    }
    // 無人プレートも「どのカメラの部屋か」が要点なので、ファイル名ではなくカメラで呼ぶ。
    if (s.source === TAKE.SRC_PLATE) {
      const id = cameras[s.camera] ? cameras[s.camera].id : '?';
      return `無人 ${id}`;
    }
    const kind = s.source === TAKE.SRC_STILL ? '静止画' : '映像';
    return `${kind} ${baseName(s.assetUrl) || '（未選択）'}`;
  }
  const DUR_KIND_LABEL = {
    exact: '尺 秒指定',
    measured: '尺 素材の実尺（実測）',
    estimated: '尺 推定（素材の trim から。実機は素材の実尺で終わる）',
    unknown: '尺 不明（実機は素材の終わりまで。卓では最大長まで走る）',
    fallback: '尺 素材なし → 既定 4s（実機も同じ）',
    untilZone: '尺 次にカメラが切り替わるまで（体験者がこの区間を出るまで出し続ける。絵の幅は仮）',
  };
  function stepTitle(s, segCam) {
    const bad = stepIssue(s, segCam);
    const tr = s.transition === TAKE.TRANS_CUT ? 'カット'
      : s.transition === TAKE.TRANS_FADE ? 'フェード'
      : s.transition === TAKE.TRANS_GLITCH ? '乱れ' : '暗転';
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
    const info = dropAt(e.clientX, e.clientY, drag.t);
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
  function dropAt(cx, cy, dragged) {
    const hit = document.elementFromPoint(cx, cy);
    const segEl = hit && hit.closest ? hit.closest('.rb-seg') : null;
    if (!segEl || !container.contains(segEl)) return null;
    const lap = parseInt(segEl.dataset.lap, 10);
    const cam = parseInt(segEl.dataset.cam, 10);
    const r = segEl.getBoundingClientRect();
    // 右端へ吸着＝離脱時。ただし既に離脱時の演出がある区間へは吸着させない
    //（2 本目は配列順で必ず負けて出ないので、そもそも作らせない）。
    if (cx > r.right - MAGNET && !exitTakeOf(lap, cam, dragged)) {
      return { lap, cam, at: TAKE.AT_EXIT, el: segEl };
    }
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
    // 2 本目以降は「前の進入演出が終わったあたり」に置く。全部 +0s に積むと、リボンの絵と
    // 実際の発火時刻（1 本目が終わってから順に出る）が食い違って読めなくなる。
    const prior = (seg.takes || []).filter((x) => x.at === TAKE.AT_ENTER);
    const stacked = at === TAKE.AT_ENTER
      ? prior.reduce((m, x) => Math.max(m, (x.offsetSec || 0) + takeSeconds(x).sec), 0)
      : 0;
    const t = newTake(uniqueTakeId(seg, takeId(lap, camera, seg.takes.length)), {
      at, offsetSec: Math.round(stacked * 10) / 10,
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

    // 「離脱時」は 1 区間 1 本まで。2 本目は配列順で必ず負けて出ないので、作らせない。
    const addExitBtn = inspectorEl.querySelector('.rb-add-exit');
    const existingExit = exitTakeOf(sel.lap, sel.camera, null);
    if (existingExit) {
      addExitBtn.disabled = true;
      addExitBtn.title = `離脱時に出せるのは 1 本だけです（既に「${existingExit.name || '離脱の演出'}」があります）`;
    } else {
      addExitBtn.onclick = () => addTake(sel.lap, sel.camera, TAKE.AT_EXIT);
    }

    const addLineBtn = inspectorEl.querySelector('.rb-add-line');
    const ownLines = ownLinesOf(sel.camera);
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
    const ownLines = ownLinesOf(sel.camera);
    // 「離脱時」に出られるのは 1 区間 1 本だけ。既に別の 1 本があるなら選ばせない。
    const otherExit = exitTakeOf(sel.lap, sel.camera, t);
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
            <option value="${TAKE.AT_EXIT}"${t.at === TAKE.AT_EXIT ? ' selected' : ''}${otherExit ? ' disabled' : ''}>離脱時（この区間を離れる瞬間）${otherExit ? '— 既に 1 本あります' : ''}</option>
            <option value="${TAKE.AT_LINE}"${onLine ? ' selected' : ''}${ownLines.length ? '' : ' disabled'}>このラインを通過したら${ownLines.length ? '' : `— ${camLabel(sel.camera)} 担当のラインがありません`}</option>
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
          <label class="chk" title="画面が塞がっていて出られなかったとき、区間を出ても『塞いでいた演出が終わるまで』待ちます。前の区間の離脱時演出が長くて、この区間の滞在が短いときに、著作した演出が消えるのを防ぎます。">
            <input class="rb-t-wait" type="checkbox" ${t.wait === TAKE.WAIT_CHAIN ? 'checked' : ''}
              ${t.at === TAKE.AT_EXIT ? 'disabled' : ''}>前の演出が終わるまで待つ</label>
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
      // 離脱時の演出は持ち越せない（別の区間で出すと文脈が最も壊れる）。
      t.wait = (i('.rb-t-wait').checked && t.at !== TAKE.AT_EXIT) ? TAKE.WAIT_CHAIN : TAKE.WAIT_SEGMENT;
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
    inspectorEl.querySelectorAll('.rb-t-name, .rb-t-off, .rb-t-policy, .rb-t-wait, .rb-t-once, .rb-t-max, .rb-t-missed, .rb-t-line')
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
    const lane = resolveBgmLane(timeline.segments, rows(), lapsOf() + 1, rootBgmTrackId()).get(`${lap}:${camera}`);
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

    const srcOpts = [TAKE.SRC_LIVE, TAKE.SRC_INHERIT, TAKE.SRC_CLIP, TAKE.SRC_STILL, TAKE.SRC_REC,
                     TAKE.SRC_PLATE]
      .map((k) => `<option value="${k}"${s.source === k ? ' selected' : ''}>${SRC_LABEL[k]}</option>`).join('');
    const camOpts = cameras.map((c, k) => `<option value="${k}"${s.camera === k ? ' selected' : ''}>${escapeHtml(camLabel(k))}</option>`).join('');
    const wantVideo = s.source !== TAKE.SRC_STILL && s.source !== TAKE.SRC_PLATE;
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
    // 構図が合う cue だけを選べるようにする。合わないものは理由付きで無効化して見せる
    //（隠すと「あるはずの素材が無い」に見えるので、無効の理由ごと出す）。
    const scam = stepScreenCam(s);
    let cueOpts = '<option value="">（重ねない）</option>';
    const fitCues = cues.filter((c) => cueFitsCam(c, scam));
    const offCues = cues.filter((c) => !cueFitsCam(c, scam));
    for (const c of fitCues) cueOpts += `<option value="${escapeHtml(c.id)}"${s.cueId === c.id ? ' selected' : ''}>${escapeHtml(c.name || c.id)}</option>`;
    if (offCues.length) {
      cueOpts += '<optgroup label="他カメラの素材（構図が合いません）">';
      for (const c of offCues) {
        // 既に割り当て済みのものだけは選択状態を保てるよう有効にしておく（無効化すると編集で黙って消える）。
        const cur = s.cueId === c.id;
        cueOpts += `<option value="${escapeHtml(c.id)}"${cur ? ' selected' : ' disabled'}>${escapeHtml(c.name || c.id)}（カメラ ${escapeHtml(c.camera || '?')}）</option>`;
      }
      cueOpts += '</optgroup>';
    }
    if (s.cueId && !cues.some((c) => c.id === s.cueId)) cueOpts += `<option value="${escapeHtml(s.cueId)}" selected>${escapeHtml(s.cueId)}（未定義）</option>`;
    // 立ち位置のラジオはカットごとに別グループにする（name が衝突すると別カットのカットを選び直す）。
    const cgModeName = `rb-cgmode-${t.id}-${idx}`;
    const fixedPlace = !!s.cg && s.cgMode === TAKE.CG_FIXED;
    const pl = placementOf(s);

    const isLive = s.source === TAKE.SRC_LIVE;
    const isRec = s.source === TAKE.SRC_REC;
    const isAsset = isAssetSource(s.source);
    const hasCam = usesStepCamera(s.source);
    const bySec = s.durKind === TAKE.DUR_SEC;
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
        <label class="rb-s-cam-l" style="display:${hasCam ? '' : 'none'}" title="${s.source === TAKE.SRC_PLATE ? 'どのカメラで撮った無人の部屋か。人形はこのカメラの較正で重なる' : ''}">カメラ<select class="rb-s-cam">${camOpts}</select></label>
        <label class="rb-s-reclap-l" style="display:${isRec ? '' : 'none'}" title="この体験のうち何周目に録った映像か（録れていなければ実機ではこのカットは飛ばされます）">録った周<input class="rb-s-reclap" type="number" min="1" step="1" value="${s.recLap > 0 ? s.recLap : 1}">周目<span class="rb-hint2">${escapeHtml(recLenNote(s))}</span></label>
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
          <option value="${TAKE.DUR_UNTIL_CLIP_END}"${s.durKind === TAKE.DUR_UNTIL_CLIP_END ? ' selected' : ''}>素材の終わりまで</option>
          <option value="${TAKE.DUR_UNTIL_ZONE_CHANGE}"${s.durKind === TAKE.DUR_UNTIL_ZONE_CHANGE ? ' selected' : ''}>次にカメラが切り替わるまで</option>
        </select></label>
        <label class="rb-s-dur-l" style="display:${bySec ? '' : 'none'}"><input class="rb-s-dur" type="number" min="0.2" step="0.1" value="${s.durSec > 0 ? s.durSec : TAKE.FALLBACK_STEP_DUR_SEC}">s</label>
        <label>遷移<select class="rb-s-trans">
          <option value="${TAKE.TRANS_CUT}"${s.transition === TAKE.TRANS_CUT ? ' selected' : ''}>カット（瞬時）</option>
          <option value="${TAKE.TRANS_DIP}"${s.transition === TAKE.TRANS_DIP || !s.transition ? ' selected' : ''}>暗転（dip）</option>
          <option value="${TAKE.TRANS_FADE}"${s.transition === TAKE.TRANS_FADE ? ' selected' : ''}>フェード</option>
          <option value="${TAKE.TRANS_GLITCH}"${s.transition === TAKE.TRANS_GLITCH ? ' selected' : ''}>乱れ（伝送の劣化を装う）</option>
        </select></label>
        <label><input class="rb-s-transms" type="number" min="0" step="10" value="${s.transitionMs || 0}">ms<span class="rb-hint2">0=既定</span></label>
        <label title="カットが始まってから 1 回だけ走らせる映像の乱れ。遷移の「乱れ」とは別物で、こちらは体験者の注意を引くために使う">乱れ<input class="rb-s-glitch" type="number" min="0" max="1" step="0.05" value="${s.glitch || 0}"><span class="rb-hint2">0=出さない</span></label>
        <label class="rb-s-glitchsec-l" style="display:${(s.glitch || 0) > 0 ? '' : 'none'}"><input class="rb-s-glitchsec" type="number" min="0.05" step="0.05" value="${s.glitchSec > 0 ? s.glitchSec : TAKE.DEFAULT_STEP_GLITCH_SEC}">s</label>
        <label class="chk"><input class="rb-s-post-on" type="checkbox" ${s.hasPost ? 'checked' : ''}>🎨 画像加工を上書き</label>
        <label title="映像の上に CG の人形を立てる。人形は 🎭 CG 人形パネルで定義する。カメラ姿勢が著作済みのカメラでのみ出る">CG 人形<select class="rb-s-cg">${cgOpts}</select></label>
        <span class="rb-s-cgmode-l" style="display:${s.cg ? '' : 'none'}">立ち位置
          <label class="chk"><input type="radio" name="${cgModeName}" class="rb-s-cgmode-follow" ${fixedPlace ? '' : 'checked'}>体験者の位置</label>
          <label class="chk"><input type="radio" name="${cgModeName}" class="rb-s-cgmode-fixed" ${fixedPlace ? 'checked' : ''}>決めた位置</label>
          <span class="rb-s-place-note">${escapeHtml(cgIssue(s) || '')}</span>
        </span>
      </div>
      <div class="rb-grid rb-s-place" style="display:${fixedPlace ? '' : 'none'}">
        <label title="course 空間の X（東西）。フロアマップと同じ座標系">X<input class="rb-s-px" type="number" step="0.05" value="${round2(pl.x)}">m</label>
        <label title="course 空間の Z（南北）。フロアマップと同じ座標系">Z<input class="rb-s-pz" type="number" step="0.05" value="${round2(pl.z)}">m</label>
        <label title="人形が向く方向。0° = course +Z（フロアマップの上・体験者から見て奥）">向き<input class="rb-s-pyaw" type="number" step="5" value="${Math.round(pl.yawDeg)}">°</label>
        <button class="rb-s-place-open accent" title="このカットが映すカメラの実映像を出して、床をクリックで立ち位置を置く">📍 画面で置く</button>
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
      if (!usesStepCamera(s.source)) s.camera = -1;
      s.recLap = s.source === TAKE.SRC_REC ? Math.max(1, Math.round(numOr(r('.rb-s-reclap').value, 1))) : 0;
      s.cg = r('.rb-s-cg').value.trim();
      s.cgMode = r('.rb-s-cgmode-fixed').checked ? TAKE.CG_FIXED : TAKE.CG_FOLLOW;
      // 立ち位置は「決めた位置」を選んでいる間だけ実体を持つ（present-flag は宣言 bool ∧ 実体の AND 規約）。
      // 体験者追従に戻した時に placement を書き残すと、Unity 側で幽霊の立ち位置が武装する。
      setPlacement(s, {
        x: numOr(r('.rb-s-px').value, pl.x),
        z: numOr(r('.rb-s-pz').value, pl.z),
        yawDeg: numOr(r('.rb-s-pyaw').value, pl.yawDeg),
      });
      s.assetUrl = r('.rb-s-asseturl').value.trim();
      if (!isAssetSource(s.source)) s.assetUrl = '';
      s.cueId = r('.rb-s-cue').value;
      const dk = r('.rb-s-durkind').value;
      s.durKind = (dk === TAKE.DUR_UNTIL_CLIP_END || dk === TAKE.DUR_UNTIL_ZONE_CHANGE) ? dk : TAKE.DUR_SEC;
      s.durSec = s.durKind === TAKE.DUR_SEC ? Math.max(0.2, numOr(r('.rb-s-dur').value, TAKE.FALLBACK_STEP_DUR_SEC)) : 0;
      s.transition = r('.rb-s-trans').value;
      s.transitionMs = Math.max(0, numOr(r('.rb-s-transms').value, 0));
      s.glitch = Math.max(0, Math.min(1, numOr(r('.rb-s-glitch').value, 0)));
      s.glitchSec = s.glitch > 0
        ? Math.max(0.05, numOr(r('.rb-s-glitchsec').value, TAKE.DEFAULT_STEP_GLITCH_SEC))
        : 0;
      s.strength = numOr(r('.rb-s-strength').value, -1);
      s.fadeInSec = numOr(r('.rb-s-fin').value, -1);
      s.fadeOutSec = numOr(r('.rb-s-fout').value, -1);
      s.trimStartSec = numOr(r('.rb-s-tstart').value, -1);
      s.trimEndSec = numOr(r('.rb-s-tend').value, -1);
      markDirty(); render();
      if (rebuild) renderStepRows(t);
    };
    row.querySelectorAll('.rb-s-cue, .rb-s-transms, .rb-s-trans, .rb-s-strength, .rb-s-fin, .rb-s-fout, .rb-s-tstart, .rb-s-tend, .rb-s-glitchsec')
      .forEach((el) => { el.onchange = () => commit(false); });
    // 乱れの強さを 0 にしたら秒欄を畳む（0 のとき秒だけ残っていると「効いているのか」が読めない）。
    r('.rb-s-glitch').onchange = () => commit(true);
    r('.rb-s-src').onchange = () => commit(true);
    r('.rb-s-cam').onchange = () => commit(true);
    r('.rb-s-reclap').onchange = () => commit(true);
    r('.rb-s-cg').onchange = () => commit(true);
    row.querySelectorAll('.rb-s-cgmode-follow, .rb-s-cgmode-fixed').forEach((el) => { el.onchange = () => commit(true); });
    row.querySelectorAll('.rb-s-px, .rb-s-pz, .rb-s-pyaw').forEach((el) => { el.onchange = () => commit(false); });
    // 📍 画面で置く: 実映像の床をクリック → 立ち位置。数値だけでは誰も置けない（ユーザーの明示的な指摘）。
    r('.rb-s-place-open').onclick = () => openPlaceUi(t, s, idx);
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

  // 画面を素材で埋めるカット（TakeSchema.IsAssetSource の対）。無人プレートもここに入る。
  const isAssetSource = (src) => src === TAKE.SRC_CLIP || src === TAKE.SRC_STILL || src === TAKE.SRC_PLATE;
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
      // 使っている演出がある cue は消させない（消せると演出が黙って映らなくなる）。
      getCueUsage: (id) => {
        const out = [];
        for (const s of (timeline.segments || [])) {
          for (const t of (s.takes || [])) {
            for (const st of (t.steps || [])) {
              if (st.cueId !== id) continue;
              const label = `${s.lap}周目 ${camLabel(s.camera)}「${t.name || t.id || '演出'}」`;
              if (!out.includes(label)) out.push(label);
            }
          }
        }
        return out;
      },
      deleteCue: (id) => deps.deleteCue(id),
      onDeleted: (id) => {
        const k = cues.findIndex((c) => c.id === id);
        if (k >= 0) cues.splice(k, 1);
        if (cueEditorTarget && cueEditorTarget.step && cueEditorTarget.step.cueId === id) {
          cueEditorTarget.step.cueId = '';
          markDirty();
        }
        render();
        const t = selTake(); if (t) renderStepRows(t);
      },
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

  // ---- 📍 画面で置く（CG 人形の立ち位置）---------------------------------------
  //
  //   「座標指定でしか人形を配置できないのも難しい」（ユーザー原文）への答え。
  //   実映像の床をクリックすると、較正の**逆写像**（calib.unprojectToFloor）で course 座標が
  //   一意に決まる — 床が平面だと分かっているので奥行きの不定性が消える。
  //
  //   ⚠ ここに描くのは**輪郭プロキシだけ**。卓は CG 人形を実描画しない（設計 §2.1）。
  //     写実に見せると著作者がそれを信じて Unity での最終確認を飛ばす。半透明の輪郭・単色を守る。
  let placeUi = null;

  function ensurePlaceUi() {
    if (placeUi) return placeUi;
    const root = document.createElement('div');
    root.className = 'place-ui';
    root.style.display = 'none';
    root.innerHTML = `
      <div class="pu-backdrop"></div>
      <div class="pu-panel" role="dialog" aria-modal="true" aria-label="CG 人形の立ち位置">
        <div class="pu-head">
          <b class="pu-title">📍 画面で人形を置く</b>
          <span class="pu-frameinfo"></span>
          <span class="spacer"></span>
          <button class="pu-close">✕ 閉じる</button>
        </div>
        <div class="pu-body">
          <div class="pu-viewcol">
            <div class="pu-canvas-wrap"><canvas class="pu-canvas" width="640" height="480"></canvas></div>
            <div class="pu-hint">映像の<b>床をクリック</b>すると、そこに人形が立ちます。
              足元の丸をドラッグで移動、<b>矢印の先</b>をドラッグで向きを回します。</div>
          </div>
          <div class="pu-side">
            <div class="pu-warn"></div>
            <div class="pu-grid">
              <label>X<input class="pu-x" type="number" step="0.05">m</label>
              <label>Z<input class="pu-z" type="number" step="0.05">m</label>
              <label>向き<input class="pu-yaw" type="number" step="5">°</label>
            </div>
            <div class="pu-issue"></div>
            <div class="pu-actions">
              <button class="pu-refresh" title="いまのライブ映像で静止フレームを取り直す">🔄 フレームを取り直す</button>
              <button class="pu-done accent">できた</button>
            </div>
            <div class="pu-key">重ねている線: <span class="k-floor">床・0.3m 格子</span>
              <span class="k-wall">壁</span> ／ <span class="k-proxy">人形の輪郭（足元・身長・向き）</span></div>
            <div class="pu-note">これは<b>輪郭だけ</b>です。見た目・影・腕の動きは Unity が描きます
              （卓が人形を実描画すると、実機と一致しているか確かめる手段が無くなるため）。</div>
          </div>
        </div>
      </div>`;
    document.body.appendChild(root);

    const pq = (sq) => root.querySelector(sq);
    const canvas = pq('.pu-canvas');
    const ctx = canvas.getContext('2d');
    const xI = pq('.pu-x'), zI = pq('.pu-z'), yawI = pq('.pu-yaw');
    const warnEl = pq('.pu-warn'), issueEl = pq('.pu-issue'), infoEl = pq('.pu-frameinfo');

    // 状態
    let step = null, take = null, stepIdx = 0;
    let frame = null;            // { canvas|null, w, h } — 映像が無くても w/h は決める（線だけで置ける）
    let res = { calib: null, source: 'none', note: '' };
    let place = { x: 0, z: 0, yawDeg: 0 };
    let heightM = DEFAULT_HEIGHT_M;
    let drag = '';               // '' | 'move' | 'yaw'
    let farNote = '';

    function grabFrame(camId) {
      const img = deps.getLiveImg ? deps.getLiveImg(camId) : null;
      if (img && img.naturalWidth && img.naturalHeight) {
        const cv = document.createElement('canvas');
        cv.width = img.naturalWidth; cv.height = img.naturalHeight;
        cv.getContext('2d').drawImage(img, 0, 0);
        return { canvas: cv, w: cv.width, h: cv.height };
      }
      return null;
    }

    /** 映像が来ていなくても置けるようにする（現場でカメラ 1 台だけ繋がっている状況は普通に起きる）。 */
    function resolveFrame(cam) {
      const live = grabFrame(cam ? cam.id : null);
      if (live) return live;
      const c = cam && cam.calib;
      return { canvas: null, w: (c && c.srcW > 1) ? c.srcW : 640, h: (c && c.srcH > 1) ? c.srcH : 480 };
    }

    function syncInputs() {
      xI.value = round2(place.x); zI.value = round2(place.z); yawI.value = Math.round(place.yawDeg);
    }

    function geometry() {
      return actorProxyGeometry(res.calib, place, heightM, { srcW: frame.w, srcH: frame.h });
    }

    function draw() {
      canvas.width = frame.w; canvas.height = frame.h;
      canvas.style.aspectRatio = `${frame.w} / ${frame.h}`;
      ctx.fillStyle = '#000'; ctx.fillRect(0, 0, frame.w, frame.h);
      if (frame.canvas) ctx.drawImage(frame.canvas, 0, 0);
      const s2 = Math.max(1, frame.w / 640);

      // 部屋のワイヤー（較正 UI と同じ線）。概算 pose のときは薄くする —
      // 「この線はだいたいの位置」と見て分かるようにするため。
      if (res.calib) {
        ctx.globalAlpha = res.source === 'calib' ? 1 : 0.45;
        for (const seg of wireSegments(deps.getLayout ? deps.getLayout() : null)) {
          const a = projectPoint(res.calib, seg.a[0], seg.a[1], seg.a[2]);
          const b = projectPoint(res.calib, seg.b[0], seg.b[1], seg.b[2]);
          if (!a || !b) continue;
          ctx.strokeStyle = seg.color || (seg.kind === 'wall' ? 'rgba(120,220,255,0.9)'
            : seg.kind === 'grid' ? 'rgba(255,222,173,0.3)' : 'rgba(255,222,173,0.9)');
          ctx.lineWidth = (seg.kind === 'grid' ? 1 : 2) * s2;
          ctx.beginPath(); ctx.moveTo(a.u, a.v); ctx.lineTo(b.u, b.v); ctx.stroke();
        }
        ctx.globalAlpha = 1;
      }

      const g = geometry();
      // 人型シルエットを重ねる（箱だけでは大きさ・向きの当たりが付かない）。
      const body = actorBodyGeometry(res.calib, place, heightM, { srcW: frame.w, srcH: frame.h });
      drawActorProxy(ctx, g, { scale: s2, body, label: actorLabel() });
      renderIssue(g);
    }

    function actorLabel() {
      const a = actorById(step ? step.cg : '');
      return a ? (a.name || a.id) : (step && step.cg ? step.cg : '人形');
    }

    function renderIssue(g) {
      const bits = [];
      const p = proxyIssueText(g, res.source);
      if (p) bits.push(p);
      if (farNote) bits.push(farNote);
      if (!frame.canvas) {
        bits.push('このカメラのライブ映像が来ていません（部屋の線と輪郭だけで置いています）。'
          + '実際の絵に合わせるには映像を繋いでから置き直してください。');
      }
      issueEl.textContent = bits.join(' / ');
      issueEl.className = 'pu-issue' + (bits.length ? ' on' : '');
    }

    // 画素 ↔ 表示の対応は**フレーム実寸**で持つ（表示サイズで持つとパネル幅が変わるだけで崩れる）。
    function toFramePx(ev) {
      const rect = canvas.getBoundingClientRect();
      if (!rect.width || !rect.height) return null;
      return {
        u: (ev.clientX - rect.left) / rect.width * frame.w,
        v: (ev.clientY - rect.top) / rect.height * frame.h,
        hit: 14 * frame.w / rect.width,
      };
    }

    /** クリック点を床へ落として立ち位置にする。部屋から遠すぎる解は「置けた」ことにしない。 */
    function moveTo(u, v) {
      const p = unprojectToFloor(res.calib, u, v);
      if (!p) {
        farNote = 'そこは床ではありません（地平線より上をクリックしています）';
        return false;
      }
      // 俯角の深いカメラでは画の上端でも床に当たる（遥か遠く）。数十 m 先へ人形を飛ばすと、
      // 画には点も出ず「押したのに何も起きない」に見えるので、理由を言って弾く。
      if (Math.abs(p.x) > 6 || Math.abs(p.z) > 6) {
        farNote = `そこは ${Math.max(Math.abs(p.x), Math.abs(p.z)).toFixed(1)}m 先の床です（部屋の外なので置きません）`;
        return false;
      }
      farNote = '';
      place.x = p.x; place.z = p.z;
      return true;
    }

    function aimTo(u, v) {
      const p = unprojectToFloor(res.calib, u, v);
      if (!p) return false;
      const dx = p.x - place.x, dz = p.z - place.z;
      if (Math.hypot(dx, dz) < 1e-4) return false;
      // yaw は course +Z が 0（Unity の Quaternion.Euler(0, yaw, 0) と同じ向き）。
      place.yawDeg = Math.atan2(dx, dz) * 180 / Math.PI;
      farNote = '';
      return true;
    }

    function commit(full) {
      if (!step) return;
      setPlacement(step, place);
      syncInputs();
      markDirty();
      if (full) { render(); if (take) renderStepRows(take); }
    }

    canvas.addEventListener('pointerdown', (ev) => {
      if (ev.button !== 0 || !res.calib) return;
      const tpx = toFramePx(ev);
      if (!tpx) return;
      canvas.setPointerCapture(ev.pointerId);
      const g = geometry();
      // 矢印の先を掴んでいれば回転、それ以外は移動（掴み判定は矢印を優先 —
      // 足元と矢印が重なる俯瞰では、回せないほうが困る）。
      const tip = g.arrow ? g.arrow.tip : null;
      drag = (tip && Math.hypot(tip.u - tpx.u, tip.v - tpx.v) <= tpx.hit) ? 'yaw' : 'move';
      if (drag === 'yaw') aimTo(tpx.u, tpx.v); else moveTo(tpx.u, tpx.v);
      commit(false); draw();
    });
    canvas.addEventListener('pointermove', (ev) => {
      if (!drag) return;
      const tpx = toFramePx(ev);
      if (!tpx) return;
      if (drag === 'yaw') aimTo(tpx.u, tpx.v); else moveTo(tpx.u, tpx.v);
      commit(false); draw();
    });
    const endDrag = () => { if (drag) { drag = ''; commit(true); } };
    canvas.addEventListener('pointerup', endDrag);
    canvas.addEventListener('pointercancel', endDrag);

    for (const el of [xI, zI, yawI]) {
      el.onchange = () => {
        place = { x: numOr(xI.value, place.x), z: numOr(zI.value, place.z), yawDeg: numOr(yawI.value, place.yawDeg) };
        farNote = '';
        commit(true); draw();
      };
    }
    pq('.pu-refresh').onclick = () => { openFor(step, take, stepIdx); };
    pq('.pu-done').onclick = () => close();
    pq('.pu-close').onclick = () => close();
    pq('.pu-backdrop').onclick = () => close();
    const onKey = (ev) => { if (ev.key === 'Escape' && root.style.display !== 'none') close(); };

    function openFor(s, t, i) {
      step = s; take = t; stepIdx = i;
      const camIdx = stepCameraIndex(s);
      const cam = cameras[camIdx] || null;
      frame = resolveFrame(cam);
      res = resolveProxyCalib(cam, frame.w, frame.h);
      place = placementOf(s);
      heightM = actorHeight(s.cg);
      drag = ''; farNote = '';
      pq('.pu-title').textContent = `📍 ${actorLabel()} を ${camLabel(camIdx)} の画で置く`;
      infoEl.textContent = `${frame.w}×${frame.h}`
        + (res.source === 'calib' ? ' / 🎯 較正済み' : res.source === 'pose' ? ' / 📐 概算 pose' : ' / 姿勢なし');
      warnEl.textContent = res.note || '';
      warnEl.className = 'pu-warn' + (res.note ? ' on' : '');
      syncInputs();
      root.style.display = '';
      document.addEventListener('keydown', onKey);
      draw();
    }
    function close() {
      root.style.display = 'none';
      document.removeEventListener('keydown', onKey);
      commit(true);
      step = null; take = null; frame = null; res = { calib: null, source: 'none', note: '' };
    }

    placeUi = { openFor };
    return placeUi;
  }

  // 関数宣言にしておく（const だと、初回 render がこの行より前に走った時に TDZ で落ちる）。
  function round2(v) { return Math.round(v * 100) / 100; }

  function openPlaceUi(t, s, idx) {
    // 「決めた位置」でないと押せない導線だが、念のためここでも実体化しておく
    // （押した直後に hasPlacement が false のままだと、閉じた時に立ち位置が消える）。
    s.cgMode = TAKE.CG_FIXED;
    ensurePlaceUi().openFor(s, t, idx);
  }

  // ---- 保存 -------------------------------------------------------------------
  // 周回の増減ボタンは廃止した（2026-08-02）。走り切る周数は show.json run.totalLaps だけが正で、
  // リボン側にも持たせると「卓に出ている周数 ≠ 実機が走る周数」を著作中に作れてしまう。
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
  // カメラ / 素材 / コース順は deps 側（app.js の state）が正。描画の前に必ず引き直す。
  function pullDeps(state) {
    cameras = deps.getCameras ? deps.getCameras() : ((state && state.cameras) || []);
    // 走り切る周数（run.totalLaps）。リボンはこれを読むだけで書かない。
    run = deps.getRun ? deps.getRun() : ((state && state.run) || null);
    cues = deps.getCues ? deps.getCues() : ((state && state.cues) || []);
    actors = deps.getActors ? deps.getActors() : ((state && state.actors) || []);
    record = deps.getRecord ? deps.getRecord() : ((state && state.record) || null);
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
    if (!dirty) { adoptState(state); resetUndo(); }
    if (sel && sel.camera >= cameras.length) { sel = null; closeCueEditor(); }
    // 周数を減らすと選択中の区間が画面から消えることがある（選択だけ残すと編集不能な状態になる）。
    if (sel && !isSegmentReachable(sel.lap, sel.camera, lapsOf(), rows())) { sel = null; closeCueEditor(); }
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
