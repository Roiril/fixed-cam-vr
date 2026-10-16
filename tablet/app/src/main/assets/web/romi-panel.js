// 終幕の台車（Romi）の自動走行をスタッフ設定から調整・保存・試走する面。
//
// 動きの正は台車（ESP32）の NVS。ここは GET /ending で読み、POST /ending で書き、POST /ending/go で試走する。
// 本番の合図は Quest が送る（体験者が最後の異常を解除した縁で go、電源断の縁で cut）。
// 送り先は TabletTransport の 'cart'（ネイティブは 192.168.10.51 だけに通す）。
(() => {
  'use strict';

  // 報告（解除）から電源断（終幕の頭）までの目安。Quest の RomiCartLink.ReleaseToCutSec と対。
  // 正本は show.json の締めのカット（4 周目 A の帰還の 7.1 秒・rules/show-design.md）。
  const CUT_SEC = 7.1;
  const POLL_MS = 1000;
  const ARM_MS = 3000;

  const root = document.getElementById('romiPanel');
  // 「困ったとき」の折りたたみの中に置く（スタッフ画面の折りたたみは 1 つだけという約束・verify-runtime）
  const details = document.getElementById('staffTrouble');
  // verify-runtime.cjs の代役 DOM（createElement を持たない）では組まない
  if (!root || !details || !globalThis.TabletTransport || typeof document.createElement !== 'function') return;

  const style = document.createElement('style');
  style.textContent = `
    .romi-section { grid-column: 1 / -1; border-top: 1px solid var(--staff-line); padding-top: 14px; }
    #romiPanel { display: block; }
    .romi-grid { display: grid; grid-template-columns: minmax(0,1fr) minmax(0,1fr); gap: 14px 28px; }
    .romi-field { display: grid; gap: 6px; margin: 0; padding: 0; border: 0; min-width: 0; }
    .romi-h { margin: 0 0 4px; font-size: 16px; font-weight: 600; }
    .romi-row { display: grid; grid-template-columns: 6.5em minmax(0,1fr) 5.5em; align-items: center; gap: 8px; font-size: 15px; }
    .romi-row input[type=range] { width: 100%; accent-color: var(--staff-ink); }
    .romi-row output { text-align: right; font-variant-numeric: tabular-nums; }
    .romi-seg { display: inline-flex; gap: 6px; }
    .romi-seg button { min-height: 40px; padding: 6px 14px; border: 1px solid var(--staff-line-strong); border-radius: 6px; background: transparent; color: var(--staff-ink); font-size: 15px; }
    .romi-seg button[aria-pressed=true] { background: var(--staff-ink); color: var(--staff-bg); border-color: var(--staff-ink); }
    .romi-est { color: var(--staff-muted); font-size: 14px; margin: 2px 0 0; }
    .romi-check { display: flex; align-items: center; gap: 10px; min-height: 44px; font-size: 15px; }
    .romi-check input { width: 22px; height: 22px; accent-color: var(--staff-ink); }
    .romi-time { grid-column: 1 / -1; }
    .romi-bar { position: relative; height: 34px; margin: 8px 0 22px; border: 1px solid var(--staff-line); border-radius: 4px; overflow: visible; }
    .romi-bar span { position: absolute; top: 0; bottom: 0; }
    .romi-bar .wait { background: rgba(214,222,230,.08); }
    .romi-bar .rot { background: rgba(214,222,230,.35); }
    .romi-bar .fwd { background: var(--ok); opacity: .75; }
    .romi-bar .cut, .romi-bar .meas { top: -6px; bottom: -6px; width: 0; border-left: 2px solid var(--error); }
    .romi-bar .meas { border-left-style: dashed; border-color: var(--wait); }
    .romi-bar .cut::after, .romi-bar .meas::after { position: absolute; top: 100%; left: -3em; width: 6em; text-align: center; font-size: 12px; color: var(--error); content: attr(data-label); }
    .romi-bar .meas::after { color: var(--wait); }
    .romi-note { margin: 4px 0; font-size: 15px; line-height: 1.6; }
    .romi-note[data-tone=trouble] { color: var(--error); }
    .romi-actions { grid-column: 1 / -1; display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: 0 10px; }
    .romi-actions .staff-action[data-armed=true] { border-color: var(--wait); color: var(--wait); }
    @media (max-width:760px), (max-aspect-ratio:1/1) { .romi-grid, .romi-actions { grid-template-columns: minmax(0,1fr); } }
  `;
  document.head.appendChild(style);

  root.innerHTML = `
    <div class="romi-grid">
      <p class="staff-help romi-time" id="romiIntro">最後の異常を体験者が解除すると、クエストが台車へ合図を送ります。台車は合図から待って回転し、前進します。電源断（画面が消える）は解除から約 ${CUT_SEC} 秒です。</p>
      <div class="romi-field" role="group" aria-labelledby="romiHWait">
        <h5 class="romi-h" id="romiHWait">合図と待ち</h5>
        <label class="romi-check"><input type="checkbox" id="romiEn">クエストの合図で動かす</label>
        <div class="romi-row"><label for="romiDelay">待ち</label><input type="range" id="romiDelay" min="0" max="20" step="0.1"><output id="romiDelayOut"></output></div>
        <label class="romi-check"><input type="checkbox" id="romiStopAtCut">電源断で台車も止める</label>
      </div>
      <div class="romi-field">
        <h5 class="romi-h">台車の状態</h5>
        <span class="romi-note" id="romiStatus" role="status" aria-live="polite">台車を確認しています。</span>
        <span class="romi-note" id="romiLast"></span>
      </div>
      <div class="romi-field" role="group" aria-labelledby="romiHRot">
        <h5 class="romi-h" id="romiHRot">回転</h5>
        <div class="romi-seg" role="group" aria-label="回転の向き"><button type="button" id="romiRotR">右回り</button><button type="button" id="romiRotL">左回り</button></div>
        <div class="romi-row"><label for="romiRotDuty">強さ</label><input type="range" id="romiRotDuty" min="10" max="68" step="1"><output id="romiRotDutyOut"></output></div>
        <div class="romi-row"><label for="romiRotMs">時間</label><input type="range" id="romiRotMs" min="0" max="8" step="0.05"><output id="romiRotMsOut"></output></div>
        <p class="romi-est" id="romiRotEst"></p>
      </div>
      <div class="romi-field" role="group" aria-labelledby="romiHFwd">
        <h5 class="romi-h" id="romiHFwd">前進</h5>
        <div class="romi-seg" role="group" aria-label="進む向き"><button type="button" id="romiFwdF">前へ</button><button type="button" id="romiFwdB">後ろへ</button></div>
        <div class="romi-row"><label for="romiFwdDuty">強さ</label><input type="range" id="romiFwdDuty" min="10" max="68" step="1"><output id="romiFwdDutyOut"></output></div>
        <div class="romi-row"><label for="romiFwdMs">時間</label><input type="range" id="romiFwdMs" min="0" max="8" step="0.05"><output id="romiFwdMsOut"></output></div>
        <p class="romi-est" id="romiFwdEst"></p>
      </div>
      <div class="romi-time">
        <div class="romi-bar" id="romiBar" aria-hidden="true"></div>
        <p class="romi-note" id="romiCutNote"></p>
      </div>
      <div class="romi-actions">
        <button class="staff-action" id="romiSave" type="button">保存する</button>
        <button class="staff-action" id="romiTry" type="button">試走する</button>
        <button class="staff-action" id="romiStop" type="button">台車を止める</button>
      </div>
      <p class="staff-help romi-time">試走は合図を受けたときと同じ動きをします（待ちを含む）。台車の前に物や人がいないことを確かめてから押してください。時間と強さの目安は、電池なしで測った速さから出しています。電池を入れると速くなります。</p>
    </div>`;

  const $ = (id) => document.getElementById(id);
  const fields = {
    delay: $('romiDelay'), rotDuty: $('romiRotDuty'), rotMs: $('romiRotMs'), fwdDuty: $('romiFwdDuty'), fwdMs: $('romiFwdMs'),
  };
  let saved = null;       // 台車が返した最後の設定
  let cal = null;         // 台車の較正（目安の計算に使う）
  let gapMs = 250;
  let draft = null;       // 画面の値
  let pollTimer = 0;
  let armTimer = 0;
  let busy = false;

  function request(path, method = 'GET', body = null) {
    const options = { method, headers: { 'Content-Type': 'application/json' }, signal: AbortSignal.timeout?.(4000) };
    if (body != null) options.body = JSON.stringify(body);
    return TabletTransport.request(path, options, 'cart').then((res) => {
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return res.json();
    });
  }

  function fromServer(data) {
    return {
      en: data.en ? 1 : 0, stopAtCut: data.stopAtCut ? 1 : 0, delayMs: data.delayMs,
      rotDir: data.rotDir < 0 ? -1 : 1, rotDuty: data.rotDuty, rotMs: data.rotMs,
      fwdDir: data.fwdDir < 0 ? -1 : 1, fwdDuty: data.fwdDuty, fwdMs: data.fwdMs,
    };
  }

  const keys = ['en', 'stopAtCut', 'delayMs', 'rotDir', 'rotDuty', 'rotMs', 'fwdDir', 'fwdDuty', 'fwdMs'];
  const dirty = () => saved && draft && keys.some((k) => saved[k] !== draft[k]);

  function writeForm() {
    if (!draft) return;
    $('romiEn').checked = !!draft.en;
    $('romiStopAtCut').checked = !!draft.stopAtCut;
    fields.delay.value = (draft.delayMs / 1000).toFixed(1);
    fields.rotDuty.value = draft.rotDuty;
    fields.rotMs.value = (draft.rotMs / 1000).toFixed(2);
    fields.fwdDuty.value = draft.fwdDuty;
    fields.fwdMs.value = (draft.fwdMs / 1000).toFixed(2);
    $('romiRotR').setAttribute('aria-pressed', String(draft.rotDir > 0));
    $('romiRotL').setAttribute('aria-pressed', String(draft.rotDir < 0));
    $('romiFwdF').setAttribute('aria-pressed', String(draft.fwdDir > 0));
    $('romiFwdB').setAttribute('aria-pressed', String(draft.fwdDir < 0));
    render();
  }

  function readForm() {
    if (!draft) return;
    draft.en = $('romiEn').checked ? 1 : 0;
    draft.stopAtCut = $('romiStopAtCut').checked ? 1 : 0;
    draft.delayMs = Math.round(Number(fields.delay.value) * 1000);
    draft.rotDuty = Math.round(Number(fields.rotDuty.value));
    draft.rotMs = Math.round(Number(fields.rotMs.value) * 1000);
    draft.fwdDuty = Math.round(Number(fields.fwdDuty.value));
    draft.fwdMs = Math.round(Number(fields.fwdMs.value) * 1000);
    // 0 は「その動きを省く」。台車は 50ms 未満を駆動できない（台車側でも 50ms へ上げる）
    if (draft.rotMs > 0 && draft.rotMs < 50) draft.rotMs = 50;
    if (draft.fwdMs > 0 && draft.fwdMs < 50) draft.fwdMs = 50;
    render();
  }

  function estimate(kind, duty, ms) {
    if (!cal || ms <= 0) return 0;
    const rate = kind === 'rot' ? cal.rv * (duty - cal.rd) : cal.sv * (duty - cal.sd);
    const t = (ms - (kind === 'rot' ? cal.rt : cal.st)) / 1000;
    return rate > 0 && t > 0 ? rate * t : 0;
  }

  const sec = (ms) => `${(ms / 1000).toFixed(ms % 100 === 0 ? 1 : 2)} 秒`;

  // 合図からの時刻表（秒）。台車のファームと同じ組み方: 待ち → 回転 → 整定 → 前進
  function plan(d) {
    const rot0 = d.delayMs / 1000;
    const rot1 = rot0 + d.rotMs / 1000;
    const fwd0 = d.rotMs > 0 ? rot1 + gapMs / 1000 : rot0;
    const fwd1 = fwd0 + d.fwdMs / 1000;
    return { rot0, rot1, fwd0, fwd1 };
  }

  function render() {
    if (!draft) return;
    $('romiDelayOut').textContent = sec(draft.delayMs);
    $('romiRotDutyOut').textContent = `${draft.rotDuty} %`;
    $('romiRotMsOut').textContent = draft.rotMs > 0 ? sec(draft.rotMs) : 'しない';
    $('romiFwdDutyOut').textContent = `${draft.fwdDuty} %`;
    $('romiFwdMsOut').textContent = draft.fwdMs > 0 ? sec(draft.fwdMs) : 'しない';
    const deg = estimate('rot', draft.rotDuty, draft.rotMs);
    const cm = estimate('fwd', draft.fwdDuty, draft.fwdMs);
    $('romiRotEst').textContent = draft.rotMs > 0 ? `目安 約 ${Math.round(deg)} 度` : '回転しません';
    $('romiFwdEst').textContent = draft.fwdMs > 0 ? `目安 約 ${Math.round(cm)} cm` : '前進しません';

    const p = plan(draft);
    const span = Math.max(p.fwd1, CUT_SEC) + 1;
    const pct = (s) => `${(s / span * 100).toFixed(2)}%`;
    const bar = $('romiBar');
    const parts = [
      `<span class="wait" style="left:0;width:${pct(p.rot0)}"></span>`,
      draft.rotMs > 0 ? `<span class="rot" style="left:${pct(p.rot0)};width:${pct(p.rot1 - p.rot0)}"></span>` : '',
      draft.fwdMs > 0 ? `<span class="fwd" style="left:${pct(p.fwd0)};width:${pct(p.fwd1 - p.fwd0)}"></span>` : '',
      `<span class="cut" style="left:${pct(CUT_SEC)}" data-label="電源断 ${CUT_SEC}s"></span>`,
    ];
    const lastCut = lastMeasuredCutSec();
    if (lastCut != null) parts.push(`<span class="meas" style="left:${pct(Math.min(lastCut, span))}" data-label="前回 ${lastCut.toFixed(1)}s"></span>`);
    bar.innerHTML = parts.join('');

    const note = $('romiCutNote');
    let tone = 'ok';
    let text;
    if (draft.fwdMs <= 0) {
      text = '前進しない設定です。';
      tone = 'trouble';
    } else if (CUT_SEC < p.fwd0) {
      text = `電源断のとき台車はまだ前進していません（前進は ${p.fwd0.toFixed(1)} 秒から）。待ちか回転を短くしてください。`;
      tone = 'trouble';
    } else if (CUT_SEC > p.fwd1) {
      text = `電源断の前に前進が終わります（${p.fwd1.toFixed(1)} 秒で停止）。待ちを長くするか、前進の時間を延ばしてください。`;
      tone = 'trouble';
    } else {
      const into = CUT_SEC - p.fwd0;
      const cmAtCut = estimate('fwd', draft.fwdDuty, Math.round(into * 1000));
      text = `電源断は前進を始めて ${into.toFixed(1)} 秒後です（前進 ${sec(draft.fwdMs)}のうち・約 ${Math.round(cmAtCut)} cm の地点）。`;
    }
    note.textContent = text;
    note.dataset.tone = tone;
    $('romiSave').textContent = dirty() ? '保存する（未保存の変更あり）' : '保存する';
  }

  let last = null;
  function lastMeasuredCutSec() {
    return last && last.cutMs >= 0 ? last.cutMs / 1000 : null;
  }

  function describeLast() {
    if (!last || !last.count) return '台車はまだ合図を受けていません。';
    const who = last.src === 'quest' ? 'クエスト' : last.src === 'tablet' ? 'このタブレットの試走' : last.src || '不明';
    const ago = Math.round(last.agoMs / 1000);
    const state = { running: '動いています', done: '終わりました', stopped: '途中で止めました', dry: '空走（記録だけ）', idle: '' }[last.state] || last.state;
    let text = `最後の合図: ${who}・${ago} 秒前・${state}`;
    if (last.cutMs >= 0) text += `。電源断は合図から ${(last.cutMs / 1000).toFixed(1)} 秒でした`;
    return text;
  }

  // 操作の結果（保存した・試走を始めた）は数秒残す。1 秒ごとの再読込の「接続しました」で上書きしない
  let holdUntil = 0;
  function setStatus(text, tone, holdMs = tone === 'trouble' ? 5000 : 0) {
    if (!holdMs && Date.now() < holdUntil) return;
    if (holdMs) holdUntil = Date.now() + holdMs;
    const el = $('romiStatus');
    el.textContent = text;
    el.dataset.tone = tone || '';
  }

  function applyServer(data, { keepDraft } = {}) {
    saved = fromServer(data);
    cal = data.cal || cal;
    gapMs = Number(data.gapMs) || gapMs;
    last = data.last || null;
    if (!keepDraft || !draft) {
      draft = { ...saved };
      writeForm();
    } else {
      render();
    }
    setStatus(`台車に接続しました（設定の版 ${data.ver}${data.busy ? '・動作中' : ''}）。`);
    $('romiLast').textContent = describeLast();
  }

  async function refresh(keepDraft) {
    try {
      const data = await request('/ending');
      applyServer(data, { keepDraft });
      setEnabled(true);
    } catch (error) {
      setStatus('台車に接続できません（192.168.10.51）。台車の電源と Wi-Fi を確かめてください。', 'trouble');
      if (!saved) setEnabled(false);
    }
  }

  function setEnabled(on) {
    root.querySelectorAll('input, .romi-seg button, .romi-actions button').forEach((el) => {
      if (el.id === 'romiStop') return;   // 止めるは常に押せる
      el.disabled = !on || busy;
    });
  }

  async function save() {
    if (!draft || busy) return;
    busy = true; setEnabled(true);
    try {
      const data = await request('/ending', 'POST', draft);
      applyServer(data);
      setStatus(`保存しました（設定の版 ${data.ver}）。次の体験から使われます。`, '', 5000);
    } catch (error) {
      setStatus('保存できませんでした。台車の電源と Wi-Fi を確かめてください。', 'trouble');
    } finally {
      busy = false; setEnabled(Boolean(saved));
    }
  }

  function disarm() {
    clearTimeout(armTimer);
    armTimer = 0;
    $('romiTry').dataset.armed = 'false';
    $('romiTry').textContent = '試走する';
  }

  async function tryRun() {
    if (busy) return;
    if (dirty()) {
      setStatus('未保存の変更があります。保存してから試走してください（台車は保存した設定で動きます）。', 'trouble');
      return;
    }
    // 押し間違いで台車が走り出さないよう、3 秒以内にもう一度押したときだけ走らせる
    if ($('romiTry').dataset.armed !== 'true') {
      $('romiTry').dataset.armed = 'true';
      $('romiTry').textContent = 'もう一度押すと走ります';
      armTimer = setTimeout(disarm, ARM_MS);
      return;
    }
    disarm();
    try {
      const res = await request('/ending/go', 'POST', { src: 'tablet', force: 1 });
      if (res.ok) setStatus('試走を始めました。', '', 5000);
      else setStatus(res.err === 'busy' ? '台車を手で操作している最中です。操作を止めてから試してください。' : `台車が断りました（${res.err}）。`, 'trouble');
      refresh(true);
    } catch (error) {
      setStatus('試走の合図が届きませんでした。', 'trouble');
    }
  }

  async function stop() {
    disarm();
    try {
      await request('/stop');
      setStatus('台車を止めました。', '', 5000);
      refresh(true);
    } catch (error) {
      setStatus('止める合図が届きませんでした。台車の電源を切ってください。', 'trouble');
    }
  }

  Object.values(fields).forEach((el) => el.addEventListener('input', readForm));
  $('romiEn').addEventListener('change', readForm);
  $('romiStopAtCut').addEventListener('change', readForm);
  const dirButton = (id, key, value) => $(id).addEventListener('click', () => {
    if (!draft) return;
    draft[key] = value;
    writeForm();
  });
  dirButton('romiRotR', 'rotDir', 1);
  dirButton('romiRotL', 'rotDir', -1);
  dirButton('romiFwdF', 'fwdDir', 1);
  dirButton('romiFwdB', 'fwdDir', -1);
  $('romiSave').addEventListener('click', save);
  $('romiTry').addEventListener('click', tryRun);
  $('romiStop').addEventListener('click', stop);

  // 開いている間だけ台車を見に行く（閉じたら止める）。最後の合図の状態もここで追う。
  function startPolling() {
    stopPolling();
    refresh(Boolean(draft));
    pollTimer = setInterval(() => { if (!busy) refresh(true); }, POLL_MS);
  }
  function stopPolling() {
    clearInterval(pollTimer);
    pollTimer = 0;
    disarm();
  }
  details.addEventListener('toggle', () => (details.open ? startPolling() : stopPolling()));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) stopPolling();
    else if (details.open) startPolling();
  });
  setEnabled(false);

  globalThis.RomiPanel = Object.freeze({ CUT_SEC, plan, refresh });
})();
