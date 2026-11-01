/* Frozen R7 foreground shapes, independently composed. Old invasion matte is absent. */
export async function createFrozenEffects(baked){
 const m=baked.manifest,images={};for(const[name,url]of Object.entries(baked.images)){const i=new Image();i.src=url;await i.decode();images[name]=i;}
 const make=()=>{const c=document.createElement('canvas');c.width=720;c.height=540;return c;},foil=make(),caption=make(),particles=make();
 const contexts=[foil,caption,particles].map(c=>c.getContext('2d'));
 const smooth=(a,b,t)=>{const x=Math.max(0,Math.min(1,(t-a)/(b-a)));return x*x*(3-2*x);};
 function samples(meta,t){let a=0;for(let i=0;i<meta.times.length&&meta.times[i]<=t;i++)a=i;const b=Math.min(meta.times.length-1,a+1),u=Math.max(0,Math.min(1,(t-meta.times[a])/Math.max(.001,meta.times[b]-meta.times[a])));return [[a,1-u],[b,u]];}
 function tile(c,meta,i,dest,gain){const[w,h]=meta.cell;c.globalAlpha=gain;c.drawImage(images[meta.file],i%meta.columns*w,Math.floor(i/meta.columns)*h,w,h,...dest);c.globalAlpha=1;}
 function sequence(c,key,t,gain=1){const meta=m.sequences[key];for(const[i,weight]of samples(meta,t)){if(!weight)continue;let dest=[0,0,720,540];if(key==='glyph')dest=[...meta.roi.slice(0,2),...meta.cell];if(key==='foil'){const p=meta.placements[i],stretch=1+.35*smooth(5.15,5.75,t);dest=[p.xy[0],p.xy[1]-(stretch-1)*p.size[1]/2,meta.cell[0],meta.cell[1]*stretch];}tile(c,meta,i,dest,gain*weight);}}
 function draw(t,enabled=true){for(const c of contexts){c.setTransform(1,0,0,1,0,0);c.globalAlpha=1;c.globalCompositeOperation='source-over';c.clearRect(0,0,720,540);}if(!enabled)return{foil,caption,particles};
 const[f,c,p]=contexts;
 if(t<12.1){f.globalCompositeOperation='lighter';sequence(f,'foil',Math.min(t,12));f.globalCompositeOperation='source-over';}
 if(t>=5.7&&t<=12.75){c.globalCompositeOperation='lighter';sequence(c,'glyph',Math.min(t,12.7));c.globalCompositeOperation='source-over';}
 const first=smooth(2.7,3.8,t)*(1-smooth(4.75,5.5,t)),last=smooth(10.9,12.15,t)*(1-smooth(13.02,13.27,t)),amount=Math.max(first,last);
 if(amount>0&&t<13.28){p.globalCompositeOperation='lighter';sequence(p,'redMotion',t,amount);
 const meta=m.sequences.white,[cw,ch]=meta.cell,phase=t<6?t+9:t;
 if(t<12.6)for(const item of meta.particles){if(item.area<=15&&Math.max(...item.size)<=18)continue;const depth=Math.min(1,Math.sqrt(item.area)/22),delta=phase-13,sway=Math.sin(phase*2.7+item.sprite*.71)-Math.sin(13*2.7+item.sprite*.71),vx=item.velocityPxPerSec[0]+(item.sprite%2?1:-1)*(20+depth*105),vy=item.velocityPxPerSec[1]-(14+depth*78),x=((item.xy[0]+vx*delta+sway*depth*26)%720+720)%720,y=((item.xy[1]+vy*delta+Math.sin(phase*2+item.sprite)*depth*18)%540+540)%540,[w,h]=item.size,scale=Math.max(.5,Math.min(1.75,1+delta*depth*.32));p.globalAlpha=Math.min(1,amount*1.35);p.drawImage(images[meta.file],item.sprite%meta.columns*cw,Math.floor(item.sprite/meta.columns)*ch,w,h,x-item.centerInSprite[0]*scale,y-item.centerInSprite[1]*scale,w*scale,h*scale);}
 if(t>=12.6)sequence(p,'whiteMotion',t,amount);
 const field=m.densityField.frames.filter(r=>t<6?r.t<6:r.t>6),pool=meta.particles.filter(item=>item.area<=15&&Math.max(...item.size)<=18);
 for(const[j,weight]of samples({times:field.map(r=>r.t)},t)){if(!weight)continue;for(const[x,y,peak,area,vx=0,vy=0]of field[j].points){const item=pool.reduce((a,b)=>Math.abs(a.peak-peak)+Math.abs(a.area-area)*3<Math.abs(b.peak-peak)+Math.abs(b.area-area)*3?a:b),dt=t-field[j].t,[w,h]=item.size;p.globalAlpha=Math.min(1,amount*weight*peak/Math.max(1,item.peak));p.drawImage(images[meta.file],item.sprite%meta.columns*cw,Math.floor(item.sprite/meta.columns)*ch,w,h,x+vx*dt-item.centerInSprite[0],y+vy*dt-item.centerInSprite[1],w,h);}}
 p.globalAlpha=1;p.globalCompositeOperation='source-over';}
 return{foil,caption,particles};}
 return{draw,manifest:m};
}
