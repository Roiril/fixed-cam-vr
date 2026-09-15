import { readFile, stat } from "node:fs/promises";
import { dirname, join, normalize } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const websiteRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const sourceFiles = ["index.html", "styles.css", "main.js"];
const seoFiles = ["robots.txt", "sitemap.xml", "site.webmanifest"];
const verificationFiles = ["google5081a8a413a7871f.html"];
const requiredAssets = [
  "assets/favicon.png",
  "assets/hero-desktop.webp",
  "assets/hero-small.webp",
  "assets/hero-mobile.webp",
  "assets/crt.webp",
  "assets/camera-01.webp",
  "assets/camera-02.webp",
  "assets/camera-03.webp",
  "assets/investigation-request.webp",
  "assets/wall-evidence.webp",
  "assets/yuji-boku.woff2",
  "assets/shippori-mincho.woff2",
  "assets/yuji-boku-OFL.txt",
  "assets/shippori-mincho-OFL.txt"
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

const [html, css, js] = await Promise.all(sourceFiles.map((file) => readFile(join(websiteRoot, file), "utf8")));
const [robots, sitemap, manifestSource] = await Promise.all(seoFiles.map((file) => readFile(join(websiteRoot, file), "utf8")));

for (const file of sourceFiles) await mustBeFile(file);
for (const file of seoFiles) await mustBeFile(file);
for (const file of verificationFiles) await mustBeFile(file);
for (const asset of requiredAssets) await mustBeFile(asset);

const verification = await readFile(join(websiteRoot, verificationFiles[0]), "utf8");
if (verification.trim() !== "google-site-verification: google5081a8a413a7871f.html") {
  errors.push(`${verificationFiles[0]}: unexpected verification content`);
}

try {
  JSON.parse(manifestSource);
} catch {
  errors.push("site.webmanifest: invalid JSON");
}

const localReferences = new Set();
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

const forbiddenReferences = ["scene.js", "three", "webgl", "cdn."];
const combinedSource = `${html}\n${css}\n${js}`.toLowerCase();
for (const forbidden of forbiddenReferences) {
  if (combinedSource.includes(forbidden)) errors.push(`forbidden legacy or remote reference: ${forbidden}`);
}

const sectionCount = html.match(/<section\b/g)?.length ?? 0;
if (sectionCount !== 5) errors.push(`expected 5 sections, found ${sectionCount}`);

const sectionMarkers = [
  'class="hero"',
  'id="fixed-view"',
  'id="scenario"',
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

const pageText = html.replace(/<[^>]+>/g, "").replace(/\s+/g, " ");
const requiredCopy = [
  "固定視点",
  "監視カメラに映る自分を見ながら、現実の空間を歩く。",
  "調査依頼",
  "怪異調査員として、呪われた壁の調査に向かう。"
];
for (const copy of requiredCopy) {
  if (!pageText.includes(copy)) errors.push(`required copy is missing: ${copy}`);
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
if (!html.includes('href="assets/investigation-request.webp"')) errors.push("full-size investigation request link is missing");
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
  for (const file of builtFiles) await mustBeFile(file, join(websiteRoot, "dist"));
}

if (errors.length > 0) {
  console.error("Check failed:");
  for (const error of errors) console.error(`- ${error}`);
  process.exitCode = 1;
} else {
  console.log(`Check passed: ${sourceFiles.length} source files, ${seoFiles.length} SEO files, ${verificationFiles.length} verification file, ${requiredAssets.length} assets, ${builtFiles.length} built files.`);
}
