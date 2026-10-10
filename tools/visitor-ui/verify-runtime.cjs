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
    // 設定フォームの中にあるのは言語と軽減のラジオだけ。スタッフ画面のボタンまで巻き込まない。
    if (selector === 'input, button' && this.id === 'settingsForm') return Array.from(radioElements.values())
      .filter((element) => element.name === 'lang' || element.name === 'relief');
    if (selector === 'input, button') return Array.from(elements.values()).filter((element) => (
      element.tagName === 'INPUT' || element.tagName === 'BUTTON'
    ));
    if (selector === 'span' && this.id === 'staffReadinessSummary') {
      this.summarySpans ||= Array.from({ length: 6 }, () => new MockElement('', 'span'));
      return this.summarySpans;
    }
    return [];
  }

  appendChild(child) {
    child.parentNode = this;
    return child;
  }

  setPointerCapture(pointerId) {
    this.capturedPointer = pointerId;
  }

  getBoundingClientRect() {
    return { left: 0, top: 0, right: 100, bottom: 50, width: 100, height: 50 };
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
const windowListeners = new Map();
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
  questTick: 1,
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
  window: {
    scrollX: 0, scrollY: 0, scrollTo() {},
    addEventListener(type, listener) {
      const listeners = windowListeners.get(type) || [];
      listeners.push(listener);
      windowListeners.set(type, listeners);
    },
  },
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
    activeQuest: () => activeQuest,
    beginFromTitle,
    visitorSettingsAccess,
    renderTitleEntry,
    renderText,
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
    openBriefingControls,
    resumeBriefing,
    restartBriefingCue,
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
    questStatusFresh,
    renderConnection,
    renderResult,
    observeQuestTick,
    staffScreenState,
    staffSnapshot,
    staffDeviceRows,
    staffVolumeRow,
    staffConfirmItems,
    staffRunMeta,
    staffCycleStepFor,
    createStaffAlertState,
    updateStaffAlertState,
    staffAlertTarget,
    staffStartBlocker,
    staffAlertClosedByRun,
    staffAlertPreviewAllowed,
    observeStaffAlert,
    staffAlert,
    staffAlertAudio,
    unlockStaffAlertAudio,
    playStaffAlertSound,
    cancelStaffAlertSound,
    previewStaffAlertSound,
    staffResetAllowed,
    openStaffScreen,
    closeStaffScreen,
    observeStaffStage,
    observeQuestTrouble,
    renderCallStaff,
    staffAction: () => staffAction,
    staffConfirmationRequested: () => staffConfirmationRequested,
    resetStaffTimers: () => {
      legacyEndSince = 0;
      questTroubleSince = 0;
      lastStaffStage = '';
      staffResetRequest = null;
      staffConfirmationRequested = false;
      staffReceiptState = { text: '', until: 0 };
      staffHealth = { identity: '', observed: false, problem: false };
      staffMediaVolume = null;
    },
    setQuestTroubleSince: (value) => { questTroubleSince = value; },
    setLegacyEndSince: (value) => { legacyEndSince = value; },
    preparationAcknowledged,
    restorePreparationState,
    wearReadinessConfirmed,
    advanceToWearIfReady,
    syncWearReadiness,
    canShowEquipmentGuidance,
    visitorEquipmentStatus,
    renderBriefingScene,
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
// 起動直後のスタッフ画面。各テストが開閉する前に記録する。
const startupStaff = { open: elements.get('staff').open, state: elements.get('staff').dataset.state };

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
  state.lastReceived = null;
  state.appliedSeen = false;
  state.leftWaitAfterApplied = false;
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

function pendingDelays() {
  return Array.from(timers.tasks.values()).filter((task) => !task.interval).map((task) => task.delay);
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

test('新しい受付はリセット完了前の入口を閉じ、settingsと同じページの編集だけを許可する', async () => {
  state.connection = 'online';
  state.sent = null;
  state.view = 'title';
  state.titleTransitioning = false;
  elements.get('titleView').hidden = false;
  let status = { ...clone(validStatus), questTick: 201,
    staffSetup: { stage: 'reset', reason: '', positionConfirmed: true } };
  state.status = status;
  runtime.observeQuestTick(status);
  runtime.renderTitleEntry();
  assert.equal(runtime.visitorSettingsAccess().allowed, false);
  assert.equal(elements.get('titleStart').disabled, true);
  assert.equal(elements.get('titleStartMain').textContent, '準備中');
  assert.match(elements.get('titleStartAlt').textContent, /Preparing/);
  assert.match(elements.get('titleStartAlt').textContent, /Préparation/);
  elements.get('titleStart').dispatch('click');
  assert.equal(state.view, 'title');

  status = { ...status, questTick: 202, staffSetup: { stage: 'settings', reason: 'settings', positionConfirmed: true } };
  state.status = status;
  runtime.observeQuestTick(status);
  runtime.renderTitleEntry();
  assert.equal(elements.get('titleStart').disabled, false);
  elements.get('titleStart').dispatch('click');
  assert.equal(state.view, 'edit');

  state.settingsStep = 'relief';
  status = { ...status, questTick: 203, staffSetup: { stage: 'devices', reason: '', positionConfirmed: true } };
  state.status = status;
  runtime.observeQuestTick(status);
  assert.equal(runtime.confirmSettings(), false);
  await runtime.send();
  assert.equal(state.view, 'title');

  status = { ...status, questTick: 204, staffSetup: { stage: 'explanation', reason: 'explanation', positionConfirmed: true } };
  state.status = status;
  runtime.observeQuestTick(status);
  runtime.renderTitleEntry();
  assert.equal(elements.get('titleStartMain').textContent, 'スタッフの確認をお待ちください');
  assert.match(elements.get('titleStartAlt').textContent, /staff confirmation/);
  assert.match(elements.get('titleStartAlt').textContent, /confirmation du personnel/);
  state.sent = { lang: 'ja', relief: false, seq: 5, portalSessionId: status.portalSessionId,
    targetId: runtime.activeQuest() };
  assert.equal(runtime.visitorSettingsAccess().allowed, true);
  status = { ...status, questTick: 205, staffSetup: { stage: 'playing', reason: '', positionConfirmed: true } };
  state.status = status;
  runtime.observeQuestTick(status);
  assert.equal(runtime.visitorSettingsAccess().allowed, false);

  state.sent = null;
  state.status = clone(validStatus);
  assert.equal(runtime.visitorSettingsAccess().allowed, true, 'legacy status remains compatible');
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
    ja: ['設定内容の確認', 'この設定を送る', '設定に戻る'],
    en: ['Review settings', 'Send these settings', 'Back to settings'],
    fr: ['Vérification des réglages', 'Envoyer ces réglages', 'Retour aux réglages'],
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

  runtime.openStaffScreen('manual');
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

test('確認前は現在値と未送信を示し反映後だけ明示ボタンで進める', async () => {
  state.lang = 'ja';
  state.view = 'edit';
  state.ui = 'idle';
  state.sent = null;
  state.status = clone(validStatus);
  state.connection = 'online';
  runtime.setSettingsStep('relief', false);
  elements.get('settingsForm').dispatch('submit');
  assert.equal(elements.get('headsetHeading').textContent, '現在のクエスト');
  assert.equal(elements.get('resultDetail').textContent, '言語とホラー軽減を確認してください。クエストへは未送信です');
  assert.equal(elements.get('resultContinueBtn').hidden, true);

  state.sent = { lang: 'ja', relief: false, seq: 12, portalSessionId: 'portal-a', targetId: null };
  state.status = { ...validStatus, appliedSeq: 12,
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 12, lang: 'ja', relief: false } };
  state.ui = 'sent';
  runtime.renderResult();
  assert.equal(runtime.resultKey(), 'applied');
  assert.equal(elements.get('resultContinueBtn').textContent, '博士の説明を始める');
  assert.match(elements.get('resultDetail').textContent, /画面に触れると一時停止/);
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

test('毎回の体験者リセットは送信中の古い応答と説明復元を捨てて博士の入口へ戻す', async () => {
  const delayedSet = deferred();
  fetchHandler = async (resource) => ({ ok: true, status: 200,
    json: async () => resource === './set' ? delayedSet.promise
      : { ...validStatus, portalSessionId: 'visitor-next', visitorGeneration: 2, received: 3 } });
  state.status = clone(validStatus); state.connection = 'online'; state.ui = 'idle';
  state.lang = 'fr'; state.relief = true;
  const sending = runtime.send();
  await Promise.resolve(); await Promise.resolve();
  await runtime.poll();
  delayedSet.resolve({ ok: true, seq: 33 }); await sending;
  assert.equal(state.sent, null); assert.equal(state.view, 'title');
  assert.equal(state.lang, 'ja'); assert.equal(state.relief, false);
  assert.equal(state.briefing.triggered, false);
  assert.equal(state.restartMessage, false);
  assert.equal(elements.get('restartNotice').hidden, true);
  assert.equal(storedSession.has('mawarimi.visitor.progress.v1'), false);
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
  for (const stage of ['devices', 'alignment', 'reset', 'playing']) {
    assert.equal(runtime.savedProgressMatches(progress, { ...matching,
      staffSetup: { stage, reason: '', positionConfirmed: true } }), false, stage);
  }
  assert.equal(runtime.savedProgressMatches(progress, { ...matching,
    staffSetup: { stage: 'explanation', reason: 'explanation', positionConfirmed: true } }), true);
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
  assert.equal(elements.get('briefingSettings').textContent, 'Change language / reduced horror');
  assert.equal(elements.get('briefingResume').textContent, 'Resume');
  assert.equal(elements.get('staffLabel').textContent, 'Staff only');
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

test('参考動画は説明中も停止して非表示のまま解放する', () => {
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
  assert.equal(video.playCount, before);
  assert.equal(video.hidden, true);
  assert.equal(video.dataset.source || '', '');
  assert.equal(selectorElements.get('.briefing-visual').classList.contains('is-loop-playing'), false);

  runtime.moveBriefing(1, 'auto');
  runtime.moveBriefing(1, 'auto');
  assert.equal(state.briefing.cueIndex, 3);
  assert.equal(video.playCount, before);

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

test('音声なしの説明も三言語で自動進行し、通常時は操作を常設しない', () => {
  const data = clone(briefing);
  delete data.scenes[0].video;
  for (const lang of ['ja', 'en', 'fr']) {
    prepareBriefing(data);
    state.lang = lang;
    state.briefing.auto = true;
    runtime.beginBriefingCue();
    assert.equal(elements.get('briefingControls').hidden, true);
    assert.ok(pendingDelays().includes(data.scenes[0].cues[lang][0].durationMs));
    timers.reset();
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

test('読み上げ途中の操作表示は音声と動画を同じ位置で止め、明示再開で続ける', () => {
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
    state.briefing.auto = true;
    runtime.openBriefingControls();
    assert.equal(state.briefing.auto, false);
    assert.equal(media.currentTime, before.time, kind);
    assert.ok(media.pauseCount > before.pauses, kind);
    assert.equal(media.paused, true, kind);
    assert.equal(elements.get('briefingControls').hidden, false);
    runtime.resumeBriefing();
    assert.equal(media.currentTime, before.time, kind);
    assert.equal(media.playCount, before.plays + 1, kind);
    assert.equal(media.paused, false, kind);
    media.currentTime = start + data.scenes[1].cues.ja[1].durationMs / 1000;
    media.ontimeupdate();
    assert.equal(state.briefing.cueIndex, 2, kind);

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
  assert.equal(state.briefing.auto, true, '再説明も最初の文から自動で進む');
  assert.equal(elements.get('briefingControls').hidden, true);
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
  elements.get('briefingResume').dispatch('click');
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

test('通常説明のタップは文を送らず停止し、再開と文頭再生を明示操作だけで行う', () => {
  const data = clone(briefing);
  prepareBriefing(data);
  elements.get('staff').open = false;
  state.briefing.auto = true;
  runtime.configureBriefingMedia(data.scenes[0]);
  runtime.beginBriefingCue();
  const media = runtime.getBriefingMedia();
  media.onplaying();
  media.currentTime = 1.4;
  const cueIndex = state.briefing.cueIndex;
  elements.get('briefingPauseSurface').dispatch('click');
  assert.equal(state.briefing.cueIndex, cueIndex);
  assert.equal(state.briefing.auto, false);
  assert.equal(media.paused, true);
  assert.equal(media.currentTime, 1.4);
  assert.equal(elements.get('briefingControls').hidden, false);
  assert.equal(elements.get('briefingControls').inert, false);
  elements.get('briefingResume').dispatch('click');
  assert.equal(media.currentTime, 1.4);
  assert.equal(media.paused, false);
  elements.get('briefingPauseSurface').dispatch('click');
  elements.get('briefingRestart').dispatch('click');
  assert.equal(media.currentTime, 0);
  assert.equal(media.paused, false);
});

test('字幕だけの自動進行は停止時間を除外し、同じ文の残り時間から再開する', () => {
  const data = clone(briefing);
  delete data.scenes[0].video;
  delete data.scenes[0].audio;
  prepareBriefing(data);
  state.lang = 'en';
  state.briefing.auto = true;
  context.performance.now = () => 1000;
  runtime.beginBriefingCue();
  const duration = data.scenes[0].cues.en[0].durationMs;
  assert.ok(pendingDelays().includes(duration));
  context.performance.now = () => 2500;
  elements.get('briefingPauseSurface').dispatch('click');
  assert.equal(state.briefing.cueIndex, 0);
  elements.get('briefingResume').dispatch('click');
  assert.ok(pendingDelays().includes(duration - 1500));
  context.performance.now = () => 1000;
  state.lang = 'ja';
});

test('スタッフ入口は短押し・領域外・複数指で取り消し、異常中も1.5秒長押しだけを受け付ける', () => {
  prepareBriefing(clone(briefing));
  elements.get('staff').open = false;
  state.briefing.auto = true;
  runtime.configureBriefingMedia(briefing.scenes[0]);
  runtime.beginBriefingCue();
  runtime.openBriefingControls();
  runtime.setBriefingPause('offline', true);
  const entry = elements.get('briefingStaff');
  assert.equal(entry.disabled, false);
  entry.dispatch('pointerdown', { isPrimary: true, pointerId: 81, button: 0 });
  entry.dispatch('pointerup', { pointerId: 81 });
  assert.equal(elements.get('staff').open, false);
  assert.match(elements.get('briefingStaffHint').textContent, /1\.5秒長押し/);
  entry.dispatch('pointerdown', { isPrimary: true, pointerId: 82, button: 0 });
  entry.dispatch('pointerleave', { pointerId: 82 });
  assert.equal(pendingDelays().includes(1500), false);
  entry.dispatch('pointerdown', { isPrimary: true, pointerId: 83, button: 0 });
  entry.dispatch('pointerdown', { isPrimary: false, pointerId: 84, button: 0 });
  assert.equal(pendingDelays().includes(1500), false);
  entry.dispatch('pointerdown', { isPrimary: true, pointerId: 85, button: 0 });
  timers.runOne(1500);
  assert.equal(elements.get('staff').open, true);
  elements.get('staff').close();
  runtime.setBriefingPause('offline', false);
});

test('通常説明は博士と字幕だけを常設し、375pxでも操作ボタンを44px以上にする', () => {
  assert.match(html, /\.stage\.is-briefing:not\(\.is-equipment\) \.topbar,[\s\S]*?\.content \{ display: none; \}/);
  assert.match(html, /\.briefing-control \{ min-height: 44px;/);
  assert.match(html, /\.stage\.is-briefing \.briefing-controls button \{ min-height: 44px; \}/);
  assert.match(html, /id="briefingPauseSurface"[^>]*aria-label=/);
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
  state.lastReceived = null;
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
  elements.get('staff').close();
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
  state.status.questTick = 1; state.status.staffSetup = { stage: 'playing', reason: '' };
  runtime.observeQuestTick(state.status);
  runtime.renderStaff();
  assert.equal(elements.get('staffTargetState').textContent, '動作中');
  assert.equal(elements.get('staff').dataset.state, 'playing');
  assert.equal(elements.get('staffNowTitle').textContent, '体験中');
  assert.equal(elements.get('staffResetBtn').hidden, true, '本編中はタブレットからリセットを出さない');
  assert.equal(runtime.staffResetAllowed(), false);
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
  assert.deepEqual(posts, [{ resource: './clear', body: { tabletSessionId: runtime.tabletSessionId, portalSessionId: 'portal-a', seq: 7 } }]);
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
  assert.equal(elements.get('staff').open, false, '説明の確認中はスタッフ画面を閉じる');
  assert.equal(elements.get('briefingSettings').textContent, 'スタッフ画面に戻る');
  elements.get('briefingSettings').dispatch('click');
  assert.equal(state.view, 'title');
  assert.equal(state.staffPreview, false);
  assert.equal(elements.get('staff').open, true, '説明の確認を終えるとスタッフ画面へ戻る');
  assert.equal(elements.get('staff').dataset.state, 'offline');
  assert.equal(elements.get('staffRetryBtn').hidden, false);
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


test('新APIは10文後に返却を案内し、Questの同一revision確認後だけ装着2文へ進む', async () => {
  prepareBriefing(clone(briefing));
  state.staffPreview = false; state.ui = 'sent';
  const wearIndex = briefing.scenes.findIndex(scene => scene.id === 'wear');
  const preWearCount = briefing.scenes.slice(0, wearIndex).reduce((count, scene) => count + scene.cues.ja.length, 0);
  const totalCount = briefing.scenes.reduce((count, scene) => count + scene.cues.ja.length, 0);
  assert.equal(preWearCount, 10);
  assert.equal(briefing.scenes[wearIndex].cues.ja.length, 2);
  assert.equal(totalCount, 12);
  const status = { ...clone(validStatus), questTick: 50, lang: 'ja', relief: false,
    appliedSeq: 7, received: 7, visitorGeneration: 1,
    pending: { lang: 'ja', relief: false, seq: 7 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 7, lang: 'ja', relief: false },
    staffSetup: { stage: 'explanation', reason: 'explanation', positionConfirmed: true },
    briefing: { seq: 0, revision: 0, completed: false, staffConfirmed: false } };
  state.status = status;
  state.sent = { lang: 'ja', relief: false, seq: 7, portalSessionId: status.portalSessionId, targetId: runtime.activeQuest() };
  runtime.observeQuestTick(status);
  const posts = [];
  const onlineFetch = async (resource, options = {}) => {
    if (options.method === 'POST') posts.push({ path: resource, body: JSON.parse(options.body) });
    return { ok: true, status: 200, json: async () => resource === './tablet/pulse' ? { ok: true } : clone(status) };
  };
  fetchHandler = onlineFetch;
  state.briefing.sceneIndex = wearIndex - 1;
  for (const [lang, label] of [['ja', 'スタッフへ返す'], ['en', 'Return to staff'], ['fr', 'Remettre au personnel']]) {
    state.lang = lang;
    state.briefing.cueIndex = state.briefing.data.scenes[wearIndex - 1].cues[lang].length - 1;
    runtime.renderBriefingScene();
    assert.equal(elements.get('briefingNext').textContent, label);
    const explanationTotal = state.briefing.data.scenes.slice(0, wearIndex)
      .reduce((total, scene) => total + scene.cues[lang].length, 0);
    const expectedEnd = String(explanationTotal).padStart(2, '0');
    assert.ok(elements.get('sceneCounter').textContent.endsWith(`${expectedEnd} / ${expectedEnd}`), '調査説明の進み具合に確認後の装着2文を含めない');
  }
  state.lang = 'ja';
  runtime.closeStaffScreen();
  assert.equal(elements.get('staff').open, false);
  runtime.moveBriefing(1); await flushPromises();
  assert.equal(state.briefing.handoffReady, true);
  assert.equal(state.briefing.wearStarted, false);
  assert.equal(state.briefing.ended, false);
  assert.equal(posts.at(-1).body.briefingCompleted, true);
  assert.equal(posts.at(-1).body.staffConfirmed, false);
  assert.equal(runtime.canShowEquipmentGuidance(), false);
  assert.equal(elements.get('equipmentSteps').hidden, true);
  assert.equal(elements.get('briefingTitle').textContent, 'タブレットをスタッフへ返してください');
  assert.equal(elements.get('equipmentFollowup').textContent, '確認後にスタッフが装着をお手伝いします');
  for (const [lang, handoff] of [['ja', 'タブレットをスタッフへ返してください'], ['en', 'Please return the tablet to a member of staff'], ['fr', 'Rendez la tablette au personnel']]) {
    state.lang = lang; runtime.renderBriefingScene();
    assert.equal(elements.get('briefingTitle').textContent, handoff);
  }
  state.lang = 'ja';
  assert.equal(elements.get('staff').open, false, '返却画面ではスタッフ画面を自動で開かない');
  const staffEntry = elements.get('staffToggle');
  staffEntry.dispatch('pointerdown', { isPrimary: true, pointerId: 70, button: 0 });
  staffEntry.dispatch('pointerup', { pointerId: 70 });
  assert.equal(elements.get('staff').open, false, '短押しではスタッフ画面を開かない');
  assert.match(elements.get('staffLabel').textContent, /1\.5秒長押し/);
  staffEntry.dispatch('pointerdown', { isPrimary: true, pointerId: 71, button: 0 });
  timers.runOne(1500);
  assert.equal(elements.get('staff').open, true, '返却画面の長押しでスタッフ画面を開く');
  assert.equal(elements.get('staff').dataset.state, 'confirm');
  assert.equal(elements.get('staffNowTitle').textContent, '装着の前に確認');
  assert.equal(elements.get('staffHandback').hidden, false, '返却の案内を3言語で最上段に出す');
  assert.match(html, /タブレットをスタッフにお渡しください/);
  assert.match(html, /Please hand the tablet to a member of staff\./);
  assert.match(html, /Veuillez remettre la tablette à un membre du personnel\./);
  assert.equal(elements.get('staffCycle3').getAttribute('aria-current'), 'step');
  assert.equal(elements.get('staffPrepareBtn').hidden, false);
  assert.equal(elements.get('staffPrepareBtn').disabled, false);
  assert.equal(elements.get('staffNowItem0Text').textContent, '言語：日本語');
  assert.equal(elements.get('staffNowItem1Text').textContent, 'ホラー軽減：なし');
  assert.equal(elements.get('staffNowItem0').dataset.tone, 'ok');
  const prepare = elements.get('staffPrepareBtn');
  assert.equal(prepare.dispatch('click'), undefined, 'クリックだけでは進めない');
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 7, button: 0 });
  assert.equal(prepare.capturedPointer, 7);
  assert.equal(prepare.style['--hold'], '0.02', '押した直後から満ち始める');
  prepare.dispatch('pointerup', { pointerId: 7 });
  assert.equal(pendingDelays().includes(1200), false, '離すと取り消す');
  assert.equal(prepare.style['--hold'], '0');
  assert.equal(posts.filter(p => p.body.staffConfirmed === true).length, 0);
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 8, button: 0 });
  prepare.dispatch('pointerdown', { isPrimary: false, pointerId: 9, button: 0 });
  assert.equal(pendingDelays().filter(delay => delay === 1200).length, 0, '複数指になったら長押しを取り消す');
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 10, button: 0 });
  timers.runOne(1200); await runtime.staffAction();
  assert.equal(posts.filter(p => p.body.staffConfirmed === true).length, 1, '長押しの完了で一度だけ送る');
  assert.equal(elements.get('staffPrepareBtn').disabled, true);
  assert.equal(elements.get('staffNowBody').textContent, 'クエストの確認を待っています。');
  const request = posts.at(-1).body;
  assert.equal(state.briefing.wearStarted, false, 'HTTP受理だけでは装着音声へ進めない');
  status.briefing = { seq: request.seq, revision: request.briefingRevision - 1, tabletSessionId: runtime.tabletSessionId,
    completed: true, staffConfirmed: true };
  status.staffSetup = { stage: 'handedOff', reason: '', positionConfirmed: true };
  status.questTick++; await runtime.poll();
  assert.equal(state.briefing.wearStarted, false, '古いrevisionでは装着音声へ進めない');
  assert.equal(elements.get('staff').open, true, '古いrevisionではスタッフ画面を閉じない');
  status.briefing.revision = request.briefingRevision;
  status.questTick++; await runtime.poll();
  assert.equal(state.briefing.wearStarted, true);
  assert.equal(elements.get('staff').open, false, 'Questの受理でスタッフ画面を閉じて来場者の画面へ戻す');
  const media = runtime.getBriefingMedia();
  assert.equal(media.paused, false, 'スタッフの装着確定が受理されたら追加タップなしで案内する');
  assert.equal(elements.get('briefingControls').hidden, true);
  assert.equal(state.briefing.mediaWindowActive, true);
  assert.equal(state.briefing.sceneIndex, wearIndex);
  assert.equal(state.briefing.cueIndex, 0);
  assert.equal(state.briefing.ended, false);
  assert.equal(elements.get('briefingNext').textContent, '次の文', '装着1文目から2文目へ進める');
  assert.equal(elements.get('briefingPrevious').disabled, true, '確認後の装着1文目は前段へ戻さない');
  state.briefing.lastMoveAt = 0; state.briefing.chapterChanging = false;
  assert.equal(runtime.moveBriefing(-1), false);
  assert.equal(state.briefing.sceneIndex, wearIndex);
  assert.equal(runtime.canShowEquipmentGuidance(), false);
  assert.equal(elements.get('equipmentSteps').hidden, true);
  for (const [lang, label] of [['ja', '装着の案内へ'], ['en', 'Equipment instructions'], ['fr', 'Consignes d’équipement']]) {
    state.lang = lang;
    state.briefing.cueIndex = state.briefing.data.scenes[wearIndex].cues[lang].length - 1;
    runtime.renderBriefingScene();
    assert.equal(elements.get('briefingNext').textContent, label);
  }
  state.lang = 'ja'; state.briefing.cueIndex = 0; runtime.renderBriefingScene();

  runtime.openBriefingControls();
  const briefingStaff = elements.get('briefingStaff');
  briefingStaff.dispatch('pointerdown', { isPrimary: true, pointerId: 72, button: 0 });
  timers.runOne(1500);
  assert.equal(elements.get('staff').dataset.state, 'wear');
  assert.equal(elements.get('staffNowTitle').textContent, '装着を手伝う');
  assert.equal(elements.get('staffNowItem3Text').textContent, 'ヘッドセットの中の案内に従います。体験者が X か Y を押して始めます');
  assert.equal(elements.get('staffDoneBtn').hidden, false, '装着中は来場者の画面へ戻れる');
  assert.equal(media.paused, true, 'スタッフ画面を開いている間は案内を止める');
  elements.get('staffDoneBtn').dispatch('click');
  assert.equal(elements.get('staff').open, false);
  const playCount = media.playCount;
  assert.match(elements.get('mediaMessage').textContent, /続きから/);
  elements.get('briefingResume').dispatch('click');
  assert.equal(media.playCount, playCount + 1);
  assert.equal(state.briefing.wearStarted, true, '装着1文目の音声終了でスタッフ返却へ戻らない');
  assert.equal(state.briefing.cueIndex, 0);

  for (const [reason, stage] of [['camera', 'devices'], ['content', 'devices'], ['position', 'alignment']]) {
    status.staffSetup.reason = reason; status.staffSetup.stage = stage; status.questTick++; await runtime.poll();
    assert.equal(state.briefing.cueIndex, 0);
    assert.equal(runtime.getBriefingMedia().paused, true);
    assert.equal(elements.get('equipmentSteps').hidden, true);
    assert.match(elements.get('equipmentStaffStatus').textContent, /機器を確認/);
    status.staffSetup.reason = ''; status.staffSetup.stage = 'handedOff'; status.questTick++; await runtime.poll();
    const beforeResume = runtime.getBriefingMedia().playCount;
    assert.equal(state.briefing.cueIndex, 0);
    assert.match(elements.get('mediaMessage').textContent, /続きから/);
    elements.get('briefingResume').dispatch('click');
    assert.equal(runtime.getBriefingMedia().playCount, beforeResume + 1);
  }

  fetchHandler = async () => { throw new TypeError('offline'); };
  await runtime.poll();
  assert.equal(runtime.getBriefingMedia().paused, true);
  assert.equal(state.briefing.cueIndex, 0);
  assert.match(elements.get('equipmentStaffStatus').textContent, /通信が戻るまで/);
  fetchHandler = onlineFetch; status.questTick++; await runtime.poll();
  assert.match(elements.get('mediaMessage').textContent, /続きから/);

  const realDateNow = vm.runInNewContext('Date.now', context);
  vm.runInNewContext('globalThis.__savedDateNow = Date.now; Date.now = () => globalThis.__savedDateNow() + 5000;', context);
  await runtime.poll();
  assert.equal(runtime.getBriefingMedia().paused, true);
  assert.match(elements.get('equipmentStaffStatus').textContent, /通信が戻るまで/);
  vm.runInNewContext('Date.now = globalThis.__savedDateNow;', context);
  status.questTick++; await runtime.poll();
  assert.equal(state.briefing.cueIndex, 0);
  assert.match(elements.get('mediaMessage').textContent, /続きから/);

  state.briefing.cueIndex = state.briefing.data.scenes[wearIndex].cues.ja.length - 1;
  runtime.finishBriefing(); await flushPromises();
  assert.equal(state.briefing.ended, true);
  assert.equal(runtime.canShowEquipmentGuidance(), true);
  assert.equal(elements.get('equipmentSteps').hidden, false);
});

test('待機・確認済み・装着途中・終了を復元し、確認済みをfalseで取り消さない', async () => {
  const wearIndex = briefing.scenes.findIndex(scene => scene.id === 'wear');
  const reportIndex = wearIndex - 1;
  const baseProgress = { tabletSessionId: runtime.tabletSessionId, targetId: runtime.activeQuest(),
    portalSessionId: 'portal-a', seq: 21, lang: 'ja', relief: false };
  const cases = [
    { name: 'waiting', confirmed: false, stage: 'ready', progress: { sceneIndex: reportIndex,
      cueIndex: briefing.scenes[reportIndex].cues.ja.length - 1, handoffReady: true, wearStarted: false, ended: false } },
    { name: 'acknowledged', confirmed: true, stage: 'handedOff', progress: { sceneIndex: reportIndex,
      cueIndex: briefing.scenes[reportIndex].cues.ja.length - 1, handoffReady: true, wearStarted: false, ended: false } },
    { name: 'wear', confirmed: true, stage: 'handedOff', progress: { sceneIndex: wearIndex,
      cueIndex: 1, handoffReady: true, wearStarted: true, ended: false } },
    { name: 'camera recovery', confirmed: true, stage: 'devices', reason: 'camera', progress: { sceneIndex: wearIndex,
      cueIndex: 1, handoffReady: true, wearStarted: true, ended: false } },
    { name: 'ended', confirmed: true, stage: 'handedOff', progress: { sceneIndex: wearIndex,
      cueIndex: 1, handoffReady: true, wearStarted: true, ended: true } },
  ];
  const posts = [];
  fetchHandler = async (resource, options = {}) => {
    if (options.method === 'POST') posts.push(JSON.parse(options.body));
    return { ok: true, status: 200, json: async () => ({ ok: true }) };
  };
  for (const [index, item] of cases.entries()) {
    prepareBriefing(clone(briefing)); state.staffPreview = false; state.ui = 'sent';
    const status = { ...clone(validStatus), questTick: 200 + index, lang: 'ja', relief: false,
      appliedSeq: 21, received: 21, pending: { lang: 'ja', relief: false, seq: 21 },
      lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 21, lang: 'ja', relief: false },
      staffSetup: { stage: item.stage, reason: item.reason || '', positionConfirmed: true },
      briefing: { seq: 21, revision: 30 + index, tabletSessionId: runtime.tabletSessionId,
        completed: true, staffConfirmed: item.confirmed } };
    state.status = status; runtime.observeQuestTick(status);
    const playCountBeforeRestore = elements.get('doctorVideo').playCount;
    assert.equal(runtime.restoreProgress({ ...baseProgress, ...item.progress }), true, item.name);
    await runtime.pulse();
    assert.equal(posts.at(-1).briefingCompleted, true, item.name);
    assert.equal(posts.at(-1).staffConfirmed, item.confirmed, item.name);
    assert.equal(posts.at(-1).briefingRevision, status.briefing.revision, item.name);
    if (item.name === 'acknowledged') {
      assert.equal(state.briefing.wearStarted, true);
      assert.equal(state.briefing.mediaPaused, true);
      assert.equal(runtime.getBriefingMedia().playCount, playCountBeforeRestore);
    }
    if (item.name === 'wear' || item.name === 'camera recovery') assert.equal(state.briefing.cueIndex, 1);
    if (item.name === 'camera recovery') assert.equal(state.briefing.pauseReasons.has('equipment'), true);
    if (item.name === 'ended') assert.equal(state.briefing.ended, true);
  }
  const confirmedRevision = posts.at(-1).briefingRevision;
  elements.get('briefingReplay').dispatch('click'); await flushPromises();
  assert.equal(posts.at(-1).briefingCompleted, false);
  assert.equal(posts.at(-1).staffConfirmed, false);
  assert.ok(posts.at(-1).briefingRevision > confirmedRevision);
  assert.equal(state.briefing.handoffReady, false);
  assert.equal(state.briefing.wearStarted, false);
});

test('report最終文の自動音声終了でもwearへ直行せず返却待ちに入る', async () => {
  prepareBriefing(clone(briefing)); state.staffPreview = false; state.ui = 'sent';
  const wearIndex = briefing.scenes.findIndex(scene => scene.id === 'wear');
  const report = briefing.scenes[wearIndex - 1];
  state.briefing.sceneIndex = wearIndex - 1;
  state.briefing.cueIndex = report.cues.ja.length - 1;
  state.status = { ...clone(validStatus), questTick: 301, lang: 'ja', relief: false,
    appliedSeq: 31, received: 31, pending: { lang: 'ja', relief: false, seq: 31 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 31, lang: 'ja', relief: false },
    staffSetup: { stage: 'explanation', reason: 'explanation', positionConfirmed: true } };
  state.sent = { lang: 'ja', relief: false, seq: 31, portalSessionId: state.status.portalSessionId,
    targetId: runtime.activeQuest() };
  runtime.observeQuestTick(state.status);
  fetchHandler = async () => ({ ok: true, status: 200, json: async () => ({ ok: true }) });
  runtime.configureBriefingMedia(report);
  runtime.setBriefingAuto(true);
  runtime.startBriefingMediaWindow();
  const media = runtime.getBriefingMedia();
  media.currentTime = 999;
  media.ontimeupdate();
  await flushPromises();
  assert.equal(state.briefing.handoffReady, true);
  assert.equal(state.briefing.wearStarted, false);
  assert.equal(state.briefing.ended, false);
});

test('旧APIとスタッフ確認モードはwearを含む12文を連続してから終了する', () => {
  const total = briefing.scenes.reduce((count, scene) => count + scene.cues.ja.length, 0);
  const wearIndex = briefing.scenes.findIndex(scene => scene.id === 'wear');
  assert.equal(total, 12);
  for (const preview of [false, true]) {
    prepareBriefing(clone(briefing));
    state.staffPreview = preview;
    state.status = preview ? { ...clone(validStatus), staffSetup: { stage: 'devices' } } : clone(validStatus);
    for (let index = 1; index < total; index++) assert.equal(runtime.moveBriefing(1, 'auto'), true);
    assert.equal(state.briefing.sceneIndex, wearIndex);
    assert.equal(state.briefing.cueIndex, briefing.scenes[wearIndex].cues.ja.length - 1);
    assert.equal(state.briefing.handoffReady, false);
    assert.equal(state.briefing.ended, false);
    runtime.moveBriefing(1, 'auto');
    assert.equal(state.briefing.ended, true);
  }
});

test('タブレットのリセットは一度送り、Questが世代を変えて確定するまで待つ', async () => {
  runtime.stopBriefingPlayback(); state.view = 'title'; state.sent = null; state.ui = 'idle'; state.staffPreview = false;
  state.connection = 'online';
  let status = { ...clone(validStatus), questTick: 110, visitorGeneration: 1,
    staffSetup: { stage: 'reset', reason: '', positionConfirmed: true } };
  state.status = clone(status); runtime.observeQuestTick(status);
  runtime.openStaffScreen('manual');
  assert.equal(elements.get('staff').dataset.state, 'prepare');
  assert.equal(elements.get('staffNowTitle').textContent, '次の体験者の準備');
  assert.equal(elements.get('staffResetBtn').hidden, false);
  assert.equal(elements.get('staffResetBtn').textContent, '準備を始める');
  assert.equal(elements.get('staffResetBtn').disabled, false);
  assert.equal(elements.get('staffCycle1').getAttribute('aria-current'), 'step');
  assert.equal(elements.get('titleStart').disabled, true);
  const posts = [];
  fetchHandler = async (resource, options = {}) => {
    if (options.method === 'POST') posts.push(JSON.parse(options.body));
    return { ok: true, status: 200, json: async () => resource === './tablet/pulse' ? { ok: true } : clone(status) };
  };
  const pending = elements.get('staffResetBtn').dispatch('click');
  elements.get('staffResetBtn').dispatch('click'); await pending;
  assert.equal(posts.length, 1); assert.equal(posts[0].staffReset, true);
  assert.equal(posts[0].settingsSeq, 0);
  assert.equal(elements.get('staffNowBody').textContent, '準備しています…');
  assert.equal(elements.get('staffResetBtn').disabled, true);
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), false, '送信成功だけでは完了を表示しない');
  assert.equal(elements.get('titleStart').disabled, true, 'リセットのHTTP受理後も新しい受付を開かない');
  status = { ...status, questTick: 111 };
  await runtime.poll();
  assert.equal(elements.get('staffNowBody').textContent, '準備しています…', 'HTTP 200 と同じ世代の応答では完了にしない');
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), false);
  await runtime.pulse();
  assert.equal(posts.filter((body) => body.staffReset).length, 2, '完了まで同じ要求を送り続ける');
  status = { ...status, portalSessionId: 'portal-new', visitorGeneration: 2, questTick: 112,
    staffResetId: posts[0].resetRequestId, staffSetup: { stage: 'settings', reason: 'settings', positionConfirmed: true } };
  await runtime.poll();
  assert.equal(elements.get('staff').dataset.state, 'handover', '要求IDとセッションIDの変化で完了とみなす');
  assert.equal(elements.get('staffReceipt').textContent, '次の体験者の準備ができました');
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), true);
  assert.equal(elements.get('staffNowTitle').textContent, 'タブレットを来場者に渡す');
  assert.equal(elements.get('staffNowMessage').textContent, '', '前の状態のメッセージを残さない');
  assert.equal(elements.get('staffResetBtn').hidden, true);
  assert.equal(elements.get('staffVisitorBtn').hidden, false);
  assert.equal(elements.get('staffVisitorBtn').textContent, '来場者の画面にする');
  assert.equal(elements.get('staff').open, true, 'リセット完了だけでは来場者の画面へ切り替えない');
  assert.equal(state.sent, null); assert.equal(state.view, 'title');
  assert.equal(elements.get('titleStart').disabled, false, 'Questの新しい世代を確認したら受付を開く');
  elements.get('staffVisitorBtn').dispatch('click');
  assert.equal(elements.get('staff').open, false);
  assert.equal(state.view, 'title');
  elements.get('titleStart').dispatch('click');
  assert.equal(state.view, 'edit');
});


test('スタッフ欄を閉じている間は来場者画面からリセット・装着確定を受け付けない', async () => {
  runtime.stopBriefingPlayback(); state.view = 'briefing'; state.briefing.ended = true;
  state.staffPreview = false; state.ui = 'sent'; state.connection = 'online';
  state.status = { ...clone(validStatus), questTick: 120, appliedSeq: 9, received: 9,
    lang: 'ja', relief: false, pending: { seq: 9, lang: 'ja', relief: false },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 9, lang: 'ja', relief: false },
    staffSetup: { stage: 'ready', reason: '', positionConfirmed: true } };
  state.sent = { lang: 'ja', relief: false, seq: 9, portalSessionId: state.status.portalSessionId, targetId: runtime.activeQuest() };
  runtime.observeQuestTick(state.status); elements.get('staff').open = false; runtime.renderStaff();
  let requests = 0; fetchHandler = async () => { requests++; throw new Error('must not send hidden staff actions'); };
  await elements.get('staffPrepareBtn').dispatch('click'); await elements.get('staffResetBtn').dispatch('click');
  assert.equal(requests, 0);
});

test('リセット拒否では成功帯を出さず、同じセッションの機器回復だけを短く知らせる', async () => {
  prepareStaff(); runtime.resetStaffTimers(); runtime.renderStaff();
  let status = { ...clone(validStatus), questTick: 130, staffSetup: { stage: 'reset', reason: '', positionConfirmed: true } };
  state.status = status; runtime.observeQuestTick(status);
  const posts = recordPosts(() => status);
  runtime.renderStaff();
  await elements.get('staffResetBtn').dispatch('click');
  status = { ...status, questTick: 131, staffResetRejectedId: posts[0].resetRequestId };
  await runtime.poll();
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), false);
  assert.match(elements.get('staffNowMessage').textContent, /準備を始められませんでした/);

  runtime.resetStaffTimers();
  status = { ...clone(validStatus), questTick: 140, portalSessionId: 'same-session',
    staffSetup: { stage: 'settings', reason: '', positionConfirmed: true, position: 'confirmed', content: true,
      cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'ok' }] } };
  state.status = status; state.connection = 'online'; runtime.observeQuestTick(status); runtime.renderStaff();
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), false, '最初の正常観測は復旧と言わない');
  status.staffSetup.cameras[1] = { id: 'B', state: 'trouble', problem: 'stale' };
  state.status = status; runtime.renderStaff();
  assert.equal(elements.get('staffRecovery').textContent, '対処：スマホ B の FixedCam Streamer を開き直す');
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), false);
  status.staffSetup.cameras[1] = { id: 'B', state: 'ok' };
  state.status = status; runtime.renderStaff();
  assert.equal(elements.get('staffReceipt').textContent, '接続と機器の状態が戻りました');
  assert.equal(elements.get('staffReceipt').classList.contains('is-visible'), true);
  runtime.renderStaff();
  assert.equal(elements.get('staffReceipt').textContent, '接続と機器の状態が戻りました', 'pollごとに別の復旧を作らない');
  state.view = 'briefing'; state.ui = 'sent'; state.connection = 'online';
  state.status = { ...clone(validStatus), questTick: 150, appliedSeq: 9, received: 9,
    lang: 'ja', relief: false, pending: { seq: 9, lang: 'ja', relief: false },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 9, lang: 'ja', relief: false } };
  state.sent = { lang: 'ja', relief: false, seq: 9, portalSessionId: state.status.portalSessionId, targetId: runtime.activeQuest() };
});

