from pathlib import Path
H=Path(__file__).resolve().parent
s=(H/'capture.mjs').read_text(encoding='utf-8')
s=s.replace("const out='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial';","const out='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/combined';")
s=s.replace("'spatial-preview.html'","'combined-preview.html'").replace("['local','global','none']","['combined','local','global','none']").replace("['surface','depth','wire','holes']","['surface','depth','wire','holes','weight']")
s=s.replace("s.geometry.ownership[i*4]===id)pixels.push(i)","s.geometry.ownership[i*4]===id&&s.geometry.weights[i]>.75)pixels.push(i)")
s=s.replace("pixel:s.project(s.displace(p,t))","pixel:s.combinedProject(p,t)").replace("deformed:s.project(s.displace(p,3.2))","deformed:s.combinedProject(p,3.2)")
s=s.replace("pixel:s.combinedProject(p,t)","pixel:s.combinedProject(p,t),globalPixel:s.globalProject(p,t)")
key='const metrics=await ev('
extra="""for(const t of [6.5,8.5,10.5])for(const mode of ['combined','global','local','none']){await frame(t,{mode,display:false},`${mode}-native-${t}.png`);await frame(t,{mode,display:false,diagnostic:'holes'},`${mode}-holes-native-${t}.png`);}
for(const t of [6.5,8.5,10.5])for(const diagnostic of ['surface','depth','holes','weight'])await frame(t,{mode:'combined',display:false,diagnostic},`${diagnostic}-native-${t}.png`);
const combinedMeasurements=await ev(`(()=>{const s=spatialStudy,rows=[];for(const t of [6.5,8.5,10.5])for(const mode of ['combined','local','global','none']){s.renderAt(t,{mode,display:false,diagnostic:'surface'});const a=s.readRaw(),counts={};let holes=0,cropHoles=0;for(let y=0;y<480;y++)for(let x=0;x<640;x++){const j=((479-y)*640+x)*4,k=a[j+3]<128?'holes':[a[j],a[j+1],a[j+2]].join(',');counts[k]=(counts[k]||0)+1;if(a[j+3]<128){holes++;if(x>=83&&x<557&&y>=62&&y<418)cropHoles++;}}rows.push({t,mode,holes,cropHoles,counts});}return rows;})()`);
const zeroModes={};for(const mode of ['combined','global','local'])zeroModes[mode]=(await frame(8.5,{mode,strength:0,display:false},`zero-${mode}.png`))===(await frame(8.5,{mode:'none',display:false},`zero-none-${mode}.png`));
const metrics=await ev("""
assert key in s;s=s.replace(key,extra)
s=s.replace("const a=s.readRaw(),counts={};let holes=0,cropHoles=0;", "const gl=s.canvas.getContext('webgl2'),a=new Uint8Array(640*480*4);gl.readPixels(0,0,640,480,gl.RGBA,gl.UNSIGNED_BYTE,a);const counts={};let holes=0,cropHoles=0;")
s=s.replace("counts[k]=(counts[k]||0)+1;if(a[j+3]<128)","if(mode!=='global')counts[k]=(counts[k]||0)+1;if(a[j+3]<128)")
s=s.replace('{...metrics,noneEqualsStrengthZero:zero===strengthZero,timeReturnDeterministic:first===again,trajectories,bending}','{...metrics,combinedMeasurements,zeroModes,noneEqualsStrengthZero:zero===strengthZero,timeReturnDeterministic:first===again,trajectories,bending}')
(H/'combined-gpu-calibration.mjs').write_bytes((s.split('const times=')[0]+(H/'combined-gpu-body.js').read_text(encoding='utf-8')).encode('utf-8'))
(H/'combined-capture.mjs').write_bytes(s.encode('utf-8'))
