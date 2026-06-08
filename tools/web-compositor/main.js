// エントリポイント: ソース/マスク/UI を束ねて毎フレーム Pipeline を回す。
import { createContext, SourceTexture } from './gl.js';
import { Pipeline } from './pipeline.js';
import {
  TestPattern, GradientSource, WebcamSource, VideoFileSource, ImageStreamSource, MaskCanvas,
} from './sources.js';

const RES = { '320x180': [320, 180], '640x360': [640, 360], '960x540': [960, 540], '1280x720': [1280, 720] };

const params = {
  res: '640x360',
  levels: 7,
  colorMatch: true,
  colorStrength: 1.0,
  laplacian: true,
  feather: 0.3,
  // post
  exposure: 0.0, contrast: 1.0, saturation: 1.0, temperature: 0.0,
  vignette: 0.25, grain: 0.06, aberration: 0.0, scanline: 0.0,
  showMask: false,
  fit: true,
  liveScale: [1, 1], preScale: [1, 1],
  time: 0,
};

const els = {};
let gl, pipeline, mask;
// 各スロットに今どの種類のソースが割り当たっているか（ボタンのハイライト表示用）
const assigned = { white: 'test', black: 'gradient' };
let liveSrc, preSrc;                       // ソースオブジェクト（white=live, black=pre）
const liveTex = { v: null }, preTex = { v: null }, maskTex = { v: null }; // SourceTexture

function $(id) { return document.getElementById(id); }

function setStatus(msg, isErr = false) {
  els.status.textContent = msg;
  els.status.style.color = isErr ? '#ff8080' : '#7fffb0';
}

function currentRes() { return RES[params.res]; }

// 各スロットのソースボタンに .active を付け直す（現在割当を可視化）
function refreshSourceButtons() {
  for (const which of ['white', 'black']) {
    document.querySelectorAll(`[data-which="${which}"][data-src]`).forEach((b) => {
      b.classList.toggle('active', b.dataset.src === assigned[which]);
    });
  }
}

// ソースのアスペクトを作業解像度に contain-fit する uScale。fit=false でストレッチ。
function fitScale(el, dstW, dstH) {
  if (!params.fit) return [1, 1];
  const sw = el.videoWidth || el.naturalWidth || el.width || 0;
  const sh = el.videoHeight || el.naturalHeight || el.height || 0;
  if (!sw || !sh) return [1, 1];
  const srcA = sw / sh, dstA = dstW / dstH;
  return srcA < dstA ? [srcA / dstA, 1] : [1, dstA / srcA];
}

function rebuildPipeline() {
  const [w, h] = currentRes();
  els.canvas.width = w; els.canvas.height = h;
  pipeline.allocate(w, h, params.levels);
  mask.resize(w, h);
  maskTex.v.upload(mask.element, true);
  // 内蔵ソースの解像度も合わせる
  if (liveSrc instanceof TestPattern) setLive(new TestPattern(w, h));
  if (preSrc instanceof GradientSource) setPre(new GradientSource(w, h));
}

function setLive(src) {
  if (liveSrc && liveSrc.dispose) liveSrc.dispose();
  liveSrc = src;
}
function setPre(src) {
  if (preSrc && preSrc.dispose) preSrc.dispose();
  preSrc = src;
}

// ---- ソース選択 ----
async function pickSource(which, kind) {
  const slot = which === 'white' ? '白' : '黒';
  try {
    if (kind === 'test') { which === 'white' ? setLive(new TestPattern(...currentRes())) : setPre(new TestPattern(...currentRes())); }
    else if (kind === 'gradient') { which === 'white' ? setLive(new GradientSource(...currentRes())) : setPre(new GradientSource(...currentRes())); }
    else if (kind === 'webcam') {
      const cam = new WebcamSource();
      await cam.start();
      which === 'white' ? setLive(cam) : setPre(cam);
    } else if (kind === 'file') {
      const url = await pickFile('video/*');
      if (!url) return;
      which === 'white' ? setLive(new VideoFileSource(url)) : setPre(new VideoFileSource(url));
    } else if (kind === 'url' || kind === 'stream') {
      const url = kind === 'stream'
        ? $('streamUrl').value.trim()
        : prompt('MJPEG / 動画 / 画像 URL を入力\n例: http://192.168.1.50:8080/video');
      if (!url) return;
      const isVideoFile = /\.(mp4|webm|ogg|mov)(\?|$)/i.test(url);
      const src = isVideoFile ? new VideoFileSource(url) : new ImageStreamSource(url);
      which === 'white' ? setLive(src) : setPre(src);
    }
    assigned[which] = kind;   // test/gradient/webcam/file/url/stream いずれも同名ボタンが点く
    refreshSourceButtons();
    setStatus(`${slot}スロット → ${kind}`);
  } catch (e) {
    setStatus(`${slot}スロット取得失敗: ${e.message}`, true);
  }
}

