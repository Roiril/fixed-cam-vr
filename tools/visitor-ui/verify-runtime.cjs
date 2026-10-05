'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..', '..');
const webRoot = path.join(root, 'tablet', 'app', 'src', 'main', 'assets', 'web');
const assetRoot = path.join(webRoot, 'asset');
const htmlPath = path.join(webRoot, 'index.html');
const briefingPath = path.join(assetRoot, 'briefing-v1.json');
const transportPath = path.join(webRoot, 'tablet-transport.js');
const html = fs.readFileSync(htmlPath, 'utf8');
const briefing = JSON.parse(fs.readFileSync(briefingPath, 'utf8'));
const transportSource = fs.readFileSync(transportPath, 'utf8');

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
    let result;
    for (const listener of this.listeners.get(type) || []) result = listener(event);
    return result;
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
const navigations = [];
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
  location: { host: '127.0.0.1:8091', replace: (url) => navigations.push(url) },
  matchMedia: () => motionPreference,
  performance: { now: () => 1000 },
  sessionStorage: {
    getItem: (key) => storedSession.get(key) ?? null,
    setItem: (key, value) => storedSession.set(key, String(value)),
    removeItem: (key) => storedSession.delete(key),
  },
  setInterval: (callback, delay) => timers.setInterval(callback, delay),
  setTimeout: (callback, delay) => timers.setTimeout(callback, delay),
  window: { scrollX: 0, scrollY: 0, scrollTo() {} },
};
context.globalThis = context;

for (const match of html.matchAll(/<script\s+[^>]*src="([^"]+)"[^>]*><\/script>/gi)) {
  const scriptPath = path.resolve(webRoot, match[1]);
  vm.runInNewContext(fs.readFileSync(scriptPath, 'utf8'), context, { filename: scriptPath });
}
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
    confirmSettings,
    setSettingsStep,
    syncReliefPreview,
    checkStaffQuest,
    pulse,
    renderStaff,
    targetChangeLocked,
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

