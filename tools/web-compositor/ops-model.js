// The view never promotes missing, stale, or merely reachable devices to ready.
export const CAMERA_IDS = ['A', 'B', 'C'];
export const QUEST_IDS = ['alpha', 'beta'];
export const MAX_AGE_SEC = 20;
export const CONTENT_MAX_AGE_SEC = 40;
export const MANUAL_TTL_MS = 60 * 60 * 1000;
export const MANUAL_CHECKS = [
  { id: 'space', title: '歩く場所を確認', detail: 'ケーブルと足元を確認しました。体験者の移動を妨げる物はありません。' },
  { id: 'composition', title: '3 台の構図と合成位置を確認', detail: 'A・B・C の設置場所が合っています。合成した人形や染みの位置も確認しました。' },
  { id: 'alpha', title: 'Quest α を装着して確認', detail: '映像と音を確認しました。左右の入力と位置合わせも動きます。' },
  { id: 'beta', title: 'Quest β を装着して確認', detail: '映像と音を確認しました。左右の入力と位置合わせも動きます。' },
];

export function isFresh(snapshot, now = Date.now()) {
  const age = now / 1000 - Number(snapshot?.observedAt);
  return snapshot?.ok === true && Number.isFinite(age) && age >= -5 && age <= MAX_AGE_SEC;
}

export function signature(snapshot) {
  return JSON.stringify([snapshot?.config?.revision, snapshot?.contentSignature,
    ...(snapshot?.cameras || []).map(c => [c.id, c.host, c.expectedUuid, c.observedUuid]),
    ...(snapshot?.quests || []).map(q => [q.id, q.host, q.deviceId]),
    ...(snapshot?.tablets || []).map(t => [t.id, t.portalSessionId, t.tabletSessionId])]);
}

export function validChecks(record, snapshot, now = Date.now()) {
  if (!record || record.signature !== signature(snapshot) ||
      !Number.isFinite(record.at) || now < record.at || now - record.at > MANUAL_TTL_MS) return {};
  return record.values || {};
}

export function contentSignature(content) {
  return JSON.stringify([content?.preparation?.contentId, content?.apk?.contentId,
    ...(content?.quests || []).map(q => [q.id, q.contentId, q.buildGuid])]);
}

export function summarizeContent(content, now = Date.now(), failed = false) {
  const age = now / 1000 - Number(content?.observedAt);
  const fresh = !failed && content?.ok === true && Number.isFinite(age) && age >= -5 && age <= CONTENT_MAX_AGE_SEC;
  const stages = ['preparation', 'bundle', 'apk'].map(key => content?.[key]);
  const target = content?.preparation?.contentId;
  const validId = typeof target === 'string' && /^[a-f0-9]{64}$/.test(target);
  const guid = value => typeof value === 'string' ? value.replaceAll('-','').toLowerCase() : '';
  const buildGuid = guid(content?.apk?.buildGuid);
  const validBuild = /^[a-f0-9]{32}$/.test(buildGuid);
  const stagesReady = validId && stages.every(s => s?.status === 'ok' && s.contentId === target);
  const questsReady = validId && validBuild && QUEST_IDS.every(id => {
    const q = content?.quests?.find(q => q.id === id);
    return q?.status === 'ok' && q.contentId === target && guid(q.buildGuid) === buildGuid;
  });
  const ready = fresh && content.policy === 'baked-only-v1' && content.ready === true && stagesReady && questsReady;
  return {fresh, ready, stagesReady, questsReady, status: ready ? 'ok' : !fresh ? 'unknown' : 'warning',
    title: !fresh ? '演出の準備と同梱は未確認です' : !stagesReady ? '演出の準備か再ビルドが必要です' :
      !questsReady ? '各 Quest の演出版を確認してください' : !ready ? '演出の照合が完了していません' : '両方の Quest で同じ演出を確認しました'};
}

export function summarize(snapshot, checks = {}, now = Date.now(), failed = false, detailed = null, content = null) {
  const fresh = !failed && isFresh(snapshot, now);
  const devices = [
    ...CAMERA_IDS.map(id => snapshot?.cameras?.find(c => c.id === id) || { id, status: 'unknown' }),
    ...QUEST_IDS.map(id => snapshot?.quests?.find(q => q.id === id) || { id, status: 'unknown' }),
    ...QUEST_IDS.map(id => snapshot?.tablets?.find(t => t.id === id) || { id, status: 'unknown' }),
  ];
  const blockers = fresh ? devices.filter(d => d.status === 'error').length : 0;
  const unknown = fresh ? devices.filter(d => !['ok', 'warning', 'error'].includes(d.status)).length : 7;
  const warnings = fresh ? devices.filter(d => d.status === 'warning').length : 0;
  const pending = MANUAL_CHECKS.filter(c => !checks[c.id]).length;
  const observed = fresh ? devices.filter(d => ['ok', 'warning'].includes(d.status)).length : 0;
  const globalIssues = fresh ? (snapshot?.issues || []).filter(i => i.status !== 'ok') : [];
  const detailedAge = detailed ? now - Date.parse(detailed.at) : Infinity;
  const diagnosticMissing = !(detailed?.ok && Array.isArray(detailed.rows) && detailed.rows.length &&
    detailed.signature === signature(snapshot) && detailedAge >= 0 && detailedAge <= 30 * 60 * 1000);
  const diagnosticIssues = diagnosticMissing ? 0 : detailed.rows.filter(r => r.required !== false && r.state !== 'ok').length;
  const release = summarizeContent(content, now);
  const ready = fresh && !blockers && !unknown && !warnings && !pending && !globalIssues.length && !diagnosticMissing && !diagnosticIssues && release.ready;
  return { fresh, blockers, unknown, warnings, pending, observed, ready, diagnosticMissing, diagnosticIssues, contentReady:release.ready,
    title: !fresh ? '機器の状態を確認できていません' : blockers ? `${blockers} 台に対処が必要です` :
      unknown ? `${unknown} 台が未確認です` : warnings || globalIssues.length ? '注意が必要な項目があります' :
      !release.ready ? release.title : diagnosticIssues ? '詳しい点検に確認が残っています' : pending ? '接続を確認しました。実機の確認が残っています' :
      diagnosticMissing ? 'アプリと素材の点検が残っています' : '準備の確認がそろいました',
    status: !fresh ? 'unknown' : blockers ? 'error' : unknown ? 'unknown' :
      warnings || globalIssues.length || pending || diagnosticMissing || diagnosticIssues || !release.ready ? 'warning' : 'ok' };
}
