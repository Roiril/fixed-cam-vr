"""Build a static reference and a self-contained interactive Canvas study."""
from __future__ import annotations
import argparse, base64, hashlib, json, shutil, sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding='utf-8')
HERE=Path(__file__).resolve().parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-10-30_crt-reference/crt-tuner/screen-only.png')
REFERENCE=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png')
DEFAULT_OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still')
DEFAULTS=dict(strength=1,tear=.8,echo=.65,grain=.65,contrast=.38,shadow=.5,seed=104729)

def hash_noise(x,y,seed):
    v=np.asarray(x,dtype=np.uint32)*np.uint32(374761393)^np.asarray(y,dtype=np.uint32)*np.uint32(668265263)^np.uint32((seed*1274126177)&0xffffffff)
    v=(v^(v>>13))*np.uint32(1274126177)
    return (v^(v>>16)).astype(np.float64)/4294967295

def process(src,p):
    if p['strength']==0:return src.copy()
    h,w=src.shape[:2];y,x=np.indices((h,w));s=p['strength'];rgb=src[:,:,:3]/255.
    lum=rgb@np.array([.2126,.7152,.0722])
    def l(dx=0,dy=0):return lum[np.clip(y+dy,0,h-1),np.clip(x+dx,0,w-1)]
    edge=np.minimum(1,np.abs(l(1)-l(-1))*5+np.abs(l()-l(0,-1))*2)
    shift=np.floor((hash_noise(0,y,p['seed'])-.5)*2*3.5*p['tear']*s+.5)
    sx=np.clip(x+shift*(.18+.82*edge),0,w-1);a=np.floor(sx).astype(int);b=np.minimum(w-1,a+1);f=(sx-a)[...,None]
    valid=(src[y,a,3]>0)&(src[y,b,3]>0)
    warped=(rgb[y,a]*(1-f)+rgb[y,b]*f)
    warped=np.where(valid[...,None],warped,rgb);old=warped@np.array([.2126,.7152,.0722])
    high=l()-.5*(l(-2)+l(2));ring=(l(-2)-l(3))*.18
    target=(old-.5)*(1+.65*p['contrast']*s)+.5
    target-=p['shadow']*.13*s*np.maximum(0,1-np.abs(target-.2)/.2)
    target+=(high*.55+ring)*p['echo']*s
    noise=(hash_noise(x//3,y,p['seed']+1)-.5)*.7+(hash_noise(x,y,p['seed']+2)-.5)*.3
    visible=np.clip(old/.10,0,1)*(.18+.82*edge)*(.35+.65*np.sin(np.pi*np.clip(old,0,1)))
    target=np.clip(target+noise*.16*p['grain']*s*visible,0,1)
    gain=np.divide(target,old,out=np.zeros_like(old),where=old>.00001)
    result=src.copy();result[:,:,:3]=np.floor(np.clip(warped*gain[...,None],0,1)*255+.5).astype(np.uint8)
    result[src[:,:,3]==0]=src[src[:,:,3]==0]
    return result

def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()

def labelled_pair(before,after,box,path,after_name='still-default.png',before_name='base.png'):
    tiles=[before.crop(box),after.crop(box)];w,h=tiles[0].size
    panel_width=max(w,360)
    sheet=Image.new('RGB',(panel_width*2,h+48),'white');draw=ImageDraw.Draw(sheet)
    font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',18)
    for n,(label,tile) in enumerate(zip(['追加前 / '+before_name,'追加後 / '+after_name],tiles)):
        draw.text((n*panel_width+12,12),label,fill=(26,26,26),font=font)
        bg=Image.new('RGBA',tile.size,(18,18,18,255));bg.alpha_composite(tile);sheet.paste(bg.convert('RGB'),(n*panel_width+(panel_width-w)//2,48))
    sheet.save(path)

def build(out):
    out.mkdir(parents=True,exist_ok=True)
    shutil.copyfile(BASE,out/'base.png')
    src=np.array(Image.open(BASE).convert('RGBA'));h,w=src.shape[:2]
    generated={}
    for name,strength in [('soft',.55),('default',1),('strong',1.4)]:
        p={**DEFAULTS,'strength':strength};arr=process(src,p);path=out/f'still-{name}.png';Image.fromarray(arr).save(path);generated[name]=path
    after=Image.open(generated['default']);before=Image.open(BASE).convert('RGBA')
    for name,im in [('base',before),('still-default',after)]:
        bg=Image.new('RGBA',im.size,(18,18,18,255));bg.alpha_composite(im);bg.convert('RGB').save(out/f'{name}-on-black.png')
    labelled_pair(before,after,(0,0,w,h),out/'comparison-full.png')
    crops={'curtain':(440,180,840,520),'rail':(180,420,620,680),'shadow':(780,620,1180,920)}
    for name,box in crops.items():labelled_pair(before,after,box,out/f'comparison-{name}.png')
    ab=process(src,DEFAULTS);off=process(src,{**DEFAULTS,'strength':0})
    visible=src[:,:,3]>0;delta=np.abs(ab[:,:,:3].astype(int)-src[:,:,:3].astype(int))
    checks={'off_exact':bool(np.array_equal(off,src)),'alpha_exact':bool(np.array_equal(ab[:,:,3],src[:,:,3])),'transparent_rgb_exact':bool(np.array_equal(ab[~visible],src[~visible])),'changed_visible_pixels':int(np.any(delta>0,axis=2)[visible].sum()),'visible_pixels':int(visible.sum()),'mean_abs_rgb_delta_8bit':float(delta[visible].mean()),'dimensions':[w,h]}
    if not all(checks[k] for k in ['off_exact','alpha_exact','transparent_rgb_exact']):raise RuntimeError(checks)
    manifest={'base':{'source':str(BASE),'copy':'base.png','sha256':sha(BASE),'size':[w,h],'kind':'既存Unity実描画PNG。中心+0.12EVと薄い寒色2.5%を含む採用直前の枠なし版。追加試作はこの画素から開始。'},'observation_reference':{'source':str(REFERENCE),'sha256':sha(REFERENCE),'usage':'目視観察のみ。画素・キャラ・色相を出力素材へ転用しない。'},'parameters':DEFAULTS,'variants':{'soft':.55,'default':1,'strong':1.4},'crops':crops,'checks':checks,'outputs':{p.name:sha(p) for p in [out/'base.png',*generated.values()]},'implementation':{'python_sha256':sha(HERE/'build.py'),'javascript_sha256':sha(HERE/'effect.js'),'color_space':'PNGの表示RGB。既存CRT描画への追加処理。指定色への置換なし。','alpha':'不変。全透明画素のRGBも不変。'}}
    (out/'processing.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    style=(Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css')).read_text(encoding='utf-8')
    js=(HERE/'effect.js').read_text(encoding='utf-8');template=(HERE/'index.template.html').read_text(encoding='utf-8')
    uri='data:image/png;base64,'+base64.b64encode(BASE.read_bytes()).decode()
    html=template.replace('__STYLE__',style).replace('__EFFECT__',js).replace('__BASE_URI__',uri)
    (out/'preview.html').write_text(html,encoding='utf-8')
    shutil.copyfile(HERE/'analysis.md',out/'analysis.md')
    (out/'base.rgba').write_bytes(src.tobytes());(out/'python-default.rgba').write_bytes(ab.tobytes())
    print(json.dumps({'out':str(out),'base_sha256':sha(BASE),'checks':checks},ensure_ascii=False,indent=2))

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--out',type=Path,default=DEFAULT_OUT);args=parser.parse_args();build(args.out)
