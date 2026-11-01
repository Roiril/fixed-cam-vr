from pathlib import Path
from PIL import Image,ImageDraw
import numpy as np,json,hashlib,sys
sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study')
OUT=ROOT/'revision-02/motion/smoke-proof'
m=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'))
base=Image.open(ROOT/'motion/round-07/assets/base-b.png').convert('RGBA')
original=np.asarray(base).copy()
for row in m['records']:
    layers={}
    for name,item in row['layers'].items():
        p=Path(item['path']);data=p.read_bytes();assert hashlib.sha256(data).hexdigest()==item['sha256']
        assert len(data)==720*540*4
        im=Image.frombytes('RGBA',(720,540),data);target=p.with_suffix('.png');im.save(target)
        layers[name]=np.asarray(im).copy();item['png']=str(target)
    vis=np.minimum(1,(layers['old'][:,:,0].astype(float)+layers['new'][:,:,0].astype(float))/255)
    fx=layers['particles'][:,:,3].astype(float)/255
    for label,color in [('black-flat',0),('neutral-flat',156),('white-flat',255)]:
        a=np.clip(color*vis+255*fx,0,255).astype(np.uint8);Image.fromarray(np.repeat(a[:,:,None],3,axis=2)).save(OUT/f"{row['t']:.3f}-{label}.png")
    # Native input pixels unchanged; only scalar smoke mask is resized for this diagnostic.
    visibility=Image.fromarray(np.round(vis*255).astype(np.uint8)).resize(base.size,Image.Resampling.BILINEAR)
    particles=Image.fromarray(np.round(fx*255).astype(np.uint8)).resize(base.size,Image.Resampling.BILINEAR)
    rgb=np.clip(original[:,:,:3].astype(float)*np.asarray(visibility)[:,:,None]/255+np.asarray(particles)[:,:,None],0,255).astype(np.uint8)
    rgba=original.copy();rgba[:,:,:3]=rgb;after=Image.fromarray(rgba)
    checker=np.where((np.indices((1030,1280)).sum(axis=0)//24)%2,75,105).astype(np.uint8)
    bg=Image.fromarray(np.repeat(checker[:,:,None],3,axis=2)).convert('RGBA');bg.alpha_composite(after)
    before=Image.new('RGBA',base.size,(85,85,85,255));before.alpha_composite(base)
    sheet=Image.new('RGB',(2560,1060),'#e7e3db');sheet.paste(before.convert('RGB'),(0,30));sheet.paste(bg.convert('RGB'),(1280,30));d=ImageDraw.Draw(sheet)
    d.text((8,8),f"before native input 1280x1030 / {row['t']:.3f}s",fill='black');d.text((1288,8),f"after / scalar normalized UV diagnostic / no camera mapping yet",fill='black')
    sheet.save(OUT/f"{row['t']:.3f}-input-native-before-after.png")
    sheet=Image.new('RGB',(720*4,570*2),'#e7e3db');d=ImageDraw.Draw(sheet)
    for i,name in enumerate(['entryA','oldWindow','exitB','endingC','old','new','density','particles']):
        im=Image.fromarray(layers[name])
        if name=='particles':
            black=Image.new('RGBA',im.size,(0,0,0,255));black.alpha_composite(im);im=black
        im=im.convert('RGB');x=i%4*720;y=i//4*570;sheet.paste(im,(x,y+30));d.text((x+8,y+8),f"{row['t']:.3f}s / {name} / 720x540",fill='black')
    sheet.save(OUT/f"{row['t']:.3f}-fields-native.png")
    for item in row['layers'].values():
        p=Path(item['path']);assert p.with_suffix('.png').exists();p.unlink();item.pop('path')
(OUT/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8')
print('80 field PNGs, 30 flat composites, 10 native input comparisons, 10 native field decompositions')