function pickFile(accept) {
  return new Promise((resolve) => {
    const inp = document.createElement('input');
    inp.type = 'file'; inp.accept = accept;
    inp.onchange = () => resolve(inp.files[0] ? URL.createObjectURL(inp.files[0]) : null);
    inp.click();
  });
}

// ==== 配信フレームのキャプチャ / 録画（PC内 captures/ に保存）====
let captureImg = null;
let streamRec = null, streamRecChunks = [], streamRecCanvas = null, streamRecRAF = 0, streamRecEl = null;

// 「配信されている画像」を返す。接続中スロットの MJPEG を優先、無ければ URL 直の <img>。
function getStreamElement() {
  for (const s of [liveSrc, preSrc]) {
    if (s instanceof ImageStreamSource && s.ready) return s.element;
  }
  if (!captureImg) { captureImg = new Image(); captureImg.crossOrigin = 'anonymous'; }
  const url = $('streamUrl').value.trim();
  if (captureImg.src !== url) captureImg.src = url;
  return (captureImg.complete && captureImg.naturalWidth > 0) ? captureImg : null;
}

function elSize(el) {
  return [el.naturalWidth || el.videoWidth || el.width, el.naturalHeight || el.videoHeight || el.height];
}

// 静止画キャプチャ → PC 保存
async function captureStill() {
  const el = getStreamElement();
  if (!el) { setStatus('配信が見つからない（URL/接続を確認。読込中なら再度押す）', true); return; }
  const [w, h] = elSize(el);
  const c = document.createElement('canvas'); c.width = w; c.height = h;
  c.getContext('2d').drawImage(el, 0, 0, w, h);
  const blob = await new Promise((r) => c.toBlob(r, 'image/jpeg', 0.92));
  await uploadCapture(blob, 'image');
}

// セルフタイマー: sec 秒カウントダウンしてからキャプチャ
let captureTimer = 0;
function captureStillAfter(sec) {
  const btn = $('captureTimerBtn');
  if (captureTimer) {            // カウントダウン中の再押下はキャンセル
    clearInterval(captureTimer); captureTimer = 0;
    btn.textContent = '⏱ 3秒後にキャプチャ';
    setStatus('タイマーキャンセル');
    return;
  }
  let n = sec;
  setStatus(`${n} 秒後にキャプチャ…`); btn.textContent = `⏱ ${n}（押すと中止）`;
  captureTimer = setInterval(() => {
    n -= 1;
    if (n > 0) { setStatus(`${n} 秒後にキャプチャ…`); btn.textContent = `⏱ ${n}（押すと中止）`; return; }
    clearInterval(captureTimer); captureTimer = 0;
    btn.textContent = '⏱ 3秒後にキャプチャ';
    captureStill();
  }, 1000);
}

