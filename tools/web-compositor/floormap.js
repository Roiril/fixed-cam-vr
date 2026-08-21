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

import {
  zonesFromLayout, cameraAtPoint, linesFromLayout, lineMid, lineLength,
  LINE_DIR_BOTH, LINE_DIR_FWD, LINE_DIR_BACK, LINE_MIN_LENGTH_M,
} from './zone-layout.js';
import {
  newWall, newBox, defaultLight, nextRoomId,
  roomFromLayout, writeRoomToLayout, wallsFromLegacyWall,
  wallFootprint, boxFootprint, floorRect,
  isWallUsable, isBoxUsable, roomHasData, wallLength,
  lightDirCourse, kelvinToCss,
  LIGHT_RANGE, WALL_ID_PREFIX, BOX_ID_PREFIX, WALL_MIN_LENGTH_M, MIN_DIM_M,
} from './room-model.js';
import {
  normalizeStartSpot, START_SPOT_DEFAULT, START_RADIUS_MIN, START_RADIUS_MAX,
} from './intro-model.js';
import { CAM_COLORS, camColor, GEO } from './palette.js';
import {
  mirrorLine, mirrorResidualM, mirrorSideCheck, calibUsableForMirror, MIRROR_TOLERANCE_M,
} from './line-mirror.js';

// 配色は palette.js が正。palette は依存ゼロなので、
// 「floormap は common.js に依存させない」決めを守ったまま 1 本にできる
// （旧実装は CAM_COLORS を common.js と 2 本持っていて、片方だけ直せば黙って食い違った）。

// グリッド既定寸法（契約）。
const GRID_TILE = 0.15, GRID_COLS = 12, GRID_ROWS = 12;
// 歩行回廊とみなすタイル中心 → 正準ループまでの距離しきい値（m）。壁 0.5〜床端 0.9 の帯を拾う。
const CORRIDOR_HALF = 0.20;

// 位置合わせ点（HMD タッチ基準点）。course space・順序=タッチ順・最小 2・最大 5。
const REG_MIN = 2, REG_MAX = 5, REG_HIT = 15; // REG_HIT = マーカー掴み判定半径(px)
// 位置合わせでコントローラを構える高さ（床から m）。0 = 床に着ける。
// 実機はここから床の高さを測るので、実際の構え方と食い違うとゾーン全体が上下にずれる。
const REG_TOUCH_H_DEFAULT = 0, REG_TOUCH_H_MAX = 2;

// 通過ライン（演出の発火点）。course space の線分。端点を掴んで伸縮 / 線を掴んで平行移動。
const LINE_END_HIT = 11;     // 端点の掴み判定 (px)
const LINE_BODY_HIT = 8;     // 線本体の掴み判定 (px)
const LINE_MAX = 12;         // 引ける本数の上限（増やしすぎると現場で見分けがつかない）

