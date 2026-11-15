// 表示の見本（?demo=ready | issue | live | down）。機材が無くても画面を確かめるためのもの。
// 実機の値ではない。操作は実行されない。
import { CAMERA_IDS, QUEST_IDS } from './model.js';

const seconds = () => Date.now() / 1000;
const facts = (extra = {}) => ({ registered: true, needsReRegistration: false,
  controllerLeft: { connected: true, tracked: true }, controllerRight: { connected: true, tracked: false },
  sound: { resolved: 46, missing: 0, audible: 0.6 }, cameraCount: 3, activeCamera: 'A', recvFps: 30,
  contentVerified: true, lang: 'ja', relief: false, ...extra });

export function demoStatus(mode) {
  const cameras = CAMERA_IDS.map((id, i) => ({ id, label: `カメラ ${id}`, host: `192.168.10.${21 + i}`, port: 8080,
    expectedUuid: `example-${id}`, observedUuid: `example-${id}`, observedId: id, status: 'ok', code: 'ok',
    title: '映像を確認しました', action: '', httpOk: true, identityOk: true, canRestart: true,
    wirelessAdb: { state: 'verified', identityOk: true },
    stream: { ok: true, frames: 2, firstSeq: 1024 + i, lastSeq: 1025 + i, elapsedSec: 0.2, bytes: 41_520 },
    metrics: { fps: 30, batteryPct: 96 - i * 7, batteryTempC: 33.4 + i * 1.3, aeLock: true, awbLock: true, lensFovDeg: 104.3 }, issues: [] }));
  const quests = QUEST_IDS.map((id, i) => ({ id, label: `Quest ${i ? 'β' : 'α'}`, host: `192.168.10.${31 + i}`,
    status: 'ok', code: 'ok', title: 'Quest から応答があります', action: '装着して A/B/C の映像と音を確認してください',
    ageSec: 1.2 + i, deviceId: `example${id}0123456789`, expectedDeviceId: `example${id}0123456789`, shortId: `exampl`,
    serial: i ? '2G0YC1ZF7S06BW' : '2G0YC1ZF890864', standby: false,
    run: { phase: 'INTRO', titleStage: 'Wait', lap: null, totalLaps: 4, runSec: 0, introSec: 0, targetSec: 180, endHolding: false },
    facts: facts(), issues: [] }));
  const tablets = QUEST_IDS.map((id, i) => ({ id, host: `192.168.10.${31 + i}`, status: 'ok', registered: true, standby: false,
    title: 'タブレットの設定が反映されています', action: '「廻リ視 博士」を起動したままにしてください',
    portalStatus: 'ok', connectionStatus: 'ok', reflectionStatus: 'ok', tabletName: `博士タブレット ${i ? 'β' : 'α'}`,
    tabletModel: 'Redmi', tabletSerial: i ? 'example-b' : '122d7871', tabletHost: `192.168.10.${41 + i}`,
    ageSec: 2.4, sentSeq: 3, appliedSeq: 3, received: 3, applyCount: 3, activePages: 1,
    requestedLang: 'ja', requestedRelief: false, lang: 'ja', relief: false }));
  const snapshot = { ok: true, observedAt: seconds(), cameras, quests, tablets, issues: [], config: { fixed: true, revision: 1 },
    desk: { expectedHost: '192.168.10.10', addresses: ['192.168.10.10'], status: 'ok', code: 'ok', title: 'この PC は 192.168.10.10 です', action: '' },
    watch: { status: 'ok', running: true, ageSec: 11, title: '自動の見張りが動いています', action: '', events: [] } };

  if (mode === 'issue') {
    Object.assign(cameras[1], { status: 'error', code: 'frame_stalled', title: '映像が進んでいません',
      action: '配信アプリを画面に出して再確認してください', stream: { ok: false, frames: 0, elapsedSec: 3 },
      issues: [{ status: 'error', code: 'frame_stalled', title: '映像が進んでいません', action: '配信アプリを画面に出して再確認してください' }] });
    Object.assign(cameras[2], { status: 'warning', code: 'exposure_unlocked', title: '露出と色が自動調整中です',
      action: '設営後に端末の露出と色を固定してください', metrics: { ...cameras[2].metrics, aeLock: false, awbLock: false },
      issues: [{ status: 'warning', code: 'exposure_unlocked', title: '露出と色が自動調整中です', action: '設営後に端末の露出と色を固定してください' }] });
    Object.assign(quests[0], { facts: facts({ registered: false }) });
    Object.assign(quests[1], { status: 'unknown', code: 'identity_unregistered', title: '端末 ID が未登録です',
      action: 'Quest の端末 ID を実測して固定台帳へ登録してください', expectedDeviceId: '', shortId: '',
      deviceId: '7fa0c2d99e1b4a5586aa01c8356aae11' });
    Object.assign(tablets[0], { status: 'warning', reflectionStatus: 'warning', title: '設定の反映を待っています',
      action: '少し待ってから反映を確認してください', requestedLang: 'en', appliedSeq: 2, applyCount: 2 });
    Object.assign(tablets[1], { status: 'unknown', registered: false, tabletName: null, tabletSerial: null, tabletHost: null,
      title: '対応する Quest の端末 ID が未登録です', portalStatus: 'unknown', connectionStatus: 'unknown', reflectionStatus: 'unknown' });
    snapshot.watch = { status: 'unknown', running: false, ageSec: null, title: '自動の見張りは動いていません',
      action: '会期中は py -3.11 tools/onsite.py watch を動かしてください', events: [] };
  }
  if (mode === 'live') {
    quests[0].run = { phase: 'RUN', titleStage: 'Done', lap: 2, totalLaps: 4, runSec: 65 + (seconds() % 40), introSec: 20, targetSec: 180, endHolding: false };
    quests[0].facts = facts({ activeCamera: 'B', lang: 'en', relief: true });
    Object.assign(cameras[2], { status: 'warning', code: 'thermal', title: '熱で画質が下がっています', action: '端末を冷ましてください',
      metrics: { ...cameras[2].metrics, batteryTempC: 43.8 },
      issues: [{ status: 'warning', code: 'thermal', title: '熱で画質が下がっています', action: '端末を冷ましてください' }] });
    snapshot.watch.events = [
      { at: '11-15 10:02:11', text: '監視を始めます（20 秒ごと / 自動復旧 あり）' },
      { at: '11-15 13:40:20', text: 'カメラB（192.168.10.22）のフレーム連番が進んでいません' },
      { at: '11-15 13:40:40', text: '次の巡回でも止まっていたら起こし直します（カメラB）' },
      { at: '11-15 13:41:02', text: '画面を起こしてアプリを起こし直しました' },
      { at: '11-15 13:41:09', text: '再確認: 復旧（連番 20114→20119）' }];
  }
  if (mode === 'down') {
    snapshot.desk = { expectedHost: '192.168.10.10', addresses: ['169.254.31.8'], status: 'error', code: 'link_local',
      title: 'この PC が展示の網から外れています',
      action: 'LAN ケーブルとルーターの電源を確認し、PowerShell で ipconfig /renew "イーサネット" を実行してください' };
    for (const camera of cameras) Object.assign(camera, { status: 'error', title: '通信できません', stream: { ok: false, frames: 0 }, httpOk: false, identityOk: false });
    for (const quest of quests) Object.assign(quest, { status: 'error', title: 'Quest から連絡がありません', run: null, facts: null, ageSec: null });
  }
  return snapshot;
}

