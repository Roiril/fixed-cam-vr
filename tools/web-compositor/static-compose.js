// 静止画 2 枚合成ページ。
//   ベース画像(黒側) + overlay 画像(白側) を、廻リ視の合成パイプライン
//   （色統計マッチング → ラプラシアンピラミッド → ポストFX）で合成する。
//   マスク・境界ブレンド・ポストFX を GUI で調整し、結果を保存できる。
//   pipeline.js / gl.js / shaders.js は live カメラ版と共有。
import { createContext, SourceTexture } from './gl.js';
import { Pipeline } from './pipeline.js';

const MAX_DIM = 1920; // 作業解像度の上限（性能）
const $ = (id) => document.getElementById(id);

const canvas = $('view');
let ctx;
try { ctx = createContext(canvas); }
catch (e) {
  document.querySelector('.canvas-holder').textContent = 'WebGL2/float RT 非対応: ' + e.message;
  throw e;
}
const gl = ctx.gl;
const pipe = new Pipeline(gl);
const texOver = new SourceTexture(gl); // uA（白側・色補正される）
const texBase = new SourceTexture(gl); // uB（黒側・色の基準）
const texMask = new SourceTexture(gl);

// マスクは 2D canvas に描く（R チャンネルを使用）
const maskCanvas = document.createElement('canvas');
const mctx = maskCanvas.getContext('2d', { willReadFrequently: true });

const baseImg = new Image();
const overImg = new Image();
let W = 0, H = 0;

// --- パラメータ（UI と同期）--------------------------------------------------
const P = {
  stretch: false,
  feather: 0.15, showMask: false,
  colorMatch: true, colorStrength: 0.8, laplacian: true, levels: 6,
  exposure: 0, contrast: 1, saturation: 1, temperature: 0,
  vignette: 0.25, grain: 0.06, scanline: 0, aberration: 0,
};
const FX_DEFAULT = { exposure: 0, contrast: 1, saturation: 1, temperature: 0, vignette: 0.25, grain: 0.06, scanline: 0, aberration: 0 };

// アスペクト維持フィット用スケール（app.js と同じ contain）
function containScale(w, h, fa) {
  if (!w || !h) return [1, 1];
  const a = w / h;
  return a > fa ? [1, fa / a] : [a / fa, 1];
}

// --- マスク操作 --------------------------------------------------------------
function fillMask(mode) {
  if (!W) return;
  mctx.save();
  if (mode === 'none') { mctx.fillStyle = '#000'; mctx.fillRect(0, 0, W, H); }
  else if (mode === 'all') { mctx.fillStyle = '#fff'; mctx.fillRect(0, 0, W, H); }
  else if (mode === 'invert') {
    const im = mctx.getImageData(0, 0, W, H);
    const d = im.data;
    for (let i = 0; i < d.length; i += 4) { d[i] = d[i + 1] = d[i + 2] = 255 - d[i]; }
    mctx.putImageData(im, 0, 0);
  } else {
    const x = Math.round((P.splitX ?? 0.5) * W);
    mctx.fillStyle = '#000'; mctx.fillRect(0, 0, W, H);
    mctx.fillStyle = '#fff';
    if (mode === 'right') mctx.fillRect(x, 0, W - x, H);
    else if (mode === 'left') mctx.fillRect(0, 0, x, H);
  }
  mctx.restore();
}

// 現在の白い側設定で縦割りマスクを再生成（境界ドラッグモード用）
function applySplit() { fillMask(P.whiteSide || 'right'); }

// ガイド線を splitX に合わせて配置
function updateGuide() {
  const g = document.getElementById('guide');
  if (g) g.style.left = ((P.splitX ?? 0.5) * 100).toFixed(2) + '%';
}

