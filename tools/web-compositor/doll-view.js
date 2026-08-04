// 人形を**実メッシュ＋実テクスチャ**で描く WebGL2 の層。
// 透明な離れたキャンバスへ描いて、2D の合成面へ `drawImage` で重ねる。
//
// ⚠ **陰影は写実へ寄せない。** ここにあるのは「上から光が当たっている」が読める最小限
//   （包み込み拡散 + 環境光）だけで、法線マップ・鏡面・リムは持たない。
//   合成の見た目の正は Unity（Preview Show Composite）で、この層は位置合わせのための目安。
//   ここを写実へ育てると「卓では馴染んで見えるのに実機が違う」を再生産する
//   （rules/streaming.md / visual-verification.md）。
//
// ⚠ 投影は `doll-math.js` 経由で `calib.js` の `projectPoint` と**同じ画素**に落ちることを
//   `doll-math.test.mjs` が固定している。ワイヤーと人形が別の式だと、
//   「ワイヤーは合っているのに人形だけずれる」という切り分け不能な壊れ方をする。
import { viewMatrix, shadowMatrix, lightDirection, kelvinLinear } from './doll-math.js';
import { kelvinToRgb } from './room-model.js';

const ASSET = 'assets/doll/';
const NEAR = 0.02, FAR = 60.0;

// 内部パラメータからクリップ座標を組む。**歪みを掛けてから**内部行列（camToPixel と同じ順序）。
const CLIP_GLSL = `
vec4 clipFrom(vec3 pc) {
  if (pc.z <= 1e-4) return vec4(0.0, 0.0, -2.0, 1.0);      // カメラ後方は捨てる
  vec2 n = pc.xy / pc.z;
  float ru2 = dot(n, n);
  float s = 1.0;
  if (abs(uK1) > 1e-9 && ru2 > 1e-16) {
    float disc = 1.0 - 4.0 * uK1 * ru2;
    if (disc < 0.0) return vec4(0.0, 0.0, -2.0, 1.0);      // このモデルでは像を結ばない
    float ru = sqrt(ru2);
    s = ((1.0 - sqrt(disc)) / (2.0 * uK1 * ru)) / ru;
  }
  vec2 nd = n * s;
  float u = uIntr.z + uIntr.x * nd.x;
  float v = uIntr.w - uIntr.y * nd.y;
  float x = (2.0 * u / uSize.x - 1.0) * pc.z;
  float y = (1.0 - 2.0 * v / uSize.y) * pc.z;
  float zc = pc.z * (uFar + uNear) / (uFar - uNear) - 2.0 * uFar * uNear / (uFar - uNear);
  return vec4(x, y, zc, pc.z);
}`;

const VS = `#version 300 es
precision highp float;
in vec3 aPos; in vec3 aNrm; in vec2 aUv;
uniform mat4 uView, uModel;
uniform vec4 uIntr; uniform vec2 uSize; uniform float uK1, uNear, uFar;
out vec3 vN; out vec2 vUv;
${CLIP_GLSL}
void main() {
  vec4 wp = uModel * vec4(aPos, 1.0);
  vN = mat3(uModel) * aNrm;
  vUv = aUv;
  gl_Position = clipFrom((uView * wp).xyz);
}`;

const FS = `#version 300 es
precision highp float;
in vec3 vN; in vec2 vUv;
uniform sampler2D uTex;
uniform vec3 uLightDir, uLightColor;
uniform float uAmbient;
out vec4 o;
void main() {
  vec4 t = texture(uTex, vUv);
  // 包み込み拡散。真横〜裏側でも真っ黒にしない（人形は小さく、真っ黒だと形が読めない）。
  float ndl = dot(normalize(vN), normalize(uLightDir));
  float wrap = clamp((ndl + 0.6) / 1.6, 0.0, 1.0);
  vec3 col = t.rgb * (uLightColor * wrap + vec3(uAmbient));
  o = vec4(col, 1.0);
}`;

