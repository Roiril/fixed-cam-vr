// 🎯 カメラを**手で合わせる**面（旧・自動求解の較正パネルの置き換え）。
//
// 考え方: 位置合わせがあるのは**人形をうまく合成するため**だけ。だから機械に解かせず、
// 実映像の上に部屋のワイヤーと人形を重ねて、人が見ながらカメラを動かす。
//
// ⚠ 掴めるのは**カメラの外部 6 自由度だけ**。画角と歪みはレンズ単位で一度だけ決める
//   （理由は align-model.js の冒頭）。
// ⚠ 部屋（layout.room / layout.floor）は **4 台のカメラが共有する実測値**。
//   ここで直すのは「実測を直す」ことであって「このカメラに合わせる」ことではない。
//   1 台に合わせて頂点をずらすと他のカメラが黙って狂う。UI は常にそれを言う。
// ⚠ **合成の見た目の正は Unity**（Preview Show Composite）。卓の人形は幾何の目安で、
//   陰影・素材は描かない。ここで「馴染んでいる」を判定しない。
import {
  initialCalib, dragRotate, dragPan, dolly, setGroundPos, setField, setLens,
  toManualCalib, isManual, summaryLines, refDistance, hfovFromFocalPx,
  dragConstrained, AXES, projectPoint, unprojectToFloor,
} from './align-model.js';
import { wireSegments, calibMatchesSource, applyCalibToCameras } from './calib-session.js';
import { actorProxyGeometry, actorBodyGeometry, drawActorProxy, proxyIssueText } from './actor-proxy.js';
import { createDollView } from './doll-view.js';
import { defaultLight } from './room-model.js';

// Ichimatsu の実寸。Unity のプレハブの renderer bounds を実測した値
// （高さ 0.410 / 幅 0.321 は腕を広げた状態なので、腕を下ろした胴の幅として 0.18 を採る）。
// ⚠ `actor-proxy.js` の既定は**身長 1.6m の人間**用（肩幅 0.4m・足元半径 0.2m）。
//    そのまま 0.40m の人形に使うと、**足元の円が人形の背丈と同じ幅**になって絵が読めない
//    （実際そうなっていた）。人形の寸法は必ず渡すこと。
const DOLL = { h: 0.410, w: 0.18, d: 0.11, foot: 0.05 };
const DOLL_H_M = DOLL.h;

/** 人形の高さを変えたときに、幅・厚み・足元も一緒に縮める（比率は実寸から）。 */
function dollDims(heightM) {
  const s = (heightM > 0 ? heightM : DOLL.h) / DOLL.h;
  return { bodyW: DOLL.w * s, bodyD: DOLL.d * s, footRadiusM: DOLL.foot * s };
}
const WIRE_COLORS = { floor: '#ffdead', grid: 'rgba(255,222,173,.28)', wall: '#7ad6ff', box: '#7ad6ff' };

