// CG 人形の**輪郭プロキシ** — 「どこに・どれくらいの大きさで立つか」だけを映像の上に線で言う。
//
// ⚠ この卓は CG 人形を**描かない**（設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.1）。
//   glTF + スキニング + IK + 影を素の WebGL2 で二重実装すると、Unity と一致していることを
//   検証する手段が消える。ミラーしてよいのは**数式**であって描画器ではない。
//   → ここが出すのは足元の楕円・身長ボックス・頭頂点・向きの矢印だけ。
//   **写実に寄せた瞬間にこの線を踏み越える**（著作者がプロキシを信じて Unity 確認を飛ばす）ので、
//   半透明の輪郭・単色を規約として守ること。
//
// 幾何の出どころ:
//   - 足元の円（半径 0.2m）を床に置いて投影すると**楕円**になる。この歪み方そのものが
//     「カメラがどこからどう見ているか」を語るので、四角より円のほうが接地感を判断しやすい
//   - 身長ボックス（0.4m × 0.25m × heightM）は「人形が画角に収まるか」を見るためのもの。
//     ボックスの上辺が枠から出ていれば実機でも頭が切れる
//   - 向きの矢印は yaw のドラッグハンドルを兼ねる（数値の「向き 180°」だけでは誰も回せない）
//
// 純関数（DOM 非依存）。node --test tools/web-compositor/actor-proxy.test.mjs で固定する。
// 唯一の例外が末尾の drawActorProxy（Canvas2D へ描くだけの薄いヘルパ。ribbon と show-sim が共有する）。

import { projectPoint } from './calib.js';
import { calibMatchesSource } from './calib-ui.js';

// ---- プロキシの寸法（人体の概形。厳密さは要らないが、変えると「収まるか」の判断がずれる）----
export const FOOT_RADIUS_M = 0.2;    // 足元マーカーの半径
export const BODY_W_M = 0.4;         // 肩幅相当（ボックスの左右）
export const BODY_D_M = 0.25;        // 厚み（ボックスの前後）
export const DEFAULT_HEIGHT_M = 1.6; // actors[].heightM の既定と揃える
export const ARROW_LEN_M = 0.45;     // 向きの矢印（＝ yaw のドラッグハンドル）の長さ
const FOOT_SEGMENTS = 16;

/** pose の画角が未著作のときの水平画角。Unity `ShowCgLayer.DefaultHfovDeg` と揃えること。 */
export const DEFAULT_HFOV_DEG = 70;
/** pose の高さが未著作のときの値。Unity `ShowCameraPoseDef.y` の既定と揃えること。 */
const DEFAULT_POSE_Y = 1.2;

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const num = (v, d) => (Number.isFinite(v) ? v : d);

/**
 * 概算 pose（人がフロアマップでドラッグした値）から**擬似 calib** を作る。
 *
 * 較正（`cameras[].calib`）が無いカメラでも「だいたいここ」は言えるようにするためのもの。
 * 主点は画像中心・歪み 0・roll 0 と決め打つので、**広角レンズでは画面の端が数十 cm ずれる**。
 * 卓はこれを使ったことを必ず画面に出すこと（黙って概算で置かせると、実機で合わない理由が分からない）。
 *
 * @param {object|null} pose show.json の `cameras[].pose`
 * @param {number} srcW @param {number} srcH 映像の実寸 (px)
 * @returns {object|null} calib 互換オブジェクト（`approx:true` が付く）
 */
export function poseToCalib(pose, srcW, srcH) {
  if (!pose || typeof pose !== 'object') return null;
  const w = srcW > 1 ? srcW : 640, h = srcH > 1 ? srcH : 480;
  // 水平画角が正（旧 fovDeg は垂直で意味が食い違っていたので読まない）。
  const hfov = clamp(pose.hfovDeg > 0 ? pose.hfovDeg : DEFAULT_HFOV_DEG, 10, 170) * Math.PI / 180;
  // 正方画素 → fy = fx。Unity は hfov を映像アスペクトで垂直へ直してから Camera に渡すが、
  // その変換を展開すると fy = fx になる（HorizontalToVerticalFovDeg と同値である根拠）。
  const fx = (w / 2) / Math.tan(hfov / 2);
  return {
    x: num(pose.x, 0), y: num(pose.y, DEFAULT_POSE_Y), z: num(pose.z, 0),
    yawDeg: num(pose.yawDeg, 0), pitchDeg: num(pose.pitchDeg, 0), rollDeg: 0,
    fxPx: fx, fyPx: fx, cxPx: w / 2, cyPx: h / 2, k1: 0,
    srcW: w, srcH: h,
    approx: true,
  };
}

