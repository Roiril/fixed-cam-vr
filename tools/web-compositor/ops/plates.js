// 無人の写真。採用中の 1 枚を、いまの映像と重ねて見比べる。撮影と採用は分ける。
import { h, setText, badge, setBadge, when, liveUrl } from './ui.js';
import { CAMERA_IDS } from './model.js';

const LIVE_MS = 60_000;

export function mountPlates(root, ctx) {
  let inventory = null, snapshot = null, live = false, capabilities = null, busy = false;
  const message = h('p.message', { role: 'status' });
  const cards = CAMERA_IDS.map(id => {
    const state = badge();
    const photo = h('img', { alt: `カメラ ${id} の無人の写真`, hidden: true });
    const now = h('img.overlay-top', { alt: `カメラ ${id} のいまの映像`, hidden: true });
    const empty = h('p.media-note', { text: '写真がありません' });
    const box = h('figure.media-box', {}, photo, now, empty);
    const mix = h('input', { type: 'range', min: 0, max: 100, value: 50, 'aria-label': `カメラ ${id} の重ね具合` });
    const mixRow = h('label.field', { hidden: true }, '左が写真、右がいまの映像', mix);
    const overlay = h('button.button.small', { type: 'button', text: 'いまの映像を重ねる', 'aria-pressed': 'false' });
    const select = h('select', { 'aria-label': `カメラ ${id} の写真` });
    const meta = h('p.note');
    const capture = h('button.button.small', { type: 'button', text: '無人の写真を撮る' });
    const connection = h('p.note');
    const sure = h('input', { type: 'checkbox' });
    const adopt = h('button.button.primary', { type: 'button', text: 'この写真を採用' });
    const adoptNote = h('p.note.warn-note', { hidden: true });
    const deps = h('section.deps', { 'aria-label': `カメラ ${id} の写真に合わせて作った素材` });
    const node = h('article.plate', { 'aria-label': `カメラ ${id}` },
      h('h3', {}, `カメラ ${id}`, state), box, mixRow, h('div.actions', {}, overlay, capture), connection,
      h('label.field', {}, '見る写真', select), meta,
      h('label.confirm', {}, sure, h('span', { text: '人が写っていない。本番の構図と照明で撮った。' })), adoptNote, h('div.actions', {}, adopt), deps);
    const view = { id, node, state, photo, now, empty, mix, mixRow, overlay, select, meta, capture, connection, sure, adopt,
      adoptNote, deps, base: null, item: null, key: '', loaded: false, timer: 0 };
    mix.addEventListener('input', () => { now.style.opacity = String(Number(mix.value) / 100); });
    overlay.addEventListener('click', () => (view.now.hidden ? openLive(view) : closeLive(view)));
    now.addEventListener('error', () => { if (!now.hidden) { closeLive(view); setText(message, `カメラ ${id} の映像を開けません。`); message.dataset.state = 'error'; } });
    select.addEventListener('change', () => { sure.checked = false; view.base = null; renderImage(view); });
    sure.addEventListener('change', () => renderActions(view));
    capture.addEventListener('click', () => perform(view, 'capture'));
    adopt.addEventListener('click', () => perform(view, 'adopt'));
    photo.addEventListener('load', () => { view.loaded = photo.naturalWidth > 0; renderActions(view); });
    photo.addEventListener('error', () => { view.loaded = false; sure.checked = false; setText(meta, '写真を読めません。'); renderActions(view); });
    return view;
  });
  root.append(
    h('div.hints', {},
      h('p', {}, h('strong', { text: '人形視点を撮り終えてから撮ります。' }), '借りたスマホを本番の位置へ戻し、照明を本番と同じにして、全員が画面から出ます。'),
      h('p', {}, h('strong', { text: '前の日の写真を使い続けるなら、いまの映像と重ねます。' }), '壁と床の線がぴったり重なれば、カメラは動いていません。ずれていたら、向きを戻すか撮り直します。')),
    h('div.plates', {}, ...cards.map(c => c.node)), message);

  const camera = view => (live ? snapshot?.cameras?.find(c => c.id === view.id) : null);
  const connected = view => { const c = camera(view); return !!c && c.identityOk === true && c.stream?.ok === true; };
  const chosen = view => (view.item?.candidates || []).find(c => c.url === view.select.value)
    || (view.item?.current?.url === view.select.value ? view.item.current : null);

  function openLive(view) {
    const c = camera(view); if (!c || ctx.demo) return;
    view.now.src = liveUrl(c.host, c.port || 8080); view.now.hidden = false; view.mixRow.hidden = false;
    view.now.style.opacity = String(Number(view.mix.value) / 100);
    view.overlay.setAttribute('aria-pressed', 'true'); setText(view.overlay, '重ねるのをやめる');
    clearTimeout(view.timer); view.timer = setTimeout(() => closeLive(view), LIVE_MS);
  }
  function closeLive(view) {
    clearTimeout(view.timer); view.now.removeAttribute('src'); view.now.hidden = true; view.mixRow.hidden = true;
    view.overlay.setAttribute('aria-pressed', 'false'); setText(view.overlay, 'いまの映像を重ねる');
  }
  function renderActions(view) {
    const candidate = chosen(view), known = (view.item?.candidates || []).some(c => c.url === candidate?.url);
    const online = connected(view), canCapture = capabilities?.plateCapture === true;
    view.overlay.disabled = ctx.demo || !online;
    view.capture.disabled = ctx.demo || busy || !canCapture || !online;
    view.capture.title = !canCapture ? '撮影と採用は、卓の PC で開いた画面からだけできます。' : '';
    view.select.disabled = busy || !view.select.options.length;
    view.sure.disabled = busy || !known || !view.loaded || !!view.base;
    view.adopt.disabled = ctx.demo || busy || !canCapture || !known || !view.loaded || !view.sure.checked || !!view.base;
    for (const fit of view.deps.querySelectorAll('button[data-realign]')) {
      fit.disabled = ctx.demo || busy || !canCapture;
      fit.title = !canCapture ? '合わせ直しは、卓の PC で開いた画面からだけできます。' : '';
    }
    setText(view.connection, online ? '担当と映像を確かめてあります。' : '「準備」でこのカメラの映像が進んでいると、重ねたり撮ったりできます。');
    if (!online && !view.now.hidden) closeLive(view);
  }
  function renderImage(view) {
    const candidate = chosen(view);
    // 素材を合わせた写真を見ているあいだは、そちらを下に敷く（採用の対象にはしない）。
    const shown = view.base?.plateUrl || candidate?.url;
    view.photo.hidden = !shown; view.empty.hidden = !!shown;
    if (shown && view.photo.getAttribute('src') !== shown) { view.loaded = false; view.photo.src = shown; }
    if (!shown) { view.loaded = false; view.photo.removeAttribute('src'); }
    const adopted = candidate?.url && candidate.url === view.item?.current?.url;
    setText(view.meta, view.base ? `素材を合わせた写真（${view.base.plate}）。いまの映像と重ねて、壁と床の線が合うかを見ます`
      : candidate ? `${adopted ? '採用中' : 'まだ採用していない候補'}／${when(candidate.capturedAt) || '撮った日時は不明'}` : view.item ? '' : '写真の状態を読めていません。');
    renderAdoptNote(view);
    renderActions(view);
  }
  /** 採用しようとしている写真が、素材を合わせた写真と違えば、作り直しが要る素材を先に言う。 */
  function renderAdoptNote(view) {
    const url = chosen(view)?.url, name = url?.split('/').pop();
    const misses = url && url !== view.item?.current?.url ? (view.item?.dependents || []).filter(d => d.plate !== name) : [];
    view.adoptNote.hidden = !misses.length;
    setText(view.adoptNote, misses.length ? `この写真を採用すると、${misses.map(d => d.title).join('、')}は前の写真に合わせたまま残ります。下の「元にした写真と重ねる」でずれていれば作り直します。` : '');
  }
  function renderDeps(view) {
    const list = view.item?.dependents;
    if (!Array.isArray(list)) { view.deps.replaceChildren(h('p.note', { text: '写真に合わせて作った素材の台帳を読めていません。' })); return; }
    if (!list.length) { view.deps.replaceChildren(h('p.note', { text: 'この写真に合わせて作った素材はありません。写真を替えても作り直しは要りません。' })); return; }
    view.deps.replaceChildren(
      h('h4', { text: 'この写真に合わせて作った素材' }),
      ...list.map(d => {
        const look = h('button.button.small', { type: 'button', text: '元にした写真と重ねる',
          disabled: !d.plateExists, title: d.plateExists ? '' : `${d.plate} が卓にありません` });
        look.addEventListener('click', () => { view.base = d; renderImage(view); if (connected(view) && view.now.hidden) openLive(view); });
        // 位置合わせだけで作り直せる素材（2 周目 A の右の幕）は、ここから採用中の写真へ合わせ直す
        const fit = d.realign && !d.current ? h('button.button.primary.small', { type: 'button', text: 'この写真に合わせ直す',
          dataset: { realign: d.group } }) : null;
        fit?.addEventListener('click', () => realign(view, d));
        return h('div.dep', { dataset: { state: d.current ? 'ok' : 'warning' } },
          h('p.dep-title', {}, badge(d.current ? 'ok' : 'warning', d.current ? '合っている' : '作り直し'), h('span', { text: d.title })),
          h('p.note', { text: d.current ? `採用中の写真に合わせてあります（${d.cueIds.length} 本）。写真を替えたら作り直します。`
            : `前の写真（${d.plate}）に合わせてあります（${d.cueIds.length} 本）。${d.realign ? '採用中の写真に合わせ直してから焼き直します。' : '重ねてずれていれば作り直してから焼き直します。'}` }),
          d.current ? null : h('p.note', { text: `作り方: ${d.how}${d.needsInternet ? '（生成に外の回線が要ります）' : ''}` }),
          d.current ? null : h('div.actions', {}, fit, look));
      }));
  }
  function setInventory(next, preferred = {}) {
    inventory = next;
    for (const view of cards) {
      const item = next?.items?.find(i => i.cameraId === view.id) || null;
      const key = JSON.stringify(item);
      if (key !== view.key || preferred[view.id]) {
        const keep = preferred[view.id] || view.select.value;
        view.key = key; view.item = item; view.sure.checked = false; view.base = null;
        renderDeps(view);
        const entries = [...(item?.current ? [item.current] : []), ...(item?.candidates || []).filter(c => c.url !== item?.current?.url)];
        view.select.replaceChildren(...entries.map(c => h('option', { value: c.url, text: `${c.url === item?.current?.url ? '採用中' : '候補'}　${when(c.capturedAt) || c.name}` })));
        if (entries.some(c => c.url === keep)) view.select.value = keep;
        setBadge(view.state, item?.ready && item.current?.exists ? 'ok' : 'unknown', item?.ready && item.current?.exists ? '採用済み' : 'まだ');
        renderImage(view);
      }
      renderActions(view);
    }
  }
  async function perform(view, action) {
    if (busy || (action === 'capture' ? view.capture.disabled : view.adopt.disabled)) return;
    const url = view.select.value; busy = true; cards.forEach(renderActions);
    message.dataset.state = ''; setText(message, action === 'capture' ? `カメラ ${view.id} の写真を撮っています…` : `カメラ ${view.id} の写真を採用しています…`);
    try {
      const result = await ctx.request(`/shoot/plate-${action}`, { method: 'POST', body: { cameraId: view.id, ...(action === 'adopt' ? { url } : {}) }, timeout: 20_000 });
      if (!result.ok) throw new Error(result.detail || result.error || '操作できませんでした。');
      const fresh = await ctx.request('/shoot/plates');
      setInventory(fresh, action === 'capture' ? { [view.id]: result.candidate.url } : {});
      ctx.setPlates(fresh);   // 本体の持つ一覧も替える（古い一覧で描き直されると、選んだ写真が戻る）
      ctx.materialsChanged();
      message.dataset.state = 'ok';
      setText(message, action === 'capture' ? '撮りました。人が写っていないことと、構図と照明を見てから採用します。'
        : `カメラ ${view.id} の写真を採用しました。${view.id === 'A' ? '右半分に使う写真も同時に替えました。' : ''}実機に出すには焼き直します。`);
    } catch (error) { message.dataset.state = 'error'; setText(message, `操作できませんでした。${error.message}`); }
    finally { busy = false; cards.forEach(renderActions); }
  }
  /** 素材を採用中の写真へ合わせ直す。断られたら、生成し直すしかない（カメラか物の位置が変わった）。 */
  async function realign(view, d) {
    if (busy || ctx.demo || capabilities?.plateCapture !== true) return;
    busy = true; cards.forEach(renderActions);
    message.dataset.state = ''; setText(message, `${d.title}を採用中の写真に合わせ直しています（1 分ほど）…`);
    try {
      const result = await ctx.request('/shoot/plate-realign', { method: 'POST', body: { group: d.group }, timeout: 900_000 });
      if (!result.ok) throw new Error(result.detail || result.error || '合わせ直せませんでした。');
      const fresh = await ctx.request('/shoot/plates');
      setInventory(fresh); ctx.setPlates(fresh); ctx.materialsChanged();
      message.dataset.state = 'ok';
      setText(message, `${d.title}を ${result.plate} に合わせ直しました。実機に出すには焼き直します。`);
    } catch (error) { message.dataset.state = 'error'; setText(message, `合わせ直せませんでした。${error.message}`); }
    finally { busy = false; cards.forEach(renderActions); }
  }

  return {
    update(state, d) {
      snapshot = state.snapshot; live = d.live; capabilities = state.capabilities;
      setInventory(state.platesPayload);
    },
    hide() { cards.forEach(closeLive); },
  };
}
