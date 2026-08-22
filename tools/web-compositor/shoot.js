// 撮影コンソール（当日の素材撮り）の配線。**判定は shoot-model.js が持つ**（node テストあり）。
// ここは DOM と I/O だけ。
//
// 流れ: ショットを選ぶ → ⏺（端末が自動停止）→ 卓が新テイクを回収 → 頭の窓だけ試写 → 採用。

import {
  SHOTS, shotName, shotStatus, neededHeadSec, cutCount, headWindow, adoptName,
} from './shoot-model.js';

const $ = (s) => document.querySelector(s);
const api = async (path, body) => {
  const r = await fetch(path, body
    ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
    : undefined);
  return r.json();
};

let state = null;          // show.json
let devices = [];          // /shoot/devices
let takes = { device: [], local: [] };
let manifest = {};
let cur = SHOTS[0];        // 選択中のショット
let curTake = null;        // 試写しているテイク

const segments = () => (state && state.timeline && state.timeline.segments) || [];
const cueOf = (id) => ((state && state.cues) || []).find((c) => c.id === id) || null;

// ---- 端末 -------------------------------------------------------------------
async function loadDevices() {
  const r = await api('/shoot/devices');
  devices = (r && r.items) || [];
  renderDevices();
  syncDevSelect();
}

function renderDevices() {
  const el = $('#devices');
  if (!devices.length) { el.textContent = '配信端末が見つからない（show.json の host が空）'; return; }
  el.innerHTML = devices.map((d) => {
    const bits = [];
    if (!d.reachable) bits.push('<span class="sh-ng">届かない</span>');
    else if (d.recording) bits.push('<span class="sh-warn">録画中</span>');
    else if (d.counting) bits.push(`<span class="sh-warn">構えて ${d.countdownLeft}</span>`);
    else bits.push('<span class="sh-ok">待機</span>');
    // v0.9.0 以前は新しい録画 API を持たない。当日ここに気づかないと 1 本も撮れない。
    if (d.reachable && !d.canList) bits.push('<span class="sh-ng">旧アプリ（要 v0.10.0）</span>');
    if (d.throttleStage > 0) bits.push(`<span class="sh-warn">熱で画質低下 ${d.throttleStage}</span>`);
    return `<span><b>${d.id || d.host}</b> ${bits.join(' ')}</span>`;
  }).join('');
}

function devFor(shot) {
  // 偽ライブはそのカメラの端末で撮る（画角・粗さ・色が定義上一致する）。
  if (shot.dev === 'cam') {
    const cue = cueOf(shotName(shot));
    const camId = (cue && cue.camera) || 'C';
    return devices.find((d) => d.id === camId) || devices[0] || null;
  }
  return devices.find((d) => d.host === $('#devSel').value) || devices[0] || null;
}

function syncDevSelect() {
  const sel = $('#devSel');
  const keep = sel.value;
  sel.innerHTML = devices.map((d) =>
    `<option value="${d.host}">${d.id || '?'} — ${d.host}</option>`).join('');
  const wanted = cur.dev === 'cam' ? (devFor(cur) || {}).host : keep;
  if (wanted) sel.value = wanted;
  sel.disabled = cur.dev === 'cam';   // 偽ライブは端末を選べない（その場所のカメラで撮る）
}

// ---- ショット ---------------------------------------------------------------
function renderShotList() {
  $('#shotList').innerHTML = SHOTS.map((s, i) => {
    const st = statusOf(s);
    const mark = st.level === 'ok' ? '✅' : st.level === 'warn' ? '⚠' : '—';
    const used = cutCount(segments(), shotName(s));
    return `<button class="sh-shot ${s === cur ? 'on' : ''}" data-i="${i}">
      <span class="mark">${mark}</span>
      <span class="n">${s.label}</span>
      <span class="s">${shotName(s)}${used ? ` ・ ${used} カットで使う` : ' ・ 未使用'}</span>
    </button>`;
  }).join('');
  for (const b of document.querySelectorAll('.sh-shot')) {
    b.onclick = () => { cur = SHOTS[+b.dataset.i]; curTake = null; selectShot(); };
  }
}

function localTakesFor(shot) {
  const name = shotName(shot);
  return takes.local.filter((t) => t.shot === name || (t.name || '').startsWith(name + '_t'));
}

function statusOf(shot) {
  const assets = new Set(takes.local.map((t) => t.url));
  const cue = cueOf(shotName(shot));
  if (cue && cue.sourceUrl) assets.add(cue.sourceUrl);   // 素材一覧に無い経路で入れた素材も許す
  return shotStatus(shot, state, segments(), assets, localTakesFor(shot));
}

function selectShot() {
  $('#shotTitle').textContent = cur.label;
  $('#shotHint').innerHTML = cur.hint.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');
  $('#maxSec').value = cur.recSec;
  $('#countSec').value = cur.dev === 'pov' ? 3 : 0;   // 手持ちだけ構える時間が要る
  syncDevSelect();
  renderShotList();
  renderState();
  renderTakes();
  connectLive();
}

