const feeds = [...document.querySelectorAll("[data-camera-feed]")];
const cameraButtons = [...document.querySelectorAll("[data-camera-button]")];
const screen = document.querySelector(".crt-screen");
const indicator = document.querySelector("#camera-indicator");
const status = document.querySelector("#camera-status");
const cameraConsole = document.querySelector(".camera-console");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

const CAMERA_COUNT = 3;
const AUTO_SWITCH_INTERVAL_MS = 6000;

let currentCamera = readCameraFromUrl();
let noiseTimer = null;
let autoSwitchTimer = null;

function readCameraFromUrl() {
  const raw = new URL(window.location.href).searchParams.get("camera");
  return /^[1-3]$/.test(raw ?? "") ? Number(raw) : 1;
}

function cameraLabel(camera) {
  return `CAM ${String(camera).padStart(2, "0")}`;
}

function writeCameraToUrl(camera, historyMode) {
  const url = new URL(window.location.href);
  url.searchParams.set("camera", String(camera));
  window.history[historyMode]({ camera }, "", url);
}

function stopSwitchNoise() {
  window.clearTimeout(noiseTimer);
  noiseTimer = null;
  screen.classList.remove("is-switching");
}

function showSwitchNoise() {
  if (reducedMotion.matches) return;

  stopSwitchNoise();
  window.requestAnimationFrame(() => {
    screen.classList.add("is-switching");
    noiseTimer = window.setTimeout(stopSwitchNoise, 190);
  });
}

function renderCamera(camera, { animate = true } = {}) {
  const label = cameraLabel(camera);
  currentCamera = camera;

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

  indicator.textContent = label;
  status.textContent = `カメラ ${String(camera).padStart(2, "0")}`;
  if (animate) showSwitchNoise();
}

function stopAutoSwitch() {
  window.clearTimeout(autoSwitchTimer);
  autoSwitchTimer = null;
  cameraConsole.classList.remove("is-running");
}

// 選ばれているカメラの下に、次の切り替えまでの残りを引く。タイマーを掛け直すたびに頭から
function restartSwitchProgress() {
  cameraConsole.style.setProperty("--switch-ms", `${AUTO_SWITCH_INTERVAL_MS}ms`);
  cameraConsole.classList.remove("is-running");
  void cameraConsole.offsetWidth;
  cameraConsole.classList.add("is-running");
}

function scheduleAutoSwitch() {
  stopAutoSwitch();
  if (document.hidden) return;

  autoSwitchTimer = window.setTimeout(advanceCamera, AUTO_SWITCH_INTERVAL_MS);
  restartSwitchProgress();
}

function advanceCamera() {
  const nextCamera = (currentCamera % CAMERA_COUNT) + 1;
  renderCamera(nextCamera);
  writeCameraToUrl(nextCamera, "replaceState");
  scheduleAutoSwitch();
}

function selectCamera(camera) {
  scheduleAutoSwitch();
  if (camera === currentCamera) return;
  renderCamera(camera);
  writeCameraToUrl(camera, "pushState");
}

for (const button of cameraButtons) {
  button.addEventListener("click", () => {
    selectCamera(Number(button.dataset.cameraButton));
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
    selectCamera(nextIndex + 1);
  });
}

reducedMotion.addEventListener("change", (event) => {
  if (event.matches) stopSwitchNoise();
});

document.addEventListener("visibilitychange", () => {
  if (document.hidden) {
    stopAutoSwitch();
  } else {
    scheduleAutoSwitch();
  }
});

window.addEventListener("popstate", () => {
  renderCamera(readCameraFromUrl(), { animate: false });
  scheduleAutoSwitch();
});

renderCamera(currentCamera, { animate: false });
writeCameraToUrl(currentCamera, "replaceState");
scheduleAutoSwitch();

// 記録の荷札と銘板は、既定の CSS では見えている。ここで js-reveal を付けてから隠すので、
// この処理が動かなくても内容は消えない。
const revealTargets = [...document.querySelectorAll("[data-reveal]")];
if (revealTargets.length > 0) {
  document.documentElement.classList.add("js-reveal");
  if (reducedMotion.matches || !("IntersectionObserver" in window)) {
    for (const target of revealTargets) target.classList.add("is-revealed");
  } else {
    const revealObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        entry.target.classList.add("is-revealed");
        revealObserver.unobserve(entry.target);
      }
    }, { rootMargin: "0px 0px -8% 0px", threshold: 0.12 });
    for (const target of revealTargets) revealObserver.observe(target);
  }
}

// ───── ヘッダー・目次・経過時間・キービジュアルの暗転 ─────

const siteHeader = document.querySelector("[data-site-header]");
const hero = document.querySelector(".hero");
const desktopHeader = window.matchMedia("(min-width: 721px)");
let lastScrollY = window.scrollY;
let scrollFrame = 0;

// 地の上に出たら面を敷く。下へ読み進めている間は退き、上へ戻すと出る
function updateOnScroll() {
  scrollFrame = 0;
  const y = window.scrollY;
  const heroHeight = hero.offsetHeight;

  if (desktopHeader.matches) {
    siteHeader.classList.toggle("is-solid", y > 24);
    const goingDown = y > lastScrollY + 2;
    const goingUp = y < lastScrollY - 2;
    if (goingDown && y > heroHeight * 0.6) siteHeader.classList.add("is-away");
    else if (goingUp || y <= heroHeight * 0.6) siteHeader.classList.remove("is-away");
  } else {
    siteHeader.classList.remove("is-solid", "is-away");
  }
  lastScrollY = y;

  // 0 から 0.7 まで、キービジュアルの高さを読み進める間に線形で落とす
  const dim = reducedMotion.matches ? 0 : Math.min(Math.max(y / heroHeight, 0), 1) * 0.7;
  hero.style.setProperty("--hero-dim", dim.toFixed(3));
}

window.addEventListener("scroll", () => {
  if (!scrollFrame) scrollFrame = window.requestAnimationFrame(updateOnScroll);
}, { passive: true });
desktopHeader.addEventListener("change", updateOnScroll);
reducedMotion.addEventListener("change", updateOnScroll);
updateOnScroll();

// いま読んでいる章を目次に示す
const navLinks = [...document.querySelectorAll(".site-nav a[href^='#']")];
// 目次に無い章（予告動画・ストーリー・クレジット）へ入ったら、どれも示さない
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

// ページを開いてからの経過時間。監視カメラの時刻表示の形で、実際の経過だけを出す
const clock = document.querySelector("[data-site-clock]");
if (clock) {
  const openedAt = performance.now();
  const pad = (value) => String(value).padStart(2, "0");
  const tick = () => {
    const total = Math.floor((performance.now() - openedAt) / 1000);
    clock.textContent = `${pad(Math.floor(total / 3600))}:${pad(Math.floor(total / 60) % 60)}:${pad(total % 60)}`;
  };
  tick();
  window.setInterval(tick, 1000);
}
