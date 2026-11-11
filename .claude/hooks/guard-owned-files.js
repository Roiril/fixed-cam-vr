#!/usr/bin/env node
// PreToolUse(Bash|PowerShell): git commit がユーザー所有ファイルを巻き込むのを止める。
// 所有ファイルの表は .claude/rules/git-workflow.md。rule に書くだけでは commit の瞬間に読まれない
// （2026-10-22 のハーネス見直しで rule から hook へ移した）。
// 意図して含めるときは、コマンドの末尾に `# allow-owned` を付けて打ち直す。
//
// もう 1 つ（2026-11-11）: 幹 1 本の運用を守る。メインの木（このプロジェクトの main worktree）は常に master。
// - メインの木で master 以外の枝（detached を含む）へ git commit しようとしたら止める
// - メインの木で枝を切る・移る（git switch -c / git checkout -b / git switch <master 以外> / git checkout <既存の枝>）を止める
// ファイルの復元（git checkout -- <パス> など）・git worktree add -b・linked worktree・別のリポジトリは通す。
// 対象の木を解けないとき（変数・--git-dir 等）は通す（誤爆より素通しに倒す）。意図して打つなら末尾に `# allow-branch`。
// 起源: メインの木が 6 日間 feature/tablet-ui-motion に居続け、無関係な作業が全部そこへ積まれた。
'use strict';
const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const TRUNK = 'master';
const GIT_TIMEOUT_MS = 5000;

const OWNED = [
  [/^\.claude\/settings\.json$/, 'ユーザーのローカル権限・MCP 設定'],
  [/^ProjectSettings\/(?!EditorBuildSettings\.asset$)/, 'ProjectSettings は調整中（EditorBuildSettings だけは可）'],
  [/^Packages\/manifest\.json$/, '依存の追加削除は fable アドバイザー手続き'],
  [/^Assets\/Settings\/Cameras\/Phone[^/]*\.asset$/, '現場で書き換える host'],
  [/^Assets\/Settings\/ShowServer\.asset$/, '現場で書き換える host'],
  [/^Assets\/Settings\/URP-[^/]*\.asset$/, 'ビルドの副産物'],
  [/^Assets\/XR\/.*\.asset$/, 'ビルドの副産物'],
  [/^docs\/ivrc-[^/]*\.md$/, 'ユーザー作の文書'],
  [/^Assets\/Editor\/Layouts\/FixedCamVr\.wlt$/, 'ユーザーのウィンドウ配置'],
  [/^\.claude\/worktrees\//, 'エージェント worktree'],
];

function deny(reason) {
  process.stdout.write(JSON.stringify({
    hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: reason },
  }));
  process.exit(0);
}

function tokenize(s) {
  const out = [];
  const re = /"((?:[^"\\]|\\.)*)"|'([^']*)'|(\S+)/g;
  let m;
  while ((m = re.exec(s))) out.push(m[1] ?? m[2] ?? m[3]);
  return out;
}

function git(args, cwd) {
  try {
    return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
      .split('\n').map(l => l.trim()).filter(Boolean);
  } catch { return []; }
}

// ---- メインの木の枝（幹 1 本） ----

// 1 回の git の出力（前後の空白を落とす）。失敗・時間切れは null。
function gitOut(args) {
  try {
    return execFileSync('git', args, {
      encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: GIT_TIMEOUT_MS,
    }).trim();
  } catch { return null; }
}

