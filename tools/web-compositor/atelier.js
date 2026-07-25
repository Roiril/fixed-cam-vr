// 素材工房 — カメラ別のプロンプトと生成結果を 1 か所に束ねる。
//
// 設計の芯（.claude/plans/2026-07-26_material-atelier.md）:
//   貯める単位は「プロンプト」ではなく **生成**（入力フレーム + 指示 + 出力 + 採否）。
//   プロンプトだけ残しても「それを使ったら何が出たか」が分からず使えない、が出発点。
//
//   レシピ  = 場所に依存しない演出テンプレ。{{スロット}} を持ち、カメラをまたいで再利用する
//   束縛値  = そのカメラでの具体値（位置・動作）。ここだけが場所依存
//   生成    = レシピ × 束縛値 × 入力フレームの 1 回の試行。全文プロンプトを焼き込んで残す
//
// 素材は構図に属する。カメラ A のフレームから作った動画は A のカットでしか使えないので、
// UI もデータもカメラで分ける（カット側で素材を選ぶときの絞り込みにそのまま効く）。

const $ = (s, r = document) => r.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"]/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// 固定カメラ i2v の不変部。マスク合成が前提なので「足す」より「変えるな」を強く書く。
const SPINE = `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon.`;

const STARTER_RECIPES = [
  {
    name: '人影が立っている',
    slug: 'standing',
    kind: 'video',
    intent: 'マスク合成の本命。動きが少ないほど継ぎ目が安定するので、最初の 1 本はこれから試す。',
    slots: ['位置', '見た目', '動き'],
    body: `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
A figure is standing motionless at {{位置}}, {{見た目}}. {{動き}} It never approaches the camera and stays in place for the whole clip.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure. The figure casts a soft contact shadow on the floor.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down). Practical-effects horror, no glow, no supernatural aura.
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, extra limbs, deformed hands.`,
  },
  {
    name: '横切る（一瞬よぎる）',
    slug: 'crossing',
    kind: 'video',
    intent: '絵コンテ A1 / A3 の「隅で何かが一瞬よぎる」。1 秒未満で通過させ、残りは完全な無人に保つ。',
    slots: ['入る側', '出る側', '遮蔽物'],
    body: `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
A dark human silhouette crosses the frame quickly, entering from {{入る側}} and exiting at {{出る側}}, partially occluded by {{遮蔽物}}, visible for less than one second. Motion blur consistent with a phone camera at 30fps. The rest of the clip is completely empty and static.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon.`,
  },
  {
    name: '無人のまま異変（人を出さない）',
    slug: 'ambient',
    kind: 'video',
    intent: '人物ブロックの切り分けにも使える保険。これが通ればパイプライン自体は生きていると分かる。',
    slots: ['動くもの', '起きること'],
    body: `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
The room stays completely empty of people. {{動くもの}} {{起きること}} Nothing else in the room moves.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, people, figures, humans.`,
  },
];

// レシピ面はカメラではない擬似タブ。カメラ一覧に無いので存在チェックから除外する必要がある。
const RECIPES_TAB = '__recipes__';

