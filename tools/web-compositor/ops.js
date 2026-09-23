import {CAMERA_IDS, QUEST_IDS, MANUAL_CHECKS, isFresh, signature, contentSignature, summarizeContent, summarizeOnsiteShots, validChecks, summarize} from './ops-model.js';

const $ = id => document.getElementById(id);
const demo = new URLSearchParams(location.search).get('demo');
const labels = {ok:'確認済み', warning:'要確認', error:'対処が必要', unknown:'未確認'};
const storageKey = demo ? 'mawarimi-ops-demo' : 'mawarimi-ops-checks-v1';
let snapshot = null, failed = false, busy = false, pollTimer, record = null, deepResult = null;
let contentSnapshot = null, contentFailed = false, contentBusy = false, contentTimer;
let shootProof = null, shootBusy = false;
const cameraViews = new Map(), questViews = new Map();
try { record = JSON.parse(sessionStorage.getItem(storageKey)); } catch {}

function el(tag, cls = '', text = '') {
  const n = document.createElement(tag); n.className = cls; n.textContent = text; return n;
}
function state(node, status) { node.dataset.state = status; node.textContent = labels[status] || labels.unknown; }
function num(value, unit = '', decimals = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? `${value.toFixed(decimals)}${unit}` : '未確認';
}
function saveRecord() { try { sessionStorage.setItem(storageKey, JSON.stringify(record)); } catch {} }
function metric(label) {
  const cell = el('div'), dt = el('dt', '', label), dd = el('dd', '', '未確認'); cell.append(dt,dd); return {cell,dd};
}
function closePreview(view) {
  view.image.removeAttribute('src'); view.preview.hidden = true;
  view.previewButton.textContent = '映像を見る'; view.previewButton.setAttribute('aria-expanded','false');
}

for (const [i,id] of CAMERA_IDS.entries()) {
  const card = el('article','camera-card'); card.setAttribute('aria-labelledby',`camera-${id}`);
  const head = el('div','camera-head'), text = el('div'), name = el('h3','',`カメラ ${id}`);
  name.id = `camera-${id}`;
  const address = el('p','mono',`192.168.10.${21+i}:8080`); text.append(name,address);
  head.append(el('span','camera-letter',id),text);
  const body = el('div','camera-state'), badge = el('span','state'), title = el('p','device-title','確認しています…');
  state(badge,'unknown'); body.append(badge,title);
  const metrics = el('dl','metrics');
  const frames = metric('新しい映像'), temp = metric('端末の温度'), battery = metric('電池残量');
  metrics.append(frames.cell,temp.cell,battery.cell);
  const next = el('div','camera-next'), action = el('p','','端末の状態を待っています。');
  next.append(el('p','next-label','次にすること'),action);
  const actions = el('div','camera-actions'), previewButton = el('button','','映像を見る'), restart = el('button','','この端末を起こし直す');
  previewButton.type = restart.type = 'button'; previewButton.disabled = restart.disabled = true;
  previewButton.setAttribute('aria-expanded','false'); previewButton.setAttribute('aria-controls',`preview-${id}`);
  restart.setAttribute('aria-label',`カメラ ${id} を起こし直す`); actions.append(previewButton,restart);
  const message = el('p','action-message'); message.hidden = true; message.setAttribute('role','status');
  const preview = el('figure','preview'); preview.id = `preview-${id}`; preview.hidden = true;
  const image = el('img'); image.alt = `カメラ ${id} の加工前の映像`;
  image.addEventListener('error', () => { message.hidden = false; message.textContent = '映像を開けません。端末の状態をもう一度確認してください。'; closePreview(view); });
  preview.append(image,el('figcaption','','加工前の映像です。Quest の合成表示は装着して確認します。'));
  const details = el('details'), detailBody = el('div','device-details'); details.append(el('summary','','接続と設定の詳細'),detailBody);
  card.append(head,body,metrics,next,actions,message,preview,details); $('cameras').append(card);
  const view = {card,address,badge,title,frames,temp,battery,action,previewButton,restart,message,preview,image,detailBody,restarting:false};
  cameraViews.set(id,view);
  previewButton.addEventListener('click', () => {
    if (!preview.hidden) return closePreview(view);
    const cam = snapshot?.cameras?.find(c => c.id === id);
    if (!cam?.identityOk || !isFresh(snapshot) || failed || demo) return;
    const u = new URL('/cam',location.href); u.port = String(Number(location.port || 80)+1);
    u.search = new URLSearchParams({host:cam.host,port:String(cam.port),path:'/video'}).toString();
    image.src = u.href; preview.hidden = false; previewButton.textContent = '映像を閉じる'; previewButton.setAttribute('aria-expanded','true');
  });
  restart.addEventListener('click', () => restartCamera(id));
}

