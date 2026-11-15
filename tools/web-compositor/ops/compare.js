// 見比べ。採用中の素材と、焼いたアプリに入っている素材を並べる。
// カメラの映像に重ねる場面は、選んだ 1 つだけ、いまの映像に合成して見せる。
import { h, setText, badge } from './ui.js';
import { createCompositeView } from '../composite-view.js';
import { streamBase } from '../common.js';
import { resolveLibraryPreview } from '../ops-library-preview.js';

const STATUS = { same: ['ok', '焼いたアプリと同じ'], changed: ['warning', '採用し直した（焼き直しが要る）'], unbuilt: ['warning', 'まだ焼いていない'],
  removed: ['unknown', 'いまは使っていない'], unknown: ['unknown', '照合できない'] };

function localUrl(value, sha = '') {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')) return '';
  const url = new URL(value, location.origin);
  if (url.origin !== location.origin) return '';
  if (sha) url.searchParams.set('v', sha.slice(0, 16));
  return url.href;
}

/** カメラのいまの映像に、採用中の素材を重ねる。 */
function liveFrame(spec) {
  const canvas = h('canvas', { width: 640, height: 360, 'aria-label': `カメラ ${spec.camera.id} のいまの映像と採用中の素材の合成` });
  const status = h('p.media-note', { role: 'status', text: 'カメラの映像を待っています…' });
  const box = h('figure.media-box', { dataset: { wide: 'true' } }, canvas, status);
  const live = new Image(); live.crossOrigin = 'anonymous';
  const overlay = spec.kind === 'video' ? document.createElement('video') : new Image();
  const mask = spec.mask ? new Image() : null;
  let overlayReady = false, maskReady = !mask, fresh = false, failed = '', retry = 0, closed = false;
  const startedAt = performance.now();
  if (overlay.tagName === 'VIDEO') {
    Object.assign(overlay, { muted: true, playsInline: true, autoplay: true, preload: 'auto', loop: !(spec.trimStart > 0 || spec.trimEnd > 0) });
    overlay.addEventListener('loadedmetadata', () => { if (spec.trimStart > 0) overlay.currentTime = spec.trimStart; });
    overlay.addEventListener('loadeddata', () => { overlayReady = true; overlay.play().catch(() => { failed = '動画を再生できません。'; }); });
    overlay.addEventListener('timeupdate', () => { if (spec.trimEnd > 0 && overlay.currentTime >= spec.trimEnd - 0.03) overlay.currentTime = Math.max(0, spec.trimStart); });
    overlay.addEventListener('ended', () => { overlay.currentTime = Math.max(0, spec.trimStart); overlay.play().catch(() => {}); });
  } else overlay.onload = () => { overlayReady = true; };
  overlay.onerror = () => { failed = '採用中の素材を読めません。'; };
  if (mask) { mask.onload = () => { maskReady = true; }; mask.onerror = () => { failed = '合成する範囲の画像を読めません。'; }; }
  const view = createCompositeView(canvas, { sample: () => ({
    liveImg: fresh ? live : null, overlayEl: overlay, overlayReady: overlayReady && maskReady,
    overlayW: overlay.tagName === 'VIDEO' ? overlay.videoWidth : overlay.naturalWidth,
    overlayH: overlay.tagName === 'VIDEO' ? overlay.videoHeight : overlay.naturalHeight,
    maskEl: mask, overlayOn: fresh && overlayReady && maskReady, loopPreview: true,
    fadeSec: spec.fadeSec, strength: spec.strength, feather: 0, post: spec.post, trimEnd: 0, onVideoEnd: null }) });
  const connect = () => {
    if (closed) return;
    const url = new URL(`${streamBase()}/cam`);
    url.search = new URLSearchParams({ host: spec.camera.host, port: String(spec.camera.port || 8080), path: '/video', t: String(Date.now()) }).toString();
    if (spec.camera.auth) url.searchParams.set('auth', spec.camera.auth);
    live.src = url.href;
  };
  live.onerror = () => { fresh = false; if (!closed) { clearTimeout(retry); retry = setTimeout(connect, 3000); } };
  overlay.src = localUrl(spec.source.url, spec.source.sha256);
  if (mask) mask.src = localUrl(spec.mask.url, spec.mask.sha256);
  connect();
  async function check() {
    if (closed) return;
    try {
      const response = await fetch('/cam/liveness', { cache: 'no-store' });
      if (!response.ok) throw new Error('liveness');
      const data = await response.json();
      const health = data.cams?.[`${spec.camera.host}:${spec.camera.port || 8080}`];
      // 途切れた映像を、合成の結果として見せない。
      fresh = !!(health && health.clients > 0 && health.ageMs !== null && health.ageMs < 2500 && live.naturalWidth);
    } catch { fresh = false; }
    if (closed) return;
    status.hidden = !!(fresh && overlayReady && maskReady && !failed);
    if (!status.hidden) setText(status, failed || (!fresh ? (performance.now() - startedAt < 5000 ? 'カメラの映像を待っています…' : 'カメラのいまの映像を受け取れません。')
      : !overlayReady ? '採用中の素材を読んでいます…' : '合成する範囲を読んでいます…'));
  }
  const timer = setInterval(check, 1500); check();
  return { box, destroy() {
    closed = true; clearInterval(timer); clearTimeout(retry); view.destroy(); live.src = '';
    if (overlay.tagName === 'VIDEO') overlay.pause();
    overlay.removeAttribute('src'); if (mask) mask.src = '';
  } };
}