let painting = false, paintWhite = true;
function paintAt(ev) {
  const r = canvas.getBoundingClientRect();
  const x = (ev.clientX - r.left) / r.width * W;
  const y = (ev.clientY - r.top) / r.height * H;
  const rad = (+$('brush').value) / 2;
  mctx.beginPath();
  mctx.arc(x, y, rad, 0, Math.PI * 2);
  mctx.fillStyle = paintWhite ? '#fff' : '#000';
  mctx.fill();
}
// 境界ドラッグ: マウス X を splitX にして縦割りマスクを再生成
function dragBoundary(ev) {
  const r = canvas.getBoundingClientRect();
  P.splitX = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width));
  const s = $('splitX'); if (s) s.value = P.splitX; $('splitXv').textContent = P.splitX.toFixed(2);
  updateGuide();
  applySplit();
}
canvas.addEventListener('pointerdown', (e) => {
  if (!W) return;
  painting = true;
  canvas.setPointerCapture(e.pointerId);
  if (P.boundaryMode) { dragBoundary(e); return; }
  paintWhite = !(e.button === 2 || e.shiftKey);
  paintAt(e);
});
canvas.addEventListener('pointermove', (e) => {
  if (!painting) return;
  if (P.boundaryMode) dragBoundary(e); else paintAt(e);
});
canvas.addEventListener('pointerup', () => { painting = false; });
canvas.addEventListener('contextmenu', (e) => e.preventDefault());

// --- 画像読み込み ------------------------------------------------------------
function fitWorkingSize(w, h) {
  const s = Math.min(1, MAX_DIM / Math.max(w, h));
  W = Math.max(2, Math.round(w * s));
  H = Math.max(2, Math.round(h * s));
  canvas.width = W; canvas.height = H;
  maskCanvas.width = W; maskCanvas.height = H;
}
function onBaseLoaded() {
  fitWorkingSize(baseImg.naturalWidth, baseImg.naturalHeight);
  applySplit(); // 既定: 現在の白い側で縦割り
  updateGuide();
}
baseImg.addEventListener('load', onBaseLoaded);

function loadFile(input, img) {
  const f = input.files && input.files[0];
  if (!f) return;
  const url = URL.createObjectURL(f);
  img.onloadCleanup && img.onloadCleanup();
  img.onloadCleanup = () => URL.revokeObjectURL(url);
  img.src = url;
}
$('fBase').addEventListener('change', () => loadFile($('fBase'), baseImg));
$('fOver').addEventListener('change', () => loadFile($('fOver'), overImg));

// 既定でツール同梱の入力をロード
baseImg.src = './static-inputs/base.png';
overImg.src = './static-inputs/overlay.jpeg';

// --- UI 配線 -----------------------------------------------------------------
function bindRange(id, key, fmt = (v) => v.toFixed(2)) {
  const el = $(id), out = $(id + 'v');
  const apply = () => { P[key] = parseFloat(el.value); if (out) out.textContent = fmt(P[key]); };
  el.addEventListener('input', apply); apply();
}
bindRange('feather', 'feather');
bindRange('colorStrength', 'colorStrength');
bindRange('levels', 'levels', (v) => String(v | 0));
bindRange('exposure', 'exposure');
bindRange('contrast', 'contrast');
bindRange('saturation', 'saturation');
bindRange('temperature', 'temperature');
bindRange('vignette', 'vignette');
bindRange('grain', 'grain', (v) => v.toFixed(3));
bindRange('scanline', 'scanline');
bindRange('aberration', 'aberration', (v) => v.toFixed(1));

P.splitX = 0.5;
$('splitX').addEventListener('input', (e) => {
  P.splitX = parseFloat(e.target.value); $('splitXv').textContent = P.splitX.toFixed(2);
  updateGuide(); if (P.boundaryMode) applySplit();
});
$('brush').addEventListener('input', (e) => { $('brushv').textContent = e.target.value; });
const bindChk = (id, key) => { const el = $(id); const a = () => P[key] = el.checked; el.addEventListener('change', a); a(); };
bindChk('stretch', 'stretch');
bindChk('showMask', 'showMask');
bindChk('colorMatch', 'colorMatch');
bindChk('laplacian', 'laplacian');

