// 🕹 ショーシミュレーション（実機なしでショーを検証する卓 UI）。
//   計画: .claude/plans/2026-07-25_show-simulator.md 段 S4
//
// フロアマップのドットをドラッグすると、その位置を毎 tick（20ms）供給してショーが**実時間で進む**。
// 判定は scenario-engine.js（Unity 純ロジックの JS ミラー）を通す。ミラーが Unity と同じ答えを出すことは
// golden fixture（scenario_walk.trace.json）で機械固定してある — この UI は「独自解釈」を一切持たない。
//
// ⚠ **これは「ロジックの検証」であって「体感の検証」ではない**。VR のスケール感・dip の体感時間・
//   MJPEG の遅延・位置合わせ・Quest の性能は卓では分からない（パネル下部に常時明示）。
//   卓で通っても実機確認は要る。

import { camColor, escapeHtml, createMediaCache, FX_DEFAULT } from './common.js';
import { createCompositeView } from './composite-view.js';
import { durationOf, onDurationResolved } from './media-duration.js';
import { createShowRunner, parseScenario, sampleAt, DEFAULT_TICK_MS } from './scenario-engine.js';
import { buildScenarioConfig, serializeScenario } from './show-scenario.js';

const LS_KEY = 'mawarimi.scenarios.v1';
const MAX_EVENTS = 300;
const SPEEDS = [1, 4, 16];

