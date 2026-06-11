// 映像ソース管理: 内蔵テストパターン / グラデ / Webcam / 動画ファイル / MJPEG・動画URL。
// 各ソースは getElement() で texImage2D 可能な要素(canvas/video/img)を返し、ready で判定。

export class TestPattern {
  // 動くテストパターン（カメラ無しでも合成が見える）。live 既定。
  constructor(w = 640, h = 360) {
    this.canvas = document.createElement('canvas');
    this.canvas.width = w; this.canvas.height = h;
    this.ctx = this.canvas.getContext('2d');
    this.t = 0;
  }
  update(dt) {
    this.t += dt;
    const c = this.ctx, w = this.canvas.width, h = this.canvas.height;
    c.fillStyle = '#202830'; c.fillRect(0, 0, w, h);
    // 動くカラーバー
    const bars = 8;
    const cols = ['#d04545', '#d0a045', '#c8d045', '#45d060', '#45c8d0', '#4560d0', '#9045d0', '#d045b0'];
    const shift = (this.t * 60) % (w / bars);
    for (let i = -1; i < bars + 1; i++) {
      c.fillStyle = cols[(i + bars) % bars];
      c.fillRect(i * (w / bars) + shift, h * 0.0, w / bars + 1, h * 0.55);
    }
    // 動く円（追跡対象っぽいモチーフ）
    const cx = w * (0.5 + 0.35 * Math.sin(this.t * 0.9));
    const cy = h * 0.72;
    c.fillStyle = '#f0f0f0';
    c.beginPath(); c.arc(cx, cy, h * 0.12, 0, Math.PI * 2); c.fill();
    c.fillStyle = '#101010';
    c.beginPath(); c.arc(cx - 8, cy - 6, 4, 0, Math.PI * 2); c.fill();
    c.beginPath(); c.arc(cx + 8, cy - 6, 4, 0, Math.PI * 2); c.fill();
    // タイムスタンプ
    c.fillStyle = '#ffdead'; c.font = '20px monospace';
    c.fillText('LIVE ' + this.t.toFixed(1) + 's', 12, h - 16);
  }
  get element() { return this.canvas; }
  get ready() { return true; }
  get isStream() { return false; }
  dispose() {}
}

export class GradientSource {
  // 静的グラデ（pre 既定）。live と色味を変えて色統計マッチングの効果を見せる。
  constructor(w = 640, h = 360) {
    this.canvas = document.createElement('canvas');
    this.canvas.width = w; this.canvas.height = h;
    const c = this.canvas.getContext('2d');
    const g = c.createLinearGradient(0, 0, w, h);
    g.addColorStop(0, '#1a1230'); g.addColorStop(0.5, '#402038'); g.addColorStop(1, '#704830');
    c.fillStyle = g; c.fillRect(0, 0, w, h);
    // それっぽい暗めの背景に格子
    c.strokeStyle = 'rgba(255,200,160,0.10)'; c.lineWidth = 1;
    for (let x = 0; x < w; x += 32) { c.beginPath(); c.moveTo(x, 0); c.lineTo(x, h); c.stroke(); }
    for (let y = 0; y < h; y += 32) { c.beginPath(); c.moveTo(0, y); c.lineTo(w, y); c.stroke(); }
    c.fillStyle = '#c0a080'; c.font = '20px monospace';
    c.fillText('PRE (recorded bg)', 12, h - 16);
  }
  update() {}
  get element() { return this.canvas; }
  get ready() { return true; }
  get isStream() { return false; }
  dispose() {}
}

export class WebcamSource {
  constructor() {
    this.video = document.createElement('video');
    this.video.autoplay = true; this.video.muted = true; this.video.playsInline = true;
    this.stream = null; this.error = null;
  }
  async start() {
    this.stream = await navigator.mediaDevices.getUserMedia({ video: { width: 1280, height: 720 }, audio: false });
    this.video.srcObject = this.stream;
    await this.video.play();
  }
  update() {}
  get element() { return this.video; }
  get ready() { return this.video.readyState >= 2 && this.video.videoWidth > 0; }
  get isStream() { return true; }
  dispose() { if (this.stream) this.stream.getTracks().forEach(t => t.stop()); }
}

export class VideoFileSource {
  constructor(url, { loop = true } = {}) {
    this.video = document.createElement('video');
    this.video.src = url; this.video.loop = loop; this.video.muted = true;
    this.video.playsInline = true; this.video.crossOrigin = 'anonymous';
    this.video.play().catch(() => {});
  }
  update() {}
  get element() { return this.video; }
  get ready() { return this.video.readyState >= 2 && this.video.videoWidth > 0; }
  get isStream() { return true; }
  dispose() { this.video.pause(); this.video.removeAttribute('src'); this.video.load(); }
}

