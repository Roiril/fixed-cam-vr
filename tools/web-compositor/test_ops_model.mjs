import test from 'node:test';
import assert from 'node:assert/strict';
import {summarize, validChecks, signature, MANUAL_CHECKS} from './ops-model.js';
const now = 100_000;
const snapshot = () => ({ok:true, observedAt:100, config:{revision:12},
  cameras:['A','B','C'].map(id => ({id,status:'ok',host:id,observedUuid:id})),
  quests:['alpha','beta'].map(id => ({id,status:'ok',host:id,deviceId:id})), issues:[]});
const checked = Object.fromEntries(MANUAL_CHECKS.map(c => [c.id,true]));
test('fresh complete evidence plus manual and detailed checks is ready', () => {
  const s=snapshot(), d={ok:true,at:new Date(now).toISOString(),signature:signature(s),rows:[{state:'ok'}]};
  assert.equal(summarize(s,checked,now,false,d).ready,true);
  assert.equal(summarize(s,checked,now).ready,false);
  d.rows.push({state:'skip'});assert.equal(summarize(s,checked,now,false,d).ready,false);
  d.rows[1].required=false;assert.equal(summarize(s,checked,now,false,d).ready,true);
});
test('manual checks cannot hide a missing camera or Quest', () => {
  for (const group of ['cameras','quests']) { const s=snapshot(); s[group].pop(); assert.equal(summarize(s,checked,now).ready,false); assert.equal(summarize(s,checked,now).unknown,1); }
});
test('old observation and failed refresh remove previous healthy state', () => {
  assert.equal(summarize(snapshot(),checked,now+21000).observed,0);
  assert.equal(summarize(snapshot(),checked,now,true).ready,false);
});
test('HTTP-only camera and uncertain measurements do not pass', () => {
  for (const status of ['error','unknown','warning']) { const s=snapshot(); s.cameras[1]={id:'B',httpOk:true,status}; assert.equal(summarize(s,checked,now).ready,false); }
});
test('manual confirmations expire or invalidate on changed receiver/config', () => {
  const s=snapshot(), r={signature:signature(s),at:now,values:checked};
  assert.deepEqual(validChecks(r,s,now),checked);
  assert.deepEqual(validChecks(r,s,now+3600001),{});
  s.quests[1].deviceId='replacement'; assert.deepEqual(validChecks(r,s,now),{});
});
test('unconfirmed real-world checks stay visible after successful automatic checks', () => {
  const r=summarize(snapshot(),{},now); assert.equal(r.ready,false); assert.equal(r.pending,4);
});
