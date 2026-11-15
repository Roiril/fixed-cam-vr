// 当日の画面の本体。通信と保存と面の切り替えだけを持つ。判定は model.js、表示は各面。
import * as model from './model.js';
import { h, setText, request, session, ago } from './ui.js';
import { mountBoard } from './board.js';
import { mountMaterials } from './materials.js';
import { mountLive } from './live.js';
import { mountHelp } from './help.js';
import { demoStatus, demoContent, demoShoot, demoInspection } from './demo.js';

const $ = id => document.getElementById(id);
const demo = new URLSearchParams(location.search).get('demo');
const VIEWS = { ready: '準備', shoot: '素材', live: '本番', help: '困ったとき' };
// 2026-09 の 6 ページ版の入口。印刷物や履歴から来ても迷わせない。
const LEGACY = { setup: 'ready/desk', devices: 'ready/cameras', 'tablet-check': 'ready/tablet', content: 'shoot/compare',
  build: 'ready/build', checks: 'ready/human' };
const KEYS = { checks: demo ? 'mawarimi-day-checks-demo' : 'mawarimi-day-checks-v2',
  review: demo ? 'mawarimi-day-review-demo' : 'mawarimi-day-review-v3' };

const state = {
  snapshot: null, failed: false, content: null, contentFailed: false,
  plan: null, manifest: null, platesPayload: null, capabilities: null,
  inspection: null, inspectionBusy: false, inspectionError: '',
  checksRecord: session.get(KEYS.checks), reviewRecord: session.get(KEYS.review),
};
let lastSub = '';
let active = '', busy = { status: false, content: false, shoot: false }, timers = {};

function derive(now = Date.now()) {
  const content = state.contentFailed ? null : state.content;
  const shots = model.summarizeShots(state.plan, state.manifest);
  const plates = model.summarizePlates(state.platesPayload, now);
  const review = model.validReview(state.reviewRecord, content, state.snapshot, now);
  const checks = model.validChecks(state.checksRecord, state.snapshot, now);
  const gates = model.gates({ snapshot: state.snapshot, failed: state.failed, content: state.content,
    contentFailed: state.contentFailed, shots, plates, review, checks, inspection: state.inspection }, now);
  const outage = model.outage(state.snapshot, now, state.failed, busy.status);
  return { now, shots, plates, review, checks, gates, outage, verdict: model.verdict(gates),
    lamps: model.lamps(state.snapshot, now, state.failed), alerts: model.liveAlerts(state.snapshot, now, state.failed),
    checksOpen: model.humanChecksOpen(gates), offline: outage?.kind === 'offline', live: !outage };
}

// ---- 面への橋渡し ----
const ctx = {
  demo: !!demo, request,
  refresh() { pollStatus(true); pollContent(); pollShoot(); },
  setCheck(id, value) {
    const values = { ...model.validChecks(state.checksRecord, state.snapshot), [id]: value };
    state.checksRecord = { signature: model.signature(state.snapshot), at: Date.now(), values };
    session.set(KEYS.checks, state.checksRecord); render();
  },
  clearChecks() { state.checksRecord = null; session.set(KEYS.checks, null); render(); },
  setReview(key, value) {
    const content = state.contentFailed ? null : state.content;
    const current = model.validReview(state.reviewRecord, content, state.snapshot);
    state.reviewRecord = { ...current, [key]: value, at: Date.now(), contentId: content?.preparation?.contentId,
      drift: model.driftedCameras(state.snapshot).join(',') };
    session.set(KEYS.review, state.reviewRecord); render();
  },
  /** 採用が変わった。素材の版が変わるので人の確認は自然に外れる。照合を取り直す。 */
  materialsChanged() { pollContent(); pollShoot(); },
  setPlates(payload) { state.platesPayload = payload; render(); },
  async runInspection() {
    if (demo || state.inspectionBusy) return;
    state.inspectionBusy = true; state.inspectionError = ''; render();
    try {
      const result = await request('/onsite/check', { method: 'POST', body: {}, timeout: 245_000 });
      if (!result.ok) throw new Error(result.error || '点検の結果がありません');
      state.inspection = { ...result, signature: model.signature(state.snapshot) };
    } catch (error) { state.inspectionError = error.message; }
    finally { state.inspectionBusy = false; render(); }
  },
  state: () => state,
};

const views = {
  ready: mountBoard($('view-ready'), ctx),
  shoot: mountMaterials($('view-shoot'), ctx),
  live: mountLive($('view-live'), ctx),
  help: mountHelp($('view-help'), ctx),
};

// ---- 上の帯 ----
const lampTargets = { desk: 'ready/desk', A: 'ready/cameras', B: 'ready/cameras', C: 'ready/cameras',
  alpha: 'ready/quests', beta: 'ready/quests', tablet: 'ready/tablet' };
const lampNodes = new Map();
function renderBar(d) {
  for (const lamp of d.lamps) {
    let node = lampNodes.get(lamp.id);
    if (!node) {
      node = h('a.lamp', { href: `#${lampTargets[lamp.id]}` }, h('span', { text: lamp.label, 'aria-hidden': 'true' }), h('span.glyph', { 'aria-hidden': 'true' }));
      lampNodes.set(lamp.id, node); $('lamps').append(node);
    }
    if (node.dataset.state !== lamp.status) node.dataset.state = lamp.status;
    const name = { desk: '卓', alpha: 'Quest α', beta: 'Quest β', tablet: '博士タブレット' }[lamp.id] || `カメラ ${lamp.id}`;
    const label = `${name}: ${lamp.title}`;
    if (node.title !== label) { node.title = label; node.setAttribute('aria-label', label); }
  }
  const age = state.snapshot?.observedAt ? Math.max(0, d.now / 1000 - state.snapshot.observedAt) : NaN;
  setText($('age'), busy.status && !state.snapshot ? '確認中…' : Number.isFinite(age) ? `${ago(age)}に確認` : '未確認');
  const banner = $('outage');
  banner.hidden = !d.outage;
  banner.dataset.tone = d.outage?.waiting ? 'wait' : 'error';
  banner.setAttribute('role', d.outage?.waiting ? 'status' : 'alert');
  if (d.outage) { setText(banner.querySelector('.outage-title'), d.outage.title); setText(banner.querySelector('.outage-detail'), d.outage.detail); }
}