test('設定は言語から軽減と確認へ進み、戻る操作と三言語の案内を保つ', () => {
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
  assert.equal(elements.get('submitLabel').textContent, '設定を確認する');

  state.lang = 'en';
  runtime.setSettingsStep('language', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Please choose the language you would like to use.');
  runtime.setSettingsStep('relief', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Please choose whether you would like to reduce the horror.');
  assert.equal(elements.get('submitLabel').textContent, 'Review settings');

  state.lang = 'fr';
  runtime.setSettingsStep('language', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Choisissez la langue que vous souhaitez utiliser.');
  runtime.setSettingsStep('relief', false);
  assert.equal(elements.get('subtitleTyped').textContent, 'Choisissez si vous souhaitez atténuer l’horreur.');
  assert.equal(elements.get('submitLabel').textContent, 'Vérifier les réglages');
  elements.get('settingsBackBtn').dispatch('click');
  assert.equal(state.settingsStep, 'language');
});

test('軽減効果は未選択でも三言語で常に読める', () => {
  const expected = {
    ja: ['なし：通常の演出と音量', 'あり：音量半分と陽気な曲'],
    en: ['Off: normal effects and volume', 'On: half volume with a cheerful tune'],
    fr: ['Non : effets et volume normaux', 'Oui : volume réduit de moitié et musique gaie'],
  };
  state.view = 'edit';
  state.relief = false;
  for (const [lang, lines] of Object.entries(expected)) {
    state.lang = lang;
    runtime.setSettingsStep('relief', false);
    assert.equal(elements.get('reliefDesc').getAttribute('aria-hidden'), null);
    for (const line of lines) assert.ok(elements.get('reliefDesc').textContent.includes(line));
  }
  state.lang = 'ja';
});

test('確認画面の見出しと操作は三言語に揃う', () => {
  const copies = {
    ja: ['設定内容の確認', 'この設定で始める', '設定に戻る'],
    en: ['Review settings', 'Start with these settings', 'Back to settings'],
    fr: ['Vérification des réglages', 'Commencer avec ces réglages', 'Retour aux réglages'],
  };
  state.connection = 'online';
  state.status = clone(validStatus);
  for (const [lang, [heading, apply, edit]] of Object.entries(copies)) {
    state.view = 'edit';
    state.ui = 'idle';
    state.lang = lang;
    runtime.setSettingsStep('relief', false);
    assert.equal(runtime.confirmSettings(), true);
    assert.equal(elements.get('resultTitle').textContent, heading);
    assert.equal(elements.get('applyBtn').textContent, apply);
    assert.equal(elements.get('editBtn').textContent, edit);
    elements.get('editBtn').dispatch('click');
    assert.equal(state.view, 'edit');
  }
});

test('軽減音は軽減選択中だけ再生し、画面を離れると先頭へ戻る', async () => {
  assert.equal((html.match(/<audio\b/g) || []).length, 2);
  assert.match(html, /<audio[^>]*id="reliefPreview"[^>]*src="\.\/asset\/bed-relief-tablet-v1\.mp3"[^>]*\bloop\b/);
  assert.ok(fs.existsSync(path.join(assetRoot, 'bed-relief-tablet-v1.mp3')));
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
  assert.equal(preview.paused, true);
  assert.equal(preview.currentTime, 0);
  runtime.setSettingsStep('relief', false);
  assert.equal(preview.playCount, 2);

  elements.get('staffToggle').dispatch('click');
  assert.equal(preview.paused, true);
  elements.get('staff').close();
  assert.equal(preview.playCount, 3);
  assert.equal(preview.paused, false);

  document.hidden = true;
  dispatchDocument('visibilitychange');
  assert.equal(preview.paused, true);
  document.hidden = false;
  dispatchDocument('visibilitychange');
  assert.equal(preview.playCount, 4);

  context.__tabletLifecycle(true);
  assert.equal(preview.paused, true);
  context.__tabletLifecycle(false);
  assert.equal(preview.playCount, 5);
  assert.equal(preview.paused, false);

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

test('軽減音は反映確認・送信・博士説明では再生せず音量も変えない', () => {
  const preview = runtime.getReliefPreview();
  state.view = 'edit';
  state.relief = false;
  state.reliefPreviewStarted = false;
  document.hidden = false;
  elements.get('staff').open = false;
  runtime.setSettingsStep('relief', false);
  elements.get('settingsForm').dispatch('change', { target: radioElements.get('relief:on') });
  assert.equal(preview.paused, false);
  elements.get('settingsForm').dispatch('submit');
  assert.equal(state.view, 'result');
  assert.equal(state.ui, 'confirm');
  assert.equal(preview.paused, true);
  assert.equal(preview.currentTime, 0);

  const data = clone(briefing);
  delete data.scenes[0].video;
  data.scenes[0].audio = { ja: 'introduction-ja-v1.mp3' };
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[0]);
  runtime.beginBriefingCue();
  const doctorAudio = runtime.getBriefingMedia();
  assert.equal(preview.paused, true);
  assert.equal(preview.volume, 0.28);
  doctorAudio.onplaying();
  assert.equal(preview.volume, 0.28);
  runtime.stopBriefingMediaWindow();
  assert.equal(preview.volume, 0.28);
  assert.equal(preview.paused, true);

  runtime.finishBriefing();
  assert.equal(state.briefing.ended, true);
  assert.equal(preview.paused, true);
  assert.equal(preview.currentTime, 0);

  elements.get('briefingReplay').dispatch('click');
  assert.equal(state.briefing.ended, false);
  assert.equal(preview.paused, true);
  state.view = 'title';
  runtime.syncReliefPreview();
  assert.equal(preview.paused, true);
  state.relief = false;
  state.reliefPreviewStarted = false;
});

test('確認画面はPOSTせず、900msの送信表示後に受理番号と実値で反映を判定する', async () => {
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
  elements.get('settingsForm').dispatch('submit');
  assert.equal(state.ui, 'confirm');
  assert.equal(state.view, 'result');
  assert.equal(requests.length, 0, '確認画面へ進むだけでは通信しない');
  assert.equal(elements.get('resultTitle').textContent, 'Review settings');
  assert.equal(elements.get('selectedLanguage').textContent, 'English');
  assert.equal(elements.get('selectedRelief').textContent, 'on');
  assert.equal(elements.get('applyBtn').hidden, false);
  assert.equal(elements.get('editBtn').hidden, false);
  assert.equal(preview.paused, true);

  elements.get('editBtn').dispatch('click');
  assert.equal(state.view, 'edit');
  assert.equal(state.settingsStep, 'language');
  runtime.setSettingsStep('relief', false);
  elements.get('settingsForm').dispatch('submit');
  assert.equal(requests.length, 0, '設定へ戻っても確認までは通信しない');

  const sending = elements.get('applyBtn').dispatch('click');
  await flushPromises();
  assert.equal(state.ui, 'sending');
  assert.equal(runtime.resultKey(), 'sending');
  assert.equal(elements.get('resultTitle').textContent, 'Applying settings');
  assert.equal(requests[0].resource, './set');
  assert.deepEqual(JSON.parse(requests[0].options.body), { lang: 'en', relief: true,
    tabletSessionId: runtime.tabletSessionId, portalSessionId: 'portal-a' });
  timers.runOne(900);
  await sending;
  assert.equal(JSON.stringify(state.sent), JSON.stringify({ lang: 'en', relief: true, seq: 7,
    portalSessionId: 'portal-a', targetId: null }));
  assert.equal(runtime.resultKey(), 'waiting');

  state.status = { ...validStatus, lang: 'en', relief: true, appliedSeq: 6, lastRequest };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lang: 'ja', appliedSeq: 7 };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lang: 'en', relief: false };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, relief: true };
  assert.equal(runtime.resultKey(), 'applied');
  state.status = { ...state.status, lastRequest: { ...lastRequest, lang: 'ja' } };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lastRequest: { ...lastRequest, relief: false } };
  assert.equal(runtime.resultKey(), 'waiting');
  state.status = { ...state.status, lastRequest };
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
  assert.equal(elements.get('resultContinueBtn').hidden, false);
  assert.equal(elements.get('headsetHeading').textContent, 'Settings confirmed on Quest');
});

