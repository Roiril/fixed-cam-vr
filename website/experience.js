// 演出一覧と、ストーリーの章の博士の説明。
// 一覧の中身（周ごとの並び・名前・説明）は index.html に書いてある（scripts/prepare-fx.py が作る）。
// 折り畳みを開くまで、映像も小さい絵も読み込まない。再生は手で始める。

const footageGate = document.querySelector("#footage-gate");
const footagePlayer = document.querySelector("#footage-player");
const footageViewer = document.querySelector(".footage-viewer");
const footagePart = document.querySelector("#footage-part");
const footageTitle = document.querySelector("#footage-title");
const footageDescription = document.querySelector("#footage-description");
const footageMeta = document.querySelector("#footage-meta");
const footageError = document.querySelector("#footage-error");
const footageButtons = [...document.querySelectorAll("[data-video-select]")];
const briefingPlayer = document.querySelector("#briefing-player");
const fewerMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

if (footageGate && footagePlayer && footageViewer && footagePart && footageTitle && footageDescription && footageMeta && footageError) {
  const footage = footageButtons.map((button) => {
    const row = button.closest("li");
    return {
      button,
      part: button.closest(".fx-part")?.querySelector(".fx-part-name")?.textContent ?? "",
      title: button.querySelector(".fx-name")?.textContent ?? "",
      description: row?.querySelector(".fx-say")?.textContent ?? "",
      meta: row?.querySelector(".fx-meta")?.textContent ?? "",
      src: button.dataset.src,
      poster: button.dataset.poster
    };
  });
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
    footagePart.textContent = item.part;
    footageTitle.textContent = item.title;
    footageDescription.textContent = item.description;
    footageMeta.textContent = item.meta;
    for (const entry of footage) entry.button.setAttribute("aria-pressed", String(entry === item));
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
    for (const entry of footage) entry.button.setAttribute("aria-pressed", "false");
  }

  // 小さい絵もネタバレなので、開いたときに読み込ませる
  function loadThumbs() {
    for (const img of footageGate.querySelectorAll("img[data-src]")) {
      img.src = img.dataset.src;
      img.removeAttribute("data-src");
    }
  }

  for (const item of footage) {
    item.button.addEventListener("click", () => {
      if (!footageGate.open) return;
      if (selectedFootage !== item) selectFootage(item);
      // 選んだ映像が画面の外にあれば、見える所まで寄せる（見えていれば動かない）
      footageViewer.scrollIntoView({ block: "nearest", behavior: fewerMotion.matches ? "instant" : "smooth" });
    });
  }

  footageGate.addEventListener("toggle", () => {
    if (footageGate.open) {
      loadThumbs();
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

// 博士の説明は音が出る。もう片方の映像を始めたら止め、タブを離れても止める
if (briefingPlayer) {
  briefingPlayer.addEventListener("play", () => footagePlayer?.pause());
  footagePlayer?.addEventListener("play", () => briefingPlayer.pause());
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) briefingPlayer.pause();
  });
}
