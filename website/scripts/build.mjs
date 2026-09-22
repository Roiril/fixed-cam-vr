import { cp, mkdir, rm, lstat } from "node:fs/promises";
import { dirname, join, resolve, basename } from "node:path";
import { fileURLToPath } from "node:url";

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const outputRoot = resolve(websiteRoot, "dist");
const publicFiles = [
  "index.html",
  "styles.css",
  "main.js",
  "experience.css",
  "experience-config.js",
  "experience.js",
  "entrance.css",
  "entrance-geometry.js",
  "entrance-glass.js",
  "entrance.js",
  "robots.txt",
  "sitemap.xml",
  "site.webmanifest",
  "google5081a8a413a7871f.html",
  "assets/favicon.png",
  "assets/hero-desktop.webp",
  "assets/hero-small.webp",
  "assets/hero-mobile.webp",
  "assets/title.png",
  "assets/camera-01.webp",
  "assets/camera-02.webp",
  "assets/camera-03.webp",
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
