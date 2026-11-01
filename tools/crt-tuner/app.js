'use strict';

(() => {
  const $ = id => document.getElementById(id);
  const storageKey = 'fixedcam-crt-tuner-adopted-0302';
  const dollStorageKey = 'fixedcam-crt-tuner-doll-v1';
  const activeModeKey = 'fixedcam-crt-tuner-active-mode';
  const legacyStorageKey = 'fixedcam-crt-tuner-v1';
  const controls = new Map();
  const inputs = new Map();
  let config;
  let parameters = {};
  let camera;
  let mode = 'normal';
  let source = 'live';
  let revision = 0;
  let renderedRevision = -1;
  let renderedCamera;
  let renderedMode;
  let renderedSource;
  let renderedLabel;
  let currentUrl;
  let currentBlob;
  let inFlight = false;
  let pending = false;
  let renderTimer;
  let retryTimer;
  let loadingConfig = false;
  let retryMode;
  let previousView = 'current';
  let controlsEpoch = 0;

  function status(message) {
    $('status').textContent = message;
  }

  function normalize(value, control) {
    const bounded = Math.max(control.min, Math.min(control.max, value));
    const stepped = control.min + Math.round((bounded - control.min) / control.step) * control.step;
    return Number(Math.max(control.min, Math.min(control.max, stepped)).toFixed(10));
  }

  function format(value, control) {
    const decimals = Math.min(10, Math.max(0, Math.ceil(-Math.log10(control.step)) + 1));
    return new Intl.NumberFormat('ja-JP', { maximumFractionDigits: decimals }).format(value);
  }

  function syncControl(key, skipNumber = false) {
    const control = controls.get(key);
    const fields = inputs.get(key);
    fields.range.value = parameters[key];
    if (!skipNumber) fields.number.value = parameters[key];
    fields.output.textContent = `${format(parameters[key], control)}${control.unit ? ` ${control.unit}` : ''}`;
  }

  function snapshot() {
    return { version: 1, mode, source, camera, parameters: { ...parameters }, baseLabel: config.baseLabel };
  }

  const modeLabel = value => value === 'doll' ? '人形視点' : '通常画面';
  const sourceLabel = value => config.sources.find(item => item.id === value)?.label ?? value;

  function updateSourceControls() {
    const pov = source !== 'live';
    for (const key of ['sigmaR', 'sigmaGB', 'convergenceMultiplier']) {
      const fields = inputs.get(key);
      fields.range.disabled = pov;
      fields.number.disabled = pov;
    }
    const monochrome = parameters.mono >= 1;
    for (const field of [inputs.get('saturation').range, inputs.get('saturation').number]) field.disabled = monochrome;
    $('source-note').hidden = !pov && !monochrome;
    $('source-note').textContent = [
      pov ? '人形視点素材では、本編と同じく赤・緑青のぼけと色の位置ずれを使いません。これらは無人写真を選ぶと調整できます。' : '',
      monochrome ? '加工量1では白黒になるため、彩度は加工量を下げると調整できます。' : ''
    ].filter(Boolean).join(' ');
  }

  function persist() {
    try {
      localStorage.setItem(mode === 'doll' ? dollStorageKey : storageKey, JSON.stringify(snapshot()));
      localStorage.setItem(activeModeKey, mode);
    } catch {
      $('file-message').textContent = 'このブラウザに保存できません。設定をファイルに保存できます。';
    }
  }

  function updateCaption() {
    if (!currentUrl) return;
    const suffix = renderedRevision === revision ? '' : ' · 最新の描画を待っています';
    $('current-caption').textContent = `現在の調整 · ${renderedLabel}${suffix}`;
    $('save-png').disabled = renderedRevision !== revision;
  }

  function changed() {
    revision += 1;
    pending = true;
    clearTimeout(retryTimer);
    retryTimer = undefined;
    persist();
    updateCaption();
    status('Unityの描画待ち…');
    clearTimeout(renderTimer);
    renderTimer = setTimeout(render, 80);
  }

  function setValue(key, value, skipNumber) {
    const normalized = normalize(value, controls.get(key));
    if (parameters[key] === normalized) return;
    parameters[key] = normalized;
    syncControl(key, skipNumber);
    if (key === 'mono') updateSourceControls();
    changed();
  }

  function makeControl(control, index) {
    // Removing a focused old input can emit blur during a mode's DOM rebuild.
    // Ignore that input instead of writing its value into the new mode.
    const epoch = controlsEpoch;
    const wrapper = document.createElement('div');
    wrapper.className = 'control';
    const heading = document.createElement('div');
    heading.className = 'control-heading';
    const label = document.createElement('label');
    label.htmlFor = `range-${index}`;
    label.textContent = control.label;
    const output = document.createElement('output');
    output.htmlFor = `range-${index} number-${index}`;
    heading.append(label, output);
    const row = document.createElement('div');
    row.className = 'control-inputs';
    const range = document.createElement('input');
    const number = document.createElement('input');
    range.type = 'range';
    number.type = 'number';
    range.id = `range-${index}`;
    number.id = `number-${index}`;
    number.setAttribute('aria-label', `${control.label}の数値`);
    number.inputMode = 'decimal';
    for (const input of [range, number]) {
      input.min = control.min;
      input.max = control.max;
      input.step = control.step;
      input.name = `${control.key}-${input.type}`;
    }
    range.addEventListener('input', () => {
      if (epoch === controlsEpoch) setValue(control.key, range.valueAsNumber, false);
    });
    number.addEventListener('input', () => {
      if (epoch === controlsEpoch && Number.isFinite(number.valueAsNumber)) setValue(control.key, number.valueAsNumber, true);
    });
    const commitNumber = () => {
      if (epoch !== controlsEpoch) return;
      if (Number.isFinite(number.valueAsNumber)) setValue(control.key, number.valueAsNumber, false);
      syncControl(control.key);
    };
    number.addEventListener('change', commitNumber);
    number.addEventListener('blur', commitNumber);
    number.addEventListener('keydown', event => {
      if (epoch !== controlsEpoch) return;
      if (event.key === 'Enter') { event.preventDefault(); commitNumber(); }
      if (event.key === 'Escape') { event.preventDefault(); syncControl(control.key); }
    });
    row.append(range, number);
    const limits = document.createElement('div');
    limits.className = 'range-labels';
    for (const value of [control.min, control.max]) {
      const bound = document.createElement('span');
      bound.textContent = format(value, control);
      limits.append(bound);
    }
    wrapper.append(heading, row, limits);
    inputs.set(control.key, { range, number, output });
    return wrapper;
  }

  function buildControls() {
    controlsEpoch += 1;
    const fragment = document.createDocumentFragment();
    const priority = group => mode === 'doll' && group.id === 'signal' ? -1
      : /center|centre|中心/.test(`${group.id} ${group.label}`) ? 0
      : /cold|tint|冷|寒色|色づけ/i.test(`${group.id} ${group.label}`) ? 1 : 2;
    const groups = [...config.groups].sort((a, b) => priority(a) - priority(b));
    let index = 0;
    for (const group of groups) {
      const primary = priority(group) < 2;
      const section = document.createElement(primary ? 'section' : 'details');
      section.className = 'control-group';
      const heading = document.createElement(primary ? 'h2' : 'summary');
      heading.textContent = group.id === 'signal' ? '画面の加工量' : group.label;
      section.append(heading);
      for (const control of group.controls) {
        if (control.key === 'mono') control.label = mode === 'doll' ? '人形視点の加工量' : '通常画面の加工量';
        section.append(makeControl(control, index++));
      }
      fragment.append(section);
    }
    $('groups').replaceChildren(fragment);
    $('groups').setAttribute('aria-busy', 'false');
    for (const key of controls.keys()) syncControl(key);
    $('cameras').replaceChildren();
    for (const name of config.cameras) {
      const label = document.createElement('label');
      label.className = 'camera-choice';
      const input = document.createElement('input');
      input.type = 'radio';
      input.name = 'camera';
      input.value = name;
      input.checked = camera === name;
      const text = document.createElement('span');
      text.textContent = name;
      input.addEventListener('change', () => {
        if (!input.checked || camera === name) return;
        camera = name;
        loadBaseline();
        changed();
      });
      label.append(input, text);
      $('cameras').append(label);
    }
    $('base-label').textContent = `ベース：${config.baseLabel}`;
    $('mode').value = mode;
    $('source').replaceChildren();
    for (const item of config.sources) {
      const option = document.createElement('option');
      option.value = item.id;
      option.textContent = item.label;
      $('source').append(option);
    }
    $('source').value = source;
    updateSourceControls();
    $('mode-note').textContent = mode === 'doll'
      ? '本編2-Cの人形視点が基準です。加工量1・暗部ノイズ0.175から調整できます。人形視点の素材は各動画の0.5秒の静止コマです。'
      : '本人が調整した本編の採用値が基準です。加工量0.5・暗部ノイズ0.05から調整できます。';
    for (const id of ['camera-options', 'mode', 'source', 'reset-baseline', 'reset-defaults', 'save-json', 'load-json']) $(id).disabled = false;
  }

  function readSettings(value) {
    if (!value || typeof value !== 'object' || value.version !== 1 || !config.cameras.includes(value.camera)
      || !value.parameters || typeof value.parameters !== 'object' || Array.isArray(value.parameters)) {
      throw new Error('この設定ファイルは読み込めません。バージョンとカメラを確認してください。');
    }
    const settingsMode = value.mode ?? 'normal';
    if (!['normal', 'doll'].includes(settingsMode)) throw new Error('設定ファイルの画面モードが不正です。');
    if (settingsMode !== mode) throw new Error(`「${modeLabel(settingsMode)}」に切り替えてから設定を読み込んでください。`);
    const settingsSource = value.source ?? 'live';
    if (!config.sources.some(item => item.id === settingsSource)) throw new Error('この画面では使えない素材が指定されています。');
    const result = { ...config.defaults };
    let known = 0;
    for (const [key, control] of controls) {
      if (!Object.hasOwn(value.parameters, key)) continue;
      const numeric = value.parameters[key];
      if (typeof numeric !== 'number' || !Number.isFinite(numeric) || numeric < control.min || numeric > control.max) {
        throw new Error(`「${control.label}」の値が範囲外か、数値ではありません。`);
      }
      result[key] = normalize(numeric, control);
      known += 1;
    }
    if (!known) throw new Error('この画面で使える調整値がありません。');
    return { camera: value.camera, source: settingsSource, parameters: result };
  }

  function applySettings(settings) {
    const cameraChanged = camera !== settings.camera || source !== settings.source;
    camera = settings.camera;
    source = settings.source;
    parameters = { ...settings.parameters };
    for (const key of controls.keys()) syncControl(key);
    for (const input of $('cameras').querySelectorAll('input')) input.checked = input.value === camera;
    $('source').value = source;
    updateSourceControls();
    if (cameraChanged) loadBaseline();
    changed();
  }

  function loadImage(id, url, errorMessage) {
    const image = $(`${id}-image`);
    const empty = $(`${id}-empty`);
    image.hidden = true;
    empty.hidden = false;
    empty.textContent = id === 'baseline' ? '基準画像を読み込んでいます…' : '参照画像を読み込んでいます…';
    image.onload = () => { image.hidden = false; empty.hidden = true; };
    image.onerror = () => { image.hidden = true; empty.hidden = false; empty.textContent = errorMessage; };
    image.src = url;
  }

  function loadBaseline() {
    $('baseline-caption').textContent = `本編の採用値 · ${modeLabel(mode)} · ${sourceLabel(source)} · カメラ ${camera}`;
    const query = new URLSearchParams({mode, source});
    loadImage('baseline', `/baseline/${encodeURIComponent(camera)}.png?${query}`, '基準画像を読み込めません。再接続で読み直せます。');
  }

  function updateView() {
    const view = $('view').value;
    $('images').dataset.view = view;
    $('current-figure').hidden = !['current', 'compare'].includes(view);
    $('baseline-figure').hidden = !['baseline', 'compare'].includes(view);
    $('reference-figure').hidden = view !== 'reference';
    $('compare-toggle').setAttribute('aria-pressed', String(view === 'baseline'));
  }

  function decodedImage(url) {
    return new Promise((resolve, reject) => {
      const image = new Image();
      image.onload = () => image.naturalWidth > 0 && image.naturalHeight > 0 ? resolve() : reject(new Error('empty image'));
      image.onerror = () => reject(new Error('invalid image'));
      image.src = url;
    });
  }

  async function render() {
    if (!config || inFlight || !pending) return;
    clearTimeout(renderTimer);
    clearTimeout(retryTimer);
    retryTimer = undefined;
    pending = false;
    inFlight = true;
    const requestedRevision = revision;
    const request = snapshot();
    const requestLabel = `${modeLabel(mode)} · ${sourceLabel(source)} · カメラ ${camera}`;
    let candidateUrl;
    try {
      status('Unityで描画しています…');
      const response = await fetch('/api/render', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ camera: request.camera, mode: request.mode, source: request.source, parameters: request.parameters }), cache: 'no-store'
      });
      if (!response.ok) throw new Error(`render ${response.status}`);
      const blob = await response.blob();
      if (!blob.size || !blob.type.toLowerCase().startsWith('image/png')) throw new Error('missing PNG');
      candidateUrl = URL.createObjectURL(blob);
      await decodedImage(candidateUrl);
      if (requestedRevision !== revision) return;
      const oldUrl = currentUrl;
      currentUrl = candidateUrl;
      candidateUrl = undefined;
      currentBlob = blob;
      renderedCamera = request.camera;
      renderedMode = request.mode;
      renderedSource = request.source;
      renderedLabel = requestLabel;
      renderedRevision = requestedRevision;
      $('current-image').src = currentUrl;
      $('current-image').hidden = false;
      $('current-empty').hidden = true;
      if (oldUrl) URL.revokeObjectURL(oldUrl);
      updateCaption();
      const milliseconds = Number(response.headers.get('X-Render-Ms'));
      $('render-info').textContent = response.headers.has('X-Render-Ms') && Number.isFinite(milliseconds)
        ? `描画 ${new Intl.NumberFormat('ja-JP', { maximumFractionDigits: 0 }).format(milliseconds)} ms · 1280 × 1030`
        : '1280 × 1030';
      status('最新の調整を表示しています');
      $('retry').hidden = true;
    } catch {
      pending = true;
      status('Unityの描画待ち · 接続を確かめています…');
      $('retry').hidden = false;
      if (!currentUrl) $('current-empty').textContent = 'Unityの最初の描画を待っています…';
      retryTimer = setTimeout(render, 1800);
    } finally {
      if (candidateUrl) URL.revokeObjectURL(candidateUrl);
      inFlight = false;
      // A newer edit takes priority over a failed older request's retry delay.
      if (pending && requestedRevision !== revision) {
        clearTimeout(retryTimer);
        retryTimer = undefined;
      }
      if (pending && !retryTimer) renderTimer = setTimeout(render, 0);
    }
  }

  function download(blob, filename) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  function validateConfig(value) {
    if (!value || value.version !== 1 || !Array.isArray(value.groups) || !Array.isArray(value.cameras)
      || !value.cameras.length || !value.cameras.every(name => typeof name === 'string')
      || typeof value.baseLabel !== 'string' || !value.defaults || !value.baseline) throw new Error('invalid config');
    if (!['normal', 'doll'].includes(value.mode) || !Array.isArray(value.sources) || !value.sources.length
      || !value.sources.every(item => typeof item.id === 'string' && typeof item.label === 'string')
      || new Set(value.sources.map(item => item.id)).size !== value.sources.length
      || !value.sources.some(item => item.id === value.defaultSource)) throw new Error('invalid sources');
    const metadata = new Map();
    for (const group of value.groups) {
      if (typeof group.id !== 'string' || typeof group.label !== 'string' || !Array.isArray(group.controls)) throw new Error('invalid group');
      for (const control of group.controls) {
        if (typeof control.key !== 'string' || typeof control.label !== 'string' || metadata.has(control.key)
          || ![control.min, control.max, control.step].every(Number.isFinite) || control.min >= control.max || control.step <= 0
          || ![value.defaults[control.key], value.baseline[control.key]].every(number => typeof number === 'number'
            && Number.isFinite(number) && number >= control.min && number <= control.max)) throw new Error('invalid control');
        // Unity serializes float32 metadata with binary tails; keep input steps
        // as the authored decimal values so arrow keys and number fields agree.
        for (const field of ['min', 'max', 'step']) control[field] = Number(control[field].toPrecision(7));
        metadata.set(control.key, control);
      }
    }
    if (!metadata.size) throw new Error('no controls');
    return metadata;
  }

  async function initialize(targetMode = mode) {
    if (loadingConfig) return;
    loadingConfig = true;
    $('file-message').textContent = '';
    retryMode = targetMode;
    revision += 1;
    pending = false;
    $('save-png').disabled = true;
    $('mode').disabled = true;
    $('source').disabled = true;
    try {
      status('設定を読み込んでいます…');
      const response = await fetch(`/api/config?mode=${encodeURIComponent(targetMode)}`, { cache: 'no-store' });
      if (!response.ok) throw new Error('config unavailable');
      const value = await response.json();
      const metadata = validateConfig(value);
      if (value.mode !== targetMode) throw new Error('wrong mode');
      config = value;
      mode = targetMode;
      controls.clear();
      inputs.clear();
      for (const [key, control] of metadata) controls.set(key, control);
      camera = mode === 'doll' && config.cameras.includes('C') ? 'C' : config.cameras[0];
      source = config.defaultSource;
      parameters = Object.fromEntries([...controls].map(([key, control]) => [key, normalize(config.defaults[key], control)]));
      try {
        $('restore-previous').hidden = mode !== 'normal' || !localStorage.getItem(legacyStorageKey);
        const saved = localStorage.getItem(mode === 'doll' ? dollStorageKey : storageKey);
        if (saved) {
          const restored = readSettings(JSON.parse(saved));
          camera = restored.camera;
          source = restored.source;
          parameters = restored.parameters;
          $('file-message').textContent = `${modeLabel(mode)}の前回の調整値を復元しました。`;
        }
      } catch {
        $('file-message').textContent = '前回の設定を復元できなかったため、本編の採用値で開きました。';
      }
      buildControls();
      retryMode = undefined;
      $('current-image').hidden = true;
      $('current-empty').hidden = false;
      $('current-empty').textContent = `${modeLabel(mode)}を描画しています…`;
      loadBaseline();
      loadImage('reference', '/reference.jpg', '参照画像を読み込めません。再接続で読み直せます。');
      $('retry').hidden = true;
      changed();
    } catch {
      status('設定を読み込めません · 接続を確認して再接続してください');
      $('retry').hidden = false;
    } finally {
      loadingConfig = false;
      $('mode').disabled = false;
      $('mode').value = mode;
    }
  }

  $('mode').addEventListener('change', () => {
    if (config) persist();
    initialize($('mode').value);
  });
  $('source').addEventListener('change', () => {
    source = $('source').value;
    updateSourceControls();
    loadBaseline();
    changed();
  });

  $('view').addEventListener('change', updateView);
  $('compare-toggle').addEventListener('click', () => {
    if ($('view').value === 'baseline') $('view').value = previousView;
    else { previousView = $('view').value; $('view').value = 'baseline'; }
    updateView();
  });
  $('reset-baseline').addEventListener('click', () => applySettings(readSettings({ version: 1, mode, source, camera, parameters: config.baseline })));
  $('reset-defaults').addEventListener('click', () => applySettings(readSettings({ version: 1, mode, source, camera, parameters: config.defaults })));
  $('restore-previous').addEventListener('click', () => {
    try {
      applySettings(readSettings(JSON.parse(localStorage.getItem(legacyStorageKey))));
      $('file-message').textContent = '更新前の調整値を読み込みました。本編の採用値と比較できます。';
    } catch { $('file-message').textContent = '更新前の調整値を読み込めません。'; }
  });
  $('save-json').addEventListener('click', () => {
    download(new Blob([JSON.stringify(snapshot(), null, 2) + '\n'], { type: 'application/json' }), `crt-${mode}-${source}-${camera}.json`);
    $('file-message').textContent = '現在の調整値を保存しました。';
  });
  $('load-json').addEventListener('click', () => $('json-file').click());
  $('json-file').addEventListener('change', async () => {
    const file = $('json-file').files[0];
    if (!file) return;
    try {
      if (file.size > 1024 * 1024) throw new Error('設定ファイルは1 MB以下にしてください。');
      let parsed;
      try { parsed = JSON.parse((await file.text()).replace(/^\uFEFF/, '')); }
      catch { throw new Error('JSON形式の設定ファイルを選んでください。'); }
      applySettings(readSettings(parsed));
      $('file-message').textContent = '設定を読み込みました。';
    } catch (error) {
      $('file-message').textContent = error.message;
    } finally { $('json-file').value = ''; }
  });
  $('show-frame').addEventListener('change', () => {
    document.documentElement.dataset.showFrame = String($('show-frame').checked);
  });
  $('save-png').addEventListener('click', async () => {
    if (!currentBlob || renderedRevision !== revision) return;
    const filename = `${renderedMode}-${renderedSource}-${renderedCamera}`;
    const savedBlob = currentBlob;
    if ($('show-frame').checked) { download(savedBlob, `crt-${filename}.png`); return; }
    const mask = new Image();
    mask.src = '/screen-mask.png';
    const savedImage = new Image();
    const savedUrl = URL.createObjectURL(savedBlob);
    savedImage.src = savedUrl;
    try {
      await Promise.all([mask.decode(), savedImage.decode()]);
      const canvas = document.createElement('canvas');
      canvas.width = 1280; canvas.height = 1030;
      const context = canvas.getContext('2d');
      context.drawImage(savedImage, 0, 0, 1280, 1030);
      context.globalCompositeOperation = 'destination-in';
      context.drawImage(mask, 0, 0, 1280, 1030);
      canvas.toBlob(blob => { if (blob) download(blob, `crt-screen-${filename}.png`); }, 'image/png');
    } catch { $('file-message').textContent = '切り抜き用の形を読み込めません。もう一度保存してください。'; }
    finally { URL.revokeObjectURL(savedUrl); }
  });
  $('retry').addEventListener('click', () => {
    if (retryMode !== undefined) { initialize(retryMode); return; }
    if (!config) { initialize(); return; }
    loadBaseline();
    loadImage('reference', '/reference.jpg', '参照画像を読み込めません。');
    clearTimeout(retryTimer);
    retryTimer = undefined;
    pending = true;
    render();
  });
  window.addEventListener('pagehide', event => {
    if (event.persisted) return;
    if (currentUrl) { URL.revokeObjectURL(currentUrl); currentUrl = undefined; }
    clearTimeout(renderTimer);
    clearTimeout(retryTimer);
  });
  try { mode = localStorage.getItem(activeModeKey) === 'doll' ? 'doll' : 'normal'; } catch { }
  initialize(mode);
})();