function render() {
  const d = derive();
  renderBar(d);
  views[active]?.update(state, d);
}

// ---- 通信 ----
async function pollStatus(force = false) {
  if (busy.status || (document.hidden && !force)) return;
  clearTimeout(timers.status); busy.status = true;
  if (!model.isFresh(state.snapshot)) render();   // 返事待ちの表示を、問い合わせを始めた時点で出す
  try {
    const next = demo ? demoStatus(demo) : await request('/ops/status', { timeout: 40_000 });
    if (!next?.ok || !Array.isArray(next.cameras) || !Array.isArray(next.quests)) throw new Error('形式が違います');
    state.snapshot = next; state.failed = false;
  } catch { state.failed = true; }
  finally { busy.status = false; render(); timers.status = setTimeout(pollStatus, 5000); }
}
async function pollContent() {
  if (busy.content || document.hidden) return;
  clearTimeout(timers.content); busy.content = true;
  try {
    const next = demo ? demoContent(demo) : await request('/ops/content', { timeout: 90_000 });
    if (!next?.ok || !Array.isArray(next.effects) || !Array.isArray(next.quests)) throw new Error('形式が違います');
    state.content = next; state.contentFailed = false;
  } catch { state.contentFailed = true; }
  finally { busy.content = false; render(); timers.content = setTimeout(pollContent, 15_000); }
}
async function pollShoot() {
  if (busy.shoot || document.hidden) return;
  clearTimeout(timers.shoot); busy.shoot = true;
  try {
    if (demo) Object.assign(state, demoShoot(demo));
    else {
      const [plan, manifest, plates] = await Promise.allSettled([request('/shoot/plan?cam=A'), request('/shoot/manifest'), request('/shoot/plates')]);
      state.plan = plan.status === 'fulfilled' ? plan.value : null;
      state.manifest = manifest.status === 'fulfilled' ? manifest.value : null;
      state.platesPayload = plates.status === 'fulfilled' ? plates.value : null;
    }
  } finally { busy.shoot = false; render(); timers.shoot = setTimeout(pollShoot, 15_000); }
}
async function loadOnce() {
  try { state.capabilities = demo ? { ok: true, mode: 'preparation', shooting: true, adoption: true, plateCapture: true } : await request('/ops/capabilities'); }
  catch { state.capabilities = null; }
  // 直前の点検は、30 分以内ならそのまま使う（開き直すたびに 40 秒待たせない）。
  try {
    const last = demo ? demoInspection(demo) : await request('/onsite/check');
    if (last?.ok && Array.isArray(last.rows)) state.inspection = { ...last, signature: null };
  } catch { /* まだ 1 度も点検していない */ }
  render();
}

// ---- 面の切り替え ----
function route() {
  let raw = decodeURIComponent(location.hash.slice(1));
  const query = raw.includes('?') ? raw.slice(raw.indexOf('?') + 1) : '';
  raw = raw.split('?')[0];
  if (LEGACY[raw]) raw = LEGACY[raw];
  let [view, ...rest] = raw.split('/');
  if (!VIEWS[view]) { view = 'ready'; rest = []; }
  let sub = rest.join('/');
  const shot = new URLSearchParams(query).get('shot');
  if (view === 'shoot' && shot) sub = `pov/${shot}`;
  if (active && active !== view) views[active].hide?.();
  const changed = active !== view;
  active = view;
  for (const id of Object.keys(VIEWS)) $(`view-${id}`).hidden = id !== view;
  for (const link of document.querySelectorAll('[data-view]')) {
    if (link.dataset.view === view) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
  }
  document.title = `${VIEWS[view]} — 廻リ視 当日`;
  const subChanged = lastSub !== sub; lastSub = sub;
  if (changed) setText($('announce'), `${VIEWS[view]}の面です`);
  // 面を替えたときと、スマホで一覧から詳細へ入ったときは頭から見せる。
  if (changed || (subChanged && matchMedia('(max-width: 900px)').matches)) window.scrollTo(0, 0);
  views[view].route?.(sub);
  render();
}

// 点検の結果は、機器の構成が分かった時点で初めて今の機器に結び付ける。
function bindInspection() {
  if (state.inspection && state.inspection.signature === null && state.snapshot && !state.failed)
    state.inspection.signature = model.signature(state.snapshot);
}

// ---- 明暗 ----
const themeButton = $('theme');
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  themeButton.setAttribute('aria-label', theme === 'dark' ? '明るい表示にする' : '暗い表示にする');
  document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#16181b' : '#f4f5f6';
}
let theme = document.documentElement.dataset.theme || 'dark';
applyTheme(theme);
themeButton.addEventListener('click', () => {
  theme = theme === 'dark' ? 'light' : 'dark'; applyTheme(theme);
  try { localStorage.setItem('mawarimi-day-theme', theme); } catch { /* 保存できなくても切り替わる */ }
});

$('demo').hidden = !demo;
addEventListener('hashchange', route);
document.addEventListener('visibilitychange', () => { if (!document.hidden) ctx.refresh(); });
setInterval(() => { if (!document.hidden) { bindInspection(); render(); } }, 1000);
route();
loadOnce();
pollStatus(true); pollContent(); pollShoot();
