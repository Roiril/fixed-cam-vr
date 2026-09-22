import { readFile, stat } from "node:fs/promises";
import { dirname, join, normalize } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { headingText, latinText, pageText, uniqueChars } from "./font-text.mjs";

const websiteRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const sourceFiles = ["index.html", "styles.css", "main.js", "experience.css", "experience-config.js", "experience.js", "entrance.css", "entrance-geometry.js", "entrance-glass.js", "entrance.js"];
sourceFiles.push("handheld.js", "handheld.css", "perspective-game/embed.js");
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
  "assets/camera-01.webp",
  "assets/camera-02.webp",
  "assets/camera-03.webp",
  "assets/camera-01-quest.webp",
  "assets/camera-02-quest.webp",
  "assets/camera-03-quest.webp",
  "assets/camera-01-doll.webp",
  "assets/crt.webp",
  "assets/investigation-request.webp",
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
  "assets/chakra-petch.woff2",
  "assets/yuji-boku-OFL.txt",
  "assets/shippori-mincho-OFL.txt",
  "assets/ibm-plex-mono-OFL.txt",
  "assets/chakra-petch-OFL.txt"
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
if (sectionCount !== 6) errors.push(`expected 6 sections, found ${sectionCount}`);

const sectionMarkers = [
  'class="hero"',
  'id="fixed-view"',
  'id="perspective-lab"',
  'id="scenario"',
  'id="archive"',
  'id="footage"',
  'id="credits"'
];
let previousSectionIndex = -1;
for (const marker of sectionMarkers) {
  const sectionIndex = html.indexOf(marker);
  if (sectionIndex === -1) errors.push(`required section is missing: ${marker}`);
  else if (sectionIndex <= previousSectionIndex) errors.push(`section order is invalid: ${marker}`);
  previousSectionIndex = sectionIndex;
}

const pageCopy = html.replace(/<[^>]+>/g, "").replace(/\s+/g, " ");
const requiredCopy = [
  "固定視点",
  "監視カメラに映る自分を見ながら、現実の空間を歩く。",
  "ストーリー",
  "展示履歴",
  "こだわりのVR演出",
  "怪異調査員として、呪われた壁の調査に向かう。"
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
  if (!html.includes(`src="assets/camera-${camera}-quest.webp"`)) errors.push(`camera ${camera} must use its Quest render`);
}
if (!html.includes('href="assets/investigation-request.webp"')) errors.push("full-size investigation request link is missing");
if (!html.includes('<div id="perspective-lab"') || !html.includes('<h3 id="perspective-title">体験してみる</h3>')) errors.push("perspective activity must be a part of fixed viewpoint");
if (html.includes('class="plate-qr"') || html.includes('class="plate-url"')) errors.push("credit code or URL remains");
if (!js.includes('searchParams.get("camera")') || !js.includes('addEventListener("popstate"')) errors.push("camera URL or back navigation support is missing");
if (!js.includes('prefers-reduced-motion')) errors.push("reduced motion support is missing");
if (!js.includes("const AUTO_SWITCH_INTERVAL_MS = 6000;")) errors.push("automatic camera interval must be 6000 ms");
if (!js.includes("const nextCamera = (currentCamera % CAMERA_COUNT) + 1;")) errors.push("automatic camera order must loop forward");
if (!/function advanceCamera\(\)\s*{[\s\S]*?writeCameraToUrl\(nextCamera, "replaceState"\);[\s\S]*?scheduleAutoSwitch\(\);[\s\S]*?}/.test(js)) {
  errors.push("automatic camera switching must replace URL state and reschedule");
}
if (!/function selectCamera\(camera\)\s*{\s*scheduleAutoSwitch\(\);\s*if \(camera === currentCamera\) return;[\s\S]*?writeCameraToUrl\(camera, "pushState"\);[\s\S]*?}/.test(js)) {
  errors.push("manual camera selection must reset the timer and avoid duplicate history entries");
}
if (!/button\.addEventListener\("click",[\s\S]*?selectCamera\(/.test(js) || !/button\.addEventListener\("keydown",[\s\S]*?selectCamera\(/.test(js)) {
  errors.push("click and keyboard camera selection must use the manual reset path");
}
if (!/function scheduleAutoSwitch\(\)\s*{[\s\S]*?if \(document\.hidden\) return;[\s\S]*?AUTO_SWITCH_INTERVAL_MS[\s\S]*?}/.test(js)) {
  errors.push("automatic camera timer must not run while the document is hidden");
}
if (!/document\.addEventListener\("visibilitychange",[\s\S]*?stopAutoSwitch\(\);[\s\S]*?scheduleAutoSwitch\(\);[\s\S]*?}\);/.test(js)) {
  errors.push("visibility changes must pause and restart automatic camera switching");
}
if (!/window\.addEventListener\("popstate",[\s\S]*?scheduleAutoSwitch\(\);[\s\S]*?}\);/.test(js)) {
  errors.push("back and forward navigation must restart the automatic camera timer");
}
if (/data-(?:auto-switch|camera-autoplay)|自動切替|自動再生|auto(?:matic)?\s+(?:switch|play)/i.test(html)) {
  errors.push("automatic camera switching must not add an on/off control");
}
if (!html.includes('class="skip-link"') || !html.includes('aria-live="polite"')) errors.push("required accessibility hooks are missing");
const gate = html.match(/<details\b[^>]*id="footage-gate"[^>]*>/)?.[0] ?? "";
if (!gate || /\sopen(?:\s|=|>)/.test(gate)) errors.push("spoiler footage must start in a closed details element");
const video = html.match(/<video\b[^>]*id="footage-player"[^>]*>/)?.[0] ?? "";
if (!video || /\s(?:src|poster|autoplay)=?/.test(video) || !video.includes('preload="none"')) errors.push("footage must not expose or fetch media before the spoiler gate opens");
if (html.includes("<iframe")) errors.push("the perspective experience must not create an iframe before launch");
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
if (!html.includes('"name": "Roil Studio"') || !html.includes('class="plate-maker">Roil Studio')) errors.push("credit organization name is missing");
if (!html.includes('href="mailto:rinkyouaoi@gmail.com"')) errors.push("credit contact link is missing");

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
  { key: "plex", file: "ibm-plex-mono", chars: uniqueChars(latinText(html)) },
  { key: "chakra", file: "chakra-petch", chars: uniqueChars(latinText(html)) }
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
