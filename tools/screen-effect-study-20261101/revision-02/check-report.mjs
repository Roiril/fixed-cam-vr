/* Isolated port/profile only. Read-only against the selected artifact package. */
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {createHash} from 'node:crypto';

const root=path.resolve(process.argv[2]??'');
if(path.basename(root)!=='revision-02')throw Error('Pass the explicit revision-02 output directory');
const report=path.join(root,'report.html');
if(!fs.existsSync(report))throw Error('Build the complete selected report first');
const server=http.createServer((req,res)=>{
  const requested=decodeURIComponent((req.url??'/').split('?')[0]);
  const file=path.resolve(path.dirname(root),'.'+(requested==='/'?'/revision-02/report.html':requested));
  // Linked evidence may be one level above, inside the same study package.
  const allowed=path.dirname(root)+path.sep;
  if(!file.startsWith(allowed)||!fs.existsSync(file)||!fs.statSync(file).isFile()){res.writeHead(404);res.end();return;}
  const mime={'.html':'text/html; charset=utf-8','.json':'application/json','.png':'image/png','.mp4':'video/mp4','.js':'text/javascript'}[path.extname(file)]??'application/octet-stream';
  res.writeHead(200,{'Content-Type':mime});fs.createReadStream(file).pipe(res);
});
await new Promise((ok,no)=>{server.once('error',no);server.listen(0,'127.0.0.1',ok);});
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'codex-revision02-report-'));
const chrome=spawn('C:/Users/kouga/.cache/remotion-chs/chrome-headless-shell.exe',['--headless','--remote-debugging-port=0',`--user-data-dir=${profile}`,'--disable-gpu','--autoplay-policy=no-user-gesture-required','about:blank'],{stdio:'ignore',windowsHide:true});
let socket;
const results={report,report_sha256:createHash('sha256').update(fs.readFileSync(report)).digest('hex'),checks:[],pageErrors:[]};
const delay=ms=>new Promise(r=>setTimeout(r,ms));
try{
  const active=path.join(profile,'DevToolsActivePort');
  for(let n=0;n<100&&!fs.existsSync(active);n++)await delay(100);
  if(!fs.existsSync(active))throw Error('Dedicated headless browser startup failed');
  const port=Number(fs.readFileSync(active,'utf8').split('\n')[0]);
  const tabs=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket=new WebSocket(tabs[0].webSocketDebuggerUrl);
  await new Promise((ok,no)=>{socket.addEventListener('open',ok,{once:true});socket.addEventListener('error',no,{once:true});});
  let id=0;const pending=new Map();
  socket.addEventListener('message',event=>{const p=JSON.parse(event.data);if(p.method==='Runtime.exceptionThrown')results.pageErrors.push(p.params.exceptionDetails);if(!p.id)return;const q=pending.get(p.id);pending.delete(p.id);p.error?q.reject(Error(JSON.stringify(p.error))):q.resolve(p.result);});
  const call=(method,params={})=>new Promise((resolve,reject)=>{const i=++id;pending.set(i,{resolve,reject});socket.send(JSON.stringify({id:i,method,params}));});
  const ev=async expression=>{const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true,userGesture:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
  const assert=(ok,value)=>{if(!ok)throw Error(JSON.stringify(value));return value;};
  await call('Page.enable');await call('Runtime.enable');
  await call('Emulation.setDeviceMetricsOverride',{width:1400,height:1000,deviceScaleFactor:1,mobile:false});
  await call('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/revision-02/report.html`});
  for(let n=0;n<300;n++){if(await ev("document.readyState==='complete'&&[...document.images].every(i=>i.complete&&i.naturalWidth>0)&&[...document.querySelectorAll('video')].every(v=>v.readyState>=1||v.error)"))break;await delay(100);}
  const media=await ev("[...document.querySelectorAll('video')].map(v=>({width:v.videoWidth,height:v.videoHeight,duration:v.duration,error:v.error?.message??null}))");
  assert(media.length===2&&media.every(v=>v.width===1170&&v.height===540&&v.duration>=13&&!v.error),media);results.checks.push({media});
  const start=await ev("(async()=>{await document.getElementById('start-both').onclick();document.getElementById('pause-both').onclick();return reportQA.state();})()");
  assert(start.times.every(t=>t<.3)&&start.paused.every(Boolean)&&!start.loop,start);results.checks.push({start});
  const loop=await ev("(async()=>{await document.getElementById('loop-both').onclick();await new Promise(r=>setTimeout(r,2700));return reportQA.state();})()");
  assert(loop.loop&&loop.loopCount>=1&&loop.times.every(t=>t>=3&&t<5)&&Math.abs(loop.times[0]-loop.times[1])<.13,loop);results.checks.push({loop});
  const pause=await ev("(async()=>{document.getElementById('pause-both').onclick();const first=reportQA.state();await new Promise(r=>setTimeout(r,250));return {first,after:reportQA.state()};})()");
  assert(!pause.after.loop&&!pause.after.active&&pause.after.paused.every(Boolean)&&pause.after.times.every((t,i)=>Math.abs(t-pause.first.times[i])<.03),pause);results.checks.push({pause});
  const rapidStop=await ev("(async()=>{const pending=document.getElementById('loop-both').onclick();document.getElementById('pause-both').onclick();await pending;await new Promise(r=>setTimeout(r,100));return reportQA.state();})()");
  assert(!rapidStop.active&&!rapidStop.loop&&rapidStop.paused.every(Boolean),rapidStop);results.checks.push({rapidStop});
  const ending=await ev("(async()=>{await document.getElementById('ending-both').onclick();await new Promise(r=>setTimeout(r,450));document.getElementById('pause-both').onclick();return reportQA.state();})()");
  assert(ending.times.every(t=>t>=11.2&&t<=12.1)&&Math.abs(ending.times[0]-ending.times[1])<.13,ending);results.checks.push({ending});
  const still=await ev("(()=>{const image=document.getElementById('still-selected'),initial=image.src;document.querySelector('[data-still=old]').click();const old=image.src;document.querySelector('[data-still=base]').click();const base=image.src;document.querySelector('[data-still=new]').click();return {different:initial!==old&&old!==base&&base!==initial,returned:image.src===initial};})()");
  assert(still.different&&still.returned,still);results.checks.push({still});
  for(const [name,width,theme]of [['desktop-light',1400,'light'],['desktop-dark',1400,'dark'],['mobile-light',375,'light'],['mobile-dark',375,'dark']]){
    await call('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:width<500});
    await ev(`document.documentElement.dataset.theme='${theme}';window.scrollTo(0,0)`);
    await ev('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');
    const metrics=await ev("({width:innerWidth,scrollWidth:document.documentElement.scrollWidth,broken:[...document.images].filter(i=>!i.complete||i.naturalWidth===0).length,paper:getComputedStyle(document.documentElement).getPropertyValue('--paper').trim()})");
    assert(metrics.scrollWidth<=metrics.width&&metrics.broken===0,metrics);results.checks.push({name,...metrics});
    const shot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});fs.writeFileSync(path.join(root,`report-${name}-QA.png`),Buffer.from(shot.data,'base64'));
  }
  const native=await ev("(()=>{const b=document.getElementById('pixel-zoom');b.click();const enabled=b.getAttribute('aria-pressed')==='true'&&[...document.querySelectorAll('.pixel-box')].every(x=>x.classList.contains('native-pixels'));const contained=document.documentElement.scrollWidth<=innerWidth;b.click();return {enabled,contained,reset:b.getAttribute('aria-pressed')==='false'&&!document.querySelector('.native-pixels')};})()");
  assert(Object.values(native).every(Boolean),native);results.checks.push({native});
  const brokenLinks=await ev("(async()=>{const bad=[];for(const a of document.querySelectorAll('a[href]')){const r=await fetch(a.href);if(!r.ok)bad.push({href:a.getAttribute('href'),status:r.status});}return bad;})()");
  assert(brokenLinks.length===0,brokenLinks);assert(results.pageErrors.length===0,results.pageErrors);results.checks.push({brokenLinks});
  for(const [name,selector] of [['still','.wide.media-grid'],['motion','#motion-section'],['spatial','.spatial-grid']]){
    await call('Emulation.setDeviceMetricsOverride',{width:1400,height:1000,deviceScaleFactor:1,mobile:false});
    await ev(`document.documentElement.dataset.theme='light';document.querySelector('${selector}').scrollIntoView()`);
    const shot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});fs.writeFileSync(path.join(root,`report-${name}-QA.png`),Buffer.from(shot.data,'base64'));
  }
  results.pass=true;console.log(JSON.stringify(results));
}catch(error){results.pass=false;results.error=String(error);throw error;}finally{
  fs.writeFileSync(path.join(root,'report-check.json'),JSON.stringify(results,null,2));socket?.close();chrome.kill();server.close();
}
