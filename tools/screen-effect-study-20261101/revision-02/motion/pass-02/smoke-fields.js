/* Independent continuous fields. No reference RGB or alpha-frame crossfade. */
const clamp=(x,a=0,b=1)=>Math.max(a,Math.min(b,x));
const smooth=(a,b,x)=>{const u=clamp((x-a)/(b-a));return u*u*(3-2*u);};
const mix=(a,b,t)=>a+(b-a)*t;
const grids=Array.from({length:4},(_,seed)=>{
  let state=(0x178bf231+seed*0x1f123bb5)>>>0;
  return Float32Array.from({length:128*128},()=>{state^=state<<13;state^=state>>>17;state^=state<<5;return (state>>>0)/4294967295;});
});
function noise(x,y,seed=0){const ix=Math.floor(x),iy=Math.floor(y),fx=x-ix,fy=y-iy;
  const u=fx*fx*(3-2*fx),v=fy*fy*(3-2*fy),g=grids[seed];
  const at=(a,b)=>g[((b%128+128)%128)*128+(a%128+128)%128];
  return mix(mix(at(ix,iy),at(ix+1,iy),u),mix(at(ix,iy+1),at(ix+1,iy+1),u),v);
}
function ellipse(x,y,cx,cy,rx,ry){if(rx<=.0001||ry<=.0001)return -10;return (1-Math.hypot((x-cx)/Math.max(.001,rx),(y-cy)/Math.max(.001,ry)))*Math.min(rx,ry);}
function box(x,y,cx,cy,rx,ry,r=.035){const qx=Math.abs(x-cx)-rx+r,qy=Math.abs(y-cy)-ry+r;return r-Math.hypot(Math.max(qx,0),Math.max(qy,0))-Math.min(Math.max(qx,qy),0);}
function key(t,points){for(let i=1;i<points.length;i++)if(t<=points[i][0]){const a=points[i-1],b=points[i];return mix(a[1],b[1],smooth(a[0],b[0],t));}return points.at(-1)[1];}
const cache=new Map();
function canvases(w,h){const k=w+'x'+h;if(!cache.has(k)){const make=()=>{const c=document.createElement('canvas');c.width=w;c.height=h;return c;};cache.set(k,Object.fromEntries(['oldVisibilityCanvas','newVisibilityCanvas','smokeDensityCanvas','edgeParticlesCanvas','entryA','oldWindow','exitB','endingC'].map(n=>[n,make()])));}return cache.get(k);}
function write(canvas,values,particles=false){const out=new Uint8ClampedArray(values.length*4);for(let i=0;i<values.length;i++){const j=i*4,v=Math.round(clamp(values[i])*255);out[j]=particles?255:v;out[j+1]=particles?255:v;out[j+2]=particles?255:v;out[j+3]=particles?v:255;}canvas.getContext('2d').putImageData(new ImageData(out,canvas.width,canvas.height),0,0);}

