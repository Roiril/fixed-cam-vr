// 動くのは、スクロールとホバー（指ならタップ）に応えるものだけ。時間で勝手に進むものは置かない。
// 動かすのは理由を言えるものだけ：
//   カメラの切り替え（選んだ映像に替わる。替わった瞬間だけ 180ms 乱れる）
//   髪（読み進めるほど下へ伸びる。裾の下の 3 割から全部まで）
//   染み（読み進めるほど芯から縁へ広がる）
//   ヘッダー（読んでいる間は退き、戻ると出る）
//   ページの終わり（暗がりを抜けると、また最初の部屋に戻っている。体験と同じく 3 周）

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
  if (!loopBridge) return;
  loopBridge.style.height = "";
  const length = docTop(loopEcho) - docTop(realHead[0]);
  const base = loopBridge.getBoundingClientRect().height;
  loopBridge.style.height = `${base + (Math.ceil(length) - length)}px`;
}

function removeLoop() {
  loopBridge?.remove();
  loopEcho?.remove();
  loopBridge = null;
  loopEcho = null;
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
  lap += 1;
  if (lap >= LAPS) removeLoop();
  return true;
}

function onLoopScroll() {
  if (!loopEcho) return;
  wrapIfInEcho();
  window.clearTimeout(idleTimer);
  idleTimer = window.setTimeout(() => wrapIfInEcho({ force: true }), 140);
}

buildLoop();
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

  // 写しのキービジュアルは、暗がりから浮かび上がる。写しの上端が画面の下端に入ったときが最初（暗がり）、
  // 画面の上端に着いたときが最後（本物と同じ明るさ）。最後で本物と同じ絵になっていないと、移ったのが見える
  if (loopEcho) {
    const t = clamp01((viewport - loopEcho.getBoundingClientRect().top) / viewport);
    loopEcho.style.setProperty("--dusk", (1 - easeInOut(t)).toFixed(3));
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
