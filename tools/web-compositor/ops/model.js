// 当日の画面の判定。DOM も通信も持たない（node:test で固定する）。
//
// 守ること:
//   - 古い値・欠けた行・届いているだけの端末を「済み」に昇格させない。
//   - 上の灯は「いまの状態」だけ。期限つきの人の確認と詳しい点検は準備の表にだけ入れる。
//   - 原因が 1 つなら表示も 1 つ。卓に届かない・卓が網から外れた、を機器の故障として並べない。
//   - 「注意」は開場を止めない。止めるのは「対処が必要」と「まだ」だけ。

export const CAMERA_IDS = ['A', 'B', 'C'];
export const QUEST_IDS = ['alpha', 'beta'];
export const QUEST_NAMES = { alpha: 'Quest α', beta: 'Quest β' };
export const MAX_AGE_SEC = 20;
export const CONTENT_MAX_AGE_SEC = 40;
export const HUMAN_TTL_MS = 60 * 60 * 1000;
export const INSPECTION_TTL_MS = 30 * 60 * 1000;

export const GATE_IDS = ['desk', 'cameras', 'quests', 'tablet', 'materials', 'build', 'inspection', 'human'];
export const GATE_TITLES = {
  desk: '回線と卓', cameras: 'カメラ', quests: 'Quest', tablet: '博士タブレット',
  materials: '会場の素材', build: 'アプリの版', inspection: '詳しい点検', human: '人が確かめること',
};

export const HUMAN_CHECKS = [
  { id: 'space', title: '歩く場所', detail: 'ケーブルと足元を見ました。体験者が歩く所に物はありません。' },
  { id: 'composition', title: '3 台の構図', detail: 'A・B・C の画が撮影時と同じです。シミと人形が壁と床に合っています。' },
  { id: 'alpha', quest: 'alpha', title: 'Quest α を被った', detail: '映像と音が出ます。左の X と Y が効きます。位置合わせの線が床の印と合っています。' },
  { id: 'beta', quest: 'beta', title: 'Quest β を被った', detail: '映像と音が出ます。左の X と Y が効きます。位置合わせの線が床の印と合っています。' },
  { id: 'tablet', title: '博士タブレットの音', detail: '本体の音量が上がっています。「困ったとき」の通知音が聞こえます。' },
  { id: 'grip', title: '左グリップの撮影を切った', detail: '切っていないと、体験者が握り込むたびに画の保存が走ります。' },
];

const RANK = { ok: 0, warning: 1, unknown: 2, error: 3 };
export const normalize = status => (status in RANK ? status : 'unknown');
export const worst = statuses => statuses.map(normalize).reduce((a, b) => (RANK[b] > RANK[a] ? b : a), 'ok');
const blocks = status => status === 'error' || status === 'unknown';

export function isFresh(snapshot, now = Date.now()) {
  const age = now / 1000 - Number(snapshot?.observedAt);
  return snapshot?.ok === true && Number.isFinite(age) && age >= -5 && age <= MAX_AGE_SEC;
}

const sameDay = (a, b) => new Date(a).toDateString() === new Date(b).toDateString();

// ---- 使う機材 -----------------------------------------------------------------

/** 今日使う Quest の行。予備（登録の standby）は判定から外す。行が欠けた機は「まだ」として残す。 */
export function activeQuests(snapshot) {
  return QUEST_IDS.map(id => snapshot?.quests?.find(q => q.id === id) || { id, status: 'unknown', missing: true })
    .filter(q => q.standby !== true);
}

/** 登録のあるタブレットだけを数える。登録の無い側は「登録なし」と見せるが、判定には入れない。 */
export function activeTablets(snapshot) {
  const inUse = new Set(activeQuests(snapshot).map(q => q.id));
  return (snapshot?.tablets || []).filter(t => t.registered !== false && t.standby !== true && inUse.has(t.id));
}

/** 位置合わせと音。Quest 自身の申告で、行の status には入っていない分。 */
export function questNotes(quest) {
  const facts = quest?.facts;
  if (!facts) return [];
  const notes = [];
  if (facts.registered === false) notes.push({ level: 'unknown', text: '位置合わせがまだです' });
  else if (facts.needsReRegistration === true) notes.push({ level: 'unknown', text: '位置合わせのやり直しが必要です' });
  if (facts.sound && facts.sound.missing > 0)
    notes.push({ level: 'warning', text: `読み込めていない音が ${facts.sound.missing} 個あります` });
  return notes;
}

