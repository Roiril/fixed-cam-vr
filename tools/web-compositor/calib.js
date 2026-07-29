// 実カメラの較正 — 床の既知点を映像上でクリックした対応から、カメラの姿勢と画角を解く。
//
// なぜ要るか: 人形を「体験者の位置」に立てるには、実カメラと同じ位置・向き・画角の仮想カメラが要る。
// これを人が数値とドラッグで置いても絶対に合わない（2026-07-27 の監査で確定）。
//
// 手法: 床は平面なので **平面ホモグラフィ 1 枚**から解ける（Zhang, MSR-TR-98-71）。
//   1. 対応点 (course XZ) ↔ (画素 uv) から DLT でホモグラフィ H を解く（Hartley 正規化つき）
//   2. H の 2 つの直交拘束から焦点距離 f を解く（skew=0 / 主点=画像中心 / fx=fy を仮定 → 未知 1 個）
//   3. H を K^-1 で正規化して回転 R と位置 C を復元
//   4. レンズ歪み k1 を粗探索し、再投影誤差が最小のものを採る
//
// 座標・符号の規約（**Unity 側と一字一句合わせること**）:
//   - course 空間は Unity と同じ左手系。X 右 / Y 上 / Z 前。床は y=0
//   - 画像は左上原点・v は下向き
//   - u = fx * (px/pz) + cx,  v = cy - fy * (py/pz)   （p は Unity カメラ座標: Y 上・Z 前）
//     → ShowCgLayer.BuildProjectionMatrix が組む射影行列と同値（そちらのコメント参照）
//   - レンズ歪みは**除算モデル** r_u = r_d / (1 + k1 r_d^2)。順・逆に解析解があり、
//     ScreenComposite.shader の逆変換と厳密に一致する
//
// 純関数のみ（DOM 非依存）。node --test tools/web-compositor/calib.test.mjs で固定する。
// 設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.2

// ---- 小さな線形代数 ---------------------------------------------------------

const sub3 = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const norm3 = (a) => Math.hypot(a[0], a[1], a[2]);
const scale3 = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
const cross3 = (a, b) => [
  a[1] * b[2] - a[2] * b[1],
  a[2] * b[0] - a[0] * b[2],
  a[0] * b[1] - a[1] * b[0],
];
const normalize3 = (a) => {
  const n = norm3(a);
  return n > 1e-12 ? scale3(a, 1 / n) : [0, 0, 0];
};

/** 正方連立 A x = b をガウス消去（部分ピボット）で解く。特異なら null。 */
export function solveLinear(A, b) {
  const n = b.length;
  const m = A.map((row, i) => [...row, b[i]]);
  for (let col = 0; col < n; col++) {
    let piv = col;
    for (let r = col + 1; r < n; r++) if (Math.abs(m[r][col]) > Math.abs(m[piv][col])) piv = r;
    if (Math.abs(m[piv][col]) < 1e-12) return null;
    if (piv !== col) { const t = m[piv]; m[piv] = m[col]; m[col] = t; }
    const d = m[col][col];
    for (let c = col; c <= n; c++) m[col][c] /= d;
    for (let r = 0; r < n; r++) {
      if (r === col) continue;
      const f = m[r][col];
      if (f === 0) continue;
      for (let c = col; c <= n; c++) m[r][c] -= f * m[col][c];
    }
  }
  return m.map((row) => row[n]);
}

// ---- レンズ歪み（除算モデル）------------------------------------------------

/** 歪んだ正規化座標 → 理想（ピンホール）正規化座標。シェーダの逆変換と同一式。 */
export function undistortNorm(nx, ny, k1) {
  if (!k1) return [nx, ny];
  const s = 1 / (1 + k1 * (nx * nx + ny * ny));
  return [nx * s, ny * s];
}

/** 理想正規化座標 → 歪んだ正規化座標（除算モデルの解析逆）。写らない領域なら null。 */
export function distortNorm(nx, ny, k1) {
  if (!k1) return [nx, ny];
  const ru2 = nx * nx + ny * ny;
  if (ru2 < 1e-16) return [nx, ny];
  const disc = 1 - 4 * k1 * ru2;
  if (disc < 0) return null;                       // このモデルでは像を結ばない
  const ru = Math.sqrt(ru2);
  const rd = (1 - Math.sqrt(disc)) / (2 * k1 * ru);
  const s = rd / ru;
  return [nx * s, ny * s];
}

// ---- ホモグラフィ -----------------------------------------------------------

