// show.json を**卓と同じ純ロジック**（scenario-engine = Unity のミラー）で回して、
// 何が・いつ・どの順で起きるかを文字で出す。
//
//   卓の ▶ 検証と同じ判定を通るので、ブラウザを開かずに「この歩き方をしたら何が起きるか」を
//   確かめられる。**検証できないもの**は卓と同じ: VR のスケール感・dip の体感・MJPEG の遅延・
//   位置合わせ・実機の性能。加えてここでは **導入 / 終了の相**（ShowRunDirector）も見ていない
//   （エンジンが持っていない）。歩きは「導入が終わった直後」から始まる。

import { createShowRunner, sampleAt, DROP_CODE } from '../scenario-engine.js';
import { buildScenarioConfig } from '../show-scenario.js';
import { buildWalk } from './walk.mjs';

const DROP_TEXT = [
  '別の演出 / ライブ卓が画面を使用中のまま区間が終わった',
  '同じ区間の別の演出が先に選ばれた',
  '待ち続けたが上限（本数 / 時間）に掛かった',
  '待っているあいだに、塞いでいた演出が人の操作で消えた',
];
const dropText = (code) => DROP_TEXT[code] || `不明(${code})`;

const fmtT = (ms) => {
  const s = ms / 1000;
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${(s % 60).toFixed(1).padStart(4, '0')}`;
};

/**
 * 1 本の歩きを回す。
 *   opts: { laps, dwellSec, travelSec, tailSec, recSeconds }
 *   recSeconds … 「録画」カットの尺（卓は録画を持たないので、この秒で代用する）
 */
export function simulate(state, opts = {}) {
  const laps = opts.laps || 3;
  const dwellSec = opts.dwellSec ?? 10;
  const recSeconds = opts.recSeconds ?? dwellSec;
  const { cfg, meta } = buildScenarioConfig(state, {
    // 「素材の終わりまで」の尺: node には <video> が無いので実測できない。
    // 録画カットだけは「録った区間の滞在時間」で代用できる（実機の挙動に一番近い）。
    getRecSeconds: () => recSeconds,
  });
  const walk = buildWalk({
    layout: state.layout || {},
    order: cfg.courseOrder,
    laps,
    dwellSec,
    travelSec: opts.travelSec ?? 2.5,
    tailSec: opts.tailSec ?? 6,
  });
  // runScenario と同じループ。ただし**通過ラインを踏んだ瞬間**も拾う
  //（「この演出はラインで出たのか、取り逃して離脱時に出たのか」は最も間違えやすい所）。
  const runner = createShowRunner(cfg);
  const tick = runner.config.tickMs;
  const endMs = walk.samples[walk.samples.length - 1].tMs;
  const trace = [];
  for (let tMs = walk.samples[0].tMs; tMs <= endMs; tMs += tick) {
    const p = sampleAt(walk.samples, tMs);
    for (const e of runner.step(tMs, p.x, p.z)) trace.push(e);
    runner.lineStates.forEach((st, i) => {
      if (st.crossed) trace.push({ kind: 'line', t: tMs, a: i, b: -1, id: '', flag: false });
    });
  }
  return { cfg, meta, walk, trace };
}

/** トレースを人が読める行に落とす。 */
export function formatTrace(trace, cfg, meta, state) {
  const cams = (state.cameras || []).map((c, i) => c.id || `#${i}`);
  const camName = (i) => (i >= 0 && i < cams.length ? `カメラ ${cams[i]}` : (i < 0 ? '—' : `#${i}`));
  const takeName = (id) => {
    const t = (meta.takes || []).find((x) => x.id === id);
    return t && t.name ? `${id}「${t.name}」` : id;
  };
  const lines = [];
  for (const e of trace) {
    const t = fmtT(e.t);
    switch (e.kind) {
      case 'lap': lines.push(`${t}  ── ${e.a} 周目 ──`); break;
      case 'seg': lines.push(`${t}  区間 ${e.a}周目 / ${camName(e.b)}`); break;
      case 'zone': break;                                   // seg と同時に出るので省く
      case 'line': {
        const l = (meta.lines || [])[e.a];
        lines.push(`${t}    ┈ ライン通過「${(l && (l.label || l.id)) || e.a}」`);
        break;
      }
      case 'screen': lines.push(`${t}    画面 → ${camName(e.a)}`); break;
      case 'take': lines.push(`${t}    ▶ 演出 ${takeName(e.id)}`); break;
      case 'step': {
        const st = ((meta.takes || []).find((x) => x.id === e.id) || {}).steps || [];
        const s = st[e.a];
        const what = s
          ? `${s.source}${s.camera >= 0 ? `(${camName(s.camera)})` : ''}${s.cueId ? ` + ${s.cueId}` : ''}`
            + `${s.recLap ? ` [${s.recLap}周目の録画]` : ''} ${s.durSec >= 0 ? `${s.durSec}s` : '尺不明'}`
          : '';
        lines.push(`${t}      カット${e.a + 1}: ${what}`);
        break;
      }
      case 'end': lines.push(`${t}    ■ 演出おわり ${takeName(e.id)} → ${camName(e.a)}${e.flag ? '（強制終了 = watchdog）' : ''}`); break;
      case 'drop': lines.push(`${t}    ⚠ 出ないまま終わった: ${takeName(e.id)} — ${dropText(e.a)}`); break;
      default: break;
    }
  }
  return lines;
}

/** 演出ごとの発火状況（出た / 出なかった）。設計の答え合わせはこの表で行う。 */
export function takeReport(trace, meta) {
  const fired = new Map();
  const dropped = new Map();
  for (const e of trace) {
    if (e.kind === 'take') fired.set(e.id, (fired.get(e.id) || 0) + 1);
    if (e.kind === 'drop') dropped.set(e.id, dropText(e.a));
  }
  return (meta.takes || []).map((t) => ({
    id: t.id,
    name: t.name,
    lap: t.lap,
    camera: t.camera,
    at: t.at,
    fired: fired.get(t.id) || 0,
    dropped: dropped.get(t.id) || '',
  }));
}

export { DROP_CODE };
