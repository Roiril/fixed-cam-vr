// 廻リ視 周回タイムライン（事前オーサリング済み cue 発火）。
//   動画編集風の横長タイムライン: layout.course.order 順の区間を
//   「1周目: A|B|C → 2周目: A|B|C → …」と横に連結表示（横スクロール・周回の区切りを明示）。
//   区間クリックで cue 割当（そのカメラの cue から選択）+ delaySec + once を編集。
//   保存は postState({schedule})。floormap.js パターン踏襲＝deps 注入・dirty ガード・onState 上書き抑止。
//
//   契約（.claude/plans/2026-07-17_pre-authored-cue-schedule.md が正・データモデルは不変）:
//   - schedule = { rev, entries: [{ lap, camera, cueId, delaySec, once }] }。
//   - lap は 1 始まり。camera は「カメラ index」（cameras[] の index。ゾーンは cameraIndex でキー）。
//   - cueId は cues[].id。cue.camera は id 文字列なので index→id 変換して候補を絞る。
//   - course.order もカメラ index の配列（order[0]=スタート）。無ければ 0..N-1 の順で表示 + 注記。

const CAM_COLORS = ['#5ad19a', '#5aa8ff', '#ffae5e', '#d98cff', '#ff6b8e', '#8ad4ff'];
const camColor = (i) => CAM_COLORS[((i % CAM_COLORS.length) + CAM_COLORS.length) % CAM_COLORS.length];
const escapeHtml = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

