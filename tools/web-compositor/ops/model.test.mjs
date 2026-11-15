import test from 'node:test';
import assert from 'node:assert/strict';
import {
  HUMAN_CHECKS, gates, verdict, lamps, outage, signature, validChecks, validReview, humanChecks, humanChecksOpen,
  summarizeContent, summarizeShots, summarizePlates, contentSignature, questNotes, activeTablets,
  phaseText, liveAlerts, clock, worst, driftedCameras,
} from './model.js';

const now = 100_000;
const facts = (extra = {}) => ({ registered: true, needsReRegistration: false,
  controllerLeft: { connected: true, tracked: true }, controllerRight: { connected: true, tracked: true },
  sound: { resolved: 40, missing: 0, audible: 0.4 }, ...extra });
const snapshot = () => ({ ok: true, observedAt: 100, config: { revision: 12 },
  desk: { status: 'ok', title: 'この PC は 192.168.10.10 です', action: '' },
  watch: { running: true, status: 'ok', events: [] },
  cameras: ['A', 'B', 'C'].map(id => ({ id, status: 'ok', title: '映像を確認しました', host: id, observedUuid: id, issues: [] })),
  quests: ['alpha', 'beta'].map(id => ({ id, status: 'ok', title: 'Quest から応答があります', host: id, deviceId: id,
    standby: false, run: { phase: 'INTRO', titleStage: 'Wait' }, facts: facts() })),
  tablets: ['alpha', 'beta'].map(id => ({ id, status: 'ok', registered: true, standby: false,
    connectionStatus: 'ok', title: 'タブレットの設定が反映されています' })), issues: [] });
const content = () => { const contentId = 'a'.repeat(64); return { ok: true, observedAt: 100, policy: 'baked-only-v1', ready: true,
  preparation: { status: 'ok', contentId }, bundle: { status: 'ok', contentId }, apk: { status: 'ok', contentId, buildGuid: 'c'.repeat(32) },
  quests: ['alpha', 'beta'].map(id => ({ id, status: 'ok', contentId, buildGuid: 'c'.repeat(32), standby: false })) }; };
const shots = () => ({ required: 5, complete: 5, today: 5, rows: [] });
const plates = () => ({ required: 3, complete: 3, today: 3, rows: [] });
const checked = Object.fromEntries(HUMAN_CHECKS.map(c => [c.id, true]));
const inspection = s => ({ ok: true, at: new Date(now).toISOString(), signature: signature(s), rows: [{ state: 'ok' }] });
const complete = (s = snapshot()) => ({ snapshot: s, content: content(), shots: shots(), plates: plates(),
  review: { placed: true, watched: true }, checks: checked, inspection: inspection(s) });
const byId = (list, id) => list.find(g => g.id === id);

test('everything fresh and confirmed opens the venue', () => {
  const list = gates(complete(), now);
  assert.deepEqual(list.map(g => g.status), Array(8).fill('ok'));
  assert.deepEqual(verdict(list), { ready: true, status: 'ok', remaining: 0, cautions: 0, next: null, headline: '開場できます' });
});

test('old or failed observation never keeps a healthy state', () => {
  for (const input of [{ ...complete(), failed: true }, complete()]) {
    const at = input.failed ? now : now + 21_000;
    const list = gates(input, at);
    for (const id of ['desk', 'cameras', 'quests', 'tablet']) assert.equal(byId(list, id).status, 'unknown', id);
    assert.equal(verdict(list).ready, false);
    assert.equal(verdict(list).headline, '確認できていません');
    assert.ok(lamps(input.snapshot, at, input.failed).every(l => l.status === 'unknown'));
    assert.equal(outage(input.snapshot, at, input.failed).kind, 'offline');
  }
  // 返事待ちは「届いていない」と言わない。ただし失敗が確定したら言う。古い値は、どちらでも済みにしない。
  assert.equal(outage(null, now, false, true).waiting, true);
  assert.equal(outage(snapshot(), now + 21_000, false, true).title, '卓の返事を待っています');
  assert.equal(outage(snapshot(), now + 21_000, true, true).title, 'この画面が卓に届いていません');
  assert.equal(outage(snapshot(), now, false, true), null);
});

