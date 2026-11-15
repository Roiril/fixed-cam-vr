// 動くのは、スクロールとホバー（指ならタップ）に応えるものだけ。時間で勝手に進むものは置かない。
// 動かすのは理由を言えるものだけ：
//   カメラの切り替え（選んだ映像に替わる。替わった瞬間だけ 180ms 乱れる）
//   髪（読み進めるほど下へ伸びる。裾の下の 3 割から全部まで）
//   染み（読み進めるほど芯から縁へ広がる）
//   ヘッダー（読んでいる間は退き、戻ると出る）
//   ページの終わり（暗がりを抜けると、また最初の部屋に戻っている。体験と同じく 3 周）
//   周ごとの異変と色（廻るたびに狂う。2 周目で色が抜け、3 周目で血の色に沈む）

const feeds = [...document.querySelectorAll("[data-camera-feed]")];
const cameraButtons = [...document.querySelectorAll("[data-camera-button]")];
const screen = document.querySelector(".crt-screen");
const indicator = document.querySelector("#camera-indicator");
const status = document.querySelector("#camera-status");
const cameraConsole = document.querySelector(".camera-console");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const canHover = window.matchMedia("(hover: hover)");

const CAMERA_COUNT = 3;

let selectedCamera = 1; // 押して選んだカメラ
let previewCamera = 0;  // 触れている間だけ映すカメラ（0 は無し）
let shownCamera = 0;
let noiseTimer = null;

function cameraLabel(camera) {
  return `CAM ${String(camera).padStart(2, "0")}`;
}

function stopSwitchNoise() {
  window.clearTimeout(noiseTimer);
  noiseTimer = null;
  screen.classList.remove("is-switching");
}

// 切り替わった瞬間だけ画面が乱れる
function showSwitchNoise() {
  if (reducedMotion.matches) return;

  stopSwitchNoise();
  window.requestAnimationFrame(() => {
    screen.classList.add("is-switching");
    noiseTimer = window.setTimeout(stopSwitchNoise, 190);
  });
}

function renderCamera({ animate = true } = {}) {
  const camera = previewCamera || selectedCamera;
  if (camera === shownCamera) return;
  shownCamera = camera;

  for (const feed of feeds) {
    const active = Number(feed.dataset.cameraFeed) === camera;
    feed.classList.toggle("is-active", active);
    feed.setAttribute("aria-hidden", String(!active));
  }

  for (const button of cameraButtons) {
    const active = Number(button.dataset.cameraButton) === camera;
    button.classList.toggle("is-active", active);
    button.setAttribute("aria-pressed", String(active));
  }

  indicator.textContent = cameraLabel(camera);
  status.textContent = `カメラ ${String(camera).padStart(2, "0")}`;
  if (animate) showSwitchNoise();
}

for (const button of cameraButtons) {
  const camera = Number(button.dataset.cameraButton);

  button.addEventListener("click", () => {
    previewCamera = 0;
    selectedCamera = camera;
    renderCamera();
  });

  // マウスを乗せている間・キーボードで選んでいる間は、そのカメラを映す
  button.addEventListener("pointerenter", (event) => {
    if (event.pointerType !== "mouse" || !canHover.matches) return;
    previewCamera = camera;
    renderCamera();
  });
  button.addEventListener("pointerleave", () => {
    if (!previewCamera) return;
    previewCamera = 0;
    renderCamera();
  });
  button.addEventListener("focus", () => {
    if (!button.matches(":focus-visible")) return;
    previewCamera = camera;
    renderCamera();
  });
  button.addEventListener("blur", () => {
    if (!previewCamera) return;
    previewCamera = 0;
    renderCamera();
  });

  button.addEventListener("keydown", (event) => {
    const currentIndex = cameraButtons.indexOf(button);
    let nextIndex = null;

    if (event.key === "ArrowRight" || event.key === "ArrowDown") {
      nextIndex = (currentIndex + 1) % CAMERA_COUNT;
    } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
      nextIndex = (currentIndex - 1 + CAMERA_COUNT) % CAMERA_COUNT;
    } else if (event.key === "Home") {
      nextIndex = 0;
    } else if (event.key === "End") {
      nextIndex = CAMERA_COUNT - 1;
    }

    if (nextIndex === null) return;
    event.preventDefault();
    cameraButtons[nextIndex].focus();
  });
}

