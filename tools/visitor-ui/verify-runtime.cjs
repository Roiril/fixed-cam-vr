'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..', '..');
const htmlPath = path.join(root, 'Assets', 'Resources', 'Visitor', 'visitor.html');
const briefingPath = path.join(root, 'Assets', 'Resources', 'Visitor', 'briefing-v1.json.bytes');
const html = fs.readFileSync(htmlPath, 'utf8');
const briefing = JSON.parse(fs.readFileSync(briefingPath, 'utf8'));

class ClassList {
  constructor() {
    this.values = new Set();
  }

  add(...names) {
    names.forEach((name) => this.values.add(name));
  }

  remove(...names) {
    names.forEach((name) => this.values.delete(name));
  }

  toggle(name, force) {
    const enabled = force === undefined ? !this.values.has(name) : Boolean(force);
    if (enabled) this.values.add(name);
    else this.values.delete(name);
    return enabled;
  }

  contains(name) {
    return this.values.has(name);
  }
}

class MockElement {
  constructor(id = '', tagName = 'div') {
    this.id = id;
    this.tagName = tagName.toUpperCase();
    this.classList = new ClassList();
    this.style = { setProperty: (name, value) => { this.style[name] = value; } };
    this.dataset = {};
    this.attributes = new Map();
    this.listeners = new Map();
    this.hidden = false;
    this.inert = false;
    this.disabled = false;
    this.checked = false;
    this.open = false;
    this.textContent = '';
    this.value = '';
    this.name = '';
    this.offsetWidth = 100;
    this.complete = false;
    this.naturalWidth = 0;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  dispatch(type, properties = {}) {
    const event = {
      target: this,
      preventDefault() {},
      ...properties,
    };
    for (const listener of this.listeners.get(type) || []) listener(event);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.has(name) ? this.attributes.get(name) : null;
  }

  removeAttribute(name) {
    this.attributes.delete(name);
    if (name === 'src') this.src = '';
  }

  querySelectorAll(selector) {
    if (selector === 'input, button') return Array.from(elements.values()).filter((element) => (
      element.tagName === 'INPUT' || element.tagName === 'BUTTON'
    ));
    return [];
  }

  closest() {
    return null;
  }

  focus() {}

  showModal() {
    this.open = true;
  }

  close() {
    this.open = false;
    this.dispatch('close');
  }
}

class MockMedia extends MockElement {
  constructor(id = '', tagName = 'audio') {
    super(id, tagName);
    this.currentTime = 0;
    this.readyState = 1;
    this.muted = false;
    this.paused = true;
    this.pauseCount = 0;
    this.playCount = 0;
    this.loadCount = 0;
    this.nextPlayPromise = null;
  }

  pause() {
    this.pauseCount += 1;
    this.paused = true;
  }

  play() {
    this.playCount += 1;
    this.paused = false;
    const result = this.nextPlayPromise || Promise.resolve();
    this.nextPlayPromise = null;
    return result;
  }

  load() {
    this.loadCount += 1;
  }
}

class FakeTimers {
  constructor() {
    this.nextId = 1;
    this.tasks = new Map();
  }

  setTimeout(callback, delay = 0) {
    const id = this.nextId++;
    this.tasks.set(id, { callback, delay, interval: false });
    return id;
  }

  setInterval(callback, delay = 0) {
    const id = this.nextId++;
    this.tasks.set(id, { callback, delay, interval: true });
    return id;
  }

  clear(id) {
    this.tasks.delete(id);
  }

  runOne(delay) {
    const entry = Array.from(this.tasks.entries()).find(([, task]) => !task.interval && task.delay === delay);
    assert.ok(entry, `expected a ${delay}ms timer`);
    this.tasks.delete(entry[0]);
    entry[1].callback();
  }

  reset() {
    this.tasks.clear();
  }
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((onResolve, onReject) => {
    resolve = onResolve;
    reject = onReject;
  });
  return { promise, resolve, reject };
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

const elements = new Map();
for (const match of html.matchAll(/<([a-z][a-z0-9-]*)\b[^>]*\bid="([^"]+)"[^>]*>/gi)) {
  const [, tagName, id] = match;
  elements.set(id, ['audio', 'video'].includes(tagName.toLowerCase())
    ? new MockMedia(id, tagName)
    : new MockElement(id, tagName));
}

const radioElements = new Map();
for (const match of html.matchAll(/<input\b([^>]*)>/gi)) {
  const attributes = match[1];
  const id = /\bid="([^"]+)"/.exec(attributes)?.[1];
  const element = id && elements.has(id) ? elements.get(id) : new MockElement(id || '', 'input');
  element.name = /\bname="([^"]+)"/.exec(attributes)?.[1] || '';
  element.value = /\bvalue="([^"]+)"/.exec(attributes)?.[1] || '';
  radioElements.set(`${element.name}:${element.value}`, element);
}

