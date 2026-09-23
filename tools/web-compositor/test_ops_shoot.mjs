import test from 'node:test';
import assert from 'node:assert/strict';
import {
  DEVICE_MAX_AGE_MS, normalizeShootCapabilities, capabilityGate, recordingGate, selectNewDeviceTake,
  beginRecordingContext, pullRequestFor, uncollectedDeviceTakes, recordingSettings,
  recordingObservation,
} from './ops-shoot-model.js';

const caps = normalizeShootCapabilities({ mode: 'authoring', shooting: true, capture: true,
  adoption: true, authoring: true });

test('録画開始時のショットと端末で回収要求を作る', () => {
  const context = beginRecordingContext({
    shot: { cueId: 'pov_2' }, device: { id: 'B', host: '192.168.10.22', port: 8080 },
    deviceTakes: [{ name: 'pov_2_t01.mp4' }], startedAt: 100, token: 7,
  });
  assert.deepEqual(pullRequestFor(context, { name: 'pov_2_t02.mp4' }), {
    host: '192.168.10.22', port: 8080, name: 'pov_2_t02.mp4', shot: 'pov_2',
  });
  assert.equal(pullRequestFor(context, { name: 'pov_4_t01.mp4' }), null);
});

test('自動停止後は録画前との差分から該当ショットだけを選ぶ', () => {
  const before = new Set(['pov_1_t04.mp4', 'pov_2_t08.mp4']);
  const after = [
    { name: 'pov_1_t04.mp4', createdAt: '2026-09-23T09:00:00' },
    { name: 'pov_2_t09.mp4', createdAt: '2026-09-23T09:01:00' },
    { name: 'pov_1_t05.mp4', createdAt: '2026-09-23T09:02:00' },
  ];
  assert.equal(selectNewDeviceTake(before, after, 'pov_2').name, 'pov_2_t09.mp4');
  assert.equal(selectNewDeviceTake(before, [{ name: 'pov_2_t08.mp4' }], 'pov_2'), null);
});

test('到達可能かつ一覧対応で、接続確認が新しい端末だけ録画できる', () => {
  const now = 50_000, device = { reachable: true, canList: true, identityChecked: true, identityOk: true };
  assert.equal(recordingGate(device, now - DEVICE_MAX_AGE_MS, now, caps).ok, true);
  assert.match(recordingGate(device, now - DEVICE_MAX_AGE_MS - 1, now, caps).reason, /古く/);
  assert.match(recordingGate({ ...device, reachable: false }, now, now, caps).reason, /接続/);
  assert.match(recordingGate({ ...device, canList: false }, now, now, caps).reason, /対応していません/);
  assert.match(recordingGate({ ...device, identityChecked: true, identityOk: false }, now, now, caps).reason, /登録番号/);
  assert.match(recordingGate({ ...device, identityChecked: false }, now, now, caps).reason, /確認できていません/);
});

test('capabilities が無い操作は端末が正常でも許可しない', () => {
  const unsupported = normalizeShootCapabilities({ mode: 'viewer', capabilities: {
    shooting: false, capture: false, adoption: false, authoring: false,
  } });
  assert.match(recordingGate({ reachable: true, canList: true, identityChecked: true, identityOk: true }, 100, 100, unsupported).reason, /録画/);
});

test('録画時間は1〜600秒、待ち時間は0〜10秒の整数だけを許可する', () => {
  assert.deepEqual(recordingSettings('1', '0'), { ok: true, maxSec: 1, countdownSec: 0, reason: '' });
  assert.match(recordingSettings('0', '0').reason, /1〜600/);
  assert.match(recordingSettings('3', '11').reason, /0〜10/);
  assert.match(recordingSettings('2.5', '3').reason, /整数/);
});

test('未到達は停止と見なさず、到達した非録画状態だけを停止と判定する', () => {
  assert.equal(recordingObservation({ reachable: false, recording: false }, { expectedElapsed: true }), 'unknown');
  assert.equal(recordingObservation({ reachable: true, recording: true }, { expectedElapsed: true }), 'active');
  assert.equal(recordingObservation({ reachable: true, recording: false }, { expectedElapsed: true }), 'stopped');
});

test('準備モードは shooting で録画と回収、adoption で採用を許可する', () => {
  const preparation = normalizeShootCapabilities({ mode: 'preparation', shooting: true,
    adoption: true, capture: false, authoring: false });
  assert.equal(capabilityGate(preparation, 'record').ok, true);
  assert.equal(capabilityGate(preparation, 'pull').ok, true);
  assert.equal(capabilityGate(preparation, 'adopt').ok, true);
});

test('端末側一覧は対象ショットかつ未回収だけを残す', () => {
  const remote = [{ name: 'pov_3_t01.mp4' }, { name: 'pov_3_t02.mp4' }, { name: 'pov_4_t01.mp4' }];
  const local = [{ name: 'pov_3_t01.mp4', shot: 'pov_3' }];
  assert.deepEqual(uncollectedDeviceTakes(remote, local, 'pov_3').map(t => t.name), ['pov_3_t02.mp4']);
});
