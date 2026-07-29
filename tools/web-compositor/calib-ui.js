// 実カメラの較正 UI — 静止フレームの上で床の既知点をクリックし、姿勢・画角・歪みを解いて
//   `cameras[].calib` に保存する。数学は calib.js（純関数・node テスト済み）にあり、
//   ここが持つのは**操作面と検証表示だけ**。解の正しさをここで判断しない。
//
// なぜこの形か（設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.2 /
// .claude/rules/streaming.md「較正の実装と、現場運用がそうでなければならない理由」）:
//
//   1. **静止フレームの上で打つ**。ライブ映像に直接打つと、打っている最中にフレームが差し替わって
//      点と絵の対応が崩れる（クリック誤差 1.5px で位置が数 cm 動く世界なので致命的）。
//   2. **打つ点は先に選ぶ**（course 座標が既知の候補から）。「クリックしてから座標を入れる」形にすると
//      対応の入れ違いが起きても気づけない。calib.js が「点の対応が入れ違っている可能性」としか
//      言えないのは、そこを機械では判定できないため。
//   3. **解いたら必ずワイヤーを重ねる**。rms(px) は「打った点にどれだけ合ったか」であって
//      「部屋に合っているか」ではない（4 点なら誤差 0 でも解が嘘のことがある）。
//      **実映像に部屋の線を重ねた絵だけが反証可能な一次証拠**。
//   4. **画角（焦点距離）は固定できるなら固定する**。f も推定すると人のクリック誤差 1.5px で
//      位置が 27cm ずれ、固定すれば 2cm に収まる（calib.test.mjs が実測で固定している）。
//      → 運用は「内部（画角）は一度だけ丁寧に / 外部（置き場所）は現場で毎回」。UI もその順に導く。
//
// 卓は CG 人形そのものを描かない（実描画の正は Unity）。ここで描くのは投影の**線**だけ。

import {
  calibrateFromFloorPoints, projectPoint, calibQualityLabel, MIN_POINTS_FOR_K1,
  hasCollinearTriple, isDegenerate, perPointResiduals,
} from './calib.js';
import { linesFromLayout, parseGridCells } from './zone-layout.js';
import { streamBase, camColor, escapeHtml } from './common.js';
import {
  sketchView, drawFloorSketch, snapToGrid, nearestRoomCorner, roomCorners, hitPoint,
} from './floor-sketch.js';

// ---- 純関数（DOM 非依存・calib-ui.test.mjs が固定する）------------------------

/**
 * `layout.regPoints` 未設定の show.json で使う既定 2 点（L 字壁の外角側）。
 * floormap.js の DEFAULT_REG_POINTS と**同じ値でなければならない** — 卓のフロアマップが
 * ゴーストで見せている点と、較正で打つ点が食い違うと現場のテープが二重定義になる。
 */
export const DEFAULT_REG_POINTS = [
  { x: -0.5, z: 0.5, label: '' },
  { x: 0.5, z: 0.5, label: '' },
];

/**
 * `layout.wall` が卓の既定値のままか（＝**実物の壁を測っていない**）。
 *
 * 既定は 1m × 1m の L で、現場の壁がこの寸法である保証はどこにも無い。
 * 既定のまま「壁の外角」を打つと、**打った点の座標が実物と違う**ので、いくら丁寧に
 * クリックしても解が合わない（2026-07-29 ユーザー報告「結構あってると思うのに合わない」）。
 * これは点の打ち方の問題ではないので、UI が先に言う。
 */
export function wallLooksDefault(layout) {
  const w = layout?.wall;
  if (!w || !Array.isArray(w.corner) || !Array.isArray(w.endX) || !Array.isArray(w.endZ)) return true;
  const same = (a, b) => Math.abs(a[0] - b[0]) < 1e-6 && Math.abs(a[1] - b[1]) < 1e-6;
  return same(w.corner, [-0.5, 0.5]) && same(w.endX, [0.5, 0.5]) && same(w.endZ, [-0.5, -0.5]);
}

/** 同一点判定のキー（mm 丸め）。course 座標の浮動小数差で重複が漏れるのを防ぐ。 */
export const pointKey = (x, z) => `${x.toFixed(3)},${z.toFixed(3)}`;

/**
 * 打てる候補点（course 座標が既知の床点）を並べる。
 *
 * 順序に意味がある: **位置合わせ点 → 部屋の角 → タイルの角**。
 *   - 位置合わせ点（`layout.regPoints`）が第一候補。現場の床に × 印テープが既に貼ってあり、
 *     HMD の位置合わせでも同じ点を使う。**較正用の点を別に作らない**（テープの二重定義を作らない）
 *   - 部屋の角（L 字壁の端点・床の四隅）は映像で見つけやすく、床面上なので較正に使える
 *   - タイルの角は最後。**塗られたタイルに接する交点だけ**にする（12×12 の全交点 169 個を
 *     そのまま並べると選択肢として機能しない）
 *
 * @param {object|null} layout show.json の layout
 * @param {{x:number,z:number}[]} [used] 既に打った点（同じ点を二度出さない）
 * @returns {{key:string, x:number, z:number, kind:'reg'|'room'|'grid', label:string}[]}
 */
export function candidatePoints(layout, used = []) {
  const out = [];
  const seen = new Set((used || []).filter((p) => p && Number.isFinite(p.x) && Number.isFinite(p.z))
    .map((p) => pointKey(p.x, p.z)));
  const push = (x, z, kind, label) => {
    if (!Number.isFinite(x) || !Number.isFinite(z)) return;
    const key = pointKey(x, z);
    if (seen.has(key)) return;
    seen.add(key);
    out.push({ key, x, z, kind, label });
  };

  // 作者が地図に置いた印（`layout.calibPoints`）。**位置合わせ点のコピーではなく別の集合**で、
  // 現場の床に実物の目印があるものだけを置く（ラベル必須）。候補としては union で並べ、
  // どちらかへコピーはしない（2 コピーは必ずずれる）。
  for (const p of Array.isArray(layout?.calibPoints) ? layout.calibPoints : []) {
    if (!p) continue;
    push(p.x, p.z, 'mark', p.label ? `印・${p.label}` : '印');
  }

  const regSrc = Array.isArray(layout?.regPoints) && layout.regPoints.length
    ? layout.regPoints : DEFAULT_REG_POINTS;
  regSrc.forEach((p, i) => {
    if (!p) return;
    push(p.x, p.z, 'reg', `位置合わせ点 ${i + 1}${p.label ? `・${p.label}` : ''}`);
  });

  const w = layout?.wall;
  if (w && Array.isArray(w.corner) && Array.isArray(w.endX) && Array.isArray(w.endZ)) {
    push(w.corner[0], w.corner[1], 'room', '壁の外角');
    push(w.endX[0], w.endX[1], 'room', '壁の先端（東側）');
    push(w.endZ[0], w.endZ[1], 'room', '壁の先端（南側）');
  }
  const f = layout?.floor;
  if (f && f.w > 0 && f.d > 0) {
    const hw = f.w / 2, hd = f.d / 2;
    push(-hw, hd, 'room', '床の角（北西）');
    push(hw, hd, 'room', '床の角（北東）');
    push(hw, -hd, 'room', '床の角（南東）');
    push(-hw, -hd, 'room', '床の角（南西）');
  }
  // 部屋のプロキシ（🧱 で著作した壁の端・箱の角）。**映像で最も見つけやすい物理特徴**で、
  // しかも床の上にあるので較正に使える。
  for (const c of roomCorners(layout)) push(c.x, c.z, 'room', c.label);

  const g = layout?.grid;
  if (g && g.tileM > 0 && g.rows > 0 && g.cols > 0) {
    const rows = g.rows | 0, cols = g.cols | 0;
    const cells = parseGridCells(g.cells, rows, cols);
    // 交点の座標は **ZoneLayoutSolver.cellRect と同じ式**（cols×tileM 基準）で出す。
    // layout.floor 基準にすると、床とグリッドの寸法が食い違う show.json で半タイルずれる。
    const hw = cols * g.tileM / 2, hd = rows * g.tileM / 2;
    const painted = (r, c) => r >= 0 && r < rows && c >= 0 && c < cols && cells[r * cols + c] >= 0;
    for (let r = 0; r <= rows; r++) {
      for (let c = 0; c <= cols; c++) {
        if (!(painted(r - 1, c - 1) || painted(r - 1, c) || painted(r, c - 1) || painted(r, c))) continue;
        push(-hw + c * g.tileM, hd - r * g.tileM, 'grid', 'タイルの角');
      }
    }
  }
  return out;
}