// Hartley 正規化: 重心を原点へ、平均距離を sqrt(2) へ。これが無いと 640x480 の px と
// 0.1m 単位の course 座標でスケールが 4 桁違い、DLT の条件数が悪化して解が暴れる。
function normalizeSet(pts) {
  const n = pts.length;
  let mx = 0, my = 0;
  for (const p of pts) { mx += p[0]; my += p[1]; }
  mx /= n; my /= n;
  let d = 0;
  for (const p of pts) d += Math.hypot(p[0] - mx, p[1] - my);
  d /= n;
  const s = d > 1e-12 ? Math.SQRT2 / d : 1;
  return {
    T: [[s, 0, -s * mx], [0, s, -s * my], [0, 0, 1]],
    pts: pts.map((p) => [(p[0] - mx) * s, (p[1] - my) * s]),
  };
}

const mul3 = (A, B) => A.map((row) => [0, 1, 2].map((j) => row[0] * B[0][j] + row[1] * B[1][j] + row[2] * B[2][j]));
const inv3affine = (T) => {
  // T = [[s,0,tx],[0,s,ty],[0,0,1]] 専用の逆（normalizeSet の形だけを扱う）
  const s = T[0][0], tx = T[0][2], ty = T[1][2];
  return [[1 / s, 0, -tx / s], [0, 1 / s, -ty / s], [0, 0, 1]];
};

/**
 * 平面 → 画像のホモグラフィ H を解く（[u,v,1]^T ~ H [x,z,1]^T）。
 * @param {{x:number,z:number,u:number,v:number}[]} pts 4 点以上
 * @returns {number[][]|null} 3x3（h22=1 で正規化）。退化なら null
 */
export function solveHomography(pts) {
  if (!pts || pts.length < 4) return null;
  const src = normalizeSet(pts.map((p) => [p.x, p.z]));
  const dst = normalizeSet(pts.map((p) => [p.u, p.v]));

  // 8 未知（h22=1 固定）。点が 4 個なら正方、5 個以上は正規方程式で最小二乗。
  const rows = [];
  const rhs = [];
  for (let i = 0; i < pts.length; i++) {
    const [x, z] = src.pts[i];
    const [u, v] = dst.pts[i];
    rows.push([x, z, 1, 0, 0, 0, -u * x, -u * z]); rhs.push(u);
    rows.push([0, 0, 0, x, z, 1, -v * x, -v * z]); rhs.push(v);
  }
  let h;
  if (rows.length === 8) {
    h = solveLinear(rows, rhs);
  } else {
    const ata = Array.from({ length: 8 }, () => new Array(8).fill(0));
    const atb = new Array(8).fill(0);
    for (let r = 0; r < rows.length; r++) {
      for (let i = 0; i < 8; i++) {
        atb[i] += rows[r][i] * rhs[r];
        for (let j = 0; j < 8; j++) ata[i][j] += rows[r][i] * rows[r][j];
      }
    }
    h = solveLinear(ata, atb);
  }
  if (!h || h.some((v) => !Number.isFinite(v))) return null;

  const Hn = [[h[0], h[1], h[2]], [h[3], h[4], h[5]], [h[6], h[7], 1]];
  // 正規化を解く: H = Tdst^-1 * Hn * Tsrc
  const H = mul3(mul3(inv3affine(dst.T), Hn), src.T);
  const s = H[2][2];
  if (!Number.isFinite(s) || Math.abs(s) < 1e-12) return null;
  return H.map((row) => row.map((v) => v / s));
}

// ---- 姿勢・焦点距離の復元 ---------------------------------------------------

/**
 * ホモグラフィから焦点距離を解く（Zhang の 2 直交拘束・fx=fy / skew=0 / 主点固定）。
 * 退化配置（床にほぼ正対など）では拘束が縮退するので、成立した式だけを平均する。
 * @returns {number|null} 焦点距離 (px)
 */
export function focalFromHomography(H, cxPx, cyPx) {
  // K' = [[f,0,cx],[0,-f,cy],[0,0,1]]（v が下向きなので fy に負号が入る）。
  // K'^-1 h = [(hx - cx hz)/f, -(hy - cy hz)/f, hz] なので、内積の f^-2 項は符号が揃う。
  const col = (j) => [H[0][j], H[1][j], H[2][j]];
  const h1 = col(0), h2 = col(1);
  const a1 = h1[0] - cxPx * h1[2], b1 = h1[1] - cyPx * h1[2];
  const a2 = h2[0] - cxPx * h2[2], b2 = h2[1] - cyPx * h2[2];

  const cands = [];
  // 拘束 1: (K^-1 h1)·(K^-1 h2) = 0
  const d12 = h1[2] * h2[2];
  if (Math.abs(d12) > 1e-12) {
    const f2 = -(a1 * a2 + b1 * b2) / d12;
    if (f2 > 1) cands.push(Math.sqrt(f2));
  }
  // 拘束 2: |K^-1 h1| = |K^-1 h2|
  const dd = h2[2] * h2[2] - h1[2] * h1[2];
  if (Math.abs(dd) > 1e-12) {
    const f2 = ((a1 * a1 + b1 * b1) - (a2 * a2 + b2 * b2)) / dd;
    if (f2 > 1) cands.push(Math.sqrt(f2));
  }
  if (!cands.length) return null;
  return cands.reduce((s, v) => s + v, 0) / cands.length;
}

