import { createScene } from './scene.js';

const INTRO_KEY = 'mawarimi:intro-seen';
const MOTION_KEY = 'mawarimi:reduced-motion';
const INTRO_MIN_MS = 1100;
const INTRO_TIMEOUT_MS = 3000;
const RETURNING_TIMEOUT_MS = 320;
const ECHO_DELAY_MS = 450;

const clamp = (value, min = 0, max = 1) => Math.min(max, Math.max(min, value));
const delay = (milliseconds) => new Promise((resolve) => window.setTimeout(resolve, milliseconds));

function readStorage(key) {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key, value) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // The experience remains usable when storage is unavailable.
  }
}

function safeSceneCall(scene, method, ...args) {
  try {
    scene?.[method]?.(...args);
  } catch (error) {
    console.warn(`Scene ${method} failed.`, error);
  }
}

function setPressed(button, pressed) {
  if (!button) return;
  button.setAttribute('aria-pressed', String(pressed));
  const indicator = button.querySelector('[aria-hidden="true"]');
  if (indicator) indicator.textContent = pressed ? '●' : '○';
}

function getInitialMotionPreference(mediaQuery) {
  const stored = readStorage(MOTION_KEY);
  if (stored === 'reduce') return true;
  if (stored === 'full') return false;
  return mediaQuery.matches;
}

function collectCriticalImages() {
  const sources = new Set();

  document
    .querySelectorAll('link[rel="preload"][as="image"]')
    .forEach((link) => sources.add(link.href));

  document
    .querySelectorAll('img[data-critical], img:not([loading="lazy"])')
    .forEach((image) => sources.add(image.currentSrc || image.src));

  return [...sources].filter(Boolean).map((source) => {
    const image = new Image();
    image.src = source;
    if (typeof image.decode === 'function') {
      return image.decode().catch(() => undefined);
    }
    if (image.complete) return Promise.resolve();
    return new Promise((resolve) => {
      image.addEventListener('load', resolve, { once: true });
      image.addEventListener('error', resolve, { once: true });
    });
  });
}

function createLoadingController({ reducedMotion, skipIntro, returningVisitor }) {
  const loading = document.querySelector('#loading');
  const count = document.querySelector('#loading-count');
  const line = document.querySelector('#loading-line');
  const skip = document.querySelector('#skip-intro');
  let finished = false;
  const introAnimations = [];
  let resolveSkip;
  const skipped = new Promise((resolve) => {
    resolveSkip = resolve;
  });

  if (loading && !reducedMotion && !skipIntro && !returningVisitor) {
    const origins = ['left top', 'right top', 'right bottom', 'left bottom'];
    loading.querySelectorAll('.loading-frame i').forEach((corner, index) => {
      if (typeof corner.animate !== 'function') return;
      corner.style.transformOrigin = origins[index];
      introAnimations.push(corner.animate([
        { opacity: 0, transform: 'scale(0.36)' },
        { opacity: 1, transform: 'scale(1)' },
      ], {
        duration: 760,
        delay: index * 55,
        easing: 'cubic-bezier(.16,1,.3,1)',
        fill: 'backwards',
      }));
    });
    const inner = loading.querySelector('.loading-inner');
    if (typeof inner?.animate === 'function') {
      introAnimations.push(inner.animate([
        { opacity: 0, transform: 'translate3d(0, 8px, 0)' },
        { opacity: 1, transform: 'translate3d(0, 0, 0)' },
      ], {
        duration: 460,
        delay: 220,
        easing: 'cubic-bezier(.2,.7,.2,1)',
        fill: 'backwards',
      }));
    }
  }

  const update = (completed, total) => {
    const ratio = total === 0 ? 1 : clamp(completed / total);
    const percent = Math.round(ratio * 100);
    if (count) count.textContent = String(percent);
    if (line) {
      line.style.setProperty('--loading-progress', String(ratio));
      line.style.transformOrigin = 'left center';
      line.style.transform = `scaleX(${ratio})`;
    }
  };

  const finish = ({ immediate = false } = {}) => {
    if (finished) return;
    finished = true;
    introAnimations.forEach((animation) => animation.cancel());
    window.clearTimeout(window.__mawarimiFallbackTimeout);
    document.body.classList.add('is-ready');
    document.querySelectorAll('#hero [data-reveal]').forEach((element) => element.classList.add('is-visible'));
    loading?.setAttribute('aria-hidden', 'true');
    if (loading) {
      loading.style.pointerEvents = 'none';
      loading.inert = true;
      const hide = () => {
        loading.hidden = true;
      };
      if (immediate || reducedMotion) hide();
      else {
        loading.addEventListener('transitionend', hide, { once: true });
        window.setTimeout(hide, 900);
      }
    }
    writeStorage(INTRO_KEY, '1');
  };

  skip?.addEventListener('click', () => {
    resolveSkip();
    finish({ immediate: true });
  }, { once: true });

  if (skipIntro) {
    resolveSkip();
    finish({ immediate: true });
  }

  return { finish, skipped, update, get finished() { return finished; } };
}

