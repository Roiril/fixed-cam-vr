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

// capturedAt is the collection time, not the phone's filming time.
// Today's venue and the final selection must be confirmed separately by a person.
export function summarizeOnsiteShots(plan, manifest) {
  if (plan?.ok !== true || manifest?.ok !== true || !/^\d{4}-\d{2}-\d{2}/.test(plan.nowIso || '')) return null;
  const day = plan.nowIso.slice(0,10);
  const entries = Object.values(manifest.items || {});
  const shots = (plan.shots || []).filter(s => s.dev === 'pov' && Number(s.cuts) > 0);
  const ready = shots.filter(s => s.status === 'ok' && !!s.adoptedName);
  const collected = ready.filter(s => entries.some(e =>
    e.shot === s.cueId && e.name === s.adoptedName &&
    typeof e.capturedAt === 'string' && e.capturedAt.slice(0,10) === day));
  return {required:shots.length, complete:ready.length, collectedToday:collected.length, missing:shots.filter(s => !ready.includes(s)).map(s => s.label || s.cueId)};
}

export function summarizePlates(payload, now = Date.now()) {
  const age=now/1000-Number(payload?.observedAt);
  if(payload?.ok!==true||!Number.isFinite(age)||age < -5||age>CONTENT_MAX_AGE_SEC)return null;
  const rows=CAMERA_IDS.map(id=>payload.items?.find(item=>item.cameraId===id));
  const adopted=rows.filter(item=>item?.ready===true&&item.current?.exists===true);
  const today=adopted.filter(item=>{
    const captured=new Date(item.current.capturedAt||'');
    return Number.isFinite(captured.getTime())&&captured.getTime()<=now+5000&&captured.toDateString()===new Date(now).toDateString();
  });
  return {required:3,complete:adopted.length,today:today.length,ready:today.length===3};
}

export function validPreparation(record, content, now = Date.now()) {
  const current = content?.preparation?.contentId;
  if (!current || record?.contentId !== current || !Number.isFinite(record?.at) || now < record.at ||
      new Date(record.at).toDateString() !== new Date(now).toDateString() ||
      !summarizeContent(content,now).fresh || content.preparation.status !== 'ok') return {};
  return {venue:record.venue === true,media:record.media === true};
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

export function summarize(snapshot, checks = {}, now = Date.now(), failed = false, detailed = null, content = null, onsiteShots = undefined) {
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
  const onsiteReady = onsiteShots === undefined || !!(onsiteShots && onsiteShots.complete === onsiteShots.required &&
    onsiteShots.verified === true && onsiteShots.mediaReviewed === true);
  const ready = fresh && !blockers && !unknown && !warnings && !pending && !globalIssues.length && !diagnosticMissing && !diagnosticIssues && release.ready && onsiteReady;
  return { fresh, blockers, unknown, warnings, pending, observed, ready, diagnosticMissing, diagnosticIssues, contentReady:release.ready && onsiteReady,
    title: !fresh ? '機器の状態を確認できていません' : blockers ? `${blockers} 台に対処が必要です` :
      unknown ? `${unknown} 台が未確認です` : warnings || globalIssues.length ? '注意が必要な項目があります' :
      !release.ready ? release.title : !onsiteReady ? '当日の人形視点と無人シーンを確認してください' : diagnosticIssues ? '詳しい点検に確認が残っています' : pending ? '接続を確認しました。実機の確認が残っています' :
      diagnosticMissing ? 'アプリと素材の点検が残っています' : '準備の確認がそろいました',
    status: !fresh ? 'unknown' : blockers ? 'error' : unknown ? 'unknown' :
      warnings || globalIssues.length || pending || diagnosticMissing || diagnosticIssues || !release.ready || !onsiteReady ? 'warning' : 'ok' };
}
