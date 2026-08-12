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
uniform float uStrength; // 演出フェード（0=オーバーレイ無し / 1=フル）。未指定時は 1。
void main(){
  float sum = 0.0; float wsum = 0.0;
  // 5x5 で広めにサンプルしてフェザー（境界ぼかし）を効かせる
  for(int j=-2;j<=2;j++){
    for(int i=-2;i<=2;i++){
      vec2 off = vec2(float(i), float(j)) * uTexel * uRadius;
      sum += texture(uTex, vUv + off).r;
      wsum += 1.0;
    }
  }
  float m = (sum / wsum) * uStrength;
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
//   ★ ビューの最終見た目（= Quest と同じ絵）はこの FS_POST。Unity
//      Assets/Art/Shaders/Streaming/ScreenComposite.shader と数式・順序を一致させてある
//      （露出→色温度[乗算]→色かぶり[乗算]→コントラスト→黒浮き→彩度→ヴィネット[dot*2.2]→走査線→グレイン）。
//      合成（contain-fit / マスク / 色統計 / ラプラシアン）は pipeline.js の多パスで行う。
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
uniform float uAberration;   // 0..1（画面端で最大 2% ずれ。Unity の _Aberration と同式）
uniform float uPixelate;     // 0..1（サンプル位置の量子化。Unity の _Pixelate と同式）
uniform float uScanline;     // 0..1
uniform float uScanlineCount;// 走査線の本数（0 = 既定 240）
uniform float uLift;         // 0..0.3 黒浮き（コントラストの後に床上げ）
uniform float uTint;         // -1..1 色かぶり（+ 緑 / - マゼンタ）
uniform float uShowMask;     // 0/1 マスク境界オーバーレイ
uniform vec2  uTexel;

float hash(vec2 p){
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

void main(){
  vec2 uv = vUv;
  vec2 dir = uv - 0.5;

  // 低解像度化: サンプル位置を量子化する（撮像側が粗い、という表現なので色ではなく位置）。
  // ブロックが正方に見えるよう縦は枠アスペクトで割る。
  if(uPixelate > 0.001){
    float frameAspect = (uTexel.y > 0.0 ? (1.0/uTexel.x) / (1.0/uTexel.y) : 1.0);
    float bx = max(6.0, floor(mix(400.0, 18.0, clamp(uPixelate, 0.0, 1.0))));
    float by = max(4.0, floor(bx / max(frameAspect, 1e-3)));
    vec2 b = vec2(bx, by);
    uv = (floor(uv * b) + 0.5) / b;
  }

  // 色収差: 中心からの放射方向に RGB をずらす（端ほど強い）。
  vec3 col;
  if(uAberration > 0.001){
    vec2 c = uv - 0.5;
    float r = clamp(length(c) * 2.0, 0.0, 1.0);
    vec2 off = c * (r * r) * uAberration * 0.02;
    col.r = texture(uTex, uv + off).r;
    col.g = texture(uTex, uv).g;
    col.b = texture(uTex, uv - off).b;
  } else {
    col = texture(uTex, uv).rgb;
  }

  // ⚠ 順序は**撮像の順**（レンズ → センサ → ISP）。2026-08-12 に Unity と揃えて組み替えた
  //    （canon/LEDGER.md 0018）。旧実装は現像の後にヴィネットを掛けており、角が
  //    「後から乗せた黒い楕円」に見えていた。届かなかった光は後段でも戻らないので、レンズが先。
  //    ⚠ 卓に無いもの: センサの粒・レンズのグレア・周回で痩せる伝送（実機だけが持つ装置の挙動）。
  // レンズ（周辺光量。cos^4 に近い r^4。放射 2 次だと中心付近から効いて楕円に見える）
  {
    float r2 = clamp(dot(dir, dir) * 4.0, 0.0, 1.0);
    col *= 1.0 - clamp(uVignette, 0.0, 1.0) * 0.58 * r2 * r2;
  }
  // 露出
  col *= pow(2.0, uExposure);
  // 色温度
  col.r *= 1.0 + 0.25 * uTemperature;
  col.b *= 1.0 - 0.25 * uTemperature;
  // 色かぶり（緑↔マゼンタ・色温度と直交）
  col.g *= 1.0 + 0.25 * uTint;
  col.r *= 1.0 - 0.12 * uTint;
  col.b *= 1.0 - 0.12 * uTint;
  // コントラスト
  col = (col - 0.5) * uContrast + 0.5;
  // 黒浮き（コントラストの後。前だと潰れて効かない）
  col = col * (1.0 - uLift) + uLift;
  // 彩度
  float l = dot(col, vec3(0.299, 0.587, 0.114));
  col = mix(vec3(l), col, uSaturation);

  // 走査線。**本数は uScanlineCount（0 = 既定 240）**。
  // canvas の縦画素を使っていた頃は、面ごとに 360〜480 とばらつき、同じ値でも実機（240 固定）と
  // 縞のピッチが 1.5〜2 倍食い違っていた。
  if(uScanline > 0.001){
    float lines = uScanlineCount > 0.0 ? uScanlineCount : 240.0;
    float s = 0.5 + 0.5 * sin(vUv.y * lines * 3.14159265);
    col *= 1.0 - uScanline * (1.0 - s) * 0.6;
  }

  // フィルムグレイン。Unity と同じ等方 480 グリッド + fract(時間) にする
  // （canvas 実寸を使うと面ごとに粒の大きさが変わり、実機と食い違う）。
  if(uGrain > 0.001){
    float n = hash(vUv * 480.0 + fract(uTime));
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
