// show.json layout.grid（タイルペイント）→ ゾーン矩形の展開（DOM 非依存）。
//   移植元（**C# が正**）: Assets/Scripts/Tracking/ZoneLayoutSolver.cs
//     CellRect / ParseGridCells / SolveGrid / ClampHysteresis
//
// フロアマップの `camAt`（生タイルの色を読むだけ）とは別物。実機のゾーン判定は
// 「タイルを貪欲に矩形へまとめ、隣接カメラ境界で overlapM 重ねた矩形」に対して行われるので、
// シミュレータが実機と同じ答えを出すにはこの展開を通す必要がある。
//
// ⚠ **cuts のみ（grid 不在）の layout は未対応**。Unity は grid 優先（ChooseSource）で、卓の保存は
//   常に grid を書くため実運用では grid が存在する。cuts しか無い show.json ではシミュレーションを
//   行わず、UI にその旨を出す（黙って別の答えを出さない）。

import { boxAabb } from './scenario-engine.js';

/** ZoneLayoutApplier の生成ゾーン既定（中心 y / 半高）。 */
export const ZONE_CENTER_Y = 1;
export const ZONE_HALF_HEIGHT = 2;
/** HMD の高さ（course space）。ゾーンの y 判定に使うだけ。 */
export const HEAD_Y = 1.6;

/** ZoneLayoutSolver.CellRect（NW 角アンカー: col0 = x 最小 / row0 = z 最大）。 */
export function cellRect(r, c, rows, cols, tileM) {
  const halfW = (cols * tileM) / 2;
  const halfD = (rows * tileM) / 2;
  return {
    xLo: -halfW + c * tileM,
    xHi: -halfW + (c + 1) * tileM,
    zHi: halfD - r * tileM,
    zLo: halfD - (r + 1) * tileM,
  };
}

/** ZoneLayoutSolver.ClampHysteresis（shrink ≤ overlap/2 を保証してデッドバンドを守る）。 */
export const clampHysteresis = (hysteresisM, overlapM) =>
  Math.min(Math.max(0, hysteresisM || 0), Math.max(0, overlapM || 0) * 0.5);

/**
 * ZoneLayoutSolver.ParseGridCells。cells（rows 本の文字列）を row-major の
 * int 配列（-1=未割当 / 0..8=カメラ index）へ**例外を投げずに**変換する。
 */
export function parseGridCells(cells, rows, cols) {
  const rN = Math.max(0, rows | 0);
  const cN = Math.max(0, cols | 0);
  const grid = new Array(rN * cN).fill(-1);
  if (rN === 0 || cN === 0 || !Array.isArray(cells)) return grid;
  const usableRows = Math.min(rN, cells.length);
  for (let r = 0; r < usableRows; r++) {
    const row = typeof cells[r] === 'string' ? cells[r] : '';
    const usableCols = Math.min(cN, row.length);
    for (let c = 0; c < usableCols; c++) {
      const ch = row[c];
      if (ch >= '0' && ch <= '8') grid[r * cN + c] = ch.charCodeAt(0) - 48;
    }
  }
  return grid;
}

/**
 * ZoneLayoutSolver.SolveGrid。タイルを決定的な貪欲分解で矩形へまとめ、
 * 全方向 overlapM/2 拡張する（隣接カメラ境界で計 overlapM 重なる）。
 *   1. 左上→右下の走査で未消費かつ同一カメラのタイルを見つける
 *   2. その行で右へ伸ばす → 3. 列幅を保ったまま下へ伸ばす → 4. 消費して 1 矩形確定
 * 走査順が決定的なので同一入力 → 同一出力・同一順序（＝ Pick の「先頭が勝つ」も一致する）。
 */
export function solveGridZones(g) {
  const result = [];
  const rows = g.rows | 0, cols = g.cols | 0;
  const cells = g.cells;
  if (!cells || rows <= 0 || cols <= 0 || !(g.tileM > 0)) return result;
  if (cells.length < rows * cols) return result;

  const consumed = new Array(rows * cols).fill(false);
  const overlapHalf = Math.max(0, g.overlapM || 0) * 0.5;
  const camCount = new Map();

  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      const idx = r * cols + c;
      if (consumed[idx]) continue;
      const cam = cells[idx];
      if (cam < 0) continue;

      let c1 = c;
      while (c1 + 1 < cols) {
        const j = r * cols + (c1 + 1);
        if (consumed[j] || cells[j] !== cam) break;
        c1++;
      }

      let r1 = r;
      let grow = true;
      while (grow && r1 + 1 < rows) {
        const rr = r1 + 1;
        for (let cc = c; cc <= c1; cc++) {
          const j = rr * cols + cc;
          if (consumed[j] || cells[j] !== cam) { grow = false; break; }
        }
        if (grow) r1 = rr;
      }

      for (let rr = r; rr <= r1; rr++) {
        for (let cc = c; cc <= c1; cc++) consumed[rr * cols + cc] = true;
      }

      const nw = cellRect(r, c, rows, cols, g.tileM);
      const se = cellRect(r1, c1, rows, cols, g.tileM);
      const n = camCount.get(cam) || 0;
      camCount.set(cam, n + 1);

      result.push({
        centerX: 0.5 * (nw.xLo + se.xHi),
        centerZ: 0.5 * (se.zLo + nw.zHi),
        halfX: 0.5 * (se.xHi - nw.xLo) + overlapHalf,
        halfZ: 0.5 * (nw.zHi - se.zLo) + overlapHalf,
        camera: cam,
        priority: 0,
        label: `cam${cam}#${n}`,
      });
    }
  }
  return result;
}

/**
 * show.json の layout からシミュレータ用のゾーン箱（scenario-engine の Box 形式）を作る。
 * → { boxes, hysteresisShrink, source, rects, warning }
 *   source: 'grid'（展開できた）/ 'cuts'（grid 不在 = 未対応）/ 'none'
 */
export function zonesFromLayout(layout) {
  const lay = layout || {};
  const g = lay.grid;
  const hasGrid = !!(g && g.tileM > 0 && g.cols > 0 && g.rows > 0 && Array.isArray(g.cells) && g.cells.length);
  const hasCuts = Array.isArray(lay.cuts) && lay.cuts.length > 0;

  if (!hasGrid) {
    return {
      boxes: [], rects: [], hysteresisShrink: 0,
      source: hasCuts ? 'cuts' : 'none',
      warning: hasCuts
        ? 'layout.grid が無く cuts だけです。シミュレータは grid（タイルペイント）専用のため実行できません（フロアマップで「cuts から自動生成」→ 💾 保存）。'
        : 'layout.grid がありません。フロアマップでタイルを塗って 💾 保存してください。',
    };
  }

  const rects = solveGridZones({
    tileM: g.tileM, cols: g.cols, rows: g.rows,
    cells: parseGridCells(g.cells, g.rows, g.cols),
    overlapM: lay.overlapM,
  });
  const boxes = rects.map((r) => boxAabb(
    r.centerX, ZONE_CENTER_Y, r.centerZ,
    r.halfX, ZONE_HALF_HEIGHT, r.halfZ, r.camera, r.priority));

  return {
    boxes, rects,
    hysteresisShrink: clampHysteresis(lay.hysteresisM, lay.overlapM),
    source: 'grid',
    warning: rects.length === 0 ? 'タイルが 1 つも塗られていません（担当カメラが決まらない）。' : '',
  };
}