// 配信を録画 → PC 保存（MJPEG を canvas に描いて MediaRecorder）
function toggleStreamRecord() {
  if (streamRec) { streamRec.stop(); return; }
  const el = getStreamElement();
  if (!el) { setStatus('配信が見つからない（URL/接続を確認）', true); return; }
  streamRecEl = el;
  const [w, h] = elSize(el);
  streamRecCanvas = document.createElement('canvas');
  streamRecCanvas.width = w; streamRecCanvas.height = h;
  const ctx = streamRecCanvas.getContext('2d');
  const draw = () => { try { ctx.drawImage(streamRecEl, 0, 0, w, h); } catch (_) {} streamRecRAF = requestAnimationFrame(draw); };
  draw();
  let stream;
  try { stream = streamRecCanvas.captureStream(30); }
  catch (e) { setStatus('captureStream 非対応: ' + e.message, true); cancelAnimationFrame(streamRecRAF); return; }
  streamRecChunks = [];
  try { streamRec = new MediaRecorder(stream, { mimeType: 'video/webm' }); }
  catch (e) { setStatus('MediaRecorder 非対応: ' + e.message, true); streamRec = null; cancelAnimationFrame(streamRecRAF); return; }
  streamRec.ondataavailable = (e) => { if (e.data.size) streamRecChunks.push(e.data); };
  streamRec.onstop = async () => {
    cancelAnimationFrame(streamRecRAF);
    const blob = new Blob(streamRecChunks, { type: 'video/webm' });
    streamRec = null;
    els.captureRecBtn.textContent = '⏺ 配信を録画';
    await uploadCapture(blob, 'video');
  };
  streamRec.start();
  els.captureRecBtn.textContent = '⏹ 録画停止';
  setStatus('配信を録画中…（停止で PC に保存）');
}

// blob を PC へ保存（/save）。保存サーバ未起動ならダウンロードにフォールバック。
async function uploadCapture(blob, type) {
  try {
    const r = await fetch(`/save?type=${type}`, { method: 'POST', body: blob });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const j = await r.json();
    setStatus(`PC に保存: ${j.name}`);
    refreshGallery();
    return j;
  } catch (e) {
    downloadBlob(blob, type === 'video' ? 'capture.webm' : 'capture.jpg');
    setStatus('保存サーバ未起動 → ダウンロードに保存（serve.ps1 で起動推奨）', true);
    return null;
  }
}

function downloadBlob(blob, name) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
}

// PC 内 captures/ の一覧を取得して表示
async function refreshGallery() {
  let items;
  try { items = await fetch('/captures/list').then((r) => r.json()); }
  catch (e) { return; } // 保存サーバ未起動時は黙って何もしない
  const g = $('gallery');
  if (!g) return;
  g.innerHTML = '';
  if (!items.length) { g.innerHTML = '<p class="hint">まだ保存なし</p>'; return; }
  for (const it of items) {
    const card = document.createElement('div');
    card.className = 'gal-item';
    const media = it.type === 'video'
      ? `<video class="gal-media" src="${it.url}" muted preload="metadata" title="クリックで保存場所を開く"></video>`
      : `<img class="gal-media" src="${it.url}" alt="" title="クリックで保存場所を開く">`;
    card.innerHTML = `${media}
      <div class="gal-name">${it.type === 'video' ? '🎬' : '📷'} ${it.name}</div>
      <div class="btns">
        <button class="gal-w">白に接続</button>
        <button class="gal-b">黒に接続</button>
        <button class="gal-open" title="保存場所を開く">📂</button>
      </div>`;
    card.querySelector('.gal-media').onclick = () => revealFile(it.name);
    card.querySelector('.gal-w').onclick = () => assignCapture('white', it);
    card.querySelector('.gal-b').onclick = () => assignCapture('black', it);
    card.querySelector('.gal-open').onclick = () => revealFile(it.name);
    g.appendChild(card);
  }
}

// 保存ファイルをファイルマネージャで開く（PC 上のサーバが explorer を起動）
async function revealFile(name) {
  try {
    const j = await fetch('/reveal?name=' + encodeURIComponent(name)).then((r) => r.json());
    setStatus(j.ok ? '保存場所を開いた: ' + name : '開けない: ' + (j.error || ''), !j.ok);
  } catch (e) {
    setStatus('保存サーバ未起動のため場所を開けない', true);
  }
}

function assignCapture(which, it) {
  const src = it.type === 'video' ? new VideoFileSource(it.url) : new ImageStreamSource(it.url);
  which === 'white' ? setLive(src) : setPre(src);
  assigned[which] = 'capture';
  refreshSourceButtons();
  setStatus(`${which === 'white' ? '白' : '黒'}スロット ← ${it.name}`);
}

