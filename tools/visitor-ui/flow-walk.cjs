#!/usr/bin/env node
'use strict';

// 起動 → スタッフの準備 → 来場者のタブレット → 装着 → 回収 → リセット を、人が押す順に押して毎画面を撮る。
// 本編の最中（playing）は、異常の知らせと「体験を中断してやり直す」だけを見る（trouble 区間）。
// 画面は本番の index.html を代役サーバ（staff-mock-server.cjs）経由で実ブラウザに出す。
//
//   node tools/visitor-ui/flow-walk.cjs                       # 日本語・通常の一巡
//   node tools/visitor-ui/flow-walk.cjs --lang en --out <dir>
//   node tools/visitor-ui/flow-walk.cjs --only launch,reset    # 区間を絞る（区間名は --list）
//   node tools/visitor-ui/flow-walk.cjs --web-root <dir>       # 別の版の画面で歩く（直す前と比べる・校正）
//
// 出力: <out>/NN-<id>.png ・ steps.json（画面ごとの可視文字・押せるボタン・状態）・ index.html（絵コンテ）
// ⚠ Quest の応答は代役が真似る。実機の bind・時間・音は確かめられない（tablet/README.md の実機試験が別にある）。

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const { spawn } = require('node:child_process');

const root = path.resolve(__dirname, '..', '..');
const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };
const flag = (name) => args.includes(`--${name}`);

const WIDTH = Number(arg('width', 1097));
const HEIGHT = Number(arg('height', 686));
const LANG = arg('lang', 'ja');
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 13);
const driveRoot = 'G:\\マイドライブ\\shubie\\screenshots\\fixed-cam-vr\\flow-walk';
const OUT = path.resolve(arg('out', fs.existsSync('G:\\マイドライブ\\shubie') ? path.join(driveRoot, `${stamp}-${LANG}`) : path.join(root, 'output', 'flow-walk', `${stamp}-${LANG}`)));
const ONLY = (arg('only', '') || '').split(',').filter(Boolean);

function browserPath() {
  const candidates = [
    process.env.FLOW_WALK_BROWSER,
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  ].filter(Boolean);
  const found = candidates.find((p) => fs.existsSync(p));
  if (!found) throw new Error('Chrome / Edge が見つかりません（FLOW_WALK_BROWSER で指定できます）');
  return found;
}

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitHttp(url, timeoutMs = 10000) {
  const end = Date.now() + timeoutMs;
  for (;;) {
    try { const r = await fetch(url, { signal: AbortSignal.timeout(1000) }); if (r.ok) return r; } catch (_) { /* まだ */ }
    if (Date.now() > end) throw new Error(`${url} が立ち上がりません`);
    await sleep(150);
  }
}

class Cdp {
  constructor(wsUrl) { this.wsUrl = wsUrl; this.nextId = 1; this.pending = new Map(); this.listeners = []; }
  async open() {
    this.ws = new WebSocket(this.wsUrl);
    await new Promise((resolve, reject) => { this.ws.addEventListener('open', resolve, { once: true }); this.ws.addEventListener('error', reject, { once: true }); });
    this.ws.addEventListener('message', (event) => {
      const msg = JSON.parse(event.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id); this.pending.delete(msg.id);
        if (msg.error) reject(new Error(`${msg.error.message}`)); else resolve(msg.result);
      } else if (msg.method) this.listeners.forEach((fn) => fn(msg));
    });
  }
  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); this.ws.send(JSON.stringify({ id, method, params })); });
  }
  on(fn) { this.listeners.push(fn); }
  close() { try { this.ws.close(); } catch (_) { /* 済み */ } }
}