// 境界ドラッグモード切替
P.whiteSide = 'right';
const holder = document.querySelector('.canvas-holder');
function syncMode() {
  holder.classList.toggle('boundary', P.boundaryMode);
  $('modeHint').textContent = P.boundaryMode
    ? 'キャンバスを左右にドラッグ = 白黒の境目を移動（青い破線が境界）。白い所に overlay が出ます。'
    : 'キャンバスをドラッグ = マスクを塗る（左=白 / 右 or Shift=黒）。白い所に overlay が出ます。';
  updateGuide();
}
$('boundaryMode').addEventListener('change', (e) => { P.boundaryMode = e.target.checked; syncMode(); });
P.boundaryMode = $('boundaryMode').checked;
$('whiteSide').addEventListener('change', (e) => { P.whiteSide = e.target.value; applySplit(); });
syncMode();

document.querySelectorAll('[data-mask]').forEach((b) => b.addEventListener('click', () => fillMask(b.dataset.mask)));
$('resetFx').addEventListener('click', () => {
  Object.assign(P, FX_DEFAULT);
  for (const k of Object.keys(FX_DEFAULT)) { const el = $(k); if (el) { el.value = P[k]; el.dispatchEvent(new Event('input')); } }
});

// --- レンダリング ------------------------------------------------------------
const t0 = performance.now();
function render() {
  if (!W || !baseImg.naturalWidth) return;
  pipe.allocate(W, H, Math.max(2, Math.min(9, P.levels | 0)));
  const fa = W / H;
  texBase.upload(baseImg);
  if (overImg.naturalWidth) texOver.upload(overImg);
  texMask.upload(maskCanvas);
  const sc = (img) => P.stretch ? [1, 1] : containScale(img.naturalWidth, img.naturalHeight, fa);
  // pipe.render(uA=liveSrc, uB=preSrc, mask): 白→uA / 黒→uB。
  //   白側=overlay を uA、黒側=base を uB。色統計は uA を uB の色味へ寄せる。
  pipe.render(texOver, texBase, texMask, {
    liveScale: overImg.naturalWidth ? sc(overImg) : [1, 1],
    preScale: sc(baseImg),
    feather: P.feather,
    colorMatch: P.colorMatch, colorStrength: P.colorStrength,
    laplacian: P.laplacian,
    maskStrength: 1,
    exposure: P.exposure, contrast: P.contrast, saturation: P.saturation,
    temperature: P.temperature, vignette: P.vignette, grain: P.grain,
    aberration: P.aberration, scanline: P.scanline, showMask: P.showMask ? 1 : 0,
    time: (performance.now() - t0) / 1000,
  }, { fbo: null, w: W, h: H });
}
setInterval(render, 40); // 非表示タブでも動くよう setInterval（RAF は停止する）

// --- 保存 --------------------------------------------------------------------
const msg = (t) => { $('msg').textContent = t; };
function grab(type, q) {
  render(); // 直前フレームを確定（preserveDrawingBuffer なので読める）
  return new Promise((r) => canvas.toBlob(r, type, q));
}
$('savePng').addEventListener('click', async () => {
  const blob = await grab('image/png');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `compose_${new Date().toISOString().replace(/[:.]/g, '-')}.png`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  msg('PNG をダウンロードしました。');
});
$('saveServer').addEventListener('click', async () => {
  const blob = await grab('image/jpeg', 0.95);
  try {
    const res = await fetch('/save?type=image&to=recordings&cam=compose', { method: 'POST', body: blob });
    const j = await res.json();
    msg(j.ok ? `recordings に保存: ${j.name}（${(j.size / 1024 | 0)}KB）` : `保存失敗: ${j.error}`);
  } catch (e) { msg('保存失敗（サーバ未起動? serve.ps1 経由で起動してください）: ' + e.message); }
});
$('openDir').addEventListener('click', async () => {
  try { await fetch('/open-dir?dir=recordings'); } catch (e) { msg('フォルダを開けません: ' + e.message); }
});
