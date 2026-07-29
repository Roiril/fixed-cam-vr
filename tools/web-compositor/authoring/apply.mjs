// 企画書どおりのタイムラインを組み立て → 検証 → （--apply なら）卓へ書き込む。
//
//   使い方:
//     node authoring/apply.mjs                 … 組み立てて検証と歩き検証を出すだけ（書き込まない）
//     node authoring/apply.mjs --apply         … 退避コピーを取ってから卓へ書き込む
//     node authoring/apply.mjs --dwell 6       … 速く歩く人（1 区間 6 秒）で検証する
//     SHOW_API=http://127.0.0.1:8099 node ...  … 卓のポートを変える（既定 8299）
//
//   ⚠ 書き込みは show.json を**丸ごと作り直す**（timeline / run / record / cameras.pose / cues）。
//     実行のたびに退避コピーを取るが、サーバ側の .bak は 1 世代しかないので当てにしない。

import { getState, patchState, backupShow, DEFAULT_BASE } from './show-api.mjs';
import { buildTimeline, REQUIRED_CUES, CAMERA_POSES, RUN, RECORD, SWITCH_GLITCH } from './mawarimi-show.mjs';
import { checkShow, formatCheck } from './check.mjs';
import { simulate, formatTrace, takeReport } from './sim.mjs';

const argv = process.argv.slice(2);
const has = (f) => argv.includes(f);
const val = (f, d) => {
  const i = argv.indexOf(f);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};

const dwellSec = Number(val('--dwell', '7.5'));
if (!Number.isFinite(dwellSec) || dwellSec <= 0) {
  console.error(`--dwell の値が数値ではありません: ${val('--dwell', '')}`);
  process.exit(2);
}
const doApply = has('--apply');

const { state, from } = await getState();
console.log(`# 現在の show.json（${from} / rev ${state.rev}）`);

// ---- 組み立て ---------------------------------------------------------------
const cams = state.cameras || [];
const indexOfCam = (id) => {
  const i = cams.findIndex((c) => c.id === id);
  if (i < 0) throw new Error(`カメラ ${id} が show.json にありません`);
  return i;
};

// 姿勢: 既に著作 / 較正済みのカメラは触らない（現場の値の方が正しい）。
const cameras = cams.map((c) => {
  const pose = CAMERA_POSES[c.id];
  if (!pose || c.pose || (c.calib && c.calib.fxPx > 1)) return c;
  return { ...c, pose: { ...pose } };
});

// 素材: この体験が使う cue は **mawarimi-show.mjs が正**（素材 URL・マスク・フェードごと上書き）。
// それ以外の cue（卓で作ったもの）は触らない。
const cues = (state.cues || []).slice();
const addedCues = [];
for (const c of REQUIRED_CUES) {
  const i = cues.findIndex((x) => x.id === c.id);
  if (i < 0) { cues.push({ ...c }); addedCues.push(`${c.id}（新規）`); continue; }
  if (JSON.stringify(cues[i]) === JSON.stringify({ ...cues[i], ...c })) continue;
  cues[i] = { ...cues[i], ...c };
  addedCues.push(`${c.id}（更新）`);
}

const timeline = buildTimeline(indexOfCam, (state.timeline && state.timeline.rev) || 0);
// control は**丸ごと置き換わる**（POST /state はトップレベルの浅い置換）。現場の値
//（activeCue / cameraOverride / autoFollow / discoveryEnabled …）を落とさないよう必ずマージする。
const control = { ...(state.control || {}), switchGlitch: SWITCH_GLITCH };
const next = { ...state, cameras, cues, timeline, control, run: { ...RUN }, record: { ...RECORD } };

// ---- 中身を読める形で出す -----------------------------------------------------
const camLabel = (i) => `カメラ ${(cams[i] && cams[i].id) || i}`;
console.log('\n# 組み立てたタイムライン');
for (const s of timeline.segments) {
  console.log(`\n[${s.lap}周目 / ${camLabel(s.camera)}]`
    + `${s.hasPost ? '  画像加工: 上書き' : ''}`
    + `${s.hasBgm ? `  BGM: ${s.bgm.action}` : ''}`);
  for (const t of s.takes) {
    const start = t.at === 'line' ? `ライン「${t.lineId}」通過`
      : t.at === 'exit' ? '離脱時' : `進入 +${t.offsetSec}s`;
    console.log(`  ▶ ${t.name}（${start} / ${t.once ? '1 回だけ' : '毎回'}`
      + `${t.wait === 'chain' ? ' / 前の演出が終わるまで待つ' : ''}`
      + `${t.hasBgm ? ` / 音: ${t.bgm.action}` : ''}）`);
    t.steps.forEach((st, i) => {
      const what = st.source === 'rec' ? `${st.recLap}周目の録画（${camLabel(st.camera)}）`
        : st.source === 'live' ? `生映像（${camLabel(st.camera)}）`
          : st.source === 'inherit' ? (st.cueId ? `いまの画面に「${st.cueId}」を重ねる` : 'いまの画面のまま')
            : `${st.source} ${st.assetUrl}`;
      console.log(`      ${i + 1}. ${what} — ${st.durSec}s / 遷移 ${st.transition}`
        + `${st.cg ? ` / CG 人形 ${st.cg}(${st.cgMode})` : ''}`
        + `${st.glitch > 0 ? ` / 頭に乱れ ${st.glitch}` : ''}`);
    });
  }
}
if (addedCues.length) console.log(`\n# 足した素材: ${addedCues.join(', ')}`);
const posed = cameras.filter((c, i) => c.pose && !(cams[i] || {}).pose).map((c) => c.id);
if (posed.length) console.log(`# 概算の姿勢を書いたカメラ: ${posed.join(', ')}（現場で 🎯 較正したら上書きされる）`);

