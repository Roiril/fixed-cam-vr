#!/usr/bin/env node
'use strict';

// 予告動画に使う卓（web compositor）の画面を、ヘッドレスの Chrome で 1920×1080 の静止画にする。
//
//   node tools/trailer/ui-shots.cjs --spec tools/trailer/ui-shots.json --out <フォルダ>
//   node tools/trailer/ui-shots.cjs --spec tools/trailer/ui-shots.json --out <フォルダ> --only ops-ready,author-cut
//
// spec は [{ "name", "url", "steps": [...], "full": false }] の配列。steps は順に実行する:
//   { "click": "<ボタンの文字の一部>" }   見えているボタン・リンクのうち文字が一致する最初の 1 つを押す
//   { "css": "<セレクタ>" }               セレクタの最初の要素を押す
//   { "scroll": "<セレクタ>" }            その要素が画面の上端に来るまで送る
//   { "scrollY": 1200 }                   ページを縦に送る
//   { "eval": "<式>" }                    ページで式を評価する（表示の整え）
//   { "wait": 1500 }                      待つ（ミリ秒）
// "full": true なら縦に長いページ全体を撮る（幅は 1920 のまま）。
//   ⚠ 1 画面より下は半透明の白が被って写る（2026-11-16 卓の各ページで確認）。動画に使う画は画面 1 枚分で撮る。
// "scale": 2.5 のように書くと、その画面だけ画素密度を上げて撮る（出力は 1920×scale 幅）。
// 卓のサーバ（tools/web-compositor/serve.ps1・既定 :8099）が立っていること。

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const { spawn } = require('node:child_process');

const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };
const SPEC = arg('spec', path.join(__dirname, 'ui-shots.json'));
const OUT = path.resolve(arg('out', path.join(__dirname, '..', '..', 'Logs', 'trailer', 'ui')));
const ONLY = (arg('only', '') || '').split(',').filter(Boolean);
const WIDTH = Number(arg('width', 1920));
const HEIGHT = Number(arg('height', 1080));
const SCALE = Number(arg('scale', 1));

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function browserPath() {
  const p = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
  if (!fs.existsSync(p)) throw new Error('Chrome が見つかりません');
  return p;
}

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

async function waitJson(url, timeoutMs = 15000) {
  const end = Date.now() + timeoutMs;
  for (;;) {
    try { const r = await fetch(url, { signal: AbortSignal.timeout(1000) }); if (r.ok) return await r.json(); } catch (_) { /* まだ */ }
    if (Date.now() > end) throw new Error(`${url} が立ち上がりません`);
    await sleep(200);
  }
}

class Cdp {
  constructor(wsUrl) { this.wsUrl = wsUrl; this.nextId = 1; this.pending = new Map(); }
  async open() {
    this.ws = new WebSocket(this.wsUrl);
    await new Promise((resolve, reject) => { this.ws.addEventListener('open', resolve, { once: true }); this.ws.addEventListener('error', reject, { once: true }); });
    this.ws.addEventListener('message', (event) => {
      const msg = JSON.parse(event.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id); this.pending.delete(msg.id);
        if (msg.error) reject(new Error(msg.error.message)); else resolve(msg.result);
      }
    });
  }
  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); this.ws.send(JSON.stringify({ id, method, params })); });
  }
  close() { try { this.ws.close(); } catch (_) { /* 済み */ } }
}

async function evaluate(cdp, expression) {
  const r = await cdp.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(`評価に失敗: ${r.exceptionDetails.text} ${expression.slice(0, 80)}`);
  return r.result.value;
}

const CLICK_TEXT = (text) => `(() => {
  const want = ${JSON.stringify(text)};
  const vis = (el) => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 1 && r.height > 1 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
  const el = [...document.querySelectorAll('button, a, [role=button], [role=tab], summary, label')].find((b) => vis(b) && (b.innerText || '').replace(/\\s+/g, ' ').includes(want));
  if (!el) return false;
  el.click();
  return true;
})()`;