for (const [i,id] of QUEST_IDS.entries()) {
  const card = el('article','quest-card'), top = el('div','quest-top'), name = el('h3','',`Quest ${i ? 'β' : 'α'}`), badge = el('span','state');
  state(badge,'unknown'); top.append(name,badge);
  const body = el('div','quest-body'), text = el('div'), title = el('div','device-title','応答を待っています'), address = el('p','mono',`192.168.10.${31+i}`), detail = el('p','note');
  text.append(title,address,detail);
  const link = el('span','portal-link','博士 UI :8090');
  body.append(text,link); card.append(top,body); $('quests').append(card); questViews.set(id,{badge,title,address,detail,link});
}

for (const check of MANUAL_CHECKS) {
  const label = el('label','manual-check'), input = el('input'); input.type = 'checkbox'; input.name = check.id; input.id = `check-${check.id}`;
  const copy = el('span'); copy.append(el('span','check-title',check.title),el('span','check-detail',check.detail)); label.append(input,copy); $('manual-checks').append(label);
  input.addEventListener('change', () => {
    const values = validChecks(record,snapshot); values[check.id] = input.checked;
    record = {signature:signature(snapshot),at:Date.now(),values}; saveRecord(); render();
  });
}

function render() {
  const checks = validChecks(record,snapshot), s = summarize(snapshot,checks,Date.now(),failed,deepResult,contentFailed?null:contentSnapshot,shootProof);
  $('overview-title').textContent = s.title; state($('overall-state'),s.status);
  $('online-count').textContent = s.observed; $('error-count').textContent = s.fresh ? s.blockers : '—';
  $('unknown-count').textContent = s.unknown; $('manual-count').textContent = s.pending;
  $('diagnostic-summary').textContent = s.diagnosticMissing ? 'アプリと素材の詳しい点検が必要です。下から実行できます。結果は 30 分間有効です。' :
    s.diagnosticIssues ? `詳しい点検に未確認または対処が必要な項目が ${s.diagnosticIssues} 件あります。下の結果を確認してください。` : 'アプリと素材の詳しい点検も確認済みです。';
  $('overview-detail').textContent = !s.fresh ? 'サーバーの起動と PC のネットワークを確認してください。古い機器の値は判定に使っていません。' :
    s.blockers ? '該当する機器の「次にすること」から対処します。担当の入れ替えは必要ありません。' :
    s.ready ? '自動で調べられる状態と装着して確認した結果がそろっています。動作中も接続を確認します。' :
    '演出の同梱と当日の接続を別々に確認します。最後に装着して映像と音を確かめます。';
  const next = !s.fresh || s.blockers || s.unknown || s.warnings ? ['devices','機器の状態と対処を見る'] :
    !s.contentReady ? ['content','演出の同梱を確認する'] : ['checks','開場前の確認へ進む'];
  $('primary-action').href = `#${next[0]}`;
  $('primary-action').firstChild.textContent = `${next[1]} `;
  renderContent();
  renderTablets(s.fresh);
  $('connection-error').hidden = !failed;
  if (failed) $('connection-error').textContent = '状態を取得できません。この PC の点検サーバーとネットワークを確認してください。接続が戻れば自動で確認を再開します。';
  if (snapshot?.observedAt) {
    const age = Math.max(0,Math.floor(Date.now()/1000-snapshot.observedAt));
    $('updated').textContent = `${age} 秒前に確認${s.fresh ? '' : ' / 古い結果'}`;
  }
  for (const id of CAMERA_IDS) {
    const v = cameraViews.get(id), c = snapshot?.cameras?.find(d => d.id === id);
    state(v.badge,s.fresh ? c?.status || 'unknown' : 'unknown');
    v.title.textContent = s.fresh ? c?.title || '端末の情報がありません' : '現在の状態は未確認';
    if (c?.host) v.address.textContent = `${c.host}:${c.port || 8080}`;
    v.frames.dd.textContent = s.fresh && c?.stream?.ok ? `${c.stream.frames} 枚 / ${num(c.stream.elapsedSec,'秒',1)}` : '未確認';
    v.temp.dd.textContent = s.fresh ? num(c?.metrics?.batteryTempC,' ℃',1) : '未確認';
    v.battery.dd.textContent = s.fresh ? num(c?.metrics?.batteryPct,' %') : '未確認';
    v.action.textContent = s.fresh ? c?.action || '映像と設置場所を確認してください。' : 'サーバーとの接続を確認してください。復旧後にこの端末を測り直します。';
    v.previewButton.disabled = !!demo || !s.fresh || !c?.identityOk;
    v.restart.disabled = !!demo || !s.fresh || !c?.canRestart || v.restarting;
    v.restart.title = v.restart.disabled ? '正しい端末だと確認できた場合に使えます。' : 'このカメラだけを起こし直します。';
    if (!s.fresh || !c?.identityOk) closePreview(v);
    const details = [];
    details.push(el('p','',`端末の応答: ${s.fresh && c?.httpOk === true ? 'あり' : '未確認'} / 担当の照合: ${s.fresh && c?.identityOk === true ? '一致' : '未確認または不一致'}`));
    details.push(el('p','',`名乗った担当: ${s.fresh ? c?.observedId || '未確認' : '未確認'} / 画角: ${s.fresh ? num(c?.metrics?.lensFovDeg,'°',1) : '未確認'}`));
    details.push(el('p','',`映像の連番: ${s.fresh && c?.stream?.ok ? `${c.stream.firstSeq} → ${c.stream.lastSeq}` : '未確認'}`));
    const lock = x => x === true ? '固定' : x === false ? '自動' : '未確認';
    details.push(el('p','',`露出: ${s.fresh ? lock(c?.metrics?.aeLock) : '未確認'} / 色: ${s.fresh ? lock(c?.metrics?.awbLock) : '未確認'}`));
    details.push(el('p','mono',`登録番号: ${c?.expectedUuid || '未確認'}`));
    details.push(el('p','mono',`応答した番号: ${s.fresh ? c?.observedUuid || '未確認' : '未確認'}`));
    for (const issue of s.fresh ? c?.issues || [] : []) details.push(el('p','',`${issue.title || ''} ${issue.detail || ''} ${issue.action || ''}`.trim()));
    v.detailBody.replaceChildren(...details);
  }
  for (const id of QUEST_IDS) {
    const v=questViews.get(id),q=snapshot?.quests?.find(d=>d.id===id);
    state(v.badge,s.fresh ? q?.status || 'unknown' : 'unknown');
    v.title.textContent = s.fresh ? q?.title || '応答がありません' : '現在の状態は未確認';
    v.detail.textContent = s.fresh ? q?.action || '装着して映像と音を確認してください。' : 'Quest を起動して同じ Wi-Fi につないでください。';
    if (q?.host) v.address.textContent = `${q.host} / ${s.fresh ? num(q.ageSec,' 秒前',1) : '未確認'}`;
  }
  $('global-issues').replaceChildren(...(s.fresh ? snapshot?.issues || [] : []).filter(i => !i.device).map(i => {
    const box=el('div','global-issue'); box.append(el('div','',i.title || '要確認'),el('p','',`${i.detail || ''} ${i.action || ''}`.trim()));return box;
  }));
  for (const c of MANUAL_CHECKS) {
    const input=$(`check-${c.id}`); input.checked = !!checks[c.id];
    // Confirm real-world checks only while the evidence they refer to is current.
    input.disabled = !s.fresh || s.blockers > 0 || s.unknown > 0;
  }
  $('download').disabled = !snapshot;
}

