import assert from 'node:assert/strict';
import {test} from 'node:test';
import {resolveLibraryPreview} from './ops-library-preview.js';

const media = (key, name, kind='image') => ({key, kind, current:{url:`/captures/${name}`,name,exists:true}});
const show = {
  post:{exposure:-1.5},
  cameras:[{id:'A',host:'192.168.10.21',port:8080,post:{exposure:-1}}],
  cues:[{id:'stain',camera:'A',sourceUrl:'/captures/stain.mp4',maskUrl:'/masks/stain.png',strength:.8,fadeIn:.6}],
  timeline:{segments:[{camera:0,hasPost:true,post:{exposure:-.5},takes:[{
    id:'L2C0#0',steps:[{source:'inherit',camera:-1,cueId:'stain',strength:-1,fadeInSec:-1,
      trimStartSec:-1,trimEndSec:-1,hasPost:false}]}]}]},
};
const effect = {id:'L2C0#0',assets:[media('step:0:sourceUrl','stain.mp4','video'),
  {key:'step:0:maskUrl',kind:'image',current:{url:'/masks/stain.png',exists:true}}]};

test('mask selection previews its video over the assigned live camera',()=>{
  const preview=resolveLibraryPreview(show,effect,effect.assets[1]);
  assert.equal(preview.camera.id,'A');
  assert.equal(preview.live,true);
  assert.equal(preview.source.name,'stain.mp4');
  assert.equal(preview.mask.url,'/masks/stain.png');
  assert.equal(preview.strength,.8);
  assert.equal(preview.fadeSec,.6);
  assert.equal(preview.post.exposure,-.5);
});

test('step overrides camera, source and post without using an unrelated mask',()=>{
  const copy=structuredClone(show);
  copy.cameras.push({id:'B',host:'192.168.10.22',port:8080});
  const step=copy.timeline.segments[0].takes[0].steps[0];
  Object.assign(step,{camera:1,assetUrl:'/recordings/other.mp4',strength:.4,hasPost:true,post:{exposure:.25}});
  const changed={...effect,assets:[...effect.assets,media('step:0:assetUrl','other.mp4','video')]};
  const preview=resolveLibraryPreview(copy,changed,changed.assets[2]);
  assert.equal(preview.camera.id,'B');
  assert.equal(preview.source.name,'other.mp4');
  assert.equal(preview.strength,.4);
  assert.equal(preview.post.exposure,.25);
});

test('full replacement is not labeled a live composite',()=>{
  const copy=structuredClone(show);
  copy.timeline.segments[0].takes[0].steps[0].source='clip';
  assert.equal(resolveLibraryPreview(copy,effect,effect.assets[0]).live,false);
});

test('camera post applies when the step and segment have no override',()=>{
  const copy=structuredClone(show);
  copy.timeline.segments[0].hasPost=false;
  assert.equal(resolveLibraryPreview(copy,effect,effect.assets[0]).post.exposure,-1);
});

test('a mask required by the step cannot silently become a full-frame overlay',()=>{
  const copy=structuredClone(show);
  copy.cues[0].maskUrl='';
  copy.timeline.segments[0].takes[0].steps[0].maskUrl='/masks/stain.png';
  assert.equal(resolveLibraryPreview(copy,effect,effect.assets[0]).maskRequired,true);
});
