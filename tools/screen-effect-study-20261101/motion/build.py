from pathlib import Path
from PIL import Image,ImageDraw
import base64,json,sys
import numpy as np
from background_metric import occlusion_fraction
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path(__file__).parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
OUT=BASE/'round-07'
OUT.mkdir(exist_ok=True)
def publish_text(name,text):
    target=OUT/name;temporary=target.with_name(target.name+'.tmp');temporary.write_text(text,encoding='utf-8');temporary.replace(target)
html=(ROOT/'index.html').read_text(encoding='utf-8')
js=(ROOT/'motion.js').read_text(encoding='utf-8').replace('export function renderAt','function renderAt')
js=js.replace("const cameraTrack=await (await fetch('./motion-track.json')).json();",'const cameraTrack='+json.dumps(json.loads((ROOT/'motion-track.json').read_text(encoding='utf-8')),separators=(',',':'))+';')
manifest=json.loads((OUT/'assets/effect-assets.json').read_text(encoding='utf-8'))
names=['base-a.png','base-b.png']+[r['file'] for r in manifest['sequences'].values()]
baked={'manifest':manifest,'images':{n:'data:image/png;base64,'+base64.b64encode((OUT/'assets'/n).read_bytes()).decode() for n in names}}
marker="import {createReferenceEffects} from './reference-effects.js';"
assert marker in js
js=js.replace(marker,'')
effectJS=(ROOT/'reference-effects.js').read_text(encoding='utf-8').replace('export async function','async function')
js='globalThis.__motionReferenceAssets='+json.dumps(baked,separators=(',',':'))+';\n'+effectJS+'\n'+js
html=html.replace('<script type="module" src="motion.js"></script>','<script type="module">'+js+'</script>')
publish_text('motion-prototype.html',html)
times=[0,1,2,3,4,5,6,7,8,9,10,11,11.4,11.8,12.2,12.4,12.6,12.8,13,13.6,14,15]
w=585;h=298
sheet=Image.new('RGB',(w*2,h*len(times)),(245,245,245));draw=ImageDraw.Draw(sheet)
for i,t in enumerate(times):
    for j,prefix in enumerate(['ref','prototype']):
        p=(BASE if prefix=='ref' else OUT)/f'{prefix}-{t:06.3f}.png'
        if not p.exists():raise FileNotFoundError(p)
        im=Image.open(p).convert('RGB');im.thumbnail((w,270))
        sheet.paste(im,(j*w,i*h+28));draw.text((j*w+8,i*h+7),f'{t:.3f}s | {prefix} | {p.name}',fill='black')
sheet.save(OUT/'time-aligned-comparison.jpg',quality=92)
overview=Image.new('RGB',(468*4,244*4),(24,24,24));od=ImageDraw.Draw(overview)
for t in range(16):
    p=OUT/f'prototype-{t:06.3f}.png'
    if not p.exists():raise FileNotFoundError(p)
    im=Image.open(p).convert('RGB');im.thumbnail((468,216));x=t%4*468;y=t//4*244;overview.paste(im,(x,y+28));od.text((x+8,y+7),f'{t:.3f}s | round-07 | {p.name}',fill='white')
overview.save(OUT/'candidate-overview.jpg',quality=93)
metrics={}
for t in [0,11.4,11.8,12.2,12.4,12.6,12.8,13,13.3,13.6,14]:
    p=OUT/f'prototype-{t:06.3f}.png';im=Image.open(p).convert('L');im=im.crop((225,0,945,540))
    hist=im.histogram();count=im.width*im.height
    metrics[str(t)]={'fractionAbove32':sum(hist[33:])/count,'meanLuminance':sum(i*n for i,n in enumerate(hist))/count}
publish_text('pixel-metrics.json',json.dumps(metrics,indent=2))
assert metrics['13.6']['meanLuminance']==0,metrics['13.6']
assert metrics['12.4']['fractionAbove32']>0.03,'central scene must remain visible at 12.4'
assert metrics['12.8']['fractionAbove32']<metrics['12.2']['fractionAbove32']
occlusion={}
for t in [11.4,11.5,11.8,12.2,12.4,12.6,12.8,14.4]:
    label=f'{t:06.3f}'
    a=np.array(Image.open(OUT/f'background-{label}.png').convert('L'),dtype=float)[:,225:945]
    b=np.array(Image.open(OUT/f'control-{label}.png').convert('L'),dtype=float)[:,225:945]
    valid=b>32
    occlusion[str(t)]=occlusion_fraction(a,b,t)
publish_text('background-occlusion.json',json.dumps(occlusion,indent=2))
assert occlusion['11.5']>0.01,'outer invasion must be present by 11.5'
assert occlusion['12.2']>occlusion['11.5']
assert occlusion['12.4']<.99,'some background must remain at 12.4 independently of particles'
assert occlusion['12.6']>.90,'background must almost vanish by 12.6'
assert occlusion['14.4']<.01,'no reentry of invasion after black hold'
print('standalone HTML, comparison, closure assertions: PASS')
