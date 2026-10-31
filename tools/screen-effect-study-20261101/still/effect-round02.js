/* Round 02: hard luminance plateaus and sparse horizontal edge failures. */
(function(root){
  'use strict';
  const defaults={strength:1,tear:1,echo:1,grain:.4,contrast:1,shadow:1,seed:104729};
  function hash(x,y,seed){let v=(Math.imul(x,374761393)^Math.imul(y,668265263)^Math.imul(seed,1274126177))>>>0;v=Math.imul(v^(v>>>13),1274126177)>>>0;return ((v^(v>>>16))>>>0)/4294967295;}
  function process(source,w,h,options={}){
    const p={...defaults,...options},out=new Uint8ClampedArray(source);if(p.strength===0)return out;
    const lum=new Float64Array(w*h);for(let i=0;i<lum.length;i++)lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;
    function l(x,y){return lum[Math.max(0,Math.min(h-1,y))*w+Math.max(0,Math.min(w-1,x))];}
    function tone(value){const t=Math.max(0,Math.min(1,(value-.16)/.40)),smooth=t*t*(3-2*t);return Math.max(0,Math.min(1,(smooth-.5)*(1+.65*p.contrast)+.5));}
    for(let y=0;y<h;y++){
      const row=hash(0,y,p.seed),direction=hash(1,y,p.seed)>.5?1:-1;
      const shift=row>.78?Math.floor((hash(2,y,p.seed)-.5)*18*p.tear+.5):row>.36?Math.floor((hash(3,y,p.seed)-.5)*5*p.tear+.5):0;
      const length=4+Math.floor(hash(4,y,p.seed)*12);
      for(let x=0;x<w;x++){
        const i=y*w+x,j=i*4;if(!source[j+3])continue;
        const original=lum[i],edge=Math.min(1,Math.abs(l(x+3,y)-l(x-3,y))*4+Math.abs(l(x+8,y)-l(x-8,y))*2);
        const sx=Math.max(0,Math.min(w-1,x+Math.floor(shift*edge+.5))),sj=(y*w+sx)*4,valid=source[sj+3]>0;
        const old=valid?lum[y*w+sx]:original;
        // A broad sharpening support counters the inherited diagonal softness.
        const detail=old-(l(sx-5,y)+l(sx+5,y)+l(sx,y-3)+l(sx,y+3))*.25;
        let target=tone(old+detail*.8);
        const cell=Math.floor(x/length),chance=hash(cell,y,p.seed+7),neighbor=l(sx+direction*length,y),difference=neighbor-old;
        if(chance>(.78-.2*p.grain)&&Math.abs(difference)>.075&&edge>.12){
          const displaced=tone(neighbor+detail*.5);
          target=target+(displaced-target)*Math.min(1,p.echo*.85);
          // Paired bright / dark rows create actual missing and extended parts.
          if(hash(cell,y,p.seed+8)>.72)target=difference>0?Math.max(target,.92):Math.min(target,.015);
        }
        if(target<.1)target*=1-p.shadow*.8;
        target=original+(target-original)*Math.min(1.5,p.strength);
        target=Math.max(0,Math.min(1,target));
        const gain=old>.00001?target/old:0;
        for(let c=0;c<3;c++){const v=source[(valid?sj:j)+c]/255;out[j+c]=Math.floor(Math.max(0,Math.min(1,v*gain))*255+.5);}
      }
    }return out;
  }
  const api={defaults,hash,process};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
