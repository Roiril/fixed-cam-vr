// 人形視点の撮影・回収・採用。録画の帰属と操作可否の判定は ../ops-shoot-model.js と ../shoot-model.js（node:test あり）。
import { h, setText, when, liveUrl } from './ui.js';
import {
  SHOTS, loadShots, shotName, shotStatus, neededHeadSec, cutCount, headWindow, countdownSecOf,
} from '../shoot-model.js';
import {
  normalizeShootCapabilities, capabilityGate, recordingGate, takeName, selectNewDeviceTake, beginRecordingContext,
  pullRequestFor, localTakesForShot, uncollectedDeviceTakes, recordingSettings, recordingObservation,
} from '../ops-shoot-model.js';

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const secondsText = value => (Number.isFinite(Number(value)) && Number(value) > 0 ? `${Number(value).toFixed(1)} 秒` : '長さ不明');
const sizeText = take => (take?.width && take?.height ? `${take.width}×${take.height}` : '大きさ不明');

export function mountShoot(root, ctx) {
  const list = h('div.pick-list', { role: 'group', 'aria-label': '撮るもの' });
  const title = h('h2'), direction = h('p.direction'), requirement = h('p.note');
  const device = h('select', { 'aria-label': '端末' });
  const countdown = h('input', { type: 'number', min: 0, max: 10, step: 1, inputMode: 'numeric' });
  const duration = h('input', { type: 'number', min: 1, max: 600, step: 1, inputMode: 'numeric' });
  const deviceState = h('p.message');
  const record = h('button.button.primary', { type: 'button', text: '録画を始める' });
  const liveButton = h('button.button', { type: 'button', text: '構図を見る', 'aria-pressed': 'false' });
  const message = h('p.message', { role: 'status' });
  const liveImage = h('img', { alt: '選んだ端末の加工前の映像' });
  const liveBox = h('figure.media-box', { hidden: true }, liveImage);
  const remote = h('div');
  const preview = h('video.media', { controls: true, muted: true, playsInline: true, preload: 'metadata' });
  const previewNote = h('p.note');
  const inPoint = h('input', { type: 'number', min: 0, max: 60, step: 0.05, value: 0, inputMode: 'decimal' });
  const adopt = h('button.button.primary', { type: 'button', text: 'この位置から採用' });
  const local = h('div');
  const refreshButton = h('button.button.small', { type: 'button', text: '端末を調べ直す' });

  root.append(h('div.shoot-layout', {}, list,
    h('div.stage', {},
      h('header.stage-head', {}, title, direction, requirement),
      h('div.columns', {},
        h('section.panel', {}, h('h3', { text: '撮る' }),
          h('p.note', { text: 'スマホを手に持って撮ります。スマホの撮影パネルからでも、ここからでも録画を始められます。' }),
          h('div.fields', {}, h('label.field', {}, '端末', device), h('label.field', {}, '構える時間（秒）', countdown), h('label.field', {}, '撮る長さ（秒）', duration)),
          deviceState, h('div.actions', {}, record, liveButton, refreshButton), message, liveBox,
          h('h3', { text: '端末に残っている、まだ回収していないもの' }), remote),
        h('section.panel', {}, h('h3', { text: '見て採用する' }), preview, previewNote,
          h('div.fields', {}, h('label.field', {}, '頭を送る（秒）', inPoint), adopt),
          h('p.note', { text: '頭を送ると、別のファイルを作って採用します。元の録画は残ります。' }), local)))));

  let active = false, ready = false, pending = '';
  let capabilities = normalizeShootCapabilities(null), capabilitiesKey = 'null';
  let show = null, devices = [], devicesAt = 0, takes = { device: [], local: [] }, remoteHost = '';
  let shot = null, take = null, actionBusy = false, refreshBusy = false, deviceBusy = false;
  let generation = 0, recording = false, context = null, pollTimer = 0;

  const segments = () => show?.timeline?.segments || [];
  const cueFor = cueId => (show?.cues || []).find(cue => cue.id === cueId) || null;
  const cueId = () => shotName(shot);
  const currentDevice = () => devices.find(d => d.host === device.value) || devices[0] || null;
  const say = (text, state = '') => { setText(message, text || ''); message.dataset.state = state; };

  function closeLive() {
    liveImage.removeAttribute('src'); liveBox.hidden = true;
    setText(liveButton, '構図を見る'); liveButton.setAttribute('aria-pressed', 'false');
  }
  function pauseMedia() { preview.pause(); closeLive(); }
  function statusOf(item) {
    const mine = localTakesForShot(takes.local, shotName(item));
    const assets = new Set(mine.map(t => t.url));
    const source = cueFor(shotName(item))?.sourceUrl;
    if (source) assets.add(source);
    return shotStatus(item, show, segments(), assets, mine);
  }

  function renderList() {
    list.replaceChildren(...SHOTS.map((item, index) => {
      const status = show ? statusOf(item) : null;
      const meta = h('span.pick-meta', { text: !status ? '確認中…' : status.level === 'ok' ? '採用済み' : status.level === 'warn' ? `要確認: ${status.reason || ''}` : 'まだ' });
      meta.dataset.state = !status ? '' : status.level === 'ok' ? 'ok' : status.level === 'warn' ? 'warning' : '';
      const button = h('button.pick', { type: 'button', 'aria-pressed': String(item === shot), disabled: recording || actionBusy },
        h('span.pick-no', { text: String(index + 1).padStart(2, '0'), 'aria-hidden': 'true' }), h('span.pick-name', { text: item.label || item.cueId }), meta);
      button.addEventListener('click', () => { location.hash = `#shoot/pov/${item.cueId}`; });
      return button;
    }));
  }

  function renderDevices() {
    const keep = device.value;
    device.replaceChildren(...devices.map(d => h('option', { value: d.host, text: `${d.id || '?'}（${d.host}）` })));
    if (devices.some(d => d.host === keep)) device.value = keep;
    const current = currentDevice();
    const gate = recordingGate(current, devicesAt, Date.now(), capabilities);
    setText(deviceState, !current ? '録画できる端末が見つかりません。'
      : `${current.reachable ? '端末に届きます' : '端末に届きません'}／${current.identityChecked ? (current.identityOk ? '担当は登録どおり' : '担当が登録と違います') : '担当は未確認'}`);
    deviceState.dataset.state = gate.ok ? 'ok' : 'warning';
    record.disabled = ctx.demo || actionBusy || (!recording && !gate.ok);
    record.title = ctx.demo ? '見本では録画しません。' : recording ? '' : gate.reason;
    device.disabled = recording || actionBusy || !devices.length;
    liveButton.disabled = ctx.demo || !current?.reachable || actionBusy;
  }

  const window_ = () => headWindow(neededHeadSec(segments(), cueId()), Number(inPoint.value));
  function seekStart() {
    if (!take || preview.readyState < 1) return;
    const start = window_().start;
    preview.currentTime = Math.min(start, Number.isFinite(preview.duration) ? Math.max(0, preview.duration - 0.01) : start);
  }
  function renderAdopt() {
    const gate = capabilityGate(capabilities, 'adopt');
    adopt.disabled = ctx.demo || actionBusy || !take || !gate.ok;
    adopt.title = ctx.demo ? '見本では採用しません。' : gate.reason;
  }
  function watch(next) {
    take = next || null;
    const url = take?.url || '';
    if (ctx.demo) { setText(previewNote, '見本では録画を読みません。'); return renderAdopt(); }
    if (!url) {
      preview.pause(); preview.removeAttribute('src'); delete preview.dataset.url; preview.load();
      setText(previewNote, '採用中の素材か、回収した録画を選ぶと、ここで見られます。');
      return renderAdopt();
    }
    if (preview.dataset.url !== url) { preview.pause(); preview.dataset.url = url; preview.src = url; preview.load(); }
    const range = window_();
    setText(previewNote, `実機で使うのは ${range.start.toFixed(2)}〜${range.end.toFixed(2)} 秒です。再生して中身を見ます。`);
    seekStart(); renderAdopt();
  }

  function takeRow(item, adopted) {
    const row = h('div.take', { dataset: { adopted: String(adopted) } },
      h('div', {}, h('p.take-name', { text: `${adopted ? '採用中　' : ''}${item.name || takeName(item)}` }),
        h('p.take-meta', { text: `${secondsText(item.durSec)}／${sizeText(item)}／${item.capturedAt ? `${when(item.capturedAt)} に回収` : '回収した日時は不明'}` })),
      h('div.actions', {}));
    const actions = row.querySelector('.actions');
    actions.append(h('button.button.small', { type: 'button', text: '見る', onclick: () => watch(item) }));
    if (!adopted) {
      const gate = capabilityGate(capabilities, 'adopt');
      actions.append(h('button.button.small', { type: 'button', text: '採用', disabled: ctx.demo || actionBusy || !gate.ok, title: gate.reason, onclick: () => adoptTake(item) }));
    }
    return row;
  }

  function renderTakes() {
    const id = cueId(), mine = localTakesForShot(takes.local, id), source = cueFor(id)?.sourceUrl || '';
    const wasOpen = local.querySelector('details')?.open === true;
    const adopted = mine.find(t => t.url === source) || (source ? { url: source, name: source.split('/').pop() || source } : null);
    const candidates = mine.filter(t => t.url !== source);
    const nodes = [];
    if (adopted) nodes.push(takeRow(adopted, true));
    else if (!candidates.length) nodes.push(h('p.empty', { text: 'この場面の録画は、まだ卓にありません。' }));
    if (candidates.length) nodes.push(h('details', { open: wasOpen }, h('summary', { text: `回収済みのほかの録画 ${candidates.length} 本` }), ...candidates.map(t => takeRow(t, false))));
    local.replaceChildren(...nodes);
    const onDevice = remoteHost && remoteHost === currentDevice()?.host ? takes.device : [];
    const waiting = uncollectedDeviceTakes(onDevice, takes.local, id);
    const gate = capabilityGate(capabilities, 'pull');
    remote.replaceChildren(...(waiting.length ? waiting.map(item => h('div.take', {},
      h('div', {}, h('p.take-name', { text: takeName(item) }), h('p.take-meta', { text: `${secondsText(item.durSec)}／${sizeText(item)}` })),
      h('button.button.small', { type: 'button', text: '卓へ回収', disabled: ctx.demo || actionBusy || !gate.ok, title: gate.reason, onclick: () => pullListed(item) })))
      : [h('p.empty', { text: 'ありません。' })]));
    const shown = adopted ? [adopted, ...candidates] : candidates;
    if ((!take || !shown.some(t => t.url === take.url)) && adopted) watch(adopted);
    else renderAdopt();
  }

  function render() {
    if (!shot) return;
    const status = show ? statusOf(shot) : null;
    setText(title, shot.label);
    setText(direction, (shot.hint || '').replaceAll('**', ''));
    setText(requirement, !status ? '必要な長さと採用の状態を確認しています…' : [
      status.need == null ? 'いまの台本では使いません' : `画に出るのは頭の ${status.need.toFixed(1)} 秒`,
      `${cutCount(segments(), shot.cueId)} か所で使用`, status.level === 'ok' ? '採用済み' : status.reason || 'まだ'].join('／'));
    renderList(); renderDevices(); renderTakes();
  }

  function mergeIdentity(rows, status) {
    const fresh = status?.ok === true && Number.isFinite(Number(status.observedAt)) && Date.now() / 1000 - Number(status.observedAt) <= 20;
    return rows.map(row => {
      const camera = (status?.cameras || []).find(c => c.id === row.id || c.host === row.host);
      if (!camera) return row;
      const observed = fresh && (camera.httpOk === true || !!camera.observedUuid);
      return { ...row, identityChecked: observed, identityOk: observed ? camera.identityOk === true : undefined };
    });
  }
  async function fetchTakes(target) {
    const query = target ? `?host=${encodeURIComponent(target.host)}&port=${encodeURIComponent(target.port || 8080)}` : '';
    const payload = await ctx.request(`/shoot/takes${query}`, { timeout: 20_000 });
    return { device: payload?.device || [], local: payload?.local || [] };
  }

  async function refresh() {
    if (!active || !ready || refreshBusy) return;
    refreshBusy = true; const token = ++generation, selected = cueId();
    try {
      if (ctx.demo) {
        show = { cues: SHOTS.map((s, i) => ({ id: s.cueId, sourceUrl: i < 3 ? `/recordings/${s.cueId}_example.mp4` : '' })), timeline: { segments: [] } };
        devices = [{ id: 'P', host: '192.168.10.29', port: 8080, reachable: true, canList: true, identityChecked: true, identityOk: true }];
        takes = { device: [], local: [] }; devicesAt = Date.now();
      } else {
        // 回収済みの録画は、端末の調査（届かない台を待つ）より先に出す。
        const [nextShow, localPayload] = await Promise.all([ctx.request('/state', { timeout: 20_000 }), fetchTakes(null)]);
        if (!active || token !== generation || (selected && selected !== cueId())) return;
        show = nextShow; takes = { device: takes.device, local: localPayload.local };
      }
      if (!shot) shot = SHOTS[0] || null;
      render();
      if (!ctx.demo) refreshDevices();
    } catch (error) {
      if (active && token === generation) say(`状態を読めません。${error.message}`, 'error');
    } finally {
      refreshBusy = false;
      if (active && token !== generation) setTimeout(refresh, 0);
    }
  }

  async function refreshDevices() {
    if (!active || ctx.demo || recording || deviceBusy) return;
    deviceBusy = true; const token = generation;
    try {
      const [payload, status] = await Promise.all([ctx.request('/shoot/devices', { timeout: 20_000 }), ctx.request('/ops/status').catch(() => null)]);
      if (!active || token !== generation) return;
      devicesAt = Date.now(); devices = mergeIdentity(payload?.items || [], status);
      renderDevices();
      const selected = currentDevice();
      if (selected) {
        const onDevice = await fetchTakes(selected);
        if (!active || token !== generation || selected.host !== currentDevice()?.host) return;
        takes = { device: onDevice.device, local: takes.local }; remoteHost = selected.host;
        renderTakes();
      }
    } catch {
      devicesAt = 0; renderDevices();
      say('端末を調べ切れませんでした。回収済みの録画は見られます。', 'warning');
    } finally {
      deviceBusy = false;
      if (active && token !== generation) setTimeout(refreshDevices, 0);
    }
  }

  function select(id) {
    if (!SHOTS.length) { pending = id; return; }
    if (recording || actionBusy) return;
    const next = SHOTS.find(s => s.cueId === id) || SHOTS[0];
    if (!next || next === shot) return;
    generation++; shot = next; take = null; delete preview.dataset.url;
    inPoint.value = '0'; duration.value = String(shot.recSec ?? 3); countdown.value = String(countdownSecOf(shot));
    preview.pause(); preview.removeAttribute('src'); preview.load(); closeLive(); say('');
    render(); refresh();
  }

  async function pullWith(ctxRecording, item) {
    const body = pullRequestFor(ctxRecording, item);
    if (!body) throw new Error('録画した場面と、端末の録画が一致しません。');
    const response = await ctx.request('/shoot/pull', { method: 'POST', body, timeout: 130_000 });
    if (!response?.ok) throw new Error(response?.detail || '回収できませんでした。');
    return response;
  }

  async function start() {
    if (ctx.demo || actionBusy || recording) return;
    const target = currentDevice(), gate = recordingGate(target, devicesAt, Date.now(), capabilities);
    if (!gate.ok) return say(gate.reason, 'error');
    const settings = recordingSettings(duration.value, countdown.value);
    if (!settings.ok) return say(settings.reason, 'error');
    actionBusy = true; render(); say('録画の前に、端末の一覧を控えています…');
    try {
      const before = await fetchTakes(target);
      context = beginRecordingContext({ shot, device: target, deviceTakes: before.device, token: Date.now() });
      const response = await ctx.request('/shoot/start', { method: 'POST', timeout: 20_000,
        body: { host: target.host, port: target.port, shot: context.cueId, maxSec: settings.maxSec, countdownSec: settings.countdownSec } });
      if (!response?.ok || !response?.result?.ok) throw new Error(response?.detail || '端末で録画を始められませんでした。');
      recording = true; setText(record, '録画を止める');
      say(settings.countdownSec ? `構えるまで ${settings.countdownSec} 秒です。` : '録画を始めました。', 'ok');
      monitor(context, settings.maxSec, settings.countdownSec);
    } catch (error) { context = null; say(error.message, 'error'); }
    finally { actionBusy = false; render(); }
  }

  async function finish(ctxRecording, manual = false) {
    if (!ctxRecording || actionBusy) return;
    actionBusy = true; say('新しい録画を探して、卓へ回収しています…');
    let stopped = !manual, failure = '', success = '';
    try {
      if (manual) {
        const response = await ctx.request('/shoot/stop', { method: 'POST', body: ctxRecording.device, timeout: 25_000 });
        if (!response?.ok || response?.result?.ok === false) throw new Error(response?.detail || response?.result?.detail || '端末が止まったことを確かめられませんでした。');
        stopped = true;
      }
      const latest = await fetchTakes(ctxRecording.device);
      const fresh = selectNewDeviceTake(ctxRecording.beforeNames, latest.device, ctxRecording.cueId);
      if (!fresh) throw new Error('録画のあとで増えた録画が見つかりません。端末側の一覧を見てください。');
      const response = await pullWith(ctxRecording, fresh);
      success = `回収しました。${secondsText(response.entry?.durSec)}／${sizeText(response.entry)}`;
    } catch (error) { failure = error.message; }
    finally {
      if (stopped && context?.token === ctxRecording.token) { recording = false; context = null; }
      actionBusy = false; setText(record, recording ? '録画を止める' : '録画を始める'); render(); await refresh();
      if (!stopped) say(`${failure || '端末が止まったことを確かめられませんでした。'} 録画の状態は分かりません。もう一度止めてください。`, 'error');
      else if (failure) say(`録画は止まりましたが、回収できませんでした。${failure} 下の「まだ回収していないもの」から回収します。`, 'error');
      else if (success) say(success, 'ok');
    }
  }

  async function monitor(ctxRecording, maxSec, countdownSec) {
    let seenActive = false;
    const expectedEnd = ctxRecording.startedAt + (maxSec + countdownSec) * 1000, deadline = expectedEnd + 10_000;
    while (recording && context?.token === ctxRecording.token) {
      await wait(active ? 450 : 1000);
      if (!active || !recording || context?.token !== ctxRecording.token) continue;
      try {
        const payload = await ctx.request(`/shoot/devices?host=${encodeURIComponent(ctxRecording.device.host)}`, { timeout: 8000 });
        const row = (payload?.items || []).find(item => item.host === ctxRecording.device.host);
        const observed = recordingObservation(row, { observedActive: seenActive, expectedElapsed: Date.now() >= expectedEnd });
        if (observed === 'unknown') { say('端末に届かないので、止まったか分かりません。接続を戻すか、止める操作を続けてください。', 'warning'); continue; }
        if (observed === 'active') { seenActive = true; say(row.counting ? `構えるまで ${row.countdownLeft ?? '—'} 秒です。` : '録画中です。', 'ok'); continue; }
        if (actionBusy) continue;
        if (observed === 'stopped') return finish(ctxRecording, false);
      } catch {
        say('録画の状態を読めません。端末を見て、必要なら止めてください。', 'warning');
        if (Date.now() >= deadline) return;
      }
    }
  }

  async function pullListed(item) {
    if (ctx.demo || actionBusy) return;
    const gate = capabilityGate(capabilities, 'pull');
    if (!gate.ok) return say(gate.reason, 'error');
    const once = beginRecordingContext({ shot, device: currentDevice(), deviceTakes: [] });
    actionBusy = true; render(); say('端末から回収しています…');
    try { await pullWith(once, item); say('回収しました。', 'ok'); }
    catch (error) { say(error.message, 'error'); }
    finally { actionBusy = false; render(); await refresh(); }
  }

  async function adoptTake(item = take) {
    if (ctx.demo || actionBusy || !item) return;
    const gate = capabilityGate(capabilities, 'adopt');
    if (!gate.ok) return say(gate.reason, 'error');
    actionBusy = true; render(); say('採用しています…');
    try {
      const response = await ctx.request('/shoot/adopt', { method: 'POST', timeout: 30_000,
        body: { cueId: cueId(), url: item.url, inPointSec: Math.max(0, Number(inPoint.value) || 0) } });
      if (!response?.ok) throw new Error(response?.detail || '採用できませんでした。');
      say('採用しました。実機に出すには、焼き直して Quest に入れます。', 'ok');
      ctx.materialsChanged();
    } catch (error) { say(error.message, 'error'); }
    finally { actionBusy = false; render(); await refresh(); }
  }

  refreshButton.addEventListener('click', () => { refresh(); });
  device.addEventListener('change', () => { generation++; takes = { device: [], local: takes.local }; remoteHost = ''; closeLive(); renderTakes(); refresh(); });
  record.addEventListener('click', () => (recording ? finish(context, true) : start()));
  liveButton.addEventListener('click', () => {
    if (!liveBox.hidden) return closeLive();
    const target = currentDevice(); if (!target?.reachable) return;
    liveImage.src = liveUrl(target.host, target.port || 8080); liveBox.hidden = false;
    setText(liveButton, '構図を閉じる'); liveButton.setAttribute('aria-pressed', 'true');
  });
  liveImage.addEventListener('error', () => { if (!liveBox.hidden) { closeLive(); say('映像を開けません。端末の接続を見てください。', 'error'); } });
  adopt.addEventListener('click', () => adoptTake());
  inPoint.addEventListener('input', () => { if (take) watch(take); });
  preview.addEventListener('loadedmetadata', seekStart);
  preview.addEventListener('timeupdate', () => {
    if (!take || preview.seeking) return;
    const range = window_();
    if (preview.currentTime + 0.02 < range.end) return;
    const playing = !preview.paused && !preview.ended;
    preview.currentTime = range.start;
    if (playing) preview.play().catch(() => {});
  });
  preview.addEventListener('ended', () => { if (take) preview.currentTime = window_().start; });
  preview.addEventListener('error', () => { if (preview.dataset.url) setText(previewNote, '録画を読めません。ファイルがあるかと形式を確かめます。'); });
  document.addEventListener('visibilitychange', () => { if (document.hidden) pauseMedia(); else if (active) refresh(); });

  async function boot() {
    try { if (!SHOTS.length) await loadShots(); }
    catch (error) { say(`撮るものの一覧を読めません。${error.message}`, 'error'); return; }
    shot = SHOTS.find(s => s.cueId === pending) || SHOTS[0] || null; pending = '';
    if (shot) { inPoint.value = '0'; duration.value = String(shot.recSec ?? 3); countdown.value = String(countdownSecOf(shot)); }
    ready = true; render();
    if (active) refresh();
  }
  boot();

  return {
    select,
    setCapabilities(value) {
      // 本体は毎秒呼ぶ。変わったときだけ描き直す（描き直すと端末の選択と再生中の録画が戻る）。
      const key = JSON.stringify(value || null);
      if (key === capabilitiesKey) return;
      capabilitiesKey = key; capabilities = normalizeShootCapabilities(value);
      if (ready) render();
    },
    setActive(next) {
      if (active === !!next) return;
      active = !!next; generation++;
      clearInterval(pollTimer);
      if (!active) return pauseMedia();
      refresh();
      // 応答待ちが長くても、前の接続結果を「操作できる」のまま残さない。
      pollTimer = setInterval(() => { renderDevices(); refreshDevices(); }, 4000);
    },
  };
}