const questStatus = quest => worst([quest?.status, ...questNotes(quest).map(n => n.level)]);

// ---- 原因をまとめる -------------------------------------------------------------

/**
 * 全体を覆う原因。あれば機器ごとの判定は当てにならない。
 *   offline = この画面が卓に届いていない ／ desk = 卓の PC が展示の網から外れた
 */
export function outage(snapshot, now = Date.now(), failed = false, pending = false) {
  // 問い合わせの返事を待っているだけのときは、届いていないとは言わない（届かない端末があると卓の返事は遅くなる）。
  if (!failed && pending && !isFresh(snapshot, now))
    return { kind: 'offline', waiting: true, title: '卓の返事を待っています',
      detail: '機器を調べています。届かない端末があると、返事に 20 秒ほど掛かることがあります。' };
  if (failed || !isFresh(snapshot, now))
    return { kind: 'offline', title: 'この画面が卓に届いていません',
      detail: '下の表示は古い値です。卓の PC が動いているかと、この端末の Wi-Fi を確かめてください。' };
  if (snapshot.desk?.status === 'error')
    return { kind: 'desk', title: snapshot.desk.title || '卓の PC が展示の網から外れています',
      detail: `${snapshot.desk.action || 'PC の LAN ケーブルとルーターを確かめてください'}。卓が戻るまで、カメラと Quest の判定は当てになりません。` };
  return null;
}

/** 上の灯。卓・A・B・C・α・β・博士。6 秒〜20 秒以内の生の値だけで作る。 */
export function lamps(snapshot, now = Date.now(), failed = false) {
  const out = outage(snapshot, now, failed);
  const lamp = (id, label, status, title) => ({ id, label, status, title });
  const dead = reason => [
    lamp('desk', '卓', out.kind === 'desk' ? 'error' : 'unknown', out.title),
    ...CAMERA_IDS.map(id => lamp(id, id, 'unknown', reason)),
    ...QUEST_IDS.map(id => lamp(id, id === 'alpha' ? 'α' : 'β', 'unknown', reason)),
    lamp('tablet', '博士', 'unknown', reason),
  ];
  if (out) return dead(out.kind === 'offline' ? '卓に届いていないので分かりません' : '卓が網に戻るまで分かりません');
  const tablets = activeTablets(snapshot);
  const tabletQuestDown = tablets.find(t => {
    const quest = snapshot.quests?.find(q => q.id === t.id);
    return !quest || !['ok', 'warning'].includes(quest.status);
  });
  return [
    lamp('desk', '卓', normalize(snapshot.desk?.status), snapshot.desk?.title || '卓の PC'),
    ...CAMERA_IDS.map(id => {
      const row = snapshot.cameras?.find(c => c.id === id);
      return lamp(id, id, normalize(row?.status), row?.title || '未確認');
    }),
    ...QUEST_IDS.map(id => {
      const row = snapshot.quests?.find(q => q.id === id);
      const label = id === 'alpha' ? 'α' : 'β';
      if (row?.standby === true) return lamp(id, label, 'standby', '予備（今日は使わない）');
      return lamp(id, label, row ? questStatus(row) : 'unknown', row?.title || '未確認');
    }),
    !tablets.length ? lamp('tablet', '博士', 'unknown', '博士タブレットが登録されていません')
      : tabletQuestDown ? lamp('tablet', '博士', 'unknown', `${QUEST_NAMES[tabletQuestDown.id]} の応答待ち`)
        : lamp('tablet', '博士', worst(tablets.map(t => t.status)), tablets[0].title || '未確認'),
  ];
}

// ---- 人の確認 -------------------------------------------------------------------

/** 機器が入れ替わったら人の確認は取り消す。 */
export function signature(snapshot) {
  return JSON.stringify([snapshot?.config?.revision,
    ...(snapshot?.cameras || []).map(c => [c.id, c.host, c.expectedUuid, c.observedUuid]),
    ...(snapshot?.quests || []).map(q => [q.id, q.host, q.deviceId, q.standby === true])]);
}

