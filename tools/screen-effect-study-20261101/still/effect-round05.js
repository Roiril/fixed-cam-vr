/* Round05: coherent surviving detail, intermittent edge faults, independent surface fragments. */
(function(root){'use strict';
const defaults={strength:1,tear:.9,echo:.9,grain:0,contrast:1};
function hash(x,y){let v=(Math.imul(x,374761393)^Math.imul(y,668265263)^104729)>>>0;v=Math.imul(v^(v>>>13),1274126177)>>>0;return((v^(v>>>16))>>>0)/4294967295;}
function box(a,w,h,r){const stride=w+1,s=new Float64Array((w+1)*(h+1)),o=new Float64Array(w*h);for(let y=0;y<h;y++){let v=0;for(let x=0;x<w;x++){v+=a[y*w+x];s[(y+1)*stride+x+1]=s[y*stride+x+1]+v;}}for(let y=0;y<h;y++)for(let x=0;x<w;x++){const x0=Math.max(0,x-r),x1=Math.min(w,x+r+1),y0=Math.max(0,y-r),y1=Math.min(h,y+r+1);o[y*w+x]=(s[y1*stride+x1]-s[y0*stride+x1]-s[y1*stride+x0]+s[y0*stride+x0])/((x1-x0)*(y1-y0));}return o;}
function process(source,w,h,options={}){const p={...defaults,...options},lookup=options.lookup||root.StillMeasuredLookup,out=new Uint8ClampedArray(source);if(!p.strength)return out;if(!lookup)throw Error('measured lookup required');
const n=w*h,lum=new Float64Array(n);for(let i=0;i<n;i++)lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;
const m1=box(lum,w,h,1),m3=box(lum,w,h,3),m7=box(lum,w,h,7),m15=box(lum,w,h,15),xx=new Float64Array(n),yy=new Float64Array(n),xy=new Float64Array(n);
for(let y=0;y<h;y++)for(let x=0;x<w;x++){const i=y*w+x,gx=m1[y*w+Math.min(w-1,x+2)]-m1[y*w+Math.max(0,x-2)],gy=m1[Math.min(h-1,y+2)*w+x]-m1[Math.max(0,y-2)*w+x];xx[i]=gx*gx;yy[i]=gy*gy;xy[i]=gx*gy;}
const jx=box(xx,w,h,4),jy=box(yy,w,h,4),jc=box(xy,w,h,4),confidence=new Float64Array(n),detail=new Float64Array(n),pre=new Float64Array(n);
const knots=[[0,.008],[.07,.015],[.15,.09],[.35,.34],[.55,.63],[.75,.845],[1,.85]];function tone(v){v=Math.max(0,Math.min(1,v));let k=1;while(knots[k][0]<v)k++;const a=knots[k-1],b=knots[k];return a[1]+(b[1]-a[1])*(v-a[0])/(b[0]-a[0]);}
for(let i=0;i<n;i++){
const coherence=Math.sqrt((jx[i]-jy[i])**2+4*jc[i]*jc[i])/(jx[i]+jy[i]+.00002),d1=m1[i]-m3[i],d2=m3[i]-m7[i],d3=m7[i]-m15[i];
const persistent=d1*d2>0?1:.2,energy=Math.max(0,Math.min(1,(Math.abs(d1)+Math.abs(d2)-.004)/.025)),c=Math.max(0,Math.min(1,(coherence-.20)/.65))*energy*persistent;confidence[i]=c;detail[i]=d1+d2;
// Suppress uncorrelated high-frequency grain only. No full-frame blur or unsharp.
const cleaned=lum[i]-(lum[i]-m1[i])*(1-c)*.65,boost=(d1*2.8+d2*1.8+d3*.35)*c*p.contrast;
pre[i]=Math.max(.008,Math.min(.85,tone(cleaned)+boost));
}
const count=lookup.row_count,activity=new Float64Array(count);for(let y=0;y<count;y++){let s=0;for(let k=-6;k<=6;k++)s+=Math.abs(lookup.jitter[(y+k+count)%count]);activity[y]=s/13;}const sorted=Array.from(activity).sort((a,b)=>a-b),threshold=sorted[Math.floor(count*.68)];
function read(a,x,y){return a[y*w+Math.max(0,Math.min(w-1,x))];}
for(let y=0;y<h;y++){
const edges=[];for(let x=9;x<w-9;x++){const g=read(m1,x+3,y)-read(m1,x-3,y),c=read(m1,x+8,y)-read(m1,x-8,y);if(Math.abs(c)>.16&&Math.abs(g)>.035&&Math.abs(g)>=Math.abs(read(m1,x+2,y)-read(m1,x-4,y))&&Math.abs(g)>Math.abs(read(m1,x+4,y)-read(m1,x-2,y))){if(edges.length&&x-edges[edges.length-1].x<10){if(Math.abs(g)>Math.abs(edges[edges.length-1].g))edges[edges.length-1]={x,g,c};}else edges.push({x,g,c});}}
let nearest=0;for(let x=0;x<w;x++){const i=y*w+x,j=i*4;if(!source[j+3])continue;while(nearest+1<edges.length&&Math.abs(edges[nearest+1].x-x)<Math.abs(edges[nearest].x-x))nearest++;const e=edges[nearest],distance=e?x-e.x:999,row=e?(y+Math.floor(e.x/160)*37)%count:0,gate=e?Math.max(0,Math.min(1,(Math.abs(e.c)-.15)/.30)):0;
let target=pre[i];const active=activity[row]>threshold||Math.abs(lookup.jitter[row])>10;
if(e&&active&&Math.abs(distance)<=28){const shift=lookup.jitter[row]*.78*p.tear*gate,sx=Math.max(0,Math.min(w-1,x+Math.floor(shift+.5)));target=pre[y*w+sx];if(Math.abs(distance)<=24)target+=lookup.residual[row][distance+32]*Math.abs(e.c)*Math.sign(e.c)*.4*p.echo*gate;}
// Separate sparse surface interruptions use measured run lengths, not the edge residual.
if(confidence[i]>.4&&Math.abs(detail[i])>.01){const cx=Math.floor(x/64),cy=Math.floor(y/6),event=hash(cx,cy);if(event>.965){const phase=(cy*17+cx*31)%count,runs=lookup.runs_threshold_008[phase];if(runs.length){const run=runs[Math.floor(hash(cx+11,cy)*runs.length)],length=Math.max(2,Math.min(20,run.length)),start=Math.floor(hash(cx+29,cy)*Math.max(1,64-length));if(x%64>=start&&x%64<start+length&&y%6<1+Math.floor(hash(cx+47,cy)*3)){const nx=Math.max(0,Math.min(w-1,x+run.sign*length));target=target+(pre[y*w+nx]-target)*.85*p.echo;}}}}
if(p.grain>0)target+=(hash(x,y)-.5)*.012*p.grain*confidence[i];target=lum[i]+(target-lum[i])*Math.min(1.5,p.strength);target=Math.max(.008,Math.min(.85,target));const gain=lum[i]>.00001?target/lum[i]:0;for(let c=0;c<3;c++)out[j+c]=Math.floor(Math.max(0,Math.min(1,source[j+c]/255*gain))*255+.5);
}}
return out;}
const api={defaults,process};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
