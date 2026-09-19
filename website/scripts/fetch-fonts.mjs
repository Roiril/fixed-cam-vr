// 書体のサブセットを Google Fonts の配布版から取り直す（ビルド時にだけ使う。閲覧時は自前配信）。
//
//   node scripts/fetch-fonts.mjs            … 全書体
//   node scripts/fetch-fonts.mjs plex chakra … 名前で絞る
//
// 各書体は「必要な文字」だけを含む WOFF2 として取得する（`text=` パラメータ）。
// 日本語の 2 書体は index.html の可視テキストから文字を集めるので、文言を変えたら必ず走らせ直す。
// 取得後に assets/fonts-manifest.json へ収録文字を書き出し、scripts/check.mjs が本文との差を検査する。
import { readFile, writeFile } from "node:fs/promises";
import { argv } from "node:process";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { headingText, pageText, uniqueChars } from "./font-text.mjs";

const websiteRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const assetRoot = join(websiteRoot, "assets");
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";

// 欧文の 2 書体は印字できる ASCII 全部と、データ表記で使う記号を持たせる。
const ASCII = Array.from({ length: 95 }, (_, i) => String.fromCharCode(32 + i)).join("");
const LATIN_EXTRA = "×·—–→";

export const FONTS = [
  { key: "mincho", family: "Shippori Mincho", weights: [400], out: "shippori-mincho", ofl: "shipporimincho", text: "page" },
  { key: "yuji", family: "Yuji Boku", weights: [400], out: "yuji-boku", ofl: "yujiboku", text: "headings" },
  { key: "plex", family: "IBM Plex Mono", weights: [400, 500], out: "ibm-plex-mono", ofl: "ibmplexmono", text: ASCII + LATIN_EXTRA },
  { key: "chakra", family: "Chakra Petch", weights: [600], out: "chakra-petch", ofl: "chakrapetch", text: ASCII + LATIN_EXTRA }
];

function parseUnicodeRange(range) {
  const points = new Set();
  for (const token of range.split(",")) {
    const m = token.trim().match(/^U\+([0-9a-f]+)(?:-([0-9a-f]+))?$/i);
    if (!m) continue;
    const start = parseInt(m[1], 16);
    const end = m[2] ? parseInt(m[2], 16) : start;
    for (let cp = start; cp <= end; cp += 1) points.add(cp);
  }
  return points;
}

async function fetchFont(entry, html) {
  const source = entry.text === "page" ? pageText(html) : entry.text === "headings" ? headingText(html) : entry.text;
  const chars = uniqueChars(source);
  const familyParam = encodeURIComponent(entry.family).replace(/%20/g, "+");
  const weightParam = entry.weights.length === 1 && entry.weights[0] === 400 ? "" : `:wght@${entry.weights.join(";")}`;
  const cssUrl = `https://fonts.googleapis.com/css2?family=${familyParam}${weightParam}&text=${encodeURIComponent(chars.join(""))}&display=swap`;
  const css = await (await fetch(cssUrl, { headers: { "User-Agent": UA } })).text();
  const faces = [...css.matchAll(/font-weight:\s*(\d+);\s*(?:font-display:\s*\w+;\s*)?src:\s*url\(([^)]+)\)\s*format\('woff2'\);\s*unicode-range:\s*([^;]+);/g)];
  if (faces.length !== entry.weights.length) throw new Error(`${entry.family}: expected ${entry.weights.length} faces, got ${faces.length}\n${css.slice(0, 300)}`);

  const covered = new Set();
  for (const [, weight, url, range] of faces) {
    for (const cp of parseUnicodeRange(range)) covered.add(cp);
    const bytes = new Uint8Array(await (await fetch(url, { headers: { "User-Agent": UA } })).arrayBuffer());
    const name = entry.weights.length === 1 ? `${entry.out}.woff2` : `${entry.out}-${weight}.woff2`;
    await writeFile(join(assetRoot, name), bytes);
    console.log(`${name}: ${bytes.length} bytes (weight ${weight})`);
  }
  const missing = chars.filter((ch) => !covered.has(ch.codePointAt(0)));
  if (missing.length > 0) console.warn(`${entry.family}: not in font, will fall back: ${missing.join(" ")}`);

  const oflUrl = `https://raw.githubusercontent.com/google/fonts/main/ofl/${entry.ofl}/OFL.txt`;
  const ofl = await (await fetch(oflUrl)).text();
  if (!ofl.includes("SIL OPEN FONT LICENSE")) throw new Error(`${entry.family}: OFL text not found at ${oflUrl}`);
  await writeFile(join(assetRoot, `${entry.out}-OFL.txt`), ofl);

  return { chars: chars.filter((ch) => covered.has(ch.codePointAt(0))).join(""), missing: missing.join("") };
}

// 取得は直接実行したときだけ走らせる。check.mjs が文字集合の規則を読むだけのときは動かさない。
export async function fetchFonts(keys = []) {
  const only = new Set(keys);
  const html = await readFile(join(websiteRoot, "index.html"), "utf8");
  const manifestPath = join(assetRoot, "fonts-manifest.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8").catch(() => "{}"));

  for (const entry of FONTS) {
    if (only.size > 0 && !only.has(entry.key)) continue;
    const result = await fetchFont(entry, html);
    manifest[entry.key] = {
      family: entry.family,
      weights: entry.weights,
      source: entry.text === "page" || entry.text === "headings" ? entry.text : "fixed",
      fetched: new Date().toISOString().slice(0, 10),
      chars: result.chars,
      missing: result.missing
    };
    console.log(`${entry.family}: ${result.chars.length} glyphs`);
  }

  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  console.log(`wrote ${manifestPath}`);
}

if (argv[1] && import.meta.url === pathToFileURL(argv[1]).href) {
  await fetchFonts(argv.slice(2));
}
