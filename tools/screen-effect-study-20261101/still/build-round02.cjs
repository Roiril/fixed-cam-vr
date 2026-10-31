'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const effect=require('./effect-round02.js');
const out=process.argv[2];if(!out)throw Error('output path required');
const round01=path.join(out,'round-01');fs.mkdirSync(round01,{recursive:true});
const previous=['analysis.md','base.png','base-on-black.png','comparison-curtain.png','comparison-full.png','comparison-rail.png','comparison-shadow.png','javascript-check.json','preview.html','processing.json','still-default.png','still-default-on-black.png','still-soft.png','still-strong.png'];
for(const name of previous){const dest=path.join(round01,name);if(!fs.existsSync(dest))fs.copyFileSync(path.join(out,name),dest);}
const original=JSON.parse(fs.readFileSync(path.join(out,'processing.json'),'utf8')),[w,h]=original.base.size,source=new Uint8ClampedArray(fs.readFileSync(path.join(out,'base.rgba')));
const start=performance.now(),actual=effect.process(source,w,h),again=effect.process(source,w,h),off=effect.process(source,w,h,{strength:0});
let maxAlpha=0,transparentDelta=0,offDelta=0,deterministicDelta=0,changed=0,abs=0,black=0,white=0,visible=0;
for(let i=0;i<w*h;i++){const j=i*4;maxAlpha=Math.max(maxAlpha,Math.abs(actual[j+3]-source[j+3]));let different=false;for(let c=0;c<4;c++){offDelta=Math.max(offDelta,Math.abs(off[j+c]-source[j+c]));deterministicDelta=Math.max(deterministicDelta,Math.abs(actual[j+c]-again[j+c]));if(!source[j+3])transparentDelta=Math.max(transparentDelta,Math.abs(actual[j+c]-source[j+c]));if(c<3&&actual[j+c]!==source[j+c])different=true;if(c<3&&source[j+3])abs+=Math.abs(actual[j+c]-source[j+c]);}if(source[j+3]){visible++;if(different)changed++;const v=(actual[j]*.2126+actual[j+1]*.7152+actual[j+2]*.0722)/255;if(v<.05)black++;if(v>.9)white++;}}
const checks={alpha_max_delta:maxAlpha,transparent_rgba_max_delta:transparentDelta,off_max_delta:offDelta,deterministic_max_delta:deterministicDelta,changed_visible_pixels:changed,visible_pixels:visible,mean_abs_rgb_delta:abs/(visible*3),black_below_005:black/visible,white_above_09:white/visible,render_ms:performance.now()-start};
if(maxAlpha||transparentDelta||offDelta||deterministicDelta||!changed)throw Error(JSON.stringify(checks));
for(const [name,strength]of [['default',1],['soft',.75],['strong',1.2]])fs.writeFileSync(path.join(out,`round-02-${name}.rgba`),effect.process(source,w,h,{strength}));
const template=fs.readFileSync(path.join(__dirname,'index.template.html'),'utf8'),oldPreview=fs.readFileSync(path.join(out,'preview.html'),'utf8'),style=oldPreview.match(/<style>([\s\S]*?)\.wide\{/)[1],base='data:image/png;base64,'+fs.readFileSync(path.join(out,'base.png')).toString('base64');
let html=template.replace('__STYLE__',style).replace('__EFFECT__',fs.readFileSync(path.join(__dirname,'effect-round02.js'),'utf8')).replace('__BASE_URI__',base).replaceAll('細かいざらつき','横筋の密度').replaceAll('初回の値','2版目の値').replace('静止画の追加加工','静止画の追加加工・2版目');
fs.writeFileSync(path.join(out,'round-02-preview.html'),html);
const sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const manifest={round:2,base:original.base,observation_reference:original.observation_reference,parameters:effect.defaults,variants:{default:1,soft:.75,strong:1.2},implementation_sha256:sha(path.join(__dirname,'effect-round02.js')),changes:['全面に加えるランダム粒は0','輝度曲線の暗部と明部に平坦部を作る','4..15pxの疎な横方向の延長と欠けを輪郭へ限定','無変位行と小変位行と大変位行を混ぜる'],checks};
fs.writeFileSync(path.join(out,'round-02-processing.json'),JSON.stringify(manifest,null,2)+'\n');console.log(JSON.stringify(checks,null,2));
