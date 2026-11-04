'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict'),effect=require('./effect-round03.js');
const out=process.argv[2];if(!out)throw Error('output path required');const sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const original=JSON.parse(fs.readFileSync(path.join(out,'processing.json'),'utf8')),[w,h]=original.base.size;
assert.equal(sha(path.join(out,'base.png')),original.base.sha256,'frozen base hash mismatch');
const source=new Uint8ClampedArray(fs.readFileSync(path.join(out,'base.rgba'))),t=performance.now(),actual=effect.process(source,w,h),again=effect.process(source,w,h),off=effect.process(source,w,h,{strength:0});
let alpha=0,hidden=0,offMax=0,repeat=0,changed=0;
for(let i=0;i<w*h;i++){let different=false;for(let c=0;c<4;c++){const j=i*4+c;offMax=Math.max(offMax,Math.abs(source[j]-off[j]));repeat=Math.max(repeat,Math.abs(actual[j]-again[j]));if(c===3)alpha=Math.max(alpha,Math.abs(actual[j]-source[j]));if(!source[i*4+3])hidden=Math.max(hidden,Math.abs(actual[j]-source[j]));if(c<3&&actual[j]!==source[j])different=true;}if(different&&source[i*4+3])changed++;}
const checks={frozen_base_hash_match:true,alpha_max_delta:alpha,transparent_rgba_max_delta:hidden,off_max_delta:offMax,repeat_max_delta:repeat,changed_visible_pixels:changed,three_render_ms:performance.now()-t};assert.equal(alpha+hidden+offMax+repeat,0);assert(changed>0);
for(const[name,strength]of[['default',1],['soft',.75],['strong',1.2]])fs.writeFileSync(path.join(out,`round-03-${name}.rgba`),effect.process(source,w,h,{strength}));
const template=fs.readFileSync(path.join(__dirname,'index.template.html'),'utf8'),old=fs.readFileSync(path.join(out,'preview.html'),'utf8'),style=old.match(/<style>([\s\S]*?)\.wide\{/)[1],uri='data:image/png;base64,'+fs.readFileSync(path.join(out,'base.png')).toString('base64');
let html=template.replace('__STYLE__',style).replace('__EFFECT__',fs.readFileSync(path.join(__dirname,'effect-round03.js'),'utf8')).replace('__BASE_URI__',uri).replaceAll('細かいざらつき','粒の整理').replaceAll('初回の値','3版目の値').replace('静止画の追加加工','静止画の追加加工・3版目');
fs.writeFileSync(path.join(out,'round-03-preview.html'),html);
const manifest={round:3,base:{...original.base,source_for_this_round:path.join(out,'base.png'),frozen:true},parameters:effect.defaults,implementation_sha256:sha(path.join(__dirname,'effect-round03.js')),checks,notes:['5x5 bilateral smoothing with luminance range sigma .035','continuous piecewise tone curve through middle tones; target luminance .008..85','edge-local mixed 2..7px / 8..20px horizontal substitution; no random grain'],reference_roi_usage:'診断のみ。参照の全画面分布と面積比は使用しない。'};
fs.writeFileSync(path.join(out,'round-03-processing.json'),JSON.stringify(manifest,null,2)+'\n');console.log(JSON.stringify(checks,null,2));
