#!/usr/bin/env node
'use strict';

// スタッフ画面を実ブラウザで確認するための代役。Quest の本番ポート 8099 には接続しない。
//   node tools/visitor-ui/staff-mock-server.cjs --port 8108
//   curl.exe -X POST -H "Content-Type: application/json" -d '{"scenario":"playing"}' http://127.0.0.1:8108/_scenario

const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const root = path.resolve(__dirname, '..', '..');
const webRoot = path.join(root, 'tablet', 'app', 'src', 'main', 'assets', 'web');
const args = process.argv.slice(2);
const portIndex = args.indexOf('--port');
const port = portIndex >= 0 ? Number(args[portIndex + 1]) : 8108;
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('port must be 1024..65535');

let sequence = 0;
let generation = 1;
let tick = 1;
let frozen = false;
let failStatus = false;
let status;

function cameras(overrides = {}) {
  return ['A', 'B', 'C'].map((id) => ({ id, state: 'ok', problem: '', ...(overrides[id] || {}) }));
}

function setup(stage, reason = '', extra = {}) {
  return {
    stage,
    reason,
    positionConfirmed: true,
    resetProgress: 0,
    position: 'confirmed',
    cameras: cameras(),
    tablet: 'ok',
    content: true,
    run: { phase: 'INTRO', lap: 0, laps: 3, sec: 0, outro: 'off', ending: '' },
    ...extra,
  };
}

function resetState() {
  sequence = 0;
  generation += 1;
  tick += 1;
  frozen = false;
  failStatus = false;
  status = {
    ok: true,
    lang: 'ja',
    relief: false,
    phase: 'INTRO',
    titleStage: 'Wait',
    appliedSeq: 0,
    applyCount: 0,
    received: 0,
    pending: null,
    questTick: tick,
    portalSessionId: randomUUID(),
    lastRequest: null,
    visitorGeneration: generation,
    staffResetId: '',
    staffResetRejectedId: '',
    briefing: null,
    staffSetup: setup('settings', 'settings'),
    ip: '127.0.0.1',
    port,
  };
}

resetState();
const timer = setInterval(() => {
  if (frozen) return;
  tick += 1;
  status.questTick = tick;
  if (status.staffSetup?.run && ['playing', 'ended'].includes(status.staffSetup.stage)) {
    status.staffSetup.run.sec += 1;
  }
}, 1000);
timer.unref();

const scenarios = {
  setup() {
    status.phase = 'INTRO';
    status.staffSetup = setup('setup', 'camera', {
      positionConfirmed: false,
      position: 'needed',
      cameras: cameras({ B: { state: 'trouble', problem: 'nostream' } }),
    });
  },
  devices() {
    status.staffSetup = setup('devices', 'camera', {
      positionConfirmed: false,
      position: undefined,
      cameras: undefined,
    });
  },
  alignment() {
    status.staffSetup = setup('alignment', 'position', { positionConfirmed: false, position: 'recenter' });
  },
  handover() {
    status.phase = 'INTRO';
    status.titleStage = 'Wait';
    status.staffSetup = setup('settings', 'settings');
  },
  visitor() {
    status.staffSetup = setup('explanation', 'explanation');
  },
  'confirm-problem'() {
    status.staffSetup = setup('ready', '', { cameras: cameras({ B: { state: 'trouble', problem: 'stale' } }) });
  },
  confirm() {
    status.staffSetup = setup('ready');
  },
  wear() {
    status.staffSetup = setup('handedOff');
  },
  playing() {
    status.phase = 'RUN';
    status.staffSetup = setup('playing', '', {
      run: { phase: 'RUN', lap: 2, laps: 3, sec: 84, outro: 'off', ending: '' },
    });
  },
  outro() {
    status.phase = 'END';
    status.staffSetup = setup('ended', '', {
      run: { phase: 'END', lap: 3, laps: 3, sec: 168, outro: 'playing', ending: 'released' },
    });
  },
  finished() {
    status.phase = 'END';
    status.staffSetup = setup('ended', '', {
      run: { phase: 'END', lap: 3, laps: 3, sec: 176, outro: 'done', ending: 'released' },
    });
  },
  asleep() {
    frozen = true;
  },
  wake() {
    frozen = false;
    tick += 1;
    status.questTick = tick;
  },
  offline() {
    failStatus = true;
  },
  online() {
    failStatus = false;
    frozen = false;
    tick += 1;
    status.questTick = tick;
  },
  legacy() {
    delete status.staffSetup;
  },
};