// ---- ページ内で使う観測（可視の文字・ボタン・どの面が出ているか） ----
const OBSERVE = `(() => {
  // モーダルが開いていればその中だけが見えている（裏の画面は inert）。
  const modal = document.querySelector('dialog:modal');
  const vis = (el) => {
    if (modal && !modal.contains(el)) return false;
    for (let e = el; e && e !== document.documentElement; e = e.parentElement) {
      if (e.hidden || e.inert) return false;
      const cs = getComputedStyle(e);
      if (cs.display === 'none' || cs.visibility === 'hidden' || Number(cs.opacity) < 0.05) return false;
    }
    const r = el.getBoundingClientRect();
    return r.width > 1 && r.height > 1 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
  };
  const texts = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const t = n.textContent.replace(/\\s+/g, ' ').trim();
    if (!t || !n.parentElement || ['SCRIPT', 'STYLE'].includes(n.parentElement.tagName)) continue;
    if (!vis(n.parentElement)) continue;
    const r = n.parentElement.getBoundingClientRect();
    const cs = getComputedStyle(n.parentElement);
    texts.push({ t, y: Math.round(r.top), x: Math.round(r.left), size: parseFloat(cs.fontSize), tag: n.parentElement.tagName.toLowerCase(), id: n.parentElement.id || n.parentElement.closest('[id]')?.id || '' });
  }
  const buttons = [...document.querySelectorAll('button, [role=button], input[type=radio], input[type=checkbox]')]
    .filter((b) => vis(b.tagName === 'INPUT' ? (b.closest('label') || b) : b))
    .map((b) => {
      const r = (b.tagName === 'INPUT' ? (b.closest('label') || b) : b).getBoundingClientRect();
      return { id: b.id, label: (b.innerText || b.closest('label')?.innerText || b.getAttribute('aria-label') || '').replace(/\\s+/g, ' ').trim(),
        disabled: !!b.disabled, checked: b.checked ?? null, w: Math.round(r.width), h: Math.round(r.height), x: Math.round(r.left), y: Math.round(r.top) };
    });
  const views = [...document.querySelectorAll('section[id], dialog[id], [id$=View], #staff, #staffHandback')]
    .filter((s) => vis(s)).map((s) => s.id);
  // モーダルは自分の中で縦に送られるので、はみ出しはモーダルの中で測る。
  const box = modal || document.documentElement;
  const overflow = { x: box.scrollWidth > (modal ? modal.clientWidth : innerWidth) + 1, y: box.scrollHeight > (modal ? modal.clientHeight : innerHeight) + 1 };
  return { texts, buttons, views, overflow, viewport: { w: innerWidth, h: innerHeight } };
})()`;

async function main() {
  if (flag('list')) { console.log(SECTIONS.map((s) => `${s.id}\t${s.title}`).join('\n')); return; }
  fs.mkdirSync(OUT, { recursive: true });
  const mockPort = await freePort();
  const cdpPort = await freePort();
  const webRootArg = arg('web-root', '');
  const mock = spawn(process.execPath, [path.join(__dirname, 'staff-mock-server.cjs'), '--port', String(mockPort),
    ...(webRootArg ? ['--web-root', path.resolve(webRootArg)] : [])], { stdio: ['ignore', 'pipe', 'pipe'] });
  const mockLog = [];
  mock.stdout.on('data', (d) => mockLog.push(String(d)));
  mock.stderr.on('data', (d) => mockLog.push(String(d)));
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'flow-walk-'));
  const browser = spawn(browserPath(), [
    '--headless=new', `--remote-debugging-port=${cdpPort}`, `--user-data-dir=${profile}`,
    `--window-size=${WIDTH},${HEIGHT}`, '--autoplay-policy=no-user-gesture-required', '--mute-audio',
    '--no-first-run', '--no-default-browser-check', '--hide-scrollbars', 'about:blank',
  ], { stdio: 'ignore' });
  const base = `http://127.0.0.1:${mockPort}`;
  const steps = [];
  let cdp;
  try {
    await waitHttp(`${base}/_state`);
    await waitHttp(`http://127.0.0.1:${cdpPort}/json/version`);
    const pages = await (await fetch(`http://127.0.0.1:${cdpPort}/json/list`)).json();
    cdp = new Cdp(pages.find((p) => p.type === 'page').webSocketDebuggerUrl);
    await cdp.open();
    const consoleErrors = [];
    const dialogEvents = [];
    cdp.on((m) => {
      if (m.method === 'Runtime.exceptionThrown') consoleErrors.push(m.params.exceptionDetails?.exception?.description || m.params.exceptionDetails?.text);
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'info' && String(m.params.args[0]?.value).startsWith('[flow-walk]')) {
        dialogEvents.push(m.params.args.map((a) => a.value ?? a.description).join(' '));
      }
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') consoleErrors.push(m.params.args.map((a) => a.value ?? a.description).join(' '));
    });
    await cdp.send('Runtime.enable');
    await cdp.send('Page.enable');
    // スタッフ画面が「誰の操作でもなく」閉じた／開いたときに経路を残す（画面が飛んだ理由はここでしか分からない）。
    await cdp.send('Page.addScriptToEvaluateOnNewDocument', { source: `(() => {
      const wrap = (name) => { const orig = HTMLDialogElement.prototype[name];
        HTMLDialogElement.prototype[name] = function (...a) { if (this.id === 'staff') console.info('[flow-walk] staff.' + name, new Error().stack.split('\\n').slice(2, 6).join(' <- ')); return orig.apply(this, a); }; };
      wrap('close'); wrap('showModal');
    })()` });
    await cdp.send('Emulation.setDeviceMetricsOverride',{ width: WIDTH, height: HEIGHT, deviceScaleFactor: 1, mobile: true });
    await cdp.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });

    const api = makeApi({ cdp, base, steps, consoleErrors, dialogEvents });
    await api.mock('_reset');
    for (const section of SECTIONS) {
      if (ONLY.length && !ONLY.includes(section.id)) continue;
      api.section = section;
      console.log(`== ${section.id}: ${section.title}`);
      try {
        await section.run(api);
      } catch (error) {
        // 止まった画面そのものが一番の手掛かりなので、落ちる前に撮る。
        await api.shot('FAILED', { who: '—', note: `止まった: ${error.message}` }).catch(() => {});
        throw error;
      }
    }
  } finally {
    fs.writeFileSync(path.join(OUT, 'steps.json'), JSON.stringify({ lang: LANG, viewport: { w: WIDTH, h: HEIGHT }, steps }, null, 2));
    fs.writeFileSync(path.join(OUT, 'index.html'), storyboard(steps));
    fs.writeFileSync(path.join(OUT, 'mock.log'), mockLog.join(''));
    if (cdp) cdp.close();
    browser.kill(); mock.kill();
    await sleep(300);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch (_) { /* ブラウザが掴んでいる */ }
    console.log(`\n${steps.length} 画面 → ${OUT}`);
    if (expectFailures.length) {
      console.log(`期待外れ ${expectFailures.length} 件:\n  ${expectFailures.join('\n  ')}`);
      process.exitCode = 1;
    }
  }
}