// ==== 動画生成プロンプトのストック（PC内 prompts.json）====
let promptEditingId = null;

// 雛形は種別ごと。画像=インペイント、動画=静止カメラ。
const PROMPT_TEMPLATES = {
  image:
`Add a Japanese yūrei woman standing in the far corner of THIS room, long stringy wet
black hair completely covering her face, white burial kimono (shini-shōzoku), pale
greyish skin, soft contact shadow on the floor beneath her feet.
Photorealistic, practical-effects horror, hyper-detailed texture, dim low light, shot
on a phone camera, subtle film grain.
Keep the room, walls, floor, furniture and lighting exactly the same. Change nothing
except adding the figure.
Negative: glow, ethereal, ukiyo-e, anime, cartoon, well-lit, extra limbs, deformed face.`,
  video:
`Static shot, locked-off tripod camera, no camera movement, background perfectly still.
The yūrei woman slowly tilts her head, her long black hair drifting, then takes one
jerky step toward the camera. Undercranked, unnatural stuttering motion. Her white
kimono sways slightly. Everything else in the room stays completely static.
Duration ~4 seconds, minimal motion. Photorealistic, dim low light, phone-camera look,
film grain.
Negative: camera movement, pan, zoom, dolly, glow, ethereal, anime, cartoon, warping background.`,
};

function getPromptKind() {
  const el = document.querySelector('input[name="promptKind"]:checked');
  return el ? el.value : 'image';
}
function setPromptKind(kind) {
  const el = document.querySelector(`input[name="promptKind"][value="${kind === 'video' ? 'video' : 'image'}"]`);
  if (el) el.checked = true;
}

async function loadPrompts() {
  let items;
  try { items = await fetch('/prompts').then((r) => r.json()); }
  catch (e) { return; } // 保存サーバ未起動時は何もしない
  renderPrompts(items);
}

function renderPrompts(items) {
  const list = $('promptList');
  if (!list) return;
  list.innerHTML = '';
  if (!items.length) { list.innerHTML = '<p class="hint">まだ保存なし</p>'; return; }
  // 画像生成 / 動画生成 でグループ分け
  const groups = [
    { kind: 'image', label: '🖼️ 画像生成', items: items.filter((p) => (p.kind || 'video') === 'image') },
    { kind: 'video', label: '🎬 動画生成', items: items.filter((p) => (p.kind || 'video') === 'video') },
  ];
  for (const g of groups) {
    if (!g.items.length) continue;
    const head = document.createElement('div');
    head.className = 'prompt-group-head';
    head.textContent = `${g.label}（${g.items.length}）`;
    list.appendChild(head);
    for (const p of g.items) list.appendChild(makePromptItem(p));
  }
}

function makePromptItem(p) {
  const item = document.createElement('div');
  item.className = 'prompt-item';
  item.innerHTML = `<div class="prompt-head">${escapeHtml(p.title || '(無題)')}</div>
    <div class="prompt-body">${escapeHtml(p.text)}</div>
    <div class="btns">
      <button class="pr-copy">📋 コピー</button>
      <button class="pr-edit">編集</button>
      <button class="pr-del">削除</button>
    </div>`;
  item.querySelector('.pr-copy').onclick = () => copyText(p.text);
  item.querySelector('.pr-edit').onclick = () => editPrompt(p);
  item.querySelector('.pr-del').onclick = () => deletePrompt(p.id);
  return item;
}

function escapeHtml(s) {
  return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

async function savePrompt() {
  const title = $('promptTitle').value.trim();
  const text = $('promptText').value.trim();
  if (!text) { setStatus('プロンプト本文が空', true); return; }
  try {
    const r = await fetch('/prompts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: promptEditingId, title, text, kind: getPromptKind() }),
    });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const j = await r.json();
    renderPrompts(j.items);
    newPrompt();
    setStatus(promptEditingId ? 'プロンプト更新' : 'プロンプト保存');
  } catch (e) {
    setStatus('保存サーバ未起動（serve.ps1 で起動）', true);
  }
}

