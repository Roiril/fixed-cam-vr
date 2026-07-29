// 較正パネルの**判断**（DOM を持たない純関数）。描画とイベントは calib-ui.js。
//
// なぜ分けたか: 状態遷移（開く / フレームを取り直す / 打つ / 解く / 保存）が DOM ハンドラの中に
// 埋まっていたため、**一度もテストを通っていなかった**。2026-07-29 の監査で出た実バグ 5 件は
// すべてこの空白から出ている（解像度が変わっても固定画角を捨てない・映像前に開くと点が復元されない・
// 壁の高さの後変更が既存点に届かない・失敗直後に古い線が残る・卓と実機で有効判定が違う）。
// 判断をここへ出せば `calib-session.test.mjs` が守れる。
//
// 数学は calib.js（平面ホモグラフィ・LM・投影）。解の正しさはここでは判断しない。

import {
  projectPoint, unprojectToFloor, calibrateFromFloorPoints, calibQualityLabel,
  MIN_POINTS_FOR_K1, MIN_RAISED_FOR_K1, hasCollinearTriple, isDegenerate,
} from './calib.js';
import { linesFromLayout, parseGridCells } from './zone-layout.js';
import { camColor } from './common.js';
import { roomCorners } from './floor-sketch.js';

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

/** `layout.floor` が実測されているか（既定 1.8×1.8 のままなら測っていない疑い）。 */
export function floorLooksDefault(layout) {
  const f = layout?.floor;
  if (!f || !(f.w > 0) || !(f.d > 0)) return true;
  return Math.abs(f.w - 1.8) < 1e-6 && Math.abs(f.d - 1.8) < 1e-6;
}

/** 同一点判定のキー（mm 丸め）。course 座標の浮動小数差で重複が漏れるのを防ぐ。 */
export const pointKey = (x, z) => `${x.toFixed(3)},${z.toFixed(3)}`;

/**
 * 候補点の**出典**。「実物が現場にあるか」で分ける。
 *
 * これが要る理由: `layout.wall` の既定値（1m×1m の L）や `layout.grid` の定数格子
 * （0.15m × 12×12）から作った点は、**現場の床にその目印が無い**。座標は綺麗な数字なので
 * 打てば解は出るが、実物と違う場所を指しているので合わない。
 * 「打った点が悪い」ようにしか見えないまま何度でも失敗する（2026-07-29 の実害）。
 *
 *   measured — 作者が実物を測って置いたもの（印・実測した部屋のプロキシ）
 *   assumed  — 卓の既定値・定数から生成したもの（未測定の壁・床・タイル格子）
 */
export const SOURCE_MEASURED = 'measured';
export const SOURCE_ASSUMED = 'assumed';

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
 * 各点は `source`（measured / assumed）を持つ。UI は既定で measured だけを出す。
 *
 * @param {object|null} layout show.json の layout
 * @param {{x:number,z:number}[]} [used] 既に打った点（同じ点を二度出さない）
 * @returns {{key:string, x:number, z:number, kind:string, label:string, source:string}[]}
 */