// 開始位置（導入演出の発火点）。course space の 1 点 + 半径。
// カメラ色（CAM_COLORS）とも壁（#ffdead）ともライン（担当カメラ色）とも被らない色にする
// — どのモードでも薄く描くので、他の要素と見間違えると「塗ったはずの色が違う」と誤読される。
const START_COLOR = GEO.start;
const START_HIT = 13;        // 中心マーカーの掴み判定 (px)
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
        <div class="fm-modes" role="group" aria-label="マップの操作">
          <button class="fm-mode on" data-mode="paint" title="タイルを塗ってゾーン（担当カメラ）を決める">🖌 塗る</button>
          <button class="fm-mode" data-mode="reg" title="HMD でタッチする位置合わせ点を置く">📍 位置合わせ点</button>
          <button class="fm-mode" data-mode="walk" title="体験者を歩かせる。マップのどこを押してもそこへ立つ">🚶 歩かせる</button>
          <button class="fm-mode" data-mode="cam" title="実カメラの置き場所と向きを決める（CG 人形を立てる視点）">📐 カメラ姿勢</button>
          <button class="fm-mode" data-mode="line" title="演出の発火点（体験者がここを通過したら）を引く">📏 通過ライン</button>
          <button class="fm-mode" data-mode="room" title="実物の壁・什器を写した不可視の 3D プロキシ（CG のオクルーダ / 影の落ち先 / 較正の参照）">🧱 部屋</button>
          <button class="fm-mode" data-mode="start" title="体験者がここへ来たら導入演出が始まる床の 1 点">🎬 開始位置</button>
        </div>
        <canvas class="fm-canvas" width="${SIZE}" height="${SIZE}"></canvas>
        <div class="fm-modehint"></div>
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
          <div class="fm-reg-note"></div>
          <div class="fm-reg-list"></div>
          <button class="fm-reg-add"></button>
          <div class="fm-reg-coords"></div>
          <div class="fm-row">
            <label class="fm-num">構える高さ (m)<input class="fm-reg-h" type="number"
              min="0" max="${REG_TOUCH_H_MAX}" step="0.01"></label>
          </div>
          <div class="fm-hint2">位置合わせのとき、コントローラを床から何 m の高さに構えるか。0 なら床に着ける。<b>ここから床の高さを測ります</b> — 実際の構え方と違う値を入れると、ゾーンが上下にずれたまま体験が始まります。</div>
          <div class="fm-hint2">床の×印テープを置く位置に点を打つ。番号＝HMD でタッチする順。最小2・最大5点。<b>📍 位置合わせ点</b>モードでキャンバスをクリック配置・ドラッグ移動・右クリック削除。未設定なら既定2点を使用。</div>
        </div>
        <div class="fm-lines" style="display:none">
          <div class="fm-course-label">通過ライン（演出の発火点）</div>
          <div class="fm-line-note"></div>
          <div class="fm-line-list"></div>
          <div class="fm-hint2">体験者がこの線を通過したら演出が始まります（タイムラインの演出で「このラインを通過したら」を選ぶ）。
            <b>ラインは担当カメラに紐づきます</b> — 担当と違う区間の演出からは選べず、他の領域で踏んでも何も起きません。
            <b>キャンバスをドラッグで線を引く</b>・端点をドラッグで伸縮・線をドラッグで平行移動・右クリックで削除。
            向きを決めると「矢印の向きに通過した時だけ」にできます（既定は両方向）。</div>
        </div>
        <div class="fm-campose" style="display:none">
          <div class="fm-course-label">カメラ姿勢（CG 人形を立てる視点）</div>
          <div class="fm-campose-list"></div>
          <div class="fm-hint2">実カメラを置いた場所・向きを写す。<b>姿勢を著作したカメラでだけ</b> CG 人形が出る（当てずっぽうのパースで出すと床に埋まる / 宙に浮く）。高さ・俯角・水平画角はカメラ列の 📐 欄。扇形＝画角の目安。</div>
        </div>
        <div class="fm-room" style="display:none">
          <div class="fm-course-label">部屋の 3D プロキシ（CG のオクルーダ / 影の落ち先 / 較正の参照）</div>
          <div class="fm-room-note"></div>
          <div class="fm-row">
            <button class="fm-room-tool on" data-tool="wall" title="ドラッグで壁（線分＋厚み）を引く">▬ 壁を引く</button>
            <button class="fm-room-tool" data-tool="box" title="ドラッグで箱（机・柱）を作る">◻ 箱を作る</button>
          </div>
          <div class="fm-row">
            <label class="fm-num">床 幅 (m)<input class="fm-room-fw" type="number" min="0.1" max="12" step="0.05"></label>
            <label class="fm-num">床 奥行 (m)<input class="fm-room-fd" type="number" min="0.1" max="12" step="0.05"></label>
          </div>
          <div class="fm-room-sel"></div>
          <div class="fm-room-list"></div>
          <div class="fm-row">
            <button class="fm-room-import" title="登録リチュアルが使っている L 字壁（layout.wall）と同じ形を、プロキシの壁として起こす">L 字壁を取り込む</button>
            <button class="fm-room-clear" title="部屋そのものを未著作に戻す（Unity は既定の無限床へフォールバックする）">✕ 未著作に戻す</button>
          </div>
          <div class="fm-hint2"><b>実物と同じ位置に置く不可視の幾何</b>です（VR には映りません）。1 つの幾何が
            <b>①CG 人形を隠す壁 ②影の落ち先 ③較正の参照</b>を兼ねるので、用途ごとに別々に作らないこと。
            壁 / 箱をクリックで選択・ドラッグで移動・端点や角をドラッグで伸縮・右クリックで削除。
            高さ・厚み・向きは選択中の欄で調整します。</div>
        </div>
        <div class="fm-start" style="display:none">
          <div class="fm-course-label">🎬 開始位置（導入演出が始まる床の 1 点）</div>
          <div class="fm-start-note"></div>
          <div class="fm-row">
            <label class="fm-num">半径 (m)<input class="fm-start-r" type="number"
              min="${START_RADIUS_MIN}" max="${START_RADIUS_MAX}" step="0.05"></label>
            <button class="fm-start-del" title="開始位置を未設定に戻す（スタッフが手で始める運用）">✕ 未設定に戻す</button>
          </div>
          <div class="fm-row">
            <span class="fm-start-lbl-cap">ラベル</span>
            <input class="fm-start-label" type="text" placeholder="${START_SPOT_DEFAULT.label}">
          </div>
          <div class="fm-hint2">体験者がこの円の中に入ったら導入演出（現実 → 映像）が始まります。
            <b>置くのは 1 点だけ</b>（2 回目のクリックは移動）。ドラッグで移動・右クリックで削除。
            <b>未設定なら自動では始まりません</b> — スタッフが卓の「導入を進める」で手動開始する運用になります。
            <b>周回コースのスタート区間の中に置く</b>こと（外に置くと、演出が終わった直後に体験者は
            本編のスタート区間へ入り直すことになります）。</div>
        </div>
        <div class="fm-light">
          <div class="fm-course-label">💡 CG 照明（人形の陰影と影・部屋で 1 つ）</div>
          <div class="fm-light-note"></div>
          <div class="fm-light-rows"></div>
          <div class="fm-row">
            <button class="fm-light-clear" title="照明を未著作に戻す（Unity 内蔵の既定へフォールバック）">✕ 未著作に戻す</button>
          </div>
          <div class="fm-hint2">Quest に環境光の自動推定はありません。<b>人が実際の部屋を見て合わせます</b>。
            とはいえ監視カメラ画質で観客が読めるのは<b>主光源の向きと影の濃さ</b>だけなので、そこだけ合っていれば足ります。
            マップ上の ☀ が「光が来ている方向」です。</div>
        </div>
        <label class="fm-num">オーバーラップ (m)<input class="fm-overlap" type="number" min="0" max="0.5" step="0.01"></label>
        <label class="fm-num">ヒステリシス (m)<input class="fm-hyst" type="number" min="0" max="0.5" step="0.01"></label>
        <div class="fm-sim-out"></div>
        <div class="fm-live-out"></div>
      </div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const canvas = q('.fm-canvas');
  const ctx = canvas.getContext('2d');
  const overlapI = q('.fm-overlap'), hystI = q('.fm-hyst');
  const simOut = q('.fm-sim-out'), liveOut = q('.fm-live-out');
  const dirtyEl = q('.fm-dirty'), paletteEl = q('.fm-palette');
  const courseStartSel = q('.fm-course-start'), courseDirBtn = q('.fm-course-dir'), courseOrderEl = q('.fm-course-order');
  const regNoteEl = q('.fm-reg-note'), regListEl = q('.fm-reg-list');
  const modeHint = q('.fm-modehint');
  const regAddBtn = q('.fm-reg-add'), regCoordsEl = q('.fm-reg-coords'), regHeightI = q('.fm-reg-h');

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
  // マップの操作モード。'paint'（タイルを塗る）/ 'reg'（位置合わせ点）/ 'walk'（歩かせる）。
  //   旧実装はチェックボックス 2 個（位置合わせ点編集 / シミュレーション）が
  //   コントロール列の下の方にあり、**マップから遠い上に「ON にしないと歩かせられない」**という
  //   隠れた前提を作っていた。キャンバス直上の 3 択に畳んで、操作対象と選択肢を隣に置く。
  let mapMode = 'paint';
  let regDragIndex = -1;     // ドラッグ中の regPoint index
  let camPoseIndex = 0;      // 📐 カメラ姿勢モードで選択中のカメラ index
  let lineDragIndex = -1;    // ドラッグ中の通過ライン index
  let lineUses = {};         // lineId → それを使っている演出の数（timeline 由来・削除の警告に使う）
  let drawing = null;        // 新規ラインの引き始め { x, z }（ドラッグ中のプレビュー）
  // 部屋の 3D プロキシ。**layout に直接持たず編集モデルを別に持つ**（regPoints / lines と同じ流儀）。
  //   layout へ書き戻すのは 💾 保存の瞬間だけで、そこで present-flag（hasRoom）と退化の除外が確定する。
  let roomModel = null;      // room-model.js の編集モデル（adoptLayout で必ず入る）
  let roomAuthored = false;  // layout.hasRoom（AND 規約で解決済み）
  let roomTool = 'wall';     // 空きをドラッグした時に作るもの 'wall' | 'box'
  let roomSel = null;        // 選択中 { kind:'wall'|'prop', id }
  let roomDrawing = null;    // 引きかけ { x, z, cur:{x,z} }
  const isReg = () => mapMode === 'reg';
  const isWalk = () => mapMode === 'walk';
  const isCamPose = () => mapMode === 'cam';
  const isLine = () => mapMode === 'line';
  const isRoom = () => mapMode === 'room';
  const isStart = () => mapMode === 'start';

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
  // コントローラを構える高さ（床から m）。未設定は 0 = 床に着ける。
  function regTouchHeight() {
    const v = parseFloat(layout.regTouchHeightM);
    return Number.isFinite(v) && v > 0 ? Math.min(REG_TOUCH_H_MAX, v) : REG_TOUCH_H_DEFAULT;
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

  // ---- 通過ライン（演出の発火点）アクセサ ----------------------------------------
  function lineArr() {
    if (!Array.isArray(layout.lines)) layout.lines = [];
    return layout.lines;
  }
  // 新しい id（line_1, line_2, …）。既存の最大番号 + 1（削除しても衝突しない）。
  function nextLineId() {
    let max = 0;
    for (const l of lineArr()) {
      const m = /^line_(\d+)$/.exec(l.id || '');
      if (m) max = Math.max(max, parseInt(m[1], 10));
    }
    return `line_${max + 1}`;
  }
  // 端点 / 線本体のどちらを掴んだか（端点が優先。伸縮したい時に平行移動になると事故る）。
  function lineHit(px, py) {
    const arr = lineArr();
    for (let i = arr.length - 1; i >= 0; i--) {
      const [ax, ay] = courseToPx(arr[i].x1, arr[i].z1);
      const [bx, by] = courseToPx(arr[i].x2, arr[i].z2);
      if (Math.hypot(px - ax, py - ay) <= LINE_END_HIT) return { index: i, part: 'a' };
      if (Math.hypot(px - bx, py - by) <= LINE_END_HIT) return { index: i, part: 'b' };
      if (distToSegmentPx(px, py, ax, ay, bx, by) <= LINE_BODY_HIT) return { index: i, part: 'body' };
    }
    return null;
  }
  // 点と線分の距離 (px)。
  function distToSegmentPx(px, py, ax, ay, bx, by) {
    const dx = bx - ax, dy = by - ay;
    const len2 = dx * dx + dy * dy;
    if (len2 <= 0) return Math.hypot(px - ax, py - ay);
    let t = ((px - ax) * dx + (py - ay) * dy) / len2;
    t = Math.max(0, Math.min(1, t));
    return Math.hypot(px - (ax + dx * t), py - (ay + dy * t));
  }
  // その線が実際に置かれているゾーンのカメラ（実機のゾーン判定と同じ / -1 = 未割当）。
  function lineZoneCamera(l) {
    const { boxes } = zonesFromLayout(layout);
    const m = lineMid(l);
    return cameraAtPoint(boxes, m.x, m.z);
  }
  // ライン → 単位法線（A→B を左に 90°）。向き矢印の描画・通過方向の意味づけに使う。
  function lineNormal(l) {
    const dx = l.x2 - l.x1, dz = l.z2 - l.z1;
    const len = Math.hypot(dx, dz);
    if (len < 1e-6) return { x: 0, z: 0 };
    return { x: dz / len, z: -dx / len };
  }

  // ---- 開始位置（導入演出の発火点）アクセサ ---------------------------------------
  // 1 点だけ。正規化は intro-model.js の normalizeStartSpot が単一の正
  // （本番前チェックと丸め方を分けると「卓の絵と卓の警告が食い違う」が起きる）。
  function startSpot() {
    const s = layout.startSpot;
    return (s && Number.isFinite(s.x) && Number.isFinite(s.z)) ? s : null;
  }
  function startHit(px, py) {
    const s = startSpot(); if (!s) return false;
    const [x, y] = courseToPx(s.x, s.z);
    return Math.hypot(px - x, py - y) <= START_HIT;
  }
  // クリック位置へ置く / 移す。半径とラベルは既にあれば引き継ぐ（置き直しで設定が消えない）。
  function setStartAt(x, z) {
    const cur = startSpot();
    layout.startSpot = {
      x: +x.toFixed(3), z: +z.toFixed(3),
      radiusM: cur ? cur.radiusM : START_SPOT_DEFAULT.radiusM,
      label: cur ? cur.label : START_SPOT_DEFAULT.label,
    };
    // ⚠ 宣言 bool を必ず対で書く。Unity の JsonUtility は **キーが無くても入れ子の実体を作る**ので、
    //   「startSpot が無い」を実体の有無では表せない（未著作でも (0,0) の円が生きて、体験者が
    //   そこに立つと導入が勝手に始まる／正しい場所に立っても始まらない）。
    //   hasRoom / hasPost / hasBgm と同じ流儀。Unity 側は ShowLayoutDef.ResolveStartSpot() が AND で確定する。
    layout.hasStartSpot = true;
    markDirty();
  }
  function clearStart() {
    if (!startSpot()) return;
    delete layout.startSpot;
    layout.hasStartSpot = false;
    markDirty(); renderStart(); render();
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

    // 部屋の 3D プロキシ（壁・箱・部屋の床外周）。位置合わせ点やラインの下に敷く
    // — プロキシは「実物の形」なので、その上に発火点や基準点が乗って見えるのが正しい重なり。
    drawRoom();

    // 位置合わせ点（番号つきマーカー + タッチ順の破線）
    drawRegPoints();

    // カメラ姿勢（著作済みのカメラだけ。📐 モードでは強調 + 向きハンドル）
    drawCameraPoses();

    // 通過ライン（演出の発火点。どのモードでも薄く見せる = 塗りながら位置関係が分かる）
    drawLines();

    // 開始位置（導入演出の発火点。ラインと同じくどのモードでも薄く見せる）
    drawStartSpot();

    // 光が来ている方向（床の外側に出るので最後に描く）
    drawLightArrow();

    // シミュレーションドット
    if (isWalk() && sim) {
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

  // 実カメラの置き場所・向き・画角。CG 人形のパースはこの姿勢で決まるので、
  // 「どこに置いたか」を塗りや位置合わせ点と同じ画面で見られるようにする。
  function drawCameraPoses() {
    const edit = isCamPose();
    for (let i = 0; i < cameras.length; i++) {
      const p = poseOf(i); if (!p) continue;
      const [px, py] = courseToPx(p.x, p.z);
      const d = yawDir(p.yawDeg);
      const col = camColor(i);
      const sel = edit && i === camPoseIndex;

      ctx.save();
      ctx.globalAlpha = edit ? 1 : 0.55;

      // 画角の扇（見えている範囲の目安。人形が入るかを見るために出す）。
      // **水平**画角（hfovDeg）。旧 `fovDeg` は Unity が垂直として使っていて食い違っていたので読まない。
      const fov = Math.max(10, Math.min(170, p.hfovDeg || 70)) * Math.PI / 180;
      const base = Math.atan2(d.dx, d.dz);
      const rad = 74;
      ctx.beginPath();
      ctx.moveTo(px, py);
      // canvas は y 下向き。course の角度 θ（+Z 基準）→ 画面角は (sinθ, -cosθ)。
      for (let t = -fov / 2; t <= fov / 2 + 1e-4; t += fov / 24) {
        const a = base + t;
        ctx.lineTo(px + Math.sin(a) * rad, py - Math.cos(a) * rad);
      }
      ctx.closePath();
      ctx.fillStyle = col;
      ctx.globalAlpha *= 0.14;
      ctx.fill();
      ctx.globalAlpha = edit ? 1 : 0.55;

      // 向きハンドル
      const [hx, hy] = poseHandlePx(i);
      ctx.beginPath(); ctx.moveTo(px, py); ctx.lineTo(hx, hy);
      ctx.strokeStyle = col; ctx.lineWidth = sel ? 3 : 2; ctx.stroke();
      ctx.beginPath(); ctx.arc(hx, hy, sel ? 6 : 4.5, 0, Math.PI * 2);
      ctx.fillStyle = sel ? '#fffaf0' : col; ctx.fill();
      ctx.lineWidth = 1.5; ctx.strokeStyle = col; ctx.stroke();

      // カメラ本体
      ctx.beginPath(); ctx.arc(px, py, sel ? 11 : 9, 0, Math.PI * 2);
      ctx.fillStyle = '#141820'; ctx.fill();
      ctx.lineWidth = sel ? 3 : 2; ctx.strokeStyle = col; ctx.stroke();
      ctx.fillStyle = col; ctx.font = 'bold 11px system-ui, sans-serif';
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText(cameras[i].id || String(i), px, py + 0.5);
      ctx.restore();
    }
  }

  // 通過ライン。編集モードでは端点ハンドルと向き矢印つきで強調する。
  //   色は**担当カメラの色**にする（「このラインはどの区間のものか」が一目で分かるのが要点）。
  //   担当未指定は白の破線（＝設定漏れが目で分かる）。
  function drawLines() {
    const arr = Array.isArray(layout.lines) ? layout.lines : [];
    if (!arr.length && !drawing) return;
    const edit = isLine();
    arr.forEach((l, i) => {
      const [ax, ay] = courseToPx(l.x1, l.z1);
      const [bx, by] = courseToPx(l.x2, l.z2);
      const sel = edit && i === lineDragIndex;
      const col = Number.isInteger(l.camera) && l.camera >= 0 ? camColor(l.camera) : '#fffaf0';
      ctx.save();
      ctx.globalAlpha = edit ? 1 : 0.45;

      // 線本体
      ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
      ctx.strokeStyle = col; ctx.lineWidth = sel ? 5 : 3.5; ctx.lineCap = 'round';
      if (!(Number.isInteger(l.camera) && l.camera >= 0)) ctx.setLineDash([7, 5]);
      ctx.stroke();
      ctx.setLineDash([]);

      // 端点ハンドル（編集モードのみ）
      if (edit) {
        for (const [hx, hy] of [[ax, ay], [bx, by]]) {
          ctx.beginPath(); ctx.arc(hx, hy, 5, 0, Math.PI * 2);
          ctx.fillStyle = '#fffaf0'; ctx.fill();
          ctx.lineWidth = 1.5; ctx.strokeStyle = col; ctx.stroke();
        }
      }

      // 通過方向の矢印（両方向は双頭）
      const mid = [(ax + bx) / 2, (ay + by) / 2];
      const n = lineNormal(l);
      // course の法線 → 画面方向（canvas は y 下向き）
      const nx = n.x, ny = -n.z;
      const arrow = (sign) => {
        const tipX = mid[0] + nx * 20 * sign, tipY = mid[1] + ny * 20 * sign;
        ctx.beginPath(); ctx.moveTo(mid[0], mid[1]); ctx.lineTo(tipX, tipY);
        ctx.strokeStyle = col; ctx.lineWidth = 2; ctx.stroke();
        // 矢じり
        const ang = Math.atan2(tipY - mid[1], tipX - mid[0]);
        ctx.beginPath();
        ctx.moveTo(tipX, tipY);
        ctx.lineTo(tipX - Math.cos(ang - 0.4) * 7, tipY - Math.sin(ang - 0.4) * 7);
        ctx.lineTo(tipX - Math.cos(ang + 0.4) * 7, tipY - Math.sin(ang + 0.4) * 7);
        ctx.closePath(); ctx.fillStyle = col; ctx.fill();
      };
      if (l.dir === LINE_DIR_FWD) arrow(1);
      else if (l.dir === LINE_DIR_BACK) arrow(-1);
      else { arrow(1); arrow(-1); }

      // ラベル
      const name = l.label || l.id || '';
      if (name) {
        ctx.fillStyle = 'rgba(255,250,240,0.92)';
        ctx.font = '10px system-ui, sans-serif';
        ctx.textAlign = 'center'; ctx.textBaseline = 'bottom';
        ctx.fillText(`📏 ${name}`, mid[0], Math.min(ay, by) - 6);
      }
      ctx.restore();
    });

    // 引きかけの線（ドラッグ中のプレビュー）
    if (drawing && drawing.cur) {
      const [ax, ay] = courseToPx(drawing.x, drawing.z);
      const [bx, by] = courseToPx(drawing.cur.x, drawing.cur.z);
      ctx.save();
      ctx.setLineDash([5, 4]);
      ctx.strokeStyle = '#fffaf0'; ctx.lineWidth = 2;
      ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
      ctx.restore();
    }
  }

  // 開始位置。円（半径 = 実寸）と中心の点。編集モードでは実線 + ラベル、他モードでは薄い破線。
  function drawStartSpot() {
    const s = startSpot(); if (!s) return;
    const edit = isStart();
    const [px, py] = courseToPx(s.x, s.z);
    const r = Math.max(3, (Number(s.radiusM) || START_SPOT_DEFAULT.radiusM) * scale);
    ctx.save();
    ctx.globalAlpha = edit ? 1 : 0.4;

    // 半径の円（体験者がこの中に入ったら始まる）
    ctx.beginPath(); ctx.arc(px, py, r, 0, Math.PI * 2);
    ctx.fillStyle = START_COLOR; ctx.globalAlpha *= 0.12; ctx.fill();
    ctx.globalAlpha = edit ? 1 : 0.4;
    ctx.setLineDash(edit ? [] : [5, 4]);
    ctx.strokeStyle = START_COLOR; ctx.lineWidth = edit ? 2 : 1.5; ctx.stroke();
    ctx.setLineDash([]);

    // 中心（掴む場所）
    ctx.beginPath(); ctx.arc(px, py, edit ? 8 : 6, 0, Math.PI * 2);
    ctx.fillStyle = '#141820'; ctx.fill();
    ctx.lineWidth = 2; ctx.strokeStyle = START_COLOR; ctx.stroke();
    ctx.fillStyle = START_COLOR; ctx.font = 'bold 9px system-ui, sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText('▶', px + 0.5, py + 0.5);

    if (edit && s.label) {
      ctx.fillStyle = 'rgba(255,250,240,0.92)';
      ctx.font = '10px system-ui, sans-serif';
      ctx.textBaseline = 'top';
      ctx.fillText(`🎬 ${s.label}`, px, py + r + 4);
    }
    ctx.restore();
  }

  // 通過ラインの一覧（ラベル / 担当カメラ / 通過方向 / 置かれているゾーン / 使用中の演出数 / 削除）。
  function renderLineList() {
    const noteEl = q('.fm-line-note'), listEl = q('.fm-line-list');
    if (!noteEl || !listEl) return;
    const arr = Array.isArray(layout.lines) ? layout.lines : [];
    noteEl.textContent = arr.length
      ? `${arr.length} 本（タイムラインの演出から「このラインを通過したら」で選ぶ）`
      : '未設定 — キャンバスをドラッグして線を引く';
    noteEl.className = 'fm-line-note';
    listEl.innerHTML = '';
    const { boxes } = zonesFromLayout(layout);
    arr.forEach((l, i) => {
      const row = document.createElement('div'); row.className = 'fm-line-item';
      const badge = document.createElement('span'); badge.className = 'fm-line-badge'; badge.textContent = '📏';

      const lab = document.createElement('input');
      lab.type = 'text'; lab.className = 'fm-line-label'; lab.placeholder = l.id; lab.value = l.label || '';
      lab.oninput = () => { l.label = lab.value; markDirty(); render(); };

      // 担当カメラ（実機がこれと区間のカメラを照合する = ここが紐づけの正）
      const cam = document.createElement('select');
      cam.className = 'fm-line-cam'; cam.title = 'このラインを使える区間のカメラ（他のカメラの区間からは選べない）';
      cam.innerHTML = '<option value="-1">担当なし</option>'
        + cameras.map((c, k) => `<option value="${k}"${l.camera === k ? ' selected' : ''}>カメラ ${c.id}</option>`).join('');
      cam.value = String(Number.isInteger(l.camera) ? l.camera : -1);
      cam.onchange = () => { l.camera = parseInt(cam.value, 10); markDirty(); renderLineList(); render(); };

      const dir = document.createElement('select');
      dir.className = 'fm-line-dir'; dir.title = '通過方向（矢印の向き）。両方向が既定';
      dir.innerHTML = `<option value="${LINE_DIR_BOTH}">両方向</option>`
        + `<option value="${LINE_DIR_FWD}">→ 矢印の向き</option>`
        + `<option value="${LINE_DIR_BACK}">← 逆向き</option>`;
      dir.value = l.dir || LINE_DIR_BOTH;
      dir.onchange = () => { l.dir = dir.value; markDirty(); render(); };

      const zone = cameraAtPoint(boxes, lineMid(l).x, lineMid(l).z);
      const info = document.createElement('span');
      const mismatch = Number.isInteger(l.camera) && l.camera >= 0 && zone >= 0 && zone !== l.camera;
      const tooShort = lineLength(l) < LINE_MIN_LENGTH_M;
      const uses = lineUses[l.id] || 0;
      // 鏡像の線は「まだ元の線の鏡か」を数値で出す（canon/LEDGER.md 0102）。
      //   ⚠ 片方を動かしただけでは何も起きないので、**ここに出さないと崩れたことに気づけない**。
      const mSrc = l.mirrorOf ? arr.find((o) => o && o.id === l.mirrorOf) : null;
      const mRes = mSrc ? mirrorResidualM(calibOf(l.camera), mSrc, l) : null;
      const mirrorStale = mRes != null && mRes > MIRROR_TOLERANCE_M;
      info.className = 'fm-line-info'
        + (mismatch || tooShort || mirrorStale || !(l.camera >= 0) ? ' warn' : '');
      info.textContent = tooShort ? '⚠ 短すぎる'
        : mismatch ? `⚠ 線の場所は ${camLabelIdx(zone)}`
        : !(l.camera >= 0) ? '⚠ 担当なし'
        : mirrorStale ? `⚠ 鏡が ${Math.round(mRes * 100)}cm ずれ`
        : mRes != null ? `🪞 ${labelOfId(arr, l.mirrorOf)} の鏡`
        : uses ? `${uses} 演出` : '未使用';
      info.title = tooShort ? `${LINE_MIN_LENGTH_M}m 未満のラインは無効（誤検出のもと）`
        : mismatch ? `担当は ${camLabelIdx(l.camera)} ですが線は ${camLabelIdx(zone)} のゾーンに引かれています。`
          + '体験者がここを通る時の区間と食い違うので、担当を変えるか線を移してください。'
        : !(l.camera >= 0) ? '担当カメラを決めると、その区間の演出からだけ選べるようになります。'
        : mirrorStale ? `元の線「${labelOfId(arr, l.mirrorOf)}」を動かしたので鏡像がずれています。`
          + '元の線の 🪞 を押して作り直してください（このままだと 3 周目の録画が凍結位置と合いません）。'
        : mRes != null ? `「${labelOfId(arr, l.mirrorOf)}」を画像空間で反転した線です`
        : `${uses} 本の演出がこのラインを使っています`;

      // 🪞 この線の鏡像を作る / 作り直す（canon/LEDGER.md 0102）。
      //   3 周目 A は左半分だけライブを左右反転して読むので、「凍結の線」と「録画の起点」は
      //   **画像空間の鏡**で対応していなければならない。course 空間で対称に置いても、
      //   カメラが対称軸の真上に無い限り画面上では対称に写らない（＝ 録画が外から始まる）。
      const mir = document.createElement('button');
      mir.className = 'fm-reg-btn fm-line-mirror'; mir.textContent = '🪞';
      const pairIdx = arr.findIndex((o) => o && o.mirrorOf === l.id);
      const mirCal = calibOf(l.camera);
      const mirUsable = calibUsableForMirror(mirCal);
      mir.disabled = !mirUsable || l.mirrorOf;
      mir.title = l.mirrorOf
        ? `この線は「${labelOfId(arr, l.mirrorOf)}」の鏡像です（元の線の 🪞 で作り直します）`
        : !mirUsable
          ? 'このカメラが較正されていないと鏡へ写せません（🎯 カメラを合わせる）'
          : pairIdx >= 0
            ? '鏡像を作り直す（元の線を動かしたら必ず押す）'
            : '画像空間の鏡へ写した線を作る（3 周目 A の録画の起点）';
      mir.onclick = () => mirrorLineInto(i);

      const del = document.createElement('button');
      del.className = 'fm-reg-btn fm-reg-del'; del.textContent = '🗑'; del.title = '削除';
      del.onclick = () => deleteLine(i);

      row.append(badge, lab, cam, dir, info, mir, del);
      listEl.appendChild(row);
    });
  }

  // 較正（cameras[i].calib）。無ければ null。**pose では鏡へ写せない** —
  // 内部行列（fxPx / cxPx / 歪み）が無いと画素と床の対応が付かない。
  function calibOf(cameraIndex) {
    const c = Number.isInteger(cameraIndex) && cameraIndex >= 0 ? cameras[cameraIndex] : null;
    return c && c.hasCalib !== false && c.calib && c.calib.fxPx > 1 ? c.calib : null;
  }

  function labelOfId(arr, id) {
    const l = (arr || []).find((o) => o && o.id === id);
    return l ? (l.label || l.id) : id;
  }

  /**
   * 線 index の**画像空間の鏡像**を作る / 作り直す（canon/LEDGER.md 0102）。
   *
   * ⚠ 既に鏡像がある（`mirrorOf` がこの線を指す線がある）なら**その線の座標だけ**を書き換える。
   *   新しく作ると、`record.startLineId` や演出が指している id が置き去りになる。
   */
  function mirrorLineInto(i) {
    const arr = lineArr();
    const src = arr[i];
    if (!src) return;
    const calib = calibOf(src.camera);
    const m = mirrorLine(calib, src);
    if (m.error) { alert(`鏡へ写せません: ${m.error}`); return; }

    const side = mirrorSideCheck(calib, src);
    let note = '';
    if (!side.ok) note = `\n\n⚠ ${side.detail}`;

    const at = arr.findIndex((o) => o && o.mirrorOf === src.id);
    if (at >= 0) {
      Object.assign(arr[at], m);
      markDirty(); renderLineList(); render();
      alert(`「${arr[at].label || arr[at].id}」を作り直しました。${note}`);
      return;
    }
    if (arr.length >= LINE_MAX) { alert('線の本数が上限です'); return; }
    arr.push({
      id: nextLineId(),
      camera: src.camera,
      ...m,
      dir: src.dir || LINE_DIR_BOTH,
      label: `${src.label || src.id}（鏡）`,
      mirrorOf: src.id,
    });
    markDirty(); renderLineList(); render();
    alert('鏡像の線を作りました。⏺ 端末内録画パネルで「録り始めの線」にこれを選んでください。'
          + note);
  }

  function deleteLine(i) {
    const arr = lineArr();
    const l = arr[i];
    if (!l) return;
    const uses = lineUses[l.id] || 0;
    if (uses && !confirm(`「${l.label || l.id}」は ${uses} 個の演出が使っています。削除すると、その演出は発火しなくなります。削除しますか？`)) return;
    arr.splice(i, 1);
    lineDragIndex = -1;
    markDirty(); renderLineList(); render();
  }

  // 引き終わった線を追加する（短すぎる線は作らない）。担当カメラは中点のゾーンから自動で決める。
  function addLine(x1, z1, x2, z2) {
    const arr = lineArr();
    if (arr.length >= LINE_MAX) return -1;
    if (Math.hypot(x2 - x1, z2 - z1) < LINE_MIN_LENGTH_M) return -1;
    const { boxes } = zonesFromLayout(layout);
    const camera = cameraAtPoint(boxes, (x1 + x2) / 2, (z1 + z2) / 2);
    arr.push({
      id: nextLineId(),
      camera,
      x1: +x1.toFixed(3), z1: +z1.toFixed(3), x2: +x2.toFixed(3), z2: +z2.toFixed(3),
      dir: LINE_DIR_BOTH,
      label: `ライン${arr.length + 1}`,
    });
    markDirty(); renderLineList();
    return arr.length - 1;
  }

  // 現在の regPoints 座標をサマリ 1 行で表示。
  function updateRegCoords() {
    const a = regArr();
    regCoordsEl.textContent = (a && a.length)
      ? a.map((p, i) => `${regBadge(i)}(${p.x.toFixed(2)}, ${p.z.toFixed(2)})`).join('  ')
      : '';
  }

  // ---- 部屋の 3D プロキシ（layout.room）-----------------------------------------
  //   実物と同じ位置に置く不可視の幾何。**1 つの幾何が 3 用途を兼ねる**（CG のオクルーダ /
  //   影の落ち先 / 較正の参照）。別々に作ると必ずズレるので、ここが唯一の著作面。
  //   幾何・既定値・退化の判定は room-model.js（Unity の ShowRoomProxy / ShowRoomDef のミラー）。
  const ROOM_END_HIT = 11;      // 壁の端点・箱の角の掴み判定 (px)
  const ROOM_BODY_HIT = 8;      // 壁本体の掴み判定 (px)
  // ドラッグで新規作成する最小サイズ (m)。room-model の退化しきい値（1cm）より**わざと大きい**
  // — クリックのつもりの数 px が「実機で見えない極小の壁」になると、消し方が分からず残り続ける。
  const ROOM_DRAW_MIN_M = 0.05;
  const ROOM_WALL_COLOR = GEO.wall;    // 較正 UI のワイヤー配色（cu-wirekey .k-wall）と揃える
  const ROOM_PROP_COLOR = '#9fd8a8';

  function roomWalls() { return roomModel ? roomModel.walls : []; }
  function roomProps() { return roomModel ? roomModel.props : []; }
  function roomLight() { return roomModel ? roomModel.light : defaultLight(); }
  // 部屋を触ったら著作済みにする。**触っていないのに hasRoom を立てない**のが要点で、
  // 未著作の部屋を書き出すと Unity が「著作された既定の部屋」として組んでしまう。
  function markRoomDirty() {
    roomAuthored = true;
    markDirty();
  }
  function selectedRoomItem() {
    if (!roomSel) return null;
    const list = roomSel.kind === 'wall' ? roomWalls() : roomProps();
    return list.find((o) => o.id === roomSel.id) || null;
  }

  // 点 (px) が多角形（course 座標の四隅）の中か。ray casting。
  function pointInFootprint(px, py, pts) {
    let inside = false;
    for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
      const [ax, ay] = courseToPx(pts[i].x, pts[i].z);
      const [bx, by] = courseToPx(pts[j].x, pts[j].z);
      if ((ay > py) !== (by > py) && px < ((bx - ax) * (py - ay)) / (by - ay) + ax) inside = !inside;
    }
    return inside;
  }

  // 何を掴んだか。**端点・角が本体より優先**（伸縮したい時に平行移動になると事故る）。
  function roomHit(px, py) {
    const walls = roomWalls(), props = roomProps();
    for (let i = walls.length - 1; i >= 0; i--) {
      const [ax, ay] = courseToPx(walls[i].x1, walls[i].z1);
      const [bx, by] = courseToPx(walls[i].x2, walls[i].z2);
      if (Math.hypot(px - ax, py - ay) <= ROOM_END_HIT) return { kind: 'wall', id: walls[i].id, part: 'a' };
      if (Math.hypot(px - bx, py - by) <= ROOM_END_HIT) return { kind: 'wall', id: walls[i].id, part: 'b' };
    }
    for (let i = props.length - 1; i >= 0; i--) {
      const fp = boxFootprint(props[i]);
      for (let k = 0; k < fp.length; k++) {
        const [cx, cy] = courseToPx(fp[k].x, fp[k].z);
        if (Math.hypot(px - cx, py - cy) <= ROOM_END_HIT) return { kind: 'prop', id: props[i].id, part: `c${k}` };
      }
    }
    for (let i = walls.length - 1; i >= 0; i--) {
      const [ax, ay] = courseToPx(walls[i].x1, walls[i].z1);
      const [bx, by] = courseToPx(walls[i].x2, walls[i].z2);
      if (distToSegmentPx(px, py, ax, ay, bx, by) <= ROOM_BODY_HIT) return { kind: 'wall', id: walls[i].id, part: 'body' };
    }
    for (let i = props.length - 1; i >= 0; i--) {
      if (pointInFootprint(px, py, boxFootprint(props[i]))) return { kind: 'prop', id: props[i].id, part: 'body' };
    }
    return null;
  }

  // 箱のローカル軸（Unity の Euler Y と同じ: ローカル +X=(cos,-sin) / +Z=(sin,cos)）。
  function boxAxes(b) {
    const yaw = (b.yawDeg || 0) * Math.PI / 180;
    const c = Math.cos(yaw), s = Math.sin(yaw);
    return { ex: { x: c, z: -s }, ez: { x: s, z: c } };
  }

  function deleteRoomItem(kind, id) {
    const list = kind === 'wall' ? roomWalls() : roomProps();
    const i = list.findIndex((o) => o.id === id);
    if (i < 0) return;
    list.splice(i, 1);
    if (roomSel && roomSel.id === id) roomSel = null;
    markRoomDirty(); renderRoom(); render();
  }

  // ---- 部屋の描画 -------------------------------------------------------------
  //   どのモードでも薄く描く（塗りながら / 線を引きながら実物との位置関係が見える）。
  function drawRoom() {
    if (!roomModel) return;
    const edit = isRoom();
    const ghost = !roomAuthored;
    ctx.save();
    ctx.globalAlpha = edit ? 1 : (ghost ? 0.22 : 0.4);

    // 部屋の床外周（layout.floor の外周とは別データ。ズレていたら目で分かるように破線で出す）
    if (roomHasData(roomModel)) {
      const f = floorRect(roomModel);
      const [x0, y0] = courseToPx(f.xLo, f.zHi);
      const [x1, y1] = courseToPx(f.xHi, f.zLo);
      ctx.setLineDash([4, 4]);
      ctx.strokeStyle = ROOM_WALL_COLOR; ctx.lineWidth = 1;
      ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
      ctx.setLineDash([]);
    }

    for (const w of roomWalls()) drawRoomShape(wallFootprint(w), ROOM_WALL_COLOR, edit,
      roomSel && roomSel.kind === 'wall' && roomSel.id === w.id, !isWallUsable(w));
    for (const b of roomProps()) drawRoomShape(boxFootprint(b), ROOM_PROP_COLOR, edit,
      roomSel && roomSel.kind === 'prop' && roomSel.id === b.id, !isBoxUsable(b));

    // 壁の端点ハンドル（編集モードのみ。箱は四隅がそのままハンドル）
    if (edit) {
      for (const w of roomWalls()) {
        for (const [hx, hy] of [courseToPx(w.x1, w.z1), courseToPx(w.x2, w.z2)]) {
          ctx.beginPath(); ctx.arc(hx, hy, 4.5, 0, Math.PI * 2);
          ctx.fillStyle = '#fffaf0'; ctx.fill();
          ctx.lineWidth = 1.5; ctx.strokeStyle = ROOM_WALL_COLOR; ctx.stroke();
        }
      }
    }

    // 引きかけ（壁は線・箱は矩形のプレビュー）
    if (roomDrawing && roomDrawing.cur) {
      ctx.setLineDash([5, 4]);
      ctx.strokeStyle = roomTool === 'wall' ? ROOM_WALL_COLOR : ROOM_PROP_COLOR;
      ctx.lineWidth = 2;
      const [ax, ay] = courseToPx(roomDrawing.x, roomDrawing.z);
      const [bx, by] = courseToPx(roomDrawing.cur.x, roomDrawing.cur.z);
      ctx.beginPath();
      if (roomTool === 'wall') { ctx.moveTo(ax, ay); ctx.lineTo(bx, by); }
      else ctx.rect(Math.min(ax, bx), Math.min(ay, by), Math.abs(bx - ax), Math.abs(by - ay));
      ctx.stroke();
      ctx.setLineDash([]);
    }
    ctx.restore();
  }

  // footprint（四隅）を塗り + 縁で描く。退化しているものは赤の破線（＝保存されないことを目で示す）。
  function drawRoomShape(pts, color, edit, selected, degenerate) {
    if (!pts.length) return;
    ctx.save();
    ctx.beginPath();
    pts.forEach((p, i) => { const [x, y] = courseToPx(p.x, p.z); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
    ctx.closePath();
    ctx.fillStyle = color;
    ctx.globalAlpha *= selected ? 0.42 : 0.24;
    ctx.fill();
    ctx.restore();

    ctx.save();
    ctx.beginPath();
    pts.forEach((p, i) => { const [x, y] = courseToPx(p.x, p.z); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
    ctx.closePath();
    if (degenerate) { ctx.setLineDash([4, 3]); ctx.strokeStyle = '#ff6b5e'; }
    else ctx.strokeStyle = color;
    ctx.lineWidth = selected && edit ? 2.5 : 1.5;
    ctx.stroke();
    ctx.restore();
  }

  // 光が来ている方向を ☀ で描く。**方向が一目で分かる**ことが要点で、精密さは要らない
  // （監視カメラ画質で観客が読めるのは主光源の向きと影の濃さだけ）。
  function drawLightArrow() {
    const l = roomLight();
    const dir = lightDirCourse(l.yawDeg, l.pitchDeg);
    const horiz = Math.hypot(dir.x, dir.z);
    const authored = !!(roomModel && roomModel.hasLight);
    const col = authored ? kelvinToCss(l.tempK) : 'rgba(255,250,240,0.5)';
    const R = SIZE / 2 - PAD + 24;
    ctx.save();
    ctx.globalAlpha = authored ? (isRoom() ? 1 : 0.7) : 0.35;
    const cx = SIZE / 2, cy = SIZE / 2;
    // 仰角が高いほど光源は中心へ寄る（真上なら中心の ☀ だけ = 方位角に意味が無い状態が見える）
    const r = R * horiz;
    const sx = cx + dir.x * r, sy = cy - dir.z * r;
    if (r > 14) {
      const ang = Math.atan2(cy - sy, cx - sx);
      const tipX = sx + Math.cos(ang) * (r - 26), tipY = sy + Math.sin(ang) * (r - 26);
      ctx.beginPath(); ctx.moveTo(sx, sy); ctx.lineTo(tipX, tipY);
      ctx.strokeStyle = col; ctx.lineWidth = 2; ctx.setLineDash([6, 4]); ctx.stroke();
      ctx.setLineDash([]);
      ctx.beginPath();
      ctx.moveTo(tipX, tipY);
      ctx.lineTo(tipX - Math.cos(ang - 0.4) * 8, tipY - Math.sin(ang - 0.4) * 8);
      ctx.lineTo(tipX - Math.cos(ang + 0.4) * 8, tipY - Math.sin(ang + 0.4) * 8);
      ctx.closePath(); ctx.fillStyle = col; ctx.fill();
    }
    ctx.beginPath(); ctx.arc(sx, sy, 8, 0, Math.PI * 2);
    ctx.fillStyle = col; ctx.fill();
    ctx.lineWidth = 1.5; ctx.strokeStyle = 'rgba(0,0,0,0.65)'; ctx.stroke();
    ctx.fillStyle = 'rgba(255,250,240,0.85)'; ctx.font = '10px system-ui, sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'top';
    ctx.fillText(`☀ ${Math.round(l.pitchDeg)}°`, sx, sy + 10);
    ctx.restore();
  }

  // ---- 部屋の UI --------------------------------------------------------------
  function renderRoom() { renderRoomNote(); renderRoomList(); renderRoomSel(); }

  function renderRoomNote() {
    const el = q('.fm-room-note'); if (!el) return;
    const nw = roomWalls().filter(isWallUsable).length;
    const np = roomProps().filter(isBoxUsable).length;
    if (!roomAuthored) {
      el.textContent = '未設定 — 壁か箱を引くと著作されます（それまで Unity は床だけの既定で影を落とします）';
      el.className = 'fm-room-note';
    } else if (!roomHasData(roomModel)) {
      el.textContent = `⚠ 床が小さすぎます（${roomModel.floorW}×${roomModel.floorD}m）— このままでは Unity が部屋ごと無視します`;
      el.className = 'fm-room-note warn';
    } else {
      el.textContent = `壁 ${nw} 枚 / 箱 ${np} 個（床 ${roomModel.floorW}×${roomModel.floorD}m）`;
      el.className = 'fm-room-note';
    }
  }

  function renderRoomList() {
    const el = q('.fm-room-list'); if (!el) return;
    el.innerHTML = '';
    const items = [
      ...roomWalls().map((w) => ({ kind: 'wall', o: w })),
      ...roomProps().map((b) => ({ kind: 'prop', o: b })),
    ];
    if (!items.length) return;
    for (const { kind, o } of items) {
      const row = document.createElement('div');
      row.className = 'fm-room-item' + (roomSel && roomSel.kind === kind && roomSel.id === o.id ? ' on' : '');
      const badge = document.createElement('span');
      badge.className = 'fm-room-badge';
      badge.textContent = kind === 'wall' ? '▬' : '◻';
      badge.style.color = kind === 'wall' ? ROOM_WALL_COLOR : ROOM_PROP_COLOR;

      const pick = document.createElement('button');
      pick.className = 'fm-room-pick';
      pick.textContent = kind === 'wall'
        ? `${o.id} — 長さ ${wallLength(o).toFixed(2)}m / 高さ ${(+o.h).toFixed(2)}m / 厚み ${(+o.thick).toFixed(3)}m`
        : `${o.id} — ${(+o.w).toFixed(2)}×${(+o.d).toFixed(2)}m / 高さ ${(+o.h).toFixed(2)}m / ${Math.round(o.yawDeg || 0)}°`;
      pick.onclick = () => { roomSel = { kind, id: o.id }; setMapMode('room'); renderRoom(); render(); };

      const bad = (kind === 'wall' ? !isWallUsable(o) : !isBoxUsable(o));
      if (bad) {
        const warn = document.createElement('span');
        warn.className = 'fm-room-info warn';
        warn.textContent = '⚠ 保存されません';
        warn.title = '潰れた壁 / 箱は Unity が黙って捨てるので、卓も書き出しません（1cm 以上にしてください）';
        row.append(badge, pick, warn);
      } else row.append(badge, pick);

      const del = document.createElement('button');
      del.className = 'fm-reg-btn fm-reg-del'; del.textContent = '🗑'; del.title = '削除';
      del.onclick = () => deleteRoomItem(kind, o.id);
      row.append(del);
      el.appendChild(row);
    }
  }

  // 選択中の 1 個だけを数値で詰める欄。**選択していない時は何も出さない**
  // （全部の壁の高さ欄を並べると、どれを触っているのか分からなくなる）。
  function renderRoomSel() {
    const el = q('.fm-room-sel'); if (!el) return;
    el.innerHTML = '';
    const o = selectedRoomItem();
    if (!o) {
      el.textContent = roomWalls().length || roomProps().length
        ? '（マップか一覧で壁 / 箱を選ぶと寸法を調整できます）' : '';
      el.className = 'fm-room-sel hint-only';
      return;
    }
    el.className = 'fm-room-sel';
    const head = document.createElement('div');
    head.className = 'fm-course-label';
    head.textContent = roomSel.kind === 'wall' ? `選択中: 壁 ${o.id}` : `選択中: 箱 ${o.id}`;
    el.appendChild(head);

    const numField = (label, min, max, step, get, set) => {
      const lab = document.createElement('label');
      lab.className = 'fm-num';
      lab.appendChild(document.createTextNode(label));
      const inp = document.createElement('input');
      inp.type = 'number'; inp.min = String(min); inp.max = String(max); inp.step = String(step);
      inp.value = String(get());
      inp.oninput = () => {
        const v = parseFloat(inp.value);
        if (!Number.isFinite(v)) return;
        set(Math.max(min, Math.min(max, v)));
        markRoomDirty(); renderRoomNote(); renderRoomList(); render();
      };
      lab.appendChild(inp);
      return lab;
    };

    const row = document.createElement('div'); row.className = 'fm-row';
    if (roomSel.kind === 'wall') {
      row.append(
        numField('高さ (m)', MIN_DIM_M, 4, 0.05, () => o.h, (v) => { o.h = v; }),
        numField('厚み (m)', 0.005, 0.5, 0.005, () => o.thick, (v) => { o.thick = v; }));
    } else {
      row.append(
        numField('高さ (m)', MIN_DIM_M, 4, 0.05, () => o.h, (v) => { o.h = v; }),
        numField('向き (°)', -180, 180, 1, () => Math.round(o.yawDeg || 0), (v) => { o.yawDeg = v; }));
    }
    el.appendChild(row);

    const meta = document.createElement('div');
    meta.className = 'fm-room-info';
    meta.textContent = roomSel.kind === 'wall'
      ? `長さ ${wallLength(o).toFixed(2)}m（マップで端点をドラッグ）`
      : `${(+o.w).toFixed(2)}×${(+o.d).toFixed(2)}m（マップで角をドラッグ）`;
    el.appendChild(meta);
  }

  // ---- 照明 UI ----------------------------------------------------------------
  //   ⚠ applied:false = **Unity がまだこの値を読んでいない**（ShowCgLayer が使うのは
  //   yawDeg / pitchDeg / shadowDensity の 3 つだけ）。スライダを動かして何も変わらない時に
  //   「壊れている」と誤解しないよう、著作面のほうで正直に出す。
  const LIGHT_FIELDS = [
    { key: 'yawDeg', label: '向き（方位角）', unit: '°', applied: true,
      hint: 'マップ上の ☀ が光源。course の +Z（マップ上＝北）が 0 で、+X（東）へ増える' },
    { key: 'pitchDeg', label: '高さ（仰角）', unit: '°', applied: true,
      hint: '0 = 真横から / 90 = 真上から。天井照明なら 50〜70° あたり' },
    { key: 'tempK', label: '色温度', unit: 'K', applied: true,
      hint: '蛍光灯 4000K / 電球 2700K / 昼光 6500K' },
    { key: 'intensity', label: '強さ', unit: '', applied: true,
      hint: '1 が基準。上げると人形が明るく、下げると沈む' },
    { key: 'ambient', label: '環境光', unit: '', applied: true,
      hint: '影側をどれだけ持ち上げるか（0 = 影が真っ黒）' },
    { key: 'shadowDensity', label: '影の濃さ', unit: '', applied: true,
      hint: '0 = 影なし。人形が床に着いて見えるかを決める一番効く値' },
    { key: 'shadowSoftM', label: '影のにじみ', unit: 'm', applied: true,
      hint: '接地影（足元）の縁のぼけ幅。床への投影シャドウの形は変わらない' },
  ];

  function renderLight() {
    const rowsEl = q('.fm-light-rows'), noteEl = q('.fm-light-note');
    if (!rowsEl) return;
    const l = roomLight();
    const authored = !!(roomModel && roomModel.hasLight);
    if (noteEl) {
      noteEl.textContent = authored
        ? '著作済み（この値で人形の陰影と影が決まります）'
        : '未設定 — スライダを動かすと著作されます（それまでは Unity 内蔵の既定）';
      noteEl.className = 'fm-light-note' + (authored ? '' : ' ghost');
    }
    rowsEl.innerHTML = '';
    for (const f of LIGHT_FIELDS) {
      const r = LIGHT_RANGE[f.key];
      const row = document.createElement('div');
      row.className = 'fm-light-row' + (f.applied ? '' : ' pending');

      const lab = document.createElement('span');
      lab.className = 'fm-light-lbl';
      lab.textContent = f.label + (f.applied ? '' : '（未適用）');
      lab.title = (f.hint ? f.hint + '\n' : '')
        + (f.applied ? '' : '⚠ Unity 側がまだこの値を読んでいません（保存はされます）。動かしても実機の見た目は変わりません。');

      const range = document.createElement('input');
      range.type = 'range'; range.min = String(r.min); range.max = String(r.max); range.step = String(r.step);
      range.value = String(l[f.key]);

      const numI = document.createElement('input');
      numI.type = 'number'; numI.className = 'fm-light-num';
      numI.min = String(r.min); numI.max = String(r.max); numI.step = String(r.step);
      numI.value = String(l[f.key]);

      const apply = (raw) => {
        const v = parseFloat(raw);
        if (!Number.isFinite(v)) return;
        const clamped = Math.max(r.min, Math.min(r.max, v));
        l[f.key] = clamped;
        range.value = String(clamped); numI.value = String(clamped);
        if (roomModel) roomModel.hasLight = true;
        markRoomDirty();
        renderLightSwatch(); renderRoomNote();
        if (noteEl) { noteEl.textContent = '著作済み（この値で人形の陰影と影が決まります）'; noteEl.className = 'fm-light-note'; }
        render();
      };
      range.oninput = () => apply(range.value);
      numI.oninput = () => apply(numI.value);

      const unit = document.createElement('span');
      unit.className = 'fm-light-unit'; unit.textContent = f.unit;

      row.append(lab, range, numI, unit);
      if (f.key === 'tempK') {
        const sw = document.createElement('i');
        sw.className = 'fm-light-sw';
        row.appendChild(sw);
      }
      rowsEl.appendChild(row);
    }
    renderLightSwatch();
  }

  function renderLightSwatch() {
    const sw = q('.fm-light-sw');
    if (sw) sw.style.background = kelvinToCss(roomLight().tempK);
  }

  // ---- カメラ姿勢（CG レイヤの視点）------------------------------------------
  // pose は「著作したカメラだけ」が持つ（Unity は pose の有無で CG を出すか決める）。
  const POSE_DEFAULT = { x: 0, z: 0, y: 1.2, yawDeg: 0, pitchDeg: 0, hfovDeg: 70 };
  const POSE_HIT = 14, POSE_HANDLE_PX = 40, POSE_HANDLE_HIT = 12;
  const isFxCam = (i) => !!(cameras[i] && cameras[i].role === 'fx');

  function poseOf(i) { return cameras[i] ? cameras[i].pose : null; }
  // yaw（course +Z が 0・+X 方向へ増える）→ マップ上のピクセル方向。
  function yawDir(yawDeg) {
    const r = (yawDeg || 0) * Math.PI / 180;
    return { dx: Math.sin(r), dz: Math.cos(r) };
  }
  function poseHandlePx(i) {
    const p = poseOf(i); if (!p) return null;
    const [px, py] = courseToPx(p.x, p.z);
    const d = yawDir(p.yawDeg);
    return [px + d.dx * POSE_HANDLE_PX, py - d.dz * POSE_HANDLE_PX];
  }
  // px 座標が掴んでいるもの（カメラ本体 / 向きハンドル）を返す。
  function poseHit(px, py) {
    for (let i = 0; i < cameras.length; i++) {
      const p = poseOf(i); if (!p) continue;
      const h = poseHandlePx(i);
      if (h && Math.hypot(px - h[0], py - h[1]) <= POSE_HANDLE_HIT) return { index: i, part: 'yaw' };
    }
    for (let i = 0; i < cameras.length; i++) {
      const p = poseOf(i); if (!p) continue;
      const [cx, cy] = courseToPx(p.x, p.z);
      if (Math.hypot(px - cx, py - cy) <= POSE_HIT) return { index: i, part: 'pos' };
    }
    return null;
  }
  // 📐 モードのカメラ一覧（選択・未設定へ戻す）。マップの印と同じ色で並べる。
  function renderCamPoseList() {
    const el = q('.fm-campose-list'); if (!el) return;
    el.innerHTML = '';
    if (!cameras.length) { el.textContent = 'カメラがありません'; return; }
    cameras.forEach((c, i) => {
      const row = document.createElement('div');
      row.className = 'fm-campose-row' + (i === camPoseIndex ? ' on' : '');
      const sw = document.createElement('i');
      sw.className = 'fm-campose-sw'; sw.style.background = camColor(i);
      const p = c.pose;
      const label = document.createElement('button');
      label.className = 'fm-campose-pick';
      label.textContent = `カメラ ${c.id}${c.role === 'fx' ? '（演出専用）' : ''}`
        + (p ? ` — (${(+p.x).toFixed(2)}, ${(+p.z).toFixed(2)}) ${Math.round(p.yawDeg || 0)}°` : ' — 未設定');
      label.onclick = () => { camPoseIndex = i; renderCamPoseList(); render(); };
      row.append(sw, label);
      if (p) {
        const clr = document.createElement('button');
        clr.className = 'fm-campose-clear'; clr.textContent = '✕'; clr.title = '姿勢を未設定に戻す';
        clr.onclick = () => { delete cameras[i].pose; saveCameras(); renderCamPoseList(); render(); };
        row.append(clr);
      }
      el.appendChild(row);
    });
  }

  function ensurePose(i) {
    if (!cameras[i]) return null;
    if (!cameras[i].pose) cameras[i].pose = { ...POSE_DEFAULT };
    return cameras[i].pose;
  }
  // カメラの変更は layout と違い「その場保存」（カメラ列の 📐 欄と同じ挙動）。
  function saveCameras() {
    if (deps && typeof deps.saveCameras === 'function') deps.saveCameras(cameras);
    window.dispatchEvent(new CustomEvent('fc-cameras-changed'));
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
      // 演出専用カメラ（role:"fx" = カメラ D）はゾーンに割り当てない。塗れてしまうと
      // 「周回に出てこないカメラ」という前提そのものが壊れるのでパレットに出さない。
      if (isFxCam(i)) continue;
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
    if (regHeightI && document.activeElement !== regHeightI) regHeightI.value = regTouchHeight();
    updateRegCoords();
  }

  if (regHeightI) {
    regHeightI.onchange = () => {
      const v = parseFloat(regHeightI.value);
      const h = Number.isFinite(v) && v > 0 ? Math.min(REG_TOUCH_H_MAX, v) : REG_TOUCH_H_DEFAULT;
      layout.regTouchHeightM = +h.toFixed(3);
      regHeightI.value = layout.regTouchHeightM;
      markDirty();
    };
  }

  regAddBtn.onclick = () => {
    if (isRegUnset()) {
      layout.regPoints = clone(DEFAULT_REG_POINTS);
    } else {
      const a = regArr();
      if (a.length >= REG_MAX) return;
      a.push({ x: +(-0.3 + 0.2 * (a.length % 3)).toFixed(3), z: 0, label: '' });
    }
    setMapMode('reg');
    markDirty(); renderRegList(); render();
  };

  // ---- 部屋パネルのボタン・数値欄 -------------------------------------------------
  container.querySelectorAll('.fm-room-tool').forEach((b) => {
    b.onclick = () => {
      roomTool = b.dataset.tool;
      container.querySelectorAll('.fm-room-tool').forEach((x) => x.classList.toggle('on', x.dataset.tool === roomTool));
      modeHint.textContent = roomModeHint();
    };
  });
  const roomFwI = q('.fm-room-fw'), roomFdI = q('.fm-room-fd');
  roomFwI.onchange = () => {
    const v = parseFloat(roomFwI.value);
    if (!Number.isFinite(v) || !roomModel) return;
    roomModel.floorW = Math.max(0.1, v);
    roomFwI.value = String(roomModel.floorW);
    markRoomDirty(); renderRoomNote(); render();
  };
  roomFdI.onchange = () => {
    const v = parseFloat(roomFdI.value);
    if (!Number.isFinite(v) || !roomModel) return;
    roomModel.floorD = Math.max(0.1, v);
    roomFdI.value = String(roomModel.floorD);
    markRoomDirty(); renderRoomNote(); render();
  };
  // L 字壁（layout.wall）→ プロキシの壁 2 本。**layout.wall は消さない**
  // （HMD 位置合わせリチュアルのワイヤー表示がまだ読んでいる。ここは移行ではなく取り込み）。
  q('.fm-room-import').onclick = () => {
    const walls = wallsFromLegacyWall(layout.wall || DEFAULT_LAYOUT.wall);
    if (!walls.length) return;
    if (roomWalls().length
        && !confirm(`いまの壁 ${roomWalls().length} 枚を、L 字壁から起こした ${walls.length} 枚で置き換えますか？`)) return;
    roomModel.walls = walls;
    roomSel = { kind: 'wall', id: walls[0].id };
    markRoomDirty(); renderRoom(); render();
  };
  q('.fm-room-clear').onclick = () => {
    if (!roomAuthored && !roomWalls().length && !roomProps().length) return;
    if (!confirm('部屋を未著作に戻します（壁・箱・照明の設定を捨てます）。よろしいですか？')) return;
    const seeded = roomFromLayout({ floor: layout.floor });
    roomModel = seeded.room;
    roomAuthored = false;
    roomSel = null;
    markDirty();   // markRoomDirty ではない（hasRoom を立て直してしまう）
    syncRoomInputs(); renderRoom(); renderLight(); render();
  };
  q('.fm-light-clear').onclick = () => {
    if (!roomModel || !roomModel.hasLight) return;
    roomModel.light = defaultLight();
    roomModel.hasLight = false;
    markDirty();   // 照明を捨てただけで部屋の著作状態は変えない
    renderLight(); render();
  };
  function syncRoomInputs() {
    if (!roomModel) return;
    if (document.activeElement !== roomFwI) roomFwI.value = String(roomModel.floorW);
    if (document.activeElement !== roomFdI) roomFdI.value = String(roomModel.floorD);
  }

  // ---- 開始位置パネル -------------------------------------------------------------
  const startNoteEl = q('.fm-start-note'), startRadiusI = q('.fm-start-r'), startLabelI = q('.fm-start-label');
  function renderStart() {
    const s = startSpot();
    if (s) {
      startNoteEl.textContent = `(${(+s.x).toFixed(2)}, ${(+s.z).toFixed(2)}) / 半径 ${(+s.radiusM).toFixed(2)}m`;
      startNoteEl.className = 'fm-start-note';
    } else {
      startNoteEl.textContent = '未設定 — 自動では始まりません（スタッフが卓の「導入を進める」で開始）。'
        + 'キャンバスをクリックすると置けます。';
      startNoteEl.className = 'fm-start-note warn';
    }
    if (document.activeElement !== startRadiusI) {
      startRadiusI.value = s ? s.radiusM : START_SPOT_DEFAULT.radiusM;
    }
    if (document.activeElement !== startLabelI) startLabelI.value = s ? (s.label || '') : '';
    startRadiusI.disabled = !s;
    startLabelI.disabled = !s;
    q('.fm-start-del').disabled = !s;
  }
  startRadiusI.onchange = () => {
    const s = startSpot(); if (!s) return;
    const v = parseFloat(startRadiusI.value);
    s.radiusM = Number.isFinite(v)
      ? Math.min(START_RADIUS_MAX, Math.max(START_RADIUS_MIN, v))
      : START_SPOT_DEFAULT.radiusM;
    startRadiusI.value = s.radiusM;
    markDirty(); renderStart(); render();
  };
  // ラベルはリストを再構築しない（フォーカス保持。位置合わせ点のラベル入力と同じ流儀）。
  startLabelI.oninput = () => {
    const s = startSpot(); if (!s) return;
    s.label = startLabelI.value;
    markDirty(); render();
  };
  q('.fm-start-del').onclick = () => clearStart();

  // ---- マップの操作モード（🖌 塗る / 📍 位置合わせ点 / 🚶 歩かせる）----------------
  const MODE_HINT = {
    paint: 'パレットの色でタイルをクリック / ドラッグ。色 = 担当カメラ。',
    reg: 'クリックで点を置く / ドラッグで移動 / 右クリックで削除。番号 = HMD でタッチする順。',
    walk: 'マップのどこでも押した場所に体験者が立ちます。押したまま動かすと歩きます。',
    cam: 'カメラ印をドラッグで移動 / 矢印の先をドラッグで向き。何も無い所をクリックすると選択中カメラをそこへ置きます。高さ・俯角・水平画角はカメラ列の 📐 欄。',
    line: 'ドラッグで発火点のラインを引く / 端点をドラッグで伸縮 / 線をドラッグで平行移動 / 右クリックで削除。',
    room: '',   // 引くもの（壁 / 箱）で文言が変わるので roomModeHint() が組み立てる
    start: 'クリックで開始位置を置く（1 点だけ・2 回目は移動）/ ドラッグで移動 / 右クリックで削除。',
  };
  // 部屋モードのヒントは「いま何を引くか」で変わる（壁と箱で操作が違う）。
  function roomModeHint() {
    return roomTool === 'wall'
      ? 'ドラッグで壁を引く / 端点をドラッグで伸縮 / 壁をドラッグで平行移動 / 右クリックで削除。高さ・厚みは下の欄。'
      : 'ドラッグで箱（机・柱）を作る / 角をドラッグでサイズ変更 / 箱をドラッグで移動 / 右クリックで削除。高さ・向きは下の欄。';
  }
  function setMapMode(next, opt) {
    if (MODE_HINT[next] === undefined) return;
    mapMode = next;
    container.querySelectorAll('.fm-mode').forEach((b) => b.classList.toggle('on', b.dataset.mode === next));
    canvas.classList.toggle('fm-reg-edit', isReg());
    canvas.classList.toggle('fm-walk', isWalk());
    canvas.classList.toggle('fm-campose-edit', isCamPose());
    canvas.classList.toggle('fm-line-edit', isLine());
    canvas.classList.toggle('fm-room-edit', isRoom());
    canvas.classList.toggle('fm-start-edit', isStart());
    modeHint.textContent = isRoom() ? roomModeHint() : MODE_HINT[next];
    const camPanel = q('.fm-campose');
    if (camPanel) camPanel.style.display = isCamPose() ? '' : 'none';
    const linePanel = q('.fm-lines');
    if (linePanel) linePanel.style.display = isLine() ? '' : 'none';
    const roomPanel = q('.fm-room');
    if (roomPanel) roomPanel.style.display = isRoom() ? '' : 'none';
    const startPanel = q('.fm-start');
    if (startPanel) startPanel.style.display = isStart() ? '' : 'none';
    if (isCamPose()) renderCamPoseList();
    if (isLine()) renderLineList();
    if (isRoom()) { syncRoomInputs(); renderRoom(); }
    if (isStart()) renderStart();
    // 歩かせるモードに入った時、ドットが無ければ南辺中央に出す（旧チェックボックスと同じ初期位置）。
    if (isWalk() && !sim && !(opt && opt.keepPos)) sim = { x: 0, z: -0.7 };
    updateSimOut(); render(); emitSim();
  }
  container.querySelectorAll('.fm-mode').forEach((b) => { b.onclick = () => setMapMode(b.dataset.mode); });

  overlapI.onchange = () => { layout.overlapM = Math.max(0, parseFloat(overlapI.value) || 0); markDirty(); };
  hystI.onchange = () => { layout.hysteresisM = Math.max(0, parseFloat(hystI.value) || 0); markDirty(); };

  // ショーシミュレーション（show-sim.js）へドットの有効状態と位置を流す。
  // deps.onSim 未指定なら従来どおり「タイルの色を読むだけ」の表示で完結する。
  function emitSim() {
    if (!deps || typeof deps.onSim !== 'function') return;
    deps.onSim({ enabled: isWalk(), x: sim ? sim.x : null, z: sim ? sim.z : null });
  }
  function camLabel(cam) {
    return cam !== null ? (cameras[cam] ? `カメラ ${cameras[cam].id}` : `#${cam}`) : '未割当';
  }
  function updateSimOut() {
    if (!isWalk() || !sim) { simOut.textContent = ''; return; }
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
    // 構える高さ: 入力途中の値をそのまま書かない（範囲へ丸めてから保存する）。
    layout.regTouchHeightM = regTouchHeight();
    // 通過ライン: 正規化（id 必須 / 座標が有限 / 重複 id を落とす）。空なら書かない（未著作）。
    const lines = linesFromLayout(layout);
    if (lines.length) layout.lines = lines; else delete layout.lines;
    // 部屋: present-flag を確定して書き戻す（未著作 / 床が退化していれば room キーごと消える）。
    writeRoomToLayout(layout, roomModel, roomAuthored);
    roomAuthored = !!layout.hasRoom;
    // 開始位置: 正規化（座標が無ければキーごと消える = 未設定＝スタッフが手で始める運用）。
    const spot = normalizeStartSpot(layout.startSpot);
    if (spot) layout.startSpot = spot; else delete layout.startSpot;
    layout.rev = (parseInt(layout.rev, 10) || 0) + 1;
    const res = await deps.saveLayout(clone(layout));
    if (res && res.ok !== false) {
      dirty = false; renderDirty(); renderRegList(); renderLineList(); renderRoom(); renderLight(); renderStart();
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
    if (isReg()) {
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
    // カメラ姿勢モード: 印を掴めば移動 / 矢印の先を掴めば向き / 空きを押せば選択中カメラを置く。
    if (isCamPose()) {
      const hit = poseHit(m.px, m.py);
      if (hit) {
        camPoseIndex = hit.index;
        drag = { mode: 'campose', part: hit.part };
      } else if (cameras.length) {
        camPoseIndex = Math.min(camPoseIndex, cameras.length - 1);
        const p = ensurePose(camPoseIndex);
        if (p) { p.x = +m.x.toFixed(3); p.z = +m.z.toFixed(3); saveCameras(); }
        drag = { mode: 'campose', part: 'pos' };
      }
      renderCamPoseList(); render();
      e.preventDefault(); return;
    }
    // 通過ラインモード: 端点 / 線を掴めば編集、空きからドラッグで新しい線を引く。
    if (isLine()) {
      const hit = lineHit(m.px, m.py);
      if (hit) {
        lineDragIndex = hit.index;
        const l = lineArr()[hit.index];
        drag = { mode: 'line', part: hit.part, grabX: m.x, grabZ: m.z, orig: { ...l } };
      } else {
        drawing = { x: m.x, z: m.z, cur: { x: m.x, z: m.z } };
        drag = { mode: 'draw' };
      }
      renderLineList(); render();
      e.preventDefault(); return;
    }
    // 部屋モード: 端点 / 角 / 本体を掴めば編集、空きからドラッグで新しい壁 or 箱を作る。
    if (isRoom()) {
      const hit = roomHit(m.px, m.py);
      if (hit) {
        roomSel = { kind: hit.kind, id: hit.id };
        const o = selectedRoomItem();
        if (o) {
          const d = { mode: 'room', kind: hit.kind, part: hit.part, grabX: m.x, grabZ: m.z, orig: { ...o } };
          // 箱の角ドラッグは**対角を世界座標で固定**して詰める（yaw を保ったままサイズだけ変える）。
          if (hit.kind === 'prop' && hit.part.startsWith('c')) {
            const k = parseInt(hit.part.slice(1), 10);
            d.opp = boxFootprint(o)[(k + 2) % 4];
            d.axes = boxAxes(o);
          }
          drag = d;
        }
      } else {
        roomSel = null;
        roomDrawing = { x: m.x, z: m.z, cur: { x: m.x, z: m.z } };
        drag = { mode: 'roomdraw' };
      }
      renderRoom(); render();
      e.preventDefault(); return;
    }
    // 開始位置モード: 印を掴めば移動、空きを押せばそこへ置く（1 点だけなので追加と移動は同じ操作）。
    if (isStart()) {
      if (!startHit(m.px, m.py)) setStartAt(m.x, m.z);
      drag = { mode: 'start' };
      renderStart(); render();
      e.preventDefault(); return;
    }
    // 歩かせるモード: **押した場所へそのまま立たせる**（ドットを狙って掴む必要はない）。
    //   旧実装は半径 14px 以内を掴んだ時だけドラッグで、外すとタイル塗りに化けていた。
    if (isWalk()) {
      sim = { x: +m.x.toFixed(3), z: +m.z.toFixed(3) };
      drag = { mode: 'sim' };
      updateSimOut(); render(); emitSim();
      e.preventDefault(); return;
    }
    ensureGrid();
    drag = { mode: 'paint' };
    paintAt(m);
    e.preventDefault();
  });
  // 右クリックで位置合わせ点 / 通過ライン / 開始位置を削除（それぞれの編集モード時のみ）。
  canvas.addEventListener('contextmenu', (e) => {
    if (isStart()) {
      const m = mouseCourse(e);
      if (startHit(m.px, m.py)) clearStart();
      e.preventDefault(); return;
    }
    if (isRoom()) {
      const m = mouseCourse(e);
      const hit = roomHit(m.px, m.py);
      if (hit) deleteRoomItem(hit.kind, hit.id);
      e.preventDefault(); return;
    }
    if (isLine()) {
      const m = mouseCourse(e);
      const hit = lineHit(m.px, m.py);
      if (hit) deleteLine(hit.index);
      e.preventDefault(); return;
    }
    if (!isReg()) return;
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
    else if (drag.mode === 'sim') { sim = { x: +m.x.toFixed(3), z: +m.z.toFixed(3) }; updateSimOut(); render(); emitSim(); }
    else if (drag.mode === 'reg') {
      const a = regArr();
      if (a && regDragIndex >= 0 && regDragIndex < a.length) {
        a[regDragIndex].x = +m.x.toFixed(3); a[regDragIndex].z = +m.z.toFixed(3);
        markDirty(); updateRegCoords(); render();
      }
    }
    else if (drag.mode === 'start') {
      const s = startSpot();
      if (s) { s.x = +m.x.toFixed(3); s.z = +m.z.toFixed(3); markDirty(); renderStart(); render(); }
    }
    else if (drag.mode === 'draw') {
      if (drawing) { drawing.cur = { x: m.x, z: m.z }; render(); }
    }
    else if (drag.mode === 'line') {
      const l = lineArr()[lineDragIndex];
      if (l) {
        if (drag.part === 'a') { l.x1 = +m.x.toFixed(3); l.z1 = +m.z.toFixed(3); }
        else if (drag.part === 'b') { l.x2 = +m.x.toFixed(3); l.z2 = +m.z.toFixed(3); }
        else {
          // 平行移動（掴んだ点からの差分を両端へ）
          const dx = m.x - drag.grabX, dz = m.z - drag.grabZ;
          l.x1 = +(drag.orig.x1 + dx).toFixed(3); l.z1 = +(drag.orig.z1 + dz).toFixed(3);
          l.x2 = +(drag.orig.x2 + dx).toFixed(3); l.z2 = +(drag.orig.z2 + dz).toFixed(3);
        }
        markDirty(); renderLineList(); render();
      }
    }
    else if (drag.mode === 'roomdraw') {
      if (roomDrawing) { roomDrawing.cur = { x: m.x, z: m.z }; render(); }
    }
    else if (drag.mode === 'room') {
      const o = selectedRoomItem();
      if (o) {
        const dx = m.x - drag.grabX, dz = m.z - drag.grabZ;
        if (drag.kind === 'wall') {
          if (drag.part === 'a') { o.x1 = +m.x.toFixed(3); o.z1 = +m.z.toFixed(3); }
          else if (drag.part === 'b') { o.x2 = +m.x.toFixed(3); o.z2 = +m.z.toFixed(3); }
          else {
            o.x1 = +(drag.orig.x1 + dx).toFixed(3); o.z1 = +(drag.orig.z1 + dz).toFixed(3);
            o.x2 = +(drag.orig.x2 + dx).toFixed(3); o.z2 = +(drag.orig.z2 + dz).toFixed(3);
          }
        } else if (drag.part === 'body') {
          o.x = +(drag.orig.x + dx).toFixed(3); o.z = +(drag.orig.z + dz).toFixed(3);
        } else if (drag.opp) {
          // 対角（drag.opp）を固定したまま、箱のローカル軸へ射影して幅・奥行を取り直す。
          // 世界の XZ で幅を測ると yaw のある箱が回転してしまう。
          const px = m.x - drag.opp.x, pz = m.z - drag.opp.z;
          const du = px * drag.axes.ex.x + pz * drag.axes.ex.z;
          const dv = px * drag.axes.ez.x + pz * drag.axes.ez.z;
          o.w = +Math.max(MIN_DIM_M, Math.abs(du)).toFixed(3);
          o.d = +Math.max(MIN_DIM_M, Math.abs(dv)).toFixed(3);
          o.x = +(drag.opp.x + drag.axes.ex.x * du / 2 + drag.axes.ez.x * dv / 2).toFixed(3);
          o.z = +(drag.opp.z + drag.axes.ex.z * du / 2 + drag.axes.ez.z * dv / 2).toFixed(3);
        }
        markRoomDirty(); renderRoomNote(); renderRoomList(); renderRoomSel(); render();
      }
    }
    else if (drag.mode === 'campose') {
      const p = poseOf(camPoseIndex);
      if (p) {
        if (drag.part === 'yaw') {
          // 印から掴んだ点へ向く角度（course +Z が 0）。
          p.yawDeg = Math.round(Math.atan2(m.x - p.x, m.z - p.z) * 180 / Math.PI);
        } else {
          p.x = +m.x.toFixed(3); p.z = +m.z.toFixed(3);
        }
        renderCamPoseList(); render();
      }
    }
  });
  window.addEventListener('mouseup', () => {
    // カメラ姿勢はドラッグ終了時にだけ保存する（1 フレームごとに show.json を書かない）。
    if (drag && drag.mode === 'campose') saveCameras();
    // 引き終わった線を確定（短すぎるドラッグは線にしない = クリックの取り消しになる）。
    if (drag && drag.mode === 'draw' && drawing) {
      const i = addLine(drawing.x, drawing.z, drawing.cur.x, drawing.cur.z);
      if (i >= 0) lineDragIndex = i;
      drawing = null;
      renderLineList(); render();
    }
    // 引き終わった壁 / 箱を確定（小さすぎるドラッグは作らない = クリックの取り消しになる）。
    if (drag && drag.mode === 'roomdraw' && roomDrawing) {
      addRoomShape(roomDrawing.x, roomDrawing.z, roomDrawing.cur.x, roomDrawing.cur.z);
      roomDrawing = null;
      renderRoom(); render();
    }
    drag = null;
    regDragIndex = -1;
  });

  // ドラッグ矩形 → 壁（線分）or 箱。ROOM_DRAW_MIN_M 未満は作らない。
  function addRoomShape(x1, z1, x2, z2) {
    if (!roomModel) return;
    if (roomTool === 'wall') {
      if (Math.hypot(x2 - x1, z2 - z1) < Math.max(ROOM_DRAW_MIN_M, WALL_MIN_LENGTH_M)) return;
      const w = newWall(+x1.toFixed(3), +z1.toFixed(3), +x2.toFixed(3), +z2.toFixed(3),
        { id: nextRoomId(roomModel.walls, WALL_ID_PREFIX) });
      roomModel.walls.push(w);
      roomSel = { kind: 'wall', id: w.id };
    } else {
      const w = Math.abs(x2 - x1), d = Math.abs(z2 - z1);
      if (w < ROOM_DRAW_MIN_M || d < ROOM_DRAW_MIN_M) return;
      const b = newBox(+((x1 + x2) / 2).toFixed(3), +((z1 + z2) / 2).toFixed(3), +w.toFixed(3), +d.toFixed(3),
        { id: nextRoomId(roomModel.props, BOX_ID_PREFIX) });
      roomModel.props.push(b);
      roomSel = { kind: 'prop', id: b.id };
    }
    markRoomDirty();
  }

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
    // 構える高さ: 数値でなければ 0（床に着ける）へ落とす。regPoints と違い常にキーを持つ
    // （0 と未設定が同じ意味なので、揺れる余地を残さない）。
    layout.regTouchHeightM = regTouchHeight();
    // 通過ライン: 不正要素を落とす（未著作なら配列そのものを持たない）。
    const lines = linesFromLayout(layout);
    if (lines.length) layout.lines = lines; else delete layout.lines;
    // 開始位置: 座標が無ければキーごと落とす（未設定＝スタッフが手で始める運用）。
    const spot = normalizeStartSpot(layout.startSpot);
    if (spot) layout.startSpot = spot; else delete layout.startSpot;
    // 部屋の 3D プロキシ: present-flag（hasRoom）の AND 規約で解決して編集モデルへ。
    // 未著作なら layout.floor を種にする（部屋を作り始めた瞬間に床が 1.8m へ化けないように）。
    const rm = roomFromLayout(layout);
    roomModel = rm.room;
    roomAuthored = rm.hasRoom;
    if (roomSel && !selectedRoomItem()) roomSel = null;
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
    lineUses = countLineUses(state);
    renderPalette();
    if (isCamPose()) renderCamPoseList();
    renderCourse();
    if (!dirty) { renderRegList(); renderLineList(); syncRoomInputs(); renderRoom(); renderLight(); renderStart(); }
    // ↑ 編集中は入力フォーカスを潰さないため再構築しない
    updateSimOut(); render();
  }

  // 「このラインを使っている演出が何本あるか」（削除時の警告・一覧の注記に使う）。
  function countLineUses(state) {
    const out = {};
    const segs = (state && state.timeline && Array.isArray(state.timeline.segments))
      ? state.timeline.segments : [];
    for (const seg of segs) {
      for (const t of (seg && Array.isArray(seg.takes) ? seg.takes : [])) {
        if (!t || t.at !== 'line' || !t.lineId) continue;
        out[t.lineId] = (out[t.lineId] || 0) + 1;
      }
    }
    return out;
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

  // シナリオ再実行（show-sim.js）がドットを動かすための外部入口。
  // emitSim は呼ばない（呼び元へ跳ね返して無限ループにしない）。
  function setSimPos(x, z) {
    if (!Number.isFinite(x) || !Number.isFinite(z)) return;
    if (!isWalk()) setMapMode('walk', { keepPos: true });
    sim = { x: +x.toFixed(3), z: +z.toFixed(3) };
    updateSimOut(); render();
  }

  // タイムラインの「▶ 検証」から呼ばれる外部入口。歩かせるモードへ入れてドットを出す
  // （卓の導線を 1 本にするため。既に歩かせる中なら位置は動かさない）。
  function enableSim() {
    if (isWalk()) { emitSim(); return; }
    setMapMode('walk');
  }

  adoptLayout(DEFAULT_LAYOUT);
  setMapMode('paint');
  renderPalette();
  renderCourse();
  renderRegList();
  renderLineList();
  syncRoomInputs();
  renderRoom();
  renderLight();
  renderStart();
  renderDirty();
  render();
  return { onState, onUnity, isDirty: () => dirty, setSimPos, enableSim };
}
