// 合成パイプライン本体。
//   ingest(live,pre,mask) → 色統計マッチング → ラプラシアンピラミッドブレンド → ポストFX
import { Program, RenderTarget } from './gl.js';
import * as S from './shaders.js';

function pyramidSizes(w, h, maxLevels) {
  const sizes = [[w, h]];
  while (sizes.length < maxLevels) {
    const [pw, ph] = sizes[sizes.length - 1];
    if (Math.min(pw, ph) <= 8) break;
    sizes.push([Math.max(1, Math.ceil(pw / 2)), Math.max(1, Math.ceil(ph / 2))]);
  }
  return sizes;
}

function reductionSizes(w, h) {
  const sizes = [];
  let cw = w, ch = h;
  while (cw > 1 || ch > 1) {
    cw = Math.max(1, Math.ceil(cw / 2));
    ch = Math.max(1, Math.ceil(ch / 2));
    sizes.push([cw, ch]);
  }
  if (sizes.length === 0) sizes.push([1, 1]);
  return sizes;
}

export class Pipeline {
  constructor(gl) {
    this.gl = gl;
    this.prog = {
      ingest: new Program(gl, S.FS_INGEST),
      mask: new Program(gl, S.FS_MASK),
      down: new Program(gl, S.FS_DOWN),
      colormatch: new Program(gl, S.FS_COLORMATCH),
      top: new Program(gl, S.FS_TOP),
      collapse: new Program(gl, S.FS_COLLAPSE),
      simple: new Program(gl, S.FS_SIMPLE),
      post: new Program(gl, S.FS_POST),
    };
    this.W = 0; this.H = 0; this.maxLevels = 0;
  }

  allocate(w, h, maxLevels) {
    const gl = this.gl;
    if (this.W === w && this.H === h && this.maxLevels === maxLevels) return;
    this._disposeAll();
    this.W = w; this.H = h; this.maxLevels = maxLevels;

    this.sizes = pyramidSizes(w, h, maxLevels);
    const N = this.sizes.length;
    const mk = ([sw, sh]) => new RenderTarget(gl, sw, sh);

    this.liveTex = new RenderTarget(gl, w, h);     // 色補正前 live
    this.gaussA = this.sizes.map(mk);               // A = 色補正後 live
    this.gaussB = this.sizes.map(mk);               // B = pre
    this.gaussM = this.sizes.map(mk);               // mask
    this.blended = this.sizes.map(mk);

    // 統計リダクション用チェーン（mean / meanSq × live / pre）
    this.redSizes = reductionSizes(w, h);
    this.redML = this.redSizes.map(mk);
    this.redSL = this.redSizes.map(mk);
    this.redMP = this.redSizes.map(mk);
    this.redSP = this.redSizes.map(mk);
    this._N = N;
  }

  _disposeAll() {
    const all = [this.liveTex, ...(this.gaussA || []), ...(this.gaussB || []),
      ...(this.gaussM || []), ...(this.blended || []),
      ...(this.redML || []), ...(this.redSL || []), ...(this.redMP || []), ...(this.redSP || [])];
    for (const rt of all) if (rt) rt.dispose();
    this.gaussA = this.gaussB = this.gaussM = this.blended = null;
    this.redML = this.redSL = this.redMP = this.redSP = null;
    this.liveTex = null;
  }

  // チェーンを 1x1 まで平均縮小。square=true なら最初の段で二乗。
  _reduce(chain, src) {
    for (let i = 0; i < chain.length; i++) {
      const source = i === 0 ? src : chain[i - 1];
      this.prog.down.draw(chain[i], {
        uTex: source,
        uTexel: source.texel,
        uSquare: 0,
      });
    }
  }
  _reduceSq(chain, src) {
    for (let i = 0; i < chain.length; i++) {
      const source = i === 0 ? src : chain[i - 1];
      this.prog.down.draw(chain[i], {
        uTex: source,
        uTexel: source.texel,
        uSquare: i === 0 ? 1 : 0,
      });
    }
  }

  // p: パラメータ。liveSrc/preSrc/maskSrc は SourceTexture。
  render(liveSrc, preSrc, maskSrc, p, screen) {
    const P = this.prog;

    // 1) 取り込み（作業解像度へ。fit 指定があれば letterbox）
    P.ingest.draw(this.liveTex, { uTex: liveSrc, uScale: p.liveScale || [1, 1] });
    P.ingest.draw(this.gaussB[0], { uTex: preSrc, uScale: p.preScale || [1, 1] });
    P.mask.draw(this.gaussM[0], {
      uTex: maskSrc,
      uTexel: this.gaussM[0].texel,
      uRadius: 0.5 + p.feather * 6.0,
    });

    // 2) 色統計マッチング
    if (p.colorMatch && p.colorStrength > 0.0001) {
      this._reduce(this.redML, this.liveTex);
      this._reduceSq(this.redSL, this.liveTex);
      this._reduce(this.redMP, this.gaussB[0]);
      this._reduceSq(this.redSP, this.gaussB[0]);
      P.colormatch.draw(this.gaussA[0], {
        uLive: this.liveTex,
        uMeanL: this.redML[this.redML.length - 1],
        uMeanSqL: this.redSL[this.redSL.length - 1],
        uMeanP: this.redMP[this.redMP.length - 1],
        uMeanSqP: this.redSP[this.redSP.length - 1],
        uStrength: p.colorStrength,
      });
    } else {
      P.ingest.draw(this.gaussA[0], { uTex: this.liveTex, uScale: [1, 1] });
    }

    let composite;
    if (p.laplacian) {
      // 3) ガウシアンピラミッド構築
      const N = this._N;
      for (let i = 1; i < N; i++) {
        P.down.draw(this.gaussA[i], { uTex: this.gaussA[i - 1], uTexel: this.gaussA[i - 1].texel, uSquare: 0 });
        P.down.draw(this.gaussB[i], { uTex: this.gaussB[i - 1], uTexel: this.gaussB[i - 1].texel, uSquare: 0 });
        P.down.draw(this.gaussM[i], { uTex: this.gaussM[i - 1], uTexel: this.gaussM[i - 1].texel, uSquare: 0 });
      }
      // 4) collapse
      P.top.draw(this.blended[N - 1], {
        uA: this.gaussA[N - 1], uB: this.gaussB[N - 1], uM: this.gaussM[N - 1],
      });
      for (let i = N - 2; i >= 0; i--) {
        P.collapse.draw(this.blended[i], {
          gA0: this.gaussA[i], gA1: this.gaussA[i + 1],
          gB0: this.gaussB[i], gB1: this.gaussB[i + 1],
          gM0: this.gaussM[i],
          uPrev: this.blended[i + 1],
        });
      }
      composite = this.blended[0];
    } else {
      // ラプラシアン OFF: 単純フェザー合成
      P.simple.draw(this.blended[0], {
        uA: this.gaussA[0], uB: this.gaussB[0], uM: this.gaussM[0],
      });
      composite = this.blended[0];
    }

    // 5) ポストFX → 画面
    P.post.draw(screen, {
      uTex: composite,
      uMask: this.gaussM[0],
      uTime: p.time,
      uExposure: p.exposure,
      uContrast: p.contrast,
      uSaturation: p.saturation,
      uTemperature: p.temperature,
      uVignette: p.vignette,
      uGrain: p.grain,
      uAberration: p.aberration,
      uScanline: p.scanline,
      uShowMask: p.showMask ? 1 : 0,
      uTexel: [1 / screen.w, 1 / screen.h],
    });
  }
}
