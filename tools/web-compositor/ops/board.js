// 準備の面。「開場できるか」と「次にやること」を 1 枚で答える。
import { h, setText, badge, setBadge, fact, role, when, ago, copyText } from './ui.js';
import { CAMERA_IDS, QUEST_IDS, QUEST_NAMES, GATE_IDS, GATE_TITLES, humanChecks, summarizeContent } from './model.js';
import { createMonitor, createQuestUnit, createTabletUnit } from './parts.js';

const KICKERS = { ok: '開場まで', warning: '開場まで', error: '開場まで', unknown: '開場まで' };

export function mountBoard(root, ctx) {
  // ---- 見出し: 開場できるか ----
  const headline = h('h1.verdict-headline', { id: 'ready-heading' });
  const kicker = h('p.verdict-kicker');
  const nextText = h('p');
  const nextLink = h('a.button.primary', { text: 'この行を開く' });
  const verdictNode = h('div.verdict.hide-when-open', {}, kicker, headline, h('div.verdict-next', {}, nextText, nextLink));
  const meterLinks = GATE_IDS.map((id, i) => h('a', { href: `#ready/${id}`, title: GATE_TITLES[id] },
    h('span', { text: `${String(i + 1).padStart(2, '0')} ${GATE_TITLES[id]}` })));
  const meter = h('nav.meter.hide-when-open', { 'aria-label': '確かめることの進み具合' }, ...meterLinks);

  // ---- 一覧 ----
  const rows = GATE_IDS.map((id, i) => {
    const mark = badge();
    const summary = h('span.gate-summary');
    const link = h('a.gate-row', { href: `#ready/${id}` },
      h('span.gate-no', { text: String(i + 1).padStart(2, '0'), 'aria-hidden': 'true' }),
      h('span.gate-title', { text: GATE_TITLES[id] }), mark, summary);
    return { id, link, mark, summary, node: h('li', {}, link) };
  });
  const list = h('ol.gate-list', { 'aria-label': '開場までに確かめること' }, ...rows.map(r => r.node));

  // ---- 詳細 ----
  const detailTitle = h('h2');
  const detailBadge = badge();
  const detailSummary = h('strong');
  const detailAction = h('span');
  const sections = {
    desk: deskSection(ctx), cameras: camerasSection(ctx), quests: questsSection(ctx), tablet: tabletSection(ctx),
    materials: materialsSection(ctx), build: buildSection(ctx), inspection: inspectionSection(ctx), human: humanSection(ctx),
  };
  for (const [id, section] of Object.entries(sections)) { section.node.hidden = true; section.node.dataset.gate = id; }
  const detail = h('div.gate-detail', {},
    h('a.back', { href: '#ready', text: '一覧へ戻る' }),
    h('div.panel', {}, h('div.panel-head', {}, detailTitle, detailBadge),
      h('p.gate-state', {}, detailSummary, detailAction), ...Object.values(sections).map(s => s.node)));
  const board = h('div.board', {}, list, detail);
  root.append(verdictNode, meter, board);

  let chosen = '';   // URL で選ばれた行（スマホではこれがあるときだけ詳細を出す）
  let shown = '';

  function route(sub) {
    chosen = GATE_IDS.includes(sub) ? sub : '';
    board.dataset.open = String(!!chosen);
    root.dataset.open = String(!!chosen);
    if (!chosen) for (const monitor of sections.cameras.monitors) monitor.close();
  }

  function update(state, d) {
    const v = d.verdict;
    verdictNode.dataset.state = v.status;
    setText(kicker, v.ready ? (v.cautions ? `注意が ${v.cautions} 件あります` : '機器・素材・アプリの版・人の確認がそろいました') : KICKERS[v.status]);
    setText(headline, v.headline);
    const nextKey = JSON.stringify([v.next, v.cautions]);
    if (nextText.dataset.key !== nextKey) {   // 毎秒は作り直さない（選択と読み上げが切れる）
      nextText.dataset.key = nextKey;
      if (v.next) nextText.replaceChildren(h('strong', { text: `次は「${v.next.title}」。` }), v.next.summary,
        v.next.action ? h('span.next-action', { text: v.next.action }) : '');
      else nextText.replaceChildren(v.cautions ? '注意の行は、開場を止めません。時間があれば直します。' : '会期中は「本番」の面で見張ります。');
    }
    nextLink.href = v.next ? `#ready/${v.next.id}` : '#live';
    setText(nextLink, v.next ? 'この行を開く' : '本番の面へ');

    // 行を選んでいないあいだは「次にやること」の行を出す。選んだら、その行に留まる。
    const current = chosen || v.next?.id || shown || 'desk';
    shown = current;
    for (const [i, gate] of d.gates.entries()) {
      const row = rows[i];
      row.link.dataset.state = gate.status;
      setBadge(row.mark, gate.status);
      setText(row.summary, gate.summary);
      row.link.setAttribute('aria-current', String(gate.id === current));
      meterLinks[i].dataset.state = gate.status;
      meterLinks[i].setAttribute('aria-current', String(gate.id === current));
    }
    const gate = d.gates.find(g => g.id === current);
    setText(detailTitle, gate.title);
    setBadge(detailBadge, gate.status);
    setText(detailSummary, gate.summary);
    setText(detailAction, gate.action);
    for (const [id, section] of Object.entries(sections)) {
      section.node.hidden = id !== current;
      if (id === current) section.update(state, d);
    }
    if (current !== 'cameras') for (const monitor of sections.cameras.monitors) monitor.close();
  }

  return { route, update, hide() { for (const monitor of sections.cameras.monitors) monitor.close(); shown = ''; } };
}

