(() => {
  'use strict';
  // The selected generated recording is shared byte-for-byte with Quest.
  async function prepare() {
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio) return null;
    let context;
    const abort = new AbortController();
    const timeout = setTimeout(() => abort.abort(), 8000);
    try {
      context = new Audio();
      const response = await fetch('assets/sfx_shatter.wav', { signal: abort.signal });
      if (!response.ok) throw new Error('Entrance audio unavailable');
      const buffer = await context.decodeAudioData(await response.arrayBuffer());
      clearTimeout(timeout);
      let source = null;
      let disposed = false;
      function dispose(stop) {
        if (disposed) return;
        disposed = true;
        document.removeEventListener('visibilitychange', visibility);
        window.removeEventListener('pagehide', onPageHide);
        if (source) {
          if (stop) { try { source.stop(); } catch (_) {} }
          source.disconnect();
        }
        context.close().catch(() => {});
      }
      function visibility() {
        if (document.hidden) context.suspend().catch(() => {});
        else context.resume().catch(() => {});
      }
      function onPageHide() { dispose(true); }
      return {
        unlock() { return context.resume(); },
        start() {
          if (disposed || source) return;
          const timing = window.MAWARIMI_ENTRANCE_TIMING;
          const start = context.currentTime + timing.audioLead;
          source = context.createBufferSource();
          source.buffer = buffer;
          source.connect(context.destination);
          source.onended = () => dispose(false);
          document.addEventListener('visibilitychange', visibility);
          window.addEventListener('pagehide', onPageHide);
          source.start(start + timing.shatterAt * timing.duration / 1000);
          if (document.hidden) visibility();
        },
        pause() { if (!disposed) context.suspend().catch(() => {}); },
        resume() { if (!disposed) context.resume().catch(() => {}); },
        dispose() { dispose(true); }
      };
    } catch (_) {
      clearTimeout(timeout);
      abort.abort();
      context?.close().catch(() => {});
      return null;
    }
  }
  window.MAWARIMI_ENTRANCE_AUDIO = { prepare };
})();
