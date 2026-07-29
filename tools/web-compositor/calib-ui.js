// 実カメラの較正 UI — 静止フレームの上で床の既知点をクリックし、姿勢・画角・歪みを解いて
//   `cameras[].calib` に保存する。**判断は calib-session.js（純関数・node テスト済み）**、
//   数学は calib.js。ここが持つのは描画とイベントだけ。解の正しさをここで判断しない。
//
// なぜこの形か（設計の正本: .claude/plans/2026-07-27_cg-compositing-rebuild.md §2.2 /
// .claude/plans/2026-07-29_calib-ui-rebuild.md / .claude/rules/streaming.md「較正の実装と、
// 現場運用がそうでなければならない理由」）:
//
//   1. **静止フレームの上で打つ**。ライブ映像に直接打つと、打っている最中にフレームが差し替わって
//      点と絵の対応が崩れる（クリック誤差 1.5px で位置が数 cm 動く世界なので致命的）。
//   2. **打つ点は先に選ぶ**（course 座標が既知の候補から）。「クリックしてから座標を入れる」形にすると
//      対応の入れ違いが起きても気づけない。calib.js が「点の対応が入れ違っている可能性」としか
//      言えないのは、そこを機械では判定できないため。
//   3. **解いたら必ずワイヤーを重ねる**。rms(px) は「打った点にどれだけ合ったか」であって
//      「部屋に合っているか」ではない（4 点なら誤差 0 でも解が嘘のことがある）。
//      **実映像に部屋の線を重ねた絵だけが反証可能な一次証拠**。
//   4. **画角（焦点距離）は固定できるなら固定する**。f も推定すると人のクリック誤差 1.5px で
//      位置が 27cm ずれ、固定すれば 2cm に収まる（calib.test.mjs が実測で固定している）。
//      → 運用は「内部（画角）は一度だけ丁寧に / 外部（置き場所）は現場で毎回」。UI もその順に導く。
//   5. **実測していない幾何を実測のように扱わない**。既定値の壁・定数のタイル格子から作った点は
//      現場の床に目印が無い。打てば解は出るが実物と違う場所を指すので、いくら丁寧にクリックしても
//      合わない。候補からは畳み、検証線では点線にして「これは推測」と分かる形で出す。
//
// 卓は CG 人形そのものを描かない（実描画の正は Unity）。ここで描くのは投影の**線**だけ。

import {
  projectPoint, perPointResiduals, calibQualityLabel, calibrateFromFloorPoints,
} from './calib.js';
import { streamBase, escapeHtml } from './common.js';
import {
  sketchView, drawFloorSketch, snapToGrid, nearestRoomCorner, hitPoint,
} from './floor-sketch.js';
import {
  candidatePoints, wireSegments, calibMatchesSource, lockableFocalPx, hfovFromFocal,
  calibSummaryLines, calibWarnings, localIsoNow, applyCalibToCameras, clearCalibFromCameras,
  pointQuality, qualityHeadline, pointKey, wallLooksDefault, openSession, refreshSession,
  relocateSuspicion, leaveOneOutError, accuracyLine, SOURCE_ASSUMED, SOURCE_MEASURED,
  lensesOf, resolveLens, lensFocalFor, lensFromSolve, upsertLens, removeLens, nextLensId,
  applyLensToCameras, detachLensFromCameras, lensSummary, lensTrustIssues, rescaleCalib,
} from './calib-session.js';

// 純関数は calib-session.js が正本。既存の import 元（app.js など）を壊さないよう再輸出する。
export {
  DEFAULT_REG_POINTS, wallLooksDefault, pointKey, candidatePoints, wireSegments,
  pointsFromRefs, calibMatchesSource, lockableFocalPx, hfovFromFocal, calibSummaryLines,
  raisedRefCount, calibWarnings, calibBadgeText, formatSolvedAt, localIsoNow,
  applyCalibToCameras, clearCalibFromCameras, pointQuality,
} from './calib-session.js';

const CAND_GROUPS = [
  ['mark', '自分で置いた印'],
  ['reg', '位置合わせ点（床の×印テープ）'],
  ['room', '部屋の角（壁・床の縁）'],
  ['grid', 'タイルの角（仮想の格子）'],
  ['manual', '手入力'],
];

/**
 * 較正パネルを作る。カメラ列の［🎯 姿勢を合わせる］から `open(camId)` で開く。
 *
 * @param {HTMLElement} container 置き場所（通常 document.body。全画面オーバーレイを足す）
 * @param {{getCameras:()=>object[], getLayout:()=>object|null,
 *          getLiveImg:(camId:string)=>HTMLImageElement|null,
 *          saveCameras:(cams:object[])=>any, saveLayout?:(layout:object)=>any}} deps
 */