export class ImageStreamSource {
  // MJPEG (multipart/x-mixed-replace) または静止画 URL。<img> はブラウザが自動更新する。
  constructor(url) {
    this.img = new Image();
    this.img.crossOrigin = 'anonymous';
    this.img.src = url;
  }
  update() {}
  get element() { return this.img; }
  get ready() { return this.img.complete && this.img.naturalWidth > 0; }
  get isStream() { return false; }
  dispose() { this.img.src = ''; }
}

// マスク: 2D canvas に白(=live表示)/黒(=pre表示)を塗る。
// モード: 'split'（境界線をドラッグで移動） / 'paint'（ブラシ手描き）。
export class MaskCanvas {
  constructor(w, h) {
    this.canvas = document.createElement('canvas');
    this.canvas.width = w; this.canvas.height = h;
    this.ctx = this.canvas.getContext('2d');
    this.dirty = true;
    this.brush = 64;
    this.erasing = false;
    // スプリットモード状態
    this.mode = 'split';
    this.splitAxis = 'x';        // 'x'=縦境界(左右) / 'y'=横境界(上下)
    this.splitPos = 0.5;         // 0..1 の境界位置
    this.splitFirstWhite = true; // 先頭側(x<pos or y<pos)を白にするか
    this.renderSplit();
  }
  resize(w, h) {
    if (this.canvas.width === w && this.canvas.height === h) return;
    const prev = document.createElement('canvas');
    prev.width = this.canvas.width; prev.height = this.canvas.height;
    prev.getContext('2d').drawImage(this.canvas, 0, 0);
    this.canvas.width = w; this.canvas.height = h;
    if (this.mode === 'split') { this.renderSplit(); return; }
    this.ctx.drawImage(prev, 0, 0, w, h);
    this.dirty = true;
  }
  preset(name) {
    // 分割系はスプリットモード（境界ドラッグ可）、それ以外はペイントモード
    switch (name) {
      case 'split-lr': this.setSplit('x', true); return;
      case 'split-rl': this.setSplit('x', false); return;
      case 'split-tb': this.setSplit('y', true); return;
      case 'split-bt': this.setSplit('y', false); return;
    }
    this.mode = 'paint';
    const c = this.ctx, w = this.canvas.width, h = this.canvas.height;
    c.fillStyle = '#000'; c.fillRect(0, 0, w, h);
    c.fillStyle = '#fff';
    switch (name) {
      case 'full-live': c.fillRect(0, 0, w, h); break;
      case 'full-pre': break;
      case 'rect': c.fillRect(w * 0.3, h * 0.25, w * 0.4, h * 0.5); break;
      case 'circle':
        c.beginPath(); c.arc(w / 2, h / 2, Math.min(w, h) * 0.3, 0, Math.PI * 2); c.fill(); break;
    }
    this.dirty = true;
  }

  // スプリットモードへ。pos は維持（プリセット切替で境界位置を失わない）。
  setSplit(axis, firstWhite) {
    this.mode = 'split';
    this.splitAxis = axis;
    this.splitFirstWhite = firstWhite;
    this.renderSplit();
  }

  renderSplit() {
    const c = this.ctx, w = this.canvas.width, h = this.canvas.height;
    c.fillStyle = '#000'; c.fillRect(0, 0, w, h);
    c.fillStyle = '#fff';
    if (this.splitAxis === 'x') {
      const x = this.splitPos * w;
      if (this.splitFirstWhite) c.fillRect(0, 0, x, h);
      else c.fillRect(x, 0, w - x, h);
    } else {
      const y = this.splitPos * h;
      if (this.splitFirstWhite) c.fillRect(0, 0, w, y);
      else c.fillRect(0, y, w, h - y);
    }
    this.dirty = true;
  }

  // 境界をドラッグ位置へ移動（スプリットモード）。x,y は canvas ピクセル座標。
  moveSplit(x, y) {
    const w = this.canvas.width, h = this.canvas.height;
    this.splitPos = this.splitAxis === 'x'
      ? Math.min(1, Math.max(0, x / w))
      : Math.min(1, Math.max(0, y / h));
    this.renderSplit();
  }
  invert() {
    if (this.mode === 'split') { this.splitFirstWhite = !this.splitFirstWhite; this.renderSplit(); return; }
    const c = this.ctx, w = this.canvas.width, h = this.canvas.height;
    c.globalCompositeOperation = 'difference';
    c.fillStyle = '#fff'; c.fillRect(0, 0, w, h);
    c.globalCompositeOperation = 'source-over';
    this.dirty = true;
  }
  paint(x, y) {
    const c = this.ctx;
    const r = this.brush / 2;
    const g = c.createRadialGradient(x, y, 0, x, y, r);
    const col = this.erasing ? '0,0,0' : '255,255,255';
    g.addColorStop(0, `rgba(${col},1)`);
    g.addColorStop(1, `rgba(${col},0)`);
    c.fillStyle = g;
    c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
    this.dirty = true;
  }
  get element() { return this.canvas; }
}
