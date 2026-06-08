// WebGL2 (GLSL ES 3.00) シェーダ群。
// 固定視点合成パイプライン用: ingest / 統計リダクション / 色統計マッチング /
// ガウシアン downsample / ラプラシアン collapse / 単純合成 / 後段ポストFX。

// 全パス共通: フルスクリーン三角形（属性なし、gl_VertexID で生成）
export const VS = `#version 300 es
precision highp float;
out vec2 vUv;
const vec2 P[3] = vec2[3](vec2(-1.0,-1.0), vec2(3.0,-1.0), vec2(-1.0,3.0));
void main(){
  vec2 p = P[gl_VertexID];
  vUv = p * 0.5 + 0.5;
  gl_Position = vec4(p, 0.0, 1.0);
}`;

// ソース（video/canvas テクスチャ）を作業解像度へ取り込み。
// uScale=[1,1] でストレッチ。contain-fit 時は <1 を渡し、はみ出しは黒(letterbox)。
export const FS_INGEST = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uTex;
uniform vec2 uScale;
void main(){
  vec2 uv = (vUv - 0.5) / uScale + 0.5;
  if(any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))){
    o = vec4(0.0, 0.0, 0.0, 1.0);
  } else {
    o = texture(uTex, uv);
  }
}`;

// マスク取り込み + 簡易ぼかし（フェザー）。9タップ。
export const FS_MASK = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uTex;
uniform vec2 uTexel;
uniform float uRadius; // texel 単位のぼかし半径
void main(){
  float sum = 0.0; float wsum = 0.0;
  for(int j=-1;j<=1;j++){
    for(int i=-1;i<=1;i++){
      vec2 off = vec2(float(i), float(j)) * uTexel * uRadius;
      float w = 1.0;
      sum += texture(uTex, vUv + off).r * w;
      wsum += w;
    }
  }
  float m = sum / wsum;
  o = vec4(m, m, m, 1.0);
}`;

// 2x2 平均 downsample。uSquare=1 で色を二乗してから平均（分散用 E[x^2]）。
export const FS_DOWN = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uTex;
uniform vec2 uTexel;   // source の texel size (1/srcW, 1/srcH)
uniform float uSquare; // 0 or 1
void main(){
  vec4 a = texture(uTex, vUv + vec2(-0.5,-0.5)*uTexel);
  vec4 b = texture(uTex, vUv + vec2( 0.5,-0.5)*uTexel);
  vec4 c = texture(uTex, vUv + vec2(-0.5, 0.5)*uTexel);
  vec4 d = texture(uTex, vUv + vec2( 0.5, 0.5)*uTexel);
  if(uSquare > 0.5){ a*=a; b*=b; c*=c; d*=d; }
  o = 0.25 * (a + b + c + d);
}`;

// 色統計マッチング（Reinhard per-channel transfer）
// live をターゲット(pre)の平均/標準偏差へ寄せる。strength で線形補間。
export const FS_COLORMATCH = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uLive;           // 作業解像度の live
uniform sampler2D uMeanL, uMeanSqL; // 1x1: live の平均 / 二乗平均
uniform sampler2D uMeanP, uMeanSqP; // 1x1: pre  の平均 / 二乗平均
uniform float uStrength;
void main(){
  vec3 c   = texture(uLive, vUv).rgb;
  vec3 mL  = texture(uMeanL,   vec2(0.5)).rgb;
  vec3 mP  = texture(uMeanP,   vec2(0.5)).rgb;
  vec3 vL  = max(texture(uMeanSqL, vec2(0.5)).rgb - mL*mL, 1e-5);
  vec3 vP  = max(texture(uMeanSqP, vec2(0.5)).rgb - mP*mP, 1e-5);
  vec3 matched = (c - mL) / sqrt(vL) * sqrt(vP) + mP;
  o = vec4(mix(c, matched, uStrength), 1.0);
}`;