reducedMotion.addEventListener("change", (event) => {
  if (event.matches) stopSwitchNoise();
});

// ───── 3 周するページ ─────
// 体験は 3 区間を 3 周する。サイトも、終わりまで読むと暗がりの先に最初の部屋（キービジュアル）が
// もう一度現れ、そのまま読み進めると 2 周目の頭に居る。3 周目の終わりで本当に終わる。
// 仕組み：フッターの後ろに暗がりと「ページの頭の写し」を置く。写しは本物と同じ組版なので、
// 写しの中の位置 p と本物の位置 p は同じ絵になる。写しへ入ったら、同じ絵の本物の位置へ瞬時に移す。
// 移すのは、スクロールが止まったとき（慣性を切らない）か、写しの絵が尽きる手前のどちらか早い方。

const LAPS = 3;
const realHead = [...document.querySelectorAll("main > .hero, main > #trailer")];
let lap = 1;
let loopBridge = null;
let loopEcho = null;
let idleTimer = 0;
let suppressHeaderOnce = false;

function buildLoop() {
  if (realHead.length === 0) return;

  loopBridge = document.createElement("div");
  loopBridge.className = "loop-bridge";
  loopBridge.setAttribute("aria-hidden", "true");

  loopEcho = document.createElement("div");
  loopEcho.className = "loop-echo";
  loopEcho.setAttribute("aria-hidden", "true");
  loopEcho.inert = true;

  for (const original of realHead) {
    const copy = original.cloneNode(true);
    // 写しは見た目だけ。読み上げ・ページ内リンク・見出しの数に混ざらないようにする
    for (const node of [copy, ...copy.querySelectorAll("*")]) {
      node.removeAttribute("id");
      node.removeAttribute("aria-labelledby");
      node.removeAttribute("fetchpriority");
    }
    for (const node of copy.querySelectorAll(".sr-only")) node.remove();
    for (const frame of copy.querySelectorAll("iframe")) frame.setAttribute("loading", "lazy");
    loopEcho.append(copy);
  }

  document.body.append(loopBridge, loopEcho);
  alignLoop();
}

// 本物の頭から写しの頭までを整数の画素にする。端数が残ると、移った瞬間に文字のにじみ方が変わる
function alignLoop() {
  if (!loopBridge || !loopEcho) return;
  loopBridge.style.height = "";
  const length = docTop(loopEcho) - docTop(realHead[0]);
  const base = loopBridge.getBoundingClientRect().height;
  loopBridge.style.height = `${base + (Math.ceil(length) - length)}px`;
}

// 3 周目に入ったら、写しを外す。暗がりはそのまま残し、その先には頭ではなく人形の顔を置く。
// 読み手は 3 度目の「頭へ戻る」を待っているので、暗がりから出てくるのは最初の部屋だと思っている。
// 顔の絵はここで読み込み始める（3 周目を読み終えるまでには届いている）
let loopFinal = null;
function removeLoop() {
  loopEcho?.remove();
  loopEcho = null;
  if (!loopBridge) return;
  loopFinal = document.createElement("div");
  loopFinal.className = "loop-final";
  loopFinal.setAttribute("aria-hidden", "true");
  loopFinal.innerHTML = '<picture><source media="(max-width: 600px)" srcset="assets/final-mobile.webp"><img src="assets/final-desktop.webp" width="1672" height="941" alt="" decoding="async"></picture>';
  loopBridge.after(loopFinal);
}

const docTop = (element) => element.getBoundingClientRect().top + window.scrollY;

