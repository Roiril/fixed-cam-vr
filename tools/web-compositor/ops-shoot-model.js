// 設営画面へ統合する撮影 UI の純粋な判定。
// DOM と通信を置かず、録画の帰属と操作可否を node:test で固定する。

export const DEVICE_MAX_AGE_MS = 12_000;

const capValue = (value) => value === true || value?.enabled === true;

/** capabilities は新旧の返却形（直下 / capabilities 配下）を受ける。 */
export function normalizeShootCapabilities(payload) {
  const source = payload?.capabilities || payload || {};
  return {
    loaded: payload != null,
    mode: String(payload?.mode || source.mode || ''),
    shooting: capValue(source.shooting),
    adoption: capValue(source.adoption),
    capture: capValue(source.capture),
    authoring: capValue(source.authoring),
  };
}

export function capabilityGate(capabilities, action) {
  if (!capabilities?.loaded) return { ok: false, reason: 'この卓で使える操作を確認できていません。' };
  if (action === 'record' && !capabilities.shooting)
    return { ok: false, reason: 'この卓は端末録画に対応していません。' };
  // 回収は端末録画の一部。capture は固定カメラの静止画撮影を表す別機能。
  if (action === 'pull' && !capabilities.shooting)
    return { ok: false, reason: 'この卓は端末からの回収に対応していません。' };
  // authoring は素材工房の編集。回収済みテイクの採用は adoption だけで判定する。
  if (action === 'adopt' && !capabilities.adoption)
    return { ok: false, reason: 'この卓では素材を採用できません。編集できる卓で開いてください。' };
  return { ok: true, reason: '' };
}

/** 端末一覧はブラウザ自身が取得した時刻で鮮度を判定する。 */
export function recordingGate(device, fetchedAt, now = Date.now(), capabilities = null) {
  const cap = capabilityGate(capabilities, 'record');
  if (!cap.ok) return cap;
  if (!Number.isFinite(fetchedAt) || now < fetchedAt || now - fetchedAt > DEVICE_MAX_AGE_MS)
    return { ok: false, reason: '端末の接続確認が古くなっています。もう一度確認してください。' };
  if (!device) return { ok: false, reason: '録画する端末を選んでください。' };
  if (!device.reachable) return { ok: false, reason: 'この端末へ接続できません。' };
  if (!device.canList) return { ok: false, reason: 'この端末のアプリは録画回収に対応していません。' };
  if (!device.identityChecked)
    return { ok: false, reason: '端末の担当と登録番号を確認できていません。接続画面で確認してください。' };
  if (device.identityOk !== true)
    return { ok: false, reason: '端末の担当または登録番号が一致していません。接続画面で確認してください。' };
  if (device.recording || device.counting) return { ok: false, reason: 'この端末はすでに録画中です。' };
  return { ok: true, reason: '' };
}

export function recordingSettings(duration, countdown) {
  const maxSec = Number(duration), countdownSec = Number(countdown);
  if (!Number.isInteger(maxSec) || maxSec < 1 || maxSec > 600)
    return { ok: false, reason: '録画時間は 1〜600 秒の整数で入力してください。' };
  if (!Number.isInteger(countdownSec) || countdownSec < 0 || countdownSec > 10)
    return { ok: false, reason: '待ち時間は 0〜10 秒の整数で入力してください。' };
  return { ok: true, maxSec, countdownSec, reason: '' };
}

/** 未到達は停止の証拠にしない。停止は到達できた端末の非録画状態だけで確定する。 */
export function recordingObservation(device, { observedActive = false, expectedElapsed = false } = {}) {
  if (!device || !device.reachable) return 'unknown';
  if (device.counting || device.recording) return 'active';
  return observedActive || expectedElapsed ? 'stopped' : 'waiting';
}

export const takeName = (take) => String(take?.name || take?.file || '');

export function takeBelongsToShot(take, cueId) {
  if (!take || !cueId) return false;
  if (take.shot) return take.shot === cueId;
  const name = takeName(take);
  return name === cueId || name.startsWith(`${cueId}_`) || name.startsWith(`${cueId}-`);
}

const takeTime = (take) => {
  for (const value of [take?.capturedAt, take?.createdAt, take?.mtime, take?.modifiedAt]) {
    const n = typeof value === 'number' ? value : Date.parse(value || '');
    if (Number.isFinite(n)) return n;
  }
  return 0;
};

/**
 * 録画前に無かったファイルだけを返す。先頭要素を「最新」と推測しない。
 * 同じ録画で複数増えた場合は時刻の新しいものを 1 本選ぶ。
 */
export function selectNewDeviceTake(beforeNames, afterTakes, cueId) {
  const before = beforeNames instanceof Set ? beforeNames : new Set(beforeNames || []);
  const fresh = (afterTakes || []).filter((take) => {
    const name = takeName(take);
    return name && !before.has(name) && takeBelongsToShot(take, cueId);
  });
  fresh.sort((a, b) => takeTime(b) - takeTime(a) || takeName(b).localeCompare(takeName(a)));
  return fresh[0] || null;
}

export function beginRecordingContext({ shot, device, deviceTakes, startedAt = Date.now(), token = 0 }) {
  return Object.freeze({
    token,
    cueId: String(shot?.cueId || ''),
    device: Object.freeze({ id: device?.id || '', host: device?.host || '', port: Number(device?.port) || 8080 }),
    beforeNames: Object.freeze((deviceTakes || []).map(takeName).filter(Boolean)),
    startedAt,
  });
}

/** 回収要求は現在の画面選択ではなく録画開始時の文脈から作る。 */
export function pullRequestFor(context, take) {
  const name = takeName(take);
  if (!context?.cueId || !context?.device?.host || !name || !takeBelongsToShot(take, context.cueId)) return null;
  return { host: context.device.host, port: context.device.port, name, shot: context.cueId };
}

export function localTakesForShot(localTakes, cueId) {
  return (localTakes || []).filter((take) => take?.shot === cueId || takeBelongsToShot(take, cueId));
}

/** 端末側一覧から、対象ショットに合い、同名の回収済み素材が無いものを出す。 */
export function uncollectedDeviceTakes(deviceTakes, localTakes, cueId) {
  const localNames = new Set((localTakes || []).map(takeName).filter(Boolean));
  return (deviceTakes || []).filter((take) => takeBelongsToShot(take, cueId) && !localNames.has(takeName(take)));
}
