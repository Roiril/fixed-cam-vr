from pathlib import Path
H=Path(__file__).resolve().parent
s=(H/'capture.mjs').read_text(encoding='utf-8')
s=s.replace("const out='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial';","const out='G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/pinned';")
s=s.replace("'spatial-preview.html'","'spatial-pinned-preview.html'")
s=s.replace("['surface','depth','wire','holes']","['surface','depth','wire','holes','weight']")
s=s.replace("s.geometry.ownership[i*4]===id)pixels.push(i)","s.geometry.ownership[i*4]===id&&s.geometry.weights[i]>.75)pixels.push(i)")
key="const metrics=await ev("
assert key in s
s=s.replace(key,"""for(const t of [3.2,8.5,10.5]){await frame(t,{display:false},`pinned-native-${t}.png`);await frame(t,{display:false,diagnostic:'holes'},`holes-native-${t}.png`);await frame(t,{display:false,diagnostic:'weight'},`weight-native-${t}.png`);}
const pinnedMeasurements=await ev(`(()=>{const s=spatialStudy,rows=[];for(const t of [3.2,8.5,10.5]){s.renderAt(t,{display:false,diagnostic:'surface'});const a=s.readRaw(),counts={};let holes=0,interiorHoles=0,edgeHoles=0;for(let y=0;y<480;y++)for(let x=0;x<640;x++){const j=((479-y)*640+x)*4,k=a[j+3]<128?'holes':[a[j],a[j+1],a[j+2]].join(',');counts[k]=(counts[k]||0)+1;if(a[j+3]<128){holes++;if(x<12||x>=628||y<12||y>=468)edgeHoles++;else interiorHoles++;}}rows.push({t,holes,interiorHoles,perimeter12pxHoles:edgeHoles,counts});}return{rows,pinStats:s.geometry.pinStats,pinRule:s.geometry.pinRule};})()`);
const metrics=await ev(""")
s=s.replace("{...metrics,noneEqualsStrengthZero:zero===strengthZero,timeReturnDeterministic:first===again,trajectories,bending}","{...metrics,pinnedMeasurements,noneEqualsStrengthZero:zero===strengthZero,timeReturnDeterministic:first===again,trajectories,bending}")
(H/'pinned-capture.mjs').write_bytes(s.encode('utf-8'))