/**
 * ホモグラフィと焦点距離から、カメラの回転行列（ローカル→ワールド）と位置を復元する。
 * @returns {{R:number[][], C:number[]}|null} R は行優先 3x3、C は course 空間の位置
 */
export function poseFromHomography(H, f, cxPx, cyPx) {
  if (!(f > 0)) return null;
  const col = (j) => [H[0][j], H[1][j], H[2][j]];
  // K'^-1 を掛ける（v 下向きの符号を含む）
  const kinv = (h) => [(h[0] - cxPx * h[2]) / f, -(h[1] - cyPx * h[2]) / f, h[2]];

  const m1 = kinv(col(0));   // course +X 方向がカメラ座標でどう見えるか
  const m2 = kinv(col(1));   // course +Z 方向
  const m3 = kinv(col(2));   // 原点の並進

  const n1 = norm3(m1), n2 = norm3(m2);
  if (n1 < 1e-9 || n2 < 1e-9) return null;
  const lambda = 2 / (n1 + n2);          // 2 本の平均でスケールを決める（片方だけだとノイズに弱い）

  // R_cam の行ベクトル: 1 行目 = course X 軸の像、3 行目 = course Z 軸の像。
  // （p_cam = R_cam^T (p_world - C) なので、R_cam^T の列 = R_cam の行）
  let r1 = normalize3(scale3(m1, lambda));
  let r3 = normalize3(scale3(m2, lambda));
  // 直交化（測定誤差で厳密には直交しない）
  const proj = dot3(r3, r1);
  r3 = normalize3(sub3(r3, scale3(r1, proj)));
  const r2 = normalize3(cross3(r3, r1));   // det=+1 になる組み合わせ
  const R = [r1, r2, r3];

  // t = R_cam^T (-C) = lambda * m3  →  C = -R_cam t
  // （R は**行**ベクトル格納なので、R_cam t は各行と t の内積。転置を取り違えると
  //   回転が完全に正しいまま位置だけ別の場所に出る＝気づきにくい）
  const t = scale3(m3, lambda);
  const C = [
    -dot3(R[0], t),
    -dot3(R[1], t),
    -dot3(R[2], t),
  ];
  if (![...C, ...R.flat()].every(Number.isFinite)) return null;
  return { R, C };
}

/**
 * 回転行列 → Unity の Euler 角（ZXY 順・度）。`Quaternion.Euler(x, y, z)` に渡すと同じ回転になる。
 * @returns {{x:number, y:number, z:number}}
 */
export function matrixToUnityEuler(R) {
  const clamp = (v) => Math.max(-1, Math.min(1, v));
  const x = Math.asin(clamp(-R[1][2]));
  let y, z;
  if (Math.abs(R[1][2]) < 0.99999) {
    y = Math.atan2(R[0][2], R[2][2]);
    z = Math.atan2(R[1][0], R[1][1]);
  } else {
    // ジンバルロック（真上 / 真下を向いている）: yaw と roll が縮退するので roll=0 に寄せる
    y = Math.atan2(-R[2][0], R[0][0]);
    z = 0;
  }
  const D = 180 / Math.PI;
  return { x: x * D, y: y * D, z: z * D };
}

// ---- 順投影（検証・ワイヤー重畳で使う）--------------------------------------

/**
 * course 空間の点 → 映像上の画素座標。カメラ後方なら null。
 * **Unity の ShowCgLayer / ScreenComposite と同じ結果になること**が本関数の存在理由。
 * @param {object} calib 較正（fxPx/fyPx/cxPx/cyPx/k1/x/y/z/yawDeg/pitchDeg/rollDeg）
 * @param {number} x @param {number} y @param {number} z course 空間 (m)
 * @returns {{u:number, v:number}|null} 画素座標（左上原点）
 */
