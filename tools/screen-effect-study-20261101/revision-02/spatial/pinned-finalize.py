from pathlib import Path
import json,hashlib
import numpy as np
from PIL import Image,ImageDraw,ImageFont
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/pinned')
m=json.loads((O/'render-metrics.json').read_text(encoding='utf-8'))
def similarity(a,b):
 a=np.asarray(a);b=np.asarray(b);ac=a-a.mean(0);bc=b-b.mean(0);u,sv,vt=np.linalg.svd(ac.T@bc);r=u@vt
 if np.linalg.det(r)<0:u[:,-1]*=-1;r=u@vt
 scale=np.trace((ac@r).T@bc)/(ac*ac).sum();errors=np.linalg.norm(ac@r*scale+b.mean(0)-b,axis=1)
 return float(np.sqrt(np.mean(errors**2)))
for t in [0,1.2,3.2,4.2,6.5,8.5,10.5,12.4]:
 a=[p['original'][:2] for p in m['trajectories']['points']];b=[next(q for q in p['times'] if q['t']==t)['pixel'][:2] for p in m['trajectories']['points']]
 m.setdefault('localResidualAfterBestSimilarity',[]).append({'t':t,'rmsPx':similarity(a,b)})
for line in m['bending']:
 a=np.array([p['deformed'][:2] for p in line['points']]);v=a[-1]-a[0];d=np.abs(v[0]*(a[:,1]-a[0,1])-v[1]*(a[:,0]-a[0,0]))/np.linalg.norm(v);line['maxDeviationFromEndpointLinePx']=float(d.max())
font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',16)
for t in [3.2,8.5,10.5]:
 sheet=Image.new('RGB',(1920,520),'#f7f7f5');d=ImageDraw.Draw(sheet)
 for i,(kind,name)in enumerate([('pinned',f'pinned-native-{t}.png'),('holes',f'holes-native-{t}.png'),('weight',f'weight-native-{t}.png')]):
  sheet.paste(Image.open(O/name).convert('RGB'),(i*640,40));d.text((i*640+12,7),f'{kind} | 12px boundary | {t}s',font=font,fill='#242424')
 sheet.save(O/f'comparison-pinned-holes-weight-{t}.png')
m['sourceHashesUnchanged']={f:hashlib.sha256((H/f).read_bytes()).hexdigest() for f in ['renderer.js','geometry.js','assets.js']}
(O/'verification.json').write_text(json.dumps(m,indent=2),encoding='utf-8')
(O/'frame-hashes.json').write_text(json.dumps({p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in O.glob('*.png')},indent=2),encoding='utf-8')
print(json.dumps({'holes':m['pinnedMeasurements']['rows'],'residuals':m['localResidualAfterBestSimilarity'],'bending':{l['name']:l['maxDeviationFromEndpointLinePx'] for l in m['bending']}},indent=2))