function renderContent() {
  const s = summarizeContent(contentSnapshot,Date.now(),contentFailed), c = contentSnapshot;
  $('content-summary').textContent = s.title;
  $('content-observed').textContent = c?.observedAt ? `${Math.max(0,Math.floor(Date.now()/1000-c.observedAt))} 秒前に照合${s.fresh?'':' / 現在は未確認'}` : '最初の照合を待っています。';
  $('content-stages').replaceChildren(...[
    ['preparation','1 / 参照ファイル'],['bundle','2 / ビルド用の書き出し'],['apk','3 / APK'],
  ].map(([key,label]) => {
    const d = s.fresh ? c?.[key] : null, box = el('article','content-stage'), badge = el('span','state');
    state(badge,d?.status || 'unknown');box.append(el('p','eyebrow',label),badge,el('h3','',key==='preparation'&&d?.status==='ok'?'参照ファイルは揃っています':d?.title || 'まだ確認できていません'),
      el('p','',d?.action || (d?.status==='ok'?'内容を照合しました。':'照合結果を待っています。機器の接続確認は下で続けられます。')));
    if(d?.contentId)box.append(el('p','content-id',`演出版 ${d.contentId.slice(0,12)}`));
    if(d?.buildGuid)box.append(el('p','content-id',`アプリ版 ${d.buildGuid.replaceAll('-','').slice(0,12)}`));
    return box;
  }));
  const effects = c?.effects || [];
  const effectIssues = s.fresh ? effects.filter(d => !d.sourceReady || !d.bundled || !d.inApk).length : 0;
  $('effect-summary').textContent = !s.fresh ? '現在の状態は未確認' : effectIssues ? `${effects.length} 件中 ${effectIssues} 件を要確認` : `${effects.length} 件を照合済み`;
  $('effect-list').replaceChildren(...(effects.length ? effects.map(d => {
    const row = el('article','effect-row'), title = el('div'), progress = el('div','effect-progress');
    title.append(el('h3','',d.name || d.id),el('p','note',s.fresh ? d.title || `${d.assetCount ?? '—'} 個の素材を使用` : '現在の準備状態は未確認です'));
    for(const [name,key,yes,no] of [['ファイル','sourceReady','あり','見つからない'],['ビルド用','bundled','同梱済み','要更新'],['APK','inApk','同梱済み','要ビルド']]) {
      const cell = el('div','effect-step',name), badge = el('span','state'), value = s.fresh ? d[key] : null;
      state(badge,value===true?'ok':value===false?'warning':'unknown');badge.textContent=value===true?yes:value===false?no:'未確認';cell.append(badge);progress.append(cell);
    }
    row.append(title,progress);if(s.fresh&&d.action)row.append(el('p','effect-action',d.action));return row;
  }) : [el('p','note','使用する演出の一覧はまだ確認できていません。')]));
  $('content-quests').replaceChildren(...QUEST_IDS.map((id,i) => {
    const d = s.fresh ? c?.quests?.find(q=>q.id===id) : null, box=el('article','quest-card content-quest'),badge=el('span','state');
    state(badge,d?.status||'unknown');box.append(el('h3','',`Quest ${i?'β':'α'}`),badge,el('p','',d?.title||'動いている演出版は未確認です'),
      el('p','note',d?.action||(d?.status==='ok'?'装着して映像と音を確認します。':'同梱したアプリを起動すると照合できます。')));
    if(d?.contentId)box.append(el('p','content-id',`演出版 ${d.contentId.slice(0,12)}`));
    if(d?.buildGuid)box.append(el('p','content-id',`アプリ版 ${d.buildGuid.replaceAll('-','').slice(0,12)}`));return box;
  }));
}