test('確認前は現在値と未送信を示し反映後だけ明示ボタンで進める', () => {
  state.lang = 'ja';
  state.view = 'edit';
  state.ui = 'idle';
  state.sent = null;
  state.status = clone(validStatus);
  state.connection = 'online';
  runtime.setSettingsStep('relief', false);
  elements.get('settingsForm').dispatch('submit');
  assert.equal(elements.get('headsetHeading').textContent, '現在のクエスト');
  assert.match(elements.get('resultDetail').textContent, /まだクエストへ送信していません/);
  assert.equal(elements.get('resultContinueBtn').hidden, true);

  state.sent = { lang: 'ja', relief: false, seq: 12, portalSessionId: 'portal-a', targetId: null };
  state.status = { ...validStatus, appliedSeq: 12,
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 12, lang: 'ja', relief: false } };
  state.ui = 'sent';
  runtime.poll();
  assert.equal(runtime.resultKey(), 'applied');
  assert.equal(elements.get('resultContinueBtn').textContent, '画面をタップして次へ');
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
  state.sent = { lang: 'ja', relief: false, seq: 7, portalSessionId: 'portal-a', targetId: null };
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
  state.sent = { lang: 'ja', relief: false, seq: 9, portalSessionId: 'portal-a', targetId: null };
  state.briefing.sceneIndex = 1;
  state.briefing.cueIndex = 2;
  runtime.saveBriefingProgress();
  const progress = JSON.parse(storedSession.get('mawarimi.visitor.progress.v1'));
  const matching = { ...validStatus, appliedSeq: 9,
    pending: { lang: 'ja', relief: false, seq: 9 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 9, lang: 'ja', relief: false } };
  assert.equal(runtime.savedProgressMatches(progress, matching), true);
  assert.equal(runtime.savedProgressMatches({ ...progress, targetId: 'beta' }, matching), false);
  assert.equal(runtime.savedProgressMatches(progress, { ...matching, pending: null }), false);
  assert.equal(runtime.savedProgressMatches(progress, { ...matching,
    lastRequest: { ...matching.lastRequest, tabletSessionId: 'another-page' } }), false);
  assert.equal(runtime.savedProgressMatches(progress, { ...matching, appliedSeq: 10 }), false);
});