export function projectPoint(calib, x, y, z) {
  const R = unityEulerToMatrix(-calib.pitchDeg, calib.yawDeg, calib.rollDeg || 0);
  const d = [x - calib.x, y - calib.y, z - calib.z];
  // p_cam = R^T d
  const pz = R[0][2] * d[0] + R[1][2] * d[1] + R[2][2] * d[2];
  if (!(pz > 1e-4)) return null;                    // カメラ後方 / 焦点面上
  const px = R[0][0] * d[0] + R[1][0] * d[1] + R[2][0] * d[2];
  const py = R[0][1] * d[0] + R[1][1] * d[1] + R[2][1] * d[2];

  const nd = distortNorm(px / pz, py / pz, calib.k1 || 0);
  if (!nd) return null;
  return { u: calib.cxPx + calib.fxPx * nd[0], v: calib.cyPx - calib.fyPx * nd[1] };
}

/**
 * 映像上の画素 → 床平面 (y=planeY) 上の course 座標。**`projectPoint` の厳密な逆**。
 *
 * なぜ要るか: 人形の立ち位置を数値で打たせると誰も置けない（ユーザーからの明示的な指摘）。
 * 「映像の床をクリックしたらそこに立つ」を成立させるのがこの関数。較正が解けているカメラなら、
 * 画素 1 個から床の 1 点が一意に決まる（床が平面だと分かっているので奥行きの不定性が消える）。
 *
 * 順序は projectPoint の逆をそのまま辿る: 画素 → 歪んだ正規化 → **undistort** → カメラ座標のレイ →
 * ワールド方向 → 床との交点。ここで undistort を飛ばすと、広角レンズの画面端で数十 cm ずれた
 * 場所に人形が立ち、しかも「クリックした場所に出ない」理由が誰にも分からなくなる。
 *
 * @param {object} calib 較正（projectPoint と同じキー）
 * @param {number} u @param {number} v 画素座標（左上原点）
 * @param {number} [planeY] 交わらせる水平面の高さ (m)。既定は床 (0)
 * @returns {{x:number, z:number, t:number}|null} course 座標と、カメラからのレイ長。
 *   地平線より上（床と交わらない）・カメラ後方へ伸びる方向なら null
 */
export function unprojectToFloor(calib, u, v, planeY = 0) {
  if (!calib) return null;
  const fx = calib.fxPx, fy = calib.fyPx > 0 ? calib.fyPx : calib.fxPx;
  if (!(fx > 0) || !(fy > 0)) return null;

  // v は下向きなので符号が反転する（projectPoint の `v = cy - fy * ny` の逆）。
  const [nx, ny] = undistortNorm((u - calib.cxPx) / fx, (calib.cyPx - v) / fy, calib.k1 || 0);

  // p_cam = t * (nx, ny, 1) をワールドへ。projectPoint は p_cam = R^T d なので d = R p_cam。
  const R = unityEulerToMatrix(-calib.pitchDeg, calib.yawDeg, calib.rollDeg || 0);
  const dx = R[0][0] * nx + R[0][1] * ny + R[0][2];
  const dy = R[1][0] * nx + R[1][1] * ny + R[1][2];
  const dz = R[2][0] * nx + R[2][1] * ny + R[2][2];

  // 床と平行なレイ（地平線）はここで弾く。弾かないと t が発散して、画面のわずか上を
  // クリックしただけで人形が数百 m 先へ飛ぶ。
  if (Math.abs(dy) < 1e-9) return null;
  const t = (planeY - calib.y) / dy;
  if (!(t > 1e-6) || !Number.isFinite(t)) return null;    // カメラ後方 / 自分自身の位置

  const x = calib.x + t * dx, z = calib.z + t * dz;
  if (!Number.isFinite(x) || !Number.isFinite(z)) return null;
  return { x, z, t };
}

/** Unity の Euler（ZXY 順・度）→ 回転行列（行優先・ローカル→ワールド）。 */
export function unityEulerToMatrix(xDeg, yDeg, zDeg) {
  const r = Math.PI / 180;
  const cx = Math.cos(xDeg * r), sx = Math.sin(xDeg * r);
  const cy = Math.cos(yDeg * r), sy = Math.sin(yDeg * r);
  const cz = Math.cos(zDeg * r), sz = Math.sin(zDeg * r);
  // R = Ry * Rx * Rz（Unity の適用順は Z → X → Y）
  return [
    [cy * cz + sy * sx * sz, -cy * sz + sy * sx * cz, sy * cx],
    [cx * sz, cx * cz, -sx],
    [-sy * cz + cy * sx * sz, sy * sz + cy * sx * cz, cy * cx],
  ];
}

// ---- 較正の本体 -------------------------------------------------------------

const K1_MIN = -0.45, K1_MAX = 0.45, K1_STEP = 0.01;

