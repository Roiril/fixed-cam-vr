'use strict';

// 博士タブレットの効果音（tablet-sound.js と asset/tablet-sounds.json）を机上で確かめる。
// 「鳴ったか」は耳でしか分からないので、ここで見るのは 対になっている値が揃っているか と、鳴らす側の振る舞い。
//   node tools/visitor-ui/verify-tablet-sound.cjs

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..', '..');
const web = path.join(root, 'tablet', 'app', 'src', 'main', 'assets', 'web');
const html = fs.readFileSync(path.join(web, 'index.html'), 'utf8');
const manifest = JSON.parse(fs.readFileSync(path.join(web, 'asset', 'tablet-sounds.json'), 'utf8'));
const source = fs.readFileSync(path.join(web, 'tablet-sound.js'), 'utf8');

const tests = [];
const test = (name, fn) => tests.push([name, fn]);

test('画面が鳴らす名前は全部表にあり、表の素材は全部同梱されている', () => {
  const used = new Set(Array.from(html.matchAll(/\bsfx\('([a-z-]+)'/g), (m) => m[1]));
  assert.ok(used.size >= 8, `鳴らす名前 ${used.size}`);
  for (const name of used) assert.ok(manifest.sounds[name], `表に無い: ${name}`);
  for (const [name, sound] of Object.entries(manifest.sounds)) {
    assert.ok(used.has(name), `表にあるのに鳴らしていない: ${name}`);
    for (const entry of sound.files) assert.ok(fs.existsSync(path.join(web, 'asset', entry.file)), `同梱されていない: ${entry.file}`);
  }
});

test('本編（Quest）の音を 1 本も使っていない（LEDGER 0267）', () => {
  const crypto = require('node:crypto');
  const digest = (file) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  const mainDir = path.join(root, 'Assets', 'Resources', 'Sound');
  const main = new Set(fs.readdirSync(mainDir).filter((f) => f.endsWith('.wav')).map((f) => digest(path.join(mainDir, f))));
  const mainNames = new Set(fs.readdirSync(mainDir).filter((f) => f.endsWith('.wav')));
  for (const sound of Object.values(manifest.sounds)) {
    for (const entry of sound.files) {
      assert.equal(mainNames.has(entry.file), false, `本編と同じ名前: ${entry.file}`);
      assert.equal(main.has(digest(path.join(web, 'asset', entry.file))), false, `本編の音と同じ中身: ${entry.file}`);
    }
  }
  const maker = fs.readFileSync(path.join(root, 'tools', 'make-tablet-sounds.py'), 'utf8');
  // 説明文の言及は数えない。読むコード（read_wav・パスの組み立て）だけを見る
  assert.equal(/read_wav\(|["']Resources["']|Resources\/Sound["']/.test(maker), false, '作る道具が本編の音を読んでいる');
  // 同梱の音の置き場に、本編から複写した音が残っていない
  const stray = fs.readdirSync(path.join(web, 'asset')).filter((f) => mainNames.has(f));
  assert.deepEqual(Array.from(stray), [], `本編の音が同梱されたまま: ${stray.join(', ')}`);
});

test('どの音も博士の声より小さい', () => {
  for (const [name, sound] of Object.entries(manifest.sounds)) {
    for (const entry of sound.files) {
      assert.ok(entry.sourceLufs + entry.gainDb < manifest.voiceLufs, `${name}/${entry.file} が声以上`);
    }
  }
});

test('説明の終わりの音と画は同じ時刻で切れる（signal-cut.js と make-tablet-sounds.py は対）', () => {
  const js = fs.readFileSync(path.join(web, 'signal-cut.js'), 'utf8');
  const py = fs.readFileSync(path.join(root, 'tools', 'make-tablet-sounds.py'), 'utf8');
  const num = (text, re) => Number(re.exec(text)?.[1]);
  assert.equal(num(js, /NOISE_MS = (\d+)/), num(py, /NOISE_SEC = ([\d.]+)/) * 1000);
  assert.equal(num(js, /OFF_MS = (\d+)/), num(py, /OFF_SEC = ([\d.]+)/) * 1000);
  const jsBursts = /BURSTS = (\[\[[^\n]+\]\]);/.exec(js)[1].replace(/\s/g, '');
  const pyBursts = /BURSTS = \[([^\n]+)\]\n/.exec(py)[1].replace(/\(/g, '[').replace(/\)/g, ']').replace(/\s/g, '');
  assert.equal(jsBursts, `[${pyBursts}]`.replace(/0\.(\d)0\b/g, '0.$1'));
});

function createRuntime({ hidden = false } = {}) {
  const started = [];
  let clock = 0;
  class FakeSource {
    connect(target) { this.target = target; }
    start(when) { this.when = when; started.push(this); }
    stop() { this.stopped = true; this.onended?.(); }
  }
  class FakeGain { constructor() { this.gain = { value: 1 }; } connect() {} }
  class FakeContext {
    constructor() { this.state = 'running'; this.currentTime = 10; this.destination = {}; }
    resume() { this.state = 'running'; return Promise.resolve(); }
    createAnalyser() { return { fftSize: 256, connect() {}, getFloatTimeDomainData(d) { d.fill(0.01); } }; }
    createBufferSource() { return new FakeSource(); }
    createGain() { return new FakeGain(); }
    decodeAudioData() { return Promise.resolve({ duration: 0.3 }); }
  }
  const context = {
    AudioContext: FakeContext,
    document: { hidden },
    performance: { now: () => clock },
    requestAnimationFrame: () => 0,
    cancelAnimationFrame() {},
    fetch: (url) => Promise.resolve({
      ok: true,
      json: () => Promise.resolve(JSON.parse(JSON.stringify(manifest))),
      arrayBuffer: () => Promise.resolve(new ArrayBuffer(8)),
    }),
    window: {},
  };
  context.globalThis = context;
  vm.runInNewContext(source, context);
  return { api: context.window.TabletSound, started, context };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test('素材が複数ある音は順に回し、表の gainDb を掛け、軽減ありは 6dB 下げる', async () => {
  const rt = createRuntime();
  await rt.api.load(); await flush();
  const files = manifest.sounds.choice.files;
  assert.equal(rt.api.play('choice'), true);
  assert.equal(rt.api.play('choice'), true);
  assert.equal(rt.api.play('choice'), true);
  const names = Array.from(rt.api.snapshot().events.filter((e) => e.type === 'start'), (e) => String(e.file));
  assert.deepEqual(names, [files[0].file, files[1].file, files[0].file], '0153 と同じく交互（乱数で選ばない）');
  rt.api.setRelief(true);
  rt.api.play('stamp');
  rt.api.setRelief(false);
  rt.api.play('stamp');
  const reliefGain = rt.started.at(-2).target.gain.value;
  const normalGain = rt.started.at(-1).target.gain.value;
  assert.ok(Math.abs(normalGain - 10 ** (manifest.sounds.stamp.files[0].gainDb / 20)) < 1e-9, '表の gainDb をそのまま掛ける');
  assert.ok(Math.abs(reliefGain / normalGain - 10 ** (-6 / 20)) < 1e-9, '軽減ありは -6dB（音量半分）');
  assert.equal(rt.api.snapshot().started, 5);
});

test('消音・画面が隠れている・読み込み前は鳴らさず、理由を数える', async () => {
  const rt = createRuntime();
  assert.equal(rt.api.play('key'), false, '読み込み前');
  await rt.api.load(); await flush();
  rt.api.setMuted(true);
  assert.equal(rt.api.play('key'), false);
  rt.api.setMuted(false);
  const hidden = createRuntime({ hidden: true });
  await hidden.api.load(); await flush();
  assert.equal(hidden.api.play('key'), false);
  const reasons = Array.from(rt.api.snapshot().events.filter((e) => e.type === 'skip'), (e) => String(e.reason));
  assert.deepEqual(reasons, ['not-loaded', 'muted']);
});

test('判子は印が押される瞬間まで遅らせて予約する。名前で止められる', async () => {
  const rt = createRuntime();
  await rt.api.load(); await flush();
  rt.api.play('stamp', { delayMs: 510 });
  assert.ok(Math.abs(rt.started.at(-1).when - 10.51) < 1e-9);
  rt.api.play('signal-cut');
  assert.equal(rt.api.stop('signal-cut'), true);
  assert.equal(rt.started.at(-1).stopped, true);
  assert.equal(rt.api.stop('signal-cut'), false, '鳴っていなければ何もしない');
});

(async () => {
  let failed = 0;
  for (const [name, fn] of tests) {
    try { await fn(); console.log(`ok - ${name}`); }
    catch (error) { failed += 1; console.log(`not ok - ${name}`); console.log(error); }
  }
  if (failed) { console.log(`${failed} of ${tests.length} failed`); process.exit(1); }
  console.log(`${tests.length} tests passed`);
})();
