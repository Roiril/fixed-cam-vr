// 廻リ視 フロアマップ（ゾーン校正 v2 / PC 側エディタ = タイルペイント）。
//   上から見た 2D マップ（course space: 原点=フロア中心, +Z=北=画面上, 単位 m）。
//   1.8m 床を 12×12 タイル（0.15m）に分割し、パレット（カメラ 0..N の色 + 消しゴム'.'）を
//   選んでクリック / ドラッグで塗る。色 = そのタイルを担当するカメラ。'.' = 未割当。
//   L 字壁は固定描画（壁下のタイルも塗れるが初期は '.'）。保存 → show.json layout.grid を更新。
//   Unity heartbeat の HMD 位置（hmdX/hmdZ）と現在ゾーンをライブドットで描く。
//   シミュレーション: ドットをドラッグ → タイル → 色（カメラ）を表示。
//
//   契約（Unity 側エージェントと共有・変更不可）:
//   - grid = { tileM:0.15, cols:12, rows:12, cells:["............", ...] }。
//   - cells は rows 本の文字列。rows[0] = 北端（z=+0.9 側）、col 0 = 西端（x=-0.9）。
//     cell(r,c) 中心: x = -w/2 + (c+0.5)·tileM, z = +d/2 - (r+0.5)·tileM。
//   - 文字 '0'..'8' = カメラ index（cameras[] の index）、'.' = 未割当。
//   - grid が無く cuts だけある show.json は、各タイル中心を正準ループへ射影し
//     区間ロジックで初期塗りを生成（歩行回廊のタイルのみ。壁内側・判定不能は '.'）。
//   - cuts は後方互換でデータとして保持（編集 UI は無し。grid が正）。

// カメラ index → 色（unity-vr.md の校正フットプリント配色に合わせる: 緑=0 / 青=1 / 橙=2）。
const CAM_COLORS = ['#5ad19a', '#5aa8ff', '#ffae5e', '#d98cff', '#ff6b8e', '#8ad4ff'];
const camColor = (i) => CAM_COLORS[((i % CAM_COLORS.length) + CAM_COLORS.length) % CAM_COLORS.length];

// グリッド既定寸法（契約）。
const GRID_TILE = 0.15, GRID_COLS = 12, GRID_ROWS = 12;
// 歩行回廊とみなすタイル中心 → 正準ループまでの距離しきい値（m）。壁 0.5〜床端 0.9 の帯を拾う。
const CORRIDOR_HALF = 0.20;

// 位置合わせ点（HMD タッチ基準点）。course space・順序=タッチ順・最小 2・最大 5。
const REG_MIN = 2, REG_MAX = 5, REG_HIT = 15; // REG_HIT = マーカー掴み判定半径(px)
// regPoints 未設定 show.json の既定表示（L 字壁の外角側 2 点）。クリックで実データ化する。
const DEFAULT_REG_POINTS = [
  { x: -0.5, z: 0.5, label: '' },
  { x: 0.5, z: 0.5, label: '' },
];

// ---- 正準ループ（course space の矩形 ±0.7）: cuts → grid 初期化にのみ使う -------
const LOOP = [
  { a: [0.0, -0.7], b: [0.7, -0.7], d0: 0.0 },  // 南辺の東半分
  { a: [0.7, -0.7], b: [0.7, 0.7], d0: 0.7 },  // 東辺
  { a: [0.7, 0.7], b: [-0.7, 0.7], d0: 2.1 },  // 北辺
  { a: [-0.7, 0.7], b: [-0.7, -0.7], d0: 3.5 },  // 西辺
  { a: [-0.7, -0.7], b: [0.0, -0.7], d0: 4.9 },  // 南辺の西半分
];
const PERIM = 5.6;

// course space の点 (x,z) を最近傍でループへ射影 → { s, dist }。
function projectToLoop(x, z) {
  let best = { s: 0, dist: Infinity };
  for (const seg of LOOP) {
    const ax = seg.a[0], az = seg.a[1];
    const dx = seg.b[0] - ax, dz = seg.b[1] - az;
    const L2 = dx * dx + dz * dz;
    let t = L2 > 0 ? ((x - ax) * dx + (z - az) * dz) / L2 : 0;
    t = Math.max(0, Math.min(1, t));
    const cx = ax + dx * t, cz = az + dz * t;
    const dist = Math.hypot(x - cx, z - cz);
    if (dist < best.dist) best = { s: ((seg.d0 + Math.hypot(dx, dz) * t) / PERIM) % 1, dist };
  }
  return best;
}

// s に対応するカメラ index（区間ロジック）。cuts が空なら null。
function camAtS(s, cuts) {
  if (!cuts || !cuts.length) return null;
  const sorted = [...cuts].sort((a, b) => a.s - b.s);
  let cam = sorted[sorted.length - 1].camAfter; // 先頭カット手前（wrap セグメント）
  for (const c of sorted) { if (s >= c.s) cam = c.camAfter; else break; }
  return cam;
}

// タイル (r,c) の中心 course 座標 [x,z]。
function tileCenter(r, c, floor, tileM) {
  return [-floor.w / 2 + (c + 0.5) * tileM, floor.d / 2 - (r + 0.5) * tileM];
}