/**
 * 実映像に重ねる検証ワイヤーの線分列（course 空間・投影前の純幾何）。
 * `projectPoint` はここでは掛けない — 幾何とカメラモデルを混ぜると、ずれた時に
 * 「部屋の記述が違うのか較正が違うのか」を切り分けられなくなる。
 *
 * @param {object|null} layout
 * @param {{gridStepM?:number, wallH?:number}} [opts]
 * @returns {{a:number[], b:number[], kind:'floor'|'grid'|'wall'|'line', color?:string}[]} a/b は [x,y,z]
 */
export function wireSegments(layout, opts = {}) {
  const step = opts.gridStepM > 0 ? opts.gridStepM : 0.3;
  const wallH = opts.wallH > 0 ? opts.wallH : 1.0;
  const f = (layout?.floor && layout.floor.w > 0 && layout.floor.d > 0) ? layout.floor : { w: 1.8, d: 1.8 };
  const hw = f.w / 2, hd = f.d / 2;
  const segs = [];
  const add = (a, b, kind, color) => segs.push(color ? { a, b, kind, color } : { a, b, kind });

  // 床の外周（これが実際の床の縁と合わないなら、姿勢より先に layout.floor を疑う）
  add([-hw, 0, -hd], [hw, 0, -hd], 'floor');
  add([hw, 0, -hd], [hw, 0, hd], 'floor');
  add([hw, 0, hd], [-hw, 0, hd], 'floor');
  add([-hw, 0, hd], [-hw, 0, -hd], 'floor');

  // 内側の格子。外周と重ならないよう内側だけ引く（整数カウンタで刻む＝誤差を溜めない）。
  for (let i = 1; -hw + i * step < hw - 1e-6; i++) {
    const x = -hw + i * step;
    add([x, 0, -hd], [x, 0, hd], 'grid');
  }
  for (let i = 1; -hd + i * step < hd - 1e-6; i++) {
    const z = -hd + i * step;
    add([-hw, 0, z], [hw, 0, z], 'grid');
  }

  // L 字壁（床の線・高さ wallH の上端・両端と外角の垂直線）。
  // **垂直線が実物の柱と重なるか**が、床だけでは分からない高さ方向のずれを暴く。
  const w = layout?.wall;
  if (w && Array.isArray(w.corner) && Array.isArray(w.endX) && Array.isArray(w.endZ)) {
    const chain = [w.endZ, w.corner, w.endX];
    for (let i = 0; i + 1 < chain.length; i++) {
      const p = chain[i], q = chain[i + 1];
      add([p[0], 0, p[1]], [q[0], 0, q[1]], 'wall');
      add([p[0], wallH, p[1]], [q[0], wallH, q[1]], 'wall');
    }
    for (const p of chain) add([p[0], 0, p[1]], [p[0], wallH, p[1]], 'wall');
  }

  // 通過ライン（演出の発火点）。担当カメラ色で描くとフロアマップと同じ見え方になる。
  for (const l of linesFromLayout(layout)) {
    add([l.x1, 0, l.z1], [l.x2, 0, l.z2], 'line', l.camera >= 0 ? camColor(l.camera) : '#fffaf0');
  }
  return segs;
}

/**
 * 保存済み `calib.refs`（正規化 uv）→ いまのフレームの画素座標の対応点。
 * これがあるので「カメラを動かした → 開く → ずれた点だけ掴んで直す → 解く」が 30 秒で回る。
 * refs は正規化なので解像度が変わっても使える（px 焦点距離と違って従属しない）。
 */
export function pointsFromRefs(refs, w, h) {
  if (!Array.isArray(refs) || !(w > 1) || !(h > 1)) return [];
  const out = [];
  for (const r of refs) {
    if (!r || ![r.u, r.v, r.x, r.z].every(Number.isFinite)) continue;
    // y は「床の点なら 0 / 壁の縦エッジの上端ならその高さ」。復元しないと、次に解いた時に
    // 高さの拘束が黙って消える（＝前回より精度が落ちるのに理由が分からない）。
    out.push({ x: r.x, z: r.z, y: Number.isFinite(r.y) && r.y > 0 ? r.y : 0, u: r.u * w, v: r.v * h });
  }
  return out;
}

/**
 * この較正はいま流れている映像に対して有効か。
 * Unity の `ShowCameraCalibDef.MatchesSource` と同じ判定（lensId はここでは見ない）。
 * **食い違ったまま重ねると、ワイヤーが盛大にずれて「較正が壊れた」と誤診する**。
 * 実際に壊れているのは解ではなく前提（配信解像度を変えた）なので、線を引かずにそう言う。
 */
export function calibMatchesSource(calib, w, h) {
  if (!calib || !(calib.fxPx > 1)) return false;
  if (!(w > 1) || !(h > 1)) return true;      // 実寸が分からないうちは邪魔しない
  return calib.srcW === w && calib.srcH === h;
}

/**
 * 「前回の画角」として固定に使える焦点距離 (px)。使えなければ 0。
 * **px 単位の焦点距離は解像度に従属する**ので、配信解像度が変わったら固定してはいけない
 * （黙って流用すると画角が 2 倍ずれた解を「固定したから正確」と誤認する）。
 */
export function lockableFocalPx(calib, w, h) {
  if (!calib || !(calib.fxPx > 1)) return 0;
  if (!(w > 1) || !(h > 1)) return 0;
  if (calib.srcW !== w || calib.srcH !== h) return 0;
  return calib.fxPx;
}

/** 焦点距離 (px) → 水平画角 (度)。卓の 📐 欄・フロアマップの扇と同じ「水平」で言う。 */
export function hfovFromFocal(fxPx, w) {
  if (!(fxPx > 0) || !(w > 0)) return 0;
  return 2 * Math.atan(w / 2 / fxPx) * 180 / Math.PI;
}