test('Quest更新停止は接続と設定の緑表示を外し、復帰後に戻す', () => {
  runtime.observeQuestTick(state.status); runtime.renderResult();
  assert.equal(elements.get('resultView').dataset.tone, 'ok');
  vm.runInNewContext('globalThis.__savedDateNow = Date.now; Date.now = () => globalThis.__savedDateNow() + 5000;', context);
  runtime.renderConnection(); runtime.renderResult();
  assert.equal(elements.get('staffToggle').dataset.state, 'checking');
  runtime.renderText();
  assert.match(elements.get('connectionText').textContent, /復帰・更新待ち/, '文言の更新でもQuestの停止表示を接続済みに戻さない');
  assert.equal(elements.get('resultView').dataset.tone, 'wait');
  assert.equal(elements.get('headsetLanguage').textContent, '—');
  assert.match(elements.get('resultTitle').textContent, /復帰・更新待ち/);
  vm.runInNewContext('Date.now = globalThis.__savedDateNow;', context);
  state.status.questTick++; runtime.observeQuestTick(state.status); runtime.renderConnection(); runtime.renderResult();
  assert.equal(elements.get('staffToggle').dataset.state, 'online');
  assert.equal(elements.get('resultView').dataset.tone, 'ok');
});

test('Native通信は対象と許可APIをJavaへ渡し、応答と取消を一度だけ処理する', async () => {
  const calls = [];
  let savedQuest = '';
  let mediaVolume = '{"current":0,"max":15,"muted":false}';
  const nativeContext = {
    AbortController,
    TabletHost: {
      getQuest: () => savedQuest,
      setQuest: (quest) => { savedQuest = quest; },
      getMediaVolume: () => mediaVolume,
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
  assert.equal(JSON.stringify(nativeTransport.getMediaVolume()), JSON.stringify({ current: 0, max: 15, muted: false }));
  mediaVolume = '{"current":7,"max":15,"muted":false}';
  assert.equal(JSON.stringify(nativeTransport.getMediaVolume()), JSON.stringify({ current: 7, max: 15, muted: false }));
  mediaVolume = '{"current":15,"max":15,"muted":false}';
  assert.equal(JSON.stringify(nativeTransport.getMediaVolume()), JSON.stringify({ current: 15, max: 15, muted: false }));
  for (const invalid of ['null', '{}', '{"current":16,"max":15,"muted":false}', '{bad json']) {
    mediaVolume = invalid;
    assert.equal(nativeTransport.getMediaVolume(), null, invalid);
  }

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

  const oldHostContext = {
    TabletHost: { getQuest: () => 'alpha', setQuest() {}, request() {}, cancel() {} },
    fetch: () => { throw new Error('browser fetch must not be used'); },
  };
  oldHostContext.globalThis = oldHostContext;
  vm.runInNewContext(transportSource, oldHostContext, { filename: transportPath });
  assert.equal(oldHostContext.TabletTransport.native, true);
  assert.equal(oldHostContext.TabletTransport.getMediaVolume(), null, '旧Hostは本体で確認へ戻す');
});

// ---- スタッフ画面（2026-10-10 作り直し） ----

function shiftClock(ms) {
  vm.runInNewContext(`globalThis.__realNow = globalThis.__realNow || Date.now; Date.now = () => globalThis.__realNow() + ${ms};`, context);
}

function restoreClock() {
  vm.runInNewContext('if (globalThis.__realNow) { Date.now = globalThis.__realNow; delete globalThis.__realNow; }', context);
}

function staffInput(overrides = {}) {
  return {
    native: true, quest: 'alpha', connection: 'online', fresh: true, resetPending: false, visitorFlow: false,
    handback: false, wearStarted: false, acknowledged: false, legacyEndElapsedMs: 0,
    status: { ...clone(validStatus), staffSetup: { stage: 'settings', reason: '', positionConfirmed: true } },
    ...overrides,
  };
}

function withSetup(setup, overrides = {}) {
  return staffInput({ status: { ...clone(validStatus), staffSetup: setup }, ...overrides });
}

function visibleStaffItems() {
  return [0, 1, 2, 3, 4, 5]
    .filter((index) => !elements.get(`staffNowItem${index}`).hidden)
    .map((index) => [elements.get(`staffNowItem${index}`).dataset.tone, elements.get(`staffNowItem${index}Text`).textContent]);
}

function recordPosts(statusRef) {
  const posts = [];
  fetchHandler = async (resource, options = {}) => {
    if (options.method === 'POST') posts.push(JSON.parse(options.body));
    return { ok: true, status: 200, json: async () => (resource === './status' ? clone(statusRef()) : { ok: true }) };
  };
  return posts;
}

function staffAlertInput(overrides = {}) {
  return {
    target: 'alpha|portal-a|tablet-a|7|3',
    acknowledged: true,
    close: false,
    blocked: false,
    hidden: false,
    fresh: true,
    audioReady: true,
    now: 0,
    ...overrides,
  };
}

test('引き渡し後の開始阻害は6秒で一度だけ通知し、理由変更では繰り返さない', () => {
  const machine = runtime.createStaffAlertState();
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput()), 'none');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, blocker: 'camera', now: 1000 })), 'none');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, blocker: 'position', now: 6999 })), 'none');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, blocker: 'content', now: 7000 })), 'sound');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, blocker: 'stale', now: 15000 })), 'none');
});