/**
 * このカメラで人形を投影するのに使う較正を決める。**Unity の優先順位と同じ**
 * （較正が実映像と一致していればそれ、駄目なら概算 pose、どちらも無ければ出さない）。
 *
 * @param {object|null} cam show.json の `cameras[i]`
 * @param {number} srcW @param {number} srcH いま流れている映像の実寸 (px)
 * @returns {{calib:object|null, source:'calib'|'pose'|'none', note:string}}
 *   note は卓がそのまま出せる日本語（空文字＝言うことなし）
 */
export function resolveProxyCalib(cam, srcW, srcH) {
  const c = cam && cam.calib;
  if (c && c.fxPx > 1) {
    if (calibMatchesSource(c, srcW, srcH)) return { calib: c, source: 'calib', note: '' };
    // 解像度が食い違う較正で線を引くと、ずれの原因が「解」なのか「前提」なのか分からなくなる。
    // Unity 側も同じ条件で較正を捨てるので、卓だけ合っているように見せてはいけない。
    const pose = poseToCalib(cam && cam.pose, srcW, srcH);
    return {
      calib: pose, source: pose ? 'pose' : 'none',
      note: `⚠ このカメラの較正は ${c.srcW}×${c.srcH} 用です（いまの映像は ${srcW}×${srcH}）。`
        + '実機でも無効になるので 🎯 姿勢を合わせる で解き直してください。',
    };
  }
  const pose = poseToCalib(cam && cam.pose, srcW, srcH);
  if (pose) {
    return {
      calib: pose, source: 'pose',
      note: '⚠ このカメラは較正していないので位置がずれます（🎯 姿勢を合わせる で較正すると正確になります）。',
    };
  }
  return {
    calib: null, source: 'none',
    note: '⚠ このカメラは姿勢が未著作です（📐 カメラ姿勢 で置くか 🎯 姿勢を合わせる で較正するまで、'
      + '実機でも人形は出ません）。',
  };
}

/** yaw（course +Z が 0）→ 前方・右方の単位ベクトル。Unity の Quaternion.Euler(0,yaw,0) と同じ。 */
function axes(yawDeg) {
  const r = (num(yawDeg, 0)) * Math.PI / 180;
  const s = Math.sin(r), c = Math.cos(r);
  return { fwd: [s, c], right: [c, -s] };
}

/**
 * 人形の輪郭を映像上の線分・点として返す（純関数・描画はしない）。
 *
 * @param {object|null} calib 較正（`poseToCalib` の擬似 calib でもよい）
 * @param {{x:number, z:number, yawDeg:number}} placement 立ち位置（course 空間）
 * @param {number} heightM 身長 (m)
 * @param {{srcW?:number, srcH?:number, footRadiusM?:number, bodyW?:number, bodyD?:number,
 *          segments?:number, arrowM?:number}} [opts]
 * @returns {{foot:{u,v}|null, footprint:{u,v}[], box:{u,v}[][], head:{u,v}|null,
 *            arrow:{base:{u,v}, tip:{u,v}}|null, bbox:{u0,v0,u1,v1}|null,
 *            visible:boolean, clipped:boolean, behind:boolean}}
 */