test('説明を復元した後も操作ボタンと案内を選択した言語に揃える', () => {
  prepareBriefing(clone(briefing));
  const progress = { tabletSessionId: runtime.tabletSessionId, targetId: null, portalSessionId: 'portal-a',
    seq: 11, lang: 'en', relief: true, sceneIndex: 0, cueIndex: 1, ended: false };
  state.status = { ...validStatus, lang: 'en', relief: true, appliedSeq: 11,
    pending: { lang: 'en', relief: true, seq: 11 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 11, lang: 'en', relief: true } };
  state.briefing.loadState = 'ready';
  assert.equal(runtime.restoreProgress(progress), true);
  assert.equal(document.documentElement.lang, 'en');
  assert.equal(elements.get('briefingNext').textContent, 'Next sentence');
  assert.equal(elements.get('briefingSettings').textContent, 'Back to settings');
  assert.equal(elements.get('staffLabel').textContent, 'Staff settings');
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
    for (const filename of [...Object.values(scene.audio || {}), ...Object.values(scene.video || {})]) {
      assert.ok(fs.existsSync(path.join(assetRoot, filename)));
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

test('ユーザー指定のEleven v4入力は一つのコードブロックで保存する', () => {
  const input = fs.readFileSync(path.join(root, 'tools', 'visitor-ui', 'elevenlabs-input-ja.md'), 'utf8');
  const blocks = [...input.matchAll(/```text\r?\n([\s\S]*?)\r?\n```/g)];
  assert.equal(blocks.length, 1);
  const prompt = blocks[0][1];
  for (const phrase of ['[deep, resonant male voice', 'はじめまして。', '呪われたオブジェクト',
    'こちらの、カーテンです。', '決して、離さないでください。', 'エックスかワイを、一秒間', '健闘を、祈ります。']) {
    assert.ok(prompt.includes(phrase), phrase);
  }
});

test('日本語の原音声と4場面の博士動画を配信し、新しい台詞を参照する', () => {
  const expectedVideos = {
    introduction: 'introduction-ja-v1.mp4',
    subject: 'subject-ja-v1.mp4',
    report: 'report-ja-v1.mp4',
    wear: 'wear-ja-v1.mp4',
  };
  for (const scene of briefing.scenes) {
    const filename = `${scene.id}-ja-v2.mp3`;
    const asset = path.join(assetRoot, filename);
    const bytes = fs.readFileSync(asset);
    assert.ok(bytes.length > 100_000, asset);
    assert.equal(bytes.subarray(0, 3).toString('ascii'), 'ID3');
    assert.equal(scene.audio?.ja, undefined);
    assert.equal(scene.video?.ja, expectedVideos[scene.id]);
    const videoBytes = fs.readFileSync(path.join(assetRoot, scene.video.ja));
    assert.ok(videoBytes.length > 1_000_000);
    assert.equal(videoBytes.subarray(4, 8).toString('ascii'), 'ftyp');
  }
  const introduction = briefing.scenes.find((scene) => scene.id === 'introduction');
  assert.equal(introduction.image, 'introduction-doctor-v1.jpg');
  assert.equal(introduction.doctorImage, 'introduction-doctor-v1.jpg');
  assert.equal(introduction.doctorAnchor, 50);
  const subject = briefing.scenes.find((scene) => scene.id === 'subject');
  const report = briefing.scenes.find((scene) => scene.id === 'report');
  const wear = briefing.scenes.find((scene) => scene.id === 'wear');
  for (const scene of [introduction, subject, report, wear]) {
    const imageBytes = fs.readFileSync(path.join(assetRoot, scene.doctorImage));
    assert.ok(imageBytes.length > 40_000);
    assert.deepEqual([...imageBytes.subarray(0, 2)], [0xff, 0xd8]);
    assert.equal(scene.doctorAnchor, 50);
  }
  assert.equal(wear.image, wear.doctorImage);
  assert.match(subject.cues.ja[2].text, /決して離さない/);
  assert.match(report.cues.ja[2].text, /1秒間/);
});

test('壁の動画は次の文から始まり、文送りでは再生位置を変えずにループする', () => {
  assert.match(html, /<video[^>]*id="briefingLoopVideo"[^>]*\bmuted\b[^>]*\bloop\b/);
  const data = clone(briefing);
  const scene = data.scenes[1];
  assert.ok(fs.existsSync(path.join(assetRoot, scene.loopVideo.file)));
  prepareBriefing(data);
  state.briefing.sceneIndex = 1;
  state.briefing.cueIndex = 0;
  runtime.configureBriefingMedia(scene);
  runtime.beginBriefingCue();
  const doctorVideo = elements.get('doctorVideo');
  assert.equal(runtime.getBriefingMedia(), doctorVideo);
  assert.match(doctorVideo.src, /subject-ja-v1\.mp4$/);
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
  delete data.scenes[0].video;
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

test('説明案内は常設され自動送りと音声なしを三言語で説明する', () => {
  const expected = {
    ja: ['画面をタップして次へ進みます。', '自動で次へ進みます。'],
    en: ['No voice audio is available.', 'The briefing will advance automatically.'],
    fr: ['Aucune voix n’est disponible.', 'Le passage à la suite est automatique.'],
  };
  const data = clone(briefing);
  delete data.scenes[0].video;
  for (const lang of ['ja', 'en', 'fr']) {
    prepareBriefing(data);
    state.lang = lang;
    runtime.beginBriefingCue();
    assert.equal(elements.get('mediaNotice').hidden, false);
    assert.match(elements.get('mediaMessage').textContent, new RegExp(expected[lang][0]));
    runtime.setBriefingAuto(true);
    assert.match(elements.get('mediaMessage').textContent, new RegExp(expected[lang][1]));
  }
  state.lang = 'ja';
});

test('章と文と全体の番号を区別し最後の博士章でも装着を現在扱いにしない', () => {
  const data = clone(briefing);
  prepareBriefing(data);
  state.briefing.sceneIndex = data.scenes.length - 1;
  state.briefing.cueIndex = 0;
  runtime.beginBriefingCue();
  assert.equal(elements.get('briefingChapterNumber').textContent, '04 / 04');
  assert.equal(elements.get('briefingCueNumber').textContent, '文 1 / 2');
  assert.match(elements.get('sceneCounter').textContent, /^説明 \d{2} \/ \d{2}$/);
  assert.equal(elements.get('journeyBriefing').getAttribute('aria-current'), 'step');
  assert.equal(elements.get('journeyWear').getAttribute('aria-current'), null);
  runtime.finishBriefing();
  assert.equal(elements.get('journeyWear').getAttribute('aria-current'), 'step');
});

test('観測装置の説明と報告操作で見出しと補足を文ごとに切り替える', () => {
  const data = clone(briefing);
  prepareBriefing(data);
  state.briefing.sceneIndex = data.scenes.findIndex(scene => scene.id === 'report');
  for (const lang of ['ja', 'en', 'fr']) {
    state.lang = lang;
    state.briefing.cueIndex = 0;
    runtime.beginBriefingCue();
    assert.match(elements.get('briefingTitle').textContent, /観測装置|Observation device|Dispositif d’observation/);
    assert.equal(context.document.title, `廻リ視 — ${elements.get('briefingTitle').textContent}`);
    state.briefing.cueIndex = 2;
    runtime.beginBriefingCue();
    assert.match(elements.get('briefingTitle').textContent, /X|Y/);
    assert.match(elements.get('briefingCaption').textContent, /1秒間|1 second|1 seconde/);
  }
  state.lang = 'ja';
});

test('章変更は動画を隠して静止画へ戻してから動画を解放する', () => {
  const data = clone(briefing);
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[1]);
  state.briefing.sceneIndex = 1;
  runtime.beginBriefingCue();
  const video = runtime.getBriefingMedia();
  video.onplaying();
  assert.equal(video.hidden, false);
  assert.equal(elements.get('doctorImage').hidden, true);
  const resets = [];
  const originalLoad = video.load;
  video.load = function () {
    resets.push({ videoHidden: this.hidden, stillHidden: elements.get('doctorImage').hidden });
    return originalLoad.call(this);
  };
  try {
    runtime.configureBriefingMedia(data.scenes[2]);
    assert.equal(resets.length, 2);
    for (const reset of resets) {
      assert.equal(reset.videoHidden, true);
      assert.equal(reset.stillHidden, false);
    }
  } finally {
    video.load = originalLoad;
  }
});

test('読み上げ途中のオート切替は音声と動画を止めず再生位置と文末判定を保つ', () => {
  for (const kind of ['video', 'audio']) {
    const data = clone(briefing);
    if (kind === 'audio') {
      delete data.scenes[1].video;
      data.scenes[1].audio = { ja: 'subject-ja-v2.mp3' };
    }
    prepareBriefing(data);
    state.briefing.sceneIndex = 1;
    state.briefing.cueIndex = 1;
    runtime.configureBriefingMedia(data.scenes[1]);
    runtime.beginBriefingCue();
    const media = runtime.getBriefingMedia();
    media.onplaying();
    const start = data.scenes[1].cues.ja[0].durationMs / 1000;
    media.currentTime = start + 1;
    const before = { time: media.currentTime, pauses: media.pauseCount, plays: media.playCount };
    elements.get('briefingToggle').dispatch('click');
    assert.equal(state.briefing.auto, true);
    assert.equal(media.currentTime, before.time, kind);
    assert.equal(media.pauseCount, before.pauses, kind);
    assert.equal(media.playCount, before.plays, kind);
    assert.equal(media.paused, false, kind);
    media.currentTime = start + data.scenes[1].cues.ja[1].durationMs / 1000;
    media.ontimeupdate();
    assert.equal(state.briefing.cueIndex, 2, kind);

    media.onplaying();
    media.currentTime += 1;
    const off = { time: media.currentTime, pauses: media.pauseCount, plays: media.playCount };
    elements.get('briefingToggle').dispatch('click');
    assert.equal(state.briefing.auto, false);
    assert.equal(media.currentTime, off.time, kind);
    assert.equal(media.pauseCount, off.pauses, kind);
    assert.equal(media.playCount, off.plays, kind);
    media.currentTime = data.scenes[1].cues.ja.slice(0, 3).reduce((sum, cue) => sum + cue.durationMs, 0) / 1000;
    media.ontimeupdate();
    assert.equal(state.briefing.cueIndex, 2, kind);
    assert.equal(media.paused, true, kind);
  }
});

test('媒体時計の文末判定で手動は止まり、オートは次の文へ進む', () => {
  const data = clone(briefing);
  delete data.scenes[0].video;
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

test('最後の音声が終わると装着案内を表示し、再説明はその下から始める', () => {
  const data = clone(briefing);
  const lastSceneIndex = data.scenes.length - 1;
  const lastScene = data.scenes[lastSceneIndex];
  delete lastScene.video;
  lastScene.audio = { ja: 'equipment-ja-v1.mp3' };
  prepareBriefing(data);
  state.relief = false;
  state.reliefPreviewStarted = false;
  state.briefing.sceneIndex = lastSceneIndex;
  state.briefing.cueIndex = lastScene.cues.ja.length - 1;
  runtime.configureBriefingMedia(lastScene);
  runtime.beginBriefingCue();
  assert.equal(elements.get('briefingReplay').hidden, true);
  assert.equal(elements.get('briefingNext').hidden, false);
  assert.equal(elements.get('briefingNext').textContent, '装着の案内へ');
  assert.equal(elements.get('equipmentGuide').hidden, true);
  const media = runtime.getBriefingMedia();
  media.onplaying();
  media.currentTime = lastScene.cues.ja.reduce((sum, cue) => sum + cue.durationMs, 0) / 1000;
  media.ontimeupdate();

  assert.equal(state.briefing.ended, true);
  assert.equal(state.briefing.sceneIndex, lastSceneIndex);
  assert.equal(state.briefing.cueIndex, lastScene.cues.ja.length - 1);
  assert.equal(elements.get('subtitle').hidden, true);
  assert.equal(elements.get('equipmentGuide').hidden, false);
  assert.equal(elements.get('briefingTitle').textContent, '装着の準備');
  assert.equal(elements.get('equipmentQuest').textContent, 'Meta Questを装着してください。');
  assert.equal(elements.get('equipmentHeadphones').textContent, 'ヘッドフォンを装着してください。');
  assert.equal(elements.get('equipmentController').textContent, '左手にコントローラーを持ってください。');
  assert.equal(elements.get('equipmentFollowup').textContent, '装着が終わったら、クエスト内に表示される指示に従ってください。');
  assert.equal(elements.get('briefingReplay').hidden, false);
  for (const id of ['briefingPrevious', 'briefingNext', 'briefingToggle', 'briefingMute', 'mediaNotice']) {
    assert.equal(elements.get(id).hidden, true);
  }
  assert.equal(elements.get('briefingSettings').hidden, false);
  assert.equal(media.paused, true);

  assert.equal(runtime.moveBriefing(-1, 'control'), false);
  elements.get('briefingReplay').dispatch('click');
  assert.equal(state.briefing.ended, false);
  assert.equal(state.briefing.sceneIndex, 0);
  assert.equal(state.briefing.cueIndex, 0);
  assert.equal(elements.get('equipmentGuide').hidden, true);
  assert.equal(elements.get('subtitle').hidden, false);
  assert.equal(selectorElements.get('.stage').classList.contains('is-equipment'), false);
  assert.equal(elements.get('subtitleTyped').textContent, data.scenes[0].cues.ja[0].text);
});

test('最終文の次へ操作も装着案内を開き、案内は三言語に揃う', () => {
  for (const [lang, title, leftHand] of [
    ['ja', '装着の準備', '左手'],
    ['en', 'Get ready to begin', 'left hand'],
    ['fr', 'Préparez votre équipement', 'main gauche'],
  ]) {
    const data = clone(briefing);
    prepareBriefing(data);
    state.lang = lang;
    state.briefing.sceneIndex = data.scenes.length - 1;
    state.briefing.cueIndex = data.scenes.at(-1).cues[lang].length - 1;
    runtime.beginBriefingCue();
    assert.equal(elements.get('briefingReplay').hidden, true);
    elements.get('briefingNext').dispatch('click');
    assert.equal(state.briefing.ended, true);
    assert.equal(elements.get('equipmentGuide').hidden, false);
    assert.equal(elements.get('briefingTitle').textContent, title);
    assert.ok(elements.get('equipmentController').textContent.includes(leftHand));
    assert.ok(elements.get('equipmentFollowup').textContent.includes('Quest') || lang === 'ja');
    assert.equal(runtime.moveBriefing(1, 'tap'), false);
  }
  state.lang = 'ja';
});

test('停止や一時停止後の古い再生失敗は現在の文を壊さない', async () => {
  const data = clone(briefing);
  delete data.scenes[0].video;
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

test('Native背景移行は博士媒体を止め、復帰後も同じ文で再生を待つ', () => {
  const data = clone(briefing);
  prepareBriefing(data);
  runtime.configureBriefingMedia(data.scenes[0]);
  const media = runtime.getBriefingMedia();
  media.readyState = 1;
  runtime.beginBriefingCue();
  media.currentTime = 1.25;
  runtime.setBriefingAuto(true);
  const playCount = media.playCount;
  const sceneIndex = state.briefing.sceneIndex;
  const cueIndex = state.briefing.cueIndex;
  const subtitle = elements.get('subtitleTyped').textContent;

  context.__tabletLifecycle(true);
  assert.equal(media.paused, true);
  assert.equal(state.briefing.mediaWindowActive, false);
  assert.equal(state.briefing.auto, false);
  assert.equal(state.briefing.pauseReasons.has('native'), true);
  assert.equal(state.briefing.sceneIndex, sceneIndex);
  assert.equal(state.briefing.cueIndex, cueIndex);
  assert.equal(media.currentTime, 1.25);

  context.__tabletLifecycle(false);
  assert.equal(state.briefing.pauseReasons.has('native'), false);
  assert.equal(state.briefing.sceneIndex, sceneIndex);
  assert.equal(state.briefing.cueIndex, cueIndex);
  assert.equal(elements.get('subtitleTyped').textContent, subtitle);
  assert.equal(state.briefing.mediaPaused, true);
  assert.equal(state.briefing.mediaWindowActive, false);
  assert.equal(media.playCount, playCount);
});

test('素早い文送り後の古いmetadata通知を無視する', () => {
  const data = clone(briefing);
  delete data.scenes[0].video;
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

function prepareStaff() {
  runtime.stopBriefingPlayback();
  timers.reset();
  state.view = 'title';
  state.ui = 'idle';
  state.lang = 'ja';
  state.sent = null;
  state.status = clone(validStatus);
  state.connection = 'online';
  state.staffQuest = null;
  state.staffChecking = false;
  state.staffPreview = false;
  state.clearing = false;
  state.clearReceipt = null;
  elements.get('staff').open = true;
  document.hidden = false;
  navigations.length = 0;
  runtime.renderStaff();
}

test('スタッフの接続確認は選択したβを読み、画面を移動せず通信先を切り替える', async () => {
  prepareStaff();
  state.staffQuest = 'beta';
  const requests = [];
  fetchHandler = async (resource, options) => {
    requests.push({ resource, method: options.method || 'GET' });
    return { ok: true, status: 200, json: async () => ({ ...clone(validStatus), ip: '192.168.10.32', port: 8090 }) };
  };
  await runtime.checkStaffQuest();
  assert.deepEqual(requests, [
    { resource: './status', method: 'GET' },
    { resource: './status', method: 'GET' },
  ]);
  assert.deepEqual(navigations, []);
  assert.match(elements.get('staffCheckMessage').textContent, /通信先を保存しました/);
  assert.equal(state.sent, null);
});

test('選択先の不通と別IPの応答では受付を切り替えず、古い成功も捨てる', async () => {
  prepareStaff();
  state.staffQuest = 'beta';
  fetchHandler = async () => { throw new TypeError('offline'); };
  await runtime.checkStaffQuest();
  assert.equal(navigations.length, 0);
  assert.match(elements.get('staffCheckMessage').textContent, /クエスト βに接続できません/);
  assert.equal(state.status.portalSessionId, 'portal-a');
  assert.equal(elements.get('staffDoneBtn').disabled, false);
  fetchHandler = async () => ({ ok: true, status: 200, json: async () => ({ ...clone(validStatus), ip: '192.168.10.31', port: 8090 }) });
  await runtime.checkStaffQuest();
  assert.equal(navigations.length, 0);
  assert.match(elements.get('staffCheckMessage').textContent, /192\.168\.10\.32:8090/);
  assert.match(elements.get('staffCheckMessage').textContent, /応答は192\.168\.10\.31:8090/);
  assert.match(elements.get('staffCheckMessage').textContent, /α／βラベル/);

  const old = deferred();
  fetchHandler = () => old.promise;
  const checking = runtime.checkStaffQuest();
  elements.get('staffClose').dispatch('click');
  old.resolve({ ok: true, status: 200, json: async () => ({ ...clone(validStatus), ip: '192.168.10.32', port: 8090 }) });
  await checking;
  assert.equal(navigations.length, 0);
  assert.equal(state.staffChecking, false);
});

test('体験者の設定がある間は対象を変えず、接続成功と受付待機を分ける', async () => {
  prepareStaff();
  state.status.pending = { lang: 'fr', relief: true, seq: 5 };
  runtime.renderStaff();
  assert.equal(elements.get('questAlpha').disabled, true);
  assert.equal(elements.get('questBeta').disabled, true);
  state.staffQuest = 'alpha';
  let requests = 0;
  fetchHandler = async () => { requests++; throw new Error('must not request a different Quest'); };
  await runtime.checkStaffQuest();
  assert.equal(requests, 0);
  state.status.phase = 'RUN';
  runtime.renderStaff();
  assert.match(elements.get('staffSummary').textContent, /応答を確認しました/);
  assert.match(elements.get('staffSummary').textContent, /体験中です/);
  assert.doesNotMatch(elements.get('staffSummary').textContent, /受付を始められます/);
});

test('取消の送信成功だけでは完了せず、未開始の設定が消えてから次の人へ戻す', async () => {
  prepareStaff();
  state.status.pending = { lang: 'en', relief: true, seq: 7 };
  let status = clone(state.status);
  const posts = [];
  fetchHandler = async (resource, options) => {
    if (options.method === 'POST') posts.push({ resource, body: JSON.parse(options.body) });
    return { ok: true, status: 200, json: async () => resource === './clear' ? { ok: true } : clone(status) };
  };
  await elements.get('clearBtn').dispatch('click');
  assert.deepEqual(posts, [{ resource: './clear', body: { tabletSessionId: runtime.tabletSessionId, portalSessionId: 'portal-a' } }]);
  assert.equal(state.clearing, true);
  assert.doesNotMatch(elements.get('staffMessage').textContent, /取り消しを確認しました/);
  status.pending = null;
  await runtime.poll();
  assert.equal(state.clearing, false);
  assert.equal(state.view, 'title');
  assert.match(elements.get('staffMessage').textContent, /取り消しを確認しました/);
  assert.equal(elements.get('staffPending').hidden, true);
});

test('スタッフ確認モードはオフラインで全4場面を開き、通信せずタイトルへ戻る', async () => {
  prepareStaff();
  state.connection = 'offline';
  state.status = null;
  const requests = [];
  fetchHandler = async (resource) => {
    requests.push(resource);
    throw new Error('staff preview must remain offline');
  };
  elements.get('staffBriefingBtn').dispatch('click');
  assert.equal(state.view, 'briefing');
  assert.equal(state.staffPreview, true);
  assert.equal(state.sent, null);
  assert.equal(elements.get('briefingMode').hidden, false);
  assert.match(html, /id="briefingMode"[^>]*>スタッフ確認モード/);
  assert.equal(state.briefing.data.scenes.length, 4);
  assert.equal(state.briefing.pauseReasons.has('offline'), false);
  await runtime.pulse();
  assert.deepEqual(requests, []);
  elements.get('briefingSettings').dispatch('click');
  assert.equal(state.view, 'title');
  assert.equal(state.staffPreview, false);
});

test('来場者の設定または説明中はスタッフ確認で進行を捨てない', () => {
  prepareStaff();
  state.sent = { lang: 'ja', relief: false, seq: 4, portalSessionId: 'portal-a', targetId: null };
  state.briefing.loadState = 'ready';
  state.briefing.data = clone(briefing);
  runtime.renderStaff();
  assert.equal(elements.get('staffBriefingBtn').disabled, true);
  assert.match(elements.get('staffBriefingHelp').textContent, /進行中/);
  elements.get('staffBriefingBtn').dispatch('click');
  assert.equal(state.sent.seq, 4);
  assert.equal(state.staffPreview, false);

  state.sent = null;
  state.view = 'briefing';
  state.staffPreview = false;
  runtime.renderStaff();
  assert.equal(elements.get('staffBriefingBtn').disabled, true);
});

test('博士の音声確認は閉じる操作とタブ非表示で止まり、失敗時に案内する', async () => {
  prepareStaff();
  const sound = elements.get('staffSound');
  assert.match(html, /「廻リ視 博士」を開いたままにしてください/);
  assert.doesNotMatch(html, /受付のタブは1枚だけにしてください/);
  assert.ok(html.includes('id="staffSound" src="./asset/report-ja-v2.mp3"'));
  await elements.get('staffSoundBtn').dispatch('click');
  assert.equal(sound.paused, false);
  assert.equal(elements.get('staffSoundBtn').textContent, '博士の音声を止める');
  elements.get('staffDoneBtn').dispatch('click');
  assert.equal(sound.paused, true);
  assert.equal(sound.currentTime, 0);
  elements.get('staff').open = true;
  await elements.get('staffSoundBtn').dispatch('click');
  document.hidden = true;
  for (const listener of documentListeners.get('visibilitychange') || []) listener();
  assert.equal(sound.paused, true);
  sound.nextPlayPromise = Promise.reject(new Error('decode failed'));
  await elements.get('staffSoundBtn').dispatch('click');
  assert.match(elements.get('staffSoundHelp').textContent, /繰り返す場合はアプリを開き直してください/);
  document.hidden = false;
  sound.nextPlayPromise = null;
});

test('Native通信は対象と許可APIをJavaへ渡し、応答と取消を一度だけ処理する', async () => {
  const calls = [];
  let savedQuest = '';
  const nativeContext = {
    AbortController,
    TabletHost: {
      getQuest: () => savedQuest,
      setQuest: (quest) => { savedQuest = quest; },
      request: (...args) => calls.push(['request', ...args]),
      cancel: (id) => calls.push(['cancel', id]),
    },
    fetch: () => { throw new Error('browser fetch must not be used'); },
  };
  nativeContext.globalThis = nativeContext;
  vm.runInNewContext(transportSource, nativeContext, { filename: transportPath });
  const nativeTransport = nativeContext.TabletTransport;
  assert.equal(nativeTransport.native, true);
  assert.equal(nativeTransport.getQuest(), '');
  nativeTransport.setQuest('beta');
  assert.equal(nativeTransport.getQuest(), 'beta');

  const responsePromise = nativeTransport.request('./set', { method: 'POST', body: '{"lang":"ja"}' }, 'beta');
  assert.deepEqual(calls[0], ['request', 'tablet-1', 'beta', 'POST', '/set', '{"lang":"ja"}']);
  nativeContext.__tabletNativeResponse('tablet-1', { status: 200, body: '{"ok":true,"seq":4}' });
  const response = await responsePromise;
  assert.equal(response.ok, true);
  assert.equal(JSON.stringify(await response.json()), JSON.stringify({ ok: true, seq: 4 }));

  const controller = new AbortController();
  const cancelled = nativeTransport.request('./status', { signal: controller.signal }, 'alpha');
  controller.abort();
  await assert.rejects(cancelled, (error) => error.name === 'AbortError');
  assert.deepEqual(calls.at(-1), ['cancel', 'tablet-2']);
  nativeContext.__tabletNativeResponse('tablet-2', { status: 200, body: '{"ok":true}' });
  await assert.rejects(nativeTransport.request('./asset/briefing-v1.json', {}, 'alpha'), /not allowed/);
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
