// 部屋の 3D プロキシ（show.json `layout.room`）と CG 照明の純関数（DOM 非依存）。
//   移植元（**C# が正**）:
//     Assets/Scripts/Streaming/ShowControlClient.cs … ShowRoomDef / ShowRoomWallDef /
//                                                     ShowRoomBoxDef / ShowRoomLightDef（キー名・既定値）
//     Assets/Scripts/Streaming/Cg/ShowRoomProxy.cs  … WallBox / PropBox / FloorRect（幾何）
//     Assets/Scripts/Streaming/Cg/ShowCgLayer.cs    … CourseLightDirToWorld（光の向き）
//   設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.5
//
// プロキシは**実物と同じ位置に置く不可視の幾何**で、1 つの幾何が 3 用途を兼ねる:
//   ①CG 人形のオクルーダ（人形が壁の裏へ回れる） ②影の落ち先 ③較正の参照。
// 用途ごとに別の幾何を持つと必ずズレるので分けない。ここはその**唯一の著作データ**を扱う。
//
// present-flag 契約（Web⇄Unity）: Unity は `layout.hasRoom` / `room.hasLight` を
// **宣言 bool ∧ 実体 != null** の AND で確定する（ShowControlClient.Room / ShowCgLayer.ResolveLight）。
// したがって flag=false のときは入れ子キー自体を出さない。ここを破ると、JsonUtility が
// 省略キーを既定インスタンスで埋める性質と噛み合って**端末キャッシュ往復で幽霊の部屋が湧く**
// （timeline の present-flag で実際に起きた critical バグと同型）。

// ---- 既定値（**Unity の C# 既定と一字一句合わせる**。片方だけ変えると、卓で作った部屋と
//      実機が組む部屋が静かに食い違う）------------------------------------------------
export const ROOM_DEFAULT = { floorY: 0, floorW: 1.8, floorD: 1.8 };
export const WALL_DEFAULT = { h: 1.0, thick: 0.04 };
export const BOX_DEFAULT = { y: 0, w: 0.5, d: 0.5, h: 0.7, yawDeg: 0 };

/** ShowRoomLightDef の既定。**部屋で 1 つ**（カメラ毎に持つと切替のたび人形の陰影が飛ぶ）。 */
export function defaultLight() {
  return {
    yawDeg: 30, pitchDeg: 55, tempK: 4000, intensity: 1,
    ambient: 0.35, shadowDensity: 0.55, shadowSoftM: 0.12,
  };
}

/** 照明スライダの範囲（UI とテストで同じ値を使う）。 */
export const LIGHT_RANGE = {
  yawDeg: { min: -180, max: 180, step: 1 },
  pitchDeg: { min: 0, max: 90, step: 1 },
  tempK: { min: 2000, max: 8000, step: 100 },
  intensity: { min: 0, max: 2, step: 0.05 },
  ambient: { min: 0, max: 1, step: 0.01 },
  shadowDensity: { min: 0, max: 1, step: 0.01 },
  shadowSoftM: { min: 0, max: 0.5, step: 0.01 },
};

// 退化の判定しきい値（ShowRoomWallDef.IsUsable / ShowRoomBoxDef.IsUsable / ShowRoomDef.HasData）。
// Unity は潰れた箱を**黙って捨てる**（深度を書かないのでオクルーダとして無意味 + scale 0 警告）。
// 卓が捨てずに書き出すと「マップには見えているのに実機には無い」幽霊になるので、保存側で落とす。
export const WALL_MIN_LENGTH_M = 0.01;
export const MIN_DIM_M = 0.01;
export const FLOOR_MIN_M = 0.05;

// id 接頭辞（schema の例に合わせる: 壁 w1.. / 箱 p1..）。id は Unity の Build では読まないが、
// 卓の選択・削除・将来の差分更新のキーになるので一意にしておく。
export const WALL_ID_PREFIX = 'w';
export const BOX_ID_PREFIX = 'p';

