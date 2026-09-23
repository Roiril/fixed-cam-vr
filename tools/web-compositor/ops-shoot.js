// 設営と点検画面へ埋め込む当日撮影コンソール。DOM と I/O だけを持つ。
import {
  SHOTS, loadShots, shotName, shotStatus, neededHeadSec, cutCount, headWindow,
  countdownSecOf,
} from './shoot-model.js';
import {
  normalizeShootCapabilities, capabilityGate, recordingGate, takeName,
  selectNewDeviceTake, beginRecordingContext, pullRequestFor, localTakesForShot,
  uncollectedDeviceTakes, recordingSettings, recordingObservation,
} from './ops-shoot-model.js';

const wait = (ms) => new Promise(resolve => setTimeout(resolve, ms));

async function request(path, { method = 'GET', body, timeout = 20_000 } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  try {
    const response = await fetch(path, {
      method, signal: controller.signal, cache: method === 'GET' ? 'no-store' : 'default',
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
    let payload = null;
    try { payload = await response.json(); } catch { /* HTTP エラーを下で説明する */ }
    if (!response.ok) throw new Error(payload?.detail || payload?.error || `応答エラー ${response.status}`);
    return payload;
  } finally { clearTimeout(timer); }
}

const text = (tag, className, value) => {
  const node = document.createElement(tag); node.className = className; node.textContent = value; return node;
};

const formatSeconds = (value) => Number.isFinite(Number(value)) && Number(value) > 0
  ? `${Number(value).toFixed(1)} 秒` : '未確認';
const formatResolution = (take) => take?.width && take?.height ? `${take.width} × ${take.height}` : '未確認';
const formatCollected = (value) => {
  const date = new Date(value || '');
  return Number.isFinite(date.getTime())
    ? `回収 ${date.toLocaleString('ja-JP', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })}`
    : '回収時刻は未確認';
};

export function mountShoot(root, { demo = false, onAdopt = () => {} } = {}) {
  if (!root) throw new Error('撮影 UI の取付先がありません。');
  root.classList.add('studio-shoot-root');
  root.innerHTML = `
    <div class="studio-shoot">
      <header class="studio-shoot-header">
        <div><p class="eyebrow">当日の素材撮影</p><h2>1. 人形視点を撮影して確認する</h2></div>
        <button class="shoot-refresh" type="button">接続を確認</button>
      </header>
      <p class="shoot-intro">主な作業はスマホ側です。撮影する人がスマホで構図と演技を決めて撮ります。この PC から録画を始めることもできます。</p>
      <div class="shoot-capability" role="status">この卓で使える操作を確認しています。</div>
      <div class="studio-shoot-layout">
        <aside class="shoot-shot-list" aria-label="撮影するショット"></aside>
        <section class="shoot-stage" aria-live="polite">
          <div class="shoot-shot-summary">
            <p class="shoot-cue"></p><h3 class="shoot-title">ショットを読み込んでいます</h3>
            <p class="shoot-direction"></p><p class="shoot-requirement"></p>
          </div>
          <div class="shoot-columns">
            <section class="shoot-capture-panel">
              <h4>スマホで構図を決めて撮る</h4>
              <p class="shoot-help">端末を選びます。ライブ画像は必要なときだけ開きます。</p>
              <div class="shoot-fields">
                <label>端末 <select class="shoot-device"></select></label>
                <label>待ち時間 <input class="shoot-countdown" type="number" min="0" max="10" step="1"> 秒</label>
                <label>録画時間 <input class="shoot-duration" type="number" min="1" max="600" step="1"> 秒</label>
              </div>
              <p class="shoot-device-state"></p>
              <div class="shoot-actions">
                <button class="shoot-record" type="button">録画を開始</button>
                <button class="shoot-live-toggle" type="button" aria-expanded="false">ライブ画像を開く</button>
              </div>
              <p class="shoot-action-message" role="status"></p>
              <figure class="shoot-live" hidden><img alt="選択した端末の加工前のライブ画像"><figcaption>加工前のライブ画像です。構図と演技の確認に使います。</figcaption></figure>
              <section class="shoot-device-takes"><h4>端末に残っている未回収テイク</h4><div class="shoot-device-take-list"></div></section>
            </section>
            <section class="shoot-review-panel">
              <h4>回収したテイクを試写して採用する</h4>
              <video class="shoot-preview" controls muted playsinline preload="metadata"></video>
              <p class="shoot-preview-message">採用中の素材かテイクを選ぶと、ここで試写できます。</p>
              <div class="shoot-adopt-controls">
                <label>頭を送る <input class="shoot-in-point" type="number" min="0" max="60" step="0.05" value="0"> 秒</label>
                <button class="shoot-adopt" type="button">この位置から採用</button>
              </div>
              <p class="shoot-help">頭を送ると別ファイルを作って採用します。元のテイクは残ります。</p>
              <div class="shoot-local-takes"></div>
            </section>
          </div>
        </section>
      </div>
    </div>`;

  const q = selector => root.querySelector(selector);
  const ui = {
    list: q('.shoot-shot-list'), capability: q('.shoot-capability'), cue: q('.shoot-cue'),
    title: q('.shoot-title'), direction: q('.shoot-direction'), requirement: q('.shoot-requirement'),
    device: q('.shoot-device'), countdown: q('.shoot-countdown'), duration: q('.shoot-duration'),
    deviceState: q('.shoot-device-state'), record: q('.shoot-record'), liveButton: q('.shoot-live-toggle'),
    live: q('.shoot-live'), liveImage: q('.shoot-live img'), action: q('.shoot-action-message'),
    remote: q('.shoot-device-take-list'), preview: q('.shoot-preview'),
    previewMessage: q('.shoot-preview-message'), inPoint: q('.shoot-in-point'),
    adopt: q('.shoot-adopt'), local: q('.shoot-local-takes'), refresh: q('.shoot-refresh'),
  };

  let active = !root.hidden;
  let capabilities = normalizeShootCapabilities(null);
  let show = null, devices = [], devicesFetchedAt = 0, takes = { device: [], local: [] };
  let remoteHost = '';
  let currentShot = null, currentTake = null, actionBusy = false, refreshBusy = false;
  let generation = 0, pollTimer = 0, recordingContext = null, recording = false;
  let ready = false, pendingCueId = '', deviceBusy = false;

  const segments = () => show?.timeline?.segments || [];
  const cueFor = cueId => (show?.cues || []).find(cue => cue.id === cueId) || null;
  const currentCueId = () => shotName(currentShot);
  const deviceForCurrent = () => devices.find(device => device.host === ui.device.value) || devices[0] || null;

  function setMessage(message, kind = '') {
    ui.action.textContent = message || '';
    ui.action.dataset.state = kind;
  }

  function closeLive() {
    ui.liveImage.removeAttribute('src'); ui.live.hidden = true;
    ui.liveButton.textContent = 'ライブ画像を開く'; ui.liveButton.setAttribute('aria-expanded', 'false');
  }

  function pauseMedia() {
    ui.preview.pause(); closeLive();
  }

  function localFor(cueId = currentCueId()) { return localTakesForShot(takes.local, cueId); }
  function statusFor(shot) {
    const local = localTakesForShot(takes.local, shotName(shot));
    const assets = new Set(local.map(take => take.url));
    const source = cueFor(shotName(shot))?.sourceUrl;
    if (source) assets.add(source);
    return shotStatus(shot, show, segments(), assets, local);
  }

  function renderCapabilities(error = '') {
    const disabled = [];
    if (!capabilities.shooting) disabled.push('録画');
    if (!capabilities.shooting) disabled.push('回収');
    if (!capabilities.adoption) disabled.push('採用');
    const modeName = { preparation: '準備', inspection: '点検', authoring: '素材編集' }[capabilities.mode] || '';
    ui.capability.textContent = error || (disabled.length
      ? `${disabled.join('・')}はこの卓では使えません。素材の閲覧と試写はできます。`
      : `準備用の卓です。録画・回収・採用を利用できます。${modeName ? ` 現在は${modeName}用です。` : ''}`);
    ui.capability.dataset.state = disabled.length || error ? 'warning' : 'ok';
  }

  function renderShotList() {
    ui.list.replaceChildren(...SHOTS.map((shot, index) => {
      const status = show ? statusFor(shot) : null;
      const button = text('button', 'shoot-shot', ''); button.type = 'button';
      button.dataset.selected = shot === currentShot ? 'true' : 'false';
      button.disabled = recording || actionBusy;
      button.append(text('span', 'shoot-shot-number', String(index + 1).padStart(2, '0')),
        text('span', 'shoot-shot-name', shot.label || shot.cueId),
        text('span', 'shoot-shot-meta', `${shot.cueId} / ${!status ? '確認中' : status.level === 'ok' ? '採用済み' : status.level === 'warn' ? '要確認' : '未採用'}`));
      button.addEventListener('click', () => selectShotInternal(shot.cueId));
      return button;
    }));
  }

  function renderDevices() {
    const keep = ui.device.value;
    ui.device.replaceChildren(...devices.map(device => {
      const option = document.createElement('option'); option.value = device.host;
      option.textContent = `${device.id || '?'} — ${device.host}`; return option;
    }));
    if (devices.some(device => device.host === keep)) ui.device.value = keep;
    const device = deviceForCurrent();
    const gate = recordingGate(device, devicesFetchedAt, Date.now(), capabilities);
    const identity = device?.identityChecked
      ? (device.identityOk ? '担当と登録番号は一致' : '担当または登録番号が不一致')
      : '担当と登録番号はこの画面では未確認';
    ui.deviceState.textContent = device
      ? `${device.reachable ? '端末へ接続済み' : '端末へ接続できません'} / ${device.canList ? '録画回収に対応' : '録画回収は未対応'} / ${identity}`
      : '録画する端末が見つかりません。';
    ui.deviceState.dataset.state = gate.ok ? 'ok' : 'warning';
    ui.record.disabled = demo || actionBusy || (!recording && !gate.ok);
    ui.record.title = demo ? '表示例では実際の録画をしません。' : recording ? '' : gate.reason;
    ui.device.disabled = recording || actionBusy || !devices.length;
    ui.liveButton.disabled = demo || !device?.reachable || actionBusy;
  }

  function updatePreviewNote() {
    if (!currentTake) return;
    const window = headWindow(neededHeadSec(segments(), currentCueId()), Number(ui.inPoint.value));
    ui.previewMessage.textContent = `実機で使う範囲は ${window.start.toFixed(2)}〜${window.end.toFixed(2)} 秒です。再生操作で内容を確認してください。`;
  }

  function seekPreviewToStart() {
    if (!currentTake || ui.preview.readyState < 1) return;
    const window = headWindow(neededHeadSec(segments(), currentCueId()), Number(ui.inPoint.value));
    ui.preview.currentTime = Math.min(window.start, Number.isFinite(ui.preview.duration)
      ? Math.max(0, ui.preview.duration - 0.01) : window.start);
  }

  function previewTake(take) {
    currentTake = take || null;
    const nextUrl = currentTake?.url || '';
    if (!nextUrl) {
      ui.preview.pause(); ui.preview.removeAttribute('src'); ui.preview.load();
      ui.previewMessage.textContent = '採用中の素材かテイクを選ぶと、ここで試写できます。';
      renderActions(); return;
    }
    if (ui.preview.dataset.url !== nextUrl) {
      ui.preview.pause(); ui.preview.dataset.url = nextUrl; ui.preview.src = nextUrl; ui.preview.load();
    }
    updatePreviewNote(); seekPreviewToStart(); renderActions();
  }

  function renderActions() {
    const adoptGate = capabilityGate(capabilities, 'adopt');
    ui.adopt.disabled = demo || actionBusy || !currentTake || !adoptGate.ok;
    ui.adopt.title = demo ? '表示例では採用しません。' : adoptGate.reason;
  }

  function takeCard(take, adopted) {
    const card = document.createElement('article'); card.className = 'shoot-take';
    card.dataset.adopted = adopted ? 'true' : 'false';
    const heading = text('div', 'shoot-take-heading', '');
    heading.append(text('span', 'shoot-take-name', adopted ? `採用中 / ${take.name || takeName(take)}` : take.name || takeName(take)),
      text('span', 'shoot-take-duration', formatSeconds(Number(take.durSec))));
    const meta = text('p', 'shoot-take-meta', `${formatResolution(take)} / ${formatCollected(take.capturedAt)}`);
    const actions = text('div', 'shoot-take-actions', '');
    const preview = text('button', '', '試写'); preview.type = 'button';
    preview.addEventListener('click', () => previewTake(take)); actions.append(preview);
    if (!adopted) {
      const adopt = text('button', '', '採用'); adopt.type = 'button';
      const gate = capabilityGate(capabilities, 'adopt'); adopt.disabled = demo || actionBusy || !gate.ok;
      adopt.title = gate.reason; adopt.addEventListener('click', () => adoptTake(take)); actions.append(adopt);
    }
    card.append(heading, meta, actions); return card;
  }

  function renderTakes() {
    const cueId = currentCueId(), local = localFor(cueId), source = cueFor(cueId)?.sourceUrl || '';
    const wasOpen = ui.local.querySelector('.shoot-candidate-details')?.open === true;
    const adopted = local.find(take => take.url === source) || (source ? {
      url: source, name: source.split('/').pop() || source,
    } : null);
    const candidates = local.filter(take => take.url !== source);
    const localNodes = [];
    if (adopted) localNodes.push(takeCard(adopted, true));
    else if (!candidates.length) localNodes.push(text('p', 'shoot-empty', 'このショットの回収済みテイクはありません。'));
    if (candidates.length) {
      const details = document.createElement('details'); details.className = 'shoot-candidate-details';
      details.open = wasOpen;
      details.append(text('summary', '', `回収済みの候補 ${candidates.length} 本`));
      const body = text('div', 'shoot-candidate-list', '');
      body.append(...candidates.map(take => takeCard(take, false))); details.append(body); localNodes.push(details);
    }
    ui.local.replaceChildren(...localNodes);
    const remoteTakes = remoteHost && remoteHost === deviceForCurrent()?.host ? takes.device : [];
    const remote = uncollectedDeviceTakes(remoteTakes, takes.local, cueId);
    ui.remote.replaceChildren(...(remote.length ? remote.map(take => {
      const row = document.createElement('article'); row.className = 'shoot-device-take';
      row.append(text('span', 'shoot-device-take-name', takeName(take)),
        text('span', 'shoot-device-take-meta', `${formatSeconds(Number(take.durSec))} / ${formatResolution(take)}`));
      const button = text('button', '', 'このテイクを回収'); button.type = 'button';
      const gate = capabilityGate(capabilities, 'pull'); button.disabled = demo || actionBusy || !gate.ok;
      button.title = gate.reason; button.addEventListener('click', () => pullListedTake(take)); row.append(button); return row;
    }) : [text('p', 'shoot-empty', 'このショットに合う未回収テイクはありません。')]));
    const displayed = adopted ? [adopted, ...candidates] : candidates;
    if ((!currentTake || !displayed.some(take => take.url === currentTake.url)) && adopted) previewTake(adopted);
    else renderActions();
  }

  function renderShot() {
    if (!currentShot) return;
    const status = show ? statusFor(currentShot) : null;
    ui.cue.textContent = currentShot.cueId;
    ui.title.textContent = currentShot.label;
    ui.direction.textContent = (currentShot.hint || '撮影指示はありません。').replaceAll('**', '');
    ui.requirement.textContent = !status ? '必要尺と採用状態を確認しています。' : [
      status.need == null ? '現在の台本では必要尺なし' : `必要尺 ${status.need.toFixed(1)} 秒`,
      `${cutCount(segments(), currentShot.cueId)} カットで使用`,
      status.level === 'ok' ? '採用済み' : status.reason || '未採用',
    ].join(' / ');
    renderShotList(); renderDevices(); renderTakes();
  }

  function mergeIdentity(rows, statusPayload, statusFetched) {
    const fresh = statusPayload?.ok === true && statusFetched &&
      Number.isFinite(Number(statusPayload.observedAt)) && Date.now() / 1000 - Number(statusPayload.observedAt) <= 20;
    return rows.map(device => {
      const camera = (statusPayload?.cameras || []).find(item => item.id === device.id || item.host === device.host);
      if (!camera) return device;
      const observed = fresh && (camera.httpOk === true || !!camera.observedUuid);
      return { ...device, identityChecked: observed, identityOk: observed ? camera.identityOk === true : undefined };
    });
  }

  async function fetchTakesFor(device) {
    const query = device ? `?host=${encodeURIComponent(device.host)}&port=${encodeURIComponent(device.port || 8080)}` : '';
    const payload = await request(`/shoot/takes${query}`);
    return { device: payload?.device || [], local: payload?.local || [] };
  }

  function demoState() {
    return {
      show: { cues: SHOTS.map((shot, index) => ({ id: shot.cueId, sourceUrl: index ? '' : `/recordings/${shot.cueId}_example.mp4` })), timeline: { segments: [] } },
      devices: [{ id: 'A', host: '192.168.10.21', port: 8080, reachable: true, canList: true, identityChecked: true, identityOk: true }],
      takes: { device: [{ name: 'pov_1_t02.mp4', shot: 'pov_1', durSec: 3, width: 640, height: 480 }], local: [{ name: 'pov_0_example.mp4', url: '/recordings/pov_0_example.mp4', shot: 'pov_0', durSec: 2, width: 640, height: 480, capturedAt: new Date().toISOString() }] },
    };
  }

  async function refresh() {
    if (!active || !ready || refreshBusy) return false;
    refreshBusy = true; const token = ++generation; const selected = currentCueId();
    ui.refresh.disabled = true; setMessage('撮影素材と端末を確認しています。');
    try {
      if (demo) {
        const sample = demoState(); show = sample.show; devices = sample.devices; takes = sample.takes; devicesFetchedAt = Date.now();
        remoteHost = sample.devices[0]?.host || '';
      } else {
        // 回収済み素材は端末調査を待たずに表示する。/shoot/devices はオフライン時に
        // 3 台 × status/health の timeout を待つため、下の別処理で更新する。
        const [nextShow, localPayload] = await Promise.all([
          request('/state'), fetchTakesFor(null), request('/shoot/manifest'),
        ]);
        if (!active || token !== generation || (selected && selected !== currentCueId())) return false;
        show = nextShow;
        takes = { device: takes.device, local: localPayload.local };
      }
      if (!currentShot) currentShot = SHOTS[0] || null;
      renderShot();
      setMessage(demo ? '表示例を表示しています。' : '回収済み素材を表示しました。端末の接続を確認しています。', 'ok');
      if (!demo) refreshDevicesOnly();
      return true;
    } catch (error) {
      if (active && token === generation) setMessage(`状態を更新できません。${error.name === 'AbortError' ? '応答がありませんでした。' : error.message}`, 'error');
      return false;
    } finally {
      refreshBusy = false; ui.refresh.disabled = false;
      if (active && token !== generation) setTimeout(refresh, 0);
    }
  }

  async function refreshDevicesOnly() {
    if (!active || demo || recording || deviceBusy) return;
    deviceBusy = true;
    const token = generation;
    try {
      const [payload, opsResult] = await Promise.all([request('/shoot/devices'), request('/ops/status').catch(() => null)]);
      if (!active || token !== generation) return;
      devicesFetchedAt = Date.now(); devices = mergeIdentity(payload?.items || [], opsResult, !!opsResult);
      renderDevices();
      const selectedDevice = deviceForCurrent();
      if (selectedDevice) {
        const remotePayload = await fetchTakesFor(selectedDevice);
        if (!active || token !== generation || selectedDevice.host !== deviceForCurrent()?.host) return;
        takes = { device: remotePayload.device, local: takes.local };
        remoteHost = selectedDevice.host;
        renderTakes();
      }
      setMessage('回収済み素材と端末の状態を更新しました。', 'ok');
    } catch {
      devicesFetchedAt = 0; renderDevices();
      setMessage('端末の接続確認を完了できません。回収済み素材の閲覧と試写はできます。', 'warning');
    } finally {
      deviceBusy = false;
      if (active && token !== generation) setTimeout(refreshDevicesOnly, 0);
    }
  }

  async function selectShotInternal(cueId) {
    if (!SHOTS.length) { pendingCueId = cueId; return false; }
    if (recording || actionBusy) return false;
    const shot = SHOTS.find(item => item.cueId === cueId); if (!shot) return false;
    generation++; currentShot = shot; currentTake = null; ui.preview.removeAttribute('data-url');
    ui.inPoint.value = '0';
    ui.duration.value = String(currentShot.recSec ?? 3);
    ui.countdown.value = String(countdownSecOf(currentShot));
    ui.preview.pause(); ui.preview.removeAttribute('src'); ui.preview.load(); closeLive(); renderShot();
    await refresh(); return true;
  }

  async function pullWithContext(context, take) {
    const body = pullRequestFor(context, take); if (!body) throw new Error('録画したショットとテイクが一致しません。');
    const response = await request('/shoot/pull', { method: 'POST', body, timeout: 130_000 });
    if (!response?.ok) throw new Error(response?.detail || '回収できませんでした。');
    return response;
  }

  async function findAndPullNewTake(context) {
    const latest = await fetchTakesFor(context.device);
    const fresh = selectNewDeviceTake(context.beforeNames, latest.device, context.cueId);
    if (!fresh) throw new Error('録画後に増えたテイクを確認できません。端末側の一覧を確認してください。');
    return pullWithContext(context, fresh);
  }

  async function startRecording() {
    if (demo || actionBusy || recording) return;
    const device = deviceForCurrent(), gate = recordingGate(device, devicesFetchedAt, Date.now(), capabilities);
    if (!gate.ok) return setMessage(gate.reason, 'error');
    const settings = recordingSettings(ui.duration.value, ui.countdown.value);
    if (!settings.ok) return setMessage(settings.reason, 'error');
    actionBusy = true; renderShot(); setMessage('端末の録画前一覧を確認しています。');
    try {
      const before = await fetchTakesFor(device);
      recordingContext = beginRecordingContext({ shot: currentShot, device, deviceTakes: before.device, token: Date.now() });
      const { maxSec, countdownSec } = settings;
      const response = await request('/shoot/start', { method: 'POST', body: {
        host: device.host, port: device.port, shot: recordingContext.cueId, maxSec, countdownSec,
      } });
      if (!response?.ok || !response?.result?.ok) throw new Error(response?.detail || '端末で録画を開始できませんでした。');
      recording = true; ui.record.textContent = '録画を停止'; setMessage(countdownSec ? `構えるまで ${countdownSec} 秒です。` : '録画を開始しました。', 'ok');
      monitorRecording(recordingContext, maxSec, countdownSec);
    } catch (error) {
      recordingContext = null; setMessage(error.message, 'error');
    } finally { actionBusy = false; renderShot(); }
  }

  async function finishRecording(context, manual = false) {
    if (!context || actionBusy) return;
    actionBusy = true; setMessage('新しいテイクを確認して回収しています。');
    let stopped = !manual, failure = '', success = '';
    try {
      if (manual) {
        const response = await request('/shoot/stop', { method: 'POST', body: context.device, timeout: 25_000 });
        if (!response?.ok || response?.result?.ok === false)
          throw new Error(response?.detail || response?.result?.detail || '端末の停止を確認できませんでした。');
        stopped = true;
      }
      const response = await findAndPullNewTake(context);
      success = `回収しました。${formatSeconds(Number(response.entry?.durSec))} / ${formatResolution(response.entry)}`;
    } catch (error) { failure = error.message; }
    finally {
      if (stopped && recordingContext?.token === context.token) { recording = false; recordingContext = null; }
      actionBusy = false; ui.record.textContent = '録画を開始'; renderShot(); await refresh();
      if (!stopped) {
        ui.record.textContent = '録画を停止';
        setMessage(`${failure || '端末の停止を確認できませんでした。'} 録画状態は未確認です。停止操作を続けてください。`, 'error');
      } else if (failure) {
        setMessage(`録画は停止しましたが、テイクを回収できません。${failure} 端末側の未回収テイクから回収してください。`, 'error');
      } else if (success) setMessage(success, 'ok');
    }
  }

  async function monitorRecording(context, maxSec, countdownSec) {
    let observedActive = false;
    const expectedEnd = context.startedAt + (maxSec + countdownSec) * 1000;
    const deadline = expectedEnd + 10_000;
    while (recording && recordingContext?.token === context.token) {
      await wait(active ? 450 : 1_000);
      if (!active || !recording || recordingContext?.token !== context.token) continue;
      try {
        const payload = await request(`/shoot/devices?host=${encodeURIComponent(context.device.host)}`, { timeout: 8_000 });
        const device = (payload?.items || []).find(item => item.host === context.device.host);
        const observation = recordingObservation(device, {
          observedActive, expectedElapsed: Date.now() >= expectedEnd,
        });
        if (observation === 'unknown') {
          setMessage('端末に届かないため、停止したか確認できません。接続を戻すか停止操作を続けてください。', 'warning');
          continue;
        }
        if (observation === 'active') {
          observedActive = true;
          setMessage(device.counting ? `構えるまで ${device.countdownLeft ?? '—'} 秒です。` : '録画中です。', 'ok');
          continue;
        }
        if (actionBusy) continue;
        if (observation === 'stopped') return finishRecording(context, false);
      } catch {
        setMessage('録画状態を確認できません。端末を見て、必要なら停止してください。', 'warning');
        if (Date.now() >= deadline) return;
      }
    }
  }

  async function pullListedTake(take) {
    if (demo || actionBusy) return;
    const gate = capabilityGate(capabilities, 'pull'); if (!gate.ok) return setMessage(gate.reason, 'error');
    const context = beginRecordingContext({ shot: currentShot, device: deviceForCurrent(), deviceTakes: [] });
    actionBusy = true; renderShot(); setMessage('端末から回収しています。');
    try { await pullWithContext(context, take); setMessage('テイクを回収しました。', 'ok'); }
    catch (error) { setMessage(error.message, 'error'); }
    finally { actionBusy = false; renderShot(); await refresh(); }
  }

  async function adoptTake(take = currentTake) {
    if (demo || actionBusy || !take) return;
    const gate = capabilityGate(capabilities, 'adopt'); if (!gate.ok) return setMessage(gate.reason, 'error');
    const cueId = currentCueId(), url = take.url, inPointSec = Math.max(0, Number(ui.inPoint.value) || 0);
    actionBusy = true; renderShot(); setMessage('素材を採用しています。');
    try {
      const response = await request('/shoot/adopt', { method: 'POST', body: { cueId, url, inPointSec }, timeout: 30_000 });
      if (!response?.ok) throw new Error(response?.detail || '採用できませんでした。');
      setMessage('素材を採用しました。試写結果を確認してください。', 'ok');
      onAdopt({ cueId, url: response.url || url, response });
    } catch (error) { setMessage(error.message, 'error'); }
    finally { actionBusy = false; renderShot(); await refresh(); }
  }

  function openLive() {
    if (!ui.live.hidden) return closeLive();
    const device = deviceForCurrent(); if (!device?.reachable) return;
    const url = new URL('/cam', location.href);
    url.port = String(Number(location.port || 80) + 1);
    url.search = new URLSearchParams({ host: device.host, port: String(device.port || 8080), path: '/video' });
    ui.liveImage.src = url.href; ui.live.hidden = false;
    ui.liveButton.textContent = 'ライブ画像を閉じる'; ui.liveButton.setAttribute('aria-expanded', 'true');
  }

  ui.refresh.addEventListener('click', refresh);
  ui.device.addEventListener('change', () => {
    generation++; takes = { device: [], local: takes.local }; remoteHost = '';
    closeLive(); renderTakes(); refresh();
  });
  ui.record.addEventListener('click', () => recording ? finishRecording(recordingContext, true) : startRecording());
  ui.liveButton.addEventListener('click', openLive);
  ui.adopt.addEventListener('click', () => adoptTake());
  ui.inPoint.addEventListener('input', () => {
    updatePreviewNote(); seekPreviewToStart();
  });
  ui.preview.addEventListener('loadedmetadata', seekPreviewToStart);
  ui.preview.addEventListener('timeupdate', () => {
    if (!currentTake || ui.preview.seeking) return;
    const window = headWindow(neededHeadSec(segments(), currentCueId()), Number(ui.inPoint.value));
    if (ui.preview.currentTime + 0.02 < window.end) return;
    const keepPlaying = !ui.preview.paused && !ui.preview.ended;
    ui.preview.currentTime = window.start;
    if (keepPlaying) ui.preview.play().catch(() => {});
  });
  ui.preview.addEventListener('ended', () => {
    if (!currentTake) return;
    const window = headWindow(neededHeadSec(segments(), currentCueId()), Number(ui.inPoint.value));
    ui.preview.currentTime = window.start;
  });
  ui.preview.addEventListener('error', () => { ui.previewMessage.textContent = '試写映像を読み込めません。ファイルの存在と形式を確認してください。'; });
  ui.liveImage.addEventListener('error', () => { closeLive(); setMessage('ライブ画像を開けません。端末の接続を確認してください。', 'error'); });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) pauseMedia();
    else if (active) refresh();
  });

  async function bootstrap() {
    try {
      capabilities = demo
        ? normalizeShootCapabilities({ mode: 'preparation', shooting: true, adoption: true, capture: true, authoring: true })
        : normalizeShootCapabilities(await request('/ops/capabilities'));
      renderCapabilities();
    } catch (error) {
      capabilities = normalizeShootCapabilities({});
      renderCapabilities(`操作の対応状況を確認できません。録画・回収・採用は停止しています。${error.message}`);
    }
    try { if (!SHOTS.length) await loadShots(); }
    catch (error) { setMessage(`ショット定義を読み込めません。${error.message}`, 'error'); return; }
    currentShot = SHOTS.find(shot => shot.cueId === pendingCueId) || currentShot || SHOTS[0] || null;
    pendingCueId = '';
    if (currentShot) {
      ui.inPoint.value = '0';
      ui.duration.value = String(currentShot.recSec ?? 3);
      ui.countdown.value = String(countdownSecOf(currentShot));
    }
    ready = true; renderShot();
    if (active) await refresh();
  }

  const interval = () => {
    clearInterval(pollTimer);
    if (active) pollTimer = setInterval(() => {
      // 応答待ちが長くても、前回の接続結果を操作可のまま残さない。
      renderDevices();
      refreshDevicesOnly();
    }, 4_000);
  };

  function setActive(next) {
    const value = !!next; if (active === value) return;
    active = value; generation++;
    if (!active) pauseMedia();
    else refresh();
    interval();
  }

  interval(); bootstrap();
  return { setActive, refresh, selectShot: selectShotInternal };
}
