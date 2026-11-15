// 画面の骨組みと「困ったとき」の中身が、面と行の名前から外れていないことを固定する。
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, access } from 'node:fs/promises';
import { GATE_IDS, GATE_TITLES, HUMAN_CHECKS } from './model.js';
import { SYMPTOMS } from './help.js';

const here = new URL('.', import.meta.url);
const read = name => readFile(new URL(name, here), 'utf8');
const VIEWS = ['ready', 'shoot', 'live', 'help'];
const SHOOT_PANES = ['pov', 'plates', 'compare'];

test('the page has the four views, the lamp strip and no external resources', async () => {
  const html = await read('../index.html');
  for (const view of VIEWS) {
    assert.match(html, new RegExp(`id="view-${view}"`), view);
    assert.equal(html.split(`data-view="${view}"`).length - 1, 2, `${view} は上の帯と下のドックに 1 つずつ`);
  }
  assert.match(html, /id="lamps"/);
  assert.match(html, /src="ops\/main\.js"/);
  // 会場は閉じた LAN。外のフォント・CDN を読むと、届かない待ちで画面が遅れる。
  // （www.w3.org は SVG の名前空間の文字列で、取りには行かない）
  for (const file of ['../index.html', 'ops.css'])
    assert.doesNotMatch(await read(file), /https?:\/\/(?!localhost|www\.w3\.org\/)/, `${file} が外を読んでいる`);
  assert.doesNotMatch(html, /user-scalable=no|maximum-scale/);
});

test('old entry pages only forward to the new screen', async () => {
  for (const [file, target] of [['onsite.html', '/index.html'], ['shoot.html', '/index.html#shoot'], ['runbook.html', '/index.html#help']]) {
    const html = await read(`../${file}`);
    assert.match(html, new RegExp(`url=${target.replace('/', '\\/')}"`), file);
    assert.doesNotMatch(html, /ops\.css|ops\.js/, `${file} が消したファイルを読んでいる`);
  }
  for (const gone of ['ops.js', 'ops.css', 'ops-model.js', 'ops-shoot.js', 'ops-plates.js', 'ops-library.js'])
    await assert.rejects(access(new URL(`../${gone}`, here)), `${gone} が残っている`);
});

test('every gate has a title and every human check a sentence', () => {
  assert.equal(GATE_IDS.length, 8);
  for (const id of GATE_IDS) assert.ok(GATE_TITLES[id], id);
  assert.equal(new Set(HUMAN_CHECKS.map(c => c.id)).size, HUMAN_CHECKS.length);
  for (const check of HUMAN_CHECKS) assert.ok(check.title && check.detail.endsWith('。'), check.id);
});

test('each symptom leads with one action and links only to places that exist', () => {
  assert.equal(new Set(SYMPTOMS.map(s => s.id)).size, SYMPTOMS.length);
  for (const symptom of SYMPTOMS) {
    assert.ok(symptom.title && symptom.first.endsWith('。') && symptom.steps.length >= 1, symptom.id);
    if (symptom.never) assert.equal(symptom.never.length, 2, symptom.id);
    if (symptom.fix) assert.equal(symptom.fix, 'panel', `${symptom.id}: 画面から呼べる復旧は panel だけ`);
    if (!symptom.link) continue;
    const [view, sub] = symptom.link[0].split('/');
    assert.ok(VIEWS.includes(view), `${symptom.id}: ${symptom.link[0]}`);
    if (view === 'ready' && sub) assert.ok(GATE_IDS.includes(sub), `${symptom.id}: ${sub}`);
    if (view === 'shoot' && sub) assert.ok(SHOOT_PANES.includes(sub), `${symptom.id}: ${sub}`);
  }
});