test('全回復後の新しい阻害だけを再通知する', () => {
  const machine = runtime.createStaffAlertState();
  runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, now: 0 }));
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, now: 6000 })), 'sound');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: false, now: 7000 })), 'none');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, now: 8000 })), 'none');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, now: 14000 })), 'sound');
});

test('確認前と対象・セッション・設定・revision不一致では監視を開始しない', () => {
  const variants = [
    'beta|portal-a|tablet-a|7|3',
    'alpha|portal-b|tablet-a|7|3',
    'alpha|portal-a|tablet-b|7|3',
    'alpha|portal-a|tablet-a|8|3',
    'alpha|portal-a|tablet-a|7|4',
  ];
  for (const target of variants) {
    const machine = runtime.createStaffAlertState();
    const original = staffAlertInput().target;
    runtime.updateStaffAlertState(machine, staffAlertInput());
    assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ target, acknowledged: false, blocked: true, now: 7000 })), 'none');
    assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ target: original, blocked: true, now: 14000 })), 'none');
    assert.equal(machine.target, '', '閉じた元対象は遅延状態で再開しない');
  }
  const beforeAck = runtime.createStaffAlertState();
  assert.equal(runtime.updateStaffAlertState(beforeAck, staffAlertInput({ acknowledged: false, blocked: true, now: 10000 })), 'none');
  assert.equal(beforeAck.target, '');
});

