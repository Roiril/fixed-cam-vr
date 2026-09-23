import {CAMERA_IDS, isFresh, summarizePlates} from './ops-model.js';

// 人形視点の後に撮る固定カメラの無人静止画。撮影と採用を分ける。
export function mountPlates(root, {request, demo=false, onChange=()=>{}}) {
  const cards=new Map();
  let inventory=null, snapshot=null, capabilities=null, busy=false;
  const status=document.createElement('p'); status.className='production-status'; status.setAttribute('role','status');
  const grid=document.createElement('div');grid.className='plate-grid';root.append(status,grid);
  const message=document.createElement('p');message.className='note';message.setAttribute('role','status');root.append(message);
  for(const id of CAMERA_IDS){
    const card=document.createElement('article');card.className='plate-card';
    card.innerHTML=`<h4>カメラ ${id}</h4><p class="plate-state"></p>
      <button type="button" class="plate-capture">無人の写真を撮る</button><p class="plate-connection note"></p>
      <label class="plate-choice">確認する写真<select aria-label="カメラ ${id} の写真"></select></label>
      <img class="plate-preview" alt="カメラ ${id} の無人シーン" hidden>
      <p class="plate-meta note"></p><label class="plate-confirm"><input type="checkbox"><span>人が映っていない。本番の構図と照明に合っている。</span></label>
      <button type="button" class="plate-adopt">確認した写真を採用</button>`;
    const q=s=>card.querySelector(s);
    const view={card,id,capture:q('.plate-capture'),state:q('.plate-state'),connection:q('.plate-connection'),select:q('select'),image:q('img'),meta:q('.plate-meta'),check:q('input'),adopt:q('.plate-adopt'),item:null,key:'',loaded:false};
    cards.set(id,view);grid.append(card);
    view.select.addEventListener('change',()=>{view.check.checked=false;renderImage(view);});
    view.check.addEventListener('change',()=>renderActions(view));
    view.capture.addEventListener('click',()=>perform(view,'capture'));
    view.adopt.addEventListener('click',()=>perform(view,'adopt'));
    view.image.addEventListener('load',()=>{view.loaded=view.image.naturalWidth>0;renderActions(view);});
    view.image.addEventListener('error',()=>{view.loaded=false;view.check.checked=false;view.meta.textContent='写真を読み込めません。接続とファイルを確認してください。';renderActions(view);});
  }
  function selected(v){return (v.item?.candidates||[]).find(c=>c.url===v.select.value)|| (v.item?.current?.url===v.select.value?v.item.current:null);}
  function connected(v){return isFresh(snapshot)&&snapshot.cameras?.some(c=>c.id===v.id&&c.identityOk===true&&c.stream?.ok===true);}
  function renderActions(v){
    const candidate=selected(v), known=(v.item?.candidates||[]).some(c=>c.url===candidate?.url);
    v.capture.disabled=demo||busy||!capabilities?.plateCapture||!connected(v);
    v.select.disabled=busy||!v.select.options.length;v.check.disabled=busy||!known||!v.loaded;
    v.adopt.disabled=demo||busy||!capabilities?.plateCapture||!known||!v.loaded||!v.check.checked;
    v.connection.textContent=connected(v)?'担当と映像を確認済み。人が画面から出てから撮影します。':'撮影前に「接続」でこのカメラの担当と映像を確認します。';
  }
  function renderImage(v){
    const candidate=selected(v);v.image.hidden=!candidate?.url;
    if(candidate?.url&&v.image.getAttribute('src')!==candidate.url){v.loaded=false;v.image.src=candidate.url;}
    if(!candidate?.url){v.loaded=false;v.image.removeAttribute('src');}
    const adopted=candidate?.url&&candidate.url===v.item?.current?.url;
    v.meta.textContent=candidate?`${adopted?'現在の採用':'未採用の候補'} / ${candidate.capturedAt?new Date(candidate.capturedAt).toLocaleString('ja-JP'):'撮影日は未確認'} / ${candidate.name||''}`:v.item?'まだ写真がありません。':'写真の状態を確認できません。';
    renderActions(v);
  }
  function setInventory(next, preferred={}){
    inventory=next;
    const proof=summarizePlates(next);
    status.dataset.state=proof?.ready?'ok':'warning';
    status.textContent=proof?`無人シーン ${proof.today} / 3 枚を当日撮影して採用済み。${proof.ready?'次は演出の確認へ進みます。':'A・B・C を撮影して確認し、採用します。'}`:'無人シーンの撮影状況を確認できません。';
    for(const v of cards.values()){
      const item=next?.items?.find(i=>i.cameraId===v.id)||null;
      const key=JSON.stringify(item);
      if(key!==v.key||preferred[v.id]){
        const keep=preferred[v.id]||v.select.value;v.key=key;v.item=item;v.check.checked=false;
        const entries=[...(item?.current?[item.current]:[]),...(item?.candidates||[]).filter(c=>c.url!==item?.current?.url)];
        v.select.replaceChildren(...entries.map(c=>{const option=document.createElement('option');option.value=c.url;option.textContent=`${c.url===item?.current?.url?'採用中':'候補'} · ${c.name}`;return option;}));
        if(entries.some(c=>c.url===keep))v.select.value=keep;
        v.state.textContent=!item?'未確認':!item.current?'未採用':item.ready?'採用済み。撮影日も確認します。':'現在の写真は再撮影か採用の確認が必要です。';
        renderImage(v);
      }
      renderActions(v);
    }
  }
  async function refresh(preferred={}){
    try{setInventory(await request('/shoot/plates'),preferred);}catch{setInventory(null);}
    return inventory;
  }
  async function perform(v,action){
    if(busy||(action==='capture'?v.capture.disabled:v.adopt.disabled))return;
    const url=v.select.value;busy=true;cards.forEach(renderActions);
    message.textContent=action==='capture'?`カメラ ${v.id} の写真を撮っています。`:`カメラ ${v.id} の写真を採用しています。`;
    try{
      const result=await request(`/shoot/plate-${action}`,{method:'POST',body:{cameraId:v.id,...(action==='adopt'?{url}:{})},timeout:20000});
      if(!result.ok)throw new Error(result.detail||result.error||'操作できませんでした。');
      await refresh(action==='capture'?{[v.id]:result.candidate.url}:{});
      onChange({action,inventory});
      message.textContent=action==='capture'?'撮影しました。写真を見て、無人であることと構図・照明を確認してから採用します。':`カメラ ${v.id} の写真を採用しました。${v.id==='A'?'右半分に使う写真も同時に反映しました。':''}「演出」で APK 内の素材と比較できます。`;
    }catch(error){message.textContent=`操作できませんでした。${error.message}`;}
    finally{busy=false;cards.forEach(renderActions);}
  }
  request('/ops/capabilities').then(value=>{capabilities=value;cards.forEach(renderActions);}).catch(()=>{});
  return {setInventory,setConnection(value){snapshot=value;cards.forEach(renderActions);},refresh};
}