// cuts から grid の cells（rows 本の文字列）を生成。歩行回廊のタイルのみ塗り、他は '.'。
function cellsFromCuts(cuts, floor, tileM, cols, rows) {
  const out = [];
  for (let r = 0; r < rows; r++) {
    let row = '';
    for (let c = 0; c < cols; c++) {
      const [x, z] = tileCenter(r, c, floor, tileM);
      const proj = projectToLoop(x, z);
      if (proj.dist <= CORRIDOR_HALF) {
        const cam = camAtS(proj.s, cuts);
        row += (cam !== null && cam >= 0 && cam <= 8) ? String(cam) : '.';
      } else row += '.';
    }
    out.push(row);
  }
  return out;
}

// 全 '.' の空グリッド。
function emptyCells(cols, rows) {
  const out = [];
  for (let r = 0; r < rows; r++) out.push('.'.repeat(cols));
  return out;
}

// layout 未定義の show.json を開いた時のデフォルト（契約の初期値）。
// grid は既定 cuts（s=0.125→cam1 / 0.375→cam2 / 0.875→cam0）から生成した回廊塗り。
export const DEFAULT_LAYOUT = {
  rev: 1,
  floor: { w: 1.8, d: 1.8 },
  wall: { corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] },
  course: { order: [0, 1, 2] },
  cuts: [
    { s: 0.125, camAfter: 1 },
    { s: 0.375, camAfter: 2 },
    { s: 0.875, camAfter: 0 },
  ],
  grid: {
    tileM: GRID_TILE, cols: GRID_COLS, rows: GRID_ROWS,
    cells: cellsFromCuts(
      [{ s: 0.125, camAfter: 1 }, { s: 0.375, camAfter: 2 }, { s: 0.875, camAfter: 0 }],
      { w: 1.8, d: 1.8 }, GRID_TILE, GRID_COLS, GRID_ROWS),
  },
  overlapM: 0.08,
  hysteresisM: 0.12,
};

