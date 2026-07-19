// 廻リ視 web compositor — 1 ページ統合（縦割り = カメラ列）。
//   ステータス / マルチカメラ（ビュー・設定・マスク・合成素材）だけ。show.json が唯一の正。
//   ビューは Unity ScreenComposite を WebGL で完全再現（画質を当てた最終見た目＝Quest と同じ絵）。
import { createContext, SourceTexture } from './gl.js';
import { Pipeline } from './pipeline.js';
import { createFloorMap } from './floormap.js';
import { createSchedule } from './schedule.js';

// 境界ブレンド（合成跡を消す）設定。全カメラ共通。各カメラの Pipeline がこれを参照。
const blendCfg = { feather: 0.3, colorMatch: true, colorStrength: 1, laplacian: true, levels: 7 };

const $ = (s) => document.querySelector(s);

// カメラ別 画質（cameras[i].post）。未設定カメラは Unity 側で global post にフォールバック。
const FX = [
  ['exposure', '露出', -2, 2, 0.01, 0],
  ['contrast', 'コントラスト', 0.5, 2, 0.01, 1],
  ['saturation', '彩度', 0, 2, 0.01, 1],
  ['temperature', '色温度', -1, 1, 0.01, 0],
  ['vignette', 'ヴィネット', 0, 1, 0.01, 0],
  ['grain', 'グレイン', 0, 0.3, 0.005, 0],
  ['scanline', '走査線', 0, 1, 0.01, 0],
];
const FX_DEFAULT = Object.fromEntries(FX.map(([k, , , , , d]) => [k, d]));
const MW = 640, MH = 360;

// MJPEG は メインポート+1 から（同一オリジン 6 接続制限の回避。capture-server.py が両方 listen）
const streamBase = () =>
  `${location.protocol}//${location.hostname}:${(parseInt(location.port, 10) || 80) + 1}`;
const isVideoUrl = (u) => /\.(webm|mp4|mov|m4v)(\?|$)/i.test(u);

// パス各セグメントを percent-encode（スペースや括弧入りのファイル名で Unity VideoPlayer が
// ロード失敗するのを防ぐ）。既にエンコード済みでも二重化しないよう decode してから encode。
function encPath(u) {
  if (!u) return u;
  const encSeg = (s) => { if (!s) return s; try { return encodeURIComponent(decodeURIComponent(s)); } catch { return encodeURIComponent(s); } };
  return u.split('/').map(encSeg).join('/');
}

// ---- サーバ I/O -------------------------------------------------------------
async function getState() { return (await fetch('/state')).json(); }
async function postState(patch) {
  try { return await (await fetch('/state', { method: 'POST', body: JSON.stringify(patch) })).json(); }
  catch { return { ok: false }; }
}
async function postCommand(cmd) {
  try { return await (await fetch('/command', { method: 'POST', body: JSON.stringify(cmd) })).json(); }
  catch { return { ok: false }; }
}
let pushTimer = 0;
function pushCamerasDebounced() {
  clearTimeout(pushTimer);
  pushTimer = setTimeout(() => postState({ cameras: state.cameras }), 140);
}

// ---- グローバル状態 ---------------------------------------------------------
let state = null;
let unityAlive = false;
let lastUnity = {};
let captureItems = [];
const columns = new Map();   // camId -> column controller
let floorMap = null;         // フロアマップ（ゾーン校正）コントローラ
let schedule = null;         // 周回タイムライン UI コントローラ
let lastDiscovery = { devices: [], conflicts: [] };  // 直近の /discovery（カメラ列の発見表示用）

// ---- captures/ 素材一覧（全列共有） ----------------------------------------
async function refreshCaptures() {
  try { captureItems = await (await fetch('/captures/list')).json(); } catch { captureItems = []; }
  for (const c of columns.values()) c.populateSources();
}

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

