import { readFile, stat } from "node:fs/promises";
import { dirname, join, normalize } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { headingText, latinText, pageText, uniqueChars } from "./font-text.mjs";

const websiteRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const sourceFiles = ["index.html", "styles.css", "main.js", "experience.css", "experience-config.js", "experience.js", "entrance.css", "entrance-geometry.js", "entrance-glass.js", "entrance.js"];
sourceFiles.push("handheld.js", "handheld.css", "perspective-game/embed.js");
sourceFiles.push("entrance-timing.js", "entrance-audio.js");
const seoFiles = ["robots.txt", "sitemap.xml", "site.webmanifest"];
const verificationFiles = ["google5081a8a413a7871f.html"];
const requiredAssets = [
  "assets/handheld.webp",
  "perspective-game/index.html",
  "perspective-game/embed.css",
  "perspective-game/assets/index-BZnkwpUb.js",
  "perspective-game/assets/three-BfIlZwn7.js",
  "perspective-game/assets/index-DdqsXLdv.css",
  "perspective-game/assets/three-LICENSE.txt",
  "assets/favicon.png",
  "assets/hero-desktop.webp",
  "assets/hero-small.webp",
  "assets/hero-mobile.webp",
  "assets/title.png",
  "assets/sfx_shatter.wav",
  "assets/camera-fiction-01.webp",
  "assets/camera-fiction-02.webp",
  "assets/camera-fiction-03.webp",
  "assets/camera-01.webp",
  "assets/camera-02.webp",
  "assets/camera-03.webp",
  "assets/camera-01-quest.webp",
  "assets/camera-02-quest.webp",
  "assets/camera-03-quest.webp",
  "assets/camera-01-doll.webp",
  "assets/crt.webp",
  "assets/investigation-request.webp",
  "assets/room-empty.webp",
  "assets/drape-rail.webp",
  "assets/drape-folds.webp",
  "assets/drape-hem.webp",
  "assets/wall.webp",
  "assets/cctv.webp",
  "assets/hair.webp",
  "assets/stain.webp",
  "assets/grain.png",
  "assets/footage-fracture.mp4",
  "assets/footage-fracture.webp",
  "assets/footage-error.mp4",
  "assets/footage-error.webp",
  "assets/footage-eye.mp4",
  "assets/footage-eye.webp",
  "assets/yuji-boku.woff2",
  "assets/shippori-mincho.woff2",
  "assets/ibm-plex-mono-400.woff2",
  "assets/ibm-plex-mono-500.woff2",
  "assets/yuji-boku-OFL.txt",
  "assets/shippori-mincho-OFL.txt",
  "assets/ibm-plex-mono-OFL.txt"
];
const builtFiles = [...sourceFiles, ...seoFiles, ...verificationFiles, ...requiredAssets];
const errors = [];

async function mustBeFile(relativePath, base = websiteRoot) {
  try {
    const info = await stat(join(base, relativePath));
    if (!info.isFile() || info.size === 0) errors.push(`${relativePath}: empty or not a file`);
  } catch {
    errors.push(`${relativePath}: missing`);
  }
}

const sources = await Promise.all(sourceFiles.map((file) => readFile(join(websiteRoot, file), "utf8")));
const [html, baseCss, js] = sources;
const css = [baseCss, sources[3], sources[6]].join("\n");
const [robots, sitemap, manifestSource] = await Promise.all(seoFiles.map((file) => readFile(join(websiteRoot, file), "utf8")));

for (const file of sourceFiles) await mustBeFile(file);
for (const file of seoFiles) await mustBeFile(file);
for (const file of verificationFiles) await mustBeFile(file);
for (const asset of requiredAssets) await mustBeFile(asset);

const verification = await readFile(join(websiteRoot, verificationFiles[0]), "utf8");
if (verification.trim() !== "google-site-verification: google5081a8a413a7871f.html") {
  errors.push(`${verificationFiles[0]}: unexpected verification content`);
}

