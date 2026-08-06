// 廻リ視 web compositor — 1 ページ統合。
//   🎬 事前オーサリング = 演出・カットのリボン（ribbon.js）。カメラ列は監視専用に格下げ。
//   🚨 ライブ運用 = 発見 / 診断 / ラン状態 / cue 手動発火。
//   show.json が唯一の正。ビューは Unity ScreenComposite を WebGL で再現（composite-view.js）。
import {
  FX, FX_DEFAULT, FX_CCTV, blendCfg, camColor, escapeHtml,
  streamBase, getState, postState, postCommand,
  captures, refreshCaptures, createMediaCache, saveCueObject, deleteCueObject,
} from './common.js';
import { createCompositeView } from './composite-view.js';
import { createFloorMap } from './floormap.js';
import { createRibbon } from './ribbon.js';
import { normalizeTimelineV3 } from './timeline-model.js';
import { recordConfig, recordLaps, recordCoverage, missingRecordLaps, recordTailSec, REC_DEFAULT_TAIL_SEC } from './record-model.js';
import { createShowSim } from './show-sim.js';
import { createAtelier } from './atelier.js';
import { createActorsPanel } from './actors.js';
import { createAlignUi } from './align-ui.js';
import { calibBadgeText, lensesOf, lensTrustIssues } from './calib-session.js';
import { introConfig, introStageSec, introDurationLabel, introPreflightRow } from './intro-model.js';
// 体験の骨格（走り切る周数 ＋ もどりの区間）。既定値と「その区間を踏むか」の判定はここが単一の正。
// ⚠ 順路は app.js の courseOrder()（カメラ台数でクランプする方）を渡す — リボンが並べる順と
//   同じでなければ、卓の中で「踏む区間」の答えが 2 つできる。
import {
  RUN_DEFAULT, runConfig, totalLaps as runTotalLaps,
  isSegmentReachable, returnTakeTooLate,
} from './run-model.js';

const $ = (s) => document.querySelector(s);
const MW = 640, MH = 360;

// ---- サーバ I/O（カメラ push のデバウンス）----------------------------------
let pushTimer = 0;
function pushCamerasDebounced() {
  clearTimeout(pushTimer);
  pushTimer = setTimeout(() => postState({ cameras: state.cameras }), 140);
}

// ---- 全体グレーディング（show.json トップレベル post）------------------------
//   カメラ post を持たない列はここが実効値になる（列の「↺ 画質を初期化」の戻り先）。
//   ここを触る面が無いと、既定の見た目を現場で直せない（2026-07-26 追加）。
const gfxInputs = {}, gfxVals = {};
let gfxPushTimer = 0;
function gfxNote(m) {
  const el = $('#gfxMsg');
  if (!el) return;
  el.textContent = m;
  clearTimeout(gfxNote._t);
  gfxNote._t = setTimeout(() => { el.textContent = ''; }, 4000);
}
function readGlobalFx() {
  return Object.fromEntries(FX.map(([k, , , , , d]) => {
    const v = gfxInputs[k] ? parseFloat(gfxInputs[k].value) : NaN;
    return [k, Number.isFinite(v) ? v : d];
  }));
}
function buildGlobalFx() {
  const rows = $('#gfxRows');
  if (!rows) return;
  for (const [key, label, min, max, step] of FX) {
    const row = document.createElement('label');
    row.className = 'sld fx-row';
    row.append(`${label} `);
    const inp = document.createElement('input');
    inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step;
    const val = document.createElement('span');
    inp.oninput = () => {
      val.textContent = inp.value;
      if (state) state.post = readGlobalFx();
      clearTimeout(gfxPushTimer);
      gfxPushTimer = setTimeout(() => postState({ post: readGlobalFx() }), 140);
    };
    row.append(inp, val);
    rows.appendChild(row);
    gfxInputs[key] = inp; gfxVals[key] = val;
  }
  const apply = (post, msg) => {
    if (state) state.post = { ...post };
    postState({ post });
    syncGlobalFx();
    renderColumns();      // カメラ post を持たない列の表示も即追従させる
    gfxNote(msg);
  };
  if ($('#gfxPreset')) $('#gfxPreset').onclick = () => apply({ ...FX_CCTV }, '🎥 監視カメラ既定に戻しました');
  if ($('#gfxNeutral')) $('#gfxNeutral').onclick = () => apply({ ...FX_DEFAULT }, '◻ 加工なしに戻しました');
}
function syncGlobalFx() {
  for (const [key, , , , , d] of FX) {
    const inp = gfxInputs[key];
    if (!inp || document.activeElement === inp) continue;
    const v = state?.post?.[key] ?? d;
    inp.value = v; gfxVals[key].textContent = String(v);
  }
}

// ---- グローバル状態 ---------------------------------------------------------
let state = null;
let unityAlive = false;
let lastUnity = {};
const columns = new Map();   // camId -> column controller
let floorMap = null;
let actorsPanel = null;
let calibUi = null;   // 🎯 カメラ姿勢の較正パネル（カメラ列から開く全画面オーバーレイ）
let timeline = null;
let showSim = null;   // 🕹 ショーシミュレーション（フロアマップのドットで駆動）
let lastDiscovery = { devices: [], conflicts: [] };
// 区間 (lap,camera) の実測滞在時間（capture-server が heartbeat の dwell[] を集計したもの）。
// リボン UI が「進入 +20s の演出が平均 8s の区間に置かれている」を見せるために読む。
let dwellStats = { items: {}, updatedAt: 0 };
// 卓サーバ（capture-server.py）の生死。落ちても画面は最後の絵で生き続けるので明示的に見張る。
let serverAlive = false;
let lastServerOkAt = 0;
// ライブ中の画質スライダ誤操作ロック（ライブモードで既定 ON）。

// タイムライン / フロアマップの未保存編集（保存先が show.json = 消えると戻せない）。
function anyDirty() {
  const t = timeline && timeline.isDirty && timeline.isDirty();
  const f = floorMap && floorMap.isDirty && floorMap.isDirty();
  return { timeline: !!t, floorMap: !!f, any: !!(t || f) };
}

// ---- ステータスバー（サーバ ● / Unity ● の 2 灯 + 未保存バッジ）--------------
function renderStatus() {
  const sdot = $('#stServerDot'), sinfo = $('#stServerInfo');
  const dot = $('#stDot'), info = $('#stInfo'), sync = $('#stSync');
  if (sdot) {
    sdot.className = 'st-dot ' + (serverAlive ? 'on' : 'off');
    const ago = lastServerOkAt ? Math.round((Date.now() - lastServerOkAt) / 1000) : null;
    sinfo.textContent = serverAlive ? 'サーバ: 応答中'
      : (ago == null ? 'サーバ: 未応答' : `サーバ: 断（最終応答 ${ago}s前）`);
    sinfo.className = serverAlive ? '' : 'st-ng';
  }
  const bar = $('#serverBar');
  if (bar) bar.style.display = serverAlive ? 'none' : '';
  document.body.classList.toggle('server-down', !serverAlive);

  dot.className = 'st-dot ' + (unityAlive ? 'on' : 'off');
  const u = lastUnity;
  if (!unityAlive) { info.textContent = 'Unity: 未接続'; sync.textContent = ''; sync.className = 'st-sync'; }
  else {
    info.textContent = `Unity: ${u.activeCamera || '?'} / ${(u.recvFps || 0).toFixed(1)}fps`
      + (u.playingCue ? ` / 🎬 ${u.playingCue}` : '')
      + (u.cameraOverride ? ` / 🔒 ${u.cameraOverride}` : '')
      + (u.simulator ? '（仮想）' : '');
    if (state && typeof u.appliedRev === 'number') {
      sync.textContent = u.appliedRev >= state.rev ? '✓ 反映済み' : `⏳ 同期中 (${u.appliedRev}/${state.rev})`;
      sync.className = 'st-sync ' + (u.appliedRev >= state.rev ? 'ok' : 'wait');
    }
  }
  renderDirtyBadge();
}

// 未保存バッジ（ライブモードでも見えるようヘッダに置く）。
function renderDirtyBadge() {
  const el = $('#stDirty'); if (!el) return;
  const d = anyDirty();
  const names = [d.timeline && 'タイムライン', d.floorMap && 'フロアマップ'].filter(Boolean);
  el.textContent = d.any ? `● 未保存: ${names.join(' / ')}` : '';
  el.className = 'st-dirty' + (d.any ? ' on' : '');
}

// ---- ラッチ = show.json に永続し、次の体験者へ持ち越されると事故になる状態 ------
//   ⚠ 列挙はこの 1 関数だけが持つ。警告バー・本番前チェック・⛑ 全部解除・▶ ラン開始 が
//     同じ配列を消費するので、ラッチを増やしてもどこかで見落とす、が起きない
//     （2026-07-28 まで 3 箇所が独立に cameraOverride と activeCue の 2 つだけを見ていて、
//      素材スロットの束縛・発見 OFF・📌 手動固定はどこからも警告されなかった）。
//   sev: 'ng' = 体験を壊す（⛑ が解除する） / 'warn' = 意図的な設定かもしれない（警告のみ）
function latches() {
  const ctrl = (state && state.control) || {};
  const out = [];
  if (ctrl.cameraOverride) {
    out.push({ sev: 'ng', text: `🔒 カメラ ${ctrl.cameraOverride} に固定中（ゾーン自動切替が止まっています）` });
  }
  if (ctrl.activeCue) out.push({ sev: 'ng', text: `🎬 演出 ${ctrl.activeCue} が再生指定中` });
  const slots = (ctrl.slots || []).filter((s) => s && s.name && s.url);
  if (slots.length) {
    out.push({ sev: 'ng', slots, text: `🎞 素材スロット束縛中: ${slots.map((s) => s.name).join(' / ')}（前の体験者の素材が次の演出に出ます）` });
  }
  if (ctrl.discoveryEnabled === false) out.push({ sev: 'warn', text: '📡 端末発見が OFF（IP が変わっても追従しません）' });
  if (ctrl.autoFollow === false) out.push({ sev: 'warn', text: '📡 IP の自動追従が OFF' });
  const pinned = ((state && state.cameras) || []).filter((c) => c.pinned).map((c) => c.id);
  // 📌 は iPhone 等 discovery 非対応端末で意図的に使う。⛑ では外さない（外すと配信が切れる）。
  if (pinned.length) out.push({ sev: 'warn', text: `📌 手動固定: ${pinned.join(' / ')}（発見の自動追従は効きません）` });
  return out;
}

// ⛑ が解除する対象（体験を壊すものだけ）。▶ ラン開始 も同じ集合を消す。
async function clearBreakingLatches() {
  await postCommand({ type: 'setCameraOverride', camera: null });
  await postCommand({ type: 'stopCue' });
  // 走行中の演出も畳む（stopCue だけでは自動発火の演出に届かない。2026-07-28）。
  await postCommand({ type: 'abortTake' });
  // ⚠ ここで latches()（＝卓のメモリ state）を見ない。long-poll 遅延で古いことがあり、
  //    取りこぼすと「前の体験者の素材が次の演出に出る」という最悪の形で残る。必ず取り直す。
  const s = await getState();
  for (const sl of ((s && s.control && s.control.slots) || [])) {
    if (sl && sl.name) await postCommand({ type: 'bindSlot', name: sl.name, url: '' });
  }
}

function renderLatchBar() {
  const bar = $('#latchBar'); if (!bar) return;
  const ls = latches();
  const breaking = ls.filter((l) => l.sev === 'ng');
  bar.style.display = ls.length ? '' : 'none';
  bar.classList.toggle('warn-only', ls.length > 0 && breaking.length === 0);
  const t = $('#latchText');
  if (t) t.textContent = (breaking.length ? '⚠ ' : '注意 ') + ls.map((l) => l.text).join(' ／ ');
  const btn = $('#latchClearAll');
  if (btn) btn.style.display = breaking.length ? '' : 'none';
  const ctrl = (state && state.control) || {};
  const auto = $('#emgAuto');
  if (auto) auto.classList.toggle('armed', !!ctrl.cameraOverride);
  const stop = $('#emgStop');
  if (stop) stop.classList.toggle('armed', !!ctrl.activeCue);
}
if ($('#latchClearAll')) $('#latchClearAll').onclick = () => clearBreakingLatches();

// アクティブカメラ id（heartbeat の index 優先）
function activeCamId() {
  const cams = state?.cameras || [];
  if (typeof lastUnity.activeIndex === 'number' && lastUnity.activeIndex >= 0
      && lastUnity.activeIndex < cams.length) return cams[lastUnity.activeIndex].id;
  return null;
}