// ---- 検証 1: 実機で黙って落ちる条件 -------------------------------------------
console.log(`\n# 検証（1 区間 ${dwellSec}s 想定）`);
const check = checkShow(next, { dwellSec });
for (const line of formatCheck(check)) console.log(line);

// ---- 検証 2: 歩かせてみる -----------------------------------------------------
const { cfg, meta, trace } = simulate(next, { laps: RUN.totalLaps, dwellSec, recSeconds: 6 });
console.log('\n# 3 周歩かせた結果（卓の ▶ 検証と同じ判定）');
for (const line of formatTrace(trace, cfg, meta, next)) console.log(line);

console.log('\n# 演出ごとの結果');
let missed = 0;
for (const r of takeReport(trace, meta)) {
  const ok = r.fired > 0;
  if (!ok) missed++;
  console.log(`${ok ? '✅' : '❌'} ${r.lap}周目/${camLabel(r.camera)} ${r.id}「${r.name}」`
    + ` — ${ok ? `${r.fired} 回発火` : '出なかった'}${r.dropped ? ` / ${r.dropped}` : ''}`);
}

const fatal = check.errors.length > 0 || missed > 0;
console.log(`\n# まとめ: エラー ${check.errors.length} / 警告 ${check.warns.length} / 出なかった演出 ${missed}`);

// ---- 書き込み ---------------------------------------------------------------
if (!doApply) {
  console.log('\n（--apply を付けると卓へ書き込みます）');
  process.exit(fatal ? 1 : 0);
}
if (fatal && !has('--force')) {
  console.log('\n❌ エラーが残っているので書き込みません（--force で強行できます）');
  process.exit(1);
}
const backup = backupShow();
console.log(`\n退避: ${backup}`);

// ⚠ ここまでの `state` は読んだ時点のスナップショット。書くまでの間に discovery が
// `cameras[].host` を更新したり、卓が世代カウンタ（runEpoch / takeAbortEpoch / glitchEpoch）を
// 進めたりしうる。それを古い値で送り返すと**戻し自体が中止・乱れ・ラン終了を発火させる**
//（世代カウンタは「値の変化」で伝わるため）。直前に読み直して、自分が変えるものだけを重ねる。
const { state: fresh } = await getState(DEFAULT_BASE);
if (fresh.rev !== state.rev) console.log(`（読み込み後に卓が rev ${state.rev} → ${fresh.rev} へ動いたので、最新へ重ねます）`);

const freshCams = fresh.cameras || [];
const camerasNow = freshCams.map((c) => {
  const pose = CAMERA_POSES[c.id];
  if (!pose || c.pose || (c.calib && c.calib.fxPx > 1)) return c;
  return { ...c, pose: { ...pose } };
});
const cuesNow = (fresh.cues || []).slice();
for (const c of REQUIRED_CUES) if (!cuesNow.some((x) => x.id === c.id)) cuesNow.push({ ...c });
const controlNow = { ...(fresh.control || {}), switchGlitch: SWITCH_GLITCH };

// 変えるものは変えると言う（record.laps を狭める等は黙ってやらない）。
const diff = (name, before, after) => {
  if (JSON.stringify(before) === JSON.stringify(after)) return;
  console.log(`  ${name}: ${JSON.stringify(before ?? null)} → ${JSON.stringify(after)}`);
};
console.log('変更:');
diff('run', fresh.run, next.run);
diff('record', fresh.record, next.record);
diff('control.switchGlitch', (fresh.control || {}).switchGlitch, SWITCH_GLITCH);
console.log(`  timeline: ${((fresh.timeline || {}).segments || []).length} 区間 → ${timeline.segments.length} 区間（作り直し）`);

const res = await patchState({
  cameras: camerasNow, cues: cuesNow, timeline: next.timeline,
  control: controlNow, run: next.run, record: next.record,
}, DEFAULT_BASE);
console.log(`書き込み完了: rev ${res.rev}（${DEFAULT_BASE}）`);
