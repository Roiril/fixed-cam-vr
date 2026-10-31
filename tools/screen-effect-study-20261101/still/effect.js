/* Deterministic RGB-only post effect. No image or Unity dependency. */
(function (root) {
  'use strict';
  const defaults = {strength:1,tear:0.8,echo:0.65,grain:0.65,contrast:0.38,shadow:0.5,seed:104729};
  function hash(x,y,seed) {
    let v=(Math.imul(x,374761393)^Math.imul(y,668265263)^Math.imul(seed,1274126177))>>>0;
    v=Math.imul(v^(v>>>13),1274126177)>>>0;
    return ((v^(v>>>16))>>>0)/4294967295;
  }
  function process(source,w,h,options={}) {
    const p=Object.assign({},defaults,options), out=new Uint8ClampedArray(source);
    if (p.strength===0) return out;
    const lum=new Float64Array(w*h),edge=new Float64Array(w*h);
    for(let i=0;i<lum.length;i++)lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;
    function l(x,y){return lum[y*w+Math.max(0,Math.min(w-1,x))];}
    for(let y=0;y<h;y++)for(let x=0;x<w;x++)edge[y*w+x]=Math.min(1,Math.abs(l(x+1,y)-l(x-1,y))*5+Math.abs(l(x,y)-l(x,Math.max(0,y-1)))*2);
    for(let y=0;y<h;y++) {
      const shift=Math.floor((hash(0,y,p.seed)-.5)*2*3.5*p.tear*p.strength+.5);
      for(let x=0;x<w;x++) {
        const i=y*w+x,j=i*4;if(!source[j+3])continue;
        const e=edge[i],sx=Math.max(0,Math.min(w-1,x+shift*(.18+.82*e))),a=Math.floor(sx),b=Math.min(w-1,a+1),f=sx-a;
        const valid=source[(y*w+a)*4+3]>0&&source[(y*w+b)*4+3]>0;
        let r=source[j]/255,g=source[j+1]/255,bl=source[j+2]/255;
        if(valid){r=(source[(y*w+a)*4]*(1-f)+source[(y*w+b)*4]*f)/255;g=(source[(y*w+a)*4+1]*(1-f)+source[(y*w+b)*4+1]*f)/255;bl=(source[(y*w+a)*4+2]*(1-f)+source[(y*w+b)*4+2]*f)/255;}
        const old=r*.2126+g*.7152+bl*.0722;
        const high=l(x,y)-.5*(l(x-2,y)+l(x+2,y));
        const ring=(l(x-2,y)-l(x+3,y))*.18;
        let target=(old-.5)*(1+.65*p.contrast*p.strength)+.5;
        target-=p.shadow*.13*p.strength*Math.max(0,1-Math.abs(target-.2)/.2);
        target+=(high*.55+ring)*p.echo*p.strength;
        const noise=(hash(Math.floor(x/3),y,p.seed+1)-.5)*.7+(hash(x,y,p.seed+2)-.5)*.3;
        const visible=Math.max(0,Math.min(1,old/.10))*(.18+.82*e)*(.35+.65*Math.sin(Math.PI*Math.max(0,Math.min(1,old))));
        target+=noise*.16*p.grain*p.strength*visible;
        target=Math.max(0,Math.min(1,target));
        const gain=old>.00001?target/old:0;
        out[j]=Math.floor(Math.max(0,Math.min(1,r*gain))*255+.5);out[j+1]=Math.floor(Math.max(0,Math.min(1,g*gain))*255+.5);out[j+2]=Math.floor(Math.max(0,Math.min(1,bl*gain))*255+.5);
      }
    }
    return out;
  }
  const api={defaults,process,hash};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