export function createAlignUi(container, deps) {
  const root = document.createElement('div');
  root.className = 'calib-ui align-ui';
  root.style.display = 'none';
  root.tabIndex = -1;                  // X / Y / Z キーを受けるのに要る
  root.innerHTML = `
    <div class="cu-backdrop"></div>
    <div class="cu-panel" role="dialog" aria-modal="true" aria-label="カメラを手で合わせる">
      <div class="cu-head">
        <b class="cu-title">🎯 カメラを合わせる</b>
        <span class="cu-frameinfo"></span>
        <span class="spacer"></span>
        <button class="au-refresh" title="いまのライブ映像をもう 1 枚取り込む">🔄 撮り直す</button>
        <button class="cu-close">✕ 閉じる</button>
      </div>
      <div class="cu-body">
        <div class="cu-viewcol">
          <div class="cu-canvas-wrap">
            <canvas class="cu-canvas au-canvas" width="640" height="480"></canvas>
            <div class="cu-noframe"></div>
          </div>
          <div class="au-modes">
            <button class="au-mode is-on" data-mode="rotate">🔄 回す</button>
            <button class="au-mode" data-mode="pan">✋ 平行に動かす</button>
            <button class="au-mode" data-mode="doll">🧍 人形を置く</button>
            <span class="au-axes">軸
              <button class="au-axis is-on" data-axis="">自由</button>
              <button class="au-axis" data-axis="x">X</button>
              <button class="au-axis" data-axis="y">Y</button>
              <button class="au-axis" data-axis="z">Z</button>
            </span>
            <span class="au-modehint"></span>
          </div>
          <div class="cu-hint">静止フレームです（合わせている間に絵が動かないよう固定しています）。
            <b>ドラッグ</b>で操作・<b>ホイール</b>で前後・<b>Shift+ドラッグ</b>で一時的に平行移動。
            <b>X / Y / Z キー</b>でその軸だけに拘束（もう一度押すか Esc で解除）。
            床の線と壁の縦線が実物に重なれば合っています。</div>
        </div>
        <div class="cu-side">
          <div class="au-readout"></div>
          <div class="au-grid">
            <label>X<input class="au-f" data-k="x" type="number" step="0.05"></label>
            <label>Z<input class="au-f" data-k="z" type="number" step="0.05"></label>
            <label>高さ<input class="au-f" data-k="y" type="number" step="0.05"></label>
            <label>左右<input class="au-f" data-k="yawDeg" type="number" step="1"></label>
            <label>上下<input class="au-f" data-k="pitchDeg" type="number" step="1"></label>
            <label>傾き<input class="au-f" data-k="rollDeg" type="number" step="0.5"></label>
          </div>

          <div class="au-block">
            <div class="au-blockhead">🧍 人形</div>
            <label class="chk"><input class="au-dollon" type="checkbox" checked> 合成して見る</label>
            <div class="au-dollrow">
              <label>高さ<input class="au-dollh" type="number" step="0.02" min="0.1" max="2">m</label>
              <label>向き<input class="au-dollyaw" type="number" step="5">°</label>
            </div>
            <div class="au-blockhint">「🧍 人形を置く」にして<b>映像の床をクリック</b>すると、そこに立ちます。
              <b>陰影は「上から光が当たっている」が読める最小限</b>です（法線・鏡面は描いていません）。
              見た目の良し悪しは Unity の <code>Preview Show Composite</code> で見てください。</div>
            <div class="au-dollstatus"></div>
          </div>

          <div class="au-block">
            <div class="au-blockhead">💡 光（部屋で 1 つ・全カメラ共有）</div>
            <div class="au-dollrow">
              <label>向き<input class="au-l" data-k="yawDeg" type="number" step="5">°</label>
              <label>高さ<input class="au-l" data-k="pitchDeg" type="number" step="5" min="-90" max="90">°</label>
            </div>
            <div class="au-dollrow">
              <label>色温度<input class="au-l" data-k="tempK" type="number" step="100" min="1000" max="12000">K</label>
              <label>強さ<input class="au-l" data-k="intensity" type="number" step="0.05" min="0" max="3"></label>
            </div>
            <div class="au-dollrow">
              <label>環境光<input class="au-l" data-k="ambient" type="number" step="0.05" min="0" max="1"></label>
              <label>影の濃さ<input class="au-l" data-k="shadowDensity" type="number" step="0.05" min="0" max="1"></label>
            </div>
            <div class="au-dollrow"><button class="au-lightsave">💡 この光を保存</button></div>
          </div>

          <div class="au-block">
            <div class="au-blockhead">📐 部屋（<b>全カメラ共有</b>）</div>
            <div class="au-dollrow">
              <label>床 幅<input class="au-floorw" type="number" step="0.05" min="0.3" max="12">m</label>
              <label>× 奥行<input class="au-floord" type="number" step="0.05" min="0.3" max="12">m</label>
              <button class="au-floorsave">実測値にする</button>
            </div>
            <div class="au-blockhint">⚠ ここは <b>4 台のカメラが共有する実測値</b>です。
              このカメラに合わせるために動かすと<b>他のカメラの合成が黙って狂います</b>。
              巻尺で測った値だけを入れてください。壁は フロアマップの 🧱 部屋 で引きます。</div>
          </div>

          <details class="au-block">
            <summary class="au-blockhead">🔍 レンズ（画角・歪み）</summary>
            <div class="au-dollrow">
              <label>画角<input class="au-hfov" type="number" step="0.5" min="10" max="170">°</label>
              <label>歪み<input class="au-k1" type="number" step="0.01" min="-0.6" max="0.6"></label>
            </div>
            <div class="au-blockhint">⚠ <b>現場では触らないでください。</b>「画角を広げて近づける」と
              「狭めて遠ざける」は画の上でほぼ同じで、ここを合わせ込むと位置が実測で 27cm ずれます
              （固定すれば 2cm）。レンズ 1 本につき一度だけ、直線が直線に見えるように決めます。</div>
          </details>

          <div class="au-msg"></div>
          <div class="au-actions">
            <button class="au-save">💾 このカメラに保存</button>
            <button class="au-revert">↩ 開いた時に戻す</button>
          </div>
          <div class="au-authority">合成の見た目の正は <b>Unity</b> です。ここで判定しないでください。</div>
        </div>
      </div>
    </div>`;
  container.appendChild(root);

  const $ = (s) => root.querySelector(s);
  const canvas = $('.au-canvas');
  const ctx = canvas.getContext('2d');
  const noFrame = $('.cu-noframe');
  const msgEl = $('.au-msg');

  let camId = null, calib = null, opening = null, frame = null, liveSize = null;
  let mode = 'rotate', axis = '';
  let doll = { on: true, x: 0, z: 0, yawDeg: 0, heightM: DOLL_H_M };
  let light = defaultLight();
  let drag = null, msgTimer = 0;
  const dollView = createDollView();

  const cams = () => deps.getCameras() || [];
  const cam = () => cams().find((c) => c && c.id === camId) || null;
  const layout = () => deps.getLayout() || null;

  function note(m, cls = '') {
    msgEl.textContent = m;
    msgEl.className = 'au-msg ' + cls;
    clearTimeout(msgTimer);
    if (m) msgTimer = setTimeout(() => { msgEl.textContent = ''; msgEl.className = 'au-msg'; }, 7000);
  }

  // ---- 静止フレーム --------------------------------------------------------
  // ⚠ ライブを直接使わない。動いている絵の上で合わせると「合った気」になる。
  function grabFrame() {
    const img = deps.getLiveImg(camId);
    if (!img || !img.naturalWidth || !img.naturalHeight) return false;
    const cv = document.createElement('canvas');
    cv.width = img.naturalWidth; cv.height = img.naturalHeight;
    cv.getContext('2d').drawImage(img, 0, 0);
    frame = { canvas: cv, w: cv.width, h: cv.height };
    liveSize = { w: cv.width, h: cv.height };
    canvas.width = cv.width; canvas.height = cv.height;
    canvas.style.aspectRatio = `${cv.width} / ${cv.height}`;
    return true;
  }

  // ---- 描画 ----------------------------------------------------------------
  function draw() {
    if (!calib) return;
    const w = canvas.width, h = canvas.height;
    ctx.clearRect(0, 0, w, h);
    if (frame) ctx.drawImage(frame.canvas, 0, 0, w, h);
    else { ctx.fillStyle = '#000'; ctx.fillRect(0, 0, w, h); }

    // 部屋のワイヤー。実測していない部分は点線で描く（見当は要るが、合わせる基準にはできない）
    const segs = wireSegments(layout(), { wallH: wallHeight() });
    ctx.lineWidth = Math.max(1, w / 640);
    for (const seg of segs) {
      const a = projectPoint(calib, seg.a[0], seg.a[1], seg.a[2]);
      const b = projectPoint(calib, seg.b[0], seg.b[1], seg.b[2]);
      if (!a || !b) continue;
      ctx.strokeStyle = seg.color || WIRE_COLORS[seg.kind] || '#ffdead';
      ctx.setLineDash(seg.source === 'assumed' ? [4, 4] : []);
      ctx.globalAlpha = seg.source === 'assumed' ? 0.45 : 0.9;
      ctx.beginPath(); ctx.moveTo(a.u, a.v); ctx.lineTo(b.u, b.v); ctx.stroke();
    }
    ctx.setLineDash([]); ctx.globalAlpha = 1;

    if (doll.on) drawDoll(w);
    drawHorizon(w, h);
  }

  function drawDoll(w) {
    const placement = { x: doll.x, z: doll.z, yawDeg: doll.yawDeg };

    // 実メッシュ + 実テクスチャ + 影（WebGL）。読めていればこちらを出す。
    if (dollView.ready) {
      const layer = dollView.render(calib, placement, doll.heightM, light, canvas.width, canvas.height);
      if (layer) { ctx.drawImage(layer, 0, 0); return; }
    }

    // ⚠ 読めないときだけ輪郭へ落とす（黙って何も出さない、はしない）。
    const opts = { srcW: canvas.width, srcH: canvas.height, ...dollDims(doll.heightM) };
    const geom = actorProxyGeometry(calib, placement, doll.heightM, opts);
    const body = actorBodyGeometry(calib, placement, doll.heightM, opts);
    drawActorProxy(ctx, geom, { body, scale: Math.max(1, w / 640) });
    // ⚠ 文言は `proxyIssueText` に任せる（clipped は「カメラの後ろ」ではなく
    //    「画角からはみ出す」。自分で書くと意味がずれる — 実際ずれていた）。
    const issue = proxyIssueText(geom, 'calib');
    if (issue) {
      ctx.fillStyle = 'rgba(255,120,120,.9)';
      ctx.font = `${Math.round(w / 46)}px sans-serif`;
      ctx.fillText(issue, 8, 20);
    }
  }

  /** 地平線。**上下の向きが合っているか**は床の線だけでは読みにくいので、基準線を 1 本置く。 */
  function drawHorizon(w, h) {
    const l = projectPoint(calib, calib.x - Math.sin((calib.yawDeg + 90) * Math.PI / 180) * 500,
      calib.y, calib.z - Math.cos((calib.yawDeg + 90) * Math.PI / 180) * 500);
    const r = projectPoint(calib, calib.x + Math.sin((calib.yawDeg + 90) * Math.PI / 180) * 500,
      calib.y, calib.z + Math.cos((calib.yawDeg + 90) * Math.PI / 180) * 500);
    if (!l || !r) return;
    ctx.strokeStyle = 'rgba(122,214,255,.35)';
    ctx.setLineDash([2, 6]); ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(l.u, l.v); ctx.lineTo(r.u, r.v); ctx.stroke();
    ctx.setLineDash([]);
  }

  const wallHeight = () => {
    const r = layout()?.room;
    const wl = r && Array.isArray(r.walls) && r.walls[0];
    return wl && wl.h > 0 ? wl.h : 1.0;
  };

  // ---- 入力 ----------------------------------------------------------------
  const toCanvas = (ev) => {
    const r = canvas.getBoundingClientRect();
    return {
      x: (ev.clientX - r.left) * (canvas.width / r.width),
      y: (ev.clientY - r.top) * (canvas.height / r.height),
    };
  };

  canvas.addEventListener('pointerdown', (ev) => {
    if (!calib) return;
    const p = toCanvas(ev);
    if (mode === 'doll') { placeDoll(p); return; }
    canvas.setPointerCapture(ev.pointerId);
    drag = { last: p, pan: ev.shiftKey || mode === 'pan', dist: refDistance(calib) };
  });

  canvas.addEventListener('pointermove', (ev) => {
    if (!drag || !calib) return;
    const p = toCanvas(ev);
    const dx = p.x - drag.last.x, dy = p.y - drag.last.y;
    drag.last = p;
    if (axis) {
      // 軸拘束。効き目は「いま合わせている物」の位置で決める（人形の足元を基準にする）
      calib = dragConstrained(calib, axis, drag.pan ? 'move' : 'rotate', dx, dy,
        { at: [doll.x, 0, doll.z] });
    } else {
      calib = drag.pan ? dragPan(calib, dx, dy, drag.dist) : dragRotate(calib, dx, dy);
    }
    syncFields(); draw();
  });

  const endDrag = () => { drag = null; };
  canvas.addEventListener('pointerup', endDrag);
  canvas.addEventListener('pointercancel', endDrag);

  canvas.addEventListener('wheel', (ev) => {
    if (!calib) return;
    ev.preventDefault();
    calib = dolly(calib, ev.deltaY < 0 ? 0.05 : -0.05);
    syncFields(); draw();
  }, { passive: false });

  function placeDoll(p) {
    const hit = unprojectToFloor(calib, p.x, p.y, 0);
    if (!hit) { note('そこは床と交わりません（地平線より上をクリックしています）', 'warn'); return; }
    doll.x = hit.x; doll.z = hit.z;
    note(`人形を X ${hit.x.toFixed(2)} / Z ${hit.z.toFixed(2)} に置きました`);
    draw();
  }

  root.querySelectorAll('.au-mode').forEach((b) => b.addEventListener('click', () => {
    mode = b.dataset.mode;
    root.querySelectorAll('.au-mode').forEach((o) => o.classList.toggle('is-on', o === b));
    refreshModeHint();
    canvas.style.cursor = mode === 'doll' ? 'copy' : 'grab';
  }));

  root.querySelectorAll('.au-axis').forEach((b) => b.addEventListener('click', () => setAxis(b.dataset.axis)));

  function setAxis(a) {
    axis = AXES.includes(a) ? a : '';
    root.querySelectorAll('.au-axis').forEach((o) => o.classList.toggle('is-on', o.dataset.axis === axis));
    refreshModeHint();
  }

  function refreshModeHint() {
    const what = mode === 'rotate' ? 'カメラを回す' : mode === 'pan' ? 'カメラを平行に動かす' : '';
    $('.au-modehint').textContent = mode === 'doll'
      ? '映像の床をクリックして人形を置く'
      : axis ? `ドラッグ＝${axis.toUpperCase()} 軸だけで ${what}` : `ドラッグ＝${what}（自由）`;
  }

  // Blender / CAD 流のキー。X / Y / Z で軸を切り替え、もう一度押すか Esc で解除。
  root.addEventListener('keydown', (ev) => {
    if (root.style.display === 'none') return;
    const t = ev.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) return;
    const k = ev.key.toLowerCase();
    if (AXES.includes(k)) { setAxis(axis === k ? '' : k); ev.preventDefault(); }
    else if (ev.key === 'Escape') { if (axis) { setAxis(''); ev.preventDefault(); } else close(); }
    else if (k === 'g') { root.querySelector('.au-mode[data-mode="pan"]').click(); }
    else if (k === 'r') { root.querySelector('.au-mode[data-mode="rotate"]').click(); }
  });

  root.querySelectorAll('.au-f').forEach((inp) => inp.addEventListener('change', () => {
    if (!calib) return;
    calib = setField(calib, inp.dataset.k, inp.value);
    syncFields(); draw();
  }));

  $('.au-dollon').addEventListener('change', (e) => { doll.on = e.target.checked; draw(); });
  $('.au-dollh').addEventListener('change', (e) => {
    doll.heightM = Math.max(0.1, Math.min(2, Number(e.target.value) || DOLL_H_M)); draw();
  });
  $('.au-dollyaw').addEventListener('change', (e) => { doll.yawDeg = Number(e.target.value) || 0; draw(); });

  root.querySelectorAll('.au-l').forEach((inp) => inp.addEventListener('input', () => {
    const v = Number(inp.value);
    if (Number.isFinite(v)) { light = { ...light, [inp.dataset.k]: v }; draw(); }
  }));

  $('.au-lightsave').addEventListener('click', async () => {
    if (!deps.saveLayout) { note('この面からは光を保存できません', 'warn'); return; }
    const lay = { ...(layout() || {}) };
    lay.room = { ...(lay.room || {}), light: { ...light } };
    await deps.saveLayout(lay);
    note('光を保存しました（部屋で 1 つ・全カメラ共有）');
  });

  $('.au-hfov').addEventListener('change', (e) => {
    calib = setLens(calib, { hfovDeg: Number(e.target.value) });
    syncFields(); draw();
  });
  $('.au-k1').addEventListener('change', (e) => {
    calib = setLens(calib, { k1: Number(e.target.value) });
    syncFields(); draw();
  });

  $('.au-floorsave').addEventListener('click', async () => {
    const w = Number($('.au-floorw').value), d = Number($('.au-floord').value);
    if (!(w > 0.3) || !(d > 0.3)) { note('床の寸法が読めません', 'warn'); return; }
    const lay = { ...(layout() || {}) };
    lay.floor = { w, d };
    if (lay.room) lay.room = { ...lay.room, floorW: w, floorD: d };
    if (!deps.saveLayout) { note('この面からは部屋を保存できません', 'warn'); return; }
    await deps.saveLayout(lay);
    note('床の実測値を保存しました（全カメラ共有）');
    draw();
  });

  $('.au-refresh').addEventListener('click', () => {
    if (grabFrame()) { note('いまのライブ映像を取り込みました'); draw(); }
    else note('ライブ映像がまだ来ていません', 'warn');
    updateFrameInfo();
  });

  $('.au-revert').addEventListener('click', () => {
    if (!opening) return;
    calib = { ...opening };
    syncFields(); draw();
    note('開いた時の姿勢に戻しました');
  });

  $('.au-save').addEventListener('click', async () => {
    if (!calib) return;
    const out = toManualCalib(calib, { lensId: cam()?.calib?.lensId || '', nowIso: localIso() });
    await deps.saveCameras(applyCalibToCameras(cams(), camId, out));
    opening = { ...calib };
    note('保存しました。見た目は Unity の Preview Show Composite で確かめてください');
  });

  $('.cu-close').addEventListener('click', close);
  $('.cu-backdrop').addEventListener('click', close);

  // ---- 同期 ----------------------------------------------------------------
  function syncFields() {
    if (!calib) return;
    for (const inp of root.querySelectorAll('.au-f')) {
      const v = calib[inp.dataset.k];
      if (document.activeElement !== inp) inp.value = Number(v).toFixed(inp.dataset.k.endsWith('Deg') ? 1 : 2);
    }
    $('.au-hfov').value = hfovFromFocalPx(calib.fxPx, calib.srcW).toFixed(1);
    $('.au-k1').value = (calib.k1 || 0).toFixed(2);
    $('.au-readout').innerHTML = summaryLines(calib).map((l) => `<div>${l}</div>`).join('');
    for (const inp of root.querySelectorAll('.au-l')) {
      if (document.activeElement !== inp) inp.value = light[inp.dataset.k];
    }
  }

  function syncDollStatus() {
    const el = $('.au-dollstatus');
    if (dollView.ready) { el.textContent = ''; el.className = 'au-dollstatus'; return; }
    // ⚠ 実メッシュが出せないことを黙らない（輪郭だけ出て「これが人形」と誤解される）
    el.textContent = dollView.error
      ? `⚠ 実メッシュを描けないので輪郭で代用しています（${dollView.error}）。`
      : '人形を読み込み中…';
    el.className = 'au-dollstatus' + (dollView.error ? ' warn' : '');
  }

  function updateFrameInfo() {
    const c = cam();
    const src = liveSize ? `${liveSize.w}×${liveSize.h}` : '映像なし';
    const saved = c && c.calib && c.calib.fxPx > 1;
    const mark = saved ? (isManual(c.calib) ? '（手で合わせた値あり）' : '（解いた値あり）') : '（未設定）';
    const mism = saved && liveSize && !calibMatchesSource(c.calib, liveSize.w, liveSize.h)
      ? ' ⚠ 保存済みの解像度と違います' : '';
    $('.cu-frameinfo').textContent = `${camId} / ${src} ${mark}${mism}`;
  }

  const localIso = () => {
    const d = new Date(Date.now() - new Date().getTimezoneOffset() * 60000);
    return d.toISOString().slice(0, 19);
  };

  // ---- 開閉 ----------------------------------------------------------------
  function open(id) {
    camId = id;
    frame = null; liveSize = null; drag = null;
    const ok = grabFrame();
    noFrame.style.display = ok ? 'none' : 'flex';
    noFrame.textContent = ok ? '' : 'このカメラのライブ映像がまだ来ていません。映像が出てから ［🔄 撮り直す］ を押してください。';
    const sz = liveSize || { w: 640, h: 480 };
    calib = initialCalib(cam(), sz.w, sz.h);
    opening = { ...calib };
    const lay = layout();
    doll = { on: true, x: 0, z: 0, yawDeg: 0, heightM: DOLL_H_M };
    light = { ...defaultLight(), ...(lay?.room?.light || {}) };
    $('.au-dollh').value = DOLL_H_M;
    $('.au-dollyaw').value = 0;
    $('.au-floorw').value = lay?.floor?.w ?? 1.8;
    $('.au-floord').value = lay?.floor?.d ?? 1.8;
    setAxis('');
    canvas.style.cursor = 'grab';
    syncFields(); updateFrameInfo(); syncDollStatus(); draw();
    root.style.display = 'flex';
    root.focus();
    // 人形は重い（実メッシュ 1.9MB + アルベド）。読めた時点で描き直す。
    dollView.load().then(() => { syncDollStatus(); draw(); });
  }

  function close() { root.style.display = 'none'; camId = null; drag = null; }

  /**
   * show.json が更新されたときの呼び口（long-poll 由来）。
   * ⚠ **合わせている最中の calib は上書きしない。** 別の面の保存で自分の作業が消えると、
   *   何が起きたのか作業者に分からない。反映するのは部屋（共有の実測値）だけ。
   */
  function onState() {
    if (root.style.display === 'none') return;
    const lay = layout();
    if (document.activeElement !== $('.au-floorw')) $('.au-floorw').value = lay?.floor?.w ?? 1.8;
    if (document.activeElement !== $('.au-floord')) $('.au-floord').value = lay?.floor?.d ?? 1.8;
    draw();
  }

  return { open, close, onState, el: root };
}
