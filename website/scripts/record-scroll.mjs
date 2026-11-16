// サイトを頭から 3 周目の終わりまでスクロールした動画を撮る（X などで紹介する用）。
// 使い方: 先にサーバーを起こす（npm run dev）。
//   node scripts/record-scroll.mjs --probe                章の位置と 1 周の長さを出すだけ
//   node scripts/record-scroll.mjs --out <mp4> [--width 1920 --height 1080 --dpr 1 --fps 60 --mobile]
// 仕組み: Chrome を別プロファイルで起こし、1 コマごとに決めた量だけ scrollBy して画面を撮り、ffmpeg へ流す。
// 実時間では撮らないので、速さはコマ単位で決まる（下の speedAt）。周の境目はページ自身が移す。
// 時間で動くのはヘッダーの出入りだけなので、その長さを 1 コマにかかる実時間に合わせて伸ばす。
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";

const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : fallback;
};

const url = opt("url", "http://127.0.0.1:4173/");
const width = Number(opt("width", 1920));
const height = Number(opt("height", 1080));
const dpr = Number(opt("dpr", 1));
const fps = Number(opt("fps", 30));
const mobile = flag("mobile");
const probeOnly = flag("probe");
const out = opt("out", "");
const stillsDir = opt("stills", "");
const pace = Number(opt("pace", 1));
if (!probeOnly && !out) throw new Error("--out <mp4> が要る");