// ---- コントローラ本体 --------------------------------------------------------
export function createFloorMap(container, deps) {
  // deps: { getCameras: () => [...], saveLayout: (layout) => Promise }
  const SIZE = 460, PAD = 46, EXTENT = 0.95; // course ±0.95m を描画（床 ±0.9 に余白）
  const scale = (SIZE - 2 * PAD) / (2 * EXTENT);
  const courseToPx = (x, z) => [SIZE / 2 + x * scale, SIZE / 2 - z * scale];

  container.innerHTML = `
    <div class="fm-wrap">
      <div class="fm-canvas-col">
        <canvas class="fm-canvas" width="${SIZE}" height="${SIZE}"></canvas>
        <div class="fm-palette"></div>
      </div>
      <div class="fm-controls">
        <div class="fm-row">
          <button class="fm-save accent">💾 保存</button>
          <span class="fm-dirty"></span>
        </div>
        <div class="fm-hint2">パレットの色を選んでタイルをクリック / ドラッグで塗る。色 = 担当カメラ。消しゴムで未割当（'.'）に戻す。</div>
        <div class="fm-row">
          <button class="fm-regen" title="保持している cuts から回廊を自動で塗り直す">cuts から自動生成</button>
          <button class="fm-clear" title="全タイルを未割当にする">全消去</button>
        </div>
        <div class="fm-course">
          <div class="fm-course-label">周回コース（スケジュール発火の順序）</div>
          <div class="fm-row">
            <label class="fm-course-lbl">スタート <select class="fm-course-start"></select></label>
            <button class="fm-course-dir" title="巡回の向きを反転"></button>
          </div>
          <div class="fm-course-order"></div>
          <button class="fm-course-auto" title="タイルの塗りからカメラ重心の角度順で巡回順を再提案">角度順に再提案</button>
        </div>
        <div class="fm-reg">
          <div class="fm-course-label">位置合わせ点（HMD タッチ順）</div>
          <label class="fm-chk"><input class="fm-regmode" type="checkbox"> 📍 位置合わせ点を編集</label>
          <div class="fm-reg-note"></div>
          <div class="fm-reg-list"></div>
          <button class="fm-reg-add"></button>
          <div class="fm-reg-coords"></div>
          <div class="fm-hint2">床の×印テープを置く位置に点を打つ。番号＝HMD でタッチする順。最小2・最大5点。編集 ON でキャンバスをクリック配置・ドラッグ移動・右クリック削除。未設定なら既定2点を使用。</div>
        </div>
        <label class="fm-num">オーバーラップ (m)<input class="fm-overlap" type="number" min="0" max="0.5" step="0.01"></label>
        <label class="fm-num">ヒステリシス (m)<input class="fm-hyst" type="number" min="0" max="0.5" step="0.01"></label>
        <label class="fm-chk"><input class="fm-sim" type="checkbox"> シミュレーション（ドットをドラッグ）</label>
        <div class="fm-sim-out"></div>
        <div class="fm-live-out"></div>
      </div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const canvas = q('.fm-canvas');
  const ctx = canvas.getContext('2d');
  const overlapI = q('.fm-overlap'), hystI = q('.fm-hyst');
  const simChk = q('.fm-sim'), simOut = q('.fm-sim-out'), liveOut = q('.fm-live-out');
  const dirtyEl = q('.fm-dirty'), paletteEl = q('.fm-palette');
  const courseStartSel = q('.fm-course-start'), courseDirBtn = q('.fm-course-dir'), courseOrderEl = q('.fm-course-order');
  const regModeChk = q('.fm-regmode'), regNoteEl = q('.fm-reg-note'), regListEl = q('.fm-reg-list');
  const regAddBtn = q('.fm-reg-add'), regCoordsEl = q('.fm-reg-coords');

  // 状態
  let layout = clone(DEFAULT_LAYOUT);
  let dirty = false;
  let cameras = [];
  let live = null;           // { x, z, label, activeIndex }
  let sim = null;            // { x, z } シミュレーション用ドット（course space）
  let paintChar = '0';       // 選択中のパレット（カメラ index の文字 or '.'）
  let drag = null;           // { mode:'paint'|'sim' }
  let courseStart = 0;       // 周回スタートのカメラ index（order[0]）
  let courseDir = 'ccw';     // 巡回の向き 'cw' | 'ccw'
  let regMode = false;       // 位置合わせ点の編集モード（ON でキャンバス操作が点編集になる）
  let regDragIndex = -1;     // ドラッグ中の regPoint index

  function clone(o) { return JSON.parse(JSON.stringify(o)); }
  function markDirty() { dirty = true; renderDirty(); }
  function renderDirty() { dirtyEl.textContent = dirty ? '● 未保存' : ''; dirtyEl.className = 'fm-dirty' + (dirty ? ' on' : ''); }

  // ---- グリッドアクセサ -------------------------------------------------------
  function grid() { return layout.grid; }
  function floorDims() { return layout.floor || DEFAULT_LAYOUT.floor; }
  function cellAt(r, c) {
    const g = grid(); if (!g) return '.';
    const row = g.cells[r]; return row && c >= 0 && c < row.length ? (row[c] || '.') : '.';
  }
  function setCell(r, c, ch) {
    const g = grid(); if (!g) return false;
    if (r < 0 || r >= g.rows || c < 0 || c >= g.cols) return false;
    const row = g.cells[r];
    if (!row || row[c] === ch) return false;
    g.cells[r] = row.substring(0, c) + ch + row.substring(c + 1);
    return true;
  }
  // course (x,z) → タイル {r,c}（範囲外なら null）。
  function tileAt(x, z) {
    const g = grid(); if (!g) return null;
    const f = floorDims();
    const c = Math.floor((x + f.w / 2) / g.tileM);
    const r = Math.floor((f.d / 2 - z) / g.tileM);
    if (r < 0 || r >= g.rows || c < 0 || c >= g.cols) return null;
    return { r, c };
  }
  // course (x,z) → 担当カメラ index（未割当・範囲外なら null）。
  function camAt(x, z) {
    const t = tileAt(x, z); if (!t) return null;
    const ch = cellAt(t.r, t.c);
    return /[0-8]/.test(ch) ? parseInt(ch, 10) : null;
  }

  // ---- 位置合わせ点アクセサ ----------------------------------------------------
  // materialize 済みの regPoints 配列（無ければ null = 未設定＝既定フォールバック）。
  function regArr() { return Array.isArray(layout.regPoints) ? layout.regPoints : null; }
  function isRegUnset() { const a = regArr(); return !a || a.length === 0; }
  // 描画・判定に使う点列（未設定なら既定 2 点のゴースト）。
  function displayRegPts() { const a = regArr(); return (a && a.length) ? a : DEFAULT_REG_POINTS; }
  // 未設定の既定ゴーストを実データへ昇格（クリック / ＋ボタンで発火）。
  function materializeReg() {
    if (isRegUnset()) { layout.regPoints = clone(DEFAULT_REG_POINTS); markDirty(); }
  }
  const regBadge = (i) => (i < 20 ? String.fromCodePoint(0x2460 + i) : String(i + 1)); // ①②…
  // px 座標に最も近い表示点の index（REG_HIT 内、無ければ -1）。
  function regHitIndex(px, py) {
    const pts = displayRegPts();
    let best = -1, bd = REG_HIT;
    pts.forEach((p, i) => {
      const [x, y] = courseToPx(p.x, p.z);
      const d = Math.hypot(px - x, py - y);
      if (d <= bd) { bd = d; best = i; }
    });
    return best;
  }

  // ---- 描画 -----------------------------------------------------------------
  function render() {
    ctx.clearRect(0, 0, SIZE, SIZE);
    const g = grid();
    const f = floorDims();
    const fw = f.w / 2, fd = f.d / 2;
    const liveCam = live && Number.isInteger(live.activeIndex) ? live.activeIndex : null;

    // タイル
    if (g) {
      for (let r = 0; r < g.rows; r++) {
        for (let c = 0; c < g.cols; c++) {
          const ch = cellAt(r, c);
          const [x0, z0] = tileCorner(g, f, r, c, 0, 0);
          const [x1, z1] = tileCorner(g, f, r, c, 1, 1);
          const [px0, py0] = courseToPx(x0, z0);
          const [px1, py1] = courseToPx(x1, z1);
          const isCam = /[0-8]/.test(ch);
          if (isCam) {
            const cam = parseInt(ch, 10);
            const emphasize = liveCam !== null && cam === liveCam;
            ctx.fillStyle = camColor(cam);
            ctx.globalAlpha = emphasize ? 0.92 : 0.62;
            ctx.fillRect(px0, py0, px1 - px0, py1 - py0);
            ctx.globalAlpha = 1;
          } else {
            ctx.fillStyle = 'rgba(255,250,240,0.02)';
            ctx.fillRect(px0, py0, px1 - px0, py1 - py0);
          }
        }
      }
      // グリッド線
      ctx.strokeStyle = 'rgba(255,250,240,0.08)';
      ctx.lineWidth = 1;
      for (let c = 0; c <= g.cols; c++) {
        const x = -f.w / 2 + c * g.tileM;
        const [px, ay] = courseToPx(x, fd);
        const [, by] = courseToPx(x, -fd);
        ctx.beginPath(); ctx.moveTo(px, ay); ctx.lineTo(px, by); ctx.stroke();
      }
      for (let r = 0; r <= g.rows; r++) {
        const z = f.d / 2 - r * g.tileM;
        const [ax, py] = courseToPx(-fw, z);
        const [bx] = courseToPx(fw, z);
        ctx.beginPath(); ctx.moveTo(ax, py); ctx.lineTo(bx, py); ctx.stroke();
      }
    }

    // 床外周
    const [fx0, fy0] = courseToPx(-fw, fd), [fx1, fy1] = courseToPx(fw, -fd);
    ctx.strokeStyle = 'rgba(255,250,240,0.30)';
    ctx.lineWidth = 1.5;
    ctx.strokeRect(fx0, fy0, fx1 - fx0, fy1 - fy0);

    // 方位ラベル
    ctx.fillStyle = 'rgba(255,250,240,0.4)';
    ctx.font = '11px system-ui, sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText('N', SIZE / 2, PAD - 16);
    ctx.fillText('S', SIZE / 2, SIZE - PAD + 16);
    ctx.fillText('W', PAD - 20, SIZE / 2);
    ctx.fillText('E', SIZE - PAD + 20, SIZE / 2);

    // L 字壁
    const w = layout.wall || DEFAULT_LAYOUT.wall;
    ctx.strokeStyle = 'rgba(255,222,173,0.85)';
    ctx.lineWidth = 6; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    ctx.beginPath();
    let p = courseToPx(w.endZ[0], w.endZ[1]); ctx.moveTo(p[0], p[1]);   // 西腕の先端
    p = courseToPx(w.corner[0], w.corner[1]); ctx.lineTo(p[0], p[1]);   // 外角
    p = courseToPx(w.endX[0], w.endX[1]); ctx.lineTo(p[0], p[1]);       // 北腕の先端
    ctx.stroke();

    // 位置合わせ点（番号つきマーカー + タッチ順の破線）
    drawRegPoints();

    // シミュレーションドット
    if (simChk.checked && sim) {
      const cam = camAt(sim.x, sim.z);
      const [dx, dy] = courseToPx(sim.x, sim.z);
      drawDot(dx, dy, cam !== null ? camColor(cam) : '#fff', true);
    }

    // ライブ（HMD）ドット
    if (live && Number.isFinite(live.x) && Number.isFinite(live.z)) {
      const [px, py] = courseToPx(live.x, live.z);
      drawDiamond(px, py, '#fffaf0');
      if (live.label) {
        ctx.fillStyle = 'rgba(255,250,240,0.9)'; ctx.font = '11px system-ui, sans-serif';
        ctx.textAlign = 'center'; ctx.textBaseline = 'bottom';
        ctx.fillText(live.label, px, py - 12);
      }
    }
  }

  // タイル (r,c) の角 course 座標。dc/dr ∈ {0,1}（0=west/north 端, 1=east/south 端）。
  function tileCorner(g, f, r, c, dc, dr) {
    return [-f.w / 2 + (c + dc) * g.tileM, f.d / 2 - (r + dr) * g.tileM];
  }

  function drawDot(px, py, color, ring) {
    ctx.beginPath(); ctx.arc(px, py, 6, 0, Math.PI * 2);
    ctx.fillStyle = color; ctx.fill();
    if (ring) { ctx.lineWidth = 2; ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.stroke(); }
  }
  function drawDiamond(px, py, color) {
    ctx.save(); ctx.translate(px, py); ctx.rotate(Math.PI / 4);
    ctx.fillStyle = color; ctx.fillRect(-6, -6, 12, 12);
    ctx.lineWidth = 2; ctx.strokeStyle = '#000'; ctx.strokeRect(-6, -6, 12, 12);
    ctx.restore();
  }

  // 位置合わせ点を番号つきで描く。未設定は薄色ゴースト。順序を破線で結ぶ。
  function drawRegPoints() {
    const pts = displayRegPts();
    if (!pts.length) return;
    const ghost = isRegUnset();
    // タッチ順の破線
    if (pts.length >= 2) {
      ctx.save();
      ctx.strokeStyle = ghost ? 'rgba(255,222,173,0.22)' : 'rgba(255,222,173,0.55)';
      ctx.lineWidth = 1.5; ctx.setLineDash([5, 4]);
      ctx.beginPath();
      pts.forEach((p, i) => { const [px, py] = courseToPx(p.x, p.z); i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); });
      ctx.stroke();
      ctx.restore();
    }
    pts.forEach((p, i) => {
      const [px, py] = courseToPx(p.x, p.z);
      ctx.save();
      ctx.globalAlpha = ghost ? 0.4 : 1;
      ctx.beginPath(); ctx.arc(px, py, 11, 0, Math.PI * 2);
      ctx.fillStyle = '#141820'; ctx.fill();
      ctx.lineWidth = 2; ctx.strokeStyle = '#ffdead'; ctx.stroke();
      ctx.fillStyle = '#ffdead'; ctx.font = 'bold 13px system-ui, sans-serif';
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText(String(i + 1), px, py + 0.5);
      if (p.label) {
        ctx.fillStyle = 'rgba(255,250,240,0.9)'; ctx.font = '10px system-ui, sans-serif';
        ctx.textBaseline = 'top'; ctx.fillText(p.label, px, py + 13);
      }
      ctx.restore();
    });
  }

  // 現在の regPoints 座標をサマリ 1 行で表示。
  function updateRegCoords() {
    const a = regArr();
    regCoordsEl.textContent = (a && a.length)
      ? a.map((p, i) => `${regBadge(i)}(${p.x.toFixed(2)}, ${p.z.toFixed(2)})`).join('  ')
      : '';
  }

  // ---- パレット --------------------------------------------------------------
  function renderPalette() {
    paletteEl.innerHTML = '';
    // 有効なパレット候補: 現在のカメラ + grid に既に現れるカメラ index（カメラ未取得でも塗れる）。
    const maxCam = Math.max(
      cameras.length - 1,
      ...((grid()?.cells || []).flatMap((row) => [...row].filter((ch) => /[0-8]/.test(ch)).map((ch) => +ch))),
      -1);
    const n = Math.max(cameras.length, maxCam + 1, 1);
    for (let i = 0; i < n; i++) {
      const btn = document.createElement('button');
      btn.className = 'fm-swatch' + (paintChar === String(i) ? ' on' : '');
      const sw = document.createElement('i'); sw.style.background = camColor(i);
      btn.append(sw, document.createTextNode(cameras[i] ? `カメラ ${cameras[i].id}` : `#${i}`));
      btn.onclick = () => { paintChar = String(i); renderPalette(); };
      paletteEl.appendChild(btn);
    }
    const eraser = document.createElement('button');
    eraser.className = 'fm-swatch fm-eraser' + (paintChar === '.' ? ' on' : '');
    const es = document.createElement('i'); es.className = 'fm-eraser-sw';
    eraser.append(es, document.createTextNode('消しゴム'));
    eraser.onclick = () => { paintChar = '.'; renderPalette(); };
    paletteEl.appendChild(eraser);
  }

  // ---- サイド UI（自動生成 / 全消去 / 数値 / シミュ）--------------------------
  function ensureGrid() {
    if (!grid()) layout.grid = { tileM: GRID_TILE, cols: GRID_COLS, rows: GRID_ROWS, cells: emptyCells(GRID_COLS, GRID_ROWS) };
  }

  q('.fm-regen').onclick = () => {
    const g = grid() || { tileM: GRID_TILE, cols: GRID_COLS, rows: GRID_ROWS };
    const cuts = Array.isArray(layout.cuts) && layout.cuts.length ? layout.cuts : DEFAULT_LAYOUT.cuts;
    layout.grid = {
      tileM: g.tileM || GRID_TILE, cols: g.cols || GRID_COLS, rows: g.rows || GRID_ROWS,
      cells: cellsFromCuts(cuts, floorDims(), g.tileM || GRID_TILE, g.cols || GRID_COLS, g.rows || GRID_ROWS),
    };
    markDirty(); render();
  };
  q('.fm-clear').onclick = () => {
    ensureGrid();
    const g = grid();
    g.cells = emptyCells(g.cols, g.rows);
    markDirty(); render();
  };

  // ---- 周回コース（course.order）------------------------------------------------
  // grid の塗りからカメラ毎のタイル重心を出し、床中心からの角度順で巡回順を提案。
  function camCentroids() {
    const g = grid(), f = floorDims();
    const acc = {}; // idx -> {x,z,n}
    if (g) for (let r = 0; r < g.rows; r++) for (let c = 0; c < g.cols; c++) {
      const ch = cellAt(r, c);
      if (!/[0-8]/.test(ch)) continue;
      const i = +ch;
      const [x, z] = tileCenter(r, c, f, g.tileM);
      (acc[i] || (acc[i] = { x: 0, z: 0, n: 0 }));
      acc[i].x += x; acc[i].z += z; acc[i].n++;
    }
    const out = {};
    for (const k of Object.keys(acc)) out[k] = [acc[k].x / acc[k].n, acc[k].z / acc[k].n];
    return out;
  }
  // grid に現れるカメラ index を床中心からの角度で昇順（CCW 基準）に並べる。
  function angularCycle() {
    const cen = camCentroids();
    const idx = Object.keys(cen).map(Number);
    idx.sort((a, b) => Math.atan2(cen[a][1], cen[a][0]) - Math.atan2(cen[b][1], cen[b][0]));
    return idx;
  }
  // courseStart / courseDir から order（index 配列。order[0]=start）を算出。
  function computeOrder() {
    let cyc = angularCycle();
    if (!cyc.length) return [];
    if (courseDir === 'cw') cyc = cyc.slice().reverse();
    let i = cyc.indexOf(courseStart);
    if (i < 0) i = 0;
    return cyc.slice(i).concat(cyc.slice(0, i));
  }
  function camLabelIdx(i) { return cameras[i] ? `カメラ ${cameras[i].id}` : `#${i}`; }
  // layout.course.order を現在の start/dir から設定（markDirty はしない — 呼び元が制御）。
  function setCourseOrder() {
    layout.course = { order: computeOrder() };
    renderCourse();
  }
  function renderCourse() {
    const cyc = angularCycle();
    // スタート候補（grid に現れるカメラのみ）
    courseStartSel.innerHTML = '';
    for (const i of cyc) {
      const o = document.createElement('option');
      o.value = String(i); o.textContent = camLabelIdx(i);
      if (i === courseStart) o.selected = true;
      courseStartSel.appendChild(o);
    }
    if (!cyc.includes(courseStart) && cyc.length) { courseStart = cyc[0]; courseStartSel.value = String(courseStart); }
    courseDirBtn.textContent = courseDir === 'cw' ? '↻ 時計回り (CW)' : '↺ 反時計回り (CCW)';
    const order = (layout.course && Array.isArray(layout.course.order)) ? layout.course.order : computeOrder();
    courseOrderEl.textContent = order.length ? '順序: ' + order.map(camLabelIdx).join(' → ') + ' →（1周）' : '（タイルを塗るとカメラが現れます）';
  }
  courseStartSel.onchange = () => { courseStart = parseInt(courseStartSel.value, 10) || 0; setCourseOrder(); markDirty(); };
  courseDirBtn.onclick = () => { courseDir = courseDir === 'cw' ? 'ccw' : 'cw'; setCourseOrder(); markDirty(); };
  q('.fm-course-auto').onclick = () => {
    const cyc = angularCycle();
    courseDir = 'ccw';
    courseStart = cyc.length ? cyc[0] : 0;
    setCourseOrder(); markDirty();
  };

  // ---- 位置合わせ点エディタ ----------------------------------------------------
  function swapReg(i, j) {
    const a = regArr();
    if (!a || j < 0 || j >= a.length) return;
    const t = a[i]; a[i] = a[j]; a[j] = t;
    markDirty(); renderRegList(); render();
  }
  // 点の行 UI を作る（ラベル入力 / 順序入替 / 削除）。ラベル入力はリストを再構築しない
  // （フォーカス保持のため markDirty のみ）。番号・座標は canvas とサマリ行で確認する。
  function renderRegList() {
    const a = regArr();
    const unset = isRegUnset();
    if (unset) {
      regNoteEl.textContent = '未設定（既定 2 点を使用中）— キャンバスをクリック / 下のボタンで実データ化';
      regNoteEl.className = 'fm-reg-note';
    } else if (a.length < REG_MIN) {
      regNoteEl.textContent = `⚠ ${a.length} 点（最小 ${REG_MIN}）— このまま保存すると既定にフォールバックします`;
      regNoteEl.className = 'fm-reg-note warn';
    } else {
      regNoteEl.textContent = `${a.length} 点（HMD はこの番号順にタッチ）`;
      regNoteEl.className = 'fm-reg-note';
    }
    regListEl.innerHTML = '';
    if (!unset) {
      a.forEach((p, i) => {
        const row = document.createElement('div'); row.className = 'fm-reg-item';
        const badge = document.createElement('span'); badge.className = 'fm-reg-badge'; badge.textContent = regBadge(i);
        const lab = document.createElement('input');
        lab.type = 'text'; lab.className = 'fm-reg-label'; lab.placeholder = 'ラベル（任意）'; lab.value = p.label || '';
        lab.oninput = () => { p.label = lab.value; markDirty(); updateRegCoords(); };
        const up = document.createElement('button'); up.className = 'fm-reg-btn'; up.textContent = '▲'; up.title = '前へ';
        up.disabled = i === 0; up.onclick = () => swapReg(i, i - 1);
        const dn = document.createElement('button'); dn.className = 'fm-reg-btn'; dn.textContent = '▼'; dn.title = '後へ';
        dn.disabled = i === a.length - 1; dn.onclick = () => swapReg(i, i + 1);
        const del = document.createElement('button'); del.className = 'fm-reg-btn fm-reg-del'; del.textContent = '🗑'; del.title = '削除';
        del.onclick = () => { a.splice(i, 1); markDirty(); renderRegList(); render(); };
        row.append(badge, lab, up, dn, del);
        regListEl.appendChild(row);
      });
    }
    regAddBtn.textContent = unset ? '✎ 既定を編集可能にする' : '＋ 点を追加';
    regAddBtn.disabled = !unset && a.length >= REG_MAX;
    updateRegCoords();
  }
  regModeChk.onchange = () => { regMode = regModeChk.checked; canvas.classList.toggle('fm-reg-edit', regMode); render(); };
  regAddBtn.onclick = () => {
    if (isRegUnset()) {
      layout.regPoints = clone(DEFAULT_REG_POINTS);
    } else {
      const a = regArr();
      if (a.length >= REG_MAX) return;
      a.push({ x: +(-0.3 + 0.2 * (a.length % 3)).toFixed(3), z: 0, label: '' });
    }
    if (!regMode) { regMode = true; regModeChk.checked = true; canvas.classList.add('fm-reg-edit'); }
    markDirty(); renderRegList(); render();
  };

  overlapI.onchange = () => { layout.overlapM = Math.max(0, parseFloat(overlapI.value) || 0); markDirty(); };
  hystI.onchange = () => { layout.hysteresisM = Math.max(0, parseFloat(hystI.value) || 0); markDirty(); };
  simChk.onchange = () => {
    if (simChk.checked && !sim) sim = { x: 0, z: -0.7 }; // 南辺中央に初期配置
    updateSimOut(); render();
  };
  function camLabel(cam) {
    return cam !== null ? (cameras[cam] ? `カメラ ${cameras[cam].id}` : `#${cam}`) : '未割当';
  }
  function updateSimOut() {
    if (!simChk.checked || !sim) { simOut.textContent = ''; return; }
    const cam = camAt(sim.x, sim.z);
    simOut.innerHTML = `SIM (${sim.x.toFixed(2)}, ${sim.z.toFixed(2)}) → <b>${camLabel(cam)}</b>`;
  }
  function updateLiveOut() {
    if (!live || !Number.isFinite(live.x)) { liveOut.textContent = ''; liveOut.classList.remove('on'); return; }
    const cl = Number.isInteger(live.activeIndex) && cameras[live.activeIndex]
      ? `カメラ ${cameras[live.activeIndex].id}` : '?';
    liveOut.innerHTML = `🚶 HMD (${live.x.toFixed(2)}, ${live.z.toFixed(2)})`
      + (live.label ? ` / ${live.label}` : '') + ` / 表示中: <b>${cl}</b>`;
    liveOut.classList.add('on');
  }

  q('.fm-save').onclick = async () => {
    ensureGrid();
    // regPoints: 2 点未満は書かない（Unity 既定 2 点へフォールバック）。5 点超は切り詰め。
    let regFellBack = false;
    const rp = regArr();
    if (rp && rp.length < REG_MIN) { delete layout.regPoints; regFellBack = true; }
    else if (rp && rp.length > REG_MAX) { layout.regPoints = rp.slice(0, REG_MAX); }
    layout.rev = (parseInt(layout.rev, 10) || 0) + 1;
    const res = await deps.saveLayout(clone(layout));
    if (res && res.ok !== false) {
      dirty = false; renderDirty(); renderRegList();
      if (regFellBack) regNoteEl.textContent = '⚠ 2 点未満のため regPoints を保存せず既定にフォールバックしました';
    } else { dirtyEl.textContent = '✕ 保存失敗'; }
  };

  // ---- マウス操作（タイル塗り / シミュレーションドット）------------------------
  function mouseCourse(e) {
    const r = canvas.getBoundingClientRect();
    const px = (e.clientX - r.left) * (canvas.width / r.width);
    const py = (e.clientY - r.top) * (canvas.height / r.height);
    return { px, py, x: (px - SIZE / 2) / scale, z: (SIZE / 2 - py) / scale };
  }
  function paintAt(m) {
    const t = tileAt(m.x, m.z);
    if (t && setCell(t.r, t.c, paintChar)) { markDirty(); render(); }
  }
  canvas.addEventListener('mousedown', (e) => {
    const m = mouseCourse(e);
    // 位置合わせ点編集モードが最優先（掴む / 追加）。
    if (regMode) {
      const hit = regHitIndex(m.px, m.py);
      materializeReg();                 // 未設定ゴーストは実データへ昇格（index は既定と一致）
      const a = regArr();
      if (hit >= 0) { regDragIndex = hit; drag = { mode: 'reg' }; }
      else if (a.length < REG_MAX) {
        a.push({ x: +m.x.toFixed(3), z: +m.z.toFixed(3), label: '' });
        regDragIndex = a.length - 1; drag = { mode: 'reg' }; markDirty(); renderRegList();
      }
      render(); e.preventDefault(); return;
    }
    // シミュレーションドット優先（ドラッグで動かす）
    if (simChk.checked && sim) {
      const [sx, sy] = courseToPx(sim.x, sim.z);
      if (Math.hypot(m.px - sx, m.py - sy) <= 14) { drag = { mode: 'sim' }; e.preventDefault(); return; }
    }
    ensureGrid();
    drag = { mode: 'paint' };
    paintAt(m);
    e.preventDefault();
  });
  // 右クリックで位置合わせ点を削除（編集モード時のみ）。
  canvas.addEventListener('contextmenu', (e) => {
    if (!regMode) return;
    const m = mouseCourse(e);
    const hit = regHitIndex(m.px, m.py);
    const a = regArr();
    if (hit >= 0 && a) { a.splice(hit, 1); markDirty(); renderRegList(); render(); }
    e.preventDefault();
  });
  window.addEventListener('mousemove', (e) => {
    if (!drag) return;
    const m = mouseCourse(e);
    if (drag.mode === 'paint') paintAt(m);
    else if (drag.mode === 'sim') { sim = { x: +m.x.toFixed(3), z: +m.z.toFixed(3) }; updateSimOut(); render(); }
    else if (drag.mode === 'reg') {
      const a = regArr();
      if (a && regDragIndex >= 0 && regDragIndex < a.length) {
        a[regDragIndex].x = +m.x.toFixed(3); a[regDragIndex].z = +m.z.toFixed(3);
        markDirty(); updateRegCoords(); render();
      }
    }
  });
  window.addEventListener('mouseup', () => { drag = null; regDragIndex = -1; });

  // ---- 外部 API ---------------------------------------------------------------
  function adoptLayout(src) {
    layout = src ? clone(src) : clone(DEFAULT_LAYOUT);
    if (!layout.floor) layout.floor = clone(DEFAULT_LAYOUT.floor);
    if (!layout.wall) layout.wall = clone(DEFAULT_LAYOUT.wall);
    if (!Array.isArray(layout.cuts)) layout.cuts = clone(DEFAULT_LAYOUT.cuts);
    // regPoints: 不正要素を除去し最大 5 点へ。空/不在は未設定（既定フォールバック）に保つ。
    if (Array.isArray(layout.regPoints)) {
      layout.regPoints = layout.regPoints
        .filter((p) => p && Number.isFinite(p.x) && Number.isFinite(p.z))
        .slice(0, REG_MAX)
        .map((p) => ({ x: p.x, z: p.z, label: typeof p.label === 'string' ? p.label : '' }));
      if (!layout.regPoints.length) delete layout.regPoints;
    } else if (layout.regPoints !== undefined) {
      delete layout.regPoints;
    }
    // grid が無ければ cuts から初期塗りを生成。dims 不整合や cells 欠落も同様に正規化。
    const g = layout.grid;
    const okGrid = g && Number.isFinite(g.tileM) && Number.isInteger(g.cols) && Number.isInteger(g.rows)
      && Array.isArray(g.cells) && g.cells.length === g.rows
      && g.cells.every((row) => typeof row === 'string' && row.length === g.cols);
    if (!okGrid) {
      layout.grid = {
        tileM: GRID_TILE, cols: GRID_COLS, rows: GRID_ROWS,
        cells: cellsFromCuts(layout.cuts, floorDims(), GRID_TILE, GRID_COLS, GRID_ROWS),
      };
    }
    if (document.activeElement !== overlapI) overlapI.value = layout.overlapM ?? 0.08;
    if (document.activeElement !== hystI) hystI.value = layout.hysteresisM ?? 0.12;
    // paintChar が現在のパレット範囲外なら 0 に寄せる
    if (paintChar !== '.' && cameras.length && +paintChar >= Math.max(cameras.length, GRID_COLS)) paintChar = '0';
    // 周回コース: order があれば start/dir を逆算、無ければ角度順から生成。
    if (layout.course && Array.isArray(layout.course.order) && layout.course.order.length) {
      const order = layout.course.order;
      courseStart = order[0];
      let ccw = angularCycle();
      const i = ccw.indexOf(courseStart);
      if (i > 0) ccw = ccw.slice(i).concat(ccw.slice(0, i));
      courseDir = JSON.stringify(ccw) === JSON.stringify(order) ? 'ccw' : 'cw';
    } else {
      courseStart = 0; courseDir = 'ccw';
      layout.course = { order: computeOrder() };
    }
  }

  // show.json が更新されたら呼ぶ。ローカル未保存編集中（dirty）は上書きしない。
  function onState(state) {
    cameras = (state && state.cameras) || [];
    if (!dirty) adoptLayout(state && state.layout);
    renderPalette();
    renderCourse();
    if (!dirty) renderRegList(); // 編集中はラベル入力のフォーカスを潰さないため再構築しない
    updateSimOut(); render();
  }

  // Unity heartbeat が更新されたら呼ぶ（alive=false や hmd 欠落時はドット非表示）。
  //   フィールド名は Unity 側 Heartbeat の headCourseX / headCourseZ / currentZone が正。
  //   旧名（hmdX / hmdZ / zoneLabel）も受けて古いクライアントと両立させる。
  function onUnity(alive, u) {
    const x = u && (Number.isFinite(u.headCourseX) ? u.headCourseX : u.hmdX);
    const z = u && (Number.isFinite(u.headCourseZ) ? u.headCourseZ : u.hmdZ);
    if (alive && Number.isFinite(x) && Number.isFinite(z)) {
      live = { x, z, label: (u.currentZone || u.zoneLabel || ''), activeIndex: u.activeIndex };
    } else {
      live = null;
    }
    updateLiveOut(); render();
  }

  adoptLayout(DEFAULT_LAYOUT);
  renderPalette();
  renderCourse();
  renderRegList();
  renderDirty();
  render();
  return { onState, onUnity, isDirty: () => dirty };
}
