'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const effect=require('./effect.js');
const dir=process.argv[2];if(!dir)throw Error('output directory required');
const m=JSON.parse(fs.readFileSync(path.join(dir,'processing.json'),'utf8')),[w,h]=m.base.size;
const source=new Uint8ClampedArray(fs.readFileSync(path.join(dir,'base.rgba'))),expected=fs.readFileSync(path.join(dir,'python-default.rgba'));
const actual=effect.process(source,w,h),off=effect.process(source,w,h,{strength:0});
let max=0,count=0;for(let i=0;i<actual.length;i++){const d=Math.abs(actual[i]-expected[i]);max=Math.max(max,d);if(d)count++;}
assert.deepEqual(off,source);assert.equal(max,0,'Python / JavaScript discrepancy');
assert.equal(actual.length,w*h*4);assert(actual.some((v,i)=>v!==source[i]));
const result={python_js_max_channel_delta:max,python_js_changed_channels:count,off_exact:true,size:[w,h],input_bytes:source.length};
fs.writeFileSync(path.join(dir,'javascript-check.json'),JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result,null,2));
