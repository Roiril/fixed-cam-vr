// 展示を一時中止しているときに来場者へ見せる全画面（2026-11-14）。
// スタッフ画面の「展示を一時中止する」で開く。閉じるのは右下の「スタッフ専用（長押し）」から。
// 文面はその場で直せる。1 行が 1 ブロックで、行頭に「# 」「## 」「- 」と打つと見出し・小見出し・箇条書きになる。
// 文面と「中止中か」は端末の localStorage に残す（アプリを開き直しても中止の画面へ戻る）。
// 文字は textContent でしか書かない。貼り付けも文字だけにする（HTML を保存しない）。
(() => {
  'use strict';

  const doc = globalThis.document;
  const stub = Object.freeze({ open() {}, close() {}, raise() {}, isOpen: () => false, blocks: () => [] });
  // 回帰試験の代役 DOM は createElement を持たない。そこでは組まない。
  if (!doc || typeof doc.createElement !== 'function' || !doc.body) { globalThis.PauseScreen = stub; return; }

  const STORE_KEY = 'mawarimi.pause.v1';
  const HOLD_MS = 1500;
  const TYPES = ['h1', 'h2', 'p', 'li'];
  const TYPE_LABEL = { h1: '見出し', h2: '小見出し', p: '本文', li: '箇条書き' };
  const DEFAULT_BLOCKS = [
    { t: 'h1', text: '展示を一時中止しています' },
    { t: 'p', text: '機器を調整しています。再開まで、しばらくお待ちください。' },
    { t: 'h2', text: 'Temporarily closed' },
    { t: 'p', text: 'The exhibit is paused for adjustment. Please wait a moment.' },
    { t: 'h2', text: 'Fermeture momentanée' },
    { t: 'p', text: "L'exposition est interrompue pour un réglage. Merci de patienter." },
  ];

  function load() {
    try {
      const value = JSON.parse(globalThis.localStorage?.getItem(STORE_KEY) || 'null');
      if (!value || !Array.isArray(value.blocks)) return { paused: false, blocks: DEFAULT_BLOCKS };
      const blocks = value.blocks
        .filter((b) => b && TYPES.includes(b.t) && typeof b.text === 'string')
        .map((b) => ({ t: b.t, text: b.text.slice(0, 2000) }))
        .slice(0, 60);
      return { paused: value.paused === true, blocks: blocks.length ? blocks : DEFAULT_BLOCKS };
    } catch (error) {
      return { paused: false, blocks: DEFAULT_BLOCKS };
    }
  }
  function save() {
    try { globalThis.localStorage?.setItem(STORE_KEY, JSON.stringify({ paused: data.paused, blocks: data.blocks })); }
    catch (error) { /* 保存できなくても、いまの画面は出し続ける */ }
  }
  const data = load();

  const style = doc.createElement('style');
  style.textContent = `
    .pause-screen { position: fixed; inset: 0; width: 100%; height: 100%; max-width: none; max-height: none; margin: 0; padding: 0; border: 0; overflow: hidden auto; overscroll-behavior: contain; background: var(--bg); color: var(--ink); font: 18px/1.7 var(--sans); }
    .pause-screen::backdrop { background: var(--bg); }
    .pause-body { display: flex; flex-direction: column; justify-content: center; min-height: 100%; box-sizing: border-box; padding: max(48px,env(safe-area-inset-top)) var(--gutter) max(96px,env(safe-area-inset-bottom)); }
    .pause-doc { width: min(100%, 860px); margin: 0 auto; outline: none; }
    .pause-block { margin: 0; padding: 2px 6px; border-radius: 4px; overflow-wrap: anywhere; white-space: pre-wrap; outline: none; }
    .pause-block[data-t="h1"] { margin: 0 0 18px; font: 600 clamp(32px,4.6vw,52px)/1.35 var(--serif); letter-spacing: .04em; }
    .pause-block[data-t="h1"]::after { content: ""; display: block; width: 72px; height: 3px; margin-top: 18px; background: var(--red); }
    .pause-block[data-t="h2"] { margin: 28px 0 4px; color: var(--ink); font-size: clamp(20px,2.2vw,26px); font-weight: 600; letter-spacing: .02em; }
    .pause-block[data-t="p"] { color: var(--muted); font-size: clamp(18px,1.9vw,22px); }
    .pause-block[data-t="li"] { position: relative; padding-left: 1.4em; color: var(--muted); font-size: clamp(18px,1.9vw,22px); }
    .pause-block[data-t="li"]::before { content: ""; position: absolute; left: .45em; top: .8em; width: 6px; height: 6px; border-radius: 50%; background: var(--red); }
    .pause-block:empty::before { content: attr(data-placeholder); color: var(--faint); }
    .pause-block[data-t="li"]:empty::before { position: static; display: inline; width: auto; height: auto; background: none; }
    .pause-screen.is-editing .pause-block { box-shadow: inset 0 0 0 1px transparent; }
    .pause-screen.is-editing .pause-block:hover { box-shadow: inset 0 0 0 1px var(--line); }
    .pause-screen.is-editing .pause-block:focus { box-shadow: inset 0 0 0 1px var(--line-strong); background: rgba(232,220,196,.04); }
    .pause-staff { position: fixed; right: max(16px,env(safe-area-inset-right)); bottom: max(16px,env(safe-area-inset-bottom)); min-height: 44px; padding: 8px 14px; border: 1px solid var(--line); border-radius: 6px; background: transparent; color: var(--faint); font: 14px/1.4 var(--sans); overflow: hidden; isolation: isolate; touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none; }
    .pause-staff::before { content: ""; position: absolute; z-index: -1; inset: 0; background: rgba(232,220,196,.22); transform: scaleX(var(--hold,0)); transform-origin: left; }
    .pause-menu, .pause-tools { position: fixed; left: 50%; bottom: max(16px,env(safe-area-inset-bottom)); display: flex; flex-wrap: wrap; justify-content: center; align-items: center; gap: 10px; width: max-content; max-width: calc(100% - 32px); padding: 12px; border: 1px solid var(--staff-line-strong); border-radius: 8px; background: var(--staff-panel); color: var(--staff-ink); font: 16px/1.4 var(--sans); transform: translateX(-50%); box-shadow: 0 8px 30px rgba(0,0,0,.5); }
    .pause-menu[hidden], .pause-tools[hidden], .pause-staff[hidden] { display: none; }
    .pause-menu button, .pause-tools button { min-height: 52px; padding: 8px 18px; border: 1px solid var(--staff-line-strong); border-radius: 6px; background: transparent; color: var(--staff-ink); font: 16px/1.3 var(--sans); }
    .pause-menu .pause-main, .pause-tools .pause-main { border: 0; background: var(--staff-ink); color: var(--staff-bg); font-weight: 600; }
    .pause-tools button[aria-pressed="true"] { border-color: var(--staff-ink); background: rgba(214,222,230,.14); }
    .pause-tools .pause-sep { width: 1px; align-self: stretch; background: var(--staff-line); }
    .pause-tip { flex-basis: 100%; margin: 0; color: var(--staff-muted); font-size: 14px; text-align: center; }
    .pause-screen.is-editing .pause-body { padding-bottom: 220px; }
  `;
  doc.head.appendChild(style);

  const dialog = doc.createElement('dialog');
  dialog.className = 'pause-screen';
  dialog.id = 'pauseScreen';
  dialog.setAttribute('aria-label', '展示の一時中止');
  dialog.innerHTML = `
    <div class="pause-body"><div class="pause-doc" id="pauseDoc"></div></div>
    <button class="pause-staff" id="pauseStaff" type="button">スタッフ専用（長押し）</button>
    <div class="pause-menu" id="pauseMenu" role="group" aria-label="スタッフの操作" hidden>
      <button class="pause-main" id="pauseResume" type="button">展示を再開する</button>
      <button id="pauseEdit" type="button">文面を編集する</button>
      <button id="pauseMenuClose" type="button">閉じる</button>
    </div>
    <div class="pause-tools" id="pauseTools" role="toolbar" aria-label="文面の編集" hidden>
      <button type="button" data-type="h1">見出し</button>
      <button type="button" data-type="h2">小見出し</button>
      <button type="button" data-type="p">本文</button>
      <button type="button" data-type="li">箇条書き</button>
      <span class="pause-sep" aria-hidden="true"></span>
      <button id="pauseAddBlock" type="button">行を足す</button>
      <button id="pauseDeleteBlock" type="button">この行を消す</button>
      <button id="pauseDefault" type="button">最初の文面に戻す</button>
      <button class="pause-main" id="pauseEditDone" type="button">編集を終える</button>
      <p class="pause-tip">行をタップして書き換えます。改行で次の行。種類は上のボタンか、行頭に「# 」（見出し）「## 」（小見出し）「- 」（箇条書き）。</p>
    </div>`;
  doc.body.appendChild(dialog);

  const $ = (id) => dialog.querySelector('#' + id);
  const docEl = $('pauseDoc');
  const staffBtn = $('pauseStaff');
  const menu = $('pauseMenu');
  const tools = $('pauseTools');
  let editing = false;
  let currentBlock = null;
  let defaultArmedUntil = 0;

  function blockEl(block) {
    const el = doc.createElement(block.t === 'h1' ? 'h1' : block.t === 'h2' ? 'h2' : 'p');
    el.className = 'pause-block';
    el.dataset.t = block.t;
    el.dataset.placeholder = TYPE_LABEL[block.t] + 'を入力';
    el.textContent = block.text;
    if (editing) el.contentEditable = 'true';
    return el;
  }
  function render() {
    docEl.textContent = '';
    for (const block of data.blocks) docEl.appendChild(blockEl(block));
  }
  function readBack() {
    data.blocks = Array.from(docEl.children).map((el) => ({ t: el.dataset.t, text: (el.textContent || '').replace(/ /g, ' ') }));
    save();
  }
  function placeCaret(el, atStart = false) {
    el.focus();
    const sel = globalThis.getSelection?.();
    if (!sel) return;
    const range = doc.createRange();
    range.selectNodeContents(el);
    range.collapse(atStart);
    sel.removeAllRanges();
    sel.addRange(range);
  }
  function caretOffset(el) {
    const sel = globalThis.getSelection?.();
    if (!sel || !sel.rangeCount) return 0;
    const range = sel.getRangeAt(0);
    const pre = range.cloneRange();
    pre.selectNodeContents(el);
    pre.setEnd(range.startContainer, range.startOffset);
    return pre.toString().length;
  }
  function syncToolState() {
    for (const button of tools.querySelectorAll('[data-type]')) {
      button.setAttribute('aria-pressed', String(Boolean(currentBlock) && currentBlock.dataset.t === button.dataset.type));
    }
    $('pauseDeleteBlock').disabled = !currentBlock;
  }
  function setType(el, type) {
    if (!el || !TYPES.includes(type)) return;
    const text = el.textContent;
    const next = blockEl({ t: type, text });
    el.replaceWith(next);
    currentBlock = next;
    placeCaret(next);
    readBack();
    syncToolState();
  }

  function setEditing(on) {
    editing = on;
    dialog.classList.toggle('is-editing', on);
    menu.hidden = true;
    tools.hidden = !on;
    staffBtn.hidden = on;
    currentBlock = null;
    render();
    syncToolState();
    if (on && docEl.firstElementChild) {
      currentBlock = docEl.firstElementChild;
      placeCaret(currentBlock);
      syncToolState();
    }
    if (!on) { readBack(); globalThis.getSelection?.()?.removeAllRanges(); }
  }

  docEl.addEventListener('focusin', (event) => {
    if (!editing || !event.target.classList?.contains('pause-block')) return;
    currentBlock = event.target;
    syncToolState();
  });
  // 行頭の記号で種類を替える（Notion と同じ打ち方）。日本語の入力では「-」が「ー」、空白が全角になるので両方を受ける。
  const MARKS = [[/^[#＃]{2}[ 　]/, 'h2'], [/^[#＃][ 　]/, 'h1'], [/^[-－ー‐][ 　]/, 'li'], [/^・/, 'li']];
  // 日本語の入力では空白が変換に使われ、記号の後に空白が入らない。変換を確定した行が記号だけなら替える
  const BARE_MARKS = [[/^[#＃]{2}$/, 'h2'], [/^[#＃]$/, 'h1'], [/^[-－ー‐]$/, 'li']];
  function onBlockInput(event) {
    const el = event.target;
    if (!editing || !el.classList?.contains('pause-block')) return;
    // 変換中に中身を書き換えると入力が壊れる。確定（compositionend）を待つ
    if (event.isComposing) return;
    const text = el.textContent || '';
    const rules = event.type === 'compositionend' ? MARKS.concat(BARE_MARKS) : MARKS;
    const rule = rules.find(([mark]) => mark.test(text));
    if (rule && el.dataset.t !== rule[1]) {
      el.textContent = text.replace(rule[0], '');
      setType(el, rule[1]);
      return;
    }
    readBack();
  }
  docEl.addEventListener('input', onBlockInput);
  docEl.addEventListener('compositionend', onBlockInput);
  docEl.addEventListener('keydown', (event) => {
    const el = event.target;
    if (!editing || !el.classList?.contains('pause-block') || event.isComposing) return;
    if (event.key === 'Enter') {
      event.preventDefault();
      const text = el.textContent || '';
      const at = caretOffset(el);
      el.textContent = text.slice(0, at);
      const next = blockEl({ t: el.dataset.t === 'li' ? 'li' : 'p', text: text.slice(at) });
      el.after(next);
      currentBlock = next;
      placeCaret(next, true);
      readBack();
      syncToolState();
    } else if (event.key === 'Backspace' && caretOffset(el) === 0 && globalThis.getSelection?.()?.isCollapsed !== false) {
      const prev = el.previousElementSibling;
      if (!prev) return;
      event.preventDefault();
      const join = prev.textContent.length;
      prev.textContent += el.textContent;
      el.remove();
      currentBlock = prev;
      placeCaret(prev);
      const sel = globalThis.getSelection?.();
      if (sel && prev.firstChild) { const r = doc.createRange(); r.setStart(prev.firstChild, join); r.collapse(true); sel.removeAllRanges(); sel.addRange(r); }
      readBack();
      syncToolState();
    }
  });
  docEl.addEventListener('paste', (event) => {
    if (!editing) return;
    event.preventDefault();
    const text = (event.clipboardData?.getData('text/plain') || '').replace(/\r?\n/g, ' ');
    doc.execCommand?.('insertText', false, text);
  });
  docEl.addEventListener('drop', (event) => event.preventDefault());

  for (const button of tools.querySelectorAll('[data-type]')) {
    // 押しても行の選択を外さない
    button.addEventListener('pointerdown', (event) => event.preventDefault());
    button.addEventListener('click', () => setType(currentBlock, button.dataset.type));
  }
  $('pauseAddBlock').addEventListener('pointerdown', (event) => event.preventDefault());
  $('pauseAddBlock').addEventListener('click', () => {
    const next = blockEl({ t: 'p', text: '' });
    if (currentBlock) currentBlock.after(next); else docEl.appendChild(next);
    currentBlock = next;
    placeCaret(next);
    readBack();
    syncToolState();
  });
  $('pauseDeleteBlock').addEventListener('pointerdown', (event) => event.preventDefault());
  $('pauseDeleteBlock').addEventListener('click', () => {
    if (!currentBlock) return;
    const fallback = currentBlock.previousElementSibling || currentBlock.nextElementSibling;
    currentBlock.remove();
    if (!docEl.children.length) docEl.appendChild(blockEl({ t: 'p', text: '' }));
    currentBlock = fallback || docEl.firstElementChild;
    placeCaret(currentBlock);
    readBack();
    syncToolState();
  });
  // 書いた文面が消えるので 2 度押し（3 秒以内）にする
  $('pauseDefault').addEventListener('click', () => {
    const button = $('pauseDefault');
    if (performance.now() > defaultArmedUntil) {
      defaultArmedUntil = performance.now() + 3000;
      button.textContent = 'もう一度押すと戻します';
      setTimeout(() => { if (performance.now() > defaultArmedUntil) button.textContent = '最初の文面に戻す'; }, 3100);
      return;
    }
    defaultArmedUntil = 0;
    button.textContent = '最初の文面に戻す';
    data.blocks = DEFAULT_BLOCKS.map((b) => ({ ...b }));
    save();
    setEditing(true);
  });
  $('pauseEditDone').addEventListener('click', () => setEditing(false));

  // スタッフの入口は長押し（来場者が触れても閉じない）
  let hold = null;
  const setProgress = (v) => staffBtn.style.setProperty('--hold', String(v));
  const cancelHold = () => {
    if (!hold) return;
    clearTimeout(hold.timer); clearInterval(hold.ticker); hold = null; setProgress(0);
  };
  staffBtn.addEventListener('pointerdown', (event) => {
    if (event.isPrimary === false || event.button > 0 || hold) return;
    event.preventDefault();
    try { staffBtn.setPointerCapture?.(event.pointerId); } catch (error) { /* already released */ }
    const startedAt = performance.now();
    hold = {
      timer: setTimeout(() => { cancelHold(); menu.hidden = false; staffBtn.hidden = true; $('pauseResume').focus(); }, HOLD_MS),
      ticker: setInterval(() => setProgress(Math.min(1, (performance.now() - startedAt) / HOLD_MS)), 40),
    };
  });
  for (const type of ['pointerup', 'pointercancel', 'pointerleave', 'lostpointercapture']) staffBtn.addEventListener(type, cancelHold);
  staffBtn.addEventListener('contextmenu', (event) => event.preventDefault());

  $('pauseMenuClose').addEventListener('click', () => { menu.hidden = true; staffBtn.hidden = false; });
  $('pauseEdit').addEventListener('click', () => setEditing(true));
  $('pauseResume').addEventListener('click', () => close());

  // Android の戻る・Esc で来場者が閉じられないようにする
  dialog.addEventListener('cancel', (event) => event.preventDefault());

  function show() {
    if (dialog.open) dialog.close();
    try { dialog.showModal(); } catch (error) { dialog.setAttribute('open', ''); }
  }
  function open() {
    data.paused = true;
    save();
    if (editing) setEditing(false);
    menu.hidden = true;
    staffBtn.hidden = false;
    render();
    show();
    dialog.scrollTop = 0;
  }
  function close() {
    if (editing) setEditing(false);
    data.paused = false;
    save();
    if (dialog.open) dialog.close();
  }
  // 他の modal（スタッフ画面）が後から開くと上に重なる。中止中は前へ出し直す。
  function raise() {
    if (data.paused && !editing) show();
  }

  render();
  globalThis.PauseScreen = Object.freeze({
    open, close, raise,
    isOpen: () => dialog.open,
    blocks: () => data.blocks.map((b) => ({ ...b })),
  });
  // 中止中にアプリを開き直したら中止の画面へ戻す（起動時のスタッフ画面より前へ）
  if (data.paused) globalThis.addEventListener?.('load', () => setTimeout(raise, 0), { once: true });
})();
