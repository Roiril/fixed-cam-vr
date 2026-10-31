"""Round10: fixed round09-A optics, flat-only cleanup, irregular local fault groups."""
from pathlib import Path
import sys,json,hashlib,importlib.util,argparse,base64,time,shutil
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('round09',HERE/'offline-round09.py');r9=importlib.util.module_from_spec(spec);spec.loader.exec_module(r9)
DEFAULT=r9.DEFAULT
PARAMS={'A':{'name':'短い欠けを疎らに置く','groups':1700,'line_mix':0,'fault_mix':.85},'B':{'name':'短い欠けの群を増やす','groups':4200,'line_mix':0,'fault_mix':.95},'C':{'name':'水平の細部を弱く混ぜる','groups':2800,'line_mix':.25,'fault_mix':.9}}

def tone(l):return np.interp(np.clip(l,0,1),[0,.07,.15,.35,.55,.75,1],[.008,.015,.09,.34,.63,.845,.85])
def prepare(l):
    smooth=cv2.fastNlMeansDenoising(r9.gray8(l),None,8,7,21)/255
    edge=cv2.edgePreservingFilter(r9.triple(smooth),flags=cv2.RECURS_FILTER,sigma_s=48,sigma_r=.18);flat=r9.luminance_bgr(edge)*.65+smooth*.35
    mean=cv2.boxFilter(l,-1,(17,17),normalize=True);variance=np.maximum(0,cv2.boxFilter(l*l,-1,(17,17),normalize=True)-mean*mean);std=np.sqrt(variance)
    # Relative variance keeps dark floor structure even when its absolute std is low.
    relative=std/(mean+.04);weight=np.clip((.085-std)/.055,0,1)*np.clip((.18-relative)/.10,0,1);clean=l+(flat-l)*weight
    horizontal=clean-cv2.GaussianBlur(clean,(9,1),1.8);struct=np.clip((std-.012)/.05,0,1);pre=tone(clean+horizontal*1.15*struct)
    gx=cv2.Sobel(l,cv2.CV_64F,1,0,ksize=3)/8;gy=cv2.Sobel(l,cv2.CV_64F,0,1,ksize=3)/8;detail=np.abs(l-cv2.GaussianBlur(l,(0,0),1.4));energy=np.sqrt(gx*gx+gy*gy)*.7+detail*.6
    return clean,pre,std,energy,weight

def lines(clean,std):
    x=cv2.GaussianBlur(clean,(7,1),1.1);lo=cv2.erode(x,np.ones((5,1),np.uint8));hi=cv2.dilate(x,np.ones((5,1),np.uint8));span=hi-lo;u=np.clip(((x-lo)/np.maximum(.005,span)-.12)/.76,0,1);core=.01+.84*u*u*(3-2*u)
    dy=x-cv2.GaussianBlur(x,(1,5),.9);gate=np.clip((span-.018)/.065,0,1)*np.clip(np.abs(dy)/np.maximum(.006,span)*4,0,1)*np.clip((std-.015)/.06,0,1)
    return core,gate

def faults(pre,energy,lookup,p,visible):
    rng=np.random.default_rng(104729);h,w=pre.shape;weights=np.maximum(0,energy-.009)**1.4;weights[~visible]=0;weights/=weights.sum();centers=rng.choice(h*w,size=p['groups'],replace=True,p=weights.ravel());runs=[v for row in lookup['runs_threshold_008'] for v in row];result=pre.copy();mask=np.zeros(pre.shape,bool);jitter=np.array(lookup['jitter']);events=0;longs=0
    for center in centers:
        cy,cx=divmod(int(center),w)
        for child in range(int(rng.integers(2,5))):
            run=runs[int(rng.integers(len(runs)))];long=rng.random()<.07;length=int(rng.integers(16,33)) if long else max(2,min(12,int(run['length'])));height=int(rng.integers(1,4));x=int(np.clip(cx+rng.integers(-10,11),0,w-1));y=int(np.clip(cy+rng.integers(-4,5),0,h-1));x1=min(w,x+length);y1=min(h,y+height)
            if energy[y,x]<.009:continue
            displacement=int(np.clip(np.round(jitter[int(rng.integers(len(jitter)))]*.9),-12,12));displacement=displacement if abs(displacement)>=2 else run['sign']*max(2,min(8,length));dx=np.clip(np.arange(x,x1)+displacement,0,w-1);dy=np.clip(np.arange(y,y1)+(run['sign'] if rng.random()<.25 else 0),0,h-1)
            replacement=pre[np.ix_(dy,dx)];old=pre[y:y1,x:x1];lag=pre[np.ix_(dy,np.clip(dx-run['sign']*2,0,w-1))];new=replacement+(replacement-lag)*.25;new=np.where(visible[np.ix_(dy,dx)],new,old)
            result[y:y1,x:x1]=old+(new-old)*p['fault_mix'];mask[y:y1,x:x1]=True;events+=1;longs+=int(long)
    return np.clip(result,.008,.85),{'events':events,'long_events':longs,'changed_area_fraction':float((mask&visible).sum()/visible.sum()),'placement':'energy-weighted independent coordinates;2..4 child events per group; no regular grid;1..3px high'}

