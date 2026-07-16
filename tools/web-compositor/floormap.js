// 廻リ視 フロアマップ（ゾーン校正 Phase 2 / PC 側エディタ）。
//   上から見た 2D マップ（course space: 原点=フロア中心, +Z=北=画面上, 単位 m）。
//   1.8m 床 + L 字壁を固定描画し、カメラ区間を色帯で、切れ目（カット）を ● で表示する。
//   切れ目を正準ループに沿ってドラッグ → show.json の layout を保存。
//   Unity heartbeat の HMD 位置（hmdX/hmdZ）と現在ゾーン（zoneLabel）をライブドットで描く。
//   シミュレーション: ドットをドラッグ → カット定義から「どのカメラになるか」を同じ区間ロジックで表示。
//
//   契約（Unity 側エージェントと共有・変更不可）:
//   - 正準ループ = 矩形 x=±0.7, z=±0.7。s=0 = 南辺中央 (0,-0.7)、時計回り（南→東→北→西）、周長正規化 [0,1)。
//   - コーナー s=0.125(SE)/0.375(NE)/0.625(NW)/0.875(SW)。
//   - セグメント i = cuts[i].s〜cuts[i+1].s（wrap）が cuts[i].camAfter のカメラ（cameras[] の index）。

// layout 未定義の show.json を開いた時のデフォルト（契約の初期値）。
export const DEFAULT_LAYOUT = {
  rev: 1,
  floor: { w: 1.8, d: 1.8 },
  wall: { corner: [-0.5, 0.5], endX: [0.5, 0.5], endZ: [-0.5, -0.5] },
  cuts: [
    { s: 0.125, camAfter: 1 },
    { s: 0.375, camAfter: 2 },
    { s: 0.875, camAfter: 0 },
  ],
  overlapM: 0.08,
  hysteresisM: 0.12,
};

// カメラ index → 色（unity-vr.md の校正フットプリント配色に合わせる: 緑=0 / 青=1 / 橙=2）。
const CAM_COLORS = ['#5ad19a', '#5aa8ff', '#ffae5e', '#d98cff', '#ff6b8e', '#8ad4ff'];
const camColor = (i) => CAM_COLORS[((i % CAM_COLORS.length) + CAM_COLORS.length) % CAM_COLORS.length];

// ---- 正準ループ（course space の矩形 ±0.7）------------------------------------
// 各セグメントは a→b の直線。d0 = ループ始点からの累積距離（m）。
const LOOP = [
  { a: [0.0, -0.7], b: [0.7, -0.7], d0: 0.0 },  // 南辺の東半分
  { a: [0.7, -0.7], b: [0.7, 0.7], d0: 0.7 },  // 東辺
  { a: [0.7, 0.7], b: [-0.7, 0.7], d0: 2.1 },  // 北辺
  { a: [-0.7, 0.7], b: [-0.7, -0.7], d0: 3.5 },  // 西辺
  { a: [-0.7, -0.7], b: [0.0, -0.7], d0: 4.9 },  // 南辺の西半分
];
const PERIM = 5.6;
const segLen = (s) => Math.hypot(s.b[0] - s.a[0], s.b[1] - s.a[1]);

