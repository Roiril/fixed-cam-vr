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
 * 複数の統計をプールして 1 つにまとめる（動画を何点かサンプルした結果を畳む）。
 *
 * sd は各群の sd の単純平均**ではない**。全体の分散 = 群内分散の平均 + 群平均の分散 で、
 * 後者を落とすと「尺の中で明るさが変わる素材」の振れ幅がまるごと消える。
 * 暗→明のクリップで gain が過大になるのはこの項を取りこぼしたときに起きる。
 */
export function averageStats(list) {
  const items = (list || []).filter((s) => s && s.count);
  if (!items.length) return { mean: [0, 0, 0], sd: [0, 0, 0], count: 0 };
  const n = items.length;
  const mean = [0, 0, 0];
  const varSum = [0, 0, 0];
  for (const s of items) for (let c = 0; c < 3; c++) mean[c] += s.mean[c] / n;
  for (const s of items) {
    for (let c = 0; c < 3; c++) {
      const d = s.mean[c] - mean[c];
      varSum[c] += (s.sd[c] * s.sd[c] + d * d) / n;
    }
  }
  return {
    mean,
    sd: [Math.sqrt(varSum[0]), Math.sqrt(varSum[1]), Math.sqrt(varSum[2])],
    count: items.reduce((a, s) => a + s.count, 0),
  };
}

/**
 * マスク（枠空間 16:9）を、素材／実写の画素と**同じ格子**で読むための矩形を返す。
 *
 * 実機のシェーダは overlay を contain-fit で枠へ収め、**マスクだけは生 uv で読む**。
 * だから「素材のこの画素はマスクのどこか」は contain-fit の逆写像で決まる。
 * 4:3 の素材が 16:9 の枠に入るなら、素材の全幅はマスクの中央 75%（x 0.125–0.875）に対応する。
 */
export function maskRectForSource(srcW, srcH, maskW, maskH) {
  const fa = maskW / maskH;
  const a = (srcW && srcH) ? srcW / srcH : fa;
  const w = a > fa ? maskW : Math.max(1, Math.round(maskH * a));
  const h = a > fa ? Math.max(1, Math.round(maskW / a)) : maskH;
  return { sx: (maskW - w) / 2, sy: (maskH - h) / 2, sw: w, sh: h };
}

/**
 * 描画可能な要素（img / video / canvas）を小さな canvas へ縮小して統計を取る。
 * 縮小するのは、統計に必要なのは分布であって解像度ではないから（32×32 でも実用上ぶれない）。
 * ブラウザ専用（node のテストは上の純関数だけを触る）。
 *
 * `maskEl` を渡すと、**マスクが白い（＝差し替える）画素を統計から外す**。
 * 差し替える所には人形や手形そのものが居て、実写側には居ない。そこを平均に混ぜると
 * 「主題が背景と違う」ぶんまで補正しようとして、**もともと合っていた背景がずれる**
 * （2026-08-05 実測: 全画素で解いた結果、背景の一致が 1.5 → 20.4 に悪化した）。
 * 残るのは「実写と素材が同じものを写している所」＝そこを合わせるのが正しい。
 */
export function statsFromElement(el, size = 48, maskEl = null) {
  if (!el) return null;
  const w = el.videoWidth || el.naturalWidth || el.width || 0;
  const h = el.videoHeight || el.naturalHeight || el.height || 0;
  if (!w || !h) return null;
  const c = document.createElement('canvas');
  c.width = size; c.height = size;
  const g = c.getContext('2d', { willReadFrequently: true });
  g.drawImage(el, 0, 0, size, size);
  try {
    const img = g.getImageData(0, 0, size, size);
    // マスクの外がほとんど残らない（画面いっぱいの素材）なら、絞らずに全体で解く。
    applyMaskAlpha(img, maskEl, w, h, size);
    return statsFromImageData(img.data);
  } catch {
    return null;   // 別オリジンの画像（tainted canvas）は諦めて恒等にする
  }
}

/**
 * マスクが白い画素の alpha を 0 にする（`statsFromImageData` は alpha<8 を数えない）。
 * 十分な画素が残らなければ何もせず false を返す＝呼び出し側は全体で解く。
 */