// ---- カメラ列の生成 ---------------------------------------------------------
function buildColumn(cam, index) {
  const col = document.createElement('div');
  col.className = 'cam-col';
  // 縦フロー（下→上）: ① 生リアルタイム映像 → ② マスク → ③ 画像加工 → ④ Quest 実映像。
  //   キャプチャ UI は「① 生映像の上」（=生フレーム保存）と「④ 実映像の上」（=合成済み保存）の 2 か所。
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
        <button class="cue-toggle" title="演出（合成映像）の ON / OFF">演出 ON</button>
        <span class="sld">fade <input class="cue-fade-sec" type="number" min="0" max="5" step="0.1" value="0.5" title="フェード秒（ON で出る / OFF・再生終了で戻る）">s</span>
      </div>
      <div class="view-wrap"><canvas class="view-canvas" width="${MW}" height="${MH}"></canvas></div>
    </div>

    <div class="col-sec">
      <div class="sec-label">③ 画像加工（画質 + 合成素材）</div>
      <div class="fx-rows"></div>
      <div class="row-btns"><button class="fx-reset">↺ 画質を初期化</button></div>

      <div class="cue-manage">
        <div class="sec-label">cue（このカメラの演出。複数可）</div>
        <div class="cue-list"></div>
        <div class="row-btns cue-ops">
          <button class="cue-add" title="空の cue を新規作成">＋ 新規</button>
          <button class="cue-dup" title="選択中の cue を複製">⧉ 複製</button>
          <button class="cue-del" title="選択中の cue を削除">🗑 削除</button>
        </div>
        <div class="row-btns"><input class="cue-name" type="text" placeholder="cue 名（任意）"></div>
        <div class="row-btns">
          <select class="src-select"></select>
          <button class="src-refresh" title="一覧を更新">↻</button>
          <button class="src-folder" title="素材フォルダ（captures/）を開く">📂</button>
        </div>
        <div class="row-btns trim-row" style="display:none">
          <span class="sld">再生区間 <input class="trim-start" type="number" min="0" step="0.1" value="0" title="開始秒">–<input class="trim-end" type="number" min="0" step="0.1" value="0" title="終了秒（0=最後まで）">s</span>
        </div>
        <div class="row-btns"><button class="cue-save accent">💾 選択中の cue を保存</button></div>
        <span class="ed-status"></span>
      </div>
    </div>

    <div class="col-sec">
      <div class="sec-label">② マスク領域調整（白 = 差し替え）</div>
      <div class="view-wrap"><canvas class="mask-canvas" width="${MW}" height="${MH}"></canvas></div>
      <div class="row-btns mask-tools">
        <span class="seg-label">白の向き</span>
        <button data-edge="left">左</button>
        <button data-edge="right" class="on">右</button>
        <button data-edge="top">上</button>
        <button data-edge="bottom">下</button>
        <span class="sld"><input type="range" class="mask-pos" min="0" max="100" value="50" title="白黒境界の位置"><span class="mask-pos-v">50%</span></span>
        <button class="mask-clear" title="全黒に戻す">取り消し</button>
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
      <div class="view-wrap"><img class="raw-live" alt="生リアルタイム映像"></div>
    </div>`;

  const q = (s) => col.querySelector(s);
  const refs = {
    el: col, cam, index, id: cam.id,
    statusEl: q('.col-status'), srcSpan: q('.col-src'),
    hostI: q('.ip-host'), portI: q('.ip-port'), authI: q('.ip-auth'),
    fxInputs: {}, fxVals: {},
    // 生ライブは DOM の <img>（最下段に直接表示）。GL テクスチャ源としても同じ要素を使い回す
    //   → MJPEG 接続は 1 本のまま（同一オリジン 6 接続制限を食わない）。
    liveImg: q('.raw-live'), srcMedia: null, srcReady: false,
    retry: 0, streamKey: '',
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
  //   通常は discovery（/discovery）で発見したこの id の端末を読み取り専用表示。
  //   host/port/auth の手動編集は「🚨 緊急: 手動接続」details 内のみ → pinned=true
  //   （自動追従を止める）。解除は 📌 固定を解除ボタン。
  const connDisc = q('.conn-disc'), connPin = q('.conn-pin'), pinClear = q('.pin-clear');
  refs.syncPin = () => {
    const pinned = !!refs.cam.pinned;
    connPin.textContent = pinned ? `📌 手動固定 ${refs.cam.host || ''}:${refs.cam.port || 8080}` : '';
    connPin.className = 'conn-pin' + (pinned ? ' on' : '');
    pinClear.style.display = pinned ? '' : 'none';
  };
  // /discovery の一致 id 端末を読み取り専用表示（conflict=赤 / 未発見=警告）。
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
    refs.cam.pinned = true;   // 手動編集は固定扱い（発見の自動追従で上書きしない）
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
    refs.applyAspect && refs.applyAspect(); // ビュー/マスクの縦横比をカメラ実寸に合わせる
  });
  refs.liveImg.addEventListener('error', () => {
    refs.statusEl.textContent = '✕ 切断 — 再試行'; refs.statusEl.className = 'col-status ng';
    clearTimeout(refs.retry); refs.retry = setTimeout(connectLive, 5000);
  });
  refs.connectLive = connectLive;

  // ===== マスク段（白黒境界をスライダで調整。白=差し替え）=====
  const maskCanvas = q('.mask-canvas');
  const mctx = maskCanvas.getContext('2d', { willReadFrequently: true });
  refs.maskEdge = 'right'; refs.maskCoverage = 50;
  function drawMask() {
    const W = maskCanvas.width, H = maskCanvas.height;
    mctx.fillStyle = '#000'; mctx.fillRect(0, 0, W, H);
    const c = Math.max(0, Math.min(100, refs.maskCoverage)) / 100;
    if (c <= 0) return;
    mctx.fillStyle = '#fff';
    if (refs.maskEdge === 'left') mctx.fillRect(0, 0, W * c, H);
    else if (refs.maskEdge === 'right') mctx.fillRect(W * (1 - c), 0, W * c, H);
    else if (refs.maskEdge === 'top') mctx.fillRect(0, 0, W, H * c);
    else mctx.fillRect(0, H * (1 - c), W, H * c);
  }
  drawMask();
  col.querySelectorAll('[data-edge]').forEach((b) => b.addEventListener('click', () => {
    refs.maskEdge = b.dataset.edge;
    col.querySelectorAll('[data-edge]').forEach((x) => x.classList.toggle('on', x === b));
    refs.maskEdited = true; drawMask();
  }));
  const maskPos = q('.mask-pos'), maskPosV = q('.mask-pos-v');
  maskPos.oninput = () => { refs.maskCoverage = parseInt(maskPos.value, 10); maskPosV.textContent = maskPos.value + '%'; refs.maskEdited = true; drawMask(); };
  q('.mask-clear').onclick = () => { refs.maskCoverage = 0; maskPos.value = 0; maskPosV.textContent = '0%'; refs.maskEdited = true; drawMask(); };
  const maskIsEmpty = () => refs.maskCoverage <= 0;

  // ===== 合成素材 =====
  const srcSelect = q('.src-select');
  const trimRow = q('.trim-row'), trimStartI = q('.trim-start'), trimEndI = q('.trim-end');
  refs.trimStart = 0; refs.trimEnd = 0; refs.sourceUrl = '';
  refs.populateSources = () => {
    const cur = srcSelect.value;
    srcSelect.innerHTML = '<option value="">（captures/ から選ぶ）</option>';
    for (const it of captureItems) {
      const o = document.createElement('option');
      o.value = it.url; o.textContent = `${it.type === 'video' ? '🎞' : '🖼'} ${it.name}`;
      srcSelect.appendChild(o);
    }
    srcSelect.value = cur;
  };
  function loadSource(url) {
    if (refs.srcMedia && refs.srcMedia.tagName === 'VIDEO') { refs.srcMedia.pause(); refs.srcMedia.src = ''; }
    refs.srcMedia = null; refs.srcReady = false; refs.sourceUrl = url || '';
    trimRow.style.display = 'none';
    if (!url) return;
    if (isVideoUrl(url)) {
      const v = document.createElement('video');
      v.muted = true; v.loop = false; v.playsInline = true; v.crossOrigin = 'anonymous'; v.src = url;
      v.addEventListener('loadedmetadata', () => {
        trimRow.style.display = '';
        if (!(refs.trimEnd > 0)) { refs.trimEnd = +(v.duration || 0).toFixed(1); trimEndI.value = refs.trimEnd; }
      });
      v.addEventListener('canplay', () => { refs.srcReady = true; });
      refs.srcMedia = v;
    } else {
      const im = new Image(); im.crossOrigin = 'anonymous';
      im.onload = () => { refs.srcReady = true; }; im.src = url;
      refs.srcMedia = im;
    }
  }
  srcSelect.onchange = () => { refs.trimStart = 0; refs.trimEnd = 0; trimStartI.value = 0; trimEndI.value = 0; loadSource(srcSelect.value); };
  q('.src-refresh').onclick = refreshCaptures;
  q('.src-folder').onclick = () => fetch('/open-dir?dir=captures').catch(() => {});
  trimStartI.onchange = () => { refs.trimStart = Math.max(0, parseFloat(trimStartI.value) || 0); };
  trimEndI.onchange = () => { refs.trimEnd = Math.max(0, parseFloat(trimEndI.value) || 0); };

  // 演出フェード秒（ON で出る / OFF・再生終了で戻る）。cue.fadeIn/Out にも反映。
  const fadeSecI = q('.cue-fade-sec');
  refs.fadeSec = parseFloat(fadeSecI.value) || 0.5;
  function patchCueFade() {
    const id = refs.selectedCueId; if (!id) return;
    const cues = state?.cues; if (!cues) return;
    const cue = cues.find((c) => c.id === id);
    if (cue) { cue.fadeIn = refs.fadeSec; cue.fadeOut = refs.fadeSec; postState({ cues }); }
  }
  fadeSecI.onchange = () => { refs.fadeSec = Math.max(0, parseFloat(fadeSecI.value) || 0); patchCueFade(); };

  // 動画が再生区間の終端に達したら演出 OFF（フェードで戻る）。ループしない。
  refs.onOverlayEnd = () => {
    if (refs.overlayEnded) return;
    refs.overlayEnded = true;
    postCommand({ type: 'stopCue' });
  };

  const ed = (m, cls = '') => { const e = q('.ed-status'); e.textContent = m; e.className = 'ed-status ' + cls; };

  // ===== cue 管理（1 カメラ複数 cue: id = cue_<camId>_<n>）=====
  //   既存の単数 cue（id=cue_<camId>、camera フィールドで所属を示す）もそのまま一覧に出す。
  const cueListEl = q('.cue-list');
  const cueNameI = q('.cue-name');
  refs.selectedCueId = null;   // 編集/発火の対象
  refs.maskEdited = false;      // 選択後にマスクを触ったか（false なら既存マスク保持）
  refs.selCueMaskUrl = '';      // 選択中 cue の既存 maskUrl（未編集時の保持用）

  // このカメラに所属する cue（camera フィールド一致）。
  function camCues() { return (state?.cues || []).filter((c) => (c.camera || '') === refs.cam.id); }

  // cue_<camId>_<n> の空き番号を返す。
  function nextCueId() {
    const base = `cue_${refs.cam.id}_`;
    const used = new Set((state?.cues || []).map((c) => c.id));
    let n = 1; while (used.has(base + n)) n++;
    return base + n;
  }

  // cue を編集エリアへ読み込む（マスクエディタは初期化。既存マスクは selCueMaskUrl に退避）。
  function loadCueIntoEditor(cue) {
    if (cue && cue.sourceUrl) { srcSelect.value = cue.sourceUrl; loadSource(cue.sourceUrl); }
    else { srcSelect.value = ''; loadSource(''); }
    refs.trimStart = cue?.trimStart || 0; refs.trimEnd = cue?.trimEnd || 0;
    trimStartI.value = refs.trimStart; trimEndI.value = refs.trimEnd;
    refs.fadeSec = (cue && cue.fadeIn != null) ? cue.fadeIn : 0.5; fadeSecI.value = refs.fadeSec;
    cueNameI.value = cue?.name || '';
    refs.selCueMaskUrl = cue?.maskUrl || '';
    refs.maskCoverage = 0; maskPos.value = 0; maskPosV.textContent = '0%'; refs.maskEdited = false; drawMask();
  }

  function selectCue(id) {
    refs.selectedCueId = id;
    loadCueIntoEditor(camCues().find((c) => c.id === id) || null);
    renderCueList();
  }

  function renderCueList() {
    const list = camCues();
    cueListEl.innerHTML = '';
    if (!list.length) {
      const e = document.createElement('span');
      e.className = 'cue-empty'; e.textContent = '（cue 未作成 — ＋新規 で追加）';
      cueListEl.appendChild(e); return;
    }
    for (const c of list) {
      const b = document.createElement('button');
      b.className = 'cue-chip'
        + (c.id === refs.selectedCueId ? ' on' : '')
        + (state?.control?.activeCue === c.id ? ' playing' : '');
      b.textContent = (state?.control?.activeCue === c.id ? '🎬 ' : '') + (c.name || c.id);
      b.title = c.id;
      b.onclick = () => selectCue(c.id);
      cueListEl.appendChild(b);
    }
  }
  refs.renderCueList = renderCueList;
  refs.loadSelectedCue = () => loadCueIntoEditor(camCues().find((c) => c.id === refs.selectedCueId) || null);

  // マスク PNG を書き出して url を返す（フェザー焼き込み）。空マスクなら '' を返す。
  async function writeMaskFor(id) {
    if (maskIsEmpty()) return '';
    ed('マスク書き出し中…');
    let blob;
    if (blendCfg.feather > 0.001) {
      const tmp = document.createElement('canvas'); tmp.width = maskCanvas.width; tmp.height = maskCanvas.height;
      const tc = tmp.getContext('2d');
      tc.filter = `blur(${Math.max(1, Math.round(blendCfg.feather * 12))}px)`;
      tc.drawImage(maskCanvas, 0, 0);
      blob = await new Promise((r) => tmp.toBlob(r, 'image/png'));
    } else {
      blob = await new Promise((r) => maskCanvas.toBlob(r, 'image/png'));
    }
    const res = await (await fetch(`/masks?name=${encodeURIComponent(id)}`, { method: 'POST', body: blob })).json();
    if (!res.ok) throw new Error(res.error || 'マスク保存失敗');
    return res.url;
  }

  q('.cue-save').onclick = async () => {
    const camId = refs.cam.id;
    const sourceUrl = refs.sourceUrl || srcSelect.value || '';
    if (!sourceUrl) return ed('合成素材を選んで', 'err');
    let id = refs.selectedCueId;
    const existing = id ? camCues().find((c) => c.id === id) : null;
    if (!id || !existing) id = nextCueId();  // 未選択 or 消えた → 新規
    try {
      // マスク: 触っていれば書き出し（空なら全面差し替え）。未編集なら既存 maskUrl を保持。
      let maskUrl;
      if (refs.maskEdited) maskUrl = await writeMaskFor(id);
      else maskUrl = existing ? (existing.maskUrl || '') : '';
      const cue = {
        id, name: cueNameI.value.trim() || `カメラ ${camId} #${id.split('_').pop()}`, camera: camId,
        maskUrl: encPath(maskUrl), sourceUrl: encPath(sourceUrl),
        strength: existing?.strength ?? 1, loop: existing?.loop ?? false,
        fadeIn: refs.fadeSec, fadeOut: refs.fadeSec,
        trimStart: refs.trimStart || 0, trimEnd: refs.trimEnd || 0,
      };
      const s = await getState();
      const cues = s.cues || [];
      const k = cues.findIndex((c) => c.id === id);
      if (k >= 0) cues[k] = cue; else cues.push(cue);
      const r2 = await postState({ cues });
      if (!r2.ok) throw new Error('state 保存失敗');
      if (state) state.cues = cues;   // poll 到達前でも即座に一覧へ反映
      refs.selectedCueId = id; refs.selCueMaskUrl = cue.maskUrl; refs.maskEdited = false;
      renderCueList();
      ed(`✓ 保存（${maskUrl ? 'マスク付き' : '全面差し替え'}）。演出 ON で出る`, 'ok');
    } catch (e) { ed('保存失敗: ' + e.message, 'err'); }
  };

  q('.cue-add').onclick = () => {
    refs.selectedCueId = null;
    loadCueIntoEditor(null);
    renderCueList();
    ed('新規 cue: 素材を選び 💾 保存', '');
  };
  q('.cue-dup').onclick = async () => {
    const cur = refs.selectedCueId ? camCues().find((c) => c.id === refs.selectedCueId) : null;
    if (!cur) return ed('複製する cue を選んで', 'err');
    const id = nextCueId();
    const cue = { ...JSON.parse(JSON.stringify(cur)), id, name: (cur.name || cur.id) + ' 複製' };
    const s = await getState();
    const cues = s.cues || []; cues.push(cue);
    const r2 = await postState({ cues });
    if (!r2.ok) return ed('複製失敗', 'err');
    if (state) state.cues = cues;
    selectCue(id);
    ed('✓ 複製', 'ok');
  };
  q('.cue-del').onclick = async () => {
    const id = refs.selectedCueId;
    if (!id || !camCues().some((c) => c.id === id)) return ed('削除する cue を選んで', 'err');
    if (state?.control?.activeCue === id) await postCommand({ type: 'stopCue' });
    const s = await getState();
    const cues = (s.cues || []).filter((c) => c.id !== id);
    const r2 = await postState({ cues });
    if (!r2.ok) return ed('削除失敗', 'err');
    if (state) state.cues = cues;
    const rest = camCues();
    refs.selectedCueId = rest[0]?.id || null;
    loadCueIntoEditor(rest[0] || null);
    renderCueList();
    ed('✓ 削除', 'ok');
  };
  cueNameI.onchange = () => {
    const id = refs.selectedCueId; if (!id) return;
    const cues = state?.cues; if (!cues) return;
    const cue = cues.find((c) => c.id === id);
    if (cue) { cue.name = cueNameI.value.trim() || cue.name; postState({ cues }); renderCueList(); }
  };

  // ===== 操作系（演出 ON/OFF・固定）= 選択中の cue を発火 =====
  q('.cue-toggle').onclick = () => {
    const id = refs.selectedCueId;
    if (!id) { ed('先に cue を選択 / 保存', 'err'); return; }
    const active = state?.control?.activeCue === id;
    if (active) { postCommand({ type: 'stopCue' }); return; }
    const exists = (state?.cues || []).some((c) => c.id === id);
    if (!exists) { ed('この cue は未保存。💾 保存してから演出 ON', 'err'); return; }
    postCommand({ type: 'playCue', id });
  };
  q('.col-switch').onclick = () => postCommand({ type: 'setCameraOverride', camera: refs.cam.id });

  // ===== ① ビュー/マスクの縦横比をカメラ実寸に合わせる（黒レターボックス背景を出さない）=====
  const viewCanvas = q('.view-canvas');
  refs.applyAspect = () => {
    const w = refs.liveImg.naturalWidth, h = refs.liveImg.naturalHeight;
    if (!w || !h) return;
    const a = w / h;
    const aStr = a.toFixed(4);
    if (refs._aspect === aStr) return;
    refs._aspect = aStr;
    viewCanvas.style.aspectRatio = aStr;
    maskCanvas.style.aspectRatio = aStr;
    refs.liveImg.style.aspectRatio = aStr; // 生ライブ表示もカメラ実寸比に追従（黒帯ゼロ）
    // ★ビューの内部レンダー解像度もカメラ比にする → 取り込み(FS_INGEST の contain-fit)が
    //   letterbox せず full-fill。表示(CSS)だけでなくピクセル自体が黒帯ゼロ
    //   → 📷/⏺ のキャプチャにも黒帯が入らない。
    viewCanvas.width = Math.max(2, Math.round(360 * a));
    viewCanvas.height = 360;
  };

  // ===== 📷 キャプチャ / ⏺ 録画（2 系統）=====
  //   ① cap-raw / rec-raw … 生フレーム（画質加工も合成も無しの素の配信映像）
  //   ④ cap-view / rec-view … 合成済みビュー（= Quest で実際に見えている最終映像）
  //   保存先はどちらも recordings/。cam タグで区別（生 = "A" / 最終 = "A_quest"）。
  const rawTag = () => refs.cam.id;            // 生フレーム
  const viewTag = () => `${refs.cam.id}_quest`; // 合成済み（Quest 実映像）

  // 生フレームを 1 枚の 2D canvas へ描く（img は toBlob 不可なので canvas 化）。
  const rawCanvas = document.createElement('canvas');
  const rawDraw = () => {
    const img = refs.liveImg;
    if (!img.naturalWidth) return false;
    if (rawCanvas.width !== img.naturalWidth) { rawCanvas.width = img.naturalWidth; rawCanvas.height = img.naturalHeight; }
    rawCanvas.getContext('2d').drawImage(img, 0, 0, rawCanvas.width, rawCanvas.height);
    return true;
  };

  async function saveStill(canvas, tag, label) {
    if (!canvas) return ed('映像が無い', 'err');
    canvas.toBlob(async (blob) => {
      if (!blob) return ed('キャプチャ不可', 'err');
      const r = await (await fetch(`/save?type=image&to=recordings&cam=${tag}`,
        { method: 'POST', body: blob })).json();
      ed(r.ok ? `📷 保存(${label}): ${r.name}` : '保存失敗', r.ok ? 'ok' : 'err');
    }, 'image/jpeg', 0.95);
  }

  // 列に 1 個のレコーダ（生 / 最終の同時録画はしない）。kind = 'raw' | 'view'。
  //   refs.startRecord / stopRecord / isRecording を公開し、個別ボタンからも
  //   「全カメラ同時録画」（ヘッダ）からも同じ経路で叩く。
  let mediaRec = null, recChunks = [], rawTimer = 0;
  const recRawBtn = q('.rec-raw'), recViewBtn = q('.rec-view');
  refs.recording = null; // 'raw' | 'view' | null
  function updateColRecUI() {
    recRawBtn.textContent = refs.recording === 'raw' ? '⏹ 停止' : '⏺ 録画';
    recRawBtn.classList.toggle('on', refs.recording === 'raw');
    recViewBtn.textContent = refs.recording === 'view' ? '⏹ 停止' : '⏺ 録画';
    recViewBtn.classList.toggle('on', refs.recording === 'view');
  }
  function colStartRecord(kind) {
    if (refs.recording) return false; // 既に録画中（1 列 1 レコーダ）
    const isView = kind === 'view';
    const streamCanvas = isView ? viewCanvas : rawCanvas;
    if (isView) { if (!streamCanvas) { ed('映像が無い', 'err'); return false; } }
    else if (!rawDraw()) { ed('生映像が無い', 'err'); return false; } // 初回フレームを描く
    const tag = isView ? viewTag() : rawTag();
    const label = isView ? 'Quest最終' : '生';
    clearInterval(rawTimer);
    if (!isView) rawTimer = setInterval(rawDraw, 33); // 生は img→canvas を回し続ける
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
      ed(r.ok ? `⏹ 録画保存(${label}): ${r.name}（${Math.round(r.size / 1024)}KB）` : '録画保存失敗', r.ok ? 'ok' : 'err');
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

  // ① 生映像のキャプチャ
  q('.cap-raw').onclick = () => saveStill(rawDraw() ? rawCanvas : null, rawTag(), '生');
  recRawBtn.onclick = () => {
    if (refs.recording === 'raw') colStopRecord();
    else if (!refs.recording) colStartRecord('raw');
    renderGlobalRecState();
  };
  // ④ 最終映像（合成済み = Quest 実映像）のキャプチャ。view-canvas は自走描画なので tick 不要。
  q('.cap-view').onclick = () => saveStill(viewCanvas, viewTag(), 'Quest最終');
  recViewBtn.onclick = () => {
    if (refs.recording === 'view') colStopRecord();
    else if (!refs.recording) colStartRecord('view');
    renderGlobalRecState();
  };

  // ===== WebGL ビュー（ScreenComposite 再現）=====
  setupView(refs, q('.view-canvas'), mctx, maskCanvas);

  // 初期選択: このカメラの最初の cue を選ぶ（無ければ空の編集状態）。
  const firstCue = camCues()[0];
  if (firstCue) selectCue(firstCue.id);
  else { renderCueList(); loadCueIntoEditor(null); }

  connectLive();
  refs.populateSources();
  return refs;
}