function renderTablets(fresh) {
  const languages={ja:'日本語',en:'English',fr:'Français'};
  const selection=(lang,relief)=>languages[lang] && typeof relief==='boolean' ? `${languages[lang]} / 軽減 ${relief?'あり':'なし'}` : '未確認';
  $('tablets').replaceChildren(...QUEST_IDS.map((id,i)=>{
    const t=fresh?snapshot?.tablets?.find(t=>t.id===id):null;
    const card=el('article','quest-card tablet-card'),top=el('div','quest-top'),badge=el('span','state');
    state(badge,t?.status||'unknown');top.append(el('h3','',`博士タブレット → Quest ${i?'β':'α'}`),badge);
    const path=el('p','mono',`http://192.168.10.${31+i}:8090/`);
    const stages=el('div','tablet-stages');
    for(const [label,key] of [['Quest の受付','portalStatus'],['タブレットの応答','connectionStatus'],['設定の反映','reflectionStatus']]){
      const row=el('div'),b=el('span','state');state(b,t?.[key]||'unknown');row.append(el('span','',label),b);stages.append(row);
    }
    card.append(top,path,stages,el('p','device-title',t?.title||'タブレットの状態は未確認です'));
    const values=el('dl','tablet-values');
    for(const [label,value] of [['タブレットから送った設定',selection(t?.requestedLang,t?.requestedRelief)],['Quest の現在の設定',selection(t?.lang,t?.relief)]]){
      const row=el('div');row.append(el('dt','',label),el('dd','',value));values.append(row);
    }
    card.append(values,el('p','note',t?`受信 ${num(t.received,' 回')} / 反映 ${num(t.applyCount,' 回')} / 開いているページ ${num(t.portalStatus==='ok'?t.activePages:null,' 個')}`:'送信と反映の回数も Quest から確認します。'));
    const next=el('div','tablet-next');next.append(el('p','next-label','次にすること'),el('p','',t?.action||(t?.status==='ok'?'このページを開いたままにしてください。次の体験者の設定もここで確認できます。':'Quest を起動します。対応するタブレットで上のアドレスを開いてください。')));card.append(next);
    if(t?.tabletIp)card.append(el('p','note',`タブレット ${t.tabletIp} / ${num(t.ageSec,' 秒前',1)}に応答`));
    return card;
  }));
}

