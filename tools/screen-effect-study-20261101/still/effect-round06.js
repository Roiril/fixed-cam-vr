/* Round06: local relative light/dark line fragments on textured surfaces. */
(function(root){'use strict';const defaults={strength:1,tear:.35,echo:1,grain:0,contrast:1};
function hash(x,y){let v=(Math.imul(x,374761393)^Math.imul(y,668265263)^104729)>>>0;v=Math.imul(v^(v>>>13),1274126177)>>>0;return((v^(v>>>16))>>>0)/4294967295;}
function box(a,w,h,r){const stride=w+1,s=new Float64Array((w+1)*(h+1)),o=new Float64Array(w*h);for(let y=0;y<h;y++){let v=0;for(let x=0;x<w;x++){v+=a[y*w+x];s[(y+1)*stride+x+1]=s[y*stride+x+1]+v;}}for(let y=0;y<h;y++)for(let x=0;x<w;x++){const x0=Math.max(0,x-r),x1=Math.min(w,x+r+1),y0=Math.max(0,y-r),y1=Math.min(h,y+r+1);o[y*w+x]=(s[y1*stride+x1]-s[y0*stride+x1]-s[y1*stride+x0]+s[y0*stride+x0])/((x1-x0)*(y1-y0));}return o;}
function process(source,w,h,options={}){const p={...defaults,...options},lookup=options.lookup||root.StillMeasuredLookup,out=new Uint8ClampedArray(source);if(!p.strength)return out;if(!lookup)throw Error('measured lookup required');const n=w*h,lum=new Float64Array(n);for(let i=0;i<n;i++)lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;
const median=new Float64Array(n),samples=new Float64Array(9);for(let y=0;y<h;y++)for(let x=0;x<w;x++){let k=0;for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++)samples[k++]=lum[Math.max(0,Math.min(h-1,y+dy))*w+Math.max(0,Math.min(w-1,x+dx))];samples.sort();median[y*w+x]=samples[4];}
const small=box(median,w,h,1),m2=box(small,w,h,2),m4=box(small,w,h,4),m8=box(small,w,h,8),sq=new Float64Array(n),fine2=new Float64Array(n),coarse2=new Float64Array(n);
for(let i=0;i<n;i++){sq[i]=small[i]*small[i];fine2[i]=(small[i]-m2[i])**2;coarse2[i]=(m2[i]-m8[i])**2;}
const varianceMean=box(sq,w,h,4),fineEnergy=box(fine2,w,h,4),coarseEnergy=box(coarse2,w,h,4),texture=new Float64Array(n),pre=new Float64Array(n);
const knots=[[0,.008],[.07,.015],[.15,.09],[.35,.34],[.55,.63],[.75,.845],[1,.85]];function tone(v){v=Math.max(0,Math.min(1,v));let k=1;while(knots[k][0]<v)k++;const a=knots[k-1],b=knots[k];return a[1]+(b[1]-a[1])*(v-a[0])/(b[0]-a[0]);}
for(let i=0;i<n;i++){
 const fe=Math.sqrt(fineEnergy[i]),ce=Math.sqrt(coarseEnergy[i]),ratio=fe/(ce+.003),energyGate=Math.max(0,Math.min(1,(fe-.0025)/.0055)),scaleGate=Math.max(0,Math.min(1,(ratio-.09)/.28));
 const t=energyGate*scaleGate;texture[i]=t;const std=Math.sqrt(Math.max(0,varianceMean[i]-m4[i]*m4[i])),px=i%w,supported=(small[i]*2+small[i-px+Math.max(0,px-2)]+small[i-px+Math.min(w-1,px+2)])*.25,normalized=(supported-m4[i])/Math.max(.006,std);
 const fragments=Math.max(.01,Math.min(.85,.43+.42*Math.tanh(normalized*2.2*p.contrast))),continuous=tone(lum[i]-(lum[i]-small[i])*(1-t)*.5);pre[i]=continuous+(fragments-continuous)*t;
}
const count=lookup.row_count,activity=new Float64Array(count);for(let y=0;y<count;y++){let s=0;for(let k=-6;k<=6;k++)s+=Math.abs(lookup.jitter[(y+k+count)%count]);activity[y]=s/13;}const sorted=Array.from(activity).sort((a,b)=>a-b),threshold=sorted[Math.floor(count*.76)];
function read(a,x,y){return a[y*w+Math.max(0,Math.min(w-1,x))];}
for(let y=0;y<h;y++){
 const edges=[];for(let x=9;x<w-9;x++){const g=read(small,x+3,y)-read(small,x-3,y),c=read(small,x+8,y)-read(small,x-8,y);if(Math.abs(c)>.20&&Math.abs(g)>.055&&Math.abs(g)>=Math.abs(read(small,x+2,y)-read(small,x-4,y))&&Math.abs(g)>Math.abs(read(small,x+4,y)-read(small,x-2,y))){if(edges.length&&x-edges[edges.length-1].x<12){if(Math.abs(g)>Math.abs(edges[edges.length-1].g))edges[edges.length-1]={x,g,c};}else edges.push({x,g,c});}}
 let nearest=0;for(let x=0;x<w;x++){const i=y*w+x,j=i*4;if(!source[j+3])continue;while(nearest+1<edges.length&&Math.abs(edges[nearest+1].x-x)<Math.abs(edges[nearest].x-x))nearest++;const e=edges[nearest],distance=e?x-e.x:999,row=e?(y+Math.floor(e.x/160)*37)%count:0,gate=e?Math.max(0,Math.min(1,(Math.abs(e.c)-.2)/.30)):0;let target=pre[i];
 if(e&&activity[row]>threshold&&Math.abs(distance)<=20){const sx=Math.max(0,Math.min(w-1,x+Math.floor(lookup.jitter[row]*.78*p.tear*gate+.5)));target=pre[y*w+sx]+lookup.residual[row][distance+32]*Math.abs(e.c)*Math.sign(e.c)*.18*p.echo*gate;}
 // Dense short horizontal interruptions on texture; independent from silhouette faults.
 if(texture[i]>.35){const cx=Math.floor(x/16),cy=Math.floor(y/4),phase=(cy*17+cx*31)%count,runs=lookup.runs_threshold_008[phase];if(hash(cx,cy)>.35&&runs.length){const run=runs[Math.floor(hash(cx+11,cy)*runs.length)],length=Math.max(3,Math.min(14,run.length)),height=1+Math.floor(hash(cx+29,cy)*4),start=Math.floor(hash(cx+47,cy)*(16-length));if(y%4<height&&x%16>=start&&x%16<start+length){const nx=Math.max(0,Math.min(w-1,x+run.sign*length));target+=(pre[y*w+nx]-target)*Math.min(1,p.echo)*texture[i];}}}
 if(p.grain>0)target+=(hash(x,y)-.5)*.01*p.grain*texture[i];target=lum[i]+(target-lum[i])*Math.min(1.5,p.strength);target=Math.max(.008,Math.min(.85,target));const gain=lum[i]>.00001?target/lum[i]:0;for(let c=0;c<3;c++)out[j+c]=Math.floor(Math.max(0,Math.min(1,source[j+c]/255*gain))*255+.5);
 }}
if(options.debug){for(const[name,box]of Object.entries({floor:[420,800,860,940],curtain:[440,180,840,520],wall:[125,200,220,420]})){let sum=0,active=0,pixels=0;for(let y=box[1];y<Math.min(h,box[3]);y++)for(let x=box[0];x<Math.min(w,box[2]);x++){const t=texture[y*w+x];sum+=t;if(t>.35)active++;pixels++;}options.debug[name]={mean_texture_gate:sum/pixels,active_fraction:active/pixels};}}
return out;}
const api={defaults,process};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
