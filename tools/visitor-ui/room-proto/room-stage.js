// 博士の部屋を 1 枚の canvas に描く舞台（試作）。
// 博士・部屋・テレビの中身を同じ画に描き、カメラの寄り・引き・ピント送りは canvas の中でやる。
// （動画を含む層を CSS の transform / filter で動かすと Android WebView は黒に飛ぶ。memory/visitor_tablet.md）
//
//   const stage = await RoomStage.create(canvas, { room, base })
//   stage.setFace(imgOrVideo)          博士の顔の元（静止画か、口の動画）
//   stage.render({ now, cam, focus, screen, cut, fadeIn })
//       now     ms。粒や揺れの時計
//       cam     { cx, cy, w }  部屋の絵の座標で、見えている範囲の中心と幅
//       focus   0 = 博士にピント … 1 = テレビにピント
//       screen  { power, powerAt, kind, src, tag, hold, shownAt, channelAt }  テレビの中身
//               src2 / swapAt  その時刻から先は src2 を映す（同じ写真の別版）
//       cut     0–1  終わりの砂嵐（画面全体）
//       exposure 0–1  部屋の灯り（始まりに点く）
(() => {
  'use strict';

  const SW = 1016;           // テレビの中身を描く解像度（.tv-screen と同じ 1.27:1）。寄ったときに甘くならない大きさ
  const SH = 800;
  const K = SW / 762;        // 762x600 で決めた寸法を掛け直す係数
  const POWER_MS = 620;
  const CHANNEL_MS = 260;
  const DOC_SCALE = 1.6;     // 博士の層は部屋の絵の 1.6 倍で持つ（寄ったときに甘くならないように）
  const PARALLAX = 0.05;     // カメラが動いたとき、手前の博士が奥の幕に対してずれる割合

  const canFilter = (() => {
    const c = document.createElement('canvas').getContext('2d');
    return typeof c.filter === 'string';
  })();

  function makeCanvas(w, h) {
    const c = document.createElement('canvas');
    c.width = Math.max(1, Math.round(w));
    c.height = Math.max(1, Math.round(h));
    return c;
  }

  function loadImage(src) {
    return new Promise((resolve, reject) => {
      const img = new Image();
      img.onload = () => resolve(img);
      img.onerror = () => reject(new Error(`読めない: ${src}`));
      img.src = src;
    });
  }

  // src を縮めてぼかした板を作る（scale 分の 1 の大きさ・その中で px ぼかす）
  function blurInto(target, src, px) {
    const g = target.getContext('2d');
    g.clearRect(0, 0, target.width, target.height);
    if (canFilter) g.filter = `blur(${px}px)`;
    g.drawImage(src, 0, 0, target.width, target.height);
    g.filter = 'none';
  }

  // ぼけ f（0–1）で、くっきり → 弱いぼけ → 強いぼけ を重ねて描く
  function drawFocus(g, sharp, mild, strong, f, x, y, w, h) {
    const a = g.globalAlpha;
    if (f <= 0.5) {
      g.drawImage(sharp, x, y, w, h);
      if (f > 0.01) { g.globalAlpha = a * Math.min(1, f * 2); g.drawImage(mild, x, y, w, h); }
    } else {
      g.drawImage(mild, x, y, w, h);
      g.globalAlpha = a * Math.min(1, (f - 0.5) * 2);
      g.drawImage(strong, x, y, w, h);
    }
    g.globalAlpha = a;
  }

  function noiseTiles(n, w, h, alpha) {
    const tiles = [];
    for (let i = 0; i < n; i++) {
      const c = makeCanvas(w, h);
      const g = c.getContext('2d');
      const img = g.createImageData(w, h);
      const d = img.data;
      for (let p = 0; p < d.length; p += 4) {
        const v = (Math.random() * 255) | 0;
        d[p] = v; d[p + 1] = v; d[p + 2] = v; d[p + 3] = alpha;
      }
      g.putImageData(img, 0, 0);
      tiles.push(c);
    }
    return tiles;
  }

  async function create(canvas, { room, base }) {
    const ctx = canvas.getContext('2d', { alpha: false });
    const PW = room.plate.w;
    const PH = room.plate.h;
    const [bg, bodyImg, lightImg, glassImg, lightDocImg] = await Promise.all([
      loadImage(`${base}bg.jpg`), loadImage(`${base}body.png`), loadImage(`${base}light.jpg`), loadImage(`${base}glass.png`), loadImage(`${base}light-doc.jpg`),
    ]);

    // テレビのまわりだけの精細な絵（寄ったときに使う。無ければ部屋の絵のまま）
    let tvHi = null;
    if (room.tvHi) {
      const img = await loadImage(`${base}tv-hi.jpg`);
      const T = room.tvHi;
      tvHi = makeCanvas(img.naturalWidth, img.naturalHeight);
      const g = tvHi.getContext('2d');
      g.drawImage(img, 0, 0);
      // 縁を落として部屋の絵へ溶かす（右と下は絵の端なので落とさない側もある）
      g.globalCompositeOperation = 'destination-out';
      const m = tvHi.width * 0.06;
      const edge = (x0, y0, x1, y1, rx, ry, rw, rh) => {
        const gr = g.createLinearGradient(x0, y0, x1, y1);
        gr.addColorStop(0, 'rgba(0,0,0,1)');
        gr.addColorStop(1, 'rgba(0,0,0,0)');
        g.fillStyle = gr;
        g.fillRect(rx, ry, rw, rh);
      };
      edge(0, 0, m, 0, 0, 0, m, tvHi.height);
      edge(0, 0, 0, m, 0, 0, tvHi.width, m);
      if (T.x + T.w < PW - 1) edge(tvHi.width, 0, tvHi.width - m, 0, tvHi.width - m, 0, m, tvHi.height);
      if (T.y + T.h < PH - 1) edge(0, tvHi.height, 0, tvHi.height - m, 0, tvHi.height - m, tvHi.width, m);
      g.globalCompositeOperation = 'source-over';
    }

    // 奥（幕とテレビ）のぼけた板
    const bgMild = makeCanvas(PW / 2, PH / 2);
    const bgStrong = makeCanvas(PW / 4, PH / 4);
    blurInto(bgMild, bg, 2.5);
    blurInto(bgStrong, bg, 4);

    // 博士の層
    const B = room.body;
    const F = room.face;
    const doc = makeCanvas(B.w * DOC_SCALE, B.h * DOC_SCALE);
    const docG = doc.getContext('2d');
    const docMild = makeCanvas(doc.width / 3, doc.height / 3);
    const docStrong = makeCanvas(doc.width / 8, doc.height / 8);
    const faceCv = makeCanvas(F.w * DOC_SCALE, F.h * DOC_SCALE);
    const faceG = faceCv.getContext('2d');
    // 顔の板の縁を落とす型（部屋の絵の胴へ溶かす）。上は髪があるので落とさない
    const feather = makeCanvas(faceCv.width, faceCv.height);
    {
      const g = feather.getContext('2d');
      const m = 46 * DOC_SCALE;
      g.fillStyle = '#fff';
      g.fillRect(0, 0, feather.width, feather.height);
      g.globalCompositeOperation = 'destination-out';
      const edge = (x0, y0, x1, y1, rx, ry, rw, rh) => {
        const gr = g.createLinearGradient(x0, y0, x1, y1);
        gr.addColorStop(0, 'rgba(0,0,0,1)');
        gr.addColorStop(1, 'rgba(0,0,0,0)');
        g.fillStyle = gr;
        g.fillRect(rx, ry, rw, rh);
      };
      edge(0, 0, m, 0, 0, 0, m, feather.height);
      edge(feather.width, 0, feather.width - m, 0, feather.width - m, 0, m, feather.height);
      edge(0, feather.height, 0, feather.height - m, 0, feather.height - m, feather.width, m);
    }
    let faceSrc = null;
    let faceIsVideo = false;
    let docDirty = true;
    const hash = (n) => { const x = Math.sin(n * 127.1 + 311.7) * 43758.5453; return x - Math.floor(x); };

    function composeDoctor() {
      docG.clearRect(0, 0, doc.width, doc.height);
      docG.drawImage(bodyImg, 0, 0, doc.width, doc.height);
      if (faceSrc) {
        const k = faceCv.width / room.faceSource.w;
        faceG.globalCompositeOperation = 'source-over';
        faceG.clearRect(0, 0, faceCv.width, faceCv.height);
        faceG.drawImage(faceSrc, 0, 0, faceCv.width, faceCv.height);
        // 博士の形で抜く（胴の層の透過をそのまま型にする）→ 縁を落とす
        faceG.globalCompositeOperation = 'destination-in';
        faceG.drawImage(bodyImg, (B.x - F.x) * DOC_SCALE, (B.y - F.y) * DOC_SCALE, B.w * DOC_SCALE, B.h * DOC_SCALE);
        faceG.drawImage(feather, 0, 0);
        faceG.globalCompositeOperation = 'source-over';
        docG.drawImage(faceCv, (F.x - B.x) * DOC_SCALE, (F.y - B.y) * DOC_SCALE);
      }
      blurInto(docMild, doc, 2.2);
      blurInto(docStrong, doc, 2.6);
      docDirty = false;
    }

    // テレビ
    const G = room.glass;
    const screenCv = makeCanvas(SW, SH);
    const sg = screenCv.getContext('2d');
    const tvSoft = makeCanvas(G.w / 3, G.h / 3);
    // 消えているガラス（映り込み）。点いた画の上から足す
    const reflect = makeCanvas(G.w * 2, G.h * 2);
    {
      const g = reflect.getContext('2d');
      if (tvHi) {
        const T = room.tvHi;
        const kx = tvHi.width / T.w;
        g.drawImage(tvHi, (G.x - T.x) * kx, (G.y - T.y) * kx, G.w * kx, G.h * kx, 0, 0, reflect.width, reflect.height);
      } else g.drawImage(bg, G.x, G.y, G.w, G.h, 0, 0, reflect.width, reflect.height);
    }
    // 蛍光体の縞（寄ったときだけ見える）
    const triad = makeCanvas(3, 1);
    {
      const g = triad.getContext('2d');
      ['#ffb4b4', '#b4ffb4', '#b4b4ff'].forEach((c, i) => { g.fillStyle = c; g.fillRect(i, 0, 1, 1); });
    }
    const triadPattern = sg.createPattern(triad, 'repeat');
    // 明るい画面から横へ伸びる光の筋（上下も左右もなだらかに消える板）
    const streakCv = makeCanvas(256, 32);
    {
      const g = streakCv.getContext('2d');
      const gh = g.createLinearGradient(0, 0, 256, 0);
      gh.addColorStop(0, 'rgba(255,255,255,0)');
      gh.addColorStop(0.5, 'rgba(255,255,255,1)');
      gh.addColorStop(1, 'rgba(255,255,255,0)');
      g.fillStyle = gh;
      g.fillRect(0, 0, 256, 32);
      g.globalCompositeOperation = 'destination-in';
      const gv = g.createLinearGradient(0, 0, 0, 32);
      gv.addColorStop(0, 'rgba(0,0,0,0)');
      gv.addColorStop(0.5, 'rgba(0,0,0,1)');
      gv.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = gv;
      g.fillRect(0, 0, 256, 32);
    }
    const tvBloom = makeCanvas(G.w / 6, G.h / 6);
    const lightDocTmp = makeCanvas(lightDocImg.width, lightDocImg.height);
    const lightDocG = lightDocTmp.getContext('2d');
    const probe = makeCanvas(8, 6);
    const probeG = probe.getContext('2d', { willReadFrequently: true });
    const lightTmp = makeCanvas(lightImg.width, lightImg.height);
    const lightG = lightTmp.getContext('2d');
    const staticTiles = noiseTiles(5, 254, 200, 255);
    const grainTiles = noiseTiles(4, 320, 200, 255);
    const cache = new Map();
    let frames = 0;
    let tearAt = 0;
    let tearUntil = 0;
    let tearY = 0;
    let light = { r: 1, g: 1, b: 1, level: 0 };
    let lastProbe = 0;

    function image(src) {
      if (!cache.has(src)) {
        const img = new Image();
        img.src = src;
        cache.set(src, img);
      }
      const img = cache.get(src);
      return img.complete && img.naturalWidth ? img : null;
    }

    function cover(g, src, sw, sh, zoom) {
      const s = Math.max(SW / sw, SH / sh) * zoom;
      g.drawImage(src, (SW - sw * s) / 2, (SH - sh * s) / 2, sw * s, sh * s);
    }

    function drawButtons(g, img, t, hold) {
      g.fillStyle = '#000';
      g.fillRect(0, 0, SW, SH);
      if (img) {
        // 正方形の図を左寄せで収め、上面へ 1.5 倍に寄せる（index.html の .is-figure-buttons と同じ）
        const ox = SW * 0.4605;
        const oy = SH * -0.01;
        g.save();
        g.translate(ox, oy);
        g.scale(1.5, 1.5);
        g.translate(-ox, -oy);
        g.drawImage(img, 0, 0, SH, SH);
        g.restore();
      }
      const press = (cx, cy, phase) => {
        const u = ((t / 6000 + phase) % 1 + 1) % 1;
        let a = 0;
        let s = 1.5;
        if (u < 0.04) { a = 0; s = 1.5; }
        else if (u < 0.08) { const k = (u - 0.04) / 0.04; a = k; s = 1.5 - 0.5 * k; }
        else if (u < 0.26) { a = 1; s = 1; }
        else if (u < 0.34) { const k = (u - 0.26) / 0.08; a = 1 - k; s = 1 + 1.1 * k; }
        if (a <= 0) return;
        const r = SW * 0.052 * s;
        g.globalAlpha = a;
        g.fillStyle = 'rgba(168,50,57,0.42)';
        g.strokeStyle = '#c23a42';
        g.lineWidth = 3 * K;
        g.beginPath();
        g.arc(cx, cy, r, 0, Math.PI * 2);
        g.fill();
        g.stroke();
        g.globalAlpha = 1;
      };
      press(SW * 0.353, SH * 0.509, 0);
      press(SW * 0.4545, SH * 0.368, 0.5);
      // 1 秒で満ちる輪
      const cx = SW * 0.81;
      const cy = SH * 0.42;
      const r = SW * 0.19 * (50 / 120);
      const u = (t % 3000) / 3000;
      const fill = Math.max(0, Math.min(1, (u - 0.16) / 0.36));
      const fade = u > 0.78 ? Math.max(0, 1 - (u - 0.78) / 0.12) : 1;
      g.lineWidth = SW * 0.19 * (5 / 120);
      g.strokeStyle = 'rgba(232,220,196,0.2)';
      g.beginPath(); g.arc(cx, cy, r, 0, Math.PI * 2); g.stroke();
      g.globalAlpha = fade;
      g.strokeStyle = '#c23a42';
      g.beginPath(); g.arc(cx, cy, r, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * fill); g.stroke();
      g.fillStyle = 'rgba(242,236,223,0.9)';
      g.beginPath(); g.arc(cx, cy, r * 0.18 * (0.7 + 0.3 * fill), 0, Math.PI * 2); g.fill();
      g.globalAlpha = 1;
      if (hold) {
        g.font = `500 ${Math.round(26 * K)}px "Noto Sans JP","Yu Gothic UI",sans-serif`;
        g.textAlign = 'center';
        g.fillStyle = 'rgba(242,236,223,0.86)';
        g.fillText(hold, cx, cy + r + 46 * K);
        g.textAlign = 'start';
      }
    }

    // zoom: テレビのガラスが画面に写っている大きさ ÷ SW。寄るほど走査線と蛍光体の縞が見える
    function drawScreen(t, s, zoom) {
      sg.globalAlpha = 1;
      sg.globalCompositeOperation = 'source-over';
      sg.fillStyle = '#050403';
      sg.fillRect(0, 0, SW, SH);
      const shown = Math.max(0, t - (s.shownAt || 0));
      const tile = staticTiles[frames % staticTiles.length];
      if (s.kind === 'image') {
        const swapped = s.src2 && s.swapAt != null && t >= s.swapAt;
        const img = image(swapped ? s.src2 : s.src) || image(s.src);
        if (s.src2) image(s.src2);          // 先に読んでおく
        if (img) cover(sg, img, img.naturalWidth, img.naturalHeight, 1 + 0.07 * Math.min(1, shown / 16000));
        // 入れ替わりの瞬間: 画が横に裂ける（0.16 秒）
        if (s.swapAt != null && t >= s.swapAt - 60 && t < s.swapAt + 100 && img) {
          for (let k = 0; k < 7; k++) {
            const y = (hash(frames * 13 + k) * SH) | 0;
            const h = (12 + hash(frames * 7 + k) * 46) * K;
            const dx = (hash(frames * 3 + k) - 0.5) * 150 * K;
            sg.drawImage(screenCv, 0, y, SW, h, dx, y, SW, h);
          }
          sg.imageSmoothingEnabled = false;
          sg.globalAlpha = 0.5;
          sg.drawImage(tile, 0, 0, 254, 40, 0, (hash(frames) * SH) | 0, SW, 26 * K);
          sg.globalAlpha = 1;
          sg.imageSmoothingEnabled = true;
        }
      } else if (s.kind === 'video') {
        const v = s.video;
        if (v && v.readyState >= 2 && v.videoWidth) cover(sg, v, v.videoWidth, v.videoHeight, 1);
        else { const img = image(s.src); if (img) cover(sg, img, img.naturalWidth, img.naturalHeight, 1); }
      } else if (s.kind === 'buttons') {
        drawButtons(sg, image(s.src), shown, s.hold);
      } else if (s.kind === 'snow') {
        sg.imageSmoothingEnabled = false;
        sg.drawImage(tile, 0, 0, SW, SH);
        sg.imageSmoothingEnabled = true;
        sg.fillStyle = 'rgba(8,10,12,0.45)';
        sg.fillRect(0, 0, SW, SH);
      }
      if (s.tag && s.kind === 'image') {
        sg.font = `500 ${Math.round(21 * K)}px "Noto Sans JP","Yu Gothic UI",sans-serif`;
        if ('letterSpacing' in sg) sg.letterSpacing = `${(4.6 * K).toFixed(1)}px`;
        const tw = sg.measureText(s.tag).width;
        const x = SW * 0.07;
        const y = SH * 0.09;
        sg.fillStyle = 'rgba(9,8,7,0.66)';
        sg.fillRect(x, y, tw + 38 * K, 40 * K);
        sg.fillStyle = '#a83239';
        sg.fillRect(x, y, 3 * K, 40 * K);
        sg.fillStyle = 'rgba(242,236,223,0.9)';
        sg.textBaseline = 'middle';
        sg.fillText(s.tag, x + 18 * K, y + 21 * K);
        sg.textBaseline = 'alphabetic';
        if ('letterSpacing' in sg) sg.letterSpacing = '0px';
      }
      // 粒・流れる帯・ときどきの横ずれ（tv-live.js と同じ）
      sg.imageSmoothingEnabled = false;
      sg.globalAlpha = 0.055;
      sg.drawImage(tile, 0, 0, SW, SH);
      const period = 6800;
      const bh = 210 * K;
      const by = ((t % period) / period) * (SH + bh) - bh;
      const band = sg.createLinearGradient(0, by, 0, by + bh);
      band.addColorStop(0, 'rgba(255,255,255,0)');
      band.addColorStop(0.5, 'rgba(255,255,255,0.05)');
      band.addColorStop(1, 'rgba(255,255,255,0)');
      sg.globalAlpha = 1;
      sg.fillStyle = band;
      sg.fillRect(0, by, SW, bh);
      if (t > tearAt || t < tearAt - 12000) {
        tearUntil = t + 70 + hash(frames) * 60;
        tearAt = t + 3800 + hash(frames + 0.3) * 5200;
        tearY = (hash(frames + 0.7) * (SH - 24)) | 0;
      }
      if (t < tearUntil) {
        sg.globalAlpha = 0.34;
        sg.drawImage(tile, 0, 0, 254, 3, ((hash(frames) * 30 - 15) * K) | 0, tearY, SW, 9 * K);
      }
      if (s.channelAt != null && t >= s.channelAt && t < s.channelAt + CHANNEL_MS) {
        const u = (t - s.channelAt) / CHANNEL_MS;
        sg.globalAlpha = 0.92 * (1 - u) * (1 - u);
        sg.drawImage(staticTiles[(frames * 2 + 1) % staticTiles.length], 0, 0, SW, SH);
        sg.globalAlpha = 0.5 * (1 - u);
        sg.fillStyle = '#000';
        for (let k = 0; k < 4; k++) sg.fillRect(0, ((k * 211 + frames * 67) % SH), SW, 24 * K);
      }
      sg.imageSmoothingEnabled = true;
      // 走査線（引いた画では細かすぎて縞が暴れるので、寄ったときだけ濃くする）
      sg.globalAlpha = 1;
      const lineA = 0.2 * Math.max(0, Math.min(1, (zoom - 0.3) / 0.45));
      if (lineA > 0.01) {
        sg.fillStyle = `rgba(0,0,0,${lineA.toFixed(3)})`;
        for (let y = 0; y < SH; y += 4) sg.fillRect(0, y, SW, 2);
      }
      // 蛍光体の縞（赤・緑・青の縦の並び）。かなり寄ったときだけ
      const triA = 0.55 * Math.max(0, Math.min(1, (zoom - 0.62) / 0.3));
      if (triA > 0.01 && triadPattern) {
        sg.globalCompositeOperation = 'multiply';
        sg.globalAlpha = triA;
        sg.fillStyle = triadPattern;
        sg.fillRect(0, 0, SW, SH);
        sg.globalCompositeOperation = 'lighter';       // 縞で落ちた明るさを少し戻す
        sg.globalAlpha = triA * 0.16;
        sg.drawImage(screenCv, 0, 0);
        sg.globalCompositeOperation = 'source-over';
        sg.globalAlpha = 1;
      }
      // 管の周辺の落ち込み
      const vg = sg.createRadialGradient(SW / 2, SH / 2, SH * 0.36, SW / 2, SH / 2, SW * 0.72);
      vg.addColorStop(0, 'rgba(0,0,0,0)');
      vg.addColorStop(1, 'rgba(0,0,0,0.6)');
      sg.fillStyle = vg;
      sg.fillRect(0, 0, SW, SH);
      // 電源の入り: 黒 → 横一線 → 上下へ開く → 白が引く
      if (s.powerAt != null && t < s.powerAt + POWER_MS) {
        const u = Math.max(0, (t - s.powerAt) / POWER_MS);
        const mid = SH / 2;
        sg.fillStyle = '#000';
        if (u < 0.22) {
          sg.fillRect(0, 0, SW, SH);
          const lw = SW * Math.min(1, u / 0.18);
          sg.fillStyle = 'rgba(235,240,255,0.95)';
          sg.fillRect((SW - lw) / 2, mid - 3 * K, lw, 6 * K);
        } else {
          const open = Math.pow((u - 0.22) / 0.5, 0.6);
          const half = Math.min(mid, mid * open);
          sg.fillRect(0, 0, SW, mid - half);
          sg.fillRect(0, mid + half, SW, mid - half);
          sg.globalAlpha = Math.max(0, 0.55 * (1 - (u - 0.22) / 0.78));
          sg.fillStyle = '#eef2ff';
          sg.fillRect(0, mid - half, SW, half * 2);
          sg.globalAlpha = 1;
        }
      }
      // ガラスの形で抜く
      sg.globalCompositeOperation = 'destination-in';
      sg.drawImage(glassImg, 0, 0, SW, SH);
      sg.globalCompositeOperation = 'source-over';
    }

    // テレビの中身の平均の色を読み、部屋へ足す光の色と強さにする
    function probeLight(t, on) {
      if (!on) { light.level += (0 - light.level) * 0.3; return; }
      if (Math.abs(t - lastProbe) < 30) return;
      lastProbe = t;
      probeG.drawImage(screenCv, 0, 0, 8, 6);
      const d = probeG.getImageData(0, 0, 8, 6).data;
      let r = 0; let g = 0; let b = 0;
      for (let p = 0; p < d.length; p += 4) { r += d[p]; g += d[p + 1]; b += d[p + 2]; }
      const n = d.length / 4;
      r /= n; g /= n; b /= n;
      const lum = (0.3 * r + 0.59 * g + 0.11 * b) / 255;
      const mx = Math.max(r, g, b, 1);
      // 色は白へ 4 割寄せる（暗い絵でも色が転びすぎないように）
      const tr = 0.4 + 0.6 * (r / mx);
      const tg = 0.4 + 0.6 * (g / mx);
      const tb = 0.4 + 0.6 * (b / mx);
      const target = Math.min(1.25, 0.1 + lum * 2.4);
      const k = 0.55;
      light.r += (tr - light.r) * k;
      light.g += (tg - light.g) * k;
      light.b += (tb - light.b) * k;
      light.level += (target - light.level) * k;
    }

    // 埃（テレビの光の中だけ見える）
    const motes = Array.from({ length: 42 }, () => ({
      x: G.x - 150 + Math.random() * (G.w + 300),
      y: G.y - 60 + Math.random() * (G.h + 200),
      r: 0.5 + Math.random() * 1.3,
      vx: (Math.random() - 0.5) * 2.2,
      vy: -0.6 - Math.random() * 1.6,
      ph: Math.random() * 6.28,
    }));

    function view(cam) {
      const aspect = canvas.height / canvas.width;
      let w = Math.min(cam.w, PW, PH / aspect);
      const h = w * aspect;
      const cx = Math.min(PW - w / 2, Math.max(w / 2, cam.cx));
      const cy = Math.min(PH - h / 2, Math.max(h / 2, cam.cy));
      return { x: cx - w / 2, y: cy - h / 2, w, h, cx, cy, s: canvas.width / w };
    }

    function render(st) {
      const t = st.now;
      frames = st.frame != null ? st.frame : frames + 1;
      const v = view(st.cam);
      const f = Math.max(0, Math.min(1, st.focus));
      const bgBlur = (1 - f) * 0.72;
      const docBlur = f * 0.62;
      const scr = st.screen || {};
      const on = Boolean(scr.power);

      if (faceIsVideo || docDirty) composeDoctor();
      if (on) drawScreen(t, scr, (G.w * v.s) / SW);
      probeLight(t, on);

      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
      ctx.fillStyle = '#050403';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.setTransform(v.s, 0, 0, v.s, -v.x * v.s, -v.y * v.s);

      // 1. 奥: 幕とテレビ。ピントが合っているときは、テレビのまわりを精細な絵で描く
      drawFocus(ctx, bg, bgMild, bgStrong, bgBlur, 0, 0, PW, PH);
      if (tvHi && bgBlur < 0.5) {
        const T = room.tvHi;
        ctx.globalAlpha = 1 - bgBlur * 2;
        ctx.drawImage(tvHi, T.x, T.y, T.w, T.h);
        ctx.globalAlpha = 1;
      }

      // 2. テレビの中身
      const gcx = G.x + G.w / 2;
      const gcy = G.y + G.h / 2;
      if (on) {
        blurInto(tvSoft, screenCv, 2);
        ctx.drawImage(screenCv, G.x, G.y, G.w, G.h);
        if (bgBlur > 0.03) {
          ctx.globalAlpha = Math.min(1, bgBlur * 1.7);
          ctx.drawImage(tvSoft, G.x, G.y, G.w, G.h);
          ctx.globalAlpha = 1;
        }
        // ガラスの映り込み（消えているときの暗いガラスを上から足す）と、にじみ
        ctx.globalCompositeOperation = 'lighter';
        ctx.globalAlpha = 0.8;
        ctx.drawImage(reflect, G.x, G.y, G.w, G.h);
        blurInto(tvBloom, tvSoft, 5);
        ctx.globalAlpha = 0.34;
        ctx.drawImage(tvBloom, G.x - G.w * 0.05, G.y - G.h * 0.05, G.w * 1.1, G.h * 1.1);
        ctx.globalAlpha = 1;
        ctx.globalCompositeOperation = 'source-over';
      }

      // 3. テレビの光を部屋へ足す
      const lit = light.level > 0.01;
      const flick = 0.93 + 0.07 * Math.sin(t * 0.021) * Math.sin(t * 0.0073 + 1.3);
      const lr = Math.round(light.r * 255);
      const lg = Math.round(light.g * 255);
      const lb = Math.round(light.b * 255);
      const tint = `rgb(${lr},${lg},${lb})`;
      const addLight = (tmp, tg, img, x, y, w, h, gain = 1) => {
        tg.globalCompositeOperation = 'source-over';
        tg.drawImage(img, 0, 0);
        tg.globalCompositeOperation = 'multiply';
        tg.fillStyle = tint;
        tg.fillRect(0, 0, tmp.width, tmp.height);
        ctx.globalCompositeOperation = 'lighter';
        let a = light.level * flick * gain;
        while (a > 0.01) {                 // 1 を超える分は重ね描きで足す
          ctx.globalAlpha = Math.min(1, a);
          ctx.drawImage(tmp, x, y, w, h);
          a -= 1;
        }
        ctx.globalAlpha = 1;
        ctx.globalCompositeOperation = 'source-over';
      };
      if (lit) {
        addLight(lightTmp, lightG, lightImg, 0, 0, PW, PH);
        // 空気に乗った光（テレビのまわりのもや）と、明るい画面から横へ伸びる筋
        ctx.globalCompositeOperation = 'lighter';
        const lv = light.level * flick;
        const haze = ctx.createRadialGradient(gcx, gcy, G.w * 0.25, gcx, gcy, G.w * 1.55);
        haze.addColorStop(0, `rgba(${lr},${lg},${lb},${(0.13 * lv).toFixed(3)})`);
        haze.addColorStop(0.45, `rgba(${lr},${lg},${lb},${(0.05 * lv).toFixed(3)})`);
        haze.addColorStop(1, `rgba(${lr},${lg},${lb},0)`);
        ctx.fillStyle = haze;
        ctx.fillRect(gcx - G.w * 1.6, gcy - G.w * 1.6, G.w * 3.2, G.w * 3.2);
        const sa = Math.max(0, lv - 0.5) * 0.2;
        if (sa > 0.005) {
          ctx.globalAlpha = Math.min(1, sa);
          ctx.drawImage(streakCv, gcx - G.w * 1.7, gcy - G.h * 0.09, G.w * 3.4, G.h * 0.18);
          ctx.globalAlpha = 1;
        }
        ctx.globalCompositeOperation = 'source-over';
      }

      // 4. 手前: 博士（カメラの動きに対して、奥より少し多くずれる）
      const px = -(v.cx - (F.x + F.w / 2)) * PARALLAX;
      const py = -(v.cy - (F.y + F.h / 2)) * PARALLAX * 0.4;
      drawFocus(ctx, doc, docMild, docStrong, docBlur, B.x + px, B.y + py, B.w, B.h);

      if (lit) {
        // 博士に当たる分（博士の形の中だけ。博士と一緒にずれる）
        addLight(lightDocTmp, lightDocG, lightDocImg, B.x + px, B.y + py, B.w, B.h, 1.6);
        // 5. 埃
        ctx.globalCompositeOperation = 'lighter';
        for (const m of motes) {
          const mx = m.x + ((m.vx * (t / 1000)) % 40) + Math.sin(t / 2300 + m.ph) * 9;
          const my = m.y + ((m.vy * (t / 1000)) % 120) + Math.cos(t / 3100 + m.ph) * 6;
          const dx = (mx - gcx) / (G.w * 0.9);
          const dy = (my - gcy) / (G.h * 0.9);
          const near = Math.max(0, 1 - (dx * dx + dy * dy));
          const a2 = near * light.level * (0.25 + 0.2 * Math.sin(t / 700 + m.ph * 3));
          if (a2 <= 0.01) continue;
          ctx.fillStyle = `rgba(${Math.round(light.r * 235)},${Math.round(light.g * 240)},${Math.round(light.b * 245)},${a2.toFixed(3)})`;
          ctx.beginPath();
          ctx.arc(mx + px * 2, my, m.r, 0, 6.283);
          ctx.fill();
        }
        ctx.globalCompositeOperation = 'source-over';
      }

      // 6. 画面全体: 灯り・周辺の落ち込み・粒
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.globalAlpha = 1;
      const W = canvas.width;
      const H = canvas.height;
      const ex = st.exposure == null ? 1 : Math.max(0, Math.min(1, st.exposure));
      if (ex < 1) {
        ctx.fillStyle = `rgba(4,3,2,${(1 - ex).toFixed(3)})`;
        ctx.fillRect(0, 0, W, H);
      }
      const vg = ctx.createRadialGradient(W / 2, H * 0.46, H * 0.42, W / 2, H * 0.5, W * 0.74);
      vg.addColorStop(0, 'rgba(0,0,0,0)');
      vg.addColorStop(1, 'rgba(0,0,0,0.62)');
      ctx.fillStyle = vg;
      ctx.fillRect(0, 0, W, H);
      ctx.globalCompositeOperation = 'overlay';
      ctx.globalAlpha = 0.11;
      ctx.drawImage(grainTiles[frames % grainTiles.length], 0, 0, W, H);
      ctx.globalCompositeOperation = 'source-over';
      ctx.globalAlpha = 1;

      // 7. 終わり: 砂嵐が画面全体を覆い、横一線に潰れ、点になって消える
      if (st.cut > 0) {
        const c = st.cut;
        const tile = staticTiles[frames % staticTiles.length];
        ctx.imageSmoothingEnabled = false;
        if (c < 0.55) {
          const a = Math.min(1, c / 0.36);
          ctx.globalAlpha = a * a;
          ctx.drawImage(tile, 0, 0, W, H);
          ctx.globalAlpha = a * 0.5;
          ctx.fillStyle = '#000';
          for (let k = 0; k < 5; k++) ctx.fillRect(0, ((k * 211 + frames * 67) % H), W, H * 0.028);
        } else {
          ctx.globalAlpha = 1;
          ctx.fillStyle = '#000';
          ctx.fillRect(0, 0, W, H);
          if (c < 0.74) {                       // 縦に潰れる
            const u = (c - 0.55) / 0.19;
            const hh = Math.max(3, H * Math.pow(1 - u, 3));
            ctx.drawImage(tile, 0, (H - hh) / 2, W, hh);
            ctx.globalAlpha = 0.35 + 0.6 * u;
            ctx.fillStyle = '#eef2ff';
            ctx.fillRect(0, (H - hh) / 2, W, hh);
          } else if (c < 0.9) {                 // 横一線が縮んで点になる
            const u = (c - 0.74) / 0.16;
            const ww = Math.max(4, W * Math.pow(1 - u, 2.2));
            ctx.globalAlpha = 1 - u * 0.5;
            ctx.fillStyle = '#eef2ff';
            ctx.fillRect((W - ww) / 2, H / 2 - 1.5, ww, 3);
            const gl = ctx.createRadialGradient(W / 2, H / 2, 0, W / 2, H / 2, ww * 0.5 + 30);
            gl.addColorStop(0, `rgba(220,230,255,${(0.35 * (1 - u)).toFixed(3)})`);
            gl.addColorStop(1, 'rgba(220,230,255,0)');
            ctx.fillStyle = gl;
            ctx.fillRect(0, H / 2 - 60, W, 120);
          } else if (c < 1) {                   // 残った点が消える
            ctx.globalAlpha = 1 - (c - 0.9) / 0.1;
            ctx.fillStyle = '#eef2ff';
            ctx.fillRect(W / 2 - 2, H / 2 - 2, 4, 4);
          }
        }
        ctx.globalAlpha = 1;
        ctx.imageSmoothingEnabled = true;
      }
      return { view: v, light: { ...light }, frames };
    }

    return {
      render,
      setFace(src) {
        faceSrc = src;
        faceIsVideo = Boolean(src && src.tagName === 'VIDEO');
        docDirty = true;
      },
      snapshot: () => ({ frames, light: { ...light }, canFilter }),
    };
  }

  window.RoomStage = { create, POWER_MS, CHANNEL_MS };
})();
