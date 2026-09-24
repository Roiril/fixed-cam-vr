(() => {
  const player = document.querySelector('#handheld-player');
  if (!player) return;
  const launch = document.querySelector('#game-launch');
  const close = document.querySelector('#game-close');
  const expand = document.querySelector('#game-expand');
  const stage = document.querySelector('#game-stage');
  const state = document.querySelector('#game-state');
  const fallback = document.querySelector('#game-fallback');
  const controls = [...player.querySelectorAll('[data-game-key], [data-game-mode]')];
  const config = window.MAWARIMI_EXPERIENCE ?? {};
  let frame = null;
  let timeout;
  let ready = false;
  let focusOnReady = false;
  let restoreFocus = null;
  const inertElements = [];
  if (!config.gameUrl) return;
  const url = new URL(config.gameUrl, location.href);
  if (url.origin !== location.origin) return;
  document.querySelector('#game-unavailable').hidden = true;
  fallback.href = new URL(config.gameStandaloneUrl || config.gameUrl, location.href).href;
  function send(action, data = {}) {
    frame?.contentWindow?.postMessage({ type: 'mawarimi-handheld', action, ...data }, url.origin);
  }
  function release() {
    send('release');
    controls.forEach(button => button.classList.remove('is-held'));
  }
  function setExpanded(value) {
    release();
    player.classList.toggle('is-expanded', value);
    document.body.classList.toggle('handheld-expanded', value);
    expand.setAttribute('aria-pressed', String(value));
    expand.textContent = value ? '元に戻す' : '拡大する';
    if (value) {
      restoreFocus = document.activeElement;
      let element = player;
      while (element.parentElement && element.parentElement !== document.documentElement) {
        for (const sibling of element.parentElement.children) {
          if (sibling !== element && !sibling.inert && sibling instanceof HTMLElement) {
            sibling.inert = true;
            inertElements.push(sibling);
          }
        }
        element = element.parentElement;
      }
      player.setAttribute('role', 'dialog');
      player.setAttribute('aria-modal', 'true');
      player.setAttribute('aria-label', '視点切り替え体験');
      expand.focus();
    } else {
      inertElements.splice(0).forEach(element => { element.inert = false; });
      player.removeAttribute('role');
      player.removeAttribute('aria-modal');
      player.removeAttribute('aria-label');
      restoreFocus?.focus();
    }
  }
  function stop() {
    release();
    clearTimeout(timeout);
    frame?.remove();
    frame = null;
    ready = false;
    controls.forEach(button => { button.disabled = true; });
    close.hidden = true;
    fallback.hidden = true;
    launch.hidden = false;
    state.textContent = '';
    launch.focus();
  }
  function start(focusClose = false) {
    if (frame) return;
    focusOnReady = focusClose;
    launch.hidden = true;
    close.hidden = false;
    state.textContent = '読み込み中です。';
    frame = document.createElement('iframe');
    frame.style.visibility = 'hidden';
    frame.title = '一人称・三人称・固定視点の切り替え体験';
    frame.allow = 'fullscreen';
    frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-pointer-lock');
    frame.src = url.href;
    stage.append(frame);
    timeout = setTimeout(() => {
      if (!ready) {
        state.textContent = '読み込みに時間がかかっています。別のタブでも開けます。';
        fallback.hidden = false;
      }
    }, 20000);
    if (focusClose) close.focus({ preventScroll: true });
  }
  launch.addEventListener('click', () => start(true));
  close.addEventListener('click', stop);
  expand.addEventListener('click', () => setExpanded(!player.classList.contains('is-expanded')));
  window.addEventListener('message', event => {
    if (!frame || event.source !== frame.contentWindow || event.origin !== url.origin || event.data?.type !== 'mawarimi-handheld') return;
    const message = event.data;
    if (message.event === 'ready') {
      ready = true;
      clearTimeout(timeout);
      frame.style.visibility = '';
      state.textContent = '';
      fallback.hidden = false;
      controls.forEach(button => { button.disabled = false; });
      if (focusOnReady) frame.focus({ preventScroll: true });
    }
    if (message.mode) player.querySelectorAll('[data-game-mode]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.gameMode === message.mode)));
    if (message.event === 'error') {
      state.textContent = 'この画面では起動できませんでした。別のタブで開いてください。';
      fallback.hidden = false;
    }
    if (message.event === 'escape' && player.classList.contains('is-expanded')) setExpanded(false);
  });
  player.querySelectorAll('[data-game-mode]').forEach(button => button.addEventListener('click', () => {
    release();
    send('mode', { mode: button.dataset.gameMode });
    frame?.focus();
  }));
  player.querySelectorAll('[data-game-key]').forEach(button => {
    const key = pressed => send('key', { code: button.dataset.gameKey, pressed });
    button.addEventListener('pointerdown', event => {
      if (!ready || event.button !== 0) return;
      event.preventDefault();
      button.setPointerCapture(event.pointerId);
      button.classList.add('is-held');
      key(true);
    });
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) button.addEventListener(name, () => { key(false); button.classList.remove('is-held'); });
    button.addEventListener('keydown', event => {
      if (event.code === 'Space' || event.code === 'Enter') { event.preventDefault(); key(true); }
    });
    button.addEventListener('keyup', () => key(false));
    button.addEventListener('blur', () => key(false));
  });
  window.addEventListener('blur', release);
  player.querySelectorAll('[data-game-stick]').forEach(stick => {
    const codes = stick.dataset.gameStick === 'move'
      ? ['KeyA', 'KeyD', 'KeyW', 'KeyS']
      : ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'];
    let pointer = null;
    function update(event) {
      const box = stick.getBoundingClientRect();
      const x = (event.clientX - box.left) / box.width - .5;
      const y = (event.clientY - box.top) / box.height - .5;
      [x < -.12, x > .12, y < -.12, y > .12].forEach((pressed, index) => send('key', { code: codes[index], pressed }));
    }
    stick.addEventListener('pointerdown', event => {
      if (!ready || event.button !== 0 || pointer !== null) return;
      event.preventDefault();
      pointer = event.pointerId;
      stick.setPointerCapture(pointer);
      stick.classList.add('is-held');
      update(event);
    });
    stick.addEventListener('pointermove', event => { if (event.pointerId === pointer) update(event); });
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) stick.addEventListener(name, event => {
      if (event.pointerId !== pointer) return;
      codes.forEach(code => send('key', { code, pressed: false }));
      pointer = null;
      stick.classList.remove('is-held');
    });
  });
  document.addEventListener('visibilitychange', () => { if (document.hidden) release(); });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && player.classList.contains('is-expanded')) setExpanded(false);
  });
  start();
})();
