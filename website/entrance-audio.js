(() => {
  'use strict';
  // User-supplied Quest clips, already mastered. No pitch or volume changes.
  async function prepare() {
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio) return null;
    let context;
    const abort = new AbortController();
    const timeout = setTimeout(() => abort.abort(), 8000);
    try {
      context = new Audio();
      const buffers = await Promise.all(['sfx_shatter', 'sfx_screen_on'].map(async name => {
        const response = await fetch(`assets/${name}.wav`, { signal: abort.signal });
        if (!response.ok) throw new Error('Entrance audio unavailable');
        return context.decodeAudioData(await response.arrayBuffer());
      }));
      clearTimeout(timeout);
      let sources = [];
      let disposed = false;
      return {
        unlock() { return context.resume(); },
        start() {
          const timing = window.MAWARIMI_ENTRANCE_TIMING;
          const start = context.currentTime + timing.audioLead;
          sources = buffers.map((buffer, index) => {
            const source = context.createBufferSource();
            source.buffer = buffer;
            source.connect(context.destination);
            source.start(start + (index === 0 ? timing.shatterAt : timing.screenOnAt) * timing.duration / 1000);
            return source;
          });
        },
        pause() { if (!disposed) context.suspend().catch(() => {}); },
        resume() { if (!disposed) context.resume().catch(() => {}); },
        dispose() {
          if (disposed) return;
          disposed = true;
          sources.forEach(source => { try { source.stop(); } catch (_) {} source.disconnect(); });
          context.close().catch(() => {});
        }
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