function corsHeaders(type, length) {
  return {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': 'Content-Type',
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    'Cache-Control': 'no-store',
    'Content-Type': type,
    'Content-Length': length,
  };
}

function send(response, code, type, body, extra = {}) {
  const bytes = Buffer.isBuffer(body) ? body : Buffer.from(body);
  response.writeHead(code, { ...corsHeaders(type, bytes.length), ...extra });
  response.end(bytes);
}

function json(response, code, value) {
  send(response, code, 'application/json; charset=utf-8', JSON.stringify(value));
}

function readBody(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    request.on('data', (chunk) => {
      size += chunk.length;
      if (size > 64 * 1024) {
        reject(new Error('request body too large'));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    request.on('error', reject);
  });
}

function parseJson(text) {
  try { return text ? JSON.parse(text) : {}; }
  catch (_) { return null; }
}

function contentType(filename) {
  return {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.mp3': 'audio/mpeg',
    '.mp4': 'video/mp4',
  }[path.extname(filename).toLowerCase()] || 'application/octet-stream';
}

function serveFile(request, response, pathname) {
  const relative = pathname === '/' ? 'index.html' : decodeURIComponent(pathname.slice(1));
  const filename = path.resolve(webRoot, relative);
  if (!(filename === webRoot || filename.startsWith(`${webRoot}${path.sep}`)) || !fs.existsSync(filename) || !fs.statSync(filename).isFile()) {
    json(response, 404, { ok: false, error: 'not_found' });
    return;
  }
  let bytes = fs.readFileSync(filename);
  if (relative === 'index.html') {
    // 内蔵ブラウザの全画面要求による縮小を避ける。製品側の全画面処理は変更しない。
    const script = '<script>document.documentElement.requestFullscreen = async () => {};</script>';
    bytes = Buffer.from(bytes.toString('utf8').replace('<head>', '<head>' + script));
  }
  const range = request.headers.range?.match(/^bytes=(\d*)-(\d*)$/);
  if (range) {
    const start = range[1] ? Number(range[1]) : 0;
    const end = Math.min(range[2] ? Number(range[2]) : bytes.length - 1, bytes.length - 1);
    if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || end < start || start >= bytes.length) {
      send(response, 416, 'text/plain; charset=utf-8', 'invalid range', { 'Content-Range': `bytes */${bytes.length}` });
      return;
    }
    send(response, 206, contentType(filename), bytes.subarray(start, end + 1), {
      'Accept-Ranges': 'bytes',
      'Content-Range': `bytes ${start}-${end}/${bytes.length}`,
    });
    return;
  }
  send(response, 200, contentType(filename), bytes, { 'Accept-Ranges': 'bytes' });
}