def preview(out):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={key:'data:image/png;base64,'+base64.b64encode((out/(f'round-10-{key}.png' if key!='base' else 'base.png')).read_bytes()).decode() for key in ['base','A','B','C']}
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>局所的な欠けの比較</title><style>CSS
.wide{max-width:72rem;padding:16px}img{display:block;width:100%;height:auto;background:var(--surface)}button,select{font:inherit;padding:8px 16px;margin:4px;border:0;background:var(--surface);color:var(--ink)}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>平坦面を整理して局所的に欠かす</h1><p>9-Aの光学処理を固定したオフライン実出力です。</p><select id="select" aria-label="加工の候補"><option value="A">A：疎らな短い欠け</option><option value="B">B：欠けの群を増やす</option><option value="C">C：水平の細部を弱く混ぜる</option></select><button id="toggle">追加加工を切る</button><p id="status"></p></div><div class="wide"><img id="image" alt="加工候補と元ベースの比較"></div><script>const images=IMAGES;let enabled=true;const s=document.getElementById('select'),b=document.getElementById('toggle');function show(){document.getElementById('image').src=images[enabled?s.value:'base'];document.getElementById('status').textContent=enabled?'追加後：'+s.value:'追加前';b.textContent=enabled?'追加加工を切る':'追加加工を入れる';b.setAttribute('aria-pressed',String(enabled))}s.onchange=show;b.onclick=()=>{enabled=!enabled;show()};show();</script></html>'''
    (out/'round-10-preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--off',action='store_true');args=ap.parse_args();out=args.out;assert sha(out/'base.png')==r9.EXPECTED;src=np.array(Image.open(out/'base.png').convert('RGBA'));assert src.tobytes()==(out/'base.rgba').read_bytes()
    if args.off:shutil.copyfile(out/'base.png',out/'round-10-off.png');assert sha(out/'base.png')==sha(out/'round-10-off.png');print('OFF byte-exact');return
    l=src[:,:,:3].astype(float)@r9.WEIGHTS/255;optical=r9.optical(l,'A');assert np.array_equal(r9.gray8(optical),np.array(Image.open(out/'round-09-A-optical-stage.png'))),'9A optical stage mismatch';lookup=json.loads((out/'round-04-measured-lookup.json').read_text(encoding='utf-8'));clean,pre,std,energy,flatWeight=prepare(optical);core,lineGate=lines(clean,std);visible=src[:,:,3]>0
    repeated=prepare(optical)
    assert all(np.array_equal(a,b) for a,b in zip((clean,pre,std,energy,flatWeight),repeated))
    manifest={'round':10,'base_sha256':r9.EXPECTED,'fixed_09A_optical_sha256':sha(out/'round-09-A-optical-stage.png'),'fixed_stage_regeneration_exact':True,'common_stage_repeat_exact':True,'source_sha256':sha(Path(__file__)),'optical_dependency_source_sha256':sha(HERE/'offline-round09.py'),'opencv':cv2.__version__,'parameters':PARAMS,'candidates':{},'generated_image_used':False,'lookup_sha256':sha(out/'round-04-measured-lookup.json')}
    for key,p in PARAMS.items():
        signal=pre+(core-pre)*lineGate*p['line_mix'];target,events=faults(signal,energy,lookup,p,visible);again,_=faults(signal,energy,lookup,p,visible);assert np.array_equal(target,again);gain=np.divide(target,l,out=np.zeros_like(l),where=l>.00001);a=src.copy();a[:,:,:3]=r9.gray8(src[:,:,:3]/255*gain[:,:,None]);a[~visible]=src[~visible];assert np.array_equal(a[:,:,3],src[:,:,3]);assert np.array_equal(a[~visible],src[~visible]);im=Image.fromarray(a);im.save(out/f'round-10-{key}.png');bg=Image.new('RGBA',im.size,(18,18,18,255));bg.alpha_composite(im);bg.convert('RGB').save(out/f'round-10-{key}-on-black.png')
        for name,roi in {'full':(0,0,1280,1030),'curtain':(440,180,840,520),'floor':(420,800,860,940),'flat':(110,200,225,430)}.items():labelled_pair(Image.fromarray(src),im,roi,out/f'round-10-{key}-comparison-{name}.png',f'round-10-{key}.png')
        manifest['candidates'][key]={'sha256':sha(out/f'round-10-{key}.png'),'alpha_exact':True,'transparent_exact':True,'repeat_exact':True,'events':events};print(json.dumps({key:manifest['candidates'][key]},indent=2))
    preview(out);(out/'round-10-processing.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
if __name__=='__main__':main()
