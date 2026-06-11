// 🎛 オペレータ卓コンソール。show.json（サーバ）を正として Unity を遠隔制御する。
//   - state: GET /state?rev=N の long-poll で常時同期（他クライアントの変更も反映）
//   - 操作: POST /command（playCue / stopCue / setCameraOverride / setPost）
//   - Unity 状態: GET /unity/status を 2s ポーリング
// コンポジット検証タブ（main.js）とは独立して動く。

const $ = (id) => document.getElementById(id);

let state = null;          // 最新の show.json
let unityAlive = false;
let previewOn = {};        // camId -> bool（MJPEG サムネは明示 ON。スマホ負荷への配慮）

// ---- タブ切替 -------------------------------------------------------------
function setupTabs() {
  const show = (console_) => {
    $('tabConsole').style.display = console_ ? '' : 'none';
    $('tabComp').style.display = console_ ? 'none' : '';
    $('tabBtnConsole').classList.toggle('active', console_);
    $('tabBtnComp').classList.toggle('active', !console_);
  };
  $('tabBtnConsole').onclick = () => show(true);
  $('tabBtnComp').onclick = () => show(false);
}

// ---- サーバ I/O -----------------------------------------------------------
async function postCommand(cmd) {
  try {
    const r = await fetch('/command', { method: 'POST', body: JSON.stringify(cmd) });
    return await r.json();
  } catch (e) {
    console.warn('command failed', e);
    return { ok: false };
  }
}

async function postState(patch) {
  try {
    const r = await fetch('/state', { method: 'POST', body: JSON.stringify(patch) });
    return await r.json();
  } catch (e) {
    return { ok: false };
  }
}

// state long-poll ループ。rev が進むたびに UI を再描画する。
async function pollStateLoop() {
  let rev = -1;
  for (;;) {
    try {
      const r = await fetch(`/state?rev=${rev}`);
      const s = await r.json();
      if (s.rev !== rev) {
        rev = s.rev;
        state = s;
        renderCameras();
        renderCues();
        syncPostSliders();
      }
    } catch (e) {
      await new Promise((res) => setTimeout(res, 2000));
    }
  }
}

async function pollUnityLoop() {
  for (;;) {
    try {
      const r = await fetch('/unity/status');
      const s = await r.json();
      unityAlive = !!s.alive;
      const st = s.status || {};
      $('unityAlive').className = 'unity-dot ' + (unityAlive ? 'on' : 'off');
      $('unityInfo').textContent = unityAlive
        ? `Unity: ${st.activeCamera || '?'} / ${(st.recvFps || 0).toFixed(1)}fps`
          + (st.playingCue ? ` / 🎬 ${st.playingCue}` : '')
          + (st.cameraOverride ? ` / 🔒 override=${st.cameraOverride}` : '')
        : 'Unity: 未接続';
      renderActiveBadges(st);
    } catch (e) {
      unityAlive = false;
      $('unityAlive').className = 'unity-dot off';
      $('unityInfo').textContent = 'サーバ未接続';
    }
    await new Promise((res) => setTimeout(res, 2000));
  }
}

// ---- カメラカード ----------------------------------------------------------
function renderCameras() {
  const wrap = $('cameraCards');
  wrap.innerHTML = '';
  for (const cam of state?.cameras || []) {
    const card = document.createElement('div');
    card.className = 'camera-card';
    card.dataset.cam = cam.id;

    const head = document.createElement('div');
    head.className = 'cam-head';
    head.innerHTML = `<b>カメラ ${cam.id}</b> <span class="cam-src">${cam.sourceId || ''}</span>`
      + `<span class="cam-active-badge" style="display:none">● ACTIVE</span>`;
    card.appendChild(head);

    const thumb = document.createElement('div');
    thumb.className = 'cam-thumb';
    const img = document.createElement('img');
    img.alt = '(プレビュー OFF)';
    if (previewOn[cam.id] && cam.host) {
      img.src = `http://${cam.host}:8080/video`;
    }
    thumb.appendChild(img);
    card.appendChild(thumb);

    const hostRow = document.createElement('div');
    hostRow.className = 'btns';
    const hostInput = document.createElement('input');
    hostInput.type = 'text';
    hostInput.placeholder = 'スマホ IP（サムネ用）';
    hostInput.value = cam.host || '';
    hostInput.onchange = () => {
      cam.host = hostInput.value.trim();
      postState({ cameras: state.cameras });
    };
    hostRow.appendChild(hostInput);
    card.appendChild(hostRow);

    const btns = document.createElement('div');
    btns.className = 'btns';
    const prevBtn = document.createElement('button');
    prevBtn.textContent = previewOn[cam.id] ? '⏸ プレビュー停止' : '▶ プレビュー';
    prevBtn.onclick = () => {
      previewOn[cam.id] = !previewOn[cam.id];
      renderCameras();
    };
    btns.appendChild(prevBtn);

    const ovrBtn = document.createElement('button');
    ovrBtn.textContent = '🔒 このカメラに固定';
    ovrBtn.onclick = () => postCommand({ type: 'setCameraOverride', camera: cam.id });
    if (state?.control?.cameraOverride === cam.id) ovrBtn.classList.add('active');
    btns.appendChild(ovrBtn);
    card.appendChild(btns);

    wrap.appendChild(card);
  }
}

