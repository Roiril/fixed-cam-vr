// 博士の部屋の試作を配る。/asset/* はタブレットの正本（tablet/app/src/main/assets/web/asset）、
// それ以外はこのフォルダ。Range に答える（声と動画の頭出しに要る）。
// POST /_frame?n=<番号>&dir=<名前> は、書き出しのコマ（JPEG）を --frames の下へ置く。
//   node tools/visitor-ui/room-proto/serve.cjs --port 8142 [--frames <フォルダ>]
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : fallback;
};
const port = Number(opt('--port', '8142'));
const here = __dirname;
const assetRoot = path.resolve(here, '../../../tablet/app/src/main/assets/web/asset');
const framesRoot = path.resolve(opt('--frames', path.join(here, 'frames')));

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.json': 'application/json',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp', '.mp3': 'audio/mpeg', '.mp4': 'video/mp4', '.wav': 'audio/wav',
};

function resolveFile(pathname) {
  const rel = decodeURIComponent(pathname).replace(/^\/+/, '') || 'index.html';
  const [root, sub] = rel.startsWith('asset/') ? [assetRoot, rel.slice(6)] : [here, rel];
  const file = path.resolve(root, sub);
  if (!file.startsWith(root + path.sep)) return null;
  return fs.existsSync(file) && fs.statSync(file).isFile() ? file : null;
}

http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${port}`);
  if (req.method === 'POST' && url.pathname === '/_frame') {
    const dir = (url.searchParams.get('dir') || 'run').replace(/[^\w-]/g, '');
    const n = String(Number(url.searchParams.get('n')) | 0).padStart(5, '0');
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const out = path.join(framesRoot, dir);
      fs.mkdirSync(out, { recursive: true });
      fs.writeFileSync(path.join(out, `f${n}.jpg`), Buffer.concat(chunks));
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end('ok');
    });
    return;
  }
  const file = resolveFile(url.pathname);
  if (!file) { res.writeHead(404); res.end('not found'); return; }
  const size = fs.statSync(file).size;
  const head = { 'Content-Type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream', 'Accept-Ranges': 'bytes', 'Cache-Control': 'no-store' };
  const m = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || '');
  if (m) {
    const start = m[1] ? Number(m[1]) : Math.max(0, size - Number(m[2]));
    const end = m[1] && m[2] ? Math.min(size - 1, Number(m[2])) : size - 1;
    if (start > end || start >= size) { res.writeHead(416, { 'Content-Range': `bytes */${size}` }); res.end(); return; }
    res.writeHead(206, { ...head, 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1 });
    fs.createReadStream(file, { start, end }).pipe(res);
    return;
  }
  res.writeHead(200, { ...head, 'Content-Length': size });
  fs.createReadStream(file).pipe(res);
}).listen(port, '127.0.0.1', () => console.log(`room-proto http://127.0.0.1:${port}/  frames=${framesRoot}`));