async function pollContent() {
  if(contentBusy || document.hidden)return;
  clearTimeout(contentTimer);contentBusy=true;
  try {
    const next=demo?demoContent(demo):await request('/ops/content',{timeout:90000});
    if(!next.ok || !Array.isArray(next.effects) || !Array.isArray(next.quests))throw new Error('演出の照合結果がありません');
    contentSnapshot=next;contentFailed=false;
    if(snapshot)snapshot.contentSignature=contentSignature(next);
  } catch {contentFailed=true;}
  finally {contentBusy=false;render();contentTimer=setTimeout(pollContent,15000);}
}

function renderShootProof() {
  const node=$('pov-status');
  if (!shootProof) { node.dataset.state='unknown';node.textContent='人形視点の撮影状況を確認できません。撮影画面で確認してください。';return; }
  if (!shootProof.required) {node.dataset.state='ok';node.textContent='この台本では人形視点の撮影は不要です。';return;}
  node.dataset.state=shootProof.complete===shootProof.required?'ok':'warning';
  node.textContent=shootProof.complete===shootProof.required?
    `人形視点 ${shootProof.complete} / ${shootProof.required} 本を当日撮影して採用済みです。`:
    `人形視点 ${shootProof.complete} / ${shootProof.required} 本を当日撮影して採用済み。残り ${shootProof.required-shootProof.complete} 本は撮影画面で確認してください。`;
}
async function pollShoot() {
  if (shootBusy || document.hidden) return;
  shootBusy=true;
  try {
    if (demo) shootProof=demo==='ready'?{required:5,complete:5,missing:[]}:{required:5,complete:3,missing:['人形視点 ③ すぐそこ','人形視点 ④ 追いつき']};
    else {
      const [plan,manifest]=await Promise.all([request('/shoot/plan?cam=A'),request('/shoot/manifest')]);
      shootProof=summarizeOnsiteShots(plan,manifest);
    }
  } catch {shootProof=null;}
  finally {shootBusy=false;renderShootProof();render();}
}

