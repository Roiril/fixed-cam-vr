// index.html から「書体が要る文字」を取り出す。
// scripts/fetch-fonts.mjs が取得する文字集合を決め、scripts/check.mjs が収録漏れを検査する。
// 両方が同じ規則で数えるように、ここ 1 か所だけに置く。

function decodeEntities(text) {
  return text
    .replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&nbsp;/g, " ");
}

function stripTags(html) {
  return decodeEntities(html.replace(/<[^>]+>/g, ""));
}

// 画面に出る文字。head / script / style / コメントは除く。alt や aria-label は書体を使わないので除く。
export function pageText(html) {
  const body = html
    .replace(/<head[\s\S]*?<\/head>/i, "")
    .replace(/<script[\s\S]*?<\/script>/gi, "")
    .replace(/<style[\s\S]*?<\/style>/gi, "")
    .replace(/<!--[\s\S]*?-->/g, "");
  return stripTags(body);
}

// 欧文書体（IBM Plex Mono / Chakra Petch）で組む要素。styles.css の font-family 指定と対にして保つ。
const LATIN_CLASSES = [
  "plate-heading-latin",
  "plate-heading-meta",
  "tag-strip",
  "tag-title",
  "tag-code",
  "tag-edge",
  "plate-spine-latin",
  "plate-url"
];

// 欧文書体で描かれる文字。ここに全角の記号を 1 字でも入れると実機で代替書体に落ちるので、check が数える。
export function latinText(html) {
  const parts = [];
  for (const className of LATIN_CLASSES) {
    const pattern = new RegExp(`<(\\w+)\\b[^>]*class="[^"]*\\b${className}\\b[^"]*"[^>]*>([\\s\\S]*?)</\\1>`, "gi");
    for (const match of html.matchAll(pattern)) parts.push(stripTags(match[2]));
  }
  return parts.join("");
}

// 筆文字（Yuji Boku）を使う要素：見出し h2 と .site-mark と銘板の背。
// h2 の中の .plate-heading-latin は欧文書体で組むので、ここでは数えない。
export function headingText(html) {
  const parts = [];
  for (const match of html.matchAll(/<h2\b[^>]*>([\s\S]*?)<\/h2>/gi)) {
    parts.push(stripTags(match[1].replace(/<span\b[^>]*class="plate-heading-latin"[^>]*>[\s\S]*?<\/span>/gi, "")));
  }
  for (const match of html.matchAll(/<a\b[^>]*class="site-mark"[^>]*>([\s\S]*?)<\/a>/gi)) parts.push(stripTags(match[1]));
  for (const match of html.matchAll(/<span\b[^>]*class="plate-spine-title"[^>]*>([\s\S]*?)<\/span>/gi)) parts.push(stripTags(match[1]));
  return parts.join("");
}

export function uniqueChars(text) {
  return [...new Set([...text])].filter((ch) => !/\s/.test(ch)).sort();
}