export function createAtelier({ root, getCameras }) {
  let data = { rev: 0, recipes: [], generations: [] };
  let frames = {};
  let activeCam = null;
  let busy = false;

  // 編集中の下書き（カメラごとに独立して覚える）
  const drafts = new Map();

  function draftFor(label) {
    if (!drafts.has(label)) {
      drafts.set(label, {
        sourceFrame: '', recipeId: '', bind: {}, promptOverride: '',
        params: { tool: 'dreamina', model: 'seedance 4.0 pro', aspect: '4:3', sec: 4, seed: '' },
      });
    }
    return drafts.get(label);
  }

  const cams = () => {
    const list = (getCameras() || []).map((c, i) => ({ label: c.id || String(i), index: i }));
    return list.length ? list : [{ label: 'A', index: 0 }];
  };

  // ---- サーバ I/O -----------------------------------------------------------

  async function api(path, body) {
    const r = await fetch(path, { method: 'POST', body: JSON.stringify(body || {}) });
    const j = await r.json();
    if (j && j.state) data = j.state;
    return j;
  }

  async function refresh() {
    try {
      const [a, f] = await Promise.all([
        fetch('/atelier').then((r) => r.json()),
        fetch('/atelier/frames').then((r) => r.json()),
      ]);
      data = a && a.recipes ? a : data;
      frames = f || {};
    } catch { /* サーバ未起動 */ }
    render();
  }

  // ---- プロンプト合成 -------------------------------------------------------

  function recipeById(id) { return data.recipes.find((r) => r.id === id) || null; }

  // {{スロット}} を束縛値で置換する。空のスロットは印を残して未入力だと分かるようにする。
  function compose(d) {
    if (d.promptOverride) return d.promptOverride;
    const r = recipeById(d.recipeId);
    if (!r) return '';
    return String(r.body || '').replace(/\{\{([^}]+)\}\}/g, (m, k) => {
      const v = (d.bind || {})[k.trim()];
      return v && String(v).trim() ? String(v).trim() : m;
    });
  }

  const missingSlots = (d) => {
    const r = recipeById(d.recipeId);
    if (!r) return [];
    return (r.slots || []).filter((s) => !((d.bind || {})[s] || '').trim());
  };

  // ---- 描画 -----------------------------------------------------------------

  function render() {
    if (!root) return;
    const list = cams();
    // レシピ面は擬似タブなのでカメラ一覧の存在チェックから除外する（除外し忘れると常に A へ戻る）。
    if (activeCam !== RECIPES_TAB && (!activeCam || !list.some((c) => c.label === activeCam))) {
      activeCam = list[0].label;
    }

    root.innerHTML = `
      <div class="atl-tabs" role="tablist">
        ${list.map((c) => {
          const n = data.generations.filter((g) => g.cameraLabel === c.label).length;
          const k = data.generations.filter((g) => g.cameraLabel === c.label && g.verdict === 'keep').length;
          return `<button class="atl-tab${c.label === activeCam ? ' on' : ''}" data-cam="${esc(c.label)}"
            role="tab" aria-selected="${c.label === activeCam}">カメラ ${esc(c.label)}
            <span class="atl-count">${k}/${n}</span></button>`;
        }).join('')}
        <button class="atl-tab${activeCam === RECIPES_TAB ? ' on' : ''}" data-cam="${RECIPES_TAB}" role="tab"
          aria-selected="${activeCam === RECIPES_TAB}">📐 レシピ <span class="atl-count">${data.recipes.length}</span></button>
      </div>
      <div class="atl-body">${activeCam === RECIPES_TAB ? recipesPane() : cameraPane(activeCam)}</div>`;

    root.querySelectorAll('.atl-tab').forEach((b) => {
      b.onclick = () => { activeCam = b.dataset.cam; render(); };
    });
    if (activeCam === RECIPES_TAB) wireRecipes(); else wireCamera(activeCam);
  }

  // ---- カメラ面 -------------------------------------------------------------

  function cameraPane(label) {
    const d = draftFor(label);
    const fr = frames[label] || [];
    const gens = data.generations
      .filter((g) => g.cameraLabel === label)
      .sort((a, b) => String(b.createdAt || '').localeCompare(String(a.createdAt || '')));
    const prompt = compose(d);
    const miss = missingSlots(d);
    const r = recipeById(d.recipeId);

    return `
    <div class="atl-make">
      <div class="atl-col">
        <div class="atl-h">① 入力フレーム<span class="atl-sub">この構図に足す。生成物はこのカメラ専用になる</span></div>
        ${fr.length ? `<div class="atl-frames">${fr.slice(0, 12).map((f) => `
          <button class="atl-frame${d.sourceFrame === f.url ? ' on' : ''}" data-url="${esc(f.url)}"
            title="${esc(f.name)}"><img src="${esc(f.url)}" alt=""><span>${esc(f.name.slice(-17, -4))}</span></button>`).join('')}</div>`
          : `<p class="atl-empty">カメラ ${esc(label)} の静止画が <code>recordings/</code> にありません。<br>
             カメラ列の <b>📷</b> で 1 枚撮ってから戻ってきてください。</p>`}
      </div>

      <div class="atl-col">
        <div class="atl-h">② 指示<span class="atl-sub">レシピ＝場所によらない骨格 / 束縛＝このカメラでの具体値</span></div>
        <select class="atl-recipe">
          <option value="">— レシピを選ぶ —</option>
          ${data.recipes.map((x) => `<option value="${esc(x.id)}"${x.id === d.recipeId ? ' selected' : ''}>${esc(x.name)}${x.usedCount ? `（${x.keptCount}/${x.usedCount} 採用）` : ''}</option>`).join('')}
        </select>
        ${r ? `<p class="atl-intent">${esc(r.intent || '')}</p>` : ''}
        ${r ? (r.slots || []).map((s) => `
          <label class="atl-slot"><span>${esc(s)}</span>
            <input type="text" data-slot="${esc(s)}" value="${esc((d.bind || {})[s] || '')}"
              placeholder="このカメラでの具体値…"></label>`).join('') : ''}

        <div class="atl-h2">合成プロンプト${miss.length ? `<span class="atl-warn">未入力: ${esc(miss.join(' / '))}</span>` : ''}</div>
        <textarea class="atl-prompt" rows="8" spellcheck="false">${esc(prompt)}</textarea>

        <div class="atl-params">
          <label>道具<input type="text" data-p="tool" value="${esc(d.params.tool)}"></label>
          <label>モデル<input type="text" data-p="model" value="${esc(d.params.model)}"></label>
          <label>比<input type="text" data-p="aspect" value="${esc(d.params.aspect)}"></label>
          <label>秒<input type="number" data-p="sec" min="1" max="15" value="${esc(d.params.sec)}"></label>
          <label>seed<input type="text" data-p="seed" value="${esc(d.params.seed)}" placeholder="任意"></label>
        </div>

        <div class="atl-actions">
          <button class="atl-copy accent" ${prompt ? '' : 'disabled'}>📋 コピーして記録する</button>
          <button class="atl-openframe" ${d.sourceFrame ? '' : 'disabled'}>🖼 フレームを開く</button>
        </div>
        <p class="atl-note">コピーと同時に<b>送信待ちのレコード</b>を作ります。生成できたら下のカードへ動画を落としてください。</p>
      </div>
    </div>

    <div class="atl-h">③ 生成結果（カメラ ${esc(label)}）<span class="atl-sub">入力・指示・出力・採否がひとかたまり。<code>atelier-index.md</code> にも同じものが出ます</span></div>
    ${gens.length ? `<div class="atl-grid">${gens.map(genCard).join('')}</div>`
      : `<p class="atl-empty">まだ生成がありません。上でレシピを選んで 📋 を押すところから。</p>`}`;
  }

  function genCard(g) {
    const bind = Object.entries(g.bind || {}).filter(([, v]) => String(v || '').trim())
      .map(([k, v]) => `${k}=${v}`).join(' / ');
    const p = g.params || {};
    const cond = [p.tool, p.model, p.aspect, p.sec && `${p.sec}s`, p.seed && `seed ${p.seed}`]
      .filter(Boolean).join(' · ');
    const out = g.outputUrl
      ? (/\.(mp4|webm|mov)$/i.test(g.outputUrl)
        ? `<video src="${esc(g.outputUrl)}" controls loop muted playsinline preload="metadata"></video>`
        : `<img src="${esc(g.outputUrl)}" alt="">`)
      : `<div class="atl-drop" data-drop="${esc(g.id)}">
           <b>⬇ 生成した動画をここへ</b><span>ドラッグ＆ドロップ / クリックで選択</span></div>`;
    return `
    <article class="atl-card v-${esc(g.verdict || 'unrated')}" data-gen="${esc(g.id)}">
      <div class="atl-out">${out}</div>
      <div class="atl-meta">
        <div class="atl-cardhead">
          <strong>${esc(g.recipeName || '(レシピなし)')}</strong>
          <span class="atl-status">${g.outputUrl ? '取り込み済み' : '送信待ち'}</span>
        </div>
        ${bind ? `<div class="atl-bind">${esc(bind)}</div>` : ''}
        ${cond ? `<div class="atl-cond">${esc(cond)}</div>` : ''}
        ${g.sourceFrame ? `<div class="atl-src">入力 <code>${esc(g.sourceFrame.split('/').pop())}</code></div>` : ''}
        <div class="atl-verdicts">
          <button data-v="keep" class="${g.verdict === 'keep' ? 'on' : ''}">✅ 採用</button>
          <button data-v="reject" class="${g.verdict === 'reject' ? 'on' : ''}">✕ 不採用</button>
          <button data-v="unrated" class="${(g.verdict || 'unrated') === 'unrated' ? 'on' : ''}">― 未評価</button>
        </div>
        <input class="atl-memo" type="text" value="${esc(g.note || '')}" placeholder="メモ（何が良かった / 悪かった）">
        <details class="atl-promptbox"><summary>プロンプト全文</summary><pre>${esc(g.prompt || '')}</pre></details>
        <div class="atl-cardfoot">
          <button class="atl-again">↻ この設定で作り直す</button>
          <button class="atl-copy1">📋</button>
          <button class="atl-del">🗑</button>
        </div>
      </div>
    </article>`;
  }

  function wireCamera(label) {
    const d = draftFor(label);
    const pane = root;

    pane.querySelectorAll('.atl-frame').forEach((b) => {
      b.onclick = () => { d.sourceFrame = b.dataset.url; render(); };
    });

    const sel = $('.atl-recipe', pane);
    if (sel) sel.onchange = () => { d.recipeId = sel.value; d.promptOverride = ''; render(); };

    pane.querySelectorAll('.atl-slot input').forEach((inp) => {
      inp.oninput = () => {
        d.bind[inp.dataset.slot] = inp.value;
        d.promptOverride = '';
        const ta = $('.atl-prompt', pane);
        if (ta) ta.value = compose(d);
      };
    });

    const ta = $('.atl-prompt', pane);
    if (ta) ta.oninput = () => { d.promptOverride = ta.value; };

    pane.querySelectorAll('.atl-params input').forEach((inp) => {
      inp.oninput = () => { d.params[inp.dataset.p] = inp.type === 'number' ? +inp.value : inp.value; };
    });

    const openBtn = $('.atl-openframe', pane);
    if (openBtn) openBtn.onclick = () => window.open(d.sourceFrame, '_blank');

    const copyBtn = $('.atl-copy', pane);
    if (copyBtn) copyBtn.onclick = async () => {
      if (busy) return;
      busy = true;
      const prompt = ($('.atl-prompt', pane) || {}).value || compose(d);
      try { await navigator.clipboard.writeText(prompt); } catch { /* 権限なし */ }
      const r = recipeById(d.recipeId);
      const cam = cams().find((c) => c.label === label) || { index: 0 };
      await api('/atelier/gen', {
        camera: cam.index, cameraLabel: label,
        sourceFrame: d.sourceFrame, recipeId: d.recipeId, recipeName: r ? r.name : '',
        recipeSlug: r ? (r.slug || '') : '',
        bind: { ...d.bind }, params: { ...d.params }, prompt, status: 'draft', verdict: 'unrated',
      });
      busy = false;
      render();
      flash(copyBtn, 'コピーしました');
    };

    wireCards(pane);
  }

  function wireCards(pane) {
    pane.querySelectorAll('.atl-card').forEach((card) => {
      const id = card.dataset.gen;
      const rec = data.generations.find((g) => g.id === id);

      card.querySelectorAll('.atl-verdicts button').forEach((b) => {
        b.onclick = async () => { await api('/atelier/gen', { id, verdict: b.dataset.v }); render(); };
      });

      const memo = $('.atl-memo', card);
      if (memo) {
        let t = 0;
        memo.oninput = () => {
          clearTimeout(t);
          t = setTimeout(() => api('/atelier/gen', { id, note: memo.value }), 400);
        };
      }

      const del = $('.atl-del', card);
      if (del) del.onclick = async () => {
        if (!confirm('この生成レコードを消します（動画ファイル自体は captures/ に残ります）。')) return;
        await api('/atelier/gen/delete', { id });
        render();
      };

      const copy1 = $('.atl-copy1', card);
      if (copy1) copy1.onclick = async () => {
        try { await navigator.clipboard.writeText(rec ? rec.prompt || '' : ''); } catch {}
        flash(copy1, '📋 コピー');
      };

      const again = $('.atl-again', card);
      if (again) again.onclick = () => {
        if (!rec) return;
        const d = draftFor(rec.cameraLabel);
        d.sourceFrame = rec.sourceFrame || '';
        d.recipeId = rec.recipeId || '';
        d.bind = { ...(rec.bind || {}) };
        d.params = { ...(rec.params || d.params), seed: '' };
        d.promptOverride = rec.prompt || '';
        d.parentId = rec.id;
        render();
        root.scrollIntoView({ behavior: 'smooth', block: 'start' });
      };

      const drop = $('.atl-drop', card);
      if (drop) wireDrop(drop, id);
    });
  }

  // 生成物の取り込み。ドロップとファイル選択の両方を同じ経路に通す。
  function wireDrop(el, id) {
    const send = async (file) => {
      if (!file) return;
      const ext = (file.name.split('.').pop() || 'mp4').toLowerCase();
      el.textContent = '取り込み中…';
      try {
        const r = await fetch(`/atelier/attach?id=${encodeURIComponent(id)}&ext=${encodeURIComponent(ext)}`,
          { method: 'POST', body: file });
        const j = await r.json();
        if (j && j.state) data = j.state;
      } catch { /* サーバ断 */ }
      render();
    };
    el.onclick = () => {
      const inp = document.createElement('input');
      inp.type = 'file';
      inp.accept = 'video/*,image/*';
      inp.onchange = () => send(inp.files[0]);
      inp.click();
    };
    el.ondragover = (e) => { e.preventDefault(); el.classList.add('over'); };
    el.ondragleave = () => el.classList.remove('over');
    el.ondrop = (e) => {
      e.preventDefault();
      el.classList.remove('over');
      send(e.dataTransfer.files && e.dataTransfer.files[0]);
    };
  }

  // ---- レシピ面 -------------------------------------------------------------

  function recipesPane() {
    return `
    <p class="atl-lead">レシピは<b>場所に依存しない骨格</b>だけを持ちます。「奥の左隅に」のような場所の話は
      <code>{{スロット}}</code> にして、カメラごとの束縛値で埋めます。これで同じ演出を別のカメラでも使い回せます。</p>
    ${data.recipes.length ? '' : `<div class="atl-seed">
      <p>まだレシピがありません。固定カメラ i2v 用の定番 3 本を入れて始められます。</p>
      <button class="atl-starter accent">＋ 定番レシピを入れる</button></div>`}
    <div class="atl-recipes">
      ${data.recipes.map((r) => `
      <article class="atl-rcard" data-rec="${esc(r.id)}">
        <div class="atl-rhead">
          <input class="atl-rname" type="text" value="${esc(r.name || '')}" placeholder="レシピ名">
          <input class="atl-rslug" type="text" value="${esc(r.slug || '')}" placeholder="slug"
            title="生成物のファイル名に入る ASCII の短縮名（例 standing）">
          <span class="atl-rstat">${r.usedCount ? `${r.keptCount}/${r.usedCount} 採用` : '未使用'}</span>
          <button class="atl-rdel">🗑</button>
        </div>
        <input class="atl-rintent" type="text" value="${esc(r.intent || '')}" placeholder="狙い（いつ使うか）">
        <textarea class="atl-rbody" rows="7" spellcheck="false">${esc(r.body || '')}</textarea>
        <div class="atl-rslots">スロット <code>${esc((r.slots || []).join(' / ') || '—')}</code>
          <span class="atl-sub">本文の <code>{{…}}</code> から自動で拾います</span></div>
      </article>`).join('')}
    </div>
    <div class="atl-actions"><button class="atl-rnew">＋ 空のレシピ</button>
      <button class="atl-spine">📋 共通の骨格だけコピー</button></div>`;
  }

  function wireRecipes() {
    const pane = root;
    const starter = $('.atl-starter', pane);
    if (starter) starter.onclick = async () => {
      for (const r of STARTER_RECIPES) await api('/atelier/recipe', r);
      render();
    };
    const nw = $('.atl-rnew', pane);
    if (nw) nw.onclick = async () => {
      await api('/atelier/recipe', { name: '新しいレシピ', kind: 'video', body: SPINE, slots: [] });
      render();
    };
    const sp = $('.atl-spine', pane);
    if (sp) sp.onclick = async () => {
      try { await navigator.clipboard.writeText(SPINE); } catch {}
      flash(sp, 'コピーしました');
    };

    pane.querySelectorAll('.atl-rcard').forEach((card) => {
      const id = card.dataset.rec;
      const save = debounce(async () => {
        const body = $('.atl-rbody', card).value;
        const slots = [...new Set([...body.matchAll(/\{\{([^}]+)\}\}/g)].map((m) => m[1].trim()))];
        await api('/atelier/recipe', {
          id, name: $('.atl-rname', card).value, intent: $('.atl-rintent', card).value,
          slug: $('.atl-rslug', card).value.replace(/[^A-Za-z0-9]/g, '').slice(0, 16),
          body, slots,
        });
        const el = $('.atl-rslots code', card);
        if (el) el.textContent = slots.join(' / ') || '—';
      }, 500);
      ['.atl-rname', '.atl-rslug', '.atl-rintent', '.atl-rbody'].forEach((s) => { $(s, card).oninput = save; });
      $('.atl-rdel', card).onclick = async () => {
        if (!confirm('このレシピを消します（過去の生成レコードは残ります）。')) return;
        await api('/atelier/recipe/delete', { id });
        render();
      };
    });
  }

  // ---- 小物 -----------------------------------------------------------------

  function debounce(fn, ms) {
    let t = 0;
    return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
  }

  function flash(btn, msg) {
    const old = btn.textContent;
    btn.textContent = msg;
    setTimeout(() => { btn.textContent = old; }, 1200);
  }

  refresh();
  return { refresh, render };
}