// heartbeat の activeCamera（DisplayName）と cameras[].sourceId を緩く突き合わせてバッジ表示。
// "Phone 01" vs "Phone01" の空白差を吸収する。
function renderActiveBadges(st) {
  const norm = (s) => (s || '').replace(/\s+/g, '').toLowerCase();
  const active = norm(st.activeCamera);
  document.querySelectorAll('.camera-card').forEach((card) => {
    const cam = (state?.cameras || []).find((c) => c.id === card.dataset.cam);
    const badge = card.querySelector('.cam-active-badge');
    if (!cam || !badge) return;
    badge.style.display = active && norm(cam.sourceId) === active ? '' : 'none';
  });
}

// ---- cue 一覧 ---------------------------------------------------------------
function renderCues() {
  const wrap = $('cueList');
  wrap.innerHTML = '';
  const cues = state?.cues || [];
  if (cues.length === 0) {
    wrap.innerHTML = '<p class="hint">cue がまだ無い。コンポジット検証タブで素材とマスクを作って登録する（T5）。</p>';
    return;
  }
  for (const cue of cues) {
    const row = document.createElement('div');
    row.className = 'cue-row';
    const playing = state?.control?.activeCue === cue.id;
    if (playing) row.classList.add('playing');

    const label = document.createElement('span');
    label.className = 'cue-label';
    label.textContent = `${cue.name || cue.id}${cue.camera ? `（カメラ ${cue.camera}）` : ''}`;
    row.appendChild(label);

    const btn = document.createElement('button');
    btn.textContent = playing ? '⏹ 停止' : '▶ 発火';
    btn.className = playing ? '' : 'accent';
    btn.onclick = () => postCommand(playing ? { type: 'stopCue' } : { type: 'playCue', id: cue.id });
    row.appendChild(btn);

    wrap.appendChild(row);
  }
}

// ---- ポスト FX --------------------------------------------------------------
const POST_IDS = {
  exposure: 'conExposure', contrast: 'conContrast', saturation: 'conSaturation',
  temperature: 'conTemperature', vignette: 'conVignette', grain: 'conGrain',
  scanline: 'conScanline',
};
let postDebounce = 0;
let syncingSliders = false;

function setupPostSliders() {
  for (const [key, id] of Object.entries(POST_IDS)) {
    const el = $(id);
    el.oninput = () => {
      el.parentElement.querySelector('span').textContent = el.value;
      if (syncingSliders) return;
      clearTimeout(postDebounce);
      postDebounce = setTimeout(() => {
        const post = {};
        for (const [k, i] of Object.entries(POST_IDS)) post[k] = parseFloat($(i).value);
        postCommand({ type: 'setPost', post });
      }, 120); // ドラッグ中の連打を抑制
    };
  }
}

function syncPostSliders() {
  const post = state?.post || {};
  syncingSliders = true;
  for (const [key, id] of Object.entries(POST_IDS)) {
    if (key in post) {
      const el = $(id);
      el.value = post[key];
      el.parentElement.querySelector('span').textContent = String(post[key]);
    }
  }
  syncingSliders = false;
}

// ---- 起動 -------------------------------------------------------------------
setupTabs();
setupPostSliders();
$('ovrAuto').onclick = () => postCommand({ type: 'setCameraOverride', camera: null });
$('cueStopAll').onclick = () => postCommand({ type: 'stopCue' });
$('consoleRefresh').onclick = async () => {
  try {
    state = await (await fetch('/state')).json();
    renderCameras(); renderCues(); syncPostSliders();
  } catch (e) { /* サーバ未接続 */ }
};
pollStateLoop();
pollUnityLoop();
