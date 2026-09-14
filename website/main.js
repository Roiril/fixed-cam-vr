const feeds = [...document.querySelectorAll("[data-camera-feed]")];
const cameraButtons = [...document.querySelectorAll("[data-camera-button]")];
const screen = document.querySelector(".crt-screen");
const indicator = document.querySelector("#camera-indicator");
const status = document.querySelector("#camera-status");
const autoSwitch = document.querySelector("#auto-switch");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

const CAMERA_COUNT = 3;
const AUTO_INTERVAL_MS = 5000;

let currentCamera = readCameraFromUrl();
let autoEnabled = false;
let autoTimer = null;
let noiseTimer = null;

function readCameraFromUrl() {
  const raw = new URL(window.location.href).searchParams.get("camera");
  const camera = /^[1-3]$/.test(raw ?? "1") ? Number(raw ?? "1") : 1;
  return Number.isInteger(camera) && camera >= 1 && camera <= CAMERA_COUNT ? camera : 1;
}

function cameraLabel(camera) {
  return `CAM ${String(camera).padStart(2, "0")}`;
}

function writeCameraToUrl(camera, historyMode) {
  const url = new URL(window.location.href);
  url.searchParams.set("camera", String(camera));
  window.history[historyMode]({ camera }, "", url);
}

function showSwitchNoise() {
  if (reducedMotion.matches) return;

  window.clearTimeout(noiseTimer);
  screen.classList.remove("is-switching");
  window.requestAnimationFrame(() => {
    screen.classList.add("is-switching");
    noiseTimer = window.setTimeout(() => screen.classList.remove("is-switching"), 190);
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
  status.textContent = label;
  if (animate) showSwitchNoise();
}

function stopAutoTimer() {
  window.clearTimeout(autoTimer);
  autoTimer = null;
}

function scheduleAutoSwitch() {
  stopAutoTimer();
  if (!autoEnabled || document.hidden) return;

  autoTimer = window.setTimeout(() => {
    const nextCamera = currentCamera % CAMERA_COUNT + 1;
    renderCamera(nextCamera);
    writeCameraToUrl(nextCamera, "replaceState");
    scheduleAutoSwitch();
  }, AUTO_INTERVAL_MS);
}

function setAutoEnabled(enabled) {
  autoEnabled = enabled;
  autoSwitch.setAttribute("aria-pressed", String(enabled));
  if (enabled) scheduleAutoSwitch();
  else stopAutoTimer();
}

function selectCamera(camera, historyMode = "pushState") {
  if (camera === currentCamera) {
    scheduleAutoSwitch();
    return;
  }

  renderCamera(camera);
  writeCameraToUrl(camera, historyMode);
  scheduleAutoSwitch();
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

autoSwitch.addEventListener("click", () => setAutoEnabled(!autoEnabled));

document.addEventListener("visibilitychange", () => {
  if (document.hidden) stopAutoTimer();
  else scheduleAutoSwitch();
});

reducedMotion.addEventListener("change", (event) => {
  if (event.matches) setAutoEnabled(false);
});

window.addEventListener("popstate", () => {
  renderCamera(readCameraFromUrl(), { animate: false });
  scheduleAutoSwitch();
});

renderCamera(currentCamera, { animate: false });
writeCameraToUrl(currentCamera, "replaceState");
