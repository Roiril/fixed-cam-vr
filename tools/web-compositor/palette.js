// 廻リ視 web compositor — 配色の単一の正。
//
// 依存ゼロで置くのは floormap.js のため。floormap は「自己完結のまま common.js には
// 依存させない」という決めがあるので、共有したい配色を common.js へ置くと
// 同じ配列をもう 1 本書くことになる（実際 CAM_COLORS は 2 本あって、片方だけ直せば
// 黙って食い違う状態だった）。配色だけを依存ゼロの島に出せば、決めを守ったまま 1 本にできる。
//
// ⚠ 色を足す・変えるときは、下の全部と衝突しないことを確かめる。
//    衝突すると「塗ったはずの色と違う」と誤読される（floormap の開始位置の色が
//    そう決められている）。CSS 側の正は style.css の :root（--geo-*）で、値を揃える。

// カメラ index → 色（unity-vr.md の校正フットプリント配色に合わせる: 緑=0 / 青=1 / 橙=2）。
export const CAM_COLORS = ['#5ad19a', '#5aa8ff', '#ffae5e', '#d98cff', '#ff6b8e', '#8ad4ff'];
export const camColor = (i) =>
  CAM_COLORS[((i % CAM_COLORS.length) + CAM_COLORS.length) % CAM_COLORS.length];

// 幾何の色。「同じ物を別の面から見ている」ことを配色で保証する
// （部屋 / 較正ワイヤー / 立ち位置 UI で壁は必ず同じ色）。
export const GEO = {
  floor: '#ffdead',                    // 床・格子・基準点
  grid:  'rgba(255,222,173,.28)',      // 床の格子（floor の薄いもの）
  wall:  '#78dcff',                    // 壁・箱
  // 人形が無彩色なのは、機材の色（カメラ 6 色・床・壁・開始位置）が彩度のある色を
  // 占め切っていて、確実に空いているのが無彩色だけだから。「機材ではなく人」とも読める。
  // 旧値 #7ad6ff は壁 #78dcff と RGB 差 (8,6,0) で、index.html が
  // 「必ず見分けが付くようにする」と書いた要求を満たしていなかった。
  actor: '#ffffff',                    // CG 人形（体験者の分身）
  check: '#5ad19a',                    // 再投影＝計算結果
  start: '#5ad1c8',                    // 開始位置（導入演出の発火点）
};

/** GEO の色を rgba() にする（描画側で塗り・淡色を作るため）。 */
export function geoAlpha(hex, a) {
  const h = hex.replace('#', '');
  const n = parseInt(h.length === 3 ? h.split('').map((c) => c + c).join('') : h, 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${a})`;
}