async function request(url,{method='GET',body,timeout=15000}={}) {
  const controller = new AbortController(), timer = setTimeout(()=>controller.abort(),timeout);
  try {
    const res = await fetch(url,{method,body:body === undefined ? undefined : JSON.stringify(body),
      headers:method === 'POST' ? {'Content-Type':'application/json'} : {},signal:controller.signal,cache:'no-store'});
    const data = await res.json(); if (!res.ok) throw new Error(data.error || data.message || `HTTP ${res.status}`); return data;
  } finally { clearTimeout(timer); }
}
async function poll() {
  if (busy || document.hidden) return;
  clearTimeout(pollTimer); busy=true; $('refresh').disabled=true; $('refresh').textContent='確認しています…';
  try {
    const next = demo ? demoSnapshot(demo) : await request('/ops/status');
    if (!next.ok || !Array.isArray(next.cameras) || !Array.isArray(next.quests)) throw new Error('診断結果の形式が違います');
    snapshot=next;snapshot.contentSignature=contentSignature(contentSnapshot);failed=false;
    const s=summarize(snapshot,{},Date.now());
    if (!s.fresh || s.blockers || s.unknown) { record=null;saveRecord();if(deepResult)deepResult.signature=''; }
  } catch { failed=true; record=null;saveRecord();if(deepResult)deepResult.signature=''; }
  finally {
    busy=false;render();$('refresh').disabled=false;$('refresh').textContent='もう一度確認';
    pollTimer=setTimeout(poll,5000);
  }
}
async function restartCamera(id) {
  const v=cameraViews.get(id); if (demo || v.restarting || v.restart.disabled) return;
  v.restarting=true;v.restart.textContent='起こし直しています…';v.message.hidden=false;
  v.message.textContent=`カメラ ${id} を再照合してから起こし直します。新しい映像が届くまで確認します。`;render();
  try {
    const result=await request('/ops/restart',{method:'POST',body:{cameraId:id},timeout:70000});
    v.message.textContent=result.message || (result.ok ? '新しい映像の到着を確認しました。' : '復旧を確認できません。端末の画面を確認してください。');
  } catch(e) {v.message.textContent=`復旧を確認できません。${e.name==='AbortError' ? '確認が時間内に終わりませんでした。' : e.message}`;}
  finally {v.restarting=false;v.restart.textContent='この端末を起こし直す';record=null;saveRecord();await poll();render();}
}

