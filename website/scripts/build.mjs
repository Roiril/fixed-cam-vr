import { cp, mkdir, rm, lstat } from "node:fs/promises";
import { dirname, join, resolve, basename } from "node:path";
import { fileURLToPath } from "node:url";

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const outputRoot = resolve(websiteRoot, "dist");
const publicFiles = [
  "index.html",
  "styles.css",
  "main.js",
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

if (dirname(outputRoot) !== websiteRoot || basename(outputRoot) !== "dist") {
  throw new Error("Build output must stay inside website/dist.");
}
const previousOutput = await lstat(outputRoot).catch(error => {
  if (error.code === "ENOENT") return null;
  throw error;
});
if (previousOutput?.isSymbolicLink()) throw new Error("Refusing to remove a linked build directory.");
await rm(outputRoot, { recursive: true, force: true });

for (const relativePath of publicFiles) {
  const destination = join(outputRoot, relativePath);
  await mkdir(dirname(destination), { recursive: true });
  await cp(join(websiteRoot, relativePath), destination);
}

console.log(`Built ${publicFiles.length} files in dist/`);