const selectorElements = new Map([
  ['.stage', new MockElement('', 'main')],
  ['.content', new MockElement('', 'div')],
  ['.portrait', new MockElement('', 'div')],
  ['.footer', new MockElement('', 'footer')],
  ['.topbar', new MockElement('', 'header')],
  ['.skip', new MockElement('', 'button')],
  ['.briefing-visual', new MockElement('', 'div')],
]);

const documentListeners = new Map();
const document = {
  hidden: false,
  title: '',
  documentElement: new MockElement('', 'html'),
  getElementById(id) {
    assert.ok(elements.has(id), `missing mock for #${id}`);
    return elements.get(id);
  },
  querySelector(selector) {
    const radio = /^input\[name="(lang|relief)"\]\[value="([^"]+)"\]$/.exec(selector);
    if (radio) {
      return radioElements.get(`${radio[1]}:${radio[2]}`);
    }
    assert.ok(selectorElements.has(selector), `missing mock for ${selector}`);
    return selectorElements.get(selector);
  },
  addEventListener(type, listener) {
    const listeners = documentListeners.get(type) || [];
    listeners.push(listener);
    documentListeners.set(type, listeners);
  },
};

const timers = new FakeTimers();
const createdAudio = [];
const warnings = [];
const storedSession = new Map();
const motionPreference = { matches: true, addEventListener() {} };
const validStatus = {
  ok: true,
  lang: 'ja',
  relief: false,
  phase: 'INTRO',
  titleStage: 'Wait',
  appliedSeq: 0,
  applyCount: 0,
  received: 0,
  pending: null,
  portalSessionId: 'portal-a',
  lastRequest: null,
};
let fetchHandler = async (resource) => ({
  ok: true,
  status: 200,
  json: async () => resource === './asset/briefing-v1.json' ? clone(briefing) : clone(validStatus),
});

const context = {
  AbortController,
  Audio: function Audio() {
    const media = new MockMedia('', 'audio');
    createdAudio.push(media);
    return media;
  },
  Element: MockElement,
  Image: class Image extends MockElement {},
  URLSearchParams,
  clearInterval: (id) => timers.clear(id),
  clearTimeout: (id) => timers.clear(id),
  console: { warn: (...args) => warnings.push(args), log() {}, error: console.error },
  document,
  encodeURIComponent,
  fetch: (...args) => fetchHandler(...args),
  location: { host: '127.0.0.1:8091' },
  matchMedia: () => motionPreference,
  performance: { now: () => 1000 },
  sessionStorage: {
    getItem: (key) => storedSession.get(key) ?? null,
    setItem: (key, value) => storedSession.set(key, String(value)),
    removeItem: (key) => storedSession.delete(key),
  },
  setInterval: (callback, delay) => timers.setInterval(callback, delay),
  setTimeout: (callback, delay) => timers.setTimeout(callback, delay),
  window: { scrollX: 0, scrollY: 0 },
};
context.globalThis = context;

const scripts = Array.from(html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi), (match) => match[1]);
const source = scripts.find((script) => script.includes('(() => {') && script.includes('validateBriefingData'));
assert.ok(source, 'visitor runtime script was not found');
const close = source.lastIndexOf('})();');
assert.notEqual(close, -1, 'visitor runtime IIFE terminator was not found');
const exportsSource = `
  globalThis.__visitorTest = {
    state,
    tabletSessionId,
    validateBriefingData,
    resultKey,
    send,
    poll,
    changeVisual,
    cancelVisualChange,
    configureBriefingMedia,
    startBriefingMediaWindow,
    stopBriefingMediaWindow,
    stopBriefingPlayback,
    setBriefingAuto,
    setBriefingPause,
    beginBriefingCue,
    moveBriefing,
    finishBriefing,
    saveBriefingProgress,
    savedProgressMatches,
    restoreProgress: (progress) => { restoreCandidate = progress; state.restored = false; return tryRestoreBriefing(); },
    wasChangedByAnotherPage,
    showPageConflict,
    setSettingsStep,
    syncReliefPreview,
    getReliefPreview: () => reliefPreview,
    getBriefingMedia: () => briefingMedia,
  };
`;
vm.runInNewContext(source.slice(0, close) + exportsSource + source.slice(close), context, {
  filename: htmlPath,
});