async function handlePost(request, response, pathname) {
  const bodyText = await readBody(request);
  const body = parseJson(bodyText);
  if (pathname === '/_scenario') {
    const name = typeof body === 'string' ? body : body?.scenario;
    if (!Object.hasOwn(scenarios, name)) {
      json(response, 400, { ok: false, error: 'unknown_scenario', scenarios: Object.keys(scenarios) });
      return;
    }
    scenarios[name]();
    json(response, 200, { ok: true, scenario: name, status });
    return;
  }
  if (pathname === '/_state') {
    if (!body || typeof body !== 'object' || Array.isArray(body)) {
      json(response, 400, { ok: false, error: 'object_required' });
      return;
    }
    status = { ...status, ...body, staffSetup: body.staffSetup == null ? status.staffSetup : { ...status.staffSetup, ...body.staffSetup } };
    json(response, 200, { ok: true, status });
    return;
  }
  if (pathname === '/_reset') {
    resetState();
    json(response, 200, { ok: true, status });
    return;
  }
  if (!body || typeof body !== 'object') {
    json(response, 400, { ok: false, error: 'invalid_json' });
    return;
  }
  if (pathname === '/set') {
    if (!['ja', 'en', 'fr'].includes(body.lang) || typeof body.relief !== 'boolean'
      || typeof body.tabletSessionId !== 'string' || body.portalSessionId !== status.portalSessionId) {
      json(response, 409, { ok: false, error: 'invalid_or_stale_request' });
      return;
    }
    sequence += 1;
    status.received += 1;
    status.lang = body.lang;
    status.relief = body.relief;
    status.appliedSeq = sequence;
    status.applyCount += 1;
    status.pending = { lang: body.lang, relief: body.relief, seq: sequence };
    status.lastRequest = { tabletSessionId: body.tabletSessionId, seq: sequence, lang: body.lang, relief: body.relief };
    status.staffSetup = setup('explanation', 'explanation');
    json(response, 200, { ok: true, seq: sequence });
    return;
  }
  if (pathname === '/clear') {
    if (body.portalSessionId !== status.portalSessionId) {
      json(response, 409, { ok: false, error: 'portal_session_changed' });
      return;
    }
    status.pending = null;
    json(response, 200, { ok: true });
    return;
  }
  if (pathname === '/tablet/pulse') {
    if (typeof body.tabletSessionId !== 'string') {
      json(response, 400, { ok: false, error: 'tablet_session_required' });
      return;
    }
    if (body.staffReset === true && body.portalSessionId === status.portalSessionId) {
      const oldId = body.resetRequestId;
      const nextPortal = randomUUID();
      generation += 1;
      status = {
        ...status,
        portalSessionId: nextPortal,
        visitorGeneration: generation,
        staffResetId: oldId,
        pending: null,
        appliedSeq: 0,
        briefing: null,
        staffSetup: setup('settings', 'settings'),
      };
    } else if (body.portalSessionId === status.portalSessionId && Number.isSafeInteger(body.seq)) {
      status.briefing = {
        tabletSessionId: body.tabletSessionId,
        seq: body.seq,
        revision: body.briefingRevision,
        completed: body.briefingCompleted === true,
        staffConfirmed: body.staffConfirmed === true,
      };
      const stage = status.staffSetup?.stage;
      if (body.staffConfirmed === true && ['ready', 'handedOff'].includes(stage)) {
        status.staffSetup = { ...status.staffSetup, stage: 'handedOff', reason: '' };
      } else if (body.briefingCompleted === true && ['settings', 'explanation', 'ready'].includes(stage)) {
        status.staffSetup = { ...status.staffSetup, stage: 'ready', reason: '' };
      }
    }
    json(response, 200, { ok: true });
    return;
  }
  json(response, 404, { ok: false, error: 'not_found' });
}

const server = http.createServer(async (request, response) => {
  const pathname = new URL(request.url, `http://127.0.0.1:${port}`).pathname;
  if (request.method === 'OPTIONS') {
    send(response, 204, 'text/plain; charset=utf-8', '');
    return;
  }
  if (request.method === 'GET' && pathname === '/status') {
    if (failStatus) {
      json(response, 503, { ok: false, error: 'mock_offline' });
      return;
    }
    json(response, 200, status);
    return;
  }
  if (request.method === 'GET' && pathname === '/_state') {
    json(response, 200, { ok: true, frozen, failStatus, scenarios: Object.keys(scenarios), status });
    return;
  }
  if (request.method === 'GET' && pathname === '/_report') {
    const filename = path.join(root, 'reports', '2026-10-10_staff-flow.html');
    if (!fs.existsSync(filename)) json(response, 404, { ok: false, error: 'report_not_generated' });
    else send(response, 200, 'text/html; charset=utf-8', fs.readFileSync(filename));
    return;
  }
  if (request.method === 'GET') {
    serveFile(request, response, pathname);
    return;
  }
  if (request.method === 'POST') {
    try { await handlePost(request, response, pathname); }
    catch (error) { json(response, 500, { ok: false, error: error.message }); }
    return;
  }
  json(response, 405, { ok: false, error: 'method_not_allowed' });
});

server.listen(port, '127.0.0.1', () => {
  console.log(`[staff-mock] http://127.0.0.1:${port}/`);
  console.log(`[staff-mock] scenarios: ${Object.keys(scenarios).join(', ')}`);
});
