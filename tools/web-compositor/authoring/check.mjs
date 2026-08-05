// 「実機だけが黙って落とす」条件を、書き込む前に機械で潰す。
//
//   実機（TakeRunner / ShowCgLayer / SegmentRecorder）は、素材が無い・録っていない・姿勢が無い
//   といった条件のカットを**警告ログだけ出して飛ばす**。現場でそれに気づく手段は無い。
//   ここは卓の「本番前チェック」と同じ判定を node 側に置いたもの。
//
//   判定の出どころ（**C# が正**）:
//     TakeRunner.OpenRecording / BeginStep … 録画・素材が解決できないカットは飛ばす
//     ShowCgLayer.ResolveCalib             … 姿勢も較正も無いカメラでは人形を出さない
//     ShowRunLogic                          … totalLaps を超えた周には到達しない

import fs from 'node:fs';
import path from 'node:path';
import { normalizeTimelineV3, TAKE } from '../timeline-model.js';
import { recordCoverage } from '../record-model.js';
import { buildScenarioConfig } from '../show-scenario.js';
import { isSegmentReachable } from '../run-model.js';
import { ROOT } from './show-api.mjs';

const LOCAL_DIRS = ['masks', 'captures', 'recordings', 'testassets', 'audio', 'static-inputs'];

/**
 * ローカル URL（/testassets/... 等）が実ファイルとして在るか。外部 URL は判定対象外。
 * 卓は cue 保存時に各セグメントを percent-encode する（common.js の encPath）ので、必ず戻してから探す。
 */