export function humanChecks(snapshot) {
  const inUse = new Set(activeQuests(snapshot).map(q => q.id));
  return HUMAN_CHECKS.filter(c => !c.quest || inUse.has(c.quest));
}

export function validChecks(record, snapshot, now = Date.now()) {
  if (!record || record.signature !== signature(snapshot) ||
      !Number.isFinite(record.at) || now < record.at || now - record.at > HUMAN_TTL_MS) return {};
  return record.values || {};
}

// ---- 会場の素材 -----------------------------------------------------------------

/** 人形視点。capturedAt は卓へ回収した時刻で、撮った日ではない。 */
export function summarizeShots(plan, manifest) {
  if (plan?.ok !== true || manifest?.ok !== true || !/^\d{4}-\d{2}-\d{2}/.test(plan.nowIso || '')) return null;
  const day = plan.nowIso.slice(0, 10);
  const entries = Object.values(manifest.items || {});
  const shots = (plan.shots || []).filter(s => s.dev === 'pov' && Number(s.cuts) > 0);
  const rows = shots.map(shot => {
    const adopted = shot.status === 'ok' && !!shot.adoptedName;
    const entry = adopted ? entries.find(e => e.shot === shot.cueId && e.name === shot.adoptedName) : null;
    const collectedAt = typeof entry?.capturedAt === 'string' ? entry.capturedAt : '';
    return { cueId: shot.cueId, label: shot.label || shot.cueId, adopted, name: adopted ? shot.adoptedName : '',
      collectedAt, today: collectedAt.slice(0, 10) === day, reason: adopted ? '' : (shot.reason || '未採用') };
  });
  return { required: rows.length, complete: rows.filter(r => r.adopted).length,
    today: rows.filter(r => r.adopted && r.today).length, rows };
}

/** 無人の写真。採用中の 3 枚と、そのうち今日撮った枚数。 */
export function summarizePlates(payload, now = Date.now()) {
  const age = now / 1000 - Number(payload?.observedAt);
  if (payload?.ok !== true || !Number.isFinite(age) || age < -5 || age > CONTENT_MAX_AGE_SEC) return null;
  const rows = CAMERA_IDS.map(id => {
    const item = payload.items?.find(i => i.cameraId === id);
    const adopted = item?.ready === true && item.current?.exists === true;
    const taken = new Date(item?.current?.capturedAt || '');
    const known = adopted && Number.isFinite(taken.getTime()) && taken.getTime() <= now + 5000;
    return { cameraId: id, adopted, url: adopted ? item.current.url : '', capturedAt: known ? taken.toISOString() : '',
      today: known && sameDay(taken, now) };
  });
  return { required: 3, complete: rows.filter(r => r.adopted).length, today: rows.filter(r => r.today).length, rows };
}

/** 較正時から傾いたカメラ。人の「このまま使う」を取り消す根拠にする。 */
export function driftedCameras(snapshot) {
  return (snapshot?.cameras || []).filter(c => (c.issues || []).some(i =>
    i.code === 'calibration_mismatch' || i.code === 'calibration_shift')).map(c => c.id);
}

/**
 * 素材を人が見たという記録。演出の版・日付・カメラの傾きのどれかが変わったら無効。
 *   placed = いまのカメラの位置と照明で撮ったものだ ／ watched = 採用中の素材を見た
 */
export function validReview(record, content, snapshot, now = Date.now()) {
  const current = content?.preparation?.contentId;
  if (!current || record?.contentId !== current || !Number.isFinite(record?.at) || now < record.at ||
      !sameDay(record.at, now) || !summarizeContent(content, now).fresh || content.preparation.status !== 'ok' ||
      record.drift !== driftedCameras(snapshot).join(',')) return {};
  return { placed: record.placed === true, watched: record.watched === true };
}

