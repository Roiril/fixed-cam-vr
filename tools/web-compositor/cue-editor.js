// 廻リ視 web compositor — cue 編集器（マスク + 素材 + trim + フェード + 強度 + 💾保存）。
//   app.js buildColumn から抽出した独立モジュール。タイムラインのセグメントインスペクタが
//   埋め込んで「その場で新規 cue 作成 / 既存 cue 編集」に使う。deps 注入（floormap/timeline 同流儀）。
//
//   deps: {
//     getLiveImg(camId),   // プレビュー背景（生ライブ <img>）。無ければ null
//     getCamPost(camId),   // プレビューのグレーディング（cameras[i].post）。無ければ null
//     getCaptures(),       // captures/ 素材一覧 [{url,name,type}]
//     refreshCaptures(),   // 一覧再取得（Promise）
//     getAllCues(),        // 既存 cue 全件（id 衝突回避・新規番号採番）
//     saveCue(cue),        // cue を state.cues へ upsert（Promise<{ok}>）
//     onSaved(cue),        // 保存成功後に呼ぶ（タイムライン側で割当・再描画）
//   }
import { blendCfg, encPath, isVideoUrl, onCaptures } from './common.js';
import { createCompositeView } from './composite-view.js';
import { isIdentity, solveMatch, statsFromElement } from './color-match.js';

const MW = 640, MH = 360;