function localAssetMissing(url) {
  if (!url || !url.startsWith('/')) return false;
  let rel = url.split('?')[0].replace(/^\//, '');
  try { rel = decodeURIComponent(rel); } catch { /* 壊れた % 列はそのまま探す */ }
  const top = rel.split('/')[0];
  if (!LOCAL_DIRS.includes(top)) return false;
  return !fs.existsSync(path.join(ROOT, rel));
}

/**
 * show.json 全体を検査する。返り値 { errors, warns, info }。
 *   errors … 実機で**黙って落ちる**もの。書き込む前に直す
 *   warns  … 出るが意図とずれうるもの（尺・歩速・順番）
 */
export function checkShow(state, opts = {}) {
  const dwellSec = opts.dwellSec ?? 10;
  const errors = [];
  const warns = [];
  const info = [];
  const err = (m) => errors.push(m);
  const warn = (m) => warns.push(m);

  const cams = state.cameras || [];
  const camName = (i) => (cams[i] ? `カメラ ${cams[i].id || i}` : `#${i}`);
  const cues = state.cues || [];
  const actors = state.actors || [];
  const lines = (state.layout && state.layout.lines) || [];
  const tracks = state.bgmTracks || [];
  const tl = normalizeTimelineV3(state.timeline || { rev: 1, segments: [] });
  const totalLaps = (state.run && state.run.totalLaps > 0) ? state.run.totalLaps : 3;
  const courseOrder = (state.layout && state.layout.course && Array.isArray(state.layout.course.order))
    ? state.layout.course.order : [];

  // 卓のシミュレータが出す警告（cue 未解決・ラインの担当違い・尺不明 …）をそのまま引き継ぐ。
  const { meta } = buildScenarioConfig(state, { getRecSeconds: () => dwellSec });
  for (const w of meta.warnings || []) warn(w);

  // 録画（rec）カット: 録る設定になっているか。
  const cov = recordCoverage(state, tl.segments, camName);
  for (const r of cov.bad) err(`${r.where} の「${r.take}」: ${r.reason} — ${r.fix}`);
  if (cov.refs.length) {
    info.push(`録画カット ${cov.refs.length} 枚 / 録る周 = [${[...cov.laps].join(', ')}]`);
  }

  for (const seg of tl.segments) {
    const where = `${seg.lap}周目 / ${camName(seg.camera)}`;
    // ⚠ 到達可能かの式は **run-model.isSegmentReachable が単一の正**（ShowRunReach / analyze-xp-log と対）。
    //   ここに `lap > totalLaps` と書くと**帰りの A**（lap=totalLaps+1 の order[0]）を到達不能と誤判定し、
    //   実機では出る演出に ❌ が出る。実際に出ていた。
    if (!isSegmentReachable(seg.lap, seg.camera, totalLaps, courseOrder)) {
      err(`${where}: この体験は ${totalLaps} 周で終わるので、この区間には到達しません（演出が出ません）`);
    }
    if (seg.camera < 0 || seg.camera >= cams.length) err(`${where}: カメラ index が範囲外です`);

    const exits = (seg.takes || []).filter((t) => t.at === TAKE.AT_EXIT);
    if (exits.length > 1) {
      err(`${where}: 「離脱時」の演出が ${exits.length} 本あります（2 本目は配列順で必ず負けて出ません）`);
    }

    for (const t of seg.takes || []) {
      const label = `${where} の「${t.name || t.id}」`;
      if (!t.steps || !t.steps.length) { err(`${label}: カットが 1 枚もありません`); continue; }

      if (t.at === TAKE.AT_LINE) {
        const def = lines.find((l) => l.id === t.lineId);
        if (!t.lineId) err(`${label}: 開始規則がライン通過なのにラインが未選択です`);
        else if (!def) err(`${label}: ライン「${t.lineId}」が layout.lines にありません`);
        else if (Number.isInteger(def.camera) && def.camera >= 0 && def.camera !== seg.camera) {
          err(`${label}: ライン「${def.label || def.id}」は ${camName(def.camera)} 担当なので、この区間では発火しません`);
        }
      }

      if (t.hasBgm && t.bgm && t.bgm.action === 'play') {
        const id = t.bgm.trackId;
        if (id && id !== '__default__' && !tracks.some((x) => x.id === id)) {
          err(`${label}: BGM トラック「${id}」が bgmTracks にありません`);
        }
      }

      let total = 0;
      // 「この演出は区間の終わりで必ず畳まれるか」。畳まれるなら滞在を食い潰す心配は無い:
      //   policy=yield          … 体験者が区間を移った瞬間に打ち切る
      //   untilZoneChange のカット … その所で終わる（＝この演出は区間より長くなりようがない）
      let boundedBySegment = t.policy === TAKE.POLICY_YIELD;
      t.steps.forEach((s, i) => {
        if (s.durKind === TAKE.DUR_UNTIL_ZONE_CHANGE) boundedBySegment = true;
        const sLabel = `${label} カット${i + 1}`;
        // カットが自分でカメラを指す集合（live / 録画 / 無人プレート）。それ以外は区間のカメラを継ぐ。
        const usesCam = s.source === TAKE.SRC_LIVE || s.source === TAKE.SRC_REC
          || s.source === TAKE.SRC_PLATE;
        const screenCam = usesCam && s.camera >= 0 ? s.camera : seg.camera;
        total += s.durKind === TAKE.DUR_SEC ? Math.max(0, s.durSec) : dwellSec;

        // 実機（TakeRunner.BeginStep）は camera<0 の live / rec カットを**画面に触らず飛ばす**。
        // 既定値が -1 なので「カメラを選び忘れた」形がそのまま通ってしまう。
        if ((s.source === TAKE.SRC_LIVE || s.source === TAKE.SRC_REC)
          && (!Number.isInteger(s.camera) || s.camera < 0 || s.camera >= cams.length)) {
          err(`${sLabel}: 映すカメラが決まっていません（camera=${s.camera}。このカットは飛ばされます）`);
        }
        // 無人プレートはカメラを指さないと人形の構図が決まらない（実機は概算姿勢にも落とせない）。
        if (s.source === TAKE.SRC_PLATE
          && (!Number.isInteger(s.camera) || s.camera < 0 || s.camera >= cams.length)) {
          err(`${sLabel}: どのカメラの無人の部屋かが決まっていません（camera=${s.camera}）`);
        }

        // 重ねる素材（cue）。マスクはそのカメラの構図に焼かれているので、別カメラの cue は合わない。
        if (s.cueId) {
          const cue = cues.find((c) => c.id === s.cueId);
          if (!cue) err(`${sLabel}: 素材「${s.cueId}」が cues にありません`);
          else {
            const camId = cams[screenCam] && cams[screenCam].id;
            if (cue.camera && camId && cue.camera !== camId) {
              err(`${sLabel}: 素材「${cue.name || cue.id}」はカメラ ${cue.camera} 用です`
                + `（このカットの画面は ${camName(screenCam)}。マスクの位置が合いません）`);
            }
            if (localAssetMissing(cue.sourceUrl)) err(`${sLabel}: 素材のファイルがありません（${cue.sourceUrl}）`);
            if (localAssetMissing(cue.maskUrl)) err(`${sLabel}: マスクのファイルがありません（${cue.maskUrl}）`);
          }
        }

        if (s.source === TAKE.SRC_CLIP || s.source === TAKE.SRC_STILL
          || s.source === TAKE.SRC_PLATE) {
          const cue = s.cueId ? cues.find((c) => c.id === s.cueId) : null;
          const url = s.assetUrl || (cue && cue.sourceUrl) || '';
          if (!url) err(`${sLabel}: 素材が指定されていません（実機はこのカットを飛ばします）`);
          else if (localAssetMissing(url)) err(`${sLabel}: 素材のファイルがありません（${url}）`);
        }

        if (s.cg) {
          if (!actors.some((a) => a.id === s.cg)) err(`${sLabel}: CG 人形「${s.cg}」が actors にありません`);
          // 一般の素材（clip / still）は撮影条件が分からないので人形が必ず浮く。
          // 録画と無人プレートは step.camera で撮った画なので、そのカメラの較正がそのまま効く。
          if (s.source === TAKE.SRC_CLIP || s.source === TAKE.SRC_STILL) {
            err(`${sLabel}: 素材のカットには CG 人形を出せません（実機も出しません）`);
          }
          const c = cams[screenCam];
          const posed = !!(c && (c.pose || (c.calib && c.calib.fxPx > 1)));
          if (!posed) {
            err(`${sLabel}: ${camName(screenCam)} は姿勢も較正もありません（実機では人形が出ません）`);
          }
        }

        if (s.durKind === TAKE.DUR_SEC && !(s.durSec > 0)) {
          warn(`${sLabel}: 尺が 0 秒です（実機は既定 ${TAKE.FALLBACK_STEP_DUR_SEC}s で畳みます）`);
        }
      });

      if (t.at === TAKE.AT_ENTER && !boundedBySegment && t.offsetSec + total > dwellSec) {
        warn(`${label}: 開始 +${t.offsetSec}s ＋ 尺 ${total.toFixed(1)}s が想定滞在 ${dwellSec}s を超えます`
          + '（体験者が先に次の区間へ行き、画面だけ前の演出が続きます）');
      }
      if (total > (t.maxDurationSec > 0 ? t.maxDurationSec : TAKE.DEFAULT_MAX_DURATION_SEC)) {
        err(`${label}: カットの合計 ${total.toFixed(1)}s が上限を超えます（途中で強制終了します）`);
      }
    }
  }

  return { errors, warns, info };
}

/** コンソール向けの整形。 */
export function formatCheck(r) {
  const out = [];
  for (const m of r.errors) out.push(`❌ ${m}`);
  for (const m of r.warns) out.push(`⚠ ${m}`);
  for (const m of r.info) out.push(`· ${m}`);
  if (!r.errors.length) out.push('✅ 実機で黙って落ちる条件はありません');
  return out;
}
