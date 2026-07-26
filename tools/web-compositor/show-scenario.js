// show.json（卓の現在設定）→ シミュレータの入力（scenario-engine の cfg）へ変換する（DOM 非依存）。
//   移植元（**C# が正**）:
//     TakeRunner.SetTakes / BuildStepDurations   … 区間 takes → TakeRunnerLogic.Def
//     ZoneLayoutApplier                          … layout → ゾーン + hysteresis クランプ
//     SwitchDirectorLogic.ResolveTiming          … control の present 判定（0/未指定 = コード既定）
//
// 変換で「卓では決まらない」ものは黙って捏造せず warnings に出す（シミュレータが嘘をついたら価値はマイナス）。

import { normalizeTimelineV3, TAKE } from './timeline-model.js';
import { zonesFromLayout, HEAD_Y } from './zone-layout.js';
import {
  resolveTiming, DEFAULT_DWELL_SEC, DEFAULT_COOLDOWN_SEC, FALLBACK_STEP_DUR_SEC, DEFAULT_TICK_MS,
} from './scenario-engine.js';

const num = (v, def) => (Number.isFinite(v) ? v : def);

/** TakeSchema.NormalizeSource（未知は live へ倒す）。 */
function normalizeSource(source) {
  return (source === TAKE.SRC_LIVE || source === TAKE.SRC_INHERIT
    || source === TAKE.SRC_CLIP || source === TAKE.SRC_STILL) ? source : TAKE.SRC_LIVE;
}

/**
 * 表示用の source（`rec` を live へ潰さない）。卓は端末内録画を持たないので、録画カットは
 * 「そのカメラのライブで代用して見せる」— 見た目は live と同じでよいが、**何のカットかは正しく言う**。
 */
export function displaySource(step) {
  return step && step.source === TAKE.SRC_REC ? TAKE.SRC_REC : normalizeSource(step && step.source);
}

/** -1 = 素材定義から継承（TakeSchema.Inherit）。 */
const inherit = (stepValue, cueValue) => (stepValue < 0 ? cueValue : stepValue);

/** カットが実際に読む素材 URL（カット自身の素材 > 重ねる素材）。無ければ空文字。 */
export function stepAssetUrl(step, cue) {
  const source = normalizeSource(step.source);
  if ((source === TAKE.SRC_CLIP || source === TAKE.SRC_STILL) && step.assetUrl) return step.assetUrl;
  return (cue && cue.sourceUrl) || '';
}

/**
 * カット 1 つの尺を解く。返り値 { durSec, kind }。
 *   kind='exact'     … 秒指定（実機と同じ）
 *   kind='fallback'  … untilClipEnd だが素材が無い → Unity と同じ 4s で畳む
 *   kind='measured'  … untilClipEnd + 素材の実尺を実測（trim を適用。実機と同じ終わり方）
 *   kind='estimated' … untilClipEnd + cue の trim から推定（実尺が測れない時の次善）
 *   kind='unknown'   … untilClipEnd で尺が分からない → -1（watchdog 任せ。実機とズレうる）
 *
 * getDuration(url) は「実測できていれば秒、まだ/測れないなら null」を返す関数（任意）。
 * ブラウザ（media-duration.js）だけが供給できるので、node テストでは未指定 = 従来どおり推定。
 */
export function resolveStepDuration(step, cue, getDuration) {
  if (step.durKind !== TAKE.DUR_UNTIL_CLIP_END) {
    return { durSec: step.durSec > 0 ? step.durSec : FALLBACK_STEP_DUR_SEC, kind: 'exact' };
  }
  const url = stepAssetUrl(step, cue);
  if (!url) {
    // 素材が無いのに untilClipEnd → Unity も既定尺で畳む（TakeRunner.BeginStep）。
    return { durSec: FALLBACK_STEP_DUR_SEC, kind: 'fallback' };
  }
  const trimStart = Math.max(0, inherit(num(step.trimStartSec, -1), num(cue && cue.trimStart, 0)));
  const trimEnd = inherit(num(step.trimEndSec, -1), num(cue && cue.trimEnd, 0));
  const measured = typeof getDuration === 'function' ? getDuration(url) : null;
  if (Number.isFinite(measured) && measured > 0) {
    // 実機は「trimEnd（あれば）か素材の終わり」の早い方で畳む。
    const end = trimEnd > trimStart ? Math.min(trimEnd, measured) : measured;
    return { durSec: Math.max(0.2, end - trimStart), kind: 'measured' };
  }
  if (trimEnd > trimStart) return { durSec: trimEnd - trimStart, kind: 'estimated' };
  return { durSec: -1, kind: 'unknown' };
}