function renderDeep(data) {
  deepResult={...data,signature:signature(snapshot)};
  const at=new Date(data.at), age=Math.floor((Date.now()-at.getTime())/60000);
  const rows=Array.isArray(data.rows)?data.rows:[];
  $('deep-message').textContent=`${Number.isFinite(age) ? `${Math.max(0,age)} 分前の結果` : '確認時刻は不明'} / 問題 ${rows.filter(r=>r.state==='ng').length} 件 / 未確認 ${rows.filter(r=>r.state==='skip'&&r.required!==false).length} 件。現在の状態は上の機器一覧で確認します。`;
  const order={ng:0,warn:1,skip:2,ok:3};
  $('deep-results').replaceChildren(...rows.slice().sort((a,b)=>(order[a.state]??2)-(order[b.state]??2)).map(r=>{
    const row=el('div','deep-row'), badge=el('span','state'), body=el('div');state(badge,({ng:'error',warn:'warning',skip:'unknown',ok:'ok'})[r.state]||'unknown');if(r.required===false)badge.textContent='対象外';
    body.append(el('h3','',r.label || ''),el('p','',r.detail || ''));if(r.fix && r.state!=='ok')body.append(el('p','fix',r.fix));row.append(badge,body);return row;
  }));
  render();
}
$('deep-check').addEventListener('click',async()=>{
  if(demo)return;const button=$('deep-check');button.disabled=true;button.textContent='詳しい点検を実行中…';
  $('deep-message').textContent='機器と素材を調べています。通常 40 秒ほどかかります。';
  try{const result=await request('/onsite/check',{method:'POST',body:{},timeout:245000});if(!result.ok)throw new Error(result.error||'点検結果がありません');renderDeep(result);}
  catch(e){$('deep-message').textContent=`点検を完了できません。${e.name==='AbortError'?'時間内に応答がありませんでした。':e.message}`;}
  finally{button.disabled=false;button.textContent='詳しい点検を実行';}
});
$('refresh').addEventListener('click',()=>{poll();pollContent();pollShoot();});
$('reset-checks').addEventListener('click',()=>{record=null;saveRecord();render();});
$('download').addEventListener('click',()=>{
  // Download the allowlisted diagnosis only. Never fetch show.json, auth, or raw heartbeat.
  const data={savedAt:new Date().toISOString(),example:!!demo,diagnosis:snapshot,content:contentSnapshot,contentCurrent:!contentFailed&&summarizeContent(contentSnapshot).fresh,manual:validChecks(record,snapshot),detailedCheck:deepResult};
  const url=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)],{type:'application/json'}));
  const a=el('a');a.href=url;a.download=`mawarimi-check-${new Date().toISOString().replaceAll(':','-')}.json`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
});
function applyTheme(theme){document.documentElement.dataset.theme=theme;$('theme').textContent=theme==='dark'?'明るい表示':'暗い表示';$('theme').setAttribute('aria-label',theme==='dark'?'明るい表示にする':'暗い表示にする');}
let theme=matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';
try{theme=localStorage.getItem('mawarimi-ops-theme')||theme;}catch{}
applyTheme(theme);
$('theme').addEventListener('click',()=>{theme=theme==='dark'?'light':'dark';applyTheme(theme);try{localStorage.setItem('mawarimi-ops-theme',theme);}catch{}});
const views = ['setup','devices','content','checks'];
function selectView() {
  const requested = location.hash.slice(1);
  const active = views.includes(requested) ? requested : requested === 'tablet-check' ? 'devices' : 'setup';
  for (const id of views) $(id).hidden = id !== active;
  for (const link of document.querySelectorAll('[data-view], [data-step]')) {
    if (link.getAttribute('href') === `#${active}`) link.setAttribute('aria-current',link.dataset.view ? 'page' : 'step');
    else link.removeAttribute('aria-current');
  }
  $('view-announcement').textContent = `${{setup:'設営',devices:'接続',content:'演出',checks:'開場前'}[active]}を表示しています`;
  if (active !== 'devices') for (const view of cameraViews.values()) closePreview(view);
  if (location.hash) requestAnimationFrame(() => $(requested === 'tablet-check' ? 'tablet-check' : active).scrollIntoView({block:'start',behavior:'instant'}));
}
addEventListener('hashchange',selectView);
selectView();
document.addEventListener('visibilitychange',()=>{if(!document.hidden){poll();pollContent();}else for(const v of cameraViews.values())closePreview(v);});
setInterval(()=>{if(!document.hidden)render();},1000);

