// 🎛 オペレータ卓コンソール。show.json（サーバ）を正として Unity を遠隔制御する。
//   - state: GET /state?rev=N の long-poll で常時同期（他クライアントの変更も反映）
//   - 操作: POST /command（playCue / stopCue / setCameraOverride / setPost）
//   - Unity 状態: GET /unity/status を 2s ポーリング
// コンポジット検証タブ（main.js）とは独立して動く。

const $ = (id) => document.getElementById(id);

let state = null;          // 最新の show.json
let unityAlive = false;
let lastUnity = {};        // 直近の /unity/status の status（appliedRev / playingCue / activeIndex）
let previewOn = {};        // camId -> bool（MJPEG サムネは明示 ON。スマホ負荷への配慮）

// MJPEG ストリームはメインポート+1 から取る（capture-server.py が両方 listen）。
// 同一ポートだとブラウザの同時接続上限（6/origin）を張りっぱなしストリームが食い潰し、
// /state long-poll や他カメラの接続が詰まる＝「接続が不安定」の正体。
function streamBase() {
  const p = (parseInt(location.port, 10) || 80) + 1;
  return `${location.protocol}//${location.hostname}:${p}`;
}

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
      lastUnity = st;
      // 反映同期表示: Unity が報告した appliedRev と show.json の rev を突き合わせる
      let sync = '';
      if (unityAlive && state && typeof st.appliedRev === 'number') {
        sync = st.appliedRev >= state.rev ? ' / ✓ 反映済み' : ` / ⏳ 同期中 (${st.appliedRev}/${state.rev})`;
      }
      $('unityAlive').className = 'unity-dot ' + (unityAlive ? 'on' : 'off');
      $('unityInfo').textContent = unityAlive
        ? `Unity: ${st.activeCamera || '?'} / ${(st.recvFps || 0).toFixed(1)}fps`
          + (st.playingCue ? ` / 🎬 ${st.playingCue}` : '')
          + (st.cameraOverride ? ` / 🔒 override=${st.cameraOverride}` : '')
          + (st.simulator ? '（仮想）' : '') + sync
        : 'Unity: 未接続';
      renderActiveBadges(st);
      renderCues(); // ✓ / ⚠ 表示は Unity 状態に依存するため毎回更新（入力欄が無いので安全）
    } catch (e) {
      unityAlive = false;
      lastUnity = {};
      $('unityAlive').className = 'unity-dot off';
      $('unityInfo').textContent = 'サーバ未接続';
    }
    await new Promise((res) => setTimeout(res, 2000));
  }
}

// heartbeat の activeIndex（優先）/ activeCamera 名（フォールバック）から
// アクティブなカメラの id（A/B/C）を引く。
function activeCameraId() {
  const cams = state?.cameras || [];
  if (typeof lastUnity.activeIndex === 'number' && lastUnity.activeIndex >= 0
      && lastUnity.activeIndex < cams.length) {
    return cams[lastUnity.activeIndex].id;
  }
  const norm = (s) => (s || '').replace(/\s+/g, '').toLowerCase();
  const active = norm(lastUnity.activeCamera);
  return cams.find((c) => norm(c.sourceId) === active)?.id || null;
}

// ---- カメラカード ----------------------------------------------------------
// 差分更新方式。innerHTML 全再構築だと state 更新のたびに <img> が作り直され、
// 全カメラの MJPEG が同時再接続（=全サムネが一斉に暗転）するため、
// カードは 1 度だけ生成し、以後は値とストリーム URL の変化分のみ反映する。
const camCards = new Map(); // camId -> { card, img, refs..., streamKey, retryTimer }

function camStreamKey(cam) {
  return previewOn[cam.id] && cam.host
    ? `${cam.host}|${cam.port || 8080}|${cam.auth || ''}` : '';
}

function buildCameraCard(cam) {
  const card = document.createElement('div');
  card.className = 'camera-card';
  card.dataset.cam = cam.id;

  const head = document.createElement('div');
  head.className = 'cam-head';
  head.innerHTML = `<b>カメラ ${cam.id}</b> <span class="cam-src"></span>`
    + `<span class="cam-active-badge" style="display:none">● ACTIVE</span>`;
  card.appendChild(head);

  const thumb = document.createElement('div');
  thumb.className = 'cam-thumb';
  const img = document.createElement('img');
  img.alt = '';
  thumb.appendChild(img);
  card.appendChild(thumb);

  const refs = {
    card, img, cam,
    srcSpan: head.querySelector('.cam-src'),
    streamKey: '', retryTimer: null,
  };

  // ストリーム切断時は 5s 後に自動リトライ（プレビュー ON のままなら）
  img.addEventListener('error', () => {
    if (!refs.streamKey) return;
    clearTimeout(refs.retryTimer);
    refs.retryTimer = setTimeout(() => {
      if (refs.streamKey) connectThumb(refs);
    }, 5000);
  });

  const hostRow = document.createElement('div');
  hostRow.className = 'btns';
  const mkInput = (props, onchange) => {
    const el = document.createElement('input');
    el.type = 'text';
    Object.assign(el, props);
    if (props.width) { el.style.width = props.width; el.style.flex = 'none'; }
    el.onchange = onchange;
    hostRow.appendChild(el);
    return el;
  };
  refs.hostInput = mkInput({ placeholder: 'スマホ IP（サムネ用）' }, () => {
    refs.cam.host = refs.hostInput.value.trim();
    postState({ cameras: state.cameras });
  });
  refs.portInput = mkInput(
    { placeholder: 'port', title: 'ポート（streamer=8080 / IP Camera Lite=8081）', width: '52px' },
    () => {
      refs.cam.port = parseInt(refs.portInput.value, 10) || 8080;
      postState({ cameras: state.cameras });
    });
  refs.authInput = mkInput(
    { placeholder: 'user:pass', title: 'Basic 認証（IP Camera Lite は admin:admin。空=認証なし）', width: '86px' },
    () => {
      refs.cam.auth = refs.authInput.value.trim();
      postState({ cameras: state.cameras });
    });
  card.appendChild(hostRow);

  const btns = document.createElement('div');
  btns.className = 'btns';
  refs.prevBtn = document.createElement('button');
  refs.prevBtn.onclick = () => {
    previewOn[cam.id] = !previewOn[cam.id];
    renderCameras();
  };
  btns.appendChild(refs.prevBtn);

  refs.ovrBtn = document.createElement('button');
  refs.ovrBtn.textContent = '🔒 このカメラに固定';
  refs.ovrBtn.onclick = () => postCommand({ type: 'setCameraOverride', camera: refs.cam.id });
  btns.appendChild(refs.ovrBtn);
  card.appendChild(btns);

  return refs;
}

