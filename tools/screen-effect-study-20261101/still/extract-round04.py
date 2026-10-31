"""Measure reference edge displacement and normalized edge-shape residual only."""
from pathlib import Path
import numpy as np,json,hashlib,sys
from PIL import Image
sys.stdout.reconfigure(encoding='utf-8')
out=Path(sys.argv[1]);ref=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png');a=np.array(Image.open(ref).convert('L'),dtype=float)/255
x0,y0,x1,y1=880,20,970,300;r=a[y0:y1,x0:x1];positions=[]
for row in r:
    p=next((x for x in range(r.shape[1]-6) if np.all(row[x:x+6]>.72)),None)
    if p is None:raise RuntimeError('window plateau missing')
    positions.append(p+x0)
yy=np.arange(len(positions));fit=np.polyval(np.polyfit(yy,positions,2),yy);jitter=np.array(positions)-fit
offsets=np.arange(-32,33);profiles=[]
left=np.median(r[:,:10]);right=np.median(r[:,-10:]);contrast=right-left
if contrast<.2:raise RuntimeError('insufficient pooled edge contrast')
for n,p in enumerate(positions):
    row=a[y0+n]
    profiles.append((row[p+offsets]-left)/contrast)
profiles=np.array(profiles);baseline=np.median(profiles,axis=0);residual=np.clip(profiles-baseline,-.5,.5)
runs=[]
for row in residual:
    spans=[];begin=None;sign=0
    for n,v in enumerate([*row,0]):
        current=1 if v>.08 else -1 if v<-.08 else 0
        if current!=sign:
            if sign:spans.append({'offset':int(offsets[begin]),'length':n-begin,'sign':sign,'mean':float(np.mean(row[begin:n]))})
            begin=n if current else None;sign=current
    runs.append(spans)
lengths=[s['length'] for row in runs for s in row]
lookup={'source':str(ref),'source_sha256':hashlib.sha256(ref.read_bytes()).hexdigest(),'roi':[x0,y0,x1,y1],'row_count':len(positions),'offset_min':-32,'offset_max':32,'formula':{'luminance':'Pillow convert L /255, scalar only','position':'first x with six consecutive pixels L > .72','trend':'least-squares quadratic x(y), subtracted','residual':'profiles normalized by pooled ROI left10/right10-column medians; aligned on detected edge; subtract all-row median at each offset; clip [-.5,.5]','transfer':'jitter *.78; dimensionless residual times local candidate edge contrast, polarity and .65; local edges only','excluded':'reference RGB, absolute luminance, room silhouette and character not transferred'},'jitter':np.round(jitter,6).tolist(),'residual':np.round(residual,5).tolist(),'runs_threshold_008':runs,'stats':{'jitter_std_px':float(jitter.std()),'jitter_quantiles':np.quantile(jitter,[0,.05,.5,.95,1]).tolist(),'run_length_quantiles':np.quantile(lengths,[0,.5,.9,1]).tolist(),'residual_std':float(residual.std())}}
(out/'round-04-measured-lookup.json').write_text(json.dumps(lookup,ensure_ascii=False,separators=(',',':')),encoding='utf-8');print(json.dumps(lookup['stats'],indent=2))