test('本編を一度観測した対象は遅れたhandedOffで再開しない', () => {
  const machine = runtime.createStaffAlertState();
  runtime.updateStaffAlertState(machine, staffAlertInput());
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ close: true, now: 1000 })), 'none');
  assert.equal(runtime.updateStaffAlertState(machine, staffAlertInput({ blocked: true, now: 10000 })), 'none');
  assert.equal(machine.target, '');
  assert.ok(machine.closedTargets.includes(staffAlertInput().target));
});

test('背景中の異常と音声未解除の異常は無音で消費し、復帰や後解除で予約再生しない', () => {
  const background = runtime.createStaffAlertState();
  runtime.updateStaffAlertState(background, staffAlertInput({ blocked: true, hidden: true, now: 0 }));
  assert.equal(runtime.updateStaffAlertState(background, staffAlertInput({ blocked: true, hidden: false, now: 6000 })), 'consume');
  assert.equal(runtime.updateStaffAlertState(background, staffAlertInput({ blocked: true, hidden: false, now: 7000 })), 'none');
  runtime.updateStaffAlertState(background, staffAlertInput({ blocked: false, now: 8000 }));
  runtime.updateStaffAlertState(background, staffAlertInput({ blocked: true, now: 9000 }));
  assert.equal(runtime.updateStaffAlertState(background, staffAlertInput({ blocked: true, now: 15000 })), 'sound');

  const locked = runtime.createStaffAlertState();
  runtime.updateStaffAlertState(locked, staffAlertInput({ blocked: true, audioReady: false, now: 0 }));
  assert.equal(runtime.updateStaffAlertState(locked, staffAlertInput({ blocked: true, audioReady: false, now: 6000 })), 'consume');
  assert.equal(runtime.updateStaffAlertState(locked, staffAlertInput({ blocked: true, audioReady: true, now: 7000 })), 'none');

  const discoveredOnResume = runtime.createStaffAlertState();
  runtime.updateStaffAlertState(discoveredOnResume, staffAlertInput({ blocked: false, hidden: false, now: 0 }));
  runtime.updateStaffAlertState(discoveredOnResume, staffAlertInput({ blocked: false, hidden: true, now: 1000 }));
  runtime.updateStaffAlertState(discoveredOnResume,
    staffAlertInput({ blocked: false, hidden: false, fresh: false, now: 7000 }));
  runtime.updateStaffAlertState(discoveredOnResume, staffAlertInput({ blocked: true, hidden: false, now: 8000 }));
  assert.equal(runtime.updateStaffAlertState(discoveredOnResume,
    staffAlertInput({ blocked: true, hidden: false, now: 14000 })), 'consume', '背景中に始まった異常は復帰時に発見しても鳴らさない');
  runtime.updateStaffAlertState(discoveredOnResume, staffAlertInput({ blocked: false, hidden: false, fresh: true, now: 15000 }));
  runtime.updateStaffAlertState(discoveredOnResume, staffAlertInput({ blocked: true, hidden: false, now: 16000 }));
  assert.equal(runtime.updateStaffAlertState(discoveredOnResume,
    staffAlertInput({ blocked: true, hidden: false, now: 22000 })), 'sound', '新しい正常応答の後の異常は通知する');
});