function renderState() {
  const st = statusOf(cur);
  const need = st.need;
  const parts = [];
  parts.push(need === null
    ? '<span class="sh-warn">この素材を使うカットが台本に無い</span>'
    : `画に出るのは頭 <b>${need.toFixed(1)}秒</b>（${st.cuts} カットで使う）`);
  if (st.level === 'ok') parts.push('<span class="sh-ok">採用済み</span>');
  else if (st.level === 'warn') parts.push(`<span class="sh-warn">${st.reason} — ${st.fix}</span>`);
  else parts.push(`<span class="sh-ng">${st.reason} — ${st.fix}</span>`);
  $('#shotState').innerHTML = parts.join(' ・ ');
}

// ---- ライブ -----------------------------------------------------------------
function connectLive() {
  const d = devFor(cur);
  const img = $('#live');
  if (!d) { img.removeAttribute('src'); return; }
  const url = `/cam?host=${encodeURIComponent(d.host)}&port=${d.port}&path=/video`;
  if (img.dataset.url !== url) { img.dataset.url = url; img.src = url; }
}

// ---- 撮る -------------------------------------------------------------------
let recording = false;

async function toggleRecord() {
  const d = devFor(cur);
  if (!d) return;
  if (recording) {
    $('#recState').textContent = '止めています…';
    const r = await api('/shoot/stop', { host: d.host, port: d.port });
    recording = false;
    syncRecBtn();
    const name = ((r || {}).result || {}).file || '';
    if (name) await pullTake(d, name);
    else $('#recState').textContent = '保存されなかった（フレームが来ていない可能性）';
    await refreshTakes();
    return;
  }
  const maxSec = Number($('#maxSec').value) || 0;
  const countdownSec = Number($('#countSec').value) || 0;
  const r = await api('/shoot/start', {
    host: d.host, port: d.port, shot: shotName(cur), maxSec, countdownSec,
  });
  if (!r || !r.ok || !(r.result && r.result.ok)) {
    $('#recState').innerHTML = '<span class="sh-ng">開始できなかった'
      + '（旧アプリ / 空き容量 / 既に録画中）</span>';
    return;
  }
  recording = true;
  syncRecBtn();
  if (maxSec > 0) waitAutoStop(d, maxSec, countdownSec);
}

function syncRecBtn() {
  const b = $('#recBtn');
  b.textContent = recording ? '⏹ 止める' : '⏺ 撮る';
  b.classList.toggle('rec', recording);
}

/** 自動停止を待って、止まったら回収する。**卓が数えるのではなく端末の状態を見る**。 */
async function waitAutoStop(d, maxSec, countdownSec) {
  const deadline = Date.now() + (maxSec + countdownSec + 8) * 1000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 400));
    if (!recording) return;                     // 手で止めた
    // ⚠ **この端末だけ**を見る。全台を叩くと 1 巡に数秒かかり、
    //   カウントダウンや自動停止の瞬間を見逃す（実測で 2 秒の待ちが 1 度も観測できなかった）。
    const st = await api(`/shoot/devices?host=${encodeURIComponent(d.host)}`);
    const cd = ((st || {}).items || []).find((x) => x.host === d.host);
    if (!cd) continue;
    if (cd.counting) { $('#recState').textContent = `構えて ${cd.countdownLeft}…`; continue; }
    if (cd.recording) { $('#recState').textContent = '撮っています…'; continue; }
    // 端末が自分で止めた（指定の尺に到達）
    recording = false;
    syncRecBtn();
    await refreshTakes();
    const latest = takes.device[0];
    if (latest) await pullTake(d, latest.name);
    await refreshTakes();
    return;
  }
  recording = false;
  syncRecBtn();
  $('#recState').innerHTML = '<span class="sh-warn">止まったか分からない（端末を見る）</span>';
}

async function pullTake(d, name) {
  $('#recState').textContent = '回収しています…';
  const r = await api('/shoot/pull', { host: d.host, port: d.port, name, shot: shotName(cur) });
  if (!r || !r.ok) {
    $('#recState').innerHTML = `<span class="sh-ng">回収できなかった: ${(r || {}).detail || ''}</span>`;
    return null;
  }
  const e = r.entry || {};
  const bits = [`${(e.durSec || 0).toFixed(1)}秒`, e.codec || '?', e.pixFmt || '?',
                `${e.width || '?'}x${e.height || '?'}`];
  $('#recState').innerHTML = `<span class="sh-ok">回収した</span> <span class="sh-note">${bits.join(' / ')}</span>`;
  return r.url;
}

// ---- テイク -----------------------------------------------------------------
async function refreshTakes() {
  const d = devFor(cur);
  const q = d ? `?host=${encodeURIComponent(d.host)}&port=${d.port}` : '';
  const r = await api('/shoot/takes' + q);
  takes = { device: (r && r.device) || [], local: (r && r.local) || [] };
  const m = await api('/shoot/manifest');
  manifest = (m && m.items) || {};
  renderTakes();
  renderShotList();
  renderState();
}

