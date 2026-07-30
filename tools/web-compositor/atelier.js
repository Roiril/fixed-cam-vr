// 素材工房 — 「この構図に、この動画を重ねたらどう見えるか」を確かめる試写室。
//
// 設計の芯（.claude/plans/2026-07-26_material-atelier.md）:
//   固定カメラ映像に AI 生成の異変を重ねる作り方の判断基準は「動画単体の出来」ではなく
//   **その構図の live に載せた時に破綻しないか** の 1 点しかない。だから工房の中心は
//   生成レコードの一覧ではなく **合成の見え** であり、記録（指示・全文プロンプト・
//   採否）はその作業の副産物として自動で溜まればよい。
//
//   列 = 1 カメラ。1 列の中で「種フレーム → 指示 → 生成物 → 合成の見え」が閉じる。
//   上から ① 試写 ② 種 ③ 指示 ④ 棚。**作業順ではなく重要度順**（試写は作業中いちばん長く見る）。
//
//   保存した指示 = よく使うプロンプトの棚（旧「レシピ」）。選ぶと本文がそのまま ③ の欄に入る。
//     穴埋めスロットは 2026-07-30 に撤去した — 骨格はサーバが自動で足すようになり、
//     残るのは作者が書く一行なので、スロットを命名するコストの方が高くついていた
//   生成 = 指示 × 種フレームの 1 回の試行。全文プロンプトを焼き込んで残す
//   採用 = **保存しない**。show.json の cues[].sourceUrl がその出力を指していれば採用（導出）
import { createCompositeView } from './composite-view.js';
import {
  captures, refreshCaptures, onCaptures, isVideoUrl, encPath, FX_DEFAULT,
  MW, MH, FRAME_ASPECT, containRect, blendCfg,
} from './common.js';
import { bakeColorMatch } from './color-match.js';

const $ = (s, r = document) => r.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"]/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const fileOf = (u) => String(u || '').split('/').pop();

// 差分マスクの作業解像度（ぼかして使うので粗くてよい）。素材と種を同じ寸法へ潰して画素差を取る。
//   書き出す PNG は common.js の MW×MH（= スクリーン枠空間 16:9）。作業解像度とは座標系が違い、
//   ソース座標 → 枠空間の contain-fit 変換を挟む（`bakeMask`）。ここを素通しすると実機だけずれる。
const DW = 192, DH = 144;

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
    intent: 'マスク合成の本命。動きが少ないほど継ぎ目が安定するので、最初の 1 本はこれから試す。太字の所を書き換えて使う。',
    body: `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
A figure is standing motionless at the far left corner of the room, wearing a long dirty white robe, long black hair covering the face. It stays completely still, only the hair drifts slightly. It never approaches the camera and stays in place for the whole clip.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure. The figure casts a soft contact shadow on the floor.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down). Practical-effects horror, no glow, no supernatural aura.
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, extra limbs, deformed hands.`,
  },
  {
    name: '横切る（一瞬よぎる）',
    slug: 'crossing',
    kind: 'video',
    intent: '絵コンテ A1 / A3 の「隅で何かが一瞬よぎる」。1 秒未満で通過させ、残りは完全な無人に保つ。入る側・出る側・遮蔽物を書き換えて使う。',
    body: `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
A dark human silhouette crosses the frame quickly, entering from the right edge and exiting behind the partition panel, partially occluded by the partition, visible for less than one second. Motion blur consistent with a phone camera at 30fps. The rest of the clip is completely empty and static.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon.`,
  },
  {
    name: '無人のまま異変（人を出さない）',
    slug: 'ambient',
    kind: 'video',
    intent: '人物ブロックの切り分けにも使える保険。これが通ればパイプライン自体は生きていると分かる。動くもの・起きることを書き換えて使う。',
    body: `Static locked-off tripod security camera. The camera does not move, pan, zoom, or shake at all.
The room stays completely empty of people. One of the partition panels shifts a few centimeters by itself. The fluorescent light flickers once, weakly. Nothing else in the room moves.
The room, walls, partition panels, ceiling, floor, furniture, lighting and framing must stay EXACTLY as in the input image. Do not change color grading or exposure.
Photorealistic, matches the input photo's lighting and lens (wide-angle, high mounted, looking down).
Negative: camera movement, zoom, pan, parallax, relighting, style change, text, watermark, anime, cartoon, people, figures, humans.`,
  },
];

/**
 * @param deps {
 *   root, getCameras,
 *   getLiveImg(camId),   // 監視列の生ライブ <img>（📷 と「背景=ライブ」用）。無ければ null
 *   getCamPost(camId),   // 試写のグレーディング（cameras[i].post）
 *   getAllCues(),        // cue id 衝突回避
 *   saveCue(cue),        // state.cues へ upsert（Promise<{ok}>）
 *   onCueSaved(cue),
 * }
 */
