// 準備と本番の両方で使う機器の表示。カメラのモニター・Quest・博士タブレット。
import { h, setText, badge, setBadge, fact, number, ago, liveUrl, confirmButton, copyText } from './ui.js';
import { QUEST_NAMES, questNotes, phaseText, worst } from './model.js';

const PREVIEW_MS = 60_000;

/** カメラ 1 台。映像は押したときだけ引き、1 分で閉じる（配信スマホと Wi-Fi に常時の負荷を掛けない）。 */
export function createMonitor(id, ctx) {
  let row = null, usable = false, timer = 0;
  const image = h('img', { alt: `カメラ ${id} の加工前の映像`, hidden: true });
  const tally = h('span.tally');
  const screenState = h('p.screen-state');
  const screen = h('div.screen', {}, image, h('span.slate', { text: id, 'aria-hidden': 'true' }), tally, screenState);
  const state = badge();
  const title = h('p.monitor-title');
  const action = h('p.monitor-action');
  const frames = fact('新しいコマ'), temperature = fact('温度'), battery = fact('電池');
  const message = h('p.message', { role: 'status' });
  const view = h('button.button.small', { type: 'button', text: '映像を見る', 'aria-pressed': 'false' });
  const restart = confirmButton('起こし直す', 'もう一度押すと実行（映像が 10 秒ほど止まります）', async () => {
    message.dataset.state = '';
    setText(message, `カメラ ${id} を照合してから起こし直します。新しい映像が届くまで見ます。`);
    try {
      const result = await ctx.request('/ops/restart', { method: 'POST', body: { cameraId: id }, timeout: 70_000 });
      message.dataset.state = result.ok ? 'ok' : 'warning';
      setText(message, result.message || (result.ok ? '映像が戻りました。' : '戻ったことを確かめられません。端末の画面を見てください。'));
    } catch (error) {
      message.dataset.state = 'error';
      setText(message, `起こし直せませんでした。${error.message}`);
    }
    ctx.refresh();
  });
  restart.classList.add('small');
  const detail = h('div.hints');
  const node = h('article.monitor', { 'aria-label': `カメラ ${id}` }, screen,
    h('div.unit-head', {}, h('h3', { text: `カメラ ${id}` }), state), title, action,
    h('dl.facts', {}, frames.node, temperature.node, battery.node),
    h('div.actions', {}, view, restart), message,
    h('details', {}, h('summary', { text: '接続の詳細' }), detail));

  function close() {
    clearTimeout(timer); timer = 0;
    image.removeAttribute('src'); image.hidden = true; screenState.hidden = false;
    view.setAttribute('aria-pressed', 'false'); setText(view, '映像を見る');
  }
  view.addEventListener('click', () => {
    if (!image.hidden) return close();
    if (!usable || !row) return;
    image.src = liveUrl(row.host, row.port || 8080); image.hidden = false; screenState.hidden = true;
    view.setAttribute('aria-pressed', 'true'); setText(view, '映像を閉じる');
    timer = setTimeout(close, PREVIEW_MS);
  });
  image.addEventListener('error', () => {
    if (image.hidden) return;
    close(); message.dataset.state = 'error'; setText(message, '映像を開けません。配信アプリが前に出ているか見てください。');
  });

  function update(next, fresh) {
    row = next || null;
    const status = fresh ? (row?.status || 'unknown') : 'unknown';
    const moving = fresh && row?.stream?.ok === true;
    usable = fresh && row?.identityOk === true && !ctx.demo;
    node.dataset.state = status; node.dataset.moving = String(moving);
    setBadge(state, status);
    setText(tally, moving ? '受信中' : fresh ? '停止' : '不明');
    setText(screenState, !fresh ? 'いまの状態は分かりません' : moving ? '映像は進んでいます' : (row?.title || '映像が届いていません'));
    setText(title, fresh ? (row?.title || '端末の情報がありません') : 'いまの状態は分かりません');
    setText(action, fresh ? (row?.action || '') : '');
    action.hidden = !action.textContent;
    setText(frames.value, moving ? `${row.stream.frames} 枚 / ${number(row.stream.elapsedSec, ' 秒', 1)}` : '—');
    const metrics = fresh ? row?.metrics || {} : {};
    setText(temperature.value, number(metrics.batteryTempC, ' ℃', 1));
    temperature.value.dataset.tone = metrics.batteryTempC >= 43 ? 'care' : '';
    setText(battery.value, number(metrics.batteryPct, ' %'));
    battery.value.dataset.tone = metrics.batteryPct < 20 ? 'care' : '';
    view.disabled = !usable;
    view.title = ctx.demo ? '見本では映像を引きません。' : usable ? '' : '担当の端末だと確かめられたときに見られます。';
    restart.disabled = ctx.demo || !fresh || row?.canRestart !== true;
    restart.title = restart.disabled
      ? (row?.wirelessAdb?.state === 'serial_unregistered' ? '端末番号が登録されていません。端末で配信アプリを開いてください。'
        : '無線で端末を確かめられたときに使えます。使えないときは端末で配信アプリを開きます。') : '';
    if (!usable && !image.hidden) close();
    const lock = value => (value === true ? '固定' : value === false ? '自動' : '—');
    const adb = { verified: '届く', unreachable: '届かない', identity_unavailable: '端末番号を読めない',
      identity_mismatch: '登録と違う端末', serial_unregistered: '端末番号が未登録' };
    const lines = [
      `${row?.host || '—'}:${row?.port || 8080}`,
      `名乗った担当 ${fresh ? row?.observedId || '—' : '—'} ／ 担当の照合 ${fresh && row?.identityOk === true ? '一致' : '未確認'}`,
      `露出 ${lock(metrics.aeLock)} ／ 色 ${lock(metrics.awbLock)} ／ 画角 ${number(metrics.lensFovDeg, '°', 1)}`,
      `無線での復旧 ${fresh ? adb[row?.wirelessAdb?.state] || '—' : '—'}`,
      ...(fresh ? row?.issues || [] : []).map(issue => `${issue.title} ${issue.action || ''}`.trim()),
    ];
    const key = lines.join('\n');
    if (detail.dataset.key !== key) { detail.dataset.key = key; detail.replaceChildren(...lines.map((line, i) => h(i === 0 ? 'p.mono' : 'p', { text: line }))); }
  }
  return { node, update, close };
}

