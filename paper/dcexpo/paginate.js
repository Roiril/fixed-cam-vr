/* 組版：本文（#stream）を行単位でページに割り付け、図（#figs）を各ページの上端・下端に固定する。
 *
 * - 本文は 1 段。ページごとの「本文の窓」に、同じ本文の複製を上へずらして映し、窓の高さで切る。
 * - 窓の高さ ＝ ページの内寸 − 見出し部（1 頁目）− 上の図 − 下の図。切れ目は行と行の間だけ。
 * - 切れ目の規則: 見出しでページを終えない／4 行以上の段落は前後に 2 行以上残す／3 行以下の段落は割らない。
 * - 図は data-page（1 始まり）と data-pos（top | bottom）で置き場所を指定する。
 * - 結果（図ごとの割り当て頁と、その図を最初に引用している段落の頁など）は
 *   <script type="application/json" id="report"> に書く。tools/build.py が読んで検査する。
 */
(async function () {
  const root = document.documentElement;
  const css = (n) => parseFloat(getComputedStyle(root).getPropertyValue(n));
  const mm = (() => {
    const d = document.createElement("div");
    d.style.cssText = "position:absolute;height:100mm;width:1px;visibility:hidden";
    document.body.appendChild(d);
    const v = d.offsetHeight / 100;
    d.remove();
    return v;
  })();
  const PAGE_H = css("--page-h") || 296.6;           // mm（297 ちょうどだと余白の丸めで次頁が生える）
  const M_TOP = css("--m-top"), M_BOTTOM = css("--m-bottom"), M_SIDE = css("--m-side");
  const GAP = css("--band-gap") || 3;                // 本文と図の間の最小の空き mm
  const contentH = (PAGE_H - M_TOP - M_BOTTOM) * mm;

  const stream = document.getElementById("stream");
  const figsRoot = document.getElementById("figs");
  const pagesRoot = document.getElementById("pages");
  const head = document.getElementById("head");
  const figs = [...figsRoot.children];

  // 画像の読み込みを待つ（高さが決まらないと窓の高さを決められない）
  await Promise.all([...document.images].map((im) => (im.decode ? im.decode().catch(() => {}) : Promise.resolve())));
  if (document.fonts && document.fonts.ready) await document.fonts.ready;

  // ---- 本文を測る（ページ幅と同じ幅の隠し領域） ----
  const meas = document.createElement("div");
  meas.id = "measure";
  const mStream = stream.cloneNode(true);
  mStream.removeAttribute("id");
  meas.appendChild(mStream);
  document.body.appendChild(meas);
  const base = mStream.getBoundingClientRect().top;
  const total = mStream.getBoundingClientRect().height;

  const blocks = [...mStream.children];
  const cands = [];                                   // {y, ok}  y = 行と行の間の切れ目（測定領域の上端からの px）
  const blockTop = [];
  blocks.forEach((b, bi) => {
    const isH = /^H\d$/.test(b.tagName);
    const br = b.getBoundingClientRect();
    blockTop.push(br.top - base);
    let lines = [];
    if (isH) {
      lines = [{ top: br.top - base, bottom: br.bottom - base }];
    } else {
      const range = document.createRange();
      range.selectNodeContents(b);
      const rs = [...range.getClientRects()].filter((r) => r.height > 0).sort((a, b2) => a.top - b2.top);
      for (const r of rs) {
        const c = (r.top + r.bottom) / 2 - base;
        const cur = lines[lines.length - 1];
        if (cur && c <= cur.bottom) {
          cur.top = Math.min(cur.top, r.top - base);
          cur.bottom = Math.max(cur.bottom, r.bottom - base);
        } else lines.push({ top: r.top - base, bottom: r.bottom - base });
      }
    }
    b._lines = lines;
    b._isH = isH;
  });
  blocks.forEach((b, bi) => {
    const n = b._lines.length;
    b._lines.forEach((ln, li) => {
      const nextTop = li + 1 < n ? b._lines[li + 1].top
        : (bi + 1 < blocks.length ? blocks[bi + 1]._lines[0].top : total);
      const y = li + 1 < n || bi + 1 < blocks.length ? (ln.bottom + nextTop) / 2 : total;
      let ok = !b._isH;                               // 見出しで終えない
      if (ok && n < 4) ok = li === n - 1;             // 短い段落は割らない
      if (ok && n >= 4 && li !== n - 1) ok = li + 1 >= 2 && n - (li + 1) >= 2;
      cands.push({ y, ok, bi, li });
    });
  });

  // ---- ページを組む ----
  let start = 0;
  let pageNo = 0;
  const wins = [];
  const placed = new Set();
  let guard = 0;
  while (guard++ < 30) {
    pageNo++;
    const page = document.createElement("section");
    page.className = "page";
    const topBand = document.createElement("div");
    topBand.className = "band top";
    const win = document.createElement("div");
    win.className = "win";
    const botBand = document.createElement("div");
    botBand.className = "band bottom";
    if (pageNo === 1) page.appendChild(head);
    page.append(topBand, win, botBand);
    pagesRoot.appendChild(page);
    const rows = {};
    for (const f of figs) {
      if (+f.dataset.page !== pageNo) continue;
      const band = f.dataset.pos === "bottom" ? botBand : topBand;
      if (f.dataset.row) {                              // 同じ data-row の図は横に並べる（図式 2 枚など）
        const key = f.dataset.pos + f.dataset.row;
        if (!rows[key]) {
          rows[key] = document.createElement("div");
          rows[key].className = "row";
          band.appendChild(rows[key]);
        }
        rows[key].appendChild(f);
      } else band.appendChild(f);
      placed.add(f);
    }
    const used = (head && pageNo === 1 ? head.offsetHeight : 0) + topBand.offsetHeight + botBand.offsetHeight;
    const gap = (topBand.childElementCount || botBand.childElementCount ? GAP : 0) * mm;
    const avail = contentH - used - gap;
    const target = start + avail;
    let end;
    if (target >= total - 0.5) end = total;
    else {
      const ok = cands.filter((c) => c.ok && c.y > start + 20 && c.y <= target);
      end = ok.length ? ok[ok.length - 1].y : start;      // 合法な切れ目が無ければ本文は 0 行（行の途中では切らない）
    }
    win.style.height = Math.max(0, end - start) + "px";
    const inner = stream.cloneNode(true);
    inner.removeAttribute("id");
    inner.className = "inner";
    inner.style.top = -start + "px";
    win.appendChild(inner);
    wins.push({ page: pageNo, start, end, avail, used: end - start, innerH: inner.getBoundingClientRect().height, winH: win.getBoundingClientRect().height });
    if (end >= total - 0.5) break;
    start = end;
  }
  // 割り当て頁が本文の頁数を超えた図は、最後に図だけの頁として付ける（検査で NG にする）
  const leftovers = figs.filter((f) => !placed.has(f));
  if (leftovers.length) {
    const page = document.createElement("section");
    page.className = "page";
    const band = document.createElement("div");
    band.className = "band top";
    leftovers.forEach((f) => band.appendChild(f));
    page.appendChild(band);
    pagesRoot.appendChild(page);
  }
  stream.style.display = "none";
  meas.remove();

  // ---- 報告 ----
  const pageOfY = (y) => {
    const w = wins.find((w2) => y >= w2.start - 1 && y < w2.end + 1);
    return w ? w.page : wins.length;
  };
  const report = {
    unit_px_per_mm: mm,
    total_px: total,
    pages: wins.map((w) => ({
      page: w.page,
      slack_mm: (w.avail - w.used) / mm,
      text_mm: (w.end - w.start) / mm,
      start: w.start, end: w.end, innerH: w.innerH, winH: w.winH,           // 窓の余り。1 行分（約 6mm）を超えたら詰め直す
    })),
    figs: figs.map((f) => {
      const n = f.id.replace("fig", "");
      const re = new RegExp("図" + n + "(?!\\d)");
      const bi = blocks.findIndex((b) => re.test(b.textContent));
      return {
        id: f.id,
        page: +f.dataset.page,
        pos: f.dataset.pos,
        placed: placed.has(f),
        cited_page: bi >= 0 ? pageOfY(blockTop[bi] + 2) : null,
      };
    }),
  };
  const sc = document.createElement("script");
  sc.type = "application/json";
  sc.id = "report";
  sc.textContent = JSON.stringify(report);
  document.body.appendChild(sc);
  document.body.dataset.ready = "1";
})();