test('a desk that fell off the network is one cause, not six broken devices', () => {
  const s = snapshot();
  s.desk = { status: 'error', title: 'この PC が展示の網から外れています', action: 'ipconfig /renew を実行してください' };
  for (const c of s.cameras) Object.assign(c, { status: 'error', title: '通信できません' });
  const list = gates(complete(s), now);
  assert.equal(byId(list, 'desk').status, 'error');
  for (const id of ['cameras', 'quests', 'tablet']) {
    assert.equal(byId(list, id).status, 'unknown');
    assert.match(byId(list, id).summary, /卓が網に戻るまで/);
  }
  assert.equal(verdict(list).headline, '卓を先に直します');
  assert.equal(verdict(list).next.id, 'desk');
  const lit = lamps(s, now);
  assert.equal(lit[0].status, 'error');
  assert.ok(lit.slice(1).every(l => l.status === 'unknown'));
  assert.deepEqual(liveAlerts(s, now).map(a => a.who), ['卓']);
});

test('a missing, unreachable or merely warned device is named with its own action', () => {
  const s = snapshot();
  s.cameras[1] = { id: 'B', status: 'error', title: '映像が進んでいません', action: '配信アプリを画面に出してください', issues: [] };
  s.cameras[2] = { id: 'C', status: 'warning', title: '熱で画質が下がっています', action: '', issues: [] };
  let cameras = byId(gates(complete(s), now), 'cameras');
  assert.equal(cameras.status, 'error');
  assert.equal(cameras.summary, 'カメラ B: 映像が進んでいません（ほか 1 台）');
  assert.equal(cameras.action, '配信アプリを画面に出してください');
  s.cameras.splice(1, 1);
  cameras = byId(gates(complete(s), now), 'cameras');
  assert.equal(cameras.status, 'unknown');
  assert.match(cameras.summary, /^カメラ B/);
  s.quests.pop();
  assert.equal(byId(gates(complete(s), now), 'quests').status, 'unknown');
});

test('cautions are counted but do not stop the opening', () => {
  const s = snapshot();
  s.cameras[0] = { id: 'A', status: 'warning', title: '露出と色が自動調整中です', action: '', issues: [] };
  const v = verdict(gates(complete(s), now));
  assert.equal(v.ready, true);
  assert.equal(v.status, 'warning');
  assert.equal(v.cautions, 1);
});

test('a standby Quest and its tablet leave the verdict, and its human check disappears', () => {
  const s = snapshot();
  Object.assign(s.quests[1], { status: 'unknown', title: '端末 ID が未登録です' });
  Object.assign(s.tablets[1], { status: 'unknown', registered: false });
  const input = complete(s);
  input.content.quests[1].status = 'unknown';
  input.content.ready = false;
  assert.equal(verdict(gates(input, now)).ready, false);
  s.quests[1].standby = true;
  input.content.quests[1].standby = true;
  input.content.ready = true;
  input.inspection = inspection(s);
  const list = gates(input, now);
  assert.equal(verdict(list).ready, true, JSON.stringify(list.filter(g => g.status !== 'ok')));
  assert.equal(lamps(s, now).find(l => l.id === 'beta').status, 'standby');
  assert.deepEqual(humanChecks(s).map(c => c.id), ['space', 'composition', 'alpha', 'tablet', 'grip']);
  assert.deepEqual(activeTablets(s).map(t => t.id), ['alpha']);
  for (const q of s.quests) q.standby = true;
  assert.equal(byId(gates(complete(s), now), 'quests').status, 'unknown');
});

test('an unregistered tablet is not counted, but zero registered tablets is not ready', () => {
  const s = snapshot();
  s.tablets[1].registered = false;
  s.tablets[1].status = 'unknown';
  assert.equal(byId(gates(complete(s), now), 'tablet').status, 'ok');
  s.tablets[0].registered = false;
  assert.equal(byId(gates(complete(s), now), 'tablet').status, 'unknown');
});

test('the tablet waits for its Quest instead of reporting its own fault', () => {
  const s = snapshot();
  Object.assign(s.quests[0], { status: 'error', title: 'Quest から連絡がありません' });
  Object.assign(s.tablets[0], { status: 'unknown', title: 'タブレットを確認できません' });
  const tablet = byId(gates(complete(s), now), 'tablet');
  assert.equal(tablet.status, 'unknown');
  assert.match(tablet.summary, /Quest α の応答待ち/);
  assert.match(lamps(s, now).find(l => l.id === 'tablet').title, /Quest α の応答待ち/);
  assert.ok(!liveAlerts(s, now).some(a => a.who === '博士タブレット'));
});