export function sampleSmokeFields(t,width=720,height=540,options={}){
  if(!Number.isFinite(t)||!Number.isInteger(width)||!Number.isInteger(height)||width<1||height<1)throw Error('Invalid smoke sampling arguments');
  const N=width*height,old=new Float32Array(N),next=new Float32Array(N),density=new Float32Array(N),particles=new Float32Array(N);
  const A=new Float32Array(N),W=new Float32Array(N),B=new Float32Array(N),C=new Float32Array(N);
  const enabled=options.enabled!==false,hold=t>=13.28&&t<13.82;
  const phase=t<6?'transition':t<13.28?'ending':hold?'black-hold':'return';
  // Temporal interpolation is applied to distances / radii, before optical density.
  const entry=smooth(2.94,3.38,t),windowMix=smooth(3.20,3.48,t);
  const wrx=key(t,[[0,.48],[3.3,.30],[3.6,.19],[3.9,.135],[4.1,.125],[4.32,.05],[4.50,0]]);
  const wry=key(t,[[0,.55],[3.3,.23],[3.6,.115],[3.9,.085],[4.1,.075],[4.32,.035],[4.50,0]]);
  const floorRise=key(t,[[0,1.15],[3.6,1.04],[3.8,.985],[4,.95],[4.2,.86],[4.4,.72],[4.7,.36],[5.15,-.2]]);
  const leftBoundary=key(t,[[0,.78],[3.6,.78],[3.8,.72],[4,.64],[4.2,.50],[4.3,.365],[4.4,.14],[4.7,-.1],[5.15,-.2]]);
  const centerRx=key(t,[[0,0],[4.05,0],[4.2,.125],[4.4,.30],[4.7,.49],[5.15,.95]]);
  const centerRy=key(t,[[0,0],[4.05,0],[4.2,.115],[4.4,.27],[4.7,.45],[5.15,.95]]);
  const cornerRx=key(t,[[0,0],[3.56,0],[3.62,.016],[3.8,.052],[4,.115],[4.2,.23],[4.4,.38],[4.7,.65],[5.15,1.2]]);
  const cornerRy=key(t,[[0,0],[3.56,0],[3.62,.015],[3.8,.055],[4,.10],[4.2,.19],[4.4,.32],[4.7,.65],[5.15,1.2]]);
  const endingTop=key(t,[[0,-.25],[11.22,-.15],[11.6,.015],[12,.15],[12.2,.26],[12.4,.31],[12.6,.35],[12.8,.4]]);
  const endingTransmission=key(t,[[0,1],[12.10,1],[12.25,.88],[12.4,.48],[12.6,.105],[12.78,0]]);
  const active=(t>=2.94&&t<5.3)||(t>=11.22&&t<13.28);
  for(let iy=0;iy<height;iy++)for(let ix=0;ix<width;ix++){
    const i=iy*width+ix,x=(ix+.5)/width,y=(iy+.5)/height;
    if(!enabled){next[i]=1;continue;}
    if(hold){density[i]=1;continue;}
    if(t>=13.82){next[i]=1;continue;}
    if(t<2.94){old[i]=1;W[i]=1;continue;}
    if(t>=5.3&&t<11.22){next[i]=1;continue;}
    // The same seeded material is advected continuously, never regenerated per frame.
    const ax=x+t*.045+.018*Math.sin(y*6.2-t*.9),ay=y-t*.068+.014*Math.sin(x*7+t*.7);
    const coarse=noise(ax*6,ay*5,0),fold=noise(ax*19,ay*17,1),fine=noise(ax*125,ay*120,2),micro=noise(ax*410,ay*390,3);
    // Signed-distance displacement acts along each front's normal. Fine detail persists by advection.
    const warp=(coarse-.5)*.026+(fold-.5)*.023+(fine-.5)*.015+(micro-.5)*.004;
    const edgeWidth=(1.5+3*noise(ax*31,ay*29,3))/720; // full transition band 3..9 native px
    let entryDistance=Math.max(
      ellipse(x,y,-.10,1.10,.04+entry*1.05,.04+entry*.92),
      ellipse(x,y,1.13,.94,.02+entry*.88,.04+entry*.90),
      ellipse(x,y,1.15,-.12,.01+smooth(3.04,3.60,t)*.85,.01+smooth(3.04,3.60,t)*.78),
      ellipse(x,y,.55,1.17,.01+smooth(3.05,3.48,t)*.65,.01+smooth(3.05,3.48,t)*.78),
      ellipse(x,y,-.12,.52,.01+smooth(3.08,3.52,t)*.64,.01+smooth(3.08,3.52,t)*.46)
    );
    const a=smooth(-edgeWidth,edgeWidth,entryDistance+warp);
    const wdistance=box(x,y,.485,.245,wrx,wry,.025)+warp*.65;
    let window=smooth(-edgeWidth,edgeWidth,wdistance)*(1-smooth(4.30,4.5,t));
    // Old window is eroded internally, rather than only scaled down.
    const pits=smooth(.60,.80,fold)*smooth(3.92,4.36,t);window*=1-pits;
    // W preserves the old upper aperture independently of incoming lower lobes.
    let oldvis=mix(1-a,window,windowMix);
    A[i]=a;W[i]=window;
    // Exit B: right bottom fleck, bottom strip, central lower aperture, then upward spread.
    let exitDistance=Math.max(ellipse(x,y,.855,.88,cornerRx,cornerRy),
      Math.min(y-floorRise+(coarse-.5)*.018,x-leftBoundary),
      ellipse(x,y,.64,.71,centerRx,centerRy),
      ellipse(x,y,.77,.68,centerRx*.80,centerRy*.81));
    exitDistance=Math.min(exitDistance,x-leftBoundary);
    const leftGate=t<=4.30?smooth(.35,.37,x):1;
    let newvis=smooth(-edgeWidth,edgeWidth,exitDistance+warp)*smooth(3.56,3.64,t)*leftGate;
    B[i]=newvis;
    // C separates broad spatial support from optical transmission. No shrinking pair of tunnels.
    const sideInset=smooth(11.60,12.42,t)*.12;
    const endingDistance=Math.min(y-endingTop+(coarse-.5)*.17,
      ellipse(x,y,.5,.80,.65-sideInset,.85))+warp;
    const endingShape=smooth(-edgeWidth,edgeWidth,endingDistance);
    const fragmentField=fold*.25+fine*.55+micro*.20;
    const fragmentThreshold=key(t,[[0,-.1],[12.20,.16],[12.4,.30],[12.6,.53],[12.78,.85]]);
    const fragments=smooth(fragmentThreshold-.055,fragmentThreshold+.055,fragmentField);
    const endingVisibility=endingShape*endingTransmission*fragments*(.40+.60*coarse);
    C[i]=t<11.22?0:1-endingVisibility;
    if(t>=6){oldvis=0;newvis=endingVisibility;}
    newvis*=1-oldvis;
    let visible=clamp(oldvis+newvis),smoke=1-visible;
    const band=4*smoke*(1-smoke),interiorHoles=smooth(.65,.84,fold)*smooth(.71,.90,fine)*.11;
    const interiorVeins=smooth(.57,.82,coarse)*smooth(.48,.80,fold)*.045;
    if(active&&t<12.78){const thin=(interiorHoles+interiorVeins)*smoke;
      if(t<6){newvis+=thin*smooth(4.02,4.55,t)*leftGate*smooth(leftBoundary,leftBoundary+.02,x);oldvis+=thin*(1-smooth(4.02,4.55,t));}
    }
    const total=oldvis+newvis;if(total>1){oldvis/=total;newvis/=total;}
    old[i]=oldvis;next[i]=newvis;density[i]=1-oldvis-newvis;
    if(active){const speck=smooth(.79,.96,micro)*(.18+.82*fine);
      particles[i]=speck*(band*.21+smoke*.035)*(t<6?1-smooth(4.65,5.25,t):1-smooth(12.78,13.24,t));}
    if(t>=12.80){old[i]=0;next[i]=0;density[i]=1;}
  }
  return {width,height,t,phase,oldVisibility:old,newVisibility:next,smokeDensity:density,edgeParticles:particles,fields:{entryA:A,oldWindow:W,exitB:B,endingC:C},enabled,hold};
}