const chromePath = opt("chrome", "C:/Program Files/Google/Chrome/Application/chrome.exe");
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "mawarimi-scroll-"));
const chrome = spawn(chromePath, [
  "--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`,
  "--use-angle=d3d11", "--force_high_performance_gpu", "--hide-scrollbars", "--mute-audio",
  "--autoplay-policy=no-user-gesture-required", `--window-size=${Math.max(width, 512)},${height}`, ...(opt("gpu-luid", "") ? [`--use-adapter-luid=${opt("gpu-luid", "")}`] : []), "about:blank"
], { stdio: "ignore", windowsHide: true });

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let ws;
let ffmpeg;
try {
  let tabs;
  for (let i = 0; i < 150; i += 1) {
    try {
      const port = Number(fs.readFileSync(path.join(profile, "DevToolsActivePort"), "utf8").split("\n")[0]);
      tabs = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).filter((tab) => tab.type === "page");
      if (tabs.length) break;
    } catch { /* まだ起きていない */ }
    await sleep(100);
  }
  if (!tabs?.length) throw new Error("Chrome が起きない");

  ws = new WebSocket(tabs[0].webSocketDebuggerUrl);
  await new Promise((resolve) => ws.addEventListener("open", resolve, { once: true }));
  let id = 0;
  const pending = new Map();
  const errors = [];
  ws.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    if (message.method === "Runtime.exceptionThrown") errors.push(message.params.exceptionDetails?.text);
    if (!message.id) return;
    const waiter = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error)));
    else waiter.resolve(message.result);
  });
  const cdp = (method, params = {}) => new Promise((resolve, reject) => {
    id += 1;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async (expression) => {
    const result = await cdp("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };

  await cdp("Runtime.enable");
  await cdp("Page.enable");
  await cdp("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: dpr, mobile });
  if (mobile) await cdp("Emulation.setTouchEmulationEnabled", { enabled: true });
  // 入口の演出は撮らない（見た済みの印を先に置く）
  await cdp("Page.addScriptToEvaluateOnNewDocument", {
    source: "try { if (location.pathname === '/') sessionStorage.setItem('mawarimi-entrance-glass-seen', '1'); } catch (_) {}"
  });
  await cdp("Page.navigate", { url });
  for (let i = 0; i < 200; i += 1) {
    if (await evaluate("document.readyState === 'complete'")) break;
    await sleep(100);
  }

  const settle = () => evaluate(`(async () => {
    for (const img of document.querySelectorAll("img[loading=lazy]")) img.loading = "eager";
    await document.fonts.ready;
    const waits = [...document.images].filter((img) => img.currentSrc || img.src).map((img) => img.complete ? null : new Promise((r) => { img.onload = img.onerror = r; setTimeout(r, 8000); }));
    await Promise.all(waits);
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    return document.images.length;
  })()`);
  await settle();
  await sleep(2500);

  const layout = await evaluate(`(() => {
    const top = (el) => Math.round(el.getBoundingClientRect().top + scrollY);
    const echo = document.querySelector(".loop-echo");
    const rows = [...document.querySelectorAll("main > *, .site-footer, .loop-bridge, .loop-echo")].map((el) => ({
      name: el.id || el.className.split(" ")[0], top: top(el), height: Math.round(el.getBoundingClientRect().height)
    }));
    return { rows, lapLength: echo ? top(echo) - top(document.querySelector("main > .hero")) : 0,
      scrollHeight: document.documentElement.scrollHeight, inner: [innerWidth, innerHeight], dpr: devicePixelRatio,
      gpu: (() => { try { const gl = document.createElement("canvas").getContext("webgl"); return gl.getParameter(gl.getExtension("WEBGL_debug_renderer_info").UNMASKED_RENDERER_WEBGL); } catch { return "?"; } })() };
  })()`);
  console.log(JSON.stringify(layout, null, 1));
  if (probeOnly) process.exit(0);

  const section = Object.fromEntries(layout.rows.map((row) => [row.name, row]));
  const lapLength = layout.lapLength;
  const vh = layout.inner[1];

  // ───── 速さ ─────
  // 単位は「1 秒に画面何枚ぶん進むか」。1 周目は読める速さ、2・3 周目は同じ中身なので速め、
  // ただし周ごとに変わる所（キービジュアル・テレビ）と、最後の暗がりから顔が出る所では落とす。
  const lapBase = [0.44, 0.8, 0.88];
  const slowZones = (lap) => {
    const zones = [];
    const around = (name, factor, before = 0.5, after = 0.25) => {
      const row = section[name];
      if (!row) return;
      // 章の主役の物が画面に入っている間。位置は画面上端の y
      zones.push({ from: row.top - vh * before, to: row.top + row.height - vh * (1 - after), factor });
    };
    if (lap === 1) {
      around("perspective-lab", 0.85);
      around("fixed-view", 0.8);
      around("scenario", 0.9);
    } else {
      around("fixed-view", 0.55);
    }
    return zones;
  };

  const speedAt = (lap, y, state) => {
    let speed = lapBase[Math.min(lap, 3) - 1];
    for (const zone of slowZones(lap)) if (y >= zone.from && y <= zone.to) speed *= zone.factor;
    // 周の境目：暗がりを抜けて頭が出てくる所と、移った後の頭はゆっくり見せる
    const footerTop = section["site-footer"].top;
    if (y > footerTop - vh * 0.4) speed = Math.min(speed, lap === 3 ? 0.36 : 0.5);
    if (lap > 1 && y < vh * 0.9) speed = Math.min(speed, 0.34);
    if (state.final) speed = Math.min(speed, 0.3);
    return speed * pace;
  };

  // ───── 撮る ─────
  const shoot = async () => Buffer.from((await cdp("Page.captureScreenshot", { format: "jpeg", quality: 90, optimizeForSpeed: true })).data, "base64");
  const stateExpr = `({ y: scrollY, max: document.documentElement.scrollHeight - innerHeight,
    lap: Number(document.querySelector("main").dataset.lap), final: Boolean(document.querySelector(".loop-final")),
    pending: [...document.images].filter((img) => (img.currentSrc || img.src) && !img.complete).length })`;
  const state = () => evaluate(stateExpr);
  // スクロールと状態の読み取りを 1 往復にまとめる
  const stepAndState = (step) => evaluate(`new Promise((r) => { ${step > 0 ? `scrollBy({ top: ${step}, behavior: "instant" });` : ""} requestAnimationFrame(() => r(${stateExpr})); })`);

  // 1 コマにかかる実時間を測って、ヘッダーの出入りを動画の時間に合わせる
  const t0 = performance.now();
  for (let i = 0; i < 12; i += 1) await shoot();
  const perFrame = (performance.now() - t0) / 12;
  const stretch = perFrame / (1000 / fps);
  await evaluate(`(() => { const s = document.createElement("style"); s.textContent = "[data-site-header]{transition-duration:${Math.round(320 * stretch)}ms !important}"; document.head.append(s); })()`);
  console.log(`1 コマ ${perFrame.toFixed(0)}ms（動画時間の ${stretch.toFixed(1)} 倍）`);

  const temp = path.join(os.tmpdir(), `mawarimi-scroll-${Date.now()}.mp4`);
  ffmpeg = spawn("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "image2pipe", "-vcodec", "mjpeg", "-r", String(fps), "-i", "pipe:0",
    "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "17", "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
    "-movflags", "+faststart", temp], { stdio: ["pipe", "inherit", "inherit"], windowsHide: true });
  const encoded = new Promise((resolve, reject) => {
    ffmpeg.on("error", reject);
    ffmpeg.on("close", (code) => (code ? reject(new Error(`ffmpeg ${code}`)) : resolve()));
  });
  let frames = 0;
  const marks = [];
  const write = async (buffer) => {
    if (!ffmpeg.stdin.write(buffer)) await new Promise((resolve) => ffmpeg.stdin.once("drain", resolve));
    if (stillsDir && frames % (fps * 2) === 0) fs.writeFileSync(path.join(stillsDir, `f${String(frames).padStart(5, "0")}.jpg`), buffer);
    frames += 1;
  };
  const hold = async (seconds) => {
    const buffer = await shoot();
    for (let i = 0; i < Math.round(seconds * fps); i += 1) await write(buffer);
  };
  if (stillsDir) fs.mkdirSync(stillsDir, { recursive: true });

  await hold(1.4);
  let velocity = 0;       // px / コマ
  let carry = 0;
  let now = await state();
  let lastLap = now.lap;
  marks.push({ t: 0, lap: 1 });
  for (let guard = 0; guard < fps * 400; guard += 1) {
    const remaining = now.max - now.y;
    const lapNow = now.lap;
    let target = speedAt(lapNow, now.y, now) * vh / fps;
    // 最後は止まる所へ向けて減速する（残りが少ないほど遅く）
    if (now.final) target = Math.min(target, Math.max(remaining * 2.2 / fps, 0.5));
    velocity += (target - velocity) * (1 - Math.exp(-1 / (fps * 0.45)));
    carry += velocity;
    const step = Math.floor(carry);
    carry -= step;
    now = await stepAndState(step);
    if (now.lap !== lastLap) {
      marks.push({ t: +(frames / fps).toFixed(2), lap: now.lap });
      lastLap = now.lap;
      await settle();
    }
    if (now.pending > 0) await settle();
    await write(await shoot());
    if (frames % (fps * 10) === 0) console.log(`${(frames / fps).toFixed(0)}s lap=${now.lap} y=${Math.round(now.y)}/${Math.round(now.max)}`);
    if (now.final && remaining <= 1) break;
  }
  await hold(2.2);
  ffmpeg.stdin.end();
  await encoded;
  fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
  fs.copyFileSync(temp, out);
  fs.rmSync(temp);
  console.log(JSON.stringify({ out, seconds: +(frames / fps).toFixed(2), frames, marks, lapLength, gpu: layout.gpu, errors }));
} finally {
  ws?.close();
  chrome.kill();
  if (ffmpeg && ffmpeg.exitCode === null) ffmpeg.stdin.destroy();
}