/**
 * 歪み k1 を推定してよい最小点数。
 * **平面 1 枚では k1 と焦点距離が縮退する**（どちらも「像を広げる／縮める」効果を持つ）。
 * 点が少ないと歪みを f に吸わせた偽の解に落ち、位置・高さが数十 cm ずれる（実測: 5 点で
 * f 430→300・高さ 1.5m→1.14m）。点が 8 個あれば両方とも真値に戻る（実測: 誤差 0.02px）。
 */
export const MIN_POINTS_FOR_K1 = 6;

/**
 * 高さの点（壁の縦エッジの上端）がこれだけあれば、床の点が少なくても k1 を推定してよい。
 * 平面 1 枚の縮退は「平面から出た点」で解ける — 縦エッジ 2 本で f と k1 が分離する。
 */
export const MIN_RAISED_FOR_K1 = 2;

/** k1 探索で許す焦点距離の変動幅（k1=0 の解に対する比）。これを超える解は縮退とみなして捨てる。 */
const K1_FOCAL_TOLERANCE = 0.3;

function buildCalib(pts, w, h, k1, fixedFocalPx = 0) {
  const cx = w / 2, cy = h / 2;
  // 観測は歪んだ座標。理想座標へ直してから平面ホモグラフィを解く。
  // f が未知なら「推定 → その f で undistort → 解き直す」を数回まわす。
  // f が既知（fixedFocalPx）なら推定を飛ばす — **これが精度を一桁変える**（下の注記）。
  const lockF = fixedFocalPx > 1;
  let f = lockF ? fixedFocalPx : Math.max(w, h);   // 未知のときの初期値は対角 90° 相当
  let best = null;
  for (let iter = 0; iter < 4; iter++) {
    const corrected = pts.map((p) => {
      const [nx, ny] = undistortNorm((p.u - cx) / f, (p.v - cy) / f, k1);
      return { x: p.x, z: p.z, u: cx + nx * f, v: cy + ny * f };
    });
    const H = solveHomography(corrected);
    if (!H) return null;
    if (lockF) { best = { H, f }; break; }
    const f2 = focalFromHomography(H, cx, cy);
    if (!f2) return null;
    const converged = Math.abs(f2 - f) / f < 1e-4;
    f = f2;
    best = { H, f };
    if (converged && iter > 0) break;
  }
  if (!best) return null;

  const pose = poseFromHomography(best.H, best.f, cx, cy);
  if (!pose) return null;
  const e = matrixToUnityEuler(pose.R);
  return {
    x: pose.C[0], y: pose.C[1], z: pose.C[2],
    yawDeg: e.y, pitchDeg: -e.x, rollDeg: e.z,
    fxPx: best.f, fyPx: best.f, cxPx: cx, cyPx: cy,
    k1,
    srcW: w, srcH: h,
  };
}

/** 較正の再投影誤差 (RMS px)。写らない点はペナルティとして大きな値を返す。 */
export function reprojectionRms(calib, pts) {
  let sum = 0;
  for (const p of pts) {
    // y は床の点なら 0、壁の縦エッジの上端ならその高さ。ここで 0 に潰すと
    // **高さの点の残差が常に巨大になり**、精密化した解ほど「悪い」と判定される。
    const q = projectPoint(calib, p.x, p.y > 0 ? p.y : 0, p.z);
    if (!q) return Number.POSITIVE_INFINITY;
    sum += (q.u - p.u) ** 2 + (q.v - p.v) ** 2;
  }
  return Math.sqrt(sum / pts.length);
}

/**
 * 床の対応点から較正を解く。**この体験の較正の入口**。
 *
 * ⚠ **焦点距離を既知にできるなら必ず渡すこと**（`opts.fixedFocalPx`）。
 * 平面 1 枚から f まで推定すると、人のクリック誤差 1.5px に対して位置が **27cm** ずれる。
 * f を固定して外部パラメータだけ解けば **2cm** に収まる（実測・同条件）。
 * 平面ホモグラフィからの f 推定は元々ノイズに弱く（Zhang も複数枚を推奨）、
 * f の誤差がそのまま奥行きスケールの誤差になるため。
 * → 運用は「**内部（画角）は一度だけ丁寧に / 外部（置き場所）は現場で毎回**」。
 *
 * @param {{x:number,z:number,u:number,v:number}[]} pts course 空間の床点 ↔ 画素（4 点以上）
 * @param {number} w @param {number} h フレーム実寸 (px)
 * @param {{lensId?:string, solvedAtIso?:string, estimateK1?:boolean, fixedFocalPx?:number}} [opts]
 * @returns {{ok:true, calib:object, k1Estimated:boolean, focalLocked:boolean}|{ok:false, reason:string}}
 */
