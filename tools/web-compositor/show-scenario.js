// show.json（卓の現在設定）→ シミュレータの入力（scenario-engine の cfg）へ変換する（DOM 非依存）。
//   移植元（**C# が正**）:
//     TakeRunner.SetTakes / BuildStepDurations   … 区間 takes → TakeRunnerLogic.Def
//     ZoneLayoutApplier                          … layout → ゾーン + hysteresis クランプ
//     SwitchDirectorLogic.ResolveTiming          … control の present 判定（0/未指定 = コード既定）
//
// 変換で「卓では決まらない」ものは黙って捏造せず warnings に出す（シミュレータが嘘をついたら価値はマイナス）。

import { normalizeTimelineV3, TAKE } from './timeline-model.js';
import {
  zonesFromLayout, linesFromLayout, cameraAtPoint, lineMid, lineLength,
  parseLineDir, LINE_MIN_LENGTH_M, HEAD_Y,
} from './zone-layout.js';
import {
  resolveTiming, DEFAULT_DWELL_SEC, DEFAULT_COOLDOWN_SEC, FALLBACK_STEP_DUR_SEC, DEFAULT_TICK_MS,
  WAIT_ZONE_CHANGE, line as makeLine,
} from './scenario-engine.js';

const num = (v, def) => (Number.isFinite(v) ? v : def);

/** TakeSchema.NormalizeSource（未知は live へ倒す）。 */
function normalizeSource(source) {
  return (source === TAKE.SRC_LIVE || source === TAKE.SRC_INHERIT
    || source === TAKE.SRC_CLIP || source === TAKE.SRC_STILL
    || source === TAKE.SRC_PLATE) ? source : TAKE.SRC_LIVE;
}

/**
 * 表示用の source（`rec` を live へ潰さない）。卓は端末内録画を持たないので、録画カットは
 * 「そのカメラのライブで代用して見せる」— 見た目は live と同じでよいが、**何のカットかは正しく言う**。
 */
export function displaySource(step) {
  return step && step.source === TAKE.SRC_REC ? TAKE.SRC_REC : normalizeSource(step && step.source);
}

/**
 * 「この画は step.camera の構図そのものか」＝ CG 人形を重ねてパースが合うか
 * （TakeSchema.MatchesCameraPerspective の対）。録画と無人プレートだけが該当する。
 */
export function matchesCameraPerspective(source) {
  return source === TAKE.SRC_REC || source === TAKE.SRC_PLATE;
}

/** -1 = 素材定義から継承（TakeSchema.Inherit）。 */
const inherit = (stepValue, cueValue) => (stepValue < 0 ? cueValue : stepValue);

/** カットが実際に読む素材 URL（カット自身の素材 > 重ねる素材）。無ければ空文字。 */
export function stepAssetUrl(step, cue) {
  const source = normalizeSource(step.source);
  if ((source === TAKE.SRC_CLIP || source === TAKE.SRC_STILL || source === TAKE.SRC_PLATE)
      && step.assetUrl) return step.assetUrl;
  return (cue && cue.sourceUrl) || '';
}

/**
 * カット 1 つの尺を解く。返り値 { durSec, kind }。
 *   kind='exact'     … 秒指定（実機と同じ）
 *   kind='fallback'  … untilClipEnd だが素材が無い → Unity と同じ 4s で畳む
 *   kind='measured'  … untilClipEnd + 素材の実尺を実測（trim を適用。実機と同じ終わり方）
 *   kind='estimated' … untilClipEnd + cue の trim から推定（実尺が測れない時の次善）
 *   kind='unknown'   … untilClipEnd で尺が分からない → -1（watchdog 任せ。実機とズレうる）
 *   kind='untilZone' … untilZoneChange → -2（体験者が次の区間へ移るまで。シミュレータも同じ所で畳む）
 *
 * getDuration(url) は「実測できていれば秒、まだ/測れないなら null」を返す関数（任意）。
 * ブラウザ（media-duration.js）だけが供給できるので、node テストでは未指定 = 従来どおり推定。
 *
 * getRecSeconds(step) は「録画」カットの尺（＝体験者がその区間に居た時間）。卓は録画そのものを
 * 持たないので、実測滞在時間の集計から推定する（任意）。**重ねる素材の長さで代用してはいけない** —
 * 録画カットの下地は録画で、cue はその上のマスクにすぎない。
 */
