// 廻リ視 タイムライン第一級オーサリング（schedule.js 後継）。
//   区間 = 「周回 L にゾーン（カメラ）C へ滞在する区間」。lap 行 × course.order 順の区間グリッド。
//   区間クリック → セグメントインスペクタで cue 割当 / インライン cue 編集 / post 上書き / insert を編集。
//   ▶ 検証（矢印キー）で Unity セマンティクス（LapCounter / CueScheduler / insert / post 3 段）を
//   JS ミラーし、体験者に見える画を WebGL プレビューで再現する（ローカルのみ・show.json は書かない）。
//
//   契約（.claude/plans/2026-07-19_webui-timeline-authoring.md が正）:
//   timeline = { rev, segments:[{ lap, camera, cues:[{cueId,delaySec,once,override,hasOverride}],
//                                 post, hasPost, insert:{anchor,camera,delaySec,durationSec,cueId,once,post,hasPost}, hasInsert }] }
//   - セグメントキー = (lap, camera)。同一キーは 1 個（この UI が保証。Unity は先勝ち）。
//   - lap は 1 始まり、camera は「カメラ index」。cueId は cues[].id（cue.camera は id 文字列）。
//   - hasPost/hasInsert/hasOverride の present-flag を必ず書く（JsonUtility の null 入れ子対策）。

import { FX, FX_DEFAULT, camColor, escapeHtml, createMediaCache } from './common.js';
import { createCompositeView } from './composite-view.js';
import { createCueEditor } from './cue-editor.js';

function defaultOverride() { return { strength: 1, fadeIn: 0.5, fadeOut: 0.5, trimStart: 0, trimEnd: 0 }; }
function newAssign(cueId) { return { cueId, delaySec: 0, once: true, override: defaultOverride(), hasOverride: false }; }
function defaultInsert() {
  return { anchor: 'exit', camera: 0, delaySec: 0, durationSec: 4, cueId: '', once: true, post: { ...FX_DEFAULT }, hasPost: false };
}
function newSeg(lap, camera) {
  return { lap, camera, cues: [], post: { ...FX_DEFAULT }, hasPost: false, insert: defaultInsert(), hasInsert: false };
}

