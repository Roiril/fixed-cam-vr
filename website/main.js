// 動くのは、スクロールとホバー（指ならタップ）に応えるものだけ。時間で勝手に進むものは置かない。

const feeds = [...document.querySelectorAll("[data-camera-feed]")];
const cameraButtons = [...document.querySelectorAll("[data-camera-button]")];
const screen = document.querySelector(".crt-screen");
const indicator = document.querySelector("#camera-indicator");
const status = document.querySelector("#camera-status");
const cameraConsole = document.querySelector(".camera-console");
const cameraScroll = document.querySelector("[data-camera-scroll]");
const cameraStage = document.querySelector(".camera-stage");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const canHover = window.matchMedia("(hover: hover)");

const CAMERA_COUNT = 3;

let scrollCamera = 1;   // 読み進めた位置が決めるカメラ
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
  const camera = previewCamera || scrollCamera;
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

// テレビが画面に留まっている区間を 3 つに割り、進んだ分でカメラを決める
function cameraTravel() {
  return Math.max(cameraScroll.offsetHeight - cameraStage.offsetHeight, 1);
}

function updateCameraFromScroll() {
  const progress = Math.min(Math.max(-cameraScroll.getBoundingClientRect().top / cameraTravel(), 0), 1);
  cameraConsole.style.setProperty("--walk", progress.toFixed(4));
  scrollCamera = Math.min(Math.floor(progress * CAMERA_COUNT), CAMERA_COUNT - 1) + 1;
  renderCamera();
}

// 押すと、そのカメラの区間の中ほどまでページを送る
function scrollToCamera(camera) {
  const top = cameraScroll.getBoundingClientRect().top + window.scrollY;
  const target = top + cameraTravel() * ((camera - 0.5) / CAMERA_COUNT);
  window.scrollTo({ top: target, behavior: reducedMotion.matches ? "auto" : "smooth" });
}

for (const button of cameraButtons) {
  const camera = Number(button.dataset.cameraButton);

  button.addEventListener("click", () => {
    previewCamera = 0;
    scrollToCamera(camera);
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

// ───── 章題と物の現れ ─────
// 既定の CSS では見えている。ここで js-reveal を付けてから隠すので、この処理が動かなくても内容は消えない。

const chapters = [...document.querySelectorAll(".chapter")];
const revealTargets = [...document.querySelectorAll("[data-reveal]")];
document.documentElement.classList.add("js-reveal");
if (reducedMotion.matches || !("IntersectionObserver" in window)) {
  for (const chapter of chapters) chapter.classList.add("is-in");
  for (const target of revealTargets) target.classList.add("is-revealed");
} else {
  const chapterObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add("is-in");
      chapterObserver.unobserve(entry.target);
    }
  }, { rootMargin: "0px 0px -22% 0px" });
  for (const chapter of chapters) chapterObserver.observe(chapter);

  const revealObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add("is-revealed");
      revealObserver.unobserve(entry.target);
    }
  }, { rootMargin: "0px 0px -8% 0px", threshold: 0.12 });
  for (const target of revealTargets) revealObserver.observe(target);
}

// ───── スクロールに応えるもの：ヘッダー・髪・カメラ ─────

const siteHeader = document.querySelector("[data-site-header]");
const heroStage = document.querySelector(".hero-stage");
const hairs = [...document.querySelectorAll("[data-grow]")];
const desktopHeader = window.matchMedia("(min-width: 721px)");
let lastScrollY = window.scrollY;
let scrollFrame = 0;

const clamp01 = (value) => Math.min(Math.max(value, 0), 1);

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

  // 髪は、裾が画面の下から 4 割の高さまで上がる間に、3 割から全部まで伸びる
  for (const hair of hairs) {
    const top = hair.getBoundingClientRect().top;
    const grown = reducedMotion.matches ? 1 : 0.3 + 0.7 * clamp01((viewport - top) / (viewport * 0.9));
    hair.style.setProperty("--grow", grown.toFixed(3));
  }

  updateCameraFromScroll();
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
