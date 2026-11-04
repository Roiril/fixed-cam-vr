// Off-screen artifact rendering only. Never attaches to an existing browser/profile.
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';

const out = 'G:/マイドライブ/shubie/reports/2026-10-30_screen-study';
const profile = await mkdtemp(join(tmpdir(), 'codex-screen-report-'));
await mkdir(join(out, 'qa'), { recursive: true });
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
  '--headless', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
  '--no-first-run', '--no-default-browser-check', '--disable-background-networking',
  'about:blank'
], { windowsHide: true, stdio: 'ignore' });
const pause = ms => new Promise(r => setTimeout(r, ms));
let ws;
const pending = new Map();
let seq = 0;
function command(method, params = {}) {
  const id = ++seq;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`timeout: ${method}`)); }, 30000);
    pending.set(id, { resolve, reject, timer });
    ws.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const r = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ': ' + JSON.stringify(r.exceptionDetails.exception));
  return r.result.value;
}
try {
  let port;
  for (let i = 0; i < 150; i++) {
    try { port = Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); break; }
    catch { await pause(200); }
  }
  assert(port, 'isolated browser did not start');
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = pages.find(p => p.type === 'page' && p.url === 'about:blank');
  assert(page, 'fresh blank page missing');
  ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  const errors = [];
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.text);
    if (!pending.has(m.id)) return;
    const p = pending.get(m.id); pending.delete(m.id); clearTimeout(p.timer);
    if (m.error) p.reject(new Error(JSON.stringify(m.error))); else p.resolve(m.result);
  };
  await command('Page.enable'); await command('Runtime.enable');
  await command('Page.navigate', { url: pathToFileURL(join(out, 'index.html')).href });
  for (let i = 0; i < 100; i++) {
    if (await evaluate(`document.readyState==='complete' && typeof update==='function'`)) break;
    await pause(100);
  }
  await evaluate(`Promise.all([...document.images].filter(i=>i.getAttribute('src')).map(i=>{i.loading='eager';return i.decode()})).then(()=>true)`);
  const controls = await evaluate(`(()=>{
    const results=[];
    for(const c of ['A','B','C'])for(const v of ['1','2','3','4']){
      camera.value=c;variant.value=v;update();
      results.push(document.getElementById('after').src===document.getElementById('img-'+c+'-'+v+'-front').src);
    }
    camera.value='A';view.value='oblique';update();
    const angled=document.getElementById('after').src===document.getElementById('img-A-4-oblique').src;
    camera.value='B';update();const reset=view.value==='front'&&view.options[1].disabled;
    camera.value='A';variant.value='4';view.value='front';update();
    document.getElementById('split').value='31';document.getElementById('split').dispatchEvent(new Event('input'));
    const slider=document.getElementById('after').style.clipPath.includes('31%');
    document.getElementById('split').value='50';document.getElementById('split').dispatchEvent(new Event('input'));
    openImage(document.getElementById('img-A-4-front'));const modal=document.getElementById('zoom').open;
    document.getElementById('close').click();
    return {all12Choices:results.every(Boolean),angled,reset,slider,modal,closed:!document.getElementById('zoom').open};
  })()`);
  assert(Object.values(controls).every(Boolean), JSON.stringify(controls));
  const layouts = [];
  for (const width of [1280, 375]) for (const theme of ['light', 'dark']) {
    await command('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: width === 375 });
    await command('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: theme }] });
    await evaluate('window.scrollTo(0,0)');
    await evaluate(`Promise.all([...document.images].filter(i=>i.getAttribute('src')).map(i=>i.decode())).then(()=>true)`);
    const layout = await evaluate(`({width:innerWidth,clientWidth:document.documentElement.clientWidth,scrollWidth:document.documentElement.scrollWidth,body:document.body.scrollWidth,images:document.querySelectorAll('.gallery img').length,allLoaded:[...document.images].filter(i=>i.getAttribute('src')).every(i=>i.complete&&i.naturalWidth>0),background:getComputedStyle(document.body).backgroundColor})`);
    assert.equal(layout.width, width); assert(layout.scrollWidth <= layout.clientWidth, 'horizontal document overflow');
    assert(layout.allLoaded); assert.equal(layout.images, 20);
    layouts.push({ width, theme, ...layout });
    let shot = await command('Page.captureScreenshot', { format: 'png' });
    await writeFile(join(out, `qa/${width}-${theme}-top.png`), Buffer.from(shot.data, 'base64'));
    await evaluate(`document.getElementById('compare').scrollIntoView()`);
    shot = await command('Page.captureScreenshot', { format: 'png' });
    await writeFile(join(out, `qa/${width}-${theme}-comparison.png`), Buffer.from(shot.data, 'base64'));
  }
  assert.equal(errors.length, 0, JSON.stringify(errors));
  const result = { renderer: 'isolated headless Microsoft Edge', controls, layouts, runtimeErrors: errors };
  await writeFile(join(out, 'qa/browser-check.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally {
  if (ws?.readyState === WebSocket.OPEN) {
    try { await command('Browser.close'); } catch { /* socket closes during successful shutdown */ }
    ws.close();
  }
}
