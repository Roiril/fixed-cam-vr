// 博士タブレットの効果音（LEDGER 0266「タブレットは音がないので、音をつけて」）。
//
// 音の中身と大きさは tools/make-tablet-sounds.py が書く asset/tablet-sounds.json が正。
// 素材の音量は触っておらず、表の gainDb をここで掛ける。曲は鳴らさない（0256）。
//
//   TabletSound.play(name, { delayMs })  鳴らせたら true。素材が複数ある名前は順に回す（乱数で選ばない）
//   TabletSound.stop(name)                その名前で鳴っている音を止める
//   TabletSound.setRelief(on)             ホラー軽減ありは全部 -6dB（音量半分）
//   TabletSound.setMuted(on)
//   TabletSound.unlock()                  指が触れたときに呼ぶ（自動送りの終わりでも鳴らせるように）
//   TabletSound.snapshot()                観測用：再生の回数と、実際に信号が出た回数
(() => {
  'use strict';

  const host = globalThis;
  const MANIFEST = './asset/tablet-sounds.json';
  const SIGNAL_FLOOR = 0.00001;
  const RELIEF_DB = -6;
  let context = null;
  let analyser = null;
  let sampleData = null;
  let manifest = null;
  let loading = null;
  const buffers = new Map();
  const turns = new Map();
  const active = new Map();   // 名前 → 鳴っている source の集合
  let muted = false;
  let relief = false;
  const counts = { requested: 0, started: 0, signal: 0, skipped: 0 };
  const byName = {};
  const events = [];
  let watching = 0;

  function record(type, detail) {
    events.push({ type, at: Math.round(host.performance?.now?.() ?? Date.now()), ...detail });
    if (events.length > 40) events.splice(0, events.length - 40);
  }

  function ensureContext() {
    if (context) return context;
    const AudioContext = host.AudioContext || host.webkitAudioContext;
    if (!AudioContext) return null;
    try {
      context = new AudioContext();
      analyser = context.createAnalyser();
      analyser.fftSize = 256;
      sampleData = new Float32Array(analyser.fftSize);
      analyser.connect(context.destination);
    } catch (error) {
      context = null;
      analyser = null;
      record('unavailable', { reason: error?.name || 'create-failed' });
    }
    return context;
  }

  function load() {
    if (loading) return loading;
    const ctx = ensureContext();
    if (!ctx || !host.fetch) return Promise.resolve(false);
    loading = host.fetch(MANIFEST, { cache: 'force-cache' })
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json();
      })
      .then((data) => {
        manifest = data;
        const files = new Set();
        Object.values(data.sounds || {}).forEach((sound) => sound.files.forEach((entry) => files.add(entry.file)));
        return Promise.all(Array.from(files, (file) => host.fetch(`./asset/${file}`, { cache: 'force-cache' })
          .then((response) => {
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            return response.arrayBuffer();
          })
          .then((bytes) => ctx.decodeAudioData(bytes))
          .then((buffer) => { buffers.set(file, buffer); })
          .catch((error) => { record('decode-failed', { file, reason: error?.message || 'failed' }); })));
      })
      .then(() => {
        record('loaded', { buffers: buffers.size });
        return true;
      })
      .catch((error) => {
        record('manifest-failed', { reason: error?.message || 'failed' });
        loading = null;
        return false;
      });
    return loading;
  }

  function unlock() {
    const ctx = ensureContext();
    if (!ctx) return Promise.resolve(false);
    void load();
    const resume = ctx.state === 'suspended' ? ctx.resume() : Promise.resolve();
    return Promise.resolve(resume).then(() => ctx.state === 'running').catch(() => false);
  }

  // 鳴った証拠：解析器に信号が乗ったかを、鳴らしているあいだだけ見る
  function watchSignal(name, until) {
    if (!analyser) return;
    const tick = () => {
      if (!analyser || (host.performance?.now?.() ?? Date.now()) > until) { watching = 0; return; }
      analyser.getFloatTimeDomainData(sampleData);
      let sum = 0;
      for (const value of sampleData) sum += value * value;
      const rms = Math.sqrt(sum / sampleData.length);
      if (rms > SIGNAL_FLOOR) {
        counts.signal += 1;
        byName[name] = { ...(byName[name] || {}), signal: (byName[name]?.signal || 0) + 1 };
        record('signal', { name, rms: Number(rms.toFixed(6)) });
        watching = 0;
        return;
      }
      watching = host.requestAnimationFrame?.(tick) || 0;
    };
    if (watching) host.cancelAnimationFrame?.(watching);
    watching = host.requestAnimationFrame?.(tick) || 0;
  }

  function play(name, { delayMs = 0 } = {}) {
    counts.requested += 1;
    const sound = manifest?.sounds?.[name];
    const skip = (reason) => {
      counts.skipped += 1;
      record('skip', { name, reason });
      return false;
    };
    if (muted) return skip('muted');
    if (host.document?.hidden) return skip('hidden');
    if (!context || !sound) {
      void load();
      return skip(sound ? 'no-context' : 'not-loaded');
    }
    const turn = turns.get(name) || 0;
    const entry = sound.files[turn % sound.files.length];
    turns.set(name, turn + 1);
    const buffer = buffers.get(entry.file);
    if (!buffer) return skip('not-decoded');
    if (context.state === 'suspended') void context.resume();
    const source = context.createBufferSource();
    const gain = context.createGain();
    source.buffer = buffer;
    gain.gain.value = 10 ** ((entry.gainDb + (relief ? RELIEF_DB : 0)) / 20);
    source.connect(gain);
    gain.connect(analyser);
    const when = context.currentTime + Math.max(0, delayMs) / 1000;
    source.start(when);
    const playing = active.get(name) || new Set();
    playing.add(source);
    active.set(name, playing);
    source.onended = () => { playing.delete(source); };
    counts.started += 1;
    byName[name] = { ...(byName[name] || {}), started: (byName[name]?.started || 0) + 1 };
    record('start', { name, file: entry.file, delayMs });
    const now = host.performance?.now?.() ?? Date.now();
    watchSignal(name, now + delayMs + buffer.duration * 1000 + 300);
    return true;
  }

  function stop(name) {
    const playing = active.get(name);
    if (!playing || playing.size === 0) return false;
    playing.forEach((source) => { try { source.stop(); } catch (error) { /* already ended */ } });
    playing.clear();
    record('stop', { name });
    return true;
  }

  function setMuted(value) { muted = Boolean(value); }
  function setRelief(value) { relief = Boolean(value); }

  function snapshot() {
    return {
      ...counts,
      byName: JSON.parse(JSON.stringify(byName)),
      loaded: buffers.size,
      contextState: context?.state || 'unavailable',
      muted,
      relief,
      events: events.slice(),
    };
  }

  // 起動時に読み始める（復号は止まった音声の文脈でもできる。鳴らすのは指が触れてから）
  void load();

  const publicHost = typeof window === 'object' ? window : host;
  publicHost.TabletSound = { play, stop, unlock, load, setMuted, setRelief, snapshot };
})();