const num = (v, def) => (Number.isFinite(v) ? v : def);
const arr = (v) => (Array.isArray(v) ? v : []);

/** 既存 id と衝突しない次の id（`w1`, `w2`, …）。削除しても衝突しない。 */
export function nextRoomId(list, prefix) {
  const used = new Set(arr(list).map((o) => (o && typeof o.id === 'string' ? o.id : '')));
  let n = 1;
  while (used.has(prefix + n)) n++;
  return prefix + n;
}

function claimId(used, wanted, prefix) {
  if (wanted && !used.has(wanted)) { used.add(wanted); return wanted; }
  let n = 1;
  while (used.has(prefix + n)) n++;
  used.add(prefix + n);
  return prefix + n;
}

// ---- 生成 --------------------------------------------------------------------

/** 空の部屋（床だけ）。壁ゼロでも影は落ちるので、床が正なら部屋として成立する。 */
export function newRoom(over = {}) {
  return {
    floorY: ROOM_DEFAULT.floorY,
    floorW: ROOM_DEFAULT.floorW,
    floorD: ROOM_DEFAULT.floorD,
    walls: [],
    props: [],
    light: defaultLight(),
    hasLight: false,
    ...over,
  };
}

/** 線分 + 高さ + 厚みの壁 1 枚。 */
export function newWall(x1, z1, x2, z2, over = {}) {
  return { id: '', x1, z1, x2, z2, h: WALL_DEFAULT.h, thick: WALL_DEFAULT.thick, ...over };
}

/**
 * 箱（机・柱）1 個。show.json のキーは `props`（Unity の ShowRoomBoxDef 配列）。
 * `y` は**床からの底面高さ**で、浮かせたい時だけ使う（中心ではない）。
 */
export function newBox(x, z, w, d, over = {}) {
  return {
    id: '', x, z,
    y: BOX_DEFAULT.y,
    w, d, h: BOX_DEFAULT.h, yawDeg: BOX_DEFAULT.yawDeg,
    ...over,
  };
}

// ---- 退化の判定（Unity の IsUsable / HasData のミラー）---------------------------

export const wallLength = (w) => Math.hypot(w.x2 - w.x1, w.z2 - w.z1);

export function isWallUsable(w) {
  if (!w || ![w.x1, w.z1, w.x2, w.z2, w.h].every(Number.isFinite)) return false;
  return w.h > MIN_DIM_M && wallLength(w) > WALL_MIN_LENGTH_M;
}

export function isBoxUsable(b) {
  if (!b || ![b.x, b.z, b.w, b.d, b.h].every(Number.isFinite)) return false;
  return b.w > MIN_DIM_M && b.d > MIN_DIM_M && b.h > MIN_DIM_M;
}

/** ShowRoomDef.HasData（床が正なら成立）。false の部屋は Unity が丸ごと無視する。 */
export function roomHasData(room) {
  return !!room && num(room.floorW, 0) > FLOOR_MIN_M && num(room.floorD, 0) > FLOOR_MIN_M;
}

// ---- 読み書き ------------------------------------------------------------------

const LIGHT_KEYS = Object.keys(defaultLight());

function normalizeLight(raw) {
  const out = defaultLight();
  if (!raw) return out;
  for (const k of LIGHT_KEYS) if (Number.isFinite(raw[k])) out[k] = raw[k];
  return out;
}

/**
 * show.json 由来の生データ → 編集モデル（既定を再充填・不正要素を落とす・id を一意化）。
 * raw が null でも**必ず完全な部屋を返す**（UI が「まだ無い」状態でも寸法欄を出せるようにする）。
 * 「部屋が著作されているか」は present-flag 側の話なので、ここでは判定しない。
 */
