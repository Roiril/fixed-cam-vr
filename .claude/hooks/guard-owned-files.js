#!/usr/bin/env node
// PreToolUse(Bash|PowerShell): git commit がユーザー所有ファイルを巻き込むのを止める。
// 所有ファイルの表は .claude/rules/git-workflow.md。rule に書くだけでは commit の瞬間に読まれない
// （2026-10-22 のハーネス見直しで rule から hook へ移した）。
// 意図して含めるときは、コマンドの末尾に `# allow-owned` を付けて打ち直す。
'use strict';
const { execFileSync } = require('child_process');

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

let input = '';
process.stdin.on('data', d => { input += d; });
process.stdin.on('end', () => {
  let cmd = '';
  try { cmd = (JSON.parse(input).tool_input || {}).command || ''; } catch { process.exit(0); }
  if (!/\bgit\b[^\n;&|]*\bcommit\b/.test(cmd)) process.exit(0);
  if (/#\s*allow-owned\b/.test(cmd)) process.exit(0);
  const cwd = process.env.CLAUDE_PROJECT_DIR || process.cwd();

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
  process.exit(0);
});
