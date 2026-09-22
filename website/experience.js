const experience = window.MAWARIMI_EXPERIENCE ?? {};

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
