(() => {
  if (new URLSearchParams(location.search).get('embedded') !== '1') return;

  document.documentElement.classList.add('embedded');

  const type = 'mawarimi-handheld';
  const modes = new Set(['fps', 'tps', 'fixed']);
  const keys = new Set([
    'KeyW', 'KeyA', 'KeyS', 'KeyD',
    'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ShiftLeft',
  ]);
  const pressed = new Set();
  const world = document.querySelector('#world');
  const loading = document.querySelector('#loading');
  const error = document.querySelector('#error');
  const app = document.querySelector('#app');
  let ready = false;
  let errorReported = false;
  let currentMode = app.dataset.mode;

  function notify(event, details = {}) {
    window.parent.postMessage({ type, event, ...details }, location.origin);
  }

  function dispatchKey(code, down) {
    window.dispatchEvent(new KeyboardEvent(down ? 'keydown' : 'keyup', {
      code,
      bubbles: true,
      cancelable: true,
    }));
    if (down) pressed.add(code);
    else pressed.delete(code);
  }

  window.addEventListener('message', event => {
    if (event.source !== window.parent || event.origin !== location.origin) return;
    const message = event.data;
    if (!message || typeof message !== 'object' || message.type !== type) return;

    if (message.action === 'mode' && modes.has(message.mode)) {
      document.querySelector(`button[data-mode="${message.mode}"]`)?.click();
    } else if (message.action === 'key' && keys.has(message.code) && typeof message.pressed === 'boolean') {
      dispatchKey(message.code, message.pressed);
    } else if (message.action === 'release') {
      for (const code of [...pressed]) dispatchKey(code, false);
    } else if (message.action === 'reset') {
      document.querySelector('#reset-position')?.click();
    }
  });

  window.addEventListener('keydown', event => {
    if (['KeyH', 'KeyG', 'KeyU'].includes(event.code)) {
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }
    if (event.code !== 'Escape' || event.repeat) return;
    event.preventDefault();
    notify('escape');
  }, true);

  window.setInterval(() => {
    if (!errorReported && !error.hidden) {
      errorReported = true;
      notify('error');
    }

    if (!ready && Number(world.dataset.frames) > 0 && loading.hidden) {
      ready = true;
      currentMode = app.dataset.mode;
      notify('ready');
      notify('state', { mode: currentMode });
    }

    if (ready && app.dataset.mode !== currentMode && modes.has(app.dataset.mode)) {
      currentMode = app.dataset.mode;
      notify('state', { mode: currentMode });
    }
  }, 250);
})();
