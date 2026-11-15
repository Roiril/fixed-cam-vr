// 素材の面。人形視点を撮る → 無人の写真を撮る → 採用中の素材を見比べる。
import { h, setText } from './ui.js';
import { mountShoot } from './shoot.js';
import { mountPlates } from './plates.js';
import { mountCompare } from './compare.js';

const PANES = [['pov', '人形視点'], ['plates', '無人の写真'], ['compare', '見比べ']];

export function mountMaterials(root, ctx) {
  const mode = h('p.message', { role: 'status' });
  const counts = Object.fromEntries(PANES.map(([id]) => [id, h('span.count')]));
  const links = Object.fromEntries(PANES.map(([id, label]) => [id, h('a', { href: `#shoot/${id}` }, label, counts[id])]));
  const nodes = Object.fromEntries(PANES.map(([id]) => [id, h('div', { hidden: true, style: 'display:grid;gap:24px' })]));
  root.append(
    h('header.view-head', {}, h('h1', { id: 'shoot-heading', text: '会場の素材' }),
      h('p', { text: '会場で撮るのは、人形視点の動画 5 本と、カメラ A・B・C の無人の写真 3 枚です。シミ・手形・人形の動画は、前もってアプリに入れてあります。' }), mode),
    h('nav.segmented', { 'aria-label': '素材の種類' }, ...Object.values(links)), ...Object.values(nodes));

  const panes = { pov: mountShoot(nodes.pov, ctx), plates: mountPlates(nodes.plates, ctx), compare: mountCompare(nodes.compare, ctx) };
  let current = 'pov', shown = false;

  function activate() {
    for (const [id] of PANES) {
      nodes[id].hidden = id !== current;
      links[id].setAttribute('aria-current', String(id === current));
    }
    panes.pov.setActive(shown && current === 'pov');
    panes.compare.setActive(shown && current === 'compare');
    if (!shown || current !== 'plates') panes.plates.hide();
  }

  return {
    route(sub) {
      const [pane, ...rest] = (sub || '').split('/');
      current = PANES.some(([id]) => id === pane) ? pane : 'pov';
      shown = true; activate();
      const target = rest.join('/');
      if (current === 'pov' && target) panes.pov.select(target);
      if (current === 'compare' && target) panes.compare.select(target);
    },
    update(state, d) {
      const caps = state.capabilities;
      mode.dataset.state = caps?.shooting ? '' : 'warning';
      const onDesk = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
      setText(mode, !caps ? '' : caps.shooting ? ''
        : onDesk ? 'この卓は点検用に起動しています。撮影と採用をするには、卓を止めて tools\\web-compositor\\serve.ps1 で起動し直します。'
          : 'この端末では見るだけです。撮影と採用は、卓の PC で http://localhost:8099/ を開いて行います。');
      mode.hidden = !mode.textContent;
      setText(counts.pov, d.shots ? `${d.shots.complete}/${d.shots.required}` : '');
      setText(counts.plates, d.plates ? `${d.plates.complete}/3` : '');
      panes.pov.setCapabilities(caps);
      if (current === 'plates') panes.plates.update(state, d);
    },
    hide() { shown = false; activate(); },
  };
}