// ---- 列ごとの WebGL ビュー（マルチパス: 色統計マッチング→ラプラシアン合成→ポスト FX）----
function setupView(refs, canvas, mctx, maskCanvas) {
  let ctx;
  try { ctx = createContext(canvas); } // half-float RT が要るため EXT_color_buffer_float 必須
  catch (e) {
    canvas.replaceWith(Object.assign(document.createElement('div'),
      { className: 'view-fallback', textContent: 'WebGL2/float RT 非対応: ' + e.message }));
    return;
  }
  const gl = ctx.gl;
  const pipe = new Pipeline(gl);
  const texLive = new SourceTexture(gl), texMask = new SourceTexture(gl), texOv = new SourceTexture(gl);
  const containScale = (w, h, fa) => {
    if (!w || !h) return [1, 1];
    const a = w / h;
    return a > fa ? [1, fa / a] : [a / fa, 1];
  };
  const t0 = performance.now();
  const render = () => {
    const W = canvas.width, H = canvas.height;
    pipe.allocate(W, H, Math.max(2, Math.min(9, blendCfg.levels))); // 同一サイズなら no-op
    const frameAspect = W / H;
    const img = refs.liveImg;
    if (img.naturalWidth) texLive.upload(img);
    texMask.upload(maskCanvas);
    const hasSrc = refs.srcMedia && refs.srcReady;
    // 動画の再生区間終端 → 自動終了（ループしない）
    if (refs.cueActive && !refs.overlayEnded && hasSrc && refs.srcMedia.tagName === 'VIDEO') {
      const v = refs.srcMedia;
      const tEnd = (refs.trimEnd > 0) ? refs.trimEnd : (v.duration || 0);
      if (tEnd && v.currentTime >= tEnd - 0.03) refs.onOverlayEnd && refs.onOverlayEnd();
    }
    // 演出フェード（cueActive=target、強度をイージング。マスクに掛けて overlay を出し入れ）
    const target = (refs.cueActive && hasSrc && !refs.overlayEnded) ? 1 : 0;
    const fadeSec = Math.max(0.05, refs.fadeSec ?? 0.5);
    const stepMax = 0.04 / fadeSec;
    const cur = refs._ov ?? 0;
    refs._ov = cur + Math.max(-stepMax, Math.min(stepMax, target - cur));
    const ov = hasSrc ? refs._ov : 0;
    if (ov > 0.002 && hasSrc) texOv.upload(refs.srcMedia);
    const p = refs.cam.post || state?.post || FX_DEFAULT;
    // overlay が出ている時だけ色統計/ラプラシアンを走らせる（live-only 時の無駄/誤補正を回避）
    const active = ov > 0.01;
    // Pipeline は mix(uB, uA, mask)=白→uA/黒→uB。白=差し替え=overlay なので
    //   uA(liveSrc 引数)=overlay、uB(preSrc 引数)=live を渡す。
    //   色統計は uA(overlay) を uB(live) の色味へ寄せる＝AI素材が背景に馴染む。
    pipe.render(texOv, texLive, texMask, {
      liveScale: hasSrc ? containScale(refs.srcMedia.videoWidth || refs.srcMedia.naturalWidth,
        refs.srcMedia.videoHeight || refs.srcMedia.naturalHeight, frameAspect) : [1, 1],
      preScale: containScale(img.naturalWidth, img.naturalHeight, frameAspect),
      feather: blendCfg.feather,
      colorMatch: blendCfg.colorMatch && active, colorStrength: blendCfg.colorStrength,
      laplacian: blendCfg.laplacian && active,
      maskStrength: ov,
      exposure: p.exposure ?? 0, contrast: p.contrast ?? 1, saturation: p.saturation ?? 1,
      temperature: p.temperature ?? 0, vignette: p.vignette ?? 0, grain: p.grain ?? 0,
      aberration: 0, scanline: p.scanline ?? 0, showMask: 0,
      time: (performance.now() - t0) / 1000,
    }, { fbo: null, w: W, h: H });
  };
  // RAF は非表示タブで停止するので setInterval（multicam の教訓）
  setInterval(render, 40);
}

