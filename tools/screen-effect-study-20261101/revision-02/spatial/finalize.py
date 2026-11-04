from pathlib import Path
import json,hashlib,math
import numpy as np
from PIL import Image,ImageDraw,ImageFont
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial')
m=json.loads((O/'render-metrics.json').read_text())
times=[0,1.2,3.2,4.2,6.5,8.5,10.5,12.4]
res=[]
for i,t in enumerate(times):
 a=np.array([p['original'][:2] for p in m['trajectories']['points']]);b=np.array([p['times'][i]['pixel'][:2] for p in m['trajectories']['points']]);ac=a-a.mean(0);bc=b-b.mean(0)
 u,sv,vt=np.linalg.svd(ac.T@bc);r=u@vt
 if np.linalg.det(r)<0:u[:,-1]*=-1;r=u@vt
 scale=np.trace((ac@r).T@bc)/(ac*ac).sum();fit=ac@r*scale+b.mean(0);errors=np.linalg.norm(fit-b,axis=1)
 res.append({'t':t,'bestSimilarityRmsPx':float(np.sqrt(np.mean(errors**2))),'maxResidualPx':float(errors.max()),'rmsBySurface':{name:float(np.sqrt(np.mean(errors[[j for j,p in enumerate(m['trajectories']['points']) if p['name']==name]]**2))) for name in set(p['name'] for p in m['trajectories']['points'])}})
for line in m['bending']:
 a=np.array([p['deformed'][:2] for p in line['points']]);v=a[-1]-a[0];d=np.abs(v[0]*(a[:,1]-a[0,1])-v[1]*(a[:,0]-a[0,0]))/np.linalg.norm(v);line['maxDeviationFromEndpointLinePx']=float(d.max())
m['localResidualAfterBestSimilarity']=res
names={1:'floor',2:'curtain-w1',3:'curtain-w2',4:'outer-back',5:'outer-left',6:'outer-right',7:'outer-behind',8:'ceiling',9:'estimated-frame',10:'estimated-shelf'}
palette={1:(0,158,115),2:(230,158,0),3:(204,102,0),9:(204,153,179),10:(87,178,230)}
for i in range(4,9):palette[i]=tuple(round(255*(a+(i-4)*.07)) for a in [.18,.24,.3])
m['visiblePixelsBySurfaceAt3_2']={}
for rgb,n in m['visibleOutput'].items():
 if rgb=='holes':m['visiblePixelsBySurfaceAt3_2']['holes']=n;continue
 color=tuple(map(int,rgb.split(',')));key=min(palette,key=lambda k:sum((a-b)**2 for a,b in zip(color,palette[k])))
 m['visiblePixelsBySurfaceAt3_2'][names[key]]=m['visiblePixelsBySurfaceAt3_2'].get(names[key],0)+n
(O/'verification.json').write_text(json.dumps(m,indent=2),encoding='utf-8')
font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',16)
for t in times:
 sheet=Image.new('RGB',(2160,580),'#f7f7f5');d=ImageDraw.Draw(sheet)
 for i,mode in enumerate(['none','global','local']):
  p=O/f'{mode}-{t:.1f}.png';assert p.exists();sheet.paste(Image.open(p).convert('RGB'),(i*720,40));d.text((i*720+14,8),f'{mode} | {t:.1f} s | same fixed display',font=font,fill='#242424')
 sheet.save(O/f'comparison-{t:.1f}.png')
overlay=Image.new('RGB',(990,520),'#f7f7f5');overlay.paste(Image.open(O/'local-native-3.2.png').convert('RGB'),(0,40));d=ImageDraw.Draw(overlay);d.text((12,7),'Fixed camera | world trajectory | 3.2 s',font=font,fill='#242424')
for i,p in enumerate(m['trajectories']['points']):
 q=next(x for x in p['times'] if x['t']==3.2)['pixel'];x,y=q[:2];y+=40;x0,y0=p['original'][:2];y0+=40;d.line((x0,y0,x,y),fill='yellow',width=2);d.ellipse((x-3,y-3,x+3,y+3),fill='red');d.text((min(620,x+4),y),str(i+1),font=font,fill='white');d.text((655,43+i*29),f'{i+1:02}  {p["name"]}',font=font,fill='#242424')
overlay.save(O/'camera-overlay-3.2.png')
hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in O.glob('*.png')}
(O/'frame-hashes.json').write_text(json.dumps(hashes,indent=2),encoding='utf-8')
print(json.dumps({'zeroMAE':m['mae'],'max':m['max'],'coverage':m['coverage'],'visible':m['visiblePixelsBySurfaceAt3_2'],'residuals':res,'bending':{l['name']:l['maxDeviationFromEndpointLinePx'] for l in m['bending']}},indent=2))
