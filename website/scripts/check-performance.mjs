import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// Exercise the actual parent controller through viewport, entrance and power changes.
class Element {
  constructor() {
    this.events = {};
    this.classes = new Set();
    this.classList = {
      contains: name => this.classes.has(name),
      remove: name => this.classes.delete(name),
      toggle: (name, value) => value ? this.classes.add(name) : this.classes.delete(name),
    };
    this.style = {};
    this.dataset = {};
    this.sent = [];
    this.contentWindow = { postMessage: message => this.sent.push(message) };
  }
  addEventListener(name, callback) { this.events[name] = callback; }
  querySelectorAll() { return []; }
  setAttribute() {}
  focus() {}
  remove() { frames.splice(frames.indexOf(this), 1); }
}
const elements = new Map();
const frames = [];
let viewport;
let entrance = true;
let timerId = 0;
const mutations = [];
const document = {
  hidden: false,
  documentElement: new Element(), body: new Element(), events: {},
  querySelector(selector) {
    if (selector === '#entrance[open]') return entrance ? {} : null;
    if (!elements.has(selector)) elements.set(selector, new Element());
    return elements.get(selector);
  },
  createElement() { return new Element(); },
  addEventListener(name, callback) { this.events[name] = callback; },
};
document.querySelector('#game-stage').append = frame => { frames.push(frame); };
const window = {
  MAWARIMI_EXPERIENCE: { gameUrl: 'perspective-game/?embedded=1' }, events: {},
  addEventListener(name, callback) { this.events[name] = callback; },
  IntersectionObserver: true,
};
const context = {
  document, window, location: new URL('https://mawarimi.vercel.app/'), URL,
  setTimeout: () => ++timerId, clearTimeout() {},
  IntersectionObserver: class {
    constructor(callback) { viewport = callback; }
    observe() {}
  },
  MutationObserver: class {
    constructor(callback) { mutations.push(callback); }
    observe() {}
  },
};
await vm.runInNewContext(await readFile(new URL('../handheld.js', import.meta.url), 'utf8'), context);
assert.equal(frames.length, 0, 'offscreen game must not load');
viewport([{ isIntersecting: true }]);
assert.equal(frames.length, 0, 'entrance must defer even an intersecting game');
entrance = false;
mutations[1]();
assert.equal(frames.length, 1, 'visible game powers on automatically');
const frame = frames[0];
frame.events.load();
assert.equal(frame.sent.at(-1).active, true);
viewport([{ isIntersecting: false }]);
assert.equal(frame.sent.at(-1).active, false, 'offscreen game pauses');
assert.equal(frames[0], frame, 'pause preserves the existing game');
viewport([{ isIntersecting: true }]);
assert.equal(frame.sent.at(-1).active, true, 'visible game resumes');
document.hidden = true;
document.events.visibilitychange();
assert.equal(frame.sent.at(-1).active, false, 'hidden tab pauses');
document.hidden = false;
document.events.visibilitychange();
entrance = true;
mutations[1]();
assert.equal(frame.sent.at(-1).active, false, 'replayed entrance pauses');
entrance = false;
mutations[1]();
assert.equal(frame.sent.at(-1).active, true);
elements.get('#game-close').events.click();
assert.equal(frames.length, 0, 'power off destroys iframe');
viewport([{ isIntersecting: false }]);
viewport([{ isIntersecting: true }]);
assert.equal(frames.length, 0, 'scrolling must not undo explicit power off');
elements.get('#game-launch').events.click();
assert.equal(frames.length, 1, 'explicit restart works');
const bundle = await readFile(new URL('../perspective-game/assets/index-BZnkwpUb.js', import.meta.url), 'utf8');
assert.equal(bundle.split('!document.hidden&&window.MAWARIMI_GAME_ACTIVE!==false').length, 2,
  'actual render loop must honor the parent pause gate');
console.log('Performance checks passed: deferred startup, entrance/replay, offscreen/tab pause, preserved state, power off/restart, renderer gate.');
