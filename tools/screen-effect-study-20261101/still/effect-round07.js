/* Round07: row-coherent horizontal slivers. Generated target is never sampled. */
(function(root){'use strict';const defaults={strength:1,tear:1.1,echo:.85,grain:1,contrast:.7};
function process(source,w,h,options={}){const p={...defaults,...options},lookup=options.lookup||root.StillMeasuredLookup,out=new Uint8ClampedArray(source);if(!p.strength)return out;if(!lookup)throw Error('measured lookup required');const n=w*h,lum=new Float64Array(n),clean=new Float64Array(n),five=new Float64Array(5);
for(let i=0;i<n;i++)lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;
// Vertical-only median suppresses fine grain without rounding shapes in x/y.
for(let y=0;y<h;y++)for(let x=0;x<w;x++){const i=y*w+x;for(let k=-2;k<=2;k++)five[k+2]=lum[Math.max(0,Math.min(h-1,y+k))*w+x];five.sort();clean[i]=lum[i]+(five[2]-lum[i])*Math.min(1,p.grain);}
function l(x,y){return clean[y*w+Math.max(0,Math.min(w-1,x))];}
const knots=[[0,.008],[.07,.015],[.15,.09],[.35,.34],[.55,.63],[.75,.845],[1,.85]];function tone(v){v=Math.max(0,Math.min(1,v));let k=1;while(knots[k][0]<v)k++;const a=knots[k-1],b=knots[k];return a[1]+(b[1]-a[1])*(v-a[0])/(b[0]-a[0]);}
for(let y=0;y<h;y++){
const row=y%lookup.row_count;let slow=0;for(let k=-7;k<=7;k++)slow+=lookup.jitter[(row+k+lookup.row_count)%lookup.row_count]/15;const jitter=lookup.jitter[row]-slow,shift=Math.round(jitter*.78*p.tear),runs=lookup.runs_threshold_008[row],run=runs.length?runs[row%runs.length]:{length:3,sign:jitter>0?1:-1},length=Math.max(2,Math.min(8,run.length)),direction=run.sign;
const sparse=Math.abs(jitter)>4?1:.4,shortEcho=(Math.abs(jitter)>8?1:.5)*p.echo;
for(let x=0;x<w;x++){
const i=y*w+x,j=i*4;if(!source[j+3])continue;let sx=Math.max(0,Math.min(w-1,x+shift));if(!source[(y*w+sx)*4+3])sx=x;
const c=l(sx,y),left=l(sx-3,y),right=l(sx+3,y),near=l(sx-direction*length,y),far=l(sx-direction*(length+2),y),gradient=Math.abs(left-right),edge=Math.min(1,gradient*5);
// All spatial filtering is horizontal. No isotropic local threshold / texture gain.
let signal=c+(c-(left+right)*.5)*p.contrast*1.0;
signal+=(c-near)*.30*p.echo*sparse;
signal+=(near-far)*.42*shortEcho;
signal=signal*(1-.12*p.echo*edge)+near*(.12*p.echo*edge);
let target=tone(signal);target=lum[i]+(target-lum[i])*Math.min(1.5,p.strength);target=Math.max(.008,Math.min(.85,target));
const si=y*w+sx,raw=lum[si],gain=raw>.00001?target/raw:0;for(let ch=0;ch<3;ch++)out[j+ch]=Math.floor(Math.max(0,Math.min(1,source[si*4+ch]/255*gain))*255+.5);
}}
return out;}
const api={defaults,process};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
