"""Round12: longer local fragments, weak directional cores, zero-mean dark variation."""
from pathlib import Path
import sys,argparse,base64,importlib.util,json,shutil
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
def module(name,file):
    s=importlib.util.spec_from_file_location(name,HERE/file);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
r11=module('round11','offline-round11.py');r9=r11.r9;r10=r11.r10
SOURCE=r11.DEFAULT;DEFAULT=SOURCE.parent/'round-12'
PROFILES={'soft':{'length_scale':1.15,'tail_max':40,'line_mix':.18,'detail_gain':.3,'black_variation':.003},'medium':{'length_scale':1.4,'tail_max':44,'line_mix':.30,'detail_gain':.6,'black_variation':.0045},'strong':{'length_scale':1.65,'tail_max':48,'line_mix':.45,'detail_gain':.9,'black_variation':.006}}

def line_core(clean,std,p):
    x=cv2.GaussianBlur(clean,(7,1),1.1);lo=cv2.erode(x,np.ones((5,1),np.uint8));hi=cv2.dilate(x,np.ones((5,1),np.uint8));span=hi-lo
    dy1=x-cv2.GaussianBlur(x,(1,5),.8);dy2=x-cv2.GaussianBlur(x,(1,9),1.6);persist=(dy1*dy2>0)*np.clip(np.abs(dy2)/(np.abs(dy1)+.003),0,1)
    position=(x-lo)/np.maximum(.005,span);u=np.clip((position-.10)/.80,0,1);core=.01+.84*u*u*(3-2*u)
    gate=np.clip((span-.012)/.065,0,1)*np.clip(np.abs(dy1)/np.maximum(.006,span)*4,0,1)*np.clip((std-.012)/.05,0,1)*persist
    return core,gate,dy1

def faults(pre,energy,lookup,visible,p):
    rng=np.random.default_rng(104729);h,w=pre.shape;weights=np.maximum(0,energy-.009)**1.4;weights[~visible]=0;weights/=weights.sum();centers=rng.choice(h*w,size=4200,replace=True,p=weights.ravel());runs=[v for row in lookup['runs_threshold_008'] for v in row];result=pre.copy();mask=np.zeros(pre.shape,bool);jitter=np.array(lookup['jitter']);events=longs=0;lengths=[]
    for center in centers:
        cy,cx=divmod(int(center),w)
        for child in range(int(rng.integers(2,5))):
            run=runs[int(rng.integers(len(runs)))];long=rng.random()<.07
            length=int(rng.integers(20,p['tail_max']+1)) if long else max(3,min(20,int(round(run['length']*p['length_scale']))));height=int(rng.integers(1,4));x=int(np.clip(cx+rng.integers(-10,11),0,w-1));y=int(np.clip(cy+rng.integers(-4,5),0,h-1));x1=min(w,x+length);y1=min(h,y+height)
            if energy[y,x]<.009:continue
            displacement=int(np.clip(np.round(jitter[int(rng.integers(len(jitter)))]*.9),-14,14));displacement=displacement if abs(displacement)>=2 else run['sign']*max(3,min(10,length));dx=np.clip(np.arange(x,x1)+displacement,0,w-1);dy=np.clip(np.arange(y,y1)+(run['sign'] if rng.random()<.25 else 0),0,h-1)
            replacement=pre[np.ix_(dy,dx)];old=pre[y:y1,x:x1];lag=pre[np.ix_(dy,np.clip(dx-run['sign']*2,0,w-1))];new=replacement+(replacement-lag)*.25;new=np.where(visible[np.ix_(dy,dx)],new,old);result[y:y1,x:x1]=old+(new-old)*.95;mask[y:y1,x:x1]=True;events+=1;longs+=int(long);lengths.append(length)
    return np.clip(result,.001,.85),{'events':events,'long_events':longs,'length_quantiles':np.quantile(lengths,[0,.5,.9,1]).tolist(),'fault_mask_area_fraction':float((mask&visible).sum()/visible.sum())}

def dark_variation(target,l,p,visible):
    rng=np.random.default_rng(8675309);random=cv2.GaussianBlur(rng.uniform(-1,1,l.shape),(9,1),1.8);pattern=np.tanh(random*4)
    native=cv2.GaussianBlur(l,(7,1),1.2)-cv2.GaussianBlur(l,(0,0),3);pattern=pattern*.7+np.clip(native/.01,-1,1)*.3
    weight=np.clip((.05-target)/.035,0,1)*visible
    pattern-=np.sum(pattern*weight)/max(1e-9,weight.sum());variation=pattern*p['black_variation']*weight
    return np.clip(target+variation,.001,.85),{'weighted_zero_mean_before_clip':float(variation.sum()/max(1,visible.sum())),'maximum_amplitude_requested':p['black_variation']}