export function calibrateFromFloorPoints(all, w, h, opts = {}) {
  // 高さの点（壁の縦エッジの上端・y>0）は**平面の外**なので、ホモグラフィには入れられない。
  // 床の点で初期解を作り、最後に全点で LM 精密化する（refineCalib）。
  const src = Array.isArray(all) ? all : [];
  const pts = src.filter((p) => !(p.y > 0.01));
  const raised = src.filter((p) => p.y > 0.01);
  if (pts.length < 4) {
    return { ok: false, reason: '床の点が足りません（4 点以上）' };
  }
  if (!(w > 1) || !(h > 1)) return { ok: false, reason: '映像の実寸が分かりません' };
  if (isDegenerate(pts)) {
    return { ok: false, reason: '点が一直線に近いか重なっています（部屋の広がりを使って離して打つ）' };
  }

  const fixedF = opts.fixedFocalPx > 1 ? opts.fixedFocalPx : 0;

  // まず歪み無しで基準を取る。これが f の錨になる。
  const base = buildCalib(pts, w, h, 0, fixedF);
  if (!base) {
    // 4 点ちょうどだと DLT が正方系になり、3 点が一直線に並んだだけで特異になる。
    // 「解けません」で突き放さず、何を直せばいいか言う（現場で詰まらせない）。
    if (pts.length === 4 && hasCollinearTriple(pts)) {
      return { ok: false, reason: '4 点のうち 3 点が一直線に並んでいます（点をずらすか、もう 1 点足す）' };
    }
    return { ok: false, reason: '解けませんでした（点の対応が入れ違っている可能性）' };
  }
  let best = { calib: base, rms: reprojectionRms(base, pts) };
  if (!Number.isFinite(best.rms)) return { ok: false, reason: '解けませんでした（点の打ち間違いを疑う）' };

  // 歪みは「解いてから測る」しかないので粗探索する。広角レンズで k1 を 0 に固定した較正は
  // 画面の端で必ずずれ、それが姿勢誤差と区別できなくなる（＝ワイヤー重畳での検証が成立しない）。
  // f を固定していれば k1 との縮退が起きないので点数を問わない。
  // f も推定するときは点が少ないと縮退するので、そのときは歪み無しのまま置く。
  // 高さの点が 2 本以上あれば平面の縮退が解けるので、床の点が少なくても k1 を推定してよい。
  const estimateK1 = opts.estimateK1 !== false
    && (fixedF > 0 || pts.length >= MIN_POINTS_FOR_K1 || raised.length >= MIN_RAISED_FOR_K1);
  if (estimateK1) {
    const steps = Math.round((K1_MAX - K1_MIN) / K1_STEP) + 1;
    for (let i = 0; i < steps; i++) {
      const k1 = K1_MIN + i * K1_STEP;
      if (!k1) continue;
      const c = buildCalib(pts, w, h, k1, fixedF);
      if (!c) continue;
      // 焦点距離が基準から大きく離れる解は「歪みを f に吸わせた」縮退解。位置と高さが壊れる。
      if (!fixedF && Math.abs(c.fxPx / base.fxPx - 1) > K1_FOCAL_TOLERANCE) continue;
      const rms = reprojectionRms(c, pts);
      if (Number.isFinite(rms) && rms < best.rms) best = { calib: c, rms };
    }
  }

  // 高さの点があるならここで精密化する。**平面の外の拘束が入って初めて**画角・高さ・傾きが
  // まとめて決まる（床だけだと「f を伸ばして遠ざける解」と「縮めて近づける解」が区別できない）。
  let refined = false;
  if (raised.length > 0) {
    // 比較は**同じ点の集合**で行う。初期解の rms は床の点だけで測ったものなので、
    // そのまま全点の rms と比べると「点が増えたぶん悪化した」を退化と誤判定する。
    const beforeAll = reprojectionRms(best.calib, src);
    const r = refineCalib(best.calib, src, { fixedFocalPx: fixedF, estimateK1 });
    if (r.ok && Number.isFinite(r.rmsPx) && (!Number.isFinite(beforeAll) || r.rmsPx <= beforeAll)) {
      best = { calib: r.calib, rms: r.rmsPx };
      refined = true;
    } else if (Number.isFinite(beforeAll)) {
      best = { calib: best.calib, rms: beforeAll };   // 精密化しなくても rms は全点で言う
    }
  }

  // カメラが床下に潜る / 天井を突き抜ける解は物理的にありえない。
  if (!(best.calib.y > 0.05) || best.calib.y > 5) {
    return { ok: false, reason: `カメラの高さが ${best.calib.y.toFixed(2)}m と出ました（点の対応が入れ違っている可能性）` };
  }

  return {
    ok: true,
    // 高さの点で精密化したか（卓が「床だけ / 高さも使った」を出し分ける）。
    refined,
    raisedCount: raised.length,
    // 歪みを推定できたか。false なら卓が「点を増やせば歪みも直せる」と案内する。
    k1Estimated: estimateK1 && best.calib.k1 !== 0,
    // 焦点距離を固定して解いたか。false（＝推定した）なら位置は数十 cm ずれうるので、
    // 卓は「この画角で固定して打ち直す」を促す。
    focalLocked: fixedF > 0,
    calib: {
      ...best.calib,
      lensId: opts.lensId || '',
      rmsPx: best.rms,
      pointCount: src.length,
      solvedAtIso: opts.solvedAtIso || '',
      // refs は**高さの点も含めて**残す（次回は「ずれた点だけ直して解き直す」が現場の通常運転）。
      refs: src.map((p) => ({ u: p.u / w, v: p.v / h, x: p.x, z: p.z, y: p.y > 0 ? p.y : 0 })),
    },
  };
}