function mediaFrame(asset, side, kind, label) {
  const box = h('figure.media-box', { dataset: { wide: 'true' } });
  const ext = (asset?.name || '').split('.').pop().toLowerCase();
  const type = ['mp4', 'webm', 'mov', 'm4v'].includes(ext) ? 'video' : ['jpg', 'jpeg', 'png', 'webp', 'gif', 'bmp'].includes(ext) ? 'image'
    : ['mp3', 'wav', 'ogg', 'aac', 'm4a', 'flac'].includes(ext) ? 'audio' : kind;
  const url = asset?.exists && localUrl(asset.url, asset.sha256);
  if (!url) { box.append(h('p.media-note', { text: side === 'built' ? '焼いたアプリの中に、この素材はありません' : 'この用途に採用中の素材がありません' })); return box; }
  const node = type === 'video' ? h('video', { controls: true, muted: true, playsInline: true, preload: 'metadata', src: url })
    : type === 'audio' ? h('audio', { controls: true, preload: 'metadata', src: url, style: 'position:relative;width:90%' })
      : type === 'image' ? h('img', { alt: `${label}（${side === 'built' ? '焼いたアプリの中' : '採用中'}）`, src: url, loading: 'lazy' })
        : h('a.link', { href: url, text: '素材のファイルを開く', style: 'position:relative' });
  node.addEventListener('error', () => { if (!box.querySelector('.media-note')) box.append(h('p.media-note', { text: '素材を表示できません。' })); });
  box.append(node);
  return box;
}

