// 本番の面。会期中に、いま人が動くことだけを上に出す。
import { h, setText, badge } from './ui.js';
import { CAMERA_IDS, QUEST_IDS } from './model.js';
import { createMonitor, createQuestUnit, createTabletUnit } from './parts.js';

export function mountLive(root, ctx) {
  const kicker = h('p.verdict-kicker', { text: '本番' });
  const headline = h('h1.verdict-headline', { id: 'live-heading' });
  const lead = h('p');
  const head = h('div.verdict', {}, kicker, headline, h('div.verdict-next', {}, lead));
  const alerts = h('ul.alerts', { 'aria-label': 'いま動くこと' });

  const quests = QUEST_IDS.map(id => createQuestUnit(id, ctx, { live: true }));
  const tablets = QUEST_IDS.map(id => createTabletUnit(id, ctx));
  const monitors = CAMERA_IDS.map(id => createMonitor(id, ctx));
  const watchState = badge();
  const watchNote = h('p.note');
  const log = h('ol.log');

  root.append(head, alerts,
    h('section', {}, h('h2.section-title', { text: '体験のいま' }), h('div.units', {}, ...quests.map(q => q.node)),
      h('p.note', { style: 'margin-top:16px', text: '体験者 1 人ごとの進め方は、博士タブレットの画面が案内します。この卓から体験は操作しません。' })),
    h('section', {}, h('h2.section-title', { text: 'カメラ' }), h('div.monitors', {}, ...monitors.map(m => m.node))),
    h('section', {}, h('details', {}, h('summary', { text: '博士タブレット' }), h('div.units', { style: 'margin-top:8px' }, ...tablets.map(t => t.node)))),
    h('section', {}, h('div.unit-head', { style: 'margin-bottom:12px' }, h('h2.section-title', { style: 'margin:0', text: '見張りの記録' }), watchState),
      watchNote, log));

  function update(state, d) {
    const list = d.alerts;
    const worstLevel = list[0]?.level || 'ok';
    head.dataset.state = worstLevel;
    setText(headline, !list.length ? '異常なし' : `いま動くこと ${list.length} 件`);
    setText(lead, !list.length ? 'カメラ・Quest・博士タブレットは動いています。下は 5 秒ごとに更新します。'
      : '上から順に対処します。体験の最中なら、体験者の安全を先に見ます。');
    const key = JSON.stringify(list);
    if (alerts.dataset.key !== key) {
      alerts.dataset.key = key;
      alerts.replaceChildren(...list.map(a => h('li.alert', { dataset: { state: a.level } },
        h('span.alert-who', { text: a.who }), h('span.alert-text', { text: a.text }),
        a.action ? h('span.alert-action', { text: a.action }) : null)));
    }
    alerts.hidden = !list.length;
    for (const [i, id] of QUEST_IDS.entries()) {
      const quest = state.snapshot?.quests?.find(q => q.id === id);
      quests[i].update(quest, d.live);
      tablets[i].update(state.snapshot?.tablets?.find(t => t.id === id), d.live, quest);
    }
    for (const [i, id] of CAMERA_IDS.entries()) monitors[i].update(state.snapshot?.cameras?.find(c => c.id === id), d.live);

    const watch = d.offline ? null : state.snapshot?.watch;
    watchState.dataset.state = !watch ? 'unknown' : watch.running ? 'ok' : 'warning';
    setText(watchState.querySelector('.badge-text'), !watch ? '不明' : watch.running ? '動いています' : '止まっています');
    setText(watchNote, !watch ? '' : watch.running
      ? '20 秒ごとに全カメラを見ています。2 巡続けて止まっていたら、自動で起こし直します（1 台につき 90 秒に 1 回まで）。'
      : '見張りが動いていないあいだは、止まったカメラを誰も起こしません。シュビーに「見張って」と頼むか、py -3.11 tools/onsite.py watch を実行します。');
    const events = (watch?.events || []).slice().reverse();
    const logKey = JSON.stringify(events);
    if (log.dataset.key !== logKey) {
      log.dataset.key = logKey;
      log.replaceChildren(...(events.length ? events.map(e => h('li', {}, h('time', { text: e.at }), h('span', { text: e.text })))
        : [h('li.empty', { text: 'まだ記録はありません。' })]));
    }
  }
  return { update, hide() { for (const monitor of monitors) monitor.close(); } };
}
