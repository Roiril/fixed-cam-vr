(() => {
  'use strict';

  const IMAGE_URL = 'assets/title.png';
  const boot = window.MAWARIMI_ENTRANCE_BOOT;
  const DURATION = window.MAWARIMI_ENTRANCE_TIMING.duration;
  const DEPTH = 0.14;
  const TITLE_WIDTH = 3.2;
  const TITLE_HEIGHT = TITLE_WIDTH * 531 / 1210;

  function randomGenerator(seed) {
    let state = seed >>> 0;
    return () => {
      state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
      return state / 4294967296;
    };
  }

  function clipPolygon(polygon, nx, ny, limit) {
    const result = [];
    for (let i = 0; i < polygon.length; i += 1) {
      const a = polygon[i];
      const b = polygon[(i + 1) % polygon.length];
      const da = a[0] * nx + a[1] * ny - limit;
      const db = b[0] * nx + b[1] * ny - limit;
      if (da <= 0) result.push(a);
      if ((da < 0 && db > 0) || (da > 0 && db < 0)) {
        const t = da / (da - db);
        result.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]);
      }
    }
    return result;
  }

  function makeShards() {
    const random = randomGenerator(19092026);
    const seeds = [];
    for (let row = 0; row < 6; row += 1) {
      for (let col = 0; col < 10; col += 1) {
        seeds.push([
          (col + 0.5 + (random() - 0.5) * 0.6) / 10,
          (row + 0.5 + (random() - 0.5) * 0.6) / 6
        ]);
      }
    }
    return seeds.map((seed, index) => {
      let polygon = [[0, 0], [1, 0], [1, 1], [0, 1]];
      for (let other = 0; other < seeds.length && polygon.length; other += 1) {
        if (other === index) continue;
        const rival = seeds[other];
        const nx = rival[0] - seed[0];
        const ny = rival[1] - seed[1];
        const limit = (rival[0] * rival[0] + rival[1] * rival[1] - seed[0] * seed[0] - seed[1] * seed[1]) / 2;
        polygon = clipPolygon(polygon, nx, ny, limit);
      }
      return { center: seed, polygon, random: [random(), random(), random(), random()] };
    });
  }

  function insidePolygon(x, y, polygon) {
    let inside = false;
    for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i, i += 1) {
      const a = polygon[i];
      const b = polygon[j];
      if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
    }
    return inside;
  }

  function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const error = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error(error);
    }
    return shader;
  }

  function createRenderer(canvas, image) {
    const gl = canvas.getContext('webgl', { alpha: true, antialias: true, depth: true, premultipliedAlpha: false });
    if (!gl) throw new Error('WebGL unavailable');
    let program;
    let texture;
    const buffers = [];
    try {
      const vertex = compile(gl, gl.VERTEX_SHADER, `
        attribute vec3 aPosition;
        attribute vec2 aUv;
        attribute float aShade;
        uniform vec2 uViewport;
        uniform float uPixelScale;
        uniform vec2 uTilt;
        uniform vec2 uCenter;
        uniform vec3 uShift;
        uniform vec3 uRotation;
        varying vec2 vUv;
        varying float vShade;
        void main() {
          vec3 p = aPosition - vec3(uCenter, 0.0);
          float cz = cos(uRotation.z), sz = sin(uRotation.z);
          p.xy = mat2(cz, -sz, sz, cz) * p.xy;
          float cy = cos(uRotation.y), sy = sin(uRotation.y);
          p.xz = mat2(cy, sy, -sy, cy) * p.xz;
          float cx = cos(uRotation.x), sx = sin(uRotation.x);
          p.yz = mat2(cx, -sx, sx, cx) * p.yz;
          p += vec3(uCenter, 0.0) + uShift;
          cy = cos(uTilt.x); sy = sin(uTilt.x);
          p.xz = mat2(cy, sy, -sy, cy) * p.xz;
          cx = cos(uTilt.y); sx = sin(uTilt.y);
          p.yz = mat2(cx, -sx, sx, cx) * p.yz;
          float perspective = 4.2 / (4.2 - p.z);
          gl_Position = vec4(p.xy * uPixelScale * 2.0 / uViewport * perspective, -p.z / 4.2, 1.0);
          vUv = aUv;
          vShade = aShade;
        }
      `);
      const fragment = compile(gl, gl.FRAGMENT_SHADER, `
        precision mediump float;
        uniform sampler2D uTexture;
        varying vec2 vUv;
        varying float vShade;
        void main() {
          vec4 color = texture2D(uTexture, vUv);
          if (color.a < 0.08) discard;
          gl_FragColor = vec4(color.rgb * vShade, color.a);
        }
      `);
      program = gl.createProgram();
      gl.attachShader(program, vertex);
      gl.attachShader(program, fragment);
      gl.linkProgram(program);
      gl.deleteShader(vertex);
      gl.deleteShader(fragment);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
      texture = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

      const sampleCanvas = document.createElement('canvas');
      sampleCanvas.width = 240;
      sampleCanvas.height = 104;
      const sampleContext = sampleCanvas.getContext('2d', { willReadFrequently: true });
      if (!sampleContext) throw new Error('Canvas unavailable');
      sampleContext.drawImage(image, 0, 0, 240, 104);
      const pixels = sampleContext.getImageData(0, 0, 240, 104).data;
      const occupied = (x, y) => x >= 0 && x < 240 && y >= 0 && y < 104 && pixels[(y * 240 + x) * 4 + 3] > 100;
      const shards = makeShards();
      const vertices = shards.map(() => []);
      const point = (u, v, z, shade) => [(u - 0.5) * TITLE_WIDTH, (0.5 - v) * TITLE_HEIGHT, z, u, 1 - v, shade];
      const triangle = (output, a, b, c) => output.push(...a, ...b, ...c);
      const wall = (output, u1, v1, u2, v2, shade, sampleU = (u1 + u2) / 2, sampleV = (v1 + v2) / 2) => {
        const a = point(u1, v1, DEPTH / 2, shade);
        const b = point(u2, v2, DEPTH / 2, shade);
        const c = point(u2, v2, -DEPTH / 2, shade);
        const d = point(u1, v1, -DEPTH / 2, shade);
        for (const vertex of [a, b, c, d]) {
          vertex[3] = sampleU;
          vertex[4] = 1 - sampleV;
        }
        triangle(output, a, b, c);
        triangle(output, a, c, d);
      };

      shards.forEach((shard, index) => {
        const output = vertices[index];
        const polygon = shard.polygon;
        if (polygon.length < 3) return;
        for (let i = 1; i < polygon.length - 1; i += 1) {
          triangle(output, point(...polygon[0], DEPTH / 2, 1), point(...polygon[i], DEPTH / 2, 1), point(...polygon[i + 1], DEPTH / 2, 1));
          triangle(output, point(...polygon[i + 1], -DEPTH / 2, 0.4), point(...polygon[i], -DEPTH / 2, 0.4), point(...polygon[0], -DEPTH / 2, 0.4));
        }
        for (let edge = 0; edge < polygon.length; edge += 1) {
          const a = polygon[edge];
          const b = polygon[(edge + 1) % polygon.length];
          const steps = Math.max(1, Math.ceil(Math.hypot((b[0] - a[0]) * 240, (b[1] - a[1]) * 104)));
          for (let step = 0; step < steps; step += 1) {
            const t1 = step / steps;
            const t2 = (step + 1) / steps;
            const midX = a[0] + (b[0] - a[0]) * (t1 + t2) / 2;
            const midY = a[1] + (b[1] - a[1]) * (t1 + t2) / 2;
            if (!occupied(Math.floor(midX * 240), Math.floor(midY * 104))) continue;
            wall(output, a[0] + (b[0] - a[0]) * t1, a[1] + (b[1] - a[1]) * t1,
              a[0] + (b[0] - a[0]) * t2, a[1] + (b[1] - a[1]) * t2, 0.52);
          }
        }
      });

      for (let y = 0; y < 104; y += 1) {
        for (let x = 0; x < 240; x += 1) {
          if (!occupied(x, y)) continue;
          const u = (x + 0.5) / 240;
          const v = (y + 0.5) / 104;
          const owner = shards.findIndex((shard) => insidePolygon(u, v, shard.polygon));
          if (owner < 0) continue;
          const output = vertices[owner];
          if (!occupied(x - 1, y)) wall(output, x / 240, y / 104, x / 240, (y + 1) / 104, 0.56, u, v);
          if (!occupied(x + 1, y)) wall(output, (x + 1) / 240, (y + 1) / 104, (x + 1) / 240, y / 104, 0.56, u, v);
          if (!occupied(x, y - 1)) wall(output, (x + 1) / 240, y / 104, x / 240, y / 104, 0.72, u, v);
          if (!occupied(x, y + 1)) wall(output, x / 240, (y + 1) / 104, (x + 1) / 240, (y + 1) / 104, 0.36, u, v);
        }
      }

      const meshes = vertices.map((data, index) => {
        const buffer = gl.createBuffer();
        if (!buffer) throw new Error('WebGL buffer unavailable');
        buffers.push(buffer);
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), gl.STATIC_DRAW);
        return { buffer, count: data.length / 6, shard: shards[index] };
      });
      const positions = {
        position: gl.getAttribLocation(program, 'aPosition'),
        uv: gl.getAttribLocation(program, 'aUv'),
        shade: gl.getAttribLocation(program, 'aShade'),
        viewport: gl.getUniformLocation(program, 'uViewport'),
        pixelScale: gl.getUniformLocation(program, 'uPixelScale'),
        tilt: gl.getUniformLocation(program, 'uTilt'),
        center: gl.getUniformLocation(program, 'uCenter'),
        shift: gl.getUniformLocation(program, 'uShift'),
        rotation: gl.getUniformLocation(program, 'uRotation')
      };
      gl.useProgram(program);
      gl.enable(gl.DEPTH_TEST);
      gl.depthFunc(gl.LEQUAL);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      const display = { width: 0, height: 0, scale: 0 };
      function resize() {
        const ratio = Math.min(window.devicePixelRatio || 1, 2);
        display.width = Math.max(1, Math.round(window.innerWidth * ratio));
        display.height = Math.max(1, Math.round(window.innerHeight * ratio));
        display.scale = Math.min(display.width * 0.82 / TITLE_WIDTH, display.height * 0.48 / TITLE_HEIGHT, 1000 * ratio / TITLE_WIDTH);
        canvas.width = display.width;
        canvas.height = display.height;
        gl.viewport(0, 0, display.width, display.height);
      }
      function render(progress, origin, pointer) {
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        gl.uniform2f(positions.viewport, display.width, display.height);
        gl.uniform1f(positions.pixelScale, display.scale);
        gl.uniform2f(positions.tilt, -0.2 + pointer[0] * 0.08, 0.11 + pointer[1] * 0.07);
        for (const mesh of meshes) {
          const shard = mesh.shard;
          const centerX = (shard.center[0] - 0.5) * TITLE_WIDTH;
          const centerY = (0.5 - shard.center[1]) * TITLE_HEIGHT;
          const dx = shard.center[0] - origin[0];
          const dy = origin[1] - shard.center[1];
          const distance = Math.hypot(dx, dy) + 0.08;
          const outward = progress * progress * (1.45 + shard.random[0] * 0.6);
          gl.uniform2f(positions.center, centerX, centerY);
          gl.uniform3f(positions.shift,
            dx / distance * outward + (shard.random[1] - 0.5) * progress * 0.28,
            dy / distance * outward + (shard.random[2] - 0.5) * progress * 0.24,
            progress * (0.18 + shard.random[3] * 1.2));
          gl.uniform3f(positions.rotation,
            progress * (shard.random[1] - 0.5) * 2.4,
            progress * (shard.random[2] - 0.5) * 2.4,
            progress * (shard.random[3] - 0.5) * 2.0);
          gl.bindBuffer(gl.ARRAY_BUFFER, mesh.buffer);
          gl.enableVertexAttribArray(positions.position);
          gl.enableVertexAttribArray(positions.uv);
          gl.enableVertexAttribArray(positions.shade);
          gl.vertexAttribPointer(positions.position, 3, gl.FLOAT, false, 24, 0);
          gl.vertexAttribPointer(positions.uv, 2, gl.FLOAT, false, 24, 12);
          gl.vertexAttribPointer(positions.shade, 1, gl.FLOAT, false, 24, 20);
          gl.drawArrays(gl.TRIANGLES, 0, mesh.count);
        }
      }
      resize();
      render(0, [0.5, 0.5], [0, 0]);
      return {
        resize,
        render,
        scale: () => display.scale / Math.min(window.devicePixelRatio || 1, 2),
        dispose() {
          buffers.forEach((buffer) => gl.deleteBuffer(buffer));
          gl.deleteTexture(texture);
          gl.deleteProgram(program);
          gl.getExtension('WEBGL_lose_context')?.loseContext();
        }
      };
    } catch (error) {
      buffers.forEach((buffer) => gl.deleteBuffer(buffer));
      if (texture) gl.deleteTexture(texture);
      if (program) gl.deleteProgram(program);
      gl.getExtension('WEBGL_lose_context')?.loseContext();
      throw error;
    }
  }

  function loadImage() {
    return new Promise((resolve, reject) => {
      const image = new Image();
      const timeout = setTimeout(() => reject(new Error('Title image timeout')), 14000);
      image.onload = async () => {
        try {
          if (image.decode) await image.decode();
          clearTimeout(timeout);
          resolve(image);
        } catch (error) {
          clearTimeout(timeout);
          reject(error);
        }
      };
      image.onerror = () => {
        clearTimeout(timeout);
        reject(new Error('Title image unavailable'));
      };
      image.src = IMAGE_URL;
    });
  }

  let active = false;
  async function openEntrance(replay = false) {
    if (active) return;
    active = true;
    if (replay) boot.show();
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let image;
    let sound = null;
    const audioReady = reduced ? Promise.resolve(null) : window.MAWARIMI_ENTRANCE_AUDIO.prepare();
    try {
      [image, sound] = await Promise.all([loadImage(), audioReady]);
    } catch (_) {
      audioReady.then(audio => audio?.dispose());
      boot.release();
      active = false;
      return;
    }
    // A loading skip, timeout, or navigation must never reopen the entrance later.
    if (!boot.pending) {
      sound?.dispose();
      active = false;
      return;
    }
    const dialog = document.createElement('dialog');
    dialog.id = 'entrance';
    dialog.setAttribute('aria-labelledby', 'entrance-heading');
    dialog.innerHTML = '<h2 id="entrance-heading" class="entrance-sr-only">廻リ視</h2><div class="entrance-stage"><canvas class="entrance-title" aria-hidden="true"></canvas><canvas class="entrance-glass" aria-hidden="true"></canvas><img src="assets/title.png" width="1210" height="531" alt="" aria-hidden="true"></div><div class="entrance-actions"><button type="button" class="entrance-enter"><span></span></button><button type="button" class="entrance-skip">スキップ</button></div>';
    const canvas = dialog.querySelector('canvas');
    const enter = dialog.querySelector('.entrance-enter');
    const skip = dialog.querySelector('.entrance-skip');
    enter.querySelector('span').textContent = window.matchMedia('(pointer: coarse)').matches ? 'タップ' : 'クリック';
    const glassCanvas = dialog.querySelector('.entrance-glass');
    let renderer = null;
    let glass = null;
    if (!reduced) {
      try {
        renderer = createRenderer(canvas, image);
      } catch (_) {
        dialog.classList.add('entrance-flat');
      }
    } else {
      dialog.classList.add('entrance-flat');
    }
    let raf = 0;
    let timer = 0;
    let started = false;
    let finished = false;
    let startTime = 0;
    let hiddenAt = 0;
    let origin = [0.5, 0.5];
    let pointer = [0, 0];
    function finish() {
      if (finished) return;
      finished = true;
      cancelAnimationFrame(raf);
      clearTimeout(timer);
      sound?.dispose();
      boot.release();
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('resize', resize);
      glass?.dispose();
      renderer?.dispose();
      if (dialog.open) dialog.close();
      dialog.remove();
      active = false;
      const target = document.getElementById('main');
      target?.focus({ preventScroll: true });
    }
    function frame(now) {
      if (finished || document.hidden) return;
      const progress = Math.min(1, (now - startTime) / DURATION);
      try {
        glass.render(progress);
      } catch (_) {
        finish();
        return;
      }
      if (progress < 1) raf = requestAnimationFrame(frame);
      else finish();
    }
    async function begin() {
      if (started || finished) return;
      started = true;
      // Resume in the click/keyboard gesture, before any asynchronous work.
      const unlocked = sound?.unlock().catch(() => { sound?.dispose(); sound = null; });
      if (renderer && !reduced) {
        try {
          const frozen = document.createElement('canvas');
          frozen.width = canvas.width;
          frozen.height = canvas.height;
          const context = frozen.getContext('2d');
          if (!context) throw new Error('Title capture unavailable');
          renderer.render(0, origin, pointer);
          context.fillStyle = '#000';
          context.fillRect(0, 0, frozen.width, frozen.height);
          context.drawImage(canvas, 0, 0);
          glass = window.MAWARIMI_GLASS.create(glassCanvas, frozen);
          glass.render(0);
          dialog.dataset.pieces = String(glass.pieceCount);
        } catch (_) {
          dialog.classList.add('entrance-flat');
        }
      }
      await unlocked;
      if (finished) return;
      dialog.classList.add('is-fracturing');
      if (reduced || !glass) {
        sound?.dispose();
        timer = setTimeout(finish, reduced ? 260 : 650);
      } else {
        startTime = performance.now();
        sound?.start();
        if (!document.hidden) raf = requestAnimationFrame(frame);
        else { hiddenAt = performance.now(); sound?.pause(); }
      }
    }
    function visibility() {
      if (!started || !glass || finished) return;
      if (document.hidden) {
        hiddenAt = performance.now();
        cancelAnimationFrame(raf);
        sound?.pause();
      } else {
        startTime += performance.now() - hiddenAt;
        sound?.resume();
        raf = requestAnimationFrame(frame);
      }
    }
    function resize() {
      if (!renderer || finished) return;
      try {
        renderer.resize();
        glass?.resize();
        if (!started) renderer.render(0, origin, pointer);
      } catch (_) {
        finish();
      }
    }
    dialog.addEventListener('pointermove', (event) => {
      if (started || !renderer || finished) return;
      pointer = [event.clientX / window.innerWidth * 2 - 1, event.clientY / window.innerHeight * 2 - 1];
      try {
        renderer.render(0, origin, pointer);
      } catch (_) {
        finish();
      }
    });
    canvas.addEventListener('webglcontextlost', (event) => {
      event.preventDefault();
      finish();
    });
    glassCanvas.addEventListener('webglcontextlost', (event) => {
      event.preventDefault();
      finish();
    });
    dialog.addEventListener('click', (event) => {
      if (!event.target.closest('.entrance-skip')) begin();
    });
    skip.addEventListener('click', (event) => { event.stopPropagation(); finish(); });
    dialog.addEventListener('cancel', (event) => {
      event.preventDefault();
      finish();
    });
    dialog.addEventListener('close', finish);
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('resize', resize);
    document.body.append(dialog);
    try {
      if (replay) window.scrollTo({ top: 0, behavior: 'instant' });
      dialog.showModal();
      boot.release();
      enter.focus();
    } catch (_) {
      finish();
    }
  }

  const replayButton = document.getElementById('replay-entrance');
  if (replayButton) {
    replayButton.hidden = false;
    replayButton.addEventListener('click', () => openEntrance(true));
  }
  if (boot.pending) openEntrance();
})();