// ラプラシアンピラミッド最上位の初期化: gaussian 同士をマスクで合成
export const FS_TOP = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uA, uB, uM; // A=live(色補正後), B=pre, M=mask
void main(){
  float m = texture(uM, vUv).r;
  o = mix(texture(uB, vUv), texture(uA, vUv), m);
}`;

// ラプラシアン collapse 1レベル分:
//   lap = mix(lapB, lapA, mask);  out = lap + upsample(prevBlended)
//   小さい方(gA1/gB1/prev)は bilinear で自動 upsample される。
export const FS_COLLAPSE = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D gA0, gA1; // gaussA[i], gaussA[i+1]
uniform sampler2D gB0, gB1; // gaussB[i], gaussB[i+1]
uniform sampler2D gM0;      // gaussM[i]
uniform sampler2D uPrev;    // blended[i+1]
void main(){
  vec4 lapA = texture(gA0, vUv) - texture(gA1, vUv);
  vec4 lapB = texture(gB0, vUv) - texture(gB1, vUv);
  float m = texture(gM0, vUv).r;
  o = mix(lapB, lapA, m) + texture(uPrev, vUv);
}`;

// ラプラシアン OFF 時の単純フェザー合成（フル解像度）
export const FS_SIMPLE = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uA, uB, uM;
void main(){
  float m = texture(uM, vUv).r;
  o = mix(texture(uB, vUv), texture(uA, vUv), m);
}`;

// 後段ポストFX + 画面出力
export const FS_POST = `#version 300 es
precision highp float;
in vec2 vUv; out vec4 o;
uniform sampler2D uTex;
uniform sampler2D uMask;     // マスク可視化用
uniform float uTime;
uniform float uExposure;     // EV
uniform float uContrast;     // 1=neutral
uniform float uSaturation;   // 1=neutral
uniform float uTemperature;  // -1..1 (cool..warm)
uniform float uVignette;     // 0..1
uniform float uGrain;        // 0..1
uniform float uAberration;   // px 相当 0..~20
uniform float uScanline;     // 0..1
uniform float uShowMask;     // 0/1 マスク境界オーバーレイ
uniform vec2  uTexel;

float hash(vec2 p){
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

void main(){
  vec2 uv = vUv;
  // 色収差: 中心からの放射方向に RGB をずらす
  vec2 dir = uv - 0.5;
  vec3 col;
  if(uAberration > 0.001){
    vec2 off = dir * uAberration * uTexel.x;
    col.r = texture(uTex, uv + off).r;
    col.g = texture(uTex, uv).g;
    col.b = texture(uTex, uv - off).b;
  } else {
    col = texture(uTex, uv).rgb;
  }

  // 露出
  col *= pow(2.0, uExposure);
  // 色温度
  col.r *= 1.0 + 0.25 * uTemperature;
  col.b *= 1.0 - 0.25 * uTemperature;
  // コントラスト
  col = (col - 0.5) * uContrast + 0.5;
  // 彩度
  float l = dot(col, vec3(0.299, 0.587, 0.114));
  col = mix(vec3(l), col, uSaturation);

  // ヴィネット
  float vig = 1.0 - uVignette * dot(dir, dir) * 2.2;
  col *= clamp(vig, 0.0, 1.0);

  // 走査線
  if(uScanline > 0.001){
    float s = 0.5 + 0.5 * sin(uv.y * (1.0/uTexel.y) * 3.14159);
    col *= 1.0 - uScanline * (1.0 - s) * 0.6;
  }

  // フィルムグレイン
  if(uGrain > 0.001){
    float n = hash(uv * vec2(1.0/uTexel.x, 1.0/uTexel.y) + uTime);
    col += (n - 0.5) * uGrain;
  }

  // マスク境界オーバーレイ（編集補助）
  if(uShowMask > 0.5){
    float m = texture(uMask, uv).r;
    float e = abs(dFdx(m)) + abs(dFdy(m));
    col = mix(col, vec3(0.1, 1.0, 0.4), clamp(e * 6.0, 0.0, 0.9));
  }

  o = vec4(clamp(col, 0.0, 1.0), 1.0);
}`;