// ---- 列の状態同期（IP / FX / active / lock）--------------------------------
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
  // ストリーム再接続は接続パラメータ変化時のみ
  const key = `${cam.host || ''}|${cam.port || 8080}|${cam.auth || ''}`;
  if (key !== refs.streamKey) { refs.streamKey = key; refs.connectLive(); }
  // cue 一覧を state に追従（選択が消えたら先頭へ寄せて編集エリアも更新）。
  if (refs.renderCueList) {
    const cc = (state?.cues || []).filter((c) => (c.camera || '') === cam.id);
    if (refs.selectedCueId && !cc.some((c) => c.id === refs.selectedCueId)) {
      refs.selectedCueId = cc[0] ? cc[0].id : null;
      if (refs.loadSelectedCue) refs.loadSelectedCue();
    }
    refs.renderCueList();
  }
  // 演出 ON/OFF トグル（選択中の cue が発火中か）
  const cueActive = state?.control?.activeCue === refs.selectedCueId;
  if (cueActive && !refs.cueActive) {
    // 演出 ON になった瞬間: 動画を再生区間の頭から再生、終了フラグをリセット
    refs.overlayEnded = false;
    const v = refs.srcMedia;
    if (v && v.tagName === 'VIDEO') { try { v.currentTime = refs.trimStart || 0; v.play().catch(() => {}); } catch {} }
  } else if (!cueActive && refs.cueActive) {
    refs.overlayEnded = false; // OFF で次回に備える
  }
  refs.cueActive = cueActive;
  const tg = refs.el.querySelector('.cue-toggle');
  tg.textContent = cueActive ? '演出 OFF' : '演出 ON';
  tg.classList.toggle('on', cueActive);

  // active / lock 表示
  const activeId = activeCamId();
  refs.el.classList.toggle('active', unityAlive && activeId === cam.id);
  // 📺 切替: 実際に表示中（heartbeat の activeIndex）なら「● 表示中」、それ以外は「📺 切替」
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
    for (const c of columns.values()) c.el.remove();
    columns.clear();
    cams.forEach((cam, i) => {
      const refs = buildColumn(cam, i);
      columns.set(cam.id, refs);
      wrap.appendChild(refs.el);
    });
  }
  cams.forEach((cam) => { const r = columns.get(cam.id); if (r) syncColumn(r, cam); });
}

