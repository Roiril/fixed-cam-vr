// 廻り視 デジタルツインの見る面。
// 動画は固定カメラ 3 本を同じ時間軸で持ち、Unity の仮想体験の記録（active / dim）でカットする。
// 機械から操作できる: window.twin.seek(t) / state() / setCam() / setRoute() / setOverlay()、URL の ?route= &t= &cam= &overlay=1 &panel=1。
(() => {
  "use strict";
  const $ = (id) => document.getElementById(id);
  const FPS = 15;
  const NAMES = ["A", "B", "C"];
  const COLORS = ["#d8c26a", "#6ab5a7", "#b58ad0"];
  const params = new URLSearchParams(location.search);
  const S = {
    twin: null, route: null, routeName: params.get("route") || "forward", check: null,
    t: Number(params.get("t") || 0), playing: false, manual: params.get("cam") ? NAMES.indexOf(params.get("cam")) : -1,
    overlay: params.get("overlay") === "1", showMap: params.get("map") !== "0", showPov: params.get("pov") === "1", showPanel: params.get("panel") === "1",
    sound: false, audio: null, lastFrame: 0, feeds: [false, false, false], lastStep: -1, lastCam: -1,
  };
  const videos = [$("v0"), $("v1"), $("v2")];

  // ---- データ ----
  async function json(url) { const r = await fetch(url); if (!r.ok) throw new Error(url + " " + r.status); return r.json(); }
  function row(t) {
    const tb = S.route.table; let lo = 0, hi = tb.length - 1;
    while (hi - lo > 1) { const m = (lo + hi) >> 1; if (tb[m][0] <= t) lo = m; else hi = m; }
    const a = tb[lo], b = tb[hi], u = b[0] > a[0] ? Math.min(1, Math.max(0, (t - a[0]) / (b[0] - a[0]))) : 0;
    const lerp = (i) => a[i] + (b[i] - a[i]) * u;
    return { t, x: lerp(1), z: lerp(2), yaw: a[3], active: a[4], dim: lerp(5), glitch: lerp(6), speed: lerp(7), dist: lerp(8), clear: lerp(9) };
  }
  const duration = () => S.route.table[S.route.table.length - 1][0];
  const camNow = (r) => (S.manual >= 0 ? S.manual : r.active);

  async function loadRoute(name) {
    S.routeName = name;
    S.route = await json(`data/route_${name}.json`);
    $("route").value = name;
    S.feeds = await Promise.all(NAMES.map(async (c, i) => {
      const url = `/game/${name}/${c}.mp4`;
      const head = await fetch(url, { headers: { Range: "bytes=0-1" } }).catch(() => null);
      const ok = !!head && head.ok;
      if (ok) videos[i].src = url; else videos[i].removeAttribute("src");
      return ok;
    }));
    const pov = $("pov");
    pov.src = `/walk/20261030_222807_verified-height_${name}/walk.mp4`;
    S.t = Math.min(S.t, duration());
    drawTimelineBase();
    renderEvents();
    draw(true);
  }

  // ---- 投影（Unity 座標。rig.json の pos / lookAt / roll / hfov と同じ定義） ----
  function camBasis(c) {
    const p = c.pos, t = c.lookAt;
    let f = [t[0] - p[0], t[1] - p[1], t[2] - p[2]];
    const n = Math.hypot(...f); f = f.map((v) => v / n);
    let r = [f[2], 0, -f[0]]; const rn = Math.hypot(...r); r = r.map((v) => v / rn);
    let u = [f[1] * r[2] - f[2] * r[1], f[2] * r[0] - f[0] * r[2], f[0] * r[1] - f[1] * r[0]];
    if (u[1] < 0) u = u.map((v) => -v);
    const a = (c.rollDeg || 0) * Math.PI / 180, ca = Math.cos(a), sa = Math.sin(a);
    const r2 = r.map((v, i) => v * ca + u[i] * sa), u2 = u.map((v, i) => u[i] * ca - r[i] * sa);
    return { p, f, r: r2, u: u2, tanH: Math.tan(c.hfovDeg * Math.PI / 360) };
  }
  function project(b, q, W, H) {
    const d = [q[0] - b.p[0], q[1] - b.p[1], q[2] - b.p[2]];
    const z = d[0] * b.f[0] + d[1] * b.f[1] + d[2] * b.f[2];
    if (z < 0.05) return null;
    const x = (d[0] * b.r[0] + d[1] * b.r[1] + d[2] * b.r[2]) / z / b.tanH;
    const y = (d[0] * b.u[0] + d[1] * b.u[1] + d[2] * b.u[2]) / z / (b.tanH * 0.75);
    return [W * (0.5 + x / 2), H * (0.5 - y / 2), z];
  }
  function line3(g, b, a, c, W, H) {
    const pa = project(b, a, W, H), pc = project(b, c, W, H);
    if (!pa || !pc) return;
    g.moveTo(pa[0], pa[1]); g.lineTo(pc[0], pc[1]);
  }

  function drawOverlay(r) {
    const cv = $("overlay"); const box = cv.getBoundingClientRect();
    const W = Math.round(box.width), H = Math.round(box.height);
    if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; }
    const g = cv.getContext("2d"); g.clearRect(0, 0, W, H);
    const feed = S.feeds[camNow(r)];
    if (!S.overlay && feed) return;
    const b = camBasis(S.twin.cameras[camNow(r)]);
    // 区画の床
    const grid = S.twin.grid, half = grid.tileM * grid.cols / 2;
    g.lineWidth = 1;
    for (let j = 0; j < grid.rows; j++) for (let i = 0; i < grid.cols; i++) {
      const c = Number(grid.cells[j][i]);
      const x0 = -half + i * grid.tileM, x1 = x0 + grid.tileM, z1 = half - j * grid.tileM, z0 = z1 - grid.tileM;
      const qs = [[x0, 0, z0], [x1, 0, z0], [x1, 0, z1], [x0, 0, z1]].map((q) => project(b, q, W, H));
      if (qs.some((q) => !q)) continue;
      g.beginPath(); qs.forEach((q, k) => (k ? g.lineTo(q[0], q[1]) : g.moveTo(q[0], q[1]))); g.closePath();
      g.fillStyle = COLORS[c] + "26"; g.fill(); g.strokeStyle = COLORS[c] + "33"; g.stroke();
    }
    // 壁
    g.strokeStyle = "#e7e3d6cc"; g.lineWidth = 1.5; g.beginPath();
    for (const w of S.twin.walls) {
      line3(g, b, [w.x1, 0, w.z1], [w.x2, 0, w.z2], W, H);
      line3(g, b, [w.x1, w.h, w.z1], [w.x2, w.h, w.z2], W, H);
      line3(g, b, [w.x1, 0, w.z1], [w.x1, w.h, w.z1], W, H);
      line3(g, b, [w.x2, 0, w.z2], [w.x2, w.h, w.z2], W, H);
    }
    g.stroke();
    // 経路
    g.strokeStyle = "#ffffff55"; g.lineWidth = 1; g.beginPath();
    const tb = S.route.table; let last = null;
    for (let i = 0; i < tb.length; i += 6) {
      const q = project(b, [tb[i][1], 0.01, tb[i][2]], W, H);
      if (q && last) { g.moveTo(last[0], last[1]); g.lineTo(q[0], q[1]); }
      last = q;
    }
    g.stroke();
    // 人（足元の輪と身長の線）
    const head = project(b, [r.x, 1.65, r.z], W, H), foot = project(b, [r.x, 0, r.z], W, H);
    g.strokeStyle = "#ff6b5f"; g.lineWidth = 2; g.beginPath();
    if (head && foot) { g.moveTo(head[0], head[1]); g.lineTo(foot[0], foot[1]); }
    for (let i = 0; i <= 28; i++) {
      const a = i / 28 * Math.PI * 2;
      const q = project(b, [r.x + Math.cos(a) * 0.39, 0, r.z + Math.sin(a) * 0.39], W, H);
      if (q) { if (i === 0) g.moveTo(q[0], q[1]); else g.lineTo(q[0], q[1]); }
    }
    g.stroke();
  }

  function drawMap(r) {
    const cv = $("map"); const g = cv.getContext("2d"), W = cv.width, H = cv.height;
    g.clearRect(0, 0, W, H);
    // 図 4 と同じ向き（+z が上、+x が右）
    const k = W / 3.6, X = (x) => W / 2 + x * k, Y = (z) => H / 2 - z * k;
    const grid = S.twin.grid, half = grid.tileM * grid.cols / 2;
    for (let j = 0; j < grid.rows; j++) for (let i = 0; i < grid.cols; i++) {
      const c = Number(grid.cells[j][i]);
      g.fillStyle = COLORS[c] + "22";
      g.fillRect(X(-half + i * grid.tileM), Y(half - j * grid.tileM), grid.tileM * k - 0.5, grid.tileM * k - 0.5);
    }
    const sh = S.twin.shelf;
    g.fillStyle = "#3a3a33"; g.fillRect(X(sh.min[0]), Y(sh.max[2]), (sh.max[0] - sh.min[0]) * k, (sh.max[2] - sh.min[2]) * k);
    g.strokeStyle = "#e7e3d6"; g.lineWidth = 2.5; g.beginPath();
    for (const w of S.twin.walls) { g.moveTo(X(w.x1), Y(w.z1)); g.lineTo(X(w.x2), Y(w.z2)); }
    g.stroke();
    g.strokeStyle = "#ffffff44"; g.lineWidth = 1; g.beginPath();
    const tb = S.route.table;
    tb.forEach((q, i) => { if (i % 4 === 0) { if (i === 0) g.moveTo(X(q[1]), Y(q[2])); else g.lineTo(X(q[1]), Y(q[2])); } });
    g.stroke();
    const cur = camNow(r);
    S.twin.cameras.forEach((c, i) => {
      const b = camBasis(c);
      const px = X(c.pos[0]), py = Y(c.pos[2]);
      const a = Math.atan2(b.f[0], b.f[2]), h = Math.atan(b.tanH), L = 1.5 * k;
      g.fillStyle = COLORS[i] + (i === cur ? "44" : "12");
      g.beginPath(); g.moveTo(px, py);
      g.lineTo(px + Math.sin(a - h) * L, py - Math.cos(a - h) * L);
      g.lineTo(px + Math.sin(a + h) * L, py - Math.cos(a + h) * L);
      g.closePath(); g.fill();
      g.fillStyle = COLORS[i]; g.beginPath(); g.arc(px, py, 5, 0, 7); g.fill();
      g.fillStyle = "#000"; g.font = "bold 9px sans-serif"; g.textAlign = "center"; g.textBaseline = "middle"; g.fillText(NAMES[i], px, py + 0.5);
    });
    g.fillStyle = "#ff6b5f"; g.beginPath(); g.arc(X(r.x), Y(r.z), 5, 0, 7); g.fill();
    const yaw = r.yaw * Math.PI / 180;
    g.strokeStyle = "#ff6b5f"; g.lineWidth = 2; g.beginPath(); g.moveTo(X(r.x), Y(r.z)); g.lineTo(X(r.x) + Math.sin(yaw) * 13, Y(r.z) - Math.cos(yaw) * 13); g.stroke();
  }

  // ---- タイムライン ----
  let timelineBase = null;
  function drawTimelineBase() {
    const cv = $("timeline"); cv.width = Math.max(100, Math.round(cv.getBoundingClientRect().width));
    const c2 = document.createElement("canvas"); c2.width = cv.width; c2.height = cv.height;
    const g = c2.getContext("2d"); const d = duration(); const W = c2.width;
    const tb = S.route.table;
    for (let i = 0; i < tb.length - 1; i++) {
      g.fillStyle = COLORS[tb[i][4]] + "99";
      g.fillRect(tb[i][0] / d * W, 8, Math.ceil((tb[i + 1][0] - tb[i][0]) / d * W), 16);
    }
    g.fillStyle = "#fff";
    for (const e of S.route.events) { if (e[1] === "commit" || e[1] === "outro") g.fillRect(e[0] / d * W, 2, 1, 30); }
    timelineBase = c2;
  }
  function drawTimeline() {
    const cv = $("timeline"), g = cv.getContext("2d");
    g.clearRect(0, 0, cv.width, cv.height);
    if (timelineBase) g.drawImage(timelineBase, 0, 0);
    g.fillStyle = "#ff6b5f"; g.fillRect(S.t / duration() * cv.width - 1, 0, 2, cv.height);
  }

  // ---- 検証の読み出し ----
  function renderEvents() {
    const ol = $("events"); ol.innerHTML = "";
    for (const e of S.route.events) {
      const li = document.createElement("li"); li.dataset.t = e[0];
      li.textContent = `${e[0].toFixed(2)} ${e[1]} ${e[2]}`;
      ol.appendChild(li);
    }
    const ul = $("rigcheck"); ul.innerHTML = "";
    if (S.check) {
      for (const [k, c] of Object.entries(S.check.cameras)) {
        const li = document.createElement("li"); li.textContent = `${k}: ${c.text}（図4 と ${c.fromFig4M.toFixed(2)}m）`; ul.appendChild(li);
      }
      const rr = S.check.routes[S.routeName];
      if (rr) for (const k of ["A", "B", "C"]) {
        const v = rr.inFrame[k]; const li = document.createElement("li");
        li.textContent = `${k} の区間で人が映る ${(v.any * 100).toFixed(0)}%`; ul.appendChild(li);
      }
      for (const f of S.check.failures) { const li = document.createElement("li"); li.className = "fail"; li.textContent = f; ul.appendChild(li); }
    }
  }
  function renderReadout(r) {
    const rows = [["時刻", r.t.toFixed(2) + " s"], ["位置", `${r.x.toFixed(2)}, ${r.z.toFixed(2)}`], ["速さ", r.speed.toFixed(2) + " m/s"],
      ["担当", NAMES[r.active]], ["表示", NAMES[camNow(r)] + (S.manual >= 0 ? "（手動）" : "")], ["暗転", r.dim.toFixed(2)], ["乱れ", r.glitch.toFixed(2)], ["全身の余白", r.clear.toFixed(3) + " m"]];
    $("readout").innerHTML = rows.map(([a, b]) => `<dt>${a}</dt><dd>${b}</dd>`).join("");
    for (const li of $("events").children) li.classList.toggle("past", Number(li.dataset.t) <= r.t);
  }

  // ---- 音（部屋の唸り・足音・切替の小さな音。録音素材を使わず作る） ----
  function audioInit() {
    if (S.audio) return;
    const ac = new (window.AudioContext || window.webkitAudioContext)();
    const master = ac.createGain(); master.gain.value = 0.7; master.connect(ac.destination);
    const hum = ac.createOscillator(); hum.type = "sawtooth"; hum.frequency.value = 50;
    const lp = ac.createBiquadFilter(); lp.type = "lowpass"; lp.frequency.value = 180;
    const humG = ac.createGain(); humG.gain.value = 0.018;
    hum.connect(lp); lp.connect(humG); humG.connect(master); hum.start();
    const len = ac.sampleRate * 3, buf = ac.createBuffer(1, len, ac.sampleRate), d = buf.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
    const air = ac.createBufferSource(); air.buffer = buf; air.loop = true;
    const airF = ac.createBiquadFilter(); airF.type = "bandpass"; airF.frequency.value = 500; airF.Q.value = 0.4;
    const airG = ac.createGain(); airG.gain.value = 0.012;
    air.connect(airF); airF.connect(airG); airG.connect(master); air.start();
    S.audio = { ac, master, noise: buf };
  }
  function thump(gain, freq, dur) {
    if (!S.audio || !S.sound || !S.playing) return;
    const { ac, master, noise } = S.audio;
    const src = ac.createBufferSource(); src.buffer = noise; src.loop = true;
    const f = ac.createBiquadFilter(); f.type = "lowpass"; f.frequency.value = freq;
    const g = ac.createGain(); const t0 = ac.currentTime;
    g.gain.setValueAtTime(gain, t0); g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    src.connect(f); f.connect(g); g.connect(master); src.start(t0, Math.random() * 2); src.stop(t0 + dur);
  }
  function soundTick(r) {
    const step = Math.floor(r.dist / (S.route.cycleM / 2));
    if (step !== S.lastStep) { if (S.lastStep >= 0) thump(0.16, 420, 0.13); S.lastStep = step; }
    const c = camNow(r);
    if (c !== S.lastCam) { if (S.lastCam >= 0) thump(0.1, 2600, 0.04); S.lastCam = c; }
  }

  // ---- 描画 ----
  function syncVideos(r, force) {
    const cam = camNow(r);
    videos.forEach((v, i) => {
      const on = i === cam && S.feeds[i];
      v.classList.toggle("on", on);
      if (!S.feeds[i] || !v.duration) return;
      const target = Math.min(S.t, v.duration - 0.01);
      if (on) {
        if (S.playing) {
          if (v.paused) v.play().catch(() => {});
          if (Math.abs(v.currentTime - target) > 0.12) v.currentTime = target;
        } else {
          if (!v.paused) v.pause();
          if (force || Math.abs(v.currentTime - target) > 0.02) v.currentTime = target;
        }
      } else {
        if (!v.paused) v.pause();
        if (force || Math.abs(v.currentTime - target) > 0.5) v.currentTime = target;  // 切替で待たない
      }
    });
    const pov = $("pov");
    if (S.showPov && pov.duration) {
      if (S.playing) { if (pov.paused) pov.play().catch(() => {}); if (Math.abs(pov.currentTime - S.t) > 0.2) pov.currentTime = S.t; }
      else { if (!pov.paused) pov.pause(); if (force || Math.abs(pov.currentTime - S.t) > 0.05) pov.currentTime = S.t; }
    }
  }
  function draw(force) {
    if (!S.route) return;
    const r = row(S.t);
    syncVideos(r, force);
    $("dim").style.opacity = S.manual >= 0 ? 0 : Math.min(1, r.dim);
    const c = camNow(r);
    $("osd-cam").textContent = "CAM " + NAMES[c];
    const m = Math.floor(S.t / 60), s = S.t - m * 60;
    $("osd-time").textContent = `${String(m).padStart(2, "0")}:${s.toFixed(1).padStart(4, "0")}`;
    $("osd-rec").style.visibility = Math.floor(S.t * 1.2) % 2 === 0 ? "visible" : "hidden";
    $("nofeed").hidden = S.feeds[c];
    $("screen").style.transform = r.glitch > 0.02 && S.manual < 0 ? `translateX(${(Math.sin(S.t * 91) * r.glitch * 10).toFixed(1)}px)` : "";
    drawOverlay(r);
    $("map").hidden = !S.showMap;
    if (S.showMap) drawMap(r);
    drawTimeline();
    $("panel").hidden = !S.showPanel;
    if (S.showPanel) renderReadout(r);
    $("pov").hidden = !S.showPov;
    soundTick(r);
    $("play").textContent = S.playing ? "❚❚" : "▶";
  }
  function tick(now) {
    if (S.playing) {
      const dt = Math.min(0.1, (now - S.lastFrame) / 1000);
      S.t += dt;
      if (S.t >= duration()) { S.t = duration(); S.playing = false; }
    }
    S.lastFrame = now;
    draw(false);
    requestAnimationFrame(tick);
  }

  // ---- 操作 ----
  function setCam(v) {
    S.manual = v === "auto" || v === -1 ? -1 : (typeof v === "string" ? NAMES.indexOf(v) : v);
    for (const b of $("cams").children) b.classList.toggle("on", b.dataset.cam === (S.manual < 0 ? "auto" : String(S.manual)));
    draw(true);
  }
  function seek(t) { S.t = Math.max(0, Math.min(duration(), t)); draw(true); }
  function press(id, v) { $(id).setAttribute("aria-pressed", String(v)); }
  function setOverlay(v) { S.overlay = v; press("b-overlay", v); draw(true); }
  function setMap(v) { S.showMap = v; press("b-map", v); draw(true); }
  function setPov(v) { S.showPov = v; press("b-pov", v); draw(true); }
  function setPanel(v) { S.showPanel = v; press("b-panel", v); draw(true); }
  function setSound(v) { S.sound = v; press("b-sound", v); if (v) { audioInit(); S.audio.ac.resume(); } else if (S.audio) S.audio.ac.suspend(); }
  function play(v) { S.playing = v ?? !S.playing; if (S.t >= duration() && S.playing) S.t = 0; draw(true); }
  function start() { $("title").hidden = true; setSound(true); play(true); }

  $("start").onclick = start;
  $("play").onclick = () => play();
  $("route").onchange = (e) => loadRoute(e.target.value);
  for (const b of $("cams").children) b.onclick = () => setCam(b.dataset.cam === "auto" ? "auto" : Number(b.dataset.cam));
  $("b-overlay").onclick = () => setOverlay(!S.overlay);
  $("b-map").onclick = () => setMap(!S.showMap);
  $("b-pov").onclick = () => setPov(!S.showPov);
  $("b-panel").onclick = () => setPanel(!S.showPanel);
  $("b-sound").onclick = () => setSound(!S.sound);
  const tl = $("timeline");
  const scrub = (e) => { const b = tl.getBoundingClientRect(); seek((e.clientX - b.left) / b.width * duration()); };
  let dragging = false;
  tl.onpointerdown = (e) => { dragging = true; tl.setPointerCapture(e.pointerId); scrub(e); };
  tl.onpointermove = (e) => { if (dragging) scrub(e); };
  tl.onpointerup = () => { dragging = false; };
  addEventListener("resize", () => { if (S.route) { drawTimelineBase(); draw(true); } });
  addEventListener("keydown", (e) => {
    if (e.target.tagName === "SELECT") return;
    const k = e.key.toLowerCase();
    if (k === " ") { e.preventDefault(); if (!$("title").hidden) start(); else play(); }
    else if (k === "arrowleft") { S.playing = false; seek(S.t - 1 / FPS); }
    else if (k === "arrowright") { S.playing = false; seek(S.t + 1 / FPS); }
    else if ("123".includes(k)) setCam(Number(k) - 1);
    else if (k === "0") setCam("auto");
    else if (k === "l") setOverlay(!S.overlay);
    else if (k === "m") setMap(!S.showMap);
    else if (k === "h") setPov(!S.showPov);
    else if (k === "d") setPanel(!S.showPanel);
  });

  window.twin = {
    seek, play: () => play(true), pause: () => play(false), setCam, setRoute: loadRoute, setOverlay, setMap, setPov, setPanel,
    state: () => {
      const r = row(S.t); const c = camNow(r); const v = videos[c];
      return { route: S.routeName, ...r, shown: NAMES[c], activeName: NAMES[r.active], manual: S.manual >= 0, playing: S.playing, feeds: S.feeds.slice(),
        video: { src: v.currentSrc, currentTime: v.currentTime, duration: v.duration, readyState: v.readyState, videoWidth: v.videoWidth, on: v.classList.contains("on") },
        overlay: S.overlay, cuts: S.route.table.reduce((n, q, i, a) => n + (i && a[i - 1][4] !== q[4] ? 1 : 0), 0), duration: duration() };
    },
  };

  (async () => {
    const [twin, check] = await Promise.all([json("data/twin.json"), json("data/rig_check.json").catch(() => null)]);
    S.twin = twin; S.check = check;
    await loadRoute(S.routeName);
    press("b-overlay", S.overlay); press("b-map", S.showMap); press("b-pov", S.showPov); press("b-panel", S.showPanel);
    if (S.manual >= 0) setCam(S.manual);
    if (params.get("skiptitle") === "1") $("title").hidden = true;
    S.lastFrame = performance.now();
    requestAnimationFrame(tick);
    window.twinReady = true;
  })().catch((e) => { document.body.insertAdjacentHTML("beforeend", `<pre style="position:absolute;left:8px;top:8px;color:#f66">${e}</pre>`); });
})();