async function runIntro(controller, readiness, returningVisitor) {
  let completed = 0;
  const total = readiness.length;
  controller.update(completed, total);

  const tracked = readiness.map((promise) => Promise.resolve(promise).finally(() => {
    completed += 1;
    controller.update(completed, total);
  }));
  const assetsReady = Promise.allSettled(tracked);

  if (returningVisitor) {
    await Promise.race([assetsReady, delay(RETURNING_TIMEOUT_MS), controller.skipped]);
    return;
  }

  await Promise.race([
    Promise.all([
      Promise.race([assetsReady, delay(INTRO_TIMEOUT_MS)]),
      delay(INTRO_MIN_MS),
    ]),
    controller.skipped,
  ]);
}

function setupMenu() {
  const toggle = document.querySelector('#menu-toggle');
  const menu = document.querySelector('#site-menu');
  if (!toggle || !menu) return;
  const header = toggle.closest('header');

  const focusableSelector = 'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])';
  const close = ({ restoreFocus = false } = {}) => {
    if (!document.body.classList.contains('menu-open')) return;
    document.body.classList.remove('menu-open');
    toggle.setAttribute('aria-expanded', 'false');
    if (restoreFocus) toggle.focus({ preventScroll: true });
  };
  const open = () => {
    document.body.classList.add('menu-open');
    toggle.setAttribute('aria-expanded', 'true');
    const firstLink = menu.querySelector(focusableSelector);
    window.requestAnimationFrame(() => {
      if (!document.body.classList.contains('menu-open')) return;
      firstLink?.focus({ preventScroll: true });
    });
  };

  toggle.addEventListener('click', () => {
    if (document.body.classList.contains('menu-open')) close({ restoreFocus: true });
    else open();
  });

  menu.addEventListener('click', (event) => {
    if (event.target.closest('a[href]')) close();
  });

  document.addEventListener('pointerdown', (event) => {
    if (!document.body.classList.contains('menu-open')) return;
    if (menu.contains(event.target) || toggle.contains(event.target)) return;
    close();
  });

  document.addEventListener('keydown', (event) => {
    if (!document.body.classList.contains('menu-open')) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      close({ restoreFocus: true });
      return;
    }
    if (event.key !== 'Tab') return;

    const candidates = [...(header || menu).querySelectorAll(focusableSelector)]
      .filter((element) => {
        const style = window.getComputedStyle(element);
        return !element.hidden && element.getAttribute('aria-hidden') !== 'true'
          && style.display !== 'none' && style.visibility !== 'hidden';
      });
    if (candidates.length === 0) return;
    const first = candidates[0];
    const last = candidates[candidates.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  });
}

