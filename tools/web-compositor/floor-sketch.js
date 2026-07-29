// 部屋を真上から見た**略図**を描く共有モジュール（DOM 非依存の計算 + canvas 描画）。
//
//   フロアマップ（floormap.js）は編集機能を持つ大きな面で、他の画面へ持ち出せない。
//   ここは「レイアウトを小さく絵にして、その上の 1 点を指させる」だけを担う軽い部品で、
//   いまは較正パネル（calib-ui.js）のミニマップが使う。
//
//   座標系は course 空間（x = 東が +、z = 北が +）。画面は「北が上」で描く。

import { zonesFromLayout, linesFromLayout } from './zone-layout.js';
import { roomFromLayout, isWallUsable, isBoxUsable, boxFootprint } from './room-model.js';
import { camColor } from './common.js';

export const DEFAULT_FLOOR = { w: 1.8, d: 1.8 };

/** レイアウトから床の大きさを取る（未著作なら 1.8m 四方）。 */
export function floorDims(layout) {
  const f = (layout && layout.floor) || DEFAULT_FLOOR;
  return { w: f.w > 0 ? f.w : DEFAULT_FLOOR.w, d: f.d > 0 ? f.d : DEFAULT_FLOOR.d };
}

/**
 * canvas の実寸に合わせた変換を作る。返り値の toPx / toCourse は互いの逆。
 *   padPx … 外周の余白（点を端に置いても切れないように）
 */
export function sketchView(layout, canvas, padPx = 10) {
  const { w, d } = floorDims(layout);
  const cw = canvas.width;
  const ch = canvas.height;
  const scale = Math.min((cw - padPx * 2) / w, (ch - padPx * 2) / d);
  const cx = cw / 2;
  const cy = ch / 2;
  return {
    scale, cx, cy, w, d,
    // 北（z+）が上、東（x+）が右。
    toPx: (x, z) => ({ px: cx + x * scale, py: cy - z * scale }),
    toCourse: (px, py) => ({ x: (px - cx) / scale, z: (cy - py) / scale }),
  };
}

/** タイル格子（既定 0.15m）へ丸める。grid が無ければ 0.05m 刻み。 */
export function snapToGrid(layout, x, z) {
  const g = layout && layout.grid;
  const tile = g && g.tileM > 0 ? g.tileM : 0.05;
  const { w, d } = floorDims(layout);
  // タイルの**角**（格子交点）へ寄せる。角は現場でメジャーを当てられる唯一の目印。
  const rx = Math.round((x + w / 2) / tile) * tile - w / 2;
  const rz = Math.round((z + d / 2) / tile) * tile - d / 2;
  return { x: round3(rx), z: round3(rz) };
}

/**
 * 部屋のプロキシ（壁の端点・箱の床接地角）の座標。
 * **較正で最も使える点**（映像で見つけやすく、床の上にあり、現場で実物を指させる）。
 */
export function roomCorners(layout) {
  // roomFromLayout は { room, hasRoom }（present-flag の AND 規約）。未著作なら角は無い。
  const { room, hasRoom } = roomFromLayout(layout || {});
  if (!hasRoom) return [];
  const out = [];
  (room.walls || []).forEach((w, i) => {
    if (!isWallUsable(w)) return;
    out.push({ x: round3(w.x1), z: round3(w.z1), label: `壁${i + 1}の端` });
    out.push({ x: round3(w.x2), z: round3(w.z2), label: `壁${i + 1}の端` });
  });
  (room.props || []).forEach((p, i) => {
    if (!isBoxUsable(p)) return;
    for (const c of boxFootprint(p)) out.push({ x: round3(c.x), z: round3(c.z), label: `箱${i + 1}の角` });
  });
  return out;
}

/** 部屋のプロキシの角のうち、指定点に最も近いもの。無ければ null。 */
export function nearestRoomCorner(layout, x, z, maxDistM = 0.2) {
  let best = null; let bestD = maxDistM;
  for (const c of roomCorners(layout)) {
    const dd = Math.hypot(c.x - x, c.z - z);
    if (dd <= bestD) { best = c; bestD = dd; }
  }
  return best ? { x: best.x, z: best.z } : null;
}

const round3 = (v) => Math.round(v * 1000) / 1000;

/** 点列のうち、画面上で (px, py) に近いものの index（無ければ -1）。 */
export function hitPoint(points, view, px, py, radiusPx = 12) {
  let best = -1; let bestD = radiusPx;
  (points || []).forEach((p, i) => {
    const q = view.toPx(p.x, p.z);
    const dd = Math.hypot(q.px - px, q.py - py);
    if (dd <= bestD) { best = i; bestD = dd; }
  });
  return best;
}

/**
 * 略図を描く。opts:
 *   points   … [{x, z, label, done}]（done = 映像側のクリックまで済んだ点）
 *   activeIndex … 強調する点
 *   cameras  … [{index, id, pose}]（pose があるものだけ位置と向きを描く）
 *   showZones / showRoom / showLines … 既定 true
 */
