// 廻リ視 web compositor — 1 ページ統合。
//   🎬 事前オーサリング = タイムライン第一級（timeline.js）。カメラ列は監視専用に格下げ。
//   🚨 ライブ運用 = 発見 / 診断 / ラン状態 / cue 手動発火。
//   show.json が唯一の正。ビューは Unity ScreenComposite を WebGL で再現（composite-view.js）。
import {
  FX, FX_DEFAULT, blendCfg, camColor, escapeHtml,
  streamBase, getState, postState, postCommand,
  captures, refreshCaptures, createMediaCache, saveCueObject,
} from './common.js';
import { createCompositeView } from './composite-view.js';
import { createFloorMap } from './floormap.js';
import { createTimeline } from './timeline.js';

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
let lastDiscovery = { devices: [], conflicts: [] };

// ---- ステータスバー ---------------------------------------------------------
function renderStatus() {
  const dot = $('#stDot'), info = $('#stInfo'), sync = $('#stSync');
  dot.className = 'st-dot ' + (unityAlive ? 'on' : 'off');
  if (!unityAlive) { info.textContent = 'Unity: 未接続'; sync.textContent = ''; return; }
  const u = lastUnity;
  info.textContent = `Unity: ${u.activeCamera || '?'} / ${(u.recvFps || 0).toFixed(1)}fps`
    + (u.playingCue ? ` / 🎬 ${u.playingCue}` : '')
    + (u.cameraOverride ? ` / 🔒 ${u.cameraOverride}` : '')
    + (u.simulator ? '（仮想）' : '');
  if (state && typeof u.appliedRev === 'number') {
    sync.textContent = u.appliedRev >= state.rev ? '✓ 反映済み' : `⏳ 同期中 (${u.appliedRev}/${state.rev})`;
    sync.className = 'st-sync ' + (u.appliedRev >= state.rev ? 'ok' : 'wait');
  }
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
      <button class="col-switch" title="Quest の表示をこのカメラに切り替える（ゾーン自律は一時オフ。戻すのは上部の 🚶）">📺 切替</button>
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
      <div class="row-btns"><button class="fx-reset">↺ 画質を初期化</button></div>
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
      <div class="view-wrap"><img class="raw-live" alt="生リアルタイム映像"></div>
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
  q('.fx-reset').onclick = () => { delete refs.cam.post; postState({ cameras: state.cameras }); };

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
    refs.statusEl.textContent = '接続中…'; refs.statusEl.className = 'col-status';
    refs.liveImg.src = `${streamBase()}/cam?host=${encodeURIComponent(c.host || '')}`
      + `&port=${c.port || 8080}&path=/video`
      + (c.auth ? `&auth=${encodeURIComponent(c.auth)}` : '') + `&t=${Date.now()}`;
  }
  refs.liveImg.addEventListener('load', () => {
    refs.statusEl.textContent = '● LIVE'; refs.statusEl.className = 'col-status ok';
    refs.applyAspect && refs.applyAspect();
  });
  refs.liveImg.addEventListener('error', () => {
    refs.statusEl.textContent = '✕ 切断 — 再試行'; refs.statusEl.className = 'col-status ng';
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
    recChunks = [];
    const mime = (window.MediaRecorder && MediaRecorder.isTypeSupported('video/webm;codecs=vp9'))
      ? 'video/webm;codecs=vp9' : 'video/webm';
    mediaRec = new MediaRecorder(stream, { mimeType: mime });
    mediaRec.ondataavailable = (e) => { if (e.data && e.data.size) recChunks.push(e.data); };
    mediaRec.onstop = async () => {
      clearInterval(rawTimer);
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
  q('.cap-view').onclick = () => saveStill(viewCanvas, viewTag(), 'Quest最終');
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
        renderColumns(); renderStatus(); renderLiveCuePanel();
        floorMap && floorMap.onState(s); timeline && timeline.onState(s);
      }
    } catch { await new Promise((r) => setTimeout(r, 2000)); }
  }
}
async function pollUnity() {
  for (;;) {
    try {
      const s = await (await fetch('/unity/status')).json();
      unityAlive = !!s.alive; lastUnity = s.status || {};
    } catch { unityAlive = false; lastUnity = {}; }
    renderStatus();
    renderRunPanel();
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

// ---- 起動 -------------------------------------------------------------------
$('#autoZone').onclick = () => postCommand({ type: 'setCameraOverride', camera: null });
$('#openRecordings').onclick = () => fetch('/open-dir?dir=recordings').catch(() => {});

// ---- モードナビ（🎬 事前オーサリング / 🚨 ライブ運用）------------------------
const appEl = $('.app');
document.querySelectorAll('.mode-btn').forEach((b) => {
  b.onclick = () => {
    if (appEl) appEl.dataset.mode = b.dataset.mode;
    document.querySelectorAll('.mode-btn').forEach((x) => x.classList.toggle('active', x === b));
  };
});

// ---- ラン状態表示（Lap / cam / mode）+ ▶ ラン開始 ---------------------------
function renderRunPanel() {
  const el = $('#runState'); if (!el) return;
  if (!unityAlive) { el.textContent = 'Unity 未接続'; el.className = 'run-state off'; return; }
  const u = lastUnity;
  const lap = (typeof u.lap === 'number') ? `Lap ${u.lap}` : 'Lap —';
  const cam = u.cam || activeCamId() || '—';
  const mode = u.mode || '—';
  el.textContent = `${lap} / cam ${cam} / ${mode}`;
  el.className = 'run-state on';
}
if ($('#runStart')) {
  $('#runStart').onclick = async () => {
    if (!confirm('体験者交代時に押します。周回とワンショット演出がリセットされます。実行しますか？')) return;
    const s = await getState();
    const ctrl = { ...(s.control || {}) };
    ctrl.runEpoch = (parseInt(ctrl.runEpoch, 10) || 0) + 1;
    const r = await postState({ control: ctrl });
    const el = $('#runState');
    if (el && r && r.ok !== false) el.textContent = `▶ ラン開始（epoch ${ctrl.runEpoch}）`;
  };
}
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
  const del = document.createElement('button'); del.textContent = '🗑'; del.title = '削除';
  del.onclick = async () => { try { await fetch('/prompts/delete', { method: 'POST', body: JSON.stringify({ id: it.id }) }); } catch {} loadPrompts(); };
  btns.append(copy, edit, del);
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
  });
  if (state) floorMap.onState(state);
}

// ---- 周回タイムライン（第一級オーサリング）---------------------------------
if ($('#timeline')) {
  timeline = createTimeline($('#timeline'), {
    getCameras: () => state?.cameras || [],
    getCues: () => state?.cues || [],
    getCourseOrder: () => state?.layout?.course?.order || null,
    getGlobalPost: () => state?.post || FX_DEFAULT,
    getLiveImg: (camId) => { const c = columns.get(camId); return c ? c.liveImg : null; },
    getCaptures: () => captures.items,
    refreshCaptures: () => refreshCaptures(),
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

// ---- ビルド用エクスポート ----------------------------------------------------
if ($('#exportBuild')) {
  $('#exportBuild').onclick = async () => {
    const r = $('#exportResult');
    r.textContent = 'エクスポート中…'; r.className = 'ed-status';
    try {
      const res = await (await fetch('/export-build', { method: 'POST' })).json();
      if (!res.ok) throw new Error(res.error || 'エクスポート失敗');
      const mb = (res.totalBytes / 1024 / 1024).toFixed(2);
      r.textContent = `✓ ${res.count} ファイル / ${mb}MB → ${res.outDir}`;
      r.className = 'ed-status ok';
      const fl = $('#exportFiles'); fl.innerHTML = '';
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