export function createSchedule(container, deps) {
  // deps: { getCameras, getCues, getCourseOrder, saveSchedule }
  container.innerHTML = `
    <div class="tl-wrap">
      <div class="tl-row">
        <button class="tl-save accent">💾 保存</button>
        <span class="tl-dirty"></span>
        <span class="spacer"></span>
        <button class="tl-lap-add" title="周回を 1 つ増やす">＋ 周回</button>
        <button class="tl-lap-del" title="最後の周回を削除">－ 周回</button>
      </div>
      <div class="tl-hint">区間をクリックして、その周・そのカメラで発火する cue を割り当てる。空 = そのゾーンで発火なし。ライブ手動操作（演出 ON）が優先で、その間は抑止される。</div>
      <div class="tl-note"></div>
      <div class="tl-track-wrap"><div class="tl-track"></div></div>
      <div class="tl-editor"></div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const track = q('.tl-track');
  const editorEl = q('.tl-editor');
  const dirtyEl = q('.tl-dirty');
  const noteEl = q('.tl-note');

  let schedule = { rev: 1, entries: [] };
  let dirty = false;
  let cameras = [];
  let cues = [];
  let order = null;          // カメラ index の配列
  let lapCount = 1;
  let sel = null;            // 選択中区間 { camera, lap }
  let orderIsExplicit = false;

  const clone = (o) => JSON.parse(JSON.stringify(o));
  function markDirty() { dirty = true; renderDirty(); }
  function renderDirty() { dirtyEl.textContent = dirty ? '● 未保存' : ''; dirtyEl.className = 'tl-dirty' + (dirty ? ' on' : ''); }

  // ---- データアクセサ --------------------------------------------------------
  function rows() {
    // course.order（index 配列）→ 有効な index のみ。無ければ全カメラ（0..N-1）。
    const n = cameras.length;
    orderIsExplicit = false;
    if (Array.isArray(order) && order.length) {
      const valid = order.filter((i) => Number.isInteger(i) && i >= 0 && i < n);
      if (valid.length) { orderIsExplicit = true; return valid; }
    }
    return cameras.map((_, i) => i);
  }
  function cuesForCam(idx) {
    const id = cameras[idx] ? cameras[idx].id : null;
    return id == null ? [] : cues.filter((c) => (c.camera || '') === id);
  }
  function entryAt(camera, lap) {
    return schedule.entries.find((e) => e.camera === camera && e.lap === lap) || null;
  }
  function cueName(cueId) {
    const c = cues.find((x) => x.id === cueId);
    return c ? (c.name || c.id) : cueId;
  }

  // ---- タイムライン描画 ------------------------------------------------------
  function render() {
    const rs = rows();
    const needNote = !orderIsExplicit && cameras.length > 0;
    noteEl.textContent = needNote
      ? 'コース順が未設定です。フロアマップで周回コースを設定してください（今は 0..N-1 の順で表示中）。' : '';
    noteEl.className = 'tl-note' + (needNote ? ' on' : '');

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
      for (const ci of rs) {
        const cam = cameras[ci];
        const e = entryAt(ci, lap);
        const on = sel && sel.camera === ci && sel.lap === lap;
        const camId = cam ? cam.id : `#${ci}`;
        const clip = e
          ? cueName(e.cueId) + (e.delaySec ? ` +${e.delaySec}s` : '') + (e.once === false ? ' ⟳' : '')
          : '—';
        const seg = document.createElement('button');
        seg.className = 'tl-seg' + (e ? ' filled' : '') + (on ? ' sel' : '');
        seg.style.setProperty('--cam', camColor(ci));
        seg.dataset.cam = ci; seg.dataset.lap = lap;
        seg.innerHTML = `<span class="tl-seg-cam"><i></i>${escapeHtml(camId)}</span>`
          + `<span class="tl-seg-clip">${escapeHtml(clip)}</span>`;
        seg.onclick = () => { sel = { camera: ci, lap }; render(); renderEditor(); };
        segs.appendChild(seg);
      }
      lapEl.appendChild(segs);
      track.appendChild(lapEl);
    }
  }

  // ---- 区間エディタ（小編集）------------------------------------------------
  function renderEditor() {
    if (!sel) { editorEl.innerHTML = ''; return; }
    const cam = cameras[sel.camera];
    const list = cuesForCam(sel.camera);
    const e = entryAt(sel.camera, sel.lap);
    const camLabel = cam ? `カメラ ${cam.id}` : `#${sel.camera}`;
    let opts = '<option value="">（何もしない）</option>';
    for (const c of list) opts += `<option value="${escapeHtml(c.id)}"${e && e.cueId === c.id ? ' selected' : ''}>${escapeHtml(c.name || c.id)}</option>`;
    editorEl.innerHTML = `
      <div class="tl-edit-head">${escapeHtml(camLabel)} / ${sel.lap}周目</div>
      <div class="tl-edit-grid">
        <label>cue<select class="tl-cue">${opts}</select></label>
        <label>遅延(s)<input class="tl-delay" type="number" min="0" step="0.1" value="${e ? (e.delaySec || 0) : 0}"></label>
        <label class="tl-chk"><input class="tl-once" type="checkbox" ${!e || e.once !== false ? 'checked' : ''}> このランで1回だけ</label>
        <button class="tl-edit-close">閉じる</button>
      </div>
      ${list.length ? '' : '<div class="tl-warn">このカメラには cue がありません（上のマルチカメラで作成）。</div>'}`;

    const cueSel = editorEl.querySelector('.tl-cue');
    const delayI = editorEl.querySelector('.tl-delay');
    const onceI = editorEl.querySelector('.tl-once');
    const commit = () => {
      const cueId = cueSel.value;
      // cue 未選択（何もしない）→ エントリ削除
      schedule.entries = schedule.entries.filter((x) => !(x.camera === sel.camera && x.lap === sel.lap));
      if (cueId) {
        schedule.entries.push({
          lap: sel.lap, camera: sel.camera, cueId,
          delaySec: Math.max(0, parseFloat(delayI.value) || 0),
          once: !!onceI.checked,
        });
      }
      markDirty(); render(); // エディタは開いたまま
    };
    cueSel.onchange = commit;
    delayI.onchange = commit;
    onceI.onchange = commit;
    editorEl.querySelector('.tl-edit-close').onclick = () => { sel = null; render(); renderEditor(); };
  }

  // ---- 周回の増減（末尾）----------------------------------------------------
  q('.tl-lap-add').onclick = () => { lapCount++; markDirty(); render(); };
  q('.tl-lap-del').onclick = () => {
    if (lapCount <= 1) return;
    const removed = lapCount;
    schedule.entries = schedule.entries.filter((e) => e.lap < removed);
    lapCount--;
    if (sel && sel.lap >= lapCount + 1) { sel = null; renderEditor(); }
    markDirty(); render();
  };

  q('.tl-save').onclick = async () => {
    schedule.rev = (parseInt(schedule.rev, 10) || 0) + 1;
    const res = await deps.saveSchedule(clone(schedule));
    if (res && res.ok !== false) { dirty = false; renderDirty(); }
    else { dirtyEl.textContent = '✕ 保存失敗'; }
  };

  // ---- 外部 API --------------------------------------------------------------
  function adopt(sc) {
    schedule = sc && Array.isArray(sc.entries) ? clone(sc) : { rev: 1, entries: [] };
    if (!Number.isInteger(schedule.rev)) schedule.rev = 1;
    if (!Array.isArray(schedule.entries)) schedule.entries = [];
  }
  function recomputeLapCount() {
    const maxLap = schedule.entries.reduce((m, e) => Math.max(m, e.lap || 1), 0);
    lapCount = Math.max(1, maxLap, lapCount);
  }

  function onState(state) {
    cameras = deps.getCameras ? deps.getCameras() : (state?.cameras || []);
    cues = deps.getCues ? deps.getCues() : (state?.cues || []);
    order = deps.getCourseOrder ? deps.getCourseOrder() : (state?.layout?.course?.order || null);
    if (!dirty) { adopt(state && state.schedule); recomputeLapCount(); }
    // カメラ集合が縮んで選択が無効になったら解除
    if (sel && sel.camera >= cameras.length) sel = null;
    render(); renderEditor();
  }

  onState(null);
  renderDirty();
  return { onState };
}