test('実操作での音声解除はresume失敗を表示し、後の操作で解除しても古い異常を再生しない', async () => {
  let resumeCount = 0;
  let oscillatorStarts = 0;
  class TestAudioContext {
    constructor() {
      this.state = 'suspended';
      this.currentTime = 10;
      this.destination = {};
    }

    async resume() {
      resumeCount++;
      if (resumeCount === 1) throw new Error('resume denied');
      this.state = 'running';
    }

    createOscillator() {
      return { type: '', frequency: { setValueAtTime() {} }, connect() {},
        start: () => { oscillatorStarts++; }, stop() {} };
    }

    createGain() {
      return { gain: { setValueAtTime() {}, exponentialRampToValueAtTime() {} }, connect() {} };
    }
  }
  context.AudioContext = TestAudioContext;
  runtime.staffAlertAudio.context = null;
  runtime.staffAlertAudio.unlockPromise = null;
  runtime.staffAlertAudio.unlocked = false;
  assert.equal(await runtime.unlockStaffAlertAudio({ isTrusted: false }), false, '合成操作では解除しない');
  assert.equal(resumeCount, 0);
  assert.equal(await runtime.unlockStaffAlertAudio({ isTrusted: true }), false);
  assert.match(elements.get('staffAlertSoundHelp').textContent, /有効にできませんでした/);
  assert.equal(await runtime.unlockStaffAlertAudio({ isTrusted: true }), true);
  assert.match(elements.get('staffAlertSoundHelp').textContent, /再生できます/);
  assert.equal(oscillatorStarts, 0, '解除だけで過去の異常を再生しない');
  state.status = null;
  assert.equal(runtime.playStaffAlertSound(), true);
  assert.equal(oscillatorStarts, 2, '試聴は短い2音だけを開始する');
  runtime.cancelStaffAlertSound();
  runtime.staffAlertAudio.activeTones.clear();
  delete context.AudioContext;
  runtime.staffAlertAudio.context = null;
  runtime.staffAlertAudio.unlockPromise = null;
  runtime.staffAlertAudio.unlocked = false;
});

test('予約した通知音は本編・背景・対象変更で残りを停止し、終了ノードを解放する', () => {
  const oscillators = [];
  const gains = [];
  class ActiveAudioContext {
    constructor() { this.state = 'running'; this.currentTime = 20; this.destination = {}; }
    createOscillator() {
      const oscillator = { frequency: { setValueAtTime() {} }, starts: [], stops: [], disconnected: false,
        connect() {}, start(when) { this.starts.push(when); }, stop(when) { this.stops.push(when); },
        disconnect() { this.disconnected = true; } };
      oscillators.push(oscillator);
      return oscillator;
    }
    createGain() {
      const gain = { values: [], disconnected: false,
        gain: { setValueAtTime(value, when) { gain.values.push([value, when]); },
          exponentialRampToValueAtTime(value, when) { gain.values.push([value, when]); },
          cancelScheduledValues(when) { gain.cancelledAt = when; } },
        connect() {}, disconnect() { this.disconnected = true; } };
      gains.push(gain);
      return gain;
    }
  }
  const audio = new ActiveAudioContext();
  runtime.staffAlertAudio.context = audio;
  runtime.staffAlertAudio.unlocked = true;
  runtime.staffAlertAudio.activeTones.clear();
  runtime.staffAlertAudio.generation = 0;
  state.status = null;
  document.hidden = false;
  assert.equal(runtime.playStaffAlertSound(), true);
  audio.currentTime = 20.13;
  state.status = { phase: 'RUN', staffSetup: { stage: 'playing' } };
  runtime.observeStaffAlert(1, true);
  assert.ok(oscillators[1].starts[0] > audio.currentTime, '二音目の開始前に本編へ入る');
  assert.ok(oscillators.every(item => item.stops.includes(audio.currentTime)), '予約済みoscillatorを現在時刻で停止する');
  assert.ok(gains.every(item => item.cancelledAt === audio.currentTime
    && item.values.some(([value, when]) => value === 0 && when === audio.currentTime)), 'gain予約を消して0にする');
  for (const oscillator of oscillators) oscillator.onended();
  assert.equal(runtime.staffAlertAudio.activeTones.size, 0);
  assert.ok(oscillators.every(item => item.disconnected));
  assert.ok(gains.every(item => item.disconnected));

  const beforeBackground = oscillators.length;
  state.status = null;
  assert.equal(runtime.playStaffAlertSound(), true);
  audio.currentTime = 21;
  document.hidden = true;
  runtime.observeStaffAlert(2, false);
  assert.ok(oscillators.slice(beforeBackground).every(item => item.stops.includes(21)), '背景移行で停止する');
  for (const oscillator of oscillators.slice(beforeBackground)) oscillator.onended();
  document.hidden = false;

  const statusA = { ...clone(validStatus), questTick: 930, lang: 'ja', relief: false, appliedSeq: 63, received: 63,
    pending: { lang: 'ja', relief: false, seq: 63 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 63, lang: 'ja', relief: false },
    briefing: { tabletSessionId: runtime.tabletSessionId, seq: 63, revision: 13, completed: true, staffConfirmed: true },
    staffSetup: { stage: 'handedOff', reason: '', positionConfirmed: true, position: 'confirmed', content: true,
      cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'ok' }] } };
  state.status = statusA;
  state.sent = { lang: 'ja', relief: false, seq: 63, portalSessionId: statusA.portalSessionId, targetId: runtime.activeQuest() };
  runtime.restorePreparationState(statusA);
  runtime.staffAlert.target = '';
  runtime.staffAlert.closedTargets.splice(0);
  runtime.observeStaffAlert(3, true);
  const beforeTargetChange = oscillators.length;
  assert.equal(runtime.playStaffAlertSound(), true);
  audio.currentTime = 22;
  state.status = { ...statusA, portalSessionId: 'portal-b' };
  state.sent.portalSessionId = 'portal-b';
  runtime.observeStaffAlert(4, true);
  assert.ok(oscillators.slice(beforeTargetChange).every(item => item.stops.includes(22)), '対象変更で停止する');
  for (const oscillator of oscillators.slice(beforeTargetChange)) oscillator.onended();
  runtime.staffAlertAudio.context = null;
  runtime.staffAlertAudio.unlocked = false;
});

test('音声解除待ちの試聴は本編開始と対象変更で開始しない', async () => {
  let gate = deferred();
  let starts = 0;
  class PendingAudioContext {
    constructor() { this.state = 'suspended'; this.currentTime = 30; this.destination = {}; }
    async resume() { await gate.promise; this.state = 'running'; }
    createOscillator() { return { frequency: { setValueAtTime() {} }, connect() {}, disconnect() {},
      start() { starts++; }, stop() {} }; }
    createGain() { return { gain: { setValueAtTime() {}, exponentialRampToValueAtTime() {}, cancelScheduledValues() {} },
      connect() {}, disconnect() {} }; }
  }
  const resetAudio = () => {
    runtime.staffAlertAudio.context = null;
    runtime.staffAlertAudio.unlockPromise = null;
    runtime.staffAlertAudio.unlocked = false;
    runtime.staffAlertAudio.pendingTarget = null;
    runtime.staffAlertAudio.activeTones.clear();
  };
  context.AudioContext = PendingAudioContext;
  resetAudio();
  state.status = null;
  const playingRequest = runtime.previewStaffAlertSound({ isTrusted: true });
  await flushPromises();
  state.status = { phase: 'RUN', staffSetup: { stage: 'playing' } };
  runtime.observeStaffAlert(10, true);
  gate.resolve();
  assert.equal(await playingRequest, false);
  assert.equal(starts, 0);

  gate = deferred();
  resetAudio();
  const statusA = { ...clone(validStatus), portalSessionId: 'portal-c', briefing: {
    tabletSessionId: runtime.tabletSessionId, seq: 64, revision: 14, completed: true, staffConfirmed: true },
    staffSetup: { stage: 'handedOff', reason: '', positionConfirmed: true } };
  state.status = statusA;
  state.sent = { lang: 'ja', relief: false, seq: 64, portalSessionId: 'portal-c', targetId: runtime.activeQuest() };
  runtime.restorePreparationState(statusA);
  const targetRequest = runtime.previewStaffAlertSound({ isTrusted: true });
  await flushPromises();
  state.status = { ...statusA, portalSessionId: 'portal-d' };
  state.sent.portalSessionId = 'portal-d';
  runtime.observeStaffAlert(11, true);
  gate.resolve();
  assert.equal(await targetRequest, false);
  assert.equal(starts, 0);
  resetAudio();
  delete context.AudioContext;
});

