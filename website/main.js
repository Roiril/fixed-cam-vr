const feeds = [...document.querySelectorAll("[data-camera-feed]")];
const cameraButtons = [...document.querySelectorAll("[data-camera-button]")];
const screen = document.querySelector(".crt-screen");
const indicator = document.querySelector("#camera-indicator");
const status = document.querySelector("#camera-status");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

const CAMERA_COUNT = 3;

let currentCamera = readCameraFromUrl();
let noiseTimer = null;

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

function selectCamera(camera) {
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

window.addEventListener("popstate", () => {
  renderCamera(readCameraFromUrl(), { animate: false });
});

renderCamera(currentCamera, { animate: false });
writeCameraToUrl(currentCamera, "replaceState");