// s ∈ [0,1) → course space 座標 [x,z]（コーナーを正しく折り返す）。
function loopPoint(s) {
  const d = (((s % 1) + 1) % 1) * PERIM;
  for (let i = 0; i < LOOP.length; i++) {
    const seg = LOOP[i], L = segLen(seg);
    if (d <= seg.d0 + L + 1e-9 || i === LOOP.length - 1) {
      const t = Math.max(0, Math.min(1, (d - seg.d0) / L));
      return [seg.a[0] + (seg.b[0] - seg.a[0]) * t, seg.a[1] + (seg.b[1] - seg.a[1]) * t];
    }
  }
  return [0, -0.7];
}

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
        <div class="fm-legend"></div>
      </div>
      <div class="fm-controls">
        <div class="fm-row">
          <button class="fm-save accent">💾 保存</button>
          <span class="fm-dirty"></span>
        </div>
        <label class="fm-num">オーバーラップ (m)<input class="fm-overlap" type="number" min="0" max="0.5" step="0.01"></label>
        <label class="fm-num">ヒステリシス (m)<input class="fm-hyst" type="number" min="0" max="0.5" step="0.01"></label>
        <label class="fm-chk"><input class="fm-sim" type="checkbox"> シミュレーション（ドットをドラッグ）</label>
        <div class="fm-sim-out"></div>
        <div class="fm-live-out"></div>
        <div class="fm-cuts-title">切れ目 / カメラ区間</div>
        <div class="fm-cuts"></div>
        <button class="fm-add">＋ 切れ目を追加</button>
      </div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const canvas = q('.fm-canvas');
  const ctx = canvas.getContext('2d');
  const overlapI = q('.fm-overlap'), hystI = q('.fm-hyst');
  const simChk = q('.fm-sim'), simOut = q('.fm-sim-out'), liveOut = q('.fm-live-out');
  const dirtyEl = q('.fm-dirty'), cutsEl = q('.fm-cuts'), legendEl = q('.fm-legend');

  // 状態
  let layout = clone(DEFAULT_LAYOUT);
  let dirty = false;
  let cameras = [];
  let live = null;           // { x, z, label, activeIndex }
  let sim = null;            // { x, z } シミュレーション用ドット（course space）
  let drag = null;           // { mode:'cut'|'sim', cut? }

  function clone(o) { return JSON.parse(JSON.stringify(o)); }
  function markDirty() { dirty = true; renderDirty(); }
  function renderDirty() { dirtyEl.textContent = dirty ? '● 未保存' : ''; dirtyEl.className = 'fm-dirty' + (dirty ? ' on' : ''); }
  function sortCuts() { layout.cuts.sort((a, b) => a.s - b.s); }

  // ---- 描画 -----------------------------------------------------------------
  function render() {
    ctx.clearRect(0, 0, SIZE, SIZE);

    // 床（1.8×1.8）
    const fw = (layout.floor?.w ?? 1.8) / 2, fd = (layout.floor?.d ?? 1.8) / 2;
    const [fx0, fy0] = courseToPx(-fw, fd), [fx1, fy1] = courseToPx(fw, -fd);
    ctx.fillStyle = 'rgba(255,250,240,0.03)';
    ctx.fillRect(fx0, fy0, fx1 - fx0, fy1 - fy0);
    ctx.strokeStyle = 'rgba(255,250,240,0.28)';
    ctx.lineWidth = 1;
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

    // 正準ループ（薄いガイド）
    ctx.strokeStyle = 'rgba(255,250,240,0.14)';
    ctx.lineWidth = 1;
    strokeArc(0, 1, null);

    // カメラ区間（色帯）
    const cuts = layout.cuts || [];
    const sorted = [...cuts].sort((a, b) => a.s - b.s);
    const liveCam = live && Number.isInteger(live.activeIndex) ? live.activeIndex : null;
    for (let i = 0; i < sorted.length; i++) {
      const s0 = sorted[i].s, s1 = sorted[(i + 1) % sorted.length].s;
      const cam = sorted[i].camAfter;
      const emphasize = liveCam !== null && cam === liveCam;
      strokeArc(s0, s1, camColor(cam), emphasize ? 9 : 6, emphasize);
    }

    // 切れ目マーカー
    for (const c of cuts) {
      const [px, py] = courseToPx(...loopPoint(c.s));
      ctx.beginPath(); ctx.arc(px, py, 7, 0, Math.PI * 2);
      ctx.fillStyle = '#000'; ctx.fill();
      ctx.lineWidth = 2; ctx.strokeStyle = 'rgba(255,250,240,0.9)'; ctx.stroke();
    }

    // シミュレーションドット
    if (simChk.checked && sim) {
      const proj = projectToLoop(sim.x, sim.z);
      const cam = camAtS(proj.s, cuts);
      // 射影線
      const [dx, dy] = courseToPx(sim.x, sim.z);
      const [lx, ly] = courseToPx(...loopPoint(proj.s));
      ctx.setLineDash([4, 4]); ctx.strokeStyle = 'rgba(255,250,240,0.35)'; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(dx, dy); ctx.lineTo(lx, ly); ctx.stroke(); ctx.setLineDash([]);
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

  // s0→s1 をループに沿って描く（wrap 対応）。color=null はガイド（現在の strokeStyle を使う）。
  function strokeArc(s0, s1, color, width, glow) {
    let span = s1 - s0; if (span <= 0) span += 1;
    const steps = Math.max(2, Math.ceil(span / 0.004));
    ctx.beginPath();
    for (let k = 0; k <= steps; k++) {
      const [x, z] = loopPoint(s0 + span * (k / steps));
      const [px, py] = courseToPx(x, z);
      k === 0 ? ctx.moveTo(px, py) : ctx.lineTo(px, py);
    }
    if (color) { ctx.strokeStyle = color; ctx.lineWidth = width || 6; }
    ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    if (glow) { ctx.save(); ctx.shadowColor = color; ctx.shadowBlur = 10; ctx.stroke(); ctx.restore(); }
    else ctx.stroke();
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

  // ---- サイド UI --------------------------------------------------------------
  function renderLegend() {
    legendEl.innerHTML = '';
    cameras.forEach((cam, i) => {
      const chip = document.createElement('span'); chip.className = 'fm-chip';
      const sw = document.createElement('i'); sw.style.background = camColor(i);
      chip.append(sw, document.createTextNode(`カメラ ${cam.id}`));
      legendEl.appendChild(chip);
    });
  }

  function renderCuts() {
    cutsEl.innerHTML = '';
    const sorted = [...layout.cuts].sort((a, b) => a.s - b.s);
    for (const c of sorted) {
      const row = document.createElement('div'); row.className = 'fm-cut-row';
      const sSpan = document.createElement('span'); sSpan.className = 'fm-cut-s';
      sSpan.textContent = 's=' + c.s.toFixed(3);
      const arrow = document.createElement('span'); arrow.className = 'fm-cut-arrow'; arrow.textContent = '→';
      const sel = document.createElement('select'); sel.className = 'fm-cut-cam';
      const nOpts = Math.max(cameras.length, c.camAfter + 1);
      for (let i = 0; i < nOpts; i++) {
        const o = document.createElement('option');
        o.value = i; o.textContent = cameras[i] ? `カメラ ${cameras[i].id}` : `#${i}`;
        sel.appendChild(o);
      }
      sel.value = c.camAfter;
      sel.onchange = () => { c.camAfter = parseInt(sel.value, 10) || 0; markDirty(); render(); };
      const del = document.createElement('button'); del.className = 'fm-cut-del'; del.textContent = '×';
      del.title = '切れ目を削除';
      del.onclick = () => {
        if (layout.cuts.length <= 1) return; // 最低 1 個（全域のカメラを定義するため）
        layout.cuts = layout.cuts.filter((x) => x !== c);
        markDirty(); renderCuts(); render();
      };
      row.append(sSpan, arrow, sel, del);
      cutsEl.appendChild(row);
    }
  }

  q('.fm-add').onclick = () => {
    // 既存カットの最大ギャップの中点に新しい切れ目を置く。
    const sorted = [...layout.cuts].sort((a, b) => a.s - b.s);
    let bestGap = -1, bestS = 0.5;
    for (let i = 0; i < sorted.length; i++) {
      const s0 = sorted[i].s, s1 = sorted[(i + 1) % sorted.length].s;
      let gap = s1 - s0; if (gap <= 0) gap += 1;
      if (gap > bestGap) { bestGap = gap; bestS = (s0 + gap / 2) % 1; }
    }
    layout.cuts.push({ s: +bestS.toFixed(3), camAfter: 0 });
    sortCuts(); markDirty(); renderCuts(); render();
  };

  overlapI.onchange = () => { layout.overlapM = Math.max(0, parseFloat(overlapI.value) || 0); markDirty(); };
  hystI.onchange = () => { layout.hysteresisM = Math.max(0, parseFloat(hystI.value) || 0); markDirty(); };
  simChk.onchange = () => {
    if (simChk.checked && !sim) sim = { x: 0, z: -0.7 }; // 南辺中央に初期配置
    updateSimOut(); render();
  };
  function updateSimOut() {
    if (!simChk.checked || !sim) { simOut.textContent = ''; return; }
    const proj = projectToLoop(sim.x, sim.z);
    const cam = camAtS(proj.s, layout.cuts);
    const camLabel = cam !== null ? (cameras[cam] ? `カメラ ${cameras[cam].id}` : `#${cam}`) : '—';
    simOut.innerHTML = `SIM (${sim.x.toFixed(2)}, ${sim.z.toFixed(2)}) → s=${proj.s.toFixed(3)} → <b>${camLabel}</b>`;
  }
  function updateLiveOut() {
    if (!live || !Number.isFinite(live.x)) { liveOut.textContent = ''; liveOut.classList.remove('on'); return; }
    const camLabel = Number.isInteger(live.activeIndex) && cameras[live.activeIndex]
      ? `カメラ ${cameras[live.activeIndex].id}` : '?';
    liveOut.innerHTML = `🚶 HMD (${live.x.toFixed(2)}, ${live.z.toFixed(2)})`
      + (live.label ? ` / ${live.label}` : '') + ` / 表示中: <b>${camLabel}</b>`;
    liveOut.classList.add('on');
  }

  q('.fm-save').onclick = async () => {
    layout.rev = (parseInt(layout.rev, 10) || 0) + 1;
    sortCuts();
    const res = await deps.saveLayout(clone(layout));
    if (res && res.ok !== false) { dirty = false; renderDirty(); }
    else { dirtyEl.textContent = '✕ 保存失敗'; }
  };

  // ---- マウス操作（切れ目 / シミュレーションドットのドラッグ）------------------
  function mouseCourse(e) {
    const r = canvas.getBoundingClientRect();
    const px = (e.clientX - r.left) * (canvas.width / r.width);
    const py = (e.clientY - r.top) * (canvas.height / r.height);
    return { px, py, x: (px - SIZE / 2) / scale, z: (SIZE / 2 - py) / scale };
  }
  canvas.addEventListener('mousedown', (e) => {
    const m = mouseCourse(e);
    // シミュレーションドット優先
    if (simChk.checked && sim) {
      const [sx, sy] = courseToPx(sim.x, sim.z);
      if (Math.hypot(m.px - sx, m.py - sy) <= 14) { drag = { mode: 'sim' }; return; }
    }
    // 最近傍の切れ目マーカー
    let nearest = null, nd = 16;
    for (const c of layout.cuts) {
      const [cx, cy] = courseToPx(...loopPoint(c.s));
      const d = Math.hypot(m.px - cx, m.py - cy);
      if (d < nd) { nd = d; nearest = c; }
    }
    if (nearest) { drag = { mode: 'cut', cut: nearest }; e.preventDefault(); }
  });
  window.addEventListener('mousemove', (e) => {
    if (!drag) return;
    const m = mouseCourse(e);
    if (drag.mode === 'cut') {
      drag.cut.s = +projectToLoop(m.x, m.z).s.toFixed(4);
      markDirty(); render(); renderCuts();
    } else if (drag.mode === 'sim') {
      sim = { x: +m.x.toFixed(3), z: +m.z.toFixed(3) };
      updateSimOut(); render();
    }
  });
  window.addEventListener('mouseup', () => { drag = null; });

  // ---- 外部 API ---------------------------------------------------------------
  function adoptLayout(src) {
    layout = src ? clone(src) : clone(DEFAULT_LAYOUT);
    if (!Array.isArray(layout.cuts)) layout.cuts = clone(DEFAULT_LAYOUT.cuts);
    if (!layout.floor) layout.floor = clone(DEFAULT_LAYOUT.floor);
    if (!layout.wall) layout.wall = clone(DEFAULT_LAYOUT.wall);
    if (document.activeElement !== overlapI) overlapI.value = layout.overlapM ?? 0.08;
    if (document.activeElement !== hystI) hystI.value = layout.hysteresisM ?? 0.12;
    renderCuts();
  }

  // show.json が更新されたら呼ぶ。ローカル未保存編集中（dirty）は上書きしない。
  function onState(state) {
    cameras = (state && state.cameras) || [];
    renderLegend();
    if (!dirty) adoptLayout(state && state.layout);
    else renderCuts(); // dirty 中でもカメラ名の表示は更新
    updateSimOut(); render();
  }

  // Unity heartbeat が更新されたら呼ぶ（alive=false や hmd 欠落時はドット非表示）。
  function onUnity(alive, u) {
    if (alive && u && Number.isFinite(u.hmdX) && Number.isFinite(u.hmdZ)) {
      live = { x: u.hmdX, z: u.hmdZ, label: u.zoneLabel || '', activeIndex: u.activeIndex };
    } else {
      live = null;
    }
    updateLiveOut(); render();
  }

  adoptLayout(DEFAULT_LAYOUT);
  renderDirty();
  render();
  return { onState, onUnity };
}
