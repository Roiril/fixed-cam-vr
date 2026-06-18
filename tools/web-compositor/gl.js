// 最小 WebGL2 ヘルパー: プログラム / レンダーターゲット / ドロー。
import { VS } from './shaders.js';

export function createContext(canvas) {
  const gl = canvas.getContext('webgl2', {
    antialias: false,
    premultipliedAlpha: false,
    // 合成ビュー（= Quest 実映像）を 📷 で 1 枚 toBlob する時、描画バッファが
    // composite 後にクリアされていると空画像になる。保持して読み出せるようにする。
    preserveDrawingBuffer: true,
  });
  if (!gl) throw new Error('WebGL2 が使えません。Chrome/Edge/Firefox の新しめのバージョンで開いてください。');
  // half-float RT（ラプラシアンは負値を持つので必須）
  const extCBF = gl.getExtension('EXT_color_buffer_float') || gl.getExtension('EXT_color_buffer_half_float');
  if (!extCBF) throw new Error('EXT_color_buffer_float 非対応。half-float レンダーターゲットが使えません。');
  gl.getExtension('OES_texture_float_linear');

  // 属性なし描画用の空 VAO
  const vao = gl.createVertexArray();
  gl.bindVertexArray(vao);

  gl.disable(gl.DEPTH_TEST);
  gl.disable(gl.BLEND);
  return { gl, vao };
}

function compileShader(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(s);
    console.error(src);
    throw new Error('Shader compile error: ' + log);
  }
  return s;
}

export class Program {
  constructor(gl, fsSrc) {
    this.gl = gl;
    const p = gl.createProgram();
    gl.attachShader(p, compileShader(gl, gl.VERTEX_SHADER, VS));
    gl.attachShader(p, compileShader(gl, gl.FRAGMENT_SHADER, fsSrc));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
      throw new Error('Program link error: ' + gl.getProgramInfoLog(p));
    }
    this.program = p;
    this._loc = new Map();
  }

  loc(name) {
    if (!this._loc.has(name)) {
      this._loc.set(name, this.gl.getUniformLocation(this.program, name));
    }
    return this._loc.get(name);
  }

  // target: RenderTarget または {fbo:null,w,h}（画面）
  draw(target, uniforms) {
    const gl = this.gl;
    gl.useProgram(this.program);
    let unit = 0;
    for (const key in uniforms) {
      const v = uniforms[key];
      const l = this.loc(key);
      if (l === null) continue;
      if (v && v.tex !== undefined) {
        gl.activeTexture(gl.TEXTURE0 + unit);
        gl.bindTexture(gl.TEXTURE_2D, v.tex);
        gl.uniform1i(l, unit);
        unit++;
      } else if (typeof v === 'number') {
        gl.uniform1f(l, v);
      } else if (typeof v === 'boolean') {
        gl.uniform1f(l, v ? 1 : 0);
      } else if (Array.isArray(v) || v instanceof Float32Array) {
        if (v.length === 2) gl.uniform2fv(l, v);
        else if (v.length === 3) gl.uniform3fv(l, v);
        else if (v.length === 4) gl.uniform4fv(l, v);
      }
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, target.fbo);
    gl.viewport(0, 0, target.w, target.h);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
}

// half-float のレンダーターゲット（テクスチャ + FBO）
export class RenderTarget {
  constructor(gl, w, h) {
    this.gl = gl;
    this.w = w;
    this.h = h;
    this.tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, w, h, 0, gl.RGBA, gl.HALF_FLOAT, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.fbo = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.tex, 0);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  }
  dispose() {
    this.gl.deleteTexture(this.tex);
    this.gl.deleteFramebuffer(this.fbo);
  }
  get texel() { return [1 / this.w, 1 / this.h]; }
}

// 外部画像（video/canvas/img）をアップロードする RGBA8 ソーステクスチャ
export class SourceTexture {
  constructor(gl) {
    this.gl = gl;
    this.tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 255]));
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.ok = false;
  }
  // flipY=true で video/canvas の上下を GL 座標へ合わせる
  upload(el, flipY = true) {
    const gl = this.gl;
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, flipY);
    try {
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, el);
      this.ok = true;
    } catch (e) {
      this.ok = false;
    }
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
  }
}