export function candidatePoints(layout, used = []) {
  const out = [];
  const seen = new Set((used || []).filter((p) => p && Number.isFinite(p.x) && Number.isFinite(p.z))
    .map((p) => pointKey(p.x, p.z)));
  const push = (x, z, kind, label, source) => {
    if (!Number.isFinite(x) || !Number.isFinite(z)) return;
    const key = pointKey(x, z);
    if (seen.has(key)) return;
    seen.add(key);
    out.push({ key, x, z, kind, label, source });
  };

  // 作者が地図に置いた印（`layout.calibPoints`）。**位置合わせ点のコピーではなく別の集合**で、
  // 現場の床に実物の目印があるものだけを置く（ラベル必須）。候補としては union で並べ、
  // どちらかへコピーはしない（2 コピーは必ずずれる）。
  for (const p of Array.isArray(layout?.calibPoints) ? layout.calibPoints : []) {
    if (!p) continue;
    push(p.x, p.z, 'mark', p.label ? `印・${p.label}` : '印', SOURCE_MEASURED);
  }

  // 位置合わせ点は現場の床に × 印テープが貼ってある＝実物がある。ただし既定 2 点への
  // フォールバックは「置いていない」ので assumed。
  const authored = Array.isArray(layout?.regPoints) && layout.regPoints.length;
  const regSrc = authored ? layout.regPoints : DEFAULT_REG_POINTS;
  regSrc.forEach((p, i) => {
    if (!p) return;
    push(p.x, p.z, 'reg', `位置合わせ点 ${i + 1}${p.label ? `・${p.label}` : ''}`,
      authored ? SOURCE_MEASURED : SOURCE_ASSUMED);
  });

  const w = layout?.wall;
  const wallSrc = wallLooksDefault(layout) ? SOURCE_ASSUMED : SOURCE_MEASURED;
  if (w && Array.isArray(w.corner) && Array.isArray(w.endX) && Array.isArray(w.endZ)) {
    push(w.corner[0], w.corner[1], 'room', '壁の外角', wallSrc);
    push(w.endX[0], w.endX[1], 'room', '壁の先端（東側）', wallSrc);
    push(w.endZ[0], w.endZ[1], 'room', '壁の先端（南側）', wallSrc);
  }
  const f = layout?.floor;
  const floorSrc = floorLooksDefault(layout) ? SOURCE_ASSUMED : SOURCE_MEASURED;
  if (f && f.w > 0 && f.d > 0) {
    const hw = f.w / 2, hd = f.d / 2;
    push(-hw, hd, 'room', '床の角（北西）', floorSrc);
    push(hw, hd, 'room', '床の角（北東）', floorSrc);
    push(hw, -hd, 'room', '床の角（南東）', floorSrc);
    push(-hw, -hd, 'room', '床の角（南西）', floorSrc);
  }
  // 部屋のプロキシ（🧱 で著作した壁の端・箱の角）。**映像で最も見つけやすい物理特徴**で、
  // しかも床の上にあるので較正に使える。著作しない限り存在しないので必ず実測。
  for (const c of roomCorners(layout)) push(c.x, c.z, 'room', c.label, SOURCE_MEASURED);

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
        // タイルは**ゾーンを塗るための仮想の格子**で、床に線は引かれていない。
        push(-hw + c * g.tileM, hd - r * g.tileM, 'grid', 'タイルの角', SOURCE_ASSUMED);
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
 * **実測していない幾何は描かない**（`opts.includeAssumed` で明示的に許したときだけ描く）。
 * ワイヤー重畳は較正の唯一の反証可能な一次証拠なので、そこに実在しない部屋の絵が混ざると
 * 検証そのものが成立しない。
 *
 * @param {object|null} layout
 * @param {{gridStepM?:number, wallH?:number, includeAssumed?:boolean}} [opts]
 * @returns {{a:number[], b:number[], kind:string, source:string, color?:string}[]} a/b は [x,y,z]
 */
export function wireSegments(layout, opts = {}) {
  const step = opts.gridStepM > 0 ? opts.gridStepM : 0.3;
  const wallH = opts.wallH > 0 ? opts.wallH : 1.0;
  const allowAssumed = opts.includeAssumed !== false;
  const segs = [];
  const add = (a, b, kind, source, color) => {
    if (source === SOURCE_ASSUMED && !allowAssumed) return;
    segs.push(color ? { a, b, kind, source, color } : { a, b, kind, source });
  };

  // 床の外周（これが実際の床の縁と合わないなら、姿勢より先に layout.floor を疑う）。
  // 実測していなければ 1.8m 四方の**捏造**なので、既定では引かない。
  const hasFloor = layout?.floor && layout.floor.w > 0 && layout.floor.d > 0;
  const f = hasFloor ? layout.floor : { w: 1.8, d: 1.8 };
  const floorSrc = floorLooksDefault(layout) ? SOURCE_ASSUMED : SOURCE_MEASURED;
  const hw = f.w / 2, hd = f.d / 2;
  add([-hw, 0, -hd], [hw, 0, -hd], 'floor', floorSrc);
  add([hw, 0, -hd], [hw, 0, hd], 'floor', floorSrc);
  add([hw, 0, hd], [-hw, 0, hd], 'floor', floorSrc);
  add([-hw, 0, hd], [-hw, 0, -hd], 'floor', floorSrc);

  // 内側の格子。外周と重ならないよう内側だけ引く（整数カウンタで刻む＝誤差を溜めない）。
  for (let i = 1; -hw + i * step < hw - 1e-6; i++) {
    const x = -hw + i * step;
    add([x, 0, -hd], [x, 0, hd], 'grid', floorSrc);
  }
  for (let i = 1; -hd + i * step < hd - 1e-6; i++) {
    const z = -hd + i * step;
    add([-hw, 0, z], [hw, 0, z], 'grid', floorSrc);
  }

  // L 字壁（床の線・高さ wallH の上端・両端と外角の垂直線）。
  // **垂直線が実物の柱と重なるか**が、床だけでは分からない高さ方向のずれを暴く。
  const w = layout?.wall;
  const wallSrc = wallLooksDefault(layout) ? SOURCE_ASSUMED : SOURCE_MEASURED;
  if (w && Array.isArray(w.corner) && Array.isArray(w.endX) && Array.isArray(w.endZ)) {
    const chain = [w.endZ, w.corner, w.endX];
    for (let i = 0; i + 1 < chain.length; i++) {
      const p = chain[i], q = chain[i + 1];
      add([p[0], 0, p[1]], [q[0], 0, q[1]], 'wall', wallSrc);
      add([p[0], wallH, p[1]], [q[0], wallH, q[1]], 'wall', wallSrc);
    }
    for (const p of chain) add([p[0], 0, p[1]], [p[0], wallH, p[1]], 'wall', wallSrc);
  }

  // 通過ライン（演出の発火点）。担当カメラ色で描くとフロアマップと同じ見え方になる。
  // 作者が引いた線なので実測扱い。
  for (const l of linesFromLayout(layout)) {
    add([l.x1, 0, l.z1], [l.x2, 0, l.z2], 'line', SOURCE_MEASURED,
      l.camera >= 0 ? camColor(l.camera) : '#fffaf0');
  }
  return segs;
}

/**
 * 検証ワイヤーとして信用できる幾何が 1 本でもあるか。
 * 無ければ「線を引かない」ではなく「**測ってから来てください**」と言うべき局面。
 */
export function hasMeasuredGeometry(layout) {
  return wireSegments(layout, { includeAssumed: false }).length > 0;
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
 * 復元した点に「何の目印か」を戻す。
 *
 * `refs` は座標しか持たない（Unity と共有する DTO を太らせない）。名前を焼かずに
 * **layout から引き直す**のは、layout を直したら名前もそれに従うのが正しいため。
 * 引けない点（消した印・手入力）は座標そのものを名前にする — 番号だけだと
 * 「どの点を直せばいいか」が現場で分からない。
 */
export function resolvePointLabels(pts, layout) {
  const byKey = new Map(candidatePoints(layout, []).map((c) => [c.key, c]));
  return (pts || []).map((p) => {
    if (p.label) return p;
    const c = byKey.get(pointKey(p.x, p.z));
    const base = c ? c.label : `(${p.x.toFixed(2)}, ${p.z.toFixed(2)})`;
    return { ...p, label: p.y > 0 ? `${base} の上端` : base };
  });
}

/**
 * この較正はいま流れている映像に対して有効か。
 * **Unity の `ShowCameraCalibDef.MatchesSource` と同じ判定**（解像度 + レンズ ID）。
 * ここで lensId を見落とすと、卓は「🎯 較正済み」と言うのに実機だけ黙って概算姿勢へ落ちる
 * （＝理由の分からない「人形が出ない / 場所が違う」になる）。
 *
 * @param {object|null} calib
 * @param {number} w @param {number} h いまのフレーム実寸
 * @param {string} [lensId] いま流れている映像の /info レンズ ID（不明なら空）
 */
export function calibMatchesSource(calib, w, h, lensId = '') {
  if (!calib || !(calib.fxPx > 1)) return false;
  // 両方が非空のときだけ照合する（Unity 側と同じ規則。/info を持たない配信アプリは普通にある）
  if (calib.lensId && lensId && calib.lensId !== lensId) return false;
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

/** 水平画角 (度) → 焦点距離 (px)。レンズ登録の逆算に使う。 */
export function focalFromHfov(hfovDeg, w) {
  if (!(hfovDeg > 1) || !(hfovDeg < 179) || !(w > 0)) return 0;
  return w / 2 / Math.tan(hfovDeg * Math.PI / 180 / 2);
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
 * カメラ列の 📐 欄に出すバッジ。srcW/srcH/lensId を渡すと、いま流れている映像との食い違いも見せる。
 * 食い違ったまま黙っていると、Unity 側で較正が無効化されて**理由の分からない「人形が出ない」**になる。
 */
export function calibBadgeText(cam, srcW, srcH, lensId = '') {
  const c = cam && cam.calib;
  if (!c || !(c.fxPx > 1)) return '';
  const when = c.solvedAtIso ? formatSolvedAt(c.solvedAtIso) : '';
  const acc = Number.isFinite(c.accuracyM) && c.accuracyM > 0 ? `・ずれ ${Math.round(c.accuracyM * 100)}cm` : '';
  const head = `🎯 較正済み（誤差 ${Number.isFinite(c.rmsPx) ? c.rmsPx.toFixed(1) : '?'}px${acc}${when ? `・${when}` : ''}）`;
  if (c.lensId && lensId && c.lensId !== lensId) {
    return `${head} ⚠ レンズ ${c.lensId} 用（いまは ${lensId}）— 解き直しが必要`;
  }
  if (!calibMatchesSource(c, srcW, srcH, lensId)) {
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

/**
 * 較正を別の解像度へ移す。**同じ画角の映像をリサイズしただけなら厳密に正しい**
 * （ピンホールの内部行列は解像度に比例し、歪み k1 は正規化半径なので不変、姿勢は不変）。
 *
 * これが要るのは、現場で撮っておいた映像（プレート）が配信そのままの寸法とは限らないため
 * （Quest 経由の記録は 480×360、配信は 640×480）。移せないなら null を返す — 縦横比が違う
 * のは「リサイズ」ではなくクロップなので、比例で移すと**黙って嘘の較正になる**。
 */
export function rescaleCalib(calib, w, h) {
  if (!calib || !(calib.fxPx > 1) || !(w > 1) || !(h > 1)) return null;
  if (!(calib.srcW > 1) || !(calib.srcH > 1)) return null;
  if (calib.srcW === w && calib.srcH === h) return { ...calib };
  const sx = w / calib.srcW, sy = h / calib.srcH;
  if (Math.abs(sx / sy - 1) > 0.01) return null;
  return {
    ...calib,
    fxPx: calib.fxPx * sx,
    fyPx: (calib.fyPx || calib.fxPx) * sy,
    cxPx: calib.cxPx * sx,
    cyPx: calib.cyPx * sy,
    srcW: w, srcH: h,
    rmsPx: Number.isFinite(calib.rmsPx) ? calib.rmsPx * sx : calib.rmsPx,
  };
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

// ---- 点の質（解く前に言えるだけ言う）-----------------------------------------

/**
 * 打った点の**質**を、解く前に言えるだけ言う。
 *   実測（calib.test.mjs）で分かっている効き方:
 *     画角を推定するなら床 6 点以上（4〜5 点だと歪みを f に吸わせた偽解に落ちる）
 *     画角を固定するなら床 4 点でよいが、どの 3 点も一直線でないこと
 *     高さの点が 2 本あれば平面の縮退が解けるので、床が少なくても歪みまで解ける
 *     床で散っていても**画面の隅に固まっていれば**解は暴れる（両方を見る）
 *
 * ⚠ 一直線・広がりの判定は**床の点だけ**で行う。高さの点は基準の床点と (x,z) が同一なので、
 * 混ぜると `hasCollinearTriple` が必ず true になり「正しく打っているのに点をずらせ」と言う
 * （2026-07-29 監査で発見。node で実測: 床 4 点 + 高さ 1 点 → collinear:true）。
 *
 * @param {{x:number,z:number,u:number,v:number,y?:number}[]} pts
 * @param {number} frameW @param {number} frameH
 * @param {boolean} focalLocked 画角を固定して解くか
 */
export function pointQuality(pts, frameW, frameH, focalLocked) {
  const all = pts || [];
  const floor = all.filter((p) => !(p.y > 0));
  const nRaised = all.length - floor.length;
  const n = floor.length;
  // 歪みまで解ける点数。高さの点が 2 本あれば平面の縮退が解けるので床 4 点で足りる。
  const need = (focalLocked || nRaised >= MIN_RAISED_FOR_K1) ? 4 : MIN_POINTS_FOR_K1;
  const span = (arr) => (arr.length ? Math.max(...arr) - Math.min(...arr) : 0);
  const floorSpread = n >= 2
    ? Math.hypot(span(floor.map((p) => p.x)), span(floor.map((p) => p.z))) : 0;
  const imgDiag = Math.hypot(frameW || 0, frameH || 0) || 1;
  // 画面の広がりは高さの点も含めて見る（縦エッジは画の上下を使うので、その寄与は本物）。
  const imgSpread = all.length >= 2
    ? Math.hypot(span(all.map((p) => p.u)), span(all.map((p) => p.v))) / imgDiag : 0;
  const collinear = n >= 3 && hasCollinearTriple(floor);
  const degenerate = n >= 3 && isDegenerate(floor);

  const issues = [];
  // 「あと何点で解けるか」は見出し（qualityHeadline）が言う。ここで繰り返さない
  // （旧実装は「6 点まであと 6」と「あと 4 点（最低 4 点）」を並べて出していた）。
  if (n >= 4 && n < need) issues.push(`レンズ歪みまで解くなら床があと ${need - n} 個`
    + `（${need} 個以上、または高さの点を ${MIN_RAISED_FOR_K1} 本、または画角を固定）`);
  if (degenerate) issues.push('床の点が一直線に近い（部屋の広がりを使って離す）');
  else if (collinear && n <= 5) issues.push('床の 3 点が一直線に並んでいる（4 点ちょうどだと解けない）');
  if (n >= 4 && imgSpread < 0.35) issues.push('画面の中で点が固まっている（画の端まで使う）');
  if (n >= 4 && floorSpread < 0.6) issues.push('床の上で点が近すぎる（離れた場所の点を混ぜる）');

  return {
    n, nRaised, need, floorSpread, imgSpread, collinear, degenerate, issues,
    ready: n >= 4 && !degenerate && !(n === 4 && collinear),
  };
}

/** 品質欄の見出し（1 行）。**同じことを 2 度言わない** — 詳細は issues 側だけが言う。 */
export function qualityHeadline(qy) {
  const head = `床 ${qy.n} 点 + 高さ ${qy.nRaised} 点`;
  if (qy.ready) return `${head} / 解けます`;
  if (qy.n < 4) return `${head} / あと ${4 - qy.n} 点で解けます`;
  return `${head} / このままでは解けません`;
}

// ---- 状態遷移（DOM を持たない判断）------------------------------------------

/**
 * パネルを開いたときの初期状態。
 *
 * 固定できる画角の供給源は **割り当てレンズ > このカメラの前回の解**。レンズを先に見るのは、
 * レンズの方が「測定条件が分かっている値」だから（前回の解は 4 点でフリーに解いた値かもしれない）。
 *
 * @param {object|null} cam show.json の cameras[i]
 * @param {{w:number,h:number}|null} frame 静止フレームの実寸（映像が来ていなければ null）
 * @param {object|null} layout
 * @param {object|null} [lens] このカメラに割り当てられたレンズ
 * @returns {{saved:object|null, pts:object[], lockedFocalPx:number, lockFocal:boolean,
 *            focalFrom:'lens'|'last'|'', restoredCount:number, needFrame:boolean}}
 */
export function openSession(cam, frame, layout, lens = null) {
  const saved = (cam?.calib && cam.calib.fxPx > 1) ? { ...cam.calib } : null;
  // 前回の対応点を復元する（「ずれた点だけ直して解き直す」が現場の通常運転）。
  const pts = frame
    ? resolvePointLabels(pointsFromRefs(saved?.refs, frame.w, frame.h), layout) : [];
  const fromLens = frame ? lensFocalFor(lens, frame.w, frame.h) : 0;
  const fromLast = frame ? lockableFocalPx(saved, frame.w, frame.h) : 0;
  const lockedFocalPx = fromLens || fromLast;
  return {
    saved,
    pts,
    lockedFocalPx,
    lockFocal: lockedFocalPx > 0,
    focalFrom: fromLens ? 'lens' : (fromLast ? 'last' : ''),
    restoredCount: pts.length,
    // 映像が来る前に開いた（＝復元できなかった）。🔄 で取り直したときに復元し直す必要がある。
    needFrame: !frame && !!(saved?.refs || []).length,
  };
}

/**
 * 🔄 フレームを取り直したときに何を保ち、何を捨てるか。
 *
 * 3 つの経路がある:
 *   同じ寸法       — 点はそのまま。**映像前に開いて復元できていなければ、ここで復元する**
 *   同じ縦横比     — 点の画素座標を比例で移す（画角が同じなら対応は保たれる）
 *   縦横比が違う   — 画素座標は意味を失う。保存済み refs から引き直し、無ければ捨てる
 *
 * 解像度が変わったら **固定画角も捨てる**（px 焦点距離は解像度に従属する）。旧実装は
 * `lockableFocalPx(...) || lockedFocalPx` と書いていたため旧値が残り、**別解像度の焦点距離で
 * 解いた嘘の解が「画角固定済み」として保存されて**実機で人形が別の場所に立った。
 *
 * @param {{frame:{w,h}|null, pts:object[], lockFocal:boolean}} prev
 * @param {{w:number,h:number}} frame 新しいフレーム
 * @param {object|null} saved 保存済み calib
 * @param {object|null} layout
 * @param {object|null} [lens] 割り当てレンズ（あれば固定画角の第一供給源）
 */
export function refreshSession(prev, frame, saved, layout, lens = null) {
  const old = prev?.frame || null;
  const oldPts = prev?.pts || [];
  const lockedFocalPx = lensFocalFor(lens, frame.w, frame.h) || lockableFocalPx(saved, frame.w, frame.h);
  const keepLock = lockedFocalPx > 0 && !!prev?.lockFocal;

  const sameSize = old && old.w === frame.w && old.h === frame.h;
  if (sameSize) {
    // 映像が来る前に開いていて点が空なら、ここで復元する（旧実装は open() でしか復元せず、
    // LIVE 前に開いた作業者は全点を打ち直していた）。
    if (!oldPts.length) {
      const pts = resolvePointLabels(pointsFromRefs(saved?.refs, frame.w, frame.h), layout);
      return { pts, lockedFocalPx, lockFocal: lockedFocalPx > 0, restored: pts.length, resized: false, dropped: 0 };
    }
    return { pts: oldPts, lockedFocalPx, lockFocal: keepLock, restored: 0, resized: false, dropped: 0 };
  }

  const sameAspect = old && Math.abs((old.w / old.h) / (frame.w / frame.h) - 1) < 0.01;
  if (sameAspect && oldPts.length) {
    const sx = frame.w / old.w, sy = frame.h / old.h;
    return {
      pts: oldPts.map((p) => ({ ...p, u: p.u * sx, v: p.v * sy })),
      lockedFocalPx, lockFocal: lockedFocalPx > 0, restored: 0, resized: true, dropped: 0,
    };
  }
  // 縦横比が変わった（4:3 → 16:9 等）。画素の対応は意味を失うので、保存済み refs から引き直す。
  const pts = resolvePointLabels(pointsFromRefs(saved?.refs, frame.w, frame.h), layout);
  return {
    pts, lockedFocalPx, lockFocal: lockedFocalPx > 0,
    restored: pts.length, resized: true, dropped: oldPts.length,
  };
}

/**
 * 復元した点が、いまの絵と合っていそうか（＝カメラを置き直していないか）。
 *
 * 較正は設営ごとに無効になるのに、鮮度の手掛かりは日時表示しか無かった。置き直した後の
 * 復元点は無関係な画素に散らばるので、**開いた瞬間に問う**のが正しい。
 * 判定は保存済み解での再投影ずれ（中央値）。ドリフトなら数 px、置き直しなら数十 px 出る。
 *
 * @returns {{suspect:boolean, medianPx:number}} 点が無ければ suspect=false
 */
export function relocateSuspicion(saved, pts, thresholdPx = 24) {
  const list = (pts || []).filter((p) => p && Number.isFinite(p.u) && Number.isFinite(p.v));
  if (!saved || list.length < 3) return { suspect: false, medianPx: 0 };
  const d = [];
  for (const p of list) {
    const q = projectPoint(saved, p.x, p.y > 0 ? p.y : 0, p.z);
    if (!q) return { suspect: true, medianPx: Number.POSITIVE_INFINITY };
    d.push(Math.hypot(q.u - p.u, q.v - p.v));
  }
  d.sort((a, b) => a - b);
  const median = d[Math.floor(d.length / 2)];
  return { suspect: median > thresholdPx, medianPx: median };
}

// ---- 精度（当てはまりではなく、人形が何 cm ずれるか）--------------------------

/**
 * leave-one-out の再投影誤差。**rms とは別物**。
 *
 * rms は「打った点にどれだけ合ったか（当てはまり）」で、4 点なら誤差 0 でも解が嘘のことがある
 * （設計文書自身がそう書いている）。1 点を抜いて残りで解き、抜いた点がどれだけ外れるかを測ると
 * 「その解が**知らない点**をどれだけ当てられるか」＝実際の精度が出る。
 *
 * @returns {{ok:boolean, medianPx:number, medianM:number, worstPx:number, samples:number, reason?:string}}
 *   medianM は「その画素ずれが床の上で何 m か」（点の位置での逆投影で換算）
 */
export function leaveOneOutError(pts, w, h, opts = {}) {
  const all = (pts || []).filter((p) => p && Number.isFinite(p.u) && Number.isFinite(p.v));
  const floor = all.filter((p) => !(p.y > 0));
  // 1 点抜いても解ける（床 4 点）だけの余裕が要る。
  if (floor.length < 5) return { ok: false, medianPx: 0, medianM: 0, worstPx: 0, samples: 0,
    reason: `床の点が ${floor.length} 個では精度を測れません（5 個以上で測れます）` };

  const errsPx = [], errsM = [];
  for (let i = 0; i < all.length; i++) {
    const held = all[i];
    // 高さの点を抜いても床 4 点は残るが、床の点を抜くときだけ最小点数を割らないか確認する。
    const rest = all.filter((_, j) => j !== i);
    if (rest.filter((p) => !(p.y > 0)).length < 4) continue;
    const r = calibrateFromFloorPoints(rest, w, h, opts);
    if (!r.ok) continue;
    const q = projectPoint(r.calib, held.x, held.y > 0 ? held.y : 0, held.z);
    if (!q) continue;
    const px = Math.hypot(q.u - held.u, q.v - held.v);
    errsPx.push(px);
    // 画素 → メートル。その点の位置で 1px が床の上で何 m かを、隣接画素の逆投影で測る。
    const m = pixelToMeters(r.calib, held.u, held.v, px);
    if (Number.isFinite(m)) errsM.push(m);
  }
  if (!errsPx.length) return { ok: false, medianPx: 0, medianM: 0, worstPx: 0, samples: 0,
    reason: '点を 1 つ抜くと解けなくなります（点を増やすと精度が測れます）' };

  const med = (arr) => { const s = [...arr].sort((a, b) => a - b); return s[Math.floor(s.length / 2)]; };
  return {
    ok: true,
    medianPx: med(errsPx),
    medianM: errsM.length ? med(errsM) : 0,
    worstPx: Math.max(...errsPx),
    samples: errsPx.length,
  };
}

/**
 * 画像上の距離 (px) を、その場所の床の上での距離 (m) に直す。
 * 遠くの床ほど 1px が長い距離になるので、**その点の位置で測る**のが要点。
 */
export function pixelToMeters(calib, u, v, distPx) {
  if (!calib || !(distPx > 0)) return 0;
  const a = unprojectToFloor(calib, u, v, 0);
  if (!a) return 0;
  const b = unprojectToFloor(calib, u + distPx, v, 0);
  const c = unprojectToFloor(calib, u, v + distPx, 0);
  const dx = b ? Math.hypot(b.x - a.x, b.z - a.z) : 0;
  const dz = c ? Math.hypot(c.x - a.x, c.z - a.z) : 0;
  const vals = [dx, dz].filter((x) => x > 0);
  if (!vals.length) return 0;
  return vals.reduce((s, x) => s + x, 0) / vals.length;
}

// ---- レンズ（内部パラメータ）--------------------------------------------------
//
// なぜ独立したものにするか（設計批評 2026-07-29 の最重要指摘）:
//   画角を固定する値の供給源が「**このカメラの前回の解の fxPx**」しか無かった。
//   4 点でフリーに解いたいい加減な f が、翌日「正確な内部パラメータ」として再利用される。
//   しかも **f を固定すると rms はむしろ下がる**ので、誤りが良い数字に化けて発見できない。
//
//   レンズを独立させると 3 つが同時に解ける:
//     1. **素性が追える** — 何点で・高さの点は何本で・実際のずれは何 cm だったかを一緒に焼く
//     2. **同型機で共有できる** — iPhone 13 Pro ×2 は同じレンズ。片方で丁寧に測れば両方に効く
//     3. **運用が UI に現れる** — 「内部は一度だけ丁寧に / 外部は現場で毎回」が選択肢になる
//
// ⚠ Unity は lenses[] を読まない。解いた結果は `cameras[].calib.fxPx` に**解決済みの値**で
// 入るので、契約は広がらない（卓だけの概念）。

/** レンズ 1 件を正規化する（欠けたキーは既定値・壊れていれば null）。 */
export function normalizeLens(raw) {
  if (!raw || typeof raw !== 'object') return null;
  const id = String(raw.id || '').trim();
  const fxPx = Number(raw.fxPx);
  if (!id || !(fxPx > 1)) return null;
  const num = (v, d = 0) => (Number.isFinite(Number(v)) ? Number(v) : d);
  return {
    id,
    name: String(raw.name || id),
    fxPx,
    srcW: num(raw.srcW) | 0,
    srcH: num(raw.srcH) | 0,
    k1: num(raw.k1),
    measuredAtIso: String(raw.measuredAtIso || ''),
    pointCount: num(raw.pointCount) | 0,
    raisedCount: num(raw.raisedCount) | 0,
    rmsPx: num(raw.rmsPx),
    accuracyM: num(raw.accuracyM),
  };
}

/** show.json の lenses[] を正規化した配列にする。 */
export function lensesOf(show) {
  return (Array.isArray(show?.lenses) ? show.lenses : []).map(normalizeLens).filter(Boolean);
}

/** このカメラに割り当てられたレンズ。未割当・見つからないなら null。 */
export function resolveLens(lenses, cam) {
  const ref = String(cam?.lensRef || '').trim();
  if (!ref) return null;
  return (lenses || []).find((l) => l && l.id === ref) || null;
}

/**
 * このレンズの焦点距離を、いまの映像で固定値として使ってよいか。
 * **解像度が違えば使えない**（px 焦点距離は解像度に従属する）。
 * ここで黙って流用すると、画角が 2 倍ずれた解を「固定したから正確」と誤認する。
 */
export function lensFocalFor(lens, w, h) {
  if (!lens || !(lens.fxPx > 1)) return 0;
  if (!(w > 1) || !(h > 1)) return 0;
  if (lens.srcW && lens.srcH && (lens.srcW !== w || lens.srcH !== h)) return 0;
  return lens.fxPx;
}

/** 既存 id と衝突しない新しいレンズ id。 */
export function nextLensId(lenses) {
  const used = new Set((lenses || []).map((l) => l && l.id));
  for (let i = 1; i < 999; i++) {
    const id = `lens_${i}`;
    if (!used.has(id)) return id;
  }
  return `lens_${Date.now()}`;
}

/**
 * 解いた結果からレンズを作る。**測定条件を一緒に焼く**のが要点で、
 * これが無いと「素性の分からない f」が同型機へ伝播する。
 *
 * @param {object} result calibrateFromFloorPoints の戻り（ok）
 * @param {{id:string, name:string, accuracyM?:number}} meta
 */
export function lensFromSolve(result, meta) {
  if (!result || !result.ok || !result.calib) return null;
  const c = result.calib;
  return normalizeLens({
    id: meta.id,
    name: meta.name,
    fxPx: c.fxPx,
    srcW: c.srcW,
    srcH: c.srcH,
    k1: c.k1 || 0,
    measuredAtIso: c.solvedAtIso || '',
    pointCount: (c.pointCount || 0) - (result.raisedCount || 0),
    raisedCount: result.raisedCount || 0,
    rmsPx: c.rmsPx,
    accuracyM: Number.isFinite(meta.accuracyM) ? meta.accuracyM : (c.accuracyM || 0),
  });
}

/** レンズを追加 / 差し替えた新しい配列（元は書き換えない）。 */
export function upsertLens(lenses, lens) {
  if (!lens) return lenses || [];
  const list = (lenses || []).filter(Boolean);
  const i = list.findIndex((l) => l.id === lens.id);
  if (i < 0) return [...list, lens];
  const next = [...list];
  next[i] = lens;
  return next;
}

/** レンズを消す。参照しているカメラの lensRef も同時に外さないと参照切れになる。 */
export function removeLens(lenses, lensId) {
  return (lenses || []).filter((l) => l && l.id !== lensId);
}

/** カメラにレンズを割り当てた新しい配列（空文字で解除）。 */
export function applyLensToCameras(cameras, camId, lensId) {
  return (cameras || []).map((c) => {
    if (!c || c.id !== camId) return c;
    const next = { ...c };
    if (lensId) next.lensRef = lensId;
    else delete next.lensRef;
    return next;
  });
}

/** レンズを消したとき、参照が残らないよう全カメラから外す。 */
export function detachLensFromCameras(cameras, lensId) {
  return (cameras || []).map((c) => {
    if (!c || c.lensRef !== lensId) return c;
    const next = { ...c };
    delete next.lensRef;
    return next;
  });
}

/** レンズ 1 行の説明（測定条件つき）。選ぶ前に素性が見えるようにする。 */
export function lensSummary(lens) {
  if (!lens) return '';
  const hfov = hfovFromFocal(lens.fxPx, lens.srcW);
  const parts = [
    lens.srcW ? `${lens.srcW}×${lens.srcH}` : '解像度不明',
    hfov > 0 ? `水平 ${hfov.toFixed(0)}°` : `${Math.round(lens.fxPx)}px`,
    `床 ${lens.pointCount} 点 + 高さ ${lens.raisedCount} 本`,
  ];
  if (lens.accuracyM > 0) parts.push(`ずれ ${Math.round(lens.accuracyM * 100)}cm`);
  else if (Number.isFinite(lens.rmsPx) && lens.rmsPx > 0) parts.push(`当てはまり ${lens.rmsPx.toFixed(1)}px`);
  const when = formatSolvedAt(lens.measuredAtIso);
  if (when) parts.push(when);
  return parts.join(' / ');
}

/**
 * このレンズの値をどれだけ信用してよいか。**素性の悪い f を黙って共有させない**。
 * 実測（calib.test.mjs）で分かっている効き方をそのまま条件にする。
 */
export function lensTrustIssues(lens) {
  if (!lens) return [];
  const out = [];
  if (lens.raisedCount < MIN_RAISED_FOR_K1) {
    out.push(`高さの点が ${lens.raisedCount} 本で測った画角です`
      + '（床だけだと「広げて近づける解」と「狭めて遠ざける解」が区別できません）');
  }
  if (lens.pointCount < MIN_POINTS_FOR_K1) {
    out.push(`床の点が ${lens.pointCount} 個で測った画角です`
      + `（${MIN_POINTS_FOR_K1} 個未満だと歪みを画角に吸わせた偽解に落ちることがあります）`);
  }
  if (lens.accuracyM > 0.05) {
    out.push(`測ったときの実際のずれが ${Math.round(lens.accuracyM * 100)}cm ありました`);
  }
  return out;
}

/** 精度の一行（人の言葉）。「当てはまり」と分けて言う。 */
export function accuracyLine(loo, rmsPx) {
  const fit = Number.isFinite(rmsPx) ? `当てはまり ${rmsPx.toFixed(1)}px（${calibQualityLabel(rmsPx).text}）` : '';
  if (!loo || !loo.ok) return loo?.reason ? `${fit} / 精度は測れません — ${loo.reason}` : fit;
  const cm = Math.round(loo.medianM * 100);
  const cmText = loo.medianM > 0 ? `およそ ${cm}cm` : `${loo.medianPx.toFixed(1)}px`;
  return `実際のずれ ${cmText}（1 点を伏せて解いたときの中央値・${loo.samples} 回）／ ${fit}`;
}