async function runShot(cdp, shot) {
  // "scale" を持つ画面だけ解像度を上げる（床の図のように小さく描かれる画面を大きく使うため）
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: HEIGHT, deviceScaleFactor: shot.scale ?? SCALE, mobile: false });
  await cdp.send('Page.navigate', { url: shot.url });
  await sleep(shot.load ?? 2500);
  for (const step of shot.steps || []) {
    if (step.click) {
      const ok = await evaluate(cdp, CLICK_TEXT(step.click));
      if (!ok) throw new Error(`${shot.name}: 「${step.click}」のボタンが見つからない`);
      await sleep(step.after ?? 800);
    } else if (step.css) {
      const ok = await evaluate(cdp, `(() => { const el = document.querySelector(${JSON.stringify(step.css)}); if (!el) return false; el.click(); return true; })()`);
      if (!ok) throw new Error(`${shot.name}: セレクタ ${step.css} が見つからない`);
      await sleep(step.after ?? 800);
    } else if (step.scroll) {
      const ok = await evaluate(cdp, `(() => { const el = document.querySelector(${JSON.stringify(step.scroll)}); if (!el) return false; el.scrollIntoView({ block: 'start' }); window.scrollBy(0, ${Number(step.offset || 0)}); return true; })()`);
      if (!ok) throw new Error(`${shot.name}: 送り先 ${step.scroll} が見つからない`);
      await sleep(step.after ?? 600);
    } else if (step.scrollY !== undefined) {
      await evaluate(cdp, `window.scrollTo(0, ${Number(step.scrollY)})`);
      await sleep(step.after ?? 600);
    } else if (step.eval) {
      await evaluate(cdp, step.eval);
      await sleep(step.after ?? 300);
    } else if (step.wait) {
      await sleep(step.wait);
    }
  }
  const params = { format: 'png' };
  if (shot.full) {
    const h = await evaluate(cdp, 'Math.max(document.documentElement.scrollHeight, document.body.scrollHeight)');
    params.captureBeyondViewport = true;
    params.clip = { x: 0, y: 0, width: WIDTH, height: Math.min(h, 12000), scale: 1 };
  }
  const shotRes = await cdp.send('Page.captureScreenshot', params);
  const file = path.join(OUT, `${shot.name}.png`);
  fs.writeFileSync(file, Buffer.from(shotRes.data, 'base64'));
  const title = await evaluate(cdp, 'document.title');
  return { name: shot.name, file, title };
}

async function main() {
  const spec = JSON.parse(fs.readFileSync(SPEC, 'utf8')).filter((s) => !ONLY.length || ONLY.includes(s.name));
  if (!spec.length) throw new Error('撮る画面がない（--only の名前を確かめる）');
  fs.mkdirSync(OUT, { recursive: true });
  const port = await freePort();
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'trailer-ui-'));
  const browser = spawn(browserPath(), [
    '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
    `--window-size=${WIDTH},${HEIGHT}`, '--hide-scrollbars', '--mute-audio', '--force-color-profile=srgb',
    '--no-first-run', '--no-default-browser-check', 'about:blank',
  ], { stdio: 'ignore' });
  try {
    const targets = await waitJson(`http://127.0.0.1:${port}/json/list`);
    const page = targets.find((t) => t.type === 'page');
    const cdp = new Cdp(page.webSocketDebuggerUrl);
    await cdp.open();
    await cdp.send('Page.enable');
    await cdp.send('Runtime.enable');
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE, mobile: false });
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
    const done = [];
    for (const shot of spec) {
      try {
        const r = await runShot(cdp, shot);
        done.push(r);
        console.log(`[ui] ${r.name}  ${r.title}  → ${r.file}`);
      } catch (e) {
        console.error(`[ui] ✗ ${shot.name}: ${e.message}`);
        process.exitCode = 1;
      }
    }
    cdp.close();
    console.log(`[ui] ${done.length}/${spec.length} 枚を撮った`);
  } finally {
    browser.kill();
    await sleep(500);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch (_) { /* Chrome がまだ握っている */ }
  }
}

main().catch((e) => { console.error(e.stack || e.message); process.exit(1); });