export function normalizeRoom(raw) {
  const src = raw || {};
  const room = newRoom({
    floorY: num(src.floorY, ROOM_DEFAULT.floorY),
    floorW: num(src.floorW, ROOM_DEFAULT.floorW),
    floorD: num(src.floorD, ROOM_DEFAULT.floorD),
    light: normalizeLight(src.light),
    // AND 規約: 宣言 bool ∧ 実体。どちらが欠けても「照明は未著作」
    hasLight: !!src.hasLight && !!src.light,
  });

  const wallIds = new Set();
  for (const w of arr(src.walls)) {
    if (!w || ![w.x1, w.z1, w.x2, w.z2].every(Number.isFinite)) continue;
    room.walls.push({
      id: claimId(wallIds, typeof w.id === 'string' ? w.id : '', WALL_ID_PREFIX),
      x1: w.x1, z1: w.z1, x2: w.x2, z2: w.z2,
      h: num(w.h, WALL_DEFAULT.h),
      thick: num(w.thick, WALL_DEFAULT.thick),
    });
  }

  const boxIds = new Set();
  for (const b of arr(src.props)) {
    if (!b || ![b.x, b.z].every(Number.isFinite)) continue;
    room.props.push({
      id: claimId(boxIds, typeof b.id === 'string' ? b.id : '', BOX_ID_PREFIX),
      x: b.x, z: b.z,
      y: num(b.y, BOX_DEFAULT.y),
      w: num(b.w, BOX_DEFAULT.w),
      d: num(b.d, BOX_DEFAULT.d),
      h: num(b.h, BOX_DEFAULT.h),
      yawDeg: num(b.yawDeg, BOX_DEFAULT.yawDeg),
    });
  }
  return room;
}

/**
 * 編集モデル → show.json の `layout.room`。
 *   ・退化した壁・箱は**書かない**（Unity が黙って捨てるものを残すと卓と実機が食い違う）
 *   ・`hasLight=false` のときは `light` キー自体を出さない（present-flag の AND 規約）
 */
export function serializeRoom(room) {
  const r = room || newRoom();
  const out = {
    floorY: num(r.floorY, ROOM_DEFAULT.floorY),
    floorW: num(r.floorW, ROOM_DEFAULT.floorW),
    floorD: num(r.floorD, ROOM_DEFAULT.floorD),
    walls: arr(r.walls).filter(isWallUsable).map((w) => ({
      id: w.id || '', x1: w.x1, z1: w.z1, x2: w.x2, z2: w.z2,
      h: num(w.h, WALL_DEFAULT.h), thick: num(w.thick, WALL_DEFAULT.thick),
    })),
    props: arr(r.props).filter(isBoxUsable).map((b) => ({
      id: b.id || '', x: b.x, z: b.z,
      y: num(b.y, BOX_DEFAULT.y),
      w: b.w, d: b.d, h: b.h,
      yawDeg: num(b.yawDeg, BOX_DEFAULT.yawDeg),
    })),
  };
  if (r.hasLight) out.light = normalizeLight(r.light);
  out.hasLight = !!r.hasLight;
  return out;
}

/**
 * `layout` から部屋を取り出す（present-flag の AND 規約 + Unity の HasData 判定を通す）。
 * 未著作なら `layout.floor`（登録ワイヤー用の旧データ）の寸法を種にした空の部屋を返す
 * ——「部屋を作り始めた瞬間に床が 1.8m 固定へ化ける」のを避けるため。
 *
 * @returns {{ room: object, hasRoom: boolean }}
 */
export function roomFromLayout(layout) {
  const lay = layout || {};
  const declared = !!lay.hasRoom && !!lay.room;
  const room = normalizeRoom(declared ? lay.room : {
    floorW: lay.floor && lay.floor.w,
    floorD: lay.floor && lay.floor.d,
  });
  return { room, hasRoom: declared && roomHasData(room) };
}

/**
 * `layout` へ部屋を書き戻す（保存直前に呼ぶ）。**flag は常に書き、false なら `room` キーを消す**。
 * layout は破壊的に更新する（呼び元が clone してから saveLayout する既存の流儀に合わせる）。
 */
export function writeRoomToLayout(layout, room, hasRoom) {
  const on = !!hasRoom && roomHasData(room);
  if (on) layout.room = serializeRoom(room);
  else delete layout.room;
  layout.hasRoom = on;
  return layout;
}