// 写しの中で、本物と同じ絵が続く高さ（その下の余白は、移すまでの滑走路）
function echoContentHeight() {
  const last = loopEcho.lastElementChild;
  return last.getBoundingClientRect().bottom - loopEcho.getBoundingClientRect().top;
}

function wrapIfInEcho({ force = false } = {}) {
  if (!loopEcho) return false;
  const y = window.scrollY;
  const echoTop = docTop(loopEcho);
  const offset = y - echoTop;
  if (offset < 0) return false;

  // 写しの絵が尽きる手前に来たら、止まるのを待たずに移す
  const nearEnd = offset + window.innerHeight >= echoContentHeight() - 40;
  if (!force && !nearEnd) return false;

  alignLoop();
  const loopLength = Math.round(docTop(loopEcho) - docTop(realHead[0]));
  const target = y - loopLength;
  window.scrollTo({ top: target, behavior: "instant" });
  // 移れなかったら周を数えない（数えると写しを外して、ページの途中で行き止まりになる）
  if (window.scrollY >= echoTop) return false;
  lastScrollY = window.scrollY;
  suppressHeaderOnce = true;
  loopLengthKnown = loopLength;
  lap += 1;
  if (lap >= LAPS) removeLoop();
  applyLap();
  return true;
}

// ───── 周ごとの異変 ─────
// 周は main・フッター・頭の写しが data-lap で持つ。写しは次の周の姿。
// 替えるのは移った瞬間だけ。そのとき画面は頭を映していて、頭は写しと同じ姿なので、替わったのは見えない。
// 画面の外にある物（テレビの映像）は、その場で入れ替える。

const lapHolders = [document.querySelector("main"), document.querySelector(".site-footer")].filter(Boolean);
let loopLengthKnown = 0;

// テレビの 3 台。周ごとに、映る廊下が狂っていく（2 周目は 02 だけ、3 周目は 3 台とも）
const feedsByLap = {
  1: [
    ["assets/camera-fiction-01.webp", "薄暗い廊下に立つ人物を天井近くから見下ろす"],
    ["assets/camera-fiction-02.webp", "同じ廊下と人物を反対側のカメラから見る"],
    ["assets/camera-fiction-03.webp", "廊下の曲がり角と壁に隠れる人物を見る"]
  ],
  2: [
    ["assets/camera-fiction-01.webp", "薄暗い廊下に立つ人物を天井近くから見下ろす"],
    ["assets/camera-fiction-02-lap2.webp", "同じ廊下と人物を反対側のカメラから見る。奥に小さな人形が立っている"],
    ["assets/camera-fiction-03.webp", "廊下の曲がり角と壁に隠れる人物を見る"]
  ],
  3: [
    ["assets/camera-fiction-01-lap3.webp", "人物の立っていた所に人形が立ち、カメラを見上げている"],
    ["assets/camera-fiction-02-lap3.webp", "うつむく人物の周りを、カメラを向いた人形が埋めている"],
    ["assets/camera-fiction-03-lap3.webp", "レンズのすぐ前に人形の顔がある"]
  ]
};

function applyFeeds(n) {
  const set = feedsByLap[n] ?? feedsByLap[3];
  for (const feed of feeds) {
    const [src, alt] = set[Number(feed.dataset.cameraFeed) - 1];
    if (!feed.src.endsWith(src)) feed.src = src;
    feed.alt = alt;
  }
  for (const button of cameraButtons) {
    const thumb = button.querySelector(".camera-thumb img");
    const [src] = set[Number(button.dataset.cameraButton) - 1];
    if (thumb && !thumb.src.endsWith(src)) thumb.src = src;
  }
}