test('開始阻害は通信・更新・位置・カメラ・素材・設定で、tabletFreshだけは対象外', () => {
  prepareBriefing(clone(briefing));
  const status = { ...clone(validStatus), questTick: 910, lang: 'ja', relief: false, appliedSeq: 61, received: 61,
    pending: { lang: 'ja', relief: false, seq: 61 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 61, lang: 'ja', relief: false },
    briefing: { tabletSessionId: runtime.tabletSessionId, seq: 61, revision: 11, completed: true, staffConfirmed: true },
    staffSetup: { stage: 'handedOff', reason: '', positionConfirmed: true, position: 'confirmed', tablet: 'trouble', content: true,
      cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'ok' }] } };
  state.status = status;
  state.sent = { lang: 'ja', relief: false, seq: 61, portalSessionId: status.portalSessionId, targetId: runtime.activeQuest() };
  runtime.restorePreparationState(status);
  runtime.observeQuestTick(status);
  assert.equal(runtime.staffStartBlocker(status), '', 'tabletFreshは開始阻害ではない');
  status.staffSetup.cameras[1] = { id: 'B', state: 'trouble', problem: 'stale' };
  assert.equal(runtime.staffStartBlocker(status), 'camera');
  status.staffSetup.cameras[1] = { id: 'B', state: 'ok' };
  status.staffSetup.position = 'recenter';
  assert.equal(runtime.staffStartBlocker(status), 'position');
  status.staffSetup.position = 'confirmed';
  status.staffSetup.content = false;
  assert.equal(runtime.staffStartBlocker(status), 'content');
  status.staffSetup.content = true;
  status.lang = 'en';
  assert.equal(runtime.staffStartBlocker(status), 'settings');
});

test('本編と終幕と終了では試聴を無効にし、開始観測後の遅れたhandedOffでも戻さない', () => {
  prepareBriefing(clone(briefing));
  const status = { ...clone(validStatus), questTick: 920, lang: 'ja', relief: false, appliedSeq: 62, received: 62,
    pending: { lang: 'ja', relief: false, seq: 62 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 62, lang: 'ja', relief: false },
    briefing: { tabletSessionId: runtime.tabletSessionId, seq: 62, revision: 12, completed: true, staffConfirmed: true },
    staffSetup: { stage: 'handedOff', reason: '', positionConfirmed: true, position: 'confirmed', content: true,
      cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'ok' }] } };
  state.status = status;
  state.sent = { lang: 'ja', relief: false, seq: 62, portalSessionId: status.portalSessionId, targetId: runtime.activeQuest() };
  runtime.restorePreparationState(status);
  runtime.staffAlert.target = '';
  runtime.staffAlert.phase = 'idle';
  runtime.staffAlert.blockedAt = 0;
  runtime.staffAlert.silent = false;
  runtime.staffAlert.backgrounded = false;
  runtime.staffAlert.closedTargets.splice(0);
  const target = runtime.staffAlertTarget(status);
  runtime.updateStaffAlertState(runtime.staffAlert, staffAlertInput({ target }));
  assert.equal(runtime.staffAlertPreviewAllowed(status), true);
  assert.equal(runtime.staffAlertPreviewAllowed({ ...status, staffSetup: { ...status.staffSetup, stage: 'playing' } }), false);
  assert.equal(runtime.staffAlertPreviewAllowed({ ...status, phase: 'END' }), false);
  assert.equal(runtime.staffAlertPreviewAllowed({ ...status, staffSetup: { ...status.staffSetup, stage: 'ended' } }), false);
  runtime.updateStaffAlertState(runtime.staffAlert, staffAlertInput({ target, close: true }));
  assert.equal(runtime.staffAlertPreviewAllowed(status), false, '遅れたhandedOffでは試聴を戻さない');
});

test('起動時はスタッフ画面から始まり、来場者の画面の入口は「スタッフ画面」になる', () => {
  assert.equal(startupStaff.open, true);
  assert.equal(startupStaff.state, 'checking');
  assert.match(html, /id="staffLabel">スタッフ画面</);
  assert.match(html, /<dialog class="staff-screen" id="staff"[^>]*lang="ja"/);
  assert.doesNotMatch(html, /'スタッフ：/, '「スタッフ：」の前置きを付けない');
  for (const line of [
    'タブレットが反応しないとき：右コントローラーの A を 2 秒押し続けると次の体験者の準備になります',
    '体験を途中でやめるとき：ヘッドセットを外すのを手伝います。右コントローラーの A を 2 秒押し続けます',
    'ヘッドセットの画面が見えないとき：右コントローラーの B を 1 秒押すと画面が正面に戻ります',
  ]) assert.ok(html.includes(line), line);
  assert.equal((html.match(/<details\b/g) || []).length, 1, '折りたたみは「困ったとき」の1つだけ');
  assert.match(html, /--ok: #8fd3a7; --wait: #e3c27a;/);
  runtime.closeStaffScreen();
  state.connection = 'online';
  state.status = { ...clone(validStatus), staffSetup: { stage: 'reset', reason: '', positionConfirmed: true } };
  state.briefing.loadState = 'ready';
  state.briefing.data = clone(briefing);
  assert.equal(runtime.restoreProgress({ tabletSessionId: 'another-page', targetId: null, portalSessionId: 'portal-a',
    seq: 3, lang: 'ja', relief: false, sceneIndex: 0, cueIndex: 0 }), false);
  assert.equal(elements.get('staff').open, true, '復元できなければスタッフ画面から始める');
});

test('スタッフ画面の状態は Quest の応答とタブレットの進行だけから決まる', () => {
  const S = runtime.staffScreenState;
  assert.equal(S(staffInput({ quest: null })), 'unselected');
  assert.equal(S(staffInput({ native: false, quest: null })), 'handover', 'ネイティブでなければ配信元へつなぐ');
  assert.equal(S(staffInput({ connection: 'offline' })), 'offline');
  assert.equal(S(staffInput({ connection: 'checking', status: null })), 'checking');
  assert.equal(S(staffInput({ status: clone(validStatus) })), 'legacy', 'staffSetup の無い旧 Quest');
  assert.equal(S(staffInput({ fresh: false })), 'asleep', 'questTick が止まったら休止');
  assert.equal(S(withSetup({ stage: 'setup', positionConfirmed: false })), 'setup');
  assert.equal(S(withSetup({ stage: 'devices', reason: 'camera', positionConfirmed: false })), 'setup', '旧 Quest の devices');
  assert.equal(S(withSetup({ stage: 'devices', reason: 'camera', positionConfirmed: true },
    { visitorFlow: true, handback: true })), 'confirm', '来場者の進行中のカメラ断は進行の画面を保つ');
  assert.equal(S(withSetup({ stage: 'alignment', positionConfirmed: false, position: 'recenter' })), 'alignment');
  assert.equal(S(withSetup({ stage: 'reset', positionConfirmed: true })), 'prepare');
  assert.equal(S(staffInput({ resetPending: true })), 'prepare', 'リセット要求中は準備しています');
  assert.equal(S(staffInput({ resetPending: true, fresh: false })), 'asleep', '休止中は休止の案内を優先する');
  assert.equal(S(withSetup({ stage: 'settings', positionConfirmed: true })), 'handover');
  assert.equal(S(withSetup({ stage: 'settings', positionConfirmed: true }, { visitorFlow: true })), 'visitorActive');
  assert.equal(S(withSetup({ stage: 'explanation', positionConfirmed: true }, { visitorFlow: true })), 'visitorActive');
  assert.equal(S(withSetup({ stage: 'explanation', positionConfirmed: true },
    { visitorFlow: true, handback: true })), 'confirm', '来場者がスタッフへ返した');
  assert.equal(S(withSetup({ stage: 'ready', positionConfirmed: true }, { visitorFlow: true })), 'confirm');
  assert.equal(S(withSetup({ stage: 'handedOff', positionConfirmed: true },
    { visitorFlow: true, handback: true })), 'confirm', '受理の証拠が無ければ確認に留める');
  assert.equal(S(withSetup({ stage: 'handedOff', positionConfirmed: true },
    { visitorFlow: true, handback: true, acknowledged: true })), 'wear');
  const run = { phase: 'RUN', lap: 2, laps: 3, sec: 84, outro: 'off', ending: '' };
  assert.equal(S(withSetup({ stage: 'playing', positionConfirmed: true, run })), 'playing');
  assert.equal(S(withSetup({ stage: 'ended', positionConfirmed: true, run: { ...run, phase: 'END', outro: 'playing' } })), 'outro');
  assert.equal(S(withSetup({ stage: 'ended', positionConfirmed: true, run: { ...run, phase: 'END', outro: 'done' } })), 'finished');
  const legacyEnd = { ...clone(validStatus), phase: 'END', staffSetup: { stage: 'playing', positionConfirmed: true } };
  assert.equal(S(staffInput({ status: legacyEnd, legacyEndElapsedMs: 7999 })), 'outro', 'run の無い旧 Quest は END から8秒待つ');
  assert.equal(S(staffInput({ status: legacyEnd, legacyEndElapsedMs: 8000 })), 'finished');
  assert.equal(S(staffInput({ status: { ...legacyEnd, phase: 'RUN' } })), 'playing');
  assert.equal(S(withSetup({ stage: 'future', positionConfirmed: true })), 'unknown');
  assert.equal(runtime.staffCycleStepFor('visitorActive', 1), 2);
  assert.equal(runtime.staffCycleStepFor('wear', 1), 3);
  assert.equal(runtime.staffCycleStepFor('asleep', 3), 3, '通信の状態では一巡の段を動かさない');
  assert.equal(runtime.staffCycleStepFor('finished', 4), 5);
});

test('機器の一覧は記号と色と語で示し、異常には直し方を添え、止まった値は「—」に戻す', () => {
  const rows = (setup, overrides = {}) => Object.fromEntries(
    runtime.staffDeviceRows(withSetup(setup, overrides)).map((row) => [row.id, row]));
  let r = rows({ stage: 'settings', positionConfirmed: true, position: 'confirmed', content: true,
    cameras: [{ id: 'A', state: 'ok', problem: '' }, { id: 'B', state: 'trouble', problem: 'nostream' },
      { id: 'C', state: 'checking', problem: '' }] });
  assert.deepEqual([r.Quest.tone, r.Quest.value], ['ok', '動作中']);
  assert.deepEqual([r.Position.tone, r.Position.value], ['ok', '済み']);
  assert.deepEqual([r.CameraA.tone, r.CameraA.value, r.CameraA.fix], ['ok', '映っています', '']);
  assert.deepEqual([r.CameraB.tone, r.CameraB.value, r.CameraB.fix],
    ['trouble', '映像が届いていません', 'スマホ B の FixedCam Streamer を開き、Wi-Fi を確かめる']);
  assert.deepEqual([r.CameraC.tone, r.CameraC.value], ['wait', '確認しています']);
  assert.deepEqual([r.Content.tone, r.Content.value], ['ok', '読み込み済み']);
  const problems = { stale: '映像が止まっています', identity: 'どのカメラか確認できません',
    wrongcam: '別のカメラが映っています', wrongshow: '別の作品の設定です' };
  for (const [problem, value] of Object.entries(problems)) {
    r = rows({ stage: 'settings', positionConfirmed: true, cameras: [{ id: 'C', state: 'trouble', problem }] });
    assert.deepEqual([r.CameraC.tone, r.CameraC.value], ['trouble', value], problem);
    assert.match(r.CameraC.fix, /^スマホ C /);
    assert.equal(r.CameraA.value, '—', '一覧に無いカメラは「—」');
  }
  r = rows({ stage: 'settings', positionConfirmed: true });
  for (const id of ['A', 'B', 'C']) assert.equal(r[`Camera${id}`].value, '—', 'cameras の無い旧 Quest');
  r = rows({ stage: 'setup', positionConfirmed: false, position: 'needed', content: false });
  assert.deepEqual([r.Position.tone, r.Position.value], ['wait', 'まだです']);
  assert.match(r.Position.fix, /右トリガーを 2 秒押し続ける/);
  assert.deepEqual([r.Content.tone, r.Content.value, r.Content.fix],
    ['trouble', '読み込めていません', 'クエストで『廻リ視』を起動し直す']);
  assert.deepEqual([rows({ stage: 'alignment', positionConfirmed: false, position: 'recenter' }).Position.tone,
    rows({ stage: 'alignment', positionConfirmed: false, position: 'recenter' }).Position.value], ['trouble', 'やり直しが必要です']);
  assert.equal(rows({ stage: 'alignment', positionConfirmed: false }).Position.tone, 'trouble', 'position の無い旧 Quest');
  assert.equal(rows({ stage: 'setup', position: 'aligning' }).Position.value, '位置合わせ中');
  assert.equal(rows({ stage: 'settings', reason: 'content' }).Content.tone, 'trouble', 'content の無い旧 Quest は reason で知る');
  r = rows({ stage: 'settings', positionConfirmed: true, cameras: [{ id: 'A', state: 'ok' }] }, { fresh: false });
  assert.deepEqual([r.Quest.tone, r.Quest.value, r.Quest.fix], ['wait', '休止しています', 'ヘッドセット側面の電源ボタンを 1 回押す']);
  assert.equal(r.CameraA.value, '—', '止まった Quest の値を緑で出さない');
  assert.equal(r.Position.value, '—');
  r = Object.fromEntries(runtime.staffDeviceRows(staffInput({ connection: 'offline' })).map((row) => [row.id, row]));
  assert.deepEqual([r.Quest.tone, r.Quest.value, r.Quest.fix], ['trouble', '接続できません', '電源と『廻リ視』の起動を確かめる']);
  assert.equal(r.Position.value, '—');
});

test('機器6区画は成功数を実測し、タブレット音量は別の注意として表示する', () => {
  prepareStaff(); runtime.resetStaffTimers();
  state.status.staffSetup = { stage: 'settings', reason: '', positionConfirmed: true, position: 'confirmed', content: true,
    cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'checking' }] };
  runtime.renderStaff();
  assert.equal(elements.get('staffReadinessCount').textContent, '5 / 6');
  assert.deepEqual(elements.get('staffReadinessSummary').querySelectorAll('span').map((item) => item.dataset.tone),
    ['ok', 'ok', 'ok', 'ok', 'wait', 'ok']);
  assert.deepEqual(Object.values(runtime.staffVolumeRow({ current: 0, max: 15, muted: false })),
    ['Audio', 'wait', '音量 0 / 15', '本体の音量ボタンで上げる']);
  assert.deepEqual(Object.values(runtime.staffVolumeRow({ current: 7, max: 15, muted: false })),
    ['Audio', 'ok', '音量 7 / 15', '']);
  assert.deepEqual(Object.values(runtime.staffVolumeRow({ current: 15, max: 15, muted: true })),
    ['Audio', 'wait', '音量 15 / 15', '本体の音量ボタンで上げる']);
  assert.deepEqual(Object.values(runtime.staffVolumeRow(null)),
    ['Audio', 'none', '本体で確認', '']);
  assert.equal(elements.get('devAudioValue').textContent, '本体で確認', 'ブラウザでは本体で確認とする');
});