const expectFailures = [];

function makeApi({ cdp, base, steps, consoleErrors, dialogEvents }) {
  const api = {
    section: null,
    async eval(expression) {
      const r = await cdp.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
      if (r.exceptionDetails) throw new Error(`eval 失敗: ${r.exceptionDetails.exception?.description || r.exceptionDetails.text}`);
      return r.result.value;
    },
    async mock(pathname, body) {
      const r = await fetch(`${base}/${pathname}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });
      if (!r.ok) throw new Error(`mock ${pathname} → ${r.status} ${await r.text()}`);
      return r.json();
    },
    scenario: (name) => api.mock('_scenario', { scenario: name }),
    state: (patch) => api.mock('_state', patch),
    async status() { return (await (await fetch(`${base}/_state`)).json()).status; },
    async open() {
      await cdp.send('Page.navigate', { url: `${base}/?lang=${LANG}` });
      await api.waitFor('document.readyState === "complete"', 10000, 'ページの読み込み');
      await sleep(800);
    },
    async waitFor(expr, timeoutMs = 8000, what = expr) {
      const end = Date.now() + timeoutMs;
      for (;;) {
        try { if (await api.eval(`Boolean(${expr})`)) return true; } catch (_) { /* 途中 */ }
        if (Date.now() > end) throw new Error(`待ち切れません: ${what}`);
        await sleep(120);
      }
    },
    visible: (id) => `(() => { const e = document.getElementById(${JSON.stringify(id)}); if (!e) return false;
      for (let x = e; x && x !== document.documentElement; x = x.parentElement) { if (x.hidden || x.inert) return false; const cs = getComputedStyle(x); if (cs.display==='none'||cs.visibility==='hidden'||Number(cs.opacity)<0.05) return false; }
      const r = e.getBoundingClientRect(); return r.width > 1 && r.height > 1; })()`,
    async waitVisible(id, timeoutMs = 8000) { return api.waitFor(api.visible(id), timeoutMs, `#${id} が出る`); },
    async center(id) {
      const sel = /^[A-Za-z][\w-]*$/.test(id) ? `#${id}` : id;
      const r = await api.eval(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return null; const t = e.tagName === 'INPUT' ? (e.closest('label') || e) : e; t.scrollIntoView({block:'nearest'}); const b = t.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }; })()`);
      if (!r) throw new Error(`#${id} がありません`);
      return r;
    },
    async touch(x, y, holdMs = 60) {
      const tp = [{ x, y, id: 1, radiusX: 4, radiusY: 4, force: 1 }];
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: tp });
      await sleep(holdMs);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      await sleep(250);
    },
    async tap(id) { if (/^[A-Za-z][\w-]*$/.test(id)) await api.waitVisible(id); const c = await api.center(id); await api.touch(c.x, c.y); },
    // 見えているボタンを文言で押す（id を知らない人と同じ押し方）。
    async tapLabel(text) {
      const id = await api.eval(`(() => { const m = document.querySelector('dialog:modal');
        const b = [...document.querySelectorAll('button')].find((x) => (!m || m.contains(x)) && !x.hidden && !x.disabled
          && x.getBoundingClientRect().width > 1 && (x.innerText || '').replace(/\\s+/g, ' ').includes(${JSON.stringify(text)}));
        if (!b) return null; if (!b.id) b.id = 'flowwalk-' + Math.random().toString(36).slice(2); return b.id; })()`);
      if (!id) throw new Error(`「${text}」のボタンが見えていません`);
      await api.tap(id);
    },
    async hold(id, ms) { await api.waitVisible(id); const c = await api.center(id); await api.touch(c.x, c.y, ms); },
    async tapAt(x, y) { await api.touch(x, y); },
    // 画面が期待どおりかを確かめる。外れても止めずに記録し、最後に終了コードで知らせる。
    // 直前に撮った画面の記録へ付けるので、絵コンテで「どの画面で外れたか」が見える。
    async expect(expr, message) {
      let ok = false;
      try { ok = Boolean(await api.eval(expr)); } catch (_) { ok = false; }
      const last = steps.at(-1);
      if (last) (last.expects ??= []).push({ ok, message });
      if (!ok) { expectFailures.push(`${last?.n ?? '--'} ${message}`); console.log(`  ✗ ${message}`); }
    },
    hasButtonLabel: (text) => `[...document.querySelectorAll('button')].some((b) => !b.hidden && b.getBoundingClientRect().width > 4
      && getComputedStyle(b).visibility !== 'hidden' && (b.innerText || '').includes(${JSON.stringify(text)}))`,
    // 1 画面を撮る。who = 誰がタブレットを見ているか / doing = その人が次にすること（期待）
    async shot(id, { who, note = '', doing = '' } = {}) {
      await sleep(350);
      const n = String(steps.length + 1).padStart(2, '0');
      const file = `${n}-${id}.png`;
      const png = await cdp.send('Page.captureScreenshot', { format: 'png' });
      fs.writeFileSync(path.join(OUT, file), Buffer.from(png.data, 'base64'));
      const obs = await api.eval(OBSERVE);
      const st = await api.status();
      const errors = consoleErrors.splice(0);
      const staffDialog = dialogEvents.splice(0);
      steps.push({ n, id, section: api.section.id, sectionTitle: api.section.title, who, note, doing, file,
        stage: st.staffSetup?.stage ?? null, phase: st.phase, errors, staffDialog, ...obs });
      const primary = obs.buttons.filter((b) => !b.disabled).map((b) => b.label || b.id).join(' | ');
      console.log(`  ${n} ${id}  [${obs.views.join(',')}]  押せる: ${primary || '-'}${errors.length ? `  ⚠ ${errors.length} errors` : ''}`);
    },
  };
  return api;
}