function demoSnapshot(mode) {
  const cameras=CAMERA_IDS.map((id,i)=>({id,host:`192.168.10.${21+i}`,port:8080,expectedUuid:`example-${id}`,observedUuid:`example-${id}`,observedId:id,status:'ok',title:'新しい映像を受信しています',action:'担当と映像の向きを確認してください。',identityOk:true,httpOk:true,canRestart:false,stream:{ok:true,frames:2,firstSeq:1024,lastSeq:1025,elapsedSec:.2,bytes:41520},metrics:{batteryTempC:34.2+i,batteryPct:88-i*9,lensFovDeg:104.3,aeLock:true,awbLock:true},issues:[]}));
  if(mode!=='ready')Object.assign(cameras[1],{status:'error',title:'応答はありますが映像が止まっています',action:'カメラ B の画面を点けて配信アプリを開いてください。改善しなければこの端末を起こし直します。',stream:{ok:false,frames:0}});
  const quests=QUEST_IDS.map((id,i)=>({id,label:`Quest ${i?'β':'α'}`,host:`192.168.10.${31+i}`,deviceId:`example-${id}`,status:'ok',title:'Quest から応答があります',action:'受付ページが起動しています。装着して映像と音を確認してください。',ageSec:1.2,visitorPort:8090,issues:[]}));
  if(mode!=='ready')Object.assign(quests[1],{status:'unknown',title:'Quest β の応答がありません',action:'Quest β を起動して会場の Wi-Fi につないでください。',ageSec:null,visitorPort:0});
  const tablets=QUEST_IDS.map((id,i)=>({id,status:'ok',portalStatus:'ok',connectionStatus:'ok',reflectionStatus:'ok',portalSessionId:`demo-${id}`,tabletSessionId:`page-${id}`,title:'送った設定が Quest に反映されています',action:'タブレットはこのページを開いたままにしてください。',tabletIp:`192.168.10.${41+i}`,ageSec:2,activePages:1,sentSeq:3,appliedSeq:3,received:3,applyCount:3,requestedLang:'ja',requestedRelief:true,lang:'ja',relief:true}));
  if(mode!=='ready')Object.assign(tablets[1],{status:'warning',reflectionStatus:'warning',title:'設定は届いています。反映を待っています',action:'Quest を体験開始前の画面へ戻してください。言語と軽減が一致するまで確認します。',appliedSeq:2,applyCount:2,requestedLang:'en',lang:'ja'});
  return {ok:true,observedAt:Date.now()/1000,cameras,quests,tablets,issues:[],config:{fixed:true,revision:1}};
}
function demoContent(mode) {
  const id='a'.repeat(64),old='b'.repeat(64),ready=mode==='ready';
  const stage=(title,status='ok',contentId=id)=>({status,contentId,buildGuid:'c'.repeat(32),title,action:status==='ok'?'内容を照合しました。':'素材を同梱して再ビルドします。'});
  return {ok:true,observedAt:Date.now()/1000,policy:'baked-only-v1',ready,
    preparation:stage('参照ファイルは揃っています'),bundle:stage(ready?'ビルド用の内容が一致しています':'壁のシミの差し替えが残っています',ready?'ok':'error',ready?id:old),
    apk:stage(ready?'APK に同じ演出が入っています':'APK は更新前の演出です',ready?'ok':'error',ready?id:old),
    effects:['人形視点','手形','壁のシミ'].map((name,i)=>({id:`effect-${i}`,name,status:ready||i<2?'ok':'error',title:`${i+1} 個の素材を使用`,action:!ready&&i===2?'差し替えた動画を含めて再ビルドします。':'',sourceReady:true,bundled:ready||i<2,inApk:ready||i<2,assetCount:i+1})),
    quests:QUEST_IDS.map(idQ=>({id:idQ,status:ready?'ok':'error',contentId:ready?id:old,buildGuid:ready?'c'.repeat(32):'d'.repeat(32),title:ready?'今回の演出と一致しています':'更新前の演出で動いています',action:ready?'この後に装着して映像と音を確認します。':'新しい APK を入れて起動し直します。'}))};
}
if(demo){$('demo-banner').hidden=false;$('deep-check').disabled=true;for(const v of questViews.values()){v.link.removeAttribute('href');v.link.textContent='受付ページ（表示例）';}}
render();poll();pollContent();
pollShoot();setInterval(pollShoot,15000);
