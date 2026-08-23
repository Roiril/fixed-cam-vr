// show.json の著作を卓と同じ経路で組み立てて、警告と演出の一覧を出す（Unity も卓の UI も開かない）。
//
//   node tools/web-compositor/check-timeline.mjs [show.json のパス]
//
// 見るのは 2 つ:
//   ①`buildScenarioConfig` の warnings … 卓の UI が赤字で出すのと同じもの
//   ②区間ごとの演出とカットの一覧 … 著作したものがその区間に載っているか
//
// ⚠ これは**著作が壊れていないか**しか言わない。卓と実機は既に 5 件食い違っている
//   （`.claude/memory/sim_device_divergence.md`）。通ったことを合格の根拠にしない。

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { buildScenarioConfig } from './show-scenario.js';

const here = dirname(fileURLToPath(import.meta.url));
const path = process.argv[2] || join(here, 'show.json');
const state = JSON.parse(readFileSync(path, 'utf8'));

const { cfg, meta } = buildScenarioConfig(state);

console.log(`show: ${path}`);
console.log(`周回 ${state.run?.totalLaps ?? '?'} / コース ${JSON.stringify(state.layout?.course?.order ?? [])}`);

const warns = meta?.warnings ?? [];
console.log(`\n警告 ${warns.length} 件`);
for (const w of warns) console.log(`  ⚠ ${w}`);

console.log('\n区間と演出');
const segs = state.timeline?.segments ?? [];
for (const seg of segs) {
  const takes = seg.takes ?? [];
  const head = `  ${seg.lap}周目 カメラ${seg.camera}  演出 ${takes.length} 本`;
  if (!takes.length) { console.log(`${head}  （空）`); continue; }
  console.log(head);
  for (const t of takes) {
    const flags = [
      `at=${t.at}${t.offsetSec ? `+${t.offsetSec}s` : ''}`,
      `policy=${t.policy}`,
      t.once ? 'once' : '',
      t.dismissible ? '報告で畳む' : '',
    ].filter(Boolean).join(' ');
    console.log(`    ${t.id}  ${t.name || ''}`);
    console.log(`      ${flags}`);
    (t.steps ?? []).forEach((s, i) => {
      const dur = s.durKind === 'sec' ? `${s.durSec}s` : s.durKind;
      const cue = s.cueId ? ` cue=${s.cueId}` : '';
      const ov2 = s.overlay2CueId ? ` 第2層=${s.overlay2CueId}` : '';
      console.log(`      [${i}] ${s.source} cam=${s.camera}${cue}${ov2}  ${dur}  ${s.transition}`);
    });
  }
}

// 演出が指している cue が実在するか（卓は「unknown cue id」で黙って何も出さない）
const ids = new Set((state.cues ?? []).map((c) => c.id));
const missing = [];
for (const seg of segs) {
  for (const t of seg.takes ?? []) {
    for (const s of t.steps ?? []) {
      for (const key of ['cueId', 'overlay2CueId']) {
        const v = s[key];
        if (v && !ids.has(v)) missing.push(`${t.id} の ${key}='${v}'`);
      }
    }
  }
}
console.log(`\n指し先の無い cue ${missing.length} 件`);
for (const m of missing) console.log(`  ❌ ${m}`);

process.exitCode = (warns.length || missing.length) ? 1 : 0;
