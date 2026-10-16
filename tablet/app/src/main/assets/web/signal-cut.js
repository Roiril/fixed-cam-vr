// 博士の説明の終わり：通信が切れる（LEDGER 0265「ざざざっとノイズがかかって全体の画面が消える感じ」）。
//
// 止まった博士の絵に砂嵐が乗り、ざ・ざ・ざと強まり、ブラウン管が切れるように横一線へ潰れて消える。
// 黒のまま一拍置いてから次の画面を出す。画面の切り替えは呼び出し側（黒になった後）が行う。
//
//   run({ gain, muted })  → 黒になるまでの ms を返す（動きを減らす設定・canvas 無しでは 0 で何もしない）
//   release()             → 黒の間に次の画面を描いたら呼ぶ。一拍おいて溶かして戻す
//   abort()               → 途中で取り消す（すぐ元の画面に戻す）
//   unlock()              → 指が触れたときに音の経路を開けておく（自動送りの終わりでも鳴らせるように）
(() => {
  'use strict';

  const host = globalThis;
  const NOISE_MS = 950;   // 砂嵐が強まる
  const OFF_MS = 260;     // 横一線に潰れて消える
  const DARK_MS = 620;    // 黒のまま置く一拍
  const RETURN_MS = 650;  // 次の画面が溶けて出る
  const SAFETY_MS = 2600; // release が来なくても黒に閉じ込めない
  // ざ・ざ・ざ（砂嵐が一段ずつ強まる瞬間。NOISE_MS に対する割合）
  const BURSTS = [[0.5, 0.58], [0.65, 0.71], [0.79, 0.85]];
  const NOISE_FRAMES = 6;
  const reducedMotion = host.matchMedia?.('(prefers-reduced-motion: reduce)') || { matches: false };

  let audioContext = null;
  let noiseBuffer = null;
  let frame = 0;
  let timers = [];
  let running = false;
  let released = false;
  let startedAt = 0;
  let stageEl = null;
  let canvas = null;
  let noiseTiles = [];
  let activeNodes = [];
  let runs = 0;
  let lastGain = 0;
  let safetyTimer = 0;
  let frozenAt = null;

  function now() { return host.performance?.now?.() ?? Date.now(); }
  function later(fn, ms) { timers.push(host.setTimeout(fn, ms)); }
  function clearTimers() { timers.forEach((id) => host.clearTimeout(id)); timers = []; }

  function capable() {
    const doc = host.document;
    return Boolean(!reducedMotion.matches && doc?.createElement && host.requestAnimationFrame
      && doc.getElementById?.('signalCut')?.getContext);
  }

  // 速い擬似乱数（Math.random を 100 万回呼ぶより軽い）
  function rng(seed) {
    let x = seed >>> 0 || 0x9e3779b9;
    return () => { x ^= x << 13; x ^= x >>> 17; x ^= x << 5; return (x >>> 0) / 4294967296; };
  }

  function buildTiles(w, h) {
    if (noiseTiles.length && noiseTiles[0].width === w && noiseTiles[0].height === h) return;
    noiseTiles = [];
    for (let k = 0; k < NOISE_FRAMES; k += 1) {
      const tile = host.document.createElement('canvas');
      tile.width = w;
      tile.height = h;
      const ctx = tile.getContext('2d');
      const image = ctx.createImageData(w, h);
      const px = new Uint32Array(image.data.buffer);
      const r = rng(0x51f15e + k * 7919);
      for (let i = 0; i < px.length; i += 1) {
        // 暗い側へ寄せた灰（白い砂嵐だと眩しすぎる）。行ごとに少し明るさを揺らして走査線を残す
        const row = (i / w) | 0;
        const v = Math.min(255, (r() ** 1.35) * (row & 1 ? 230 : 200)) | 0;
        px[i] = (255 << 24) | (v << 16) | (v << 8) | v;
      }
      ctx.putImageData(image, 0, 0);
      noiseTiles.push(tile);
    }
  }

  function intensity(p) {
    let i = p ** 1.7 * 0.75;
    for (const [a, b] of BURSTS) if (p >= a && p < b) i += 0.38;
    if (p > 0.88) i = 1;
    return Math.min(1, i);
  }

  function draw() {
    frame = 0;
    if (!running || !canvas) return;
    const t = frozenAt ?? (now() - startedAt);
    const ctx = canvas.getContext('2d');
    const { width: w, height: h } = canvas;
    const p = Math.min(1, t / NOISE_MS);
    const i = t >= NOISE_MS ? 1 : intensity(p);
    const r = rng((t * 977) | 0);
    const tile = noiseTiles[((t / 42) | 0) % noiseTiles.length];
    ctx.clearRect(0, 0, w, h);
    // 全体に薄く乗る砂嵐
    ctx.globalAlpha = Math.min(0.94, 0.08 + i * 0.86) * (0.82 + r() * 0.18);
    ctx.drawImage(tile, (r() * 8 - 4) | 0, (r() * 6 - 3) | 0);
    // 裂け目：横の帯がずれて走る。強まるほど本数が増え、太くなる
    const bands = 1 + Math.round(i * 9);
    for (let k = 0; k < bands; k += 1) {
      const y = (r() * h) | 0;
      const bh = Math.max(1, (r() * (4 + i * 22)) | 0);
      const shift = ((r() - 0.5) * w * 0.12 * (0.3 + i)) | 0;
      ctx.globalAlpha = 0.6 + r() * 0.4;
      if (r() < 0.3) { ctx.fillStyle = '#000'; ctx.fillRect(0, y, w, bh); }
      else ctx.drawImage(tile, 0, (r() * (h - bh)) | 0, w, bh, shift, y, w, bh);
    }
    // 下へ転がる暗い帯（同期が外れた画）
    const barH = h * 0.2;
    const barY = ((t * 0.55) % (h + barH * 2)) - barH;
    ctx.globalAlpha = 0.45 * i;
    ctx.fillStyle = '#000';
    ctx.fillRect(0, barY, w, barH);
    // ざ・ざ・ざの頭で一瞬白む
    if (BURSTS.some(([a]) => p >= a && p < a + 0.025)) {
      ctx.globalAlpha = 0.16;
      ctx.fillStyle = '#e8e2d6';
      ctx.fillRect(0, 0, w, h);
    }
    ctx.globalAlpha = 1;
    if (frozenAt == null) frame = host.requestAnimationFrame(draw);
  }

  function ensureAudio() {
    if (audioContext) return audioContext;
    const AudioContext = host.AudioContext || host.webkitAudioContext;
    if (!AudioContext) return null;
    try { audioContext = new AudioContext(); } catch (error) { audioContext = null; }
    return audioContext;
  }

  function unlock() {
    const context = ensureAudio();
    if (!context) return Promise.resolve(false);
    const resume = context.state === 'suspended' ? context.resume() : Promise.resolve();
    return Promise.resolve(resume).then(() => context.state === 'running').catch(() => false);
  }

  function noise(context) {
    if (noiseBuffer && noiseBuffer.sampleRate === context.sampleRate) return noiseBuffer;
    const length = Math.ceil(context.sampleRate * 1.6);
    noiseBuffer = context.createBuffer(1, length, context.sampleRate);
    const data = noiseBuffer.getChannelData(0);
    const r = rng(0xabcdef);
    for (let i = 0; i < length; i += 1) data[i] = r() * 2 - 1;
    return noiseBuffer;
  }

  // 音：細いヒスが這い上がり、ざ・ざ・ざで強まり、消える瞬間に音程が落ちて途切れる。下に低い唸り。
  function playSound(gainValue) {
    const context = ensureAudio();
    if (!context || context.state !== 'running' || gainValue <= 0) return false;
    const t0 = context.currentTime + 0.01;
    const end = t0 + (NOISE_MS + OFF_MS) / 1000;
    const master = context.createGain();
    master.gain.value = gainValue;
    master.connect(context.destination);

    const hiss = context.createBufferSource();
    hiss.buffer = noise(context);
    const band = context.createBiquadFilter();
    band.type = 'bandpass';
    band.frequency.setValueAtTime(1800, t0);
    band.frequency.linearRampToValueAtTime(3200, end);
    band.Q.value = 0.55;
    const env = context.createGain();
    const g = env.gain;
    g.setValueAtTime(0, t0);
    g.linearRampToValueAtTime(0.06, t0 + 0.12);
    g.linearRampToValueAtTime(0.2, t0 + 0.46);
    for (const [a, b] of BURSTS) {
      const s = t0 + a * NOISE_MS / 1000;
      const e = t0 + b * NOISE_MS / 1000;
      g.setValueAtTime(0.2 + a * 0.4, s);
      g.linearRampToValueAtTime(0.85, s + 0.012);
      g.linearRampToValueAtTime(0.5, e - 0.01);
      g.linearRampToValueAtTime(0.18 + b * 0.3, e);
    }
    g.linearRampToValueAtTime(0.95, t0 + 0.92);
    g.setValueAtTime(0.95, end - 0.03);
    g.linearRampToValueAtTime(0, end);
    hiss.connect(band).connect(env).connect(master);

    const hum = context.createOscillator();
    hum.type = 'sawtooth';
    hum.frequency.value = 52;
    const humLow = context.createBiquadFilter();
    humLow.type = 'lowpass';
    humLow.frequency.value = 180;
    const humEnv = context.createGain();
    humEnv.gain.setValueAtTime(0, t0);
    humEnv.gain.linearRampToValueAtTime(0.35, t0 + 0.6);
    humEnv.gain.linearRampToValueAtTime(0.5, end - 0.05);
    humEnv.gain.linearRampToValueAtTime(0, end);
    hum.connect(humLow).connect(humEnv).connect(master);

    // 消える瞬間：音程が落ちて切れる
    const drop = context.createOscillator();
    drop.type = 'sine';
    const offAt = t0 + NOISE_MS / 1000;
    drop.frequency.setValueAtTime(1100, offAt);
    drop.frequency.exponentialRampToValueAtTime(45, end);
    const dropEnv = context.createGain();
    dropEnv.gain.setValueAtTime(0, offAt);
    dropEnv.gain.linearRampToValueAtTime(0.3, offAt + 0.02);
    dropEnv.gain.linearRampToValueAtTime(0, end);
    drop.connect(dropEnv).connect(master);

    hiss.start(t0); hum.start(t0); drop.start(offAt);
    hiss.stop(end + 0.05); hum.stop(end + 0.05); drop.stop(end + 0.05);
    activeNodes = [hiss, hum, drop, master];
    return true;
  }

  function stopSound() {
    activeNodes.forEach((node) => {
      try { if (node.stop) node.stop(); else node.disconnect(); } catch (error) { /* already ended */ }
    });
    activeNodes = [];
  }

  function setPhase(phase) {
    if (!stageEl) return;
    stageEl.classList.toggle('is-signal-noise', phase === 'noise');
    stageEl.classList.toggle('is-signal-off', phase === 'off');
    stageEl.classList.toggle('is-signal-dark', phase === 'dark');
    stageEl.classList.toggle('is-signal-return', phase === 'return');
    stageEl.dataset.signal = phase || '';
    if (!phase) delete stageEl.dataset.signal;
  }

  function cleanup() {
    clearTimers();
    host.clearTimeout(safetyTimer);
    if (frame) host.cancelAnimationFrame?.(frame);
    frame = 0;
    running = false;
    released = false;
    frozenAt = null;
    stopSound();
    if (canvas) {
      canvas.getContext('2d')?.clearRect(0, 0, canvas.width, canvas.height);
      canvas.hidden = true;
    }
    setPhase('');
  }

  function returnToScreen() {
    clearTimers();
    host.clearTimeout(safetyTimer);
    setPhase('return');
    later(cleanup, RETURN_MS);
  }

  function run({ gain = 0.32, muted = false } = {}) {
    if (running) cleanup();
    if (!capable()) return 0;
    stageEl = host.document.querySelector('.stage');
    canvas = host.document.getElementById('signalCut');
    const cssW = Math.max(1, stageEl?.clientWidth || host.innerWidth || 1280);
    const cssH = Math.max(1, stageEl?.clientHeight || host.innerHeight || 800);
    const w = Math.ceil(cssW / 2);
    const h = Math.ceil(cssH / 2);
    try {
      buildTiles(w, h);
      canvas.width = w;
      canvas.height = h;
    } catch (error) {
      return 0;   // 砂嵐を作れなければ、ただのカットで切り替える
    }
    runs += 1;
    lastGain = muted ? 0 : gain;
    running = true;
    released = false;
    startedAt = now();
    canvas.hidden = false;
    setPhase('noise');
    if (!muted) playSound(gain);
    frame = host.requestAnimationFrame(draw);
    later(() => setPhase('off'), NOISE_MS);
    later(() => {
      setPhase('dark');
      if (frame) host.cancelAnimationFrame?.(frame);
      frame = 0;
      canvas.hidden = true;
    }, NOISE_MS + OFF_MS);
    safetyTimer = host.setTimeout(() => { if (running) returnToScreen(); }, NOISE_MS + OFF_MS + SAFETY_MS);
    return NOISE_MS + OFF_MS;
  }

  function release() {
    if (!running || released) return;
    released = true;
    const blackAt = startedAt + NOISE_MS + OFF_MS;
    const wait = Math.max(0, blackAt + DARK_MS - now());
    host.clearTimeout(safetyTimer);
    later(returnToScreen, wait);
  }

  function abort() {
    if (running || stageEl?.dataset.signal) cleanup();
  }

  // 確認用：時刻 ms の画で止める（砂嵐と CSS の動きの両方）。run の直後に呼ぶ。abort で戻す
  function freezeAt(ms) {
    if (!running) return '';
    clearTimers();
    host.clearTimeout(safetyTimer);
    if (frame) host.cancelAnimationFrame?.(frame);
    frame = 0;
    stopSound();
    frozenAt = ms;
    const phase = ms < NOISE_MS ? 'noise' : (ms < NOISE_MS + OFF_MS ? 'off' : 'dark');
    setPhase(phase);
    canvas.hidden = phase === 'dark';
    const offset = phase === 'noise' ? ms : ms - NOISE_MS;
    host.document.getAnimations?.().forEach((animation) => {
      if (['signal-jitter', 'crt-off'].includes(animation.animationName)) {
        animation.pause();
        animation.currentTime = offset;
      }
    });
    if (phase !== 'dark') draw();
    return phase;
  }

  function snapshot() {
    return { runs, running, phase: stageEl?.dataset.signal || '', gain: lastGain };
  }

  const publicHost = typeof window === 'object' ? window : host;
  publicHost.SignalCut = { run, release, abort, unlock, snapshot, freezeAt, NOISE_MS, OFF_MS, DARK_MS, RETURN_MS };
})();
