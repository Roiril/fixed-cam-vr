'use strict';
// Android Chrome に USB で転送した一時ポートだけを使う。追加パッケージ不要。
const port = process.argv[2];
const expected = process.argv[3];
async function call(target, method, params = {}) {
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  const timer = setTimeout(() => socket.close(), 8000);
  try {
    await new Promise((resolve, reject) => {
      socket.addEventListener('open', resolve, {once:true});
      socket.addEventListener('error', reject, {once:true});
      socket.addEventListener('close', () => reject(new Error('Chrome に接続できません')), {once:true});
    });
    return await new Promise((resolve, reject) => {
      socket.addEventListener('close', () => reject(new Error('Chrome の応答がありません')), {once:true});
      socket.addEventListener('message', event => {
        const response = JSON.parse(event.data);
        if (response.id !== 1) return;
        if (response.error) reject(new Error(response.error.message));
        else resolve(response.result);
      });
      socket.send(JSON.stringify({id:1,method,params}));
    });
  } finally { clearTimeout(timer); socket.close(); }
}
async function main() {
  if (!/^\d+$/.test(port || '') || !/^http:\/\/192\.168\.10\.\d+:8090\/$/.test(expected || '')) {
    throw new Error('展示 URL と一時ポートを指定してください');
  }
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`, {signal: AbortSignal.timeout(5000)})).json();
  const pages = targets.filter(t => t.type === 'page' && t.url === expected);
  if (!pages.length) { process.exitCode = 2; throw new Error('博士 UI がまだ開いていません'); }
  let chosen = pages[0];
  for (const page of pages) {
    const observed = await call(page, 'Runtime.evaluate', {expression:'document.visibilityState',returnByValue:true});
    if (observed.result?.value === 'visible') { chosen = page; break; }
  }
  for (const page of pages) {
    if (page.id !== chosen.id) await call(chosen, 'Target.closeTarget', {targetId:page.id});
  }
  const socket = new WebSocket(chosen.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, {once:true});
    socket.addEventListener('error', reject, {once:true});
  });
  try {
    const value = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('画面確認がタイムアウトしました')), 8000);
      socket.addEventListener('message', event => {
        const response = JSON.parse(event.data);
        if (response.id !== 1) return;
        clearTimeout(timer);
        if (response.error || response.result?.exceptionDetails) reject(new Error('画面の全画面化に失敗しました'));
        else resolve(response.result.result.value);
      });
      socket.send(JSON.stringify({id:1, method:'Runtime.evaluate', params:{
        expression: "(async()=>{if(!document.fullscreenElement)await document.documentElement.requestFullscreen({navigationUI:'hide'});await new Promise(r=>setTimeout(r,700));const images=Array.from(document.images).filter(i=>i.getBoundingClientRect().width>0&&i.getBoundingClientRect().height>0);return {verified:location.href==='" + expected + "'&&!!document.getElementById('titleStart')&&images.length>0&&images.every(i=>i.complete&&i.naturalWidth>0),fullscreen:!!document.fullscreenElement,title:document.title,width:innerWidth,height:innerHeight}})()",
        userGesture:true, awaitPromise:true, returnByValue:true
      }}));
    });
    process.stdout.write(JSON.stringify({...value,closedDuplicates:pages.length-1}));
  } finally { socket.close(); }
}
main().catch(error => { console.error(error.message); process.exitCode ||= 1; });