const runtime = context.__visitorTest;
const state = runtime.state;

async function flushPromises() {
  await Promise.resolve();
  await Promise.resolve();
  await new Promise((resolve) => setImmediate(resolve));
}

function prepareBriefing(data) {
  runtime.stopBriefingPlayback();
  timers.reset();
  state.lang = 'ja';
  state.view = 'briefing';
  state.connection = 'online';
  state.briefing.data = data;
  state.briefing.loadState = 'ready';
  state.briefing.sceneIndex = 0;
  state.briefing.cueIndex = 0;
  state.briefing.pauseReasons.clear();
  state.briefing.auto = false;
  state.briefing.mediaFailed = false;
  state.briefing.doctorFailed = false;
  elements.get('doctorImage').hidden = false;
  elements.get('doctorVideo').hidden = true;
}

const tests = [];
function test(name, body) {
  tests.push({ name, body });
}

function dispatchDocument(type, properties = {}) {
  const event = { target: document, preventDefault() {}, ...properties };
  for (const listener of documentListeners.get(type) || []) listener(event);
}

test('タイトルの操作内でナビゲーションを隠す全画面化を要求する', () => {
  let options = null;
  document.fullscreenEnabled = true;
  document.fullscreenElement = null;
  document.documentElement.requestFullscreen = (value) => {
    options = value;
    return Promise.resolve();
  };
  state.view = 'title';
  state.titleTransitioning = false;
  elements.get('titleView').hidden = false;
  elements.get('titleStart').dispatch('click');
  assert.equal(options?.navigationUI, 'hide');
  assert.equal(state.view, 'edit');
  document.fullscreenEnabled = false;
  delete document.documentElement.requestFullscreen;
});

