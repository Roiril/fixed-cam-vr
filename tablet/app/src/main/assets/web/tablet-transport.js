(() => {
  'use strict';

  const host = globalThis.TabletHost;
  const native = Boolean(host
    && typeof host.getQuest === 'function'
    && typeof host.setQuest === 'function'
    && typeof host.request === 'function'
    && typeof host.cancel === 'function');
  const pending = new Map();
  const allowedTargets = new Set(['alpha', 'beta']);
  const allowedRequests = new Set(['GET /status', 'POST /set', 'POST /clear', 'POST /tablet/pulse']);
  let nextRequestId = 1;

  function abortError() {
    const error = new Error('The request was aborted');
    error.name = 'AbortError';
    return error;
  }

  function normalizePath(value) {
    const path = String(value || '');
    if (path === './status') return '/status';
    if (path === './set') return '/set';
    if (path === './clear') return '/clear';
    if (path === './tablet/pulse') return '/tablet/pulse';
    return path;
  }

  function getQuest() {
    if (!native) return '';
    const quest = host.getQuest();
    return allowedTargets.has(quest) ? quest : '';
  }

  function setQuest(quest) {
    if (!native) return;
    if (!allowedTargets.has(quest)) throw new Error('invalid Quest');
    host.setQuest(quest);
  }

  function getMediaVolume() {
    if (!native || typeof host.getMediaVolume !== 'function') return null;
    try {
      const value = JSON.parse(host.getMediaVolume());
      if (!value || typeof value !== 'object' || Array.isArray(value)
        || !Number.isInteger(value.current) || value.current < 0
        || !Number.isInteger(value.max) || value.max <= 0 || value.current > value.max
        || typeof value.muted !== 'boolean') return null;
      return { current: value.current, max: value.max, muted: value.muted };
    } catch (error) {
      return null;
    }
  }

  function request(path, options = {}, target = '') {
    if (!native) return fetch(path, options);
    const method = String(options.method || 'GET').toUpperCase();
    const normalizedPath = normalizePath(path);
    if (!allowedTargets.has(target)) return Promise.reject(new Error('Quest is not selected'));
    if (!allowedRequests.has(`${method} ${normalizedPath}`)) {
      return Promise.reject(new Error('request is not allowed'));
    }
    const signal = options.signal;
    if (signal?.aborted) return Promise.reject(abortError());
    const id = `tablet-${nextRequestId++}`;
    return new Promise((resolve, reject) => {
      const cleanup = () => {
        signal?.removeEventListener?.('abort', onAbort);
        pending.delete(id);
      };
      const onAbort = () => {
        if (!pending.has(id)) return;
        try { host.cancel(id); } catch (error) { /* The response may already be complete. */ }
        cleanup();
        reject(abortError());
      };
      pending.set(id, { resolve, reject, cleanup });
      signal?.addEventListener?.('abort', onAbort, { once: true });
      try {
        host.request(id, target, method, normalizedPath, options.body == null ? '' : String(options.body));
      } catch (error) {
        cleanup();
        reject(error);
      }
    });
  }

  globalThis.__tabletNativeResponse = (id, result) => {
    const entry = pending.get(String(id));
    if (!entry) return;
    entry.cleanup();
    if (!result || result.error) {
      entry.reject(new Error(result?.error || 'invalid native response'));
      return;
    }
    const status = Number(result.status) || 0;
    const body = String(result.body ?? '');
    entry.resolve({
      ok: status >= 200 && status < 300,
      status,
      json: async () => JSON.parse(body),
      text: async () => body,
    });
  };

  globalThis.TabletTransport = Object.freeze({ native, getQuest, setQuest, getMediaVolume, request });
})();
