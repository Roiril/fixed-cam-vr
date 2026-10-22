#!/usr/bin/env node
// PostToolUse(Edit|Write): .claude/ のハーネス資産を書き換えたら tools/harness-check.py を流し、
// ERROR / WARN を文脈へ返す。遮断はしない（書いた直後に直せるように知らせるだけ）。
// 2026-10-22 のハーネス見直しで追加。rule の積み増しは「書き方の規律」だけでは止まらなかった。
'use strict';
const { execFileSync } = require('child_process');
const path = require('path');

let input = '';
process.stdin.on('data', d => { input += d; });
process.stdin.on('end', () => {
  let file = '';
  try { file = ((JSON.parse(input).tool_input || {}).file_path || '').replace(/\\/g, '/'); } catch { process.exit(0); }
  if (!/\/\.claude\/(rules\/|memory\/MEMORY\.md|skills\/|canon\/(NOW|OPEN|TASTE)\.md|plans\/)|\/CLAUDE\.md$/.test(file)) process.exit(0);
  const root = process.env.CLAUDE_PROJECT_DIR || process.cwd();
  let out = '';
  try {
    out = execFileSync('py', ['-3.11', path.join(root, 'tools', 'harness-check.py'), '--quiet'],
      { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  } catch (e) {
    out = (e.stdout || '').toString();
  }
  const lines = out.split('\n').filter(l => l.startsWith('ERROR')).slice(0, 15);
  if (!lines.length) process.exit(0);
  process.stdout.write(JSON.stringify({
    hookSpecificOutput: {
      hookEventName: 'PostToolUse',
      additionalContext: 'harness-check（tools/harness-check.py）:\n' + lines.join('\n') +
        '\n上限を超えたら要約して縮めない。節ごと memory/ か reference/ へ移す（rules/doc-sync.md）。',
    },
  }));
  process.exit(0);
});