export function createCueEditor(deps) {
  const el = document.createElement('div');
  el.className = 'ce-wrap';
  el.innerHTML = `
    <div class="ce-head">
      <span class="ce-title">cue 編集</span>
      <input class="ce-name" type="text" placeholder="cue 名（任意）">
      <span class="spacer"></span>
      <button class="ce-close" title="閉じる">✕</button>
    </div>

    <div class="ce-body">
      <div class="ce-col ce-col-preview">
        <div class="sec-label">プレビュー（生映像 + 差し替え + カメラ画質）</div>
        <div class="view-wrap"><canvas class="ce-view" width="${MW}" height="${MH}"></canvas></div>
        <label class="ce-preview-toggle chk"><input class="ce-preview-on" type="checkbox" checked> 合成プレビュー ON</label>
      </div>

      <div class="ce-col ce-col-controls">
        <div class="sec-label">合成素材</div>
        <div class="row-btns">
          <select class="ce-src"></select>
          <button class="ce-src-refresh" title="一覧を更新">↻</button>
          <button class="ce-src-folder" title="素材フォルダ（captures/）を開く">📂</button>
        </div>
        <div class="row-btns ce-trim-row" style="display:none">
          <span class="sld">再生区間 <input class="ce-trim-start" type="number" min="0" step="0.1" value="0" title="開始秒">–<input class="ce-trim-end" type="number" min="0" step="0.1" value="0" title="終了秒（0=最後まで）">s</span>
        </div>
        <div class="row-btns">
          <span class="sld">フェード <input class="ce-fade" type="number" min="0" max="5" step="0.1" value="0.5" title="フェードイン/アウト秒">s</span>
          <span class="sld">強度 <input class="ce-strength" type="range" min="0" max="1" step="0.01" value="1" title="差し替え不透明度"><span class="ce-strength-v">1.00</span></span>
        </div>

        <div class="sec-label">マスク領域（白 = 差し替え / 未指定 = 全面差し替え）</div>
        <div class="view-wrap"><canvas class="ce-mask" width="${MW}" height="${MH}"></canvas></div>
        <div class="row-btns ce-mask-tools">
          <span class="seg-label">白の向き</span>
          <button data-edge="left">左</button>
          <button data-edge="right" class="on">右</button>
          <button data-edge="top">上</button>
          <button data-edge="bottom">下</button>
          <span class="sld"><input type="range" class="ce-mask-pos" min="0" max="100" value="50" title="白黒境界の位置"><span class="ce-mask-pos-v">50%</span></span>
          <button class="ce-mask-clear" title="全面差し替え（マスク無し）に戻す">全消去</button>
        </div>

        <div class="row-btns"><button class="ce-save accent">💾 cue を保存</button></div>
        <span class="ce-status ed-status"></span>
      </div>
    </div>`;

  const q = (s) => el.querySelector(s);

  // ---- 状態 ------------------------------------------------------------------
  let camId = null;
  let cueId = null;          // 編集中の cue id（null = 新規）
  let sourceUrl = '';
  let trimStart = 0, trimEnd = 0;
  let fadeSec = 0.5;
  let strength = 1;
  let srcMedia = null, srcReady = false;
  let maskEdge = 'right', maskCoverage = 50, maskEdited = false;
  let selMaskUrl = '';       // 既存 cue の maskUrl（未編集時の保持用）
  let previewOn = true;

  // ---- マスク canvas ---------------------------------------------------------
  const maskCanvas = q('.ce-mask');
  const mctx = maskCanvas.getContext('2d', { willReadFrequently: true });
  function drawMask() {
    const W = maskCanvas.width, H = maskCanvas.height;
    mctx.fillStyle = '#000'; mctx.fillRect(0, 0, W, H);
    const c = Math.max(0, Math.min(100, maskCoverage)) / 100;
    if (c <= 0) return;
    mctx.fillStyle = '#fff';
    if (maskEdge === 'left') mctx.fillRect(0, 0, W * c, H);
    else if (maskEdge === 'right') mctx.fillRect(W * (1 - c), 0, W * c, H);
    else if (maskEdge === 'top') mctx.fillRect(0, 0, W, H * c);
    else mctx.fillRect(0, H * (1 - c), W, H * c);
  }
  const maskIsEmpty = () => maskCoverage <= 0;
  drawMask();

  el.querySelectorAll('.ce-mask-tools [data-edge]').forEach((b) => b.addEventListener('click', () => {
    maskEdge = b.dataset.edge;
    el.querySelectorAll('.ce-mask-tools [data-edge]').forEach((x) => x.classList.toggle('on', x === b));
    maskEdited = true; drawMask();
  }));
  const maskPos = q('.ce-mask-pos'), maskPosV = q('.ce-mask-pos-v');
  maskPos.oninput = () => { maskCoverage = parseInt(maskPos.value, 10); maskPosV.textContent = maskPos.value + '%'; maskEdited = true; drawMask(); };
  q('.ce-mask-clear').onclick = () => { maskCoverage = 0; maskPos.value = 0; maskPosV.textContent = '0%'; maskEdited = true; drawMask(); };

  // ---- 素材ロード ------------------------------------------------------------
  const srcSelect = q('.ce-src');
  const trimRow = q('.ce-trim-row'), trimStartI = q('.ce-trim-start'), trimEndI = q('.ce-trim-end');
  function loadSource(url) {
    if (srcMedia && srcMedia.tagName === 'VIDEO') { try { srcMedia.pause(); srcMedia.src = ''; } catch { /* noop */ } }
    srcMedia = null; srcReady = false; sourceUrl = url || '';
    trimRow.style.display = 'none';
    if (!url) return;
    if (isVideoUrl(url)) {
      const v = document.createElement('video');
      v.muted = true; v.loop = false; v.playsInline = true; v.crossOrigin = 'anonymous'; v.src = url;
      v.addEventListener('loadedmetadata', () => {
        trimRow.style.display = '';
        if (!(trimEnd > 0)) { trimEnd = +(v.duration || 0).toFixed(1); trimEndI.value = trimEnd; }
      });
      v.addEventListener('canplay', () => { srcReady = true; if (previewOn) startVideo(); });
      // プレビューは trim 区間をループ（オーサリング中は繰り返し確認したい）
      v.addEventListener('timeupdate', () => {
        const tEnd = (trimEnd > 0) ? trimEnd : (v.duration || 0);
        if (tEnd && v.currentTime >= tEnd - 0.03) { try { v.currentTime = trimStart || 0; } catch { /* noop */ } }
      });
      srcMedia = v;
    } else {
      const im = new Image(); im.crossOrigin = 'anonymous';
      im.onload = () => { srcReady = true; }; im.src = url;
      srcMedia = im;
    }
  }
  function startVideo() {
    if (srcMedia && srcMedia.tagName === 'VIDEO') {
      try { srcMedia.currentTime = trimStart || 0; srcMedia.play().catch(() => {}); } catch { /* noop */ }
    }
  }
  srcSelect.onchange = () => { trimStart = 0; trimEnd = 0; trimStartI.value = 0; trimEndI.value = 0; loadSource(srcSelect.value); };
  q('.ce-src-refresh').onclick = () => deps.refreshCaptures && deps.refreshCaptures();
  // captures/ が更新されたら素材 select を張り直す（録画・キャプチャの反映）。
  const unsubCaptures = onCaptures(() => populateSources());
  q('.ce-src-folder').onclick = () => fetch('/open-dir?dir=captures').catch(() => {});
  trimStartI.onchange = () => { trimStart = Math.max(0, parseFloat(trimStartI.value) || 0); };
  trimEndI.onchange = () => { trimEnd = Math.max(0, parseFloat(trimEndI.value) || 0); };

  const fadeI = q('.ce-fade');
  fadeI.onchange = () => { fadeSec = Math.max(0, parseFloat(fadeI.value) || 0); };
  const strengthI = q('.ce-strength'), strengthV = q('.ce-strength-v');
  strengthI.oninput = () => { strength = parseFloat(strengthI.value); strengthV.textContent = strength.toFixed(2); };
  const previewChk = q('.ce-preview-on');
  previewChk.onchange = () => { previewOn = previewChk.checked; if (previewOn) startVideo(); };

  // ---- 素材 select の再構築（captures 更新時）---------------------------------
  function populateSources() {
    const items = (deps.getCaptures && deps.getCaptures()) || [];
    const cur = srcSelect.value;
    srcSelect.innerHTML = '<option value="">（素材を選ぶ）</option>';
    const mkOpt = (it) => {
      const o = document.createElement('option');
      o.value = it.url; o.textContent = `${it.type === 'video' ? '🎞' : '🖼'} ${it.name}`;
      return o;
    };
    // 実素材が先・動作確認用のダミー（testassets/）は別グループで末尾に。
    for (const it of items) if (it.kind !== 'test') srcSelect.appendChild(mkOpt(it));
    const test = items.filter((it) => it.kind === 'test');
    if (test.length) {
      const g = document.createElement('optgroup');
      g.label = '動作確認用（testassets）';
      for (const it of test) g.appendChild(mkOpt(it));
      srcSelect.appendChild(g);
    }
    srcSelect.value = cur;
  }

  const ed = (m, cls = '') => { const e = q('.ce-status'); e.textContent = m; e.className = 'ce-status ed-status ' + cls; };

  // ---- マスク PNG 書き出し（フェザー焼き込み）--------------------------------
  async function writeMaskFor(id) {
    if (maskIsEmpty()) return '';
    ed('マスク書き出し中…');
    let blob;
    if (blendCfg.feather > 0.001) {
      const tmp = document.createElement('canvas'); tmp.width = maskCanvas.width; tmp.height = maskCanvas.height;
      const tc = tmp.getContext('2d');
      tc.filter = `blur(${Math.max(1, Math.round(blendCfg.feather * 12))}px)`;
      tc.drawImage(maskCanvas, 0, 0);
      blob = await new Promise((r) => tmp.toBlob(r, 'image/png'));
    } else {
      blob = await new Promise((r) => maskCanvas.toBlob(r, 'image/png'));
    }
    const res = await (await fetch(`/masks?name=${encodeURIComponent(id)}`, { method: 'POST', body: blob })).json();
    if (!res.ok) throw new Error(res.error || 'マスク保存失敗');
    return res.url;
  }

  // ---- 色統計マッチングの焼き込み ------------------------------------------
  // 卓のプレビューは GPU の縮約で毎フレーム統計を取るが、実機は同じことをしない（Quest で重い、
  // かつ固定視点では事前に解ける）。ここで per-channel の gain/offset へ落として cue に持たせる。
  // 向きは「素材を実写へ寄せる」— 実機で動かせるのは素材の側だけだから。
  function bakeColorMatch() {
    if (!blendCfg.colorMatch || !(blendCfg.colorStrength > 0.0001)) {
      return { hasMatch: false, matchGain: [1, 1, 1], matchOffset: [0, 0, 0] };
    }
    const liveEl = (deps.getLiveImg && camId) ? deps.getLiveImg(camId) : null;
    const liveStats = statsFromElement(liveEl);
    const srcStats = statsFromElement(srcMedia);
    if (!liveStats || !srcStats || !liveStats.count || !srcStats.count) {
      // 実写か素材のどちらかが読めない（未接続・別オリジン）ときは黙って恒等にする。
      return { hasMatch: false, matchGain: [1, 1, 1], matchOffset: [0, 0, 0] };
    }
    const m = solveMatch(liveStats, srcStats, blendCfg.colorStrength);
    if (isIdentity(m)) return { hasMatch: false, matchGain: [1, 1, 1], matchOffset: [0, 0, 0] };
    return { hasMatch: true, matchGain: m.gain, matchOffset: m.offset };
  }

  // cue_<camId>_<n> の空き番号。
  function nextCueId() {
    const base = `cue_${camId}_`;
    const used = new Set(((deps.getAllCues && deps.getAllCues()) || []).map((c) => c.id));
    let n = 1; while (used.has(base + n)) n++;
    return base + n;
  }

  q('.ce-save').onclick = async () => {
    if (!camId) return ed('カメラ未設定', 'err');
    const url = sourceUrl || srcSelect.value || '';
    if (!url) return ed('合成素材を選んで', 'err');
    let id = cueId;
    const all = (deps.getAllCues && deps.getAllCues()) || [];
    const existing = id ? all.find((c) => c.id === id) : null;
    if (!id || !existing) id = nextCueId();
    try {
      let maskUrl;
      if (maskEdited) maskUrl = await writeMaskFor(id);
      else maskUrl = existing ? (existing.maskUrl || '') : '';
      const cue = {
        id, name: (q('.ce-name').value.trim()) || `カメラ ${camId} #${id.split('_').pop()}`, camera: camId,
        maskUrl: encPath(maskUrl), sourceUrl: encPath(url),
        strength, loop: existing?.loop ?? false,
        fadeIn: fadeSec, fadeOut: fadeSec,
        trimStart: trimStart || 0, trimEnd: trimEnd || 0,
      };
      // 色統計マッチングを 6 つの数へ落として焼く（実機はこれを掛けるだけ）。
      // プレビューの「境界ブレンド」の色統計トグルと同じ意思決定を使う。
      Object.assign(cue, bakeColorMatch());
      const r = await deps.saveCue(cue);
      if (!r || r.ok === false) throw new Error('state 保存失敗');
      cueId = id; selMaskUrl = cue.maskUrl; maskEdited = false;
      ed(`✓ 保存（${maskUrl ? 'マスク付き' : '全面差し替え'}）`, 'ok');
      deps.onSaved && deps.onSaved(cue);
    } catch (e) { ed('保存失敗: ' + e.message, 'err'); }
  };

  q('.ce-close').onclick = () => { el.dispatchEvent(new CustomEvent('ce-close')); };

  // ---- プレビュー（composite-view）-------------------------------------------
  const view = createCompositeView(q('.ce-view'), {
    sample: () => {
      const liveImg = (deps.getLiveImg && camId) ? deps.getLiveImg(camId) : null;
      const isVid = srcMedia && srcMedia.tagName === 'VIDEO';
      return {
        liveImg,
        overlayEl: srcMedia, overlayReady: srcReady,
        overlayW: isVid ? (srcMedia.videoWidth || 0) : (srcMedia ? srcMedia.naturalWidth : 0),
        overlayH: isVid ? (srcMedia.videoHeight || 0) : (srcMedia ? srcMedia.naturalHeight : 0),
        maskEl: maskIsEmpty() ? null : maskCanvas,
        overlayOn: previewOn && !!sourceUrl,
        loopPreview: true,             // trim 区間の巻き戻しはこちらで持つ（終端で畳ませない）
        fadeSec, strength,
        feather: blendCfg.feather,     // ライブハードマスク → 焼き込み結果に寄せてフェザー適用
        post: (deps.getCamPost && camId) ? deps.getCamPost(camId) : null,
        trimEnd: 0,                    // ループはこちらで管理（timeupdate）→ view には終端を渡さない
        onVideoEnd: null,
      };
    },
  });

  // ---- 外部 API --------------------------------------------------------------
  function open(newCamId, cue) {
    camId = newCamId;
    cueId = cue ? cue.id : null;
    q('.ce-title').textContent = cue ? `cue 編集: ${cue.id}` : `新規 cue（カメラ ${camId}）`;
    q('.ce-name').value = cue?.name || '';
    if (cue && cue.sourceUrl) { srcSelect.value = cue.sourceUrl; loadSource(cue.sourceUrl); }
    else { srcSelect.value = ''; loadSource(''); }
    trimStart = cue?.trimStart || 0; trimEnd = cue?.trimEnd || 0;
    trimStartI.value = trimStart; trimEndI.value = trimEnd;
    fadeSec = (cue && cue.fadeIn != null) ? cue.fadeIn : 0.5; fadeI.value = fadeSec;
    strength = (cue && cue.strength != null) ? cue.strength : 1; strengthI.value = strength; strengthV.textContent = strength.toFixed(2);
    selMaskUrl = cue?.maskUrl || '';
    maskCoverage = 0; maskPos.value = 0; maskPosV.textContent = '0%'; maskEdited = false;
    // 既存マスクがあれば、既定は「未編集＝既存を保持」。プレビューは全面差し替え相当で出るが
    // 保存時は maskEdited=false のため既存 maskUrl を維持する。
    drawMask();
    populateSources();
    ed(cue ? '' : '素材を選び 💾 保存', '');
  }

  return {
    el, open, populateSources,
    destroy() { unsubCaptures(); view.destroy(); },
    getCamId: () => camId,
  };
}