export function drawFloorSketch(ctx, layout, view, opts = {}) {
  const { w, d } = view;
  const cv = ctx.canvas;
  ctx.clearRect(0, 0, cv.width, cv.height);
  ctx.fillStyle = '#0d0f12';
  ctx.fillRect(0, 0, cv.width, cv.height);

  // ゾーンの塗り（どのカメラの担当かが薄く分かる程度に）
  if (opts.showZones !== false) {
    const { rects } = zonesFromLayout(layout || {});
    for (const r of rects || []) {
      const a = view.toPx(r.x - r.w / 2, r.z + r.d / 2);
      ctx.fillStyle = camColor(r.camera);
      ctx.globalAlpha = 0.16;
      ctx.fillRect(a.px, a.py, r.w * view.scale, r.d * view.scale);
      ctx.globalAlpha = 1;
    }
  }

  // タイル格子（スナップ先が見える）
  const tile = layout && layout.grid && layout.grid.tileM > 0 ? layout.grid.tileM : 0;
  if (tile > 0 && tile * view.scale >= 6) {
    ctx.strokeStyle = 'rgba(255,250,240,.07)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = -w / 2; x <= w / 2 + 1e-6; x += tile) {
      const a = view.toPx(x, d / 2); const b = view.toPx(x, -d / 2);
      ctx.moveTo(a.px, a.py); ctx.lineTo(b.px, b.py);
    }
    for (let z = -d / 2; z <= d / 2 + 1e-6; z += tile) {
      const a = view.toPx(-w / 2, z); const b = view.toPx(w / 2, z);
      ctx.moveTo(a.px, a.py); ctx.lineTo(b.px, b.py);
    }
    ctx.stroke();
  }

  // 床の外周
  const tl = view.toPx(-w / 2, d / 2);
  ctx.strokeStyle = 'rgba(255,250,240,.45)';
  ctx.lineWidth = 1.5;
  ctx.strokeRect(tl.px, tl.py, w * view.scale, d * view.scale);

  // 部屋のプロキシ（壁・箱）
  if (opts.showRoom !== false) {
    const { room } = roomFromLayout(layout || {});
    ctx.strokeStyle = 'rgba(150,200,255,.75)';
    ctx.lineWidth = 3;
    ctx.beginPath();
    for (const s of room.walls || []) {
      if (!isWallUsable(s)) continue;
      const a = view.toPx(s.x1, s.z1); const b = view.toPx(s.x2, s.z2);
      ctx.moveTo(a.px, a.py); ctx.lineTo(b.px, b.py);
    }
    ctx.stroke();
    ctx.strokeStyle = 'rgba(150,200,255,.5)';
    ctx.lineWidth = 1.5;
    for (const p of room.props || []) {
      if (!isBoxUsable(p)) continue;
      const corners = boxFootprint(p).map((c) => view.toPx(c.x, c.z));
      ctx.beginPath();
      corners.forEach((c, i) => (i ? ctx.lineTo(c.px, c.py) : ctx.moveTo(c.px, c.py)));
      ctx.closePath();
      ctx.stroke();
    }
  }

  // 通過ライン（担当カメラの色）
  if (opts.showLines !== false) {
    for (const l of linesFromLayout(layout || {})) {
      const a = view.toPx(l.x1, l.z1); const b = view.toPx(l.x2, l.z2);
      ctx.strokeStyle = l.camera >= 0 ? camColor(l.camera) : 'rgba(255,255,255,.4)';
      ctx.lineWidth = 2;
      ctx.setLineDash([5, 4]);
      ctx.beginPath(); ctx.moveTo(a.px, a.py); ctx.lineTo(b.px, b.py); ctx.stroke();
      ctx.setLineDash([]);
    }
  }

  // カメラ（位置と向き）
  for (const c of opts.cameras || []) {
    if (!c.pose) continue;
    const p = view.toPx(c.pose.x, c.pose.z);
    const yaw = ((c.pose.yawDeg || 0) * Math.PI) / 180;
    ctx.fillStyle = camColor(c.index);
    ctx.beginPath(); ctx.arc(p.px, p.py, 5, 0, Math.PI * 2); ctx.fill();
    // 向き（course の yaw 0 = 北 = 上、+ で東回り）
    const len = 22;
    ctx.strokeStyle = camColor(c.index);
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(p.px, p.py);
    ctx.lineTo(p.px + Math.sin(yaw) * len, p.py - Math.cos(yaw) * len);
    ctx.stroke();
    ctx.fillStyle = 'rgba(255,250,240,.8)';
    ctx.font = '10px system-ui, sans-serif';
    ctx.fillText(c.id || `#${c.index}`, p.px + 7, p.py - 6);
  }

  // 候補点（まだ打っていない点）。小さく出す — 主役は「打った点」なので数字は振らない。
  const activeKey = opts.activeKey;
  for (const m of opts.marks || []) {
    const q = view.toPx(m.x, m.z);
    const on = activeKey && m.key === activeKey;
    ctx.beginPath();
    ctx.arc(q.px, q.py, on ? 6.5 : 3, 0, Math.PI * 2);
    ctx.strokeStyle = on ? '#ffd479' : (m.kind === 'mark' ? 'rgba(126,231,135,.8)' : 'rgba(255,250,240,.4)');
    ctx.lineWidth = on ? 2.5 : 1.5;
    ctx.stroke();
    if (on) {
      ctx.fillStyle = 'rgba(255,212,121,.25)';
      ctx.fill();
    }
  }

  // 基準点
  (opts.points || []).forEach((pt, i) => {
    const q = view.toPx(pt.x, pt.z);
    const active = i === opts.activeIndex;
    const r = active ? 7 : 5;
    ctx.beginPath(); ctx.arc(q.px, q.py, r, 0, Math.PI * 2);
    ctx.fillStyle = pt.done ? '#7ee787' : (active ? '#ffd479' : 'rgba(255,250,240,.55)');
    ctx.fill();
    if (active) { ctx.strokeStyle = '#ffd479'; ctx.lineWidth = 2; ctx.stroke(); }
    ctx.fillStyle = '#0d0f12';
    ctx.font = 'bold 9px system-ui, sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText(String(i + 1), q.px, q.py + 0.5);
    ctx.textAlign = 'start'; ctx.textBaseline = 'alphabetic';
    if (pt.label) {
      ctx.fillStyle = 'rgba(255,250,240,.75)';
      ctx.font = '10px system-ui, sans-serif';
      ctx.fillText(pt.label, q.px + 9, q.py + 4);
    }
  });
}
