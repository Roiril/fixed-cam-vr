import fs from 'node:fs';import path from 'node:path';import {pathToFileURL} from 'node:url';import crypto from 'node:crypto';
const out='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/smoke-proof';fs.mkdirSync(out,{recursive:true});
// Exercise the public canvas API without a browser or a shared port.
globalThis.ImageData=class{constructor(data,width,height){Object.assign(this,{data,width,height});}};
globalThis.document={createElement:()=>({width:0,height:0,getContext(){return{putImageData:d=>this.image=d};}})};
const {drawSmokeAt,smokeFieldProvenance}=await import(pathToFileURL(path.resolve(import.meta.dirname,'smoke-fields.js')).href);
const times=[3.2,4,4.1,4.2,4.4,4.7,12,12.4,12.6,13.6],records=[];
const hash=d=>crypto.createHash('sha256').update(d).digest('hex');
function sample(t,opts={}){return drawSmokeAt(t,720,540,{diagnostics:true,...opts});}
for(const t of times){const r=sample(t),layers={old:r.oldVisibilityCanvas,new:r.newVisibilityCanvas,density:r.smokeDensityCanvas,particles:r.edgeParticlesCanvas,...r.fields};const row={t,layers:{},stats:{}};
  for(const [name,c] of Object.entries(layers)){const p=path.join(out,`${t.toFixed(3)}-${name}.rgba`),data=Buffer.from(c.image.data);fs.writeFileSync(p,data);let sum=0,partial=0,min=255,max=0;const alpha=name==='particles';for(let i=alpha?3:0;i<data.length;i+=4){const v=data[i];sum+=v;partial+=v>0&&v<255;min=Math.min(min,v);max=Math.max(max,v);}row.layers[name]={path:p,width:720,height:540,sha256:hash(data)};row.stats[name]={mean:sum/(720*540*255),partialPixels:partial,min,max};}
  records.push(row);
}
const first=hash(Buffer.from(sample(4.2).newVisibilityCanvas.image.data));sample(12.4);const again=hash(Buffer.from(sample(4.2).newVisibilityCanvas.image.data));if(first!==again)throw Error('Non deterministic smoke');
const off=sample(4.2,{enabled:false});for(const [name,c,expect] of [['old',off.oldVisibilityCanvas,0],['new',off.newVisibilityCanvas,255],['density',off.smokeDensityCanvas,0]]){for(let i=0;i<c.image.data.length;i+=4)if(c.image.data[i]!==expect)throw Error('OFF not identity '+name);}
const hold=sample(13.6);for(let i=0;i<hold.oldVisibilityCanvas.image.data.length;i+=4)if(hold.oldVisibilityCanvas.image.data[i]||hold.newVisibilityCanvas.image.data[i]||hold.edgeParticlesCanvas.image.data[i+3])throw Error('Hold is not black');
fs.writeFileSync(path.join(out,'manifest.json'),JSON.stringify({provenance:smokeFieldProvenance,times,records,checks:{deterministicSeek:true,offIdentity:true,blackHold:true,referenceRGBIncluded:false}},null,2));
console.log(JSON.stringify({times:times.length,fieldCanvases:80,deterministicSeek:true,offIdentity:true,blackHold:true}));
