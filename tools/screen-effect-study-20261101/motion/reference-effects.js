export async function createReferenceEffects(assetBase='./assets/') {
  const baked=globalThis.__motionReferenceAssets;
  const manifest=baked?.manifest??await (await fetch(assetBase+'effect-assets.json')).json();
  const names=['base-a.png','base-b.png',...Object.values(manifest.sequences).map(s=>s.file)];
  const images=Object.fromEntries(await Promise.all(names.map(async name=>{const img=new Image();img.src=baked?.images[name]??assetBase+name;await img.decode();return [name,img];})));
  const make=()=>{const c=document.createElement('canvas');c.width=720;c.height=540;return c;};
  const foil=make(),caption=make(),matte=make(),particles=make();
  const contexts=[foil,caption,matte,particles].map(c=>c.getContext('2d'));
  const ss=(a,b,t)=>{let x=Math.max(0,Math.min(1,(t-a)/(b-a)));return x*x*(3-2*x)};
  function sample(meta,t){let a=0;for(let i=0;i<meta.times.length;i++){if(meta.times[i]<=t)a=i;else break;}const b=Math.min(meta.times.length-1,a+1),u=Math.max(0,Math.min(1,(t-meta.times[a])/Math.max(.001,meta.times[b]-meta.times[a])));return [[a,1-u],[b,u]];}
  function tile(ctx,meta,index,dest,alpha=1){const [w,h]=meta.cell;ctx.globalAlpha=alpha;ctx.drawImage(images[meta.file],index%meta.columns*w,Math.floor(index/meta.columns)*h,w,h,...dest);ctx.globalAlpha=1;}
  function sequence(ctx,key,t,gain=1){const meta=manifest.sequences[key];let samples=sample(meta,t);if(key==='glyph'){const u=ss(.45,.55,samples[1][1]);samples=[[samples[0][0],1-u],[samples[1][0],u]];}for(const [i,weight] of samples){if(!weight)continue;if(key==='glyph'){tile(ctx,meta,i,[meta.roi[0],meta.roi[1],meta.cell[0],meta.cell[1]],weight*gain)}else if(key==='foil'){const p=meta.placements[i];const stretch=1+.35*ss(5.15,5.75,t);tile(ctx,meta,i,[p.xy[0],p.xy[1]-(stretch-1)*p.size[1]/2,meta.cell[0],meta.cell[1]*stretch],weight*gain)}else tile(ctx,meta,i,[0,0,720,540],weight*gain);}}
  const white=manifest.sequences.white,finePool=white.particles.filter(p=>p.area<=15&&Math.max(...p.size)<=18);
  function draw(t,options={}) {
    const [fg,cap,mask,fx]=contexts;contexts.forEach(c=>{c.setTransform(1,0,0,1,0,0);c.globalAlpha=1;c.globalCompositeOperation='source-over';c.clearRect(0,0,720,540)});
    if(options.foreground!==false){
      if(t<12.7){fg.globalCompositeOperation='lighter';sequence(fg,'foil',Math.min(t,12.4));fg.globalCompositeOperation='source-over';}
      if(t>=5.7&&t<=12.75){cap.globalCompositeOperation='lighter';sequence(cap,'glyph',Math.min(t,12.7));cap.globalCompositeOperation='source-over';}
      if(t>=13.83){cap.globalAlpha=ss(13.83,14.22,t);cap.font='22px "Yu Mincho",serif';cap.textAlign='center';cap.fillStyle='#d7d3c9';cap.fillText('誰かのデブリではなく',360,274.2);cap.globalAlpha=1;}
    }
    mask.fillStyle='black';mask.fillRect(0,0,720,540);
    if(t>=2.5&&t<=5.5||t>=10.9&&t<=13.28){mask.globalCompositeOperation='lighter';sequence(mask,'invasion',t);mask.globalCompositeOperation='source-over';}
    const first=ss(2.7,3.8,t)*(1-ss(4.75,5.5,t)),last=ss(10.9,12.15,t)*(1-ss(13.02,13.27,t));
    const amount=Math.max(first,last);
    if(amount>0&&t<13.28){
      if(options.invasion!==0){fx.globalCompositeOperation='lighter';sequence(fx,'redMotion',t,amount);fx.globalCompositeOperation='source-over';}
      const meta=manifest.sequences.white,[cw,ch]=meta.cell,phase=t<6?t+9:t;
      if(t<12.6)for(const p of meta.particles){
        if(p.area<=15&&Math.max(...p.size)<=18)continue;
        const depth=Math.min(1,Math.sqrt(p.area)/22),delta=phase-13,sway=Math.sin(phase*2.7+p.sprite*.71)-Math.sin(13*2.7+p.sprite*.71);
        const vx=p.velocityPxPerSec[0]+(p.sprite%2?1:-1)*(20+depth*105),vy=p.velocityPxPerSec[1]-(14+depth*78);
        const x=((p.xy[0]+vx*delta+sway*depth*26)%720+720)%720,y=((p.xy[1]+vy*delta+Math.sin(phase*2+p.sprite)*depth*18)%540+540)%540;
        const [w,h]=p.size,scale=Math.max(.5,Math.min(1.75,1+delta*depth*.32));fx.globalAlpha=Math.min(1,amount*1.35);fx.drawImage(images[meta.file],p.sprite%meta.columns*cw,Math.floor(p.sprite/meta.columns)*ch,w,h,x-p.centerInSprite[0]*scale,y-p.centerInSprite[1]*scale,w*scale,h*scale);
      }
      if(t>=12.6){fx.globalCompositeOperation='lighter';sequence(fx,'whiteMotion',t,amount);fx.globalCompositeOperation='source-over';}
      const field=manifest.densityField.frames.filter(frame=>t<6?frame.t<6:frame.t>6),fieldTimes={times:field.map(f=>f.t)};
      fx.globalCompositeOperation='lighter';for(const [frameIndex,weight] of sample(fieldTimes,t)){if(!weight)continue;for(const [x,y,peak,area,vx=0,vy=0] of field[frameIndex].points){const p=finePool.reduce((a,b)=>Math.abs(a.peak-peak)+Math.abs(a.area-area)*3<Math.abs(b.peak-peak)+Math.abs(b.area-area)*3?a:b),dt=t-field[frameIndex].t;const [w,h]=p.size,gain=amount*weight*peak/Math.max(1,p.peak);fx.globalAlpha=Math.min(1,gain);fx.drawImage(images[meta.file],p.sprite%meta.columns*cw,Math.floor(p.sprite/meta.columns)*ch,w,h,x+vx*dt-p.centerInSprite[0],y+vy*dt-p.centerInSprite[1],w,h);if(gain>1){fx.globalAlpha=Math.min(1,gain-1);fx.drawImage(images[meta.file],p.sprite%meta.columns*cw,Math.floor(p.sprite/meta.columns)*ch,w,h,x+vx*dt-p.centerInSprite[0],y+vy*dt-p.centerInSprite[1],w,h);}}}fx.globalCompositeOperation='source-over';
      if(options.invasion!==0&&t>=11.8&&t<=12.7){fx.globalCompositeOperation='lighter';sequence(fx,'edgeGranules',t);fx.globalCompositeOperation='source-over';}
      fx.globalAlpha=1;
    }
    return {foil,caption,matte,particles};
  }
  return {manifest,inputs:{a:images['base-a.png'],b:images['base-b.png']},draw,canvases:{foil,caption,matte,particles}};
}