function setupPointerInput(canvas, scene, { keyboard = false } = {}) {
  if (!canvas || !scene) return;
  const touchPointers = new Set();
  const position = canvas.id === 'hero-canvas' ? { x: 0.76, y: 0.26 } : { x: 0.5, y: 0.62 };

  const normalize = (event) => {
    const bounds = canvas.getBoundingClientRect();
    if (!bounds.width || !bounds.height) return position;
    position.x = clamp((event.clientX - bounds.left) / bounds.width);
    position.y = clamp((event.clientY - bounds.top) / bounds.height);
    return position;
  };

  canvas.addEventListener('pointerdown', (event) => {
    if (event.pointerType !== 'touch' || !event.isPrimary) return;
    touchPointers.add(event.pointerId);
    const point = normalize(event);
    safeSceneCall(scene, 'setPointer', point.x, point.y, true);
  });

  canvas.addEventListener('pointermove', (event) => {
    if (event.pointerType === 'touch' && !touchPointers.has(event.pointerId)) return;
    const point = normalize(event);
    safeSceneCall(scene, 'setPointer', point.x, point.y, true);
  }, { passive: true });

  const release = (event) => {
    if (event.pointerType === 'touch') touchPointers.delete(event.pointerId);
    safeSceneCall(scene, 'setPointer', position.x, position.y, false);
  };
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('pointerleave', release);

  if (!keyboard) return;
  canvas.tabIndex = 0;
  const label = canvas.getAttribute('aria-label') || '視点体験';
  canvas.setAttribute('aria-label', `${label}。矢印キーで人物を動かせます。`);
  canvas.addEventListener('keydown', (event) => {
    const step = event.shiftKey ? 0.1 : 0.035;
    const movement = {
      ArrowLeft: [-step, 0],
      ArrowRight: [step, 0],
      ArrowUp: [0, -step],
      ArrowDown: [0, step],
    }[event.key];
    if (!movement) return;
    event.preventDefault();
    position.x = clamp(position.x + movement[0]);
    position.y = clamp(position.y + movement[1]);
    safeSceneCall(scene, 'setPointer', position.x, position.y, true);
  });
  canvas.addEventListener('keyup', (event) => {
    if (!event.key.startsWith('Arrow')) return;
    safeSceneCall(scene, 'setPointer', position.x, position.y, false);
  });
  canvas.addEventListener('blur', () => {
    safeSceneCall(scene, 'setPointer', position.x, position.y, false);
  });
}

function setupCursorEcho(getReducedMotion) {
  const echo = document.querySelector('#cursor-echo');
  const finePointer = window.matchMedia('(pointer: fine)');
  if (!echo || !finePointer.matches) return;

  const history = [];
  let animationFrame = 0;
  let visible = false;

  const render = (now) => {
    if (!visible || getReducedMotion()) {
      echo.classList.remove('is-visible');
      animationFrame = 0;
      return;
    }
    const targetTime = now - ECHO_DELAY_MS;
    while (history.length > 1 && history[1].time <= targetTime) history.shift();
    const point = history[0];
    if (point && point.time <= targetTime) {
      echo.style.transform = `translate3d(${point.x}px, ${point.y}px, 0) translate(-50%, -50%)`;
      echo.classList.add('is-visible');
    }
    const latest = history[history.length - 1];
    if (latest && now >= latest.time + ECHO_DELAY_MS) {
      history.splice(0, Math.max(0, history.length - 1));
      animationFrame = 0;
      return;
    }
    animationFrame = window.requestAnimationFrame(render);
  };

  document.addEventListener('pointermove', (event) => {
    if (event.pointerType === 'touch' || getReducedMotion()) return;
    visible = true;
    history.push({ x: event.clientX, y: event.clientY, time: performance.now() });
    while (history.length > 120) history.shift();
    if (!animationFrame) animationFrame = window.requestAnimationFrame(render);
  }, { passive: true });

  document.documentElement.addEventListener('pointerleave', () => {
    visible = false;
    history.length = 0;
    echo.classList.remove('is-visible');
  });
}

