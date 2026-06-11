// 🖥 仮想 Unity — ShowControlClient のブラウザ実装（Quest の代役）。
// Unity と同じ手順で show.json を long-poll し、ScreenComposite シェーダ相当の合成
// （contain-fit letterbox → mask × overlay → post FX）を canvas 2D で再現する。
// heartbeat も送るので、コンソール側からは本物の Unity と同じに見える
// （simulator: true フラグ付き）。UI / プロトコルの動作確認が Unity 不要になる。

const canvas = document.getElementById('simCanvas');
const ctx = canvas.getContext('2d');
const W = canvas.width, H = canvas.height;

let state = null;
let rev = -1;

// ライブソース: アクティブカメラの host があれば MJPEG <img>、無ければテストパターン
let liveImg = null;
let liveHost = '';

// オーバーレイ: cue 適用結果
let overlay = { el: null, mask: null, strength: 0, target: 0, fadeSpeed: 4, cueId: '' };

let activeIndex = 0;
let cameraOverride = '';

// ---- ソース管理 -------------------------------------------------------------
function setLiveHost(host) {
  if (host === liveHost) return;
  liveHost = host;
  liveImg = null;
  if (host) {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.src = `http://${host}:8080/video`;
    liveImg = img;
  }
}

function loadMask(url) {
  return new Promise((res) => {
    if (!url) return res(null);
    const img = new Image();
    img.onload = () => res(img);
    img.onerror = () => res(null);
    img.src = url;
  });
}

async function applyCue(cue) {
  overlay.cueId = cue ? cue.id : '';
  if (!cue) {
    // フェードアウト
    overlay.target = 0;
    overlay.fadeSpeed = overlay.strength / Math.max(overlay.fadeOutSeconds || 0.5, 1e-3);
    return;
  }
  const mask = await loadMask(cue.maskUrl || '');
  let el = null;
  const url = cue.sourceUrl || '';
  if (/\.(mp4|webm|mov)(\?|$)/i.test(url)) {
    el = document.createElement('video');
    el.muted = true; el.loop = cue.loop !== false; el.playsInline = true;
    el.src = url;
    try { await el.play(); } catch (e) { /* autoplay block 時は黒のまま */ }
  } else if (url) {
    el = new Image();
    el.src = url;
    await new Promise((res) => { el.onload = res; el.onerror = res; });
  }
  overlay.el = el;
  overlay.mask = mask;
  overlay.fadeOutSeconds = cue.fadeOut ?? 0.5;
  overlay.target = cue.strength ?? 1;
  overlay.fadeSpeed = Math.max(overlay.target - overlay.strength, 0.01) / Math.max(cue.fadeIn ?? 0.5, 1e-3);
}

// ---- state 適用（ShowControlClient.Apply と同じ責務）------------------------
function applyState(s) {
  state = s;
  const ctrl = s.control || {};
  // カメラ
  cameraOverride = ctrl.cameraOverride || '';
  if (cameraOverride) {
    const i = (s.cameras || []).findIndex((c) => c.id === cameraOverride);
    if (i >= 0) activeIndex = i;
  }
  const cam = (s.cameras || [])[activeIndex];
  setLiveHost(cam?.host || '');
  // cue
  const cueId = ctrl.activeCue || '';
  if (cueId !== overlay.cueId) {
    const cue = (s.cues || []).find((c) => c.id === cueId) || null;
    applyCue(cue);
  }
}

async function pollLoop() {
  for (;;) {
    try {
      const r = await fetch(`/state?rev=${rev}`);
      const s = await r.json();
      if (s.rev !== rev) {
        rev = s.rev;
        applyState(s);
        document.getElementById('simState').textContent =
          `state: rev=${rev} / camera=${(s.cameras || [])[activeIndex]?.id || '?'}${cameraOverride ? ' (override)' : ''}`;
      }
    } catch (e) {
      await new Promise((res) => setTimeout(res, 2000));
    }
  }
}

// ---- heartbeat ---------------------------------------------------------------
async function heartbeatLoop() {
  for (;;) {
    try {
      const cam = (state?.cameras || [])[activeIndex];
      await fetch('/unity/heartbeat', {
        method: 'POST',
        body: JSON.stringify({
          simulator: true,
          activeCamera: cam?.sourceId || '',
          activeIndex,
          recvFps: liveImg && liveImg.naturalWidth > 0 ? 30 : 0,
          playingCue: overlay.cueId,
          cameraOverride,
          appliedRev: rev,
        }),
      });
    } catch (e) { /* ベストエフォート */ }
    await new Promise((res) => setTimeout(res, 2000));
  }
}

