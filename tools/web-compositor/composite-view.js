// 廻リ視 web compositor — 合成ビュー（ScreenComposite 再現）の再利用モジュール。
//   app.js の setupView を抽出・一般化。カメラ列（監視）/ cue エディタ（オーサリング）/
//   タイムライン検証モードが同じパイプラインを provider 経由で駆動する。
//
//   provider.sample() は毎フレーム次を返す（全て任意・欠落は安全側に倒す）:
//   {
//     liveImg,       // HTMLImageElement|null   背景（生ライブ）
//     overlayEl,     // HTMLVideoElement|HTMLImageElement|null  差し替え素材
//     overlayReady,  // bool
//     overlayW, overlayH,  // 素材の実寸（contain-fit 用）
//     maskEl,        // canvas|img|null   R チャンネルのマスク（null = 全面差し替え=白）
//     overlayOn,     // bool   差し替えの目標 ON/OFF（フェードは view が計算）
//     fadeSec,       // number フェード秒
//     strength,      // 0..1   差し替え強度（overlay 不透明度の倍率）
//     feather,       // number マスクぼかし（省略時 blendCfg.feather）。焼き込み済みマスクは 0 推奨
//     post,          // {exposure,...}
//     trimEnd,       // number 動画の自動終端（0=無し）
//     onVideoEnd,    // fn|null 動画が trimEnd に達した時に 1 度呼ぶ
//     loopPreview,   // bool   繰り返し試写（オーサリング面）。終端で差し替えを畳まない
//   }
//
//   ⚠ loopPreview を渡さないと、素材が 1 周した瞬間に「再生終了」と判定して以後 live に
//   戻り続ける（overlayOn が落ちるまで復帰しない）。ループさせて見る面では必ず true にする。
import { createContext, SourceTexture } from './gl.js';
import { Pipeline } from './pipeline.js';
import { blendCfg, FX_DEFAULT } from './common.js';

const containScale = (w, h, fa) => {
  if (!w || !h) return [1, 1];
  const a = w / h;
  return a > fa ? [1, fa / a] : [a / fa, 1];
};

export function createCompositeView(canvas, provider) {
  let ctx;
  try {
    ctx = createContext(canvas); // half-float RT が要る（EXT_color_buffer_float 必須）
  } catch (e) {
    canvas.replaceWith(Object.assign(document.createElement('div'),
      { className: 'view-fallback', textContent: 'WebGL2/float RT 非対応: ' + e.message }));
    return { destroy() {} };
  }
  const gl = ctx.gl;
  const pipe = new Pipeline(gl);
  const texLive = new SourceTexture(gl), texMask = new SourceTexture(gl), texOv = new SourceTexture(gl);

  // マスク未指定（全面差し替え）用の 2x2 白テクスチャ。
  const whiteMask = document.createElement('canvas'); whiteMask.width = whiteMask.height = 2;
  { const c = whiteMask.getContext('2d'); c.fillStyle = '#fff'; c.fillRect(0, 0, 2, 2); }

  let ov = 0;          // フェード現在値（0..1）
  let ended = false;   // 動画終端到達フラグ
  let lastOn = false;  // 前フレームの overlayOn（立ち上がり検出）
  const t0 = performance.now();

  const render = () => {
    const s = (provider && provider.sample) ? provider.sample() : null;
    const W = canvas.width, H = canvas.height;
    pipe.allocate(W, H, Math.max(2, Math.min(9, blendCfg.levels))); // 同一サイズなら no-op
    const frameAspect = W / H;

    const img = s && s.liveImg;
    if (img && img.naturalWidth) texLive.upload(img);

    const hasSrc = !!(s && s.overlayEl && s.overlayReady);

    // overlayOn の立ち上がりで終端フラグをリセット
    const on = !!(s && s.overlayOn);
    if (on && !lastOn) ended = false;
    lastOn = on;

    // 動画の再生区間終端 → 自動終了（ループしない差し替えの復帰契機）
    if (on && !ended && hasSrc && !s.loopPreview && s.overlayEl.tagName === 'VIDEO') {
      const v = s.overlayEl;
      const tEnd = (s.trimEnd > 0) ? s.trimEnd : (v.duration || 0);
      if (tEnd && v.currentTime >= tEnd - 0.03) { ended = true; s.onVideoEnd && s.onVideoEnd(); }
    }

    // フェード（overlayOn=target、強度をイージング）
    const target = (on && hasSrc && !ended) ? 1 : 0;
    const fadeSec = Math.max(0.05, (s && s.fadeSec != null) ? s.fadeSec : 0.5);
    const stepMax = 0.04 / fadeSec;
    ov = ov + Math.max(-stepMax, Math.min(stepMax, target - ov));
    const strength = (s && s.strength != null) ? s.strength : 1;
    const amt = hasSrc ? ov * strength : 0;
    if (ov > 0.002 && hasSrc) texOv.upload(s.overlayEl);

    const maskEl = (s && s.maskEl) || whiteMask;
    texMask.upload(maskEl);

    const p = (s && s.post) || FX_DEFAULT;
    const feather = (s && s.feather != null) ? s.feather : blendCfg.feather;
    const active = amt > 0.01; // 色統計/ラプラシアンは overlay 表示中だけ（誤補正・無駄を回避）

    // Pipeline: mix(uB, uA, mask)=白→uA/黒→uB。白=差し替え=overlay なので
    //   uA(liveSrc 引数)=overlay、uB(preSrc 引数)=live。色統計は overlay を live の色味へ寄せる。
    pipe.render(texOv, texLive, texMask, {
      liveScale: hasSrc ? containScale(s.overlayW, s.overlayH, frameAspect) : [1, 1],
      preScale: containScale(img ? img.naturalWidth : 0, img ? img.naturalHeight : 0, frameAspect),
      feather,
      colorMatch: blendCfg.colorMatch && active, colorStrength: blendCfg.colorStrength,
      laplacian: blendCfg.laplacian && active,
      maskStrength: amt,
      exposure: p.exposure ?? 0, contrast: p.contrast ?? 1, saturation: p.saturation ?? 1,
      temperature: p.temperature ?? 0, vignette: p.vignette ?? 0, grain: p.grain ?? 0,
      aberration: 0, scanline: p.scanline ?? 0, showMask: 0,
      time: (performance.now() - t0) / 1000,
    }, { fbo: null, w: W, h: H });
  };

  // RAF は非表示タブで停止するので setInterval（multicam の教訓）
  const timer = setInterval(render, 40);
  return { destroy() { clearInterval(timer); }, canvas };
}
