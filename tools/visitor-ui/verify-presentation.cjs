'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..', '..');
const source = fs.readFileSync(path.join(root, 'tablet', 'app', 'src', 'main', 'assets', 'web', 'briefing-presentation.js'), 'utf8');

function createRuntime({ reduced = false, signal = 0.02 } = {}) {
  let clock = 1000;
  let nextTimer = 1;
  const timers = new Map();
  const sources = [];
  const frames = new Map();
  let nextFrame = 1;

  class FakeSource {
    connect(target) { this.target = target; }
    start() { this.started = true; }
    stop() { this.stopped = true; }
  }

  class FakeAnalyser {
    constructor() { this.fftSize = 256; }
    connect(target) { this.target = target; }
    getFloatTimeDomainData(data) { data.fill(signal); }
  }

  class FakeAudioContext {
    constructor() {
      this.state = 'suspended';
      this.destination = {};
    }
    resume() { this.state = 'running'; return Promise.resolve(); }
    createAnalyser() { return new FakeAnalyser(); }
    createBufferSource() {
      const node = new FakeSource();
      sources.push(node);
      return node;
    }
    createGain() { return { gain: { value: 0 }, connect(target) { this.target = target; } }; }
    decodeAudioData() { return Promise.resolve({ duration: 0.18 }); }
  }

  const context = {
    AudioContext: FakeAudioContext,
    clearTimeout(id) { timers.delete(id); },
    cancelAnimationFrame(id) { frames.delete(id); },
    document: { hidden: false },
    fetch: async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(16) }),
    matchMedia: () => ({ matches: reduced }),
    performance: { now: () => clock },
    requestAnimationFrame(callback) {
      const id = nextFrame++;
      frames.set(id, callback);
      return id;
    },
    setTimeout(callback, delay = 0) {
      const id = nextTimer++;
      timers.set(id, { callback, delay });
      return id;
    },
    window: {},
  };
  vm.runInNewContext(source, context, { filename: 'briefing-presentation.js' });
  return {
    api: context.window.BriefingPresentation,
    sources,
    timerDelays: () => Array.from(timers.values(), (timer) => timer.delay),
    runDelay(delay) {
      const entry = Array.from(timers.entries()).find(([, timer]) => timer.delay === delay);
      assert.ok(entry, `${delay}ms のタイマーが存在する`);
      timers.delete(entry[0]);
      clock += delay;
      entry[1].callback();
    },
  };
}

async function testPresentationAndSignal() {
  const runtime = createRuntime();
  assert.equal(await runtime.api.unlock(), true);
  assert.deepEqual(runtime.api.snapshot().decoded, true);

  let started = 0;
  runtime.api.presentReference({ scene: 'subject', initial: true, onStart: () => { started += 1; } });
  assert.deepEqual(runtime.timerDelays(), [120]);
  runtime.api.cancel('controls');
  assert.equal(started, 0);
  assert.equal(runtime.api.snapshot().sourceStartCount, 0, '取り消した古い資料音は開始しない');

  runtime.api.presentReference({ scene: 'subject', initial: true, onStart: () => { started += 1; } });
  runtime.runDelay(120);
  let snapshot = runtime.api.snapshot();
  assert.equal(started, 1);
  assert.equal(snapshot.referenceDisplayCount, 1);
  assert.equal(snapshot.sourceStartCount, 1);
  assert.equal(snapshot.soundPlayCount, 1, 'AnalyserNode が実信号を検出してから再生回数へ数える');
  assert.ok(snapshot.peakRms > 0.00001);

  assert.equal(runtime.api.presentReference({ scene: 'subject', onStart: () => { started += 1; } }), false);
  snapshot = runtime.api.snapshot();
  assert.equal(snapshot.sourceStartCount, 1, '同じ資料の再描画では音を重ねない');

  runtime.api.presentReference({ scene: 'report', onStart: () => { started += 1; } });
  snapshot = runtime.api.snapshot();
  assert.equal(started, 2, '資料から資料への交換は入力と同じ呼び出しで開始する');
  assert.equal(snapshot.referenceDisplayCount, 2);
  assert.equal(snapshot.sourceStartCount, 2);
  assert.equal(snapshot.soundPlayCount, 2);
  assert.equal(runtime.sources[0].stopped, true, '次の資料音は前の音を停止してから始める');

  for (let index = 0; index < 70; index += 1) runtime.api.cancel(`test-${index}`);
  assert.ok(runtime.api.snapshot().events.length <= 60, '実機観測イベントは直近60件に収める');
}

async function testStartIsNotSignal() {
  const runtime = createRuntime({ signal: 0 });
  await runtime.api.unlock();
  runtime.api.presentReference({ scene: 'subject' });
  const snapshot = runtime.api.snapshot();
  assert.equal(snapshot.sourceStartCount, 1);
  assert.equal(snapshot.soundPlayCount, 0, 'source.start だけを可聴確認にしない');
  assert.equal(snapshot.peakRms, 0);
}

async function testMutedReferenceStillAppears() {
  const runtime = createRuntime();
  await runtime.api.unlock();
  runtime.api.setMuted(true);
  let shown = 0;
  runtime.api.presentReference({ scene: 'subject', onStart: () => { shown += 1; } });
  assert.equal(shown, 1, '消音でも資料を表示する');
  assert.equal(runtime.api.snapshot().sourceStartCount, 0, '消音時は音だけを止める');
}

function testReducedMotion() {
  const runtime = createRuntime({ reduced: true });
  let shown = 0;
  let completed = 0;
  runtime.api.presentReference({ scene: 'subject', initial: true, sound: false, onStart: () => { shown += 1; } });
  runtime.api.holdThen({ scene: 'handoff', delayMs: 220, onComplete: () => { completed += 1; } });
  assert.equal(shown, 1);
  assert.equal(completed, 1);
  assert.deepEqual(runtime.timerDelays(), [], '動きを減らす設定では資料と案内を即時に切り替える');
}

function testReferenceCancelKeepsCompletion() {
  const runtime = createRuntime();
  let completed = 0;
  runtime.api.holdThen({ scene: 'wear-complete', delayMs: 1210, onComplete: () => { completed += 1; } });
  // 状態の定期取得が資料を隠し直すたびに呼ぶ取消。終わりの待ちは残す（2026-10-16 実機で説明が止まった）
  runtime.api.cancelReference('reference-hidden');
  runtime.api.presentReference({ scene: 'wear', initial: true, sound: false, onStart() {} });
  runtime.runDelay(1210);
  assert.equal(completed, 1, '資料の取消では終わりの待ちを消さない');
  runtime.api.holdThen({ scene: 'wear-complete', delayMs: 1210, onComplete: () => { completed += 1; } });
  runtime.api.cancel('controls');
  assert.equal(runtime.timerDelays().includes(1210), false, '一時停止の取消では終わりの待ちも消す');
  assert.equal(completed, 1);
}

(async () => {
  await testPresentationAndSignal();
  console.log('ok - pending取消・資料交換・実信号RMS・再描画の重複防止');
  await testStartIsNotSignal();
  console.log('ok - source.startと実信号を区別する');
  await testMutedReferenceStillAppears();
  console.log('ok - 消音でも資料は表示する');
  testReducedMotion();
  console.log('ok - prefers-reduced-motionでは遷移待ちを置かない');
  testReferenceCancelKeepsCompletion();
  console.log('ok - 資料の取消は終わりの待ちを残し、一時停止の取消は消す');
  console.log('5 tests passed');
})().catch((error) => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
