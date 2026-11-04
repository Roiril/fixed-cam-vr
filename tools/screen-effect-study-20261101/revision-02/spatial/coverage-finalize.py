from pathlib import Path
import json,hashlib
import numpy as np
from PIL import Image,ImageDraw,ImageFont
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/coverage')
m=json.loads((O/'render-metrics.json').read_text(encoding='utf-8'))
def residual(a,b):
 a=np.array(a);b=np.array(b);ac=a-a.mean(0);bc=b-b.mean(0);u,sv,vt=np.linalg.svd(ac.T@bc);r=u@vt
 if np.linalg.det(r)<0:u[:,-1]*=-1;r=u@vt
 scale=np.trace((ac@r).T@bc)/(ac*ac).sum();e=np.linalg.norm(ac@r*scale+b.mean(0)-b,axis=1)
 return float(np.sqrt(np.mean(e*e)))
for i,t in enumerate([0,1.2,3.2,4.2,6.5,8.5,10.5,12.4]):
 a=np.array([p['original'][:2] for p in m['trajectories']['points']]);b=np.array([p['times'][i]['pixel'][:2] for p in m['trajectories']['points']]);g=np.array([p['times'][i]['globalPixel'][:2] for p in m['trajectories']['points']]);names=[p['name'] for p in m['trajectories']['points']]
 local=np.array([p['times'][i]['localPixel'][:2] for p in m['trajectories']['points']]);x=np.column_stack([a,np.ones(len(a))]);S=np.linalg.lstsq(x,g,rcond=None)[0];removed=(b-S[2])@np.linalg.inv(S[:2]);m.setdefault('globalSRemoved',[]).append({'t':t,'rmsResidualAfterSimilarityPx':residual(a,removed),'localOnlyRmsAfterSimilarityPx':residual(a,local),'inverseSvsLocalMaxPx':float(np.max(np.linalg.norm(removed-local,axis=1)))})
 m.setdefault('residuals',[]).append({'t':t,'combinedAfterBestSimilarityRmsPx':residual(a,b),'combinedMinusGlobalRmsPx':float(np.sqrt(np.mean(np.sum((b-g)**2,axis=1)))),'bySurface':{name:float(np.sqrt(np.mean(np.sum((b[np.array(names)==name]-g[np.array(names)==name])**2,axis=1)))) for name in set(names)}})
font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',16)
for t in [3.2,6.5,8.5,10.5]:
 for kind,w,h in [('full',720,540),('native',640,480)]:
  c=Image.new('RGB',(w*2,(h+36)*2),'#f7f7f5');d=ImageDraw.Draw(c)
  for i,mode in enumerate(['combined','global','local','none']):
   p=O/(f'{mode}-{t:.1f}.png' if kind=='full' else f'{mode}-native-{t}.png');assert p.exists();x=i%2*w;y=i//2*(h+36);c.paste(Image.open(p).convert('RGB'),(x,y+36));d.text((x+12,y+5),f'{mode} | {t}s | crop '+('1.35' if kind=='full' else 'none'),font=font,fill='#242424')
  c.save(O/f'comparison-{kind}-{t}.png')
 im=Image.open(O/f'combined-native-{t}.png').convert('RGB');d=ImageDraw.Draw(im);d.rectangle((83,62,557,418),outline='red',width=2);im.save(O/f'crop-diagnostic-{t}.png')
m['flatGpuCalibration']=json.loads((O/'flat-gpu-calibration.json').read_text(encoding='utf-8'))
m['sourceHashesUnchanged']={f:hashlib.sha256((H/f).read_bytes()).hexdigest() for f in ['renderer.js','renderer-pinned.js','geometry.js','assets.js','pinned-field.js']}
(O/'verification.json').write_text(json.dumps(m,indent=2),encoding='utf-8')
(O/'frame-hashes.json').write_text(json.dumps({p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in O.glob('*.png')},indent=2),encoding='utf-8')
print(json.dumps({'zero':m['zeroModes'],'measurements':[{'t':p['t'],'mode':p['mode'],'holes':p['holes'],'cropHoles':p['cropHoles']} for p in m['combinedMeasurements']],'residuals':m['residuals'],'globalSRemoved':m['globalSRemoved']},indent=2))