// ---- ポーリング -------------------------------------------------------------
async function pollState() {
  let rev = -1;
  for (;;) {
    try {
      const s = await (await fetch(`/state?rev=${rev}`)).json();
      if (s.rev !== rev) { rev = s.rev; state = s; renderColumns(); renderStatus(); floorMap && floorMap.onState(s); schedule && schedule.onState(s); }
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
    // active バッジ更新（state 再描画は重いので列だけ）
    for (const r of columns.values()) {
      const aid = activeCamId();
      r.el.classList.toggle('active', unityAlive && aid === r.id);
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
}

// ---- 全カメラ同時録画（ヘッダ）---------------------------------------------
//   生 / Quest最終 の 2 系統。押すと全列で一斉に開始、もう一度押すと全停止。
//   ファイルは各列のタグ（A / A_quest）+ ほぼ同一タイムスタンプで recordings/ へ。
let globalRecKind = null; // 'raw' | 'view' | null（全録画を発火した系統）
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
    // 何か録画中なら全停止（個別録画も含めてまとめて止める）
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
//   セクションは data-group="author|live" でタグ付け。無タグ（マルチカメラ等）は常時表示。
//   .app[data-mode] を切り替え → CSS が非該当グループを display:none にする。
const appEl = $('.app');
document.querySelectorAll('.mode-btn').forEach((b) => {
  b.onclick = () => {
    if (appEl) appEl.dataset.mode = b.dataset.mode;
    document.querySelectorAll('.mode-btn').forEach((x) => x.classList.toggle('active', x === b));
  };
});

// ---- ラン状態表示（Lap / cam / mode）+ ▶ ラン開始 ---------------------------
//   heartbeat（/unity/status）に lap / cam / mode が来たら表示。フィールド不在は「—」で互換。
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
// ▶ ラン開始（周回リセット）: control.runEpoch を +1 して postState（無ければ 0 起点）。
//   postState は control を shallow 置換するので、既存フィールドごと送り直す（活性 cue 等を保つ）。
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

// ---- 周回スケジュール --------------------------------------------------------
if ($('#schedule')) {
  schedule = createSchedule($('#schedule'), {
    getCameras: () => state?.cameras || [],
    getCues: () => state?.cues || [],
    getCourseOrder: () => state?.layout?.course?.order || null,
    saveSchedule: (sc) => postState({ schedule: sc }),
  });
  if (state) schedule.onState(state);
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
//   /discovery を 3 秒毎ポーリング。camera / show-server を一覧、二重 ID は赤警告。
//   自動追従トグルはサーバ状態（control.autoFollow）を正として反映する。
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
  // 自動追従トグル（フォーカス中は上書きしない）
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
      <div class="disc-id">${roleLabel(d.role)} <b>${idTxt}</b>${d.self ? ' <span class="disc-tag">自機</span>' : ''}${isConf ? ' <span class="disc-tag warn">⚠ 二重 ID</span>' : ''}</div>
      <div class="disc-meta-row">
        <span class="disc-ip">${d.ip}:${d.port}</span>
        <span class="disc-ver">v${d.version || '?'}</span>
        <span class="disc-age">${fmtAge(d.ageSec)}</span>
      </div>
      <div class="disc-name">${d.name || ''} <span class="disc-uuid">${(d.uuid || '').slice(0, 14)}</span></div>`;
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
    // Quest 行
    const q = d.quest || {};
    const questRow = document.createElement('div');
    questRow.className = 'diag-quest';
    questRow.innerHTML = `<b>Quest heartbeat</b> ${okMark(q.alive)} ${q.alive ? `（${q.activeCamera || '?'} / ${fmtAge(q.ageSec)}）` : '（未受信）'}`;
    out.appendChild(questRow);
    // カメラ行
    let apSuspect = false;
    for (const c of (d.cameras || [])) {
      const beaconOkHttpNg = c.beacon === true && c.http === false;
      if (beaconOkHttpNg) apSuspect = true;
      const row = document.createElement('div');
      row.className = 'diag-row' + (beaconOkHttpNg ? ' ap-suspect' : '');
      row.innerHTML = `
        <span class="diag-cam"><b>${c.id}</b>${c.pinned ? ' 📌' : ''}</span>
        <span class="diag-http">/info ${okMark(c.http)} <span class="diag-detail">${c.host ? `${c.host}:${c.port}` : ''} ${c.httpDetail || ''}</span></span>
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