export function createAtelier(deps) {
  const root = deps.root;
  let data = { rev: 0, recipes: [], generations: [] };
  let frames = {};              // cameraLabel -> [{name,url,mtime}]（新しい順）
  const panes = new Map();      // cameraLabel -> pane
  let camsSig = '';

  const cams = () => {
    const list = (deps.getCameras() || []).map((c, i) => ({
      id: c.id || String(i), index: i, role: c.role === 'fx' ? 'fx' : 'zone',
    }));
    return list.length ? list : [{ id: 'A', index: 0, role: 'zone' }];
  };

  // ---- サーバ I/O -----------------------------------------------------------

  async function api(path, body) {
    try {
      const r = await fetch(path, { method: 'POST', body: JSON.stringify(body || {}) });
      const j = await r.json();
      if (j && j.state) { data = j.state; applyData(); }
      return j;
    } catch { return { ok: false }; }
  }

  async function refresh() {
    try {
      const [a, f] = await Promise.all([
        fetch('/atelier').then((r) => r.json()),
        fetch('/atelier/frames').then((r) => r.json()),
      ]);
      if (a && a.recipes) data = a;
      frames = f || {};
    } catch { /* サーバ未起動: 直近の内容で描き続ける */ }
    render();
  }

  // 撮った 1 枚を待たずに反映する（リロードも面の往復もさせない）。
  function notifyFrameSaved(camId, info) {
    if (!info || !info.url) return;
    const list = frames[camId] || (frames[camId] = []);
    if (!list.some((f) => f.url === info.url)) {
      list.unshift({ name: info.name || fileOf(info.url), url: info.url, mtime: Date.now() / 1000 });
    }
    const p = panes.get(camId);
    if (p) { p.renderSeeds(); if (!p.st.seedUrl) p.selectSeed(info.url); }
  }

  // ---- プロンプト合成 -------------------------------------------------------

  const recipeById = (id) => data.recipes.find((r) => r.id === id) || null;

  /**
   * 保存したプロンプトを選ぶと、その本文が**そのまま**テキストエリアに入る。
   *
   * `{{スロット}}` の穴埋め機構は 2026-07-30 に撤去した。不変部（locked-off / 部屋を変えるな /
   * negative 列）はサーバが `/generate` で自動付与するようになり、テンプレに残るのは作者が書く
   * 一行だけになった。その一行を穴埋めの形に分解して名前を付けるコストは、文をそのまま書く
   * コストを上回る（レシピ 3 本に対し実際の生成は 1 件で、しかもレシピを使っていなかった）。
   */
  function compose(st) {
    if (st.promptOverride) return st.promptOverride;
    const r = recipeById(st.recipeId);
    return r ? String(r.body || '') : '';
  }

  /** 埋め忘れの `{{…}}` が残っていないか（移行してきた古い本文に含まれることがある）。 */
  const leftoverSlots = (text) =>
    [...new Set([...String(text || '').matchAll(/\{\{([^}]+)\}\}/g)].map((m) => m[1].trim()))];

  // ---- 全体の骨組み ---------------------------------------------------------

  function render() {
    if (!root) return;
    if (!root.dataset.built) {
      root.dataset.built = '1';
      root.innerHTML = `
        <div class="atl-bar">
          <span class="spacer"></span>
          <button class="atl-reload" title="種フレームと生成レコードを読み直す">↻ 読み直す</button>
          <button class="atl-opendir" title="撮影フォルダ（recordings/）を開く">📂 撮影</button>
          <button class="atl-opencap" title="素材フォルダ（captures/）を開く">📂 素材</button>
        </div>
        <details class="atl-recipes-box"><summary>📝 保存した指示（よく使うプロンプト）<span class="atl-rcount"></span></summary>
          <div class="atl-recipes-body"></div>
        </details>
        <div class="atl-cams"></div>`;
      $('.atl-reload', root).onclick = () => { refreshCaptures(); refresh(); };
      $('.atl-opendir', root).onclick = () => fetch('/open-dir?dir=recordings').catch(() => {});
      $('.atl-opencap', root).onclick = () => fetch('/open-dir?dir=captures').catch(() => {});
    }
    syncCams();
    applyData();
  }

  function syncCams() {
    const list = cams();
    const sig = list.map((c) => c.id).join(',');
    const wrap = $('.atl-cams', root);
    if (sig !== camsSig) {
      camsSig = sig;
      for (const p of panes.values()) p.destroy();
      panes.clear();
      wrap.innerHTML = '';
      wrap.style.setProperty('--atl-n', String(list.length));
      for (const c of list) {
        const p = createPane(c);
        panes.set(c.id, p);
        wrap.appendChild(p.el);
      }
    }
  }

  // データ更新の反映。入力中のテキストを飛ばさないため、**署名が変わった部分だけ**作り直す。
  function applyData() {
    const box = $('.atl-recipes-box', root);
    if (box) {
      const cnt = $('.atl-rcount', box);
      if (cnt) cnt.textContent = data.recipes.length ? `　${data.recipes.length} 本` : '　未作成';
      renderRecipes();
    }
    for (const p of panes.values()) p.applyData();
  }

  // ---- 保存した指示（全カメラ共通の棚）----------------------------------------

  let recipeSig = '';
  function renderRecipes() {
    const host = $('.atl-recipes-body', root);
    if (!host) return;
    const sig = data.recipes.map((r) => r.id).join('|') + '#' + data.recipes.length;
    if (sig === recipeSig && host.childElementCount) {
      // 使用回数だけは更新する（本文の編集中に作り直さない）。
      data.recipes.forEach((r) => {
        const s = host.querySelector(`.atl-rcard[data-rec="${CSS.escape(r.id)}"] .atl-rstat`);
        if (s) s.textContent = r.usedCount ? `${r.keptCount}/${r.usedCount} 本番で使用` : '未使用';
      });
      return;
    }
    recipeSig = sig;
    host.innerHTML = `
      <p class="atl-lead">よく使う指示を置いておく棚です。選ぶと本文が ③ の欄にそのまま入るので、
        その場で書き換えて 📋 で持っていきます。<b>穴埋めのスロットは持ちません</b> — 構図・画角・照明を
        保つ骨格は 🪄 生成のときサーバが自動で足すので、ここに残るのは作者が書く一行だからです。</p>
      ${data.recipes.length ? '' : `<div class="atl-seed-box">
        <p>まだ 1 つもありません。固定カメラ i2v 用の定番 3 本を入れて始められます。</p>
        <button class="atl-starter accent">＋ 定番を入れる</button></div>`}
      <div class="atl-recipes">
        ${data.recipes.map((r) => `
        <article class="atl-rcard" data-rec="${esc(r.id)}">
          <div class="atl-rhead">
            <input class="atl-rname" type="text" value="${esc(r.name || '')}" placeholder="名前">
            <input class="atl-rslug" type="text" value="${esc(r.slug || '')}" placeholder="slug"
              title="生成物のファイル名に入る ASCII の短縮名（例 standing）">
            <span class="atl-rstat">${r.usedCount ? `${r.keptCount}/${r.usedCount} 本番で使用` : '未使用'}</span>
            <button class="atl-rdel" title="これを削除">🗑</button>
          </div>
          <input class="atl-rintent" type="text" value="${esc(r.intent || '')}" placeholder="狙い（いつ使うか）">
          <textarea class="atl-rbody" rows="6" spellcheck="false">${esc(r.body || '')}</textarea>
        </article>`).join('')}
      </div>
      <div class="atl-actions">
        <button class="atl-rnew">＋ 空の指示</button>
        <button class="atl-spine">📋 共通の骨格だけコピー</button>
      </div>`;

    const starter = $('.atl-starter', host);
    if (starter) starter.onclick = async () => {
      starter.disabled = true;
      for (const r of STARTER_RECIPES) await api('/atelier/recipe', r);
    };
    $('.atl-rnew', host).onclick = () => api('/atelier/recipe', { name: '新しい指示', kind: 'video', body: SPINE });
    const sp = $('.atl-spine', host);
    sp.onclick = async () => {
      try { await navigator.clipboard.writeText(SPINE); } catch { /* 権限なし */ }
      flash(sp, 'コピーしました');
    };

    host.querySelectorAll('.atl-rcard').forEach((card) => {
      const id = card.dataset.rec;
      const save = debounce(async () => {
        await api('/atelier/recipe', {
          id, name: $('.atl-rname', card).value, intent: $('.atl-rintent', card).value,
          slug: $('.atl-rslug', card).value.replace(/[^A-Za-z0-9]/g, '').slice(0, 16),
          body: $('.atl-rbody', card).value,
        });
        for (const p of panes.values()) p.refreshRecipeOptions();
      }, 600);
      ['.atl-rname', '.atl-rslug', '.atl-rintent', '.atl-rbody'].forEach((s) => { $(s, card).oninput = save; });
      $('.atl-rdel', card).onclick = () => {
        if (!confirm('この指示を消します（過去の生成レコードは残ります）。')) return;
        api('/atelier/recipe/delete', { id });
      };
    });
  }

  // ---- カメラ列 -------------------------------------------------------------

  function createPane(cam) {
    // 書きかけのプロンプトは端末に残す。📋 でコピーして外部ツールへ行き、できたファイルを持って
    //   戻るまでの間にリロードが挟まっても、記録が本文ごと繋がるようにするため。
    const draftKey = `atl.prompt.${cam.id}`;
    const loadDraft = () => { try { return localStorage.getItem(draftKey) || ''; } catch { return ''; } };
    const saveDraft = (v) => { try { localStorage.setItem(draftKey, v || ''); } catch { /* private mode */ } };

    const st = {
      seedUrl: '', bg: 'seed',
      pickUrl: '', pickGenId: '',
      maskMode: 'full', thresh: 0.12, strength: 1, graded: true,
      recipeId: '', bind: {}, promptOverride: loadDraft(),
      params: { tool: 'dreamina', model: 'seedance 4.0 pro', aspect: '4:3', sec: 4, seed: '' },
      parentId: '',
    };

    const el = document.createElement('section');
    // 演出専用カメラ（role:"fx"）は既定で畳む。ゾーンを持たず種フレームも来ないので、
    //   他と同じ重さで並べると 4 台目で列が折り返し、構図を並べて見比べるという芯が壊れる。
    el.className = 'atl-cam' + (cam.role === 'fx' ? ' folded' : '');
    el.dataset.role = cam.role;
    el.innerHTML = `
      <header class="atl-camhead">
        <b class="atl-camname">カメラ ${esc(cam.id)}</b>
        ${cam.role === 'fx' ? '<span class="atl-camrole">演出専用</span>' : ''}
        <span class="atl-camstat"></span>
        <span class="spacer"></span>
        <span class="atl-camcount"></span>
        ${cam.role === 'fx' ? '<button class="atl-camfold" title="この列を開く / 畳む">開く</button>' : ''}
      </header>

      <div class="atl-sec atl-stage">
        <div class="atl-h">① 試写<span class="atl-sub">Quest と同じ合成式で「載せた見え」を出す</span></div>
        <div class="view-wrap"><canvas class="atl-view" width="640" height="360"></canvas></div>
        <div class="atl-stagebar">
          <span class="atl-seg" role="group" aria-label="背景">
            <button data-bg="seed" class="on" title="選んだ種フレームを背景にする（カメラ未接続でも見える）">背景: 種</button>
            <button data-bg="live" title="生ライブ映像を背景にする（監視列が接続している時だけ）">ライブ</button>
          </span>
          <span class="atl-seg" role="group" aria-label="マスク">
            <button data-mask="full" class="on" title="素材で画面全部を差し替える（実際の演出の最も単純な形）">全面</button>
            <button data-mask="motion" title="種フレームとの差分＝動いているところだけ差し替える">動くところだけ</button>
          </span>
          <button class="atl-remask" title="差分の蓄積を捨てて取り直す" style="display:none">↺</button>
        </div>
        <div class="atl-stagebar">
          <span class="atl-seg" role="group" aria-label="画質">
            <button data-post="on" class="on" title="このカメラの画像加工を掛けた、体験者に見えるとおりの絵">画質: 本番</button>
            <button data-post="off" title="加工を外して素材そのものを見る（暗いグレーディングで判断できない時に）">素の色</button>
          </span>
        </div>
        <div class="atl-stagebar atl-motionbar" style="display:none">
          <label class="sld">差の閾値 <input class="atl-thresh" type="range" min="0.02" max="0.4" step="0.01" value="0.12"><span class="atl-threshv">0.12</span></label>
        </div>
        <div class="atl-stagebar">
          <label class="sld">強度 <input class="atl-strength" type="range" min="0" max="1" step="0.01" value="1"><span class="atl-strengthv">1.00</span></label>
          <span class="spacer"></span>
          <button class="atl-tocue accent" title="この素材とマスクで cue を作る（タイムラインの演出で選べるようになる）" disabled>💾 カットの素材にする</button>
        </div>
        <div class="atl-pick"></div>
        <div class="atl-savemsg"></div>
      </div>

      <div class="atl-sec atl-seeds">
        <div class="atl-h">② 種フレーム<span class="atl-sub">i2v の入力 1 枚。ここから作った素材はこの構図専用</span></div>
        <div class="atl-seedbar">
          <button class="atl-shoot" title="いまの生ライブから 1 枚切り出して recordings/ に保存する">📷 いま撮る</button>
          <span class="atl-shootmsg"></span>
        </div>
        <div class="atl-strip"></div>
        <!-- 種フレームを入力にして、この PC の Codex に 1 枚描かせる（60〜150 秒）。
             構図・照明を保ったまま「足すものだけ」を足させるので、差分がそのままマスクになる。 -->
        <div class="atl-genbox" data-group="material">
          <div class="atl-genrow">
            <button class="atl-gen accent" title="選んでいる種フレームを入力にして Codex に 1 枚描かせる">🪄 Codex に生成させる</button>
            <span class="atl-genmsg"></span>
          </div>
          <textarea class="atl-genprompt" rows="3"
            placeholder="この構図に何を足すか（例: 画面奥のカーテンの隙間から、白い着物の人影が半分だけ覗いている。血は入れない）"></textarea>
          <div class="atl-genhint">構図・画角・照明を保つ指示はサーバが自動で足します。
            書くのは<b>足すものだけ</b>。できた画像は下の棚（未記録の素材）に出ます。</div>
        </div>
      </div>

      <div class="atl-sec atl-brief">
        <div class="atl-h">③ 指示<span class="atl-sub">保存した指示を選ぶか、直接書く</span></div>
        <select class="atl-recipe"></select>
        <div class="atl-intentbox"></div>
        <div class="atl-h2">合成プロンプト<span class="atl-warn"></span></div>
        <textarea class="atl-prompt" rows="6" spellcheck="false"></textarea>
        <div class="atl-params">
          <label>道具<input type="text" data-p="tool" value="${esc(st.params.tool)}"></label>
          <label>モデル<input type="text" data-p="model" value="${esc(st.params.model)}"></label>
          <label>比<input type="text" data-p="aspect" value="${esc(st.params.aspect)}"></label>
          <label>秒<input type="number" data-p="sec" min="1" max="15" value="${esc(st.params.sec)}"></label>
          <label>seed<input type="text" data-p="seed" value="" placeholder="任意"></label>
        </div>
        <p class="atl-toolhint">動画を作る道具は検閲の強さが違う。<b>Veo / Flow</b> は horror・ghost・blood 等で弾かれる。
          <b>Kling</b> と<b>ローカル Wan</b> は寛容。弾かれたら言葉ではなく道具を替えるのが速い。</p>
        <div class="atl-actions">
          <button class="atl-copy accent">📋 コピーする</button>
        </div>
        <p class="atl-note">コピーと同時に<b>送信待ちのレコード</b>ができます。生成したら棚のカードへ動画を落としてください。</p>
      </div>

      <div class="atl-sec atl-shelfsec">
        <div class="atl-h">④ 生成物<span class="atl-sub">クリックで試写に載る。ここへ動画を落とすと素材だけ先に入る</span></div>
        <div class="atl-shelf"></div>
        <details class="atl-unfiled"><summary>📁 まだ記録していないファイル（撮影・素材・動作確認用）<span class="atl-uncount"></span></summary>
          <div class="atl-unfiled-body"></div>
        </details>
      </div>`;

    const q = (s) => el.querySelector(s);

    // 演出専用カメラの列の開閉。開けば普通の列と同じに振る舞う（試写も差分マスクも動く）。
    const foldBtn = q('.atl-camfold');
    if (foldBtn) {
      const syncFold = () => { foldBtn.textContent = el.classList.contains('folded') ? '開く' : '畳む'; };
      const toggle = () => { el.classList.toggle('folded'); syncFold(); };
      foldBtn.onclick = (e) => { e.stopPropagation(); toggle(); };
      q('.atl-camhead').onclick = () => { if (el.classList.contains('folded')) toggle(); };
      syncFold();
    }
    const msg = (m, cls = '') => { const e = q('.atl-shootmsg'); e.textContent = m; e.className = 'atl-shootmsg ' + cls; };

    // ===== 種フレーム =====
    let seedImg = null;
    function selectSeed(url) {
      st.seedUrl = url || '';
      seedImg = null;
      resetMotion();
      if (st.seedUrl) {
        const im = new Image();
        im.crossOrigin = 'anonymous';
        im.onload = () => applyViewAspect();   // 実寸が入った時点で試写の枠を合わせる
        im.src = st.seedUrl;
        seedImg = im;
      }
      renderSeeds();
      syncStage();
    }

    // 試写の枠を背景ソースの実寸比に合わせる（監視列の applyAspect と同じ流儀）。
    //   固定 4:3 だと 16:9 のカメラで左右に黒帯が出て、素材の見えを判断しにくい。
    let viewAspect = '';
    // 試写枠は **実機のスクリーン枠と同じ 16:9 固定**。ソースのアスペクトに追従させてはいけない。
    //   実機の枠（MjpegScreenStage の Quad）は 16:9 で、4:3 の映像は左右 12.5% ずつ黒帯になる。
    //   卓が枠をソースに合わせると、その黒帯が消え、マスクの端も枠の端に見えてしまう
    //   ＝「卓で合っていたのに実機でずれる」を卓の側から見えなくする（2026-07-30）。
    function applyViewAspect() {
      if (viewAspect === 'fixed') return;
      viewAspect = 'fixed';
      const cv = q('.atl-view');
      cv.style.aspectRatio = String(FRAME_ASPECT);
      cv.width = MW; cv.height = MH;
    }

    let seedSig = '';
    function renderSeeds() {
      const list = frames[cam.id] || [];
      const sig = list.map((f) => f.url).join('|') + '#' + st.seedUrl;
      if (sig === seedSig) return;
      seedSig = sig;
      const strip = q('.atl-strip');
      if (!list.length) {
        strip.innerHTML = `<p class="atl-empty">まだ 1 枚もありません。<b>📷 いま撮る</b>（監視列が接続している時）か、
          <code>recordings/</code> に <code>cam${esc(cam.id)}_*.jpg</code> を置いてください。</p>`;
        return;
      }
      strip.innerHTML = list.slice(0, 24).map((f) => `
        <button class="atl-seedbtn${f.url === st.seedUrl ? ' on' : ''}" data-url="${esc(f.url)}" title="${esc(f.name)}">
          <img src="${esc(f.url)}" alt="" loading="lazy"><span>${esc(f.name.replace(/\.[a-z]+$/i, '').slice(-13))}</span>
        </button>`).join('');
      strip.querySelectorAll('.atl-seedbtn').forEach((b) => {
        b.onclick = () => selectSeed(b.dataset.url);
        b.ondblclick = () => window.open(b.dataset.url, '_blank');
      });
    }

    q('.atl-shoot').onclick = async () => {
      const img = deps.getLiveImg && deps.getLiveImg(cam.id);
      if (!img || !img.naturalWidth) return msg('生映像がありません（下の監視列で接続してから）', 'err');
      const c = document.createElement('canvas');
      c.width = img.naturalWidth; c.height = img.naturalHeight;
      try { c.getContext('2d').drawImage(img, 0, 0); }
      catch { return msg('切り出せませんでした', 'err'); }
      msg('保存中…');
      const blob = await new Promise((r) => c.toBlob(r, 'image/jpeg', 0.95));
      if (!blob) return msg('切り出せませんでした', 'err');
      try {
        const r = await (await fetch(`/save?type=image&to=recordings&cam=${encodeURIComponent(cam.id)}`,
          { method: 'POST', body: blob })).json();
        if (!r.ok) return msg('保存失敗', 'err');
        notifyFrameSaved(cam.id, r);
        selectSeed(r.url);
        msg(`📷 ${r.name}`, 'ok');
      } catch { msg('保存失敗（サーバ断）', 'err'); }
    };

    // ===== 🪄 Codex に生成させる =====
    //   サーバ（/generate）が別スレッドで Codex CLI を回す。ここは投げて待つだけ。
    //   ⚠ 生成は**卓を動かしている PC からしか実行できない**（サーバが 127.0.0.1 に限っている）。
    const genMsg = (m, cls = '') => {
      const e = q('.atl-genmsg'); e.textContent = m; e.className = 'atl-genmsg ' + cls;
    };
    let genTimer = 0;
    q('.atl-gen').onclick = async () => {
      if (!st.seedUrl) return genMsg('先に種フレームを選んでください（📷 いま撮る）', 'err');
      const prompt = q('.atl-genprompt').value.trim();
      if (prompt.length < 8) return genMsg('何を足すのかを書いてください', 'err');
      const btn = q('.atl-gen');
      btn.disabled = true;
      genMsg('生成中… 0s（1〜2 分かかります）');
      try {
        const r = await (await fetch('/generate', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cam: cam.id, seedUrl: st.seedUrl, prompt, slug: 'codex' }),
        })).json();
        if (!r.ok) { btn.disabled = false; return genMsg(r.error || '生成を始められませんでした', 'err'); }
        clearInterval(genTimer);
        genTimer = setInterval(async () => {
          let s;
          try { s = await (await fetch(`/generate/status?id=${encodeURIComponent(r.id)}`)).json(); }
          catch { return; }                       // 一時的な断は次の周期で拾う
          if (!s.ok) return;
          if (s.status === 'running') return genMsg(`生成中… ${Math.round(s.elapsedSec)}s`);
          clearInterval(genTimer);
          btn.disabled = false;
          if (s.status === 'done') {
            genMsg('できました（棚に追加）', 'ok');
            // captures/ の一覧を取り直す → onCaptures 経由で「未記録の素材」の棚が更新される。
            await refreshCaptures();
            loadPick(s.url, '');
          } else {
            genMsg(s.error || '生成に失敗しました', 'err');
          }
        }, 2000);
      } catch {
        btn.disabled = false;
        genMsg('サーバに繋がりません', 'err');
      }
    };

    // ===== 試写（合成プレビュー）=====
    let ovEl = null, ovReady = false;

    function loadPick(url, genId) {
      if (ovEl && ovEl.tagName === 'VIDEO') { try { ovEl.pause(); ovEl.src = ''; } catch { /* noop */ } }
      ovEl = null; ovReady = false;
      st.pickUrl = url || ''; st.pickGenId = genId || '';
      resetMotion();
      if (st.pickUrl) {
        if (isVideoUrl(st.pickUrl)) {
          const v = document.createElement('video');
          v.muted = true; v.loop = true; v.playsInline = true; v.crossOrigin = 'anonymous';
          v.addEventListener('canplay', () => { ovReady = true; try { v.play().catch(() => {}); } catch { /* noop */ } });
          v.src = st.pickUrl;
          ovEl = v;
        } else {
          const im = new Image();
          im.crossOrigin = 'anonymous';
          im.onload = () => { ovReady = true; };
          im.src = st.pickUrl;
          ovEl = im;
        }
      }
      renderShelf(true);
      syncStage();
    }

    // ---- 差分マスク（種フレームと素材の差 = 「足されたもの」）------------------
    //   Quest 側は静的マスク PNG を使うので、瞬間差分ではなく **累積の最大値** を取る。
    //   再生が進むほど「その素材で動きうる領域」に収束し、実運用と同じ形になる。
    const mk = (w, h) => { const c = document.createElement('canvas'); c.width = w; c.height = h; return c; };
    const seedC = mk(DW, DH), ovC = mk(DW, DH), accC = mk(DW, DH), maskC = mk(MW, MH);
    const seedCtx = seedC.getContext('2d', { willReadFrequently: true });
    const ovCtx = ovC.getContext('2d', { willReadFrequently: true });
    const accCtx = accC.getContext('2d');
    const maskCtx = maskC.getContext('2d');
    let seedData = null, acc = null, accImg = null, motionDirty = false, motionPixels = 0;

    function resetMotion() {
      seedData = null; acc = null; accImg = null; motionPixels = 0;
      maskCtx.clearRect(0, 0, MW, MH);
      const b = q('.atl-tocue'); if (b) b.disabled = !st.pickUrl;
    }

    /**
     * 累積差分（ソース映像の座標 DW×DH）を **スクリーン枠空間**（MW×MH = 16:9）へ焼く。
     *
     * 差分は素材の画素座標で取れているので、実機で素材が置かれる矩形（シェーダの `_OverlayScale`
     * と同じ contain-fit）へそのまま収める。**枠いっぱいに引き伸ばしてはいけない** — マスクだけは
     * 実機で contain-fit を通らず生 uv で読まれるため、4:3 素材なら水平 1.33 倍にずれる。
     *
     * ぼかしは矩形の内側でクリップする。外へ滲ませると「マスクは白いが素材が無い」領域ができ、
     * live が隠されて黒が出る（`ContainUv` が範囲外の overlay を 0 にするため）。
     */
    function bakeMask() {
      const isVid = ovEl && ovEl.tagName === 'VIDEO';
      const ow = isVid ? (ovEl.videoWidth || 0) : (ovEl ? ovEl.naturalWidth : 0);
      const oh = isVid ? (ovEl.videoHeight || 0) : (ovEl ? ovEl.naturalHeight : 0);
      const sw = ow || (seedImg ? seedImg.naturalWidth : 0);
      const sh = oh || (seedImg ? seedImg.naturalHeight : 0);
      const [x, y, w, h] = containRect(sw, sh, MW, MH);
      maskCtx.clearRect(0, 0, MW, MH);
      maskCtx.save();
      maskCtx.beginPath();
      maskCtx.rect(x, y, w, h);
      maskCtx.clip();
      maskCtx.filter = 'blur(6px)';
      maskCtx.drawImage(accC, x, y, w, h);
      maskCtx.filter = 'none';
      maskCtx.restore();
    }

    function updateMotion() {
      if (st.maskMode !== 'motion') return null;
      if (!seedImg || !seedImg.naturalWidth || !ovEl || !ovReady) return null;
      if (!seedData) {
        seedCtx.drawImage(seedImg, 0, 0, DW, DH);
        seedData = seedCtx.getImageData(0, 0, DW, DH).data;
      }
      try { ovCtx.drawImage(ovEl, 0, 0, DW, DH); } catch { return null; }
      const cur = ovCtx.getImageData(0, 0, DW, DH).data;
      if (!acc) { acc = new Uint8Array(DW * DH); accImg = accCtx.createImageData(DW, DH); }
      const th = st.thresh * 255;
      let changed = false;
      for (let i = 0, p = 0; i < acc.length; i++, p += 4) {
        if (acc[i]) continue;
        const d = Math.max(Math.abs(cur[p] - seedData[p]),
          Math.abs(cur[p + 1] - seedData[p + 1]),
          Math.abs(cur[p + 2] - seedData[p + 2]));
        if (d > th) { acc[i] = 255; changed = true; motionPixels++; }
      }
      if (changed) {
        const d = accImg.data;
        for (let i = 0, p = 0; i < acc.length; i++, p += 4) {
          d[p] = d[p + 1] = d[p + 2] = acc[i]; d[p + 3] = 255;
        }
        accCtx.putImageData(accImg, 0, 0);
        bakeMask();
        motionDirty = true;
      }
      return motionPixels > 0 ? maskC : null;
    }

    const view = createCompositeView(q('.atl-view'), {
      sample: () => {
        const live = deps.getLiveImg && deps.getLiveImg(cam.id);
        const bgImg = st.bg === 'live' ? live : seedImg;
        const isVid = ovEl && ovEl.tagName === 'VIDEO';
        const maskEl = updateMotion();
        return {
          liveImg: bgImg && bgImg.naturalWidth ? bgImg : null,
          overlayEl: ovEl, overlayReady: ovReady,
          overlayW: isVid ? (ovEl.videoWidth || 0) : (ovEl ? ovEl.naturalWidth : 0),
          overlayH: isVid ? (ovEl.videoHeight || 0) : (ovEl ? ovEl.naturalHeight : 0),
          maskEl,
          overlayOn: !!st.pickUrl,
          loopPreview: true,            // 工房は繰り返し試写。1 周した瞬間に畳まれると使えない
          fadeSec: 0.3, strength: st.strength,
          feather: st.maskMode === 'motion' ? 0 : 0.3,  // 差分マスクは焼き込み済み（自前で blur）
          // 「本番」= カメラの画像加工込み（体験者に見えるとおり）。ホラー用の暗いグレーディングだと
          // 素材そのものが判別できないので、加工を外して素の色で見る道を用意する。
          post: st.graded ? ((deps.getCamPost && deps.getCamPost(cam.id)) || FX_DEFAULT) : FX_DEFAULT,
          trimEnd: 0, onVideoEnd: null,
        };
      },
    });

    el.querySelectorAll('.atl-stagebar [data-bg]').forEach((b) => {
      b.onclick = () => {
        st.bg = b.dataset.bg;
        el.querySelectorAll('[data-bg]').forEach((x) => x.classList.toggle('on', x === b));
        resetMotion();
        applyViewAspect();
      };
    });
    el.querySelectorAll('.atl-stagebar [data-mask]').forEach((b) => {
      b.onclick = () => {
        st.maskMode = b.dataset.mask;
        el.querySelectorAll('[data-mask]').forEach((x) => x.classList.toggle('on', x === b));
        q('.atl-motionbar').style.display = st.maskMode === 'motion' ? '' : 'none';
        q('.atl-remask').style.display = st.maskMode === 'motion' ? '' : 'none';
        resetMotion();
        syncStage();
      };
    });
    el.querySelectorAll('.atl-stagebar [data-post]').forEach((b) => {
      b.onclick = () => {
        st.graded = b.dataset.post === 'on';
        el.querySelectorAll('[data-post]').forEach((x) => x.classList.toggle('on', x === b));
      };
    });
    q('.atl-remask').onclick = () => resetMotion();
    const thI = q('.atl-thresh'), thV = q('.atl-threshv');
    thI.oninput = () => { st.thresh = parseFloat(thI.value); thV.textContent = st.thresh.toFixed(2); resetMotion(); };
    const strI = q('.atl-strength'), strV = q('.atl-strengthv');
    strI.oninput = () => { st.strength = parseFloat(strI.value); strV.textContent = st.strength.toFixed(2); };

    // ---- 試写の内容を cue にする（工房 → タイムラインの唯一の橋）---------------
    q('.atl-tocue').onclick = async () => {
      if (!st.pickUrl) return stageMsg('先に生成物を選んでください', 'err');
      const btn = q('.atl-tocue');
      btn.disabled = true;
      stageMsg('保存中…');
      try {
        const id = nextCueId();
        let maskUrl = '';
        if (st.maskMode === 'motion' && motionPixels > 0) {
          const blob = await new Promise((r) => maskC.toBlob(r, 'image/png'));
          const res = await (await fetch(`/masks?name=${encodeURIComponent(id)}`, { method: 'POST', body: blob })).json();
          if (!res.ok) throw new Error(res.error || 'マスク保存失敗');
          maskUrl = res.url;
        }
        const cue = {
          id, name: `カメラ ${cam.id} ${fileOf(st.pickUrl)}`, camera: cam.id,
          sourceUrl: encPath(st.pickUrl), maskUrl: encPath(maskUrl),
          strength: st.strength, loop: false, fadeIn: 0.5, fadeOut: 0.5,
          trimStart: 0, trimEnd: 0,
        };
        // 色統計マッチングを 6 float に焼く（cue エディタと同じ経路）。
        //   これを忘れると、工房のプレビューは色を合わせた絵を出すのに実機は素の色で出る
        //   ＝「卓で見て良かったから採用した」という判断そのものが嘘になる（2026-07-30 に是正）。
        //   基準の実写はライブ優先・無ければ種フレーム（工房はカメラ未接続の自宅作業を前提にしている）。
        const refEl = (st.bg === 'live' && deps.getLiveImg && deps.getLiveImg(cam.id)) || seedImg;
        Object.assign(cue, await bakeColorMatch(refEl, ovEl, blendCfg));
        const r = deps.saveCue ? await deps.saveCue(cue) : { ok: false };
        if (!r || r.ok === false) throw new Error('show.json 保存失敗');
        deps.onCueSaved && deps.onCueSaved(cue);
        stageMsg(`✓ ${id} を作成（${maskUrl ? '差分マスク付き' : '全面差し替え'}）— タイムラインの演出で選べます`, 'ok');
      } catch (e) {
        stageMsg('保存失敗: ' + e.message, 'err');
      }
      btn.disabled = !st.pickUrl;
    };

    function nextCueId() {
      const used = new Set(((deps.getAllCues && deps.getAllCues()) || []).map((c) => c.id));
      const base = `cue_${cam.id}_`;
      let n = 1; while (used.has(base + n)) n++;
      return base + n;
    }

    // 保存の結果は専用行に出す。`.atl-pick` に出すと、cue 保存 → show.json rev 更新 →
    // 再描画（syncStage）で消えてしまい「押したのに何も起きていない」ように見える。
    function stageMsg(m, cls = '') {
      const e = q('.atl-savemsg');
      e.textContent = m;
      e.className = 'atl-savemsg ' + cls;
    }

    function syncStage() {
      const live = deps.getLiveImg && deps.getLiveImg(cam.id);
      const hasLive = !!(live && live.naturalWidth);
      applyViewAspect();
      const bLive = el.querySelector('[data-bg="live"]');
      bLive.disabled = !hasLive;
      bLive.title = hasLive ? '生ライブ映像を背景にする' : '監視列がこのカメラに接続していません';
      if (!hasLive && st.bg === 'live') { st.bg = 'seed'; el.querySelectorAll('[data-bg]').forEach((x) => x.classList.toggle('on', x.dataset.bg === 'seed')); }
      q('.atl-camstat').textContent = hasLive ? '● LIVE' : '';
      q('.atl-camstat').className = 'atl-camstat' + (hasLive ? ' ok' : '');
      q('.atl-tocue').disabled = !st.pickUrl;
      // 📷 は接続が無くても押せるままにする（disabled にすると理由が出ないので、押して理由を出す）。
      const shoot = q('.atl-shoot');
      shoot.classList.toggle('dim', !hasLive);
      shoot.title = hasLive ? 'いまの生ライブから 1 枚切り出して recordings/ に保存する'
        : 'このカメラの生映像が来ていません（下の監視列で接続してから押してください）';

      // syncStage は 2 秒ごとにも走る。内容が同じなら DOM を作り直さない
      // （作り直すと ✕ はずす のクリックが取りこぼされることがある）。
      const pick = q('.atl-pick');
      const sig = `${st.pickUrl}|${st.seedUrl ? 1 : 0}`;
      if (pick.dataset.sig !== sig) {
        pick.dataset.sig = sig;
        if (!st.pickUrl) {
          pick.innerHTML = st.seedUrl
            ? '<span class="atl-stagemsg">下の生成物をクリックすると、この構図に重なります</span>'
            : '<span class="atl-stagemsg">まず種フレームを 1 枚選んでください</span>';
        } else {
          pick.innerHTML = `<span class="atl-stagemsg">重ねている素材: <code>${esc(fileOf(st.pickUrl))}</code></span>
            <button class="atl-unpick" title="重ねるのをやめる">✕ はずす</button>`;
          $('.atl-unpick', pick).onclick = () => loadPick('', '');
        }
      }
    }

    // ===== 指示（保存した指示 + プロンプト本文）=====
    const recipeSel = q('.atl-recipe'), promptTa = q('.atl-prompt');

    function refreshRecipeOptions() {
      const cur = st.recipeId;
      recipeSel.innerHTML = '<option value="">— 保存した指示から選ぶ —</option>'
        + data.recipes.map((x) => `<option value="${esc(x.id)}"${x.id === cur ? ' selected' : ''}>${esc(x.name)}${x.usedCount ? `（${x.keptCount}/${x.usedCount} 使用）` : ''}</option>`).join('');
      recipeSel.value = cur;
      renderSlots();
    }

    function renderSlots() {
      const r = recipeById(st.recipeId);
      q('.atl-intentbox').innerHTML = r && r.intent ? `<p class="atl-intent">${esc(r.intent)}</p>` : '';
      promptTa.value = compose(st);
      syncWarn();
    }

    function syncWarn() {
      const left = leftoverSlots(promptTa.value);
      const w = q('.atl-warn');
      w.textContent = left.length ? `${left.map((s) => `{{${s}}}`).join(' / ')} が残っています — 実際の言葉に置き換えてください` : '';
    }

    recipeSel.onchange = () => { st.recipeId = recipeSel.value; st.promptOverride = ''; saveDraft(''); renderSlots(); };
    promptTa.oninput = () => { st.promptOverride = promptTa.value; saveDraft(promptTa.value); syncWarn(); };
    el.querySelectorAll('.atl-params input').forEach((inp) => {
      inp.oninput = () => { st.params[inp.dataset.p] = inp.type === 'number' ? +inp.value : inp.value; };
    });

    const copyBtn = q('.atl-copy');
    copyBtn.onclick = async () => {
      const prompt = promptTa.value || compose(st);
      if (!prompt.trim()) return msg('先にプロンプトを選ぶか書いてください', 'err');
      copyBtn.disabled = true;
      try { await navigator.clipboard.writeText(prompt); } catch { /* 権限なし */ }
      copyBtn.disabled = false;
      // **ここでレコードは作らない**（2026-07-30）。先に「送信待ち」を起こす方式は、外部ツールへ
      //   行ったきり戻らない下書きが溜まるだけだった（作られた 1 件は 4 日後もそのまま）。
      //   記録は「戻ってきたファイルを棚へ落とした時」に、この本文ごと 1 回で作る。
      //   本文は端末に覚えるので、途中でリロードしても繋がる。
      flash(copyBtn, '📋 コピー — できたファイルを下の棚へ落とすと記録されます');
    };

    // ===== 棚（生成物 + 未記録素材）=====
    let shelfSig = '';
    function myGens() {
      return data.generations
        .filter((g) => g.cameraLabel === cam.id)
        .sort((a, b) => String(b.createdAt || '').localeCompare(String(a.createdAt || '')));
    }

    /** この構図の「台帳に無いまま本番で使われている素材」（サーバが show.json から導出して寄越す）。 */
    function myUnlogged() {
      const mine = new Set((((deps.getAllCues && deps.getAllCues()) || [])).filter((c) => c.camera === cam.id)
        .map((c) => decodeURI(String(c.sourceUrl || ''))));
      return (data.unlogged || []).filter((u) => mine.has(decodeURI(String(u.sourceUrl || ''))));
    }

    function renderShelf(force) {
      const gens = myGens();
      const unlogged = myUnlogged();
      const sig = gens.map((g) => `${g.id}:${(g.usedByCues || []).map((c) => c.id).join(',')}:${g.outputUrl || ''}`).join('|')
        + '#' + unlogged.map((u) => u.sourceUrl).join('|')
        + '#' + st.pickGenId + '#' + st.pickUrl;
      if (!force && sig === shelfSig) return;
      shelfSig = sig;
      const host = q('.atl-shelf');
      host.innerHTML = (unlogged.map(unloggedCard).join('')
        + (gens.length ? gens.map(genCard).join('')
          : '<p class="atl-empty">まだ生成がありません。上で指示を書いて 📋 を押すか、ここへ動画を落としてください。</p>'));
      host.querySelectorAll('.atl-card').forEach((card) => wireCard(card));
      // 「採用したか」は show.json が持っている（cues[].sourceUrl）。台帳の札ではなく導出値を出す
      //   — 手で押す 3 択にしていた頃は、押し忘れた瞬間に「素材なし」と嘘をついていた（2026-07-30）。
      const used = gens.filter((g) => (g.usedByCues || []).length).length;
      const parts = [];
      if (gens.length) parts.push(`素材 ${gens.length}（本番で使用 ${used}）`);
      if (unlogged.length) parts.push(`⚠ 台帳外 ${unlogged.length}`);
      q('.atl-camcount').textContent = parts.join(' / ') || '素材なし';
      renderUnfiled();
    }

    /**
     * 台帳に記録が無いのに cue が使っている素材のカード。
     * 外部ツールで作って直接 cue にした分・過去分がここに出る。**黙って隠さない** —
     * 隠していたから「工房は素材なしと言うのに本番では出ている」という状態が半年続いた。
     */
    function unloggedCard(u) {
      const url = u.sourceUrl;
      const on = url === st.pickUrl;
      const ids = (u.cues || []).map((c) => c.id).join(', ');
      const thumb = isVideoUrl(url)
        ? `<video src="${esc(url)}#t=0.1" muted playsinline preload="metadata"></video><span class="atl-play">▶ 試写へ</span>`
        : `<img src="${esc(url)}" alt=""><span class="atl-play">試写へ</span>`;
      return `
      <article class="atl-card atl-card-unlogged${on ? ' picked' : ''}" data-unlogged="${esc(url)}">
        <div class="atl-out clickable" data-pick="${esc(url)}">${thumb}</div>
        <div class="atl-meta">
          <div class="atl-cardhead">
            <strong>${esc(fileOf(url))}</strong>
            <span class="atl-status atl-used">✅ ${esc(ids)}</span>
          </div>
          <div class="atl-unlogged-note">本番で使われていますが、作り方の記録がありません。</div>
          <div class="atl-cardfoot">
            <button class="atl-adopt" title="この素材の生成レコードを作り、プロンプトを書き足す">✎ 作り方を書き足す</button>
          </div>
        </div>
      </article>`;
    }

    function genCard(g) {
      const bind = Object.entries(g.bind || {}).filter(([, v]) => String(v || '').trim())
        .map(([k, v]) => `${k}=${v}`).join(' / ');
      const p = g.params || {};
      const cond = [p.tool, p.model, p.aspect, p.sec && `${p.sec}s`, p.seed && `seed ${p.seed}`]
        .filter(Boolean).join(' · ');
      const on = g.outputUrl && g.outputUrl === st.pickUrl;
      const thumb = g.outputUrl
        ? (isVideoUrl(g.outputUrl)
          ? `<video src="${esc(g.outputUrl)}#t=0.1" muted playsinline preload="metadata"></video><span class="atl-play">▶ 試写へ</span>`
          : `<img src="${esc(g.outputUrl)}" alt=""><span class="atl-play">試写へ</span>`)
        : `<div class="atl-drop" data-drop="${esc(g.id)}"><b>⬇ 動画を<br>ここへ</b><span>ドラッグ / クリック</span></div>`;
      // 「採用」は show.json 由来の導出値。押すボタンではないので、状態としてだけ出す。
      const hits = g.usedByCues || [];
      const useMark = hits.length
        ? `<span class="atl-status atl-used" title="この素材を使っている演出素材">✅ ${esc(hits.map((c) => c.id).join(', '))}</span>`
        : `<span class="atl-status">${g.outputUrl ? '未使用' : '送信待ち'}</span>`;
      return `
      <article class="atl-card${hits.length ? ' v-used' : ''}${on ? ' picked' : ''}" data-gen="${esc(g.id)}">
        <div class="atl-out${g.outputUrl ? ' clickable' : ''}" ${g.outputUrl ? `data-pick="${esc(g.outputUrl)}"` : ''}>${thumb}</div>
        <div class="atl-meta">
          <div class="atl-cardhead">
            <strong>${esc(g.recipeName || '(指示の記録なし)')}</strong>
            ${useMark}
          </div>
          ${bind ? `<div class="atl-bind">${esc(bind)}</div>` : ''}
          ${cond ? `<div class="atl-cond">${esc(cond)}</div>` : ''}
          ${g.sourceFrame ? `<div class="atl-src">種 <code>${esc(fileOf(g.sourceFrame))}</code></div>` : ''}
          <input class="atl-memo" type="text" value="${esc(g.note || '')}" placeholder="メモ（何が良かった / 悪かった）">
          <details class="atl-promptbox"><summary>プロンプト全文</summary><pre>${esc(g.prompt || '')}</pre></details>
          <div class="atl-cardfoot">
            <button class="atl-again" title="この設定を ③ 指示へ戻して作り直す">↻ 作り直す</button>
            <button class="atl-copy1" title="プロンプトをコピー">📋</button>
            <button class="atl-del" title="このレコードを削除">🗑</button>
          </div>
        </div>
      </article>`;
    }

    function wireCard(card) {
      // 台帳外カード（記録の無い素材）は「試写へ」と「作り方を書き足す」だけ。
      if (card.dataset.unlogged) {
        const u = card.dataset.unlogged;
        const outEl = $('.atl-out', card);
        if (outEl) outEl.onclick = () => loadPick(u, '');
        const adopt = $('.atl-adopt', card);
        if (adopt) {
          adopt.onclick = async () => {
            adopt.disabled = true;
            // 出力だけ埋めた空のレコードを起こす。プロンプトは ③ 指示に書いて 📋 で結びつける。
            await api('/atelier/gen', {
              camera: cam.index, cameraLabel: cam.id, outputUrl: u,
              sourceFrame: st.seedUrl || '', status: 'done', prompt: '',
              note: '（本番で使用中。作り方をここに書き足す）',
            });
            loadPick(u, '');
          };
        }
        return;
      }

      const id = card.dataset.gen;
      const rec = data.generations.find((g) => g.id === id);

      const out = $('.atl-out', card);
      if (out && out.dataset.pick) out.onclick = () => loadPick(out.dataset.pick, id);

      const memo = $('.atl-memo', card);
      if (memo) {
        let t = 0;
        memo.oninput = () => {
          clearTimeout(t);
          // メモは棚の署名に含めない（入力中に作り直すと値が飛ぶ）。
          t = setTimeout(() => fetch('/atelier/gen', { method: 'POST', body: JSON.stringify({ id, note: memo.value }) })
            .then((r) => r.json()).then((j) => { if (j && j.state) data = j.state; })
            .catch(() => {}), 500);
        };
      }

      const del = $('.atl-del', card);
      if (del) del.onclick = () => {
        if (!confirm('この生成レコードを消します（動画ファイル自体は captures/ に残ります）。')) return;
        if (rec && rec.outputUrl === st.pickUrl) loadPick('', '');
        api('/atelier/gen/delete', { id });
      };

      const copy1 = $('.atl-copy1', card);
      if (copy1) copy1.onclick = async () => {
        try { await navigator.clipboard.writeText(rec ? rec.prompt || '' : ''); } catch { /* noop */ }
        flash(copy1, '✓');
      };

      const again = $('.atl-again', card);
      if (again) again.onclick = () => {
        if (!rec) return;
        if (rec.sourceFrame) selectSeed(rec.sourceFrame);
        st.recipeId = rec.recipeId || '';
        st.bind = { ...(rec.bind || {}) };
        st.params = { ...(rec.params || st.params), seed: '' };
        st.promptOverride = rec.prompt || '';
        st.parentId = rec.id;
        refreshRecipeOptions();
        promptTa.value = st.promptOverride;
        el.querySelectorAll('.atl-params input').forEach((inp) => { inp.value = st.params[inp.dataset.p] ?? ''; });
        q('.atl-brief').scrollIntoView({ behavior: 'smooth', block: 'center' });
      };

      const drop = $('.atl-drop', card);
      if (drop) wireDrop(drop, () => id);
    }

    // 棚そのものへのドロップ = レコードを作ってから取り込む（「先に生成してから記録する」順序）。
    wireDrop(q('.atl-shelfsec'), async () => {
      const r = recipeById(st.recipeId);
      const res = await api('/atelier/gen', {
        camera: cam.index, cameraLabel: cam.id, sourceFrame: st.seedUrl,
        recipeId: st.recipeId, recipeName: r ? r.name : '', recipeSlug: r ? (r.slug || '') : '',
        bind: { ...st.bind }, params: { ...st.params }, prompt: promptTa.value || compose(st),
        status: 'draft',
      });
      return res && res.id;
    }, true);

    function wireDrop(el2, idFn, dropOnly) {
      const send = async (file) => {
        if (!file) return;
        const id = await idFn();
        if (!id) return;
        const ext = (file.name.split('.').pop() || 'mp4').toLowerCase();
        try {
          const r = await fetch(`/atelier/attach?id=${encodeURIComponent(id)}&ext=${encodeURIComponent(ext)}`,
            { method: 'POST', body: file });
          const j = await r.json();
          if (j && j.state) { data = j.state; applyData(); }
          if (j && j.url) loadPick(j.url, id);
          refreshCaptures();
        } catch { /* サーバ断 */ }
      };
      if (!dropOnly) {
        el2.onclick = () => {
          const inp = document.createElement('input');
          inp.type = 'file'; inp.accept = 'video/*,image/*';
          inp.onchange = () => send(inp.files[0]);
          inp.click();
        };
      }
      el2.addEventListener('dragover', (e) => { e.preventDefault(); el2.classList.add('over'); });
      el2.addEventListener('dragleave', () => el2.classList.remove('over'));
      el2.addEventListener('drop', (e) => {
        e.preventDefault(); e.stopPropagation();
        el2.classList.remove('over');
        send(e.dataTransfer.files && e.dataTransfer.files[0]);
      });
    }

    // 未記録素材（captures/ にあるが生成レコードに紐づいていないファイル）
    let unfiledSig = '';
    /**
     * まだ生成レコードに紐づいていないファイルの一覧。
     *
     * ⚠ 中身は `captures/` だけではない（`/captures/list` は撮影・素材・動作確認用ダミーの合併）。
     * 名乗りと中身が食い違っていたので 2026-07-30 に文言を直した。
     * **カメラで絞り込みはしない** — 命名規約が揃っておらず（`camB_*` / `gen_handB_*` が混在）、
     * 絞ると本物が消える。代わりにこの構図に関係しそうなものを先頭へ並べる。
     */
    function renderUnfiled() {
      const known = new Set(data.generations.map((g) => g.outputUrl).filter(Boolean));
      const all = (captures.items || []).filter((it) => !known.has(it.url));
      const mine = (it) => new RegExp(`(^|[^A-Za-z])cam${cam.id}[_.]|${cam.id}[_.-]?\\d`, 'i').test(it.name);
      const items = [...all.filter(mine), ...all.filter((it) => !mine(it))];
      const nMine = all.filter(mine).length;
      const sig = items.map((i) => i.url).join('|') + '#' + st.pickUrl;
      if (sig === unfiledSig) return;
      unfiledSig = sig;
      q('.atl-uncount').textContent = items.length ? `　${items.length}` : '　0';
      const host = q('.atl-unfiled-body');
      host.innerHTML = items.length ? `<div class="atl-unlist">${items.map((it, i) => `
        <button class="atl-unitem${it.url === st.pickUrl ? ' on' : ''}${i < nMine ? ' mine' : ''}" data-url="${esc(it.url)}" title="${esc(it.name)}${it.kind === 'test' ? '（動作確認用）' : ''}">
          <span class="atl-unkind">${it.type === 'video' ? '🎞' : '🖼'}</span>
          <span class="atl-unname">${esc(it.name)}</span>
        </button>`).join('')}</div>
        <p class="atl-note">名前にこの構図（${esc(cam.id)}）が入っているものを先に並べています。
          クリックで試写に載り、良ければ <b>💾 カットの素材にする</b> で cue になります。</p>`
        : '<p class="atl-empty">まだ記録していないファイルはありません。</p>';
      host.querySelectorAll('.atl-unitem').forEach((b) => { b.onclick = () => loadPick(b.dataset.url, ''); });
    }

    // ---- 初期化 ---------------------------------------------------------------
    function applyDataPane() {
      refreshRecipeOptions();
      renderSeeds();
      renderShelf();
      syncStage();
      // 種が未選択なら最新の 1 枚を自動で選ぶ（開いた瞬間から試写が成立する）。
      const list = frames[cam.id] || [];
      if (!st.seedUrl && list.length) selectSeed(list[0].url);
    }

    const unsubCaptures = onCaptures(() => renderUnfiled());
    const liveTimer = setInterval(syncStage, 2000);   // ライブ接続の入切を拾う

    return {
      el, st, selectSeed, renderSeeds, refreshRecipeOptions,
      applyData: applyDataPane,
      destroy() {
        clearInterval(liveTimer);
        unsubCaptures();
        view.destroy();
        if (ovEl && ovEl.tagName === 'VIDEO') { try { ovEl.pause(); ovEl.src = ''; } catch { /* noop */ } }
      },
    };
  }

  // ---- 小物 -----------------------------------------------------------------

  function debounce(fn, ms) {
    let t = 0;
    return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
  }

  function flash(btn, m) {
    const old = btn.dataset.old || btn.textContent;
    btn.dataset.old = old;
    btn.textContent = m;
    setTimeout(() => { btn.textContent = btn.dataset.old || old; }, 1200);
  }

  refresh();
  refreshCaptures();
  return { refresh, render, notifyFrameSaved };
}
