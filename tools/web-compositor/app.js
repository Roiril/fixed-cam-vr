// 廻リ視 web compositor — 1 ページ統合。
//   🎬 事前オーサリング = 演出・カットのリボン（ribbon.js）。カメラ列は監視専用に格下げ。
//   🚨 ライブ運用 = 発見 / 診断 / ラン状態 / cue 手動発火。
//   show.json が唯一の正。ビューは Unity ScreenComposite を WebGL で再現（composite-view.js）。
import {
  FX, FX_DEFAULT, blendCfg, camColor, escapeHtml,
  streamBase, getState, postState, postCommand,
  captures, refreshCaptures, createMediaCache, saveCueObject,
} from './common.js';
import { createCompositeView } from './composite-view.js';
import { createFloorMap } from './floormap.js';
import { createRibbon } from './ribbon.js';
import { normalizeTimelineV3 } from './timeline-model.js';
import { createShowSim } from './show-sim.js';
import { createAtelier } from './atelier.js';

const $ = (s) => document.querySelector(s);
const MW = 640, MH = 360;

// ---- サーバ I/O（カメラ push のデバウンス）----------------------------------
let pushTimer = 0;
function pushCamerasDebounced() {
  clearTimeout(pushTimer);
  pushTimer = setTimeout(() => postState({ cameras: state.cameras }), 140);
}

// ---- グローバル状態 ---------------------------------------------------------
let state = null;
let unityAlive = false;
let lastUnity = {};
const columns = new Map();   // camId -> column controller
let floorMap = null;
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
let fxLocked = true;

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