// ---- アプリの版 -----------------------------------------------------------------

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
  const guid = value => (typeof value === 'string' ? value.replaceAll('-', '').toLowerCase() : '');
  const buildGuid = guid(content?.apk?.buildGuid);
  const validBuild = /^[a-f0-9]{32}$/.test(buildGuid);
  const stagesReady = validId && stages.every(s => s?.status === 'ok' && s.contentId === target);
  const inUse = (content?.quests || []).filter(q => q.standby !== true);
  const questsReady = validId && validBuild && inUse.length > 0 && inUse.every(q =>
    q.status === 'ok' && q.contentId === target && guid(q.buildGuid) === buildGuid);
  const ready = fresh && content.policy === 'baked-only-v1' && content.ready === true && stagesReady && questsReady;
  const broken = fresh && [...stages, ...inUse].some(row => row?.status === 'error');
  return { fresh, ready, stagesReady, questsReady, broken };
}

// ---- 準備の表 -------------------------------------------------------------------

const gate = (id, status, summary, action = '') => ({ id, title: GATE_TITLES[id], status, summary, action });

function rowsGate(id, rows, names, allOk) {
  const status = worst(rows.map(r => r.status));
  if (status === 'ok') return gate(id, 'ok', allOk);
  const order = ['error', 'unknown', 'warning'];
  const sorted = rows.filter(r => r.status !== 'ok').sort((a, b) => order.indexOf(a.status) - order.indexOf(b.status));
  const first = sorted[0];
  const rest = sorted.length > 1 ? `（ほか ${sorted.length - 1} 台）` : '';
  return gate(id, status, `${names(first)}: ${first.title || '未確認'}${rest}`, first.action || '');
}

/**
 * 準備の表の 8 行。
 * input = { snapshot, failed, content, contentFailed, shots, plates, review, checks, inspection }
 */
