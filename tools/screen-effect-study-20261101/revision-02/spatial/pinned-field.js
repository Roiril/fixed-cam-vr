import {makeGeometry,project} from './geometry.js';
export const PIN_BAND_PX=12;
export function makePinnedGeometry(c,room){
 const g=makeGeometry(c,room),W=640,H=480,d=new Float32Array(W*H);d.fill(1e6);
 const id=(x,y)=>g.ownership[(y*W+x)*4];
 for(let y=0;y<H;y++)for(let x=0;x<W;x++){const i=y*W+x,a=id(x,y);if(x===0||y===0||x===W-1||y===H-1||id(x-1,y)!==a||id(x+1,y)!==a||id(x,y-1)!==a||id(x,y+1)!==a)d[i]=0;}
 const diag=Math.SQRT2;
 for(let y=0;y<H;y++)for(let x=0;x<W;x++){const i=y*W+x;if(x)d[i]=Math.min(d[i],d[i-1]+1);if(y){d[i]=Math.min(d[i],d[i-W]+1);if(x)d[i]=Math.min(d[i],d[i-W-1]+diag);if(x<W-1)d[i]=Math.min(d[i],d[i-W+1]+diag);}}
 for(let y=H-1;y>=0;y--)for(let x=W-1;x>=0;x--){const i=y*W+x;if(x<W-1)d[i]=Math.min(d[i],d[i+1]+1);if(y<H-1){d[i]=Math.min(d[i],d[i+W]+1);if(x)d[i]=Math.min(d[i],d[i+W-1]+diag);if(x<W-1)d[i]=Math.min(d[i],d[i+W+1]+diag);}}
 const ss=v=>{const t=Math.max(0,Math.min(1,v/PIN_BAND_PX));return t*t*(3-2*t);};
 const weights=Float32Array.from(d,ss);
 function weightAt(u,v){let x=u-.5,y=v-.5;if(x<0||y<0||x>=W-1||y>=H-1)return 0;const a=Math.floor(x),b=Math.floor(y),fx=x-a,fy=y-b;return weights[b*W+a]*(1-fx)*(1-fy)+weights[b*W+a+1]*fx*(1-fy)+weights[(b+1)*W+a]*(1-fx)*fy+weights[(b+1)*W+a+1]*fx*fy;}
 const vertexWeights=new Float32Array(g.triangles.length/7);for(let i=0;i<vertexWeights.length;i++){const j=i*7,uv=project(Array.from(g.triangles.subarray(j,j+3)),c);vertexWeights[i]=weightAt(uv[0],uv[1]);}
 const stats={};for(let i=0;i<W*H;i++){const id=g.ownership[i*4];const s=stats[id]??={pixels:0,fullyFree:0,fullyPinned:0,weightSum:0};s.pixels++;s.fullyFree+=weights[i]>.999;s.fullyPinned+=weights[i]===0;s.weightSum+=weights[i];}
 for(const s of Object.values(stats))s.meanWeight=s.weightSum/s.pixels;
 return {...g,vertexWeights,weights,distance:d,weightAt,weightWorld:p=>{const uv=project(p,c);return weightAt(...uv);},pinStats:stats,pinBandPx:PIN_BAND_PX,pinRule:'All ownership boundaries and image perimeter pinned; 0→12px smoothstep world displacement. Floor/curtain joins also constrained in this candidate.'};
}