const block = (...children) => h('div', { style: 'display:grid;gap:16px' }, ...children);

// ---- 01 回線と卓 ----
function deskSection(ctx) {
  const address = fact('この PC の IP'), mode = fact('この画面でできること'), watch = fact('自動の見張り');
  const node = block(h('dl.facts', {}, address.node, mode.node, watch.node),
    h('div.hints', {},
      h('p', {}, h('strong', { text: 'ルーターを先に起動します。' }), 'PC は有線でつなぎ、IP は 192.168.10.10 です。インターネットは要りません。'),
      h('p', {}, h('strong', { text: '全部がいっぺんに「まだ」になったら、まずここを見ます。' }), '3 台が同時に壊れることはめったにありません。PC が網から外れた姿と、現場が全滅した姿は同じ顔で出ます。'),
      h('p', {}, h('strong', { text: '卓は 1 つだけ動かします。' }), '2 つ立つと、互いの設定を巻き戻し合います。')),
    role('人', 'ルーターの電源、LAN ケーブル、PC へのサインイン（サインインすると卓が自動で立ちます）。'),
    role('シュビー', 'Quest と博士タブレットを USB でつないで「起動して」と頼むと、アプリの起動まで確かめます（onsite.py setup）。'));
  return { node, update(state, d) {
    const desk = d.offline ? null : state.snapshot?.desk;
    setText(address.value, !desk ? '—' : desk.status === 'ok' ? desk.expectedHost : (desk.addresses?.join('、') || 'なし'));
    address.value.dataset.tone = desk && desk.status !== 'ok' ? 'bad' : '';
    const caps = state.capabilities;
    setText(mode.value, !caps ? '—' : caps.shooting ? '撮影と採用ができます' : '見ることと復旧だけです（撮影は卓の PC から）');
    const w = d.offline ? null : state.snapshot?.watch;
    setText(watch.value, !w ? '—' : w.running ? `動いています（${ago(w.ageSec)}）` : '動いていません');
    watch.value.dataset.tone = w && !w.running ? 'care' : '';
  } };
}