function connectThumb(refs) {
  const cam = refs.cam;
  refs.img.src = `${streamBase()}/cam?host=${encodeURIComponent(cam.host)}`
    + `&port=${cam.port || 8080}&path=/video`
    + (cam.auth ? `&auth=${encodeURIComponent(cam.auth)}` : '') + `&t=${Date.now()}`;
}

function renderCameras() {
  const wrap = $('cameraCards');
  const cams = state?.cameras || [];
  const ids = new Set(cams.map((c) => c.id));

  for (const [id, refs] of camCards) {
    if (!ids.has(id)) {
      clearTimeout(refs.retryTimer);
      refs.card.remove();
      camCards.delete(id);
    }
  }

  for (const cam of cams) {
    let refs = camCards.get(cam.id);
    if (!refs) {
      refs = buildCameraCard(cam);
      camCards.set(cam.id, refs);
      wrap.appendChild(refs.card);
    }
    refs.cam = cam; // state 再取得でオブジェクトが差し替わるため毎回更新

    refs.srcSpan.textContent = cam.sourceId || '';
    // 編集中の入力欄は上書きしない（入力中の値が吹っ飛ぶのを防ぐ）
    const setVal = (el, v) => { if (document.activeElement !== el) el.value = v; };
    setVal(refs.hostInput, cam.host || '');
    setVal(refs.portInput, cam.port || 8080);
    setVal(refs.authInput, cam.auth || '');

    refs.prevBtn.textContent = previewOn[cam.id] ? '⏸ プレビュー停止' : '▶ プレビュー';
    refs.ovrBtn.classList.toggle('active', state?.control?.cameraOverride === cam.id);

    // ストリームは接続パラメータが変わった時だけ張り替える
    const key = camStreamKey(cam);
    if (key !== refs.streamKey) {
      refs.streamKey = key;
      clearTimeout(refs.retryTimer);
      if (key) connectThumb(refs);
      else refs.img.removeAttribute('src');
    }
  }
}

function renderActiveBadges(st) {
  const activeId = activeCameraId();
  document.querySelectorAll('.camera-card').forEach((card) => {
    const badge = card.querySelector('.cam-active-badge');
    if (badge) badge.style.display = unityAlive && card.dataset.cam === activeId ? '' : 'none';
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
  const activeId = activeCameraId();
  for (const cue of cues) {
    const row = document.createElement('div');
    row.className = 'cue-row';
    const playing = state?.control?.activeCue === cue.id;
    if (playing) row.classList.add('playing');

    const label = document.createElement('span');
    label.className = 'cue-label';
    label.textContent = `${cue.name || cue.id}${cue.camera ? `（カメラ ${cue.camera}）` : ''}`;
    row.appendChild(label);

    // Unity 反映確認: heartbeat の playingCue がこの cue になっていれば ✓
    if (playing && unityAlive) {
      const mark = document.createElement('span');
      const applied = lastUnity.playingCue === cue.id;
      mark.className = 'cue-mark ' + (applied ? 'ok' : 'wait');
      mark.textContent = applied ? '✓ Unity 反映' : '⏳ 送信済み…';
      row.appendChild(mark);
    }

    // カメラ不一致警告: cue が想定するカメラと現在のアクティブが違う
    if (cue.camera && unityAlive && activeId && cue.camera !== activeId) {
      const warn = document.createElement('span');
      warn.className = 'cue-warn';
      warn.textContent = `⚠ 現在 ${activeId} 表示中`;
      warn.title = `この cue はカメラ ${cue.camera} 用。今発火すると位置のズレた絵が出る。`;
      row.appendChild(warn);
    }

    const btn = document.createElement('button');
    btn.textContent = playing ? '⏹ 停止' : '▶ 発火';
    btn.className = 'cue-fire ' + (playing ? '' : 'accent');
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
$('postReset').onclick = () => postCommand({
  type: 'setPost',
  post: { exposure: 0, contrast: 1, saturation: 1, temperature: 0, vignette: 0.25, grain: 0.06, scanline: 0 },
});
$('consoleRefresh').onclick = async () => {
  try {
    state = await (await fetch('/state')).json();
    renderCameras(); renderCues(); syncPostSliders();
  } catch (e) { /* サーバ未接続 */ }
};
pollStateLoop();
pollUnityLoop();
