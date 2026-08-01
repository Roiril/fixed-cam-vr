// 体験の骨格の「配線」を固定する。判定そのものは run-model.test.mjs が見ている。
//
// ここで見るのは **同じ値が複数の面に散っていないか** の 1 点だけ。
//   卓の既定（run-model.js の RUN_DEFAULT）／ サーバの初期 show.json（capture-server.py）
//   ／ 卓の UI（index.html の入力欄）／ リボン・シミュレータが読む関数
// が食い違うと、どの面も自分の値では正しく動くので**誰も気づけない**。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { RUN_DEFAULT } from './run-model.js';

const read = (name) => readFile(new URL(`./${name}`, import.meta.url), 'utf8');

test('サーバの初期 show.json が run-model.js の既定と一致している', async () => {
  const py = await read('capture-server.py');
  const m = py.match(/'run':\s*\{([\s\S]*?)'intro':/);
  assert.ok(m, "_default_show に run が無い");
  const body = m[1];
  assert.match(body, new RegExp(`'totalLaps':\\s*${RUN_DEFAULT.totalLaps}\\b`));
  assert.match(body, new RegExp(`'endGraceSec':\\s*${RUN_DEFAULT.endGraceSec}\\b`));
  assert.match(body, new RegExp(`'endHoldMaxSec':\\s*${RUN_DEFAULT.endHoldMaxSec}\\b`));
  assert.match(body, new RegExp(`'hardLimitSec':\\s*${RUN_DEFAULT.hardLimitSec}\\b`));
});

test('サーバの初期 layout が構える高さを持つ（0 = 床に着ける）', async () => {
  const py = await read('capture-server.py');
  assert.match(py, /'regTouchHeightM':\s*0\b/);
});

test('卓の UI に猶予・上限・構える高さの入力欄がある', async () => {
  const html = await read('index.html');
  for (const id of ['runEndGrace', 'runEndHold']) {
    assert.match(html, new RegExp(`id="${id}"`), `${id} の入力欄が無い`);
  }
  const fm = await read('floormap.js');
  assert.match(fm, /class="fm-reg-h"/, '構える高さの入力欄が無い');
  assert.match(fm, /layout\.regTouchHeightM/, '構える高さが layout へ書かれていない');
});

test('周の並び・終端・本番前チェックがすべて run-model.js を読む', async () => {
  // 同じ判定を各面が自前で書き直すと、卓とシミュレータと実機で答えがずれる。
  for (const f of ['app.js', 'ribbon.js', 'show-sim.js']) {
    assert.match(await read(f), /from '\.\/run-model\.js'/, `${f} が run-model.js を読んでいない`);
  }
});

test('リボンは周数を自前で増減しない（run.totalLaps が単一の正）', async () => {
  const rb = await read('ribbon.js');
  assert.doesNotMatch(rb, /rb-lap-add|rb-lap-del/, '周回の増減ボタンが残っている');
  assert.doesNotMatch(rb, /\blapCount\b/, 'リボン内に独自の周数が残っている');
});