test('position registration reported by the Quest blocks, a missing sound only warns', () => {
  const s = snapshot();
  s.quests[0].facts = facts({ registered: false });
  let quests = byId(gates(complete(s), now), 'quests');
  assert.equal(quests.status, 'unknown');
  assert.match(quests.summary, /Quest α: 位置合わせがまだです/);
  assert.match(quests.action, /右トリガー/);
  s.quests[0].facts = facts({ needsReRegistration: true });
  assert.equal(questNotes(s.quests[0])[0].text, '位置合わせのやり直しが必要です');
  s.quests[0].facts = facts({ sound: { resolved: 38, missing: 2, audible: 0.4 } });
  quests = byId(gates(complete(s), now), 'quests');
  assert.equal(quests.status, 'warning');
  s.quests[0].facts = null;
  quests = byId(gates(complete(s), now), 'quests');
  assert.equal(quests.status, 'ok');
  assert.doesNotMatch(quests.summary, /位置も合っています/);
});

test('materials need every clip, every photo and both human looks', () => {
  const at = (patch) => byId(gates({ ...complete(), ...patch }, now), 'materials');
  assert.equal(at({ shots: null }).status, 'unknown');
  assert.match(at({ shots: { required: 5, complete: 3, today: 3, rows: [] } }).summary, /人形視点 3 \/ 5 本/);
  assert.match(at({ plates: { required: 3, complete: 2, today: 2, rows: [] } }).summary, /無人の写真 2 \/ 3 枚/);
  assert.match(at({ review: {} }).summary, /会場と合っているか/);
  assert.match(at({ review: { placed: true } }).summary, /採用中の素材をまだ見ていません/);
  // 前の日に撮った素材でも、人が今の映像と見比べていれば通す。
  assert.equal(at({ shots: { required: 5, complete: 5, today: 0, rows: [] }, plates: { required: 3, complete: 3, today: 0, rows: [] } }).status, 'ok');
  assert.equal(at({ shots: { required: 0, complete: 0, today: 0, rows: [] } }).status, 'ok');
});

test('a replaced photo names the materials made for the old one, without blocking the opening', () => {
  const time = new Date(2026, 10, 15, 12).getTime();
  const dep = (current) => ({ group: 'stainA', title: 'シミ', plate: 'plate_A_old.jpg', current, cueIds: ['a', 'b'] });
  const item = (cameraId, dependents) => ({ cameraId, ready: true, dependents,
    current: { exists: true, url: `/captures/${cameraId}.jpg`, capturedAt: new Date(time - 1000).toISOString() } });
  const payload = (a) => ({ ok: true, observedAt: time / 1000, unclassified: [], items: [item('A', a), item('B', []), item('C', [])] });
  const same = summarizePlates(payload([dep(true)]), time);
  assert.deepEqual([same.stale.length, same.ledger], [0, true]);
  const replaced = summarizePlates(payload([dep(false)]), time);
  assert.equal(replaced.stale.length, 1);
  assert.equal(replaced.stale[0].cameraId, 'A');
  const at = (plates) => byId(gates({ ...complete(), plates }, now), 'materials');
  const materials = at(replaced);
  assert.equal(materials.status, 'warning');
  assert.match(materials.summary, /カメラ A の写真を替えました。前の写真に合わせた素材が 1 種類/);
  assert.equal(at(same).status, 'ok');
  assert.equal(at({ ...same, unclassified: ['cue_new'] }).status, 'warning');
  // 台帳を読めない卓（古い版）では何も言わない。
  assert.equal(summarizePlates({ ...payload(undefined), unclassified: undefined }, time).ledger, false);
  // 注意は開場を止めない。
  assert.equal(verdict(gates({ ...complete(), plates: replaced }, now)).ready, true);
});

test('the human look at materials dies with the content, the day, or a moved camera', () => {
  const s = snapshot(), c = content();
  const time = new Date(2026, 10, 15, 12).getTime();
  c.observedAt = time / 1000;
  const record = { contentId: c.preparation.contentId, at: time - 1000, placed: true, watched: true, drift: '' };
  assert.deepEqual(validReview(record, c, s, time), { placed: true, watched: true });
  assert.deepEqual(validReview({ ...record, at: time - 86_400_000 }, c, s, time), {});
  assert.deepEqual(validReview({ ...record, contentId: 'b'.repeat(64) }, c, s, time), {});
  s.cameras[1].issues = [{ code: 'calibration_shift' }];
  assert.deepEqual(driftedCameras(s), ['B']);
  assert.deepEqual(validReview(record, c, s, time), {});
  assert.deepEqual(validReview({ ...record, drift: 'B' }, c, s, time), { placed: true, watched: true });
  c.preparation.status = 'error';
  assert.deepEqual(validReview({ ...record, drift: 'B' }, c, s, time), {});
});

