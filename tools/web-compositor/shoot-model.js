// 当日の素材撮り（2 周目 B・C の接近）の判定。DOM 非依存の純関数だけを置く。
//
// ⚠ ショットの定義は**ここ（コード）に持つ。show.json の cue には足さない**。
//   cue エディタは保存時にフィールドの白名簿でオブジェクトを作り直すので（cue-editor.js の
//   saveCue）、cue へ足した未知キーは**次に誰かが cue を保存した瞬間に消える**。
//
// ⚠ ただし「その素材の頭が何秒画に出るか」は **timeline から機械導出する**。
//   著作を変えたら要求も追従するので、手順書と実装が黙って食い違わない。
//
// 移植元は無い（卓だけの機能）。ただし cue → 実機の契約は rules/streaming.md が正。

/**
 * ショットの並び順は撮る順（onsite-checklist §0）。
 *
 * ⚠ **偽ライブ（fake_live_C）は 0104 で廃止した。** 差し込みは人形視点だけで、
 *   2-C の連続演出中はライブに戻らない（戻るのは追いつきの後・黒マスクと同時）。
 *   当日撮るのはこの 5 本だけ。
 */
export const SHOTS = [
  {
    cueId: 'pov_0', label: '2 周目 C の予備動作', dev: 'pov',
    hint: '部屋の隅からの遠景。人物は入れない。高さ 40cm で C の立ち位置へ正対',
    recSec: 2,
  },
  {
    cueId: 'pov_1', label: '人形視点 ① 遠い', dev: 'pov',
    hint: '遠い。白衣は米粒か不在。高さ 40cm で正対', recSec: 3,
  },
  {
    cueId: 'pov_2', label: '人形視点 ② 近い', dev: 'pov',
    hint: '近い。歩きの揺れが出る速さで', recSec: 3,
  },
  {
    cueId: 'pov_3', label: '人形視点 ③ すぐそこ', dev: 'pov',
    hint: 'すぐそこ。白衣の一部が画面に入る', recSec: 3,
  },
  {
    cueId: 'pov_4', label: '人形視点 ④ 追いつき', dev: 'pov',
    hint: '白衣の背中の至近（0.5m）。**もう届いている所から**撮り始める（寄る過程は画に出ない）',
    recSec: 3,
  },
];

/** ショット名（streamer の `shot=` に渡す値）。cueId をそのまま使う。 */
export const shotName = (shot) => (shot && shot.cueId) || '';

const num = (v) => {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
};

/**
 * その cue の「頭から何秒が画に出るか」を timeline から導く。
 *
 * 同じ cue を複数のカットが `trimStartSec` を進めながら使うので（偽ライブは 0 / 1.9 / 3.6 / 5.0）、
 * **max(trimStartSec + durSec)** が要求尺になる。カットが 1 本なら単にその尺。
 * 参照が無ければ null（＝要求が無い ＝ そのショットは今の著作では使われていない）。
 */
export function neededHeadSec(segments, cueId) {
  if (!cueId) return null;
  let max = null;
  for (const seg of segments || []) {
    for (const take of (seg.takes || [])) {
      for (const st of (take.steps || [])) {
        if (!st || st.cueId !== cueId) continue;
        // trimStartSec / durSec の -1 は「cue から継承」。durSec<=0 は尺が別の条件で決まるカット
        // （untilZoneChange 等）なので、頭の要求としては数えない。
        const dur = num(st.durSec);
        if (dur <= 0) continue;
        const trim = Math.max(0, num(st.trimStartSec));
        max = Math.max(max === null ? 0 : max, trim + dur);
      }
    }
  }
  return max;
}

/** その cue を参照しているカットの数（偽ライブは 4）。 */
export function cutCount(segments, cueId) {
  let n = 0;
  for (const seg of segments || []) {
    for (const take of (seg.takes || [])) {
      for (const st of (take.steps || [])) if (st && st.cueId === cueId) n++;
    }
  }
  return n;
}

/**
 * 1 ショットの状態。**この 1 関数が単一の正**（コンソール・本番前チェックが同じ答えを読む）。
 *
 * @param shot   SHOTS の 1 要素
 * @param state  show.json（cues を見る）
 * @param segments timeline.segments（要求尺を導く）
 * @param assets 素材の実在一覧（url の Set。null なら実在は判定しない）
 * @param takes  この cue に採用候補として回収済みのテイク（{name, url, durSec, ...}）
 */