// 次の周の絵は、その周が近づいたら読み込ませる（初めの人には余計な通信をさせない）
const armedLaps = new Set();
function armLap(n) {
  if (armedLaps.has(n)) return;
  armedLaps.add(n);
  for (const picture of document.querySelectorAll(`.hero-anomaly[data-anomaly-lap="${n}"]`)) {
    for (const source of picture.querySelectorAll("source[data-srcset]")) source.srcset = source.dataset.srcset;
    const img = picture.querySelector("img[data-src]");
    if (img) img.src = img.dataset.src;
  }
  for (const [src] of feedsByLap[n] ?? []) {
    const preload = new Image();
    preload.src = src;
  }
}

function applyLap() {
  for (const holder of lapHolders) holder.dataset.lap = String(lap);
  if (loopEcho) loopEcho.dataset.lap = String(lap + 1);
  if (lap > 1) {
    armLap(lap);
    applyFeeds(lap);
  }
}

// 色の崩れ。重ねる層は 4 枚（色を抜く・血の色に染める・明るい所まで血の色に焼く・四隅から暗く沈める）
const hauntLayers = ["drain", "blood", "burn", "dark"].map((name) => {
  const layer = document.createElement("div");
  layer.className = `haunt haunt-${name}`;
  layer.setAttribute("aria-hidden", "true");
  layer.hidden = true;
  document.body.append(layer);
  return layer;
});

const smooth = (from, to, value) => {
  const t = Math.min(Math.max((value - from) / (to - from), 0), 1);
  return t * t * (3 - 2 * t);
};

// 読み進めた量を周で数える（1 周目の頭が 0、2 周目の頭が 1、3 周目の頭が 2）。
// 写しの中の位置 p は次の周の頭から p の所なので、移る前後でこの値は続いている
function lapProgress() {
  let length = loopLengthKnown;
  if (loopEcho) length = loopLengthKnown = docTop(loopEcho) - docTop(realHead[0]);
  if (!length) return 0;
  return lap - 1 + window.scrollY / length;
}

// 1 周目は手を付けない。2 周目は読み進めるほど色が抜け、終わり際に赤みが差す。
// 3 周目は読み進めるほど血の色に沈み、四隅から暗くなる。3 周目の終わり（フッター）でいちばん深い
function renderHaunt() {
  const p = lapProgress();
  if (p > 0.55) armLap(2);
  if (p > 1.55) armLap(3);
  const second = smooth(1.0, 2.0, p);
  const third = smooth(2.0, 2.8, p);
  const values = [
    0.7 * second + 0.3 * third,                       // drain
    0.14 * smooth(1.55, 2.0, p) + 0.8 * third,        // blood
    0.8 * smooth(2.1, 2.9, p),                        // burn
    0.3 * second + 0.6 * third                        // dark
  ];
  hauntLayers.forEach((layer, index) => {
    layer.hidden = values[index] < 0.002;
    layer.style.opacity = values[index].toFixed(3);
  });
}

function onLoopScroll() {
  if (!loopEcho) return;
  wrapIfInEcho();
  window.clearTimeout(idleTimer);
  idleTimer = window.setTimeout(() => wrapIfInEcho({ force: true }), 140);
}

buildLoop();
applyLap();
if (loopEcho) {
  window.addEventListener("scroll", onLoopScroll, { passive: true });
  window.addEventListener("scrollend", () => wrapIfInEcho({ force: true }), { passive: true });
  // 組版が変わると端数も変わる（画像と書体の読み込み・幅の変更）
  window.addEventListener("resize", alignLoop, { passive: true });
  document.fonts?.ready.then(alignLoop);
  // 再読み込みで写しの中へ位置が戻された場合も、本物の側へ移す
  window.addEventListener("load", () => { alignLoop(); wrapIfInEcho({ force: true }); }, { once: true });
}

// ───── スクロールに応えるもの：ヘッダーと髪 ─────

const siteHeader = document.querySelector("[data-site-header]");
const heroStage = document.querySelector(".hero-stage");
const hairs = [...document.querySelectorAll("[data-grow]")];
const stains = [...document.querySelectorAll("[data-spread]")];
const desktopHeader = window.matchMedia("(min-width: 721px)");
let lastScrollY = window.scrollY;
let scrollFrame = 0;