/** 結果表示の本文（人間の言葉）。俯角の符号は卓の 📐 欄と同じ「下向きが負」で揃える。 */
export function calibSummaryLines(calib) {
  if (!calib) return [];
  const hfov = hfovFromFocal(calib.fxPx, calib.srcW);
  const k1 = calib.k1 || 0;
  return [
    `カメラ位置 (${calib.x.toFixed(2)}, ${calib.z.toFixed(2)}) 高さ ${calib.y.toFixed(2)}m`,
    `水平画角 ${hfov.toFixed(1)}° / 俯角 ${calib.pitchDeg.toFixed(1)}°（下向きが負） / 傾き ${(calib.rollDeg || 0).toFixed(1)}°`,
    `歪み k1 ${k1 >= 0 ? '+' : ''}${k1.toFixed(2)} / 焦点距離 ${Math.round(calib.fxPx)}px / 点 ${calib.pointCount || 0} 個`
      + (raisedRefCount(calib) ? `（うち高さ ${raisedRefCount(calib)} 点）` : ''),
  ];
}

/** 保存された対応点のうち、床から浮いている（壁の縦エッジの上端）点の数。 */
export function raisedRefCount(calib) {
  return (calib && Array.isArray(calib.refs) ? calib.refs : []).filter((r) => r && r.y > 0.01).length;
}

/**
 * 解けた後に必ず出す注意書き。**黙って精度を落とさない**のがここの役目。
 * @param {{ok:boolean, calib?:object, focalLocked?:boolean, k1Estimated?:boolean}} result
 */
export function calibWarnings(result) {
  if (!result || !result.ok) return [];
  const out = [];
  const n = result.calib?.pointCount || 0;
  if (!result.focalLocked) {
    out.push('⚠ 画角も推定しています — 位置が数十 cm ずれることがあります（実測: クリック誤差 1.5px で 27cm）。'
      + '画角を固定して解けば 2cm 程度に収まります。');
  }
  if (!result.k1Estimated && n < MIN_POINTS_FOR_K1) {
    out.push(`⚠ 点が ${n} 個なのでレンズ歪みは推定していません（${MIN_POINTS_FOR_K1} 個以上、`
      + 'または画角を固定すれば歪みも解きます）。広角レンズだと画面の端でワイヤーがずれます。');
  }
  // 高さの点は「あると良い」ではなく、**無いと画角と距離が原理的に分離しない**（実測で桁が変わる）。
  const raised = result.raisedCount ?? raisedRefCount(result.calib);
  if (!result.focalLocked && raised < 2) {
    out.push(`⚠ 高さの点が ${raised} 本です。床の点だけだと「画角を広げて近づける解」と`
      + '「狭めて遠ざける解」がほとんど同じ絵になります（実測: 誤差 7cm → 高さ 3 本で 1cm）。'
      + '壁の縦エッジを 2〜3 本足してください。');
  }
  return out;
}

/**
 * カメラ列の 📐 欄に出すバッジ。srcW/srcH を渡すと、いま流れている映像との食い違いも見せる。
 * 食い違ったまま黙っていると、Unity 側で較正が無効化されて**理由の分からない「人形が出ない」**になる。
 */
export function calibBadgeText(cam, srcW, srcH) {
  const c = cam && cam.calib;
  if (!c || !(c.fxPx > 1)) return '';
  const when = c.solvedAtIso ? formatSolvedAt(c.solvedAtIso) : '';
  const head = `🎯 較正済み（誤差 ${Number.isFinite(c.rmsPx) ? c.rmsPx.toFixed(1) : '?'}px${when ? `・${when}` : ''}）`;
  if (!calibMatchesSource(c, srcW, srcH)) {
    return `${head} ⚠ ${c.srcW}×${c.srcH} 用（いまは ${srcW}×${srcH}）— 解き直しが必要`;
  }
  return head;
}

/** `2026-07-27T20:15:03` → `7/27 20:15`（表示専用・解釈に使わない）。 */
export function formatSolvedAt(iso) {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(String(iso || ''));
  if (!m) return '';
  return `${Number(m[2])}/${Number(m[3])} ${m[4]}:${m[5]}`;
}

