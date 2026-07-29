// 卓（capture-server.py）を node から操作する薄いクライアント。
//
//   なぜ要るか: 演出の著作はブラウザの卓が正面だが、**同じことを機械が再現できない**と
//   「この体験はどう組まれているか」がクリックの履歴にしか残らない。ここを通せば
//   show.json の生成・検証・書き込みが 1 本のスクリプトになり、diff も取れる。
//
//   規約:
//   - **書き込みは必ずサーバ経由**（POST /state）。サーバは rev を上げて long-poll で
//     Quest へ配る。ファイルを直に書くと、動いているサーバのメモリ上の状態に上書きされて消える。
//   - POST /state は**トップレベルキーの浅い置換**。送らなかったキーは触られない。
//   - show.json は git 管理外で、サーバの .bak は 1 世代しか無い。破壊的な書き込みの前に
//     backupShow() で日付つきの退避を取る（作業ディレクトリの外へ置く）。

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const ROOT = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
export const SHOW_FILE = path.join(ROOT, 'show.json');

/**
 * 既定の卓 URL。`serve.ps1` の既定ポート = 8099。`SHOW_API=... node ...` で差し替えられる。
 * ⚠ 卓は **1 プロセスだけ**にすること。2 つ立てると各々がメモリ上の show.json を正として
 * 書き戻すので、古い方が新しい方を黙って巻き戻す（discovery スレッドが勝手に書くので無操作でも起きる）。
 */
export const DEFAULT_BASE = process.env.SHOW_API || 'http://127.0.0.1:8099';

async function req(base, url, init, timeoutMs = 8000) {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), timeoutMs);
  try {
    const res = await fetch(base + url, { ...init, signal: ac.signal });
    if (!res.ok) throw new Error(`${init && init.method === 'POST' ? 'POST' : 'GET'} ${url} → HTTP ${res.status}`);
    return await res.json();
  } finally {
    clearTimeout(timer);
  }
}

/** 卓が起きているか（起きていなければ書き込みはできない）。 */
export async function serverUp(base = DEFAULT_BASE) {
  try { await req(base, '/state', undefined, 2500); return true; } catch { return false; }
}

/** show.json を読む。卓が起きていればそちらが正（メモリ上の最新）。落ちていればファイル。 */
export async function getState(base = DEFAULT_BASE) {
  try {
    return { state: await req(base, '/state'), from: 'server' };
  } catch {
    return { state: JSON.parse(fs.readFileSync(SHOW_FILE, 'utf8')), from: 'file' };
  }
}

/**
 * トップレベルキーだけを浅く置換する。渡さなかったキーは不変。
 * 返り値は { ok, rev }（rev はサーバが上げた後の値）。
 */
export async function patchState(partial, base = DEFAULT_BASE) {
  if (!(await serverUp(base))) {
    throw new Error(`卓サーバ（${base}）が応答しません。tools/web-compositor/serve.ps1 で起動してください`
      + '（ファイルを直に書くと、動いているサーバのメモリで上書きされて消えます）');
  }
  return req(base, '/state', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(partial),
  }, 15000);
}

/** ラン操作（playCue / stopCue / abortTake / advanceIntro / endRun / glitch / bindSlot ...）。 */
export async function command(cmd, base = DEFAULT_BASE) {
  return req(base, '/command', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cmd),
  });
}

/** 実測滞在（実機 heartbeat 由来）。演出の開始位置が歩速に対して遅すぎないかの判定に使う。 */
export async function dwellStats(base = DEFAULT_BASE) {
  try { return await req(base, '/dwell/stats'); } catch { return { items: {}, updatedAt: 0 }; }
}

/**
 * show.json を日付つきで退避する。**破壊的な書き込みの前に必ず呼ぶ。**
 * 既定の退避先はリポジトリの外（Google ドライブ）— サーバの 1 世代 .bak では復元できないため。
 */
export function backupShow(dir = process.env.SHOW_BACKUP_DIR || 'G:\\マイドライブ\\shubie\\fixed-cam-vr-backups') {
  fs.mkdirSync(dir, { recursive: true });
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  const stamp = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
  const dst = path.join(dir, `show.json.${stamp}.bak.json`);
  fs.copyFileSync(SHOW_FILE, dst);
  return dst;
}