test('設定は言語から軽減へ進み、戻る操作と三言語の案内を保つ', () => {
  state.view = 'edit';
  state.lang = 'ja';
  runtime.setSettingsStep('language', false);
  assert.equal(elements.get('subtitleTyped').textContent, '使う言語を教えてください。');
  assert.equal(elements.get('languageStep').hidden, false);
  assert.equal(elements.get('reliefStep').hidden, true);
  assert.equal(elements.get('settingsNextBtn').hidden, false);
  assert.equal(elements.get('sendBtn').hidden, true);

  elements.get('settingsNextBtn').dispatch('click');
  assert.equal(state.settingsStep, 'relief');
  assert.equal(elements.get('subtitleTyped').textContent, '恐怖を軽減するかどうかを決めてください。');
  assert.equal(elements.get('languageStep').hidden, true);
  assert.equal(elements.get('reliefStep').hidden, false);
  assert.equal(elements.get('settingsBackBtn').hidden, false);
  assert.equal(elements.get('sendBtn').hidden, false);

  state.lang = 'en';
  runtime.setSettingsStep('language', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Please choose the language you would like to use.');
  runtime.setSettingsStep('relief', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Please choose whether you would like to reduce the horror.');

  state.lang = 'fr';
  runtime.setSettingsStep('language', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Choisissez la langue que vous souhaitez utiliser.');
  runtime.setSettingsStep('relief', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Choisissez si vous souhaitez atténuer l’horreur.');
  elements.get('settingsBackBtn').dispatch('click');
  assert.equal(state.settingsStep, 'language');
});

test('軽減音は選択後に同じ要素で続き、遮蔽と解除では先頭へ戻る', async () => {
  assert.equal((html.match(/<audio\b/g) || []).length, 1);
  assert.match(html, /<audio[^>]*id="reliefPreview"[^>]*src="\.\/asset\/bed-relief-tablet-v1\.mp3"[^>]*\bloop\b/);
  assert.ok(fs.existsSync(path.join(root, 'Assets', 'Resources', 'Visitor', 'bed-relief-tablet-v1.mp3.bytes')));
  const preview = runtime.getReliefPreview();
  assert.equal(preview.volume, 0.28);
  state.view = 'edit';
  state.lang = 'ja';
  state.relief = false;
  document.hidden = false;
  elements.get('staff').open = false;
  runtime.setSettingsStep('relief', false);
  const on = radioElements.get('relief:on');
  const off = radioElements.get('relief:off');

  elements.get('settingsForm').dispatch('change', { target: on });
  assert.equal(state.relief, true);
  assert.equal(preview.playCount, 1);
  assert.equal(preview.paused, false);

  preview.currentTime = 3.5;
  elements.get('settingsBackBtn').dispatch('click');
  assert.equal(preview.paused, false);
  assert.equal(preview.currentTime, 3.5);
  runtime.setSettingsStep('relief', false);
  assert.equal(preview.playCount, 1);

  elements.get('staffToggle').dispatch('click');
  assert.equal(preview.paused, true);
  elements.get('staff').close();
  assert.equal(preview.playCount, 2);
  assert.equal(preview.paused, false);

  document.hidden = true;
  dispatchDocument('visibilitychange');
  assert.equal(preview.paused, true);
  document.hidden = false;
  dispatchDocument('visibilitychange');
  assert.equal(preview.playCount, 3);

  elements.get('settingsForm').dispatch('change', { target: off });
  assert.equal(state.relief, false);
  assert.equal(preview.paused, true);

  const warningCount = warnings.length;
  preview.nextPlayPromise = Promise.reject(Object.assign(new Error('gesture required'), { name: 'NotAllowedError' }));
  elements.get('settingsForm').dispatch('change', { target: on });
  await flushPromises();
  assert.equal(preview.paused, true);
  assert.equal(warnings.length, warningCount);
  elements.get('settingsForm').dispatch('change', { target: off });
});

test('軽減音は反映確認と博士説明まで続き、音声中だけ下がって説明終了で止まる', () => {
  const preview = runtime.getReliefPreview();
  state.view = 'edit';
  state.relief = false;
  state.reliefPreviewStarted = false;
  document.hidden = false;
  elements.get('staff').open = false;
  runtime.setSettingsStep('relief', false);
  elements.get('settingsForm').dispatch('change', { target: radioElements.get('relief:on') });
  assert.equal(preview.paused, false);
  const playCount = preview.playCount;

  state.view = 'result';
  runtime.syncReliefPreview();
  assert.equal(preview.paused, false);
  assert.equal(preview.playCount, playCount);

  const data = clone(briefing);
  data.scenes[0].audio = { ja: 'introduction-ja-v1.mp3' };
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[0]);
  runtime.beginBriefingCue();
  const doctorAudio = runtime.getBriefingMedia();
  assert.equal(preview.paused, false);
  assert.equal(preview.volume, 0.28);
  doctorAudio.onplaying();
  assert.equal(preview.volume, 0.08);
  runtime.stopBriefingMediaWindow();
  assert.equal(preview.volume, 0.28);
  assert.equal(preview.paused, false);

  runtime.finishBriefing();
  assert.equal(state.briefing.ended, true);
  assert.equal(preview.paused, true);
  assert.equal(preview.currentTime, 0);

  elements.get('briefingReplay').dispatch('click');
  assert.equal(state.briefing.ended, false);
  assert.equal(preview.paused, false);
  state.view = 'title';
  runtime.syncReliefPreview();
  assert.equal(preview.paused, true);
  state.relief = false;
  state.reliefPreviewStarted = false;
});

test('反映判定はPOSTの受理番号と実値の両方を見る', async () => {
  const requests = [];
  const lastRequest = { tabletSessionId: runtime.tabletSessionId, seq: 7, lang: 'en', relief: true };
  let status = { ...validStatus, lang: 'ja', relief: false, appliedSeq: 7, received: 1, lastRequest };
  fetchHandler = async (resource, options = {}) => {
    requests.push({ resource, options });
    return {
      ok: true,
      status: 200,
      json: async () => resource === './set' ? { ok: true, seq: 7 } : clone(status),
    };
  };
  state.lang = 'en';
  state.relief = true;
  state.ui = 'idle';
  state.view = 'edit';
  state.status = clone(validStatus);
  state.connection = 'online';
  runtime.setSettingsStep('relief', false);
  runtime.syncReliefPreview();
  const preview = runtime.getReliefPreview();
  assert.equal(preview.paused, false);
  await runtime.send();
  assert.equal(preview.paused, false);
  assert.equal(requests[0].resource, './set');
  assert.deepEqual(JSON.parse(requests[0].options.body), { lang: 'en', relief: true,
    tabletSessionId: runtime.tabletSessionId, portalSessionId: 'portal-a' });
  assert.equal(JSON.stringify(state.sent), JSON.stringify({ lang: 'en', relief: true, seq: 7, portalSessionId: 'portal-a' }));
  assert.equal(runtime.resultKey(), 'waiting');

  state.status = { ...validStatus, lang: 'en', relief: true, appliedSeq: 6, lastRequest };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lang: 'ja', appliedSeq: 7 };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lang: 'en', relief: false };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, relief: true };
  assert.equal(runtime.resultKey(), 'applied');
  state.status = { ...state.status, appliedSeq: 8 };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, appliedSeq: 7, lastRequest: { ...lastRequest, tabletSessionId: 'other' } };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lastRequest, portalSessionId: 'portal-b' };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, portalSessionId: 'portal-a' };

  status = { ...validStatus, lang: 'en', relief: true, appliedSeq: 7, received: 1, lastRequest };
  await runtime.poll();
  assert.equal(runtime.resultKey(), 'applied');
});

