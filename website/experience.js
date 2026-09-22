const experience = window.MAWARIMI_EXPERIENCE ?? {};

const gameLaunch = document.querySelector("#game-launch");
const gameUnavailable = document.querySelector("#game-unavailable");
const gameStage = document.querySelector("#game-stage");
const gameState = document.querySelector("#game-state");
const gameClose = document.querySelector("#game-close");
const gameFallback = document.querySelector("#game-fallback");

function resolveGameUrl(rawUrl) {
  if (typeof rawUrl !== "string" || !rawUrl.trim()) return null;
  const value = rawUrl.trim();
  try {
    const url = new URL(value, window.location.href);
    if (url.protocol !== "http:" && url.protocol !== "https:") return null;
    if (!/^[a-z][a-z\d+.-]*:/i.test(value) && url.origin !== window.location.origin) return null;
    return url.href;
  } catch {
    return null;
  }
}

if (gameLaunch && gameUnavailable && gameStage && gameState && gameClose && gameFallback) {
  const gameUrl = resolveGameUrl(experience.gameUrl);
  const LOAD_TIMEOUT_MS = 12000;
  let gameFrame = null;
  let loadTimeout = null;

  function showGameRecovery(message) {
    gameState.textContent = message;
    gameFallback.href = gameUrl;
    gameFallback.hidden = false;
  }

  function closeGame() {
    window.clearTimeout(loadTimeout);
    loadTimeout = null;
    gameFrame?.remove();
    gameFrame = null;
    gameClose.hidden = true;
    gameLaunch.hidden = false;
    gameFallback.hidden = true;
    gameFallback.removeAttribute("href");
    gameState.textContent = "";
    gameLaunch.focus();
  }

  if (gameUrl) {
    gameLaunch.hidden = false;
    gameUnavailable.hidden = true;
    gameLaunch.addEventListener("click", () => {
      if (gameFrame) return;
      gameState.textContent = "読み込み中です。";
      gameFallback.hidden = true;
      gameClose.hidden = false;
      gameLaunch.hidden = true;

      const frame = document.createElement("iframe");
      frame.title = "一人称・三人称・固定視点の切り替え体験";
      frame.setAttribute("allow", "fullscreen");
      frame.setAttribute("allowfullscreen", "");
      frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-pointer-lock");
      frame.loading = "eager";
      frame.addEventListener("load", () => {
        if (gameFrame !== frame) return;
        window.clearTimeout(loadTimeout);
        loadTimeout = null;
        showGameRecovery("操作できない場合は別のタブで開いてください。");
      });
      frame.addEventListener("error", () => {
        if (gameFrame !== frame) return;
        window.clearTimeout(loadTimeout);
        loadTimeout = null;
        showGameRecovery("読み込めませんでした。別のタブで開いてください。");
      });
      gameFrame = frame;
      frame.src = gameUrl;
      gameStage.append(frame);
      loadTimeout = window.setTimeout(() => {
        if (gameFrame === frame) showGameRecovery("読み込みに時間がかかっています。別のタブで開いてください。");
      }, LOAD_TIMEOUT_MS);
      gameClose.focus();
    });
    gameClose.addEventListener("click", closeGame);
    document.addEventListener("keydown", (event) => {
      if (event.key !== "Escape" || !gameFrame || document.activeElement === gameFrame) return;
      event.preventDefault();
      closeGame();
    });
  } else {
    gameLaunch.hidden = true;
    gameUnavailable.hidden = false;
  }
}

const footageGate = document.querySelector("#footage-gate");
const footagePlayer = document.querySelector("#footage-player");
const footageTitle = document.querySelector("#footage-title");
const footageDescription = document.querySelector("#footage-description");
const footageMeta = document.querySelector("#footage-meta");
const footageError = document.querySelector("#footage-error");
const footageButtons = [...document.querySelectorAll("[data-video-select]")];

if (footageGate && footagePlayer && footageTitle && footageDescription && footageMeta && footageError) {
  const footage = Array.isArray(experience.footage) ? experience.footage : [];
  let selectedFootage = null;

  function showFootageError() {
    if (!selectedFootage || !footageGate.open) return;
    footageError.replaceChildren();
    footageError.append("動画を再生できません。 ");
    const link = document.createElement("a");
    link.href = selectedFootage.src;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    link.textContent = "動画を別のタブで開く";
    footageError.append(link);
  }

  function selectFootage(item) {
    footagePlayer.pause();
    footageError.replaceChildren();
    selectedFootage = item;
    footageTitle.textContent = item.title;
    footageDescription.textContent = item.description;
    footageMeta.textContent = item.meta;
    for (const button of footageButtons) {
      button.setAttribute("aria-pressed", String(button.dataset.videoSelect === item.id));
    }
    footagePlayer.src = item.src;
    footagePlayer.poster = item.poster;
    footagePlayer.load();
  }

  function clearFootage() {
    footagePlayer.pause();
    selectedFootage = null;
    footagePlayer.removeAttribute("src");
    footagePlayer.removeAttribute("poster");
    footagePlayer.load();
    footageError.replaceChildren();
  }

  for (const button of footageButtons) {
    const item = footage.find((entry) => entry.id === button.dataset.videoSelect);
    if (!item) continue;
    button.addEventListener("click", () => {
      if (footageGate.open && selectedFootage !== item) selectFootage(item);
    });
  }

  footageGate.addEventListener("toggle", () => {
    if (footageGate.open) {
      if (footage.length > 0) selectFootage(footage[0]);
    } else {
      clearFootage();
    }
  });
  footagePlayer.addEventListener("error", showFootageError);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) footagePlayer.pause();
  });
}