// ---- 02 カメラ ----
function camerasSection(ctx) {
  const monitors = CAMERA_IDS.map(id => createMonitor(id, ctx));
  const node = block(h('div.monitors', {}, ...monitors.map(m => m.node)),
    h('div.hints', {},
      h('p', {}, h('strong', { text: '3 台とも充電ケーブルを挿します。' }), '挿していないと 30 分で画面が消え、アプリは生きたまま配信だけ止まります。'),
      h('p', {}, h('strong', { text: '照明を決めて数秒待ってから、端末の錠の印で露出と色を固定します。' }), '自動のままだと、素材を撮った時と本番で色が変わります。'),
      h('p', {}, h('strong', { text: 'Wi-Fi は入れ直しません。' }), '入れ直すと、保存済みの別の網へ全台が移ることがあります。')));
  return { node, monitors, update(state, d) {
    for (const [i, id] of CAMERA_IDS.entries())
      monitors[i].update(state.snapshot?.cameras?.find(c => c.id === id), d.live);
  } };
}

// ---- 03 Quest ----
function questsSection(ctx) {
  const units = QUEST_IDS.map(id => createQuestUnit(id, ctx));
  const node = block(h('div.units', {}, ...units.map(u => u.node)),
    h('div.hints', {},
      h('p', {}, h('strong', { text: '位置合わせは Quest ごとに取ります。' }), '被って右トリガーを 2 秒。床の×印の上で右 A を 0.5 秒ずつ。線が実物と合っているのを見てから B で決めます。'),
      h('p', {}, h('strong', { text: '外すと Quest は休止します。' }), '休止中は応答が止まります。電源ボタンを 1 度押すと戻ります。'),
      h('p', {}, h('strong', { text: '今日使わない機は、登録で予備にできます。' }), '予備の機は判定から外れ、この画面には「予備」と出ます。')));
  return { node, update(state, d) {
    for (const [i, id] of QUEST_IDS.entries()) units[i].update(state.snapshot?.quests?.find(q => q.id === id), d.live);
  } };
}

// ---- 04 博士タブレット ----
function tabletSection(ctx) {
  const units = QUEST_IDS.map(id => createTabletUnit(id, ctx));
  const node = block(h('div.units', {}, ...units.map(u => u.node)),
    h('div.hints', {},
      h('p', {}, h('strong', { text: 'ホーム画面の「廻リ視 博士」を開きます。' }), 'スタッフ設定でクエスト α か β を選び、「接続を確認する」を押します。'),
      h('p', {}, h('strong', { text: '開場前に 1 度、言語と軽減を送ります。' }), 'Quest のいまの設定が送った値と同じになれば、ここが済みになります。'),
      h('p', {}, h('strong', { text: '設定はこの卓を通りません。' }), 'タブレットが Quest へ直接送ります。体験者 1 人ごとの進め方も、タブレットの画面が案内します。')));
  return { node, update(state, d) {
    for (const [i, id] of QUEST_IDS.entries())
      units[i].update(state.snapshot?.tablets?.find(t => t.id === id), d.live, state.snapshot?.quests?.find(q => q.id === id));
  } };
}