export function createTimeline(container, deps) {
  // deps: { getCameras, getCues, getCourseOrder, getGlobalPost, getLiveImg,
  //         saveTimeline, saveCue, getCaptures, refreshCaptures }
  container.innerHTML = `
    <div class="tl-wrap">
      <div class="tl-row">
        <button class="tl-save accent">💾 保存</button>
        <span class="tl-dirty"></span>
        <span class="spacer"></span>
        <button class="tl-validate" title="矢印キーで周回×ゾーン進行をシミュレートし、発火順と見える画を確認（ローカルのみ）">▶ 検証</button>
        <button class="tl-lap-add" title="周回を 1 つ増やす">＋ 周回</button>
        <button class="tl-lap-del" title="最後の周回を削除">－ 周回</button>
      </div>
      <div class="tl-hint">区間をクリックして、その周・そのカメラの cue 割当・画像加工（post）上書き・インサートショットを編集する。空 = そのゾーンで発火なし。ライブ手動操作（演出 ON）が優先で、その間は抑止される。</div>
      <div class="tl-note"></div>
      <div class="tl-track-wrap"><div class="tl-track"></div></div>
      <div class="tl-validate-panel" style="display:none"></div>
      <div class="tl-inspector"></div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const track = q('.tl-track');
  const inspectorEl = q('.tl-inspector');
  const dirtyEl = q('.tl-dirty');
  const noteEl = q('.tl-note');
  const validateBtn = q('.tl-validate');
  const validatePanel = q('.tl-validate-panel');

  // ---- 状態 ------------------------------------------------------------------
  let timeline = { rev: 1, segments: [] };
  let dirty = false;
  let cameras = [];
  let cues = [];
  let order = null;
  let lapCount = 1;
  let sel = null;            // 選択中区間 { lap, camera }
  let orderIsExplicit = false;
  let cueEditor = null;      // 遅延生成（WebGL context 節約）
  let editorOpen = false;    // cue エディタ展開中（onState でインスペクタを潰さないためのガード）

  function markDirty() { dirty = true; renderDirty(); }
  function renderDirty() { dirtyEl.textContent = dirty ? '● 未保存' : ''; dirtyEl.className = 'tl-dirty' + (dirty ? ' on' : ''); }

  // ---- アクセサ ---------------------------------------------------------------
  function rows() {
    const n = cameras.length;
    orderIsExplicit = false;
    if (Array.isArray(order) && order.length) {
      const valid = order.filter((i) => Number.isInteger(i) && i >= 0 && i < n);
      if (valid.length) { orderIsExplicit = true; return valid; }
    }
    return cameras.map((_, i) => i);
  }
  function segAt(lap, camera) { return timeline.segments.find((s) => s.lap === lap && s.camera === camera) || null; }
  function ensureSeg(lap, camera) {
    let s = segAt(lap, camera);
    if (!s) { s = newSeg(lap, camera); timeline.segments.push(s); }
    return s;
  }
  function pruneSeg(s) {
    if (!s) return;
    if ((!s.cues || !s.cues.length) && !s.hasPost && !s.hasInsert) {
      timeline.segments = timeline.segments.filter((x) => x !== s);
    }
  }
  function cuesForCam(idx) {
    const id = cameras[idx] ? cameras[idx].id : null;
    return id == null ? [] : cues.filter((c) => (c.camera || '') === id);
  }
  function cueById(id) { return cues.find((c) => c.id === id) || null; }
  function cueName(id) { const c = cueById(id); return c ? (c.name || c.id) : id; }
  function camLabel(idx) { return cameras[idx] ? `カメラ ${cameras[idx].id}` : `#${idx}`; }
  function globalPost() { return (deps.getGlobalPost && deps.getGlobalPost()) || FX_DEFAULT; }

  // ---- グリッド描画 ----------------------------------------------------------
  function render() {
    const rs = rows();
    const needNote = !orderIsExplicit && cameras.length > 0;
    noteEl.textContent = needNote
      ? 'コース順が未設定です。フロアマップで周回コースを設定してください（今は 0..N-1 の順で表示中）。' : '';
    noteEl.className = 'tl-note' + (needNote ? ' on' : '');

    const v = validation.active ? validation.current() : null;

    track.innerHTML = '';
    for (let lap = 1; lap <= lapCount; lap++) {
      if (lap > 1) {
        const arrow = document.createElement('div');
        arrow.className = 'tl-arrow'; arrow.textContent = '→';
        track.appendChild(arrow);
      }
      const lapEl = document.createElement('div');
      lapEl.className = 'tl-lap';
      const head = document.createElement('div');
      head.className = 'tl-lap-head'; head.textContent = `${lap}周目`;
      lapEl.appendChild(head);
      const segs = document.createElement('div');
      segs.className = 'tl-segs';
      rs.forEach((ci) => {
        const seg = segAt(lap, ci);
        const on = sel && sel.camera === ci && sel.lap === lap;
        const isCur = v && v.lap === lap && v.activeCam === ci && !v.transient;
        const camId = cameras[ci] ? cameras[ci].id : `#${ci}`;
        const btn = document.createElement('button');
        btn.className = 'tl-seg' + (seg && seg.cues && seg.cues.length ? ' filled' : '')
          + (on ? ' sel' : '') + (isCur ? ' cur' : '');
        btn.style.setProperty('--cam', camColor(ci));
        // cue チップ
        let clipHtml;
        if (seg && seg.cues && seg.cues.length) {
          clipHtml = seg.cues.map((a) => {
            const label = cueName(a.cueId) + (a.delaySec ? ` +${a.delaySec}s` : '')
              + (a.once === false ? ' ⟳' : '') + (a.hasOverride ? ' ✎' : '');
            return `<span class="tl-cue-chip">${escapeHtml(label)}</span>`;
          }).join('');
        } else clipHtml = '<span class="tl-seg-empty">—</span>';
        // バッジ（post / enter insert）
        let badges = '';
        if (seg && seg.hasPost) badges += '<span class="tl-badge tl-badge-post" title="画像加工の上書きあり">🎨</span>';
        if (seg && seg.hasInsert && seg.insert && seg.insert.anchor === 'enter') {
          badges += `<span class="tl-badge tl-badge-insert" title="進入インサート → ${escapeHtml(camLabel(seg.insert.camera))}">⏢${escapeHtml(camLabel(seg.insert.camera))}</span>`;
        }
        btn.innerHTML = `<span class="tl-seg-cam"><i></i>${escapeHtml(camId)}${badges}</span>`
          + `<span class="tl-seg-clip">${clipHtml}</span>`;
        btn.onclick = () => { sel = { lap, camera: ci }; render(); renderInspector(); };
        segs.appendChild(btn);

        // exit insert は区間境界チップとして次の区間との間に可視化
        if (seg && seg.hasInsert && seg.insert && seg.insert.anchor === 'exit') {
          const chip = document.createElement('div');
          chip.className = 'tl-exit-insert';
          chip.title = `退出インサート → ${camLabel(seg.insert.camera)}（${seg.insert.durationSec}s）`;
          chip.textContent = `⏢ ${camLabel(seg.insert.camera)}`;
          segs.appendChild(chip);
        }
      });
      lapEl.appendChild(segs);
      track.appendChild(lapEl);
    }
  }

  // ---- セグメントインスペクタ ------------------------------------------------
  function renderInspector() {
    editorOpen = false; // インスペクタ再構築で cue エディタホストは作り直される
    if (!sel) { inspectorEl.innerHTML = ''; return; }
    const seg = segAt(sel.lap, sel.camera); // 無ければ null（編集で ensureSeg）
    inspectorEl.innerHTML = `
      <div class="tl-insp-head">${escapeHtml(camLabel(sel.camera))} / ${sel.lap}周目
        <span class="spacer"></span>
        <button class="tl-insp-close">閉じる</button>
      </div>
      <div class="tl-insp-sec">
        <div class="tl-insp-label">cue（進入 + 遅延で発火・複数可）</div>
        <div class="tl-insp-cues"></div>
        <div class="row-btns">
          <button class="tl-cue-assign" title="このカメラの既存 cue を割り当てる">＋ 既存 cue を割当</button>
          <button class="tl-cue-new accent" title="このカメラ向けの cue をその場で作成">＋ 新規 cue 作成</button>
        </div>
        <div class="tl-cue-none"></div>
      </div>
      <div class="tl-insp-sec">
        <label class="tl-insp-toggle chk"><input class="tl-post-on" type="checkbox"> 🎨 このゾーンで画像加工を上書き（segment post &gt; camera post &gt; global）</label>
        <div class="tl-post-fx fx-rows" style="display:none"></div>
      </div>
      <div class="tl-insp-sec">
        <label class="tl-insp-toggle chk"><input class="tl-insert-on" type="checkbox"> ⏢ インサートショット（別カメラを N 秒差し込む）</label>
        <div class="tl-insert-body" style="display:none"></div>
      </div>
      <div class="tl-cue-editor-host"></div>`;

    inspectorEl.querySelector('.tl-insp-close').onclick = () => { sel = null; closeCueEditor(); render(); renderInspector(); };
    renderCueRows();
    renderPost();
    renderInsert();
  }

  // ---- cue 割当行 ------------------------------------------------------------
  function renderCueRows() {
    const host = inspectorEl.querySelector('.tl-insp-cues');
    const noneEl = inspectorEl.querySelector('.tl-cue-none');
    if (!host) return;
    const seg = segAt(sel.lap, sel.camera);
    const camCues = cuesForCam(sel.camera);
    noneEl.textContent = camCues.length ? '' : 'このカメラには cue がありません。＋新規 cue 作成 で追加してください。';
    noneEl.className = 'tl-cue-none' + (camCues.length ? '' : ' warn');
    host.innerHTML = '';
    const list = (seg && seg.cues) || [];
    if (!list.length) {
      const e = document.createElement('div'); e.className = 'tl-cue-empty';
      e.textContent = '（cue 未割当）'; host.appendChild(e);
    }
    list.forEach((a, idx) => {
      const row = document.createElement('div'); row.className = 'tl-cue-row';
      let opts = '';
      for (const c of camCues) opts += `<option value="${escapeHtml(c.id)}"${a.cueId === c.id ? ' selected' : ''}>${escapeHtml(c.name || c.id)}</option>`;
      if (!camCues.some((c) => c.id === a.cueId)) opts = `<option value="${escapeHtml(a.cueId)}" selected>${escapeHtml(a.cueId)}（未定義）</option>` + opts;
      row.innerHTML = `
        <select class="tl-cr-cue" title="発火する cue">${opts}</select>
        <label class="tl-cr-l">遅延<input class="tl-cr-delay" type="number" min="0" step="0.1" value="${a.delaySec || 0}" title="進入からの遅延秒"></label>
        <label class="tl-cr-l chk"><input class="tl-cr-once" type="checkbox" ${a.once !== false ? 'checked' : ''}>1回</label>
        <label class="tl-cr-l chk"><input class="tl-cr-ovr" type="checkbox" ${a.hasOverride ? 'checked' : ''}>上書き</label>
        <button class="tl-cr-edit" title="この cue の中身（マスク・素材・フェード）を編集">✎ 編集</button>
        <button class="tl-cr-del" title="この割当を外す">🗑</button>
        <div class="tl-cr-override" style="display:${a.hasOverride ? 'flex' : 'none'}">
          <label>強度<input class="tl-ov-strength" type="number" min="0" max="1" step="0.05" value="${a.override?.strength ?? 1}"></label>
          <label>IN<input class="tl-ov-fadein" type="number" min="0" step="0.1" value="${a.override?.fadeIn ?? 0.5}"></label>
          <label>OUT<input class="tl-ov-fadeout" type="number" min="0" step="0.1" value="${a.override?.fadeOut ?? 0.5}"></label>
          <label>開始<input class="tl-ov-trimstart" type="number" min="0" step="0.1" value="${a.override?.trimStart ?? 0}"></label>
          <label>終了<input class="tl-ov-trimend" type="number" min="0" step="0.1" value="${a.override?.trimEnd ?? 0}"></label>
        </div>`;
      const cueSel = row.querySelector('.tl-cr-cue');
      const delayI = row.querySelector('.tl-cr-delay');
      const onceI = row.querySelector('.tl-cr-once');
      const ovrI = row.querySelector('.tl-cr-ovr');
      const ovBox = row.querySelector('.tl-cr-override');
      const commit = () => {
        a.cueId = cueSel.value;
        a.delaySec = Math.max(0, parseFloat(delayI.value) || 0);
        a.once = !!onceI.checked;
        a.hasOverride = !!ovrI.checked;
        if (!a.override) a.override = defaultOverride();
        a.override.strength = clampNum(row.querySelector('.tl-ov-strength').value, 1);
        a.override.fadeIn = clampNum(row.querySelector('.tl-ov-fadein').value, 0.5);
        a.override.fadeOut = clampNum(row.querySelector('.tl-ov-fadeout').value, 0.5);
        a.override.trimStart = clampNum(row.querySelector('.tl-ov-trimstart').value, 0);
        a.override.trimEnd = clampNum(row.querySelector('.tl-ov-trimend').value, 0);
        ovBox.style.display = a.hasOverride ? 'flex' : 'none';
        markDirty(); render();
      };
      cueSel.onchange = delayI.onchange = onceI.onchange = ovrI.onchange = commit;
      row.querySelectorAll('.tl-cr-override input').forEach((i) => { i.onchange = commit; });
      row.querySelector('.tl-cr-edit').onclick = () => openCueEditor(cueById(a.cueId));
      row.querySelector('.tl-cr-del').onclick = () => {
        seg.cues.splice(idx, 1); pruneSeg(seg); markDirty(); render(); renderCueRows();
      };
      host.appendChild(row);
    });

    inspectorEl.querySelector('.tl-cue-assign').onclick = () => {
      const camCues2 = cuesForCam(sel.camera);
      if (!camCues2.length) return;
      const s = ensureSeg(sel.lap, sel.camera);
      s.cues.push(newAssign(camCues2[0].id));
      markDirty(); render(); renderCueRows();
    };
    inspectorEl.querySelector('.tl-cue-new').onclick = () => openCueEditor(null);
  }

  function clampNum(v, def) { const n = parseFloat(v); return Number.isFinite(n) ? Math.max(0, n) : def; }

  // ---- post 上書き -----------------------------------------------------------
  let postFx = null;
  function renderPost() {
    const seg = segAt(sel.lap, sel.camera);
    const onChk = inspectorEl.querySelector('.tl-post-on');
    const fxBox = inspectorEl.querySelector('.tl-post-fx');
    const has = !!(seg && seg.hasPost);
    onChk.checked = has;
    fxBox.style.display = has ? '' : 'none';
    postFx = buildFxSliders(fxBox, (seg && seg.post) || globalPost(), () => {
      const s = ensureSeg(sel.lap, sel.camera);
      s.hasPost = true; s.post = postFx.read();
      onChk.checked = true; markDirty(); render();
    });
    onChk.onchange = () => {
      if (onChk.checked) {
        const s = ensureSeg(sel.lap, sel.camera);
        s.hasPost = true; if (!s.post) s.post = { ...globalPost() };
        postFx.set(s.post); fxBox.style.display = '';
      } else {
        const s2 = segAt(sel.lap, sel.camera);
        if (s2) { s2.hasPost = false; pruneSeg(s2); }
        fxBox.style.display = 'none';
      }
      markDirty(); render();
    };
  }

  // ---- insert 編集 -----------------------------------------------------------
  let insertPostFx = null;
  function renderInsert() {
    const seg = segAt(sel.lap, sel.camera);
    const onChk = inspectorEl.querySelector('.tl-insert-on');
    const body = inspectorEl.querySelector('.tl-insert-body');
    const has = !!(seg && seg.hasInsert);
    onChk.checked = has;
    body.style.display = has ? '' : 'none';

    const ins = (seg && seg.insert) || defaultInsert();
    const camOpts = cameras.map((c, i) => `<option value="${i}"${ins.camera === i ? ' selected' : ''}>${escapeHtml(camLabel(i))}</option>`).join('');
    const insCamCues = cuesForCam(ins.camera);
    let cueOpts = `<option value="">（cue なし）</option>`;
    for (const c of insCamCues) cueOpts += `<option value="${escapeHtml(c.id)}"${ins.cueId === c.id ? ' selected' : ''}>${escapeHtml(c.name || c.id)}</option>`;
    if (ins.cueId && !insCamCues.some((c) => c.id === ins.cueId)) cueOpts += `<option value="${escapeHtml(ins.cueId)}" selected>${escapeHtml(ins.cueId)}（未定義）</option>`;

    body.innerHTML = `
      <div class="tl-insert-grid">
        <label>アンカー<select class="tl-in-anchor">
          <option value="exit"${ins.anchor === 'exit' ? ' selected' : ''}>退出時（このゾーンを離れる瞬間）</option>
          <option value="enter"${ins.anchor === 'enter' ? ' selected' : ''}>進入時（このゾーンに入った直後）</option>
        </select></label>
        <label>差し込むカメラ<select class="tl-in-cam">${camOpts}</select></label>
        <label>表示秒<input class="tl-in-dur" type="number" min="0.2" step="0.1" value="${ins.durationSec || 4}"></label>
        <label class="tl-in-delay-l">遅延<input class="tl-in-delay" type="number" min="0" step="0.1" value="${ins.delaySec || 0}"></label>
        <label>cue<select class="tl-in-cue">${cueOpts}</select></label>
        <label class="chk"><input class="tl-in-once" type="checkbox" ${ins.once !== false ? 'checked' : ''}>1回</label>
      </div>
      <label class="tl-insp-toggle chk"><input class="tl-in-post-on" type="checkbox" ${ins.hasPost ? 'checked' : ''}> 🎨 インサート中の画像加工を上書き</label>
      <div class="tl-in-post-fx fx-rows" style="display:${ins.hasPost ? '' : 'none'}"></div>`;

    const anchorSel = body.querySelector('.tl-in-anchor');
    const delayL = body.querySelector('.tl-in-delay-l');
    delayL.style.display = ins.anchor === 'enter' ? '' : 'none';

    const commit = () => {
      const s = ensureSeg(sel.lap, sel.camera);
      s.hasInsert = true;
      if (!s.insert) s.insert = defaultInsert();
      s.insert.anchor = anchorSel.value;
      s.insert.camera = parseInt(body.querySelector('.tl-in-cam').value, 10) || 0;
      s.insert.durationSec = Math.max(0.2, parseFloat(body.querySelector('.tl-in-dur').value) || 4);
      s.insert.delaySec = Math.max(0, parseFloat(body.querySelector('.tl-in-delay').value) || 0);
      s.insert.cueId = body.querySelector('.tl-in-cue').value;
      s.insert.once = !!body.querySelector('.tl-in-once').checked;
      delayL.style.display = s.insert.anchor === 'enter' ? '' : 'none';
      markDirty(); render();
    };
    anchorSel.onchange = () => { commit(); renderInsert(); };  // カメラ変更で cue 候補も変わるため再描画
    body.querySelector('.tl-in-cam').onchange = () => { commit(); renderInsert(); };
    body.querySelectorAll('.tl-in-dur, .tl-in-delay, .tl-in-cue, .tl-in-once').forEach((i) => { i.onchange = commit; });

    const inPostOn = body.querySelector('.tl-in-post-on');
    const inPostFxBox = body.querySelector('.tl-in-post-fx');
    insertPostFx = buildFxSliders(inPostFxBox, ins.post || globalPost(), () => {
      const s = ensureSeg(sel.lap, sel.camera);
      s.hasInsert = true; if (!s.insert) s.insert = defaultInsert();
      s.insert.hasPost = true; s.insert.post = insertPostFx.read();
      inPostOn.checked = true; markDirty();
    });
    inPostOn.onchange = () => {
      const s = ensureSeg(sel.lap, sel.camera);
      s.hasInsert = true; if (!s.insert) s.insert = defaultInsert();
      s.insert.hasPost = inPostOn.checked;
      if (inPostOn.checked && !s.insert.post) s.insert.post = { ...globalPost() };
      inPostFxBox.style.display = inPostOn.checked ? '' : 'none';
      if (inPostOn.checked) insertPostFx.set(s.insert.post);
      markDirty();
    };

    onChk.onchange = () => {
      if (onChk.checked) {
        const s = ensureSeg(sel.lap, sel.camera);
        s.hasInsert = true; if (!s.insert) s.insert = defaultInsert();
        body.style.display = '';
      } else {
        const s2 = segAt(sel.lap, sel.camera);
        if (s2) { s2.hasInsert = false; pruneSeg(s2); }
        body.style.display = 'none';
      }
      markDirty(); render(); renderInsert();
    };
  }

  // ---- FX スライダ生成（segment / insert post 共用）---------------------------
  function buildFxSliders(containerEl, initial, onChange) {
    containerEl.innerHTML = '';
    const inputs = {}, vals = {};
    for (const [key, label, min, max, step] of FX) {
      const row = document.createElement('label');
      row.className = 'sld fx-row';
      row.append(label + ' ');
      const inp = document.createElement('input');
      inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step;
      inp.value = (initial && key in initial) ? initial[key] : FX_DEFAULT[key];
      const val = document.createElement('span'); val.textContent = inp.value;
      inp.oninput = () => { val.textContent = inp.value; onChange(); };
      row.append(inp, val);
      containerEl.appendChild(row);
      inputs[key] = inp; vals[key] = val;
    }
    return {
      read() { const o = {}; for (const [k] of FX) o[k] = parseFloat(inputs[k].value); return o; },
      set(vv) { for (const [k] of FX) { const v = (vv && k in vv) ? vv[k] : FX_DEFAULT[k]; inputs[k].value = v; vals[k].textContent = String(v); } },
    };
  }

  // ---- 埋め込み cue エディタ --------------------------------------------------
  function ensureCueEditor() {
    if (cueEditor) return cueEditor;
    cueEditor = createCueEditor({
      getLiveImg: (camId) => (deps.getLiveImg ? deps.getLiveImg(camId) : null),
      getCamPost: (camId) => { const c = cameras.find((x) => x.id === camId); return (c && c.post) || globalPost(); },
      getCaptures: () => (deps.getCaptures ? deps.getCaptures() : []),
      refreshCaptures: () => deps.refreshCaptures && deps.refreshCaptures(),
      getAllCues: () => cues,
      saveCue: (cue) => deps.saveCue(cue),
      onSaved: (cue) => {
        // 新規作成なら現在セグメントへ自動割当（未割当時のみ）。
        if (sel) {
          const s = ensureSeg(sel.lap, sel.camera);
          if (!s.cues.some((a) => a.cueId === cue.id)) { s.cues.push(newAssign(cue.id)); markDirty(); }
        }
        // cues は saveCue で app 側 state.cues に反映済み。ローカル cues も更新。
        const k = cues.findIndex((c) => c.id === cue.id);
        if (k >= 0) cues[k] = cue; else cues.push(cue);
        render(); renderCueRows(); renderInsert();
      },
    });
    cueEditor.el.addEventListener('ce-close', closeCueEditor);
    return cueEditor;
  }
  function openCueEditor(cue) {
    if (!sel) return;
    const host = inspectorEl.querySelector('.tl-cue-editor-host');
    if (!host) return;
    ensureCueEditor();
    if (cueEditor.el.parentNode !== host) { host.innerHTML = ''; host.appendChild(cueEditor.el); }
    const camId = cameras[sel.camera] ? cameras[sel.camera].id : null;
    cueEditor.open(camId, cue);
    cueEditor.el.style.display = '';
    editorOpen = true;
  }
  function closeCueEditor() { if (cueEditor) cueEditor.el.style.display = 'none'; editorOpen = false; }

  // ---- 周回の増減 ------------------------------------------------------------
  q('.tl-lap-add').onclick = () => { lapCount++; markDirty(); render(); };
  q('.tl-lap-del').onclick = () => {
    if (lapCount <= 1) return;
    const removed = lapCount;
    timeline.segments = timeline.segments.filter((s) => s.lap < removed);
    lapCount--;
    if (sel && sel.lap >= lapCount + 1) { sel = null; closeCueEditor(); renderInspector(); }
    markDirty(); render();
  };

  // ---- 保存 ------------------------------------------------------------------
  q('.tl-save').onclick = async () => {
    timeline.rev = (parseInt(timeline.rev, 10) || 0) + 1;
    const res = await deps.saveTimeline(serialize());
    if (res && res.ok !== false) { dirty = false; renderDirty(); }
    else { dirtyEl.textContent = '✕ 保存失敗'; }
  };

  // 保存用に正規化（present-flag を確定。空セグメントは落とす）。
  function serialize() {
    const segs = timeline.segments
      .filter((s) => (s.cues && s.cues.length) || s.hasPost || s.hasInsert)
      .map((s) => ({
        lap: s.lap, camera: s.camera,
        cues: (s.cues || []).map((a) => ({
          cueId: a.cueId, delaySec: a.delaySec || 0, once: a.once !== false,
          override: a.hasOverride ? { ...defaultOverride(), ...(a.override || {}) } : defaultOverride(),
          hasOverride: !!a.hasOverride,
        })),
        post: s.hasPost ? { ...FX_DEFAULT, ...(s.post || {}) } : { ...FX_DEFAULT },
        hasPost: !!s.hasPost,
        insert: s.hasInsert ? { ...defaultInsert(), ...(s.insert || {}), post: { ...FX_DEFAULT, ...((s.insert && s.insert.post) || {}) } } : defaultInsert(),
        hasInsert: !!s.hasInsert,
      }));
    return { rev: timeline.rev, segments: segs };
  }

  // ---- adopt / migration ------------------------------------------------------
  function adopt(state) {
    const tl = state && state.timeline;
    const hasTl = tl && Array.isArray(tl.segments) && tl.segments.length > 0;
    if (hasTl) {
      timeline = normalize(tl);
    } else {
      // migration: schedule.entries → timeline（編集開始）。無ければ空。
      const entries = (state && state.schedule && Array.isArray(state.schedule.entries)) ? state.schedule.entries : [];
      timeline = migrateFromSchedule(entries, tl ? tl.rev : 1);
    }
  }
  function normalize(tl) {
    const segs = (tl.segments || []).map((s) => {
      const seg = newSeg(s.lap, s.camera);
      seg.cues = Array.isArray(s.cues) ? s.cues.map((a) => ({
        cueId: a.cueId, delaySec: a.delaySec || 0, once: a.once !== false,
        override: { ...defaultOverride(), ...(a.override || {}) }, hasOverride: !!a.hasOverride,
      })) : [];
      seg.hasPost = !!s.hasPost; if (s.post) seg.post = { ...FX_DEFAULT, ...s.post };
      seg.hasInsert = !!s.hasInsert;
      if (s.insert) seg.insert = { ...defaultInsert(), ...s.insert, post: { ...FX_DEFAULT, ...(s.insert.post || {}) } };
      return seg;
    });
    return { rev: Number.isInteger(tl.rev) ? tl.rev : 1, segments: segs };
  }
  function migrateFromSchedule(entries, rev) {
    const out = { rev: Number.isInteger(rev) ? rev : 1, segments: [] };
    for (const e of entries) {
      if (!Number.isInteger(e.lap) || !Number.isInteger(e.camera) || !e.cueId) continue;
      let s = out.segments.find((x) => x.lap === e.lap && x.camera === e.camera);
      if (!s) { s = newSeg(e.lap, e.camera); out.segments.push(s); }
      const a = newAssign(e.cueId); a.delaySec = e.delaySec || 0; a.once = e.once !== false;
      s.cues.push(a);
    }
    return out;
  }
  function recomputeLapCount() {
    const maxLap = timeline.segments.reduce((m, s) => Math.max(m, s.lap || 1), 0);
    lapCount = Math.max(1, maxLap, lapCount);
  }

  // ==========================================================================
  //  検証モード（矢印キーシミュレーション）— Unity セマンティクスの JS ミラー
  // ==========================================================================
  const validation = createValidation();
  validateBtn.onclick = () => (validation.active ? validation.stop() : validation.start());

  function createValidation() {
    let active = false;
    let steps = 0;
    let cur = null;                 // computeState(steps) の結果
    let transientQueue = [], transient = null, transientTimer = 0;
    const valMedia = createMediaCache();
    let view = null, canvas = null;

    // --- Unity セマンティクスの純ロジック ---
    // computeState(n): 検証開始から → を n 回押した「落ち着いた」離散状態を決定的に再計算。
    //   LapCounter: order 順方向一致でのみ前進・order[0] 復帰で lap++・開始ゾーンをシード。
    //   CueScheduler: (lap,camera) 一致 + delaySec で発火・once はラン内 1 回。
    //   insert: enter/exit を fireLog に記録（表示は transient で実時間再生）。
    function computeState(n) {
      const ord = rows();
      const cn = ord.length;
      const log = [];
      if (!cn) return { lap: 1, pointer: 0, activeCam: null, fireLog: log, showCueId: null, showPost: globalPost() };
      const onceCue = new Set();     // `${lap}:${cam}:${cueId}`
      const onceIns = new Set();     // `${lap}:${cam}` （そのセグメントの insert）
      let lap = 1, pointer = 0, showCueId = null;

      const fireCues = (l, cam) => {
        const seg = segAt(l, cam);
        let shown = null;
        if (seg && seg.cues) for (const a of seg.cues) {
          const key = `${l}:${cam}:${a.cueId}`;
          if (a.once !== false && onceCue.has(key)) continue;
          if (a.once !== false) onceCue.add(key);
          log.push({ type: 'cue', lap: l, camera: cam, cueId: a.cueId, delaySec: a.delaySec || 0 });
          shown = a.cueId;
        }
        return shown;
      };
      const fireInsert = (l, cam, anchor) => {
        const seg = segAt(l, cam);
        if (!seg || !seg.hasInsert || !seg.insert || seg.insert.anchor !== anchor) return;
        const key = `${l}:${cam}`;
        if (seg.insert.once !== false && onceIns.has(key)) return;
        if (seg.insert.once !== false) onceIns.add(key);
        log.push({
          type: 'insert', anchor, hostLap: l, hostCam: cam,
          camera: seg.insert.camera, cueId: seg.insert.cueId || '',
          durationSec: seg.insert.durationSec || 4,
          post: seg.insert.hasPost ? seg.insert.post : null, hasPost: !!seg.insert.hasPost,
        });
      };

      // seed: 開始ゾーン（lap1, order[0]）に居る扱い
      fireInsert(1, ord[0], 'enter');
      showCueId = fireCues(1, ord[0]);
      for (let step = 1; step <= n; step++) {
        const prevCam = ord[pointer], prevLap = lap;
        pointer = (pointer + 1) % cn;
        if (pointer === 0) lap++;
        const cam = ord[pointer];
        fireInsert(prevLap, prevCam, 'exit');
        fireInsert(lap, cam, 'enter');
        showCueId = fireCues(lap, cam);
      }
      return { lap, pointer, activeCam: ord[pointer], fireLog: log, showCueId, showPost: resolvePost(lap, ord[pointer]) };
    }

    function resolvePost(lap, cam) {
      const seg = segAt(lap, cam);
      if (seg && seg.hasPost && seg.post) return seg.post;
      const camObj = cameras[cam];
      if (camObj && camObj.post) return camObj.post;
      return globalPost();
    }

    function recompute() { cur = computeState(steps); }

    // 前ステップとの差分から「今回新たに発火した insert」を抽出 → 実時間再生。
    function playNewInserts(prevLen) {
      const news = cur.fireLog.slice(prevLen).filter((e) => e.type === 'insert');
      if (!news.length) return;
      transientQueue = news.slice();
      advanceTransient();
    }
    function advanceTransient() {
      clearTimeout(transientTimer);
      if (!active || !transientQueue.length) { transient = null; renderPanel(); render(); return; }
      const ins = transientQueue.shift();
      transient = {
        cam: ins.camera, cueId: ins.cueId || null,
        post: (ins.hasPost && ins.post) ? ins.post : (cameras[ins.camera]?.post || globalPost()),
        label: `⏢ INSERT ${ins.anchor === 'exit' ? '退出' : '進入'} → ${camLabel(ins.camera)}（${ins.durationSec}s）`,
      };
      transientTimer = setTimeout(advanceTransient, Math.max(200, (ins.durationSec || 0) * 1000));
      renderPanel(); render();
    }
    function cancelTransient() { clearTimeout(transientTimer); transientQueue = []; transient = null; }

    // --- 操作 ---
    function advance() { const prevLen = cur ? cur.fireLog.length : 0; steps++; cancelTransient(); recompute(); playNewInserts(prevLen); renderPanel(); render(); }
    function back() { if (steps <= 0) return; steps--; cancelTransient(); recompute(); renderPanel(); render(); }
    function reset() { steps = 0; cancelTransient(); recompute(); renderPanel(); render(); }

    function onKey(e) {
      if (!active) return;
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA')) return;
      if (e.key === 'ArrowRight') { e.preventDefault(); advance(); }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); back(); }
      else if (e.key === 'r' || e.key === 'R') { e.preventDefault(); reset(); }
      else if (e.key === 'Escape') { e.preventDefault(); stop(); }
    }

    function start() {
      active = true; steps = 0; cancelTransient(); recompute();
      validateBtn.classList.add('on'); validateBtn.textContent = '■ 検証終了';
      validatePanel.style.display = '';
      buildPanel();
      document.addEventListener('keydown', onKey);
      renderPanel(); render();
    }
    function stop() {
      active = false; cancelTransient();
      validateBtn.classList.remove('on'); validateBtn.textContent = '▶ 検証';
      validatePanel.style.display = 'none';
      document.removeEventListener('keydown', onKey);
      if (view) { view.destroy(); view = null; }
      render();
    }

    // --- パネル + プレビュー ---
    function buildPanel() {
      validatePanel.innerHTML = `
        <div class="tl-val-head">▶ 検証（ローカルシミュレーション・show.json は書きません）</div>
        <div class="tl-val-body">
          <div class="tl-val-view"><div class="view-wrap"><canvas class="tl-val-canvas" width="640" height="360"></canvas></div>
            <div class="tl-val-keys">→ 次ゾーン ／ ← 1手戻る ／ R 先頭 ／ Esc 終了</div></div>
          <div class="tl-val-info">
            <div class="tl-val-state"></div>
            <div class="tl-val-log"></div>
          </div>
        </div>`;
      canvas = validatePanel.querySelector('.tl-val-canvas');
      if (view) view.destroy();
      view = createCompositeView(canvas, { sample: sampleView });
    }

    function sampleView() {
      const camIdx = transient ? transient.cam : (cur ? cur.activeCam : null);
      const cueId = transient ? transient.cueId : (cur ? cur.showCueId : null);
      const post = transient ? transient.post : (cur ? cur.showPost : globalPost());
      const camObj = (camIdx != null) ? cameras[camIdx] : null;
      const liveImg = (camObj && deps.getLiveImg) ? deps.getLiveImg(camObj.id) : null;
      let overlayEl = null, overlayReady = false, overlayW = 0, overlayH = 0, maskEl = null, strength = 1, fadeSec = 0.5;
      if (cueId) {
        const cue = cueById(cueId);
        if (cue && cue.sourceUrl) {
          const m = valMedia.get(cue.sourceUrl);
          if (m && m.el) {
            overlayEl = m.el; overlayReady = m.ready;
            if (m.isVideo && m.ready && m.el.paused) { try { m.el.play().catch(() => {}); } catch { /* noop */ } }
            overlayW = m.isVideo ? (m.el.videoWidth || 0) : (m.el.naturalWidth || 0);
            overlayH = m.isVideo ? (m.el.videoHeight || 0) : (m.el.naturalHeight || 0);
          }
          maskEl = cue.maskUrl ? valMedia.getImage(cue.maskUrl) : null; // null => 全面差し替え
          strength = cue.strength ?? 1; fadeSec = cue.fadeIn ?? 0.5;
        }
      }
      return {
        liveImg, overlayEl, overlayReady, overlayW, overlayH, maskEl,
        overlayOn: !!cueId, fadeSec, strength, feather: 0, post, trimEnd: 0, onVideoEnd: null,
      };
    }

    function renderPanel() {
      if (!active || !validatePanel.querySelector('.tl-val-state')) return;
      const stateEl = validatePanel.querySelector('.tl-val-state');
      const logEl = validatePanel.querySelector('.tl-val-log');
      const camTxt = cur && cur.activeCam != null ? camLabel(cur.activeCam) : '—';
      const showTxt = transient ? transient.label
        : (cur && cur.showCueId ? `🎬 ${cueName(cur.showCueId)}` : 'ライブのみ');
      stateEl.innerHTML = `<b>${cur ? cur.lap : 1}周目</b> ／ ゾーン <b>${escapeHtml(camTxt)}</b> ／ ${escapeHtml(showTxt)}`
        + `<span class="tl-val-step">step ${steps}</span>`;
      // 発火ログ（新しい順）
      const log = cur ? cur.fireLog : [];
      logEl.innerHTML = log.length ? '' : '<div class="tl-val-log-empty">（まだ発火なし）</div>';
      log.slice().reverse().forEach((e) => {
        const d = document.createElement('div'); d.className = 'tl-val-log-row';
        if (e.type === 'cue') {
          d.textContent = `${e.lap}周 ${camLabel(e.camera)}: 🎬 ${cueName(e.cueId)}${e.delaySec ? ` +${e.delaySec}s` : ''}`;
        } else {
          d.textContent = `${e.hostLap}周 ${camLabel(e.hostCam)} ${e.anchor === 'exit' ? '退出' : '進入'}: ⏢ ${camLabel(e.camera)}（${e.durationSec}s）${e.cueId ? ` / 🎬 ${cueName(e.cueId)}` : ''}`;
        }
        logEl.appendChild(d);
      });
    }

    return {
      get active() { return active; },
      start, stop,
      current: () => cur,
      // onState 時に再計算（編集結果を検証へ反映）
      refresh() { if (active) { recompute(); renderPanel(); } },
      destroy() { cancelTransient(); if (view) view.destroy(); valMedia.dispose(); document.removeEventListener('keydown', onKey); },
    };
  }

  // ---- 外部 API --------------------------------------------------------------
  function onState(state) {
    cameras = deps.getCameras ? deps.getCameras() : (state?.cameras || []);
    cues = deps.getCues ? deps.getCues() : (state?.cues || []);
    order = deps.getCourseOrder ? deps.getCourseOrder() : (state?.layout?.course?.order || null);
    if (!dirty) { adopt(state); recomputeLapCount(); }
    if (sel && sel.camera >= cameras.length) { sel = null; closeCueEditor(); }
    validation.refresh();
    render();
    // cue エディタ展開中はインスペクタを作り直さない（開いている編集器を潰さない）。
    if (sel && !editorOpen) renderInspector();
    else if (!sel) inspectorEl.innerHTML = '';
  }

  onState(null);
  renderDirty();
  return {
    onState,
    destroy() { validation.destroy(); if (cueEditor) cueEditor.destroy(); },
  };
}
