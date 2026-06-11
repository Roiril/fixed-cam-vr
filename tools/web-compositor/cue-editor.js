// 🎬 cue エディタ — コンポジット検証タブで作ったマスクを「差し替え演出 cue」として
// show.json に登録する（T5）。発火・運用はコンソールタブ / Unity 側。
//
// マスク変換の要点:
//   web-compositor のマスクは 白=白スロット(live) / 黒=黒スロット(差し替え)。
//   Unity の ScreenComposite._MaskTex は R チャンネル 白=差し替え。
//   → 書き出し時に「反転」して保存する（黒スロット領域が白 PNG になる）。

const $ = (id) => document.getElementById(id);
const status = (msg, err = false) => {
  const el = $('cueStatus');
  el.textContent = msg;
  el.style.color = err ? 'var(--danger-color)' : 'var(--accent-color)';
};

async function fetchState() {
  return await (await fetch('/state')).json();
}

// ---- セレクト類の populate ---------------------------------------------------
async function refreshCameraSelect() {
  try {
    const s = await fetchState();
    const sel = $('cueCamera');
    sel.innerHTML = '';
    for (const cam of s.cameras || []) {
      const o = document.createElement('option');
      o.value = cam.id;
      o.textContent = `${cam.id}（${cam.sourceId || '?'}）`;
      sel.appendChild(o);
    }
  } catch (e) { /* サーバ未起動 */ }
}

async function refreshSourceSelect() {
  try {
    const items = await (await fetch('/captures/list')).json();
    const sel = $('cueSource');
    sel.innerHTML = '<option value="">（素材を選ぶ）</option>';
    for (const it of items) {
      const o = document.createElement('option');
      o.value = it.url;
      o.textContent = `${it.type === 'video' ? '🎞' : '🖼'} ${it.name}`;
      sel.appendChild(o);
    }
  } catch (e) { /* サーバ未起動 */ }
}

// ---- マスク書き出し（反転）---------------------------------------------------
async function exportInvertedMask(slug) {
  const mask = window.compositorMask;
  if (!mask) throw new Error('マスクが初期化されていない');
  const src = mask.element || mask.canvas;
  const c = document.createElement('canvas');
  c.width = src.width; c.height = src.height;
  const g = c.getContext('2d');
  g.filter = 'invert(1)';
  g.drawImage(src, 0, 0);
  const blob = await new Promise((r) => c.toBlob(r, 'image/png'));
  const res = await (await fetch(`/masks?name=${encodeURIComponent(slug)}`, {
    method: 'POST', body: blob,
  })).json();
  if (!res.ok) throw new Error(res.error || 'マスク保存失敗');
  return res.url;
}

// ---- cue 保存 -----------------------------------------------------------------
function slugify(name) {
  // サーバの /masks は [A-Za-z0-9_-] のみ受けるので、日本語名は時刻ベースの slug に落とす
  const ascii = name.replace(/[^A-Za-z0-9_-]/g, '');
  if (ascii.length >= 3) return ascii.slice(0, 48);
  const t = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  return `cue_${t.getFullYear()}${pad(t.getMonth() + 1)}${pad(t.getDate())}_${pad(t.getHours())}${pad(t.getMinutes())}${pad(t.getSeconds())}`;
}

async function saveCue(withMask) {
  const name = $('cueName').value.trim();
  const sourceUrl = ($('cueSourceUrl').value.trim() || $('cueSource').value || '').trim();
  if (!name) return status('cue 名を入れて', true);
  if (!sourceUrl) return status('差し替え素材を選んで（or URL 直書き）', true);

  const id = slugify(name);
  try {
    let maskUrl = '';
    if (withMask) {
      status('マスク書き出し中…');
      maskUrl = await exportInvertedMask(id);
    }
    const cue = {
      id,
      name,
      camera: $('cueCamera').value || '',
      maskUrl,
      sourceUrl,
      strength: parseFloat($('cueStrength').value) || 1,
      loop: $('cueLoop').checked,
      fadeIn: parseFloat($('cueFadeIn').value) || 0.5,
      fadeOut: parseFloat($('cueFadeOut').value) || 0.5,
    };
    const s = await fetchState();
    const cues = s.cues || [];
    const i = cues.findIndex((c) => c.id === id);
    if (i >= 0) cues[i] = cue; else cues.push(cue);
    const res = await (await fetch('/state', {
      method: 'POST', body: JSON.stringify({ cues }),
    })).json();
    if (!res.ok) throw new Error(res.error || 'state 保存失敗');
    status(`✓ cue「${name}」を保存（id=${id}${maskUrl ? ' / マスク付き' : ' / 全面'}）。コンソールタブから発火できる`);
  } catch (e) {
    status('保存失敗: ' + e.message, true);
  }
}

// ---- 起動 -----------------------------------------------------------------------
$('cueSave').onclick = () => saveCue(true);
$('cueSaveFull').onclick = () => saveCue(false);
$('cueSourceRefresh').onclick = () => { refreshSourceSelect(); refreshCameraSelect(); };
refreshCameraSelect();
refreshSourceSelect();