// ---- 05 会場の素材 ----
function materialsSection(ctx) {
  const shotRows = h('ul.rows');
  const thumbs = h('div.thumbs');
  const old = h('p.note');
  const placed = h('input', { type: 'checkbox' }), watched = h('input', { type: 'checkbox' });
  placed.addEventListener('change', () => ctx.setReview('placed', placed.checked));
  watched.addEventListener('change', () => ctx.setReview('watched', watched.checked));
  const node = block(
    h('div', {}, h('h3.section-title', { text: '人形視点（スマホを手に持って撮る 5 本）' }), shotRows,
      h('p', { style: 'margin-top:12px' }, h('a.link', { href: '#shoot/pov', text: '撮影と採用の画面へ' }))),
    h('div', {}, h('h3.section-title', { text: '無人の写真（カメラ A・B・C を 1 枚ずつ）' }), thumbs,
      h('p', { style: 'margin-top:12px' }, h('a.link', { href: '#shoot/plates', text: 'いまの映像と重ねて確かめる' }))),
    old,
    h('div.checklist', {},
      h('label.check', {}, placed, h('span', {}, h('span.check-title', { text: 'いまのカメラの位置と照明で撮った素材だ' }),
        h('span.check-detail', { text: '無人の写真をいまの映像と重ねて、壁と床の線が合うのを見ました。合わなければ撮り直します。' }))),
      h('label.check', {}, watched, h('span', {}, h('span.check-title', { text: '採用中の素材を見た' }),
        h('span.check-detail', { text: '「見比べ」で、シミ・手形・人形がカメラの映像の上で正しい所に出るのを見ました。' })))),
    h('p.note', { text: 'この 2 つの印は、素材かカメラの傾きが変わるか、日付が変わると外れます。' }),
    role('人', '撮ること。構図と照明を決めること。合っているかを見ること。'),
    role('シュビー', '尺と形式の検査、頭の切り出し、会場に合わせたシミや手形の作り直し。'));
  return { node, update(state, d) {
    const shots = d.shots, plates = d.plates;
    const shotKey = JSON.stringify(shots?.rows || null);
    if (shotRows.dataset.key !== shotKey) {
      shotRows.dataset.key = shotKey;
      shotRows.replaceChildren(...(shots ? shots.rows.map(row => h('li.row', {},
        badge(row.adopted ? 'ok' : 'unknown', row.adopted ? '採用' : 'まだ'),
        h('div.row-body', {}, h('p.row-title', { text: row.label }),
          h('p.row-detail', { text: row.adopted ? `${row.collectedAt ? `${when(row.collectedAt)} に卓へ回収` : '回収した日時は不明'}` : row.reason }))))
        : [h('li.empty', { text: '撮影の状況を読めていません。' })]));
      if (shots && !shots.rows.length) shotRows.replaceChildren(h('li.empty', { text: 'いまの台本では人形視点を使いません。' }));
    }
    const plateKey = JSON.stringify(plates?.rows || null);
    if (thumbs.dataset.key !== plateKey) {
      thumbs.dataset.key = plateKey;
      thumbs.replaceChildren(...CAMERA_IDS.map(id => {
        const row = plates?.rows.find(r => r.cameraId === id);
        return h('figure.thumb', {},
          row?.adopted ? h('img', { src: row.url, alt: `カメラ ${id} の無人の写真`, loading: 'lazy', width: 640, height: 480 }) : h('div.thumb-empty', { text: 'まだ' }),
          h('figcaption', { text: `${id}　${row?.adopted ? (row.capturedAt ? when(row.capturedAt) : '撮った日時は不明') : '採用なし'}` }));
      }));
    }
    const stale = shots && plates && (shots.today < shots.complete || plates.today < plates.complete);
    old.hidden = !stale;
    setText(old, stale ? '今日より前に撮った素材が入っています。カメラと照明を動かしていなければ、そのまま使えます。動かしたなら撮り直します。' : '');
    const complete = !!(shots && plates && shots.complete === shots.required && plates.complete === plates.required);
    const fresh = summarizeContent(state.contentFailed ? null : state.content, d.now).fresh && state.content?.preparation?.status === 'ok';
    placed.checked = d.review.placed === true; watched.checked = d.review.watched === true;
    placed.disabled = watched.disabled = !complete || !fresh;   // 見本でも印は付けられる（この端末の中だけに残る）
  } };
}