/** ローカル時刻の ISO（タイムゾーン無し）。登録リチュアルの savedAtIso と同じ流儀。 */
export function localIsoNow(now = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${p(now.getMonth() + 1)}-${p(now.getDate())}`
    + `T${p(now.getHours())}:${p(now.getMinutes())}:${p(now.getSeconds())}`;
}

/**
 * 較正の解を cameras[] へ載せた**新しい配列**を返す（元配列は書き換えない）。
 * `pose` は消さない — pose = 人がドラッグする概算 / calib = 実測の解、で役割が別。
 * 統合すると卓のドラッグが解を破壊する（設計 §2.2 / 罠 5）。
 * present-flag は Unity の AND 規約に合わせて明示 bool で書く。
 */
export function applyCalibToCameras(cameras, camId, calib) {
  return (cameras || []).map((c) => (c && c.id === camId ? { ...c, calib: { ...calib }, hasCalib: true } : c));
}

/** 較正を捨てる（pose へ戻す）。誤った解を残す方が「出ない」より危ないので出口を必ず作る。 */
export function clearCalibFromCameras(cameras, camId) {
  return (cameras || []).map((c) => {
    if (!c || c.id !== camId) return c;
    const next = { ...c, hasCalib: false };
    delete next.calib;
    return next;
  });
}

// ---- UI ---------------------------------------------------------------------

const CAND_GROUPS = [
  ['mark', '自分で置いた印'],
  ['reg', '位置合わせ点（床の×印テープ）'],
  ['room', '部屋の角（壁・床の縁）'],
  ['grid', 'タイルの角（0.15m 格子）'],
  ['manual', '手入力'],
];

/**
 * 打った点の**質**を、解く前に言えるだけ言う。
 *   実測（calib.test.mjs）で分かっている効き方:
 *     画角を推定するなら 6 点以上（4〜5 点だと歪みを f に吸わせた偽解に落ちる）
 *     画角を固定するなら 4 点でよいが、どの 3 点も一直線でないこと
 *     床で散っていても**画面の隅に固まっていれば**解は暴れる（両方を見る）
 *
 * @param {{x:number,z:number,u:number,v:number}[]} pts
 * @param {number} frameW @param {number} frameH
 * @param {boolean} focalLocked 画角を固定して解くか
 */
export function pointQuality(pts, frameW, frameH, focalLocked) {
  const n = (pts || []).length;
  const need = focalLocked ? 4 : MIN_POINTS_FOR_K1;
  const span = (arr) => (arr.length ? Math.max(...arr) - Math.min(...arr) : 0);
  const floorSpread = n >= 2
    ? Math.hypot(span(pts.map((p) => p.x)), span(pts.map((p) => p.z))) : 0;
  const imgDiag = Math.hypot(frameW || 0, frameH || 0) || 1;
  const imgSpread = n >= 2
    ? Math.hypot(span(pts.map((p) => p.u)), span(pts.map((p) => p.v))) / imgDiag : 0;
  const collinear = n >= 3 && hasCollinearTriple(pts);
  const degenerate = n >= 3 && isDegenerate(pts);

  const issues = [];
  if (n < 4) issues.push(`あと ${4 - n} 点（最低 4 点）`);
  else if (n < need) issues.push(`画角も一緒に解くなら あと ${need - n} 点（${need} 点以上）`);
  if (degenerate) issues.push('点が一直線に近い（部屋の広がりを使って離す）');
  else if (collinear && n <= 5) issues.push('3 点が一直線に並んでいる（4 点ちょうどだと解けない）');
  if (n >= 4 && imgSpread < 0.35) issues.push('画面の中で点が固まっている（画の端まで使う）');
  if (n >= 4 && floorSpread < 0.6) issues.push('床の上で点が近すぎる（離れた場所の点を混ぜる）');

  return {
    n, need, floorSpread, imgSpread, collinear, degenerate, issues,
    ready: n >= 4 && !degenerate && !(n === 4 && collinear),
  };
}

/**
 * 較正パネルを作る。カメラ列の［🎯 姿勢を合わせる］から `open(camId)` で開く。
 *
 * @param {HTMLElement} container 置き場所（通常 document.body。全画面オーバーレイを足す）
 * @param {{getCameras:()=>object[], getLayout:()=>object|null,
 *          getLiveImg:(camId:string)=>HTMLImageElement|null,
 *          saveCameras:(cams:object[])=>any}} deps
 */
export function createCalibUi(container, deps) {
  const root = document.createElement('div');
  root.className = 'calib-ui';
  root.style.display = 'none';
  root.innerHTML = `
    <div class="cu-backdrop"></div>
    <div class="cu-panel" role="dialog" aria-modal="true" aria-label="カメラ姿勢の較正">
      <div class="cu-head">
        <b class="cu-title">🎯 姿勢を合わせる</b>
        <span class="cu-frameinfo"></span>
        <span class="spacer"></span>
        <button class="cu-close">✕ 閉じる</button>
      </div>
      <div class="cu-body">
        <div class="cu-viewcol">
          <div class="cu-canvas-wrap">
            <canvas class="cu-canvas" width="640" height="480"></canvas>
            <div class="cu-noframe"></div>
          </div>
          <div class="cu-ptlist"></div>
          <div class="cu-hint">静止フレームです（打っている間に絵が動かないよう固定しています）。
            <b>打つ点</b>を選んでから映像上のその場所をクリック。点はドラッグで微調整・右クリックで削除。
            4 点以上で <b>✨ 解く</b>（4 点ちょうどのときは 3 点が一直線に並ばないように）。</div>
        </div>
        <div class="cu-side">
          <div class="cu-mapblock">
            <div class="cu-maphead">部屋を上から見た図<span class="cu-mapmode-hint"></span></div>
            <canvas class="cu-map" width="300" height="300"></canvas>
            <div class="cu-maprow">
              <label class="chk"><input class="cu-mapadd" type="checkbox"> 印を置く</label>
              <input class="cu-marklabel" type="text" placeholder="目印の名前（例: 棚の脚）" maxlength="24">
              <button class="cu-markdel" title="選んでいる印を消す">🗑</button>
            </div>
            <div class="cu-maphint">図の点をクリックすると「打つ点」がそこに切り替わります。
              <b>印を置く</b>にすると、クリックした場所に自分の基準点を作れます（タイルの角と部屋の角へ吸着）。
              <b>点は床の上にあるもの・実物が現場にあるものだけ</b>にしてください（机の上の印は使えません）。</div>
          </div>
          <label class="cu-lbl">打つ点 <select class="cu-cand"></select></label>
          <div class="cu-manual">
            <label>X<input class="cu-mx" type="number" step="0.05" placeholder="0.00"></label>
            <label>Z<input class="cu-mz" type="number" step="0.05" placeholder="0.00"></label>
            <button class="cu-madd" title="course 座標が分かっている点を候補に足す（テープを増やした時など）">＋ 座標を足す</button>
          </div>
          <!-- 高さの点。床だけでは画角と距離が縮退するので、縦エッジを入れて平面の外から拘束する -->
          <div class="cu-heightbox">
            <div class="cu-heightrow">
              <button class="cu-addtop" title="いま打った点の真上（壁の上端）を次のクリックで打つ">▲ 高さの点を足す</button>
              <label>壁の高さ<input class="cu-height" type="number" step="0.05" min="0.1" value="1.80">m</label>
            </div>
            <div class="cu-heighthint">床の点だけだと<b>画角と距離が区別できません</b>（同じ絵になる解が無数にある）。
              壁の縦エッジを 2〜3 本入れると、画角・高さ・傾きがまとめて決まります。
              打ち方は「床の角を打つ → ▲ を押す → <b>同じ角の真上（壁の上端）</b>をクリック」。</div>
          </div>
          <div class="cu-quality"></div>
          <label class="chk cu-locklbl"><input class="cu-lock" type="checkbox"> <span class="cu-locktext"></span></label>
          <div class="cu-lockhint"></div>
          <div class="cu-actions">
            <button class="cu-refresh" title="いまのライブ映像で静止フレームを取り直す（点はそのまま残る）">🔄 フレームを取り直す</button>
            <button class="cu-solve accent">✨ 解く</button>
            <button class="cu-save accent">💾 保存</button>
          </div>
          <div class="cu-msg"></div>
          <div class="cu-result"></div>
          <div class="cu-wirekey">重ねている線: <span class="k-floor">床</span> <span class="k-grid">0.3m 格子</span>
            <span class="k-wall">壁</span> <span class="k-line">通過ライン</span> ／
            <span class="k-mark">○ 打った点</span> <span class="k-reproj">✕ 解が言う位置</span></div>
          <div class="cu-danger"><button class="cu-discard">✕ 較正を捨てて概算 pose に戻す</button></div>
        </div>
      </div>
    </div>`;
  container.appendChild(root);

  const q = (s) => root.querySelector(s);
  const canvas = q('.cu-canvas');
  const ctx = canvas.getContext('2d');
  const noFrameEl = q('.cu-noframe');
  const candSel = q('.cu-cand');
  const lockChk = q('.cu-lock'), lockText = q('.cu-locktext'), lockHint = q('.cu-lockhint');
  const msgEl = q('.cu-msg'), resultEl = q('.cu-result'), ptListEl = q('.cu-ptlist');
  const saveBtn = q('.cu-save'), discardBtn = q('.cu-discard');
  const mapCanvas = q('.cu-map'), mapCtx = mapCanvas.getContext('2d');
  const heightInput = q('.cu-height'), addTopBtn = q('.cu-addtop');
  const mapAddChk = q('.cu-mapadd'), markLabelInput = q('.cu-marklabel'), qualityEl = q('.cu-quality');

  // 状態
  let camId = '';
  let frame = null;        // { canvas, w, h } 静止フレーム
  let pts = [];            // [{x,z,u,v,label}] u,v は**フレーム実寸の画素**
  let manual = [];         // 手入力で足した候補（このセッション限り）
  let solved = null;       // calibrateFromFloorPoints の戻り（新しく解いたもの）
  let saved = null;        // show.json に入っている calib（開いた時点のもの）
  let lensId = '';
  let lockedFocalPx = 0;
  let dragIdx = -1;
  let msgTimer = 0;
  // 「次のクリックはこの床点の真上（高さ wallHeight）」という予約。null なら通常の床点。
  let topFor = null;

  /**
   * 壁の高さ（m）。**高さの点の y** と **検証ワイヤーの壁の高さ**の両方に効く単一の値。
   * 分けて持つと「打った上端」と「重ねた線」が違う高さになり、ずれの原因が分からなくなる。
   */
  const wallHeight = () => {
    const v = parseFloat(heightInput.value);
    return Number.isFinite(v) && v > 0.05 ? v : 1.8;
  };
  const cameras = () => deps.getCameras() || [];
  const camOf = (id) => cameras().find((c) => c.id === id) || null;
  const layout = () => deps.getLayout() || null;
  /** いま画面に出すべき較正（新しく解いたものが優先・無ければ保存済み）。 */
  const shownCalib = () => (solved && solved.ok ? solved.calib : saved);

  function note(m, cls = '') {
    msgEl.textContent = m;
    msgEl.className = 'cu-msg ' + cls;
    clearTimeout(msgTimer);
    if (m) msgTimer = setTimeout(() => { msgEl.textContent = ''; msgEl.className = 'cu-msg'; }, 6000);
  }

  // ---- 静止フレーム ----------------------------------------------------------
  //   ライブ <img> を 1 枚だけコピーして固定する。ここを省いてライブを直接使うと、
  //   点を打っている最中にフレームが変わって「打った位置と絵」がずれる。
  function grabFrame() {
    const img = deps.getLiveImg(camId);
    if (!img || !img.naturalWidth || !img.naturalHeight) return false;
    const cv = document.createElement('canvas');
    cv.width = img.naturalWidth; cv.height = img.naturalHeight;
    cv.getContext('2d').drawImage(img, 0, 0);
    frame = { canvas: cv, w: cv.width, h: cv.height };
    canvas.width = frame.w; canvas.height = frame.h;
    canvas.style.aspectRatio = `${frame.w} / ${frame.h}`;
    return true;
  }

  function syncFrameInfo() {
    const el = q('.cu-frameinfo');
    el.textContent = frame ? `${frame.w}×${frame.h}${lensId ? ` / レンズ ${lensId}` : ''}` : '映像なし';
    noFrameEl.style.display = frame ? 'none' : '';
    noFrameEl.textContent = frame ? ''
      : 'このカメラの映像が来ていません（① 生リアルタイム映像が LIVE になってから開いてください）';
  }

  // /info の lensId。取れなくても較正はできる（照合が緩くなるだけ）ので静かに諦める。
  async function fetchLensId() {
    // 開き直し / 別カメラへの切り替えを跨いで結果が返ることがある。**別カメラのレンズ ID を
    // 焼き込むと較正が黙って無効化される**（Unity が lensId で照合する）ので世代を照合する。
    const id = camId;
    lensId = '';
    const cam = camOf(id);
    if (!cam || !cam.host) return;
    let found = '';
    try {
      const r = await fetch(`${streamBase()}/cam?host=${encodeURIComponent(cam.host)}`
        + `&port=${cam.port || 8080}&path=/info`
        + (cam.auth ? `&auth=${encodeURIComponent(cam.auth)}` : ''));
      const j = await r.json();
      if (j && typeof j.lensId === 'string') found = j.lensId;
    } catch { /* /info を持たない配信アプリ（IP Camera Lite 等）は普通にある */ }
    if (id !== camId) return;
    lensId = found;
    syncFrameInfo();
  }

  // ---- 候補点セレクト --------------------------------------------------------
  function allCandidates() {
    return [...candidatePoints(layout(), pts), ...manual.filter((m) => !pts.some((p) => pointKey(p.x, p.z) === m.key))];
  }
  function renderCandidates(keepKey) {
    const list = allCandidates();
    const prev = keepKey || candSel.value;
    candSel.innerHTML = '';
    for (const [kind, label] of CAND_GROUPS) {
      const items = list.filter((c) => c.kind === kind);
      if (!items.length) continue;
      const og = document.createElement('optgroup');
      og.label = `${label}（${items.length}）`;
      for (const c of items) {
        const o = document.createElement('option');
        o.value = c.key;
        o.textContent = `${c.label} (${c.x.toFixed(2)}, ${c.z.toFixed(2)})`;
        og.appendChild(o);
      }
      candSel.appendChild(og);
    }
    if (!list.length) {
      const o = document.createElement('option');
      o.value = ''; o.textContent = '（候補がありません — 座標を手入力してください）';
      candSel.appendChild(o);
    }
    // 直前の選択が残っていればそれを、消えていれば先頭（＝次に打つべき点）へ送る。
    candSel.value = list.some((c) => c.key === prev) ? prev : (list[0]?.key || '');
  }
  const selectedCandidate = () => allCandidates().find((c) => c.key === candSel.value) || null;

  // ---- ミニ地図（部屋を上から見た図）------------------------------------------
  //   セレクトの文字列（「タイルの角 (0.30, -0.45)」）だけでは、それが部屋のどこなのか
  //   作者には分からない。ここで**候補と打った点を絵にして**、地図の上で選べるようにする。
  //   置ける点（`layout.calibPoints`）は「現場の床に実物の目印があるもの」に限る — 地図で
  //   決めた座標は、床に印が無ければ現場で同定できず、誤差として表面化しないまま解を歪める。
  const camPose = (c) => ((c.calib && c.calib.fxPx > 1) ? c.calib : c.pose) || null;
  function drawMap() {
    const lay = layout();
    const view = sketchView(lay, mapCanvas, 12);
    drawFloorSketch(mapCtx, lay, view, {
      // 地図は床の図なので、高さの点（同じ床位置の上端）は重ねて描かない。
      points: pts.filter((p) => !(p.y > 0)).map((p) => ({ x: p.x, z: p.z, done: true })),
      marks: allCandidates().map((c) => ({ x: c.x, z: c.z, key: c.key, kind: c.kind })),
      activeKey: candSel.value,
      cameras: cameras().map((c, i) => ({ index: i, id: c.id, pose: camPose(c) }))
        .filter((c) => c.pose),
    });
    return view;
  }
  /** 地図のクリック位置 → course 座標（吸着つき）。 */
  function mapToCourse(ev) {
    const rect = mapCanvas.getBoundingClientRect();
    if (!rect.width) return null;
    const view = sketchView(layout(), mapCanvas, 12);
    const px = (ev.clientX - rect.left) / rect.width * mapCanvas.width;
    const py = (ev.clientY - rect.top) / rect.height * mapCanvas.height;
    return { view, px, py, ...view.toCourse(px, py) };
  }
  function onMapClick(ev) {
    const hit = mapToCourse(ev);
    if (!hit) return;
    const cands = allCandidates();
    if (!mapAddChk.checked) {
      // 近い候補を「打つ点」にする（地図から選ぶ）。
      const view = hit.view;
      const i = hitPoint(cands, view, hit.px, hit.py, 14);
      if (i < 0) return note('その辺りに候補点がありません（印を置くならチェックを入れる）', 'err');
      candSel.value = cands[i].key;
      afterCandidateChanged();
      return;
    }
    // 自分の印を置く。物理の目印がある所を指すのが前提なので**ラベル必須**。
    const label = markLabelInput.value.trim();
    if (!label) return note('印の名前を入れてください（例: 棚の脚・テープの×）', 'err');
    const room = nearestRoomCorner(layout(), hit.x, hit.z, 0.12);
    const p = room || snapToGrid(layout(), hit.x, hit.z);
    saveMarks([...(layout()?.calibPoints || []), { x: p.x, z: p.z, label }]);
    note(`印「${label}」を (${p.x.toFixed(2)}, ${p.z.toFixed(2)}) に置きました — 現場の床にも印を付けてください`, 'ok');
  }
  async function saveMarks(list) {
    const lay = { ...(layout() || {}), calibPoints: list };
    if (typeof deps.saveLayout === 'function') await deps.saveLayout(lay);
    renderCandidates();
    afterCandidateChanged();
  }
  function deleteSelectedMark() {
    const c = selectedCandidate();
    if (!c || c.kind !== 'mark') return note('消せるのは自分で置いた印だけです', 'err');
    const list = (layout()?.calibPoints || []).filter((p) => pointKey(p.x, p.z) !== c.key);
    saveMarks(list);
    note('印を消しました', 'ok');
  }
  /** 「打つ点」が変わった時（地図・セレクトの両方から呼ぶ）。 */
  function afterCandidateChanged() { drawMap(); renderQuality(); }

  // ---- 高さの点（壁の縦エッジ）------------------------------------------------
  function syncTopUi() {
    addTopBtn.classList.toggle('armed', !!topFor);
    addTopBtn.textContent = topFor ? '▲ 上端をクリック（Esc で取消）' : '▲ 高さの点を足す';
  }
  /** 直近に打った床の点の真上を、次のクリックで打てるようにする。 */
  function armTop() {
    // 上端をぶら下げられるのは**床の点**だけ（上端の上端は無い）。
    for (let i = pts.length - 1; i >= 0; i--) {
      const p = pts[i];
      if (p.y > 0) continue;
      // 同じ床位置に既に上端があるなら二度打たせない（同じ拘束が重複しても精度は上がらない）。
      if (pts.some((q) => q.y > 0 && Math.abs(q.x - p.x) < 1e-6 && Math.abs(q.z - p.z) < 1e-6)) continue;
      topFor = { x: p.x, z: p.z, h: wallHeight(), label: p.label || `(${p.x.toFixed(2)}, ${p.z.toFixed(2)})` };
      syncTopUi();
      note(`${topFor.label} の真上（高さ ${topFor.h.toFixed(2)}m）を映像でクリックしてください`, 'ok');
      return;
    }
    note('先に床の点を打ってください（その真上が高さの点になります）', 'err');
  }

  // ---- 点の質（解く前に言う）--------------------------------------------------
  function renderQuality() {
    const qy = pointQuality(pts, frame?.w || 0, frame?.h || 0, lockChk.checked && lockedFocalPx > 0);
    const raised = pts.filter((p) => p.y > 0).length;
    const head = `床 ${qy.n - raised} 点 + 高さ ${raised} 点 / `
      + `${qy.ready ? '解けます' : `${qy.need} 点まであと ${Math.max(0, qy.need - qy.n)}`}`;
    const detail = qy.n >= 2
      ? `　床の広がり ${qy.floorSpread.toFixed(2)}m ・ 画面の広がり ${Math.round(qy.imgSpread * 100)}%` : '';
    const tips = qy.issues.slice();
    if (raised < 2) {
      tips.push(raised === 0
        ? '高さの点がありません（壁の縦エッジを 2〜3 本入れると画角と距離の縮退が解けます）'
        : '高さの点が 1 本だけです（もう 1 本入れると画角が決まります）');
    }
    qualityEl.innerHTML = `<b>${escapeHtml(head)}</b><span class="cu-qdetail">${escapeHtml(detail)}</span>`
      + (tips.length ? `<ul>${tips.map((s) => `<li>${escapeHtml(s)}</li>`).join('')}</ul>` : '');
    qualityEl.className = 'cu-quality' + (qy.ready ? ' ok' : '');
  }

  // ---- 打った点 --------------------------------------------------------------
  function renderPointList() {
    ptListEl.innerHTML = '';
    if (!pts.length) {
      const e = document.createElement('span');
      e.className = 'cu-pt-empty';
      e.textContent = '（まだ点がありません。右で点を選んで映像をクリック）';
      ptListEl.appendChild(e);
      return;
    }
    pts.forEach((p, i) => {
      const chip = document.createElement('span');
      chip.className = 'cu-pt';
      chip.className = 'cu-pt' + (p.y > 0 ? ' up' : '');
      chip.innerHTML = `<b>${i + 1}</b> ${p.y > 0 ? '▲ ' : ''}${escapeHtml(p.label || '')} `
        + `(${p.x.toFixed(2)}, ${p.z.toFixed(2)}${p.y > 0 ? `, 高さ ${p.y.toFixed(2)}m` : ''})`;
      const del = document.createElement('button');
      del.textContent = '✕';
      del.title = 'この点を消す';
      del.onclick = () => { pts.splice(i, 1); afterPointsChanged(); };
      chip.appendChild(del);
      ptListEl.appendChild(chip);
    });
  }
  function afterPointsChanged() {
    // 点を動かしたら前の解は無効。**古い解のワイヤーを残したまま点だけ動く**のが一番危ない
    // （合っていないのに合っているように見える）。
    solved = null;
    renderCandidates();
    renderPointList();
    renderResult();
    renderQuality();
    drawMap();
    draw();
  }

  // ---- 描画 ------------------------------------------------------------------
  const WIRE_STYLE = {
    floor: { color: 'rgba(255,222,173,0.95)', w: 2 },
    grid: { color: 'rgba(255,222,173,0.35)', w: 1 },
    wall: { color: 'rgba(120,220,255,0.95)', w: 2 },
    line: { color: '#fffaf0', w: 2 },
  };

  function draw() {
    if (!frame) { ctx.clearRect(0, 0, canvas.width, canvas.height); return; }
    ctx.drawImage(frame.canvas, 0, 0);
    const s = Math.max(1, frame.w / 640);        // 640px 基準で線幅・字を拡縮
    const c0 = shownCalib();
    // 解像度が食い違う較正で線を引くと、ずれの原因が「解」なのか「前提」なのか分からなくなる。
    const calib = calibMatchesSource(c0, frame.w, frame.h) ? c0 : null;

    if (calib) {
      for (const seg of wireSegments(layout(), { wallH: wallHeight() })) {
        const a = projectPoint(calib, seg.a[0], seg.a[1], seg.a[2]);
        const b = projectPoint(calib, seg.b[0], seg.b[1], seg.b[2]);
        // 片端がカメラ後方なら描かない。無理に伸ばすと画面外へ暴れて「較正が壊れた」ように見える。
        if (!a || !b) continue;
        const st = WIRE_STYLE[seg.kind] || WIRE_STYLE.line;
        ctx.strokeStyle = seg.color || st.color;
        ctx.lineWidth = st.w * s;
        ctx.beginPath(); ctx.moveTo(a.u, a.v); ctx.lineTo(b.u, b.v); ctx.stroke();
      }
    }

    // 打った点（○ + 番号）と、解が言う位置（✕）。この 2 つのズレが残差そのもの。
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.font = `bold ${11 * s}px system-ui, sans-serif`;
    // 高さの点は「同じ床位置の点」と線で結ぶ。縦エッジが実物の柱・壁の角に重なっているかは、
    // この線が実際の縦の稜線に乗っているかで一目で分かる（打ち間違いの最有力な検出手段）。
    ctx.strokeStyle = 'rgba(120,220,255,0.85)';
    ctx.lineWidth = 2 * s;
    pts.forEach((p) => {
      if (!(p.y > 0)) return;
      const base = pts.find((q) => !(q.y > 0) && Math.abs(q.x - p.x) < 1e-6 && Math.abs(q.z - p.z) < 1e-6);
      if (!base) return;
      ctx.beginPath(); ctx.moveTo(base.u, base.v); ctx.lineTo(p.u, p.v); ctx.stroke();
    });
    pts.forEach((p, i) => {
      const up = p.y > 0;
      ctx.beginPath(); ctx.arc(p.u, p.v, 9 * s, 0, Math.PI * 2);
      // 塗ってから縁取る（逆にすると半透明の塗りが縁を食って番号が読みにくくなる）
      ctx.fillStyle = 'rgba(20,24,32,0.75)'; ctx.fill();
      ctx.strokeStyle = up ? '#78dcff' : '#ffdead'; ctx.lineWidth = 2 * s; ctx.stroke();
      ctx.fillStyle = up ? '#78dcff' : '#ffdead'; ctx.fillText(String(i + 1), p.u, p.v + 0.5 * s);
      if (calib) {
        const rp = projectPoint(calib, p.x, p.y || 0, p.z);
        if (rp) {
          // 打った点と「解が言う位置」を結ぶ。これが残差そのもので、長い線＝直すべき点。
          const far = Math.hypot(rp.u - p.u, rp.v - p.v) > 6 * s;
          if (far) {
            ctx.strokeStyle = 'rgba(255,90,90,0.9)'; ctx.lineWidth = 1.5 * s;
            ctx.beginPath(); ctx.moveTo(p.u, p.v); ctx.lineTo(rp.u, rp.v); ctx.stroke();
          }
          ctx.strokeStyle = far ? '#ff5a5a' : '#5ad19a'; ctx.lineWidth = 2 * s;
          ctx.beginPath();
          ctx.moveTo(rp.u - 6 * s, rp.v - 6 * s); ctx.lineTo(rp.u + 6 * s, rp.v + 6 * s);
          ctx.moveTo(rp.u + 6 * s, rp.v - 6 * s); ctx.lineTo(rp.u - 6 * s, rp.v + 6 * s);
          ctx.stroke();
        }
      }
    });
  }

  // ---- 解く / 保存 -----------------------------------------------------------
  function syncLockUi() {
    const hfov = hfovFromFocal(lockedFocalPx, frame ? frame.w : 0);
    lockChk.disabled = !(lockedFocalPx > 0);
    lockText.textContent = lockedFocalPx > 0
      ? `画角を固定して解く（${Math.round(lockedFocalPx)}px 相当・水平 ${hfov.toFixed(0)}°）`
      : '画角を固定して解く（固定できる値がまだありません）';
    lockHint.textContent = lockedFocalPx > 0
      ? '内部（画角）は一度だけ丁寧に、外部（置き場所）は現場で毎回。固定して解くと位置の精度が一桁上がります。'
      : '一度解くと、その画角を次から固定できます（同じ解像度・同じレンズのときだけ）。';
  }

  function renderResult() {
    const r = solved;
    resultEl.innerHTML = '';
    const addLine = (text, cls = '') => {
      const d = document.createElement('div');
      d.className = 'cu-res-line ' + cls;
      d.textContent = text;
      resultEl.appendChild(d);
      return d;
    };

    // 壁の座標が既定のままなら、**点の打ち方より先に**そこを疑わせる。
    if (wallLooksDefault(layout())) {
      addLine('⚠ 壁の座標（layout.wall）が卓の既定値（1m × 1m の L）のままです。'
        + '現場の壁がこの寸法でないなら、「壁の外角」などを打っても座標が実物と違うので合いません。'
        + 'フロアマップの 🧱 部屋 で実物を測って置くか、床のタイルの角など寸法が確かな点だけで解いてください。',
      'warn');
    }
    if (!r && saved) {
      const qy = Number.isFinite(saved.rmsPx) ? calibQualityLabel(saved.rmsPx).text : '誤差の記録なし';
      addLine(`保存済みの較正を表示中 — ${qy}`, 'saved');
      for (const l of calibSummaryLines(saved)) addLine(l, 'sub');
      if (frame && !calibMatchesSource(saved, frame.w, frame.h)) {
        addLine(`⚠ この較正は ${saved.srcW}×${saved.srcH} 用です（いまの映像は ${frame.w}×${frame.h}）。`
          + 'ワイヤーは重ねません — 実機でも較正は無効になるので解き直してください。', 'bad');
      } else {
        addLine('ワイヤーが実物とずれていれば、点を直して ✨ 解く。', 'sub');
      }
    } else if (r && r.ok) {
      const qy = calibQualityLabel(r.calib.rmsPx);
      addLine(`結果: ${qy.text}`, qy.level);
      for (const l of calibSummaryLines(r.calib)) addLine(l, 'sub');
      // どの点が悪いのかを名指しする。「点を打ち直してください」だけでは、
      // 9 点のうちどれを直せばいいのか分からない（2026-07-29 ユーザー報告）。
      const worst = perPointResiduals(r.calib, pts)
        .map((x, i) => ({ ...x, no: i + 1, up: pts[i].y > 0 }))
        .sort((a, b) => b.dist - a.dist)
        .filter((x) => !x.ok || x.dist > Math.max(4, r.calib.rmsPx * 1.8))
        .slice(0, 3);
      if (worst.length) {
        addLine(`ずれの大きい点: ${worst.map((x) => `${x.no}番${x.up ? '（高さ）' : ''} `
          + `${x.ok ? `${Math.round(x.dist)}px` : '投影できない'}`).join(' / ')}`, 'bad');
        addLine('その点だけ掴んで直すか、右クリックで消してから解き直してください。'
          + '高さの点なら「壁の高さ」の値が実物と合っているかも確かめてください。', 'sub');
      }
      for (const w of calibWarnings(r)) addLine(w, 'warn');
      if (!r.focalLocked && r.calib.fxPx > 1) {
        const b = document.createElement('button');
        b.className = 'cu-lockto';
        b.textContent = `📌 この画角（${Math.round(r.calib.fxPx)}px）を固定して解き直す`;
        b.title = '同じ点のままなら結果はほぼ変わりません。効くのは次回 — カメラを動かした後に'
          + '「置き場所だけ」を解けるようになります。';
        b.onclick = () => {
          lockedFocalPx = r.calib.fxPx;
          lockChk.checked = true;
          syncLockUi();
          solve();
        };
        resultEl.appendChild(b);
      }
    } else if (r && !r.ok) {
      addLine(`解けません: ${r.reason}`, 'bad');
      if (Array.isArray(r.badPoints) && r.badPoints.length) {
        addLine('その番号の点を掴んで直すか、右クリックで消してから解き直してください。', 'sub');
      }
    } else {
      // 未較正で開いた直後。空箱にすると「何をすれば進むのか」が画面から消える。
      addLine('まだ較正していません。床の点を 4 個以上打って ✨ 解く。', 'sub');
      addLine('点は部屋いっぱいに散らす（一箇所に固まると解が暴れます）。', 'sub');
    }
    saveBtn.disabled = !(r && r.ok);
    discardBtn.style.display = saved ? '' : 'none';
  }

  function solve() {
    if (!frame) return note('映像がありません（🔄 フレームを取り直す）', 'err');
    // 床の点が 4 つ要る（高さの点は平面の外なのでホモグラフィには入らない）。
    const floorCount = pts.filter((p) => !(p.y > 0)).length;
    if (floorCount < 4) return note(`床の点が ${floorCount} 個です（4 点以上必要）`, 'err');
    solved = calibrateFromFloorPoints(pts, frame.w, frame.h, {
      lensId,
      solvedAtIso: localIsoNow(),
      fixedFocalPx: lockChk.checked ? lockedFocalPx : 0,
    });
    renderResult();
    draw();
    note(solved.ok ? 'ワイヤーが実物と重なっているか確かめてください（重なっていなければ保存しない）'
      : '', solved.ok ? 'ok' : '');
  }

  async function save() {
    if (!(solved && solved.ok)) return;
    const next = applyCalibToCameras(cameras(), camId, solved.calib);
    await deps.saveCameras(next);
    saved = { ...solved.calib };
    // 保存した画角はそのまま「次に固定できる値」になる。
    lockedFocalPx = lockableFocalPx(saved, frame?.w, frame?.h) || lockedFocalPx;
    syncLockUi();
    renderResult();
    note('💾 保存しました（Unity へは long-poll で届きます）', 'ok');
  }

  async function discard() {
    const next = clearCalibFromCameras(cameras(), camId);
    await deps.saveCameras(next);
    saved = null; solved = null;
    renderResult(); draw();
    note('較正を捨てました（このカメラは 📐 の概算 pose に戻ります）', 'ok');
  }

  // ---- キャンバス操作 --------------------------------------------------------
  //   座標は必ず**フレーム実寸の画素**へ直してから持つ（表示サイズで持つと、パネル幅が
  //   変わっただけで対応が壊れる）。
  function toFramePx(ev) {
    const rect = canvas.getBoundingClientRect();
    if (!rect.width || !rect.height || !frame) return null;
    return {
      u: (ev.clientX - rect.left) / rect.width * frame.w,
      v: (ev.clientY - rect.top) / rect.height * frame.h,
      hit: 12 * frame.w / rect.width,      // 掴み判定は「見た目 12px」相当
    };
  }
  const nearestPoint = (u, v, hit) => {
    let best = -1, bd = hit;
    pts.forEach((p, i) => { const d = Math.hypot(p.u - u, p.v - v); if (d <= bd) { bd = d; best = i; } });
    return best;
  };

  canvas.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0) return;
    const t = toFramePx(ev);
    if (!t) return;
    const hitIdx = nearestPoint(t.u, t.v, t.hit);
    if (hitIdx >= 0) {                    // 既存点を掴んで微調整
      dragIdx = hitIdx;
      canvas.setPointerCapture(ev.pointerId);
      return;
    }
    if (topFor) {
      pts.push({ x: topFor.x, z: topFor.z, y: topFor.h, u: t.u, v: t.v, label: `${topFor.label} の上端` });
      topFor = null;
      syncTopUi();
      afterPointsChanged();
      return;
    }
    const cand = selectedCandidate();
    if (!cand) return note('打つ点を選んでください（候補が無ければ座標を手入力）', 'err');
    pts.push({ x: cand.x, z: cand.z, y: 0, u: t.u, v: t.v, label: cand.label });
    afterPointsChanged();
  });
  canvas.addEventListener('pointermove', (ev) => {
    if (dragIdx < 0) return;
    const t = toFramePx(ev);
    if (!t) return;
    pts[dragIdx].u = t.u; pts[dragIdx].v = t.v;
    solved = null;                        // 動かした時点で前の解は無効
    draw();
  });
  const endDrag = () => { if (dragIdx >= 0) { dragIdx = -1; afterPointsChanged(); } };
  canvas.addEventListener('pointerup', endDrag);
  canvas.addEventListener('pointercancel', endDrag);
  canvas.addEventListener('contextmenu', (ev) => {
    ev.preventDefault();
    const t = toFramePx(ev);
    if (!t) return;
    const i = nearestPoint(t.u, t.v, t.hit);
    if (i >= 0) { pts.splice(i, 1); afterPointsChanged(); }
  });

  // ---- 配線 ------------------------------------------------------------------
  q('.cu-close').onclick = () => close();
  q('.cu-backdrop').onclick = () => close();
  q('.cu-solve').onclick = () => solve();
  saveBtn.onclick = () => save();
  discardBtn.onclick = () => discard();
  q('.cu-refresh').onclick = () => {
    if (!grabFrame()) return note('ライブ映像が来ていません', 'err');
    lockedFocalPx = lockableFocalPx(saved, frame.w, frame.h) || lockedFocalPx;
    syncFrameInfo(); syncLockUi(); draw();
    note('フレームを取り直しました（点はそのまま）', 'ok');
  };
  lockChk.onchange = () => { syncLockUi(); renderQuality(); };
  candSel.onchange = () => afterCandidateChanged();
  mapCanvas.addEventListener('click', onMapClick);
  mapAddChk.onchange = () => {
    q('.cu-mapmode-hint').textContent = mapAddChk.checked
      ? '（クリックした場所に印を置きます）' : '（点をクリックすると「打つ点」が切り替わります）';
  };
  q('.cu-markdel').onclick = () => deleteSelectedMark();
  addTopBtn.onclick = () => { if (topFor) { topFor = null; syncTopUi(); note('', ''); } else armTop(); };
  heightInput.onchange = () => { if (topFor) topFor.h = wallHeight(); draw(); };
  q('.cu-madd').onclick = () => {
    const x = parseFloat(q('.cu-mx').value), z = parseFloat(q('.cu-mz').value);
    if (!Number.isFinite(x) || !Number.isFinite(z)) return note('X と Z を入れてください', 'err');
    const key = pointKey(x, z);
    if (!manual.some((m) => m.key === key)) manual.push({ key, x, z, kind: 'manual', label: '手入力' });
    renderCandidates(key);
    note(`候補に (${x.toFixed(2)}, ${z.toFixed(2)}) を足しました`, 'ok');
  };
  const onKey = (ev) => {
    if (ev.key !== 'Escape' || root.style.display === 'none') return;
    // 上端の予約中は「予約の取消」を先に食わせる（間違えて閉じると打った点が消える）。
    if (topFor) { topFor = null; syncTopUi(); note('高さの点をやめました', ''); return; }
    close();
  };

  function open(id) {
    camId = id;
    const cam = camOf(id);
    if (!cam) return;
    q('.cu-title').textContent = `🎯 カメラ ${cam.id} の姿勢を合わせる`;
    manual = []; solved = null; dragIdx = -1;
    saved = (cam.calib && cam.calib.fxPx > 1) ? { ...cam.calib } : null;
    frame = null;
    grabFrame();
    // 前回の対応点を復元する（「ずれた点だけ直して解き直す」が現場の通常運転）。
    pts = frame ? pointsFromRefs(saved?.refs, frame.w, frame.h) : [];
    lockedFocalPx = lockableFocalPx(saved, frame?.w, frame?.h);
    lockChk.checked = lockedFocalPx > 0;
    root.style.display = '';
    document.addEventListener('keydown', onKey);
    mapAddChk.checked = false;
    mapAddChk.onchange();
    topFor = null;
    syncTopUi();
    syncFrameInfo(); syncLockUi(); renderCandidates(); renderPointList(); renderResult();
    renderQuality(); drawMap(); draw();
    note('', '');
    fetchLensId();
  }
  function close() {
    root.style.display = 'none';
    document.removeEventListener('keydown', onKey);
    frame = null; pts = []; solved = null; saved = null; camId = '';
  }

  return {
    open,
    close,
    isOpen: () => root.style.display !== 'none',
    /** 開いているカメラが show.json 側で消えたら閉じる（カメラ削除・リロード時の取り残し防止）。 */
    onState: () => { if (root.style.display !== 'none' && !camOf(camId)) close(); },
  };
}