// 影は**同じメッシュを床へ潰して**描く（平面投影シャドウ）。色は持たない。
const VS_SHADOW = `#version 300 es
precision highp float;
in vec3 aPos;
uniform mat4 uView, uModel, uShadow;
uniform vec4 uIntr; uniform vec2 uSize; uniform float uK1, uNear, uFar;
${CLIP_GLSL}
void main() {
  vec4 wp = uShadow * (uModel * vec4(aPos, 1.0));
  gl_Position = clipFrom((uView * (wp / wp.w)).xyz);
}`;

const FS_SHADOW = `#version 300 es
precision highp float;
uniform float uDensity;
out vec4 o;
void main() { o = vec4(0.0, 0.0, 0.0, uDensity); }`;

function compile(gl, vsSrc, fsSrc) {
  const mk = (type, src) => {
    const s = gl.createShader(type);
    gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) || 'shader');
    return s;
  };
  const p = gl.createProgram();
  gl.attachShader(p, mk(gl.VERTEX_SHADER, vsSrc));
  gl.attachShader(p, mk(gl.FRAGMENT_SHADER, fsSrc));
  gl.bindAttribLocation(p, 0, 'aPos');
  gl.bindAttribLocation(p, 1, 'aNrm');
  gl.bindAttribLocation(p, 2, 'aUv');
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p) || 'link');
  return p;
}