function setupViewControls(scene, isReducedMotion, pulseSound) {
  const buttons = [...document.querySelectorAll('[data-view]')];
  const title = document.querySelector('#view-title');
  const copy = document.querySelector('#view-copy');
  const description = title?.closest('.view-description');
  const views = [
    {
      title: '01 / 外から見る',
      copy: '部屋の外から、自分の動きを見つめます。',
    },
    {
      title: '02 / 視点が変わる',
      copy: '固定された場所を切り替えるたび、身体との距離が変わります。',
    },
    {
      title: '03 / 自分と、すれ違う',
      copy: 'いまの姿と、少し前の姿が、同じ画面に残ります。',
    },
  ];

  const selectView = (index, { sound = true } = {}) => {
    const view = views[index];
    if (!view) return;
    buttons.forEach((button) => {
      button.setAttribute('aria-pressed', String(Number(button.dataset.view) === index));
    });
    if (title) title.textContent = view.title;
    if (copy) copy.textContent = view.copy;
    safeSceneCall(scene, 'setView', index);
    if (sound) pulseSound();

    if (!isReducedMotion() && description?.animate) {
      description.animate([
        { opacity: 0.58, transform: 'translate3d(0, 5px, 0)' },
        { opacity: 1, transform: 'translate3d(0, 0, 0)' },
      ], { duration: 180, easing: 'cubic-bezier(.2,.7,.2,1)' });
    }
  };

  buttons.forEach((button) => {
    button.addEventListener('click', () => selectView(Number(button.dataset.view)));
    button.addEventListener('keydown', (event) => {
      const current = buttons.indexOf(button);
      let next = null;
      if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (current + 1) % buttons.length;
      if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (current - 1 + buttons.length) % buttons.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = buttons.length - 1;
      if (next === null) return;
      event.preventDefault();
      buttons[next].focus();
      selectView(Number(buttons[next].dataset.view));
    });
  });

  selectView(0, { sound: false });
  return selectView;
}

function setupSound() {
  const toggle = document.querySelector('#sound-toggle');
  let context = null;
  let master = null;
  let enabled = false;
  let suspendTimer = 0;
  if (toggle) toggle.dataset.audioState = 'uninitialized';

  const syncAudioState = () => {
    if (!toggle) return;
    toggle.dataset.audioState = context?.state === 'running' ? 'running' : 'suspended';
  };

  const createAudio = () => {
    if (context) return;
    const AudioContext = window.AudioContext || window.webkitAudioContext;
    if (!AudioContext) throw new Error('Web Audio is unavailable.');
    context = new AudioContext();
    context.addEventListener('statechange', syncAudioState);
    master = context.createGain();
    master.gain.value = 0;
    master.connect(context.destination);

    const ambient = context.createGain();
    ambient.gain.value = 0.0045;
    ambient.connect(master);
    [43.65, 65.41].forEach((frequency, index) => {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = index === 0 ? 'sine' : 'triangle';
      oscillator.frequency.value = frequency;
      gain.gain.value = index === 0 ? 0.7 : 0.3;
      oscillator.connect(gain).connect(ambient);
      oscillator.start();
    });
    syncAudioState();
  };

  const setEnabled = async (nextEnabled) => {
    window.clearTimeout(suspendTimer);
    try {
      if (nextEnabled) {
        createAudio();
        await context.resume();
        syncAudioState();
        enabled = true;
        master.gain.cancelScheduledValues(context.currentTime);
        master.gain.setTargetAtTime(0.8, context.currentTime, 0.08);
      } else {
        enabled = false;
        if (context && master) {
          master.gain.cancelScheduledValues(context.currentTime);
          master.gain.setTargetAtTime(0, context.currentTime, 0.035);
          suspendTimer = window.setTimeout(() => context.suspend().catch(() => undefined), 160);
        }
      }
    } catch (error) {
      enabled = false;
      console.warn('Sound could not be enabled.', error);
      syncAudioState();
    }
    setPressed(toggle, enabled);
  };

  toggle?.addEventListener('click', () => setEnabled(!enabled));
  document.addEventListener('visibilitychange', () => {
    if (!context) return;
    if (document.hidden) context.suspend().catch(() => undefined);
    else if (enabled) context.resume().catch(() => {
      enabled = false;
      setPressed(toggle, false);
    });
  });

  const pulse = () => {
    if (!enabled || !context || context.state !== 'running' || !master) return;
    const now = context.currentTime;
    const oscillator = context.createOscillator();
    const gain = context.createGain();
    oscillator.type = 'sine';
    oscillator.frequency.setValueAtTime(132, now);
    oscillator.frequency.exponentialRampToValueAtTime(84, now + 0.16);
    gain.gain.setValueAtTime(0.022, now);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.18);
    oscillator.connect(gain).connect(master);
    oscillator.start(now);
    oscillator.stop(now + 0.19);
  };

  return { pulse, get enabled() { return enabled; } };
}