// ---- 描画（ScreenComposite 相当の簡易版）-------------------------------------
function drawContain(el, alpha = 1) {
  const sw = el.videoWidth || el.naturalWidth || 0;
  const sh = el.videoHeight || el.naturalHeight || 0;
  if (!sw || !sh) return;
  const sA = sw / sh, dA = W / H;
  let dw, dh;
  if (sA >= dA) { dw = W; dh = W / sA; } else { dh = H; dw = H * sA; }
  ctx.globalAlpha = alpha;
  ctx.drawImage(el, (W - dw) / 2, (H - dh) / 2, dw, dh);
  ctx.globalAlpha = 1;
}

function drawTestPattern(t) {
  ctx.fillStyle = '#101820';
  ctx.fillRect(0, 0, W, H);
  ctx.strokeStyle = '#3399ff';
  ctx.lineWidth = 4;
  ctx.strokeRect(40, 40, W - 80, H - 80);
  ctx.fillStyle = '#ffe14d';
  const x = W / 2 + Math.sin(t / 800) * 120;
  ctx.beginPath(); ctx.arc(x, H / 2, 24, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = '#8a96a3';
  ctx.font = '24px sans-serif';
  ctx.fillText('(仮想 Unity: ライブ未接続 — テストパターン)', 60, H - 64);
}

// マスク合成用オフスクリーン
const ovCanvas = document.createElement('canvas');
ovCanvas.width = W; ovCanvas.height = H;
const ovCtx = ovCanvas.getContext('2d');

let lastT = performance.now();
function frame(t) {
  const dt = (t - lastT) / 1000;
  lastT = t;

  // フェード進行（ScreenOverlayController.Update 相当）
  if (overlay.strength !== overlay.target) {
    const dir = Math.sign(overlay.target - overlay.strength);
    overlay.strength += dir * overlay.fadeSpeed * dt;
    if (dir > 0) overlay.strength = Math.min(overlay.strength, overlay.target);
    else overlay.strength = Math.max(overlay.strength, overlay.target);
  }

  // 1) live（contain-fit、枠外黒）
  ctx.fillStyle = '#000';
  ctx.fillRect(0, 0, W, H);
  if (liveImg && liveImg.naturalWidth > 0) drawContain(liveImg);
  else drawTestPattern(t);

  // 2) overlay × mask
  if (overlay.el && overlay.strength > 0.001) {
    ovCtx.clearRect(0, 0, W, H);
    ovCtx.save();
    const el = overlay.el;
    const sw = el.videoWidth || el.naturalWidth || 0;
    const sh = el.videoHeight || el.naturalHeight || 0;
    if (sw && sh) {
      const sA = sw / sh, dA = W / H;
      let dw, dh;
      if (sA >= dA) { dw = W; dh = W / sA; } else { dh = H; dw = H * sA; }
      ovCtx.drawImage(el, (W - dw) / 2, (H - dh) / 2, dw, dh);
      if (overlay.mask) {
        // マスクの R をアルファとして適用（スクリーン枠空間に引き伸ばし）
        ovCtx.globalCompositeOperation = 'destination-in';
        ovCtx.drawImage(overlay.mask, 0, 0, W, H);
      }
      ctx.globalAlpha = overlay.strength;
      ctx.drawImage(ovCanvas, 0, 0);
      ctx.globalAlpha = 1;
    }
    ovCtx.restore();
  }

  // 3) post FX（近似: filter は重いので vignette / scanline のみ手描き + CSS filter）
  const post = state?.post || {};
  canvas.style.filter =
    `brightness(${Math.pow(2, post.exposure || 0)}) contrast(${post.contrast ?? 1}) saturate(${post.saturation ?? 1})`;
  if ((post.vignette || 0) > 0.01) {
    const g = ctx.createRadialGradient(W / 2, H / 2, H * 0.35, W / 2, H / 2, H * 0.85);
    g.addColorStop(0, 'rgba(0,0,0,0)');
    g.addColorStop(1, `rgba(0,0,0,${post.vignette})`);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, W, H);
  }
  if ((post.scanline || 0) > 0.01) {
    ctx.fillStyle = `rgba(0,0,0,${post.scanline * 0.35})`;
    for (let y = 0; y < H; y += 6) ctx.fillRect(0, y, W, 2);
  }

  document.getElementById('simCue').textContent =
    overlay.cueId ? `🎬 ${overlay.cueId} (strength=${overlay.strength.toFixed(2)})` : '';
}

pollLoop();
heartbeatLoop();
// rAF はタブ非表示で止まる（ヘッドレス検証や裏タブ運用で sim が凍る）ため壁時計駆動。
setInterval(() => frame(performance.now()), 33);
