/* Ref-derived scalar edge jitter and normalized residual. No reference RGB sampling. */
(function(root){'use strict';
 const defaults={strength:1,tear:1,echo:1,grain:1};
 function box(a,w,h,r){const stride=w+1,integral=new Float64Array((w+1)*(h+1)),out=new Float64Array(w*h);for(let y=0;y<h;y++){let s=0;for(let x=0;x<w;x++){s+=a[y*w+x];integral[(y+1)*stride+x+1]=integral[y*stride+x+1]+s;}}for(let y=0;y<h;y++)for(let x=0;x<w;x++){const x0=Math.max(0,x-r),x1=Math.min(w,x+r+1),y0=Math.max(0,y-r),y1=Math.min(h,y+r+1);out[y*w+x]=(integral[y1*stride+x1]-integral[y0*stride+x1]-integral[y1*stride+x0]+integral[y0*stride+x0])/((x1-x0)*(y1-y0));}return out;}
 function process(source,w,h,options={}){const p={...defaults,...options},lookup=options.lookup||root.StillMeasuredLookup,out=new Uint8ClampedArray(source);if(!p.strength)return out;if(!lookup)throw Error('measured lookup is required');
  const lum=new Float64Array(w*h),sq=new Float64Array(w*h);for(let i=0;i<lum.length;i++){lum[i]=(source[i*4]*.2126+source[i*4+1]*.7152+source[i*4+2]*.0722)/255;sq[i]=lum[i]*lum[i];}
  // Self-guided filter: a=var/(var+eps), b=mean*(1-a), q=mean(a)*I+mean(b).
  const mean=box(lum,w,h,7),meanSq=box(sq,w,h,7),aa=new Float64Array(w*h),bb=new Float64Array(w*h);
  for(let i=0;i<lum.length;i++){const v=Math.max(0,meanSq[i]-mean[i]*mean[i]);aa[i]=v/(v+.06*.06);bb[i]=mean[i]*(1-aa[i]);}
  const ma=box(aa,w,h,7),mb=box(bb,w,h,7),clean=new Float64Array(w*h);for(let i=0;i<lum.length;i++)clean[i]=lum[i]+(ma[i]*lum[i]+mb[i]-lum[i])*Math.min(1,p.grain);
  function l(x,y){return clean[y*w+Math.max(0,Math.min(w-1,x))];}
  const knots=[[0,.008],[.07,.015],[.15,.09],[.35,.34],[.55,.63],[.75,.845],[1,.85]];function tone(v){v=Math.max(0,Math.min(1,v));let n=1;while(knots[n][0]<v)n++;const a=knots[n-1],b=knots[n];return a[1]+(b[1]-a[1])*(v-a[0])/(b[0]-a[0]);}
  for(let y=0;y<h;y++){
   const edges=[];for(let x=9;x<w-9;x++){const g=l(x+3,y)-l(x-3,y),c=l(x+8,y)-l(x-8,y);if(Math.abs(c)>.13&&Math.abs(g)>.025&&Math.abs(g)>=Math.abs(l(x+2,y)-l(x-4,y))&&Math.abs(g)>Math.abs(l(x+4,y)-l(x-2,y))){if(edges.length&&x-edges[edges.length-1].x<10){if(Math.abs(g)>Math.abs(edges[edges.length-1].g))edges[edges.length-1]={x,g,c};}else edges.push({x,g,c});}}
   let nearest=0;for(let x=0;x<w;x++){
    const i=y*w+x,j=i*4;if(!source[j+3])continue;while(nearest+1<edges.length&&Math.abs(edges[nearest+1].x-x)<Math.abs(edges[nearest].x-x))nearest++;
    const edge=edges[nearest],distance=edge?x-edge.x:999,gate=edge?Math.max(0,Math.min(1,(Math.abs(edge.c)-.12)/.28)):0,inside=Math.abs(distance)<=32&&gate>0;
    const row=(y+nearest*47)%lookup.row_count,displacement=inside?lookup.jitter[row]*.78*p.tear*gate:0,sx=Math.max(0,Math.min(w-1,x+Math.floor(displacement+.5))),si=y*w+sx,valid=source[si*4+3]>0,old=valid?clean[si]:clean[i];
    let target=tone(old);
    if(inside){const residual=lookup.residual[row][distance+32];target+=residual*Math.abs(edge.c)*Math.sign(edge.c)*.65*p.echo*gate;}
    target=lum[i]+(target-lum[i])*Math.min(1.5,p.strength);target=Math.max(.008,Math.min(.85,target));const raw=valid?lum[si]:lum[i],gain=raw>.00001?target/raw:0,sj=valid?si*4:j;
    for(let c=0;c<3;c++)out[j+c]=Math.floor(Math.max(0,Math.min(1,source[sj+c]/255*gain))*255+.5);
   }
  }return out;
 }
 const api={defaults,process};if(typeof module!=='undefined')module.exports=api;root.StillEffect=api;
})(typeof globalThis!=='undefined'?globalThis:this);