const controllerText = controller => (!controller ? '—' : !controller.connected ? 'つながっていない' : controller.tracked ? '見えている' : 'つながっている（見えていない）');

/** Quest 1 台。live = 本番の面（体験のどこに居るかを大きく出す）。 */
export function createQuestUnit(id, ctx, { live = false } = {}) {
  const name = QUEST_NAMES[id];
  const state = badge();
  const phaseLabel = h('span.phase-label'), phaseDetail = h('span.phase-detail');
  const phase = h('p.phase', { hidden: !live }, phaseLabel, phaseDetail);
  const title = h('p.unit-title'), action = h('p.unit-action');
  const seen = fact('応答'), position = fact('位置合わせ'), left = fact('左コントローラ'), right = fact('右コントローラ'),
    sound = fact('音'), video = fact('映像'), language = fact('言語');
  const ids = h('p.note.mono');
  const registerStatus = h('p.message', { role: 'status' });
  const register = h('button.button.small', { type: 'button', text: '登録の依頼文をコピー', hidden: true });
  let observedId = '';
  register.addEventListener('click', () => copyText(
    `廻リ視の ${name} を固定の登録に入れてください。\ntools/web-compositor/operations-fleet.json の quests の id="${id}" に、deviceId="${observedId}" と shortId="${observedId.slice(0, 6)}" を書きます。\n書いたあと卓を起動し直し、準備の画面で ${name} の行が「済み」になるか確かめてください。`, registerStatus));
  const node = h('article.unit', { 'aria-label': name },
    h('div.unit-head', {}, h('h3', { text: name }), state), phase, title, action,
    h('dl.facts', {}, seen.node, position.node, left.node, right.node, sound.node, video.node, language.node),
    ids, h('div.actions', {}, register), registerStatus);

  function update(row, fresh) {
    const standby = row?.standby === true;
    const notes = fresh ? questNotes(row) : [];
    const status = standby ? 'standby' : fresh ? worst([row?.status, ...notes.map(n => n.level)]) : 'unknown';
    node.dataset.state = status;
    setBadge(state, status);
    const where = phaseText(fresh && !standby ? row?.run : null);
    setText(phaseLabel, where.label); setText(phaseDetail, where.detail); phase.dataset.over = String(where.over === true);
    const note = notes.find(n => n.level === 'unknown') || notes[0];
    const ownOk = row?.status === 'ok';
    setText(title, standby ? '予備です。今日の判定には入れていません' : !fresh ? 'いまの状態は分かりません'
      : ownOk && note ? note.text : (row?.title || '応答がありません'));
    setText(action, standby ? '使う日は、登録から予備の印（standby）を外します。' : !fresh ? ''
      : ownOk && note ? (note.level === 'unknown' ? 'Quest を被り、右トリガーを 2 秒押して位置を合わせます。' : 'アプリを焼き直すと直ることがあります。')
        : ownOk ? (live ? '' : '映像と音は、被って確かめます。') : (row?.action || ''));
    action.hidden = !action.textContent;
    const facts = fresh ? row?.facts : null;
    setText(seen.value, fresh && Number.isFinite(row?.ageSec) ? ago(row.ageSec) : 'なし');
    seen.value.dataset.tone = fresh && Number.isFinite(row?.ageSec) ? '' : 'quiet';
    setText(position.value, !facts || facts.registered === null || facts.registered === undefined ? '—'
      : facts.registered === false ? 'まだ' : facts.needsReRegistration ? 'やり直し' : '済み');
    position.value.dataset.tone = facts && (facts.registered === false || facts.needsReRegistration) ? 'bad' : '';
    setText(left.value, controllerText(facts?.controllerLeft));
    setText(right.value, controllerText(facts?.controllerRight));
    const running = where.active;
    left.value.dataset.tone = facts?.controllerLeft && running && !(facts.controllerLeft.connected && facts.controllerLeft.tracked) ? 'bad' : '';
    setText(sound.value, !facts?.sound ? '—' : facts.sound.missing > 0 ? `${facts.sound.missing} 個が読めていない` : `${facts.sound.resolved} 個`);
    sound.value.dataset.tone = facts?.sound?.missing > 0 ? 'care' : '';
    setText(video.value, !facts || !facts.cameraCount ? '—' : `${facts.cameraCount} 台${facts.activeCamera ? `・${facts.activeCamera} を表示` : ''}`);
    const langs = { ja: '日本語', en: 'English', fr: 'Français' };
    setText(language.value, !facts?.lang ? '—' : `${langs[facts.lang] || facts.lang}${facts.relief === true ? '・軽減あり' : facts.relief === false ? '・軽減なし' : ''}`);
    observedId = fresh && typeof row?.deviceId === 'string' ? row.deviceId : '';
    register.hidden = !(observedId && !row?.expectedDeviceId) || ctx.demo;
    const parts = [row?.host || '', row?.shortId ? `登録 ID ${row.shortId}` : '登録 ID なし', row?.serial ? `USB ${row.serial}` : '',
      observedId ? `応答 ${observedId.slice(0, 6)}` : ''].filter(Boolean);
    setText(ids, parts.join(' ／ '));
  }
  return { node, update };
}

