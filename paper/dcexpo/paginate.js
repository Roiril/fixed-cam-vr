/* 組版：本文（#stream）を行単位で「段」に割り付け、図（#figs）を各ページの上端・下端に固定する。
 *
 * - 本文は --cols 段（既定 2。1 にすると 1 段組）。段ごとの「本文の窓」に、同じ本文の複製を上へずらして映し、窓の高さで切る。
 *   段の幅で本文を測るので、窓を左段 → 右段 → 次頁の左段…の順に読み継ぐ。
 * - 窓の高さ ＝ ページの内寸 − 見出し部（1 頁目）− 上の図 − 下の図。切れ目は行と行の間だけ。
 * - 切れ目の規則: 見出しで段を終えない／4 行以上の段落は前後に 2 行以上残す／3 行以下の段落は割らない。
 * - 最終頁は、残りの本文を段の数で均等に割る（左段だけが埋まって右段が空になるのを避ける）。
 * - 図は data-page（1 始まり）と data-pos（top | bottom）で置き場所を指定する。図は段をまたぐ全幅。
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
  const PAGE_W = css("--page-w") || 210;
  const PAGE_H = css("--page-h") || 296.6;           // mm（297 ちょうどだと余白の丸めで次頁が生える）
  const M_TOP = css("--m-top"), M_BOTTOM = css("--m-bottom"), M_SIDE = css("--m-side");
  const GAP = css("--band-gap") || 3;                // 本文と図の間の最小の空き mm
  const COLS = Math.max(1, Math.round(css("--cols") || 2));
  const COL_GAP = css("--col-gap") || 6;             // 段の間 mm
  const colW = (PAGE_W - 2 * M_SIDE - (COLS - 1) * COL_GAP) / COLS;
  root.style.setProperty("--col-w", colW + "mm");
  const contentH = (PAGE_H - M_TOP - M_BOTTOM) * mm;

  const stream = document.getElementById("stream");
  const figsRoot = document.getElementById("figs");
  const pagesRoot = document.getElementById("pages");
  const head = document.getElementById("head");
  const figs = [...figsRoot.children];

  // 画像の読み込みを待つ（高さが決まらないと窓の高さを決められない）
  await Promise.all([...document.images].map((im) => (im.decode ? im.decode().catch(() => {}) : Promise.resolve())));
  if (document.fonts && document.fonts.ready) await document.fonts.ready;

  // ---- 本文を測る（段と同じ幅の隠し領域） ----
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
  blocks.forEach((b) => {
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
  const wins = [];                                    // 段ごとの窓
  const placed = new Set();
  let guard = 0;
  let done = false;
  while (!done && guard++ < 30) {
    pageNo++;
    const page = document.createElement("section");
    page.className = "page";
    const topBand = document.createElement("div");
    topBand.className = "band top";
    const colsEl = document.createElement("div");
    colsEl.className = "cols";
    const botBand = document.createElement("div");
    botBand.className = "band bottom";
    if (pageNo === 1) page.appendChild(head);
    page.append(topBand, colsEl, botBand);
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
    const finalPage = total - start <= COLS * avail + 0.5;      // この頁で本文が尽きる → 段を均等に割る
    for (let c = 0; c < COLS; c++) {
      const win = document.createElement("div");
      win.className = "win";
      colsEl.appendChild(win);
      const left = COLS - c;
      let end;
      if (left === 1 && total - start <= avail + 0.5) end = total;
      else if (finalPage) {
        const tgt = start + (total - start) / left;
        const ok = cands.filter((k) => k.ok && k.y > start + 20 && k.y <= start + avail);
        end = ok.length ? ok.reduce((a, k) => (Math.abs(k.y - tgt) < Math.abs(a.y - tgt) ? k : a)).y : start;
      } else {
        const ok = cands.filter((k) => k.ok && k.y > start + 20 && k.y <= start + avail);
        end = ok.length ? ok[ok.length - 1].y : start;          // 合法な切れ目が無ければ本文は 0 行（行の途中では切らない）
      }
      win.style.height = Math.max(0, end - start) + "px";
      const inner = stream.cloneNode(true);
      inner.removeAttribute("id");
      inner.className = "inner";
      inner.style.top = -start + "px";
      win.appendChild(inner);
      wins.push({ page: pageNo, col: c, start, end, avail, used: end - start });
      start = end;
      if (end >= total - 0.5) { done = true; break; }
    }
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
    return w ? w.page : wins[wins.length - 1].page;
  };
  const byPage = {};
  wins.forEach((w) => {
    const p = (byPage[w.page] = byPage[w.page] || { page: w.page, slack_mm: 0, text_mm: 0, cols: 0 });
    p.slack_mm = Math.max(p.slack_mm, (w.avail - w.used) / mm);   // 段の余り。1 行分（約 6mm）を超えたら詰め直す
    p.text_mm += (w.end - w.start) / mm;
    p.cols += 1;
  });
  const report = {
    unit_px_per_mm: mm,
    cols: COLS,
    total_px: total,
    pages: Object.values(byPage),
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
