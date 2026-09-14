import { createReadStream } from 'node:fs';
import { realpath, stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const realRoot = await realpath(root);
const host = '127.0.0.1';
const defaultPort = 4173;
const publicFiles = new Set(['index.html', 'styles.css', 'main.js', 'scene.js']);

const mimeTypes = new Map([
  ['.avif', 'image/avif'],
  ['.css', 'text/css; charset=utf-8'],
  ['.gif', 'image/gif'],
  ['.html', 'text/html; charset=utf-8'],
  ['.ico', 'image/x-icon'],
  ['.jpeg', 'image/jpeg'],
  ['.jpg', 'image/jpeg'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.mp3', 'audio/mpeg'],
  ['.mp4', 'video/mp4'],
  ['.ogg', 'audio/ogg'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
  ['.webm', 'video/webm'],
  ['.webp', 'image/webp'],
  ['.woff', 'font/woff'],
  ['.woff2', 'font/woff2']
]);

function getPort(argv) {
  const index = argv.indexOf('--port');
  const raw = index === -1 ? defaultPort : argv[index + 1];
  const port = Number(raw);

  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`Invalid --port value: ${raw ?? '(missing)'}`);
  }

  return port;
}

function send(res, status, body, headers = {}, headOnly = false) {
  const payload = Buffer.from(body);
  res.writeHead(status, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Length': payload.length,
    ...headers
  });
  res.end(headOnly ? undefined : payload);
}

function resolvePublicFile(requestUrl) {
  const rawPath = requestUrl.split(/[?#]/, 1)[0];
  let decoded;

  try {
    decoded = decodeURIComponent(rawPath);
  } catch {
    return { status: 400 };
  }

  if (decoded.includes('\0')) return { status: 400 };

  const normalized = decoded.replaceAll('\\', '/');
  const segments = normalized.split('/').filter(Boolean);
  if (segments.some((segment) => segment === '..')) return { status: 403 };
  if (segments.some((segment) => segment.startsWith('.'))) return { status: 404 };

  const relativePath = segments.length === 0 ? 'index.html' : segments.join('/');
  const isAsset = segments[0] === 'assets' && segments.length > 1;
  if (!publicFiles.has(relativePath) && !isAsset) return { status: 404 };

  const filePath = path.resolve(root, ...relativePath.split('/'));
  const relativeToRoot = path.relative(root, filePath);
  if (relativeToRoot.startsWith('..') || path.isAbsolute(relativeToRoot)) return { status: 403 };

  return { filePath, isAsset, status: 200 };
}

const port = getPort(process.argv.slice(2));
const server = createServer(async (req, res) => {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    send(res, 405, 'Method Not Allowed\n', { Allow: 'GET, HEAD' });
    return;
  }

  const target = resolvePublicFile(req.url ?? '/');
  const headOnly = req.method === 'HEAD';
  if (target.status !== 200) {
    send(res, target.status, target.status === 400 ? 'Bad Request\n' : target.status === 403 ? 'Forbidden\n' : 'Not Found\n', {}, headOnly);
    return;
  }

  let metadata;
  try {
    metadata = await stat(target.filePath);
    const realFile = await realpath(target.filePath);
    const boundary = target.isAsset ? await realpath(path.join(root, 'assets')) : realRoot;
    const relativeToBoundary = path.relative(boundary, realFile);
    const topLevelSymlink = !target.isAsset && path.normalize(realFile).toLowerCase() !== path.normalize(target.filePath).toLowerCase();
    if (topLevelSymlink || relativeToBoundary.startsWith('..') || path.isAbsolute(relativeToBoundary)) {
      send(res, 403, 'Forbidden\n', {}, headOnly);
      return;
    }
  } catch {
    send(res, 404, 'Not Found\n', {}, headOnly);
    return;
  }

  if (!metadata.isFile()) {
    send(res, 404, 'Not Found\n', {}, headOnly);
    return;
  }

  const contentType = mimeTypes.get(path.extname(target.filePath).toLowerCase()) ?? 'application/octet-stream';
  res.writeHead(200, {
    'Content-Type': contentType,
    'Content-Length': metadata.size,
    'Cache-Control': 'no-cache',
    'X-Content-Type-Options': 'nosniff'
  });

  if (headOnly) {
    res.end();
    return;
  }

  const stream = createReadStream(target.filePath);
  stream.on('error', () => {
    if (!res.headersSent) send(res, 500, 'Internal Server Error\n');
    else res.destroy();
  });
  stream.pipe(res);
});

server.listen(port, host, () => {
  console.log(`廻り視 website: http://${host}:${port}/`);
});
