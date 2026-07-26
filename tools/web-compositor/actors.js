// 🎭 CG 人形（show.json トップレベル actors[]）の編集パネル。
//
// 人形は「演出のカットに重ねる 3D」。実体は Unity 側 Resources 配下のプレハブで、
// ここで決めるのは **どのプレハブを / どの背丈で / どこに立たせるか** だけ。
//   - cgMode="follow"（既定）… 体験者の位置・向きに立つ（分身）
//   - cgMode="fixed"        … 下の固定位置・向きに立つ
// 腕は体験者のハンドトラッキングで動く（プレハブに ShowActorRig が付いていれば自動）。
//
// プレハブの作り方: Unity の Tools/FixedCamVr/Setup/Build Show Actor Prefab に
// humanoid の FBX（Mixamo 等）を渡すと Resources/ShowActors/<名前>.prefab が出来る。
// 設計の正本: .claude/plans/2026-07-27_cg-actor-hand-tracking.md

const DEFAULT_ACTOR = {
  id: 'doll', name: '人形', prefab: 'ShowActors/Remy',
  heightM: 1.6, fixedX: 0, fixedZ: 0, fixedYawDeg: 0,
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => (
  { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

export function createActorsPanel(container, deps) {
  container.innerHTML = `
    <div class="row-btns actors-toolbar">
      <button class="actors-add accent">＋ 人形を追加</button>
      <span class="actors-hint">Unity の <b>Tools/FixedCamVr/Setup/Build Show Actor Prefab</b> で humanoid の FBX からプレハブを作り、その Resources パスをここに書く。姿勢を著作したカメラでだけ出る（カメラ列の 📐 欄 / フロアマップの 📐 モード）。</span>
      <span class="actors-msg ed-status"></span>
    </div>
    <div class="actors-list"></div>`;

  const listEl = container.querySelector('.actors-list');
  const msgEl = container.querySelector('.actors-msg');
  let actors = [];
  let msgTimer = 0;

  const note = (m) => {
    msgEl.textContent = m;
    clearTimeout(msgTimer);
    msgTimer = setTimeout(() => { msgEl.textContent = ''; }, 4000);
  };

  function save() {
    deps.save(actors.map((a) => ({ ...a })));
    // カットの「CG 人形」ドロップダウンを即座に追従させる。
    window.dispatchEvent(new CustomEvent('fc-actors-changed'));
  }

  function render() {
    listEl.innerHTML = '';
    if (!actors.length) {
      listEl.innerHTML = '<div class="actors-empty">まだ人形がありません。「＋ 人形を追加」で作ると、演出のカットで選べるようになります。</div>';
      return;
    }
    actors.forEach((a, i) => {
      const row = document.createElement('div');
      row.className = 'actors-row';
      row.innerHTML = `
        <label class="ac-f">ID<input class="ac-id" type="text" size="8" value="${esc(a.id)}" title="カットから指す名前（英数字）"></label>
        <label class="ac-f">表示名<input class="ac-name" type="text" size="8" value="${esc(a.name)}"></label>
        <label class="ac-f ac-prefab-l">プレハブ<input class="ac-prefab" type="text" size="18" value="${esc(a.prefab)}" title="Resources からのパス（例 ShowActors/Remy）"></label>
        <label class="ac-f">身長 m<input class="ac-h" type="number" step="0.05" min="0.2" max="3" value="${a.heightM}"></label>
        <label class="ac-f">固定X<input class="ac-x" type="number" step="0.05" value="${a.fixedX}"></label>
        <label class="ac-f">固定Z<input class="ac-z" type="number" step="0.05" value="${a.fixedZ}"></label>
        <label class="ac-f">向き°<input class="ac-yaw" type="number" step="5" value="${a.fixedYawDeg}"></label>
        <button class="ac-del" title="この人形を消す">🗑</button>`;

      const q = (s) => row.querySelector(s);
      const num = (el, d) => { const v = parseFloat(el.value); return Number.isFinite(v) ? v : d; };
      const commit = () => {
        const id = q('.ac-id').value.trim();
        if (!id) { note('ID は空にできません'); q('.ac-id').value = a.id; return; }
        if (actors.some((o, j) => j !== i && o.id === id)) {
          note(`ID「${id}」は他の人形と重複しています`);
          q('.ac-id').value = a.id;
          return;
        }
        a.id = id;
        a.name = q('.ac-name').value.trim();
        a.prefab = q('.ac-prefab').value.trim();
        a.heightM = Math.min(3, Math.max(0.2, num(q('.ac-h'), 1.6)));
        a.fixedX = num(q('.ac-x'), 0);
        a.fixedZ = num(q('.ac-z'), 0);
        a.fixedYawDeg = num(q('.ac-yaw'), 0);
        save();
      };
      row.querySelectorAll('input').forEach((inp) => { inp.onchange = commit; });
      q('.ac-del').onclick = () => {
        actors.splice(i, 1);
        save();
        render();
      };
      listEl.appendChild(row);
    });
  }

  container.querySelector('.actors-add').onclick = () => {
    let id = DEFAULT_ACTOR.id;
    for (let n = 2; actors.some((a) => a.id === id); n++) id = `${DEFAULT_ACTOR.id}${n}`;
    actors.push({ ...DEFAULT_ACTOR, id });
    save();
    render();
  };

  function onState(state) {
    actors = ((state && state.actors) || []).map((a) => ({ ...DEFAULT_ACTOR, ...a }));
    render();
  }

  render();
  return { onState, getActors: () => actors.map((a) => ({ ...a })) };
}
