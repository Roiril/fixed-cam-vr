/*
 * title-write.js — 題字「廻リ視」の筆の書き出し（来場者タブレット用）
 *
 * Quest の VR アプリの題字と同じ筆順（廻 → リ → 視 → 払い・ルビ）で墨が出る。
 * 時刻の図 asset/title-logo-write-v1.png は tools/make-tablet-title-writeon.py が焼く。
 * 依存なし・モジュールなし。ページのマークアップより後ろで読み込む。
 *
 *   <h1 id="titleBrand"><img src="asset/title-logo-v2.png"></h1>
 *   <section id="titleView" class="... is-active"> … </section>   ← is-active が付く / hidden が外れるたびに再生
 *
 * 描画: WebGL1 → (だめなら) Canvas2D の ImageData → (だめなら) <img> をそのまま見せる。
 * window.TitleWrite = { play(), stop(), seek(p, wet), mode }
 */
(function () {
  'use strict';

  // ---- 調整値（Quest 版と同じ書き出し） --------------------------------------
  var DURATION = 0.9;      // 書き出しの長さ（秒）。進み p は 0→1 を線形に
  var F = 0.035;           // 筆先の幅（時刻の単位）
  var WET_DECAY = 1.6;     // 書き終えてから濡れが引くまで（秒）
  var DITHER = 0.75 / 255; // 時刻の図の量子化の段差を消す揺らぎ（±）
  var SHEEN = 0.38;        // 筆先の照り
  var BLEED = 0.18;        // 滲みの濃さ
  var BLEED_TONE = 0.78;   // 滲みの色は墨より少し暗く
  var BLEED_SIGMA = 5;     // 滲みのぼかし（元画像の px）
  var MAP_NAME = 'title-logo-write-v1.png';
  var CAP_2D = 760;        // Canvas2D 版の作業幅の上限

  var api = {
    mode: 'none',
    ready: false,
    play: function () {},
    stop: function () {},
    seek: function () {}
  };
  window.TitleWrite = api;

  function start() {
    var img = document.querySelector('#titleBrand img');
    var view = document.getElementById('titleView');
    if (!img || !view) return;
    try {
      init(img, view);
    } catch (e) {
      giveUp(img, e);
    }
  }

  function giveUp(img, e) {
    try {
      if (window.console) console.warn('[title-write] 書き出しを諦めて画像をそのまま見せる:', e);
      img.style.opacity = '';
      var c = img.parentNode && img.parentNode.querySelector('canvas[data-title-write]');
      if (c) c.style.display = 'none';
    } catch (_) { /* 何もしない */ }
    api.mode = 'img';
    api.play = api.stop = api.seek = function () {};
  }

  function init(img, view) {
    var dead = false;
    var ready = false;       // 資源が揃って描ける
    var pending = false;     // 揃う前に play が呼ばれた
    var gl = null, prog = null, uni = null;
    var canvas = null, ctx2d = null;
    var src = null;          // {w, h, logo, map, bleed}
    var raf = 0, t0 = 0;
    var last = { p: 1, wet: 0, fin: 1 };
    var logoImg = null, mapImg = null;
    var origOpacity = img.style.opacity;

    // 先に隠す（揃うまでの一瞬に完成形が見えて、書き出しの頭で消えるのを避ける）
    img.style.opacity = '0';

    function fail(e) {
      dead = true;
      cancelAnimationFrame(raf);
      giveUp(img, e);
      img.style.opacity = origOpacity || '';
    }

    // ⚠ スタッフ画面（<dialog id="staff">）が開いているあいだ、題字はその裏で「出ている」ことになる。
    //   そこで書き出しを終えると、来場者に渡した瞬間には書き終わった絵しか残らない。
    //   来場者の目に入っているときだけ「見えている」と数える。
    var staffDialog = document.getElementById('staff');
    function visible() {
      return view.classList.contains('is-active') && !view.hidden
        && !(staffDialog && staffDialog.open) && !document.hidden;
    }

    // ---- キャンバスの置き方 ---------------------------------------------------
    function makeCanvas() {
      var c = document.createElement('canvas');
      c.setAttribute('aria-hidden', 'true');
      c.setAttribute('data-title-write', '');
      var s = c.style;
      s.position = 'absolute';
      s.pointerEvents = 'none';
      s.display = 'block';
      s.margin = '0';
      s.padding = '0';
      s.border = '0';
      s.maxWidth = 'none';
      return c;
    }

    function installCanvas(c) {
      if (canvas && canvas.parentNode) canvas.parentNode.replaceChild(c, canvas);
      else img.parentNode.insertBefore(c, img.nextSibling);
      canvas = c;
    }

    // img の箱に重ねる。戻り値は背面の大きさ（CSS px）。箱が無ければ null
    function box() {
      var w = img.offsetWidth, h = img.offsetHeight;
      if (!w || !h) return null;
      var cw = w, ch = h, cx = 0, cy = 0;
      var nw = img.naturalWidth, nh = img.naturalHeight;
      var fit = '';
      try { fit = window.getComputedStyle(img).objectFit; } catch (_) { /* */ }
      if (nw && nh && (fit === 'contain' || fit === 'scale-down')) {
        var ar = nw / nh;
        if (w / h > ar) { cw = h * ar; cx = (w - cw) / 2; }
        else { ch = w / ar; cy = (h - ch) / 2; }
      }
      return { w: cw, h: ch, x: img.offsetLeft + cx, y: img.offsetTop + cy };
    }

    function layout() {
      if (!canvas) return false;
      var b = box();
      if (!b) return false;
      var s = canvas.style;
      s.left = b.x + 'px';
      s.top = b.y + 'px';
      s.width = b.w + 'px';
      s.height = b.h + 'px';
      if (api.mode === 'webgl') {
        var dpr = Math.min(window.devicePixelRatio || 1, 3);
        var bw = Math.min(2400, Math.max(1, Math.round(b.w * dpr)));
        var bh = Math.max(1, Math.round(bw * b.h / b.w));
        if (canvas.width !== bw || canvas.height !== bh) {
          canvas.width = bw;
          canvas.height = bh;
          if (gl) gl.viewport(0, 0, bw, bh);
          if (ready) render(last.p, last.wet, last.fin);
        }
      }
      return true;
    }

    // ---- 資源 -------------------------------------------------------------------
    function loadImage(url) {
      return new Promise(function (res, rej) {
        var im = new Image();
        im.onload = function () { res(im); };
        im.onerror = function () { rej(new Error('画像を読めない: ' + url)); };
        im.src = url;
      });
    }

    function scaledCanvas(el, sw, sh, tw, th) {
      // 半分ずつ縮める（NPOT でミップマップが使えないので、エイリアスを自分で避ける）
      var cur = el, cw = sw, ch = sh;
      while (cw / 2 >= tw && ch / 2 >= th) {
        var nw = Math.ceil(cw / 2), nh = Math.ceil(ch / 2);
        var c = document.createElement('canvas');
        c.width = nw; c.height = nh;
        var g = c.getContext('2d');
        g.imageSmoothingQuality = 'high';
        g.drawImage(cur, 0, 0, nw, nh);
        cur = c; cw = nw; ch = nh;
      }
      var out = document.createElement('canvas');
      out.width = tw; out.height = th;
      var g2 = out.getContext('2d');
      g2.imageSmoothingQuality = 'high';
      g2.drawImage(cur, 0, 0, tw, th);
      return out;
    }

    // 作業幅を決める。箱がまだ無ければ元の大きさ
    function pickWidth(natW, cap) {
      var w = natW;
      var b = box();
      if (b) {
        var dpr = Math.min(window.devicePixelRatio || 1, 3);
        var target = Math.round(b.w * dpr);
        if (natW > 2 * target) w = Math.round(target * 1.25);
      }
      if (cap && w > cap) w = cap;
      return Math.max(8, Math.min(w, natW));
    }

    function boxesForGauss(sigma) {
      var n = 3;
      var wi = Math.sqrt(12 * sigma * sigma / n + 1);
      var wl = Math.floor(wi);
      if (wl % 2 === 0) wl--;
      var wu = wl + 2;
      var mi = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4);
      var m = Math.round(mi);
      var r = [];
      for (var i = 0; i < n; i++) r.push(((i < m ? wl : wu) - 1) / 2);
      return r;
    }

    function blurPlane(a, tmp, w, h, r) {
      var k = 1 / (2 * r + 1), x, y, acc, o;
      for (y = 0; y < h; y++) {
        o = y * w; acc = 0;
        for (x = 0; x <= r && x < w; x++) acc += a[o + x];
        for (x = 0; x < w; x++) {
          tmp[o + x] = acc * k;
          if (x + r + 1 < w) acc += a[o + x + r + 1];
          if (x - r >= 0) acc -= a[o + x - r];
        }
      }
      for (x = 0; x < w; x++) {
        acc = 0;
        for (y = 0; y <= r && y < h; y++) acc += tmp[y * w + x];
        for (y = 0; y < h; y++) {
          a[y * w + x] = acc * k;
          if (y + r + 1 < h) acc += tmp[(y + r + 1) * w + x];
          if (y - r >= 0) acc -= tmp[(y - r) * w + x];
        }
      }
    }

    // 題字を ~5px ぼかした「滲み」の RGBA（straight）。alpha = ぼかした alpha、色 = 近くの墨の色
    function makeBleed(logoData, w, h, sigma) {
      var n = w * h, i;
      var pr = new Float32Array(n), pg = new Float32Array(n), pb = new Float32Array(n), pa = new Float32Array(n);
      var d = logoData.data;
      for (i = 0; i < n; i++) {
        var a = d[i * 4 + 3] / 255;
        pr[i] = d[i * 4] / 255 * a;
        pg[i] = d[i * 4 + 1] / 255 * a;
        pb[i] = d[i * 4 + 2] / 255 * a;
        pa[i] = a;
      }
      var tmp = new Float32Array(n);
      var rs = boxesForGauss(Math.max(1, sigma));
      for (var j = 0; j < rs.length; j++) {
        var r = Math.max(1, rs[j]);
        blurPlane(pr, tmp, w, h, r); blurPlane(pg, tmp, w, h, r);
        blurPlane(pb, tmp, w, h, r); blurPlane(pa, tmp, w, h, r);
      }
      var out = new Uint8ClampedArray(n * 4);
      for (i = 0; i < n; i++) {
        var al = pa[i];
        var k = al > 1e-4 ? 1 / al : 0;
        out[i * 4] = Math.min(255, pr[i] * k * 255 + 0.5);
        out[i * 4 + 1] = Math.min(255, pg[i] * k * 255 + 0.5);
        out[i * 4 + 2] = Math.min(255, pb[i] * k * 255 + 0.5);
        out[i * 4 + 3] = Math.min(255, al * 255 + 0.5);
      }
      return out;
    }

    // WebGL / Canvas2D 共通の元データ。cap は作業幅の上限（Canvas2D 用）
    function buildSources(cap) {
      var natW = logoImg.naturalWidth, natH = logoImg.naturalHeight;
      var w = pickWidth(natW, cap);
      var h = Math.max(1, Math.round(natH * w / natW));
      var logoEl = (w === natW && h === natH) ? logoImg : scaledCanvas(logoImg, natW, natH, w, h);
      var mapEl = (w === mapImg.naturalWidth && h === mapImg.naturalHeight)
        ? mapImg : scaledCanvas(mapImg, mapImg.naturalWidth, mapImg.naturalHeight, w, h);
      var out = { w: w, h: h, logo: logoEl, map: mapEl, bleed: null, logoData: null, mapData: null };
      return out;
    }

    // ---- WebGL ------------------------------------------------------------------
    var VS = 'attribute vec2 aPos; varying vec2 vUv;' +
      'void main(){ vUv = vec2(aPos.x*0.5+0.5, 0.5-aPos.y*0.5); gl_Position = vec4(aPos,0.0,1.0); }';
    var FS = '#ifdef GL_FRAGMENT_PRECISION_HIGH\nprecision highp float;\n#else\nprecision mediump float;\n#endif\n' +
      'varying vec2 vUv;' +
      'uniform sampler2D uLogo; uniform sampler2D uMap; uniform sampler2D uBleed;' +
      'uniform vec2 uTexSize; uniform float uP; uniform float uWet; uniform float uFin;' +
      'const float F = ' + F.toFixed(4) + ';' +
      'void main(){' +
      '  vec4 logo = texture2D(uLogo, vUv);' +
      '  float m = texture2D(uMap, vUv).r;' +
      '  vec4 bl = texture2D(uBleed, vUv);' +
      '  vec2 cell = floor(vUv * uTexSize);' +
      '  float n = fract(52.9829189 * fract(dot(cell, vec2(0.06711056, 0.00583715)))) * 2.0 - 1.0;' +
      '  float T = m + n * ' + DITHER.toFixed(6) + ';' +
      '  if (m > 0.998) T = 4.0;' +
      '  float age = uP * (1.0 + F) - T;' +
      '  float reveal = max(clamp(age / F, 0.0, 1.0), uFin);' +
      '  float x = (age - 0.6 * F) / F;' +
      '  float sheen = 1.0 + ' + SHEEN.toFixed(3) + ' * exp(-x * x) * uWet * (1.0 - uFin);' +
      '  vec3 ink = min(logo.rgb * sheen, vec3(1.0));' +
      '  float a = logo.a * reveal;' +
      '  float bA = bl.a * clamp(age / (1.5 * F), 0.0, 1.0) * uWet * ' + BLEED.toFixed(3) + ' * (1.0 - uFin);' +
      '  vec3 bc = bl.rgb * ' + BLEED_TONE.toFixed(3) + ';' +
      '  vec3 prem = ink * a + bc * bA * (1.0 - a);' +
      '  float oa = a + bA * (1.0 - a);' +
      '  gl_FragColor = vec4(prem, oa);' +
      '}';

    function compile(type, text) {
      var s = gl.createShader(type);
      gl.shaderSource(s, text);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error('shader: ' + gl.getShaderInfoLog(s));
      return s;
    }

    function makeTex(unit, source, w, h, bytes) {
      var t = gl.createTexture();
      gl.activeTexture(gl.TEXTURE0 + unit);
      gl.bindTexture(gl.TEXTURE_2D, t);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      if (bytes) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
      else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
      return t;
    }

    function initGL() {
      var c = makeCanvas();
      var g = c.getContext('webgl', { alpha: true, premultipliedAlpha: true, antialias: false, depth: false, stencil: false }) ||
        c.getContext('experimental-webgl', { alpha: true, premultipliedAlpha: true, antialias: false });
      if (!g) throw new Error('WebGL が無い');
      gl = g;
      installCanvas(c);
      api.mode = 'webgl';
      c.addEventListener('webglcontextlost', function (ev) {
        ev.preventDefault();
        ready = false;
        fail(new Error('WebGL の文脈を失った'));
      }, false);

      prog = gl.createProgram();
      gl.attachShader(prog, compile(gl.VERTEX_SHADER, VS));
      gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, FS));
      gl.bindAttribLocation(prog, 0, 'aPos');
      gl.linkProgram(prog);
      if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error('link: ' + gl.getProgramInfoLog(prog));
      gl.useProgram(prog);
      uni = {
        logo: gl.getUniformLocation(prog, 'uLogo'), map: gl.getUniformLocation(prog, 'uMap'),
        bleed: gl.getUniformLocation(prog, 'uBleed'), size: gl.getUniformLocation(prog, 'uTexSize'),
        p: gl.getUniformLocation(prog, 'uP'), wet: gl.getUniformLocation(prog, 'uWet'),
        fin: gl.getUniformLocation(prog, 'uFin')
      };
      var buf = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
      gl.enableVertexAttribArray(0);
      gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
      gl.disable(gl.BLEND);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      if (gl.NONE !== undefined) gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);

      src = buildSources(0);
      makeTex(0, src.logo, 0, 0, null);
      makeTex(1, src.map, 0, 0, null);
      // 滲み。読めなければ空の滲みで続ける（書き出し自体は動く）
      var bleedBytes = null;
      try {
        var cv = document.createElement('canvas');
        cv.width = src.w; cv.height = src.h;
        var cx = cv.getContext('2d');
        cx.drawImage(src.logo, 0, 0, src.w, src.h);
        var ld = cx.getImageData(0, 0, src.w, src.h);
        bleedBytes = makeBleed(ld, src.w, src.h, BLEED_SIGMA * src.w / logoImg.naturalWidth);
      } catch (e) {
        if (window.console) console.warn('[title-write] 滲みを作れない（滲み無しで続ける）:', e);
      }
      if (bleedBytes) makeTex(2, null, src.w, src.h, bleedBytes);
      else makeTex(2, null, 1, 1, new Uint8Array([0, 0, 0, 0]));
      gl.uniform1i(uni.logo, 0);
      gl.uniform1i(uni.map, 1);
      gl.uniform1i(uni.bleed, 2);
      gl.uniform2f(uni.size, src.w, src.h);
      var err = gl.getError();
      if (err !== gl.NO_ERROR) throw new Error('GL エラー ' + err);
    }

    // ---- Canvas2D（ImageData） -------------------------------------------------
    var d2 = null;
    function initCanvas2D() {
      var c = makeCanvas();
      var g = c.getContext('2d');
      if (!g) throw new Error('Canvas2D が無い');
      api.mode = 'canvas2d';
      gl = null;
      installCanvas(c);
      ctx2d = g;
      src = buildSources(CAP_2D);
      c.width = src.w; c.height = src.h;
      var cv = document.createElement('canvas');
      cv.width = src.w; cv.height = src.h;
      var cx = cv.getContext('2d');
      cx.drawImage(src.logo, 0, 0, src.w, src.h);
      var ld = cx.getImageData(0, 0, src.w, src.h);   // 汚染されていればここで投げる
      cx.clearRect(0, 0, src.w, src.h);
      cx.drawImage(src.map, 0, 0, src.w, src.h);
      var md = cx.getImageData(0, 0, src.w, src.h);
      var n = src.w * src.h, gray = new Uint8Array(n);
      for (var i = 0; i < n; i++) gray[i] = md.data[i * 4];
      var bleed = null;
      try { bleed = makeBleed(ld, src.w, src.h, BLEED_SIGMA * src.w / logoImg.naturalWidth); } catch (e) { /* 滲み無し */ }
      d2 = { logo: ld.data, map: gray, bleed: bleed, out: ctx2d.createImageData(src.w, src.h) };
    }

    function render2D(p, wet, fin) {
      var W = src.w, H = src.h, n = W * H;
      var L = d2.logo, M = d2.map, B = d2.bleed, O = d2.out.data;
      var inv255 = 1 / 255;
      for (var i = 0; i < n; i++) {
        var o = i * 4;
        var la = L[o + 3] * inv255;
        var ba0 = B ? B[o + 3] * inv255 : 0;
        if (la === 0 && ba0 === 0) { O[o] = O[o + 1] = O[o + 2] = O[o + 3] = 0; continue; }
        var m = M[i] * inv255;
        var T = m;
        if (m > 0.998) T = 4;
        else {
          var xx = i % W, yy = (i - xx) / W;
          var v = (xx * 0.06711056 + yy * 0.00583715);
          v = v - Math.floor(v);
          v = 52.9829189 * v;
          v = v - Math.floor(v);
          T += (v * 2 - 1) * DITHER;
        }
        var age = p * (1 + F) - T;
        var rev = age / F; rev = rev < 0 ? 0 : (rev > 1 ? 1 : rev); if (fin > rev) rev = fin;
        var sheen = 1;
        if (!fin && wet > 0) {
          var x = (age - 0.6 * F) / F;
          if (x > -3.5 && x < 3.5) sheen = 1 + SHEEN * Math.exp(-x * x) * wet;
        }
        var a = la * rev;
        var bA = 0;
        if (!fin && B && wet > 0) {
          var bk = age / (1.5 * F); bk = bk < 0 ? 0 : (bk > 1 ? 1 : bk);
          bA = ba0 * bk * wet * BLEED;
        }
        var oa = a + bA * (1 - a);
        if (oa <= 0) { O[o] = O[o + 1] = O[o + 2] = O[o + 3] = 0; continue; }
        var ia = a, ib = bA * (1 - a);
        for (var k = 0; k < 3; k++) {
          var ink = L[o + k] * sheen; if (ink > 255) ink = 255;
          var bc = B ? B[o + k] * BLEED_TONE : 0;
          O[o + k] = (ink * ia + bc * ib) / oa + 0.5;
        }
        O[o + 3] = oa * 255 + 0.5;
      }
      ctx2d.putImageData(d2.out, 0, 0);
    }

    // ---- 描画と時間 -------------------------------------------------------------
    function render(p, wet, fin) {
      last.p = p; last.wet = wet; last.fin = fin;
      if (!ready) return;
      if (gl) {
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.uniform1f(uni.p, p);
        gl.uniform1f(uni.wet, wet);
        gl.uniform1f(uni.fin, fin);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      } else if (ctx2d) {
        render2D(p, wet, fin);
      }
    }

    function smooth(x) { x = x < 0 ? 0 : (x > 1 ? 1 : x); return x * x * (3 - 2 * x); }

    function frame(now) {
      raf = 0;
      if (dead) return;
      var t = (now - t0) / 1000;
      if (t < 0) t = 0;
      var end = DURATION + WET_DECAY;
      if (t >= end) { render(1, 0, 1); return; }          // 完成形。ここでループを止める
      var p = t >= DURATION ? 1 : t / DURATION;
      var wet = t < DURATION ? 1 : 1 - smooth((t - DURATION) / WET_DECAY);
      render(p, wet, 0);
      raf = requestAnimationFrame(frame);
    }

    function reduced() {
      try { return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches); }
      catch (_) { return false; }
    }

    function stop() {
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
      pending = false;
      render(1, 0, 1);
    }

    function play() {
      if (dead) return;
      if (!ready) { pending = true; return; }
      pending = false;
      api.plays = (api.plays || 0) + 1;   // 観測用（何回書き出したか）
      // 書き出しの頭で題字の音を鳴らす（index.html が拾う。音の正は tablet-sounds.json）
      try { document.dispatchEvent(new CustomEvent('titlewrite:play')); } catch (_) { /* */ }
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
      layout();
      if (reduced()) { render(1, 0, 1); return; }
      render(0, 1, 0);                       // 最初の 1 枚を描いてから進める（完成形を見せない）
      t0 = performance.now();
      raf = requestAnimationFrame(frame);
    }

    function seek(p, wet) {                  // 試験用: 任意の 1 コマを描いて止める
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
      layout();
      var fin = (p >= 1 && !wet) ? 1 : 0;
      render(p, wet === undefined ? 1 : wet, fin);
    }

    api.play = play;
    api.stop = stop;
    api.seek = seek;

    // ---- 起動 -------------------------------------------------------------------
    var wasVisible = false;
    function onView() {
      var v = visible();
      if (v && !wasVisible) play();
      wasVisible = v;
    }

    var mo = new MutationObserver(onView);
    mo.observe(view, { attributes: true, attributeFilter: ['class', 'hidden'] });
    if (staffDialog) mo.observe(staffDialog, { attributes: true, attributeFilter: ['open'] });
    document.addEventListener('visibilitychange', onView, false);

    var relayout = function () { if (!dead) layout(); };
    window.addEventListener('resize', relayout, false);
    window.addEventListener('orientationchange', relayout, false);
    if (typeof ResizeObserver === 'function') {
      try { new ResizeObserver(relayout).observe(img); } catch (_) { /* */ }
    }
    img.addEventListener('load', relayout, false);

    var logoUrl = img.currentSrc || img.src;
    var mapUrl = img.getAttribute('data-write-map') || new URL(MAP_NAME, logoUrl).href;
    Promise.all([loadImage(logoUrl), loadImage(mapUrl)]).then(function (r) {
      if (dead) return;
      logoImg = r[0]; mapImg = r[1];
      try {
        initGL();
      } catch (e1) {
        if (window.console) console.warn('[title-write] WebGL をやめて Canvas2D へ:', e1);
        try {
          initCanvas2D();
        } catch (e2) { fail(e2); return; }
      }
      layout();
      ready = true;
      api.ready = true;
      // 取りこぼし: 箱がまだ無かった場合は、のちの resize / load で layout が走る
      if (visible()) { wasVisible = true; play(); }
      else if (pending) play();
      else render(1, 0, 1);                  // 非表示のあいだは完成形を持っておく
    }).catch(fail);

    wasVisible = visible();
    if (wasVisible) pending = true;
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, false);
  else start();
})();