function newPrompt() {
  promptEditingId = null;
  $('promptTitle').value = '';
  $('promptText').value = '';
  $('promptSave').textContent = '＋ 保存';
}

function editPrompt(p) {
  promptEditingId = p.id;
  setPromptKind(p.kind || 'video');
  $('promptTitle').value = p.title || '';
  $('promptText').value = p.text;
  $('promptSave').textContent = '✓ 更新';
  $('promptText').scrollIntoView({ block: 'nearest' });
  setStatus('編集中: ' + (p.title || '(無題)'));
}

async function deletePrompt(id) {
  try {
    const j = await fetch('/prompts/delete', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id }),
    }).then((r) => r.json());
    renderPrompts(j.items);
    if (promptEditingId === id) newPrompt();
    setStatus('プロンプト削除');
  } catch (e) { setStatus('削除失敗', true); }
}

// クリップボードへ。LAN(http)で Clipboard API が不可なら execCommand にフォールバック。
async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    setStatus('コピーした');
  } catch (e) {
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    setStatus(ok ? 'コピーした' : 'コピー失敗（手動選択して）', !ok);
  }
}

// ---- マスク描画 ----
function setupMaskCanvas() {
  const view = els.maskView;
  let painting = false;
  const toMask = (ev) => {
    const r = view.getBoundingClientRect();
    const x = (ev.clientX - r.left) / r.width * mask.canvas.width;
    const y = (ev.clientY - r.top) / r.height * mask.canvas.height;
    return [x, y];
  };
  const apply = (ev) => {
    if (mask.mode === 'split') mask.moveSplit(...toMask(ev));
    else mask.paint(...toMask(ev));
  };
  const down = (ev) => { painting = true; mask.erasing = ev.button === 2 || ev.shiftKey; apply(ev); ev.preventDefault(); };
  const move = (ev) => { if (painting) apply(ev); };
  const up = () => { painting = false; };
  view.addEventListener('pointerdown', down);
  view.addEventListener('pointermove', move);
  window.addEventListener('pointerup', up);
  view.addEventListener('contextmenu', (e) => e.preventDefault());
}

// ---- UI 配線 ----
function bindUI() {
  // ソースボタン
  document.querySelectorAll('[data-src]').forEach((b) => {
    b.onclick = () => pickSource(b.dataset.which, b.dataset.src);
  });
  // マスクプリセット
  document.querySelectorAll('[data-mask]').forEach((b) => {
    b.onclick = () => mask.preset(b.dataset.mask);
  });
  $('maskInvert').onclick = () => mask.invert();
  $('brush').oninput = (e) => { mask.brush = +e.target.value; };

  // レンジ/チェックの汎用バインド
  const bind = (id, key, isCheck = false) => {
    const el = $(id);
    if (!el) return;
    const out = $(id + 'Val');
    const apply = () => {
      params[key] = isCheck ? el.checked : +el.value;
      if (out) out.textContent = (+el.value).toFixed(2);
    };
    el.addEventListener('input', apply);
    apply();
  };
  bind('colorStrength', 'colorStrength');
  bind('feather', 'feather');
  bind('exposure', 'exposure');
  bind('contrast', 'contrast');
  bind('saturation', 'saturation');
  bind('temperature', 'temperature');
  bind('vignette', 'vignette');
  bind('grain', 'grain');
  bind('aberration', 'aberration');
  bind('scanline', 'scanline');
  bind('colorMatch', 'colorMatch', true);
  bind('laplacian', 'laplacian', true);
  bind('showMask', 'showMask', true);
  bind('fit', 'fit', true);

  $('res').onchange = (e) => { params.res = e.target.value; rebuildPipeline(); };
  $('levels').oninput = (e) => { params.levels = +e.target.value; $('levelsVal').textContent = e.target.value; rebuildPipeline(); };

  // 配信のキャプチャ / 録画（PC 保存）と保存済み一覧
  $('captureBtn').onclick = captureStill;
  $('captureTimerBtn').onclick = () => captureStillAfter(3);
  $('captureRecBtn').onclick = toggleStreamRecord;
  $('galleryRefresh').onclick = refreshGallery;

  // 動画生成プロンプト
  $('promptSave').onclick = savePrompt;
  $('promptNew').onclick = newPrompt;
  $('promptTemplate').onclick = () => {
    const k = getPromptKind();
    $('promptText').value = PROMPT_TEMPLATES[k];
    setStatus(`${k === 'image' ? '画像' : '動画'}生成の雛形を挿入`);
  };

  // 白 ⇄ 黒 スロット入替
  $('swap').onclick = () => { const t = liveSrc; liveSrc = preSrc; preSrc = t; setStatus('白 ⇄ 黒 入替'); };
}