// ---- フロー（本編中を除く） ----
// 各区間は「現場で起きる順」。代役の状態を進めるのは Quest が勝手に進むところだけ。
const SECTIONS = [
  {
    id: 'launch', title: '起動（初回の設営）',
    async run(a) {
      await a.scenario('devices');
      await a.state({ staffSetup: { cameras: [{ id: 'A', state: 'checking' }, { id: 'B', state: 'checking' }, { id: 'C', state: 'checking' }], tablet: 'checking', position: 'needed', positionConfirmed: false } });
      await a.open();
      await a.shot('app-open', { who: 'スタッフ', note: 'タブレットのアプリを開いた直後', doing: '機器がそろうのを待つ' });
      await a.scenario('setup');
      await sleep(1500);
      await a.shot('camera-b-missing', { who: 'スタッフ', note: 'カメラ B だけ映像が来ていない。位置は未登録', doing: 'B を直す。並行して Quest で位置合わせ' });
      await a.expect(`/スマホ B/.test(document.getElementById('staffRecovery').textContent)`, '「対処」は本文の位置合わせを繰り返さず、並行して直すカメラ B を指す');
      await a.state({ staffSetup: { cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'ok' }] } });
      await sleep(1500);
      await a.shot('cameras-ok-position-needed', { who: 'スタッフ', note: 'カメラはそろった。位置は未登録', doing: 'Quest を被って位置合わせ' });
      await a.scenario('alignment');
      await sleep(1500);
      await a.shot('alignment-in-progress', { who: 'スタッフ（Quest を被っている）', note: 'Quest で位置合わせ中', doing: '位置合わせを終える' });
      await a.scenario('prepare');
      // 状態の取得は周期なので、固定の待ちだけだとボタンが出る前に撮ることがある（2026-10-21 に 1 回）
      await a.waitVisible('staffResetBtn', 8000).catch(() => {});
      await sleep(300);
      await a.shot('setup-done', { who: 'スタッフ', note: '機器と位置がそろった。最初の来場者の前', doing: '準備を始める' });
    },
  },
  {
    id: 'reset', title: '来場者を迎える前のリセット',
    async run(a) {
      if (await a.eval(a.visible('staffResetBtn'))) {
        await a.tap('staffResetBtn');
        await sleep(1500);
        await a.shot('after-reset', { who: 'スタッフ', note: '「準備を始める」を押した後（Quest の完了応答を待つ／受けた）', doing: '来場者の画面にする' });
        await a.expect(a.visible('staffVisitorBtn'), 'リセットが済んだら「来場者の画面にする」で渡す');
      } else {
        await a.shot('no-reset-button', { who: 'スタッフ', note: '⚠ リセットのボタンが見えない' });
      }
    },
  },
  {
    id: 'visitor', title: '来場者のタブレット（設定と博士の説明）',
    async run(a) {
      await a.waitVisible('staffVisitorBtn', 10000);
      await a.tap('staffVisitorBtn');
      await sleep(1200);
      await a.shot('visitor-title', { who: '来場者', note: 'タブレットを来場者に渡した', doing: '画面をタップして始める' });
      await a.tap('titleStart');
      await sleep(1500);
      await a.shot('settings-language', { who: '来場者', doing: '言語を選ぶ' });
      await a.tap(`input[name=lang][value=${LANG}]`);
      await sleep(400);
      await a.shot('settings-language-picked', { who: '来場者', doing: '次へ' });
      const nextRing = await a.eval(`(() => { const r = document.getElementById('settingsNextBtn').getBoundingClientRect(); return { x: r.left, y: r.top }; })()`);
      // 二度押し：「次へ」と「この設定で始める」は同じ位置なので、2 回目で軽減を選ばずに送ってしまわないか
      const nextAt = await a.center('settingsNextBtn');
      await a.tapAt(nextAt.x, nextAt.y);
      await a.tapAt(nextAt.x, nextAt.y);
      await a.expect(`document.getElementById('resultView').hidden && !document.getElementById('reliefStep').hidden`,
        '「次へ」の二度押しで軽減を飛ばして送らない');
      await sleep(900);
      await a.shot('settings-relief', { who: '来場者', doing: '恐怖の軽減を選ぶ' });
      // 2026-10-21: テンポよく押すと「戻る」に入りかけた。進む輪は言語と軽減で同じ位置、戻るは左上に離す
      await a.expect(`(() => { const r = document.getElementById('sendBtn').getBoundingClientRect();
        return Math.abs(r.left - ${nextRing.x}) < 2 && Math.abs(r.top - ${nextRing.y}) < 2; })()`, '進む輪は言語と軽減で同じ位置');
      await a.expect(`(() => { const b = document.getElementById('settingsBackBtn').getBoundingClientRect(), s = document.getElementById('sendBtn').getBoundingClientRect();
        return b.bottom < 120 && b.bottom < s.top - 200; })()`, '戻るは左上に置き、進む操作から離す');
      await a.tap('input[name=relief][value=off]');
      await sleep(400);
      await a.shot('settings-relief-picked', { who: '来場者', doing: '「この設定で始める」' });
      await a.tap('sendBtn');
      // 反映中は最短 0.9 秒。撮影に時間がかかるので、撮る前に判定する
      await a.expect(`!document.getElementById('resultView').hidden && document.getElementById('editBtn').hidden`, '確認だけの段を挟まずに送り、送っている間は戻らせない');
      await sleep(1800);
      await a.shot('settings-sent', { who: '来場者', doing: '画面をタップして博士の説明へ' });
      // 0204「画面タップでいいならボタンは不要」。画面の言うとおりに画面をタップする。
      await a.expect(`!(${a.hasButtonLabel('博士の説明を始める')})`, '反映の画面に説明開始の専用ボタンを出さない（LEDGER 0204）');
      await a.expect(`(() => { const e = document.getElementById('editBtn').getBoundingClientRect(); return !document.getElementById('editBtn').hidden && e.bottom < 120; })()`,
        '「設定に戻る」は画面タップで進む範囲の外（上の帯）');
      await a.tap('resultLine');
      await sleep(1500);
      // 博士の説明。字幕が変わるたびに撮る（自動送り）。
      // 博士が中央に立つ章では説明の面（briefingView）自体は透明なので、字幕が出たかで待つ。
      await a.waitFor(`(document.getElementById('subtitleReserve')?.textContent || '').trim().length > 0`, 15000, '博士の説明の字幕');
      let last = '';
      const end = Date.now() + 240000;
      let count = 0;
      while (Date.now() < end) {
        const cue = await a.eval(`(document.getElementById('subtitleReserve')?.textContent || document.getElementById('briefingTitle')?.innerText || '').trim()`);
        const handback = await a.eval(a.visible('equipmentGuide'));
        if (cue && cue !== last) {
          last = cue; count += 1;
          await a.shot(`briefing-${String(count).padStart(2, '0')}`, { who: '来場者', note: '博士の説明（自動送り）', doing: '見る' });
          if (count === 4) {
            // 一時停止の盤も撮る（右上を誤って押した来場者が次に見る画面）
            await a.tap('briefingPauseBtn');
            await sleep(700);
            await a.shot('briefing-paused', { who: '来場者', note: '右上の一時停止を押した', doing: '再開' });
            await a.tap('briefingResume');
            await sleep(600);
          }
        }
        if (handback) break;
        await sleep(300);
      }
      await sleep(1200);
      await a.shot('handback', { who: '来場者 → スタッフ', note: '説明が終わった。装着の案内を読み、タブレットを返す', doing: 'スタッフが受け取って「スタッフ専用」を長押し' });
      // 2026-10-21: 案内は返す前に来場者が読む。返すのは読み終えてから
      await a.expect(`${a.visible('equipmentSteps')} && ${a.visible('equipmentHandback')}`, '砂嵐の直後に装着の案内と「読み終わったら渡す」を出す');
      await a.expect(a.visible('briefingStaff'), 'スタッフの入口が案内の画面に見える形である');
      if (LANG === 'ja') {
        await a.expect(`!/Meta Quest|クエスト内/.test(document.getElementById('equipmentGuide').innerText)`, '機器の呼び名を「クエスト」に揃える');
      }
    },
  },
  {
    id: 'confirm', title: '装着前の確認（スタッフ）',
    async run(a) {
      await a.hold('briefingStaff', 1700);
      await sleep(1200);
      await a.shot('staff-confirm', { who: 'スタッフ', note: '今回の設定と機器を確認する画面', doing: '「長押しで装着へ進む」' });
      await a.expect(`!(${a.visible('staffHandback')})`, '自分で開いたスタッフに「スタッフにお渡しください」を出さない');
      await a.expect(`(() => { const d = document.getElementById('staff'); return d.scrollHeight <= d.clientHeight + 1; })()`, '装着前の確認が 1 画面に収まる');
      await a.waitVisible('staffPrepareBtn', 8000);
      await a.hold('staffPrepareBtn', 1400);
      await sleep(1500);
      await a.shot('wear-staff', { who: 'スタッフ', note: '確定の後。タブレットはスタッフの手にあるので、スタッフ画面のまま', doing: '装着を手伝う' });
      await a.expect(`document.getElementById('staff').open && document.getElementById('staff').dataset.state === 'wear'`, '確定の後はスタッフ画面の「装着を手伝う」へ進む（来場者の画面へ戻さない）');
    },
  },
  {
    id: 'after', title: '体験の後（回収とリセット）',
    async run(a) {
      await a.scenario('playing');
      await sleep(1500);
      await a.shot('during-run', { who: '（タブレットは台の上）', note: '本編中のタブレット。中身の評価は対象外、置き場の確認だけ' });
      await a.scenario('outro');
      await sleep(1500);
      await a.shot('outro', { who: 'スタッフ', note: '終わりの演出中（まだ外さない）', doing: '待つ' });
      await a.scenario('finished');
      // 「準備を始める」と同じく、状態の取得周期しだいで固定の待ちに間に合わない（2026-10-21・仏語の走行で 1 回）
      await a.waitVisible('staffCollectBtn', 8000).catch(() => {});
      await sleep(300);
      await a.shot('finished', { who: 'スタッフ', note: '体験が終わった', doing: 'HMD を外す・回収・清拭 → 次の準備' });
      if (!(await a.eval(a.visible('staffCollectBtn')))) {
        await a.shot('finished-no-collect', { who: 'スタッフ', note: '⚠ 次の準備ボタンが見えない' });
        return;
      }
      await a.hold('staffCollectBtn', 1800);
      await sleep(1500);
      await a.shot('next-reset', { who: 'スタッフ', note: '次の準備を押した後', doing: '来場者の画面にする' });
      // 長押しを離した指の click が、同じ位置に出た「来場者の画面にする」へ入っていた（2026-10-17）。
      await a.expect(`document.getElementById('staff').open && ${a.visible('staffVisitorBtn')}`,
        '次の準備の長押しを離しても来場者の画面へ勝手に飛ばない（2 人目も 1 人目と同じ手順）');
    },
  },
  {
    // 本編中の想定外（2026-10-21）。中身の評価ではなく、スタッフに名指しと直し方が届くか・中断できるかだけを見る。
    id: 'trouble', title: '本編中の異常と体験の中断（スタッフ）',
    async run(a) {
      await a.scenario('healthy');
      await a.scenario('playing');
      // --only trouble で単独に歩くときは、まだページを開いていない
      if (!String(await a.eval('location.href')).startsWith('http')) await a.open();
      await a.waitFor(`document.getElementById('staff').dataset.state === 'playing'`, 8000, '体験中の画面');
      await a.scenario('ctrl-off');
      await a.waitVisible('staffRunAlerts', 12000).catch(() => {});
      await a.shot('run-alert-controller', { who: 'スタッフ', note: '本編中に左コントローラーが切れて 6 秒', doing: '電池と電源を確かめる' });
      await a.expect(`/左コントローラーが切れています/.test(document.getElementById('staffRunAlerts').innerText)`,
        '本編中の異常を 6 秒で機器の欄に名指しする');
      await a.scenario('healthy');
      await a.scenario('hung');
      await a.waitFor(`document.getElementById('staff').dataset.state === 'hung'`, 10000, '固まりの画面').catch(() => {});
      await a.waitFor(`/固まっています/.test(document.getElementById('staffRunAlerts').innerText)`, 12000, '固まりの知らせ').catch(() => {});
      await a.shot('run-hung', { who: 'スタッフ', note: 'Quest のアプリが固まった（応答は返るのに進まない）', doing: '「体験を中断してやり直す」を試す' });
      await a.expect(`document.getElementById('staffNowTitle').textContent === 'Quest のアプリが固まっています'`, '休止ではなく固まりとして出す');
      await a.expect(`!/電源ボタン/.test(document.getElementById('staffMain').innerText)`, '固まった Quest に電源ボタンを押させない');
      await a.scenario('wake');
      await a.waitFor(`document.getElementById('staff').dataset.state === 'playing'`, 8000, '体験中へ戻る');
      await a.tap('#staffTrouble > summary');
      await sleep(500);
      await a.scenario('aligning');
      await a.hold('staffForceResetBtn', 1800);
      await sleep(1500);
      await a.shot('force-reset-rejected', { who: 'スタッフ', note: '「体験を中断してやり直す」を長押し。Quest は位置合わせの作業中で断った', doing: '作業を終えてから押す' });
      await a.expect(`document.getElementById('staffForceMessage').textContent === 'Quest で位置合わせの作業中です。終えてから押す'`, '拒否は理由と次の手を出す');
      await a.expect(`document.getElementById('staff').dataset.state === 'playing'`, '拒否なら体験中のまま');
      await a.scenario('accept-reset');
      await a.hold('staffForceResetBtn', 1800);
      await a.waitFor(`document.getElementById('staff').dataset.state === 'handover'`, 8000, '次の体験者の準備').catch(() => {});
      await sleep(500);
      await a.shot('force-reset-done', { who: 'スタッフ', note: '体験の途中で中断した後', doing: '来場者の画面にする' });
      await a.expect(`document.getElementById('staff').open && document.getElementById('staff').dataset.state === 'handover' && ${a.visible('staffVisitorBtn')}`,
        '体験の途中でも中断すると次の体験者の準備へ戻る');
    },
  },
];

