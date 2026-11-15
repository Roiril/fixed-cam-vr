// 画面の小さな部品。状態は持たない。

export const STATE_LABELS = { ok: '済み', warning: '注意', error: '対処', unknown: 'まだ', standby: '予備' };

/** h('div.name', {attrs}, ...children)。children は Node か文字列。null と false は捨てる。 */
export function h(spec, attrs = {}, ...children) {
  const [tag, ...classes] = spec.split('.');
  const node = document.createElement(tag || 'div');
  if (classes.length) node.className = classes.join(' ');
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'text') node.textContent = value;
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else if (key in node && typeof value !== 'string') node[key] = value;
    else node.setAttribute(key, value === true ? '' : value);
  }
  node.append(...children.flat().filter(child => child !== null && child !== undefined && child !== false));
  return node;
}

export function setText(node, value) {
  const text = value === null || value === undefined ? '' : String(value);
  if (node.textContent !== text) node.textContent = text;
}

/** 状態の印。色と形と文字の 3 つで重ねる。 */
export function badge(status = 'unknown', label = '') {
  const node = h('span.badge', {}, h('span.glyph', { 'aria-hidden': 'true' }), h('span.badge-text'));
  setBadge(node, status, label);
  return node;
}

export function setBadge(node, status = 'unknown', label = '') {
  const state = status in STATE_LABELS ? status : 'unknown';
  if (node.dataset.state !== state) node.dataset.state = state;
  setText(node.querySelector('.badge-text'), label || STATE_LABELS[state]);
}

/** 誰がやるか。人か、シュビー（エージェント）か。 */
export const role = (who, text) => h('p.role', {}, h('span.role-who', { text: who, dataset: { who: who === '人' ? 'human' : 'agent' } }), text);

export const fact = (label, value = '') => {
  const dd = h('dd', { text: value });
  return { node: h('div.fact', {}, h('dt', { text: label }), dd), value: dd };
};

export function number(value, unit = '', digits = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? `${value.toFixed(digits)}${unit}` : '—';
}

const dateTime = new Intl.DateTimeFormat('ja-JP', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });
const dateOnly = new Intl.DateTimeFormat('ja-JP', { month: 'numeric', day: 'numeric' });
export function when(iso, withTime = true) {
  const date = new Date(iso || '');
  return Number.isFinite(date.getTime()) ? (withTime ? dateTime : dateOnly).format(date) : '';
}

export function ago(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return '';
  if (seconds < 60) return `${Math.floor(seconds)} 秒前`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分前`;
  return `${Math.floor(seconds / 3600)} 時間前`;
}

export async function request(url, { method = 'GET', body, timeout = 15_000 } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  try {
    const response = await fetch(url, { method, signal: controller.signal, cache: 'no-store',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body) });
    let data = null;
    try { data = await response.json(); } catch { /* 下で HTTP の番号を出す */ }
    if (!response.ok) throw new Error(data?.detail || data?.error || data?.message || `応答エラー ${response.status}`);
    return data;
  } catch (error) {
    if (error.name === 'AbortError') throw new Error('時間内に応答がありませんでした');
    throw error;
  } finally { clearTimeout(timer); }
}

/** 配信スマホの映像を卓経由で引く URL。中継は卓のポート + 1。 */
export function liveUrl(host, port = 8080) {
  const url = new URL('/cam', location.href);
  url.port = String(Number(location.port || 80) + 1);
  url.search = new URLSearchParams({ host, port: String(port), path: '/video', t: String(Date.now()) }).toString();
  return url.href;
}

/** 2 度押しで実行するボタン。止まると困る操作に使う。 */
export function confirmButton(label, confirmLabel, run) {
  let armed = 0;
  const button = h('button.button', { type: 'button', text: label });
  const disarm = () => { clearTimeout(armed); armed = 0; button.dataset.armed = 'false'; setText(button, label); };
  button.addEventListener('click', async () => {
    if (button.dataset.busy === 'true') return;
    if (!armed) {
      button.dataset.armed = 'true'; setText(button, confirmLabel);
      armed = setTimeout(disarm, 5000);
      return;
    }
    disarm();
    button.dataset.busy = 'true'; button.setAttribute('aria-busy', 'true');
    try { await run(button); } finally { button.dataset.busy = 'false'; button.removeAttribute('aria-busy'); setText(button, label); }
  });
  button.addEventListener('blur', () => { if (armed) disarm(); });
  return button;
}

export async function copyText(text, status) {
  try {
    await navigator.clipboard.writeText(text);
    setText(status, 'コピーしました。');
  } catch {
    setText(status, 'コピーできませんでした。下の文を選んでコピーしてください。');
    const area = h('textarea.copy-fallback', { readOnly: true, 'aria-label': 'コピーする文' });
    area.value = text;
    status.after(area); area.focus(); area.select();
  }
}

/** 保存と読み出し。使えない環境（プライベートブラウズ等）でも落ちない。 */
export const session = {
  get(key) { try { return JSON.parse(sessionStorage.getItem(key)); } catch { return null; } },
  set(key, value) { try { value === null ? sessionStorage.removeItem(key) : sessionStorage.setItem(key, JSON.stringify(value)); } catch { /* 保存できなくても動く */ } },
};