/** どれか 3 点が（ほぼ）一直線に並んでいるか。4 点ちょうどのときだけ致命的になる。 */
export function hasCollinearTriple(pts) {
  for (let i = 0; i < pts.length; i++)
    for (let j = i + 1; j < pts.length; j++)
      for (let k = j + 1; k < pts.length; k++) {
        const a = pts[i], b = pts[j], c = pts[k];
        const area2 = Math.abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z));
        if (area2 < 1e-3) return true;
      }
  return false;
}

/** 点が一直線 / 重複で平面が定まらないか。 */
export function isDegenerate(pts) {
  // 最大の三角形面積が閾値未満なら平面の広がりが無い。
  let maxArea = 0;
  for (let i = 0; i < pts.length; i++)
    for (let j = i + 1; j < pts.length; j++)
      for (let k = j + 1; k < pts.length; k++) {
        const a = pts[i], b = pts[j], c = pts[k];
        const area = Math.abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) / 2;
        if (area > maxArea) maxArea = area;
      }
  return maxArea < 0.02;   // 0.02 m^2 = 20cm 四方の三角形すら作れない
}

// ---- 高さの点を使った精密化（LM）----------------------------------------------
//
//   **床の点だけでは足りない**（ユーザー報告 2026-07-29「床だけじゃ精度に不安が残る／うまくいかない」）。
//   理由は幾何にある: 1 枚の平面だけを見ていると、
//     ・焦点距離を伸ばして遠ざける解と、縮めて近づける解が、床の上ではほとんど同じ絵になる
//     ・歪み k1 も「像を広げる／縮める」効果なので f と縮退する（MIN_POINTS_FOR_K1 の注記）
//   壁の縦エッジ（床の角と、その真上 h[m] の点）を入れると**平面から出た拘束**が入り、
//   この縮退が原理的に解ける。3 本も入れれば画角・高さ・傾きがまとめて決まる。
//
//   やり方は「ホモグラフィで初期解 → 全点（床 + 高さ）で Levenberg–Marquardt」。
//   **順投影は projectPoint をそのまま使う**（＝ Unity / シェーダと同じ式）。ここで別式を書くと、
//   卓の中では合っているのに実機で合わない、という最悪の破れ方をする。

const LM_STEPS = { pos: 2e-3, ang: 2e-2, f: 0.4, k1: 2e-4 };   // 数値微分の刻み（単位ごと）
const LM_BEHIND_PENALTY = 4000;   // カメラ後方へ回った解を弾き返すための残差

/** 較正 → LM のパラメータ配列（free で選んだものだけ）。 */
function lmPack(c, free) {
  const all = { x: c.x, y: c.y, z: c.z, yawDeg: c.yawDeg, pitchDeg: c.pitchDeg, rollDeg: c.rollDeg || 0, f: c.fxPx, k1: c.k1 || 0 };
  return free.map((k) => all[k]);
}
function lmUnpack(c, free, v) {
  const out = { ...c };
  free.forEach((k, i) => {
    if (k === 'f') { out.fxPx = v[i]; out.fyPx = v[i]; } else out[k] = v[i];
  });
  return out;
}
const lmStep = (k) => (k === 'f' ? LM_STEPS.f : k === 'k1' ? LM_STEPS.k1
  : (k === 'x' || k === 'y' || k === 'z') ? LM_STEPS.pos : LM_STEPS.ang);

