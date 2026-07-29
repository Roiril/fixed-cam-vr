// いま卓に入っているショー（show.json の現物）を検証する。
//
//   apply.mjs は「これから書くもの」を検証するが、こちらは**書いた後の現物**を読んで同じ検査をかける。
//   卓でクリックして直したあとにも使える（誰が直したかに関係なく、実機で落ちる条件を洗う）。
//
//     node authoring/verify.mjs              … 1 区間 7.5 秒の歩きで検証
//     node authoring/verify.mjs --dwell 4    … 速く歩く人で検証
//     node authoring/verify.mjs --quiet      … トレースを出さず結果だけ

import { getState } from './show-api.mjs';
import { checkShow, formatCheck } from './check.mjs';
import { simulate, formatTrace, takeReport } from './sim.mjs';

const argv = process.argv.slice(2);
const val = (f, d) => {
  const i = argv.indexOf(f);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};
const dwellSec = Number(val('--dwell', '7.5'));
const quiet = argv.includes('--quiet');

const { state, from } = await getState();
const laps = (state.run && state.run.totalLaps > 0) ? state.run.totalLaps : 3;
console.log(`# 卓の現物（${from} / rev ${state.rev} / timeline rev ${(state.timeline || {}).rev} / ${laps} 周）`);

const check = checkShow(state, { dwellSec });
for (const line of formatCheck(check)) console.log(line);

const { cfg, meta, trace } = simulate(state, { laps, dwellSec, recSeconds: 6 });
if (!quiet) {
  console.log(`\n# ${laps} 周歩かせた結果（1 区間 ${dwellSec}s）`);
  for (const line of formatTrace(trace, cfg, meta, state)) console.log(line);
}

const camLabel = (i) => `カメラ ${((state.cameras || [])[i] || {}).id || i}`;
console.log('\n# 演出ごとの結果');
let missed = 0;
for (const r of takeReport(trace, meta)) {
  const ok = r.fired > 0;
  if (!ok) missed++;
  console.log(`${ok ? '✅' : '❌'} ${r.lap}周目/${camLabel(r.camera)} ${r.id}「${r.name}」`
    + ` — ${ok ? `${r.fired} 回発火` : '出なかった'}${r.dropped ? ` / ${r.dropped}` : ''}`);
}
console.log(`\n# まとめ: エラー ${check.errors.length} / 警告 ${check.warns.length} / 出なかった演出 ${missed}`);
process.exit(check.errors.length || missed ? 1 : 0);