test('接続できないときは確かめる3項目と再確認を出し、まだ不通なら案内を残す', async () => {
  prepareStaff(); runtime.resetStaffTimers();
  fetchHandler = async () => { throw new TypeError('offline'); };
  await runtime.poll();
  runtime.openStaffScreen('manual');
  assert.equal(elements.get('staff').dataset.state, 'offline');
  assert.equal(elements.get('staffNowTitle').textContent, 'クエストに接続できません');
  assert.deepEqual(visibleStaffItems().map(([, text]) => text), [
    'クエストの電源が入っている', 'クエストで『廻リ視』を起動している', 'タブレットとクエストが同じ会場 Wi-Fi につながっている']);
  assert.equal(elements.get('staffRetryBtn').hidden, false);
  assert.equal(elements.get('devQuestValue').textContent, '接続できません');
  assert.equal(elements.get('devQuestMark').textContent, '✕');
  assert.equal(elements.get('devQuest').dataset.tone, 'trouble');
  assert.equal(elements.get('devQuestFix').hidden, false);
  assert.equal(elements.get('staffTargetState').textContent, '接続できません');
  await elements.get('staffRetryBtn').dispatch('click');
  assert.equal(elements.get('staffNowMessage').textContent, 'まだ接続できません。上の 3 つを確かめてから、もう一度押してください。');
  const status = { ...clone(validStatus), questTick: 900, staffSetup: { stage: 'reset', reason: '', positionConfirmed: true } };
  fetchHandler = async () => ({ ok: true, status: 200, json: async () => clone(status) });
  await elements.get('staffRetryBtn').dispatch('click');
  assert.equal(elements.get('staff').dataset.state, 'prepare');
  assert.equal(elements.get('staffNowMessage').textContent, '', '状態が変わったら前の状態のメッセージを消す');
  assert.equal(elements.get('devQuestMark').textContent, '●');
  assert.equal(elements.get('staffRetryBtn').hidden, true);
});

test('休止中は電源ボタンの案内を出し、押された準備の要求を保って起きたら続ける', async () => {
  prepareStaff(); runtime.resetStaffTimers();
  let status = { ...clone(validStatus), questTick: 600, visitorGeneration: 2,
    staffSetup: { stage: 'reset', reason: '', positionConfirmed: true } };
  state.status = clone(status); runtime.observeQuestTick(status);
  const posts = recordPosts(() => status);
  runtime.openStaffScreen('manual');
  await elements.get('staffResetBtn').dispatch('click');
  assert.equal(posts.filter((body) => body.staffReset).length, 1);
  shiftClock(5000);
  try {
    await runtime.poll();
    assert.equal(elements.get('staff').dataset.state, 'asleep');
    assert.equal(elements.get('staffNowTitle').textContent, 'ヘッドセットが休止しています');
    assert.equal(elements.get('staffNowBody').textContent, 'ヘッドセット側面の電源ボタンを 1 回押してください。数秒で画面が変わります。');
    assert.equal(elements.get('staffNowNote').textContent,
      '台に置いて約 15 秒たつと休止します。クエストの設定で自動スリープを長くすると止まりにくくなります。');
    assert.deepEqual(visibleStaffItems(), [['wait', '準備の要求を保っています。起きると続けます。']]);
    assert.equal(elements.get('devQuestValue').textContent, '休止しています');
    assert.equal(elements.get('devPositionValue').textContent, '—');
    assert.equal(elements.get('staffTargetMark').textContent, '…');
    await runtime.pulse();
    assert.equal(posts.filter((body) => body.staffReset).length, 2, '休止中も同じ要求を送り続ける');
    assert.equal(new Set(posts.filter((body) => body.staffReset).map((body) => body.resetRequestId)).size, 1);
  } finally {
    restoreClock();
  }
  status = { ...status, questTick: 601, portalSessionId: 'portal-awake', visitorGeneration: 3,
    staffResetId: posts[0].resetRequestId, staffSetup: { stage: 'settings', reason: 'settings', positionConfirmed: true } };
  await runtime.poll();
  assert.equal(elements.get('staff').dataset.state, 'handover', '起きたら同じ要求で完了する');
});

test('来場者の使用中は反映済みの設定と説明の進みを出し、最初からやり直すは1.5秒の長押しで送る', async () => {
  prepareBriefing(clone(briefing)); state.staffPreview = false; state.ui = 'sent'; runtime.resetStaffTimers();
  state.lang = 'fr';
  state.briefing.sceneIndex = 1; state.briefing.cueIndex = 0;
  const status = { ...clone(validStatus), questTick: 800, lang: 'fr', relief: false, appliedSeq: 51, received: 51,
    pending: { lang: 'fr', relief: false, seq: 51 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 51, lang: 'fr', relief: false },
    staffSetup: { stage: 'explanation', reason: 'explanation', positionConfirmed: true } };
  state.status = clone(status);
  state.sent = { lang: 'fr', relief: false, seq: 51, portalSessionId: status.portalSessionId, targetId: runtime.activeQuest() };
  runtime.observeQuestTick(status);
  const posts = recordPosts(() => status);
  runtime.openStaffScreen('manual');
  const wearIndex = briefing.scenes.findIndex((scene) => scene.id === 'wear');
  const total = briefing.scenes.slice(0, wearIndex).reduce((count, scene) => count + scene.cues.fr.length, 0);
  const position = briefing.scenes[0].cues.fr.length + 1;
  assert.equal(elements.get('staff').dataset.state, 'visitorActive');
  assert.equal(elements.get('staffNowTitle').textContent, '来場者が設定と説明を見ています');
  assert.deepEqual(visibleStaffItems(), [['ok', '言語：Français'], ['ok', 'ホラー軽減：なし'], ['plain', `説明 ${position} / ${total}`]]);
  assert.equal(elements.get('staffCycle2').getAttribute('aria-current'), 'step');
  assert.equal(elements.get('staffVisitorBtn').textContent, '来場者の画面に戻る');
  assert.equal(elements.get('staffDoneBtn').hidden, true, '主操作と同じボタンを上段に重ねない');
  assert.equal(state.briefing.pauseReasons.has('staff'), true);
  elements.get('staffVisitorBtn').dispatch('click');
  assert.equal(elements.get('staff').open, false);
  assert.equal(state.briefing.pauseReasons.has('staff'), false);
  assert.equal(state.view, 'briefing', '来場者の説明はそのまま');
  runtime.openStaffScreen('manual');
  const restart = elements.get('staffRestartBtn');
  assert.equal(restart.hidden, false);
  assert.equal(restart.disabled, false);
  restart.dispatch('pointerdown', { isPrimary: true, pointerId: 3, button: 0 });
  assert.ok(pendingDelays().includes(1500));
  restart.dispatch('pointercancel', { pointerId: 3 });
  assert.equal(pendingDelays().includes(1500), false, '途中で離すと送らない');
  assert.equal(posts.length, 0);
  restart.dispatch('pointerdown', { isPrimary: true, pointerId: 4, button: 0 });
  timers.runOne(1500); await runtime.staffAction();
  assert.equal(posts.filter((body) => body.staffReset).length, 1);
  assert.equal(elements.get('staff').dataset.state, 'prepare');
  assert.equal(elements.get('staffNowBody').textContent, '準備しています…');
  state.lang = 'ja';
});