test('app version needs the prepared content in the bundle, the APK and every Quest in use', () => {
  const build = c => byId(gates({ ...complete(), content: c }, now), 'build');
  assert.equal(build(content()).status, 'ok');
  assert.equal(build(null).status, 'unknown');
  const stale = content(); stale.apk = { status: 'error', contentId: 'b'.repeat(64), buildGuid: 'c'.repeat(32) }; stale.ready = false;
  assert.equal(build(stale).status, 'error');
  assert.match(build(stale).summary, /焼き直す/);
  const quest = content(); quest.quests[1] = { id: 'beta', status: 'unknown', contentId: '', buildGuid: '' }; quest.ready = false;
  assert.equal(build(quest).status, 'unknown');
  assert.match(build(quest).summary, /Quest に入っている版/);
  const other = content(); other.quests[0].buildGuid = 'd'.repeat(32);
  assert.equal(summarizeContent(other, now).ready, false);
  const flag = content(); flag.ready = false;
  assert.equal(summarizeContent(flag, now).ready, false);
  assert.equal(summarizeContent(content(), now + 41_000).fresh, false);
  assert.equal(summarizeContent(content(), now, true).fresh, false);
  assert.notEqual(contentSignature(content()), contentSignature(other));
});

test('inspection is valid for thirty minutes and for the same devices only', () => {
  const s = snapshot();
  const run = (rows, extra = {}) => byId(gates({ ...complete(s), inspection: { ...inspection(s), rows, ...extra } }, now), 'inspection');
  assert.equal(run([{ state: 'ok' }]).status, 'ok');
  assert.equal(run([{ state: 'ok' }, { state: 'ng' }]).status, 'error');
  assert.equal(run([{ state: 'ok' }, { state: 'skip' }]).status, 'unknown');
  assert.equal(run([{ state: 'ok' }, { state: 'skip', required: false }]).status, 'ok');
  assert.equal(run([{ state: 'ok' }, { state: 'warn' }]).status, 'warning');
  assert.equal(run([{ state: 'ok' }], { at: new Date(now - 31 * 60_000).toISOString() }).status, 'unknown');
  assert.equal(run([{ state: 'ok' }], { signature: 'other' }).status, 'unknown');
  assert.equal(run([]).status, 'unknown');
  assert.equal(byId(gates({ ...complete(s), inspection: null }, now), 'inspection').status, 'unknown');
});

test('human confirmations expire, follow the devices, and open only on a clean board', () => {
  const s = snapshot(), record = { signature: signature(s), at: now, values: checked };
  assert.deepEqual(validChecks(record, s, now), checked);
  assert.deepEqual(validChecks(record, s, now + 3_600_001), {});
  assert.deepEqual(validChecks(record, s, now - 1), {});
  s.quests[1].deviceId = 'replacement';
  assert.deepEqual(validChecks(record, s, now), {});
  const list = gates({ ...complete(), checks: {} }, now);
  assert.equal(byId(list, 'human').summary, `あと ${HUMAN_CHECKS.length} 件`);
  assert.equal(humanChecksOpen(list), true);
  const broken = snapshot(); broken.cameras[0].status = 'error';
  assert.equal(humanChecksOpen(gates({ ...complete(broken), checks: {} }, now)), false);
  const warned = snapshot(); warned.cameras[0].status = 'warning';
  assert.equal(humanChecksOpen(gates({ ...complete(warned), checks: {} }, now)), true);
});

test('the next step is the first blocking row in venue order', () => {
  const s = snapshot(); s.quests[1].status = 'error';
  const list = gates({ ...complete(s), shots: { required: 5, complete: 2, today: 2, rows: [] }, checks: {} }, now);
  const v = verdict(list);
  // Quest β・β のタブレット（Quest の応答待ち）・素材・人の確認。
  assert.deepEqual(list.filter(g => g.status !== 'ok').map(g => g.id), ['quests', 'tablet', 'materials', 'human']);
  assert.equal(v.headline, 'あと 4 件');
  assert.equal(v.next.id, 'quests');
  assert.equal(v.status, 'error');
});

