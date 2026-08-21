// 床の線を「画像空間の鏡」へ写す（`canon/LEDGER.md` 0102 の 3 番目の手当て）。
//
// ## なぜ course 空間の鏡では駄目なのか
//
// 3 周目 A は画面を縦に割り、**左半分だけライブを左右反転して読む**（`splitFlip`）。
// 反転は `ScreenComposite.shader` の `uvSrc.x = 1.0 - uvSrc.x` — つまり**枠 UV の 0.5**、
// contain-fit が対称なのでソース画像の**横中央**が鏡の軸になる。
//
// ところが現行の `line_rec_start` は **course 空間で対角軸に対する鏡像**として置かれている。
// カメラ A の実姿勢が対称軸の真上に無い限り、course で対称な 2 点は**画面上では対称に写らない**
// （透視とレンズ歪みが軸の両側で違う効き方をする）。だから「録画が凍結より外から始まる」。
//
// ⇒ **画像空間で反転してから床へ逆投影する。** 順投影は `projectPoint`、逆は `unprojectToFloor`
// （どちらも `calib.js` の実装をそのまま使う ＝ Unity / シェーダと同じ式。ここで別式を書くと
// 「卓では合うのに実機で合わない」という最悪の破れ方をする）。
//
// ⚠⚠ **鏡の軸は枠の中央（0.5）で、`splitX` そのものではない。** 実機は `splitX` の値に関係なく
//   0.5 で反転する。3 周目 A は `splitX = 0.5` なので両者は一致するが、**分割位置を動かしたら
//   鏡の軸は動かない**（動くのは「どちらの半分に出るか」だけ）。ここを取り違えると、
//   0.5 以外の分割で線が黙って別の場所へ行く。
//
// ⚠ `MjpegScreen.uvRotSteps`（90 度単位の UV 回転）が 0 でないカメラでは軸が縦になるので、
//   この計算は成り立たない。あの値は show.json に無い（Unity の SerializeField）ので卓からは
//   見えない — 回した運用にするなら、ここも直す。

import { projectPoint, unprojectToFloor } from './calib.js';

/**
 * 鏡対応が崩れていると見なす残差 (m)。
 *
 * 位置合わせの残差ゲート（0.12m）より緩く取る — こちらは「作り直し忘れ」を捕まえるための門で、
 * 現場の測定誤差を弾く門ではない。線を数 cm 動かしただけで毎回赤くなると誰も見なくなる。
 */
export const MIRROR_TOLERANCE_M = 0.06;

/** 較正が鏡の計算に使えるか（内部行列と解像度が要る）。 */
export function calibUsableForMirror(calib) {
  return !!calib && calib.fxPx > 1 && calib.srcW > 1 && calib.srcH > 1;
}

/**
 * 床の 1 点 (x, z) を画像空間で横中央に対して反転し、床へ戻す。
 * 返すのは `{x, z}`、解けなければ `null`（地平線より上・カメラ後方へ抜ける）。
 */
export function mirrorFloorPoint(calib, x, z) {
  if (!calibUsableForMirror(calib)) return null;
  const p = projectPoint(calib, x, 0, z);
  if (!p) return null;
  // 枠 UV 0.5 に対する反転 ＝ ソース画素の横中央に対する反転（contain-fit は左右対称）。
  const back = unprojectToFloor(calib, calib.srcW - p.u, p.v, 0);
  if (!back) return null;
  return { x: back.x, z: back.z };
}

/**
 * 線分をまるごと鏡へ写す。
 *
 * ⚠ **端点の順序は保つ**（`dir` が矢印の向きを意味するので、入れ替えると通過方向が反転する）。
 * ⚠ **左右が入れ替わることは気にしない** — 鏡像なのだから当然で、通過方向は `dir` が別に持つ。
 *
 * @returns {{x1:number,z1:number,x2:number,z2:number}|{error:string}}
 */
export function mirrorLine(calib, line) {
  if (!calibUsableForMirror(calib)) {
    return { error: 'このカメラは較正されていません（🎯 カメラを合わせる で内部行列を出す）' };
  }
  if (!line || ![line.x1, line.z1, line.x2, line.z2].every(Number.isFinite)) {
    return { error: '線の座標が壊れています' };
  }
  const a = mirrorFloorPoint(calib, line.x1, line.z1);
  const b = mirrorFloorPoint(calib, line.x2, line.z2);
  if (!a || !b) {
    return { error: '線の端点がこのカメラに写っていません（画角の外・地平線より上）' };
  }
  return {
    x1: +a.x.toFixed(3), z1: +a.z.toFixed(3),
    x2: +b.x.toFixed(3), z2: +b.z.toFixed(3),
  };
}

/**
 * <b>いま置かれている線が、元の線の鏡像になっているか。</b>
 * 返すのは端点 2 つのうち**大きい方のずれ (m)**。判定できなければ `null`
 * （較正が無い / 写っていない ＝ 崩れているとは言えない）。
 *
 * 本番前チェックが読む。⚠ 「片方を動かしたら必ず作り直す」を人の記憶に任せないための門。
 */
export function mirrorResidualM(calib, src, dst) {
  const m = mirrorLine(calib, src);
  if (m.error || !dst) return null;
  const d1 = Math.hypot(m.x1 - dst.x1, m.z1 - dst.z1);
  const d2 = Math.hypot(m.x2 - dst.x2, m.z2 - dst.z2);
  return Math.max(d1, d2);
}

/**
 * 元の線が<b>鏡へ写す意味のある側</b>に引かれているか。
 *
 * 3 周目 A は「右半分＝ライブの自分 / 左半分＝その鏡像」なので、凍結の線は
 * **枠の右半分**（`splitX` より右）に写っていなければならない。左半分に引くと鏡像が右へ出て、
 * 反転した自分とライブの自分が入れ替わる ＝ 演出が丸ごと裏返る。
 *
 * @returns {{ok:boolean, detail:string}}
 */
export function mirrorSideCheck(calib, line, splitX = 0.5) {
  if (!calibUsableForMirror(calib)) return { ok: true, detail: '' };
  const mid = projectPoint(calib, (line.x1 + line.x2) / 2, 0, (line.z1 + line.z2) / 2);
  if (!mid) return { ok: true, detail: '' };
  const u01 = mid.u / calib.srcW;
  if (u01 >= splitX) return { ok: true, detail: '' };
  return {
    ok: false,
    detail: `凍結の線は画面の左半分（x=${u01.toFixed(2)}）に写っています。`
      + '3 周目 A は右半分がライブの自分・左半分がその鏡像なので、線は右半分に引いてください',
  };
}
