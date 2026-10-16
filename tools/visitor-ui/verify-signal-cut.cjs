'use strict';

// 博士の説明の終わり（signal-cut.js）の段の進み方を、時計を手で進めて確かめる。
// 見た目（砂嵐の濃さ・潰れ方）はここでは測らない。ブラウザで SignalCut.freezeAt(ms) を呼んで画で見る。
//   node tools/visitor-ui/verify-signal-cut.cjs

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..', '..');
const source = fs.readFileSync(path.join(root, 'tablet', 'app', 'src', 'main', 'assets', 'web', 'signal-cut.js'), 'utf8');

function createRuntime({ reduced = false, canvas = true } = {}) {
  let clock = 0;
  let nextId = 1;
  const timers = new Map();
  const frames = new Map();
  const classes = new Set();
  const stage = {
    clientWidth: 1097,
    clientHeight: 686,
    dataset: {},
    classList: {
      toggle: (name, on) => { if (on) classes.add(name); else classes.delete(name); },
      contains: (name) => classes.has(name),
    },
  };
  const context2d = {
    clearRect() {}, drawImage() {}, fillRect() {}, putImageData() {},
    createImageData: (w, h) => ({ data: new Uint8ClampedArray(w * h * 4) }),
    globalAlpha: 1, fillStyle: '',
  };
  const makeCanvas = () => ({ width: 0, height: 0, hidden: true, getContext: () => context2d });
  const cut = makeCanvas();
  const document = {
    createElement: () => makeCanvas(),
    getElementById: (id) => (id === 'signalCut' && canvas ? cut : null),
    querySelector: (selector) => (selector === '.stage' ? stage : null),
  };
  const host = {
    document,
    performance: { now: () => clock },
    matchMedia: () => ({ matches: reduced }),
    setTimeout: (fn, ms) => { const id = nextId++; timers.set(id, { at: clock + ms, fn }); return id; },
    clearTimeout: (id) => timers.delete(id),
    requestAnimationFrame: (fn) => { const id = nextId++; frames.set(id, fn); return id; },
    cancelAnimationFrame: (id) => frames.delete(id),
  };
  host.globalThis = host;
  vm.runInNewContext(source, host);
  const advance = (ms) => {
    const end = clock + ms;
    for (;;) {
      const due = [...timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      clock = due[1].at;
      timers.delete(due[0]);
      due[1].fn();
    }
    clock = end;
    const pending = [...frames.entries()];
    frames.clear();
    pending.forEach(([, fn]) => fn());
  };
  return { api: host.SignalCut, stage, cut, classes, advance, frames };
}

const tests = [];
const test = (name, fn) => tests.push([name, fn]);

test('黒になるまでの時間を返し、砂嵐 → 潰れる → 黒 の順に進む', () => {
  const rt = createRuntime();
  const { NOISE_MS, OFF_MS } = rt.api;
  assert.equal(rt.api.run({ muted: true }), NOISE_MS + OFF_MS);
  assert.equal(rt.stage.dataset.signal, 'noise');
  assert.equal(rt.cut.hidden, false, '砂嵐の面を出す');
  assert.ok(rt.frames.size > 0, '砂嵐を毎フレーム描く');
  rt.advance(NOISE_MS);
  assert.equal(rt.stage.dataset.signal, 'off');
  rt.advance(OFF_MS);
  assert.equal(rt.stage.dataset.signal, 'dark');
  assert.equal(rt.cut.hidden, true);
  assert.equal(rt.frames.size, 0, '黒の間は描かない');
});

test('release の後は黒で一拍置いてから溶かして戻り、最後に印を全部外す', () => {
  const rt = createRuntime();
  const { NOISE_MS, OFF_MS, DARK_MS, RETURN_MS } = rt.api;
  rt.api.run({ muted: true });
  rt.advance(NOISE_MS + OFF_MS);
  rt.api.release();
  rt.advance(DARK_MS - 1);
  assert.equal(rt.stage.dataset.signal, 'dark', '一拍のあいだは黒のまま');
  rt.advance(1);
  assert.equal(rt.stage.dataset.signal, 'return');
  rt.advance(RETURN_MS);
  assert.equal(rt.stage.dataset.signal, undefined);
  assert.equal(rt.classes.size, 0);
  assert.equal(rt.api.snapshot().running, false);
});

test('release が来なくても黒に閉じ込めない', () => {
  const rt = createRuntime();
  rt.api.run({ muted: true });
  rt.advance(10000);
  assert.equal(rt.stage.dataset.signal, undefined);
  assert.equal(rt.classes.size, 0);
});

test('途中で取り消すとすぐ元の画面に戻る', () => {
  const rt = createRuntime();
  rt.api.run({ muted: true });
  rt.advance(500);
  rt.api.abort();
  assert.equal(rt.stage.dataset.signal, undefined);
  assert.equal(rt.cut.hidden, true);
  rt.advance(5000);
  assert.equal(rt.classes.size, 0, '取り消した後に段が進まない');
});

test('動きを減らす設定と canvas が無い環境では何もしない（呼び出し側は従来の間で切り替える）', () => {
  for (const options of [{ reduced: true }, { canvas: false }]) {
    const rt = createRuntime(options);
    assert.equal(rt.api.run({ muted: true }), 0);
    assert.equal(rt.stage.dataset.signal, undefined);
    assert.equal(rt.api.snapshot().runs, 0);
  }
});

test('音は始まりと同時に 1 度だけ鳴らし、途中で取り消したら止める。最後まで流れたら止めない', () => {
  const rt = createRuntime();
  let played = 0;
  let stopped = 0;
  const sound = () => { played += 1; return () => { stopped += 1; }; };
  rt.api.run({ sound });
  assert.equal(played, 1);
  rt.advance(400);
  rt.api.abort();
  assert.equal(stopped, 1, '一時停止などで取り消したら砂嵐の音を残さない');
  rt.api.run({ sound });
  rt.advance(rt.api.NOISE_MS + rt.api.OFF_MS);
  rt.api.release();
  rt.advance(5000);
  assert.equal(played, 2);
  assert.equal(stopped, 2, '最後の片付けで止めても、音はもう鳴り終わっている（2.23 秒 < 黒まで 1.21 ＋ 一拍 0.62 ＋ 戻り 0.65）');
  rt.api.run({ sound: () => null });   // 音が鳴らせなかったときも画は進む
  rt.advance(10000);
  assert.equal(rt.stage.dataset.signal, undefined);
});

let failed = 0;
for (const [name, fn] of tests) {
  try { fn(); console.log(`ok - ${name}`); }
  catch (error) { failed += 1; console.log(`not ok - ${name}`); console.log(error); }
}
if (failed) { console.log(`${failed} of ${tests.length} failed`); process.exit(1); }
console.log(`${tests.length} tests passed`);