/** 博士タブレット 1 台。Quest の受付 → タブレットの応答 → 設定の反映、を別々に見せる。 */
export function createTabletUnit(id, ctx) {
  const name = `${QUEST_NAMES[id]} の博士タブレット`;
  const heading = h('h3', { text: name });
  const state = badge();
  const title = h('p.unit-title'), action = h('p.unit-action');
  const stages = [['Quest の受付', 'portalStatus'], ['タブレットの応答', 'connectionStatus'], ['設定の反映', 'reflectionStatus']]
    .map(([label, key]) => { const mark = badge(); return { key, mark, node: h('div.chain-step', {}, h('span.chain-name', { text: label }), mark) }; });
  const sent = fact('タブレットが送った設定'), applied = fact('Quest のいまの設定'), counts = fact('受信 / 反映');
  const meta = h('p.note.mono');
  const node = h('article.unit', { 'aria-label': name }, h('div.unit-head', {}, heading, state), title, action,
    h('div.chain', {}, ...stages.map(s => s.node)), h('dl.facts', {}, sent.node, applied.node, counts.node), meta);
  const langs = { ja: '日本語', en: 'English', fr: 'Français' };
  const choice = (lang, relief) => (langs[lang] && typeof relief === 'boolean' ? `${langs[lang]}・軽減${relief ? 'あり' : 'なし'}` : '—');

  function update(row, fresh, quest) {
    const registered = row?.registered !== false;
    const standby = row?.standby === true || quest?.standby === true;
    const questUp = fresh && quest && ['ok', 'warning'].includes(quest.status);
    const status = !registered || standby ? 'standby' : !fresh ? 'unknown' : questUp ? (row?.status || 'unknown') : 'unknown';
    node.dataset.state = status;
    setBadge(state, status, !registered ? '登録なし' : '');
    setText(heading, row?.tabletName ? `${row.tabletName}` : name);
    setText(title, !registered ? 'この Quest 用のタブレットは登録されていません' : standby ? '予備です。今日の判定には入れていません'
      : !fresh ? 'いまの状態は分かりません' : !questUp ? `${QUEST_NAMES[id]} の応答待ちです` : (row?.title || '未確認'));
    setText(action, !registered ? '1 台のタブレットで両方を回すなら、スタッフ設定で接続先を切り替えます。'
      : standby || !fresh ? '' : !questUp ? `タブレットの状態は Quest から聞いています。先に ${QUEST_NAMES[id]} を起動します。` : (row?.action || ''));
    action.hidden = !action.textContent;
    const live = registered && !standby && questUp;
    for (const stage of stages) setBadge(stage.mark, live ? row?.[stage.key] || 'unknown' : 'unknown');
    setText(sent.value, live ? choice(row?.requestedLang, row?.requestedRelief) : '—');
    setText(applied.value, live ? choice(row?.lang, row?.relief) : '—');
    setText(counts.value, live && Number.isFinite(row?.received) ? `${row.received} 回 / ${number(row.applyCount, ' 回')}` : '—');
    setText(meta, [row?.tabletModel, row?.tabletSerial ? `端末番号 ${row.tabletSerial}` : '', row?.tabletHost || '',
      live && Number.isFinite(row?.ageSec) ? `${ago(row.ageSec)}に応答` : ''].filter(Boolean).join(' ／ '));
  }
  return { node, update };
}
