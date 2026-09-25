import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const html = await readFile(resolve(root, 'index.html'), 'utf8');
const bootstrap = html.match(/<script>([\s\S]*?)<\/script>/)[1];

// Exercise the gate before *any* deferred script can run.
function boot({ seen = false, hash = '', storageFails = false } = {}) {
  const classes = new Set(), events = {}, timers = new Map();
  let id = 0;
  const window = { addEventListener: (name, callback) => { events[name] = callback; } };
  vm.runInNewContext(bootstrap, {
    window, location: { hash },
    document: { documentElement: { classList: { add: name => classes.add(name), remove: name => classes.delete(name) } },
      addEventListener: (name, callback) => { events[name] = callback; } },
    sessionStorage: { getItem() { if (storageFails) throw Error(); return seen ? '1' : null; }, setItem() {} },
    setTimeout: (callback, delay) => { timers.set(++id, { callback, delay }); return id; },
    clearTimeout: id => timers.delete(id)
  });
  return { gate: window.MAWARIMI_ENTRANCE_BOOT, classes, events, timers };
}
for (const options of [{}, { storageFails: true }]) {
  const state = boot(options);
  assert.equal(state.gate.pending, true);
  assert(state.classes.has('entrance-pending'));
  const timer = [...state.timers.values()][0];
  assert.equal(timer.delay, 15000);
  timer.callback();
  assert.equal(state.gate.pending, false);
  assert.equal(state.classes.size, 0);
}
for (const options of [{ seen: true }, { hash: '#credits' }]) {
  const state = boot(options);
  assert.equal(state.gate.pending, false);
  assert.equal(state.classes.size, 0);
}
for (const action of ['click', 'keydown', 'hashchange']) {
  const state = boot();
  state.events[action]({ key: 'Escape', target: { closest: () => true } });
  assert.equal(state.gate.pending, false);
  assert.equal(state.timers.size, 0);
  state.gate.show();
  assert.equal(state.gate.pending, true);
  state.gate.release();
}
assert(html.indexOf('entrance-pending') < html.indexOf('rel="stylesheet"'));
assert.match(html, /entrance-pending body > :not\(#entrance-loading\):not\(#entrance\)/);

const window = {};
vm.runInNewContext(await readFile(resolve(root, 'entrance-timing.js'), 'utf8'), { window });
const timing = window.MAWARIMI_ENTRANCE_TIMING;
const hlsl = await readFile(resolve(root, '../Assets/Art/Shaders/Intro/IntroFractureTime.hlsl'), 'utf8');
for (const [name, value] of Object.entries(timing)) {
  const match = hlsl.match(new RegExp(`float ${name} = ([0-9.]+);`));
  if (match) assert.equal(value, Number(match[1]), `Quest clock mismatch: ${name}`);
}
assert.equal(timing.duration, 5000);
const clips = await Promise.all(['sfx_shatter', 'sfx_screen_on'].map(async name => {
  const web = await readFile(resolve(root, `assets/${name}.wav`));
  const quest = await readFile(resolve(root, `../Assets/Resources/Sound/${name}.wav`));
  assert(web.equals(quest), `${name}: audio must remain byte-identical`);
  let byteRate, dataLength;
  for (let offset = 12; offset + 8 <= web.length;) {
    const chunk = web.toString('ascii', offset, offset + 4), size = web.readUInt32LE(offset + 4);
    if (chunk === 'fmt ') byteRate = web.readUInt32LE(offset + 16);
    if (chunk === 'data') dataLength = size;
    offset += 8 + size + size % 2;
  }
  assert(byteRate > 0 && dataLength > 0);
  return dataLength / byteRate;
}));
const shatter = timing.shatterAt * timing.duration / 1000 + timing.audioLead;
const screen = timing.screenOnAt * timing.duration / 1000 + timing.audioLead;
assert(screen - shatter - clips[0] > .2, 'Keep silence between the two clips');
assert(screen + clips[1] < timing.duration / 1000, 'Do not truncate the closing sound');

// Validate real scheduling and cleanup through a deterministic Web Audio double.
const calls = [], contexts = [];
class AudioContext {
  currentTime = 10;
  destination = {};
  constructor() { contexts.push(this); }
  decodeAudioData() { return Promise.resolve({}); }
  resume() { calls.push('resume'); return Promise.resolve(); }
  suspend() { calls.push('suspend'); return Promise.resolve(); }
  close() { calls.push('close'); return Promise.resolve(); }
  createBufferSource() {
    return { connect() {}, disconnect() { calls.push('disconnect'); }, start(t) { calls.push(t); }, stop() { calls.push('stop'); } };
  }
}
window.AudioContext = AudioContext;
vm.runInNewContext(await readFile(resolve(root, 'entrance-audio.js'), 'utf8'), {
  window, AbortController, setTimeout, clearTimeout,
  fetch: async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(0) })
});
const sound = await window.MAWARIMI_ENTRANCE_AUDIO.prepare();
assert.equal(calls.length, 0, 'Loading must not play audio');
await sound.unlock(); sound.start(); sound.pause(); sound.resume(); sound.dispose(); sound.dispose();
assert(Math.abs(calls.find(value => typeof value === 'number') - (10 + shatter)) < 1e-9);
assert.equal(calls.filter(value => value === 'stop').length, 2);
assert.equal(calls.filter(value => value === 'close').length, 1);
console.log(`Entrance checks passed: initial gate, repeat/hash/storage, timeout/skip, Quest clock, unchanged WAV, audio lifecycle.\nShatter ${shatter.toFixed(3)}s / ${clips[0]}s; close ${screen.toFixed(3)}s / ${clips[1]}s; silence ${(screen-shatter-clips[0]).toFixed(3)}s.`);