test('Questの起動IDが変わると送信中の古い応答を捨てる', async () => {
  await flushPromises();
  const delayedSet = deferred();
  const requests = [];
  fetchHandler = async (resource) => {
    requests.push(resource);
    return {
      ok: true,
      status: 200,
      json: async () => resource === './set' ? delayedSet.promise
        : { ...validStatus, portalSessionId: 'portal-b', received: 2 },
    };
  };
  state.status = clone(validStatus);
  state.connection = 'online';
  state.ui = 'idle';
  state.view = 'edit';
  state.lang = 'fr';
  state.relief = true;
  const sending = runtime.send();
  await runtime.poll();
  await runtime.poll();
  assert.ok(requests.includes('./status'), JSON.stringify(requests));
  assert.equal(state.connection, 'online');
  delayedSet.resolve({ ok: true, seq: 8 });
  await sending;
  assert.equal(state.status?.portalSessionId, 'portal-b');
  assert.equal(state.sent, null);
  assert.equal(state.view, 'edit');
  assert.equal(state.lang, 'fr');
  assert.equal(state.relief, true);
});

test('別ページの新しい受理番号は説明と媒体を止めて結果画面へ戻す', () => {
  prepareBriefing(clone(briefing));
  state.sent = { lang: 'ja', relief: false, seq: 7, portalSessionId: 'portal-a' };
  state.status = { ...validStatus, appliedSeq: 7,
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 7, lang: 'ja', relief: false } };
  assert.equal(runtime.wasChangedByAnotherPage({ ...state.status,
    lastRequest: { tabletSessionId: 'another-page', seq: 8, lang: 'en', relief: true } }), true);
  assert.equal(runtime.wasChangedByAnotherPage({ ...state.status, pending: null, titleStage: 'In' }), false,
    '予約消費は別ページ変更ではない');
  runtime.configureBriefingMedia(briefing.scenes[0]);
  runtime.beginBriefingCue();
  const media = runtime.getBriefingMedia();
  runtime.showPageConflict();
  assert.equal(state.view, 'result');
  assert.equal(state.ui, 'conflict');
  assert.equal(media?.paused ?? true, true);
});

test('説明位置の保存は起動ID・ページID・受理番号・実値・未消費予約の一致を要求する', () => {
  prepareBriefing(clone(briefing));
  state.sent = { lang: 'ja', relief: false, seq: 9, portalSessionId: 'portal-a' };
  state.briefing.sceneIndex = 1;
  state.briefing.cueIndex = 2;
  runtime.saveBriefingProgress();
  const progress = JSON.parse(storedSession.get('mawarimi.visitor.progress.v1'));
  const matching = { ...validStatus, appliedSeq: 9,
    pending: { lang: 'ja', relief: false, seq: 9 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 9, lang: 'ja', relief: false } };
  assert.equal(runtime.savedProgressMatches(progress, matching), true);
  assert.equal(runtime.savedProgressMatches(progress, { ...matching, pending: null }), false);
  assert.equal(runtime.savedProgressMatches(progress, { ...matching,
    lastRequest: { ...matching.lastRequest, tabletSessionId: 'another-page' } }), false);
  assert.equal(runtime.savedProgressMatches(progress, { ...matching, appliedSeq: 10 }), false);
});

test('説明を復元した後も操作ボタンと案内を選択した言語に揃える', () => {
  prepareBriefing(clone(briefing));
  const progress = { tabletSessionId: runtime.tabletSessionId, portalSessionId: 'portal-a',
    seq: 11, lang: 'en', relief: true, sceneIndex: 0, cueIndex: 1, ended: false };
  state.status = { ...validStatus, lang: 'en', relief: true, appliedSeq: 11,
    pending: { lang: 'en', relief: true, seq: 11 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 11, lang: 'en', relief: true } };
  state.briefing.loadState = 'ready';
  assert.equal(runtime.restoreProgress(progress), true);
  assert.equal(document.documentElement.lang, 'en');
  assert.equal(elements.get('briefingNext').textContent, 'Next sentence');
  assert.equal(elements.get('briefingSettings').textContent, 'Back to settings');
  assert.equal(elements.get('staffLabel').textContent, 'Staff');
});

