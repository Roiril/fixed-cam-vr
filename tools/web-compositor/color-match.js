// 色統計マッチングを「実機で使える 6 つの数」に落とす。
//
// 企画書 2.3：「差し替え素材の全体には色統計マッチングを施し、実写映像と継ぎ目なく合成する」。
//
// 卓のプレビューは GPU の縮約ピラミッドで毎フレーム統計を取っているが、**実機は同じことをしない**。
// Quest で毎フレーム縮約を回すのは重く、しかも固定視点では素材もマスクも動かないので、
// 統計は事前に解ける。ここで per-channel の Reinhard を gain/offset へ落として cue へ焼き、
// 実機（ScreenComposite の _OverlayGain / _OverlayOffset）は掛け算 1 回で済ませる。
//
// 向きに注意：**素材を実写へ寄せる**（実機ではライブ映像が土台で、動かせるのは素材の側だけ）。
//   out = (src - mSrc) / σSrc * σLive + mLive
//       = src * (σLive/σSrc) + (mLive - mSrc * σLive/σSrc)
//
// 数式は純関数に切り出してある（node --test で固定）。DOM に触るのは呼び出し側。

/** gain の許容範囲。素材がほぼ単色だと σSrc≈0 で発散するので必ず抑える。 */
export const GAIN_MIN = 0.25;
export const GAIN_MAX = 4.0;

/**
 * offset の許容範囲（0..1 の色空間）。
 * 暗い実写プレートに明るい素材を載せる（監視カメラの絵に生成映像を重ねる）と -0.6 前後まで普通に振れるので、
 * 全域を許す。狭めると「素材が実写の明るさまで下がりきらない」が黙って起きる。
 */
export const OFFSET_LIMIT = 1.0;

/** 恒等（マッチングなし）。 */
export const IDENTITY = { gain: [1, 1, 1], offset: [0, 0, 0] };

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);

/**
 * 画素統計（平均・標準偏差）を per-channel で求める。値域は 0..1。
 * @param {{mean:number[], sd:number[]}} live 実写側
 * @param {{mean:number[], sd:number[]}} src 素材側
 * @param {number} strength 0..1（0 = 恒等 / 1 = 完全に合わせる）
 */
export function solveMatch(live, src, strength = 1) {
  const s = clamp(Number(strength) || 0, 0, 1);
  if (!live || !src || s <= 0) return { gain: [...IDENTITY.gain], offset: [...IDENTITY.offset] };
  const gain = [1, 1, 1];
  const offset = [0, 0, 0];
  for (let c = 0; c < 3; c++) {
    const sdSrc = Math.max(src.sd[c], 1e-4);
    let g = clamp(live.sd[c] / sdSrc, GAIN_MIN, GAIN_MAX);
    let o = clamp(live.mean[c] - src.mean[c] * g, -OFFSET_LIMIT, OFFSET_LIMIT);
    // strength で恒等との間を線形に混ぜる（プレビューの uStrength と同じ意味）。
    gain[c] = 1 + (g - 1) * s;
    offset[c] = o * s;
  }
  return { gain, offset };
}

/** 統計が実質「合わせる必要なし」かどうか（恒等に十分近いか）。 */
export function isIdentity(m, eps = 1e-3) {
  if (!m) return true;
  for (let c = 0; c < 3; c++) {
    if (Math.abs(m.gain[c] - 1) > eps) return false;
    if (Math.abs(m.offset[c]) > eps) return false;
  }
  return true;
}

/**
 * ImageData（RGBA・0..255）から per-channel の平均と標準偏差を求める。
 * alpha が 0 の画素は数えない（素材の透過部分を平均へ混ぜない）。
 */
export function statsFromImageData(data) {
  const sum = [0, 0, 0];
  const sumSq = [0, 0, 0];
  let n = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i + 3] < 8) continue;
    n++;
    for (let c = 0; c < 3; c++) {
      const v = data[i + c] / 255;
      sum[c] += v;
      sumSq[c] += v * v;
    }
  }
  if (n === 0) return { mean: [0, 0, 0], sd: [0, 0, 0], count: 0 };
  const mean = [0, 0, 0];
  const sd = [0, 0, 0];
  for (let c = 0; c < 3; c++) {
    mean[c] = sum[c] / n;
    sd[c] = Math.sqrt(Math.max(0, sumSq[c] / n - mean[c] * mean[c]));
  }
  return { mean, sd, count: n };
}

/**
 * 描画可能な要素（img / video / canvas）を小さな canvas へ縮小して統計を取る。
 * 縮小するのは、統計に必要なのは分布であって解像度ではないから（32×32 でも実用上ぶれない）。
 * ブラウザ専用（node のテストは上の純関数だけを触る）。
 */
export function statsFromElement(el, size = 48) {
  if (!el) return null;
  const w = el.videoWidth || el.naturalWidth || el.width || 0;
  const h = el.videoHeight || el.naturalHeight || el.height || 0;
  if (!w || !h) return null;
  const c = document.createElement('canvas');
  c.width = size; c.height = size;
  const g = c.getContext('2d', { willReadFrequently: true });
  g.drawImage(el, 0, 0, size, size);
  try {
    return statsFromImageData(g.getImageData(0, 0, size, size).data);
  } catch {
    return null;   // 別オリジンの画像（tainted canvas）は諦めて恒等にする
  }
}