// ---- 危険なラッチの警告（show.json 由来 = Unity 未接続でも見える）------------
//   cameraOverride（カメラ固定）と activeCue（演出再生中）は show.json に永続する。
//   前の体験者の状態が残ったまま次を始めると「歩いても切り替わらない」事故になる。
function renderLatchBar() {
  const bar = $('#latchBar'); if (!bar) return;
  const ctrl = (state && state.control) || {};
  const ovr = ctrl.cameraOverride || '';
  const cue = ctrl.activeCue || '';
  const parts = [];
  if (ovr) parts.push(`🔒 カメラ ${ovr} に固定中（ゾーン自動切替が止まっています）`);
  if (cue) parts.push(`🎬 演出 ${cue} が再生指定中`);
  bar.style.display = parts.length ? '' : 'none';
  const t = $('#latchText');
  if (t) t.textContent = '⚠ ' + parts.join(' ／ ');
  // 🚶 ボタンの状態（固定中だけ点灯）
  const auto = $('#emgAuto');
  if (auto) auto.classList.toggle('armed', !!ovr);
  const stop = $('#emgStop');
  if (stop) stop.classList.toggle('armed', !!cue);
}
if ($('#latchClearAll')) {
  $('#latchClearAll').onclick = async () => {
    await postCommand({ type: 'setCameraOverride', camera: null });
    await postCommand({ type: 'stopCue' });
  };
}

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
      <span class="col-src"></span>
      <span class="col-status">接続中…</span>
      <button class="col-switch" title="Quest の表示をこのカメラに固定する（ゾーン自動切替は止まる。戻すのは上部の警告バー ⛑ か 🚨 ライブ運用の 🚶）">📺 切替</button>
    </div>

    <div class="col-sec">
      <div class="sec-label">④ メタクエストで見えている映像（最終 = 加工 + 合成）</div>
      <div class="cap-bar">
        <button class="cap-btn cap-view" title="この最終映像を 1 枚 recordings/ に保存">📷 1枚</button>
        <button class="rec-btn rec-view" title="最終映像を録画（recordings/ へ）">⏺ 録画</button>
        <span class="spacer"></span>
        <span class="col-cue-mini"></span>
      </div>
      <div class="view-wrap"><canvas class="view-canvas" width="${MW}" height="${MH}"></canvas></div>
    </div>

    <div class="col-sec">
      <div class="sec-label">③ 画像加工（カメラ単位の画質グレーディング = cameras[i].post）</div>
      <div class="fx-rows"></div>
      <div class="row-btns">
        <button class="fx-reset">↺ 画質を初期化</button>
        <button class="fx-copy" title="この列の画質を他のカメラにもコピーする（3 台の見えを揃える）">⇥ 他カメラにも適用</button>
        <span class="fx-msg"></span>
      </div>
    </div>

    <div class="col-sec">
      <div class="sec-label">① 生リアルタイム映像（加工前）</div>
      <div class="cap-bar">
        <button class="cap-btn cap-raw" title="生フレームを 1 枚 recordings/ に保存">📷 1枚</button>
        <button class="rec-btn rec-raw" title="生フレームを録画（recordings/ へ）">⏺ 録画</button>
        <span class="spacer"></span>
        <span class="ip-mini-label">配信元</span>
      </div>
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
      <div class="view-wrap"><img class="raw-live" alt=""></div>
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
  const fxRows = q('.fx-rows');
  for (const [key, label, min, max, step] of FX) {
    const row = document.createElement('label');
    row.className = 'sld fx-row';
    row.append(label + ' ');
    const inp = document.createElement('input');
    inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step;
    const val = document.createElement('span');
    inp.oninput = () => {
      if (!refs.cam.post) refs.cam.post = { ...FX_DEFAULT };
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
    const src = refs.cam.post ? { ...FX_DEFAULT, ...refs.cam.post } : null;
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
  refs.liveOk = false;   // 本番前チェックが読む「この列に実映像が来ているか」
  refs.liveImg.addEventListener('load', () => {
    refs.statusEl.textContent = '● LIVE'; refs.statusEl.className = 'col-status ok';
    refs.liveOk = true;
    refs.applyAspect && refs.applyAspect();
  });
  refs.liveImg.addEventListener('error', () => {
    refs.statusEl.textContent = '✕ 切断 — 再試行'; refs.statusEl.className = 'col-status ng';
    refs.liveOk = false;
    clearTimeout(refs.retry); refs.retry = setTimeout(connectLive, 5000);
  });
  refs.connectLive = connectLive;

  q('.col-switch').onclick = () => postCommand({ type: 'setCameraOverride', camera: refs.cam.id });

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
        post: refs.cam.post || state?.post || FX_DEFAULT,
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
  const hasPost = !!cam.post;
  for (const [key, , , , , d] of FX) {
    const inp = refs.fxInputs[key];
    if (document.activeElement === inp) continue;
    const v = hasPost && (key in cam.post) ? cam.post[key] : (state?.post?.[key] ?? d);
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
    applyFxLock();   // 作り直した列にもライブロックを掛け直す
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
        renderColumns(); renderStatus(); renderLiveCuePanel(); renderLatchBar();
        floorMap && floorMap.onState(s); timeline && timeline.onState(s); showSim && showSim.onState(s);
        atelier && atelier.render();   // カメラ集合の変化を工房の列へ（署名一致なら no-op）
        renderBgmSection(); renderRunPanel(); renderPreflight();
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

document.querySelectorAll('.mode-btn').forEach((b) => {
  b.onclick = () => {
    if (appEl) appEl.dataset.mode = b.dataset.mode;
    document.querySelectorAll('.mode-btn').forEach((x) => x.classList.toggle('active', x === b));
    fxLocked = true;      // ライブへ入る / 戻るたびにロックを掛け直す
    applyFxLock();
    renderPreflight();
    // 素材面へ入るたびに読み直す（📷 で撮ったフレームがすぐ候補に出るように）
    if (b.dataset.mode === 'atelier') atelier.refresh();
  };
});

// ▶ 検証 = 🕹 ショーシミュレーションへ（矢印キーの簡易シミュレーションは廃止・二重に持たない）。
//   オーサリングモードへ戻し、フロアマップのドットを有効化して、シミュレータまでスクロールする。
function focusSimulator() {
  const authorBtn = document.querySelector('.mode-btn[data-mode="author"]');
  if (authorBtn && appEl && appEl.dataset.mode !== 'author') authorBtn.click();
  if (floorMap && floorMap.enableSim) floorMap.enableSim();
  const sec = document.querySelector('.showsim');
  if (sec) sec.scrollIntoView({ behavior: 'smooth', block: 'start' });
  return true;
}

// ---- ライブ中の画質ロック（誤操作で体験者の見え方を変えない）------------------
function applyFxLock() {
  const live = appEl && appEl.dataset.mode === 'live';
  const lock = live && fxLocked;
  if (appEl) appEl.classList.toggle('fx-locked', lock);
  for (const r of columns.values()) {
    for (const k of Object.keys(r.fxInputs)) r.fxInputs[k].disabled = lock;
    const rst = r.el.querySelector('.fx-reset'); if (rst) rst.disabled = lock;
    const cpy = r.el.querySelector('.fx-copy'); if (cpy) cpy.disabled = lock;
  }
  const btn = $('#fxLockBtn');
  if (btn) {
    btn.textContent = fxLocked ? '🔒 画質ロック中' : '🔓 画質ロック解除中';
    btn.classList.toggle('on', !fxLocked);
  }
}
if ($('#fxLockBtn')) $('#fxLockBtn').onclick = () => { fxLocked = !fxLocked; applyFxLock(); };

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
function renderRunPanel() {
  const lapEl = $('#runLap'), zoneEl = $('#runZone'), modeEl = $('#runMode'), nextEl = $('#runNext');
  if (!lapEl) return;
  const u = lastUnity;
  const hasLap = unityAlive && typeof u.lap === 'number' && u.lap > 0;
  const camIdx = unityAlive && typeof u.cam === 'number' && u.cam >= 0 ? u.cam
    : (unityAlive && typeof u.activeIndex === 'number' ? u.activeIndex : -1);
  lapEl.textContent = hasLap ? `Lap ${u.lap}` : 'Lap —';
  lapEl.className = 'run-lap' + (unityAlive ? ' on' : '');
  zoneEl.textContent = camIdx >= 0 ? `ゾーン ${camLabelOf(camIdx)}` : 'ゾーン —';
  zoneEl.className = 'run-zone' + (unityAlive ? ' on' : '');
  modeEl.textContent = unityAlive ? (u.mode || 'NORMAL') : 'Unity 未接続';
  modeEl.className = 'run-mode' + (unityAlive ? (u.mode === 'REG' ? ' reg' : ' on') : ' off');

  if (!nextEl) return;
  if (!hasLap || camIdx < 0) { nextEl.textContent = '次の演出: —（Unity 未接続 / 周回未開始）'; nextEl.className = 'run-next'; return; }
  const here = segFireLabel(segmentAt(u.lap, camIdx));
  const next = findNextFire(u.lap, camIdx);
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
    if (!confirm('体験者交代時に押します。\n・周回とワンショット演出をリセット\n・カメラ固定を解除（ゾーン自律へ）\n・再生中の演出を停止\n実行しますか？')) return;
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
    const r = await postState({ control: ctrl });
    const el = $('#runNext');
    if (el && r && r.ok !== false) el.textContent = `▶ ラン開始（epoch ${ctrl.runEpoch}）— 周回 / 演出 / 固定をリセットしました`;
  };
}
if ($('#emgAuto')) $('#emgAuto').onclick = () => postCommand({ type: 'setCameraOverride', camera: null });
if ($('#emgStop')) $('#emgStop').onclick = () => postCommand({ type: 'stopCue' });

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
if ($('#switchDwell')) $('#switchDwell').onchange = applySwitchTiming;
if ($('#switchCooldown')) $('#switchCooldown').onchange = applySwitchTiming;
loadSwitchTiming();

// ---- ✅ 本番前チェック（自動更新）------------------------------------------
//   「押す前に見る場所」を 1 枚に集約する。各行がそのまま切り分けの入口。
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
    rows.push({
      s: synced ? 'ok' : 'warn',
      label: 'Quest（Unity）',
      detail: `${u.mode === 'REG' ? '位置合わせモード中（体験開始前に退出）' : 'NORMAL'} / ${synced ? '設定反映済み' : `同期中 ${u.appliedRev}/${state?.rev}`}`,
    });
  }

  const devices = (lastDiscovery && lastDiscovery.devices) || [];
  const conflicts = new Set((lastDiscovery && lastDiscovery.conflicts) || []);
  const bad = [];
  for (const c of cams) {
    const col = columns.get(c.id);
    const live = col && col.liveOk;
    const found = devices.some((d) => d.role === 'camera' && d.id === c.id);
    if (conflicts.has(c.id)) bad.push(`${c.id}=二重ID`);
    else if (!live) bad.push(`${c.id}=${found ? '未受信' : '未発見'}`);
  }
  rows.push(bad.length
    ? { s: 'ng', label: `カメラ ${cams.length} 台`, detail: `${bad.join(' / ')} — 🩺 疎通診断で層を切り分け` }
    : { s: 'ok', label: `カメラ ${cams.length} 台`, detail: '全台 LIVE 受信中' });

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

  const ctrl = state?.control || {};
  rows.push((ctrl.cameraOverride || ctrl.activeCue)
    ? { s: 'ng', label: 'ラッチ', detail: `${ctrl.cameraOverride ? `カメラ ${ctrl.cameraOverride} 固定中 ` : ''}${ctrl.activeCue ? `演出 ${ctrl.activeCue} 指定中` : ''}— ▶ ラン開始で解除される` }
    : { s: 'ok', label: 'ラッチ', detail: 'カメラ固定なし / 演出停止中' });

  const segs = timelineSegments();
  const withCue = segs.filter((s) => (s.takes || []).length).length;
  rows.push(withCue
    ? { s: 'ok', label: 'タイムライン', detail: `${segs.length} 区間 / うち演出あり ${withCue}` }
    : { s: 'warn', label: 'タイムライン', detail: '演出を持つ区間がありません（体験は映像切替のみ）' });

  // 参照素材の実在チェック（📦 エクスポート前に気づけるようにする）。
  //   v3 は カット自身が素材 URL を持つので、cue 参照とは別に assetUrl 空のカットも拾う
  //   （空だと実機でそのカットだけ映像が出ない）。
  const cueMap = new Map((state?.cues || []).map((c) => [c.id, c]));
  const missing = [], noSrc = [], emptyAsset = [];
  for (const s of segs) {
    for (const id of segCueIds(s)) {
      const cue = cueMap.get(id);
      if (!cue) { if (!missing.includes(id)) missing.push(id); }
      else if (!cue.sourceUrl) { if (!noSrc.includes(id)) noSrc.push(id); }
    }
    for (const t of (s.takes || [])) {
      for (const st of (t.steps || [])) {
        if ((st.source === 'clip' || st.source === 'still') && !st.assetUrl) {
          const label = t.name || t.id || '演出';
          if (!emptyAsset.includes(label)) emptyAsset.push(label);
        }
      }
    }
  }
  if (missing.length) rows.push({ s: 'ng', label: '演出素材', detail: `未定義の素材: ${missing.join(', ')}` });
  else if (emptyAsset.length) rows.push({ s: 'ng', label: '演出素材', detail: `映像が未選択のカットがある: ${emptyAsset.join(', ')}` });
  else if (noSrc.length) rows.push({ s: 'warn', label: '演出素材', detail: `素材未設定: ${noSrc.join(', ')}` });
  else rows.push({ s: 'ok', label: '演出素材', detail: '参照素材はすべて実在・映像あり' });

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
refreshCaptures();

// ---- 生成プロンプト（下部・コピー用）。バックエンドは既存 /prompts（prompts.json）----
async function loadPrompts() {
  let items = [];
  try { items = await (await fetch('/prompts')).json(); } catch { /* offline */ }
  const wrap = $('#promptList'); wrap.innerHTML = '';
  for (const [kind, label] of [['image', '🖼 画像生成'], ['video', '🎬 動画生成']]) {
    const list = items.filter((p) => (p.kind || 'video') === kind);
    if (!list.length) continue;
    const h = document.createElement('div'); h.className = 'prompt-group'; h.textContent = label;
    wrap.appendChild(h);
    for (const it of list) wrap.appendChild(promptCard(it));
  }
}
function copyText(text, btn) {
  const flash = () => { btn.textContent = '✓ コピー'; setTimeout(() => (btn.textContent = '📋 コピー'), 1200); };
  const fallback = () => {
    const ta = document.createElement('textarea'); ta.value = text;
    ta.style.position = 'fixed'; ta.style.opacity = '0'; document.body.appendChild(ta); ta.select();
    try { document.execCommand('copy'); flash(); } catch { /* noop */ } ta.remove();
  };
  if (navigator.clipboard) navigator.clipboard.writeText(text).then(flash).catch(fallback);
  else fallback();
}
function promptCard(it) {
  const card = document.createElement('div'); card.className = 'prompt-item';
  const head = document.createElement('div'); head.className = 'prompt-head'; head.textContent = it.title || '(無題)';
  const body = document.createElement('div'); body.className = 'prompt-body'; body.textContent = it.text;
  const btns = document.createElement('div'); btns.className = 'row-btns';
  const copy = document.createElement('button'); copy.className = 'accent'; copy.textContent = '📋 コピー';
  copy.onclick = () => copyText(it.text, copy);
  const edit = document.createElement('button'); edit.textContent = '✎'; edit.title = '編集に読み込む';
  edit.onclick = () => {
    const r = document.querySelector(`input[name=pkind][value="${it.kind || 'video'}"]`); if (r) r.checked = true;
    $('#pTitle').value = it.title || ''; $('#pText').value = it.text; $('#pText').dataset.id = it.id || '';
    $('#pText').scrollIntoView({ behavior: 'smooth', block: 'center' });
  };
  // 旧ストアの役目は「素材工房のレシピへ移し終えるまでの置き場」。1 クリックで移せないと
  // 移行が進まず、この節がいつまでも残る（＝結果と紐づかないプロンプトが残り続ける）。
  const toRecipe = document.createElement('button');
  toRecipe.textContent = '📐 レシピへ';
  toRecipe.title = '素材工房のレシピとして登録する（本文の {{…}} はスロットとして拾う）';
  toRecipe.onclick = async () => {
    toRecipe.disabled = true;
    const slots = [...new Set([...String(it.text).matchAll(/\{\{([^}]+)\}\}/g)].map((m) => m[1].trim()))];
    try {
      await fetch('/atelier/recipe', {
        method: 'POST',
        body: JSON.stringify({
          name: it.title || '(無題)', kind: it.kind || 'video', body: it.text, slots, intent: '旧プロンプトから移行',
        }),
      });
      toRecipe.textContent = '✓ 移しました';
      atelier && atelier.refresh();
    } catch { toRecipe.textContent = '✕ 失敗'; }
    setTimeout(() => { toRecipe.textContent = '📐 レシピへ'; toRecipe.disabled = false; }, 1600);
  };
  const del = document.createElement('button'); del.textContent = '🗑'; del.title = '削除';
  del.onclick = async () => { try { await fetch('/prompts/delete', { method: 'POST', body: JSON.stringify({ id: it.id }) }); } catch {} loadPrompts(); };
  btns.append(copy, toRecipe, edit, del);
  card.append(head, body, btns);
  return card;
}
$('#pSave').onclick = async () => {
  const kind = (document.querySelector('input[name=pkind]:checked') || {}).value || 'image';
  const title = $('#pTitle').value.trim(), text = $('#pText').value.trim();
  if (!text) return;
  const id = $('#pText').dataset.id || undefined;
  try { await fetch('/prompts', { method: 'POST', body: JSON.stringify({ id, kind, title, text }) }); } catch {}
  $('#pTitle').value = ''; $('#pText').value = ''; delete $('#pText').dataset.id;
  loadPrompts();
};
loadPrompts();

// ---- フロアマップ（ゾーン校正）---------------------------------------------
if ($('#floorMap')) {
  floorMap = createFloorMap($('#floorMap'), {
    getCameras: () => state?.cameras || [],
    saveLayout: (layout) => postState({ layout }),
    // シミュレーションドットの有効状態・位置を 🕹 ショーシミュレーションへ流す（下で生成）。
    onSim: (e) => showSim && showSim.onSimDot(e),
  });
  if (state) floorMap.onState(state);
}

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
    getCourseOrder: () => state?.layout?.course?.order || null,
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
        <label>ループ out<input class="bgm-le" type="number" min="0" step="0.1" value="${t.loopEndSec || 0}">s<span class="tl-hint2">0=末尾</span></label>
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

pollState();
pollUnity();
pollDiscovery();