// ---- メインループ ----
let last = performance.now();
let frames = 0, fpsT = 0;

function loop(now) {
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  params.time += dt;

  liveSrc.update(dt);
  preSrc.update(dt);

  if (liveSrc.ready) liveTex.v.upload(liveSrc.element, true);
  if (preSrc.ready) preTex.v.upload(preSrc.element, true);
  if (mask.dirty) { maskTex.v.upload(mask.element, true); mask.dirty = false; }

  const w = els.canvas.width, h = els.canvas.height;
  params.liveScale = fitScale(liveSrc.element, w, h);
  params.preScale = fitScale(preSrc.element, w, h);
  const screen = { fbo: null, w, h };
  pipeline.render(liveTex.v, preTex.v, maskTex.v, params, screen);

  // FPS
  frames++; fpsT += dt;
  if (fpsT >= 0.5) { els.fps.textContent = (frames / fpsT).toFixed(0) + ' fps'; frames = 0; fpsT = 0; }

  requestAnimationFrame(loop);
}

function init() {
  els.canvas = $('gl');
  els.status = $('status');
  els.fps = $('fps');
  els.maskView = $('maskView');
  els.captureRecBtn = $('captureRecBtn');

  try {
    const ctx = createContext(els.canvas);
    gl = ctx.gl;
  } catch (e) {
    setStatus(e.message, true);
    return;
  }
  pipeline = new Pipeline(gl);

  const [w, h] = currentRes();
  liveTex.v = new SourceTexture(gl);
  preTex.v = new SourceTexture(gl);
  maskTex.v = new SourceTexture(gl);

  setLive(new TestPattern(w, h));
  setPre(new GradientSource(w, h));
  mask = new MaskCanvas(w, h);
  // マスクのプレビューを <img> ではなく canvas そのものを表示
  els.maskView.replaceWith(mask.canvas);
  mask.canvas.id = 'maskView';
  mask.canvas.className = 'mask-view';
  els.maskView = mask.canvas;

  rebuildPipeline();
  setupMaskCanvas();
  bindUI();
  refreshSourceButtons();   // 起動時の既定割当（白=テスト / 黒=グラデ）を可視化
  refreshGallery();         // PC 内 captures/ の一覧を読み込む
  loadPrompts();            // PC 内 prompts.json を読み込む
  setStatus('準備OK: 白=テストパターン / 黒=グラデ を左右合成中');

  // 検証用: rAF が止まる環境（headless preview 等）で 1 フレーム手動描画する
  window.__wc = {
    step() {
      const dt = 0.5;
      params.time += dt;
      liveSrc.update(dt); preSrc.update(dt);
      if (liveSrc.ready) liveTex.v.upload(liveSrc.element, true);
      if (preSrc.ready) preTex.v.upload(preSrc.element, true);
      if (mask.dirty) { maskTex.v.upload(mask.element, true); mask.dirty = false; }
      const w = els.canvas.width, h = els.canvas.height;
      params.liveScale = fitScale(liveSrc.element, w, h);
      params.preScale = fitScale(preSrc.element, w, h);
      const screen = { fbo: null, w, h };
      pipeline.render(liveTex.v, preTex.v, maskTex.v, params, screen);
      return gl.getError();
    },
    readPixel(x, y) {
      const buf = new Uint8Array(4);
      gl.readPixels(x, y, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, buf);
      return Array.from(buf);
    },
  };

  requestAnimationFrame(loop);
}

window.addEventListener('DOMContentLoaded', init);