function setupMotion(mediaQuery, scenes) {
  const toggle = document.querySelector('#motion-toggle');
  let reduced = getInitialMotionPreference(mediaQuery);
  let hasExplicitPreference = readStorage(MOTION_KEY) !== null;

  const apply = (nextReduced, { persist = false } = {}) => {
    reduced = nextReduced;
    document.body.classList.toggle('motion-reduced', reduced);
    setPressed(toggle, reduced);
    scenes.forEach((scene) => safeSceneCall(scene, 'setReducedMotion', reduced));
    if (reduced) {
      document.querySelectorAll('[data-reveal]').forEach((element) => element.classList.add('is-visible'));
      document.querySelector('#cursor-echo')?.classList.remove('is-visible');
    }
    if (persist) {
      hasExplicitPreference = true;
      writeStorage(MOTION_KEY, reduced ? 'reduce' : 'full');
    }
  };

  toggle?.addEventListener('click', () => apply(!reduced, { persist: true }));
  const onSystemChange = (event) => {
    if (!hasExplicitPreference) apply(event.matches);
  };
  if (typeof mediaQuery.addEventListener === 'function') mediaQuery.addEventListener('change', onSystemChange);
  else mediaQuery.addListener(onSystemChange);

  apply(reduced);
  return { apply, get reduced() { return reduced; } };
}

