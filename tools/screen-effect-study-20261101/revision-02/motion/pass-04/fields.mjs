import fs from'node:fs';import path from'node:path';import{pathToFileURL}from'node:url';
const root='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-04',out=path.join(root,'field-proof');fs.mkdirSync(out,{recursive:true});
globalThis.ImageData=class{constructor(data,width,height){Object.assign(this,{data,width,height});}};
globalThis.document={createElement:()=>({width:0,height:0,getContext(){return{putImageData:d=>this.image=d};}})};
const{drawSmokeAt}=await import(pathToFileURL(path.resolve(import.meta.dirname,'smoke-fields.js')).href);
const times=[3.2,3.6,3.8,4,4.1,4.2,4.4,4.7,5,12,12.4,12.6,13.6],rows=[];
for(const t of times){const f=drawSmokeAt(t,720,540,{diagnostics:true}),row={time:t,fields:{}};for(const[name,c]of Object.entries({old:f.oldVisibilityCanvas,new:f.newVisibilityCanvas,density:f.smokeDensityCanvas,particles:f.edgeParticlesCanvas,...f.fields})){const p=path.join(out,`${t.toFixed(3)}-${name}.rgba`);fs.writeFileSync(p,Buffer.from(c.image.data));row.fields[name]=p;}rows.push(row);}
fs.writeFileSync(path.join(out,'manifest.json'),JSON.stringify({size:[720,540],rows},null,2));console.log('13 deterministic field decompositions saved');
