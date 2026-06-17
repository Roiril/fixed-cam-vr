// 廻リ視 web compositor — 1 ページ統合（縦割り = カメラ列）。
//   ステータス / マルチカメラ（ビュー・設定・マスク・合成素材）だけ。show.json が唯一の正。
//   ビューは Unity ScreenComposite を WebGL で完全再現（画質を当てた最終見た目＝Quest と同じ絵）。
import { Program, SourceTexture } from './gl.js';
import { FS_VIEW } from './shaders.js';

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
  col.innerHTML = `
    <div class="col-head">
      <b class="col-name">カメラ ${cam.id}</b>
      <span class="col-src"></span>
      <span class="col-status">接続中…</span>
      <button class="col-lock" title="このカメラに手動固定">🔒</button>
    </div>

    <div class="col-sec">
      <div class="sec-label">ビュー（最終見た目 = Quest と同じ）</div>
      <div class="view-wrap"><canvas class="view-canvas" width="${MW}" height="${MH}"></canvas></div>
      <div class="row-btns">
        <button class="cue-toggle" title="演出（合成映像）の ON / OFF">演出 ON</button>
        <span class="spacer"></span>
        <button class="cap-btn" title="この見た目を1枚 recordings/ に保存">📷</button>
        <button class="rec-btn" title="動画 録画 開始 / 停止（recordings/ へ）">⏺ 録画</button>
      </div>
    </div>

    <div class="col-sec">
      <div class="sec-label">設定（IP・画質）</div>
      <div class="ip-row">
        <input class="ip-host" placeholder="スマホ IP" title="配信スマホの IP">
        <input class="ip-port" placeholder="port" title="streamer=8080 / IP Camera Lite=8081">
        <input class="ip-auth" placeholder="user:pass" title="Basic 認証（空=なし）">
      </div>
      <div class="fx-rows"></div>
      <div class="row-btns"><button class="fx-reset">↺ 画質を初期化</button></div>
    </div>

    <div class="col-sec">
      <div class="sec-label">マスク（白 = 差し替え）</div>
      <div class="view-wrap"><canvas class="mask-canvas" width="${MW}" height="${MH}"></canvas></div>
      <div class="row-btns mask-tools">
        <button data-tool="rect" class="on">▭</button>
        <button data-tool="brush">🖌</button>
        <button data-tool="erase">⌫</button>
        <input type="range" class="brush-size" min="8" max="120" value="40" title="ブラシ">
        <button data-fill="left">◧</button><button data-fill="right">◨</button>
        <button data-fill="top">⬒</button><button data-fill="bottom">⬓</button>
        <button data-fill="all">■</button>
        <button class="mask-invert" title="反転">⇄</button>
        <button class="mask-clear" title="クリア">✕</button>
      </div>
    </div>

    <div class="col-sec">
      <div class="sec-label">合成素材（画像 / 動画）</div>
      <div class="row-btns">
        <select class="src-select"></select>
        <button class="src-refresh" title="captures/ を再読込">↻</button>
      </div>
      <input type="text" class="src-url" placeholder="または素材 URL（/captures/xxx.webm 等）" spellcheck="false">
      <div class="row-btns">
        <label class="chk"><input type="checkbox" class="cue-loop" checked> ループ</label>
        <span class="sld">fade <input type="range" class="cue-fade" min="0" max="3" step="0.1" value="0.5"><span class="fade-v">0.5s</span></span>
        <button class="cue-save accent">💾 cue 保存</button>
      </div>
      <span class="ed-status"></span>
    </div>`;

  const q = (s) => col.querySelector(s);
  const refs = {
    el: col, cam, index, id: cam.id,
    statusEl: q('.col-status'), srcSpan: q('.col-src'),
    hostI: q('.ip-host'), portI: q('.ip-port'), authI: q('.ip-auth'),
    fxInputs: {}, fxVals: {},
    liveImg: new Image(), srcMedia: null, srcReady: false,
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

  // ===== IP 設定（show.json へ）=====
  const onIp = () => {
    refs.cam.host = refs.hostI.value.trim();
    refs.cam.port = parseInt(refs.portI.value, 10) || 8080;
    refs.cam.auth = refs.authI.value.trim();
    postState({ cameras: state.cameras });
    connectLive();
  };
  refs.hostI.onchange = refs.portI.onchange = refs.authI.onchange = onIp;

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

  // ===== マスク段 =====
  const maskCanvas = q('.mask-canvas');
  const mctx = maskCanvas.getContext('2d', { willReadFrequently: true });
  mctx.fillStyle = '#000'; mctx.fillRect(0, 0, MW, MH);
  let tool = 'rect', dragging = false, dStart = null, dCur = null, snap = null;
  const lp = (ev) => {
    const r = maskCanvas.getBoundingClientRect();
    return { x: (ev.clientX - r.left) / r.width * MW, y: (ev.clientY - r.top) / r.height * MH };
  };
  const brush = (p, erase) => {
    const r = maskCanvas.getBoundingClientRect();
    const sz = parseInt(q('.brush-size').value, 10) / r.width * MW;
    mctx.fillStyle = erase ? '#000' : '#fff';
    mctx.beginPath(); mctx.arc(p.x, p.y, sz / 2, 0, Math.PI * 2); mctx.fill();
  };
  maskCanvas.addEventListener('pointerdown', (ev) => {
    try { maskCanvas.setPointerCapture(ev.pointerId); } catch { /* noop */ }
    dragging = true; dStart = dCur = lp(ev);
    if (tool === 'rect') snap = mctx.getImageData(0, 0, MW, MH);
    else brush(dCur, tool === 'erase');
  });
  maskCanvas.addEventListener('pointermove', (ev) => {
    if (!dragging) return; dCur = lp(ev);
    if (tool === 'rect') {
      mctx.putImageData(snap, 0, 0);
      mctx.strokeStyle = '#fff'; mctx.lineWidth = 2; mctx.setLineDash([6, 4]);
      mctx.strokeRect(Math.min(dStart.x, dCur.x), Math.min(dStart.y, dCur.y),
        Math.abs(dCur.x - dStart.x), Math.abs(dCur.y - dStart.y));
      mctx.setLineDash([]);
    } else brush(dCur, tool === 'erase');
  });
  maskCanvas.addEventListener('pointerup', () => {
    if (dragging && tool === 'rect' && dStart && dCur && snap) {
      mctx.putImageData(snap, 0, 0); mctx.fillStyle = '#fff';
      mctx.fillRect(Math.min(dStart.x, dCur.x), Math.min(dStart.y, dCur.y),
        Math.abs(dCur.x - dStart.x), Math.abs(dCur.y - dStart.y));
    }
    dragging = false; dStart = dCur = snap = null;
  });
  col.querySelectorAll('[data-tool]').forEach((b) => b.addEventListener('click', () => {
    tool = b.dataset.tool;
    col.querySelectorAll('[data-tool]').forEach((x) => x.classList.toggle('on', x === b));
  }));
  col.querySelectorAll('[data-fill]').forEach((b) => b.addEventListener('click', () => {
    const f = b.dataset.fill; mctx.fillStyle = '#fff';
    if (f === 'all') mctx.fillRect(0, 0, MW, MH);
    if (f === 'left') mctx.fillRect(0, 0, MW / 2, MH);
    if (f === 'right') mctx.fillRect(MW / 2, 0, MW / 2, MH);
    if (f === 'top') mctx.fillRect(0, 0, MW, MH / 2);
    if (f === 'bottom') mctx.fillRect(0, MH / 2, MW, MH / 2);
  }));
  q('.mask-invert').onclick = () => {
    const d = mctx.getImageData(0, 0, MW, MH);
    for (let k = 0; k < d.data.length; k += 4) {
      d.data[k] = 255 - d.data[k]; d.data[k + 1] = 255 - d.data[k + 1]; d.data[k + 2] = 255 - d.data[k + 2];
    }
    mctx.putImageData(d, 0, 0);
  };
  q('.mask-clear').onclick = () => { mctx.fillStyle = '#000'; mctx.fillRect(0, 0, MW, MH); };
  const maskIsEmpty = () => {
    const d = mctx.getImageData(0, 0, MW, MH).data;
    for (let k = 0; k < d.length; k += 4) if (d[k] > 8) return false;
    return true;
  };

  // ===== 合成素材 =====
  const srcSelect = q('.src-select'), srcUrl = q('.src-url');
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
    refs.srcMedia = null; refs.srcReady = false;
    if (!url) return;
    if (isVideoUrl(url)) {
      const v = document.createElement('video');
      v.muted = true; v.loop = true; v.playsInline = true; v.crossOrigin = 'anonymous'; v.src = url;
      v.addEventListener('canplay', () => { refs.srcReady = true; v.play().catch(() => {}); });
      refs.srcMedia = v;
    } else {
      const im = new Image(); im.crossOrigin = 'anonymous';
      im.onload = () => { refs.srcReady = true; }; im.src = url;
      refs.srcMedia = im;
    }
  }
  srcSelect.onchange = () => { srcUrl.value = ''; loadSource(srcSelect.value); };
  srcUrl.onchange = () => loadSource(srcUrl.value.trim());
  q('.src-refresh').onclick = refreshCaptures;
  const fade = q('.cue-fade');
  fade.oninput = () => { q('.fade-v').textContent = parseFloat(fade.value).toFixed(1) + 's'; };

  const ed = (m, cls = '') => { const e = q('.ed-status'); e.textContent = m; e.className = 'ed-status ' + cls; };

  // ===== cue 保存（1 カメラ 1 cue: id = cue_<camId>）=====
  q('.cue-save').onclick = async () => {
    const camId = refs.cam.id;
    const sourceUrl = (srcUrl.value.trim() || srcSelect.value || '').trim();
    if (!sourceUrl) return ed('合成素材を選んで（or URL 直書き）', 'err');
    const id = `cue_${camId}`;
    try {
      let maskUrl = '';
      if (!maskIsEmpty()) {
        ed('マスク書き出し中…');
        const blob = await new Promise((r) => maskCanvas.toBlob(r, 'image/png'));
        const res = await (await fetch(`/masks?name=${encodeURIComponent(id)}`, { method: 'POST', body: blob })).json();
        if (!res.ok) throw new Error(res.error || 'マスク保存失敗');
        maskUrl = res.url;
      }
      const f = parseFloat(fade.value) || 0.5;
      const cue = {
        id, name: `カメラ ${camId}`, camera: camId, maskUrl, sourceUrl,
        strength: 1, loop: q('.cue-loop').checked, fadeIn: f, fadeOut: f,
      };
      const s = await getState();
      const cues = s.cues || [];
      const k = cues.findIndex((c) => c.id === id);
      if (k >= 0) cues[k] = cue; else cues.push(cue);
      const r2 = await postState({ cues });
      if (!r2.ok) throw new Error('state 保存失敗');
      ed(`✓ 保存（${maskUrl ? 'マスク付き' : '全面差し替え'}）。演出 ON で出る`, 'ok');
    } catch (e) { ed('保存失敗: ' + e.message, 'err'); }
  };

  // ===== 操作系（演出 ON/OFF・固定）=====
  q('.cue-toggle').onclick = () => {
    const active = state?.control?.activeCue === `cue_${refs.cam.id}`;
    postCommand(active ? { type: 'stopCue' } : { type: 'playCue', id: `cue_${refs.cam.id}` });
  };
  q('.col-lock').onclick = () => postCommand({ type: 'setCameraOverride', camera: refs.cam.id });

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
    // ★ビューの内部レンダー解像度もカメラ比にする → FS_VIEW が letterbox せず full-fill。
    //   表示(CSS)だけでなくピクセル自体が黒帯ゼロ → 📷/⏺ のキャプチャにも黒帯が入らない。
    viewCanvas.width = Math.max(2, Math.round(360 * a));
    viewCanvas.height = 360;
  };

  // ===== 📷 キャプチャ / ⏺ 録画（★生フレーム = 画質加工も合成も無しの素の配信映像を recordings/ へ）=====
  q('.cap-btn').onclick = () => {
    const img = refs.liveImg;
    if (!img.naturalWidth) return ed('ライブ映像が無い', 'err');
    const cv = document.createElement('canvas');
    cv.width = img.naturalWidth; cv.height = img.naturalHeight;
    cv.getContext('2d').drawImage(img, 0, 0);
    cv.toBlob(async (blob) => {
      if (!blob) return ed('キャプチャ不可', 'err');
      const r = await (await fetch(`/save?type=image&to=recordings&cam=${refs.cam.id}`,
        { method: 'POST', body: blob })).json();
      ed(r.ok ? `📷 保存(生): ${r.name}` : '保存失敗', r.ok ? 'ok' : 'err');
    }, 'image/jpeg', 0.95);
  };

  let mediaRec = null, recChunks = [], rawDrawTimer = 0;
  q('.rec-btn').onclick = () => {
    const btn = q('.rec-btn');
    if (mediaRec && mediaRec.state === 'recording') { mediaRec.stop(); return; }
    const img = refs.liveImg;
    if (!img.naturalWidth) return ed('ライブ映像が無い', 'err');
    // 生フレーム用 2D canvas（加工・合成なし）。live をそのまま描いて captureStream する。
    const raw = document.createElement('canvas');
    raw.width = img.naturalWidth; raw.height = img.naturalHeight;
    const rctx = raw.getContext('2d');
    rctx.drawImage(img, 0, 0);
    clearInterval(rawDrawTimer);
    rawDrawTimer = setInterval(() => { if (img.naturalWidth) rctx.drawImage(img, 0, 0, raw.width, raw.height); }, 33);
    let stream;
    try { stream = raw.captureStream(30); }
    catch (e) { clearInterval(rawDrawTimer); return ed('録画不可: ' + e.message, 'err'); }
    recChunks = [];
    const mime = (window.MediaRecorder && MediaRecorder.isTypeSupported('video/webm;codecs=vp9'))
      ? 'video/webm;codecs=vp9' : 'video/webm';
    mediaRec = new MediaRecorder(stream, { mimeType: mime });
    mediaRec.ondataavailable = (e) => { if (e.data && e.data.size) recChunks.push(e.data); };
    mediaRec.onstop = async () => {
      clearInterval(rawDrawTimer);
      btn.textContent = '⏺ 録画'; btn.classList.remove('on');
      const blob = new Blob(recChunks, { type: 'video/webm' });
      const r = await (await fetch(`/save?type=video&to=recordings&cam=${refs.cam.id}`,
        { method: 'POST', body: blob })).json();
      ed(r.ok ? `⏹ 録画保存(生): ${r.name}（${Math.round(r.size / 1024)}KB）` : '録画保存失敗', r.ok ? 'ok' : 'err');
    };
    mediaRec.start();
    btn.textContent = '⏹ 停止'; btn.classList.add('on');
    ed('● 録画中（生）…', 'ok');
  };

  // ===== WebGL ビュー（ScreenComposite 再現）=====
  setupView(refs, q('.view-canvas'), mctx, maskCanvas);

  // 既存 cue があれば素材 URL を復元（見た目を現状に合わせる）
  const existing = (state?.cues || []).find((c) => c.id === `cue_${cam.id}`);
  if (existing?.sourceUrl) { srcUrl.value = existing.sourceUrl; loadSource(existing.sourceUrl); }

  connectLive();
  refs.populateSources();
  return refs;
}

