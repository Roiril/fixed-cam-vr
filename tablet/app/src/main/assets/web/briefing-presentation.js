(() => {
  'use strict';

  const host = globalThis;
  const referenceSound = './asset/briefing-reference-v1.wav';
  const maximumEvents = 60;
  const signalFloor = 0.00001;
  const reducedMotion = host.matchMedia?.('(prefers-reduced-motion: reduce)') || { matches: false };
  const events = [];
  let audioContext = null;
  let audioBuffer = null;
  let audioBufferPromise = null;
  let analyser = null;
  let sampleData = null;
  let activeSource = null;
  let activeSampleFrame = 0;
  let referenceTimer = 0;
  let pendingScene = '';
  let lastPresentedScene = '';
  let completionTimer = 0;
  let generation = 0;
  // 終わりの待ち（holdThen）は資料の札と分ける。資料を隠すたびに終わりの待ちまで消え、
  // 説明が装着の案内へ進まず止まった（2026-10-16 実機・状態の定期取得が 2 秒ごとに資料を隠し直す）
  let completionGeneration = 0;
  let muted = false;
  let referenceDisplayCount = 0;
  let soundPlayCount = 0;
  let sourceStartCount = 0;
  let lastStartedAt = 0;
  let rms = 0;
  let peakRms = 0;

  function now() {
    return Math.round(host.performance?.now?.() ?? Date.now());
  }

  function record(type, detail = {}) {
    events.push({ type, at: now(), ...detail });
    if (events.length > maximumEvents) events.splice(0, events.length - maximumEvents);
  }

  function createAudioContext() {
    if (audioContext) return audioContext;
    const AudioContext = host.AudioContext || host.webkitAudioContext;
    if (!AudioContext) return null;
    try {
      audioContext = new AudioContext();
      analyser = audioContext.createAnalyser();
      analyser.fftSize = 256;
      sampleData = new Float32Array(analyser.fftSize);
      analyser.connect(audioContext.destination);
    } catch (error) {
      record('audio-unavailable', { reason: error?.name || 'create-failed' });
      audioContext = null;
      analyser = null;
      sampleData = null;
    }
    return audioContext;
  }

  async function prepareAudio() {
    const context = createAudioContext();
    if (!context || audioBuffer || audioBufferPromise) return audioBufferPromise;
    audioBufferPromise = host.fetch(referenceSound, { cache: 'force-cache' })
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.arrayBuffer();
      })
      .then((bytes) => context.decodeAudioData(bytes))
      .then((decoded) => {
        audioBuffer = decoded;
        record('audio-decoded', { durationMs: Math.round(decoded.duration * 1000) });
        return decoded;
      })
      .catch((error) => {
        record('audio-decode-failed', { reason: error?.name || 'decode-failed' });
        audioBufferPromise = null;
        return null;
      });
    return audioBufferPromise;
  }

  function unlock() {
    const context = createAudioContext();
    if (!context) return Promise.resolve(false);
    const resume = context.state === 'suspended' ? context.resume() : Promise.resolve();
    return Promise.resolve(resume)
      .then(() => prepareAudio())
      .then(() => context.state === 'running' && Boolean(audioBuffer))
      .catch(() => false);
  }

  function stopSampling() {
    if (!activeSampleFrame) return;
    host.cancelAnimationFrame?.(activeSampleFrame);
    activeSampleFrame = 0;
  }

  function sampleSignal(scene, source) {
    if (activeSource !== source || !analyser || !sampleData) return;
    analyser.getFloatTimeDomainData(sampleData);
    let squareSum = 0;
    for (const value of sampleData) squareSum += value * value;
    rms = Math.sqrt(squareSum / sampleData.length);
    peakRms = Math.max(peakRms, rms);
    if (!source.signalObserved && rms > signalFloor) {
      source.signalObserved = true;
      soundPlayCount += 1;
      record('sound-signal', { scene, rms: Number(rms.toFixed(6)) });
    }
    activeSampleFrame = host.requestAnimationFrame?.(() => sampleSignal(scene, source)) || 0;
  }

  function stopSound(reason = 'cancel') {
    stopSampling();
    const source = activeSource;
    activeSource = null;
    if (!source) return;
    source.onended = null;
    try { source.stop(); } catch (error) { /* The source already ended. */ }
    record('sound-cancel', { scene: source.scene, reason });
  }

  function playSound(scene) {
    if (muted || host.document?.hidden) {
      record('sound-skip', { scene, reason: muted ? 'muted' : 'hidden' });
      return false;
    }
    if (!audioContext || audioContext.state !== 'running' || !audioBuffer || !analyser) {
      record('sound-skip', { scene, reason: audioBuffer ? 'context-not-running' : 'not-decoded' });
      return false;
    }
    stopSound('replaced');
    const source = audioContext.createBufferSource();
    const gain = audioContext.createGain();
    source.buffer = audioBuffer;
    source.scene = scene;
    source.signalObserved = false;
    gain.gain.value = 1;
    source.connect(gain);
    gain.connect(analyser);
    source.onended = () => {
      if (activeSource !== source) return;
      stopSampling();
      activeSource = null;
      rms = 0;
      record('sound-ended', { scene });
    };
    activeSource = source;
    sourceStartCount += 1;
    lastStartedAt = now();
    record('sound-start', { scene, gain: 1 });
    source.start();
    sampleSignal(scene, source);
    return true;
  }

  // 資料の提示と音だけを取り消す。終わりの待ちは残す
  function cancelReference(reason = 'cancel') {
    generation += 1;
    if (referenceTimer) host.clearTimeout(referenceTimer);
    referenceTimer = 0;
    pendingScene = '';
    stopSound(reason);
    record('reference-cancel', { reason });
  }

  function cancel(reason = 'cancel') {
    generation += 1;
    completionGeneration += 1;
    if (referenceTimer) host.clearTimeout(referenceTimer);
    if (completionTimer) host.clearTimeout(completionTimer);
    referenceTimer = 0;
    pendingScene = '';
    completionTimer = 0;
    stopSound(reason);
    record('reference-cancel', { reason });
  }

  function presentReference({ scene, initial = false, sound = true, onStart }) {
    if (pendingScene === scene || (!initial && lastPresentedScene === scene)) {
      record('reference-skip', { scene, reason: 'unchanged' });
      return false;
    }
    generation += 1;
    const token = generation;
    if (referenceTimer) host.clearTimeout(referenceTimer);
    referenceTimer = 0;
    pendingScene = scene;
    const delayMs = reducedMotion.matches ? 0 : (initial ? 120 : 0);
    record('reference-scheduled', { scene, delayMs });
    const start = () => {
      referenceTimer = 0;
      pendingScene = '';
      if (token !== generation || host.document?.hidden) return;
      lastPresentedScene = scene;
      referenceDisplayCount += 1;
      record('reference-present', { scene });
      onStart?.();
      if (sound) playSound(scene);
    };
    if (delayMs === 0) start();
    else referenceTimer = host.setTimeout(start, delayMs);
    return true;
  }

  function holdThen({ scene, delayMs = 220, onComplete }) {
    completionGeneration += 1;
    const token = completionGeneration;
    if (completionTimer) host.clearTimeout(completionTimer);
    completionTimer = 0;
    const delay = reducedMotion.matches ? 0 : Math.max(0, delayMs);
    record('completion-hold', { scene, delayMs: delay });
    const complete = () => {
      completionTimer = 0;
      if (token !== completionGeneration || host.document?.hidden) return;
      record('completion-present', { scene });
      onComplete?.();
    };
    if (delay === 0) complete();
    else completionTimer = host.setTimeout(complete, delay);
  }

  function setMuted(value) {
    muted = Boolean(value);
    if (muted) cancel('muted');
  }

  function reset() {
    cancel('reset');
    events.length = 0;
    referenceDisplayCount = 0;
    soundPlayCount = 0;
    sourceStartCount = 0;
    lastStartedAt = 0;
    lastPresentedScene = '';
    rms = 0;
    peakRms = 0;
  }

  function snapshot() {
    return {
      events: events.map((event) => ({ ...event })),
      referenceDisplayCount,
      soundPlayCount,
      sourceStartCount,
      lastStartedAt,
      audioContextState: audioContext?.state || 'unavailable',
      decoded: Boolean(audioBuffer),
      rms: Number(rms.toFixed(6)),
      peakRms: Number(peakRms.toFixed(6)),
    };
  }

  const publicHost = typeof window === 'object' ? window : host;
  publicHost.BriefingPresentation = {
    unlock,
    prepareAudio,
    presentReference,
    holdThen,
    cancel,
    cancelReference,
    setMuted,
    reset,
    snapshot,
  };
})();
