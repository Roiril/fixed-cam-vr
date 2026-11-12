#!/usr/bin/env node
'use strict';

// 来場者のタブレットの画面で、タイトル → 言語と軽減の設定 → 博士の説明（自動送り）→ 装着の案内 までを
// 1 本の動画（音つき）に録る。人に「UI の上で動いているところ」を見せるためのもの。判定はしない（判定は flow-walk.cjs）。
//
//   node tools/visitor-ui/record-briefing.cjs                     # 日本語。出力は Drive の renders/doctor-room/
//   node tools/visitor-ui/record-briefing.cjs --lang en --out <file.mp4>
//
// 画面は本番の index.html を代役サーバ（staff-mock-server.cjs）経由で headless の Chrome に出し、
// 絵は CDP の captureScreenshot を続けて撮り、音はページの中で自分のタブを getDisplayMedia（--auto-accept-this-tab-capture）で録って重ねる。
// ⚠ 代役の Quest が応答するので、実機の WebView の速さ・音の出方は確かめられない。

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const { spawn, spawnSync } = require('node:child_process');

const root = path.resolve(__dirname, '..', '..');
const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };

// 画面の大きさは Redmi Pad SE の CSS の幅（1097×686・flow-walk と同じ）。--scale の倍率で描き、出力は OUT_W×OUT_H に縮める
const WIDTH = Number(arg('width', 1097));
const HEIGHT = Number(arg('height', 686));
const SCALE = Number(arg('scale', 2));
const OUT_W = Number(arg('out-width', 1920));
const OUT_H = Math.round(OUT_W * HEIGHT / WIDTH / 2) * 2;
const LIMIT_SEC = Number(arg('seconds', 0));   // 試すとき: 説明をこの秒数で打ち切る（0 は終わりまで）
const LANG = arg('lang', 'ja');
const now = new Date();
const pad = (n) => String(n).padStart(2, '0');
const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;   // 手元の時刻
const driveDir = 'G:\\マイドライブ\\shubie\\renders\\doctor-room';
const OUT = path.resolve(arg('out', path.join(fs.existsSync(driveDir) ? driveDir : path.join(root, 'output'), `${stamp}_tablet-briefing-${LANG}.mp4`)));
// Drive へは書き終えた物を移す（書き出し中に中身を書き直す道具を G: へ直接書かせない）
const WORK = fs.mkdtempSync(path.join(os.tmpdir(), 'record-briefing-'));