export function createShowSim(container, deps) {
  // deps: { getState: () => showJson, setDot: (x, z) => void }

  container.innerHTML = `
    <div class="ss-wrap">
      <div class="ss-left">
        <div class="ss-bar">
          <button class="ss-play accent">▶ 再生</button>
          <button class="ss-stop">⏹ 先頭へ</button>
          <span class="ss-clock">00:00.000</span>
          <span class="ss-speeds"></span>
          <span class="spacer"></span>
          <button class="ss-rec">● 記録</button>
          <span class="ss-recinfo"></span>
        </div>

        <div class="ss-note ss-howto">歩かせ方: 左のフロアマップを <b>🚶 歩かせる</b> にして、
          マップの上をマウスで押したままドラッグ（押した場所に体験者が立ちます）。</div>

        <div class="ss-screen">
          <div class="ss-screen-view">
            <canvas class="ss-canvas" width="480" height="360"></canvas>
            <div class="ss-screen-badge"></div>
          </div>
          <div class="ss-screen-side">
            <div class="ss-screen-chip"><i></i><b class="ss-screen-cam">—</b></div>
            <div class="ss-screen-detail">停止中</div>
            <div class="ss-screen-note">映しているのは卓の合成器（Quest と同じ式）。ライブ映像はカメラが
              繋がっている時だけ出る。素材（事前映像・静止画）とマスク・画像加工は繋がっていなくても出る。</div>
          </div>
        </div>

        <div class="ss-readout">
          <span class="ss-ro"><i>周回</i><b class="ss-lap">—</b></span>
          <span class="ss-ro"><i>区間</i><b class="ss-seg">—</b></span>
          <span class="ss-ro"><i>確定ゾーン</i><b class="ss-zone">—</b></span>
          <span class="ss-ro"><i>位置</i><b class="ss-pos">—</b></span>
          <span class="ss-ro"><i>武装</i><b class="ss-armed">—</b></span>
        </div>

        <div class="ss-log-title">イベントログ<span class="ss-hint">時刻はシミュレーション内の経過</span></div>
        <div class="ss-log"></div>
      </div>

      <div class="ss-right">
        <div class="ss-note ss-status"></div>

        <div class="ss-block">
          <div class="ss-block-title">記録したシナリオ</div>
          <div class="ss-row">
            <input class="ss-name" type="text" placeholder="名前（例: 速く歩く）">
            <button class="ss-save">💾 保存</button>
          </div>
          <div class="ss-row">
            <select class="ss-list"></select>
            <button class="ss-replay">▶ 再実行</button>
          </div>
          <label class="ss-chk"><input class="ss-usesaved" type="checkbox"> 保存時の設定で再生（既定は今の show.json）</label>
          <div class="ss-savestate"></div>
        </div>

        <div class="ss-block ss-warn-block" style="display:none">
          <div class="ss-block-title">⚠ 設定の警告</div>
          <div class="ss-warnings"></div>
        </div>

        <div class="ss-block ss-limits">
          <div class="ss-block-title">これは実機の代わりにならない</div>
          <ul>
            <li>VR のスケール感・立体視、首追従の気持ちよさ</li>
            <li>dip の黒の体感時間、フェードの繋がり</li>
            <li>MJPEG の実レイテンシ・stall・砂嵐、Wi-Fi 由来の挙動</li>
            <li>位置合わせ（点タッチ登録）・OS recenter・コントローラ触覚</li>
            <li>Quest の性能（fps・発熱）</li>
          </ul>
          <div class="ss-hint">検証できるのは「この歩き方をしたら何が起きるか」だけ。<b>卓で通っても実機確認は要る。</b></div>
        </div>
      </div>
    </div>`;

  const q = (s) => container.querySelector(s);
  const playBtn = q('.ss-play'), stopBtn = q('.ss-stop'), recBtn = q('.ss-rec');
  const clockEl = q('.ss-clock'), speedsEl = q('.ss-speeds'), recInfoEl = q('.ss-recinfo');
  const chipEl = q('.ss-screen-chip i'), chipCamEl = q('.ss-screen-cam'), detailEl = q('.ss-screen-detail');
  const badgeEl = q('.ss-screen-badge'), howtoEl = q('.ss-howto');
  const lapEl = q('.ss-lap'), segEl = q('.ss-seg'), zoneEl = q('.ss-zone'), posEl = q('.ss-pos'), armedEl = q('.ss-armed');
  const logEl = q('.ss-log'), statusEl = q('.ss-status');
  const nameI = q('.ss-name'), saveBtn = q('.ss-save'), listSel = q('.ss-list'), replayBtn = q('.ss-replay');
  const useSavedChk = q('.ss-usesaved'), saveStateEl = q('.ss-savestate');
  const warnBlock = q('.ss-warn-block'), warnEl = q('.ss-warnings');

  // ---- 状態 -------------------------------------------------------------------
  let state = null;              // show.json
  let cfg = null, meta = null;   // buildScenarioConfig の結果
  let runner = null;
  let enabled = false;           // フロアマップの「シミュレーション」チェック
  let playing = false;
  let speed = 1;
  let simMs = 0;
  let acc = 0, lastWall = 0;
  let pos = { x: 0, z: -0.7 };
  let events = [];
  let recording = false, recSamples = [], recHoldAt = null;
  let replay = null;             // { samples, endMs, name }
  let staleConfig = false;       // 実行中に show.json が変わった
  let notice = '';               // 一時メッセージ（再実行おわり 等）。renderStatus が毎回描き直す
  let scenarios = [];            // { name, url? , local? }

  // ---- 設定の組み立て ---------------------------------------------------------
  function rebuild(force) {
    if (!state) return;
    if (playing && !force) { staleConfig = true; renderStatus(); return; }
    // 「素材の終わりまで」の尺は素材の実尺で解く（media-duration が測れた分だけ）。
    const built = buildScenarioConfig(state, { getDuration: durationOf });
    cfg = built.cfg;
    meta = built.meta;
    runner = createShowRunner(cfg);
    // 時計を 0 へ戻すので、前の走行のログは残さない（時刻が対応しなくなるため）。
    simMs = 0; acc = 0; events = []; logEl.innerHTML = ''; renderLogEmpty();
    staleConfig = false;
    renderWarnings();
    renderAll();
  }

  function camLabel(i) {
    if (!Number.isInteger(i) || i < 0) return '—';
    const c = meta && meta.cameras[i];
    return c ? `カメラ ${c.id}` : `#${i}`;
  }

  // ---- 描画 -------------------------------------------------------------------
  function renderSpeeds() {
    speedsEl.innerHTML = '';
    for (const s of SPEEDS) {
      const b = document.createElement('button');
      b.className = 'ss-speed' + (s === speed ? ' on' : '');
      b.textContent = `×${s}`;
      b.onclick = () => { speed = s; renderSpeeds(); };
      speedsEl.appendChild(b);
    }
  }

  const fmtClock = (ms) => {
    const t = Math.max(0, Math.round(ms));
    const m = Math.floor(t / 60000), s = Math.floor((t % 60000) / 1000), r = t % 1000;
    return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}.${String(r).padStart(3, '0')}`;
  };

  const baseName = (u) => String(u || '').split('/').pop();

  // 「いま画面に映っているもの」。演出が走っていればそのカットの内容が live より優先される
  //   （実機では TakeRunner が CameraSwitchDirector を占有する）。
  function describeScreen() {
    if (!runner) return { cam: -1, detail: '—' };
    if (runner.takeActive && meta) {
      const t = meta.takes[runner.activeTakeIndex];
      const st = t && t.steps[runner.activeStepIndex];
      if (t && st) {
        const nth = `カット ${runner.activeStepIndex + 1}/${t.steps.length}`;
        const overlay = st.cueId ? ` + cue ${st.cueId}` : '';
        if (st.source === 'live') {
          const cam = st.camera >= 0 ? st.camera : runner.shownCamera;
          return { cam, detail: `🎬 ${t.id}（${nth}: ${camLabel(cam)} のライブ${overlay}）` };
        }
        if (st.source === 'inherit') {
          return { cam: runner.shownCamera, detail: `🎬 ${t.id}（${nth}: ライブに重ね${overlay}）` };
        }
        const label = st.source === 'clip' ? '動画' : '静止画';
        return { cam: -1, detail: `🎬 ${t.id}（${nth}: ${label} ${baseName(st.assetUrl) || st.cueId || '素材未設定'} を全面）` };
      }
    }
    return { cam: runner.shownCamera, detail: enabled ? 'ライブ映像' : 'ライブ映像（停止中）' };
  }

  // ==========================================================================
  //  画面プレビュー（実画）— カメラ列・cue エディタと同じ合成器を provider で駆動する。
  //    実機の ScreenComposite と同じ式（live に素材をマスク合成 → post）。
  //    映せないもの: カメラが繋がっていなければライブは黒（素材と post は出る）。
  // ==========================================================================
  const media = createMediaCache();
  let lastShotKey = '';

  /** いま演出が持っているカット（無ければ null = ライブそのまま）。 */
  function currentShot() {
    if (!runner || !meta || !runner.takeActive) return null;
    const t = meta.takes[runner.activeTakeIndex];
    const st = t && t.steps[runner.activeStepIndex];
    if (!t || !st) return null;
    return { take: t, step: st, key: `${t.id}#${runner.activeStepIndex}` };
  }

  /** 画像加工の解決（実機と同じ カット > 区間 > カメラ > 全体）。 */
  function resolvePost(shot, camIdx) {
    if (shot && shot.step.hasPost && shot.step.post) return shot.step.post;
    const seg = runner && runner.segment;
    const segPost = seg && meta ? meta.segmentPosts[`${seg.lap}:${seg.camera}`] : null;
    if (segPost) return segPost;
    const cam = (meta && camIdx >= 0) ? meta.cameras[camIdx] : null;
    if (cam && cam.post) return cam.post;
    return (meta && meta.globalPost) || FX_DEFAULT;
  }

  // カットが変わった瞬間に頭出し（trimStart）して再生。早送り中は再生速度も合わせる。
  function syncVideo(el, shot) {
    if (shot.key !== lastShotKey) {
      lastShotKey = shot.key;
      try { el.currentTime = Math.max(0, shot.step.trimStartSec || 0); } catch { /* metadata 前 */ }
    }
    el.playbackRate = Math.max(0.0625, Math.min(16, speed));
    if (el.paused) { try { el.play().catch(() => {}); } catch { /* noop */ } }
  }

  function sampleScreen() {
    const camIdx = runner ? runner.shownCamera : -1;
    const camObj = (meta && camIdx >= 0) ? meta.cameras[camIdx] : null;
    const liveImg = (camObj && deps.getLiveImg) ? deps.getLiveImg(camObj.id) : null;
    const shot = currentShot();
    if (!shot) lastShotKey = '';

    let overlayEl = null, overlayReady = false, overlayW = 0, overlayH = 0, maskEl = null;
    let strength = 1, fadeSec = 0.3;
    const url = shot && shot.step.playUrl;
    if (url) {
      const rec = media.get(url);
      if (rec && rec.el) {
        overlayEl = rec.el;
        overlayReady = rec.ready;
        overlayW = rec.isVideo ? (rec.el.videoWidth || 0) : (rec.el.naturalWidth || 0);
        overlayH = rec.isVideo ? (rec.el.videoHeight || 0) : (rec.el.naturalHeight || 0);
        if (rec.isVideo && rec.ready) syncVideo(rec.el, shot);
      }
      // マスク無し = 全面差し替え（composite-view の既定と同じ）。
      maskEl = shot.step.maskUrl ? media.getImage(shot.step.maskUrl) : null;
      strength = shot.step.strength >= 0 ? shot.step.strength : 1;
      fadeSec = shot.step.fadeInSec >= 0 ? shot.step.fadeInSec : 0.3;
    }
    return {
      liveImg, overlayEl, overlayReady, overlayW, overlayH, maskEl,
      overlayOn: !!overlayEl, fadeSec, strength, feather: 0,
      post: resolvePost(shot, camIdx),
      // 尺の管理は runner（Unity と同じ判定）が持つ。合成器には終端を判定させない。
      trimEnd: 0, onVideoEnd: null,
    };
  }

  createCompositeView(q('.ss-canvas'), { sample: sampleScreen });

  function renderScreen() {
    const d = describeScreen();
    if (d.cam >= 0) {
      chipEl.style.background = camColor(d.cam);
      chipEl.style.visibility = 'visible';
      chipCamEl.textContent = camLabel(d.cam);
    } else {
      chipEl.style.visibility = 'hidden';
      chipCamEl.textContent = runner && runner.takeActive ? '素材' : '—';
    }
    detailEl.textContent = d.detail;
    badgeEl.textContent = screenIssue(d);
  }

  // 画面が黒いとき、それが「壊れている」のか「配信が来ていない」のかを画面の上で言う。
  //   卓は現場でカメラ 3 台のうち 1 台だけ繋がっている、という状態が普通に起きる。
  function screenIssue(d) {
    if (!runner || !meta) return '';
    const shot = currentShot();
    if (shot && shot.step.playUrl) return '';                 // 素材が映っている
    if (shot && shot.step.source !== 'live' && shot.step.source !== 'inherit') {
      return '⚠ このカットは素材が未設定です（実機では飛ばされます）';
    }
    if (d.cam < 0) return '';
    const camObj = meta.cameras[d.cam];
    const img = (camObj && deps.getLiveImg) ? deps.getLiveImg(camObj.id) : null;
    if (img && img.naturalWidth) return '';
    return `${camLabel(d.cam)} のライブ映像が来ていません（卓にカメラが繋がっていないだけで、`
      + '演出の判定・素材・画像加工はこのまま検証できます）';
  }

  function renderReadout() {
    if (!runner) return;
    lapEl.textContent = `${runner.lap} 周目`;
    const seg = runner.segment;
    segEl.textContent = seg ? `L${seg.lap}・${camLabel(seg.camera)}` : '—';
    const pend = runner.pendingZone;
    zoneEl.textContent = camLabel(runner.zoneCamera) + (pend >= 0 ? `（${camLabel(pend)} へ滞在待ち）` : '');
    posEl.textContent = `(${pos.x.toFixed(2)}, ${pos.z.toFixed(2)})`;
    armedEl.textContent = `${runner.armedCount} 本`;
    clockEl.textContent = fmtClock(simMs);
  }

  function renderStatus() {
    const bits = [];
    if (notice) bits.push(notice);
    if (!state) bits.push('show.json 待ち');
    else if (!meta || meta.zoneSource !== 'grid') bits.push('⚠ ゾーンを展開できません（フロアマップを確認）');
    else if (!enabled) bits.push('左のフロアマップを 🚶 歩かせる にすると動きます');
    else if (replay) bits.push(`▶ 再実行中: ${replay.name}`);
    else if (playing) bits.push(recording ? '● 記録中（マップをドラッグ）' : '実行中（マップをドラッグ）');
    else bits.push('一時停止中');
    if (staleConfig) bits.push('⚠ show.json が更新されました（⏹ 先頭へ で反映）');
    statusEl.textContent = bits.join(' / ');
    statusEl.className = 'ss-note ss-status' + (bits.some((b) => b.startsWith('⚠')) ? ' warn' : '');
    // 歩かせ方の説明は「まだ動かしていない人」だけに要る。走り出したら畳んで画面を空ける。
    howtoEl.style.display = (enabled && playing) ? 'none' : '';
  }

  function renderWarnings() {
    const ws = (meta && meta.warnings) || [];
    warnBlock.style.display = ws.length ? '' : 'none';
    warnEl.innerHTML = ws.map((w) => `<div class="ss-warning">${escapeHtml(w)}</div>`).join('');
  }

  function renderAll() { renderScreen(); renderReadout(); renderStatus(); }

  // ---- イベントログ -----------------------------------------------------------
  const EVENT_STYLE = {
    screen: 'ev-screen', zone: 'ev-zone', lap: 'ev-lap',
    seg: 'ev-seg', take: 'ev-take', step: 'ev-step', end: 'ev-end',
  };

  function describeEvent(e) {
    switch (e.kind) {
      case 'screen': return `画面 → ${camLabel(e.a)}`;
      case 'zone': return `ゾーン確定 ${camLabel(e.a)}`;
      case 'lap': return `周回 → ${e.a} 周目`;
      case 'seg': return `区間 L${e.a}・${camLabel(e.b)} へ進入`;
      case 'take': return `演出開始 ${e.id}`;
      case 'step': {
        const t = meta && meta.takes.find((x) => x.id === e.id);
        const st = t && t.steps[e.a];
        const what = st
          ? (st.source === 'live' ? `${camLabel(st.camera >= 0 ? st.camera : e.b)} のライブ`
            : st.source === 'inherit' ? `ライブに重ね${st.cueId ? ` (${st.cueId})` : ''}`
              : `${st.source === 'clip' ? '動画' : '静止画'} ${baseName(st.assetUrl) || st.cueId || '素材なし'}`)
          : (e.b >= 0 ? camLabel(e.b) : '');
        return `カット ${e.a + 1} ${e.id}${what ? `: ${what}` : ''}`;
      }
      case 'end': return `演出終了 ${e.id}${e.flag ? '（⚠ watchdog 強制）' : ''} → 復帰 ${camLabel(e.a)}`;
      default: return e.kind;
    }
  }

  // 空のログは「ただの黒い箱」に見えるので、何をすれば埋まるかを書いておく。
  function renderLogEmpty() {
    if (logEl.childElementCount) return;
    logEl.innerHTML = '<div class="ss-log-empty">（まだイベントなし）<br>'
      + '左のフロアマップを 🚶 歩かせる にしてドラッグすると、'
      + 'ゾーン確定・周回・区間・演出の発火がここに時刻つきで並びます。</div>';
  }

  function pushEvents(list) {
    if (!list.length) return;
    const empty = logEl.querySelector('.ss-log-empty');
    if (empty) empty.remove();
    for (const e of list) {
      events.push(e);
      const row = document.createElement('div');
      row.className = `ss-ev ${EVENT_STYLE[e.kind] || ''}`;
      const t = document.createElement('i');
      t.textContent = fmtClock(e.t);
      const b = document.createElement('span');
      b.textContent = describeEvent(e);
      row.append(t, b);
      logEl.appendChild(row);
    }
    while (logEl.childElementCount > MAX_EVENTS) logEl.removeChild(logEl.firstChild);
    events = events.slice(-MAX_EVENTS);
    logEl.scrollTop = logEl.scrollHeight;
  }

  // ---- 記録 -------------------------------------------------------------------
  function recordSample(tMs, p) {
    const last = recSamples[recSamples.length - 1];
    if (!last) { recSamples.push({ tMs, x: p.x, z: p.z }); recHoldAt = null; return; }
    if (last.x === p.x && last.z === p.z) { recHoldAt = tMs; return; }
    // 立ち止まりを保存する: 動く直前の時刻に「同じ位置」の点を打ってから新しい点を打つ
    //   （これが無いと再生時に長い直線補間になり「立ち止まった」が消える）。
    if (recHoldAt !== null && recHoldAt > last.tMs) recSamples.push({ tMs: recHoldAt, x: last.x, z: last.z });
    recHoldAt = null;
    recSamples.push({ tMs, x: p.x, z: p.z });
  }

  function flushRecord() {
    const last = recSamples[recSamples.length - 1];
    if (last && (last.tMs < simMs)) recSamples.push({ tMs: simMs, x: pos.x, z: pos.z });
  }

  function renderRecInfo() {
    recBtn.classList.toggle('on', recording);
    recBtn.textContent = recording ? '■ 記録停止' : '● 記録';
    recInfoEl.textContent = recSamples.length ? `記録 ${recSamples.length} 点 / ${fmtClock(recSamples[recSamples.length - 1].tMs)}` : '';
  }

  // ---- 時計 -------------------------------------------------------------------
  function advance(dtMs) {
    acc += dtMs;
    let guard = 4000;   // 早送り中の 1 フレームあたり tick 数の上限（暴走防止）
    while (acc >= DEFAULT_TICK_MS && guard-- > 0) {
      acc -= DEFAULT_TICK_MS;
      simMs += DEFAULT_TICK_MS;
      let p = pos;
      if (replay) {
        p = sampleAt(replay.samples, simMs);
        pos = p;
        if (deps.setDot) deps.setDot(p.x, p.z);
      }
      pushEvents(runner.step(simMs, p.x, p.z));
      if (recording) recordSample(simMs, p);
      if (replay && simMs >= replay.endMs) { finishReplay(); break; }
    }
  }

  // rAF はタブ非表示で止まる（裏タブで凍る）ので壁時計駆動。
  setInterval(() => {
    if (!playing || !runner) return;
    const now = performance.now();
    const dt = Math.min(now - lastWall, 250) * speed;   // タブ復帰の巨大 dt は捨てる
    lastWall = now;
    advance(dt);
    renderAll();
    if (recording) renderRecInfo();
  }, 33);

  function play() {
    if (!runner) rebuild(true);
    if (!runner) return;
    notice = '';
    playing = true;
    lastWall = performance.now();
    playBtn.textContent = '⏸ 一時停止';
    playBtn.classList.remove('accent');
    renderStatus();
  }
  function pause() {
    playing = false;
    playBtn.textContent = '▶ 再生';
    playBtn.classList.add('accent');
    renderStatus();
  }
  function resetRun() {
    pause();
    replay = null;
    notice = '';
    if (recording) { recording = false; flushRecord(); renderRecInfo(); }
    logEl.innerHTML = ''; renderLogEmpty();
    rebuild(true);
  }
  function finishReplay() {
    const name = replay ? replay.name : '';
    replay = null;
    pause();
    notice = `▶ 再実行おわり: ${name}`;   // pause → renderStatus の後に立てる（次の描画で残る）
    renderStatus();
  }

  playBtn.onclick = () => (playing ? pause() : play());
  stopBtn.onclick = () => resetRun();
  recBtn.onclick = () => {
    if (recording) {
      recording = false;
      flushRecord();
    } else {
      recording = true;
      recSamples = [];
      recHoldAt = null;
      recordSample(simMs, pos);
      if (!playing) play();
    }
    renderRecInfo();
    renderStatus();
  };

  // ---- シナリオの保存 / 一覧 / 再実行 -------------------------------------------
  function localAll() {
    try { return JSON.parse(localStorage.getItem(LS_KEY) || '{}'); } catch { return {}; }
  }
  function localSave(name, obj) {
    const all = localAll();
    all[name] = obj;
    localStorage.setItem(LS_KEY, JSON.stringify(all));
  }

  async function refreshScenarios() {
    let items = [];
    let server = false;
    try {
      const r = await fetch('/scenarios/list');
      if (r.ok) {
        const j = await r.json();
        if (Array.isArray(j.items)) { items = j.items.map((it) => ({ name: it.name, url: it.url })); server = true; }
      }
    } catch { /* サーバ未対応 / 未再起動 */ }
    const locals = Object.keys(localAll()).map((name) => ({ name, local: true }));
    scenarios = items.concat(locals.filter((l) => !items.some((s) => s.name === l.name)));
    listSel.innerHTML = '';
    if (!scenarios.length) {
      const o = document.createElement('option');
      o.textContent = '（保存したシナリオなし）'; o.value = '';
      listSel.appendChild(o);
    }
    scenarios.forEach((s, i) => {
      const o = document.createElement('option');
      o.value = String(i);
      o.textContent = s.local ? `${s.name}（この端末）` : s.name;
      listSel.appendChild(o);
    });
    saveStateEl.dataset.server = server ? '1' : '0';
    return server;
  }

  saveBtn.onclick = async () => {
    if (recording) { recording = false; flushRecord(); renderRecInfo(); }
    if (recSamples.length < 2) { saveStateEl.textContent = '✕ 記録がありません（● 記録 してからドットを動かす）'; return; }
    const name = (nameI.value || '').trim() || `walk_${new Date().toISOString().slice(0, 19).replace(/[:T-]/g, '')}`;
    const obj = serializeScenario(cfg, recSamples, { name });
    let where = '';
    try {
      const r = await fetch('/scenarios/save', { method: 'POST', body: JSON.stringify({ name, scenario: obj }) });
      const j = r.ok ? await r.json() : null;
      if (j && j.ok) where = `server:${j.path || j.name}`;
    } catch { /* fallthrough */ }
    if (!where) {
      localSave(name, obj);
      where = 'local';
    }
    saveStateEl.textContent = where === 'local'
      ? `💾 ${name}（この端末に保存 — 卓サーバに保存 API が無い / 未再起動のため）`
      : `💾 ${name} → tools/web-compositor/scenarios/`;
    nameI.value = '';
    await refreshScenarios();
  };

  replayBtn.onclick = async () => {
    const s = scenarios[parseInt(listSel.value, 10)];
    if (!s) return;
    let obj = null;
    if (s.local) obj = localAll()[s.name];
    else {
      try { obj = await (await fetch(s.url)).json(); } catch { obj = null; }
    }
    if (!obj || !Array.isArray(obj.samples) || !obj.samples.length) {
      saveStateEl.textContent = '✕ シナリオを読めませんでした';
      return;
    }
    const parsed = parseScenario(obj);
    // 既定は「いまの show.json の設定」で再実行する（設定を直しながら同じ歩きを試すため）。
    // 保存時の設定で再現したいときはチェックを入れる。
    const useSaved = useSavedChk.checked;
    if (!useSaved) rebuild(true);
    else { cfg = parsed.cfg; meta = buildScenarioConfig(state || {}).meta; runner = createShowRunner(cfg); }
    logEl.innerHTML = ''; renderLogEmpty();
    events = [];
    simMs = parsed.samples[0].tMs;
    acc = 0;
    replay = {
      samples: parsed.samples,
      endMs: parsed.samples[parsed.samples.length - 1].tMs,
      name: s.name + (useSaved ? '（保存時の設定）' : '（今の設定）'),
    };
    pos = { x: parsed.samples[0].x, z: parsed.samples[0].z };
    if (deps.setDot) deps.setDot(pos.x, pos.z);
    play();
  };

  // ---- 外部 API ---------------------------------------------------------------
  /** show.json が更新されたら呼ぶ。 */
  function onState(s) {
    state = s;
    rebuild(false);
    renderStatus();
  }

  /** フロアマップのシミュレーションドットから呼ばれる（有効/無効・位置）。 */
  function onSimDot(e) {
    if (typeof e.enabled === 'boolean') {
      enabled = e.enabled;
      if (!enabled) pause();
    }
    if (Number.isFinite(e.x) && Number.isFinite(e.z) && !replay) {
      const moved = !pos || Math.abs(pos.x - e.x) > 1e-6 || Math.abs(pos.z - e.z) > 1e-6;
      pos = { x: e.x, z: e.z };
      // **歩かせたら勝手に走り出す**。ドラッグそのものが「体験者が歩いた」という入力なので、
      // その後に ▶ 再生 を押させるのは隠れた前提でしかない（押し忘れると「動かない」に見える）。
      if (moved && enabled && !playing) play();
    }
    renderAll();
  }

  // 素材の実尺が測れたら尺を解き直す（実行中なら「設定が変わった」扱い＝⏹ で反映）。
  onDurationResolved(() => rebuild(false));

  renderSpeeds();
  renderRecInfo();
  renderAll();
  refreshScenarios();

  return { onState, onSimDot, isRunning: () => playing };
}