/** カットが切り替えるカメラ（live かつ camera>=0 のときだけ画面が動く）。 */
export function stepCamera(step) {
  return (normalizeSource(step.source) === TAKE.SRC_LIVE && step.camera >= 0) ? step.camera : -1;
}

/**
 * show.json 状態 → { cfg, meta }。
 *   cfg  … scenario-engine.runScenario / createShowRunner に渡す設定
 *   meta … UI 表示用（演出の見出し・カット内容・ゾーン矩形・警告）
 */
export function buildScenarioConfig(state, opts = {}) {
  const getDuration = typeof opts.getDuration === 'function' ? opts.getDuration : null;
  const s = state || {};
  const cams = s.cameras || [];
  const cues = s.cues || [];
  const control = s.control || {};
  const layout = s.layout || {};
  const warnings = [];

  const zoneInfo = zonesFromLayout(layout);
  if (zoneInfo.warning) warnings.push(zoneInfo.warning);

  const order = (layout.course && Array.isArray(layout.course.order)) ? layout.course.order.slice() : [];
  if (!order.length) warnings.push('周回コース（layout.course.order）が空です。周回・区間が進みません。');

  const tl = normalizeTimelineV3(s.timeline || { rev: 1, segments: [] });
  const takes = [];
  const takeIds = [];
  const stepCameras = [];
  const metaTakes = [];

  tl.segments.forEach((seg) => {
    (seg.takes || []).forEach((t, ti) => {
      const steps = t.steps || [];
      const durs = [];
      const metaSteps = [];
      steps.forEach((st) => {
        const cue = st.cueId ? cues.find((c) => c.id === st.cueId) : null;
        if (st.cueId && !cue) warnings.push(`cue 未解決: ${st.cueId}（${t.id || `L${seg.lap}C${seg.camera}#${ti}`}）`);
        const d = resolveStepDuration(st, cue, getDuration);
        durs.push(d.durSec);
        metaSteps.push({
          source: displaySource(st),
          camera: stepCamera(st),
          cueId: st.cueId || '',
          assetUrl: st.assetUrl || '',
          // 実際に画面へ出る素材（カット自身 > 重ねる素材）。卓のプレビューが読む。
          playUrl: stepAssetUrl(st, cue),
          maskUrl: (cue && cue.maskUrl) || '',
          // -1 = 素材定義から継承（TakeSchema.Inherit と同じ解決を先にやっておく）。
          strength: inherit(num(st.strength, -1), num(cue && cue.strength, 1)),
          fadeInSec: inherit(num(st.fadeInSec, -1), num(cue && cue.fadeIn, 0.5)),
          trimStartSec: Math.max(0, inherit(num(st.trimStartSec, -1), num(cue && cue.trimStart, 0))),
          trimEndSec: inherit(num(st.trimEndSec, -1), num(cue && cue.trimEnd, 0)),
          durSec: d.durSec,
          durKind: d.kind,
          transition: st.transition || TAKE.TRANS_DIP,
          hasPost: !!st.hasPost,
          post: st.hasPost && st.post ? { ...st.post } : null,
        });
      });

      takes.push({
        lap: seg.lap,
        camera: seg.camera,
        onExit: t.at === TAKE.AT_EXIT,
        offsetSec: num(t.offsetSec, 0),
        skipWhenMissed: t.ifMissed === TAKE.MISSED_SKIP,
        once: t.once !== false,
        maxDurationSec: num(t.maxDurationSec, 0),
        yieldOnZoneChange: t.policy === TAKE.POLICY_YIELD,
        stepDurSec: durs,
      });
      takeIds.push(t.id || '');
      stepCameras.push(metaSteps.map((m) => m.camera));
      metaTakes.push({
        id: t.id || `#${takes.length - 1}`,
        name: t.name || '',
        lap: seg.lap,
        camera: seg.camera,
        at: t.at === TAKE.AT_EXIT ? TAKE.AT_EXIT : TAKE.AT_ENTER,
        offsetSec: num(t.offsetSec, 0),
        ifMissed: t.ifMissed === TAKE.MISSED_SKIP ? TAKE.MISSED_SKIP : TAKE.MISSED_FIRE_ON_EXIT,
        policy: t.policy === TAKE.POLICY_YIELD ? TAKE.POLICY_YIELD : TAKE.POLICY_HOLD,
        once: t.once !== false,
        steps: metaSteps,
      });
      if (!steps.length) warnings.push(`カットが 1 つも無い演出: ${t.id || `L${seg.lap}C${seg.camera}#${ti}`}（発火しません）`);
    });
  });

  const estimated = metaTakes.filter((t) => t.steps.some((st) => st.durKind === 'estimated'));
  if (estimated.length) {
    warnings.push(`尺が「素材の終わりまで」のカットを trim から推定しています（${estimated.map((t) => t.id).join(', ')}）。実機は素材の実尺で終わります。`);
  }
  const unknown = metaTakes.filter((t) => t.steps.some((st) => st.durKind === 'unknown'));
  if (unknown.length) {
    warnings.push(`尺が「素材の終わりまで」で trim も無いカットがあります（${unknown.map((t) => t.id).join(', ')}）。卓では尺が分からないため watchdog（${45}s）まで走ります。`);
  }

  const cfg = {
    zones: zoneInfo.boxes,
    hysteresisShrink: zoneInfo.hysteresisShrink,
    keepLastWhenOutside: true,
    headY: HEAD_Y,
    dwellSec: resolveTiming(num(control.minDwellSec, 0), DEFAULT_DWELL_SEC),
    cooldownSec: resolveTiming(num(control.switchCooldownSec, 0), DEFAULT_COOLDOWN_SEC),
    courseOrder: order,
    takes,
    takeIds,
    stepCameras,
    startCamera: order.length ? order[0] : 0,
    tickMs: DEFAULT_TICK_MS,
  };

  // 画像加工（post）の解決材料。実機の優先順位は カット > 区間 > カメラ > global。
  const segmentPosts = {};
  tl.segments.forEach((seg) => {
    if (seg.hasPost && seg.post) segmentPosts[`${seg.lap}:${seg.camera}`] = { ...seg.post };
  });

  return {
    cfg,
    meta: {
      cameras: cams.map((c, i) => ({ index: i, id: c.id || `#${i}`, post: c.post || null })),
      segmentPosts,
      globalPost: s.post || null,
      courseOrder: order,
      zoneRects: zoneInfo.rects,
      zoneSource: zoneInfo.source,
      takes: metaTakes,
      warnings,
      layoutRev: num(layout.rev, 0),
      timelineRev: num(tl.rev, 0),
    },
  };
}

/**
 * 記録した歩き（samples）+ 現在の設定を、Unity の golden fixture と**同じ JSON 形式**へ落とす。
 * これをそのまま Assets/Tests/Fixtures/ に置けば C# 側 ShowScenarioRunner に食わせられる
 * （現場で見つけた歩き方を「この歩き」として固定するための出口）。
 */
export function serializeScenario(cfg, samples, extra = {}) {
  return {
    name: extra.name || '',
    savedAt: extra.savedAt || new Date().toISOString(),
    note: extra.note || '',
    tickMs: cfg.tickMs,
    dwellSec: cfg.dwellSec,
    cooldownSec: cfg.cooldownSec,
    hysteresisShrink: round(cfg.hysteresisShrink, 4),
    headY: cfg.headY,
    startCamera: cfg.startCamera,
    courseOrder: cfg.courseOrder.slice(),
    zones: cfg.zones.map((b) => ({
      cx: round(b.cx, 4), cy: round(b.cy, 4), cz: round(b.cz, 4),
      hx: round(b.hx, 4), hy: round(b.hy, 4), hz: round(b.hz, 4),
      yawDeg: 0, camera: b.camera, priority: b.priority,
    })),
    takes: cfg.takes.map((t, i) => ({
      id: cfg.takeIds[i] || '',
      lap: t.lap, camera: t.camera, onExit: t.onExit,
      offsetSec: t.offsetSec, skipWhenMissed: t.skipWhenMissed, once: t.once,
      maxDurationSec: t.maxDurationSec, yieldOnZoneChange: t.yieldOnZoneChange,
      stepDurSec: t.stepDurSec.slice(),
      stepCameras: (cfg.stepCameras && cfg.stepCameras[i]) ? cfg.stepCameras[i].slice() : [],
    })),
    samples: samples.map((p) => ({ tMs: p.tMs, x: round(p.x, 3), z: round(p.z, 3) })),
  };
}

const round = (v, digits) => {
  const m = 10 ** digits;
  return Math.round((Number.isFinite(v) ? v : 0) * m) / m;
};