export function shotStatus(shot, state, segments, assets, takes) {
  const cueId = shotName(shot);
  const cue = ((state && state.cues) || []).find((c) => c.id === cueId) || null;
  const src = (cue && cue.sourceUrl) || '';
  const need = neededHeadSec(segments, cueId);
  const cuts = cutCount(segments, cueId);
  const list = takes || [];

  if (!cue) {
    return { cueId, ok: false, level: 'ng', src, need, cuts, takes: list,
             reason: 'この cue が show.json に無い', fix: '卓のカメラ列で cue を作る' };
  }
  if (!src) {
    return { cueId, ok: false, level: 'ng', src, need, cuts, takes: list,
             reason: '素材が未採用', fix: '撮って ⏺ → 採用する' };
  }
  if (assets && !assets.has(src)) {
    return { cueId, ok: false, level: 'ng', src, need, cuts, takes: list,
             reason: 'ファイルが見つからない', fix: '撮り直して採用する' };
  }
  // 採用済みテイクの尺が要求に足りているか。足りないカットは実機が飛ばす（§6.4）。
  const adopted = list.find((t) => t.url === src) || null;
  if (need !== null && adopted && Number.isFinite(adopted.durSec) && adopted.durSec > 0) {
    const margin = adopted.durSec - need;
    if (margin < 0) {
      return { cueId, ok: false, level: 'ng', src, need, cuts, takes: list, adopted,
               reason: `尺が ${(-margin).toFixed(1)}s 足りない（要求 ${need.toFixed(1)}s / 素材 ${adopted.durSec.toFixed(1)}s）`,
               fix: `${need.toFixed(1)}s より長く撮り直す` };
    }
    if (margin < SHORT_MARGIN_SEC) {
      return { cueId, ok: true, level: 'warn', src, need, cuts, takes: list, adopted,
               reason: `余裕が ${margin.toFixed(1)}s しかない`,
               fix: '撮り直して余裕を作る（頭がぶれると足りなくなる）' };
    }
  }
  return { cueId, ok: true, level: 'ok', src, need, cuts, takes: list, adopted, reason: '', fix: '' };
}

/** 要求尺に対して、これ以下しか余裕が無ければ警告する（秒）。 */
export const SHORT_MARGIN_SEC = 0.5;

/** 全ショットの状態。 */
export function allShotStatus(state, segments, assets, takesByShot) {
  return SHOTS.map((s) => shotStatus(s, state, segments, assets,
    (takesByShot && takesByShot[shotName(s)]) || []));
}

/**
 * 撮影日が今日か。**前会場の素材の混入が最悪の事故**（照明が違うので何度検分しても直らない）。
 * 台帳の `capturedAt`（卓の時計で刻む）を見る。端末の時計は信用しない。
 */
export function isFromToday(capturedAtIso, nowIso) {
  if (!capturedAtIso || !nowIso) return null;   // 判定できない（台帳が古い等）
  return String(capturedAtIso).slice(0, 10) === String(nowIso).slice(0, 10);
}

/**
 * 本番前チェックの「接近の素材」行。効く順に 1 件だけ返す（一覧に何行も出すと読まれない）。
 *
 * ① 採用漏れ・欠損・尺不足 → ② 録りっぱなしの端末 → ③ 撮影日 → ④ 熱段の不一致
 */
export function approachPrecheck({ state, segments, assets, takesByShot, manifest, devices, nowIso }) {
  const rows = allShotStatus(state, segments, assets, takesByShot);
  const used = rows.filter((r) => r.cuts > 0);
  if (!used.length) {
    return { s: 'ok', label: '接近の素材', detail: 'この台本は接近の素材を使っていない' };
  }
  const bad = used.filter((r) => r.level === 'ng');
  if (bad.length) {
    return { s: 'ng', label: '接近の素材',
             detail: bad.map((r) => `${r.cueId}: ${r.reason}`).join(' / ') };
  }
  // ② 録りっぱなし（本番中ずっと熱と容量を食う）
  const recording = (devices || []).filter((d) => d && d.recording);
  if (recording.length) {
    return { s: 'ng', label: '接近の素材',
             detail: `録画したままの端末がある: ${recording.map((d) => d.id || d.host).join(' / ')}` };
  }
  // ③ 撮影日
  const stale = used.map((r) => ({ r, m: (manifest || {})[r.src] }))
    .filter(({ m }) => m && isFromToday(m.capturedAt, nowIso) === false);
  if (stale.length) {
    return { s: 'ng', label: '接近の素材',
             detail: `今日撮ったものではない: ${stale.map(({ r }) => r.cueId).join(' / ')}`
                     + ' — 照明が違うので撮り直す' };
  }
  // ④ 熱段（撮影時と本番で fps が違う。配信側 v0.11.0 から画質は落ちないが fps は OS が絞る）
  const hot = used.map((r) => ({ r, m: (manifest || {})[r.src] }))
    .filter(({ m }) => m && Number.isFinite(m.throttleStage) && m.throttleStage > 0);
  if (hot.length) {
    return { s: 'warn', label: '接近の素材',
             detail: `端末が熱い状態で撮った素材がある: ${hot.map(({ r }) => r.cueId).join(' / ')}`
                     + ' — 端末を冷ましてから撮り直すと本番と揃う' };
  }
  const warn = used.filter((r) => r.level === 'warn');
  if (warn.length) {
    return { s: 'warn', label: '接近の素材',
             detail: warn.map((r) => `${r.cueId}: ${r.reason}`).join(' / ') };
  }
  return { s: 'ok', label: '接近の素材', detail: `${used.length} 本すべて採用済み・尺も足りている` };
}

/**
 * 採用するときのファイル名。**毎回一意**にする。
 *
 * ⚠ Quest は URL → ローカル DL のキャッシュを持つ（ScreenOverlayController の `_videoFileCache`）。
 *   同名で差し替えると**撮り直したのに古い版が再生され続ける**（アプリ再起動まで）。
 */
export function adoptName(cueId, take, stamp, ext = 'mp4') {
  const n = Number.isFinite(take) && take > 0 ? take : 1;
  return `${cueId}_t${String(n).padStart(2, '0')}_${stamp}.${ext}`;
}

/** 頭の窓（画に出る所）だけをループ試写するための再生区間。 */
export function headWindow(need, inPointSec) {
  const start = Math.max(0, num(inPointSec));
  const len = need === null || !(need > 0) ? 2 : need;
  return { start, end: start + len };
}
