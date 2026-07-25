// 素材（動画）の**実尺**をブラウザで測ってキャッシュする小物。
//
//   なぜ要るか: カットの尺「素材の終わりまで（untilClipEnd）」は、実機では素材の実尺で終わる。
//   卓はこれまで cue の trim からしか推定できず、リボンの幅もシミュレータの尺も「≈（推定）」だった。
//   同じファイルを <video preload=metadata> で読めば実尺が分かるので、推定を実測へ置き換える。
//
//   ・測れるのは動画（と音声）だけ。静止画は尺という概念が無いので null を返す
//     （Unity 側も untilClipEnd + 静止画は durSec>0 かフォールバック 4s で畳む）。
//   ・測定は非同期。呼び出し側は同期 getter（durationOf）で「今わかっている値」を読み、
//     onDurationResolved の通知で描き直す（描画ループをブロックしない）。
//   ・sa://（焼き込み URL）は卓からは読めないので測らない = null（推定表示のまま）。

import { isVideoUrl } from './common.js';

const cache = new Map();          // url -> { sec: number|null, done: bool }
const listeners = new Set();

/** 実尺が判明したとき（url, sec）に呼ばれる購読。解除関数を返す。 */
export function onDurationResolved(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

function notify(url, sec) {
  for (const fn of listeners) { try { fn(url, sec); } catch { /* 購読側の失敗で他を止めない */ } }
}

/** 測れる URL か（動画のみ。sa:// / 空は測らない）。 */
export function canProbe(url) {
  return !!url && !String(url).startsWith('sa://') && isVideoUrl(url);
}

/**
 * 今わかっている実尺（秒）を同期で返す。未測定なら測定を開始して null を返す。
 * 測定できないもの（静止画・sa://）は常に null。
 */
export function durationOf(url) {
  if (!canProbe(url)) return null;
  const rec = cache.get(url);
  if (rec) return rec.sec;
  cache.set(url, { sec: null, done: false });
  const v = document.createElement('video');
  v.preload = 'metadata';
  v.muted = true;
  const finish = (sec) => {
    const r = cache.get(url) || { sec: null, done: false };
    if (r.done) return;
    r.sec = Number.isFinite(sec) && sec > 0 ? sec : null;
    r.done = true;
    cache.set(url, r);
    v.removeAttribute('src');
    try { v.load(); } catch { /* noop */ }
    notify(url, r.sec);
  };
  v.addEventListener('loadedmetadata', () => finish(v.duration));
  v.addEventListener('error', () => finish(NaN));
  v.src = url;
  return null;
}

/** テスト・再読込用（素材を差し替えた時に測り直す）。 */
export function forget(url) {
  if (url) cache.delete(url); else cache.clear();
}