export function gates(input, now = Date.now()) {
  const { snapshot, failed = false, content = null, contentFailed = false, shots = null, plates = null,
    review = {}, checks = {}, inspection = null } = input;
  const out = outage(snapshot, now, failed);
  const waiting = out?.kind === 'offline' ? '卓に届いていないので分かりません' : '卓が網に戻るまで分かりません';

  const desk = out?.kind === 'offline' ? gate('desk', 'unknown', out.title, 'この端末が展示の Wi-Fi につながっているか確かめてください')
    : !snapshot.desk ? gate('desk', 'unknown', 'この PC の IP を調べられません', '卓のサーバーを新しい版で起動し直してください')
      : gate('desk', normalize(snapshot.desk.status), snapshot.desk.title || '', snapshot.desk.action || '');

  let cameras, quests, tablet;
  if (out) {
    cameras = gate('cameras', 'unknown', waiting);
    quests = gate('quests', 'unknown', waiting);
    tablet = gate('tablet', 'unknown', waiting);
  } else {
    cameras = rowsGate('cameras', CAMERA_IDS.map(id => {
      const row = snapshot.cameras?.find(c => c.id === id);
      return { id, status: normalize(row?.status), title: row?.title, action: row?.action || '配信アプリを開いてください' };
    }), r => `カメラ ${r.id}`, '3 台とも映像が進んでいます');

    const inUse = activeQuests(snapshot);
    quests = !inUse.length ? gate('quests', 'unknown', '今日使う Quest がありません', '登録で予備になっていない Quest が要ります')
      : rowsGate('quests', inUse.map(q => {
        const note = questNotes(q).find(n => n.level === 'unknown') || questNotes(q)[0];
        const own = normalize(q.status);
        const useNote = note && RANK[note.level] > RANK[own];
        return { id: q.id, status: questStatus(q), title: useNote ? note.text : (q.title || '応答がありません'),
          action: useNote ? (note.level === 'unknown' ? 'Quest を被り、右トリガーを 2 秒押して位置を合わせます' : 'アプリを焼き直すと直ることがあります')
            : (q.action || 'Quest を起動して展示の Wi-Fi につないでください') };
      }), r => QUEST_NAMES[r.id], `${inUse.length > 1 ? `${inUse.length} 台とも` : ''}応答があります${
        inUse.every(q => q.facts?.registered === true) ? '。位置も合っています' : ''}`);

    const tablets = activeTablets(snapshot);
    if (!tablets.length) tablet = gate('tablet', 'unknown', '博士タブレットが登録されていません', '使うタブレットを登録してください');
    else tablet = rowsGate('tablet', tablets.map(t => {
      const quest = snapshot.quests?.find(q => q.id === t.id);
      const questUp = quest && ['ok', 'warning'].includes(quest.status);
      return { id: t.id, status: questUp ? normalize(t.status) : 'unknown',
        title: questUp ? t.title : `${QUEST_NAMES[t.id]} の応答待ち`,
        action: questUp ? t.action : `先に ${QUEST_NAMES[t.id]} を起動します` };
    }), r => `${QUEST_NAMES[r.id]} 用`, '送った設定が Quest に届いています');
  }

  let materials;
  if (!shots || !plates) materials = gate('materials', 'unknown', '撮影の状況を読めていません');
  else if (shots.complete < shots.required)
    materials = gate('materials', 'unknown', `人形視点 ${shots.complete} / ${shots.required} 本`, '残りを撮って採用します');
  else if (plates.complete < plates.required)
    materials = gate('materials', 'unknown', `無人の写真 ${plates.complete} / 3 枚`, '人が画面から出てから撮って採用します');
  else if (!review.placed)
    materials = gate('materials', 'unknown', '会場と合っているかをまだ見ていません', '無人の写真をいまの映像と重ねて確かめます');
  else if (!review.watched)
    materials = gate('materials', 'unknown', '採用中の素材をまだ見ていません', '見比べの画面で確かめます');
  else materials = gate('materials', 'ok', `人形視点 ${shots.complete} 本と無人の写真 3 枚を確かめました`);

  const release = summarizeContent(content, now, contentFailed);
  const build = !release.fresh ? gate('build', 'unknown', '照合できていません')
    : release.ready ? gate('build', 'ok', '使う Quest に、採用中の素材と同じ版が入っています')
      : !release.stagesReady ? gate('build', release.broken ? 'error' : 'unknown', 'アプリを焼き直す必要があります', '素材を確かめてからビルドします')
        : gate('build', release.broken ? 'error' : 'unknown', 'Quest に入っている版が違います', '焼いたアプリを Quest に入れて起動します');

  let check;
  const age = inspection ? now - Date.parse(inspection.at) : NaN;
  if (!(inspection?.ok && Array.isArray(inspection.rows) && inspection.rows.length &&
        inspection.signature === signature(snapshot) && age >= 0 && age <= INSPECTION_TTL_MS)) {
    check = gate('inspection', 'unknown', 'まだ実行していません', '40 秒ほどで終わります');
  } else {
    const counted = inspection.rows.filter(r => r.required !== false);
    const ng = counted.filter(r => r.state === 'ng').length;
    const skipped = counted.filter(r => r.state === 'skip').length;
    const warned = counted.filter(r => r.state === 'warn').length;
    check = ng ? gate('inspection', 'error', `直す所が ${ng} 件あります`, '結果の上から順に直します')
      : skipped ? gate('inspection', 'unknown', `調べられなかった項目が ${skipped} 件あります`, '結果に出ている手順を済ませて、もう一度実行します')
        : warned ? gate('inspection', 'warning', `注意が ${warned} 件あります`) : gate('inspection', 'ok', 'すべて通りました');
  }

  const list = humanChecks(snapshot);
  const pending = list.filter(c => !checks[c.id]).length;
  const human = pending ? gate('human', 'unknown', `あと ${pending} 件`, '機器がそろってから、現場で確かめて印を付けます')
    : gate('human', 'ok', `${list.length} 件を確かめました`);

  return [desk, cameras, quests, tablet, materials, build, check, human];
}

/** 人の確認に印を付けてよいか。機器の行に「対処」も「まだ」も無いときだけ。 */
export function humanChecksOpen(list) {
  return ['desk', 'cameras', 'quests', 'tablet'].every(id => !blocks(list.find(g => g.id === id)?.status));
}

/** 開場できるか。注意は数えるが止めない。 */
export function verdict(list) {
  const blocking = list.filter(g => blocks(g.status));
  const cautions = list.filter(g => g.status === 'warning').length;
  const next = blocking[0] || null;
  if (list.find(g => g.id === 'desk')?.status !== 'ok' && next?.id === 'desk')
    return { ready: false, status: next.status, remaining: blocking.length, cautions, next,
      headline: next.status === 'error' ? '卓を先に直します' : '確認できていません' };
  if (blocking.length) return { ready: false, status: worst(blocking.map(g => g.status)), remaining: blocking.length,
    cautions, next, headline: `あと ${blocking.length} 件` };
  return { ready: true, status: cautions ? 'warning' : 'ok', remaining: 0, cautions, next: null, headline: '開場できます' };
}

