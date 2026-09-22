import test from 'node:test';
import assert from 'node:assert/strict';
import {summarize, summarizeContent, contentSignature, validChecks, signature, MANUAL_CHECKS} from './ops-model.js';
const now = 100_000;
const snapshot = () => ({ok:true, observedAt:100, config:{revision:12},
  cameras:['A','B','C'].map(id => ({id,status:'ok',host:id,observedUuid:id})),
  quests:['alpha','beta'].map(id => ({id,status:'ok',host:id,deviceId:id})),
  tablets:['alpha','beta'].map(id => ({id,status:'ok',portalSessionId:id,tabletSessionId:id})), issues:[]});
const checked = Object.fromEntries(MANUAL_CHECKS.map(c => [c.id,true]));
const content = () => { const contentId='a'.repeat(64); return {ok:true,observedAt:100,policy:'baked-only-v1',ready:true,
  preparation:{status:'ok',contentId},bundle:{status:'ok',contentId},apk:{status:'ok',contentId,buildGuid:'c'.repeat(32)},
  quests:['alpha','beta'].map(id=>({id,status:'ok',contentId,buildGuid:'c'.repeat(32)}))}; };
test('fresh complete evidence plus manual and detailed checks is ready', () => {
  const s=snapshot(), d={ok:true,at:new Date(now).toISOString(),signature:signature(s),rows:[{state:'ok'}]};
  assert.equal(summarize(s,checked,now,false,d,content()).ready,true);
  assert.equal(summarize(s,checked,now).ready,false);
  d.rows.push({state:'skip'});assert.equal(summarize(s,checked,now,false,d,content()).ready,false);
  d.rows[1].required=false;assert.equal(summarize(s,checked,now,false,d,content()).ready,true);
  assert.equal(summarize(s,checked,now,false,d).ready,false);
});
test('manual checks cannot hide a missing camera or Quest', () => {
  for (const group of ['cameras','quests','tablets']) { const s=snapshot(); s[group].pop(); assert.equal(summarize(s,checked,now).ready,false); assert.equal(summarize(s,checked,now).unknown,1); }
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

test('prepared materials cannot stand in for a matching APK and both running Quests',()=>{
  assert.equal(summarizeContent(content(),now).ready,true);
  for(const stage of ['bundle','apk']){const c=content();c[stage].contentId='b'.repeat(64);assert.equal(summarizeContent(c,now).ready,false);}
  for(const id of ['alpha','beta']){const c=content();c.quests.find(q=>q.id===id).status='unknown';assert.equal(summarizeContent(c,now).ready,false);}
  const c=content();c.quests.pop();assert.equal(summarizeContent(c,now).ready,false);
  const olderApp=content();olderApp.quests[0].buildGuid='d'.repeat(32);assert.equal(summarizeContent(olderApp,now).ready,false);
  const noBuildProof=content();delete noBuildProof.apk.buildGuid;assert.equal(summarizeContent(noBuildProof,now).ready,false);
});
test('content readiness expires and rejects a legacy or failed proof',()=>{
  assert.equal(summarizeContent(content(),now+41000).ready,false);
  assert.equal(summarizeContent(content(),now,true).ready,false);
  const c=content();c.policy='live';assert.equal(summarizeContent(c,now).ready,false);
  c.policy='baked-only-v1';c.ready=false;assert.equal(summarizeContent(c,now).ready,false);
});
test('a changed material edition or rebuilt Quest invalidates the physical checks',()=>{
  const c=content(),s=snapshot();s.contentSignature=contentSignature(c);
  const r={signature:signature(s),at:now,values:checked};
  assert.deepEqual(validChecks(r,s,now),checked);
  c.quests[0].buildGuid='new-build';s.contentSignature=contentSignature(c);
  assert.deepEqual(validChecks(r,s,now),{});
});

test('tablet absence, pending reflection and a restarted portal invalidate readiness',()=>{
  const s=snapshot(),d={ok:true,at:new Date(now).toISOString(),signature:signature(s),rows:[{state:'ok'}]};
  for(const status of ['unknown','warning','error']) {s.tablets[0].status=status;assert.equal(summarize(s,checked,now,false,d,content()).ready,false);}
  const r={signature:signature(s),at:now,values:checked};
  s.tablets[0].portalSessionId='restart';assert.deepEqual(validChecks(r,s,now),{});
});
