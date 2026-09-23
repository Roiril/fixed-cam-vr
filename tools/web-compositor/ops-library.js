// Read-only media comparison. Playback nodes survive unchanged inventory polls.
const element = (tag, cls = '', text = '') => {
  const node = document.createElement(tag); node.className = cls; node.textContent = text; return node;
};
const statusLabels = {same:'APK の素材と一致',changed:'仮採用・要ビルド',unbuilt:'仮採用・未ビルド',removed:'現在は未使用',unknown:'照合できません'};
function localUrl(value, sha = '') {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')) return '';
  const u = new URL(value, location.origin);
  if (u.origin !== location.origin) return '';
  if (sha) u.searchParams.set('v',sha.slice(0,16));
  return u.href;
}
function badge(status) {
  const b=element('span','state',statusLabels[status] || statusLabels.unknown);
  b.dataset.state=status==='same'?'ok':status==='unknown'?'unknown':'warning';return b;
}
export function mountLibrary({onLoad=()=>{},demo=false}={}) {
  const $=id=>document.getElementById(id);
  let snapshot=null,active=false,busy=false,selected='',assetKey='',lastRender='',failed=false;
  const readSelection=()=>new URLSearchParams(location.hash.split('?')[1]||'').get('effect');
  function selection() {return snapshot?.effects?.find(e=>e.id===selected)||null;}
  function choose(id) {
    if (selected!==id) {selected=id;assetKey='';}
    const query=new URLSearchParams({effect:id});
    history.replaceState(null,'',`#content?${query}`);
    render(true);
  }
  function mediaFrame(asset, side, kind, label) {
    const box=element('figure','media-frame'),surface=element('div','media-surface');
    const ext=(asset?.name||'').split('.').pop().toLowerCase();
    kind=['mp4','webm','mov','m4v'].includes(ext)?'video':['jpg','jpeg','png','webp','gif','bmp'].includes(ext)?'image':['mp3','wav','ogg','aac','m4a','flac'].includes(ext)?'audio':kind;
    const url=asset?.exists && localUrl(asset.url,asset.sha256);
    if (!url) surface.append(element('p','media-empty',side==='built'?'確認できる APK 素材がありません':'この用途に採用中の素材がありません'));
    else {
      let node;
      if (kind==='video') {node=element('video');node.controls=true;node.muted=true;node.playsInline=true;node.preload='metadata';node.src=url;}
      else if (kind==='audio') {node=element('audio');node.controls=true;node.preload='metadata';node.src=url;}
      else if (kind==='image') {node=element('img');node.alt=`${label} / ${side==='built'?'APK 内':'現在の採用'}`;node.src=url;}
      else {node=element('a','media-file','素材ファイルを開く');node.href=url;}
      node.addEventListener('error',()=>{const error=element('p','media-load-error','素材を表示できません。素材を更新して再確認してください。');if(!surface.querySelector('.media-load-error'))surface.append(error);});
      surface.append(node);
    }
    box.append(surface,element('figcaption','',asset?.name||'素材なし'));
    return box;
  }
  function render(force=false) {
    const effects=snapshot?.effects||[];
    $('library-count').textContent=`${effects.length} 件`;
    const picked=selection();
    const renderKey=JSON.stringify([effects,selected,assetKey]);
    if (!force && lastRender===renderKey)return;
    lastRender=renderKey;
    const browserNode=$('effect-browser'),scroll=browserNode.scrollLeft;
    browserNode.replaceChildren(...effects.map((effect,i)=>{
      const b=element('button','effect-choice');b.type='button';b.setAttribute('aria-pressed',String(effect.id===selected));
      const number=element('span','effect-index',String(i+1).padStart(2,'0'));
      const copy=element('span','effect-choice-copy');copy.append(element('span','effect-place',effect.segmentName||effect.id.split('#')[0]),element('span','effect-name',effect.name||'名称未設定'));
      const assets=effect.assets||[];
      const different=assets.some(a=>a.status==='changed'||a.status==='unbuilt'||a.status==='removed');
      const status=assets.some(a=>a.status==='unknown')?'unknown':different?'changed':assets.length?'same':'unknown';
      copy.append(element('small',`effect-mini-status ${status}`,assets.length?statusLabels[status]:'アプリ内の演出'));
      b.append(number,copy);b.addEventListener('click',()=>choose(effect.id));return b;
    }));
    browserNode.scrollLeft=scroll;
    const chosen=browserNode.querySelector('[aria-pressed=true]');
    if(chosen && browserNode.scrollWidth>browserNode.clientWidth){
      const left=chosen.offsetLeft-browserNode.firstElementChild.offsetLeft;
      if(left<browserNode.scrollLeft)browserNode.scrollLeft=left;
      else if(left+chosen.offsetWidth>browserNode.scrollLeft+browserNode.clientWidth)browserNode.scrollLeft=left+chosen.offsetWidth-browserNode.clientWidth;
    }
    const detail=$('asset-detail');
    for(const v of detail.querySelectorAll('video,audio'))v.pause();
    detail.replaceChildren();
    if(!picked) {detail.append(element('p','empty-state',effects.length?'演出を選んでください。':'演出が登録されていません。'));return;}
    const head=element('div','asset-heading');head.append(element('p','eyebrow',picked.segmentName||picked.id),element('h3','',picked.name||'名称未設定'));detail.append(head);
    const assets=picked.assets||[];
    if(!assets.length){detail.append(element('div','media-empty procedural','この演出は画像・動画ファイルを参照していません。カメラの映像とアプリ内の動きを実機で確認します。'));return;}
    const item=assets.find(a=>a.key===assetKey)||assets.find(a=>!a.key.toLowerCase().includes('mask'))||assets[0];
    assetKey=item.key;
    const tabs=element('div','asset-tabs');tabs.setAttribute('aria-label','この演出で使う素材');
    for(const a of assets){const label=(a.label||a.current?.name||a.built?.name||'素材').replace(/・マスク$/,'・合成する範囲').replace(/・ステップ素材$/,'・使用素材');const b=element('button','asset-tab',label);b.type='button';b.setAttribute('aria-pressed',String(a.key===assetKey));b.addEventListener('click',()=>{assetKey=a.key;render(true);});tabs.append(b);}
    detail.append(tabs);
    const pair=element('div','media-pair');
    for(const side of ['current','built']) {
      const col=element('div',`media-column ${side}`),top=element('div','media-column-heading');
      top.append(element('h4','',side==='current'?'現在の採用':'APK 内'));
      if(side==='current')top.append(badge(item.status));
      else top.append(element('span','note',snapshot.apkVerified?'APK から読み出し':'未確認'));
      col.append(top,mediaFrame(item[side],side,item.kind,item.label));pair.append(col);
    }
    detail.append(pair,element('p','media-context','ここは素材そのものの試写です。会場への合成結果は実機で確認します。APK の素材一致と Quest への導入は別の確認です。'));
    if(item.cueId?.startsWith('pov_')) {const link=element('a','text-action','この人形視点を撮影・採用する →');link.href=`#shoot?shot=${encodeURIComponent(item.cueId)}`;detail.append(link);}
    const meta=element('details','media-meta');meta.append(element('summary','','ファイルの照合情報'));
    const rows=element('dl');
    for(const [label,value] of [['現在の採用',item.current?.name],['APK 内',item.built?.name],['現在のハッシュ',item.current?.sha256],['APK 内のハッシュ',item.built?.sha256],['APK の演出版',snapshot.apkContentId]]){
      rows.append(element('dt','',label),element('dd','mono',value||'未確認'));
    }
    meta.append(rows);detail.append(meta);
    lastRender=JSON.stringify([effects,selected,assetKey]);
  }
  async function refresh() {
    if(busy)return;busy=true;$('library-refresh').disabled=true;
    try {
      // Demo never calls mutation endpoints, and does not present production media as sample data.
      if(demo){snapshot={ok:true,observedAt:Date.now()/1000,effects:[],apkVerified:false};$('library-message').textContent='表示例では実際の素材を読み込みません。';render();return;}
      const res=await fetch('/ops/library',{cache:'no-store',signal:AbortSignal.timeout(90000)});
      if(!res.ok)throw new Error(`HTTP ${res.status}`);
      const next=await res.json();if(!next.ok||!Array.isArray(next.effects))throw new Error('素材一覧を読めません');
      snapshot=next;failed=false;
      const wanted=readSelection();
      if(next.effects.some(e=>e.id===wanted))selected=wanted;
      if(!next.effects.some(e=>e.id===selected))selected=next.effects[0]?.id||'';
      $('library-message').dataset.state=next.apkVerified?'ok':'warning';
      const at=new Date(next.observedAt*1000).toLocaleTimeString('ja-JP',{hour:'2-digit',minute:'2-digit'});
      $('library-message').textContent=next.apkVerified?`APK 内の素材と比較できます。照合 ${at}`:'APK を検証できません。現在の採用素材だけを表示します。';
      render();onLoad(next);
    }catch(e){failed=true;$('library-message').dataset.state='error';$('library-message').textContent='素材一覧を更新できません。表示中の素材と判定は前回の結果です。「素材を更新」で再試行できます。';onLoad(null);}
    finally{busy=false;$('library-refresh').disabled=false;}
  }
  $('library-refresh').addEventListener('click',refresh);
  setInterval(()=>{if(active&&!document.hidden)refresh();},20000);
  setInterval(()=>{
    const stale=failed||!snapshot||Date.now()/1000-snapshot.observedAt>40;
    $('asset-detail').closest('.asset-workspace').dataset.stale=String(stale);
    if(active&&snapshot&&stale&&!failed){$('library-message').dataset.state='warning';$('library-message').textContent='更新を待っています。表示中の素材と判定は前回の結果です。';}
  },1000);
  document.addEventListener('visibilitychange',()=>{if(document.hidden)for(const v of $('asset-detail').querySelectorAll('video,audio'))v.pause();else if(active)refresh();});
  return {refresh,selection,getSnapshot:()=>failed?null:snapshot,setActive(value){active=value;if(value)refresh();else for(const v of $('asset-detail').querySelectorAll('video,audio'))v.pause();}};
}
