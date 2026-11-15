// 困ったときの面。症状から入り、最初にやることを 1 つ出す。
// 出どころは .claude/skills/onsite と .claude/memory（onsite_day_ops / unexpected_event_recovery /
// no_internet_autojoin_kill / quest_fleet_two_devices）。手順を変えたら両方を直す。
import { h, setText, confirmButton } from './ui.js';

export const SYMPTOMS = [
  { id: 'all-down', title: '全部がいっぺんに落ちた',
    first: '卓の PC が展示の網に居るかを先に見ます。',
    steps: ['「準備」の 01「回線と卓」で、この PC の IP が 192.168.10.10 かを見ます。',
      '違っていたら、LAN ケーブルとルーターの電源を見て、PowerShell で ipconfig /renew "イーサネット" を実行します。',
      'それでも戻らなければ、ルーターのランプを見ます。'],
    why: '3 台が同時に壊れることはめったにありません。PC が有線の IP を失うと、カメラも Quest も全部が落ちたように見えます。',
    link: ['ready/desk', '回線と卓を見る'] },
  { id: 'camera-black', title: 'カメラが 1 台だけ黒い・砂嵐',
    first: 'そのカメラの「起こし直す」を押します。',
    steps: ['「本番」か「準備」のカメラで、止まっている台の「起こし直す」を 2 度押します。10 秒ほどで映像が戻ります。',
      '押せないときは、端末の画面を点けて配信アプリ（FixedCam Streamer）を前に出します。',
      '体験の最中なら、いまの体験が終わってからにします。起こし直しのあいだ、そのカメラは砂嵐になります。'],
    why: '画面が消えると、アプリは生きたまま配信だけが止まります。',
    link: ['live', '本番の面を見る'] },
  { id: 'camera-30min', title: 'カメラが 30 分おきに止まる',
    first: 'その端末に充電ケーブルが挿さっているかを見ます。',
    steps: ['挿さっていなければ挿します。', '挿したあと、止まっていれば「起こし直す」を押します。'],
    why: '画面を消さない設定は、充電中にしか効きません。挿していない端末は 30 分で画面が落ちます。電池の残量は関係ありません。' },
  { id: 'all-flicker', title: '3 台が同時に、周期的に途切れる',
    first: 'Quest の設定パネルを閉じます。',
    steps: ['下のボタンを 2 度押します（Quest が USB か無線でこの PC につながっている必要があります）。',
      'つながっていなければ、Quest を被って設定の画面を閉じます。'],
    why: 'Quest の設定パネルが開いたままだと、10 秒ごとに Wi-Fi を探し直し、そのたびに 3 台の映像が同時に途切れます。',
    fix: 'panel' },
  { id: 'quest-old', title: '卓では映るのに Quest に出ない・演出が古い',
    first: 'その Quest に入っているアプリの版を見ます。',
    steps: ['「準備」の 06「アプリの版」で、Quest の行が「済み」かを見ます。',
      '違っていたら、焼いたアプリを入れ直します（シュビーに頼むか、py -3.11 tools\\quest-fleet.py sync）。'],
    why: '実機は、焼いたアプリに入っている素材だけで動きます。卓で素材を直しても、焼き直すまで実機には出ません。',
    link: ['ready/build', 'アプリの版を見る'] },
  { id: 'stuck', title: 'Quest が固まった・体験を途中でやめたい',
    first: '博士タブレットの「困ったとき」から「体験を中断してやり直す」を 1.5 秒長押しします。',
    steps: ['体験者に声を掛け、HMD を外してもらいます。', 'タブレットの「困ったとき → 体験を中断してやり直す」を 1.5 秒長押しします。',
      'タブレットに「準備ができました」と出たら、次の体験者を迎えます。',
      'タブレットが効かないときだけ、Quest を被って右 A を 2 秒長押しします。'],
    never: ['固まった Quest の電源ボタンを押さない', '押すと休止に入り、右 A もタブレットの中断も届かなくなります。'],
    why: '外した Quest は休止します。休止中は右コントローラが読まれないので、被らずに抜けられるタブレットの中断を先に使います。' },
  { id: 'pause', title: '展示をいったん止めたい',
    first: '博士タブレットのスタッフ画面の右上「展示を一時中止する」を押します。',
    steps: ['来場者向けの全画面が出ます。文面はその場で書き換えられます。',
      '再開するときは、右下の「スタッフ専用（長押し）」を 1.5 秒押し、「展示を再開する」を押します。'],
    why: 'タブレットを開き直しても、中止の画面へ戻ります。' },
  { id: 'tablet-closed', title: '博士タブレットのアプリが閉じた・応答が無い',
    first: 'ホーム画面の「廻リ視 博士」を開きます。',
    steps: ['アプリを開き、スタッフ設定でクエスト α か β を選びます。', '「接続を確認する」を押します。',
      '「準備」の 04「博士タブレット」が済みに戻るかを見ます。'],
    why: 'タブレットの応答は 30 秒で古くなります。画面を閉じたり別のアプリを開いたりすると止まります。',
    link: ['ready/tablet', '博士タブレットを見る'] },
  { id: 'wrong-wifi', title: '端末が別の Wi-Fi につながった',
    first: '端末の Wi-Fi 設定で、保存済みの kougaku-lab-exp-a を選び直します。',
    steps: ['端末の設定を開き、Wi-Fi の一覧から kougaku-lab-exp-a を手で選びます。'],
    never: ['Wi-Fi を切って入れ直さない・保存済みの網を消さない', '入れ直すと、保存済みの別の網へ全台が移ることがあります。画面には何も出ません。'] },
  { id: 'morning-wifi', title: '朝、電源を入れても全機が Wi-Fi に戻らない',
    first: '各端末の Wi-Fi 設定で、kougaku-lab-exp-a を 1 度だけ手で選び直します。',
    steps: ['Quest と配信スマホのそれぞれで、Wi-Fi の一覧から手で選びます。',
      '戻ったあと、USB でつないで「無線を開けて」とシュビーに頼みます（py -3.11 tools/onsite.py adb-open）。次からは起きなくなります。'],
    why: '展示の網にはインターネットがありません。Android はそれを使えない網と見なし、自動でつなぐのをやめます。つながっているあいだは何も起きず、電源を入れ直したときに初めて戻らなくなります。' },
  { id: 'no-restart', title: '「起こし直す」が押せない',
    first: '端末で配信アプリを手で開きます。',
    steps: ['端末の画面を点けて、配信アプリを前に出します。',
      '無線で直せるように戻すには、端末を USB で PC につなぎ、py -3.11 tools/onsite.py adb-open を実行します。',
      'PC を再起動しただけなら USB は要りません。adb connect 192.168.10.21:5555 のように、3 台へつなぎ直します。'],
    why: '無線で端末を直す口は、端末を再起動すると閉じます。' },
  { id: 'camera-moved', title: 'カメラを動かしてしまった',
    first: 'カメラの向きを戻し、無人の写真と重ねて確かめます。',
    steps: ['「素材」の「無人の写真」で、採用中の写真といまの映像を重ねます。壁と床の線が合うまで向きを戻します。',
      '戻せないときは、無人の写真を撮り直して採用し、焼き直します。較正も取り直します。'],
    why: 'シミ・手形・人形は、カメラの構図に合わせて作ってあります。構図がずれると、その分だけ絵がずれます。',
    link: ['shoot/plates', '無人の写真を見る'] },
  { id: 'desk-down', title: 'この画面が開かない・卓が落ちた',
    first: 'PC で py -3.11 tools/onsite.py serve を実行します。',
    steps: ['PC のプロジェクトのフォルダで実行します。すでに動いていれば何もしません。'],
    never: ['卓を 2 つ立てない', '互いの設定を巻き戻し合います。'],
    why: '卓が落ちても、体験は焼いたアプリの値で最後まで動きます。ただし、音やコントローラの異常がどこにも出なくなります。' },
  { id: 'closing', title: '閉場するとき・次の日も続けるとき',
    first: 'カメラは動かさずに、Quest・コントローラ・博士タブレットを充電します。',
    steps: ['次の日も同じ会場なら、カメラ A・B・C の位置と向きはそのままにします。',
      '端末の電源を切ると、無線で直す口が閉じます。翌朝は USB でつないで py -3.11 tools/onsite.py adb-open を実行します。',
      '翌朝は「準備」の面を上から見直します。無人の写真は、いまの映像と重ねて確かめれば、撮り直さずに使えます。'],
    why: 'カメラを動かすと、無人の写真・較正・シミや手形の位置がまとめてずれます。',
    link: ['ready', '準備の面を見る'] },
];