test('表示更新は同期し、重複アニメーションを取消して縮小設定に従う', () => {
  const content = selectorElements.get('.content');
  const portrait = selectorElements.get('.portrait');
  const animations = [];
  for (const element of [content, portrait]) {
    element.animate = (keyframes, options) => {
      const animation = {
        element,
        keyframes,
        options,
        cancelCount: 0,
        cancel() { this.cancelCount += 1; },
        finished: new Promise(() => {}),
      };
      animations.push(animation);
      return animation;
    };
  }

  motionPreference.matches = false;
  let updates = 0;
  assert.equal(runtime.changeVisual(() => { updates += 1; }), true);
  assert.equal(updates, 1);
  assert.equal(animations.length, 2);

  assert.equal(runtime.changeVisual(() => { updates += 1; }), true);
  assert.equal(updates, 2);
  assert.equal(animations.length, 4);
  assert.equal(animations[0].cancelCount, 1);
  assert.equal(animations[1].cancelCount, 1);
  runtime.cancelVisualChange();
  assert.equal(animations[2].cancelCount, 1);
  assert.equal(animations[3].cancelCount, 1);

  motionPreference.matches = true;
  assert.equal(runtime.changeVisual(() => { updates += 1; }), true);
  assert.equal(updates, 3);
  assert.equal(animations.length, 4);
  delete content.animate;
  delete portrait.animate;
});

test('本番JSONと表示指定の境界値を検証する', () => {
  assert.equal(runtime.validateBriefingData(clone(briefing)).scenes.length, briefing.scenes.length);
  for (const scene of briefing.scenes) {
    for (const filename of Object.values(scene.audio || {})) {
      assert.ok(fs.existsSync(path.join(root, 'Assets', 'Resources', 'Visitor', `${filename}.bytes`)));
    }
  }
  const presentation = clone(briefing);
  presentation.scenes[0].layout = 'doctor-evidence';
  presentation.scenes[0].doctorAnchor = 0;
  delete presentation.scenes[1].loopVideo;
  presentation.scenes[1].layout = 'doctor-center';
  presentation.scenes[1].doctorAnchor = 100;
  assert.doesNotThrow(() => runtime.validateBriefingData(presentation));

  for (const mutate of [
    (data) => { data.scenes[0].layout = 'center'; },
    (data) => { data.scenes[0].doctorImage = '../doctor.jpg'; },
    (data) => { data.scenes[0].doctorImage = ''; },
    (data) => { data.scenes[0].doctorAnchor = -1; },
    (data) => { data.scenes[0].doctorAnchor = 101; },
    (data) => { data.scenes[0].doctorAnchor = Number.NaN; },
  ]) {
    const invalid = clone(briefing);
    mutate(invalid);
    assert.throws(() => runtime.validateBriefingData(invalid), /invalid briefing presentation/);
  }
  for (const mutate of [
    (data) => { data.scenes[1].loopVideo.file = '../other.mp4'; },
    (data) => { data.scenes[1].loopVideo.startCue = 4; },
    (data) => { data.scenes[1].loopVideo.startCue = -1; },
  ]) {
    const invalid = clone(briefing);
    mutate(invalid);
    assert.throws(() => runtime.validateBriefingData(invalid), /invalid briefing loop video/);
  }
});

test('壁の動画は次の文から始まり、文送りでは再生位置を変えずにループする', () => {
  assert.match(html, /<video[^>]*id="briefingLoopVideo"[^>]*\bmuted\b[^>]*\bloop\b/);
  const data = clone(briefing);
  const scene = data.scenes[1];
  assert.ok(fs.existsSync(path.join(root, 'Assets', 'Resources', 'Visitor', `${scene.loopVideo.file}.bytes`)));
  prepareBriefing(data);
  state.briefing.sceneIndex = 1;
  state.briefing.cueIndex = 0;
  runtime.configureBriefingMedia(scene);
  runtime.beginBriefingCue();
  const video = elements.get('briefingLoopVideo');
  const before = video.playCount;
  assert.equal(video.hidden, true);

  runtime.moveBriefing(1, 'auto');
  assert.equal(state.briefing.cueIndex, 1);
  assert.equal(video.playCount, before + 1);
  video.onplaying();
  assert.equal(video.hidden, false);
  assert.equal(selectorElements.get('.briefing-visual').classList.contains('is-loop-playing'), true);
  video.currentTime = 4.2;
  const pauseCount = video.pauseCount;
  const loadCount = video.loadCount;

  runtime.moveBriefing(1, 'auto');
  runtime.moveBriefing(1, 'auto');
  assert.equal(state.briefing.cueIndex, 3);
  assert.equal(video.currentTime, 4.2);
  assert.equal(video.playCount, before + 1);
  assert.equal(video.pauseCount, pauseCount);
  assert.equal(video.loadCount, loadCount);

  runtime.moveBriefing(1, 'auto');
  assert.equal(state.briefing.sceneIndex, 2);
  assert.equal(video.paused, true);
  assert.equal(video.hidden, true);
  assert.equal(selectorElements.get('.briefing-visual').classList.contains('is-loop-playing'), false);
});