const clamp01 = (value) => Math.min(Math.max(value, 0), 1);
const easeInOut = (t) => (t < 0.5 ? 4 * t ** 3 : 1 - (-2 * t + 2) ** 3 / 2);

function updateOnScroll() {
  scrollFrame = 0;
  const y = window.scrollY;
  const viewport = window.innerHeight;
  const stageHeight = heroStage.offsetHeight;

  // 下へ読み進めている間は退き、上へ戻すと出る。
  // 周の境目で移った直後は、移る前と同じ状態のまま（頭に戻ったことをヘッダーで知らせない）
  if (suppressHeaderOnce) {
    suppressHeaderOnce = false;
  } else if (desktopHeader.matches) {
    const goingDown = y > lastScrollY + 2;
    const goingUp = y < lastScrollY - 2;
    if (goingDown && y > stageHeight * 0.6) siteHeader.classList.add("is-away");
    else if (goingUp || y <= 0) siteHeader.classList.remove("is-away");
  } else {
    siteHeader.classList.remove("is-away");
  }
  lastScrollY = y;
  renderHaunt();

  // 写しのキービジュアルは、暗がりから浮かび上がる。写しの上端が画面の下端に入ったときが最初（暗がり）、
  // 画面の上端に着いたときが最後（本物と同じ明るさ）。最後で本物と同じ絵になっていないと、移ったのが見える
  // 最後の顔も同じ。暗がりから出てきて、ページの終わりで画面を埋める
  for (const room of [loopEcho, loopFinal]) {
    if (!room) continue;
    const t = clamp01((viewport - room.getBoundingClientRect().top) / viewport);
    room.style.setProperty("--dusk", (1 - easeInOut(t)).toFixed(3));
  }

  // 髪も染みも、自分の上端が画面の下端に入ったときが最初、画面の上から 2 割まで上がったときが最後。
  // 髪は長さの 3 割 → 全部、染みは芯だけ（.5）→ 縁まで（1.2）。その間は、ゆっくり出て、ゆっくり止まる
  for (const hair of hairs) {
    const t = reducedMotion.matches ? 1 : easeInOut(clamp01((viewport - hair.getBoundingClientRect().top) / (viewport * 0.8)));
    hair.style.setProperty("--grow", (0.3 + 0.7 * t).toFixed(3));
  }
  for (const stain of stains) {
    const t = reducedMotion.matches ? 1 : easeInOut(clamp01((viewport - stain.getBoundingClientRect().top) / (viewport * 0.8)));
    stain.style.setProperty("--spread", (0.5 + 0.7 * t).toFixed(3));
  }
}

function requestScrollUpdate() {
  if (!scrollFrame) scrollFrame = window.requestAnimationFrame(updateOnScroll);
}

window.addEventListener("scroll", requestScrollUpdate, { passive: true });
window.addEventListener("resize", requestScrollUpdate, { passive: true });
desktopHeader.addEventListener("change", requestScrollUpdate);
reducedMotion.addEventListener("change", requestScrollUpdate);
renderCamera({ animate: false });
updateOnScroll();

// いま読んでいる章を目次に示す。目次に無い章（予告動画・ストーリー・クレジット）へ入ったら、どれも示さない
const navLinks = [...document.querySelectorAll(".site-nav a[href^='#']")];
const chapterSections = [...document.querySelectorAll("main section[id]")];
if (navLinks.length > 0 && "IntersectionObserver" in window) {
  const navObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      for (const link of navLinks) {
        if (link.getAttribute("href") === `#${entry.target.id}`) link.setAttribute("aria-current", "true");
        else link.removeAttribute("aria-current");
      }
    }
  }, { rootMargin: "-45% 0px -50% 0px" });
  for (const section of chapterSections) navObserver.observe(section);
}