function setupScrollEffects(heroScene, perspectiveScene, getReducedMotion) {
  const reveals = [...document.querySelectorAll('[data-reveal]')];
  if (getReducedMotion() || !('IntersectionObserver' in window)) {
    reveals.forEach((element) => element.classList.add('is-visible'));
  } else {
    const revealObserver = new IntersectionObserver((entries, observer) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-visible');
        observer.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -9% 0px', threshold: 0.08 });
    reveals
      .filter((element) => !element.closest('#hero'))
      .forEach((element) => revealObserver.observe(element));
  }

  const sections = [
    ['#hero', '01', 'FIRST VIEW'],
    ['#about', '01', 'ABOUT'],
    ['#perspective', '02', 'DISPLACED VIEW'],
    ['#visit', '03', 'EXHIBITION'],
    ['#return', '04', 'AFTERIMAGE'],
  ].map(([selector, number, label]) => ({ element: document.querySelector(selector), number, label }))
    .filter(({ element }) => element);
  const chapter = document.querySelector('#current-chapter');
  const setChapter = ({ number, label }) => {
    if (!chapter || chapter.dataset.chapter === `${number}-${label}`) return;
    chapter.dataset.chapter = `${number}-${label}`;
    const numberElement = document.createElement('span');
    numberElement.textContent = number;
    chapter.replaceChildren(numberElement, ` ${label}`);
  };

  if ('IntersectionObserver' in window) {
    const chapterObserver = new IntersectionObserver((entries) => {
      const visible = entries
        .filter((entry) => entry.isIntersecting)
        .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
      const match = visible && sections.find(({ element }) => element === visible.target);
      if (match) setChapter(match);
    }, { rootMargin: '-35% 0px -50% 0px', threshold: [0, 0.25, 0.6] });
    sections.forEach(({ element }) => chapterObserver.observe(element));
  }

  const hero = document.querySelector('#hero');
  const perspective = document.querySelector('#perspective');
  const visibility = new Map([[heroScene, Boolean(hero)], [perspectiveScene, Boolean(perspective)]]);
  const updatePaused = () => {
    visibility.forEach((isVisible, scene) => safeSceneCall(scene, 'setPaused', document.hidden || !isVisible));
  };
  if ('IntersectionObserver' in window) {
    const sceneObserver = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.target === hero) visibility.set(heroScene, entry.isIntersecting);
        if (entry.target === perspective) visibility.set(perspectiveScene, entry.isIntersecting);
      });
      updatePaused();
    }, { rootMargin: '20% 0px', threshold: 0 });
    if (hero && heroScene) sceneObserver.observe(hero);
    if (perspective && perspectiveScene) sceneObserver.observe(perspective);
  }

  const progressFill = document.querySelector('#progress-fill');
  let scheduled = false;
  const update = () => {
    scheduled = false;
    const root = document.documentElement;
    const scrollRange = Math.max(1, root.scrollHeight - window.innerHeight);
    const progress = clamp(window.scrollY / scrollRange);
    if (progressFill) {
      progressFill.style.transformOrigin = 'left center';
      progressFill.style.transform = `scaleX(${progress})`;
    }
    if (hero && heroScene) {
      const rect = hero.getBoundingClientRect();
      safeSceneCall(heroScene, 'setProgress', clamp(-rect.top / Math.max(1, rect.height)));
    }
    if (perspective && perspectiveScene) {
      const rect = perspective.getBoundingClientRect();
      const sectionProgress = clamp((window.innerHeight - rect.top) / (window.innerHeight + rect.height));
      safeSceneCall(perspectiveScene, 'setProgress', sectionProgress);
    }
  };
  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    window.requestAnimationFrame(update);
  };
  window.addEventListener('scroll', schedule, { passive: true });
  window.addEventListener('resize', schedule, { passive: true });
  document.addEventListener('visibilitychange', updatePaused);
  update();
  updatePaused();
  return updatePaused;
}

function setupReplay(scene, isReducedMotion) {
  const replay = document.querySelector('#replay');
  const perspective = document.querySelector('#perspective');
  const canvas = document.querySelector('#perspective-canvas');
  const scrollTarget = canvas?.closest('.perspective-frame') || canvas || perspective;
  if (!replay || !perspective || !canvas || !scene) return;
  let pendingObserver = null;
  let pendingTimeout = 0;
  let replayRequest = 0;

  const clearPending = () => {
    pendingObserver?.disconnect();
    pendingObserver = null;
    window.clearTimeout(pendingTimeout);
    pendingTimeout = 0;
  };

  replay.addEventListener('click', (event) => {
    replayRequest += 1;
    const requestId = replayRequest;
    clearPending();
    event.preventDefault();
    if (window.location.hash !== '#perspective') history.pushState(null, '', '#perspective');
    const reduced = isReducedMotion();
    canvas.focus({ preventScroll: true });
    scrollTarget.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth', block: 'center' });
    if (reduced || !('IntersectionObserver' in window)) {
      safeSceneCall(scene, 'replay');
      return;
    }

    let completed = false;
    const minimumVisibility = 0.8;
    const visibleRatio = () => {
      const bounds = canvas.getBoundingClientRect();
      if (!bounds.height) return 0;
      const visibleHeight = Math.max(0, Math.min(bounds.bottom, window.innerHeight) - Math.max(bounds.top, 0));
      return clamp(visibleHeight / bounds.height);
    };
    const play = () => {
      if (completed || requestId !== replayRequest) return;
      completed = true;
      clearPending();
      safeSceneCall(scene, 'replay');
    };
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting && entry.intersectionRatio >= minimumVisibility)) play();
    }, { threshold: [minimumVisibility] });
    pendingObserver = observer;
    observer.observe(canvas);
    if (visibleRatio() >= minimumVisibility) play();
    if (!completed) {
      pendingTimeout = window.setTimeout(() => {
        if (completed || requestId !== replayRequest) return;
        if (visibleRatio() >= minimumVisibility) play();
        else {
          completed = true;
          clearPending();
        }
      }, 5000);
    }
  });
}