/**
 * 旧 `layout.wall`（L 字 3 点: corner / endX / endZ）→ プロキシの壁 2 本。
 * **`layout.wall` は消さない** — HMD 位置合わせリチュアルのワイヤー表示がまだ読んでいる。
 * ここは「同じ形を room 側にも起こす」取り込みであって、移行ではない。
 */
export function wallsFromLegacyWall(layoutWall) {
  const w = layoutWall;
  const ok = (p) => Array.isArray(p) && p.length >= 2 && Number.isFinite(p[0]) && Number.isFinite(p[1]);
  if (!w || !ok(w.corner)) return [];
  const out = [];
  if (ok(w.endZ)) out.push(newWall(w.endZ[0], w.endZ[1], w.corner[0], w.corner[1], { id: `${WALL_ID_PREFIX}1` }));
  if (ok(w.endX)) out.push(newWall(w.corner[0], w.corner[1], w.endX[0], w.endX[1], { id: `${WALL_ID_PREFIX}${out.length + 1}` }));
  return out.filter(isWallUsable);
}

// ---- 幾何（course 空間 XZ の footprint）------------------------------------------
// ShowRoomProxy が組む OBB を真上から見た四隅。**Unity と同じ軸の取り方**にしてあるので、
// ここがズレると「卓で見た壁」と「実機で人形を隠す壁」が別物になる。

/**
 * 壁の footprint（四隅・時計回りではなく矩形として閉じる順）。
 * ShowRoomProxy.WallBox は yaw = atan2(-dz, dx) で、ローカル +X が線分方向・
 * ローカル +Z が厚み方向 `(-dz, dx)/len` になる（符号を落とすと壁が 90° 転ぶ）。
 */
export function wallFootprint(w) {
  const dx = w.x2 - w.x1, dz = w.z2 - w.z1;
  const len = Math.hypot(dx, dz);
  if (!(len > 0)) return [];
  const ux = dx / len, uz = dz / len;          // 長さ方向（ローカル +X）
  const nx = -uz, nz = ux;                     // 厚み方向（ローカル +Z）
  const cx = (w.x1 + w.x2) / 2, cz = (w.z1 + w.z2) / 2;
  const hl = len / 2, ht = Math.max(0.005, num(w.thick, WALL_DEFAULT.thick)) / 2;
  return [
    { x: cx - ux * hl - nx * ht, z: cz - uz * hl - nz * ht },
    { x: cx + ux * hl - nx * ht, z: cz + uz * hl - nz * ht },
    { x: cx + ux * hl + nx * ht, z: cz + uz * hl + nz * ht },
    { x: cx - ux * hl + nx * ht, z: cz - uz * hl + nz * ht },
  ];
}

/**
 * 箱の footprint（四隅）。ShowRoomProxy.PropBox は Unity の Euler Y 回転なので、
 * ローカル +X → (cos yaw, -sin yaw)、ローカル +Z → (sin yaw, cos yaw)（course XZ）。
 */
export function boxFootprint(b) {
  const yaw = num(b.yawDeg, 0) * Math.PI / 180;
  const c = Math.cos(yaw), s = Math.sin(yaw);
  const ux = c, uz = -s;   // ローカル +X（幅 w）
  const vx = s, vz = c;    // ローカル +Z（奥行 d）
  const hw = num(b.w, BOX_DEFAULT.w) / 2, hd = num(b.d, BOX_DEFAULT.d) / 2;
  return [
    { x: b.x - ux * hw - vx * hd, z: b.z - uz * hw - vz * hd },
    { x: b.x + ux * hw - vx * hd, z: b.z + uz * hw - vz * hd },
    { x: b.x + ux * hw + vx * hd, z: b.z + uz * hw + vz * hd },
    { x: b.x - ux * hw + vx * hd, z: b.z - uz * hw + vz * hd },
  ];
}

