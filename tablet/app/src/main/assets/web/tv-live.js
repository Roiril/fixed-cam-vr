// 博士の説明のテレビを「点いている」画にする。絵と動画は DOM のまま、その上の canvas に走査の揺れを描く。
//
//   init({ screen, meter, isLive, isVoiceLive, voiceTime })
//       screen     .tv-screen の中の canvas。粒・流れる帯・ときどきの横ずれ・電源の入り・チャンネル替えを描く
//       meter      字幕の上の canvas。博士の声の波形（日本語だけ。声の無い言語では畳む）
//       isVoiceLive() 説明の最中なら true。博士だけの場面でも波形は動かす
//       isLive()   テレビが出ていて、止まっていないとき true。false の間は描き足さない
//       voiceTime() いま流れている場面の音声の再生位置（秒）。音声が無ければ null
//   powerOn()      テレビが出た瞬間。黒から横一線が走り、上下へ開く
//   channel()      同じテレビの中で絵が替わる瞬間。短い砂嵐
//   setVoice(env, hz)  その場面の声の大きさの列（0–9 の数字の文字列・1 秒あたり hz 個）。null で波形を畳む
//   snapshot()     試験用: 描いたコマ数・電源とチャンネルの回数
//
// ⚠ 動画を含む層を transform / filter で動かすと Android WebView は黒に飛ぶ（memory/visitor_tablet.md）。
//    だから電源の入りも砂嵐も、層は動かさず canvas の中の絵で作る。
(() => {
  'use strict';

  const host = globalThis;
  const W = 254;          // 描く解像度（.tv-screen は 1.27:1）。拡大して使うので粒が走査線の太さになる
  const H = 200;
  const FRAME_MS = 1000 / 30;
  const IDLE_MS = 250;    // 止まっている間の見回り
  const POWER_MS = 620;
  const CHANNEL_MS = 260;
  const TILES = 5;
  const BARS = 33;
  const reducedMotion = host.matchMedia?.('(prefers-reduced-motion: reduce)') || { matches: false };

  let screen = null;
  let meter = null;
  let ctx = null;
  let mctx = null;
  let isLive = () => false;
  let isVoiceLive = () => false;
  let voiceTime = () => null;
  let tiles = [];
  let timer = 0;
  let raf = 0;
  let last = 0;
  let powerAt = -1;
  let channelAt = -1;
  let tearAt = 0;
  let tearUntil = 0;
  let tearY = 0;
  let env = null;
  let envHz = 20;
  let level = 0;
  const counts = { frames: 0, power: 0, channel: 0, meter: 0 };

  function now() {
    return host.performance?.now?.() ?? Date.now();
  }

  // 砂嵐の板を先に作っておく（毎コマ乱数を引くと低い端末でコマが落ちる）
  function buildTiles() {
    if (tiles.length || !host.document?.createElement) return;
    for (let i = 0; i < TILES; i++) {
      const c = host.document.createElement('canvas');
      c.width = W;
      c.height = H;
      const g = c.getContext('2d');
      if (!g) return;
      const img = g.createImageData(W, H);
      const d = img.data;
      for (let p = 0; p < d.length; p += 4) {
        const v = (Math.random() * 255) | 0;
        d[p] = v; d[p + 1] = v; d[p + 2] = v; d[p + 3] = 255;
      }
      g.putImageData(img, 0, 0);
      tiles.push(c);
    }
  }

  function drawScreen(t) {
    ctx.clearRect(0, 0, W, H);
    const tile = tiles[(counts.frames) % tiles.length];
    // 粒
    if (tile) {
      ctx.globalAlpha = 0.055;
      ctx.drawImage(tile, 0, 0);
    }
    // 上から下へ流れる明るい帯（電源の唸り）
    const period = 6800;
    const y = ((t % period) / period) * (H + 70) - 70;
    const band = ctx.createLinearGradient(0, y, 0, y + 70);
    band.addColorStop(0, 'rgba(255,255,255,0)');
    band.addColorStop(0.5, 'rgba(255,255,255,0.05)');
    band.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.globalAlpha = 1;
    ctx.fillStyle = band;
    ctx.fillRect(0, y, W, 70);
    // ときどき 1 本だけ走る横ずれ
    if (t > tearAt) {
      tearUntil = t + 70 + Math.random() * 60;
      tearAt = t + 3800 + Math.random() * 5200;
      tearY = (Math.random() * (H - 8)) | 0;
    }
    if (t < tearUntil && tile) {
      ctx.globalAlpha = 0.34;
      ctx.drawImage(tile, 0, tearY, W, 3, (Math.random() * 10 - 5) | 0, tearY, W, 3);
    }
    // チャンネル替え: 砂嵐が一瞬かぶって引く
    if (channelAt >= 0) {
      const u = (t - channelAt) / CHANNEL_MS;
      if (u >= 1) channelAt = -1;
      else if (tile) {
        ctx.globalAlpha = 0.92 * (1 - u) * (1 - u);
        ctx.drawImage(tiles[(counts.frames * 2 + 1) % tiles.length], 0, 0);
        ctx.globalAlpha = 0.5 * (1 - u);
        ctx.fillStyle = '#000';
        for (let k = 0; k < 4; k++) ctx.fillRect(0, ((k * 53 + counts.frames * 17) % H), W, 6);
      }
    }
    // 電源の入り: 黒 → 横一線 → 上下へ開く → 白が引く
    if (powerAt >= 0) {
      const u = (t - powerAt) / POWER_MS;
      if (u >= 1) powerAt = -1;
      else {
        ctx.globalAlpha = 1;
        ctx.fillStyle = '#000';
        const mid = H / 2;
        if (u < 0.22) {
          ctx.fillRect(0, 0, W, H);
          const lw = W * Math.min(1, u / 0.18);
          ctx.fillStyle = 'rgba(235,240,255,0.95)';
          ctx.fillRect((W - lw) / 2, mid - 1, lw, 2);
        } else {
          const open = Math.pow((u - 0.22) / 0.5, 0.6);
          const half = Math.min(mid, mid * open);
          ctx.fillRect(0, 0, W, mid - half);
          ctx.fillRect(0, mid + half, W, mid - half);
          ctx.globalAlpha = Math.max(0, 0.55 * (1 - (u - 0.22) / 0.78));
          ctx.fillStyle = '#eef2ff';
          ctx.fillRect(0, mid - half, W, half * 2);
        }
      }
    }
    ctx.globalAlpha = 1;
  }

  function drawMeter() {
    if (!mctx) return;
    const w = meter.width;
    const h = meter.height;
    mctx.clearRect(0, 0, w, h);
    const sec = env ? voiceTime() : null;
    let target = 0;
    if (env && typeof sec === 'number' && Number.isFinite(sec)) {
      const i = Math.floor(sec * envHz);
      const a = Number(env[i] ?? 0) / 9;
      const b = Number(env[i + 1] ?? 0) / 9;
      target = a + (b - a) * (sec * envHz - i);
    }
    level += (target - level) * (target > level ? 0.6 : 0.25);
    const gap = w / BARS;
    const mid = (BARS - 1) / 2;
    for (let k = 0; k < BARS; k++) {
      const far = Math.abs(k - mid) / mid;
      const shape = Math.pow(1 - far, 1.4);
      const wob = 0.6 + 0.4 * Math.sin(counts.frames * 0.9 + k * 1.7);
      const amp = Math.max(0.05, level * shape * wob);
      const bh = Math.max(1, amp * h * 0.92);
      mctx.fillStyle = `rgba(232,220,196,${0.22 + 0.6 * Math.min(1, level * 1.4) * shape})`;
      mctx.fillRect(Math.round(k * gap + gap * 0.25), Math.round((h - bh) / 2), Math.max(1, Math.round(gap * 0.5)), Math.round(bh));
    }
    counts.meter += 1;
  }

  function tick() {
    raf = 0;
    timer = 0;
    const live = isLive() && Boolean(ctx);
    const voice = Boolean(env && mctx) && isVoiceLive();
    const t = now();
    if (!live) { powerAt = -1; channelAt = -1; }  // 止まっている間の電源とチャンネルの途中は捨てる
    if (live || voice) {
      if (t - last >= FRAME_MS - 2) {
        last = t;
        counts.frames += 1;
        if (live) drawScreen(t);
        if (voice) drawMeter();
      }
      raf = host.requestAnimationFrame?.(tick) || 0;
      if (!raf) timer = host.setTimeout(tick, FRAME_MS);
      return;
    }
    timer = host.setTimeout(tick, IDLE_MS);
  }

  function init(options) {
    screen = options.screen || null;
    meter = options.meter || null;
    isLive = options.isLive || isLive;
    isVoiceLive = options.isVoiceLive || isVoiceLive;
    voiceTime = options.voiceTime || voiceTime;
    if (reducedMotion.matches || !screen?.getContext) return false;
    screen.width = W;
    screen.height = H;
    ctx = screen.getContext('2d');
    if (!ctx) return false;
    if (meter?.getContext) {
      meter.width = 264;
      meter.height = 36;
      mctx = meter.getContext('2d');
    }
    // 板づくりは起動直後の空き時間に（初回の章替えで 0.4 秒止まった signal-cut.js と同じ理由）
    host.setTimeout(buildTiles, 600);
    tearAt = now() + 2500;
    if (!timer && !raf) timer = host.setTimeout(tick, IDLE_MS);
    return true;
  }

  function powerOn() {
    if (!ctx) return false;
    buildTiles();
    powerAt = now();
    counts.power += 1;
    return true;
  }

  function channel() {
    if (!ctx || powerAt >= 0) return false;
    buildTiles();
    channelAt = now();
    counts.channel += 1;
    return true;
  }

  function setVoice(values, hz = 20) {
    env = typeof values === 'string' && values.length > 0 ? values : null;
    envHz = hz;
    if (!env) level = 0;
    if (meter) meter.hidden = !env || !mctx;
  }

  const publicHost = typeof window === 'object' ? window : host;
  publicHost.TvLive = { init, powerOn, channel, setVoice, snapshot: () => ({ ...counts, voice: Boolean(env) }) };
})();