function setupDialog() {
  const dialog = document.querySelector('#visual-dialog');
  const openButton = document.querySelector('#open-visual');
  const closeButton = document.querySelector('#close-visual');
  if (!dialog || !openButton || !closeButton) return;
  let returnFocus = openButton;

  openButton.addEventListener('click', () => {
    returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : openButton;
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
    closeButton.focus({ preventScroll: true });
  });
  closeButton.addEventListener('click', () => dialog.close());
  dialog.addEventListener('pointerdown', (event) => {
    if (event.target === dialog) dialog.close();
  });
  dialog.addEventListener('close', () => returnFocus?.focus({ preventScroll: true }));
}

async function boot() {
  const mediaQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
  const initialReducedMotion = getInitialMotionPreference(mediaQuery);
  const navigation = performance.getEntriesByType('navigation')[0];
  const skipIntro = initialReducedMotion || Boolean(window.location.hash) || navigation?.type === 'back_forward';
  const returningVisitor = readStorage(INTRO_KEY) === '1';
  const loader = createLoadingController({
    reducedMotion: initialReducedMotion,
    skipIntro,
    returningVisitor,
  });
  const scenes = [];
  let refreshScenePause = () => undefined;

  try {
    const heroCanvas = document.querySelector('#hero-canvas');
    const perspectiveCanvas = document.querySelector('#perspective-canvas');
    const heroScene = heroCanvas
      ? createScene(heroCanvas, { reducedMotion: initialReducedMotion, variant: 'hero' })
      : null;
    const perspectiveScene = perspectiveCanvas
      ? createScene(perspectiveCanvas, { reducedMotion: initialReducedMotion, variant: 'interactive' })
      : null;
    if (heroScene) scenes.push(heroScene);
    if (perspectiveScene) scenes.push(perspectiveScene);

    const motion = setupMotion(mediaQuery, scenes);
    const sound = setupSound();
    setupMenu();
    setupPointerInput(heroCanvas, heroScene);
    setupPointerInput(perspectiveCanvas, perspectiveScene, { keyboard: true });
    setupCursorEcho(() => motion.reduced);
    const selectView = setupViewControls(perspectiveScene, () => motion.reduced, sound.pulse);
    refreshScenePause = setupScrollEffects(heroScene, perspectiveScene, () => motion.reduced);
    setupReplay(perspectiveScene, () => motion.reduced);
    setupDialog();

    window.mawarimi = Object.freeze({
      get reducedMotion() { return motion.reduced; },
      get soundEnabled() { return sound.enabled; },
      get sceneMetrics() {
        return Object.freeze({
          hero: heroScene?.metrics ? Object.freeze({ ...heroScene.metrics }) : null,
          perspective: perspectiveScene?.metrics ? Object.freeze({ ...perspectiveScene.metrics }) : null,
        });
      },
      replay: () => safeSceneCall(perspectiveScene, 'replay'),
      setView: (index) => selectView?.(clamp(Math.round(Number(index)), 0, 2)),
    });

    const readiness = [
      ...scenes.map((scene) => scene.ready),
      ...collectCriticalImages(),
    ];
    await runIntro(loader, readiness, returningVisitor);
  } catch (error) {
    console.error('Mawarimi website initialization failed.', error);
    document.querySelectorAll('[data-reveal]').forEach((element) => element.classList.add('is-visible'));
  } finally {
    loader.finish({ immediate: skipIntro });
  }

  window.addEventListener('pagehide', (event) => {
    scenes.forEach((scene) => {
      if (event.persisted) safeSceneCall(scene, 'setPaused', true);
      else safeSceneCall(scene, 'dispose');
    });
  });
  window.addEventListener('pageshow', (event) => {
    if (!event.persisted) return;
    loader.finish({ immediate: true });
    refreshScenePause();
  });
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot, { once: true });
} else {
  boot();
}