export function mountCompare(root, ctx) {
  const list = h('div.pick-list', { role: 'group', 'aria-label': '場面' });
  const message = h('p.message', { role: 'status', text: '素材を読んでいます…' });
  const detail = h('div.stage', {}, h('p.empty', { text: '照合しています…' }));
  const refreshButton = h('button.button.small', { type: 'button', text: '読み直す' });
  root.append(h('div.actions', {}, message, refreshButton), h('div.shoot-layout', {}, list, detail));

  let snapshot = null, show = null, preview = null, active = false, busy = false, selected = '', assetKey = '', lastKey = '', failed = false, timer = 0;

  function stopMedia() {
    if (preview) { preview.destroy(); preview = null; }
    for (const media of detail.querySelectorAll('video,audio')) media.pause();
  }

  function render(force = false) {
    const effects = snapshot?.effects || [];
    const key = JSON.stringify([effects, selected, assetKey, show?.rev, active]);
    if (!force && key === lastKey) return;
    lastKey = key;
    list.replaceChildren(...effects.map((effect, i) => {
      const assets = effect.assets || [];
      const differs = assets.some(a => ['changed', 'unbuilt', 'removed'].includes(a.status));
      const status = assets.some(a => a.status === 'unknown') ? 'unknown' : differs ? 'changed' : assets.length ? 'same' : 'unknown';
      const meta = h('span.pick-meta', { text: assets.length ? STATUS[status][1] : 'アプリが描く演出（素材なし）' });
      meta.dataset.state = !assets.length ? '' : STATUS[status][0];
      const button = h('button.pick', { type: 'button', 'aria-pressed': String(effect.id === selected) },
        h('span.pick-no', { text: String(i + 1).padStart(2, '0'), 'aria-hidden': 'true' }),
        h('span.pick-name', { text: effect.name || effect.segmentName || effect.id }), meta);
      button.addEventListener('click', () => { location.hash = `#shoot/compare/${encodeURIComponent(effect.id)}`; });
      return button;
    }));
    stopMedia();
    const picked = effects.find(e => e.id === selected);
    if (!picked) return detail.replaceChildren(h('p.empty', { text: effects.length ? '左から場面を選びます。' : ctx.demo ? '見本では素材を読みません。' : '場面がありません。' }));
    const assets = picked.assets || [];
    const head = h('header.stage-head', {}, h('p.note', { text: picked.segmentName || picked.id }), h('h2', { text: picked.name || picked.segmentName || picked.id }));
    if (!assets.length) return detail.replaceChildren(head, h('p', { text: 'この演出は画像も動画も使いません。カメラの映像にアプリが直接描きます。実機を被って確かめます。' }));
    const item = assets.find(a => a.key === assetKey) || assets.find(a => !a.key.toLowerCase().includes('mask')) || assets[0];
    assetKey = item.key;
    // 1 場面で 16 個の素材を使うことがある。並べると画が下へ押し出されるので、選ぶ形にする。
    const naming = asset => (asset.label || asset.current?.name || asset.built?.name || '素材').replace(/・マスク$/, '・合成する範囲').replace(/・ステップ素材$/, '');
    const picker = h('select', { 'aria-label': 'この場面で使う素材' }, ...assets.map((asset, i) =>
      h('option', { value: asset.key, text: `${i + 1} / ${assets.length}　${naming(asset)}`, selected: asset.key === assetKey })));
    picker.addEventListener('change', () => { assetKey = picker.value; render(true); });
    const tabs = h('label.field', {}, 'この場面で使う素材', picker);
    const spec = resolveLibraryPreview(show, picked, item);
    const composite = !!(active && !document.hidden && !ctx.demo && spec?.live && spec.camera?.host && spec.source?.exists &&
      ['image', 'video'].includes(spec.kind) && (!spec.maskRequired || spec.mask?.exists));
    const [tone, words] = STATUS[item.status] || STATUS.unknown;
    const current = h('div.media-col', {}, h('div.media-col-head', {}, h('h3', { text: composite ? '採用中（いまの映像に合成）' : '採用中' }), badge(tone, words)));
    if (composite) { preview = liveFrame(spec); current.append(preview.box); }
    else current.append(mediaFrame(item.current, 'current', item.kind, item.label));
    current.append(h('figcaption', { text: item.current?.name || '素材なし' }));
    const built = h('div.media-col', {}, h('div.media-col-head', {}, h('h3', { text: '焼いたアプリの中' }),
      h('span.note', { text: snapshot.apkVerified ? '' : 'アプリを読めていません' })),
      mediaFrame(item.built, 'built', item.kind, item.label), h('figcaption', { text: item.built?.name || '素材なし' }));
    const note = composite ? '左は、カメラのいまの映像に採用中の素材を重ねたものです。位置と明るさをここで見ます。立体感と音は、被って確かめます。'
      : spec?.live ? 'カメラの映像か素材を読めないので、素材だけを出しています。' : 'この場面は画面全体を素材に替えます。素材そのものを出しています。';
    const nodes = [head, tabs, h('div.media-pair', {}, current, built), h('p.note', { text: note })];
    if (item.cueId?.startsWith('pov_')) nodes.push(h('p', {}, h('a.link', { href: `#shoot/pov/${encodeURIComponent(item.cueId)}`, text: 'この人形視点を撮り直す' })));
    nodes.push(h('details', {}, h('summary', { text: 'ファイルの照合' }), h('dl.facts', {},
      ...[['採用中', item.current?.name], ['焼いたアプリの中', item.built?.name], ['採用中の指紋', item.current?.sha256?.slice(0, 16)], ['アプリの中の指紋', item.built?.sha256?.slice(0, 16)]]
        .map(([label, value]) => h('div.fact', {}, h('dt', { text: label }), h('dd.mono', { text: value || '—' }))))));
    detail.replaceChildren(...nodes);
  }

  async function refresh() {
    if (busy) return;
    busy = true; refreshButton.disabled = true;
    try {
      if (ctx.demo) { snapshot = { ok: true, observedAt: Date.now() / 1000, effects: [], apkVerified: false }; setText(message, '見本では素材を読みません。'); return render(); }
      const [library, state] = await Promise.all([ctx.request('/ops/library', { timeout: 90_000 }), ctx.request('/state', { timeout: 90_000 }).catch(() => null)]);
      if (!library?.ok || !Array.isArray(library.effects)) throw new Error('素材の一覧を読めません');
      snapshot = library; show = state; failed = false;
      if (!library.effects.some(e => e.id === selected)) selected = library.effects[0]?.id || '';
      message.dataset.state = library.apkVerified ? '' : 'warning';
      setText(message, library.apkVerified ? `${library.effects.length} 場面。焼いたアプリの中と比べられます。` : '焼いたアプリを読めません。採用中の素材だけを出します。');
      render();
    } catch (error) {
      failed = true; message.dataset.state = 'error';
      setText(message, `素材の一覧を読み直せません。出ているのは前の結果です。${error.message}`);
    } finally { busy = false; refreshButton.disabled = false; }
  }
  refreshButton.addEventListener('click', refresh);
  document.addEventListener('visibilitychange', () => { if (document.hidden) { stopMedia(); lastKey = ''; } else if (active) refresh(); });

  return {
    select(id) { if (id && id !== selected) { selected = id; assetKey = ''; render(true); } },
    selection: () => snapshot?.effects?.find(e => e.id === selected) || null,
    setActive(next) {
      if (active === !!next) return;
      active = !!next; lastKey = ''; clearInterval(timer);
      if (!active) return stopMedia();
      refresh(); timer = setInterval(() => { if (!document.hidden) refresh(); }, 20_000);
    },
    snapshotFresh: () => !failed && !!snapshot && Date.now() / 1000 - snapshot.observedAt <= 40,
  };
}