const localReferences = new Set();
try {
  const manifest = JSON.parse(manifestSource);
  for (const icon of manifest.icons ?? []) {
    if (typeof icon.src === "string" && !/^[a-z]+:/i.test(icon.src)) localReferences.add(icon.src.replace(/^\//, ""));
  }
} catch {
  errors.push("site.webmanifest: invalid JSON");
}

for (const content of [html, css]) {
  const referencePattern = /(?:href|src|srcset)=["']([^"']+)["']|url\(["']?([^"')]+)["']?\)/g;
  for (const match of content.matchAll(referencePattern)) {
    for (const rawGroup of [match[1], match[2]]) {
      if (!rawGroup) continue;
      if (rawGroup.startsWith("data:")) continue;
      for (const token of rawGroup.split(",")) {
        const raw = token.trim().split(/\s+/)[0];
        if (!raw || raw.startsWith("#") || raw.startsWith("data:") || /^[a-z]+:/i.test(raw)) continue;
        localReferences.add(raw.split(/[?#]/)[0]);
      }
    }
  }
}

for (const reference of localReferences) {
  const localPath = normalize(reference);
  if (localPath.startsWith("..")) errors.push(`${reference}: path escapes website root`);
  else await mustBeFile(localPath);
}

const forbiddenReferences = ["scene.js", "cdn."];
const combinedSource = `${html}\n${css}\n${js}`.toLowerCase();
for (const forbidden of forbiddenReferences) {
  if (combinedSource.includes(forbidden)) errors.push(`forbidden legacy or remote reference: ${forbidden}`);
}

const sectionCount = html.match(/<section\b/g)?.length ?? 0;
if (sectionCount !== 8) errors.push(`expected 8 sections, found ${sectionCount}`);

// 2026-11-15 の指示「根本からセクションとか並びとか変えていいよ」で、演出一覧を展示履歴の前へ移した。
// 物語（ストーリー）→ 体験の中身（演出一覧）→ 作品の記録（展示履歴・クレジット）の順。
const sectionMarkers = [
  'class="hero"',
  'id="trailer"',
  'id="perspective-lab"',
  'id="fixed-view"',
  'id="scenario"',
  'id="footage"',
  'id="archive"',
  'id="credits"'
];
let previousSectionIndex = -1;
for (const marker of sectionMarkers) {
  const sectionIndex = html.indexOf(marker);
  if (sectionIndex === -1) errors.push(`required section is missing: ${marker}`);
  else if (sectionIndex <= previousSectionIndex) errors.push(`section order is invalid: ${marker}`);
  previousSectionIndex = sectionIndex;
}
// 章は同じ格子に載る。番号の札は置かない（並びの順は章題と柱の線で読む）
if ((html.match(/<section id="[^"]+" class="chapter /g)?.length ?? 0) !== 7) errors.push("every chapter after the hero must use the shared chapter grid");
if (html.includes('class="section-number"')) errors.push("chapter number labels were removed in the 2026-11-15 redesign");
const navOrder = [...(html.match(/<nav class="site-nav"[\s\S]*?<\/nav>/)?.[0] ?? "").matchAll(/href="#([^"]+)"/g)].map((m) => m[1]);
if (navOrder.join(",") !== "perspective-lab,fixed-view,footage,archive") errors.push(`navigation must follow the page order: ${navOrder.join(",")}`);

const pageCopy = html.replace(/<[^>]+>/g, "").replace(/\s+/g, " ");
const requiredCopy = [
  "固定視点",
  "監視カメラに映る自分を見ながら、現実の空間を歩く",
  "ストーリー",
  "展示履歴",
  "演出一覧",
  "あなたは、怪異調査員として、呪われた壁の調査に向かう。"
];
for (const copy of requiredCopy) {
  if (!pageCopy.includes(copy)) errors.push(`required copy is missing: ${copy}`);
}

const forbiddenFeatures = [
  "生成イメージ",
  "画面の中の自分",
  "死角",
  "企画について",
  "blind-spot",
  "setautoenabled"
];
for (const forbidden of forbiddenFeatures) {
  if (combinedSource.includes(forbidden)) errors.push(`forbidden content or feature remains: ${forbidden}`);
}

if ((html.match(/data-camera-button=/g)?.length ?? 0) !== 3) errors.push("manual camera controls must contain 3 buttons");
if (!html.includes('class="crt-body" src="assets/crt.webp"')) errors.push("camera feeds must be shown inside the CRT photograph");
if (!html.includes('class="wall-evidence" src="assets/camera-01-doll.webp"')) errors.push("investigation photo must use the doll-vision render of camera 01");
for (const camera of ["01", "02", "03"]) {
  if (!html.includes(`src="assets/camera-fiction-${camera}.webp"`)) errors.push(`camera ${camera} must use the previously generated fictional location`);
}
if (!html.includes('href="assets/investigation-request.webp"')) errors.push("full-size investigation request link is missing");
if (!html.includes('<section id="perspective-lab"') || !html.includes('<h2 id="perspective-title">固定視点とは</h2>')) errors.push("fixed viewpoint introduction must come before real-world viewpoint");
if (/<h2 id="perspective-title">固定視点とは<\/h2>\s*<p class="section-description">/.test(html)) errors.push("fixed viewpoint introduction must not have a section description");
if (!html.includes('<h2 id="fixed-view-title">実世界×固定視点</h2>')) errors.push("real-world fixed viewpoint needs the requested chapter title");
if (!html.includes('<a href="#perspective-lab">固定視点とは</a>') || !html.includes('<a href="#fixed-view">実世界×固定視点</a>')) errors.push("navigation must match the chapter titles");
if (!html.includes('<a href="#footage">演出一覧</a>')) errors.push("navigation must use the requested footage title");
const trailerIndex = html.indexOf('<section id="trailer"');
if (trailerIndex < html.indexOf('class="hero"') || trailerIndex > html.indexOf('<section id="perspective-lab"')) errors.push("trailer must follow the hero");
if (!html.includes('<h2 id="trailer-title">予告動画</h2>')) errors.push("trailer needs the requested heading");
if (!html.includes('src="https://www.youtube-nocookie.com/embed/ipoU4gU9G4k"') || !html.includes('href="https://youtu.be/ipoU4gU9G4k"')) errors.push("requested trailer is missing");
if (!html.includes('この映像は2026.09.01時点で作成したものです。') || !html.includes('DCEXPOに向けた新しい予告動画を作成中です。')) errors.push("trailer notes are missing");
if ((html.match(/ネタバレを含みます/g)?.length ?? 0) !== 2) errors.push("trailer and footage must use the same spoiler note");
for (const oldCopy of ["この視点に入る", "ここから先は、体験中に見えるもの。", "初めての驚きを残しておきたい方は", "こだわりのVR演出", "古いバージョンです。", "ネタバレあり"]) {
  if (html.includes(oldCopy)) errors.push(`obsolete copy remains: ${oldCopy}`);
}
if (html.includes('class="plate-qr"') || html.includes('class="plate-url"')) errors.push("credit code or URL remains");
if (!js.includes('prefers-reduced-motion')) errors.push("reduced motion support is missing");
// 2026-11-15 の 3 つ目の指示「アニメーションは、スクロール or ホバー（スマホではタップ）のみ」。
// 時間で勝手に進むもの（6 秒ごとのカメラ切替・経過時間の表示・走査線の帯）を置かない。
if (/setInterval\(|AUTO_SWITCH_INTERVAL_MS/.test(js)) errors.push("nothing may advance on a timer: motion is driven by scroll, hover or tap only");
if (/animation:[^;]*infinite/.test(css)) errors.push("looping CSS animations are not allowed: motion is driven by scroll, hover or tap only");
if (html.includes("data-site-clock")) errors.push("the elapsed-time clock was removed with the timer-driven motion");
// 2026-11-16 の指示：スクロールを止めて留める演出と、章題が動くのをやめる。カメラは押して選ぶ
if (/position:\s*sticky/.test(css) || html.includes("data-camera-scroll")) errors.push("nothing may pin to the viewport while scrolling (no sticky chapters or titles)");
if (html.includes("data-reveal") || js.includes("js-reveal") || css.includes("js-reveal")) errors.push("entrance reveals were removed: move only what has a stated reason");
// 理由のある動き 2 つ：髪と染みは読み進めた位置で伸びる・広がる
if ((html.match(/data-grow/g)?.length ?? 0) !== 2 || (html.match(/data-spread/g)?.length ?? 0) !== 2 || !js.includes('"--grow"') || !js.includes('"--spread"')) errors.push("hair growth and stain spread must follow the scroll position");
// 2026-11-16 の指示：一番下まで読むと、気づいたら一番上から始まっていて、それが 3 回。再読み込みやワープに見えないこと。
// 移すのは瞬時（smooth だと移る途中が見える）、スクロールバーは出さない（つまみが跳ぶ）、3 周目で写しを外す
if (!/const LAPS = 3;/.test(js) || !js.includes('behavior: "instant"') || !js.includes("removeLoop()")) errors.push("the page must loop three times with an instant, invisible jump");
if (!/scrollbar-width:\s*none/.test(css)) errors.push("the scrollbar must stay hidden so the loop does not show as a jumping thumb");
if (!/button\.addEventListener\("click",[\s\S]*?selectedCamera = camera;/.test(js)) errors.push("camera buttons must select their camera on click");
// 章の地は幕と壁の 2 つ。幕の章には幕の絵、壁の章には壁の地が要る
if ((html.match(/class="chapter on-drape /g)?.length ?? 0) !== 4 || (html.match(/class="chapter on-wall /g)?.length ?? 0) !== 3) errors.push("chapters must alternate between the drape (4) and the wall (3)");
if ((html.match(/<div class="drape" aria-hidden="true">/g)?.length ?? 0) !== 4) errors.push("every drape chapter needs its curtain layers");
if (/data-(?:auto-switch|camera-autoplay)|自動切替|自動再生|auto(?:matic)?\s+(?:switch|play)/i.test(html)) {
  errors.push("automatic camera switching must not add an on/off control");
}
if (!html.includes('class="skip-link"') || !html.includes('aria-live="polite"')) errors.push("required accessibility hooks are missing");
const gate = html.match(/<details\b[^>]*id="footage-gate"[^>]*>/)?.[0] ?? "";
if (!gate || /\sopen(?:\s|=|>)/.test(gate)) errors.push("spoiler footage must start in a closed details element");
const video = html.match(/<video\b[^>]*id="footage-player"[^>]*>/)?.[0] ?? "";
if (!video || /\s(?:src|poster|autoplay)=?/.test(video) || !video.includes('preload="none"')) errors.push("footage must not expose or fetch media before the spoiler gate opens");
if (!html.includes('<div id="game-stage" class="game-stage"></div>')) errors.push("the perspective experience must create its iframe in JavaScript");
if ((html.match(/<iframe\b/g)?.length ?? 0) !== 1) errors.push("only the requested trailer may be embedded in the initial HTML");
if (!sources[10].includes('if (active && autoStart && !frame) start();') || !sources[12].includes("app.dataset.mode !== 'fixed'")) errors.push("the handheld game must open automatically in fixed view when visible");
for (const file of sourceFiles.filter(file => file.endsWith(".js"))) {
  const syntax = spawnSync(process.execPath, ["--check", join(websiteRoot, file)], { encoding: "utf8" });
  if (syntax.status !== 0) errors.push(`${file}: ${syntax.stderr}`);
}
if (!html.includes('<link rel="canonical" href="https://mawarimi.vercel.app/">')) errors.push("canonical URL is missing");
if (!html.includes('property="og:image"') || !html.includes('name="twitter:card"')) errors.push("social preview metadata is missing");
const structuredDataMatch = html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/);
if (!structuredDataMatch) {
  errors.push("structured data is missing");
} else {
  try {
    const structuredData = JSON.parse(structuredDataMatch[1]);
    const graph = structuredData["@graph"];
    const website = graph?.find((entry) => entry["@type"] === "WebSite");
    const work = graph?.find((entry) => entry["@type"] === "CreativeWork");
    const requiredNames = ["まわりみ", "廻り視", "マワリミ", "Mawarimi"];
    for (const name of requiredNames) {
      if (!website?.alternateName?.includes(name)) errors.push(`website alternate name is missing: ${name}`);
      if (!work?.alternateName?.includes(name)) errors.push(`work alternate name is missing: ${name}`);
    }
  } catch {
    errors.push("structured data is invalid JSON");
  }
}
if (!html.includes('href="https://ivrc.net/2026/release3/"') || !html.includes('href="https://www.dcexpo.jp/"')) errors.push("archive source links are missing");
const creditsMarkup = html.match(/<dl class="credit-list">([\s\S]*?)<\/dl>/)?.[1] ?? "";
if ((creditsMarkup.match(/<dt>/g)?.length ?? 0) !== 10) errors.push("credits must keep all 10 roles");
if (!html.includes('"name": "Roil Studio"') || !html.includes('<span>チーム：Roil Studio</span>')) errors.push("credit organization name is missing");
if (!html.includes('href="mailto:rinkyouaoi@gmail.com"')) errors.push("credit contact link is missing");
if (!html.includes('href="https://my-portfolio-ruby-delta-87.vercel.app/"')) errors.push("portfolio link is missing");
if (!pageCopy.includes("総合優勝") || !structuredDataMatch?.[1].includes('IVRC2026 総合優勝')) errors.push("overall winner copy is missing from page or structured data");

const navMarkup = html.match(/<nav class="site-nav"[\s\S]*?<\/nav>/)?.[0] ?? "";
if (!navMarkup.includes('href="#archive"')) errors.push("nav link to the archive section is missing");

const updatedOnPage = html.match(/data-updated[^>]*>([^<]+)</)?.[1]?.trim();
const lastmod = sitemap.match(/<lastmod>([^<]+)<\/lastmod>/)?.[1]?.trim();
if (!updatedOnPage) errors.push("page update date (data-updated) is missing");
else if (!lastmod) errors.push("sitemap.xml lastmod is missing");
else if (updatedOnPage !== lastmod) errors.push(`page update date ${updatedOnPage} does not match sitemap lastmod ${lastmod}`);

const fontManifestPath = "assets/fonts-manifest.json";
const fontCoverage = [
  { key: "mincho", file: "shippori-mincho", chars: uniqueChars(pageText(html)) },
  { key: "yuji", file: "yuji-boku", chars: uniqueChars(headingText(html)) },
  { key: "plex", file: "ibm-plex-mono", chars: uniqueChars(latinText(html)) }
];
let fontManifest = null;
try {
  fontManifest = JSON.parse(await readFile(join(websiteRoot, fontManifestPath), "utf8"));
} catch {
  errors.push(`${fontManifestPath}: missing or invalid JSON → run npm run fonts`);
}
if (fontManifest) {
  for (const { key, file, chars } of fontCoverage) {
    const covered = new Set([...(fontManifest[key]?.chars ?? "")]);
    if (!fontManifest[key]) {
      errors.push(`fonts: ${file} is not in ${fontManifestPath} → run npm run fonts`);
      continue;
    }
    const upstreamLacks = new Set([...(fontManifest[key]?.missing ?? "")]);
    const lacking = chars.filter((ch) => !covered.has(ch) && !upstreamLacks.has(ch));
    const unfixable = chars.filter((ch) => upstreamLacks.has(ch));
    if (lacking.length > 0) errors.push(`fonts: ${file} lacks ${lacking.join(" ")} → run npm run fonts`);
    if (unfixable.length > 0) errors.push(`fonts: the upstream ${file} has no glyph for ${unfixable.join(" ")} → change the copy (npm run fonts cannot fix this)`);
  }
}
if (!robots.includes("Sitemap: https://mawarimi.vercel.app/sitemap.xml")) errors.push("robots.txt sitemap URL is missing");
if (!sitemap.includes("<loc>https://mawarimi.vercel.app/</loc>")) errors.push("sitemap canonical URL is missing");

if (errors.length === 0) {
  const build = spawnSync(process.execPath, [join(websiteRoot, "scripts/build.mjs")], {
    cwd: websiteRoot,
    encoding: "utf8"
  });
  process.stdout.write(build.stdout);
  process.stderr.write(build.stderr);
  if (build.status !== 0) errors.push(`build failed with exit code ${build.status}`);
}

if (errors.length === 0) {
  const distRoot = join(websiteRoot, "dist");
  for (const file of builtFiles) await mustBeFile(file, distRoot);
  // html / css / webmanifest が参照するファイルが dist/ にも入っているか（build.mjs の publicFiles への足し忘れを捕まえる）
  for (const reference of localReferences) {
    const localPath = normalize(reference);
    if (!localPath.startsWith("..")) await mustBeFile(localPath, distRoot);
  }
}

if (errors.length > 0) {
  console.error("Check failed:");
  for (const error of errors) console.error(`- ${error}`);
  process.exitCode = 1;
} else {
  console.log(`Check passed: ${sourceFiles.length} source files, ${seoFiles.length} SEO files, ${verificationFiles.length} verification file, ${requiredAssets.length} assets, ${builtFiles.length} built files.`);
}