// ---- 06 アプリの版 ----
function buildSection(ctx) {
  const stageDefs = [['preparation', '採用中の素材'], ['bundle', '書き出し'], ['apk', '焼いたアプリ']];
  const steps = [...stageDefs.map(([key, label]) => ({ key, label })), ...QUEST_IDS.map(id => ({ quest: id, label: QUEST_NAMES[id] }))]
    .map(def => {
      const mark = badge(), title = h('span.chain-title'), id = h('span.chain-id');
      return { ...def, mark, title, id, node: h('div.chain-step', {}, h('span.chain-name', { text: def.label }), mark, title, id) };
    });
  const copy = h('button.button.primary', { type: 'button', text: 'ビルドの依頼文をコピー' });
  const copyStatus = h('p.message', { role: 'status' });
  const effects = h('ul.rows');
  const effectSummary = h('span.count');
  let contentId = '';
  copy.addEventListener('click', () => copyText(
    `廻リ視（fixedcam）を焼いて、使う Quest に入れてください。\n人が確かめた素材の版: ${contentId}\nまず、いまの採用がこの版と同じかを照合してください。違っていたら、人がもう一度素材を見る必要があります。\n同じなら .\\tools\\unity.ps1 build fixedcam で焼き、py -3.11 tools\\quest-fleet.py sync で入れ、準備の画面の「アプリの版」が済みになるまで確かめてください。\n入れたあと、人が被って映像と音を確かめます。`, copyStatus));
  const node = block(h('div.chain', {}, ...steps.map(s => s.node)),
    h('div.actions', {}, copy), copyStatus,
    h('div.hints', {},
      h('p', {}, h('strong', { text: '実機は、焼いたアプリに入っている素材だけで動きます。' }), '素材を採用し直したら、焼き直して、使う Quest すべてに入れます。'),
      h('p', {}, h('strong', { text: 'ビルドのあとで変えられるのは、言語とホラー軽減だけです。' }), '体験者が博士タブレットで選びます。')),
    role('人', '素材を見て決めること。入れたあとに被って確かめること。'),
    role('シュビー', '焼くこと、入れること、版の照合。回線が無いときは、下の 2 行を PowerShell で順に実行します。'),
    h('code.code', { text: '.\\tools\\unity.ps1 build fixedcam\npy -3.11 tools\\quest-fleet.py sync' }),
    h('details', {}, h('summary', {}, '演出ごとの照合 ', effectSummary), effects));
  return { node, update(state, d) {
    const content = state.contentFailed ? null : state.content;
    const fresh = summarizeContent(content, d.now).fresh;
    const short = value => (typeof value === 'string' && value ? value.replaceAll('-', '').slice(0, 10) : '');
    for (const step of steps) {
      const row = !fresh ? null : step.quest ? content.quests?.find(q => q.id === step.quest) : content[step.key];
      const standby = row?.standby === true;
      setBadge(step.mark, standby ? 'standby' : row?.status || 'unknown');
      setText(step.title, standby ? '予備' : row?.title || '照合できていません');
      setText(step.id, [short(row?.contentId) && `素材 ${short(row.contentId)}`, short(row?.buildGuid) && `アプリ ${short(row.buildGuid)}`].filter(Boolean).join(' ／ '));
      step.node.dataset.state = standby ? 'standby' : row?.status || 'unknown';
    }
    contentId = fresh ? content.preparation?.contentId || '' : '';
    const materialsOk = d.gates.find(g => g.id === 'materials')?.status === 'ok';
    copy.disabled = ctx.demo || !contentId || !materialsOk;
    copy.title = copy.disabled ? '「会場の素材」が済みになると使えます。' : '';
    const rows = fresh ? content.effects || [] : [];
    const key = JSON.stringify(rows);
    if (effects.dataset.key !== key) {
      effects.dataset.key = key;
      const pending = rows.filter(e => !(e.sourceReady && e.bundled && e.inApk)).length;
      setText(effectSummary, rows.length ? (pending ? `${rows.length} 件中 ${pending} 件が未反映` : `${rows.length} 件`) : '');
      effects.replaceChildren(...(rows.length ? rows.map(effect => {
        const done = effect.sourceReady && effect.bundled && effect.inApk;
        return h('li.row', {}, badge(done ? 'ok' : effect.sourceReady === false ? 'error' : 'unknown', done ? '入っている' : '未反映'),
          h('div.row-body', {}, h('p.row-title', { text: effect.name || effect.id }),
            h('p.row-detail', { text: `素材 ${effect.assetCount ?? '—'} 個 ／ ファイル ${effect.sourceReady ? 'あり' : 'なし'} ／ 書き出し ${effect.bundled ? '済み' : 'まだ'} ／ アプリ ${effect.inApk ? '済み' : 'まだ'}` })));
      }) : [h('li.empty', { text: '照合できていません。' })]));
    }
  } };
}