test('adopted clips and photos are counted apart from how recently they were collected', () => {
  const plan = { ok: true, nowIso: '2026-11-15T09:00:00', shots: [
    { cueId: 'pov_0', dev: 'pov', cuts: 1, status: 'ok', adoptedName: 'a.mp4', label: '遠景' },
    { cueId: 'pov_1', dev: 'pov', cuts: 1, status: 'ok', adoptedName: 'b.mp4' },
    { cueId: 'pov_2', dev: 'pov', cuts: 1, status: 'ng', adoptedName: '', reason: '尺が足りません' },
    { cueId: 'old', dev: 'pov', cuts: 0, status: 'ok', adoptedName: 'x.mp4' }] };
  const manifest = { ok: true, items: { a: { shot: 'pov_0', name: 'a.mp4', capturedAt: '2026-11-15T08:00:00' },
    b: { shot: 'pov_1', name: 'b.mp4', capturedAt: '2026-09-24T17:00:00' } } };
  const s = summarizeShots(plan, manifest);
  assert.deepEqual([s.required, s.complete, s.today], [3, 2, 1]);
  assert.equal(s.rows[2].reason, '尺が足りません');
  assert.equal(summarizeShots({ ok: true, nowIso: '' }, manifest), null);
  assert.equal(summarizeShots(plan, null), null);
  const time = new Date(2026, 10, 15, 12).getTime();
  const item = (cameraId, taken) => ({ cameraId, ready: true, current: { exists: true, url: `/captures/${cameraId}.jpg`, capturedAt: taken } });
  const payload = { ok: true, observedAt: time / 1000, items: [item('A', new Date(time - 1000).toISOString()),
    item('B', new Date(time - 86_400_000).toISOString()), item('C', new Date(time + 60_000).toISOString())] };
  const p = summarizePlates(payload, time);
  assert.deepEqual([p.complete, p.today], [3, 1]);
  assert.equal(p.rows[2].capturedAt, '');
  payload.items[1].current.exists = false;
  assert.equal(summarizePlates(payload, time).complete, 2);
  payload.items.pop();
  assert.equal(summarizePlates(payload, time).complete, 1);
  assert.equal(summarizePlates(payload, time + 41_000), null);
  assert.equal(summarizePlates(null, time), null);
});

test('where the visitor is in the run', () => {
  assert.equal(phaseText(null).label, '分かりません');
  assert.deepEqual([phaseText({ phase: 'INTRO', titleStage: 'Wait' }).label, phaseText({ phase: 'INTRO', titleStage: 'Wait' }).active], ['待機中', false]);
  assert.equal(phaseText({ phase: 'INTRO', titleStage: 'Hold' }).label, '題字');
  assert.equal(phaseText({ phase: 'INTRO', titleStage: 'Done', introSec: 12 }).label, '導入');
  const second = phaseText({ phase: 'RUN', lap: 2, totalLaps: 4, runSec: 65.4, targetSec: 180 });
  assert.deepEqual([second.label, second.detail, second.over], ['2 周目', '1:05 / 目安 3:00', false]);
  assert.equal(phaseText({ phase: 'RUN', lap: 4, totalLaps: 4, runSec: 200, targetSec: 180 }).label, '最後の A');
  assert.equal(phaseText({ phase: 'RUN', lap: 4, totalLaps: 4, runSec: 200, targetSec: 180 }).over, true);
  assert.equal(phaseText({ phase: 'RUN', lap: null }).label, '本編');
  assert.equal(phaseText({ phase: 'END' }).label, '終幕');
  assert.equal(clock(-1), '');
  assert.equal(clock(600), '10:00');
});

test('during a run the left controller matters, a sleeping right controller never does', () => {
  const s = snapshot();
  s.quests[0].facts = facts({ controllerRight: { connected: false, tracked: false }, controllerLeft: { connected: true, tracked: false } });
  assert.deepEqual(liveAlerts(s, now), []);
  s.quests[0].run = { phase: 'RUN', lap: 1, runSec: 10 };
  const alerts = liveAlerts(s, now);
  assert.equal(alerts.length, 1);
  assert.deepEqual([alerts[0].level, alerts[0].who, alerts[0].text], ['error', 'Quest α', '左コントローラが見えていません']);
  s.quests[0].facts = facts({ registered: false });
  assert.equal(liveAlerts(s, now)[0].level, 'error');
  s.quests[0].run = { phase: 'INTRO', titleStage: 'Wait' };
  assert.equal(liveAlerts(s, now)[0].level, 'warning');
});

test('live alerts list the worst first and mention a stopped watch', () => {
  const s = snapshot();
  s.cameras[2] = { id: 'C', status: 'warning', title: '熱で画質が下がっています', action: '端末を冷ましてください', issues: [] };
  s.cameras[0] = { id: 'A', status: 'error', title: '映像が進んでいません', action: '', issues: [] };
  s.watch = { running: false, title: '自動の見張りは動いていません', action: 'watch を動かしてください' };
  s.tablets[0] = { ...s.tablets[0], status: 'unknown', connectionStatus: 'unknown', title: 'タブレットの応答がありません' };
  assert.deepEqual(liveAlerts(s, now).map(a => [a.level, a.who]),
    [['error', 'カメラ A'], ['warning', 'カメラ C'], ['warning', '博士タブレット'], ['warning', '見張り']]);
  assert.equal(worst(['ok', 'warning', 'bogus']), 'unknown');
});