test('媒体未指定の場面ではaudioもvideoも作らない', () => {
  const data = clone(briefing);
  delete data.scenes[0].audio;
  prepareBriefing(data);
  const audioCount = createdAudio.length;
  const video = elements.get('doctorVideo');
  const loadCount = video.loadCount;
  runtime.configureBriefingMedia(data.scenes[0]);
  assert.equal(runtime.getBriefingMedia(), null);
  assert.equal(createdAudio.length, audioCount);
  assert.equal(video.loadCount, loadCount);
});

test('媒体時計の文末判定で手動は止まり、オートは次の文へ進む', () => {
  const data = clone(briefing);
  data.scenes[0].audio = { ja: 'introduction-ja-v1.mp3' };
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[0]);
  runtime.beginBriefingCue();
  const media = runtime.getBriefingMedia();
  media.onplaying();
  media.currentTime = data.scenes[0].cues.ja[0].durationMs / 1000 - 0.01;
  timers.runOne(100);
  assert.equal(state.briefing.cueIndex, 0);
  assert.equal(state.briefing.mediaWindowActive, false);

  runtime.setBriefingAuto(true, true);
  media.onplaying();
  media.currentTime = data.scenes[0].cues.ja[0].durationMs / 1000 - 0.01;
  timers.runOne(100);
  assert.equal(state.briefing.cueIndex, 1);
  assert.equal(elements.get('subtitleTyped').textContent, data.scenes[0].cues.ja[1].text);
});

test('最後の音声が終わっても最終画面を保ち、前の文へ戻れる', () => {
  const data = clone(briefing);
  const lastSceneIndex = data.scenes.length - 1;
  const lastScene = data.scenes[lastSceneIndex];
  lastScene.audio = { ja: 'equipment-ja-v1.mp3' };
  prepareBriefing(data);
  state.relief = false;
  state.reliefPreviewStarted = false;
  state.briefing.sceneIndex = lastSceneIndex;
  state.briefing.cueIndex = lastScene.cues.ja.length - 1;
  runtime.configureBriefingMedia(lastScene);
  runtime.beginBriefingCue();
  const finalText = lastScene.cues.ja.at(-1).text;
  const media = runtime.getBriefingMedia();
  media.onplaying();
  media.currentTime = lastScene.cues.ja.reduce((sum, cue) => sum + cue.durationMs, 0) / 1000;
  media.ontimeupdate();

  assert.equal(state.briefing.ended, true);
  assert.equal(state.briefing.sceneIndex, lastSceneIndex);
  assert.equal(state.briefing.cueIndex, lastScene.cues.ja.length - 1);
  assert.equal(elements.get('subtitleTyped').textContent, finalText);
  assert.equal(elements.get('briefingReplay').hidden, false);
  assert.equal(elements.get('briefingPrevious').disabled, false);

  assert.equal(runtime.moveBriefing(-1, 'control'), true);
  assert.equal(state.briefing.ended, false);
  assert.equal(state.briefing.cueIndex, lastScene.cues.ja.length - 2);
  assert.equal(elements.get('subtitleTyped').textContent, lastScene.cues.ja.at(-2).text);
});

test('停止や一時停止後の古い再生失敗は現在の文を壊さない', async () => {
  const data = clone(briefing);
  data.scenes[0].audio = { ja: 'introduction-ja-v1.mp3' };
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[0]);
  let media = runtime.getBriefingMedia();
  const pausedAttempt = deferred();
  media.nextPlayPromise = pausedAttempt.promise;
  runtime.beginBriefingCue();
  runtime.setBriefingPause('hidden', true);
  pausedAttempt.reject(new Error('late pause rejection'));
  await flushPromises();
  assert.equal(state.briefing.mediaFailed, false);
  assert.equal(state.briefing.cueIndex, 0);

  runtime.setBriefingPause('hidden', false);
  const stoppedAttempt = deferred();
  media.nextPlayPromise = stoppedAttempt.promise;
  runtime.startBriefingMediaWindow();
  runtime.stopBriefingPlayback();
  stoppedAttempt.reject(new Error('late stop rejection'));
  await flushPromises();
  assert.equal(state.briefing.mediaFailed, false);
  assert.equal(runtime.getBriefingMedia(), null);
});

