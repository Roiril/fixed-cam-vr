// 端末内録画（show.json `record`）の判定。DOM 非依存の純関数だけを置く。
//
// **「録画」カットが実機で本当に映るか**は 3 箇所（⏺ 録画パネル / 本番前チェック / タイムラインの
// カット警告 / シミュレータ）で同じ答えを出さなければならない。列挙と判定はこの 1 ファイルが単一の正
// （ラッチの latches()・タイムラインの timeline-model と同じ流儀）。
//
// 移植元（**C# が正**）:
//   ShowRecordDef.RecordsLap        … enabled ∧ laps に含む
//   SegmentRecorder.OnCameraEntered … 録るのは体験者が入った区間だけ
//   TakeRunner.OpenRecording        … recLap<=0 / camera<0 / ファイル無し → そのカットを飛ばす

/** show.json `record` が欠けている時の既定（capture-server.py の _default_show と同じ値）。 */
export const REC_DEFAULT = { enabled: false, laps: [1], maxSegmentSec: 60, maxTotalMB: 200, fpsCap: 15 };

const int = (v) => {
  const n = parseInt(v, 10);
  return Number.isFinite(n) ? n : 0;
};

/** show.json → 正規化した録画設定（欠落は既定で埋める）。 */
export function recordConfig(state) {
  return { ...REC_DEFAULT, ...((state && state.record) || {}) };
}

/** 録る周の集合（1 始まり・不正値は落とす）。 */
export function recordLaps(cfg) {
  const laps = cfg && Array.isArray(cfg.laps) ? cfg.laps : [];
  return new Set(laps.map(int).filter((n) => n > 0));
}

/**
 * 「録画」カット 1 枚が実機で映らない理由。映るなら null。
 * rec 以外のカットは常に null（判定対象外）。
 *
 * 返り値は { cause, fix } — **短い原因**と**打ち手**を分ける。一覧（本番前チェック・⏺ パネル）は
 * cause だけを並べ、カット単体の警告は文にして出す。1 本の長文を使い回すと入れ子の括弧になる。
 *
 * cameraCount を渡すと範囲外カメラも弾く（渡さなければ camera>=0 だけ見る）。
 */
export function recStepProblem(cfg, step, cameraCount) {
  if (!step || step.source !== 'rec') return null;
  const lap = int(step.recLap);
  const cam = int(step.camera);
  const camOk = Number.isInteger(step.camera) && cam >= 0
    && (!(cameraCount > 0) || cam < cameraCount);
  if (!camOk || lap <= 0) {
    return { cause: '録画の周 / カメラが未指定', fix: 'このカットで録画元の周とカメラを選ぶ' };
  }
  if (!cfg || !cfg.enabled) {
    return { cause: '端末内録画が OFF', fix: '⏺ 端末内録画パネルで ON にする' };
  }
  if (!recordLaps(cfg).has(lap)) {
    return { cause: `${lap}周目は録らない設定`, fix: `⏺ 端末内録画パネルで録る周に ${lap} を足す` };
  }
  return null;
}

/** <see cref="recStepProblem"/> を 1 文にしたもの（カット単体の警告用）。問題なければ空文字。 */
export function recStepIssue(cfg, step, cameraCount) {
  const p = recStepProblem(cfg, step, cameraCount);
  return p ? `${p.cause} — ${p.fix}。このままだと実機ではこのカットが飛ばされます` : '';
}

/**
 * タイムライン区間群から「録画」カットの参照を全部集める。
 * camLabel(cameraIndex) → 表示名（省略時は #index）。
 */
export function recCutRefs(segments, camLabel) {
  const label = typeof camLabel === 'function' ? camLabel : (i) => `#${i}`;
  const out = [];
  for (const seg of segments || []) {
    for (const t of (seg.takes || [])) {
      for (const st of (t.steps || [])) {
        if (!st || st.source !== 'rec') continue;
        out.push({
          lap: int(st.recLap),
          camera: Number.isInteger(st.camera) ? st.camera : -1,
          where: `Lap${seg.lap} / ${label(seg.camera)}`,
          take: t.name || t.id || '演出',
          step: st,
        });
      }
    }
  }
  return out;
}

/**
 * 設定と参照の照合。**この 1 関数が単一の正**（⏺ パネル・本番前チェックが同じ配列を読む）。
 * 返り値 { cfg, laps, refs, bad }。refs[i].ok / reason に判定が入る。
 */
export function recordCoverage(state, segments, camLabel) {
  const cfg = recordConfig(state);
  const cameraCount = ((state && state.cameras) || []).length;
  const refs = recCutRefs(segments, camLabel).map((r) => {
    const p = recStepProblem(cfg, r.step, cameraCount);
    return { ...r, ok: !p, reason: p ? p.cause : '', fix: p ? p.fix : '' };
  });
  return { cfg, laps: recordLaps(cfg), refs, bad: refs.filter((r) => !r.ok) };
}

/** 参照されている周のうち、いま録る設定になっていないもの（⟲ ワンクリック修正用）。 */
export function missingRecordLaps(state, segments) {
  const laps = recordLaps(recordConfig(state));
  const out = new Set();
  for (const r of recCutRefs(segments)) {
    if (r.lap > 0 && r.camera >= 0 && !laps.has(r.lap)) out.add(r.lap);
  }
  return [...out].sort((a, b) => a - b);
}