export function actorProxyGeometry(calib, placement, heightM, opts = {}) {
  const empty = {
    foot: null, footprint: [], box: [], head: null, arrow: null, bbox: null,
    visible: false, clipped: false, behind: false,
  };
  if (!calib || !(calib.fxPx > 0)) return empty;

  const w = opts.srcW > 1 ? opts.srcW : (calib.srcW > 1 ? calib.srcW : 640);
  const h = opts.srcH > 1 ? opts.srcH : (calib.srcH > 1 ? calib.srcH : 480);
  const p = placement || {};
  const px = num(p.x, 0), pz = num(p.z, 0);
  const hM = clamp(num(heightM, DEFAULT_HEIGHT_M), 0.2, 3);
  const rad = opts.footRadiusM > 0 ? opts.footRadiusM : FOOT_RADIUS_M;
  const hw = (opts.bodyW > 0 ? opts.bodyW : BODY_W_M) / 2;
  const hd = (opts.bodyD > 0 ? opts.bodyD : BODY_D_M) / 2;
  const seg = Math.max(6, Math.round(opts.segments > 0 ? opts.segments : FOOT_SEGMENTS));
  const arrowM = opts.arrowM > 0 ? opts.arrowM : ARROW_LEN_M;
  const { fwd, right } = axes(p.yawDeg);

  // 投影の成否をまとめて集計する。「1 点でも落ちた」＝ 体の一部がカメラ後方に回り込んでいる
  // ＝ 実機では画の端で切れる、なので clipped として必ず言う（黙って綺麗な輪郭を出さない）。
  let anyFailed = false;
  const seen = [];
  const proj = (x, y, z) => {
    const q = projectPoint(calib, x, y, z);
    if (!q) { anyFailed = true; return null; }
    seen.push(q);
    return q;
  };

  // 足元の中心が写らない = 人形はカメラの後ろ。ここで打ち切る（他を計算しても意味がない）。
  const foot = projectPoint(calib, px, 0, pz);
  if (!foot) return { ...empty, behind: true };
  seen.push(foot);

  const footprint = [];
  for (let i = 0; i < seg; i++) {
    const a = (i / seg) * Math.PI * 2;
    const q = proj(px + Math.cos(a) * rad, 0, pz + Math.sin(a) * rad);
    if (q) footprint.push(q);
  }

  // 身長ボックスの 8 頂点（下 4 → 上 4）。sx = 左右、sz = 前後。
  const corners = [];
  for (const yy of [0, hM]) {
    for (const [sx, sz] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
      corners.push(proj(
        px + right[0] * sx * hw + fwd[0] * sz * hd,
        yy,
        pz + right[1] * sx * hw + fwd[1] * sz * hd,
      ));
    }
  }
  const box = [];
  const edge = (a, b) => { if (corners[a] && corners[b]) box.push([corners[a], corners[b]]); };
  for (let i = 0; i < 4; i++) {
    edge(i, (i + 1) % 4);                 // 下辺
    edge(4 + i, 4 + ((i + 1) % 4));       // 上辺
    edge(i, 4 + i);                       // 垂直
  }

  const head = proj(px, hM, pz);
  const tip = proj(px + fwd[0] * arrowM, 0, pz + fwd[1] * arrowM);
  const arrow = tip ? { base: foot, tip } : null;

  // 可視判定は**投影点の外接矩形と画枠の交差**で見る。「どれかの点が枠の中」だと、
  // 人形が画面いっぱいに写って全頂点が枠外へ出た時に「画角外」と誤答する。
  let u0 = Infinity, v0 = Infinity, u1 = -Infinity, v1 = -Infinity;
  for (const q of seen) {
    if (q.u < u0) u0 = q.u;
    if (q.v < v0) v0 = q.v;
    if (q.u > u1) u1 = q.u;
    if (q.v > v1) v1 = q.v;
  }
  const bbox = seen.length ? { u0, v0, u1, v1 } : null;
  const visible = !!bbox && u1 >= 0 && v1 >= 0 && u0 <= w && v0 <= h;
  const clipped = anyFailed || (!!bbox && (u0 < 0 || v0 < 0 || u1 > w || v1 > h));

  return { foot, footprint, box, head, arrow, bbox, visible, clipped, behind: false };
}

/**
 * 「この画に人形が出るか」を人間の言葉にする（卓がそのまま出す）。
 * **黙って出ない**のが一番悪い — 著作した人形が画から消えたのに誰も気づかない、を作らない。
 */
export function proxyIssueText(geom, source) {
  if (!geom) return '';
  if (source === 'none') return '';                      // 姿勢が無い方の警告が優先（resolveProxyCalib の note）
  if (geom.behind) return '⚠ この立ち位置はカメラの後ろです（この画には写りません）';
  if (!geom.visible) return '⚠ この立ち位置は画角の外です（この画には写りません）';
  if (geom.clipped) return '⚠ 画角からはみ出します（実機でも体の一部が切れます）';
  return '';
}