// ---- 本番 ----------------------------------------------------------------------

export function clock(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return '';
  const whole = Math.floor(seconds);
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

/** Quest がいま体験のどこに居るか。 */
export function phaseText(run) {
  if (!run?.phase) return { label: '分かりません', detail: '', active: false };
  if (run.phase === 'END') return { label: '終幕', detail: '終わりの演出のあいだはリセットしません', active: true };
  if (run.phase === 'INTRO') {
    if (run.titleStage === 'Wait' || run.titleStage === '') return { label: '待機中', detail: '次の体験者を待っています', active: false };
    if (run.titleStage === 'In' || run.titleStage === 'Hold') return { label: '題字', detail: '体験者の短押しを待っています', active: true };
    return { label: '導入', detail: clock(run.introSec), active: true };
  }
  const last = run.lap >= 4 || (run.totalLaps >= 4 && run.lap >= run.totalLaps);
  const where = !run.lap ? '本編' : last ? '最後の A' : `${run.lap} 周目`;
  const time = clock(run.runSec);
  const target = clock(run.targetSec);
  return { label: where, detail: time ? `${time}${target ? ` / 目安 ${target}` : ''}` : '', active: true,
    over: Number.isFinite(run.runSec) && Number.isFinite(run.targetSec) && run.targetSec > 0 && run.runSec > run.targetSec };
}

/**
 * 本番中に、いますぐ人が動くこと。体験の段で重みを変える。
 *   - 体験中に左コントローラが見えていなければ、体験者は報告できない。
 *   - 待機中の右コントローラは見ない（机の上で寝ているだけ）。
 */
export function liveAlerts(snapshot, now = Date.now(), failed = false) {
  const out = outage(snapshot, now, failed);
  if (out) return [{ level: 'error', who: '卓', text: out.title, action: out.detail }];
  const alerts = [];
  for (const id of CAMERA_IDS) {
    const row = snapshot.cameras?.find(c => c.id === id);
    const status = normalize(row?.status);
    if (status !== 'ok') alerts.push({ level: status, who: `カメラ ${id}`, text: row?.title || '未確認', action: row?.action || '' });
  }
  for (const quest of activeQuests(snapshot)) {
    const status = normalize(quest.status);
    const who = QUEST_NAMES[quest.id];
    if (status !== 'ok') alerts.push({ level: status, who, text: quest.title || '応答がありません', action: quest.action || '' });
    const running = phaseText(quest.run).active;
    const left = quest.facts?.controllerLeft;
    if (running && left && (!left.connected || !left.tracked))
      alerts.push({ level: 'error', who, text: left.connected ? '左コントローラが見えていません' : '左コントローラがつながっていません',
        action: '体験者は報告できません。博士タブレットの案内に従います' });
    for (const note of questNotes(quest))
      if (note.level === 'unknown') alerts.push({ level: running ? 'error' : 'warning', who, text: note.text,
        action: running ? 'いまの体験が終わってから合わせ直します' : 'Quest を被り、右トリガーを 2 秒押します' });
  }
  for (const tablet of activeTablets(snapshot)) {
    const quest = snapshot.quests?.find(q => q.id === tablet.id);
    if (!quest || !['ok', 'warning'].includes(quest.status)) continue;
    if (tablet.status === 'error' || tablet.connectionStatus !== 'ok')
      alerts.push({ level: tablet.status === 'error' ? 'error' : 'warning', who: '博士タブレット', text: tablet.title || '応答がありません', action: tablet.action || '' });
  }
  if (snapshot.watch && snapshot.watch.running !== true)
    alerts.push({ level: 'warning', who: '見張り', text: snapshot.watch.title || '自動の見張りは動いていません', action: snapshot.watch.action || '' });
  const order = ['error', 'unknown', 'warning'];
  return alerts.sort((a, b) => order.indexOf(a.level) - order.indexOf(b.level));
}
