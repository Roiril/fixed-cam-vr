// 廻リ視 周回スケジュール（事前オーサリング済み cue 発火表）。
//   マトリクス: 行 = カメラ（layout.course.order 順）、列 = 周回（1..N・列追加/削除可）。
//   セルクリックで cue 割当（そのカメラの cue から選択）+ delaySec + once を編集。
//   保存は postState({schedule})。floormap.js パターン踏襲＝deps 注入・dirty ガード・onState 上書き抑止。
//
//   契約（.claude/plans/2026-07-17_pre-authored-cue-schedule.md が正）:
//   - schedule = { rev, entries: [{ lap, camera, cueId, delaySec, once }] }。
//   - lap は 1 始まり。camera は「カメラ index」（cameras[] の index。ゾーンは cameraIndex でキー）。
//   - cueId は cues[].id。cue.camera は id 文字列なので index→id 変換して候補を絞る。
//   - course.order もカメラ index の配列（order[0]=スタート）。無ければ 0..N-1。

const CAM_COLORS = ['#5ad19a', '#5aa8ff', '#ffae5e', '#d98cff', '#ff6b8e', '#8ad4ff'];
const camColor = (i) => CAM_COLORS[((i % CAM_COLORS.length) + CAM_COLORS.length) % CAM_COLORS.length];

export function createSchedule(container, deps) {
  // deps: { getCameras, getCues, getCourseOrder, saveSchedule }
  container.innerHTML = `
    <div class="sc-wrap">
      <div class="sc-row">
        <button class="sc-save accent">💾 保存</button>
        <span class="sc-dirty"></span>
        <span class="spacer"></span>
        <button class="sc-lap-add" title="周回（列）を 1 つ増やす">＋ 周回</button>
        <button class="sc-lap-del" title="最後の周回（列）を削除">－ 周回</button>
      </div>
      <div class="sc-hint">行＝カメラ（コース順）、列＝周回（1 始まり）。セルをクリックして cue を割り当てる。空セル = そのゾーンで発火なし。ライブ手動操作（演出 ON）が優先で、その間はスケジュールは抑止される。</div>
      <div class="sc-matrix-wrap"><table class="sc-matrix"></table></div>
      <div class="sc-editor"></div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const table = q('.sc-matrix');
  const editorEl = q('.sc-editor');
  const dirtyEl = q('.sc-dirty');

  let schedule = { rev: 1, entries: [] };
  let dirty = false;
  let cameras = [];
  let cues = [];
  let order = null;          // カメラ index の配列
  let lapCount = 1;
  let sel = null;            // 選択中セル { camera, lap }

  const clone = (o) => JSON.parse(JSON.stringify(o));
  function markDirty() { dirty = true; renderDirty(); }
  function renderDirty() { dirtyEl.textContent = dirty ? '● 未保存' : ''; dirtyEl.className = 'sc-dirty' + (dirty ? ' on' : ''); }

  // ---- データアクセサ --------------------------------------------------------
  function rows() {
    // course.order（index 配列）→ 有効な index のみ。無ければ全カメラ。
    const n = cameras.length;
    if (Array.isArray(order) && order.length) {
      const valid = order.filter((i) => Number.isInteger(i) && i >= 0 && i < n);
      if (valid.length) return valid;
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

  // ---- マトリクス描画 --------------------------------------------------------
  function render() {
    const rs = rows();
    // ヘッダ行（周回）
    let html = '<thead><tr><th class="sc-corner">カメラ ＼ 周回</th>';
    for (let lap = 1; lap <= lapCount; lap++) html += `<th>${lap}</th>`;
    html += '</tr></thead><tbody>';
    for (const ci of rs) {
      const cam = cameras[ci];
      const label = cam ? `カメラ ${cam.id}` : `#${ci}`;
      html += `<tr><th class="sc-camhead"><i style="background:${camColor(ci)}"></i>${label}</th>`;
      for (let lap = 1; lap <= lapCount; lap++) {
        const e = entryAt(ci, lap);
        const on = sel && sel.camera === ci && sel.lap === lap;
        const cls = 'sc-cell' + (e ? ' filled' : '') + (on ? ' sel' : '');
        const text = e ? cueName(e.cueId) + (e.delaySec ? ` +${e.delaySec}s` : '') + (e.once === false ? ' ⟳' : '') : '·';
        html += `<td class="${cls}" data-cam="${ci}" data-lap="${lap}">${text}</td>`;
      }
      html += '</tr>';
    }
    html += '</tbody>';
    table.innerHTML = html;
    table.querySelectorAll('.sc-cell').forEach((td) => {
      td.onclick = () => { sel = { camera: +td.dataset.cam, lap: +td.dataset.lap }; render(); renderEditor(); };
    });
  }

  // ---- セルエディタ ----------------------------------------------------------
  function renderEditor() {
    if (!sel) { editorEl.innerHTML = ''; return; }
    const cam = cameras[sel.camera];
    const list = cuesForCam(sel.camera);
    const e = entryAt(sel.camera, sel.lap);
    const camLabel = cam ? `カメラ ${cam.id}` : `#${sel.camera}`;
    let opts = '<option value="">（発火なし）</option>';
    for (const c of list) opts += `<option value="${c.id}"${e && e.cueId === c.id ? ' selected' : ''}>${(c.name || c.id)}</option>`;
    editorEl.innerHTML = `
      <div class="sc-edit-head">${camLabel} / ${sel.lap} 周目</div>
      <div class="sc-edit-grid">
        <label>cue<select class="sc-cue">${opts}</select></label>
        <label>遅延(s)<input class="sc-delay" type="number" min="0" step="0.1" value="${e ? (e.delaySec || 0) : 0}"></label>
        <label class="sc-chk"><input class="sc-once" type="checkbox" ${!e || e.once !== false ? 'checked' : ''}> このランで1回だけ</label>
      </div>
      ${list.length ? '' : '<div class="sc-warn">このカメラには cue がありません（上のマルチカメラで作成）。</div>'}`;

    const cueSel = editorEl.querySelector('.sc-cue');
    const delayI = editorEl.querySelector('.sc-delay');
    const onceI = editorEl.querySelector('.sc-once');
    const commit = () => {
      const cueId = cueSel.value;
      // cue 未選択 → エントリ削除
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
  }

  // ---- 周回列の増減 ----------------------------------------------------------
  q('.sc-lap-add').onclick = () => { lapCount++; markDirty(); render(); };
  q('.sc-lap-del').onclick = () => {
    if (lapCount <= 1) return;
    const removed = lapCount;
    schedule.entries = schedule.entries.filter((e) => e.lap < removed);
    lapCount--;
    if (sel && sel.lap >= lapCount + 1) { sel = null; renderEditor(); }
    markDirty(); render();
  };

  q('.sc-save').onclick = async () => {
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
    render(); renderEditor();
  }

  onState(null);
  renderDirty();
  return { onState };
}
