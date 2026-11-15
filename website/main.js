// 動くのは、スクロールとホバー（指ならタップ）に応えるものだけ。時間で勝手に進むものは置かない。
// 動かすのは理由を言えるものだけ：
//   カメラの切り替え（選んだ映像に替わる。替わった瞬間だけ 180ms 乱れる）
//   髪（読み進めるほど下へ伸びる。裾の下の 3 割から全部まで）
//   染み（読み進めるほど芯から縁へ広がる）
//   ヘッダー（読んでいる間は退き、戻ると出る）

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

  // 下へ読み進めている間は退き、上へ戻すと出る
  if (desktopHeader.matches) {
    const goingDown = y > lastScrollY + 2;
    const goingUp = y < lastScrollY - 2;
    if (goingDown && y > stageHeight * 0.6) siteHeader.classList.add("is-away");
    else if (goingUp || y <= stageHeight * 0.6) siteHeader.classList.remove("is-away");
  } else {
    siteHeader.classList.remove("is-away");
  }
  lastScrollY = y;

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