function browserPath() {
  const candidates = [process.env.FLOW_WALK_BROWSER, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'].filter(Boolean);
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

// 音は、ページの中で自分のタブを getDisplayMedia で録る（音のトラックだけ使う）。録れた塊は window.__rec に溜め、最後に base64 で取り出す。
// ⚠ 画面の大きさを Emulation.setDeviceMetricsOverride で上書きしない。screencast もタブの録画も、窓の実寸（枠の分だけ小さい）で
//   切り取るので、下の約 2 割にある字幕の帯が映らなかった（DOM では見えている。2026-11-13 実測）。窓の大きさを合わせて実寸で描かせる。
//   captureScreenshot の連写は字幕も映るが 5 fps しか出ない。screencast は 36 fps 前後
const START_AUDIO = `(async () => {
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: { frameRate: 5 }, audio: { suppressLocalAudioPlayback: true },
    preferCurrentTab: true, selfBrowserSurface: 'include', systemAudio: 'include' });
  stream.getVideoTracks().forEach((t) => t.stop());
  const audio = new MediaStream(stream.getAudioTracks());
  const type = ['audio/webm;codecs=opus', 'audio/webm'].find((t) => MediaRecorder.isTypeSupported(t));
  const rec = new MediaRecorder(audio, { mimeType: type, audioBitsPerSecond: 160_000 });
  window.__rec = { rec, chunks: [], done: false, audioTracks: audio.getAudioTracks().length, type, startedAt: 0 };
  rec.ondataavailable = (e) => { if (e.data.size) window.__rec.chunks.push(e.data); };
  rec.onstart = () => { window.__rec.startedAt = Date.now(); };
  rec.onstop = () => { window.__rec.done = true; audio.getTracks().forEach((t) => t.stop()); };
  rec.start(1000);
  await new Promise((r) => setTimeout(r, 50));
  return { audioTracks: window.__rec.audioTracks, type };
})()`;

async function main() {
  const mockPort = await freePort();
  const cdpPort = await freePort();
  const mock = spawn(process.execPath, [path.join(__dirname, 'staff-mock-server.cjs'), '--port', String(mockPort)], { stdio: 'ignore' });
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'record-briefing-profile-'));
  // 音は録るが、この PC のスピーカーからは鳴らさない。--mute-audio だと録った音も無音になる（-70 LUFS）ので付けず、
  // 録る側の suppressLocalAudioPlayback で手元の再生だけ止める
  const browser = spawn(browserPath(), [
    '--headless=new', `--remote-debugging-port=${cdpPort}`, `--user-data-dir=${profile}`, `--window-size=${WIDTH},${HEIGHT}`, `--force-device-scale-factor=${SCALE}`,
    '--autoplay-policy=no-user-gesture-required', '--auto-accept-this-tab-capture',
    '--no-first-run', '--no-default-browser-check', '--hide-scrollbars', 'about:blank',
  ], { stdio: 'ignore' });
  const base = `http://127.0.0.1:${mockPort}`;
  let cdp;
  try {
    await waitHttp(`${base}/_state`);
    await waitHttp(`http://127.0.0.1:${cdpPort}/json/version`);
    const pages = await (await fetch(`http://127.0.0.1:${cdpPort}/json/list`)).json();
    cdp = new Cdp(pages.find((p) => p.type === 'page').webSocketDebuggerUrl);
    await cdp.open();
    await cdp.send('Runtime.enable');
    await cdp.send('Page.enable');
    await cdp.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });

    const evaluate = async (expression, userGesture = false) => {
      const r = await cdp.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture });
      if (r.exceptionDetails) throw new Error(`eval 失敗: ${r.exceptionDetails.exception?.description || r.exceptionDetails.text}`);
      return r.result.value;
    };
    const mockPost = async (pathname, body) => {
      const r = await fetch(`${base}/${pathname}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });
      if (!r.ok) throw new Error(`mock ${pathname} → ${r.status}`);
    };
    const visible = (id) => `(() => { const e = document.getElementById(${JSON.stringify(id)}); if (!e) return false;
      for (let x = e; x && x !== document.documentElement; x = x.parentElement) { if (x.hidden || x.inert) return false; const cs = getComputedStyle(x); if (cs.display==='none'||cs.visibility==='hidden'||Number(cs.opacity)<0.05) return false; }
      const r = e.getBoundingClientRect(); return r.width > 1 && r.height > 1; })()`;
    const waitFor = async (expr, timeoutMs, what) => {
      const end = Date.now() + timeoutMs;
      for (;;) {
        try { if (await evaluate(`Boolean(${expr})`)) return; } catch (_) { /* 途中 */ }
        if (Date.now() > end) {
          // 止まった画面を出力の隣に残す（どこで詰まったかを画で見る）
          const shot = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 80 }).catch(() => null);
          if (shot) { fs.mkdirSync(path.dirname(OUT), { recursive: true }); fs.writeFileSync(`${OUT}.stuck.jpg`, Buffer.from(shot.data, 'base64')); }
          throw new Error(`待ち切れません: ${what}${shot ? `（画面は ${OUT}.stuck.jpg）` : ''}`);
        }
        await sleep(120);
      }
    };
    const tap = async (selector) => {
      const sel = /^[A-Za-z][\w-]*$/.test(selector) ? `#${selector}` : selector;
      if (sel.startsWith('#')) await waitFor(visible(sel.slice(1)), 10000, `${sel} が出る`);
      const c = await evaluate(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return null; const t = e.tagName === 'INPUT' ? (e.closest('label') || e) : e; const b = t.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }; })()`);
      if (!c) throw new Error(`${sel} がありません`);
      const tp = [{ x: c.x, y: c.y, id: 1, radiusX: 4, radiusY: 4, force: 1 }];
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: tp });
      await sleep(60);
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      await sleep(250);
    };

    // スタッフの準備を済ませ、来場者の画面にする（flow-walk.cjs の launch / reset と同じ代役の段）
    await mockPost('_reset');
    await mockPost('_scenario', { scenario: 'devices' });
    await mockPost('_state', { staffSetup: { cameras: [{ id: 'A', state: 'ok' }, { id: 'B', state: 'ok' }, { id: 'C', state: 'ok' }] } });
    await cdp.send('Page.navigate', { url: `${base}/?lang=${LANG}` });
    await waitFor('document.readyState === "complete"', 10000, 'ページの読み込み');
    await mockPost('_scenario', { scenario: 'prepare' });
    // headless の窓は、--window-size から枠の分だけ小さい画面になる（1097×686 → 1075×588）。足りない分を窓に足す
    let vp = await evaluate('[innerWidth, innerHeight, devicePixelRatio]');
    if (vp[0] !== WIDTH || vp[1] !== HEIGHT) {
      const { windowId } = await cdp.send('Browser.getWindowForTarget');
      await cdp.send('Browser.setWindowBounds', { windowId, bounds: { width: WIDTH * 2 - vp[0], height: HEIGHT * 2 - vp[1] } });
      await sleep(500);
      vp = await evaluate('[innerWidth, innerHeight, devicePixelRatio]');
    }
    console.log(`画面 ${vp.join(' × ')}`);
    if (vp[0] !== WIDTH || vp[1] !== HEIGHT) throw new Error(`画面の大きさが ${WIDTH}×${HEIGHT} になっていない`);
    await tap('staffResetBtn');
    await tap('staffVisitorBtn');
    await sleep(1500);

    // 絵: captureScreenshot を続けて撮り、撮り始めた時刻（秒）を残す。コマの尺は次のコマとの時刻の差で決める
    const framesDir = path.join(WORK, 'frames');
    fs.mkdirSync(framesDir);
    const frames = [];
    cdp.on((m) => {
      if (m.method !== 'Page.screencastFrame') return;
      const file = path.join(framesDir, `${String(frames.length).padStart(6, '0')}.jpg`);
      fs.writeFileSync(file, Buffer.from(m.params.data, 'base64'));
      frames.push({ file, t: m.params.metadata.timestamp, h: m.params.metadata.deviceHeight });
      cdp.send('Page.screencastFrameAck', { sessionId: m.params.sessionId }).catch(() => {});
    });
    await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 88, maxWidth: WIDTH * SCALE, maxHeight: HEIGHT * SCALE, everyNthFrame: 1 });
    const started = await evaluate(START_AUDIO, true);
    console.log(`録画を始めた（${started.type}・音のトラック ${started.audioTracks}）`);
    if (started.audioTracks !== 1) throw new Error('タブの音が取れない');
    await sleep(2500);
    await tap('titleStart');
    await sleep(1800);
    await tap(`input[name=lang][value=${LANG}]`);
    await sleep(900);
    await tap('settingsNextBtn');
    await sleep(1600);
    await tap('input[name=relief][value=off]');
    await sleep(900);
    await tap('sendBtn');
    await sleep(2600);
    await tap('resultLine');
    const t0 = Date.now();
    // 字幕が出ているか・映像がどこまで進んだかを 10 秒ごとに出す（録れた絵と画面の状態を突き合わせる）
    for (let next = t0; !(await evaluate(visible('equipmentGuide')));) {
      if (LIMIT_SEC && Date.now() > t0 + LIMIT_SEC * 1000) break;
      if (Date.now() > t0 + 300000) throw new Error('待ち切れません: 説明の終わり（装着の案内）');
      if (Date.now() >= next) {
        const s = await evaluate(`(() => { const e = document.getElementById('subtitle'); const v = document.getElementById('doctorVideo');
          const r = e?.getBoundingClientRect(); return { sub: (e?.innerText || '').trim().slice(0, 24), at: r ? [Math.round(r.top), Math.round(r.height)] : null, t: v ? Number(v.currentTime.toFixed(1)) : null, paused: v?.paused }; })()`);
        console.log(`  ${((Date.now() - t0) / 1000).toFixed(0)}s コマ ${frames.length} ${JSON.stringify(s)}`);
        next += 10000;
      }
      await sleep(300);
    }
    console.log(`説明 ${((Date.now() - t0) / 1000).toFixed(1)} 秒`);
    await sleep(5000);
    const stopAt = Date.now() / 1000;
    await cdp.send('Page.stopScreencast');
    await evaluate('window.__rec.rec.stop()');
    await waitFor('window.__rec.done', 20000, '録音の締め');
    const audioStartedAt = (await evaluate('window.__rec.startedAt')) / 1000;

    const count = await evaluate('window.__rec.chunks.length');
    const webm = path.join(WORK, 'tab.webm');
    const fd = fs.openSync(webm, 'w');
    for (let i = 0; i < count; i += 1) {
      const b64 = await evaluate(`(async () => { const b = window.__rec.chunks[${i}]; const u = new Uint8Array(await b.arrayBuffer()); let s = ''; for (let k = 0; k < u.length; k += 0x8000) s += String.fromCharCode(...u.subarray(k, k + 0x8000)); return btoa(s); })()`);
      fs.writeSync(fd, Buffer.from(b64, 'base64'));
    }
    fs.closeSync(fd);

    // コマの列を、届いた時刻の差を尺にして並べる（concat の demuxer）。音は録り始めの時刻の差だけずらして重ねる
    if (frames.length < 100) throw new Error(`コマが少ない（${frames.length}）`);
    const list = [];
    frames.forEach((f, i) => {
      const end = i + 1 < frames.length ? frames[i + 1].t : stopAt;
      list.push(`file '${f.file.replace(/\\/g, '/')}'`, `duration ${Math.max(0.001, end - f.t).toFixed(4)}`);
    });
    list.push(`file '${frames.at(-1).file.replace(/\\/g, '/')}'`);
    const listFile = path.join(WORK, 'frames.txt');
    fs.writeFileSync(listFile, list.join('\n') + '\n');
    const offset = audioStartedAt - frames[0].t;   // 正なら音が絵より遅れて始まった
    const audioIn = offset >= 0 ? ['-itsoffset', offset.toFixed(3), '-i', webm] : ['-ss', (-offset).toFixed(3), '-i', webm];
    console.log(`コマ ${frames.length}・平均 ${(frames.length / (stopAt - frames[0].t)).toFixed(1)} fps・音のずれ ${(offset * 1000).toFixed(0)}ms`);
    const mp4 = path.join(WORK, 'tab.mp4');
    const r = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'concat', '-safe', '0', '-i', listFile, ...audioIn,
      '-map', '0:v', '-map', '1:a', '-vf', `fps=30,scale=${OUT_W}:${OUT_H}:flags=lanczos`, '-c:v', 'libx264', '-crf', '20', '-preset', 'medium',
      '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '160k', '-shortest', '-movflags', '+faststart', mp4], { stdio: 'inherit' });
    if (r.status !== 0) throw new Error('mp4 への書き出しに失敗');
    fs.mkdirSync(path.dirname(OUT), { recursive: true });
    fs.copyFileSync(mp4, OUT);
    console.log(OUT);
  } finally {
    cdp?.close();
    browser.kill();
    mock.kill();
    // コマ（1 回で 9000 枚・約 700MB）と Chrome の作業フォルダを消す。C: は常に空きが少ない（2026-11-13 に 190MB まで落ちて Drive へ写せなかった）
    await sleep(1000);
    for (const dir of [WORK, profile]) fs.rmSync(dir, { recursive: true, force: true });
  }
}

main().catch((e) => { console.error(e); process.exit(1); });