export function resolveStepDuration(step, cue, getDuration, getRecSeconds) {
  // 「次にカメラが切り替わるまで」は秒に落とさない。**シミュレータは実機と同じ所で畳める**
  // （ゾーン確定が卓にもある）ので、推定に化けさせると卓だけが違う所で終わる。
  if (step.durKind === TAKE.DUR_UNTIL_ZONE_CHANGE) {
    return { durSec: WAIT_ZONE_CHANGE, kind: 'untilZone' };
  }
  // 「この線を横切るまで」は卓に体験者の実位置が無い（歩かせるシミュレータの座標はあるが、
  // 横断判定を二重実装すると必ず実機とずれる）。**秒へ落とさず未確定として出す**
  // — 推定に化けさせると、卓だけが違う所でカットを畳んで嘘のリボンになる。
  if (step.durKind === TAKE.DUR_UNTIL_LINE || step.durKind === TAKE.DUR_UNTIL_MARK) {
    return { durSec: FALLBACK_STEP_DUR_SEC, kind: 'unknown' };
  }
  if (step.durKind !== TAKE.DUR_UNTIL_CLIP_END) {
    return { durSec: step.durSec > 0 ? step.durSec : FALLBACK_STEP_DUR_SEC, kind: 'exact' };
  }
  // 「録画」カットは素材 URL を持たない。**残すのは区間の末尾 record.tailSec 秒だけ**なので、
  // 供給側（ribbon）が設定を知っていれば尺は確定する（kind='exact' ＝ ≈ を付けない）。
  // 供給が無い呼び出し（node テスト等）だけ既定尺で仮置きする
  // （-1 にすると卓のシミュレータが watchdog まで走って嘘をつく）。
  if (step.source === TAKE.SRC_REC) {
    const sec = typeof getRecSeconds === 'function' ? getRecSeconds(step) : null;
    if (Number.isFinite(sec) && sec > 0) return { durSec: sec, kind: 'exact' };
    return { durSec: FALLBACK_STEP_DUR_SEC, kind: 'estimated' };
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

/**
 * そのカットの**構図を決めるカメラ**。live は画面ごとそのカメラへ動き、rec / plate は画面は動かないが
 * 「そのカメラで撮った画」なので較正がそのまま効く（＝ CG 人形を重ねられる）。
 * clip / still は撮影条件が分からないので -1（どのカメラの較正も当てられない）。
 */
export function stepCamera(step) {
  const source = displaySource(step);
  const usesCamera = source === TAKE.SRC_LIVE || source === TAKE.SRC_REC || source === TAKE.SRC_PLATE;
  return (usesCamera && step.camera >= 0) ? step.camera : -1;
}

/**
 * show.json 状態 → { cfg, meta }。
 *   cfg  … scenario-engine.runScenario / createShowRunner に渡す設定
 *   meta … UI 表示用（演出の見出し・カット内容・ゾーン矩形・警告）
 */
export function buildScenarioConfig(state, opts = {}) {
  const getDuration = typeof opts.getDuration === 'function' ? opts.getDuration : null;
  // 「録画」カットの尺（＝録った区間の実測滞在時間）。供給が無ければ既定尺で仮置きする。
  const getRecSeconds = typeof opts.getRecSeconds === 'function' ? opts.getRecSeconds : null;
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

  // 通過ライン（床の線分）。配列順 = Unity のスロット順（TakeRunnerLogic.Def.lineIndex が指す）。
  const lineDefs = linesFromLayout(layout);
  const lineSlot = new Map(lineDefs.map((l, i) => [l.id, i]));
  // 線が実際に置かれているゾーン（担当カメラの著作値と食い違っていたら卓が警告する）。
  const lineZone = new Map(lineDefs.map((l) => {
    const m = lineMid(l);
    return [l.id, cameraAtPoint(zoneInfo.boxes, m.x, m.z)];
  }));
  const camName = (i) => (cams[i] && cams[i].id ? `カメラ ${cams[i].id}` : `#${i}`);
  for (const l of lineDefs) {
    if (lineLength(l) < LINE_MIN_LENGTH_M) {
      warnings.push(`ライン「${l.label || l.id}」が短すぎます（${LINE_MIN_LENGTH_M}m 未満は無効）`);
      continue;
    }
    const zone = lineZone.get(l.id);
    if (l.camera < 0) {
      warnings.push(`ライン「${l.label || l.id}」の担当カメラが未指定です（どの区間からでも使えてしまう）`);
    } else if (zone >= 0 && zone !== l.camera) {
      warnings.push(`ライン「${l.label || l.id}」は ${camName(l.camera)} 担当ですが、`
        + `線が置かれているのは ${camName(zone)} のゾーンです（体験者がそこを通る時の区間と食い違います）`);
    }
  }

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
        const d = resolveStepDuration(st, cue, getDuration, getRecSeconds);
        durs.push(d.durSec);
        metaSteps.push({
          source: displaySource(st),
          camera: stepCamera(st),
          // 「録画」カットの録画元の周。camera は rec でも「録画元カメラ」が入る
          //（normalizeSource が rec を live へ倒すため。卓はそのカメラのライブを代用表示する）が、
          // 周は camera だけでは分からないので別に持つ。録画設定との照合に要る。
          recLap: st.source === TAKE.SRC_REC ? (parseInt(st.recLap, 10) || 0) : 0,
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

      // 開始規則「このラインを通過したら」。ラインが無い / 未選択 / 別カメラ担当なら発火しない。
      const takeLabel = t.id || `L${seg.lap}C${seg.camera}#${ti}`;
      const onLine = t.at === TAKE.AT_LINE;
      let slot = -1;
      if (onLine) {
        const def = t.lineId ? lineDefs.find((l) => l.id === t.lineId) : null;
        if (!t.lineId) warnings.push(`${takeLabel} はラインが未選択です（発火しません）`);
        else if (!def) warnings.push(`${takeLabel} のライン「${t.lineId}」が layout.lines にありません（発火しません）`);
        else if (def.camera >= 0 && def.camera !== seg.camera) {
          // 実機も同じ理由で発火しない（ラインは担当カメラに紐づく）。
          warnings.push(`${takeLabel} のライン「${def.label || def.id}」は ${camName(def.camera)} 担当です`
            + `（この演出は ${camName(seg.camera)} の区間なので出ません）`);
        } else {
          slot = lineSlot.get(t.lineId);
        }
      }

      takes.push({
        lap: seg.lap,
        camera: seg.camera,
        onExit: t.at === TAKE.AT_EXIT,
        offsetSec: num(t.offsetSec, 0),
        skipWhenMissed: t.ifMissed === TAKE.MISSED_SKIP,
        once: t.once !== false,
        maxDurationSec: num(t.maxDurationSec, 0),
        yieldOnZoneChange: t.policy === TAKE.POLICY_YIELD,
        // 離脱時の演出は持ち越せない（Unity 側 ShowTakeDef.IsChainWait と同条件）。
        chainWait: t.wait === TAKE.WAIT_CHAIN && t.at !== TAKE.AT_EXIT,
        stepDurSec: durs,
        // ⚠ 使えないライン（未選択 / layout に無い / 別カメラ担当）でも onLine は下ろさない。
        //   下ろすと時刻トリガー扱いになり offsetSec(=0) で**区間進入と同時に発火**してしまい、
        //   すぐ上で「発火しません」と警告した当の演出を卓が再生する（実機は出ない = 卓が嘘をつく）。
        //   lineIndex=-1 は「決して due にならない枠」＝ Unity の TakeRunner と同じ扱い
        //   （ifMissed=fireOnExit なら離脱時に出る点まで一致する）。
        onLine,
        lineIndex: slot,
      });
      takeIds.push(t.id || '');
      stepCameras.push(metaSteps.map((m) => m.camera));
      metaTakes.push({
        id: t.id || `#${takes.length - 1}`,
        name: t.name || '',
        lap: seg.lap,
        camera: seg.camera,
        at: t.at === TAKE.AT_EXIT || t.at === TAKE.AT_LINE ? t.at : TAKE.AT_ENTER,
        offsetSec: num(t.offsetSec, 0),
        lineId: onLine ? (t.lineId || '') : '',
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
    lines: lineDefs.map((l) => makeLine(l.x1, l.z1, l.x2, l.z2, parseLineDir(l.dir), l.camera)),
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
      // 通過ライン（描画・注釈用。zone = 線が置かれているゾーンのカメラ / -1 = 未割当）。
      lines: lineDefs.map((l) => ({ ...l, zone: lineZone.get(l.id) ?? -1 })),
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
    lines: (cfg.lines || []).map((l) => ({
      x1: round(l.ax, 4), z1: round(l.az, 4), x2: round(l.bx, 4), z2: round(l.bz, 4),
      dir: l.dir | 0, camera: Number.isInteger(l.camera) ? l.camera : -1, defined: l.defined !== false,
    })),
    takes: cfg.takes.map((t, i) => ({
      id: cfg.takeIds[i] || '',
      lap: t.lap, camera: t.camera, onExit: t.onExit,
      offsetSec: t.offsetSec, skipWhenMissed: t.skipWhenMissed, once: t.once,
      maxDurationSec: t.maxDurationSec, yieldOnZoneChange: t.yieldOnZoneChange,
      chainWait: !!t.chainWait,
      stepDurSec: t.stepDurSec.slice(),
      stepCameras: (cfg.stepCameras && cfg.stepCameras[i]) ? cfg.stepCameras[i].slice() : [],
      onLine: !!t.onLine, lineIndex: Number.isInteger(t.lineIndex) ? t.lineIndex : -1,
    })),
    samples: samples.map((p) => ({ tMs: p.tMs, x: round(p.x, 3), z: round(p.z, 3) })),
  };
}

const round = (v, digits) => {
  const m = 10 ** digits;
  return Math.round((Number.isFinite(v) ? v : 0) * m) / m;
};
