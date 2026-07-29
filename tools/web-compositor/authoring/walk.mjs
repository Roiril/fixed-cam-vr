// 「体験者がコースを 3 周する」歩きを機械的に作る。
//
//   卓の ▶ 検証はマウスでドットを引きずるので、同じ歩きを二度作れない。ここで作る歩きは
//   決定的なので、演出を直すたびに**同じ条件**で回して差だけを見られる。
//
//   歩き方のモデル: 体験者は L 字壁の外側を回る（企画書「右手で壁をたどりながら移動する」）。
//   ゾーンの中心を直線で結ぶと**部屋の真ん中を突っ切って別のゾーンを踏む**ので、
//   すべての通過点を床の外周リング（内側に inset だけ入った正方形）へ投影して周回路を作る。
//
//   ⚠ これは「ロジックの検証」であって「体感の検証」ではない（卓のシミュレータと同じ限界）。
//     実際の人は等速では歩かないし、ゾーンの中心に立ち止まりもしない。

import { zonesFromLayout } from '../zone-layout.js';

/** カメラ index → そのカメラの塗り面積の重心（course 空間）。 */
export function zoneCentroids(layout) {
  const { boxes } = zonesFromLayout(layout || {});
  const acc = new Map();
  for (const b of boxes) {
    const w = 4 * b.hx * b.hz;                       // 矩形の面積
    const cur = acc.get(b.camera) || { x: 0, z: 0, w: 0 };
    acc.set(b.camera, { x: cur.x + b.cx * w, z: cur.z + b.cz * w, w: cur.w + w });
  }
  return new Map([...acc].map(([cam, v]) => [cam, { x: v.x / v.w, z: v.z / v.w }]));
}

/** 塗られている床全体の半径（内接正方形の半辺）。 */
function floorHalf(layout) {
  const { boxes } = zonesFromLayout(layout || {});
  let hx = 0; let hz = 0;
  for (const b of boxes) { hx = Math.max(hx, Math.abs(b.cx) + b.hx); hz = Math.max(hz, Math.abs(b.cz) + b.hz); }
  return Math.min(hx, hz) || 0.9;
}

/** 点を外周リング（max(|x|,|z|) = r）へ押し出す。中心付近の点は方向が不安定なので既定を返す。 */
const toRing = (p, r, fallback) => {
  const m = Math.max(Math.abs(p.x), Math.abs(p.z));
  if (m < 1e-3) return { ...fallback };
  const k = r / m;
  return { x: p.x * k, z: p.z * k };
};

/**
 * コースを laps 周する歩きを作る。
 *   dwellSec  … 1 区間に立ち止まっている時間
 *   travelSec … 区間から区間への移動にかける時間（外周を回るので 2 本の脚に分けて使う）
 *   ⇒ 1 区間あたりの実効滞在 ≒ dwellSec + travelSec（企画書の「各周 30 秒」= 3 区間 × 10 秒）
 *
 * 返り値の samples は scenario-engine 形式 [{ tMs, x, z }]。
 */
export function buildWalk({
  layout, order, laps = 3, dwellSec = 7.5, travelSec = 2.5, tailSec = 6, tickMs = 20,
}) {
  const centroids = zoneCentroids(layout);
  const route = (order || []).filter((c) => centroids.has(c));
  if (route.length < 2) throw new Error('コース（layout.course.order）が 2 区間未満です');
  // 塗られていないカメラを黙って落とすと、周回カウンタ（order の全要素で進む）だけが進まなくなり、
  // 「2・3 周目の演出が全部出ない」という無関係に見える結果になる。ここで止めて原因を言う。
  if (route.length !== (order || []).length) {
    const missing = (order || []).filter((c) => !centroids.has(c));
    throw new Error(`コースにあるカメラ ${missing.join(', ')} のゾーンが塗られていません`
      + '（フロアマップの 🖌 塗る で塗るか、周回コースから外してください）');
  }

  const r = Math.max(0.2, floorHalf(layout) - 0.15);
  const stand = new Map(route.map((cam) => [cam, toRing(centroids.get(cam), r, centroids.get(cam))]));
  // 区間から区間への曲がり角（2 点の中間を外周へ押し出したもの）。
  const corner = (a, b) => toRing(
    { x: (stand.get(a).x + stand.get(b).x) / 2, z: (stand.get(a).z + stand.get(b).z) / 2 },
    r, stand.get(b),
  );

  const samples = [];
  let tMs = 0;
  const push = (p) => samples.push({ tMs, x: p.x, z: p.z });

  // スタート区間に立っている状態から始める（導入が終わった直後 = 本編の開始時点）。
  push(stand.get(route[0]));
  tMs += Math.round(dwellSec * 1000);
  push(stand.get(route[0]));

  let from = route[0];
  const legs = [];
  for (let lap = 0; lap < laps; lap++) {
    for (let i = 1; i < route.length; i++) legs.push(route[i]);
    legs.push(route[0]);            // 1 周の締め（ここで周回カウンタが上がる）
  }

  for (const to of legs) {
    const half = Math.round((travelSec * 1000) / 2);
    tMs += half; push(corner(from, to));      // 曲がり角まで（外周をなぞる）
    tMs += half; push(stand.get(to));         // 次の区間の立ち位置へ
    tMs += Math.round(dwellSec * 1000); push(stand.get(to));
    from = to;
  }

  tMs += Math.round(tailSec * 1000);
  push(stand.get(route[0]));

  return { samples, tickMs, endMs: tMs, stand, ringRadius: r };
}