// ---- 列ごとの WebGL ビュー --------------------------------------------------
function setupView(refs, canvas, mctx, maskCanvas) {
  // preserveDrawingBuffer: true — 📷 toBlob / ⏺ captureStream で view を取り込めるようにする
  const gl = canvas.getContext('webgl2', { antialias: false, premultipliedAlpha: false, alpha: false, preserveDrawingBuffer: true });
  if (!gl) { canvas.replaceWith(Object.assign(document.createElement('div'),
    { className: 'view-fallback', textContent: 'WebGL2 非対応' })); return; }
  const vao = gl.createVertexArray(); gl.bindVertexArray(vao);
  gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND);
  const prog = new Program(gl, FS_VIEW);
  const texLive = new SourceTexture(gl), texMask = new SourceTexture(gl), texOv = new SourceTexture(gl);
  // contain-fit scale（ScreenComposite._LiveScale と同義: scale<1 を letterbox）。
  // frameAspect はキャンバス内部比（applyAspect がカメラ比へリサイズする）を毎フレーム参照。
  const containScale = (w, h, frameAspect) => {
    if (!w || !h) return [1, 1];
    const a = w / h;
    return a > frameAspect ? [1, frameAspect / a] : [a / frameAspect, 1];
  };
  let t0 = performance.now();
  const render = () => {
    // applyAspect で canvas.width/height がカメラ比に変わるので毎回再計算（黒帯を出さない肝）
    const screen = { fbo: null, w: canvas.width, h: canvas.height };
    const frameAspect = canvas.width / canvas.height;
    const img = refs.liveImg;
    if (img.naturalWidth) texLive.upload(img);
    texMask.upload(maskCanvas);
    // 演出 ON（このカメラの cue が発火中）のときだけオーバーレイを出す。
    // → 演出 OFF（停止）で合成映像が消える。Quest の挙動と一致。
    let ovScale = [1, 1], ovStrength = 0;
    if (refs.cueActive && refs.srcMedia && refs.srcReady) {
      texOv.upload(refs.srcMedia);
      ovScale = containScale(refs.srcMedia.videoWidth || refs.srcMedia.naturalWidth,
        refs.srcMedia.videoHeight || refs.srcMedia.naturalHeight, frameAspect);
      ovStrength = 1;
    }
    const p = refs.cam.post || state?.post || FX_DEFAULT;
    prog.draw(screen, {
      uLive: texLive, uOverlay: texOv, uMask: texMask,
      uLiveScale: containScale(img.naturalWidth, img.naturalHeight, frameAspect),
      uOverlayScale: ovScale, uOverlayStrength: ovStrength,
      uExposure: p.exposure ?? 0, uContrast: p.contrast ?? 1, uSaturation: p.saturation ?? 1,
      uTemperature: p.temperature ?? 0, uVignette: p.vignette ?? 0, uGrain: p.grain ?? 0,
      uScanline: p.scanline ?? 0, uScanlineCount: 240, uTime: (performance.now() - t0) / 1000,
    });
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
  // 演出 ON/OFF トグル（このカメラの cue が発火中か）
  const cueActive = state?.control?.activeCue === `cue_${cam.id}`;
  refs.cueActive = cueActive;
  const tg = refs.el.querySelector('.cue-toggle');
  tg.textContent = cueActive ? '演出 OFF' : '演出 ON';
  tg.classList.toggle('on', cueActive);

  // active / lock 表示
  const activeId = activeCamId();
  refs.el.classList.toggle('active', unityAlive && activeId === cam.id);
  refs.el.querySelector('.col-lock').classList.toggle('on', state?.control?.cameraOverride === cam.id);
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
      if (s.rev !== rev) { rev = s.rev; state = s; renderColumns(); renderStatus(); }
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
    // active バッジ更新（state 再描画は重いので列だけ）
    for (const r of columns.values()) {
      const aid = activeCamId();
      r.el.classList.toggle('active', unityAlive && aid === r.id);
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
}

// ---- 起動 -------------------------------------------------------------------
$('#autoZone').onclick = () => postCommand({ type: 'setCameraOverride', camera: null });
$('#openRecordings').onclick = () => fetch('/open-dir?dir=recordings').catch(() => {});
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

pollState();
pollUnity();