export function mountHelp(root, ctx) {
  const links = SYMPTOMS.map(s => h('a', { href: `#help/${s.id}`, text: s.title }));
  const list = h('ul.symptoms', { 'aria-label': '症状' }, ...links.map(a => h('li', {}, a)));
  const title = h('h2'), first = h('p.first'), steps = h('ol.steps'), never = h('div.never'), why = h('p.note');
  const actions = h('div.actions'), status = h('p.message', { role: 'status' });
  const remedy = h('div.remedy', {}, h('a.back', { href: '#help', text: '一覧へ戻る' }),
    h('div.panel', {}, title, first, steps, never, actions, status, why));
  const layout = h('div.help-layout', {}, list, remedy);
  root.append(h('header.view-head.hide-when-open', {}, h('h1', { id: 'help-heading', text: '困ったとき' }),
    h('p', { text: '症状を選ぶと、最初にやることが出ます。迷ったら、まず「準備」の面で、どの台が止まっているかを確かめます。' })), layout);

  const panelFix = confirmButton('Quest の設定パネルを閉じる', 'もう一度押すと実行', async () => {
    status.dataset.state = ''; setText(status, '閉じています…');
    try {
      const result = await ctx.request('/onsite/fix', { method: 'POST', body: { name: 'panel' }, timeout: 190_000 });
      status.dataset.state = result.ok ? 'ok' : 'error';
      setText(status, result.log || (result.ok ? '実行しました。' : '実行できませんでした。'));
    } catch (error) { status.dataset.state = 'error'; setText(status, `実行できませんでした。${error.message}`); }
  });

  function route(sub) {
    const chosen = SYMPTOMS.find(s => s.id === sub);
    const symptom = chosen || SYMPTOMS[0];
    layout.dataset.open = String(!!chosen); root.dataset.open = String(!!chosen);
    for (const [i, s] of SYMPTOMS.entries()) links[i].setAttribute('aria-current', String(s === symptom));
    setText(title, symptom.title); setText(first, symptom.first);
    steps.replaceChildren(...symptom.steps.map(text => h('li', {}, h('span', { text }))));
    never.hidden = !symptom.never;
    never.replaceChildren(...(symptom.never ? [h('strong', { text: `してはいけないこと: ${symptom.never[0]}` }), symptom.never[1]] : []));
    why.hidden = !symptom.why; setText(why, symptom.why || '');
    setText(status, ''); status.hidden = symptom.fix !== 'panel'; panelFix.disabled = ctx.demo;
    actions.replaceChildren(...[symptom.fix === 'panel' ? panelFix : null,
      symptom.link ? h('a.button', { href: `#${symptom.link[0]}`, text: symptom.link[1] }) : null].filter(Boolean));
    actions.hidden = !actions.children.length;
  }
  return { route, update() {} };
}