export function demoContent(mode) {
  const id = 'a3'.repeat(32), old = 'b7'.repeat(32), guid = 'c4'.repeat(16);
  const ready = mode !== 'issue';
  const stage = (title, status = 'ok', contentId = id) => ({ status, contentId, title, action: '' });
  return { ok: true, observedAt: seconds(), policy: 'baked-only-v1', ready,
    preparation: stage('参照ファイルは揃っています'), bundle: stage('同梱内容を確認しました'),
    apk: ready ? { ...stage('APK を確認しました'), buildGuid: guid }
      : { ...stage('準備済みの内容と違います', 'error', old), action: 'APK を焼き直してください', buildGuid: guid },
    effects: [['L1C1#0', '壁一面の血の手形（1周目・B）', 3], ['L2C0#0', '2周目A 左の幕のシミ → 右のシミが滲む', 10], ['L2C2#1', '2周目C 追いつき', 4]]
      .map(([effectId, name, assetCount]) => ({ id: effectId, name, assetCount, sourceReady: true, bundled: true, inApk: ready })),
    quests: QUEST_IDS.map((questId, i) => ({ id: questId, standby: false, status: ready ? 'ok' : (i ? 'unknown' : 'error'),
      contentId: ready ? id : (i ? '' : old), buildGuid: ready || !i ? guid : '',
      title: ready ? '実機版が一致しています' : (i ? '端末 ID が未登録です' : '実機版を確認できません'),
      action: ready ? '' : '対応する APK を入れ起動してください' })) };
}

export function demoShoot(mode) {
  const today = new Date();
  const iso = offsetDays => { const d = new Date(today); d.setDate(d.getDate() - offsetDays); return d.toISOString().slice(0, 19); };
  const labels = ['2 周目 C の予備動作', '人形視点 ① 遠い', '人形視点 ② 近い', '人形視点 ③ すぐそこ', '人形視点 ④ 追いつき'];
  const partial = mode === 'issue';
  const shots = labels.map((label, i) => {
    const adopted = !(partial && i >= 3);
    return { cueId: `pov_${i}`, label, dev: 'pov', cuts: 1, recSec: 3, countdownSec: 3, hint: '高さ 40cm で正対',
      status: adopted ? 'ok' : 'ng', reason: adopted ? '' : '未採用', adoptedName: adopted ? `pov_${i}_example.mp4` : '' };
  });
  const items = Object.fromEntries(shots.filter(s => s.adoptedName).map((s, i) => [`/recordings/${s.adoptedName}`,
    { name: s.adoptedName, shot: s.cueId, capturedAt: iso(partial && i < 2 ? 52 : 0), durSec: 3, width: 480, height: 640 }]));
  const pixel = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 48"><rect width="64" height="48" fill="#23262a"/><path d="M0 34h64M20 34l8-20h20l10 20" fill="none" stroke="#59616a" stroke-width="1"/></svg>');
  return { plan: { ok: true, nowIso: iso(0), shots }, manifest: { ok: true, items },
    platesPayload: { ok: true, observedAt: seconds(), items: CAMERA_IDS.map((cameraId, i) => ({ cameraId, ready: !(partial && i === 2),
      current: partial && i === 2 ? null : { url: pixel, name: `plate_${cameraId}_example.jpg`, exists: true, capturedAt: `${iso(partial ? 52 : 0)}+09:00` },
      candidates: [] })) } };
}

export function demoInspection(mode) {
  if (mode !== 'ready' && mode !== 'live') return { ok: false };
  return { ok: true, at: new Date(Date.now() - 4 * 60_000).toISOString(), rows: [
    { state: 'ok', label: '卓', detail: '1 つだけ動いています' },
    { state: 'ok', label: 'カメラ A の無線での復旧', detail: '192.168.10.21:5555 に届きます' },
    { state: 'ok', label: 'Quest α のアプリ', detail: '焼いたアプリと同じです' }] };
}
