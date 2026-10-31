"""Reconstruct a soft transmission band from frozen R5 alpha. No scene RGB."""
from pathlib import Path
import json,cv2,numpy as np
from PIL import Image
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
OUT=BASE/'round-07/assets';OLD=BASE/'round-05/assets'
m=json.loads((OUT/'effect-assets.json').read_text(encoding='utf-8'))
old=json.loads((OLD/'effect-assets.json').read_text(encoding='utf-8'))
meta=old['sequences']['invasion'];atlas=np.array(Image.open(OLD/meta['file']))
w,h=meta['cell'];cols=meta['columns'];yy,xx=np.indices((540,720))
rng=np.random.default_rng(713)
coherent=cv2.resize(rng.uniform(-1,1,(70,92)).astype(np.float32),(720,540),interpolation=cv2.INTER_CUBIC)
micro=cv2.resize(rng.uniform(0,1,(270,360)).astype(np.float32),(720,540),interpolation=cv2.INTER_CUBIC)
result=Image.new('RGBA',Image.open(OLD/meta['file']).size)
edge=Image.new('RGBA',Image.open(OUT/'edge-granules.png').size)
edgeMeta=m['sequences']['edgeGranules'];ec=edgeMeta['columns']
stats=[]
for i,t in enumerate(meta['times']):
    a=atlas[i//cols*h:(i//cols+1)*h,i%cols*w:(i%cols+1)*w,3]
    a=cv2.resize(a,(720,540),interpolation=cv2.INTER_LINEAR)
    if 11.5<=t<12.8:
        ink=(a>=128).astype(np.uint8)
        sdf=cv2.distanceTransform(ink,cv2.DIST_L2,5)-cv2.distanceTransform(1-ink,cv2.DIST_L2,5)
        sdf=cv2.GaussianBlur(sdf,(0,0),2.2)
        n=np.roll(coherent,(round(t*13),round(t*7)),axis=(0,1))
        fine=np.roll(micro,(round(t*41),round(t*31)),axis=(0,1))
        # A 5..12 pixel band, varying continuously along the organic front.
        width=7.+5.*np.clip(n+.45,0,1)
        transmission=np.clip(.5-(sdf+n*3.5)/width,0,1)
        band=np.clip(1-np.abs(sdf)/24.,0,1)
        holes=np.clip((fine-.64)*3.7,0,1)*band
        transmission*=1-holes*.80
        # Tiny soft remnants beside the front, including the lower border.
        fleck=np.clip((fine-.79)*5.,0,.35)*np.clip(1-np.abs(sdf-7)/15,0,1)
        transmission=np.maximum(transmission,fleck*(.35+.65*yy/539))
        a=np.round((1-transmission)*255).astype(np.uint8)
        grains=np.clip((fine-.73)*5,0,1)*band*90
        grain=np.zeros((540,720,4),np.uint8);grain[:,:,:3]=255;grain[:,:,3]=grains.astype(np.uint8)
    else:grain=np.zeros((540,720,4),np.uint8)
    if t>=12.8:a[:]=255
    z=np.full((540,720,4),255,np.uint8);z[:,:,3]=a
    tile=Image.fromarray(z).resize((w,h),Image.Resampling.LANCZOS)
    result.paste(tile,(i%cols*w,i//cols*h));edge.paste(Image.fromarray(grain),(i%ec*720,i//ec*540))
    if 12<=t<=12.7:stats.append({'t':t,'meanOpacity':float(a.mean()/255),'partialOpacityPixels':int(((a>8)&(a<247)).sum())})
for name,img in [('invasion-matte.png',result),('edge-granules.png',edge)]:
    temp=OUT/(name+'.tmp');img.save(temp,format='PNG');temp.replace(OUT/name)
m['version']='7.0.0';m['sequences']['invasion']=meta
m['sequences']['invasion']['method']='R5 measured scalar front; 5..12px soft transmission, coherent microholes and tiny fringe remnants; no R6 channels'
m['parts']['procedural']=[p for p in m['parts']['procedural'] if 'island division' not in p]
m['parts']['procedural']+=['soft uneven transmission and microholes from frozen R5 front','background-only brightness/contrast and blur during final invasion','small affine-driven perceptual projective yaw/pitch; not measured parallax']
temp=OUT/'effect-assets.json.tmp';temp.write_text(json.dumps(m,separators=(',',':')),encoding='utf-8');temp.replace(OUT/'effect-assets.json')
(OUT.parent/'soft-front-metrics.json').write_text(json.dumps(stats,indent=2),encoding='utf-8')
print('soft front rebuilt, no scene RGB or large channels')