export function drawSmokeAt(t,width=720,height=540,options={}){
  const s=sampleSmokeFields(t,width,height,options),c=canvases(width,height);
  for(const [key,values] of [['oldVisibilityCanvas',s.oldVisibility],['newVisibilityCanvas',s.newVisibility],['smokeDensityCanvas',s.smokeDensity],['edgeParticlesCanvas',s.edgeParticles]])write(c[key],values,key==='edgeParticlesCanvas');
  if(options.diagnostics)for(const [key,values] of Object.entries(s.fields))write(c[key],values);
  return {oldVisibilityCanvas:c.oldVisibilityCanvas,newVisibilityCanvas:c.newVisibilityCanvas,smokeDensityCanvas:c.smokeDensityCanvas,edgeParticlesCanvas:c.edgeParticlesCanvas,fields:options.diagnostics?{entryA:c.entryA,oldWindow:c.oldWindow,exitB:c.exitB,endingC:c.endingC}:undefined,metadata:{t,width,height,phase:s.phase,enabled:s.enabled,hold:s.hold,visibilityEncoding:'linear scalar in RGB, alpha=255',particleEncoding:'white RGB with scalar alpha',sourceDerived:false}};
}

export const smokeFieldProvenance={version:'revision-02-pass-02',sourceDerived:false,referenceSHA256:'2c6fd4039eedfbe4d8fd13fe8dc5b818ce355ba6a722accb285c4410f67e4fa8',referenceRole:'Timing, location and texture observations only. No decoded source RGB / alpha exported as effect material.',nativeSpace:[720,540],blackHold:[13.28,13.82],entry:'A / 2.94..3.52',oldWindow:'W / 3.26..4.50',exit:'B / 3.56..5.30 right-bottom first; x<.35 closed through4.30',ending:'C / 11.22..12.80 broad support, transmission and granular fragmentation independent'};