export function createCalibUi(container, deps) {
  const root = document.createElement('div');
  root.className = 'calib-ui';
  root.style.display = 'none';
  root.innerHTML = `
    <div class="cu-backdrop"></div>
    <div class="cu-panel" role="dialog" aria-modal="true" aria-label="カメラ姿勢の較正">
      <div class="cu-head">
        <b class="cu-title">🎯 姿勢を合わせる</b>
        <span class="cu-frameinfo"></span>
        <span class="spacer"></span>
        <button class="cu-close">✕ 閉じる</button>
      </div>
      <div class="cu-body">
        <div class="cu-viewcol">
          <div class="cu-canvas-wrap">
            <canvas class="cu-canvas" width="640" height="480"></canvas>
            <div class="cu-noframe"></div>
          </div>
          <div class="cu-ptlist"></div>
          <div class="cu-hint">静止フレームです（打っている間に絵が動かないよう固定しています）。
            <b>打つ点</b>を選んでから映像上のその場所をクリック。点はドラッグで微調整・右クリックで削除。
            4 点以上で <b>✨ 解く</b>（4 点ちょうどのときは 3 点が一直線に並ばないように）。</div>
        </div>
        <div class="cu-side">
          <div class="cu-mapblock">
            <div class="cu-maphead">部屋を上から見た図<span class="cu-mapmode-hint"></span></div>
            <canvas class="cu-map" width="300" height="300"></canvas>
            <div class="cu-maprow">
              <label class="chk"><input class="cu-mapadd" type="checkbox"> 印を置く</label>
              <input class="cu-marklabel" type="text" placeholder="目印の名前（例: 棚の脚）" maxlength="24">
              <button class="cu-markdel" title="選んでいる印を消す">🗑</button>
            </div>
            <div class="cu-maphint">図の点をクリックすると「打つ点」がそこに切り替わります。
              <b>印を置く</b>にすると、クリックした場所に自分の基準点を作れます（タイルの角と部屋の角へ吸着）。
              <b>点は床の上にあるもの・実物が現場にあるものだけ</b>にしてください（机の上の印は使えません）。</div>
          </div>
          <div class="cu-srcwarn"></div>
          <label class="cu-lbl">打つ点 <select class="cu-cand"></select></label>
          <label class="chk cu-assumedlbl"><input class="cu-assumed" type="checkbox">
            <span>測っていない点も出す（既定値の壁・仮想の格子）</span></label>
          <div class="cu-manual">
            <label>X<input class="cu-mx" type="number" step="0.05" placeholder="0.00"></label>
            <label>Z<input class="cu-mz" type="number" step="0.05" placeholder="0.00"></label>
            <button class="cu-madd" title="course 座標が分かっている点を候補に足す（テープを増やした時など）">＋ 座標を足す</button>
          </div>
          <!-- 高さの点。床だけでは画角と距離が縮退するので、縦エッジを入れて平面の外から拘束する -->
          <div class="cu-heightbox">
            <div class="cu-heightrow">
              <button class="cu-addtop" title="いま打った点の真上（壁の上端）を次のクリックで打つ">▲ 高さの点を足す</button>
              <label title="次に打つ高さの点の高さ。打った後は点ごとに直せます">次に打つ高さ<input class="cu-height" type="number" step="0.05" min="0.1" value="1.80">m</label>
            </div>
            <div class="cu-heighthint">床の点だけだと<b>画角と距離が区別できません</b>（同じ絵になる解が無数にある）。
              壁の縦エッジを 2〜3 本入れると、画角・高さ・傾きがまとめて決まります。
              打ち方は「床の角を打つ → ▲ を押す → <b>同じ角の真上（壁の上端）</b>をクリック」。
              高さの違う物（箱と壁）を混ぜてよく、<b>打った後も点ごとに直せます</b>。</div>
          </div>
          <div class="cu-quality"></div>
          <!-- レンズ（内部パラメータ）。「内部は一度だけ丁寧に / 外部は現場で毎回」を UI に出す口 -->
          <div class="cu-lensbox">
            <label class="cu-lbl">レンズ（画角）
              <span class="cu-lensrow">
                <select class="cu-lens"></select>
                <button class="cu-lensdel" title="このレンズを消す（参照しているカメラからも外れます）">🗑</button>
              </span>
            </label>
            <div class="cu-lensinfo"></div>
          </div>
          <label class="chk cu-locklbl"><input class="cu-lock" type="checkbox"> <span class="cu-locktext"></span></label>
          <div class="cu-lockhint"></div>
          <!-- 保存済みの映像から較正する。現場では「撮るだけ撮って後で解く」ことがあるし、
               ライブが切れている間も作業を進められる（＝実カメラが無くても検証できる）。 -->
          <div class="cu-plate">
            <button class="cu-plateopen" title="現場で撮っておいた映像（📷 キャプチャ・録画）を静止フレームにする">📁 保存した映像から</button>
            <select class="cu-plates" style="display:none"></select>
          </div>
          <div class="cu-actions">
            <button class="cu-refresh" title="いまのライブ映像で静止フレームを取り直す（点はそのまま残る）">🔄 フレームを取り直す</button>
            <button class="cu-clear" title="打った点を全部消す（カメラを置き直したときはこちら）">🗑 全部消す</button>
            <button class="cu-solve accent">✨ 解く</button>
            <button class="cu-save accent">💾 保存</button>
            <button class="cu-undo" title="直前に保存した較正へ戻す">↩ 元に戻す</button>
          </div>
          <div class="cu-msg"></div>
          <div class="cu-result"></div>
          <div class="cu-wirekey">重ねている線: <span class="k-floor">床</span> <span class="k-grid">0.3m 格子</span>
            <span class="k-wall">壁</span> <span class="k-line">通過ライン</span>
            ／ <span class="k-assumed">点線 = 測っていない（推測）</span>
            ／ <span class="k-mark">○ 打った点</span> <span class="k-reproj">✕ 解が言う位置</span></div>
          <div class="cu-danger"><button class="cu-discard">✕ 較正を捨てて概算 pose に戻す</button></div>
        </div>
      </div>
    </div>`;
  container.appendChild(root);

  const q = (s) => root.querySelector(s);
  const canvas = q('.cu-canvas');
  const ctx = canvas.getContext('2d');
  const noFrameEl = q('.cu-noframe');
  const candSel = q('.cu-cand');
  const lockChk = q('.cu-lock'), lockText = q('.cu-locktext'), lockHint = q('.cu-lockhint');
  const msgEl = q('.cu-msg'), resultEl = q('.cu-result'), ptListEl = q('.cu-ptlist');
  const saveBtn = q('.cu-save'), discardBtn = q('.cu-discard'), undoBtn = q('.cu-undo');
  const clearBtn = q('.cu-clear');
  const mapCanvas = q('.cu-map'), mapCtx = mapCanvas.getContext('2d');
  const heightInput = q('.cu-height'), addTopBtn = q('.cu-addtop');
  const mapAddChk = q('.cu-mapadd'), markLabelInput = q('.cu-marklabel'), qualityEl = q('.cu-quality');
  const assumedChk = q('.cu-assumed'), srcWarnEl = q('.cu-srcwarn');
  const lensSel = q('.cu-lens'), lensInfoEl = q('.cu-lensinfo'), lensDelBtn = q('.cu-lensdel');
  const plateBtn = q('.cu-plateopen'), plateSel = q('.cu-plates');

  // 状態
  let camId = '';
  let frame = null;        // { canvas, w, h } 静止フレーム
  let pts = [];            // [{x,z,y,u,v,label}] u,v は**フレーム実寸の画素**
  let manual = [];         // 手入力で足した候補（このセッション限り）
  let solved = null;       // calibrateFromFloorPoints の戻り（新しく解いたもの）
  let saved = null;        // show.json に入っている calib（開いた時点のもの）
  let prevSaved = null;    // 直前に保存した calib（↩ 元に戻す 用の 1 世代）
  let accuracy = null;     // leave-one-out の精度（解くたびに測り直す）
  let frameSource = '';    // 静止フレームの出どころ（空 = ライブ / それ以外 = 保存した映像の名前）
  let liveSize = null;     // 最後に見たライブ映像の実寸（プレートで解いた較正をここへ合わせる）
  let lensId = '';         // いま流れている映像の /info レンズ ID（機種側の識別子・lenses[] とは別物）
  let lockedFocalPx = 0;
  let focalFrom = '';      // 固定画角の出どころ: 'lens'（登録レンズ）/ 'last'（前回の解）
  let dragIdx = -1;
  let msgTimer = 0;
  // 「次のクリックはこの床点の真上（高さ wallHeight）」という予約。null なら通常の床点。
  let topFor = null;

  /** 次に打つ高さの点の高さ（m）。**打った後の点は `pts[i].y` が正**で、ここは初期値でしかない。 */
  const nextHeight = () => {
    const v = parseFloat(heightInput.value);
    return Number.isFinite(v) && v > 0.05 ? v : 1.8;
  };
  /**
   * 検証ワイヤーの壁の高さ。**実際に打った高さの点の最頻値**を使う。
   * 入力欄の値をそのまま使うと、打った後に欄を変えただけで「打った上端」と「重ねた線」が
   * 別の高さになる（2026-07-29 監査で発見した実バグ）。打った値そのものを線に使えば食い違わない。
   */
  const wireWallHeight = () => {
    const hs = pts.filter((p) => p.y > 0).map((p) => Math.round(p.y * 100) / 100);
    if (!hs.length) return nextHeight();
    const count = new Map();
    for (const h of hs) count.set(h, (count.get(h) || 0) + 1);
    return [...count.entries()].sort((a, b) => b[1] - a[1] || b[0] - a[0])[0][0];
  };
  const cameras = () => deps.getCameras() || [];
  const camOf = (id) => cameras().find((c) => c.id === id) || null;
  const layout = () => deps.getLayout() || null;
  const lenses = () => (typeof deps.getLenses === 'function' ? lensesOf({ lenses: deps.getLenses() }) : []);
  /** このカメラに割り当てられたレンズ（未割当なら null）。 */
  const lensOf = () => resolveLens(lenses(), camOf(camId));
  /**
   * いま画面に出すべき較正。
   * **解に失敗した直後は何も出さない** — 古い解のワイヤーが残ると「解けません」の横で
   * 線が合っているように見え、失敗したことが絵から読めなくなる。
   */
  const shownCalib = () => {
    if (solved) return solved.ok ? solved.calib : null;
    return saved;
  };
  /** 未保存の解を抱えているか（閉じる前に確認する）。 */
  const hasUnsaved = () => !!(solved && solved.ok);

  function note(m, cls = '') {
    msgEl.textContent = m;
    msgEl.className = 'cu-msg ' + cls;
    clearTimeout(msgTimer);
    if (m) msgTimer = setTimeout(() => { msgEl.textContent = ''; msgEl.className = 'cu-msg'; }, 6000);
  }

  // ---- 静止フレーム ----------------------------------------------------------
  //   ライブ <img> を 1 枚だけコピーして固定する。ここを省いてライブを直接使うと、
  //   点を打っている最中にフレームが変わって「打った位置と絵」がずれる。
  function setFrameFromImage(src, w, h, source) {
    const cv = document.createElement('canvas');
    cv.width = w; cv.height = h;
    cv.getContext('2d').drawImage(src, 0, 0);
    frame = { canvas: cv, w, h };
    frameSource = source;
    canvas.width = w; canvas.height = h;
    canvas.style.aspectRatio = `${w} / ${h}`;
  }

  function grabFrame() {
    const img = deps.getLiveImg(camId);
    if (!img || !img.naturalWidth || !img.naturalHeight) return false;
    setFrameFromImage(img, img.naturalWidth, img.naturalHeight, '');
    liveSize = { w: img.naturalWidth, h: img.naturalHeight };
    return true;
  }

  /** いま配信されている映像の実寸（プレートで解いた較正をここへ合わせるために覚えておく）。 */
  function currentLiveSize() {
    const img = deps.getLiveImg(camId);
    if (img && img.naturalWidth > 1) return { w: img.naturalWidth, h: img.naturalHeight };
    return liveSize;
  }

  function syncFrameInfo() {
    const el = q('.cu-frameinfo');
    const from = frameSource ? `保存した映像: ${frameSource}` : 'ライブ';
    el.textContent = frame
      ? `${from} / ${frame.w}×${frame.h}${lensId ? ` / レンズ ${lensId}` : ''}` : '映像なし';
    el.className = 'cu-frameinfo' + (frameSource ? ' plate' : '');
    noFrameEl.style.display = frame ? 'none' : '';
    noFrameEl.textContent = frame ? ''
      : 'このカメラの映像が来ていません（① 生リアルタイム映像が LIVE になるのを待つか、'
        + '📁 保存した映像から を使ってください）';
  }

  // ---- 保存した映像から（プレート）--------------------------------------------
  //   現場では「撮るだけ撮って後で解く」ことがあるし、ライブが切れている間も作業を進めたい。
  //   ⚠ ただしプレートは**撮った時のカメラ位置**の絵。その後に動かしていれば解は今と合わない。
  async function openPlates() {
    plateBtn.disabled = true;
    try {
      const r = await fetch('/captures/list');
      const list = (await r.json()).filter((x) => x && x.type === 'image');
      if (!list.length) return note('保存された画像がありません（📷 キャプチャで撮れます）', 'err');
      // このカメラの名前（camA 等）を含むものを先に出す。無ければ全部。
      const mine = list.filter((x) => new RegExp(`cam${camId}[_.]`, 'i').test(x.name));
      const shown = mine.length ? [...mine, ...list.filter((x) => !mine.includes(x))] : list;
      plateSel.innerHTML = '';
      const head = document.createElement('option');
      head.value = ''; head.textContent = `（${shown.length} 件 — 選ぶと静止フレームになります）`;
      plateSel.appendChild(head);
      for (const x of shown.slice(0, 200)) {
        const o = document.createElement('option');
        o.value = x.url;
        o.textContent = `${mine.includes(x) ? '★ ' : ''}${x.name}`;
        plateSel.appendChild(o);
      }
      plateSel.style.display = '';
      plateSel.focus();
    } catch (e) {
      note(`一覧を取れませんでした（${e && e.message ? e.message : '通信エラー'}）`, 'err');
    } finally {
      plateBtn.disabled = false;
    }
  }

  async function usePlate(url) {
    if (!url) return;
    const before = { frame: frame ? { w: frame.w, h: frame.h } : null, pts, lockFocal: lockChk.checked };
    // `<img>` の load / decode は描画パイプラインに依存し、裏タブ・非表示ウィンドウでは
    // 永久に解決しないことがある。fetch + createImageBitmap は描画に依存しないので確実。
    let bmp;
    try {
      const res = await fetch(url);
      if (!res.ok) throw new Error(String(res.status));
      bmp = await createImageBitmap(await res.blob());
    } catch (e) {
      return note(`その画像を読めませんでした（${e && e.message ? e.message : '読み込み失敗'}）`, 'err');
    }
    if (!(bmp.width > 1) || !(bmp.height > 1)) return note('その画像は空でした', 'err');
    setFrameFromImage(bmp, bmp.width, bmp.height, url.split('/').pop());
    applyFrameChange(before, '保存した映像を静止フレームにしました');
  }

  // /info の lensId。取れなくても較正はできる（照合が緩くなるだけ）ので静かに諦める。
  async function fetchLensId() {
    // 開き直し / 別カメラへの切り替えを跨いで結果が返ることがある。**別カメラのレンズ ID を
    // 焼き込むと較正が黙って無効化される**（Unity が lensId で照合する）ので世代を照合する。
    const id = camId;
    lensId = '';
    const cam = camOf(id);
    if (!cam || !cam.host) return;
    let found = '';
    try {
      const r = await fetch(`${streamBase()}/cam?host=${encodeURIComponent(cam.host)}`
        + `&port=${cam.port || 8080}&path=/info`
        + (cam.auth ? `&auth=${encodeURIComponent(cam.auth)}` : ''));
      const j = await r.json();
      if (j && typeof j.lensId === 'string') found = j.lensId;
    } catch { /* /info を持たない配信アプリ（IP Camera Lite 等）は普通にある */ }
    if (id !== camId) return;
    lensId = found;
    syncFrameInfo();
    renderResult();          // レンズが分かると「この解は今の映像に有効か」の判定が変わる
    draw();
  }

  // ---- 候補点セレクト --------------------------------------------------------
  function allCandidates(includeAssumed = assumedChk.checked) {
    const list = [
      ...candidatePoints(layout(), pts),
      ...manual.filter((m) => !pts.some((p) => pointKey(p.x, p.z) === m.key)),
    ];
    return includeAssumed ? list : list.filter((c) => c.source !== SOURCE_ASSUMED);
  }
  function renderCandidates(keepKey) {
    const list = allCandidates();
    const prev = keepKey || candSel.value;
    candSel.innerHTML = '';
    for (const [kind, label] of CAND_GROUPS) {
      const items = list.filter((c) => c.kind === kind);
      if (!items.length) continue;
      const og = document.createElement('optgroup');
      og.label = `${label}（${items.length}）`;
      for (const c of items) {
        const o = document.createElement('option');
        o.value = c.key;
        o.textContent = `${c.label} (${c.x.toFixed(2)}, ${c.z.toFixed(2)})`
          + (c.source === SOURCE_ASSUMED ? ' ⚠未測定' : '');
        og.appendChild(o);
      }
      candSel.appendChild(og);
    }
    if (!list.length) {
      const o = document.createElement('option');
      o.value = ''; o.textContent = assumedChk.checked
        ? '（候補がありません — 座標を手入力してください）'
        : '（実測した点がありません — 下のチェックで測っていない点も出せます）';
      candSel.appendChild(o);
    }
    // 直前の選択が残っていればそれを、消えていれば先頭（＝次に打つべき点）へ送る。
    candSel.value = list.some((c) => c.key === prev) ? prev : (list[0]?.key || '');
    renderSourceWarning();
  }
  const selectedCandidate = () => allCandidates().find((c) => c.key === candSel.value) || null;

  /**
   * 「打てる実測点があるか」を先に言う。
   * 現場の床に目印が無い点をいくら丁寧にクリックしても合わないので、**点の打ち方より前**に
   * ここを疑わせる（この道具が一度も成功していない最有力の原因）。
   */
  function renderSourceWarning() {
    const measured = allCandidates(true).filter((c) => c.source === SOURCE_MEASURED);
    if (measured.length >= 4) { srcWarnEl.textContent = ''; srcWarnEl.className = 'cu-srcwarn'; return; }
    srcWarnEl.className = 'cu-srcwarn warn';
    srcWarnEl.innerHTML = `⚠ <b>実測した基準点が ${measured.length} 個しかありません。</b>`
      + '較正には「course 座標が分かっていて、<b>現場の床に実物の目印がある</b>」点が 4 個以上要ります。'
      + '既定値の壁（1m×1m の L）や仮想のタイル格子は床に線が引かれていないので、'
      + 'それを打っても<b>座標が実物と違うまま解く</b>ことになります。'
      + '<br>先にフロアマップの <b>🧱 部屋</b> で壁・箱を実測して置くか、'
      + '上の図で <b>印を置く</b> にして「棚の脚」「テープの×」など<b>実物のある場所</b>を登録してください。';
  }

  // ---- ミニ地図（部屋を上から見た図）------------------------------------------
  //   セレクトの文字列（「タイルの角 (0.30, -0.45)」）だけでは、それが部屋のどこなのか
  //   作者には分からない。ここで**候補と打った点を絵にして**、地図の上で選べるようにする。
  //   置ける点（`layout.calibPoints`）は「現場の床に実物の目印があるもの」に限る — 地図で
  //   決めた座標は、床に印が無ければ現場で同定できず、誤差として表面化しないまま解を歪める。
  const camPose = (c) => ((c.calib && c.calib.fxPx > 1) ? c.calib : c.pose) || null;
  function drawMap() {
    const lay = layout();
    const view = sketchView(lay, mapCanvas, 12);
    drawFloorSketch(mapCtx, lay, view, {
      // 地図は床の図なので、高さの点（同じ床位置の上端）は重ねて描かない。
      points: pts.filter((p) => !(p.y > 0)).map((p) => ({ x: p.x, z: p.z, done: true })),
      marks: allCandidates().map((c) => ({ x: c.x, z: c.z, key: c.key, kind: c.kind })),
      activeKey: candSel.value,
      cameras: cameras().map((c, i) => ({ index: i, id: c.id, pose: camPose(c) }))
        .filter((c) => c.pose),
    });
    return view;
  }
  /** 地図のクリック位置 → course 座標（吸着つき）。 */
  function mapToCourse(ev) {
    const rect = mapCanvas.getBoundingClientRect();
    if (!rect.width) return null;
    const view = sketchView(layout(), mapCanvas, 12);
    const px = (ev.clientX - rect.left) / rect.width * mapCanvas.width;
    const py = (ev.clientY - rect.top) / rect.height * mapCanvas.height;
    return { view, px, py, ...view.toCourse(px, py) };
  }
  function onMapClick(ev) {
    const hit = mapToCourse(ev);
    if (!hit) return;
    const cands = allCandidates();
    if (!mapAddChk.checked) {
      // 近い候補を「打つ点」にする（地図から選ぶ）。
      const view = hit.view;
      const i = hitPoint(cands, view, hit.px, hit.py, 14);
      if (i < 0) return note('その辺りに候補点がありません（印を置くならチェックを入れる）', 'err');
      candSel.value = cands[i].key;
      afterCandidateChanged();
      return;
    }
    // 自分の印を置く。物理の目印がある所を指すのが前提なので**ラベル必須**。
    const label = markLabelInput.value.trim();
    if (!label) return note('印の名前を入れてください（例: 棚の脚・テープの×）', 'err');
    const room = nearestRoomCorner(layout(), hit.x, hit.z, 0.12);
    const p = room || snapToGrid(layout(), hit.x, hit.z);
    saveMarks([...(layout()?.calibPoints || []), { x: p.x, z: p.z, label }],
      `印「${label}」を (${p.x.toFixed(2)}, ${p.z.toFixed(2)}) に置きました — 現場の床にも印を付けてください`);
  }
  /** 印の保存。**保存できたときだけ ok を言う**（保存口が無いのに「置きました」と嘘をつかない）。 */
  async function saveMarks(list, okMsg) {
    if (typeof deps.saveLayout !== 'function') {
      return note('この卓では印を保存できません（layout の保存口がありません）', 'err');
    }
    const lay = { ...(layout() || {}), calibPoints: list };
    try {
      await deps.saveLayout(lay);
    } catch (e) {
      return note(`印を保存できませんでした（${e && e.message ? e.message : '通信エラー'}）`, 'err');
    }
    renderCandidates();
    afterCandidateChanged();
    if (okMsg) note(okMsg, 'ok');
  }
  function deleteSelectedMark() {
    const c = selectedCandidate();
    if (!c || c.kind !== 'mark') return note('消せるのは自分で置いた印だけです', 'err');
    const list = (layout()?.calibPoints || []).filter((p) => pointKey(p.x, p.z) !== c.key);
    saveMarks(list, '印を消しました');
  }
  /** 「打つ点」が変わった時（地図・セレクトの両方から呼ぶ）。 */
  function afterCandidateChanged() { drawMap(); renderQuality(); }

  // ---- 高さの点（壁の縦エッジ）------------------------------------------------
  function syncTopUi() {
    addTopBtn.classList.toggle('armed', !!topFor);
    addTopBtn.textContent = topFor ? '▲ 上端をクリック（Esc で取消）' : '▲ 高さの点を足す';
  }
  /** 直近に打った床の点の真上を、次のクリックで打てるようにする。 */
  function armTop() {
    // 上端をぶら下げられるのは**床の点**だけ（上端の上端は無い）。
    for (let i = pts.length - 1; i >= 0; i--) {
      const p = pts[i];
      if (p.y > 0) continue;
      // 同じ床位置に既に上端があるなら二度打たせない（同じ拘束が重複しても精度は上がらない）。
      if (pts.some((r) => r.y > 0 && Math.abs(r.x - p.x) < 1e-6 && Math.abs(r.z - p.z) < 1e-6)) continue;
      topFor = { x: p.x, z: p.z, h: nextHeight(), label: p.label || `(${p.x.toFixed(2)}, ${p.z.toFixed(2)})` };
      syncTopUi();
      note(`${topFor.label} の真上（高さ ${topFor.h.toFixed(2)}m）を映像でクリックしてください`, 'ok');
      return;
    }
    note('先に床の点を打ってください（その真上が高さの点になります）', 'err');
  }

  // ---- 点の質（解く前に言う）--------------------------------------------------
  function renderQuality() {
    const qy = pointQuality(pts, frame?.w || 0, frame?.h || 0, lockChk.checked && lockedFocalPx > 0);
    const detail = (qy.n + qy.nRaised) >= 2
      ? `　床の広がり ${qy.floorSpread.toFixed(2)}m ・ 画面の広がり ${Math.round(qy.imgSpread * 100)}%` : '';
    const tips = qy.issues.slice();
    if (qy.nRaised < 2) {
      tips.push(qy.nRaised === 0
        ? '高さの点がありません（壁の縦エッジを 2〜3 本入れると画角と距離の縮退が解けます）'
        : '高さの点が 1 本だけです（もう 1 本入れると画角が決まります）');
    }
    qualityEl.innerHTML = `<b>${escapeHtml(qualityHeadline(qy))}</b>`
      + `<span class="cu-qdetail">${escapeHtml(detail)}</span>`
      + (tips.length ? `<ul>${tips.map((s) => `<li>${escapeHtml(s)}</li>`).join('')}</ul>` : '');
    qualityEl.className = 'cu-quality' + (qy.ready ? ' ok' : '');
  }

  // ---- 打った点 --------------------------------------------------------------
  function renderPointList() {
    ptListEl.innerHTML = '';
    if (!pts.length) {
      const e = document.createElement('span');
      e.className = 'cu-pt-empty';
      e.textContent = '（まだ点がありません。右で点を選んで映像をクリック）';
      ptListEl.appendChild(e);
      return;
    }
    pts.forEach((p, i) => {
      const chip = document.createElement('span');
      chip.className = 'cu-pt' + (p.y > 0 ? ' up' : '');
      const head = document.createElement('span');
      head.innerHTML = `<b>${i + 1}</b> ${p.y > 0 ? '▲ ' : ''}${escapeHtml(p.label || '')} `
        + `(${p.x.toFixed(2)}, ${p.z.toFixed(2)})`;
      chip.appendChild(head);
      // 高さの点は**打った後も高さを直せる**。壁と箱で高さが違う現場があるうえ、
      // 実測し直したときに全点を打ち直させるのは筋が悪い（旧実装は打った時の値で固定だった）。
      if (p.y > 0) {
        const hi = document.createElement('input');
        hi.type = 'number'; hi.step = '0.05'; hi.min = '0.05';
        hi.className = 'cu-pth';
        hi.value = p.y.toFixed(2);
        hi.title = 'この点の高さ（m）';
        hi.onchange = () => {
          const v = parseFloat(hi.value);
          if (!(v > 0.05)) { hi.value = p.y.toFixed(2); return; }
          pts[i].y = v;
          afterPointsChanged();
        };
        chip.appendChild(hi);
        const unit = document.createElement('span');
        unit.textContent = 'm';
        unit.className = 'cu-ptunit';
        chip.appendChild(unit);
      }
      const del = document.createElement('button');
      del.textContent = '✕';
      del.title = 'この点を消す';
      del.onclick = () => { pts.splice(i, 1); afterPointsChanged(); };
      chip.appendChild(del);
      ptListEl.appendChild(chip);
    });
  }
  function afterPointsChanged() {
    // 点を動かしたら前の解は無効。**古い解のワイヤーを残したまま点だけ動く**のが一番危ない
    // （合っていないのに合っているように見える）。
    solved = null;
    accuracy = null;
    renderCandidates();
    renderPointList();
    renderResult();
    renderQuality();
    drawMap();
    draw();
  }

  // ---- 描画 ------------------------------------------------------------------
  const WIRE_STYLE = {
    floor: { color: 'rgba(255,222,173,0.95)', w: 2 },
    grid: { color: 'rgba(255,222,173,0.35)', w: 1 },
    wall: { color: 'rgba(120,220,255,0.95)', w: 2 },
    line: { color: '#fffaf0', w: 2 },
  };

  function draw() {
    if (!frame) { ctx.clearRect(0, 0, canvas.width, canvas.height); return; }
    ctx.drawImage(frame.canvas, 0, 0);
    const s = Math.max(1, frame.w / 640);        // 640px 基準で線幅・字を拡縮
    const c0 = shownCalib();
    // 解像度・レンズが食い違う較正で線を引くと、ずれの原因が「解」なのか「前提」なのか分からなくなる。
    const calib = calibMatchesSource(c0, frame.w, frame.h, lensId) ? c0 : null;

    if (calib) {
      for (const seg of wireSegments(layout(), { wallH: wireWallHeight() })) {
        const a = projectPoint(calib, seg.a[0], seg.a[1], seg.a[2]);
        const b = projectPoint(calib, seg.b[0], seg.b[1], seg.b[2]);
        // 片端がカメラ後方なら描かない。無理に伸ばすと画面外へ暴れて「較正が壊れた」ように見える。
        if (!a || !b) continue;
        const st = WIRE_STYLE[seg.kind] || WIRE_STYLE.line;
        ctx.strokeStyle = seg.color || st.color;
        ctx.lineWidth = st.w * s;
        // 実測していない幾何は**点線**。ずれていても「それは推測の線」と分かる形で出す
        // （消すと部屋の見当が付かず、実線で出すと較正のせいだと誤診する）。
        ctx.setLineDash(seg.source === SOURCE_ASSUMED ? [6 * s, 5 * s] : []);
        ctx.globalAlpha = seg.source === SOURCE_ASSUMED ? 0.5 : 1;
        ctx.beginPath(); ctx.moveTo(a.u, a.v); ctx.lineTo(b.u, b.v); ctx.stroke();
      }
      ctx.setLineDash([]); ctx.globalAlpha = 1;
    }

    // 打った点（○ + 番号）と、解が言う位置（✕）。この 2 つのズレが残差そのもの。
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.font = `bold ${11 * s}px system-ui, sans-serif`;
    // 高さの点は「同じ床位置の点」と線で結ぶ。縦エッジが実物の柱・壁の角に重なっているかは、
    // この線が実際の縦の稜線に乗っているかで一目で分かる（打ち間違いの最有力な検出手段）。
    ctx.strokeStyle = 'rgba(120,220,255,0.85)';
    ctx.lineWidth = 2 * s;
    pts.forEach((p) => {
      if (!(p.y > 0)) return;
      const base = pts.find((r) => !(r.y > 0) && Math.abs(r.x - p.x) < 1e-6 && Math.abs(r.z - p.z) < 1e-6);
      if (!base) return;
      ctx.beginPath(); ctx.moveTo(base.u, base.v); ctx.lineTo(p.u, p.v); ctx.stroke();
    });
    pts.forEach((p, i) => {
      const up = p.y > 0;
      ctx.beginPath(); ctx.arc(p.u, p.v, 9 * s, 0, Math.PI * 2);
      // 塗ってから縁取る（逆にすると半透明の塗りが縁を食って番号が読みにくくなる）
      ctx.fillStyle = 'rgba(20,24,32,0.75)'; ctx.fill();
      ctx.strokeStyle = up ? '#78dcff' : '#ffdead'; ctx.lineWidth = 2 * s; ctx.stroke();
      ctx.fillStyle = up ? '#78dcff' : '#ffdead'; ctx.fillText(String(i + 1), p.u, p.v + 0.5 * s);
      if (calib) {
        const rp = projectPoint(calib, p.x, p.y || 0, p.z);
        if (rp) {
          // 打った点と「解が言う位置」を結ぶ。これが残差そのもので、長い線＝直すべき点。
          const far = Math.hypot(rp.u - p.u, rp.v - p.v) > 6 * s;
          if (far) {
            ctx.strokeStyle = 'rgba(255,90,90,0.9)'; ctx.lineWidth = 1.5 * s;
            ctx.beginPath(); ctx.moveTo(p.u, p.v); ctx.lineTo(rp.u, rp.v); ctx.stroke();
          }
          ctx.strokeStyle = far ? '#ff5a5a' : '#5ad19a'; ctx.lineWidth = 2 * s;
          ctx.beginPath();
          ctx.moveTo(rp.u - 6 * s, rp.v - 6 * s); ctx.lineTo(rp.u + 6 * s, rp.v + 6 * s);
          ctx.moveTo(rp.u + 6 * s, rp.v - 6 * s); ctx.lineTo(rp.u - 6 * s, rp.v + 6 * s);
          ctx.stroke();
        }
      }
    });
  }

  // ---- 解く / 保存 -----------------------------------------------------------
  function syncLockUi() {
    const hfov = hfovFromFocal(lockedFocalPx, frame ? frame.w : 0);
    lockChk.disabled = !(lockedFocalPx > 0);
    if (!(lockedFocalPx > 0)) lockChk.checked = false;
    const from = focalFrom === 'lens' ? '登録したレンズ' : 'このカメラの前回の解';
    lockText.textContent = lockedFocalPx > 0
      ? `画角を固定して解く（${Math.round(lockedFocalPx)}px 相当・水平 ${hfov.toFixed(0)}°・${from}）`
      : '画角を固定して解く（固定できる値がまだありません）';
    lockHint.textContent = lockedFocalPx > 0
      ? '内部（画角）は一度だけ丁寧に、外部（置き場所）は現場で毎回。固定して解くと位置の精度が一桁上がります。'
      : '一度解くと、その画角をレンズとして登録できます（次からは置き場所だけを解けます）。';
  }

  /**
   * レンズ欄。**選ぶ前に素性が見える**ようにする — 何点で・高さの点は何本で測った画角なのか。
   * ここを隠すと「4 点でフリーに解いたいい加減な f」が同型機へ伝播して、
   * しかも固定すると rms は下がるので誤りが良い数字に化ける。
   */
  function renderLensUi() {
    const list = lenses();
    const cam = camOf(camId);
    const cur = String(cam?.lensRef || '');
    lensSel.innerHTML = '';
    const none = document.createElement('option');
    none.value = ''; none.textContent = '（割り当てなし — 前回の解を使う）';
    lensSel.appendChild(none);
    for (const l of list) {
      const o = document.createElement('option');
      o.value = l.id;
      o.textContent = l.name;
      lensSel.appendChild(o);
    }
    lensSel.value = list.some((l) => l.id === cur) ? cur : '';
    const lens = list.find((l) => l.id === lensSel.value) || null;
    lensDelBtn.style.display = lens ? '' : 'none';

    lensInfoEl.innerHTML = '';
    if (!lens) {
      lensInfoEl.className = 'cu-lensinfo';
      lensInfoEl.textContent = list.length
        ? 'このカメラにレンズを割り当てると、次からは置き場所だけを解けます。'
        : 'まだレンズがありません。良い解が出たら「📌 レンズとして登録」で残せます（同じ機種のカメラで使い回せます）。';
      return;
    }
    const add = (text, cls = '') => {
      const d = document.createElement('div');
      d.className = cls; d.textContent = text; lensInfoEl.appendChild(d);
    };
    add(lensSummary(lens));
    if (frame && !lensFocalFor(lens, frame.w, frame.h)) {
      add(`⚠ このレンズは ${lens.srcW}×${lens.srcH} で測った値です（いまは ${frame.w}×${frame.h}）。`
        + '画角の固定には使えません — 解像度を戻すか、この解像度で測り直してください。', 'bad');
    }
    for (const w of lensTrustIssues(lens)) add(`⚠ ${w}`, 'warn');
    lensInfoEl.className = 'cu-lensinfo';
  }

  /** レンズ割り当ての変更（show.json の cameras[i].lensRef へ即書く）。 */
  async function onLensChanged() {
    const id = lensSel.value;
    await deps.saveCameras(applyLensToCameras(cameras(), camId, id));
    refreshLockSource();
    renderLensUi(); syncLockUi(); renderQuality(); renderResult();
    note(id ? 'レンズを割り当てました' : 'レンズの割り当てを外しました', 'ok');
  }

  /** 固定画角の供給源を引き直す（レンズ > 前回の解）。 */
  function refreshLockSource() {
    const fromLens = frame ? lensFocalFor(lensOf(), frame.w, frame.h) : 0;
    const fromLast = frame ? lockableFocalPx(saved, frame.w, frame.h) : 0;
    lockedFocalPx = fromLens || fromLast;
    focalFrom = fromLens ? 'lens' : (fromLast ? 'last' : '');
    if (!(lockedFocalPx > 0)) lockChk.checked = false;
  }

  /** いま解けている画角をレンズとして登録し、このカメラへ割り当てる。 */
  async function registerLens() {
    if (!(solved && solved.ok)) return;
    if (typeof deps.saveLenses !== 'function') {
      return note('この卓ではレンズを保存できません（lenses の保存口がありません）', 'err');
    }
    const cam = camOf(camId);
    const suggested = `カメラ ${cam?.id || ''} のレンズ（${frame.w}×${frame.h}）`;
    const name = (window.prompt('レンズの名前（機種とレンズが分かる名前に。同じ機種のカメラで使い回せます）',
      suggested) || '').trim();
    if (!name) return;
    const lens = lensFromSolve(solved, {
      id: nextLensId(lenses()), name, accuracyM: accuracy?.ok ? accuracy.medianM : 0,
    });
    if (!lens) return note('この解からはレンズを作れません', 'err');
    await deps.saveLenses(upsertLens(lenses(), lens));
    await deps.saveCameras(applyLensToCameras(cameras(), camId, lens.id));
    refreshLockSource();
    renderLensUi(); syncLockUi(); renderResult();
    note(`レンズ「${name}」を登録して割り当てました`, 'ok');
  }

  /** レンズを消す。参照しているカメラからも外さないと参照切れになる。 */
  async function deleteLens() {
    const id = lensSel.value;
    if (!id || typeof deps.saveLenses !== 'function') return;
    const users = cameras().filter((c) => c && c.lensRef === id).map((c) => c.id);
    const msg = users.length
      ? `このレンズはカメラ ${users.join(' / ')} が使っています。消すと画角の固定ができなくなります。消しますか？`
      : 'このレンズを消しますか？';
    if (!confirm(msg)) return;
    await deps.saveLenses(removeLens(lenses(), id));
    await deps.saveCameras(detachLensFromCameras(cameras(), id));
    refreshLockSource();
    renderLensUi(); syncLockUi(); renderResult();
    note('レンズを消しました', 'ok');
  }

  function renderResult() {
    const r = solved;
    resultEl.innerHTML = '';
    const addLine = (text, cls = '') => {
      const d = document.createElement('div');
      d.className = 'cu-res-line ' + cls;
      d.textContent = text;
      resultEl.appendChild(d);
      return d;
    };

    // 保存した映像で解いた較正は「**撮った時のカメラ位置**」の解。その後に触っていれば今と合わない。
    // 黙っていると「解いたのに実機で合わない」の原因が分からなくなる。
    if (frameSource) {
      addLine(`📁 いま見ているのは保存した映像です（${frameSource}）。`
        + 'これを撮った後にカメラを動かしていたら、解いても実機とは合いません。', 'warn');
      // プレートの寸法が配信そのままとは限らない（Quest 経由の記録は縮む）。
      // 縦横比が同じなら保存時に移せるが、違うなら移せない＝実機で無効になる。
      const live = currentLiveSize();
      if (frame && live && (live.w !== frame.w || live.h !== frame.h)) {
        const movable = Math.abs((live.w / live.h) / (frame.w / frame.h) - 1) < 0.01;
        addLine(movable
          ? `この映像は ${frame.w}×${frame.h}、配信は ${live.w}×${live.h} です`
            + '（縦横比が同じなので、保存するときに配信の実寸へ合わせます）。'
          : `⚠ この映像は ${frame.w}×${frame.h}、配信は ${live.w}×${live.h} で縦横比が違います。`
            + '切り取られた映像なので、ここで解いても実機では無効になります（ライブで解き直してください）。',
        movable ? 'sub' : 'bad');
      } else if (frame && !live) {
        addLine(`この較正は ${frame.w}×${frame.h} 用として保存されます。`
          + '配信の実寸が違うと実機では無効になるので、ライブが戻ったら 🔄 で解き直すのが確実です。', 'sub');
      }
    }
    // 壁の座標が既定のままなら、**点の打ち方より先に**そこを疑わせる。
    if (wallLooksDefault(layout())) {
      addLine('⚠ 壁の座標（layout.wall）が卓の既定値（1m × 1m の L）のままです。'
        + '現場の壁がこの寸法でないなら、「壁の外角」などを打っても座標が実物と違うので合いません。'
        + 'フロアマップの 🧱 部屋 で実物を測って置くか、実物の目印がある点だけで解いてください。',
      'warn');
    }
    if (!r && saved) {
      const qy = Number.isFinite(saved.rmsPx) ? calibQualityLabel(saved.rmsPx).text : '誤差の記録なし';
      addLine(`保存済みの較正を表示中 — ${qy}`, 'saved');
      for (const l of calibSummaryLines(saved)) addLine(l, 'sub');
      if (frame && !calibMatchesSource(saved, frame.w, frame.h, lensId)) {
        const why = saved.lensId && lensId && saved.lensId !== lensId
          ? `レンズが違います（この較正は ${saved.lensId} 用・いまは ${lensId}）`
          : `解像度が違います（この較正は ${saved.srcW}×${saved.srcH} 用・いまは ${frame.w}×${frame.h}）`;
        addLine(`⚠ ${why}。ワイヤーは重ねません — 実機でも較正は無効になるので解き直してください。`, 'bad');
      } else {
        addLine('ワイヤーが実物とずれていれば、点を直して ✨ 解く。', 'sub');
        // カメラを置き直した後は、復元した点が無関係な画素に散らばる。開いた瞬間に問う。
        const rel = relocateSuspicion(saved, pts);
        if (rel.suspect) {
          addLine(`⚠ 復元した点が、いまの映像と平均 ${Number.isFinite(rel.medianPx) ? Math.round(rel.medianPx) : '∞'}px ずれています。`
            + 'カメラを置き直したなら 🗑 全部消す から打ち直してください（ずれが小さいなら点を掴んで直すだけで済みます）。', 'warn');
        }
      }
    } else if (r && r.ok) {
      // **精度（知らない点をどれだけ当てられるか）を先に言う。** rms は当てはまりでしかない。
      addLine(accuracyLine(accuracy, r.calib.rmsPx), accuracyLevel(accuracy, r.calib.rmsPx));
      for (const l of calibSummaryLines(r.calib)) addLine(l, 'sub');
      // どの点が悪いのかを名指しする。「点を打ち直してください」だけでは、
      // 9 点のうちどれを直せばいいのか分からない（2026-07-29 ユーザー報告）。
      const worst = perPointResiduals(r.calib, pts)
        .map((x, i) => ({ ...x, no: i + 1, up: pts[i].y > 0, label: pts[i].label || '' }))
        .sort((a, b) => b.dist - a.dist)
        .filter((x) => !x.ok || x.dist > Math.max(4, r.calib.rmsPx * 1.8))
        .slice(0, 3);
      if (worst.length) {
        addLine(`ずれの大きい点: ${worst.map((x) => `${x.no}番${x.up ? '（高さ）' : ''}`
          + `${x.label ? ` ${x.label}` : ''} ${x.ok ? `${Math.round(x.dist)}px` : '投影できない'}`).join(' / ')}`, 'bad');
        addLine('その点だけ掴んで直すか、右クリックで消してから解き直してください。'
          + '高さの点なら、点の横の数字（高さ）が実物と合っているかも確かめてください。', 'sub');
      }
      for (const w of calibWarnings(r)) addLine(w, 'warn');
      // 解いた画角を**レンズとして残す**。ここが「内部は一度だけ丁寧に」の入口で、
      // 測定条件（点数・高さの本数・実際のずれ）を一緒に焼くので素性が追える。
      if (!r.focalLocked && r.calib.fxPx > 1 && typeof deps.saveLenses === 'function') {
        const reg = document.createElement('button');
        reg.className = 'cu-lockto';
        reg.textContent = `📌 この画角（水平 ${hfovFromFocal(r.calib.fxPx, r.calib.srcW).toFixed(0)}°）をレンズとして登録`;
        reg.title = '同じ機種のカメラで使い回せます。次からは「置き場所だけ」を解けるので位置の精度が一桁上がります。';
        reg.onclick = () => registerLens();
        resultEl.appendChild(reg);
      }
      if (!r.focalLocked && r.calib.fxPx > 1) {
        const b = document.createElement('button');
        b.className = 'cu-lockto';
        b.textContent = `この画角を固定して解き直す（${Math.round(r.calib.fxPx)}px）`;
        b.title = '同じ点のままなら結果はほぼ変わりません。効くのは次回 — カメラを動かした後に'
          + '「置き場所だけ」を解けるようになります。';
        b.onclick = () => {
          lockedFocalPx = r.calib.fxPx;
          focalFrom = 'last';
          lockChk.checked = true;
          syncLockUi();
          solve();
        };
        resultEl.appendChild(b);
      }
    } else if (r && !r.ok) {
      addLine(`解けません: ${r.reason}`, 'bad');
      if (Array.isArray(r.badPoints) && r.badPoints.length) {
        addLine('その番号の点を掴んで直すか、右クリックで消してから解き直してください。', 'sub');
      }
    } else {
      // 未較正で開いた直後。空箱にすると「何をすれば進むのか」が画面から消える。
      addLine('まだ較正していません。床の点を 4 個以上打って ✨ 解く。', 'sub');
      addLine('点は部屋いっぱいに散らす（一箇所に固まると解が暴れます）。', 'sub');
    }
    saveBtn.disabled = !(r && r.ok);
    clearBtn.disabled = !pts.length;
    undoBtn.style.display = prevSaved ? '' : 'none';
    discardBtn.style.display = saved ? '' : 'none';
  }

  /** 精度の行の色。当てはまりではなく**実際のずれ**で決める。 */
  function accuracyLevel(loo, rmsPx) {
    if (!loo || !loo.ok) return calibQualityLabel(rmsPx).level;
    if (loo.medianM > 0) return loo.medianM < 0.05 ? 'good' : (loo.medianM < 0.15 ? 'ok' : 'bad');
    return calibQualityLabel(loo.medianPx).level;
  }

  function solve() {
    if (!frame) return note('映像がありません（🔄 フレームを取り直す）', 'err');
    // 床の点が 4 つ要る（高さの点は平面の外なのでホモグラフィには入らない）。
    const floorCount = pts.filter((p) => !(p.y > 0)).length;
    if (floorCount < 4) return note(`床の点が ${floorCount} 個です（4 点以上必要）`, 'err');
    const opts = {
      lensId,
      solvedAtIso: localIsoNow(),
      fixedFocalPx: lockChk.checked ? lockedFocalPx : 0,
    };
    solved = calibrateFromFloorPoints(pts, frame.w, frame.h, opts);
    // 精度は「1 点を伏せて解き直す」ので点数ぶん解く。数十 ms で終わるが、失敗時は測らない。
    accuracy = solved.ok
      ? leaveOneOutError(pts, frame.w, frame.h, { ...opts, solvedAtIso: '' }) : null;
    if (solved.ok && accuracy && accuracy.ok && accuracy.medianM > 0) {
      solved.calib.accuracyM = Math.round(accuracy.medianM * 1000) / 1000;
    }
    renderResult();
    draw();
    note(solved.ok ? 'ワイヤーが実物と重なっているか確かめてください（重なっていなければ保存しない）'
      : '', solved.ok ? 'ok' : '');
  }

  async function save() {
    if (!(solved && solved.ok)) return;
    // 直前の解を 1 世代だけ持つ。焦った作業者が悪い解で良い解を潰したときの唯一の出口。
    prevSaved = saved ? { ...saved } : null;
    // 保存した映像で解いた場合、その寸法は配信そのままとは限らない（Quest 経由の記録は縮む）。
    // 縦横比が同じなら内部行列を比例で移せる — 移せないなら**そのまま保存して警告**する
    // （黙って比例で移すとクロップされた映像で嘘の較正になる）。
    let out = solved.calib;
    let moved = null;
    const live = currentLiveSize();
    if (frameSource && live && (live.w !== out.srcW || live.h !== out.srcH)) {
      const r = rescaleCalib(out, live.w, live.h);
      if (r) { moved = `${out.srcW}×${out.srcH} → ${live.w}×${live.h}`; out = r; }
    }
    const next = applyCalibToCameras(cameras(), camId, out);
    await deps.saveCameras(next);
    saved = { ...out };
    solved = null;                     // 保存済み＝未保存の解はもう無い（閉じる確認を出さない）
    // 保存した画角はそのまま「次に固定できる値」になる（レンズがあればそちらが優先）。
    refreshLockSource();
    syncLockUi();
    renderResult();
    draw();
    note(moved
      ? `💾 保存しました（配信の実寸に合わせて ${moved} へ移しました）`
      : '💾 保存しました（Unity へは long-poll で届きます）', 'ok');
  }

  /** 直前に保存した較正へ戻す（1 世代）。 */
  async function undo() {
    if (!prevSaved) return;
    const next = applyCalibToCameras(cameras(), camId, prevSaved);
    await deps.saveCameras(next);
    saved = { ...prevSaved };
    prevSaved = null;
    solved = null; accuracy = null;
    refreshLockSource();
    lockChk.checked = lockedFocalPx > 0;
    syncLockUi(); renderResult(); draw();
    note('↩ 直前に保存した較正へ戻しました', 'ok');
  }

  async function discard() {
    if (!confirm('この較正を捨てて、フロアマップで置いた概算 pose に戻します。よろしいですか？')) return;
    prevSaved = saved ? { ...saved } : null;
    const next = clearCalibFromCameras(cameras(), camId);
    await deps.saveCameras(next);
    saved = null; solved = null; accuracy = null;
    renderResult(); draw();
    note('較正を捨てました（このカメラは 📐 の概算 pose に戻ります）', 'ok');
  }

  /** 打った点を全部消す（カメラを置き直したときの出口。1 個ずつ右クリックさせない）。 */
  function clearPoints() {
    if (!pts.length) return;
    if (pts.length >= 3 && !confirm(`打った ${pts.length} 個の点を全部消します。よろしいですか？`)) return;
    pts = [];
    topFor = null; syncTopUi();
    afterPointsChanged();
    note('点を全部消しました（保存済みの較正はそのままです）', 'ok');
  }

  // ---- キャンバス操作 --------------------------------------------------------
  //   座標は必ず**フレーム実寸の画素**へ直してから持つ（表示サイズで持つと、パネル幅が
  //   変わっただけで対応が壊れる）。
  function toFramePx(ev) {
    const rect = canvas.getBoundingClientRect();
    if (!rect.width || !rect.height || !frame) return null;
    return {
      u: (ev.clientX - rect.left) / rect.width * frame.w,
      v: (ev.clientY - rect.top) / rect.height * frame.h,
      hit: 12 * frame.w / rect.width,      // 掴み判定は「見た目 12px」相当
    };
  }
  const nearestPoint = (u, v, hit) => {
    let best = -1, bd = hit;
    pts.forEach((p, i) => { const d = Math.hypot(p.u - u, p.v - v); if (d <= bd) { bd = d; best = i; } });
    return best;
  };

  canvas.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0) return;
    const t = toFramePx(ev);
    if (!t) return;
    const hitIdx = nearestPoint(t.u, t.v, t.hit);
    if (hitIdx >= 0) {                    // 既存点を掴んで微調整
      dragIdx = hitIdx;
      canvas.setPointerCapture(ev.pointerId);
      return;
    }
    if (topFor) {
      pts.push({ x: topFor.x, z: topFor.z, y: topFor.h, u: t.u, v: t.v, label: `${topFor.label} の上端` });
      topFor = null;
      syncTopUi();
      afterPointsChanged();
      return;
    }
    const cand = selectedCandidate();
    if (!cand) return note('打つ点を選んでください（候補が無ければ座標を手入力）', 'err');
    pts.push({ x: cand.x, z: cand.z, y: 0, u: t.u, v: t.v, label: cand.label });
    afterPointsChanged();
  });
  canvas.addEventListener('pointermove', (ev) => {
    if (dragIdx < 0) return;
    const t = toFramePx(ev);
    if (!t) return;
    pts[dragIdx].u = t.u; pts[dragIdx].v = t.v;
    solved = null; accuracy = null;       // 動かした時点で前の解は無効
    draw();
  });
  const endDrag = () => { if (dragIdx >= 0) { dragIdx = -1; afterPointsChanged(); } };
  canvas.addEventListener('pointerup', endDrag);
  canvas.addEventListener('pointercancel', endDrag);
  canvas.addEventListener('contextmenu', (ev) => {
    ev.preventDefault();
    const t = toFramePx(ev);
    if (!t) return;
    const i = nearestPoint(t.u, t.v, t.hit);
    if (i >= 0) { pts.splice(i, 1); afterPointsChanged(); }
  });

  // ---- 配線 ------------------------------------------------------------------
  q('.cu-close').onclick = () => close();
  q('.cu-backdrop').onclick = () => close();
  q('.cu-solve').onclick = () => solve();
  saveBtn.onclick = () => save();
  undoBtn.onclick = () => undo();
  clearBtn.onclick = () => clearPoints();
  discardBtn.onclick = () => discard();
  q('.cu-refresh').onclick = () => refresh();
  lockChk.onchange = () => { syncLockUi(); renderQuality(); };
  candSel.onchange = () => afterCandidateChanged();
  assumedChk.onchange = () => { renderCandidates(); afterCandidateChanged(); };
  mapCanvas.addEventListener('click', onMapClick);
  mapAddChk.onchange = () => {
    q('.cu-mapmode-hint').textContent = mapAddChk.checked
      ? '（クリックした場所に印を置きます）' : '（点をクリックすると「打つ点」が切り替わります）';
  };
  q('.cu-markdel').onclick = () => deleteSelectedMark();
  lensSel.onchange = () => onLensChanged();
  lensDelBtn.onclick = () => deleteLens();
  plateBtn.onclick = () => openPlates();
  plateSel.onchange = () => usePlate(plateSel.value);
  addTopBtn.onclick = () => { if (topFor) { topFor = null; syncTopUi(); note('', ''); } else armTop(); };
  heightInput.onchange = () => { if (topFor) topFor.h = nextHeight(); draw(); };
  q('.cu-madd').onclick = () => {
    const x = parseFloat(q('.cu-mx').value), z = parseFloat(q('.cu-mz').value);
    if (!Number.isFinite(x) || !Number.isFinite(z)) return note('X と Z を入れてください', 'err');
    const key = pointKey(x, z);
    if (!manual.some((m) => m.key === key)) {
      manual.push({ key, x, z, kind: 'manual', label: '手入力', source: SOURCE_MEASURED });
    }
    renderCandidates(key);
    afterCandidateChanged();
    note(`候補に (${x.toFixed(2)}, ${z.toFixed(2)}) を足しました`, 'ok');
  };
  const onKey = (ev) => {
    if (ev.key !== 'Escape' || root.style.display === 'none') return;
    // 上端の予約中は「予約の取消」を先に食わせる（間違えて閉じると打った点が消える）。
    if (topFor) { topFor = null; syncTopUi(); note('高さの点をやめました', ''); return; }
    close();
  };

  /**
   * 🔄 フレームを取り直す。何を保つかの判断は `refreshSession`（純関数・テスト済み）。
   * ここで解像度が変わったのに固定画角を残すと、**別解像度の焦点距離で解いた嘘の解が
   * 「画角固定済み」として保存される**（実機で人形が別の場所に立つ）。
   */
  function refresh() {
    const before = { frame: frame ? { w: frame.w, h: frame.h } : null, pts, lockFocal: lockChk.checked };
    if (!grabFrame()) {
      return note('ライブ映像が来ていません（📁 保存した映像から も使えます）', 'err');
    }
    applyFrameChange(before, 'フレームを取り直しました（点はそのまま）');
  }

  /** 静止フレームを差し替えた後の後始末。**ライブも保存映像も同じ判断を通す**。 */
  function applyFrameChange(before, okMsg) {
    const r = refreshSession(before, { w: frame.w, h: frame.h }, saved, layout(), lensOf());
    pts = r.pts;
    lockedFocalPx = r.lockedFocalPx;
    lockChk.checked = r.lockFocal;
    refreshLockSource();
    lockChk.checked = lockChk.checked && lockedFocalPx > 0;
    syncFrameInfo(); syncLockUi(); renderLensUi();
    renderPointList(); renderQuality(); renderResult(); drawMap(); draw();
    if (r.resized && r.dropped) {
      note(`映像の縦横比が変わったので、打っていた ${r.dropped} 点は使えません`
        + `${r.restored ? `（保存済みの ${r.restored} 点を復元しました）` : ''}`, 'err');
    } else if (r.restored) {
      note(`${okMsg}／保存済みの ${r.restored} 点を復元しました`, 'ok');
    } else if (r.resized) {
      note('映像の解像度が変わりました（画角の固定は解除しました — 解き直してください）', 'err');
    } else {
      note(okMsg, 'ok');
    }
  }

  function open(id) {
    camId = id;
    const cam = camOf(id);
    if (!cam) return;
    q('.cu-title').textContent = `🎯 カメラ ${cam.id} の姿勢を合わせる`;
    manual = []; solved = null; accuracy = null; prevSaved = null; dragIdx = -1;
    frame = null; frameSource = '';
    plateSel.style.display = 'none';
    grabFrame();
    const s = openSession(cam, frame ? { w: frame.w, h: frame.h } : null, layout(),
      resolveLens(lenses(), cam));
    saved = s.saved;
    pts = s.pts;
    lockedFocalPx = s.lockedFocalPx;
    focalFrom = s.focalFrom;
    lockChk.checked = s.lockFocal;
    root.style.display = '';
    document.addEventListener('keydown', onKey);
    mapAddChk.checked = false;
    mapAddChk.onchange();
    // 実測点が 4 個未満しか無い show.json では、測っていない点も出さないと何も打てない。
    // ただし ⚠未測定 の印と警告文は必ず出す（黙って推測の点を打たせない）。
    assumedChk.checked = allCandidates(true).filter((c) => c.source === SOURCE_MEASURED).length < 4;
    topFor = null;
    syncTopUi();
    syncFrameInfo(); renderLensUi(); syncLockUi(); renderCandidates(); renderPointList();
    renderResult(); renderQuality(); drawMap(); draw();
    if (s.needFrame) {
      note('映像が来てから 🔄 フレームを取り直すと、前回の点を復元します', 'err');
    } else if (s.restoredCount) {
      note(`前回の ${s.restoredCount} 点を復元しました（ずれている点だけ直せば解き直せます）`, 'ok');
    } else {
      note('', '');
    }
    fetchLensId();
  }
  function close() {
    // 未保存の解を黙って捨てない。背景クリック・✕・Esc のどれでも通る唯一の出口。
    if (hasUnsaved() && !confirm('解いた結果をまだ保存していません。閉じると失われます。閉じますか？')) return;
    root.style.display = 'none';
    document.removeEventListener('keydown', onKey);
    frame = null; frameSource = ''; pts = []; solved = null; saved = null; prevSaved = null;
    accuracy = null; camId = ''; lensId = '';
  }

  return {
    open,
    close,
    isOpen: () => root.style.display !== 'none',
    /** 開いているカメラが show.json 側で消えたら閉じる（カメラ削除・リロード時の取り残し防止）。 */
    onState: () => {
      if (root.style.display === 'none' || camOf(camId)) return;
      // カメラごと消えているので、未保存の解を確認しても戻る先が無い。確認せず畳む。
      root.style.display = 'none';
      document.removeEventListener('keydown', onKey);
      frame = null; pts = []; solved = null; saved = null; prevSaved = null;
      accuracy = null; camId = ''; lensId = '';
    },
  };
}