function applyMaskAlpha(img, maskEl, srcW, srcH, size) {
  if (!maskEl) return false;
  const mw = maskEl.naturalWidth || maskEl.width || 0;
  const mh = maskEl.naturalHeight || maskEl.height || 0;
  if (!mw || !mh) return false;
  const r = maskRectForSource(srcW, srcH, mw, mh);
  const mc = document.createElement('canvas');
  mc.width = size; mc.height = size;
  const mg = mc.getContext('2d', { willReadFrequently: true });
  mg.drawImage(maskEl, r.sx, r.sy, r.sw, r.sh, 0, 0, size, size);
  let md;
  try {
    md = mg.getImageData(0, 0, size, size).data;
  } catch {
    return false;
  }
  const keep = new Uint8Array(size * size);
  let n = 0;
  for (let i = 0; i < keep.length; i++) {
    // R チャンネルが正（マスクは R に白で焼く）。少しでも掛かっている所は使わない。
    if (md[i * 4] < 24) { keep[i] = 1; n++; }
  }
  if (n < keep.length * 0.1) return false;   // 残りが 1 割未満 = 絞る意味がない
  for (let i = 0; i < keep.length; i++) if (!keep[i]) img.data[i * 4 + 3] = 0;
  return true;
}

/** シーク完了を待つ（1.2 秒で諦める。壊れた素材で焼き込みを止めない）。 */
const seekTo = (v, t) => new Promise((resolve) => {
  let done = false;
  const finish = () => { if (done) return; done = true; clearTimeout(timer); v.removeEventListener('seeked', finish); resolve(); };
  const timer = setTimeout(finish, 1200);
  v.addEventListener('seeked', finish, { once: true });
  try { v.currentTime = t; } catch { finish(); }
});

/**
 * 素材の統計。**動画は尺全体から複数点サンプルして畳む**。
 *
 * 実機が持てる補正は 1 組（gain/offset ×3）だけなので、保存した瞬間の 1 フレームで解くと
 * 「その明るさ」に固定された補正が焼かれる。暗く始まって明るくなる素材では、後半が破綻する。
 * 再生位置は元へ戻し、再生中だったものは再生を続ける（試写を乱さない）。
 */
export async function statsFromMedia(el, samples = 5, maskEl = null) {
  if (!el) return null;
  if (el.tagName !== 'VIDEO') return statsFromElement(el, 48, maskEl);
  const dur = el.duration;
  if (!Number.isFinite(dur) || dur <= 0.05) return statsFromElement(el, 48, maskEl);
  const wasPaused = el.paused;
  const t0 = el.currentTime;
  const acc = [];
  try {
    el.pause();
    for (let i = 0; i < samples; i++) {
      await seekTo(el, (dur * (i + 0.5)) / samples);
      const s = statsFromElement(el, 48, maskEl);
      if (s && s.count) acc.push(s);
    }
  } catch { /* シークできない素材は現フレームで妥協する */ }
  try {
    await seekTo(el, t0);
    if (!wasPaused) el.play().catch(() => {});
  } catch { /* 戻せなくても焼き込みは続ける */ }
  return acc.length ? averageStats(acc) : statsFromElement(el, 48, maskEl);
}

/**
 * 実機へ焼く 6 float を解く。**素材を実写へ寄せる**（実機で動かせるのは素材の側だけ）。
 * 卓のプレビューと同じ意思決定（境界ブレンドの色統計トグル）を使うので、cue を作るどの面も
 * この 1 関数を通すこと — 通し忘れると「卓で色が合って見えたのに実機は素の色」になる。
 */
export async function bakeColorMatch(liveEl, srcEl, cfg, maskEl = null) {
  const none = { hasMatch: false, matchGain: [1, 1, 1], matchOffset: [0, 0, 0] };
  if (!cfg || !cfg.colorMatch || !(cfg.colorStrength > 1e-4)) return none;
  // マスクがあるなら**差し替えない所**（実写と素材が同じものを写している所）だけで解く。
  const liveStats = statsFromElement(liveEl, 48, maskEl);
  const srcStats = await statsFromMedia(srcEl, 5, maskEl);
  // 実写か素材のどちらかが読めない（未接続・別オリジン）ときは黙って恒等にする。
  if (!liveStats || !srcStats || !liveStats.count || !srcStats.count) return none;
  const m = solveMatch(liveStats, srcStats, cfg.colorStrength);
  if (isIdentity(m)) return none;
  return { hasMatch: true, matchGain: m.gain, matchOffset: m.offset };
}
