/* Round 03: continuous middle tones, bilateral grain cleanup, edge-only line faults. */
(function(root){
 'use strict';
 const defaults={strength:1,tear:.9,echo:.9,grain:.8,contrast:.65,shadow:.8,seed:104729};
 function hash(x,y,s){let v=(Math.imul(x,374761393)^Math.imul(y,668265263)^Math.imul(s,1274126177))>>>0;v=Math.imul(v^(v>>>13),1274126177)>>>0;return((v^(v>>>16))>>>0)/4294967295;}
 function process(source,w,h,options={}){
  const p={...defaults,...options},out=new Uint8ClampedArray(source);if(!p.strength)return out;
  const lum=new Float64Array(w*h),clean=new Float64Array(w*h),rangeWeight=new Float64Array(1024),kernel=[1,2,3,2,1];
  for(let i=0;i<lum.length;i++)lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;
  for(let i=0;i<1024;i++)rangeWeight[i]=Math.exp(-.5*(i/1023/.035)**2);
  for(let y=0;y<h;y++)for(let x=0;x<w;x++){
   const i=y*w+x;if(!source[i*4+3]){clean[i]=lum[i];continue;}let sum=0,total=0;
   for(let dy=-2;dy<=2;dy++)for(let dx=-2;dx<=2;dx++){
    const ni=Math.max(0,Math.min(h-1,y+dy))*w+Math.max(0,Math.min(w-1,x+dx));if(!source[ni*4+3])continue;
    const k=Math.min(1023,Math.floor(Math.abs(lum[ni]-lum[i])*1023+.5)),wt=kernel[dy+2]*kernel[dx+2]*rangeWeight[k];sum+=lum[ni]*wt;total+=wt;
   }clean[i]=lum[i]+(sum/total-lum[i])*Math.min(1,p.grain);
  }
  const knots=[[0,.008],[.07,.015],[.15,.09],[.35,.34],[.55,.63],[.75,.845],[1,.85]];
  function tone(v){v=Math.max(0,Math.min(1,v));let n=1;while(knots[n][0]<v)n++;const[a,b]=[knots[n-1],knots[n]];return a[1]+(b[1]-a[1])*(v-a[0])/(b[0]-a[0]);}
  function l(x,y){return clean[Math.max(0,Math.min(h-1,y))*w+Math.max(0,Math.min(w-1,x))];}
  for(let y=0;y<h;y++){
   const row=hash(0,y,p.seed),direction=hash(1,y,p.seed)>.5?1:-1;
   const shift=row<.5?0:row<.86?direction*(1+Math.floor(hash(2,y,p.seed)*2))*p.tear:direction*(3+Math.floor(hash(3,y,p.seed)*5))*p.tear;
   const length=hash(4,y,p.seed)<.65?2+Math.floor(hash(5,y,p.seed)*6):8+Math.floor(hash(6,y,p.seed)*13);
   for(let x=0;x<w;x++){
    const i=y*w+x,j=i*4;if(!source[j+3])continue;
    const lo=Math.min(l(x-6,y),l(x+6,y)),hi=Math.max(l(x-6,y),l(x+6,y)),edge=Math.max(0,Math.min(1,(hi-lo-.10)*4));
    const sx=Math.max(0,Math.min(w-1,x+Math.floor(shift*edge+.5))),si=y*w+sx,valid=source[si*4+3]>0;
    const old=valid?clean[si]:clean[i],detail=old-(l(sx-3,y)+l(sx+3,y))*.5;
    let target=tone(old+detail*.45*p.contrast*edge);
    const cell=Math.floor(x/length),neighbor=l(sx+direction*length,y);
    if(edge>.25&&hi-lo>.20&&Math.abs(neighbor-old)>.15&&hash(cell,y,p.seed+7)>.7){
     target+=(tone(neighbor)-target)*Math.min(1,p.echo*.75);
    }
    // Small luminance ringing is attached to a real boundary; no dark-interior sparks.
    target+=(l(sx-2,y)-l(sx+3,y))*.09*p.echo*edge;
    if(target<.05)target=.01+(target-.01)*(1-.25*p.shadow);
    target=lum[i]+(target-lum[i])*Math.min(1.5,p.strength);target=Math.max(.008,Math.min(.85,target));
    const sourceLum=valid?lum[si]:lum[i],gain=sourceLum>.00001?target/sourceLum:0,sj=valid?si*4:j;
    for(let c=0;c<3;c++)out[j+c]=Math.floor(Math.max(0,Math.min(1,source[sj+c]/255*gain))*255+.5);
   }
  }return out;
 }
 const api={defaults,hash,process};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