test('素早い文送り後の古いmetadata通知を無視する', () => {
  const data = clone(briefing);
  data.scenes[0].audio = { ja: 'introduction-ja-v1.mp3' };
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[0]);
  const media = runtime.getBriefingMedia();
  media.readyState = 0;
  runtime.beginBriefingCue();
  const staleMetadata = media.onloadedmetadata;
  assert.equal(typeof staleMetadata, 'function');
  runtime.moveBriefing(1, 'auto');
  staleMetadata();
  assert.equal(state.briefing.cueIndex, 1);
  assert.equal(media.playCount, 0);
  assert.equal(media.currentTime, 0);
});

test('自動再生拒否後は同じ媒体をユーザー操作で再生する', async () => {
  const data = clone(briefing);
  data.scenes[0].video = { ja: 'introduction-ja-v1.mp4' };
  prepareBriefing(data);
  const video = elements.get('doctorVideo');
  video.readyState = 1;
  const blockedAttempt = deferred();
  video.nextPlayPromise = blockedAttempt.promise;
  runtime.configureBriefingMedia(data.scenes[0]);
  const source = video.src;
  const loadCount = video.loadCount;
  runtime.setBriefingAuto(true);
  runtime.beginBriefingCue();
  const notAllowed = new Error('user gesture required');
  notAllowed.name = 'NotAllowedError';
  blockedAttempt.reject(notAllowed);
  await flushPromises();
  assert.equal(runtime.getBriefingMedia(), video);
  assert.equal(state.briefing.mediaFailed, false);
  assert.equal(state.briefing.mediaPaused, true);
  assert.equal(state.briefing.auto, false);
  assert.equal(state.briefing.mediaWindowActive, false);
  assert.equal(video.src, source);
  assert.equal(video.loadCount, loadCount);

  const playCount = video.playCount;
  video.nextPlayPromise = Promise.resolve();
  elements.get('mediaRetry').dispatch('click');
  assert.equal(runtime.getBriefingMedia(), video);
  assert.equal(video.src, source);
  assert.equal(video.loadCount, loadCount);
  assert.equal(video.playCount, playCount + 1);
  assert.equal(state.briefing.mediaPaused, false);
  assert.equal(state.briefing.mediaWindowActive, true);
  await flushPromises();
  assert.equal(state.briefing.mediaFailed, false);
});

test('媒体失敗時は静止画と字幕を保ち、オート解除後に再試行できる', async () => {
  const data = clone(briefing);
  data.scenes[0].video = { ja: 'introduction-ja-v1.mp4' };
  prepareBriefing(data);
  const video = elements.get('doctorVideo');
  video.readyState = 1;
  const failedAttempt = deferred();
  video.nextPlayPromise = failedAttempt.promise;
  runtime.configureBriefingMedia(data.scenes[0]);
  runtime.setBriefingAuto(true);
  runtime.beginBriefingCue();
  const subtitle = data.scenes[0].cues.ja[0].text;
  failedAttempt.reject(new Error('decode failed'));
  await flushPromises();
  assert.equal(state.briefing.mediaFailed, true);
  assert.equal(state.briefing.auto, false);
  assert.equal(elements.get('doctorImage').hidden, false);
  assert.equal(video.hidden, true);
  assert.equal(elements.get('subtitleTyped').textContent, subtitle);

  const playCount = video.playCount;
  video.nextPlayPromise = Promise.resolve();
  elements.get('mediaRetry').dispatch('click');
  await flushPromises();
  assert.equal(state.briefing.mediaFailed, false);
  assert.equal(runtime.getBriefingMedia(), video);
  assert.equal(state.briefing.mediaWindowActive, true);
  assert.equal(video.playCount, playCount + 1);
  assert.equal(elements.get('subtitleTyped').textContent, subtitle);
});

(async () => {
  await flushPromises();
  timers.reset();
  let failed = 0;
  for (const { name, body } of tests) {
    try {
      await body();
      console.log(`ok - ${name}`);
    } catch (error) {
      failed += 1;
      console.error(`not ok - ${name}`);
      console.error(error.stack || error);
    }
  }
  runtime.stopBriefingPlayback();
  timers.reset();
  if (failed > 0) process.exitCode = 1;
  else console.log(`${tests.length} tests passed`);
})().catch((error) => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