// ---- 描画（Canvas2D）--------------------------------------------------------
//   純関数ではないが、ribbon（📍 画面で置く）と show-sim（▶ 検証）で**同じ絵**を出すために共有する。
//   別々に描くと、片方だけ色や太さが変わって「どっちが正しいのか」が分からなくなる。

const PROXY_COLOR = 'rgba(122, 214, 255, 0.9)';   // 実映像の暖色（#ffdead 系のワイヤー）と衝突しない寒色
const PROXY_FILL = 'rgba(122, 214, 255, 0.16)';
const PROXY_DIM = 'rgba(122, 214, 255, 0.4)';

/**
 * 輪郭プロキシを 2D コンテキストへ描く。
 * @param {CanvasRenderingContext2D} ctx
 * @param {object} geom actorProxyGeometry の戻り
 * @param {{toPx?:(q:{u,v})=>{x:number,y:number}, scale?:number, label?:string, dim?:boolean}} [opts]
 *   toPx はフレーム画素 → キャンバス画素の写像（レターボックスを挟む面で使う）。既定は恒等。
 */
export function drawActorProxy(ctx, geom, opts = {}) {
  if (!geom || (!geom.footprint.length && !geom.box.length)) return;
  const toPx = opts.toPx || ((q) => ({ x: q.u, y: q.v }));
  const s = opts.scale > 0 ? opts.scale : 1;
  const stroke = opts.dim ? PROXY_DIM : PROXY_COLOR;

  ctx.save();
  // 身長ボックス（薄い）→ 足元（濃い）の順。足元が最後なので接地位置が必ず読める。
  ctx.strokeStyle = opts.dim ? PROXY_DIM : 'rgba(122, 214, 255, 0.55)';
  ctx.lineWidth = 1 * s;
  for (const [a, b] of geom.box) {
    const p = toPx(a), q = toPx(b);
    ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(q.x, q.y); ctx.stroke();
  }

  if (geom.footprint.length >= 3) {
    ctx.beginPath();
    geom.footprint.forEach((q, i) => {
      const p = toPx(q);
      if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
    });
    ctx.closePath();
    ctx.fillStyle = PROXY_FILL; ctx.fill();
    ctx.strokeStyle = stroke; ctx.lineWidth = 2 * s; ctx.stroke();
  }

  if (geom.arrow) {
    const b = toPx(geom.arrow.base), t = toPx(geom.arrow.tip);
    ctx.strokeStyle = stroke; ctx.lineWidth = 2 * s;
    ctx.beginPath(); ctx.moveTo(b.x, b.y); ctx.lineTo(t.x, t.y); ctx.stroke();
    // 矢じりは**画面空間**で作る（course 空間で作ると、俯瞰の強いカメラで潰れて向きが読めない）。
    const a = Math.atan2(t.y - b.y, t.x - b.x);
    const len = 9 * s;
    ctx.beginPath();
    ctx.moveTo(t.x, t.y);
    ctx.lineTo(t.x - Math.cos(a - 0.4) * len, t.y - Math.sin(a - 0.4) * len);
    ctx.moveTo(t.x, t.y);
    ctx.lineTo(t.x - Math.cos(a + 0.4) * len, t.y - Math.sin(a + 0.4) * len);
    ctx.stroke();
    // 掴めることが分かる印（ここをドラッグすると向きが回る）。
    ctx.beginPath(); ctx.arc(t.x, t.y, 4 * s, 0, Math.PI * 2);
    ctx.fillStyle = stroke; ctx.fill();
  }

  if (geom.head) {
    const p = toPx(geom.head);
    ctx.beginPath(); ctx.arc(p.x, p.y, 3 * s, 0, Math.PI * 2);
    ctx.fillStyle = stroke; ctx.fill();
  }

  if (opts.label) {
    const p = toPx(geom.head || geom.footprint[0]);
    ctx.font = `bold ${11 * s}px system-ui, sans-serif`;
    ctx.textAlign = 'center'; ctx.textBaseline = 'bottom';
    ctx.fillStyle = 'rgba(10,12,16,0.75)';
    const wpx = ctx.measureText(opts.label).width + 8 * s;
    ctx.fillRect(p.x - wpx / 2, p.y - 16 * s, wpx, 14 * s);
    ctx.fillStyle = stroke;
    ctx.fillText(opts.label, p.x, p.y - 3 * s);
  }
  ctx.restore();
}
