import { readFile, stat } from "node:fs/promises";
import { dirname, join, normalize } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const websiteRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const sourceFiles = ["index.html", "styles.css", "main.js"];
const requiredAssets = [
  "assets/hero-desktop.webp",
  "assets/hero-small.webp",
  "assets/hero-mobile.webp",
  "assets/crt.webp",
  "assets/camera-01.webp",
  "assets/camera-02.webp",
  "assets/camera-03.webp",
  "assets/yuji-boku.woff2",
  "assets/shippori-mincho.woff2",
  "assets/yuji-boku-OFL.txt",
  "assets/shippori-mincho-OFL.txt"
];
const builtFiles = [...sourceFiles, ...requiredAssets];
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

for (const file of sourceFiles) await mustBeFile(file);
for (const asset of requiredAssets) await mustBeFile(asset);

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

const originalText = "1990年代のサバイバルホラーでは，シーン内に固定されたカメラを通して操作キャラクターを見る視点構成が用いられた．本企画はこの固定視点をHMDと複数のカメラで現実空間に再構成する．画角が変化しないため，視界の一部または全体を事前録画映像や生成映像へ差し替えられ，差し替えの対象は周囲の場面だけでなく画面の中の自分にまで及ぶ．固定画角ゆえの死角の演出も加え，日本人形との追跡劇のホラー体験を構成する．";
if (!html.includes(originalText)) errors.push("企画本文が指定原文と一致しません");
if (!html.includes('class="skip-link"') || !html.includes('aria-live="polite"')) errors.push("required accessibility hooks are missing");

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
  console.log(`Check passed: ${sourceFiles.length} source files, ${requiredAssets.length} assets, ${builtFiles.length} built files.`);
}
