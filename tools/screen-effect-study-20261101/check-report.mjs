/* Render the authored report in an isolated headless browser; no user browser profile. */
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {createHash} from 'node:crypto';

const output='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study';
const reportName=process.argv[2]??'report.html';
if(!['report.html','report-progress.html'].includes(reportName))throw Error('Use the authored final or progress report');
const report=path.join(output,reportName);
const prefix=reportName==='report.html'?'report':'report-progress';
if(!fs.existsSync(report)) throw Error('Build the completed report first');
const server=http.createServer((request,response)=>{
  if(request.url!=='/' && request.url!=='/report.html'){
    response.writeHead(404);response.end();return;
  }
  response.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});
  fs.createReadStream(report).pipe(response);
});
await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'codex-screen-report-'));
const chrome=spawn('C:/Users/kouga/.cache/remotion-chs/chrome-headless-shell.exe',[
  '--headless','--remote-debugging-port=0',`--user-data-dir=${profile}`,
  '--disable-gpu','about:blank'
],{stdio:'ignore',windowsHide:true});
let socket;
try{
  const active=path.join(profile,'DevToolsActivePort');
  for(let n=0;n<100&&!fs.existsSync(active);n++) await new Promise(r=>setTimeout(r,100));
  if(!fs.existsSync(active)) throw Error('Headless renderer did not expose its debugging port');
  const port=Number(fs.readFileSync(active,'utf8').split('\n')[0]);
  const tabs=await(await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket=new WebSocket(tabs[0].webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true});});
  let nextId=0;const pending=new Map();const pageErrors=[];
  socket.addEventListener('message',event=>{
    const packet=JSON.parse(event.data);
    if(packet.method==='Runtime.exceptionThrown')pageErrors.push(packet.params.exceptionDetails);
    if(!packet.id)return;
    const request=pending.get(packet.id);pending.delete(packet.id);
    if(packet.error)request.reject(Error(JSON.stringify(packet.error)));else request.resolve(packet.result);
  });
  const call=(method,params={})=>new Promise((resolve,reject)=>{const id=++nextId;pending.set(id,{resolve,reject});socket.send(JSON.stringify({id,method,params}));});
  const evaluate=async expression=>{const result=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true,userGesture:true});if(result.exceptionDetails)throw Error(JSON.stringify(result.exceptionDetails));return result.result.value;};
  await call('Page.enable');
  await call('Runtime.enable');
  await call('Emulation.setDeviceMetricsOverride',{width:1400,height:1000,deviceScaleFactor:1,mobile:false});
  await call('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/report.html`});
  for(let n=0;n<100;n++){
    if(await evaluate("document.readyState==='complete' && [...document.images].every(i=>i.complete&&i.naturalWidth>0)"))break;
    await new Promise(r=>setTimeout(r,100));
  }
  const results=[{report:reportName,report_sha256:createHash('sha256').update(fs.readFileSync(report)).digest('hex')}];
  for(let n=0;n<100;n++){
    if(await evaluate("[...document.querySelectorAll('video')].every(video=>video.readyState>=1||video.error)"))break;
    await new Promise(r=>setTimeout(r,100));
  }
  const media=await evaluate("[...document.querySelectorAll('video')].map(video=>({width:video.videoWidth,height:video.videoHeight,duration:video.duration,error:video.error?.message??null}))");
  if(media.length!==2||media.some(video=>video.error||video.width!==1170||video.height!==540||!Number.isFinite(video.duration)))throw Error(JSON.stringify(media));
  const seek=await evaluate("(async()=>{await document.getElementById('ending-both').onclick();document.getElementById('pause-both').onclick();return {times:[...document.querySelectorAll('video')].map(video=>video.currentTime),status:document.getElementById('play-status').textContent};})()");
  if(seek.times.some(time=>time<10.9||time>11.5)||Math.abs(seek.times[0]-seek.times[1])>.05||!seek.status)throw Error(JSON.stringify(seek));
  const playback=await evaluate("(async()=>{await document.getElementById('ending-both').onclick();await new Promise(resolve=>setTimeout(resolve,700));document.getElementById('pause-both').onclick();return {times:[...document.querySelectorAll('video')].map(video=>video.currentTime),paused:[...document.querySelectorAll('video')].every(video=>video.paused)};})()");
  if(!playback.paused||playback.times.some(time=>time<11.5||time>12.5)||Math.abs(playback.times[0]-playback.times[1])>.12)throw Error(JSON.stringify(playback));
  results.push({media,seek,playback});
  for(const [name,width,dark] of [['desktop-light',1400,false],['mobile-light',375,false],['mobile-dark',375,true]]){
    await call('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:width<500});
    await call('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:dark?'dark':'light'}]});
    await evaluate('window.scrollTo(0,0)');
    await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');
    const metrics=await evaluate("({width:innerWidth,scrollWidth:document.documentElement.scrollWidth,brokenImages:[...document.images].filter(i=>!i.complete||!i.naturalWidth).length,videoCount:document.querySelectorAll('video').length,paper:getComputedStyle(document.documentElement).getPropertyValue('--paper').trim()})");
    if(metrics.scrollWidth>metrics.width||metrics.brokenImages)throw Error(JSON.stringify(metrics));
    results.push({name,...metrics});
    const screenshot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});
    fs.writeFileSync(path.join(output,`${prefix}-${name}.png`),Buffer.from(screenshot.data,'base64'));
  }
  const interaction=await evaluate(`(() => {
    const button=document.getElementById('pixel-zoom');
    button.click();
    const enabled=button.getAttribute('aria-pressed')==='true' && [...document.querySelectorAll('.pixel-box')].every(box=>box.classList.contains('native-pixels'));
    const contained=document.documentElement.scrollWidth<=innerWidth;
    button.click();
    return {zoomEnabled:enabled,zoomContained:contained,zoomReset:button.getAttribute('aria-pressed')==='false' && !document.querySelector('.native-pixels')};
  })()`);
  if(Object.values(interaction).some(value=>!value)||pageErrors.length)throw Error(JSON.stringify({interaction,pageErrors}));
  results.push({interaction,pageErrors});
  await call('Emulation.setDeviceMetricsOverride',{width:1400,height:1000,deviceScaleFactor:1,mobile:false});
  await call('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:'light'}]});
  await evaluate("document.querySelector('.media-grid').scrollIntoView()");
  await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');
  const shot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});
  fs.writeFileSync(path.join(output,`${prefix}-still-comparison.png`),Buffer.from(shot.data,'base64'));
  await evaluate("document.getElementById('reference-video').closest('.wide').scrollIntoView()");
  await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');
  const motionShot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});
  fs.writeFileSync(path.join(output,`${prefix}-motion-comparison.png`),Buffer.from(motionShot.data,'base64'));
  fs.writeFileSync(path.join(output,`${prefix}-layout-check.json`),JSON.stringify(results,null,2));
  console.log(JSON.stringify(results));
} finally {
  socket?.close();chrome.kill();server.close();
  // The task-specific profile is kept available for inspection; no other profiles are touched.
}