/** 残差ベクトル（点ごとに u,v の 2 本）。カメラ後方は大きな値で弾き返す。 */
function lmResiduals(c, obs) {
  const r = new Array(obs.length * 2);
  for (let i = 0; i < obs.length; i++) {
    const p = projectPoint(c, obs[i].x, obs[i].y || 0, obs[i].z);
    if (!p) { r[i * 2] = LM_BEHIND_PENALTY; r[i * 2 + 1] = LM_BEHIND_PENALTY; continue; }
    r[i * 2] = p.u - obs[i].u;
    r[i * 2 + 1] = p.v - obs[i].v;
  }
  return r;
}
const lmCost = (r) => r.reduce((a, v) => a + v * v, 0);

/**
 * 3D 対応点で較正を精密化する。**床の点だけのときも呼んでよい**（悪化しない）。
 *
 * @param {object} calib 初期解（ホモグラフィの結果）
 * @param {{x:number,y:number,z:number,u:number,v:number}[]} obs 対応点（y は床なら 0、壁の上端なら高さ）
 * @param {{fixedFocalPx?:number, estimateK1?:boolean, maxIter?:number}} [opts]
 * @returns {{ok:boolean, calib:object, rmsPx:number, iterations:number}}
 */
export function refineCalib(calib, obs, opts = {}) {
  if (!calib || !Array.isArray(obs) || obs.length < 4) {
    return { ok: false, calib, rmsPx: NaN, iterations: 0 };
  }
  const free = ['x', 'y', 'z', 'yawDeg', 'pitchDeg', 'rollDeg'];
  if (!(opts.fixedFocalPx > 1)) free.push('f');
  if (opts.estimateK1) free.push('k1');

  let cur = { ...calib };
  let v = lmPack(cur, free);
  let res = lmResiduals(cur, obs);
  let cost = lmCost(res);
  let lambda = 1e-3;
  const n = free.length;
  const maxIter = opts.maxIter > 0 ? opts.maxIter : 80;
  let iter = 0;

  for (; iter < maxIter; iter++) {
    // 数値ヤコビアン（前進差分）。パラメータは 8 個までなので素直に作ってよい。
    const J = [];
    for (let k = 0; k < n; k++) {
      const step = lmStep(free[k]);
      const vv = v.slice();
      vv[k] += step;
      const rk = lmResiduals(lmUnpack(cur, free, vv), obs);
      J.push(rk.map((val, i) => (val - res[i]) / step));
    }
    // 正規方程式 (JtJ + λI) δ = -Jt r
    const JtJ = Array.from({ length: n }, (_, a) => Array.from({ length: n }, (_, b) => {
      let s = 0;
      for (let i = 0; i < res.length; i++) s += J[a][i] * J[b][i];
      return s;
    }));
    const Jtr = Array.from({ length: n }, (_, a) => {
      let s = 0;
      for (let i = 0; i < res.length; i++) s += J[a][i] * res[i];
      return -s;
    });

    let improved = false;
    for (let attempt = 0; attempt < 6; attempt++) {
      const A = JtJ.map((row, a) => row.map((val, b) => (a === b ? val * (1 + lambda) : val)));
      const delta = solveLinear(A, Jtr);
      if (!delta || delta.some((d) => !Number.isFinite(d))) { lambda *= 10; continue; }
      const vNext = v.map((val, i) => val + delta[i]);
      const cNext = lmUnpack(cur, free, vNext);
      // 物理的にありえない解へ落ちるのを止める（床下・天井裏・負の焦点距離）。
      if (!(cNext.y > 0.05) || cNext.y > 5 || !(cNext.fxPx > 20)) { lambda *= 10; continue; }
      const rNext = lmResiduals(cNext, obs);
      const costNext = lmCost(rNext);
      if (costNext < cost) {
        cur = cNext; v = vNext; res = rNext; cost = costNext;
        lambda = Math.max(1e-9, lambda / 3);
        improved = true;
        break;
      }
      lambda *= 10;
    }
    if (!improved || lambda > 1e9) break;
  }

  const rms = Math.sqrt(cost / obs.length);
  return { ok: true, calib: cur, rmsPx: rms, iterations: iter };
}

/** 較正の品質を人間の言葉にする（卓の表示用）。 */
export function calibQualityLabel(rmsPx) {
  if (!Number.isFinite(rmsPx)) return { text: '解けていません', level: 'bad' };
  if (rmsPx <= 2) return { text: `よく合っています（誤差 ${rmsPx.toFixed(1)}px）`, level: 'good' };
  if (rmsPx <= 6) return { text: `だいたい合っています（誤差 ${rmsPx.toFixed(1)}px）`, level: 'ok' };
  return { text: `ずれています（誤差 ${rmsPx.toFixed(1)}px）— 点を打ち直してください`, level: 'bad' };
}
