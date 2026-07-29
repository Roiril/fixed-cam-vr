// 廻リ視 web compositor — 共有定数・ヘルパ。
//   app.js / cue-editor.js / ribbon.js / composite-view.js が共通で使う小物を集約。
//   （floormap.js は自己完結のまま。ここには依存させない）

// ---- 画質（cameras[i].post / segment.post）12 パラメータ ----------------------
//   [key, label, min, max, step, default]
//   ⚠ ここを増やしたら **shaders.js の FS_POST と Unity の ScreenComposite.shader を同時に直す**。
//     数式・順序を一致させる規約で結ばれていて、片方だけ直すと沈黙して食い違う。
export const FX = [
  ['exposure', '露出', -2, 2, 0.01, 0],
  ['contrast', 'コントラスト', 0.5, 2, 0.01, 1],
  ['saturation', '彩度', 0, 2, 0.01, 1],
  ['temperature', '色温度', -1, 1, 0.01, 0],
  ['tint', '色かぶり', -1, 1, 0.01, 0],        // + 緑（蛍光灯 / 安物 CMOS）/ - マゼンタ
  ['lift', '黒浮き', 0, 0.3, 0.005, 0],        // 黒が締まらない安物センサー感
  ['vignette', 'ヴィネット', 0, 1, 0.01, 0],
  ['grain', 'グレイン', 0, 0.3, 0.005, 0],
  ['scanline', '走査線', 0, 1, 0.01, 0],
  ['scanlineCount', '走査線の本数', 0, 720, 10, 0],  // 0 = 既定 240（実機と卓で本数を揃えるために持つ）
  ['aberration', '色収差', 0, 1, 0.01, 0],           // 安いレンズの色ずれ（放射方向）
  ['pixelate', '低解像度化', 0, 1, 0.01, 0],         // 伝送の劣化を装うブロック化
];
export const FX_DEFAULT = Object.fromEntries(FX.map(([k, , , , , d]) => [k, d]));

/**
 * 「暗めの監視カメラ」既定グレーディング（show.json トップレベル `post` の出荷値）。
 *
 * FX_DEFAULT は**数式の恒等元**（加工なし）なので触らない。こちらは*作品の見た目*の既定で、
 * 卓の「🎥 監視カメラ既定へ」/ サーバの新規 show.json / Unity の焼き込みが同じ値を使う。
 * 現場が明るくて見えない時に動かすのは **露出だけ**でよい（他は形を決めている）。
 *   露出 -0.72   … 暗いが黒潰れしない（黒浮きが下を支える）
 *   コントラスト 1.28 + 黒浮き 0.065 … 締まった中間調 + 締まらない黒（安物センサー）
 *   彩度 0.35 / 色温度 -0.18 / 色かぶり +0.16 … 冷たく色が抜け、わずかに緑（蛍光灯 + 安物 CMOS）
 *   ヴィネット 0.45 / グレイン 0.065 / 走査線 0.20 … 広角レンズの周辺減光・ノイズ・走査線
 */
export const FX_CCTV = {
  exposure: -0.72, contrast: 1.28, saturation: 0.35,
  temperature: -0.18, tint: 0.16, lift: 0.065,
  vignette: 0.45, grain: 0.065, scanline: 0.20,
};
export const MW = 640, MH = 360;

// 境界ブレンド（合成跡を消す）設定。全カメラ・全プレビュー共通の可変オブジェクト。
//   app.js の blend-bar が書き換え、composite-view / cue-editor が参照する（live binding）。
export const blendCfg = { feather: 0.3, colorMatch: true, colorStrength: 1, laplacian: true, levels: 7 };

// カメラ index → 色（floormap.js / ribbon.js と同配色）。
export const CAM_COLORS = ['#5ad19a', '#5aa8ff', '#ffae5e', '#d98cff', '#ff6b8e', '#8ad4ff'];
export const camColor = (i) => CAM_COLORS[((i % CAM_COLORS.length) + CAM_COLORS.length) % CAM_COLORS.length];