// ---- カメラ列の生成（監視専用: ① 生映像 / ④ Quest 実映像 / 接続 / 録画 / カメラ post）----
function buildColumn(cam, index) {
  const col = document.createElement('div');
  col.className = 'cam-col';
  // 縦フロー（下→上）: ① 生リアルタイム映像 → ③ 画像加工（カメラ post）→ ④ Quest 実映像。
  //   マスク・cue 編集はタイムラインのセグメントインスペクタへ移設。ここは監視のみ。
  col.innerHTML = `
    <div class="col-head">
      <b class="col-name">カメラ ${cam.id}</b>
      <select class="col-role" title="ゾーン = 周回の担当カメラ（フロアマップで塗る）。演出専用 = どのゾーンにも割り当てず、演出のカットからだけ映すカメラ（スタッフの A 巡回にも出ない）">
        <option value="zone">ゾーン</option>
        <option value="fx">演出専用</option>
      </select>
      <span class="col-src"></span>
      <span class="col-status">接続中…</span>
      <button class="col-switch" title="Quest の表示をこのカメラに固定する（ゾーン自動切替は止まる。戻すのは上部の警告バー ⛑ か 🚨 ライブ運用の 🚶）">📺 切替</button>
    </div>

    <div class="col-sec" data-col-role="monitor">
      <div class="sec-label">④ メタクエストで見えている映像（最終 = 加工 + 合成）</div>
      <div class="cap-bar">
        <button class="cap-btn cap-view" title="この最終映像を 1 枚 recordings/ に保存">📷 1枚</button>
        <button class="rec-btn rec-view" title="最終映像を録画（recordings/ へ）">⏺ 録画</button>
        <span class="spacer"></span>
        <span class="col-cue-mini"></span>
      </div>
      <div class="view-wrap"><canvas class="view-canvas" width="${MW}" height="${MH}"></canvas></div>
    </div>

    <div class="col-sec" data-col-role="grade">
      <div class="sec-label">③ 画像加工（カメラ単位の画質グレーディング = cameras[i].post）</div>
      <div class="fx-rows"></div>
      <div class="row-btns">
        <button class="fx-reset">↺ 画質を初期化</button>
        <button class="fx-copy" title="この列の画質を他のカメラにもコピーする（3 台の見えを揃える）">⇥ 他カメラにも適用</button>
        <span class="fx-msg"></span>
      </div>
    </div>

    <div class="col-sec" data-col-role="setup">
      <div class="sec-label">📐 カメラ姿勢（CG 人形を立てる視点・course 空間）</div>
      <div class="pose-row">
        <label>X<input class="pose-f pose-x" type="number" step="0.05"></label>
        <label>Z<input class="pose-f pose-z" type="number" step="0.05"></label>
        <label>高さ<input class="pose-f pose-y" type="number" step="0.05"></label>
        <label>向き°<input class="pose-f pose-yaw" type="number" step="5"></label>
        <label>俯角°<input class="pose-f pose-pitch" type="number" step="1" title="下向きが負"></label>
        <label>水平画角°<input class="pose-f pose-fov" type="number" step="1" title="レンズの横方向の画角。フロアマップの扇と同じ軸（縦ではない）"></label>
        <button class="pose-clear" title="姿勢を未設定に戻す（このカメラでは CG 人形が出なくなる）">✕</button>
      </div>
      <div class="pose-row calib-row">
        <button class="calib-open" title="実映像に部屋のワイヤーと人形を重ねて、見ながら手でカメラを合わせる。合わせた値は cameras[].calib に入り、実機の合成に使われる">🎯 カメラを合わせる</button>
        <span class="calib-badge"></span>
      </div>
      <div class="pose-hint">未設定のカメラでは CG 人形を出しません（当てずっぽうのパースで出す方が体験を壊すため）。フロアマップの 📐 モードでドラッグしても置けます。<b>🎯 で較正すると、この概算より較正の方が使われます</b>（数値は残るので較正を捨てれば戻ります）。</div>
    </div>

    <div class="col-sec" data-col-role="monitor">
      <div class="sec-label">① 生リアルタイム映像（加工前）</div>
      <div class="cap-bar">
        <button class="cap-btn cap-raw" title="生フレームを 1 枚 recordings/ に保存">📷 1枚</button>
        <button class="rec-btn rec-raw" title="生フレームを録画（recordings/ へ）">⏺ 録画</button>
      </div>
      <div class="view-wrap"><img class="raw-live" alt=""></div>
    </div>

    <div class="col-sec" data-col-role="setup">
      <div class="sec-label">配信元（この列がどの端末を掴んでいるか）</div>
      <div class="conn-row">
        <span class="conn-disc" title="LAN 上で発見されたこのカメラ ID の端末（読み取り専用・自動追従）">📡 …</span>
        <span class="conn-pin"></span>
        <button class="pin-clear" style="display:none" title="手動固定を解除して発見の自動追従に戻す">📌 固定を解除</button>
      </div>
      <details class="conn-manual">
        <summary>🚨 緊急: 手動接続</summary>
        <div class="ip-row">
          <input class="ip-host" placeholder="スマホ IP" title="配信スマホの IP">
          <input class="ip-port" placeholder="port" title="streamer=8080 / IP Camera Lite=8081">
          <input class="ip-auth" placeholder="user:pass" title="Basic 認証（空=なし）">
        </div>
        <div class="conn-manual-hint">discovery 非対応端末（iPhone 等）や障害時の最終手段。保存すると 📌 手動固定になり自動追従を止めます。</div>
      </details>
    </div>`;

  const q = (s) => col.querySelector(s);
  const refs = {
    el: col, cam, index, id: cam.id,
    statusEl: q('.col-status'), srcSpan: q('.col-src'),
    hostI: q('.ip-host'), portI: q('.ip-port'), authI: q('.ip-auth'),
    fxInputs: {}, fxVals: {},
    liveImg: q('.raw-live'),
    retry: 0, streamKey: '',
    media: createMediaCache(),   // アクティブ cue の素材/マスク解決用
  };

  // ===== 画質スライダー（cameras[i].post）=====
  //   スライダは常に「実効値」（カメラ post > 全体 post > FX 既定）を表示する。読み出しもそこから。
  const readFx = () => Object.fromEntries(FX.map(([k, , , , , d]) => {
    const inp = refs.fxInputs[k];
    const v = inp ? parseFloat(inp.value) : NaN;
    return [k, Number.isFinite(v) ? v : (state?.post?.[k] ?? d)];
  }));
  const fxRows = q('.fx-rows');
  for (const [key, label, min, max, step] of FX) {
    const row = document.createElement('label');
    row.className = 'sld fx-row';
    row.append(label + ' ');
    const inp = document.createElement('input');
    inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step;
    const val = document.createElement('span');
    inp.oninput = () => {
      // ⚠ 1 本目のスライダを触った瞬間に**カメラ post が生まれる**。その種を FX 既定（露出 0 / コントラスト 1）
      //   にすると、画面に出ている値（＝全体グレーディング）を捨てて既定へ飛ぶ — 「露出を動かしたら
      //   コントラストまで動いた」「どう調整しても初期化時の絵に戻せない」の正体（2026-07-26 修正）。
      //   種は**いま画面に出ている値そのもの**にする（表示 = 実データ）。
      if (!refs.cam.post) refs.cam.post = readFx();
      refs.cam.post[key] = parseFloat(inp.value);
      val.textContent = inp.value;
      pushCamerasDebounced();
    };
    row.append(inp, val);
    fxRows.appendChild(row);
    refs.fxInputs[key] = inp; refs.fxVals[key] = val;
  }
  const fxMsg = q('.fx-msg');
  const fxNote = (m) => { if (!fxMsg) return; fxMsg.textContent = m; clearTimeout(refs.fxMsgTimer); refs.fxMsgTimer = setTimeout(() => { fxMsg.textContent = ''; }, 4000); };
  q('.fx-reset').onclick = () => { delete refs.cam.post; postState({ cameras: state.cameras }); };
  // 3 台の見えを揃える導線。1 台で追い込んだ値を他へ配る（1 スライダずつ真似るのは非現実的）。
  // この列が「初期化済み（post 無し = 全体グレーディングのまま）」なら、他も初期化に揃える。
  q('.fx-copy').onclick = () => {
    const others = (state?.cameras || []).filter((c) => c.id !== refs.cam.id);
    if (!others.length) return fxNote('他のカメラがいません');
    // 「この列と同じ見え方にする」なので、配るのは**画面に出ている実効値**（部分的な post でも欠けない）。
    const src = refs.cam.post ? readFx() : null;
    for (const c of others) { if (src) c.post = { ...src }; else delete c.post; }
    postState({ cameras: state.cameras });
    renderColumns();   // 他列のスライダを即座に追従させる（long-poll を待たない）
    fxNote(src ? `→ ${others.map((c) => c.id).join(' / ')} に適用` : `→ ${others.map((c) => c.id).join(' / ')} も初期化`);
  };

  // ===== 接続表示（発見ベース）+ 緊急手動接続 =====
  const connDisc = q('.conn-disc'), connPin = q('.conn-pin'), pinClear = q('.pin-clear');
  refs.syncPin = () => {
    const pinned = !!refs.cam.pinned;
    connPin.textContent = pinned ? `📌 手動固定 ${refs.cam.host || ''}:${refs.cam.port || 8080}` : '';
    connPin.className = 'conn-pin' + (pinned ? ' on' : '');
    pinClear.style.display = pinned ? '' : 'none';
  };
  refs.applyDiscovery = (disc) => {
    if (!connDisc) return;
    const devices = (disc && disc.devices) || [];
    const conflicts = new Set((disc && disc.conflicts) || []);
    const id = refs.cam.id;
    // 同一性照合（PC → /info）の不一致が最優先。HTTP は 200・映像も流れるので
    // 「LIVE なのに別スロットの端末」を掴んでいる状態はここでしか出せない。
    const ident = disc && disc.identity && disc.identity[id];
    if (ident && ident.state === 'mismatch') {
      connDisc.textContent = `⚠ 別端末に接続中 — ${ident.detail}`;
      connDisc.className = 'conn-disc conflict';
      return;
    }
    if (conflicts.has(id)) {
      connDisc.textContent = `⚠ 二重 ID（${id}）— 発見が曖昧なため自動追従停止`;
      connDisc.className = 'conn-disc conflict';
      return;
    }
    const dev = devices.find((d) => d.role === 'camera' && d.id === id);
    if (dev) {
      connDisc.textContent = `📡 ${dev.ip}:${dev.port}${dev.version ? ` (v${dev.version}` : ' ('}・${fmtAge(dev.ageSec)})`;
      connDisc.className = 'conn-disc ok';
    } else {
      connDisc.textContent = '⚠ 未発見';
      connDisc.className = 'conn-disc none';
    }
  };
  const onIp = () => {
    refs.cam.host = refs.hostI.value.trim();
    refs.cam.port = parseInt(refs.portI.value, 10) || 8080;
    refs.cam.auth = refs.authI.value.trim();
    refs.cam.pinned = true;
    postState({ cameras: state.cameras });
    refs.syncPin();
    connectLive();
  };
  refs.hostI.onchange = refs.portI.onchange = refs.authI.onchange = onIp;
  pinClear.onclick = () => {
    refs.cam.pinned = false;
    postState({ cameras: state.cameras });
    refs.syncPin();
  };
  refs.syncPin();
  refs.applyDiscovery(lastDiscovery);

  // ===== ライブ受信 =====
  refs.liveImg.crossOrigin = 'anonymous';
  function connectLive() {
    const c = refs.cam;
    refs.liveOk = false;
    refs.statusEl.textContent = '接続中…'; refs.statusEl.className = 'col-status';
    refs.liveImg.src = `${streamBase()}/cam?host=${encodeURIComponent(c.host || '')}`
      + `&port=${c.port || 8080}&path=/video`
      + (c.auth ? `&auth=${encodeURIComponent(c.auth)}` : '') + `&t=${Date.now()}`;
  }
  refs.liveOk = false;   // 本番前チェックが読む「この列に実映像が来ているか」（鮮度は下の applyLiveness が正）
  refs.liveImg.addEventListener('load', () => {
    refs.applyAspect && refs.applyAspect();
    // 実寸が分かって初めて「較正はこの解像度用か」を判定できる（px 焦点距離は解像度に従属）。
    refs.syncCalib && refs.syncCalib();
  });
  refs.liveImg.addEventListener('error', () => {
    refs.statusEl.textContent = '✕ 切断 — 再試行'; refs.statusEl.className = 'col-status ng';
    refs.liveOk = false;
    clearTimeout(refs.retry); refs.retry = setTimeout(connectLive, 5000);
  });
  refs.connectLive = connectLive;

  // 卓サーバが測った鮮度を列の表示へ落とす。「接続した」ではなく「いまバイトが来ている」を ● LIVE の意味にする。
  //   ⚠ フレームが途絶えても <img> は最後の絵を出したまま = 画面は嘘をつく。ここで必ず言う。
  refs.applyLiveness = (e) => {
    if (!e) {                       // プロキシがこの配信元をまだ開いていない
      refs.liveOk = false;
      if (refs.cam.host) { refs.statusEl.textContent = '接続中…'; refs.statusEl.className = 'col-status'; }
      else { refs.statusEl.textContent = '配信元 未設定'; refs.statusEl.className = 'col-status ng'; }
      return;
    }
    if (e.ageMs == null) {          // 開いてはいるが 1 バイトも来ていない
      refs.liveOk = false;
      refs.statusEl.textContent = e.openMs > 4000 ? '✕ 応答なし' : '接続中…';
      refs.statusEl.className = e.openMs > 4000 ? 'col-status ng' : 'col-status';
      return;
    }
    const fresh = e.ageMs < LIVE_STALE_MS;
    refs.liveOk = fresh;
    refs.statusEl.textContent = fresh ? '● LIVE' : `⏸ 停止 ${Math.round(e.ageMs / 1000)}s`;
    refs.statusEl.className = fresh ? 'col-status ok' : 'col-status ng';
  };

  q('.col-switch').onclick = () => postCommand({ type: 'setCameraOverride', camera: refs.cam.id });

  // ===== 役割（ゾーン / 演出専用）=====
  const roleSel = q('.col-role');
  refs.syncRole = () => {
    const fx = refs.cam.role === 'fx';
    roleSel.value = fx ? 'fx' : 'zone';
    refs.el.classList.toggle('fx-cam', fx);
  };
  roleSel.onchange = () => {
    refs.cam.role = roleSel.value === 'fx' ? 'fx' : 'zone';
    postState({ cameras: state.cameras });
    refs.syncRole();
    // フロアマップのパレット（塗れるカメラ）から即座に外す / 戻す。
    window.dispatchEvent(new CustomEvent('fc-cameras-changed'));
  };
  refs.syncRole();

  // ===== カメラ姿勢（CG レイヤの仮想カメラ）=====
  //   著作するまで pose キー自体を置かない（Unity は pose の有無で hasPose を決める）。
  //   画角は **水平**（hfovDeg）が正 — この卓の扇も、レンズのスペックも水平だから。
  const POSE_DEFAULT = { x: 0, z: 0, y: 1.2, yawDeg: 0, pitchDeg: 0, hfovDeg: 70 };
  const poseF = {
    x: q('.pose-x'), z: q('.pose-z'), y: q('.pose-y'),
    yawDeg: q('.pose-yaw'), pitchDeg: q('.pose-pitch'), hfovDeg: q('.pose-fov'),
  };
  // 旧 `fovDeg` からの移行。Unity 側はこれを**垂直**画角として使い、この卓は**水平**の扇で描いていて
  // 実効画角が約 25% 食い違っていた（2026-07-27 監査 HIGH 3）。4:3 前提で垂直→水平へ直し、
  // 旧キーは落とす（Unity はもう読まない）。
  const vfovToHfov = (v) =>
    Math.round(2 * Math.atan(Math.tan(Math.max(1, Math.min(179, v)) * Math.PI / 360) * 4 / 3) * 1800 / Math.PI) / 10;
  const migratePose = (p) => {
    if (!p || typeof p !== 'object') return false;
    let changed = false;
    if (!(p.hfovDeg > 0) && p.fovDeg > 0) { p.hfovDeg = vfovToHfov(p.fovDeg); changed = true; }
    if ('fovDeg' in p) { delete p.fovDeg; changed = true; }
    return changed;
  };
  refs.syncPose = () => {
    const p = refs.cam.pose;
    if (migratePose(p)) postState({ cameras: state.cameras });
    for (const [k, inp] of Object.entries(poseF)) {
      inp.value = p ? (p[k] ?? POSE_DEFAULT[k]) : '';
      inp.placeholder = String(POSE_DEFAULT[k]);
    }
    q('.pose-clear').style.display = p ? '' : 'none';
  };
  const commitPose = () => {
    // 1 個でも触れたら pose を実体化する（残りは既定値で埋める＝画面の見えと一致）。
    const p = refs.cam.pose ? { ...refs.cam.pose } : { ...POSE_DEFAULT };
    for (const [k, inp] of Object.entries(poseF)) {
      const v = parseFloat(inp.value);
      if (Number.isFinite(v)) p[k] = v;
    }
    refs.cam.pose = p;
    postState({ cameras: state.cameras });
    refs.syncPose();
    window.dispatchEvent(new CustomEvent('fc-cameras-changed'));
  };
  for (const inp of Object.values(poseF)) inp.onchange = commitPose;
  q('.pose-clear').onclick = () => {
    delete refs.cam.pose;
    postState({ cameras: state.cameras });
    refs.syncPose();
    window.dispatchEvent(new CustomEvent('fc-cameras-changed'));
  };
  refs.syncPose();

  // ===== 🎯 較正（実測の解 = cameras[i].calib）=====
  //   pose（人が置く概算）とは別枠。較正があれば Unity はそちらを使うので、
  //   「較正済みかどうか」と「いまの映像と合っているか（解像度）」をここで常に見せる。
  const calibBadge = q('.calib-badge');
  refs.syncCalib = () => {
    const txt = calibBadgeText(refs.cam, refs.liveImg.naturalWidth, refs.liveImg.naturalHeight);
    calibBadge.textContent = txt || '未較正（📐 の概算を使用）';
    calibBadge.className = 'calib-badge' + (txt ? (txt.includes('⚠') ? ' warn' : ' ok') : '');
  };
  q('.calib-open').onclick = () => calibUi && calibUi.open(refs.cam.id);
  refs.syncCalib();

  // ===== ビュー/生映像の縦横比をカメラ実寸に合わせる（黒レターボックス背景を出さない）=====
  const viewCanvas = q('.view-canvas');
  refs.applyAspect = () => {
    const w = refs.liveImg.naturalWidth, h = refs.liveImg.naturalHeight;
    if (!w || !h) return;
    const a = w / h;
    const aStr = a.toFixed(4);
    if (refs._aspect === aStr) return;
    refs._aspect = aStr;
    viewCanvas.style.aspectRatio = aStr;
    refs.liveImg.style.aspectRatio = aStr;
    viewCanvas.width = Math.max(2, Math.round(360 * a));
    viewCanvas.height = 360;
  };

  // ===== 📷 キャプチャ / ⏺ 録画（① 生 / ④ 合成済み）=====
  const rawTag = () => refs.cam.id;
  const viewTag = () => `${refs.cam.id}_quest`;
  const rawCanvas = document.createElement('canvas');
  const rawDraw = () => {
    const img = refs.liveImg;
    if (!img.naturalWidth) return false;
    if (rawCanvas.width !== img.naturalWidth) { rawCanvas.width = img.naturalWidth; rawCanvas.height = img.naturalHeight; }
    rawCanvas.getContext('2d').drawImage(img, 0, 0, rawCanvas.width, rawCanvas.height);
    return true;
  };
  const ed = (m, cls = '') => { const e = q('.col-cue-mini'); if (e) { e.textContent = m; e.className = 'col-cue-mini ' + cls; } };
  async function saveStill(canvas, tag, label) {
    if (!canvas) return ed('映像が無い', 'err');
    canvas.toBlob(async (blob) => {
      if (!blob) return ed('キャプチャ不可', 'err');
      const r = await (await fetch(`/save?type=image&to=recordings&cam=${tag}`,
        { method: 'POST', body: blob })).json();
      ed(r.ok ? `📷 ${r.name}` : '保存失敗', r.ok ? 'ok' : 'err');
      // 素材工房の種フレーム候補へ即反映（モードを往復させない・リロードさせない）。
      if (r.ok && atelier) atelier.notifyFrameSaved(refs.cam.id, r);
    }, 'image/jpeg', 0.95);
  }
  let mediaRec = null, recChunks = [], rawTimer = 0;
  const recRawBtn = q('.rec-raw'), recViewBtn = q('.rec-view');
  refs.recording = null;
  function updateColRecUI() {
    recRawBtn.textContent = refs.recording === 'raw' ? '⏹ 停止' : '⏺ 録画';
    recRawBtn.classList.toggle('on', refs.recording === 'raw');
    recViewBtn.textContent = refs.recording === 'view' ? '⏹ 停止' : '⏺ 録画';
    recViewBtn.classList.toggle('on', refs.recording === 'view');
  }
  function colStartRecord(kind) {
    if (refs.recording) return false;
    const isView = kind === 'view';
    const streamCanvas = isView ? viewCanvas : rawCanvas;
    if (isView) { if (!streamCanvas) { ed('映像が無い', 'err'); return false; } }
    else if (!rawDraw()) { ed('生映像が無い', 'err'); return false; }
    const tag = isView ? viewTag() : rawTag();
    const label = isView ? 'Quest最終' : '生';
    clearInterval(rawTimer);
    if (!isView) rawTimer = setInterval(rawDraw, 33);
    let stream;
    try { stream = streamCanvas.captureStream(30); }
    catch (e) { clearInterval(rawTimer); ed('録画不可: ' + e.message, 'err'); return false; }
    // 合成ビューは画面外だと描画を止める（省 GPU）。録画中はコマが凍るので回し続けさせる。
    if (isView && refs.view && refs.view.setForced) refs.view.setForced(true);
    recChunks = [];
    const mime = (window.MediaRecorder && MediaRecorder.isTypeSupported('video/webm;codecs=vp9'))
      ? 'video/webm;codecs=vp9' : 'video/webm';
    mediaRec = new MediaRecorder(stream, { mimeType: mime });
    mediaRec.ondataavailable = (e) => { if (e.data && e.data.size) recChunks.push(e.data); };
    mediaRec.onstop = async () => {
      clearInterval(rawTimer);
      if (refs.view && refs.view.setForced) refs.view.setForced(false);
      refs.recording = null; updateColRecUI(); renderGlobalRecState();
      const blob = new Blob(recChunks, { type: 'video/webm' });
      const r = await (await fetch(`/save?type=video&to=recordings&cam=${tag}`,
        { method: 'POST', body: blob })).json();
      ed(r.ok ? `⏹ ${label}: ${r.name}（${Math.round(r.size / 1024)}KB）` : '録画保存失敗', r.ok ? 'ok' : 'err');
    };
    mediaRec.start();
    refs.recording = kind; updateColRecUI();
    ed(`● 録画中（${label}）…`, 'ok');
    return true;
  }
  function colStopRecord() {
    if (mediaRec && mediaRec.state === 'recording') { mediaRec.stop(); return true; }
    return false;
  }
  refs.startRecord = colStartRecord;
  refs.stopRecord = colStopRecord;
  refs.isRecording = () => !!refs.recording;
  q('.cap-raw').onclick = () => saveStill(rawDraw() ? rawCanvas : null, rawTag(), '生');
  recRawBtn.onclick = () => { if (refs.recording === 'raw') colStopRecord(); else if (!refs.recording) colStartRecord('raw'); renderGlobalRecState(); };
  q('.cap-view').onclick = () => {
    if (refs.view && refs.view.renderOnce) refs.view.renderOnce();  // 画面外でも最新の 1 枚にしてから撮る
    saveStill(viewCanvas, viewTag(), 'Quest最終');
  };
  recViewBtn.onclick = () => { if (refs.recording === 'view') colStopRecord(); else if (!refs.recording) colStartRecord('view'); renderGlobalRecState(); };

  // ===== WebGL ビュー（ScreenComposite 再現）: アクティブ cue を overlay =====
  refs.view = createCompositeView(viewCanvas, {
    sample: () => {
      const cam2 = refs.cam;
      const activeCueId = state?.control?.activeCue;
      const cue = activeCueId
        ? (state?.cues || []).find((c) => c.id === activeCueId && (c.camera || '') === cam2.id)
        : null;
      let overlayEl = null, overlayReady = false, overlayW = 0, overlayH = 0, maskEl = null;
      let strength = 1, fadeSec = 0.5, trimEnd = 0;
      if (cue && cue.sourceUrl) {
        const m = refs.media.get(cue.sourceUrl);
        if (m && m.el) {
          overlayEl = m.el; overlayReady = m.ready;
          if (m.isVideo && m.ready && m.el.paused) { try { m.el.play().catch(() => {}); } catch { /* noop */ } }
          overlayW = m.isVideo ? (m.el.videoWidth || 0) : (m.el.naturalWidth || 0);
          overlayH = m.isVideo ? (m.el.videoHeight || 0) : (m.el.naturalHeight || 0);
        }
        maskEl = cue.maskUrl ? refs.media.getImage(cue.maskUrl) : null; // null => 全面差し替え
        strength = cue.strength ?? 1; fadeSec = cue.fadeIn ?? 0.5; trimEnd = cue.trimEnd || 0;
      }
      return {
        liveImg: refs.liveImg,
        overlayEl, overlayReady, overlayW, overlayH, maskEl,
        overlayOn: !!cue, fadeSec, strength, feather: 0,
        // カメラ post は**層まるごと**の上書き（実機 ApplyPostForActive と同じ）。
        // 欠けたキーは FX 既定で埋める（undefined をシェーダへ渡さない）。
        post: refs.cam.post ? { ...FX_DEFAULT, ...refs.cam.post } : (state?.post || FX_DEFAULT),
        trimEnd,
        onVideoEnd: () => { if (state?.control?.activeCue) postCommand({ type: 'stopCue' }); },
      };
    },
  });

  connectLive();
  return refs;
}

// ---- 列の状態同期（IP / FX / active / 表示中）--------------------------------
function syncColumn(refs, cam) {
  refs.cam = cam;
  refs.srcSpan.textContent = cam.sourceId || '';
  const setV = (el, v) => { if (document.activeElement !== el) el.value = v; };
  setV(refs.hostI, cam.host || '');
  setV(refs.portI, cam.port || 8080);
  setV(refs.authI, cam.auth || '');
  refs.syncPin && refs.syncPin();
  refs.syncCalib && refs.syncCalib();
  // 表示は描画と同じ解決（カメラ post があれば**層まるごと**それ・無ければ全体 post）。
  // キー単位で全体へフォールバックすると「表示 ≠ 実際の絵」になる。
  const hasPost = !!cam.post;
  for (const [key, , , , , d] of FX) {
    const inp = refs.fxInputs[key];
    if (document.activeElement === inp) continue;
    const v = hasPost ? (cam.post[key] ?? d) : (state?.post?.[key] ?? d);
    inp.value = v; refs.fxVals[key].textContent = String(v);
  }
  const key = `${cam.host || ''}|${cam.port || 8080}|${cam.auth || ''}`;
  if (key !== refs.streamKey) { refs.streamKey = key; refs.connectLive(); }

  const activeId = activeCamId();
  refs.el.classList.toggle('active', unityAlive && activeId === cam.id);
  const sw = refs.el.querySelector('.col-switch');
  const isShown = unityAlive && activeId === cam.id;
  sw.textContent = isShown ? '● 表示中' : '📺 切替';
  sw.classList.toggle('on', isShown);
}

// ---- カメラ列の再構築（カメラ集合が変わった時のみ）--------------------------
function renderColumns() {
  const wrap = $('#cams');
  const cams = state?.cameras || [];
  const ids = cams.map((c) => c.id).join(',');
  if (wrap.dataset.ids !== ids) {
    wrap.dataset.ids = ids;
    for (const c of columns.values()) { c.view && c.view.destroy(); c.media && c.media.dispose(); c.el.remove(); }
    columns.clear();
    cams.forEach((cam, i) => {
      const refs = buildColumn(cam, i);
      columns.set(cam.id, refs);
      wrap.appendChild(refs.el);
    });
  }
  cams.forEach((cam) => { const r = columns.get(cam.id); if (r) syncColumn(r, cam); });
}

// ---- ライブ運用: cue 手動発火 ------------------------------------------------
function renderLiveCuePanel() {
  const sel = $('#liveCueSelect'); if (!sel) return;
  const cuesList = state?.cues || [];
  const cur = sel.value;
  sel.innerHTML = '<option value="">（cue を選択）</option>';
  const byCam = {};
  for (const c of cuesList) (byCam[c.camera || '?'] = byCam[c.camera || '?'] || []).push(c);
  for (const camId of Object.keys(byCam).sort()) {
    const og = document.createElement('optgroup'); og.label = `カメラ ${camId}`;
    for (const c of byCam[camId]) {
      const o = document.createElement('option'); o.value = c.id; o.textContent = c.name || c.id;
      og.appendChild(o);
    }
    sel.appendChild(og);
  }
  sel.value = cur;
  const st = $('#liveCueState');
  if (st) {
    const a = state?.control?.activeCue;
    st.textContent = a ? `🎬 再生中: ${a}` : '停止中';
    st.className = 'ed-status' + (a ? ' ok' : '');
  }
}
if ($('#liveCueFire')) {
  $('#liveCueFire').onclick = () => {
    const id = $('#liveCueSelect').value;
    const st = $('#liveCueState');
    if (!id) { if (st) st.textContent = 'cue を選んでください'; return; }
    const exists = (state?.cues || []).some((c) => c.id === id);
    if (!exists) { if (st) { st.textContent = 'この cue は存在しません（保存を確認）'; st.className = 'ed-status err'; } return; }
    postCommand({ type: 'playCue', id });
  };
}
if ($('#liveCueStop')) $('#liveCueStop').onclick = () => postCommand({ type: 'stopCue' });

// ---- ポーリング -------------------------------------------------------------
async function pollState() {
  let rev = -1;
  for (;;) {
    try {
      const s = await (await fetch(`/state?rev=${rev}`)).json();
      if (s.rev !== rev) {
        rev = s.rev; state = s;
        renderColumns(); syncGlobalFx(); renderStatus(); renderLiveCuePanel(); renderLatchBar();
        floorMap && floorMap.onState(s); timeline && timeline.onState(s); showSim && showSim.onState(s);
        actorsPanel && actorsPanel.onState(s); calibUi && calibUi.onState(s);
        atelier && atelier.render();   // カメラ集合の変化を工房の列へ（署名一致なら no-op）
        renderBgmSection(); renderRecordPanel(); renderRunPanel(); renderRunCfg(s); renderFeelCfg(s); renderPreflight();
      }
    } catch { await new Promise((r) => setTimeout(r, 2000)); }
  }
}
// 実測滞在時間の集計を引き直す（更新があった時だけタイムラインへ通知して描き直す）。
//   サーバ未再起動（このエンドポイントが無い版）なら 404 → 静かに何もしない。
async function pollDwell() {
  try {
    const r = await fetch('/dwell/stats');
    if (!r.ok) return;
    const j = await r.json();
    if (!j || typeof j !== 'object' || !j.items) return;
    if (j.updatedAt === dwellStats.updatedAt) return;
    dwellStats = { items: j.items, updatedAt: j.updatedAt || 0 };
    timeline && timeline.onDwell && timeline.onDwell(dwellStats);
  } catch { /* サーバ断は pollUnity 側が検出する */ }
}

// ---- 受信の鮮度（● LIVE の意味を「接続した」から「いまバイトが来ている」へ）------------
//   卓サーバの /cam プロキシだけが upstream を実際に読んでいるので、そこで測った鮮度を正とする。
//   ブラウザ側の <img> load/error は当てにならない（upstream 断はプロキシが握り潰し、
//   multipart の各フレームで load が飛ぶかは実装依存 — 実測で飛ばない環境があった）。
const LIVE_STALE_MS = 2500;
let lastLiveness = {};
async function pollLiveness() {
  try {
    const r = await fetch('/cam/liveness');
    if (!r.ok) return;
    const j = await r.json();
    lastLiveness = (j && j.cams) || {};
  } catch { lastLiveness = {}; }   // サーバ断。列は下で「接続中…」へ落ちる
  for (const refs of columns.values()) {
    const c = refs.cam;
    refs.applyLiveness && refs.applyLiveness(lastLiveness[`${c.host || ''}:${c.port || 8080}`]);
  }
}

async function pollUnity() {
  for (;;) {
    // このポーリング（2s）が卓サーバ生存の一次判定。/state は long-poll で最大 25s
    // ブロックするため、サーバ断の検知には使えない。
    try {
      const s = await (await fetch('/unity/status')).json();
      serverAlive = true; lastServerOkAt = Date.now();
      unityAlive = !!s.alive; lastUnity = s.status || {};
      await pollDwell();
    } catch { serverAlive = false; unityAlive = false; lastUnity = {}; }
    await pollLiveness();
    renderStatus();
    renderRunPanel();
    renderLatchBar();
    renderPreflight();
    floorMap && floorMap.onUnity(unityAlive, lastUnity);
    for (const r of columns.values()) {
      const aid = activeCamId();
      r.el.classList.toggle('active', unityAlive && aid === r.id);
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
}

// ---- 全カメラ同時録画（ヘッダ）---------------------------------------------
let globalRecKind = null;
function renderGlobalRecState() {
  const anyRec = [...columns.values()].some((c) => c.isRecording && c.isRecording());
  if (!anyRec) globalRecKind = null;
  const rawBtn = $('#recAllRaw'), viewBtn = $('#recAllView');
  if (rawBtn) {
    rawBtn.textContent = globalRecKind === 'raw' ? '⏹ 全停止' : '⏺ 全カメラ録画(生)';
    rawBtn.classList.toggle('on', globalRecKind === 'raw');
  }
  if (viewBtn) {
    viewBtn.textContent = globalRecKind === 'view' ? '⏹ 全停止' : '⏺ 全カメラ録画(Quest)';
    viewBtn.classList.toggle('on', globalRecKind === 'view');
  }
}
function recordAll(kind) {
  const anyRec = [...columns.values()].some((c) => c.isRecording && c.isRecording());
  if (anyRec) {
    for (const c of columns.values()) c.stopRecord && c.stopRecord();
    globalRecKind = null;
  } else {
    let started = 0;
    for (const c of columns.values()) { if (c.startRecord && c.startRecord(kind)) started++; }
    globalRecKind = started ? kind : null;
  }
  renderGlobalRecState();
}

// ---- 節見出しの説明文を畳む -------------------------------------------------
//   各セクションの説明（.hint）は運用の要点が詰まっていて消せないが、常時全文だと
//   1 画面の情報密度が高すぎて「読まれない長文」になる。既定は 2 行クランプ + ▾ で全文。
//   開閉は localStorage に残す（現場で毎回開き直さない）。
function setupHintToggles() {
  const KEY = 'mawarimi.hintsOpen';
  let open = false;
  try { open = localStorage.getItem(KEY) === '1'; } catch { /* プライベートモード等 */ }
  const hints = [...document.querySelectorAll('.mc-title .hint')];
  const btns = [];
  const apply = () => {
    hints.forEach((h) => h.classList.toggle('clamped', !open));
    btns.forEach((b) => {
      b.textContent = open ? '▴ 説明を畳む' : '▾ 説明';
      b.title = open ? 'セクションの説明を 2 行に畳む' : 'セクションの説明を全文表示する';
    });
  };
  hints.forEach((h) => {
    const b = document.createElement('button');
    b.className = 'hint-toggle';
    b.onclick = () => {
      open = !open;
      try { localStorage.setItem(KEY, open ? '1' : '0'); } catch { /* noop */ }
      apply();
    };
    h.parentElement.appendChild(b);
    btns.push(b);
  });
  apply();
}
setupHintToggles();

// ---- 起動 -------------------------------------------------------------------
$('#openRecordings').onclick = () => fetch('/open-dir?dir=recordings').catch(() => {});

// ---- モードナビ（🎬 事前オーサリング / 🚨 ライブ運用）------------------------
const appEl = $('.app');

// 🧪 素材（素材工房）。カメラ定義は show.json を正にする（id と index をそのまま使う）。
//   工房は「合成の試写室」なので、監視列と同じ生ライブ <img> とカメラ画質を借りる
//   （二重接続しない）。💾 で cue を作れるようにして、工房 → タイムラインの導線を繋ぐ。
const atelier = createAtelier({
  root: $('#atelier'),
  getCameras: () => (state && state.cameras) || [],
  getLiveImg: (camId) => { const c = columns.get(camId); return c ? c.liveImg : null; },
  getCamPost: (camId) => {
    const c = (state?.cameras || []).find((x) => x.id === camId);
    return (c && c.post) || state?.post || FX_DEFAULT;
  },
  getAllCues: () => state?.cues || [],
  saveCue: async (cue) => {
    const r = await saveCueObject(cue);
    if (state && r.cues) state.cues = r.cues;
    return r;
  },
  onCueSaved: () => {
    renderLiveCuePanel();
    if (timeline && state) timeline.onState(state);
  },
});

// 段は覚えておく。誤ってリロードしただけで作業面が変わると、本番中なら事故に近い
//   （本番を見ていたつもりが演出面に戻っている）。?mode= を付ければ 1 回だけ上書きできる。
const MODE_KEY = 'mawarimi.mode';
const MODES = ['place', 'material', 'show', 'live'];
function restoreMode() {
  let m = null;
  try { m = new URLSearchParams(location.search).get('mode'); } catch { /* noop */ }
  if (!MODES.includes(m)) { try { m = localStorage.getItem(MODE_KEY); } catch { m = null; } }
  if (!MODES.includes(m)) return;
  const btn = document.querySelector(`.mode-btn[data-mode="${m}"]`);
  if (btn) btn.click();
}

document.querySelectorAll('.mode-btn').forEach((b) => {
  b.onclick = () => {
    if (appEl) appEl.dataset.mode = b.dataset.mode;
    try { localStorage.setItem(MODE_KEY, b.dataset.mode); } catch { /* プライベートモード等 */ }
    document.querySelectorAll('.mode-btn').forEach((x) => x.classList.toggle('active', x === b));
    renderPreflight();
    // 素材面へ入るたびに読み直す（📷 で撮ったフレームがすぐ候補に出るように）
    if (b.dataset.mode === 'material') atelier.refresh();
  };
});
// ⚠ 呼ぶのはこのファイルの末尾（`restoreMode` の定義位置ではない）。
//   復元は `.mode-btn` の click を発火し、その先で本番前チェックが走る。チェックは下の方で
//   `let` 宣言している状態（assetCheckSig 等）を読むので、ここで呼ぶと TDZ で例外になり、
//   **その後の初期化がまるごと止まる**（実際に止まっていた）。

// ▶ 検証 = 🕹 ショーシミュレーションへ（矢印キーの簡易シミュレーションは廃止・二重に持たない）。
//   オーサリングモードへ戻し、フロアマップのドットを有効化して、シミュレータまでスクロールする。
function focusSimulator() {
  const showBtn = document.querySelector('.mode-btn[data-mode="show"]');
  if (showBtn && appEl && appEl.dataset.mode !== 'show') showBtn.click();
  if (floorMap && floorMap.enableSim) floorMap.enableSim();
  const sec = document.querySelector('.showsim');
  if (sec) sec.scrollIntoView({ behavior: 'smooth', block: 'start' });
  return true;
}

// ---- 画質ロックは 2026-07-28 に撤去した ----
//   本番（🚨）ではカメラ列の画質欄（data-col-role="grade"）自体が出なくなったので、
//   「触れるように見えて触れない」減光 + ロックボタンという偽の防護が要らなくなった。
//   触らせたくないものは隠す（構造）方が、触れるが無効（状態）より強い。

// ---- ラン状態表示（Lap / ゾーン / 次の演出）+ 緊急操作 -----------------------
// course.order を 1 周とみなし、(lap, cameraIndex) の区間から「次に演出がある区間」を探す。
function courseOrder() {
  const n = (state?.cameras || []).length;
  const o = state?.layout?.course?.order;
  const valid = Array.isArray(o) ? o.filter((i) => Number.isInteger(i) && i >= 0 && i < n) : [];
  return valid.length ? valid : Array.from({ length: n }, (_, i) => i);
}
function segmentAt(lap, camIdx) {
  const segs = timelineSegments();
  if (!Array.isArray(segs)) return null;
  return segs.find((s) => s.lap === lap && s.camera === camIdx) || null;
}
function segFireLabel(seg) {
  if (!seg) return null;
  const ids = [];
  // 演出（先頭カットが何を映すかで要約する）
  for (const t of (seg.takes || [])) {
    const first = (t.steps || [])[0] || {};
    const what = first.source === 'live' ? `→${camLabelOf(first.camera)}`
      : first.cueId || (first.assetUrl ? '映像' : '');
    ids.push(`🎬${t.name || t.id || '演出'}${t.at === 'exit' ? '(離脱時)' : ''}${what ? ` ${what}` : ''}`);
  }
  return ids.length ? ids.join(', ') : null;
}

// 区間が参照する cue id（カットが重ねる素材）。診断の実在チェックに使う。
function segCueIds(seg) {
  const ids = [];
  for (const t of (seg.takes || [])) {
    for (const st of (t.steps || [])) if (st.cueId) ids.push(st.cueId);
  }
  return ids;
}

// 表示・診断で使う区間（**常に v3 として読む**）。show.json がまだ v2 でも
// normalizeTimelineV3 が演出・カットへ変換するので、ここから先に版の分岐は無い
// （Unity 側も同じで、TimelineMigration が変換してから TakeRunner が実行する）。
function timelineSegments() {
  const tl = state?.timeline;
  if (!tl || !Array.isArray(tl.segments)) return [];
  return normalizeTimelineV3(tl).segments;
}
function camLabelOf(idx) {
  const c = (state?.cameras || [])[idx];
  return c ? c.id : `#${idx}`;
}
// 現在区間の次から順に、演出を持つ区間を最大 2 周ぶん探す。
function findNextFire(lap, camIdx) {
  const order = courseOrder();
  if (!order.length) return null;
  let pos = order.indexOf(camIdx);
  if (pos < 0) pos = 0;
  let l = lap;
  for (let step = 1; step <= order.length * 2; step++) {
    let p = pos + step;
    const lapAdd = Math.floor(p / order.length);
    p %= order.length;
    const seg = segmentAt(l + lapAdd, order[p]);
    const label = segFireLabel(seg);
    if (label) return { lap: l + lapAdd, camera: order[p], label, steps: step };
  }
  return null;
}
// 秒 → m:ss（暗所で 1 秒で読める形。小数は出さない）。
function mmss(sec) {
  const t = Math.max(0, Math.floor(Number(sec) || 0));
  return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`;
}

function renderRunPanel() {
  const lapEl = $('#runLap'), zoneEl = $('#runZone'), modeEl = $('#runMode'), nextEl = $('#runNext');
  if (!lapEl) return;
  const u = lastUnity;
  const hasLap = unityAlive && typeof u.lap === 'number' && u.lap > 0;
  // ⚠ 「体験者が居るゾーン」と「画面に映っているカメラ」は別物。演出のカットが別カメラを映している間、
  //    両者は食い違う。旧実装は画面のカメラ（cam）を「ゾーン」と称して出し、しかもその index で
  //    区間を引いていたので、演出中は「次の演出」まで別区間の内容になっていた（2026-07-28）。
  const screenIdx = unityAlive && typeof u.cam === 'number' && u.cam >= 0 ? u.cam
    : (unityAlive && typeof u.activeIndex === 'number' ? u.activeIndex : -1);
  const zoneIdx = unityAlive && typeof u.zoneCam === 'number' && u.zoneCam >= 0 ? u.zoneCam : -1;
  // zoneCam を送らない旧 Unity と繋いだときは画面のカメラで代用する（表示はするが意味が違う旨は出さない）。
  const walkIdx = zoneIdx >= 0 ? zoneIdx : screenIdx;
  // 体験の相（導入 / 本編 / 終了）。企画書 3 章の「導入を含め 3 分以内」を現場で守る唯一の面。
  const phaseEl = $('#runPhase'), clockEl = $('#runClock');
  const phase = unityAlive ? (u.phase || 'RUN') : '';
  if (phaseEl) {
    const label = phase === 'INTRO' ? '導入中' : phase === 'END' ? '🏁 終了' : phase === 'RUN' ? '本編' : '—';
    phaseEl.textContent = unityAlive ? label : '—';
    phaseEl.className = 'run-mode' + (!unityAlive ? ' off' : phase === 'END' ? ' reg' : ' on');
  }
  if (clockEl) {
    const target = Number(u.targetSec) || 0;
    // ⚠ 終了条件を満たしてから走行中の演出を見せ切っている間、Unity の phase はまだ RUN。
    //   卓が「本編」としか出していなかったので、体験がもう終わっていることが分からなかった
    //   （ShowRunLogic は _endHolding の間 _phase を Finished にしない）。
    const holding = unityAlive && u.endHolding === true;
    let txt = '—';
    if (unityAlive && phase === 'INTRO') txt = `導入 ${mmss(u.introSec)}`;
    else if (unityAlive && phase === 'END') txt = `${mmss(u.runSec)}（終了）`;
    else if (unityAlive && holding) txt = `${mmss(u.runSec)}（演出を見せ切っています）`;
    else if (unityAlive) txt = target > 0 ? `${mmss(u.runSec)} / 目安 ${mmss(target)}` : mmss(u.runSec);
    clockEl.textContent = txt;
    const over = unityAlive && phase === 'RUN' && !holding && target > 0 && Number(u.runSec) > target;
    clockEl.className = 'run-zone' + (unityAlive ? (over || holding ? ' reg' : ' on') : '');
  }
  const totalLaps = Number(u.totalLaps) || 0;
  // この周の経過も出す（企画書は「各周およそ 30 秒」。速すぎ / 遅すぎは周単位でしか見えない）。
  const lapSec = Number(u.lapSec);
  const lapClock = unityAlive && phase === 'RUN' && Number.isFinite(lapSec) && lapSec > 0
    ? ` ・ この周 ${mmss(lapSec)}` : '';
  lapEl.textContent = hasLap
    ? (totalLaps > 0 ? `Lap ${u.lap}/${totalLaps}${lapClock}` : `Lap ${u.lap}${lapClock}`)
    : 'Lap —';
  lapEl.className = 'run-lap' + (unityAlive ? ' on' : '');

  // 映像の遅れ（企画書「視覚遅延は 100ms 程度以内を目標として管理する」）。
  // **絶対の end-to-end ではない**（配信端末と Unity で時計の基準が違い引き算できない）ので、
  // 観測できる 3 つと配信側の鮮度を並べ、何を測っているかを明示する。
  const latEl = $('#runLatency');
  if (latEl) {
    if (!unityAlive) { latEl.textContent = '映像の遅れ: —（Unity 未接続）'; latEl.className = 'run-next'; }
    else {
      const j = Number(u.latencyJitterMs) || 0, d = Number(u.latencyDecodeMs) || 0,
            p = Number(u.latencyPresentMs) || 0, age = Number(u.sourceAgeMs) || 0;
      const sum = Math.round(j + d + p);
      const bits = [`観測 ${sum}ms`, `揺らぎ ${Math.round(j)}`, `展開 ${Math.round(d)}`, `提示 ${Math.round(p)}`];
      if (age > 0) bits.push(`配信側の鮮度 ${Math.round(age)}ms`);
      if (u.displayHz) bits.push(`${Math.round(u.displayHz)}Hz`);
      if (Number(u.throttleStage) > 0) bits.push('⚠ 配信端末が発熱で降格中');
      latEl.textContent = '映像の遅れ: ' + bits.join(' / ') + '（撮影・エンコード・伝送の下限は含まない）';
      latEl.className = 'run-next' + (sum > 60 || Number(u.throttleStage) > 0 ? ' hot' : '');
    }
  }
  zoneEl.textContent = walkIdx >= 0 ? `ゾーン ${camLabelOf(walkIdx)}` : 'ゾーン —';
  zoneEl.className = 'run-zone' + (unityAlive ? ' on' : '');
  modeEl.textContent = unityAlive ? (u.mode || 'NORMAL') : 'Unity 未接続';
  modeEl.className = 'run-mode' + (unityAlive ? (u.mode === 'REG' ? ' reg' : ' on') : ' off');

  // 「いま画面を握っているのは誰か」— 本番中にいちばん知りたい 1 行。
  const ctrl = (state && state.control) || {};
  const ownEl = $('#runOwner');
  if (ownEl) {
    let own = { t: '画面: —', c: '' };
    if (!unityAlive) own = { t: '画面: —（Unity 未接続）', c: 'off' };
    else if (ctrl.cameraOverride) own = { t: `画面: 卓が カメラ ${ctrl.cameraOverride} に固定中`, c: 'held' };
    else if (u.takeId) own = { t: `画面: 演出「${u.takeId}」が再生中${screenIdx >= 0 ? `（${camLabelOf(screenIdx)}）` : ''}`, c: 'held' };
    else if (ctrl.activeCue) own = { t: `画面: 卓が発火した演出 ${ctrl.activeCue}`, c: 'held' };
    else own = { t: `画面: 自律${screenIdx >= 0 ? `（${camLabelOf(screenIdx)}）` : ''}`, c: 'auto' };
    ownEl.textContent = own.t;
    ownEl.className = 'run-owner ' + own.c;
  }

  if (!nextEl) return;
  if (!hasLap || walkIdx < 0) { nextEl.textContent = '次の演出: —（Unity 未接続 / 周回未開始）'; nextEl.className = 'run-next'; return; }
  // 区間は必ず「人の居場所」で引く（画面のカメラで引くと演出中に別区間の内容が出る）。
  const here = segFireLabel(segmentAt(u.lap, walkIdx));
  const next = findNextFire(u.lap, walkIdx);
  const parts = [];
  if (here) parts.push(`このゾーン: ${here}`);
  parts.push(next
    ? `次: ${next.label}（Lap ${next.lap} / ゾーン ${camLabelOf(next.camera)}・${next.steps} ゾーン先）`
    : '次: なし（この先の区間に演出未設定）');
  nextEl.textContent = parts.join(' ／ ');
  nextEl.className = 'run-next' + (here ? ' hot' : '');
}
// ▶ ラン開始 = runEpoch++ ＋ カメラ固定解除 ＋ 演出停止（前の体験者のラッチを持ち越さない）。
if ($('#runStart')) {
  $('#runStart').onclick = async () => {
    // 確認は残す（周回リセットは取り消せない。runEpoch を戻しても Unity 側の once 発火済みは復元されない）。
    // 文言は暗所で 1 秒で読める長さに畳む — 長文の confirm は読まれずに Enter される。
    if (!confirm('次の体験者へ。周回・演出・カメラ固定を全部リセットします。')) return;
    const s = await getState();
    const el0 = $('#runNext');
    if (!s) {
      if (el0) el0.textContent = '✕ ラン開始できません（卓サーバに接続できません）';
      return;
    }
    const ctrl = { ...(s.control || {}) };
    ctrl.runEpoch = (parseInt(ctrl.runEpoch, 10) || 0) + 1;
    ctrl.cameraOverride = null;
    ctrl.activeCue = null;
    ctrl.slots = [];   // 前の体験者のために束縛した素材を持ち越さない（2026-07-28）
    const r = await postState({ control: ctrl });
    // 走行中の演出は control の書き換えでは畳まれない（activeCue を使わないため）。世代カウンタで確実に止める。
    await postCommand({ type: 'abortTake' });
    const el = $('#runNext');
    if (el && r && r.ok !== false) el.textContent = `▶ ラン開始（epoch ${ctrl.runEpoch}）— 周回 / 演出 / 固定 / 素材スロットをリセットしました`;
  };
}
// ⏭ 導入を終える / 🏁 体験を終える / ⚡ 乱れ — いずれも世代カウンタで届ける
// （「空を空にする」形の指示は long-poll では原理的に伝わらない）。
if ($('#runIntroDone')) {
  $('#runIntroDone').onclick = async () => {
    const r = await postCommand({ type: 'advanceIntro' });
    const el = $('#emgStopMsg');
    if (el) el.textContent = (r && r.ok !== false) ? '導入を終えて本編へ' : '⚠ 卓サーバに届きませんでした';
  };
}
if ($('#runEnd')) {
  $('#runEnd').onclick = async () => {
    if (!confirm('体験を終えます（暗転）。走行中の演出は待たずに畳みます。')) return;
    const r = await postCommand({ type: 'endRun' });
    const el = $('#emgStopMsg');
    if (el) el.textContent = (r && r.ok !== false) ? '🏁 体験を終了しました' : '⚠ 卓サーバに届きませんでした';
  };
}
if ($('#emgGlitch')) {
  $('#emgGlitch').onclick = async () => {
    const r = await postCommand({ type: 'glitch', level: 0.85, sec: 0.3 });
    const el = $('#emgStopMsg');
    if (el) el.textContent = (r && r.ok !== false) ? '⚡ 乱れを走らせました' : '⚠ 卓サーバに届きませんでした';
  };
}
if ($('#emgAuto')) $('#emgAuto').onclick = () => postCommand({ type: 'setCameraOverride', camera: null });
// ■ 画面を取り返す = 卓の手動 cue（activeCue）**と**タイムラインが自動発火した演出（Take）の両方を畳む。
// ⚠ stopCue だけでは自動発火の演出は止まらない（activeCue が空のまま走るので Unity 側で状態変化が起きない）。
//    2026-07-28 まで、本番中に演出が出たまま戻せない実バグだった。abortTake は世代カウンタなので必ず届く。
if ($('#emgStop')) {
  $('#emgStop').onclick = async () => {
    const el = $('#emgStopMsg');
    const a = await postCommand({ type: 'stopCue' });
    const b = await postCommand({ type: 'abortTake' });
    const ok = a && a.ok !== false && b && b.ok !== false;
    if (el) el.textContent = ok ? '画面をライブへ戻しました' : '⚠ 卓サーバに届きませんでした';
  };
}

// ---- カメラ切替タイミング（control.minDwellSec / switchCooldownSec）------------
// 空 / 0 = 未指定（Unity 側でコード既定 0.5s に戻る。present 判定は Unity の ResolveTiming が >0 で行う）。
// 入力を確定（change）したらその場で反映する。「適用」ボタンの押し忘れを構造から消す。
async function loadSwitchTiming() {
  const dEl = $('#switchDwell'), cEl = $('#switchCooldown');
  if (!dEl || !cEl) return;
  let ctrl = {};
  try { ctrl = (await getState())?.control || {}; } catch { /* offline */ }
  dEl.value = (typeof ctrl.minDwellSec === 'number' && ctrl.minDwellSec > 0) ? ctrl.minDwellSec : '';
  cEl.value = (typeof ctrl.switchCooldownSec === 'number' && ctrl.switchCooldownSec > 0) ? ctrl.switchCooldownSec : '';
}
async function applySwitchTiming() {
  const st = $('#switchTimingState');
  const s = await getState();
  if (!s) {
    if (st) { st.textContent = '✕ 適用失敗（サーバ断）'; st.className = 'ed-status err'; }
    return;
  }
  const ctrl = { ...(s.control || {}) };
  ctrl.minDwellSec = parseFloat($('#switchDwell').value) || 0;
  ctrl.switchCooldownSec = parseFloat($('#switchCooldown').value) || 0;
  const r = await postState({ control: ctrl });
  if (!st) return;
  const fmt = (v) => (v > 0 ? `${v}s` : '既定 0.5s');
  st.textContent = (r && r.ok !== false)
    ? `✓ 適用（滞在 ${fmt(ctrl.minDwellSec)} / CD ${fmt(ctrl.switchCooldownSec)}）`
    : '✕ 適用失敗（サーバ断）';
  st.className = 'ed-status ' + (r && r.ok !== false ? 'ok' : 'err');
}
// ---- 体験の骨格（show.json run + control.switchGlitch）------------------------
// 企画書 3 章「3 区間を 3 周・導入を含め 3 分以内」。ここが空だと Unity はコード既定（3 周・導入 20s）で走る。
// 既定値と「どの区間を踏むか」の判定は run-model.js が単一の正（リボン・シミュレータも同じ関数を読む）。

function runCfgEls() {
  return {
    laps: $('#runLaps'), introOn: $('#runIntroOn'), introSec: $('#runIntroSec'),
    introAuto: $('#runIntroAuto'), target: $('#runTargetSec'), hard: $('#runHardSec'),
    endGrace: $('#runEndGrace'), endHold: $('#runEndHold'),
    glitch: $('#runSwitchGlitch'),
  };
}

function renderRunCfg(s) {
  const e = runCfgEls();
  if (!e.laps) return;
  const r = runConfig(s);
  e.laps.value = r.totalLaps;
  e.introOn.checked = r.introEnabled !== false;
  e.introSec.value = r.introMinSec;
  e.introAuto.checked = r.introAutoAdvance !== false;
  e.target.value = r.targetSec;
  e.hard.value = r.hardLimitSec;
  if (e.endGrace) e.endGrace.value = r.endGraceSec;
  if (e.endHold) e.endHold.value = r.endHoldMaxSec;
  e.glitch.value = Number((s && s.control && s.control.switchGlitch) || 0);
  renderIntroCfg(r, s);
}

// ---- 導入の遷移演出（show.json run.intro）------------------------------------
//   現実 → 固定カメラの映像へ格下げする約 33 秒（設計 2026-07-30_intro-passthrough-to-screen.md）。
//   run の一部なので**保存は applyRunCfg の 1 経路に乗せる**（新しい保存口を作らない）。
//   判定と既定は intro-model.js が単一の正（本番前チェックも同じ関数を読む）。
const INTRO_NUM = {
  realSec: '#introReal', degradeSec: '#introDegrade', structureSec: '#introStructure',
  frameSec: '#introFrame', swapSec: '#introSwap', maxSec: '#introMaxSec', glitchOnSwap: '#introGlitch',
};
const INTRO_BOOL = {
  enabled: '#introOn', showCameraMarks: '#introCamMarks',
  showRoomWire: '#introRoomWire', raiseHandPrompt: '#introRaiseHand',
};
// 文字列の設定。開始ラインは選択肢を layout.lines から作るので render 側で別に埋める。
const INTRO_STR = { startLineId: '#introStartLine' };

/** DOM → 正規化した intro（UI に無いキー（edgeColor 等）は現状値を引き継ぐ）。 */
function readIntroCfg(baseRun) {
  const raw = { ...((baseRun && baseRun.intro) || {}) };
  for (const [k, sel] of Object.entries(INTRO_NUM)) {
    const el = $(sel);
    if (el) raw[k] = parseFloat(el.value);
  }
  for (const [k, sel] of Object.entries(INTRO_BOOL)) {
    const el = $(sel);
    if (el) raw[k] = !!el.checked;
  }
  for (const [k, sel] of Object.entries(INTRO_STR)) {
    const el = $(sel);
    if (el) raw[k] = String(el.value || '');
  }
  return introConfig({ intro: raw });
}

/** 合計秒を出す。上限を超えたら警告色（超えた段は実機が飛ばす）。 */
function renderIntroSum(run) {
  const el = $('#introSum');
  if (!el) return;
  const intro = readIntroCfg(run);
  const over = introStageSec(intro) > intro.maxSec;
  el.textContent = `${introDurationLabel(intro, run && run.introMinSec)}${over ? ` ⚠ 上限 ${intro.maxSec}s 超` : ''}`;
  el.className = 'ed-status ' + (over ? 'err' : 'ok');
}

function renderIntroCfg(r, s) {
  if (!$('#introOn')) return;
  const intro = introConfig(r);
  for (const [k, sel] of Object.entries(INTRO_NUM)) { const el = $(sel); if (el) el.value = intro[k]; }
  for (const [k, sel] of Object.entries(INTRO_BOOL)) { const el = $(sel); if (el) el.checked = !!intro[k]; }
  renderIntroStartLine(intro.startLineId, s);
  renderIntroSum(r);
}

/**
 * 開始ラインの選択肢を layout.lines から作る。
 * **著作した id が lines から消えていたら黙って空へ戻さず、選択肢に残して警告する** —
 * 黙って戻すと「線を消したせいで導入が始まらない」に現場で気づけない。
 */
function renderIntroStartLine(current, s) {
  const sel = $('#introStartLine');
  if (!sel) return;
  const lines = (s && s.layout && s.layout.lines) || [];
  const cams = (s && s.cameras) || [];
  const camName = (i) => (cams[i] && cams[i].id) ? cams[i].id : (i >= 0 ? `#${i}` : '—');
  const opts = ['<option value="">（開始位置の円を使う）</option>'];
  for (const l of lines) {
    if (!l || !l.id) continue;
    const label = l.label || l.id;
    opts.push(`<option value="${l.id}">${label}（カメラ ${camName(l.camera)}）</option>`);
  }
  const missing = current && !lines.some(l => l && l.id === current);
  if (missing) opts.push(`<option value="${current}">⚠ ${current}（この線は今の地図に無い）</option>`);
  sel.innerHTML = opts.join('');
  sel.value = current || '';
  const note = $('#introStartLineNote');
  if (note) {
    note.textContent = missing
      ? '⚠ この線がフロアマップに無い。導入はスタッフ操作でしか始められない'
      : (current ? '被った体験者がこの線を横切ると導入が始まる' : '開始位置の円に 0.5 秒留まると始まる');
    note.className = 'ed-status ' + (missing ? 'err' : 'ok');
  }
}

async function applyRunCfg() {
  const e = runCfgEls();
  const st = $('#runCfgState');
  const s = await getState();
  if (!s) { if (st) { st.textContent = '✕ 適用失敗（サーバ断）'; st.className = 'ed-status err'; } return; }
  const run = {
    ...runConfig(s),
    totalLaps: Math.max(1, Math.round(parseFloat(e.laps.value) || RUN_DEFAULT.totalLaps)),
    introEnabled: !!e.introOn.checked,
    introMinSec: Math.max(0, parseFloat(e.introSec.value) || 0),
    introAutoAdvance: !!e.introAuto.checked,
    targetSec: Math.max(0, parseFloat(e.target.value) || 0),
    hardLimitSec: Math.max(0, parseFloat(e.hard.value) || 0),
    // 0 は「未指定」として Unity が既定へ落とすので、卓も 0 を素通しにする（勝手に既定へ書き換えない）。
    endGraceSec: e.endGrace ? Math.max(0, parseFloat(e.endGrace.value) || 0) : runConfig(s).endGraceSec,
    endHoldMaxSec: e.endHold ? Math.max(0, parseFloat(e.endHold.value) || 0) : runConfig(s).endHoldMaxSec,
  };
  run.intro = readIntroCfg(s.run);
  // control は shallow 置換なので、必ず取り直した control を土台にする（runEpoch / slots を飛ばさない）。
  const ctrl = { ...(s.control || {}) };
  ctrl.switchGlitch = Math.max(0, Math.min(1, parseFloat(e.glitch.value) || 0));
  const r = await postState({ run, control: ctrl });
  if (!st) return;
  const intoState = $('#introCfgState');
  if (intoState) {
    intoState.textContent = (r && r.ok !== false)
      ? `✓ 適用（${run.intro.enabled ? introDurationLabel(run.intro, run.introMinSec) : '導入演出なし'}）`
      : '✕ 適用失敗（サーバ断）';
    intoState.className = 'ed-status ' + (r && r.ok !== false ? 'ok' : 'err');
  }
  st.textContent = (r && r.ok !== false)
    ? `✓ 適用（${run.totalLaps} 周 / 導入 ${run.introEnabled ? `${run.introMinSec}s` : 'なし'} / 目安 ${run.targetSec}s）`
    : '✕ 適用失敗（サーバ断）';
  st.className = 'ed-status ' + (r && r.ok !== false ? 'ok' : 'err');
}

// ---- 撮像の質（show.json feel）------------------------------------------------
//   post 12 項目と違って**時間で動く**ので別系統。既定は C# ShowFeelDef / capture-server.py の
//   _default_show と 3 者で揃えること（片方だけ変えると沈黙して食い違う）。
//   ⚠ この卓のプレビューには出ない（実機だけが持つ「装置の挙動」）。UI にその旨を書いてある。
const FEEL_DEFAULT = { noiseDark: 0.10, noiseFixed: 0.035, agc: 0.7, targetLuma: 0.34, followSec: 1.1 };
const FEEL_NUM = {
  noiseDark: '#feelNoiseDark', noiseFixed: '#feelNoiseFixed', agc: '#feelAgc',
  targetLuma: '#feelTargetLuma', followSec: '#feelFollowSec',
};

function feelConfig(s) {
  return { ...FEEL_DEFAULT, ...((s && s.feel) || {}) };
}

function renderFeelCfg(s) {
  const f = feelConfig(s);
  for (const [key, sel] of Object.entries(FEEL_NUM)) {
    const el = $(sel);
    if (el && document.activeElement !== el) el.value = f[key];
  }
}

async function applyFeelCfg() {
  const st = $('#feelCfgState');
  const s = await getState();
  if (!s) { if (st) { st.textContent = '✕ 適用失敗（サーバ断）'; st.className = 'ed-status err'; } return; }
  const feel = { ...feelConfig(s) };
  for (const [key, sel] of Object.entries(FEEL_NUM)) {
    const el = $(sel);
    if (el) feel[key] = Math.max(0, parseFloat(el.value) || 0);
  }
  const r = await postState({ feel });
  if (!st) return;
  st.textContent = (r && r.ok !== false)
    ? `✓ 適用（実機で反映。この卓の画は変わりません）`
    : '✕ 適用失敗（サーバ断）';
  st.className = 'ed-status ' + (r && r.ok !== false ? 'ok' : 'err');
}

for (const sel of Object.values(FEEL_NUM)) {
  const el = $(sel);
  if (el) el.onchange = applyFeelCfg;
}

for (const el of Object.values(runCfgEls())) if (el) el.onchange = applyRunCfg;
// 導入も run の一部なので保存先は applyRunCfg（同じ 1 経路）。合計秒だけは打ちながら追従させる。
for (const sel of [...Object.values(INTRO_NUM), ...Object.values(INTRO_BOOL), ...Object.values(INTRO_STR)]) {
  const el = $(sel);
  if (!el) continue;
  el.onchange = applyRunCfg;
  el.oninput = () => renderIntroSum(runConfig(state));
}

if ($('#switchDwell')) $('#switchDwell').onchange = applySwitchTiming;
if ($('#switchCooldown')) $('#switchCooldown').onchange = applySwitchTiming;
loadSwitchTiming();

// ---- ⏺ 端末内録画（show.json record）----------------------------------------
//   「前の周を録って後の周の演出で流す」の唯一の入口。ここが OFF だと実機は 1 フレームも録らず、
//   「録画」カットは黙って飛ばされる（気づけるのは実機のログだけ）。だから設定と参照の照合まで卓で出す。
//   判定そのものは record-model.js が単一の正（リボン・シミュレータも同じ関数を読む）。
const recordCfg = () => recordConfig(state);
const recordLapSet = () => recordLaps(recordCfg());
const recCoverage = () => recordCoverage(state, timelineSegments(), camLabelOf);
function recMaxLap() {
  let m = 3;
  for (const s of timelineSegments()) m = Math.max(m, parseInt(s.lap, 10) || 0);
  for (const r of recCoverage().refs) m = Math.max(m, r.lap);
  for (const l of recordLapSet()) m = Math.max(m, l);
  return m;
}
async function saveRecord(patch) {
  const next = { ...recordCfg(), ...patch };
  next.laps = [...new Set((next.laps || []).map((n) => parseInt(n, 10)).filter((n) => n > 0))].sort((a, b) => a - b);
  if (state) state.record = next;
  const r = await postState({ record: next });
  const st = $('#recState');
  const ok = r && r.ok !== false;
  if (st) {
    st.textContent = ok ? `✓ 保存（${next.enabled ? `録る周 ${next.laps.join(', ') || 'なし'}` : 'OFF'}）` : '✕ 保存できません（卓サーバ断）';
    st.className = 'ed-status ' + (ok ? 'ok' : 'err');
  }
  renderRecordPanel(); renderPreflight();
}
function renderRecUse() {
  const el = $('#recUse');
  if (!el) return;
  const { refs, bad } = recCoverage();
  if (!refs.length) {
    el.innerHTML = '<div class="rec-row muted">「録画」カットはまだありません（タイムラインのカットで <b>映すもの → 録画</b> を選ぶと、ここに対応が出ます）</div>';
    return;
  }
  const rows = refs.map((r) => {
    const what = r.camera >= 0 && r.lap > 0 ? `${r.lap}周目 ${camLabelOf(r.camera)}` : '（未指定）';
    return `<div class="rec-row ${r.ok ? 'ok' : 'ng'}">${r.ok ? '✅' : '❌'} <b>${escapeHtml(what)}</b> の録画 → ${escapeHtml(r.where)} の「${escapeHtml(r.take)}」`
      + (r.ok ? '' : ` <span class="rec-why">${escapeHtml(r.reason)} → ${escapeHtml(r.fix)}</span>`) + '</div>';
  });
  const fixable = bad.some((r) => r.lap > 0 && r.camera >= 0);
  if (fixable) {
    rows.push('<div class="rec-row"><button id="recFixLaps" class="accent">⟲ 参照している周を録る対象にする</button>'
      + '<span class="rec-why">録画を ON にして、足りない周を追加します</span></div>');
  }
  el.innerHTML = rows.join('');
  const fix = $('#recFixLaps');
  if (fix) {
    fix.onclick = () => {
      const laps = new Set(recordLapSet());
      for (const l of missingRecordLaps(state, timelineSegments())) laps.add(l);
      saveRecord({ enabled: true, laps: [...laps] });
    };
  }
}
function renderRecordPanel() {
  const box = $('#recLaps');
  if (!box) return;
  const cfg = recordCfg();
  const laps = recordLapSet();
  const n = recMaxLap();
  let html = '';
  for (let i = 1; i <= n; i++) {
    html += `<label class="chk"><input type="checkbox" data-lap="${i}"${laps.has(i) ? ' checked' : ''}${cfg.enabled ? '' : ' disabled'}> ${i}周目</label>`;
  }
  box.innerHTML = html;
  for (const el of box.querySelectorAll('input[data-lap]')) {
    el.onchange = () => {
      const set = new Set(recordLapSet());
      const v = parseInt(el.dataset.lap, 10);
      if (el.checked) set.add(v); else set.delete(v);
      saveRecord({ laps: [...set] });
    };
  }
  const setVal = (sel, v) => { const e = $(sel); if (e && document.activeElement !== e) e.value = v; };
  const en = $('#recEnabled');
  if (en && document.activeElement !== en) en.checked = !!cfg.enabled;
  setVal('#recTail', recordTailSec(cfg));
  setVal('#recMaxMB', cfg.maxTotalMB);
  setVal('#recFps', cfg.fpsCap);
  renderRecUse();
}
if ($('#recEnabled')) $('#recEnabled').onchange = () => saveRecord({ enabled: $('#recEnabled').checked });
if ($('#recTail')) $('#recTail').onchange = () => saveRecord({ tailSec: Math.max(1, parseFloat($('#recTail').value) || REC_DEFAULT_TAIL_SEC) });
if ($('#recMaxMB')) $('#recMaxMB').onchange = () => saveRecord({ maxTotalMB: Math.max(10, parseInt($('#recMaxMB').value, 10) || 200) });
if ($('#recFps')) $('#recFps').onchange = () => saveRecord({ fpsCap: Math.max(1, parseFloat($('#recFps').value) || 15) });

// ---- ✅ 本番前チェック（自動更新）------------------------------------------
//   「押す前に見る場所」を 1 枚に集約する。各行がそのまま切り分けの入口。
// 参照している素材が**ディスクに実在するか**はサーバに聞く（ブラウザからファイルの有無は見えない）。
//   本番前チェックは 3 秒ごとに走るので、参照 URL の集合が変わった時だけ 1 回投げ、
//   描画はキャッシュを読む（結果は 1 周遅れで出る）。
let assetCheckSig = '';
let assetMissing = [];
let assetChecking = false;
function checkAssetsExist(urls) {
  const uniq = [...new Set(urls.filter(Boolean))].sort();
  const sig = uniq.join('|');
  if (!uniq.length) { assetCheckSig = sig; assetMissing = []; return; }
  if (sig === assetCheckSig || assetChecking) return;
  assetChecking = true;
  fetch('/assets/check', { method: 'POST', body: JSON.stringify({ urls: uniq }) })
    .then((r) => r.json())
    .then((j) => { assetCheckSig = sig; assetMissing = (j && j.missing) || []; })
    .catch(() => { /* サーバ断: 別の行が赤くなるのでここでは黙る */ })
    .finally(() => { assetChecking = false; });
}

function preflightRows() {
  const rows = [];
  const cams = state?.cameras || [];
  rows.push(serverAlive
    ? { s: 'ok', label: '卓サーバ', detail: '応答中（保存・配信 OK）' }
    : { s: 'ng', label: '卓サーバ', detail: '断 — serve.ps1 を再起動（この画面の操作は届いていません）' });

  const u = lastUnity;
  if (!unityAlive) rows.push({ s: 'ng', label: 'Quest（Unity）', detail: 'heartbeat 未受信 — アプリ起動 / ShowServer host を確認' });
  else {
    const synced = state && typeof u.appliedRev === 'number' && u.appliedRev >= state.rev;
    // ⚠ 位置合わせモード（REG）中は体験を始められない。旧実装は detail に書きながら ✅ を出していた。
    const inReg = u.mode === 'REG';
    rows.push({
      s: inReg ? 'ng' : (synced ? 'ok' : 'warn'),
      label: 'Quest（Unity）',
      detail: inReg
        ? '位置合わせモード中 — 体験開始前に退出（右トリガー 2 秒長押し）'
        : `NORMAL / ${synced ? '設定反映済み' : `同期中 ${u.appliedRev}/${state?.rev}`}`,
    });
    // Unity が実際に初期化できたカメラ本数と show.json の宣言がズレていないか。
    // 演出のカットは camera を **index** で指すので、ズレると狙った映像と違うカメラが出る
    // （範囲外なら §6.4 でカットごと飛ぶ）。どちらも画面には「演出が出ない / 違う画」としか
    // 現れず、原因に到達できない。Unity はこの照合のために cameraCount を送っているのに、
    // 卓は一度も見ていなかった（2026-08-07 に追加）。
    // ⚠ 実際に 2026-07-31、host 未設定の 1 台が registry の生成ループで例外を投げて
    //   **全カメラが初期化されない**事故が起きている。そのとき卓は全部 ✅ のままだった。
    // 旧 Unity は cameraCount を送らない → NaN になり、この行は出ない（後方互換）。
    const actualCams = Number(u.cameraCount);
    if (cams.length > 0 && Number.isFinite(actualCams) && actualCams !== cams.length) {
      rows.push({
        s: 'ng', label: 'カメラ本数',
        detail: actualCams === 0
          ? `Quest がカメラを 1 台も初期化できていません（show.json は ${cams.length} 台）— 実機ログの [CameraStream] / [MJPEG] を見る`
          : `Quest ${actualCams} 台 / show.json ${cams.length} 台 — 演出のカメラ指定が別カメラを指します（実機ログで初期化に失敗した台を確認）`,
      });
    }
    // 位置合わせの状態。ここがズレたまま始めると「歩いても切り替わらない」になる。
    // HMD 内には出るが、体験者が被っている間スタッフには見えないので卓で言う。
    if (u.needsReReg) {
      rows.push({ s: 'ng', label: '位置合わせ', detail: 'トラッキング原点が変わりました（OS の recenter）— 再登録するまでゾーンがズレたまま動きます' });
    } else if (u.registered === false) {
      rows.push({ s: 'ng', label: '位置合わせ', detail: '未登録 — 右トリガー 2 秒長押しで登録（ゾーンが実空間に合いません）' });
    } else if (u.registered === true) {
      rows.push({ s: 'ok', label: '位置合わせ', detail: '登録済み' });
    }
  }

  const devices = (lastDiscovery && lastDiscovery.devices) || [];
  const conflicts = new Set((lastDiscovery && lastDiscovery.conflicts) || []);
  const bad = [];
  for (const c of cams) {
    const col = columns.get(c.id);
    const live = col && col.liveOk;
    const found = devices.some((d) => d.role === 'camera' && d.id === c.id);
    // 「一度も来ていない（未受信 / 未発見）」と「来ていたのに止まった（停止）」を区別する。
    // 後者はスマホの熱落ち・Wi-Fi 断で、現場での打ち手がまるで違う。
    const e = lastLiveness[`${c.host || ''}:${c.port || 8080}`];
    if (conflicts.has(c.id)) bad.push(`${c.id}=二重ID`);
    else if (!live) {
      const stalled = e && e.ageMs != null;
      bad.push(`${c.id}=${stalled ? `停止 ${Math.round(e.ageMs / 1000)}s` : (found ? '未受信' : '未発見')}`);
    }
  }
  rows.push(bad.length
    ? { s: 'ng', label: `カメラ ${cams.length} 台`, detail: `${bad.join(' / ')} — 🩺 疎通診断で層を切り分け` }
    : { s: 'ok', label: `カメラ ${cams.length} 台`, detail: `全台 受信中（${LIVE_STALE_MS / 1000}s 以内にフレーム到着）` });

  // 2 つ以上のカメラが同じ配信元を指していると、別ゾーンなのに同じ映像が出る
  // （切替が「効いていない」ように見える）。LIVE 判定は通ってしまうのでここで別に見る。
  const byHost = new Map();
  for (const c of cams) {
    if (!c.host) continue;
    const k = `${c.host}:${c.port || 8080}`;
    byHost.set(k, [...(byHost.get(k) || []), c.id]);
  }
  const dups = [...byHost.entries()].filter(([, ids]) => ids.length > 1);
  if (dups.length) {
    rows.push({
      s: 'ng', label: '配信元の重複',
      detail: dups.map(([h, ids]) => `${ids.join(' と ')} が同じ ${h}`).join(' / ')
        + ' — 別ゾーンで同じ映像が出ます（📡 発見済み端末で ID を確認）',
    });
  }

  // 接続先の同一性（PC が /info の cameraId を 10 秒毎に照合）。
  //   host が生きていて映像も流れるのに「別スロットの端末」という事故を、ここだけが検出できる。
  const ident = (lastDiscovery && lastDiscovery.identity) || {};
  const idBad = [], idUnk = [];
  for (const c of cams) {
    const r = ident[c.id];
    if (!r) continue;
    if (r.state === 'mismatch') idBad.push(`${c.id}: ${r.detail}`);
    else if (r.state === 'unverifiable') idUnk.push(c.id);
  }
  if (idBad.length) rows.push({ s: 'ng', label: '接続先の同一性', detail: idBad.join(' / ') });
  else if (idUnk.length) {
    rows.push({ s: 'warn', label: '接続先の同一性',
      detail: `${idUnk.join(', ')} は cameraId を名乗らない端末（iPhone 等）— 照合できないため目視で確認` });
  } else if (Object.values(ident).some((r) => r.state === 'ok')) {
    rows.push({ s: 'ok', label: '接続先の同一性', detail: '各スロットが名乗る cameraId と一致' });
  }

  // ラッチは latches() が単一の正（警告バー・⛑・▶ ラン開始 と同じ配列を見る）。
  const ls = latches();
  const breaking = ls.filter((l) => l.sev === 'ng');
  rows.push(breaking.length
    ? { s: 'ng', label: 'ラッチ', detail: `${breaking.map((l) => l.text).join(' / ')} — ⛑ か ▶ ラン開始で解除` }
    : ls.length
      ? { s: 'warn', label: 'ラッチ', detail: `${ls.map((l) => l.text).join(' / ')}（意図的なら問題ありません）` }
      : { s: 'ok', label: 'ラッチ', detail: 'カメラ固定なし / 演出停止中 / 素材スロット未束縛' });

  // 前の体験者の周回が残ったまま次を始めると、1 周目の演出が出ない（lap が進んだ状態から始まる）。
  const lapNow = parseInt(lastUnity && lastUnity.lap, 10) || 0;
  if (unityAlive && lapNow > 1) {
    rows.push({ s: 'warn', label: 'ラン', detail: `${lapNow} 周目の途中です — 次の体験者なら ▶ ラン開始を押す` });
  }

  const segs = timelineSegments();
  const withCue = segs.filter((s) => (s.takes || []).length).length;
  rows.push(withCue
    ? { s: 'ok', label: 'タイムライン', detail: `${segs.length} 区間 / うち演出あり ${withCue}` }
    : { s: 'warn', label: 'タイムライン', detail: '演出を持つ区間がありません（体験は映像切替のみ）' });

  // 🎯 較正。**CG 人形を出すカットが指すカメラ**が未較正だと、実機は概算 pose へ落ちるか
  // 人形を出さない。卓の側は輪郭プロキシを描けてしまうので、押す前に見る場所で言わないと
  // 「卓では出るのに実機で出ない / 場所が違う」を現場で初めて知ることになる。
  {
    const needCalib = new Set();
    for (const s of segs) {
      for (const t of s.takes || []) {
        for (const st of t.steps || []) {
          if (!st.cg) continue;
          // カットが live:<cam> ならそのカメラ、素材・inherit なら区間のカメラに映る
          const idx = (st.source === 'live' && st.camera >= 0) ? st.camera : s.camera;
          if (Number.isInteger(idx) && idx >= 0) needCalib.add(idx);
        }
      }
    }
    // 同じ理由はまとめて 1 文にする（3 台に同じ文が並ぶと読む気が失せて見落とす）。
    const missing = [], stale = [], warn = [], ok = [];
    const lensList = lensesOf({ lenses: state?.lenses || [] });
    cams.forEach((c, i) => {
      const col = columns.get(c.id);
      const w = col?.liveImg?.naturalWidth || 0, h = col?.liveImg?.naturalHeight || 0;
      const cal = c.calib && c.calib.fxPx > 1 ? c.calib : null;
      if (!cal) {
        if (needCalib.has(i)) missing.push(c.id);
        return;                            // 人形を出さないカメラの未較正は黙る（要らないので）
      }
      if (w > 1 && h > 1 && (cal.srcW !== w || cal.srcH !== h)) {
        const t = `${c.id} は ${cal.srcW}×${cal.srcH} 用（いま ${w}×${h}）`;
        (needCalib.has(i) ? stale : warn).push(t);
        return;
      }
      if (cal.accuracyM > 0.15) { warn.push(`${c.id} はずれ ${Math.round(cal.accuracyM * 100)}cm`); return; }
      if (lensTrustIssues(lensList.find((l) => l.id === c.lensRef)).length) {
        warn.push(`${c.id} のレンズは素性が弱い（点が少ない / 高さの点なしで測った画角）`);
        return;
      }
      ok.push(c.id);
    });
    const ng = [];
    if (missing.length) ng.push(`${missing.join(' / ')} が未較正（CG 人形を出すカットがあります）`);
    ng.push(...stale);
    if (ng.length) {
      rows.push({ s: 'ng', label: '🎯 較正', detail: `${ng.join(' ・ ')} — カメラ列の［🎯 カメラを合わせる］で合わせる` });
    } else if (warn.length) {
      rows.push({ s: 'warn', label: '🎯 較正', detail: `${warn.join(' ・ ')}（人形の立ち位置がずれます）` });
    } else if (ok.length) {
      rows.push({ s: 'ok', label: '🎯 較正', detail: `${ok.join(', ')} が較正済み` });
    }
  }

  // 🎬 導入。段 3（構造）は**現実に線を直接重ねる**ので、部屋の寸法が既定値のままだと
  // 位置合わせがどれだけ良くても線が実物に合わない（＝段 3 が現地検証を兼ねる意味も消える）。
  // 導入が無効なら行ごと出さない（判定は intro-model.js が単一の正）。
  {
    const introRow = introPreflightRow({ run: state?.run, layout: state?.layout, cameras: cams });
    if (introRow) rows.push(introRow);
  }

  // 体験の骨格。踏まない区間に演出を書いても**絶対に出ない**（そこへ到達する前に終わる）。
  // 実機だけが黙って落とすので、著作の段階で気づける唯一の面がここ。
  // ⚠ 体験は「3 周 ＋ 元の位置へ戻る区間」で終わるので、もどりの区間（lap = 周数 + 1 の
  //   スタート領域）は**死んでいない**。判定は run-model.js が単一の正。
  {
    const run = runConfig(state);
    const laps = runTotalLaps(state);
    const order = courseOrder();
    const authoredMax = segs.reduce((m, s) => Math.max(m, s.lap || 0), 0);
    const dead = segs.filter((s) => (s.takes || []).length
      && !isSegmentReachable(s.lap || 0, s.camera, laps, order));
    // もどりの区間は入った瞬間に終了へ向かうので、猶予を超える開始位置の演出は実機で必ず出ない。
    const late = [];
    for (const s of segs) {
      if (!isSegmentReachable(s.lap || 0, s.camera, laps, order)) continue;
      if ((s.lap || 0) !== laps + 1) continue;
      for (const t of s.takes || []) {
        if (t.at !== 'enter' || !returnTakeTooLate(t.offsetSec, run)) continue;
        late.push(`${t.name || t.id || '演出'}（+${t.offsetSec}s）`);
      }
    }
    if (dead.length) {
      rows.push({ s: 'ng', label: '体験の骨格',
        detail: `${laps} 周＋もどりで終わる設定なのに ${dead.map((s) => `${s.lap}周目 ${camLabelOf(s.camera)}`).join(' / ')} に演出があります — 出ません（周数を増やすか演出を移す）` });
    } else if (late.length) {
      rows.push({ s: 'ng', label: '体験の骨格',
        detail: `もどりの区間は入った直後に終了へ向かいます（待つのは ${run.endGraceSec}s）。`
          + `${late.join(' / ')} はこの開始位置では実機で出ません — 開始を早めるか離脱時にする` });
    } else if (authoredMax > 0 && authoredMax < laps) {
      rows.push({ s: 'warn', label: '体験の骨格',
        detail: `${laps} 周走る設定ですが演出は ${authoredMax} 周目までです（最後の周は映像切替だけになります）` });
    } else {
      // ⚠ 導入は「演出（run.intro の段の合計）＋ 慣らし歩行（introMinSec）」。
      //   introMinSec だけを出すと**演出のぶん（既定 13.1s）が予算から丸ごと落ちる**。
      //   3 分の予算を現場で守るための行なので、実際に流れる秒を出す。
      const introSec = run.introEnabled
        ? (run.intro && run.intro.enabled !== false ? introStageSec(introConfig(run)) : 0) + (run.introMinSec || 0)
        : 0;
      const introTxt = run.introEnabled
        ? `導入 ${Math.round(introSec)}s（${introDurationLabel(run.intro, run.introMinSec)}）`
        : '導入なし';
      rows.push({ s: 'ok', label: '体験の骨格',
        detail: `${laps} 周＋もどり / ${introTxt} / 目安 ${run.targetSec}s` });
    }
  }

  // 映像の遅れ。企画書は 100ms 程度以内を目標に「管理する」と書いている。
  // ここで出すのは Unity が観測できる分だけ（絶対の end-to-end は端末間の時計を結べないと測れない）。
  if (unityAlive) {
    const u = lastUnity || {};
    const sum = Math.round((Number(u.latencyJitterMs) || 0) + (Number(u.latencyDecodeMs) || 0)
      + (Number(u.latencyPresentMs) || 0));
    if (Number(u.throttleStage) > 0) {
      rows.push({ s: 'ng', label: '映像の遅れ',
        detail: `配信端末が発熱で品質を落としています（段 ${u.throttleStage}）— 冷ますか送風。観測 ${sum}ms` });
    } else if (sum > 60) {
      rows.push({ s: 'warn', label: '映像の遅れ', detail: `観測 ${sum}ms（揺らぎ ${Math.round(Number(u.latencyJitterMs) || 0)}ms）— Wi-Fi の混雑を疑う` });
    } else {
      rows.push({ s: 'ok', label: '映像の遅れ', detail: `観測 ${sum}ms（撮影・エンコード・伝送の下限は含まない）` });
    }
  }

  // 参照素材の実在チェック（📦 エクスポート前に気づけるようにする）。
  //   v3 は カット自身が素材 URL を持つので、cue 参照とは別に assetUrl 空のカットも拾う
  //   （空だと実機でそのカットだけ映像が出ない）。
  const cueMap = new Map((state?.cues || []).map((c) => [c.id, c]));
  const missing = [], noSrc = [], emptyAsset = [], refUrls = [];
  for (const s of segs) {
    for (const id of segCueIds(s)) {
      const cue = cueMap.get(id);
      if (!cue) { if (!missing.includes(id)) missing.push(id); }
      else if (!cue.sourceUrl) { if (!noSrc.includes(id)) noSrc.push(id); }
      else { refUrls.push(cue.sourceUrl); if (cue.maskUrl) refUrls.push(cue.maskUrl); }
    }
    for (const t of (s.takes || [])) {
      for (const st of (t.steps || [])) {
        if (st.source !== 'clip' && st.source !== 'still') continue;
        if (!st.assetUrl) {
          const label = t.name || t.id || '演出';
          if (!emptyAsset.includes(label)) emptyAsset.push(label);
        } else refUrls.push(st.assetUrl);
      }
    }
  }
  checkAssetsExist(refUrls);
  // ファイルが消えている / 名前が変わっている参照。これを見ないと 📦 エクスポートまで気づけず、
  //   卓を使わない現場なら実機でその映像が出ない瞬間まで分からない。
  const gone = assetMissing.filter((u) => refUrls.includes(u));
  if (missing.length) rows.push({ s: 'ng', label: '演出素材', detail: `未定義の素材: ${missing.join(', ')}` });
  else if (emptyAsset.length) rows.push({ s: 'ng', label: '演出素材', detail: `映像が未選択のカットがある: ${emptyAsset.join(', ')}` });
  else if (gone.length) rows.push({ s: 'ng', label: '演出素材', detail: `ファイルが見つからない: ${gone.map((u) => u.split('/').pop()).join(', ')} — 消したか名前を変えた可能性` });
  else if (noSrc.length) rows.push({ s: 'warn', label: '演出素材', detail: `素材未設定: ${noSrc.join(', ')}` });
  else rows.push({ s: 'ok', label: '演出素材', detail: `参照素材 ${new Set(refUrls).size} 件はすべて実在・映像あり` });

  // 端末内録画: 「録画」カットが指す (周, カメラ) を実機が本当に録るか。
  //   ここが食い違うと実機はそのカットを黙って飛ばす（卓には何も出ない）。参照が無ければ行ごと出さない。
  const rec = recCoverage();
  if (rec.refs.length) {
    rows.push(rec.bad.length
      ? {
        s: 'ng', label: '端末内録画',
        detail: `${rec.bad.map((r) => `${r.lap > 0 ? `${r.lap}周目 ${camLabelOf(r.camera)}` : '未指定'}（${r.reason}）`).join(' / ')}`
          + ' — ⏺ 端末内録画パネルで直す（このままだと実機はそのカットを飛ばします）',
      }
      : { s: 'ok', label: '端末内録画', detail: `録る周 ${[...rec.laps].sort((a, b) => a - b).join(', ')} / 「録画」カット ${rec.refs.length} 本はすべて録る対象` });
  }

  // BGM: タイムラインとラン既定が指すトラックが実在するか（実機で無音に化けるのを防ぐ）
  const trackIds = new Set((state?.bgmTracks || []).map((t) => t.id));
  trackIds.add(DEFAULT_BGM_TRACK_ID);   // APK 同梱の既定クリップは常に有効
  const badTracks = [];
  const rb = state?.bgm;
  if (rb && rb.action === 'play' && rb.trackId && !trackIds.has(rb.trackId)) badTracks.push(`ラン既定=${rb.trackId}`);
  for (const s of segs) {
    if (!s.hasBgm || !s.bgm || s.bgm.action !== 'play') continue;
    const id = s.bgm.trackId;
    if (!id || !trackIds.has(id)) badTracks.push(`Lap${s.lap}/${camLabelOf(s.camera)}=${id || '未選択'}`);
  }
  if (badTracks.length) rows.push({ s: 'ng', label: 'BGM', detail: `未定義のトラック: ${badTracks.join(', ')}` });
  else if (state?.bgmTracks?.length || (rb && rb.action !== 'continue')) {
    rows.push({ s: 'ok', label: 'BGM', detail: `トラック ${(state?.bgmTracks || []).length} 本 / 参照はすべて実在` });
  }

  const d = anyDirty();
  rows.push(d.any
    ? { s: 'warn', label: '未保存', detail: `${[d.timeline && 'タイムライン', d.floorMap && 'フロアマップ'].filter(Boolean).join(' / ')} が未保存（💾 で保存）` }
    : { s: 'ok', label: '未保存', detail: 'なし' });
  return rows;
}
function renderPreflight() {
  const wrap = $('#preflight'); if (!wrap) return;
  const rows = preflightRows();
  const mark = { ok: '✅', warn: '⚠', ng: '❌' };
  wrap.innerHTML = '';
  for (const r of rows) {
    const el = document.createElement('div');
    el.className = 'pf-row ' + r.s;
    el.innerHTML = `<span class="pf-mark">${mark[r.s]}</span><span class="pf-label">${escapeHtml(r.label)}</span>`
      + `<span class="pf-detail">${escapeHtml(r.detail)}</span>`;
    wrap.appendChild(el);
  }
}

// 未保存のままタブを閉じる / リロードするのを止める（show.json 保存前の編集は復元できない）。
window.addEventListener('beforeunload', (e) => {
  if (!anyDirty().any) return;
  e.preventDefault();
  e.returnValue = '';
});

if ($('#recAllRaw')) $('#recAllRaw').onclick = () => recordAll('raw');
if ($('#recAllView')) $('#recAllView').onclick = () => recordAll('view');

// 境界ブレンド設定（全カメラ共通）の配線
const bindBlend = (id, fn) => { const el = $('#' + id); if (el) el.oninput = () => fn(el); };
bindBlend('bFeather', (el) => { blendCfg.feather = parseFloat(el.value); });
bindBlend('bColor', (el) => { blendCfg.colorMatch = el.checked; });
bindBlend('bColorStr', (el) => { blendCfg.colorStrength = parseFloat(el.value); });
bindBlend('bLap', (el) => { blendCfg.laplacian = el.checked; });
bindBlend('bLevels', (el) => { blendCfg.levels = parseInt(el.value, 10); $('#bLevelsV').textContent = el.value; });

// 既定は common.js の blendCfg が単一の正。DOM の初期値はそこから流す。
//   HTML の value/checked と JS の既定を二重に書くと、片方だけ変えたときに沈黙して食い違う
//   （ラプラシアンを既定 OFF にしたとき実際に踏みかけた）。
const syncBlendUi = () => {
  const set = (id, fn) => { const el = $('#' + id); if (el) fn(el); };
  set('bFeather', (el) => { el.value = blendCfg.feather; });
  set('bColor', (el) => { el.checked = blendCfg.colorMatch; });
  set('bColorStr', (el) => { el.value = blendCfg.colorStrength; });
  set('bLap', (el) => { el.checked = blendCfg.laplacian; });
  set('bLevels', (el) => { el.value = blendCfg.levels; });
  set('bLevelsV', (el) => { el.textContent = String(blendCfg.levels); });
};
syncBlendUi();
refreshCaptures();

// ---- 生成プロンプト（旧 prompts.json）は 2026-07-28 に UI ごと撤去した ----
//   結果と紐づかないプロンプト本文だけのストアで、素材工房（atelier）が「生成」を
//   種フレーム・レシピ・束縛値・出力・採否のひとかたまりで残す形に置き換えている。
//   サーバの /prompts と prompts.json は参照用に残置（読み出す UI はもう無い）。

// ---- フロアマップ（ゾーン校正）---------------------------------------------
if ($('#floorMap')) {
  floorMap = createFloorMap($('#floorMap'), {
    getCameras: () => state?.cameras || [],
    saveLayout: (layout) => postState({ layout }),
    // カメラ姿勢（📐 モード）は layout と違い「その場保存」。カメラ列の 📐 欄と同じ扱い。
    saveCameras: (cams) => postState({ cameras: cams }),
    // シミュレーションドットの有効状態・位置を 🕹 ショーシミュレーションへ流す（下で生成）。
    onSim: (e) => showSim && showSim.onSimDot(e),
  });
  if (state) floorMap.onState(state);
}

// ---- 🎯 カメラを手で合わせる ---------------------------------------------------
//   カメラ列の［🎯 カメラを合わせる］から開く全画面オーバーレイ。保存先は cameras[i].calib。
//   **自動求解はやめた**（2026-08-04）— 実測で 4 台とも一度も解けておらず、原因は数値ではなく
//   入力（既定値の壁・仮想の格子から作った、現場に実在しない候補点）だった。
//   いまは実映像へ部屋のワイヤーと人形を重ねて、人が見ながらカメラを動かす。
calibUi = createAlignUi(document.body, {
  getCameras: () => state?.cameras || [],
  getLayout: () => state?.layout || null,
  // 静止フレームの元。カメラ列の <img>（卓サーバの /cam プロキシ + crossOrigin=anonymous なので
  // canvas が汚染されず読み出せる。列の 📷 キャプチャと同じ経路）。
  getLiveImg: (camId) => { const c = columns.get(camId); return c ? c.liveImg : null; },
  // 較正の基準点（`layout.calibPoints`）を地図から置けるようにするための保存口。
  // フロアマップと同じ経路（show.json の layout を丸ごと置く）を通す。
  saveLayout: (layout) => { if (state) state.layout = layout; return postState({ layout }); },
  // レンズ（内部パラメータ）。**卓だけの概念**で Unity は読まない（解いた値は calib.fxPx に入る）。
  // 画角を固定する値の供給源をカメラから切り離すことで、同型機で使い回せて素性も追えるようになる。
  getLenses: () => state?.lenses || [],
  saveLenses: (lenses) => { if (state) state.lenses = lenses; return postState({ lenses }); },
  saveCameras: async (cams) => {
    if (state) state.cameras = cams;
    const r = await postState({ cameras: cams });
    renderColumns();                                    // 列のバッジを即更新（long-poll を待たない）
    window.dispatchEvent(new CustomEvent('fc-cameras-changed'));
    return r;
  },
});

// ---- カメラを増やす（4 台目＝演出専用のカメラ D 用）--------------------------
//   index は Unity 側 CameraStreamRegistry.sources[] の並びと 1 対 1（末尾に足すので既存は動かない）。
if ($('#camAdd')) {
  $('#camAdd').onclick = () => {
    if (!state) return;
    const cams = state.cameras || (state.cameras = []);
    const n = cams.length;
    const id = String.fromCharCode(65 + n);            // 0→A, 3→D …
    const sourceId = `Phone${String(n + 1).padStart(2, '0')}`;
    // 4 台目以降は「演出専用」を既定にする（周回ゾーンは 3 台構成が前提のため）。
    cams.push({ id, sourceId, host: '', port: 8080, auth: '', pinned: false, role: n >= 3 ? 'fx' : 'zone' });
    postState({ cameras: cams });
    renderColumns();
    window.dispatchEvent(new CustomEvent('fc-cameras-changed'));
    const msg = $('#camAddMsg');
    if (msg) {
      msg.textContent = `カメラ ${id} を追加（${sourceId} / ${n >= 3 ? '演出専用' : 'ゾーン'}）`;
      setTimeout(() => { msg.textContent = ''; }, 5000);
    }
  };
}

// ---- 🎭 CG 人形（actors[]）---------------------------------------------------
if ($('#actorsPanel')) {
  actorsPanel = createActorsPanel($('#actorsPanel'), {
    // ローカル state も即更新する（long-poll の往復を待たずにリボンの選択肢へ反映させるため）。
    save: (actors) => { if (state) state.actors = actors; postState({ actors }); },
  });
  if (state) actorsPanel.onState(state);
}

// カメラの役割 / 姿勢はカメラ列とフロアマップの両方から編集できる。
// どちらで変えても即座にもう一方へ反映する（long-poll の往復を待たせない）。
window.addEventListener('fc-cameras-changed', () => {
  for (const c of columns.values()) {
    c.syncRole && c.syncRole();
    c.syncPose && c.syncPose();
    c.syncCalib && c.syncCalib();
  }
  if (floorMap && state) floorMap.onState(state);
});

// 人形を足した / 消したら、演出リボンの「CG 人形」選択肢を即座に追従させる。
window.addEventListener('fc-actors-changed', () => {
  if (timeline && state) timeline.onState(state);
});

// ---- 🕹 ショーシミュレーション（実機なし検証）--------------------------------
if ($('#showSim')) {
  showSim = createShowSim($('#showSim'), {
    getState: () => state,
    // シナリオ再実行中はシミュレータが歩きを再現するのでドットを動かす。
    setDot: (x, z) => floorMap && floorMap.setSimPos(x, z),
    // 画面プレビュー用のライブ映像（カメラ列の <img>）。未接続なら null = 黒背景に素材だけ出る。
    getLiveImg: (camId) => { const c = columns.get(camId); return c ? c.liveImg : null; },
  });
  if (state) showSim.onState(state);
}

// ---- 周回タイムライン（第一級オーサリング）---------------------------------
if ($('#timeline')) {
  timeline = createRibbon($('#timeline'), {
    getCameras: () => state?.cameras || [],
    getCues: () => state?.cues || [],
    getActors: () => state?.actors || [],
    // ⏺ 端末内録画の設定。「録画」カットが実機で本当に録れるかをリボンが照合する。
    getRecord: () => state?.record || null,
    // 体験の骨格（走り切る周数）。リボンが並べる周はこれだけが正で、リボン側からは書き換えない。
    getRun: () => state?.run || null,
    getCourseOrder: () => state?.layout?.course?.order || null,
    // 通過ライン（layout.lines）と grid をリボンが読む（開始規則「このラインを通過したら」の選択肢・警告）。
    getLayout: () => state?.layout || null,
    getGlobalPost: () => state?.post || FX_DEFAULT,
    getBgmTracks: () => state?.bgmTracks || [],
    getRootBgm: () => state?.bgm || null,
    getLiveImg: (camId) => { const c = columns.get(camId); return c ? c.liveImg : null; },
    getCaptures: () => captures.items,
    refreshCaptures: () => refreshCaptures(),
    // 実測滞在時間（区間ごと）。無ければ空 = リボンは「滞在は体験者しだい」表示のまま。
    getDwellStats: () => dwellStats,
    resetDwell: async () => {
      try { await fetch('/dwell/reset', { method: 'POST' }); } catch { return false; }
      dwellStats = { items: {}, updatedAt: 0 };
      return true;
    },
    // ▶ 検証は 🕹 ショーシミュレーションへ一本化した（矢印キーの簡易シミュレーションは廃止）。
    openSimulator: () => focusSimulator(),
    // 素材づくりの導線: 撮る（recordings/）→ 合成 → カットの素材に選ぶ。
    openCaptureDir: () => fetch('/open-dir?dir=recordings').catch(() => {}),
    // cue を state.cues へ upsert（メモリ state も即反映して UI をスナップに保つ）。
    saveCue: async (cue) => {
      const r = await saveCueObject(cue);
      if (state && r.cues) state.cues = r.cues;
      return r;
    },
    deleteCue: async (id) => {
      const r = await deleteCueObject(id);
      if (state && r.cues) state.cues = r.cues;
      return r;
    },
    // timeline 保存: timeline を書き、schedule.entries を空化 + rev++（Unity supersede と整合）。
    saveTimeline: (tl) => postState({
      timeline: tl,
      schedule: { rev: (parseInt(state?.schedule?.rev, 10) || 0) + 1, entries: [] },
    }),
  });
  if (state) timeline.onState(state);
}

// ---- 🎵 BGM ライブラリ（audio/ の音源 → bgmTracks / ラン既定 bgm）--------------
//   トラック = 音源 + ループ範囲 + 音量。タイムラインの区間はこの id を指すだけにして、
//   同じ曲を複数区間から使い回せるようにする（cue と同じ考え方）。
const DEFAULT_BGM_TRACK_ID = '__default__';   // APK 同梱の既定クリップ（Unity BgmDirector と一致）
let audioFiles = [];
let bgmPreviewEl = null;

async function loadAudioFiles() {
  try { audioFiles = await (await fetch('/audio/list')).json(); }
  catch { audioFiles = []; }
  const sel = $('#bgmFileSelect');
  if (!sel) return;
  const cur = sel.value;
  sel.innerHTML = audioFiles.length
    ? audioFiles.map((f) => `<option value="${escapeHtml(f.url)}">${escapeHtml(f.name)}（${Math.round(f.size / 1024)}KB）</option>`).join('')
    : '<option value="">（audio/ に音源がありません）</option>';
  // 選択の復元は「その音源がまだある時だけ」。無条件に戻すと初回ロードで
  // 空文字を書き戻して選択が外れる（＝＋登録が無反応になる）。
  if (cur && audioFiles.some((f) => f.url === cur)) sel.value = cur;
}

function bgmTracksState() { return state?.bgmTracks || []; }

async function saveBgmTracks(tracks) {
  if (state) state.bgmTracks = tracks;
  return postState({ bgmTracks: tracks });
}

function stopBgmLibPreview() {
  if (bgmPreviewEl) { bgmPreviewEl.pause(); bgmPreviewEl = null; }
}

function renderBgmSection() {
  const list = $('#bgmTrackList');
  if (!list) return;
  const tracks = bgmTracksState();

  // ラン既定セレクタ（show.json トップレベル bgm）
  const rootSel = $('#bgmRootTrack');
  if (rootSel && document.activeElement !== rootSel) {
    const rb = state?.bgm || {};
    const curId = rb.action === 'play' ? (rb.trackId || '') : '';
    rootSel.innerHTML = `<option value="">（APK 同梱の既定 BGM）</option>`
      + tracks.map((t) => `<option value="${escapeHtml(t.id)}">${escapeHtml(t.name || t.id)}</option>`).join('')
      + `<option value="__silent__">（無音で始める）</option>`;
    rootSel.value = curId === '' && rb.action === 'stop' ? '__silent__' : curId;
  }

  list.innerHTML = '';
  if (!tracks.length) {
    const e = document.createElement('div');
    e.className = 'bgm-empty';
    e.textContent = '（トラック未登録。audio/ に mp3 等を置いて「＋ トラックに登録」）';
    list.appendChild(e);
    return;
  }
  tracks.forEach((t, i) => {
    const row = document.createElement('div');
    row.className = 'bgm-item';
    row.innerHTML = `
      <div class="bgm-item-head">
        <input class="bgm-name" type="text" value="${escapeHtml(t.name || '')}" placeholder="表示名">
        <span class="bgm-id">${escapeHtml(t.id)}</span>
        <span class="spacer"></span>
        <button class="bgm-play" title="試聴（ループ範囲つき）">🔊</button>
        <button class="bgm-stop" title="停止">■</button>
        <span class="bgm-time">—</span>
        <button class="bgm-del" title="トラックを削除">🗑</button>
      </div>
      <div class="bgm-item-grid">
        <span class="bgm-file" title="${escapeHtml(t.url)}">${escapeHtml((t.url || '').split('/').pop())}</span>
        <label>ループ in<input class="bgm-ls" type="number" min="0" step="0.1" value="${t.loopStartSec || 0}">s</label>
        <label>ループ out<input class="bgm-le" type="number" min="0" step="0.1" value="${t.loopEndSec || 0}">s<span class="bgm-hint">0=末尾</span></label>
        <label>音量<input class="bgm-vol" type="number" min="0" max="1" step="0.05" value="${t.volume ?? 1}"></label>
        <button class="bgm-mark-in" title="試聴中の位置をループ in に">ここを in</button>
        <button class="bgm-mark-out" title="試聴中の位置をループ out に">ここを out</button>
      </div>`;
    const q = (s) => row.querySelector(s);
    const commit = () => {
      const arr = bgmTracksState().slice();
      arr[i] = {
        ...arr[i],
        name: q('.bgm-name').value,
        loopStartSec: Math.max(0, parseFloat(q('.bgm-ls').value) || 0),
        loopEndSec: Math.max(0, parseFloat(q('.bgm-le').value) || 0),
        volume: Math.min(1, Math.max(0, parseFloat(q('.bgm-vol').value))) || 0,
      };
      saveBgmTracks(arr);
    };
    row.querySelectorAll('.bgm-name, .bgm-ls, .bgm-le, .bgm-vol').forEach((el) => { el.onchange = commit; });
    q('.bgm-play').onclick = () => {
      stopBgmLibPreview();
      bgmPreviewEl = new Audio(t.url);
      bgmPreviewEl.volume = Math.min(1, Math.max(0, t.volume ?? 1));
      bgmPreviewEl.currentTime = t.loopStartSec || 0;
      bgmPreviewEl.ontimeupdate = () => {
        if (!bgmPreviewEl) return;
        q('.bgm-time').textContent = `${bgmPreviewEl.currentTime.toFixed(1)}s`;
        const le = parseFloat(q('.bgm-le').value) || 0;
        if (le > 0 && bgmPreviewEl.currentTime >= le) bgmPreviewEl.currentTime = parseFloat(q('.bgm-ls').value) || 0;
      };
      bgmPreviewEl.play().catch(() => { q('.bgm-time').textContent = '再生不可'; });
    };
    q('.bgm-stop').onclick = () => { stopBgmLibPreview(); q('.bgm-time').textContent = '—'; };
    q('.bgm-mark-in').onclick = () => { if (bgmPreviewEl) { q('.bgm-ls').value = bgmPreviewEl.currentTime.toFixed(1); commit(); } };
    q('.bgm-mark-out').onclick = () => { if (bgmPreviewEl) { q('.bgm-le').value = bgmPreviewEl.currentTime.toFixed(1); commit(); } };
    q('.bgm-del').onclick = () => {
      const used = timelineSegments().some((s) => s.hasBgm && s.bgm && s.bgm.trackId === t.id);
      if (used && !confirm(`このトラックはタイムラインで使われています。削除すると該当区間は「未定義」になります。削除しますか？`)) return;
      stopBgmLibPreview();
      saveBgmTracks(bgmTracksState().filter((_, k) => k !== i));
    };
    list.appendChild(row);
  });
}

if ($('#bgmAddTrack')) {
  $('#bgmAddTrack').onclick = () => {
    const url = $('#bgmFileSelect').value;
    if (!url) return;
    const base = decodeURIComponent(url.split('/').pop() || 'bgm');
    const stem = base.replace(/\.[^.]+$/, '');
    const tracks = bgmTracksState().slice();
    // id は衝突しない安定値（同じ音源を別ループ範囲で 2 本登録できるよう連番）
    let n = 1, id = `bgm_${stem}`.replace(/[^A-Za-z0-9_\-]/g, '_');
    while (tracks.some((t) => t.id === id)) id = `bgm_${stem}_${++n}`.replace(/[^A-Za-z0-9_\-]/g, '_');
    tracks.push({ id, name: stem, url, loopStartSec: 0, loopEndSec: 0, volume: 0.5 });
    saveBgmTracks(tracks);
  };
}
if ($('#bgmRefreshFiles')) $('#bgmRefreshFiles').onclick = () => loadAudioFiles();
if ($('#bgmOpenDir')) $('#bgmOpenDir').onclick = () => fetch('/open-dir?dir=audio').catch(() => {});
if ($('#bgmRootTrack')) {
  $('#bgmRootTrack').onchange = async (e) => {
    const v = e.target.value;
    const cur = state?.bgm || {};
    const next = {
      action: v === '' ? 'continue' : (v === '__silent__' ? 'stop' : 'play'),
      trackId: v === '' || v === '__silent__' ? '' : v,
      loop: true, startSec: 0, loopStartSec: -1, loopEndSec: -1, volume: -1,
      fadeInSec: cur.fadeInSec ?? 1, fadeOutSec: cur.fadeOutSec ?? 1, restart: false,
    };
    if (state) state.bgm = next;
    const r = await postState({ bgm: next });
    const st = $('#bgmRootState');
    if (st) {
      st.textContent = (r && r.ok !== false)
        ? (v === '' ? '✓ APK 同梱の既定 BGM で開始' : v === '__silent__' ? '✓ 無音で開始' : '✓ 適用')
        : '✕ 保存失敗';
      st.className = 'ed-status ' + (r && r.ok !== false ? 'ok' : 'err');
    }
  };
}
loadAudioFiles();

// ---- ビルド用エクスポート ----------------------------------------------------
if ($('#exportBuild')) {
  $('#exportBuild').onclick = async () => {
    const r = $('#exportResult');
    // 未保存の編集は show.json に無い＝焼き込まれない。黙って古い内容を焼かせない。
    const d = anyDirty();
    if (d.any) {
      const names = [d.timeline && 'タイムライン', d.floorMap && 'フロアマップ'].filter(Boolean).join(' / ');
      r.textContent = `✕ ${names}が未保存です。先に 💾 保存してからエクスポートしてください`;
      r.className = 'ed-status err';
      return;
    }
    r.textContent = 'エクスポート中…'; r.className = 'ed-status';
    try {
      const res = await (await fetch('/export-build', { method: 'POST' })).json();
      if (!res.ok) throw new Error(res.error || 'エクスポート失敗');
      const mb = (res.totalBytes / 1024 / 1024).toFixed(2);
      r.textContent = `✓ ${res.exportedAt || ''}（rev ${res.showRev}）/ ${res.count} ファイル ${mb}MB → ${res.outDir}`;
      r.className = 'ed-status ok';
      const fl = $('#exportFiles'); fl.innerHTML = '';
      // 焼き込んだカメラ接続先を最上段に出す。APK には現地 DHCP の IP がそのまま入るので、
      // 「いつ・どの IP で焼いたか」が後から追える唯一の情報になる。
      if (res.hosts && res.hosts.length) {
        const h = document.createElement('div');
        h.className = 'export-hosts';
        h.innerHTML = `<b>焼き込んだ接続先</b>（現地 IP が固定される。IP が変わったら焼き直し）: `
          + res.hosts.map((x) => `<span class="export-host">${escapeHtml(x.id)} = ${escapeHtml(x.host || '未設定')}:${x.port}${x.pinned ? ' 📌' : ''}</span>`).join(' ');
        fl.appendChild(h);
      }
      if (res.unresolvedAssets && res.unresolvedAssets.length) {
        const w = document.createElement('div');
        w.className = 'export-missing';
        w.textContent = `⚠ 実ファイルが見つからない素材: ${res.unresolvedAssets.join(', ')}`
          + '（APK に入りません。現地で PC が無いとこのカットは無映像になります）';
        fl.appendChild(w);
      }
      if (res.missingCues && res.missingCues.length) {
        const w = document.createElement('div');
        w.className = 'export-missing';
        w.textContent = `⚠ 参照先が存在しない cue: ${res.missingCues.join(', ')}（この区間は実機で何も出ません）`;
        fl.appendChild(w);
      }
      if (res.missingTracks && res.missingTracks.length) {
        const w = document.createElement('div');
        w.className = 'export-missing';
        w.textContent = `⚠ 参照先が存在しない BGM トラック: ${res.missingTracks.join(', ')}（その区間で曲は変わりません）`;
        fl.appendChild(w);
      }
      for (const c of res.copied) {
        const d = document.createElement('div'); d.className = 'export-file';
        d.textContent = `${c.from} → ${c.to}（${Math.round(c.size / 1024)}KB）`;
        fl.appendChild(d);
      }
      if (!res.copied.length) { const d = document.createElement('div'); d.className = 'export-file'; d.textContent = '（コピー対象アセットなし。show.json のみ出力）'; fl.appendChild(d); }
    } catch (e) { r.textContent = '✕ ' + e.message; r.className = 'ed-status err'; }
  };
}

// ---- 📡 発見済み端末（fixedcam-discovery/1）--------------------------------
const roleLabel = (r) => (r === 'show-server' ? '🖥 PC 卓' : '📷 カメラ');
function fmtAge(sec) {
  if (sec == null) return '—';
  if (sec < 1) return 'now';
  return sec < 60 ? `${Math.round(sec)}s前` : `${Math.round(sec / 60)}m前`;
}
function renderDiscovery(data) {
  const list = $('#discList'); if (!list) return;
  const conflicts = new Set(data.conflicts || []);
  const devices = data.devices || [];
  const af = $('#autoFollow');
  if (af && document.activeElement !== af) af.checked = data.autoFollow !== false;
  const de = $('#discoveryEnabled');
  if (de && document.activeElement !== de) de.checked = data.discoveryEnabled !== false;
  const info = $('#autoFollowInfo');
  if (info) {
    if (data.lastFollow && data.lastFollow.changes && data.lastFollow.changes.length) {
      const ch = data.lastFollow.changes.map((c) => `${c.id}→${c.host}`).join(', ');
      const ago = fmtAge((Date.now() / 1000) - data.lastFollow.at);
      info.textContent = `直近の追従: ${ch}（${ago}）`;
    } else info.textContent = '';
  }
  const meta = $('#discMeta');
  if (meta) {
    meta.textContent = data.enabled === false ? 'discovery 無効'
      : `show="${data.showToken || ''}" / ${devices.length} 台`;
    meta.className = 'disc-meta' + (data.enabled === false ? ' off' : '');
  }
  list.innerHTML = '';
  if (!devices.length) {
    const e = document.createElement('div');
    e.className = 'disc-empty';
    e.textContent = data.enabled === false ? '（discovery 無効）' : '（まだ発見なし — 配信アプリの起動と同一 LAN を確認）';
    list.appendChild(e); return;
  }
  for (const d of devices) {
    const isConf = d.role === 'camera' && conflicts.has(d.id);
    const card = document.createElement('div');
    card.className = 'disc-item' + (isConf ? ' conflict' : '') + (d.self ? ' self' : '');
    const idTxt = d.id && d.id !== '?' ? d.id : '（ID 未設定）';
    card.innerHTML = `
      <div class="disc-id">${roleLabel(d.role)} <b>${escapeHtml(idTxt)}</b>${d.self ? ' <span class="disc-tag">自機</span>' : ''}${isConf ? ' <span class="disc-tag warn">⚠ 二重 ID</span>' : ''}</div>
      <div class="disc-meta-row">
        <span class="disc-ip">${d.ip}:${d.port}</span>
        <span class="disc-ver">v${d.version || '?'}</span>
        <span class="disc-age">${fmtAge(d.ageSec)}</span>
      </div>
      <div class="disc-name">${escapeHtml(d.name || '')} <span class="disc-uuid">${(d.uuid || '').slice(0, 14)}</span></div>`;
    list.appendChild(card);
  }
}
async function pollDiscovery() {
  for (;;) {
    try {
      const d = await (await fetch('/discovery')).json();
      lastDiscovery = d;
      renderDiscovery(d);
      for (const r of columns.values()) r.applyDiscovery && r.applyDiscovery(d);
    } catch { /* offline */ }
    await new Promise((r) => setTimeout(r, 3000));
  }
}
if ($('#discoveryEnabled')) {
  $('#discoveryEnabled').onchange = (e) => postCommand({ type: 'setDiscoveryEnabled', on: e.target.checked });
}
if ($('#autoFollow')) {
  $('#autoFollow').onchange = (e) => postCommand({ type: 'setAutoFollow', on: e.target.checked });
}

// ---- 🩺 疎通診断（PC→カメラ /info + ビーコン + Quest heartbeat）--------------
const okMark = (v) => (v === true ? '✅' : v === false ? '❌' : '—');
if ($('#runDiag')) {
  $('#runDiag').onclick = async () => {
    const st = $('#diagStatus'), out = $('#diagResult');
    st.textContent = '診断中…（各カメラの /info へ接続）'; st.className = 'ed-status';
    out.innerHTML = '';
    let d;
    try { d = await (await fetch('/diag')).json(); }
    catch (e) { st.textContent = '✕ 診断失敗: ' + e.message; st.className = 'ed-status err'; return; }
    const qs = d.quest || {};
    const questRow = document.createElement('div');
    questRow.className = 'diag-quest';
    questRow.innerHTML = `<b>Quest heartbeat</b> ${okMark(qs.alive)} ${qs.alive ? `（${qs.activeCamera || '?'} / ${fmtAge(qs.ageSec)}）` : '（未受信）'}`;
    out.appendChild(questRow);
    let apSuspect = false;
    for (const c of (d.cameras || [])) {
      const beaconOkHttpNg = c.beacon === true && c.http === false;
      if (beaconOkHttpNg) apSuspect = true;
      const row = document.createElement('div');
      row.className = 'diag-row' + (beaconOkHttpNg ? ' ap-suspect' : '');
      row.innerHTML = `
        <span class="diag-cam"><b>${escapeHtml(c.id)}</b>${c.pinned ? ' 📌' : ''}</span>
        <span class="diag-http">/info ${okMark(c.http)} <span class="diag-detail">${c.host ? `${c.host}:${c.port}` : ''} ${escapeHtml(c.httpDetail || '')}</span></span>
        <span class="diag-beacon">ビーコン ${okMark(c.beacon)} <span class="diag-detail">${c.beaconIp ? `${c.beaconIp} / ${fmtAge(c.beaconAgeSec)}` : '未受信'}</span></span>`;
      out.appendChild(row);
    }
    if (apSuspect) {
      const w = document.createElement('div');
      w.className = 'diag-warn';
      w.textContent = '⚠ ビーコンは届くのに /info に繋がらないカメラがある → AP のクライアント間通信遮断（アイソレーション）が濃厚。自前 AP の持ち込みを検討。';
      out.appendChild(w);
    }
    st.textContent = '✓ 完了'; st.className = 'ed-status ok';
  };
}

buildGlobalFx();
// 作業面の復元は全部の初期化が済んでから（上の restoreMode の注意書きを参照）。
restoreMode();
pollState();
pollUnity();
pollDiscovery();