function renderTakes() {
  const list = localTakesFor(cur);
  const cue = cueOf(shotName(cur));
  const src = (cue && cue.sourceUrl) || '';
  const need = neededHeadSec(segments(), shotName(cur));
  const body = list.map((t) => {
    const dur = Number(t.durSec) || 0;
    const short = need !== null && dur > 0 && dur < need;
    return `<tr class="${t.url === src ? 'adopted' : ''}" data-url="${t.url}">
      <td>${t.url === src ? '採用中' : ''}</td>
      <td>${(t.name || '').replace(/\.mp4$/, '')}</td>
      <td class="${short ? 'sh-ng' : ''}">${dur ? dur.toFixed(1) + '秒' : '?'}</td>
      <td>${t.codec || '?'} / ${t.pixFmt || '?'}</td>
      <td>${(t.capturedAt || '').replace('T', ' ')}</td>
      <td><button data-act="prev">試写</button> ${t.url === src
        ? '<button data-act="unadopt">採用をやめる</button>'
        : '<button data-act="adopt">採用</button>'}</td>
    </tr>`;
  }).join('');
  $('#takes').querySelector('tbody').innerHTML = body
    || '<tr><td colspan="6" class="sh-note">まだ撮っていない</td></tr>';
  for (const b of $('#takes').querySelectorAll('button')) {
    b.onclick = () => {
      const url = b.closest('tr').dataset.url;
      const t = list.find((x) => x.url === url);
      if (b.dataset.act === 'prev') preview(t);
      else if (b.dataset.act === 'unadopt') unadopt();
      else adopt(t);
    };
  }
}

// ---- 試写（画に出る頭の窓だけをループ）---------------------------------------
function preview(t) {
  curTake = t || null;
  const v = $('#prev');
  if (!t) { v.removeAttribute('src'); return; }
  v.src = t.url;
  v.loop = false;
  const base = '（実機で画に出るのはここだけ）';
  const apply = () => {
    const w = headWindow(neededHeadSec(segments(), shotName(cur)), Number($('#inPoint').value));
    $('#prevNote').textContent =
      `頭の ${w.start.toFixed(2)}〜${w.end.toFixed(2)} 秒だけを繰り返す${base}`;
    v.currentTime = w.start;
    // ⚠ **自動再生の失敗を握りつぶさない**。`onloadedmetadata` はクリックの文脈から外れるので、
    //   ブラウザの設定によっては muted でも拒否される。黙って止まると
    //   「素材が壊れている」と誤診する（実際に検証中そう見えた）。
    v.play().catch(() => {
      $('#prevNote').textContent =
        `頭の ${w.start.toFixed(2)}〜${w.end.toFixed(2)} 秒${base} — `
        + '自動再生が止められた。映像をクリックすると始まる';
    });
    clearInterval(Number(v.dataset.timer));
    const timer = setInterval(() => {
      if (v.currentTime >= w.end || v.ended) { v.currentTime = w.start; v.play().catch(() => {}); }
    }, 60);
    v.dataset.timer = String(timer);
  };
  // 止まっていたら映像をクリックで始められる（上の警告と対）。
  v.onclick = () => { v.play().catch(() => {}); };
  v.onloadedmetadata = apply;
  if (v.readyState >= 1) apply();
}

$('#inPoint').oninput = () => { if (curTake) preview(curTake); };

// ---- 採用 -------------------------------------------------------------------
/** 採用を取り消す（url を空で送る）。間違って採用したときの戻り道。素材は消さない。 */
async function unadopt() {
  const r = await api('/shoot/adopt', { cueId: shotName(cur), url: '' });
  if (!r || !r.ok) {
    $('#recState').innerHTML = `<span class="sh-ng">取り消せなかった: ${(r || {}).detail || ''}</span>`;
    return;
  }
  await loadState();
  await refreshTakes();
  $('#recState').innerHTML = '<span class="sh-warn">採用を取り消した</span>';
}

async function adopt(t) {
  if (!t) return;
  const inPoint = Number($('#inPoint').value) || 0;
  const r = await api('/shoot/adopt', { cueId: shotName(cur), url: t.url, inPointSec: inPoint });
  if (!r || !r.ok) {
    $('#recState').innerHTML = `<span class="sh-ng">採用できなかった: ${(r || {}).detail || ''}</span>`;
    return;
  }
  await loadState();
  await refreshTakes();
  $('#recState').innerHTML = '<span class="sh-ok">採用した</span>';
}

$('#adoptBtn').onclick = () => adopt(curTake);
$('#recBtn').onclick = toggleRecord;

// ---- 起動 -------------------------------------------------------------------
async function loadState() {
  state = await api('/state');
}

(async () => {
  await loadState();
  await loadDevices();
  await refreshTakes();
  selectShot();
  // 端末の状態は現場で変わる（熱・電池・録りっぱなし）。定期的に見る。
  setInterval(loadDevices, 4000);
})();