function esc(s) { return String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

function storyboard(steps) {
  const rows = steps.map((s) => {
    const texts = s.texts.map((t) => `<li${t.size >= 20 ? ' class="big"' : ''}>${esc(t.t)}</li>`).join('');
    const btns = s.buttons.map((b) => `<li class="${b.disabled ? 'off' : ''}">${esc(b.label || `#${b.id}`)} <small>${b.w}×${b.h}</small></li>`).join('');
    const warn = [s.overflow.x && '横にはみ出し', s.overflow.y && '縦にはみ出し', s.errors.length && `JS エラー ${s.errors.length}`].filter(Boolean);
    return `<article id="s${s.n}">
  <header><b>${s.n}</b> <span class="sec">${esc(s.sectionTitle)}</span> <code>${esc(s.id)}</code>
    <span class="who">${esc(s.who)}</span> <span class="stage">stage=${esc(s.stage)} / ${esc(s.phase)}</span>
    ${warn.length ? `<span class="warn">${esc(warn.join(' / '))}</span>` : ''}</header>
  <div class="body"><a href="${esc(s.file)}"><img src="${esc(s.file)}" loading="lazy" alt=""></a>
  <div class="side"><p class="note">${esc(s.note)}</p><p class="doing">次にすること: ${esc(s.doing || '—')}</p>
  ${s.expects?.length ? `<ul class="expects">${s.expects.map((e) => `<li class="${e.ok ? 'ok' : 'ng'}">${e.ok ? '✓' : '✗'} ${esc(e.message)}</li>`).join('')}</ul>` : ''}
  <h4>押せるもの（${s.buttons.filter((b) => !b.disabled).length}）</h4><ul class="btns">${btns || '<li>なし</li>'}</ul>
  <h4>見えている文字（${s.texts.reduce((n, t) => n + t.t.length, 0)} 字）</h4><ul class="texts">${texts}</ul>
  ${s.staffDialog?.length ? `<h4>スタッフ画面の開閉（前の画面から）</h4><pre class="dlg">${esc(s.staffDialog.join('\n'))}</pre>` : ''}
  ${s.errors.length ? `<h4>エラー</h4><pre>${esc(s.errors.join('\n'))}</pre>` : ''}</div></div></article>`;
  }).join('\n');
  return `<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>フロー絵コンテ</title><style>
body{margin:0;background:#f7f7f5;color:#1f2328;font:14px/1.6 system-ui,"Yu Gothic UI",sans-serif}
main{max-width:1400px;margin:0 auto;padding:24px}
article{background:#fff;border:1px solid #d8d8d4;border-radius:6px;margin:0 0 20px;padding:12px 16px}
header{display:flex;gap:12px;align-items:baseline;flex-wrap:wrap;margin-bottom:8px}
.sec{color:#57606a}.who{font-weight:600}.stage{color:#8c959f;font-size:12px}.warn{color:#b42318;font-weight:600}
.body{display:grid;grid-template-columns:minmax(0,640px) minmax(0,1fr);gap:16px}
img{width:100%;border:1px solid #d8d8d4}
h4{margin:10px 0 2px;font-size:12px;color:#57606a}ul{margin:0;padding-left:18px}
.texts li.big{font-weight:600}.btns li.off{color:#8c959f;text-decoration:line-through}
.note{margin:0}.doing{margin:0;color:#0b5394}pre{white-space:pre-wrap;color:#b42318}pre.dlg{color:#57606a;font-size:11px}
.expects{list-style:none;padding:0;margin:6px 0}.expects .ok{color:#1a7f37}.expects .ng{color:#b42318;font-weight:600}
@media(max-width:900px){.body{grid-template-columns:1fr}}
</style><main><h1>フロー絵コンテ（${steps.length} 画面）</h1>\n${rows}</main></html>`;
}

main().catch((error) => { console.error(error.stack || error.message); process.exitCode = 1; });