// ---- 07 詳しい点検 ----
function inspectionSection(ctx) {
  const run = h('button.button.primary', { type: 'button', text: '詳しい点検を実行' });
  const message = h('p.message', { role: 'status' });
  const results = h('ul.rows');
  run.addEventListener('click', () => ctx.runInspection());
  const node = block(
    h('p', { text: 'この PC から端末の中まで調べます。入っているアプリの版、無線で直せるか、傾きと較正の差、露出の固定、熱と電池。40 秒ほどかかります。' }),
    h('div.actions', {}, run), message, results,
    h('p.note', { text: '結果は 30 分で古くなります。Quest は本番と同じ台数を起動してから実行します。' }));
  const tone = { ng: 'error', warn: 'warning', skip: 'unknown', ok: 'ok' };
  const words = { ng: '直す', warn: '注意', skip: '未確認', ok: '済み' };
  return { node, update(state, d) {
    const busy = state.inspectionBusy === true;
    run.disabled = ctx.demo || busy || d.offline;
    if (busy) run.setAttribute('aria-busy', 'true'); else run.removeAttribute('aria-busy');
    const result = state.inspection;
    const rows = Array.isArray(result?.rows) ? result.rows : [];
    const age = result ? Math.max(0, (d.now - Date.parse(result.at)) / 1000) : NaN;
    message.dataset.state = state.inspectionError ? 'error' : '';
    setText(message, busy ? '調べています。40 秒ほどかかります…' : state.inspectionError ? `点検できませんでした。${state.inspectionError}`
      : !result ? 'まだ実行していません。'
        : `${ago(age)}の結果です。直す ${rows.filter(r => r.state === 'ng').length} 件 ／ 注意 ${rows.filter(r => r.state === 'warn').length} 件 ／ 未確認 ${rows.filter(r => r.state === 'skip' && r.required !== false).length} 件`);
    const key = JSON.stringify(rows);
    if (results.dataset.key !== key) {
      results.dataset.key = key;
      const order = { ng: 0, warn: 1, skip: 2, ok: 3 };
      results.replaceChildren(...rows.slice().sort((a, b) => (order[a.state] ?? 2) - (order[b.state] ?? 2)).map(r =>
        h('li.row', {}, badge(tone[r.state] || 'unknown', r.required === false && r.state === 'skip' ? '対象外' : words[r.state] || '未確認'),
          h('div.row-body', {}, h('p.row-title', { text: r.label || '' }), h('p.row-detail', { text: r.detail || '' }),
            r.fix && r.state !== 'ok' ? h('p.row-fix', { text: r.fix }) : null))));
    }
  } };
}

// ---- 08 人が確かめること ----
function humanSection(ctx) {
  const list = h('div.checklist');
  const reason = h('p.message');
  const reset = h('button.button.small', { type: 'button', text: '印を全部外す' });
  reset.addEventListener('click', () => ctx.clearChecks());
  const inputs = new Map();
  const node = block(h('p', { text: '機械には分からないことを、現場で見て印を付けます。印は 1 時間で外れ、機器が入れ替わっても外れます。' }),
    reason, list, h('div.actions', {}, reset),
    h('p.note', { text: '左グリップの撮影は「撮影を切って」とシュビーに頼むか、py -3.11 tools/quest-shots.py off を実行してからアプリを起動し直します。' }));
  return { node, update(state, d) {
    const checks = humanChecks(state.snapshot);
    const key = checks.map(c => c.id).join(',');
    if (list.dataset.key !== key) {
      list.dataset.key = key; inputs.clear();
      list.replaceChildren(...checks.map(check => {
        const input = h('input', { type: 'checkbox', name: check.id });
        input.addEventListener('change', () => ctx.setCheck(check.id, input.checked));
        inputs.set(check.id, input);
        return h('label.check', {}, input, h('span', {}, h('span.check-title', { text: check.title }), h('span.check-detail', { text: check.detail })));
      }));
    }
    for (const [id, input] of inputs) { input.checked = d.checks[id] === true; input.disabled = !d.checksOpen; }
    reason.dataset.state = d.checksOpen ? '' : 'warning';
    setText(reason, d.checksOpen ? '' : '機器の行（01〜04）に「対処」か「まだ」が残っているあいだは、印を付けられません。');
    reason.hidden = d.checksOpen;
  } };
}