export const escapeHtml = (s) => String(s).replace(/[&<>"]/g,
  (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// MJPEG は メインポート+1 から（同一オリジン 6 接続制限の回避。capture-server.py が両方 listen）
export const streamBase = () =>
  `${location.protocol}//${location.hostname}:${(parseInt(location.port, 10) || 80) + 1}`;
export const isVideoUrl = (u) => /\.(webm|mp4|mov|m4v)(\?|$)/i.test(u);

// パス各セグメントを percent-encode（スペース・括弧入りファイル名で Unity VideoPlayer が
// ロード失敗するのを防ぐ）。二重化しないよう decode してから encode。
export function encPath(u) {
  if (!u) return u;
  const encSeg = (s) => { if (!s) return s; try { return encodeURIComponent(decodeURIComponent(s)); } catch { return encodeURIComponent(s); } };
  return u.split('/').map(encSeg).join('/');
}

// ---- サーバ I/O -------------------------------------------------------------
// サーバ断でも呼び元が落ちないよう null を返す（postState / postCommand と同じ流儀）。
// 素通しにすると「▶ ラン開始を押したが何も起きず、エラーも出ない」になる。
export async function getState() {
  try { return await (await fetch('/state')).json(); }
  catch { return null; }
}
export async function postState(patch) {
  try { return await (await fetch('/state', { method: 'POST', body: JSON.stringify(patch) })).json(); }
  catch { return { ok: false }; }
}
export async function postCommand(cmd) {
  try { return await (await fetch('/command', { method: 'POST', body: JSON.stringify(cmd) })).json(); }
  catch { return { ok: false }; }
}

// cue オブジェクトを state.cues へ upsert（id 一致で置換、無ければ追加）→ postState。
//   最新 cues 配列を返す。呼び元は自分のメモリ state.cues に反映する。
export async function saveCueObject(cue) {
  const s = await getState();
  const cues = Array.isArray(s.cues) ? s.cues : [];
  const k = cues.findIndex((c) => c.id === cue.id);
  if (k >= 0) cues[k] = cue; else cues.push(cue);
  const r = await postState({ cues });
  return { ok: r && r.ok !== false, cues };
}

// ---- captures/ 素材一覧（全モジュール共有・購読可）-----------------------------
export const captures = { items: [] };
const captureListeners = new Set();
export function onCaptures(fn) { captureListeners.add(fn); return () => captureListeners.delete(fn); }
export async function refreshCaptures() {
  try { captures.items = await (await fetch('/captures/list')).json(); }
  catch { captures.items = []; }
  for (const fn of captureListeners) { try { fn(captures.items); } catch { /* noop */ } }
  return captures.items;
}

// ---- メディアキャッシュ（cue の sourceUrl / maskUrl を img/video で解決・再利用）------
//   consumer 毎に 1 個持つ（video 要素の currentTime 競合を避けるため共有しない）。
export function createMediaCache() {
  const cache = new Map(); // url -> { el, ready, isVideo }
  function load(url) {
    const isVideo = isVideoUrl(url);
    const rec = { el: null, ready: false, isVideo };
    if (isVideo) {
      const v = document.createElement('video');
      v.muted = true; v.loop = true; v.playsInline = true; v.crossOrigin = 'anonymous';
      v.addEventListener('canplay', () => { rec.ready = true; });
      v.src = url;
      rec.el = v;
    } else {
      const im = new Image(); im.crossOrigin = 'anonymous';
      im.onload = () => { rec.ready = true; };
      im.src = url;
      rec.el = im;
    }
    return rec;
  }
  return {
    get(url) {
      if (!url) return null;
      let rec = cache.get(url);
      if (!rec) { rec = load(url); cache.set(url, rec); }
      return rec;
    },
    // マスク PNG を img として解決（ready まで null 相当）。
    getImage(url) {
      if (!url) return null;
      let rec = cache.get(url);
      if (!rec) { rec = load(url); cache.set(url, rec); }
      return rec && rec.ready ? rec.el : null;
    },
    dispose() {
      for (const rec of cache.values()) {
        if (rec.isVideo && rec.el) { try { rec.el.pause(); rec.el.src = ''; } catch { /* noop */ } }
      }
      cache.clear();
    },
  };
}