def roi_stats(im):
    a=np.array(im);l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;rois={'flat_wall':(180,250,500,500),'dark_with_edge':(790,420,900,560),'black_interior':(820,450,880,530),'floor':(680,740,1100,900)}
    return {n:{'box':list(b),'mean':float(l[b[1]:b[3],b[0]:b[2]].mean()),'std':float(l[b[1]:b[3],b[0]:b[2]].std())} for n,b in rois.items()}

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={};available={}
    for camera in m['inputs']:
        p=out/f'base-{camera}.png';images[f'{camera}-base']='data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode();available[camera]=[]
    for key,v in m['outputs'].items():
        p=out/v['file'];assert sha(p)==v['sha256'];images[key]='data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode();camera,profile=key.split('-');available[camera].append(profile)
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>短片と細部の3段階</title><style>CSS
.wide{max-width:72rem;padding:16px}img{display:block;width:100%;height:auto;background:var(--surface)}button,select{font:inherit;margin:4px;padding:8px 16px;border:0;background:var(--surface);color:var(--ink)}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>短片と白黒の芯を3段階で比較</h1><p>入力は現行採用描画の固定コピーです。生成画像の画素は使いません。</p><select id="camera" aria-label="カメラ"></select><select id="profile" aria-label="強さ"></select><button id="toggle">追加加工を切る</button><p id="status"></p></div><div class="wide"><img id="image" alt="追加加工の強さ比較"></div><script>const images=IMAGES,available=AVAILABLE;let on=true;const camera=document.getElementById('camera'),profile=document.getElementById('profile'),button=document.getElementById('toggle');for(const c of Object.keys(available)){const o=document.createElement('option');o.value=o.textContent=c;camera.append(o)}function profiles(){profile.replaceChildren();for(const p of available[camera.value]){const o=document.createElement('option');o.value=p;o.textContent={soft:'弱め',medium:'中間',strong:'強め'}[p];profile.append(o)}show()}function show(){document.getElementById('image').src=images[camera.value+'-'+(on?profile.value:'base')];document.getElementById('status').textContent=camera.value+' ／ '+(on?profile.value:'追加前');button.textContent=on?'追加加工を切る':'追加加工を入れる';button.setAttribute('aria-pressed',String(on))}camera.onchange=profiles;profile.onchange=show;button.onclick=()=>{on=!on;show()};profiles();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)).replace('AVAILABLE',json.dumps(available)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--profile',choices=[*PROFILES,'all'],default='all');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);inputs=json.loads((SOURCE/'input-and-processing-manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));cameras=list('ABC') if args.camera=='all' else [args.camera];profiles=list(PROFILES) if args.profile=='all' else [args.profile]
    manifest={'round':12,'profiles':PROFILES,'cameras_share_parameters':True,'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round09.py',HERE/'offline-round10.py',HERE/'offline-round11.py']},'lookup_sha256':sha(r11.LOOKUP),'source_input_manifest_sha256':sha(SOURCE/'input-and-processing-manifest.json'),'inputs':{},'outputs':{},'generated_rgb_used':False}
    if (out/'manifest.json').exists():
        old=json.loads((out/'manifest.json').read_text(encoding='utf-8'));assert old['profiles']==PROFILES;manifest['inputs']=old['inputs'];manifest['outputs']=old['outputs']
    for camera in cameras:
        source=SOURCE/f'base-{camera}.png';expected=inputs['inputs'][camera]['copy_sha256'];assert sha(source)==expected;base=out/f'base-{camera}.png'
        if base.exists():assert sha(base)==expected
        else:shutil.copyfile(source,base)
        src=np.array(Image.open(base).convert('RGBA'));visible=src[:,:,3]>0;l=src[:,:,:3].astype(float)@r9.WEIGHTS/255;optical=r9.optical(l,'A');clean,pre,std,energy,flatWeight=r10.prepare(optical);baseline,_=r10.faults(pre,energy,lookup,r10.PARAMS['B'],visible);baseline_rgba=r11.rgba(src,baseline);assert np.array_equal(baseline_rgba,np.array(Image.open(SOURCE/f'{camera}-10B.png'))),'10B baseline regeneration mismatch';Image.fromarray(baseline_rgba).save(out/f'{camera}-baseline-10B.png');shutil.copyfile(base,out/f'{camera}-off.png');assert sha(out/f'{camera}-off.png')==expected
        manifest['inputs'][camera]={'file':base.name,'sha256':expected,'off_byte_exact':True,'baseline_10B_exact':True,'base_roi':roi_stats(Image.fromarray(src)),'baseline_roi':roi_stats(Image.fromarray(baseline_rgba))}
        for name in profiles:
            p=PROFILES[name];core,gate,dy=line_core(clean,std,p);signal=pre+(core-pre)*gate*p['line_mix']+dy*gate*p['detail_gain'];target,events=faults(signal,energy,lookup,visible,p);again,_=faults(signal,energy,lookup,visible,p);assert np.array_equal(target,again);target,black=dark_variation(target,l,p,visible);again_black,_=dark_variation(again,l,p,visible);assert np.array_equal(target,again_black);a=r11.rgba(src,target);assert np.array_equal(a[:,:,3],src[:,:,3]) and np.array_equal(a[~visible],src[~visible]);im=Image.fromarray(a);file=f'{camera}-{name}.png';im.save(out/file);r11.black(im,out/f'{camera}-{name}-on-black.png')
            for label,roi in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'flat':(180,250,500,500),'black':(790,420,900,560),'black-interior':(820,450,880,530)}.items():labelled_pair(Image.fromarray(src),im,roi,out/f'{camera}-{name}-comparison-{label}.png',file,base.name)
            manifest['outputs'][f'{camera}-{name}']={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_rgba_exact':True,'fault_repeat_exact':True,'black_variation_repeat_exact':True,'events':events,'black_variation':black,'roi':roi_stats(im)};print(json.dumps({f'{camera}-{name}':manifest['outputs'][f'{camera}-{name}']},indent=2),flush=True)
        assert sha(source)==expected
    preview(out,manifest);(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