// MSYS の /c/Users/... を C:\Users\... へ直す。
function toNativePath(p) {
  const m = /^\/([a-zA-Z])(\/.*)?$/.exec(p);
  return m ? m[1].toUpperCase() + ':' + (m[2] || '/').replace(/\//g, '\\') : p;
}

// base から見た arg の絶対パス。変数・展開を含むなど解けなければ null。
function resolveDir(base, arg) {
  if (base == null || !arg || /[$`%]/.test(arg)) return null;
  let p = arg;
  if (p === '~' || p.startsWith('~/') || p.startsWith('~\\')) p = path.join(os.homedir(), p.slice(1));
  return path.resolve(base, toNativePath(p));
}

function canon(p) {
  try { return fs.realpathSync.native(p).replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase(); } catch { return null; }
}

// { top, gitDir, commonDir }。作業ツリーの中でなければ null。
function repoDirs(dir) {
  const out = gitOut(['-C', dir, 'rev-parse', '--path-format=absolute', '--show-toplevel', '--git-dir', '--git-common-dir']);
  if (!out) return null;
  const [top, g, c] = out.split('\n').map(s => s.trim());
  const gitDir = g && canon(g);
  const commonDir = c && canon(c);
  return top && gitDir && commonDir ? { top, gitDir, commonDir } : null;
}

// dir がこのプロジェクトのメインの木か。linked worktree（--git-dir と --git-common-dir が違う）・別のリポジトリは違う。
function mainTreeOf(dir, projectDir) {
  if (!dir || !fs.existsSync(dir)) return null;
  const t = repoDirs(dir);
  const p = t && repoDirs(projectDir);
  if (!t || !p) return null;
  return t.gitDir === t.commonDir && t.commonDir === p.commonDir ? t.top : null;
}

const CD = new Set(['cd', 'chdir', 'pushd', 'set-location', 'push-location', 'sl']);
const PREFIX = new Set(['&', 'command', 'exec', 'time']);

// cd の行き先（解けなければ null）。
function cdTarget(cur, t) {
  const rest = t.slice(1).filter(a => !a.startsWith('#'));
  let arg = rest[0];
  if (arg && /^-(literal)?path$/i.test(arg)) arg = rest[1];
  else if (arg && arg.startsWith('-')) return null;
  return resolveDir(cur, arg);
}

// コマンドの位置にある git の { dir, sub, args }。git でなければ null。dir が null なら対象を解けない。
function parseGit(t, cur) {
  let gi = 0;
  while (gi < t.length && (PREFIX.has(t[gi]) || /^[A-Za-z_][A-Za-z0-9_]*=/.test(t[gi]))) gi++;
  if (gi >= t.length || !/^git(\.exe)?$/i.test(t[gi])) return null;
  let dir = cur;
  let i = gi + 1;
  while (i < t.length && t[i].startsWith('-')) {
    const o = t[i];
    if (o === '-C') { dir = resolveDir(dir, t[i + 1]); i += 2; continue; }
    if (o === '-c' || o === '--namespace' || o === '--config-env') { i += 2; continue; }
    if (o === '--git-dir' || o === '--work-tree') { dir = null; i += 2; continue; }
    if (o.startsWith('--git-dir=') || o.startsWith('--work-tree=')) dir = null;
    i++;
  }
  const end = t.findIndex((a, k) => k > i && a.startsWith('#'));
  return { dir, sub: t[i] || '', args: t.slice(i + 1, end < 0 ? t.length : end) };
}

// 枝を切る・移るか。返すのは止める理由の種（'create' / 'move:<名前>' / 'checkout:<名前>'）か null。
// checkout の 'checkout:<名前>' は、その名前が既存のローカル枝かを呼び出し側が確かめる。
function branchMove(sub, args) {
  const positional = args.filter(a => !a.startsWith('-'));
  if (sub === 'switch') {
    if (args.some(a => ['-c', '-C', '--create', '--force-create', '--orphan'].includes(a) ||
        /^-[cC]./.test(a) || /^--(create|force-create|orphan)=/.test(a))) return 'create';
    if (args.includes('--detach') || args.includes('-d')) return null;
    const name = positional[0];
    return name && name !== TRUNK ? 'move:' + name : null;
  }
  if (sub === 'checkout') {
    if (args.some(a => ['-b', '-B', '--orphan'].includes(a) || /^-[bB]./.test(a) || a.startsWith('--orphan='))) return 'create';
    if (args.includes('--')) return null;              // ファイルの復元
    return positional.length === 1 && positional[0] !== TRUNK ? 'checkout:' + positional[0] : null;
  }
  return null;
}

const BRANCH_HELP = 'メインの木は master だけ。`git switch master` してから打ち直す（未コミットの変更は付いてくる）。' +
  '別の木でやる作業は `git worktree add`。意図して打つならコマンド末尾に `# allow-branch` を付ける（理由を報告に書く）';

function guardBranch(cmd, projectDir) {
  let cur = projectDir;
  for (const seg of cmd.split(/&&|\|\||;|\n/)) {
    const t = tokenize(seg);
    if (!t.length) continue;
    if (CD.has(t[0].toLowerCase())) { cur = cdTarget(cur, t); continue; }
    const g = parseGit(t, cur);
    if (!g || !g.dir) continue;
    if (g.sub === 'commit') {
      const top = mainTreeOf(g.dir, projectDir);
      const branch = top && gitOut(['-C', g.dir, 'rev-parse', '--abbrev-ref', 'HEAD']);
      if (branch && branch !== TRUNK) {
        const name = branch === 'HEAD' ? 'HEAD（detached）' : branch;
        deny(`メインの木（${top}）のいまの枝は ${name}。master 以外の枝へはコミットしない。\n${BRANCH_HELP}`);
      }
      continue;
    }
    const move = branchMove(g.sub, g.args);
    if (!move) continue;
    const top = mainTreeOf(g.dir, projectDir);
    if (!top) continue;
    if (move === 'create') {
      deny(`メインの木（${top}）で枝を切ろうとしている（git ${g.sub} ${g.args.join(' ')}）。\n${BRANCH_HELP}`);
    }
    const [kind, name] = [move.slice(0, move.indexOf(':')), move.slice(move.indexOf(':') + 1)];
    if (kind === 'checkout' && gitOut(['-C', g.dir, 'rev-parse', '--verify', '--quiet', `refs/heads/${name}`]) == null) continue;
    deny(`メインの木（${top}）で枝 ${name} へ移ろうとしている（git ${g.sub} ${g.args.join(' ')}）。\n${BRANCH_HELP}`);
  }
}

// ---- ユーザー所有ファイル ----

function guardOwned(cmd, cwd) {
  if (!/\bgit\b[^\n;&|]*\bcommit\b/.test(cmd)) return;
  if (/#\s*allow-owned\b/.test(cmd)) return;

  for (const seg of cmd.split(/&&|\|\||;|\n/)) {
    const t = tokenize(seg);
    const gi = t.indexOf('git');
    if (gi < 0 || t.indexOf('commit', gi) < 0) continue;
    const args = t.slice(t.indexOf('commit', gi) + 1);
    if (args.includes('--amend') && !args.includes('--')) {
      // amend はステージ済みを見る（下の staged と同じ扱い）
    }
    if (args.some(a => a === '-a' || a === '--all' || /^-[A-Za-z]*a[A-Za-z]*$/.test(a) && !a.startsWith('--'))) {
      deny('git commit -a は追跡中の変更を全部入れる。このリポジトリは ProjectSettings・Phone*.asset 等が常に M なので巻き込む。pathspec でファイルを列挙して打ち直す（rules/git-workflow.md）');
    }
    const dd = args.indexOf('--');
    let files;
    if (dd >= 0) {
      const specs = args.slice(dd + 1).filter(a => !a.startsWith('#'));
      files = [...new Set([
        ...git(['diff', '--name-only', 'HEAD', '--', ...specs], cwd),
        ...git(['diff', '--cached', '--name-only', '--', ...specs], cwd),
        ...git(['ls-files', '--others', '--exclude-standard', '--', ...specs], cwd),
      ])];
    } else {
      files = git(['diff', '--cached', '--name-only'], cwd);
    }
    const hits = [];
    for (const f of files) {
      const p = f.replace(/\\/g, '/');
      const o = OWNED.find(([re]) => re.test(p));
      if (o) hits.push(`${p}（${o[1]}）`);
    }
    if (hits.length) {
      deny('ユーザー所有ファイルがコミットに入る:\n- ' + hits.join('\n- ') +
        '\npathspec から外して打ち直す。意図して含めるならコマンド末尾に `# allow-owned` を付ける（理由を報告に書く）。表は rules/git-workflow.md');
    }
  }
}

let input = '';
process.stdin.on('data', d => { input += d; });
process.stdin.on('end', () => {
  let cmd = '';
  try { cmd = (JSON.parse(input).tool_input || {}).command || ''; } catch { process.exit(0); }
  if (!/\bgit\b/.test(cmd)) process.exit(0);
  const cwd = process.env.CLAUDE_PROJECT_DIR || process.cwd();
  if (!/#\s*allow-branch\b/.test(cmd)) {
    try { guardBranch(cmd, cwd); } catch { /* 枝の判定の事故で所有ファイルの判定まで落とさない */ }
  }
  guardOwned(cmd, cwd);
  process.exit(0);
});
