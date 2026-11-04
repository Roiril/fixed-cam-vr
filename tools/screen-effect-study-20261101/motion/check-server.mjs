import assert from 'node:assert/strict';import {resolveRequestPath} from './serve.mjs';
for(const attack of ['/../motion-sibling/private.txt','/%2e%2e/motion-sibling/private.txt','/..%5cmotion-sibling/private.txt','/C:/Windows/win.ini','/%00foo','/%E0%A4%A'])assert.equal(resolveRequestPath(attack),null,attack);
assert.ok(resolveRequestPath('/motion.js').endsWith('motion.js'));
assert.ok(resolveRequestPath('/base.png').endsWith('base.png'));
assert.ok(resolveRequestPath('/').endsWith('index.html'));
console.log('path containment: 6 rejected / 3 accepted PASS');