export function createDollView() {
  const canvas = document.createElement('canvas');
  // ⚠ stencil が要る。無いと腕と胴の影が重なった所だけ二重に暗くなって**濃い斑**になる
  //    （Unity 側も同じ理由で ShowShadowProjector にステンシルを入れてある）。
  const gl = canvas.getContext('webgl2', {
    alpha: true, premultipliedAlpha: false, stencil: true, antialias: true, depth: true,
  });
  let prog = null, progShadow = null, vao = null, tex = null, meta = null, ready = false, failed = '';

  async function load() {
    if (ready || failed) return ready;
    try {
      if (!gl) throw new Error('WebGL2 が使えません');
      meta = await (await fetch(ASSET + 'doll_web.json')).json();
      const buf = await (await fetch(ASSET + 'doll_web.bin')).arrayBuffer();
      const n = meta.vertexCount;
      const pos = new Float32Array(buf, meta.offsets.position, n * 3);
      const nrm = new Float32Array(buf, meta.offsets.normal, n * 3);
      const uv = new Float32Array(buf, meta.offsets.uv, n * 2);

      prog = compile(gl, VS, FS);
      progShadow = compile(gl, VS_SHADOW, FS_SHADOW);
      vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      const mkBuf = (data, loc, size) => {
        const b = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, b);
        gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, size, gl.FLOAT, false, 0, 0);
      };
      mkBuf(pos, 0, 3); mkBuf(nrm, 1, 3); mkBuf(uv, 2, 2);
      gl.bindVertexArray(null);

      const img = await new Promise((res, rej) => {
        const i = new Image();
        i.onload = () => res(i); i.onerror = () => rej(new Error('アルベドを読めません'));
        i.src = ASSET + meta.albedo;
      });
      tex = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
      gl.generateMipmap(gl.TEXTURE_2D);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      // ⚠ アトラスは前面/背面パネルが隣り合うので **CLAMP**（Repeat だと縁で反対側の絵を吸う）
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      ready = true;
    } catch (e) {
      failed = e.message || String(e);
    }
    return ready;
  }

  /** 人形の姿勢行列（列優先）。足元を床（y=0）に置き、y 軸まわりに回し、高さで拡縮する。 */
  function modelMatrix(placement, heightM) {
    const s = (heightM > 0 ? heightM : meta.heightM) / (meta.heightM || 1);
    const a = ((placement.yawDeg || 0) * Math.PI) / 180;
    const c = Math.cos(a), sn = Math.sin(a);
    const m = new Float32Array(16);
    m[0] = c * s; m[2] = -sn * s;
    m[5] = s;
    m[8] = sn * s; m[10] = c * s;
    // 書き出したメッシュは足元が y=min。床へ着ける。
    m[12] = placement.x || 0;
    m[13] = -(meta.bounds.min[1] || 0) * s;
    m[14] = placement.z || 0;
    m[15] = 1;
    return m;
  }

  function setCommon(p, calib, w, h) {
    gl.uniformMatrix4fv(gl.getUniformLocation(p, 'uView'), false, viewMatrix(calib));
    gl.uniform4f(gl.getUniformLocation(p, 'uIntr'), calib.fxPx, calib.fyPx || calib.fxPx, calib.cxPx, calib.cyPx);
    gl.uniform2f(gl.getUniformLocation(p, 'uSize'), w, h);
    gl.uniform1f(gl.getUniformLocation(p, 'uK1'), calib.k1 || 0);
    gl.uniform1f(gl.getUniformLocation(p, 'uNear'), NEAR);
    gl.uniform1f(gl.getUniformLocation(p, 'uFar'), FAR);
  }

  /** 1 枚描いて、透明背景のキャンバスを返す（2D 側が drawImage で重ねる）。 */
  function render(calib, placement, heightM, light, w, h) {
    if (!ready) return null;
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    gl.viewport(0, 0, w, h);
    gl.clearColor(0, 0, 0, 0);
    gl.clearStencil(0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT | gl.STENCIL_BUFFER_BIT);
    gl.enable(gl.CULL_FACE); gl.cullFace(gl.BACK);

    const model = modelMatrix(placement, heightM);
    const lit = light || {};
    const ldir = lightDirection(lit);
    gl.bindVertexArray(vao);

    // --- 影（床へ潰す）。ステンシルで 1 画素 1 回だけ塗る ---
    const sm = shadowMatrix(0, ldir);
    if (sm) {
      gl.useProgram(progShadow);
      setCommon(progShadow, calib, w, h);
      gl.uniformMatrix4fv(gl.getUniformLocation(progShadow, 'uModel'), false, model);
      gl.uniformMatrix4fv(gl.getUniformLocation(progShadow, 'uShadow'), false, sm);
      gl.uniform1f(gl.getUniformLocation(progShadow, 'uDensity'),
        lit.shadowDensity != null ? lit.shadowDensity : 0.55);
      gl.disable(gl.DEPTH_TEST); gl.depthMask(false);
      gl.disable(gl.CULL_FACE);
      gl.enable(gl.STENCIL_TEST);
      gl.stencilFunc(gl.NOTEQUAL, 1, 0xff);
      gl.stencilOp(gl.KEEP, gl.KEEP, gl.REPLACE);
      gl.enable(gl.BLEND);
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      gl.drawArrays(gl.TRIANGLES, 0, meta.vertexCount);
      gl.disable(gl.STENCIL_TEST);
      gl.enable(gl.CULL_FACE);
    }

    // --- 人形 ---
    gl.useProgram(prog);
    setCommon(prog, calib, w, h);
    gl.uniformMatrix4fv(gl.getUniformLocation(prog, 'uModel'), false, model);
    const lc = kelvinLinear(lit.tempK != null ? lit.tempK : 4000, kelvinToRgb);
    const inten = lit.intensity != null ? Math.max(0, lit.intensity) : 1;
    gl.uniform3f(gl.getUniformLocation(prog, 'uLightDir'), ldir[0], ldir[1], ldir[2]);
    gl.uniform3f(gl.getUniformLocation(prog, 'uLightColor'), lc[0] * inten, lc[1] * inten, lc[2] * inten);
    gl.uniform1f(gl.getUniformLocation(prog, 'uAmbient'), lit.ambient != null ? lit.ambient : 0.35);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.uniform1i(gl.getUniformLocation(prog, 'uTex'), 0);
    gl.enable(gl.DEPTH_TEST); gl.depthMask(true); gl.depthFunc(gl.LEQUAL);
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.drawArrays(gl.TRIANGLES, 0, meta.vertexCount);
    gl.bindVertexArray(null);
    return canvas;
  }

  return {
    load, render,
    get ready() { return ready; },
    get error() { return failed; },
    get heightM() { return meta ? meta.heightM : 0.4; },
  };
}
