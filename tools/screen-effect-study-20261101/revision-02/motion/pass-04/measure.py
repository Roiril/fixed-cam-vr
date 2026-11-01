from pathlib import Path
import json,hashlib,sys
import numpy as np
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion/pass-04');D=OUT/'field-proof'
m=json.loads((D/'manifest.json').read_text(encoding='utf-8'));rows=[]
yy,xx=np.mgrid[:540,:720];x=(xx+.5)/720;y=(yy+.5)/540
regions={'TL':(x<.5)&(y<.5),'TR':(x>=.5)&(y<.5),'BL':(x<.5)&(y>=.5),'BR':(x>=.5)&(y>=.5)}
def stats(v):
    total=float(v.sum());return {'alphaAreaPixels':total,'centroidUV':None if total<1e-9 else [float((v*x).sum()/total),float((v*y).sum()/total)],'visiblePixelsAbove0.1':int((v>.1).sum()),'quadrants':{n:{'alphaAreaPixels':float(v[r].sum()),'visiblePixelsAbove0.1':int((v[r]>.1).sum())} for n,r in regions.items()},'left35AlphaAreaPixels':float(v[x<.35].sum())}
for item in m['rows']:
    t=item['time'];fields={k:np.frombuffer(Path(p).read_bytes(),dtype=np.uint8).reshape(540,720,4) for k,p in item['fields'].items()}
    for k,v in fields.items():Image.fromarray(v).save(D/f'{t:.3f}-{k}.png')
    old=fields['old'][:,:,0]/255;new=fields['new'][:,:,0]/255;particle=fields['particles'][:,:,3]/255
    for label,level in [('neutral',128),('black',0)]:
        rgb=np.clip(level*(old+new)+particle*255,0,255).astype(np.uint8);Image.fromarray(np.repeat(rgb[:,:,None],3,2)).save(D/f'{t:.3f}-{label}.png')
    bgPath=OUT/'background-proof'/f'new-{t:.3f}.png';assert bgPath.is_file(),bgPath
    bg=np.asarray(Image.open(bgPath).convert('RGB'),dtype=float)
    actual=np.clip(bg*new[:,:,None],0,255).astype(np.uint8);Image.fromarray(actual).save(D/f'{t:.3f}-actual-new.png')
    lum=actual.mean(axis=2);bright=lum>20
    row={'time':t,'old':stats(old),'new':stats(new),'actualNewContribution':{'brightnessWeighted':stats(lum/255),'brightPixelsAbove20':stats(bright.astype(float)),'backgroundPath':str(bgPath)},'edgeBandPixels3to9':True}
    rows.append(row)
    if 3.6<=t<=4.2:assert row['new']['left35AlphaAreaPixels']==0
record={'size':[720,540],'definitions':{'alphaAreaPixels':'sum of scalar visibility; four equal quadrants','actualNewContribution':'new independent-display B view multiplied by new visibility; excludes old view and all foreground effects','brightPixelsAbove20':'mean RGB >20, x/y centroids in active UV; measured on actual B contribution, not flat'},'rows':rows,'sourceRGBIncludedAsSmoke':False}
(OUT/'smoke-measurements.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{'t':r['time'],'newCentroid':r['new']['centroidUV'],'left35':r['new']['left35AlphaAreaPixels'],'actualBrightCentroid':r['actualNewContribution']['brightPixelsAbove20']['centroidUV'],'actualBrightCount':r['actualNewContribution']['brightPixelsAbove20']['visiblePixelsAbove0.1']} for r in rows],ensure_ascii=False))