test('装着前の確認は足りない項目を赤で示し、揃うまで長押しを受け付けない。キーボードでも長押しできる', async () => {
  prepareBriefing(clone(briefing)); state.staffPreview = false; state.ui = 'sent'; runtime.resetStaffTimers();
  const wearIndex = briefing.scenes.findIndex((scene) => scene.id === 'wear');
  state.briefing.sceneIndex = wearIndex - 1;
  state.briefing.cueIndex = briefing.scenes[wearIndex - 1].cues.ja.length - 1;
  state.briefing.handoffReady = true;
  const status = { ...clone(validStatus), questTick: 500, lang: 'en', relief: true, appliedSeq: 41, received: 41,
    pending: { lang: 'en', relief: true, seq: 41 },
    lastRequest: { tabletSessionId: runtime.tabletSessionId, seq: 41, lang: 'en', relief: true },
    staffSetup: { stage: 'ready', reason: '', positionConfirmed: true, position: 'confirmed', content: true,
      cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'trouble', problem: 'stale' }, { id: 'C', state: 'ok' }] },
    briefing: { seq: 41, revision: 0, completed: true, staffConfirmed: false } };
  state.status = status;
  state.sent = { lang: 'en', relief: true, seq: 41, portalSessionId: status.portalSessionId, targetId: runtime.activeQuest() };
  runtime.observeQuestTick(status);
  const posts = recordPosts(() => status);
  runtime.closeStaffScreen();
  runtime.openStaffScreen('manual');
  const prepare = elements.get('staffPrepareBtn');
  assert.equal(elements.get('staff').dataset.state, 'confirm');
  assert.equal(elements.get('staffHandback').hidden, false, '返却が未受理なら開き方に関係なく案内する');
  runtime.closeStaffScreen();
  runtime.openStaffScreen('manual');
  assert.equal(elements.get('staffHandback').hidden, false, '閉じて開き直しても3言語の返却案内を保つ');
  assert.deepEqual(visibleStaffItems(), [['ok', '言語：English'], ['ok', 'ホラー軽減：あり'],
    ['trouble', 'カメラ B：映像が止まっています'], ['ok', '位置合わせ']]);
  assert.equal(elements.get('staffNowItem0Mark').textContent, '●');
  assert.equal(elements.get('staffNowItem2Mark').textContent, '✕');
  assert.equal(elements.get('staffNowBody').textContent, '各項目に ● がつくと進めます。');
  assert.equal(prepare.textContent, '長押しで装着へ進む');
  assert.equal(prepare.disabled, true);
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 1, button: 0 });
  assert.equal(pendingDelays().includes(1200), false, '条件が揃わなければ押せない');
  assert.equal(elements.get('devCameraB').dataset.tone, 'trouble');
  assert.equal(elements.get('devCameraBMark').textContent, '✕');
  assert.equal(elements.get('devCameraBFix').textContent, 'スマホ B の FixedCam Streamer を開き直す');
  assert.equal(elements.get('devCameraBFix').hidden, false);
  assert.equal(elements.get('devCameraAMark').textContent, '●');
  assert.equal(elements.get('devCameraAFix').hidden, true);

  status.staffSetup.cameras[1] = { id: 'B', state: 'ok' };
  status.staffSetup.position = 'recenter';
  status.staffSetup.positionConfirmed = false;
  runtime.renderStaff();
  assert.deepEqual(visibleStaffItems().slice(2), [['ok', 'カメラ A・B・C'], ['trouble', '位置合わせ：やり直しが必要です']]);
  assert.equal(prepare.disabled, true);
  status.staffSetup.position = 'confirmed';
  status.staffSetup.positionConfirmed = true;
  status.staffSetup.content = false;
  runtime.renderStaff();
  assert.deepEqual(visibleStaffItems().at(-1), ['trouble', '体験の素材：読み込めていません']);
  assert.equal(elements.get('devContent').hidden, false);
  assert.equal(prepare.disabled, true);
  status.staffSetup.content = true;
  runtime.renderStaff();
  assert.equal(prepare.disabled, false);
  assert.equal(elements.get('devContent').hidden, false);
  assert.equal(elements.get('devContentMark').textContent, '●');
  assert.equal(elements.get('staffNowBody').textContent, '項目を確かめたら下のボタンを長押しします。');

  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 10, button: 0 });
  prepare.dispatch('pointerup', { pointerId: 11 });
  assert.ok(pendingDelays().includes(1200), '別のpointerを離しても押下を保つ');
  prepare.dispatch('pointermove', { pointerId: 10, clientX: 50, clientY: 25 });
  assert.ok(pendingDelays().includes(1200));
  prepare.dispatch('pointermove', { pointerId: 10, clientX: 101, clientY: 25 });
  assert.equal(pendingDelays().includes(1200), false, 'pointer capture中でも矩形外へ出たら取り消す');
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 11, button: 0 });
  prepare.dispatch('blur');
  assert.equal(pendingDelays().includes(1200), false, 'フォーカスを失ったら取り消す');
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 12, button: 0 });
  document.hidden = true;
  dispatchDocument('visibilitychange');
  assert.equal(pendingDelays().includes(1200), false, '背景へ移ったら取り消す');
  document.hidden = false;
  dispatchDocument('visibilitychange');
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 13, button: 0 });
  context.__tabletLifecycle(true);
  assert.equal(pendingDelays().includes(1200), false, 'Nativeの休止でも取り消す');
  context.__tabletLifecycle(false);
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 14, button: 0 });
  prepare.disabled = true;
  timers.runOne(1200); await runtime.staffAction();
  assert.equal(posts.filter((body) => body.staffConfirmed === true).length, 0, '完了時に無効なら実行しない');
  prepare.disabled = false;
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 15, button: 0 });
  prepare.hidden = true;
  timers.runOne(1200); await runtime.staffAction();
  assert.equal(posts.filter((body) => body.staffConfirmed === true).length, 0, '完了時に非表示なら実行しない');
  prepare.hidden = false;
  prepare.dispatch('pointerdown', { isPrimary: true, pointerId: 16, button: 0 });
  prepare.inert = true;
  timers.runOne(1200); await runtime.staffAction();
  assert.equal(posts.filter((body) => body.staffConfirmed === true).length, 0, '完了時に非アクティブなら実行しない');
  prepare.inert = false;

  prepare.dispatch('keydown', { key: 'Enter', repeat: false });
  assert.ok(pendingDelays().includes(1200));
  assert.ok(Array.from(timers.tasks.values()).some((task) => task.interval && task.delay === 40),
    '動きを減らす設定でも満ちる量を描き続ける');
  assert.equal(prepare.style['--hold'], '0.02');
  prepare.dispatch('keyup', { key: 'Enter' });
  assert.equal(pendingDelays().includes(1200), false, 'キーを離すと取り消す');
  assert.equal(prepare.style['--hold'], '0');
  prepare.dispatch('keydown', { key: ' ', repeat: false });
  prepare.dispatch('keydown', { key: ' ', repeat: true });
  assert.equal(pendingDelays().filter((delay) => delay === 1200).length, 1);
  timers.runOne(1200); await runtime.staffAction();
  assert.equal(posts.filter((body) => body.staffConfirmed === true).length, 1);
  assert.equal(runtime.staffConfirmationRequested(), true);
  assert.equal(prepare.disabled, true);
  assert.equal(elements.get('staffNowBody').textContent, 'クエストの確認を待っています。');
  assert.equal(Array.from(timers.tasks.values()).some((task) => task.interval && task.delay === 40), false, '完了後は描画の間隔を止める');
});

test('体験中は段と経過を出し、終わりの演出と終了を分けて、終了後に次の準備を始められる', async () => {
  assert.equal(runtime.staffRunMeta({ run: { phase: 'INTRO', lap: 0, laps: 3, sec: 12 } }, 'INTRO'), '導入 · 0:12');
  assert.equal(runtime.staffRunMeta({ run: { phase: 'RUN', lap: 2, laps: 3, sec: 84 } }, 'RUN'), '2 周目 · 1:24');
  assert.equal(runtime.staffRunMeta({ run: { phase: 'RUN', lap: 4, laps: 3, sec: 150 } }, 'RUN'), '最後の区間 · 2:30');
  assert.equal(runtime.staffRunMeta({}, 'INTRO'), '導入', 'run の無い旧 Quest は phase から推定する');
  assert.equal(runtime.staffRunMeta({}, 'RUN'), '');
  prepareStaff(); runtime.resetStaffTimers();
  let status = { ...clone(validStatus), questTick: 400, phase: 'RUN', staffSetup: { stage: 'playing', reason: '',
    positionConfirmed: true, run: { phase: 'RUN', lap: 2, laps: 3, sec: 84, outro: 'off', ending: '' } } };
  const posts = recordPosts(() => status);
  runtime.closeStaffScreen();
  await runtime.poll();
  assert.equal(elements.get('staff').open, true, '体験が始まるとスタッフ画面へ切り替える');
  assert.equal(elements.get('staff').dataset.state, 'playing');
  assert.equal(elements.get('staffNowTitle').textContent, '体験中');
  assert.equal(elements.get('staffNowMeta').textContent, '2 周目 · 1:24');
  assert.equal(elements.get('staffNowBody').textContent,
    '中止するとき：ヘッドセットを外すのを手伝います。右コントローラーの A を 2 秒押し続けます。');
  assert.equal(elements.get('staffCycle4').getAttribute('aria-current'), 'step');
  assert.equal(elements.get('staffCycle3').dataset.done, 'true');
  for (const id of ['staffResetBtn', 'staffVisitorBtn', 'staffPrepareBtn', 'staffCollectBtn', 'staffRestartBtn', 'staffRetryBtn', 'staffDoneBtn']) {
    assert.equal(elements.get(id).hidden, true, id);
  }
  runtime.closeStaffScreen();
  status = { ...status, questTick: 401 };
  await runtime.poll();
  assert.equal(elements.get('staff').open, false, '開くのは体験が始まったときだけ');
  status = { ...status, questTick: 402, phase: 'END', staffSetup: { ...status.staffSetup, stage: 'ended',
    run: { phase: 'END', lap: 4, laps: 3, sec: 176, outro: 'playing', ending: '' } } };
  await runtime.poll();
  assert.equal(elements.get('staff').open, true, '終幕を初めて観測したときもスタッフ画面へ戻す');
  assert.equal(elements.get('staffNowTitle').textContent, '終わりの演出中です');
  assert.equal(elements.get('staffNowBody').textContent, '演出が終わるまで待ってください。まだヘッドセットは外しません。');
  assert.equal(elements.get('staffResetBtn').hidden, true);
  status = { ...status, questTick: 403, staffSetup: { ...status.staffSetup,
    run: { ...status.staffSetup.run, outro: 'done', ending: 'trapped' } } };
  await runtime.poll();
  assert.equal(elements.get('staff').dataset.state, 'finished');
  assert.equal(elements.get('staffNowTitle').textContent, '体験が終わりました');
  assert.equal(elements.get('staffNowNote').textContent, '結末：人形');
  assert.equal(elements.get('staffCollectBtn').hidden, false);
  assert.equal(elements.get('staffCollectBtn').disabled, false);
  assert.deepEqual(visibleStaffItems().map(([, text]) => text), [
    'ヘッドセットを外すのを手伝う', 'ヘッドセットとヘッドフォンを清拭して台へ戻す', '左コントローラーを清拭して台へ戻す']);
  assert.equal(elements.get('staffCycle5').getAttribute('aria-current'), 'step');
  status = { ...status, questTick: 404, staffSetup: { ...status.staffSetup,
    run: { ...status.staffSetup.run, ending: 'released' } } };
  await runtime.poll();
  assert.equal(elements.get('staffNowNote').textContent, '結末：帰還');
  status = { ...clone(validStatus), questTick: 405, phase: 'END', staffSetup: { stage: 'playing', reason: '', positionConfirmed: true } };
  await runtime.poll();
  assert.equal(elements.get('staff').dataset.state, 'outro', '旧 Quest は END から8秒を終わりの演出とみなす');
  runtime.setLegacyEndSince(Date.now() - 8000);
  runtime.renderStaff();
  assert.equal(elements.get('staff').dataset.state, 'finished');
  assert.equal(elements.get('staffCollectBtn').disabled, false);
  assert.equal(elements.get('staffCollectBtn').dispatch('click'), undefined, '回収後もクリックだけでは進めない');
  elements.get('staffCollectBtn').dispatch('pointerdown', { isPrimary: true, pointerId: 20, button: 0 });
  timers.runOne(1500); await runtime.staffAction();
  assert.equal(posts.filter((body) => body.staffReset).length, 1);
  assert.equal(elements.get('staffCollectBtn').disabled, true);
});

test('背景中にRUNを見逃してendedを初めて観測してもスタッフ画面へ戻し、説明プレビューは遮らない', () => {
  prepareStaff(); runtime.resetStaffTimers();
  runtime.closeStaffScreen();
  runtime.observeStaffStage({ phase: 'INTRO', staffSetup: { stage: 'settings' } });
  runtime.observeStaffStage({ phase: 'END', staffSetup: { stage: 'ended' } });
  assert.equal(elements.get('staff').open, true);
  runtime.closeStaffScreen();
  runtime.resetStaffTimers();
  state.staffPreview = true;
  runtime.observeStaffStage({ phase: 'RUN', staffSetup: { stage: 'playing' } });
  runtime.observeStaffStage({ phase: 'END', staffSetup: { stage: 'ended' } });
  assert.equal(elements.get('staff').open, false, 'スタッフの説明確認中は自動で割り込まない');
  state.staffPreview = false;
});

test('来場者の画面でQuestが6秒以上止まると3言語でスタッフを呼ぶ', async () => {
  prepareStaff(); runtime.resetStaffTimers();
  runtime.closeStaffScreen();
  state.view = 'edit';
  let status = { ...clone(validStatus), questTick: 700, staffSetup: { stage: 'settings', reason: 'settings', positionConfirmed: true } };
  fetchHandler = async () => ({ ok: true, status: 200, json: async () => clone(status) });
  await runtime.poll();
  assert.equal(elements.get('callStaff').hidden, true);
  for (const line of ['スタッフをお呼びください', 'Please call a member of staff.', 'Veuillez appeler un membre du personnel.']) {
    assert.ok(html.includes(line), line);
  }
  fetchHandler = async () => { throw new TypeError('offline'); };
  await runtime.poll();
  assert.equal(elements.get('callStaff').hidden, true, '6秒までは出さない');
  runtime.setQuestTroubleSince(Date.now() - 6000);
  runtime.renderCallStaff();
  assert.equal(elements.get('callStaff').hidden, false);
  assert.equal(state.view, 'edit', '来場者の画面の他の部分は変えない');
  status = { ...status, questTick: 701 };
  fetchHandler = async () => ({ ok: true, status: 200, json: async () => clone(status) });
  await runtime.poll();
  assert.equal(elements.get('callStaff').hidden, true, '戻ったら消す');
  shiftClock(5000);
  try {
    runtime.observeQuestTrouble();
    runtime.renderCallStaff();
    assert.equal(elements.get('callStaff').hidden, true, '休止に入った直後は出さない');
  } finally {
    restoreClock();
  }
  shiftClock(11000);
  try {
    runtime.observeQuestTrouble();
    runtime.renderCallStaff();
    assert.equal(elements.get('callStaff').hidden, false, '休止が6秒続いたら出す');
  } finally {
    restoreClock();
  }
  runtime.resetStaffTimers();
  runtime.renderCallStaff();
  state.view = 'title';
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