/** 床の矩形（ShowRoomProxy.FloorRect: course 原点中心）。 */
export function floorRect(room) {
  const hw = Math.max(0, num(room.floorW, 0)) / 2;
  const hd = Math.max(0, num(room.floorD, 0)) / 2;
  return { xLo: -hw, xHi: hw, zLo: -hd, zHi: hd };
}

function ringSegments(pts, kind, id) {
  const out = [];
  for (let i = 0; i < pts.length; i++) {
    const a = pts[i], b = pts[(i + 1) % pts.length];
    out.push({ kind, id, x1: a.x, z1: a.z, x2: b.x, z2: b.z });
  }
  return out;
}

/**
 * 部屋 → course 空間の線分配列（床外周 → 壁 → 箱の順）。
 * **Unity が実際に組む形だけ**を返す（退化したものは serializeRoom と同じ基準で落とす）ので、
 * これを重ねて「見えているものが実機にもある」ことを確認できる。
 * 編集中の引きかけの線はここを通さず、呼び元が footprint を直接描くこと。
 */
export function roomOutlineSegments(room) {
  const r = room || newRoom();
  const out = [];
  if (roomHasData(r)) {
    const f = floorRect(r);
    out.push(...ringSegments([
      { x: f.xLo, z: f.zHi }, { x: f.xHi, z: f.zHi },
      { x: f.xHi, z: f.zLo }, { x: f.xLo, z: f.zLo },
    ], 'floor', ''));
  }
  for (const w of arr(r.walls)) {
    if (!isWallUsable(w)) continue;
    out.push(...ringSegments(wallFootprint(w), 'wall', w.id || ''));
  }
  for (const b of arr(r.props)) {
    if (!isBoxUsable(b)) continue;
    out.push(...ringSegments(boxFootprint(b), 'prop', b.id || ''));
  }
  return out;
}

// ---- 照明 ----------------------------------------------------------------------

/**
 * course 空間で「光が来る向き」（正規化）。ShowCgLayer.CourseLightDirToWorld の
 * course 部分（courseYawDeg=0）のミラー。マップに矢印を描くのと、実機の陰影が同じ式であること。
 * pitch は ±90 に畳む（真上はそのまま通す。方位角が無意味になるだけで縮退しない）。
 */
export function lightDirCourse(yawDeg, pitchDeg) {
  const yaw = num(yawDeg, 0) * Math.PI / 180;
  const pitch = Math.max(-90, Math.min(90, num(pitchDeg, 0))) * Math.PI / 180;
  const cp = Math.cos(pitch);
  return { x: Math.sin(yaw) * cp, y: Math.sin(pitch), z: Math.cos(yaw) * cp };
}

// 色温度 → RGB の近似（Tanner Helland）。**スウォッチ表示専用**で、実機の合成には使わない
// （Unity 側は tempK をまだ読まない）。数式の出所を変えると卓の見た目だけ動くので固定する。
export const KELVIN_MIN = 1000, KELVIN_MAX = 40000;
const clamp255 = (v) => Math.max(0, Math.min(255, Math.round(v)));

/** @returns {{r:number,g:number,b:number}} 0..255 */
export function kelvinToRgb(tempK) {
  const t = Math.max(KELVIN_MIN, Math.min(KELVIN_MAX, num(tempK, 6500))) / 100;
  const r = t <= 66 ? 255 : 329.698727446 * Math.pow(t - 60, -0.1332047592);
  const g = t <= 66
    ? 99.4708025861 * Math.log(t) - 161.1195681661
    : 288.1221695283 * Math.pow(t - 60, -0.0755148492);
  const b = t >= 66 ? 255 : (t <= 19 ? 0 : 138.5177312231 * Math.log(t - 10) - 305.0447927307);
  return { r: clamp255(r), g: clamp255(g), b: clamp255(b) };
}

/** スウォッチ用の CSS 色。 */
export function kelvinToCss(tempK) {
  const c = kelvinToRgb(tempK);
  return `rgb(${c.r}, ${c.g}, ${c.b})`;
}
