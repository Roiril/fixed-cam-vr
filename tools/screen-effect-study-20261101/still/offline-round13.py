"""Round13: tapered one-pixel strands over the frozen round12-strong filter stage."""
from pathlib import Path
import sys,argparse,base64,importlib.util,json,shutil
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
s=importlib.util.spec_from_file_location('r12',HERE/'offline-round12.py');r12=importlib.util.module_from_spec(s);s.loader.exec_module(r12);r9=r12.r9;r11=r12.r11
SOURCE=r12.DEFAULT;DEFAULT=SOURCE.parent/'round-13'
PROFILES={'soft':{'groups':1600,'opacity':.35,'abrasion':.003},'medium':{'groups':2800,'opacity':.50,'abrasion':.0045},'strong':{'groups':4200,'opacity':.65,'abrasion':.006}}

def fields(raw,stage):
    l=raw[:,:,:3].astype(float)@r9.WEIGHTS/255;optical=r9.optical(l,'A');smooth=cv2.GaussianBlur(optical,(3,3),.65);gx=cv2.Sobel(smooth,cv2.CV_64F,1,0,ksize=3)/8;absx=np.abs(gx);left=np.pad(absx[:,:-1],((0,0),(1,0)),mode='edge');right=np.pad(absx[:,1:],((0,0),(0,1)),mode='edge');span=cv2.dilate(smooth,np.ones((1,9),np.uint8))-cv2.erode(smooth,np.ones((1,9),np.uint8));visible=raw[:,:,3]>0;interior=cv2.erode(visible.astype(np.uint8),np.ones((19,19),np.uint8)).astype(bool)
    peaks=(absx>.012)&(absx>=left)&(absx>right)&(span>.08)&interior;low=cv2.erode(stage,np.ones((3,15),np.uint8));high=cv2.dilate(stage,np.ones((3,15),np.uint8));distance=cv2.distanceTransform((~peaks).astype(np.uint8),cv2.DIST_L2,3)
    return smooth,gx,span,peaks,low,high,distance,visible

def process(stage,f,lookup,p):
    smooth,gx,span,peaks,low,high,distance,visible=f;h,w=stage.shape;rng=np.random.default_rng(314159);count=lookup['row_count'];ys,xs=np.indices(stage.shape);j=np.array(lookup['jitter']);activity=np.abs(j-np.roll(j,1));activity=activity/(np.percentile(activity,90)+1e-9);phase=(ys+(xs*.17).astype(int))%count;burst=.55+.45*np.clip(activity[phase],0,1)
    weights=peaks*span*np.abs(gx)*burst;assert weights.sum()>0;weights/=weights.sum();anchors=rng.choice(h*w,size=max(v['groups'] for v in PROFILES.values()),replace=True,p=weights.ravel())[:p['groups']];runs=[v for row in lookup['runs_threshold_008'] for v in row];result=stage.copy();paint=np.zeros(stage.shape,bool);strands=longs=0;lengths=[]
    for anchor in anchors:
        cy,cx=divmod(int(anchor),w);contrast=float(np.clip((high[cy,cx]-low[cy,cx])/.30,0,1));polarity=1 if gx[cy,cx]>0 else -1
        for child in range(int(rng.integers(5,13))):
            run=runs[int(rng.integers(len(runs)))];kind=rng.random()
            length=max(1,min(4,run['length'])) if kind<.85 else int(rng.integers(5,12)) if kind<.95 else int(rng.integers(12,33)) if kind<.995 else int(rng.integers(33,41))
            bright=rng.random()<.5;direction=-polarity if bright else polarity;value=float(high[cy,cx] if bright else low[cy,cx]);x0=cx+int(rng.integers(-1,2));yf=cy+int(rng.integers(-4,5))+rng.uniform(-.2,.2);y0=int(np.floor(yf));fraction=yf-y0
            opacity=p['opacity']*contrast*rng.uniform(.45,1);opacity*=.6 if length>12 else 1
            for k in range(length):
                x=x0+direction*k
                if x<0 or x>=w:continue
                taper=float(np.sin(np.pi*(k+.5)/length)**.7)
                for y,vertical in [(y0,1-fraction),(y0+1,fraction)]:
                    if y<0 or y>=h or not visible[y,x]:continue
                    alpha=opacity*taper*vertical;result[y,x]+=(value-result[y,x])*alpha;paint[y,x]=True
            strands+=1;longs+=int(length>=12);lengths.append(length)
    # A faint horizontal abrasion is confined to the middle tones beside real peaks.
    noise=cv2.GaussianBlur(np.random.default_rng(271828).uniform(-1,1,stage.shape),(9,1),1.7);noise=np.tanh(noise*3);falloff=np.exp(-distance/4.5)*(distance<14);middle=np.clip((stage-.04)/.12,0,1)*np.clip((.78-stage)/.18,0,1);haze_weight=falloff*middle*np.clip(span/.2,0,1)*visible
    noise-=float(np.sum(noise*haze_weight)/max(1e-9,haze_weight.sum()));result+=noise*p['abrasion']*haze_weight;paint|=haze_weight>0
    return np.clip(result,.001,.85),paint,{'strands':strands,'long_strands':longs,'length_quantiles':np.quantile(lengths,[0,.5,.9,.99,1]).tolist(),'paint_area_fraction':float((paint&visible).sum()/visible.sum()),'geometry':'tapered x alpha, subpixel y splat over at most two rows, independent off-grid bursts'}

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={};available={}
    for camera in m['inputs']:
        for key,file in [('base',f'base-{camera}.png'),('baseline',f'{camera}-baseline-12strong.png')]:images[f'{camera}-{key}']='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
        available[camera]=[]
    for key,v in m['outputs'].items():
        p=out/v['file'];assert sha(p)==v['sha256'];images[key]='data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode();camera,profile=key.split('-');available[camera].append(profile)
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>薄い水平の糸と擦れ</title><style>CSS
.wide{max-width:72rem;padding:16px}img{display:block;width:100%;height:auto;background:var(--surface)}button,select{font:inherit;margin:4px;padding:8px 16px;border:0;background:var(--surface);color:var(--ink)}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>薄い水平の糸と擦れを比較</h1><p>12版の強めを固定し、輪郭の近くへ細い糸を重ねた実フィルタ出力です。</p><select id="camera" aria-label="カメラ"></select><select id="profile" aria-label="糸と擦れの量"></select><select id="mode" aria-label="比較対象"><option value="candidate">13版目</option><option value="baseline">12版目の強め</option><option value="base">追加加工前</option></select><p id="status"></p></div><div class="wide"><img id="image" alt="水平の糸と擦れの比較"></div><script>const images=IMAGES,available=AVAILABLE;const c=document.getElementById('camera'),p=document.getElementById('profile'),mode=document.getElementById('mode');for(const camera of Object.keys(available)){const o=document.createElement('option');o.value=o.textContent=camera;c.append(o)}function profiles(){p.replaceChildren();for(const profile of available[c.value]){const o=document.createElement('option');o.value=profile;o.textContent={soft:'弱め',medium:'中間',strong:'強め'}[profile];p.append(o)}show()}function show(){const k=mode.value==='candidate'?p.value:mode.value;document.getElementById('image').src=images[c.value+'-'+k];document.getElementById('status').textContent=c.value+' ／ '+k}c.onchange=profiles;p.onchange=mode.onchange=show;profiles();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)).replace('AVAILABLE',json.dumps(available)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--profile',choices=[*PROFILES,'all'],default='all');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);source_m=json.loads((SOURCE/'manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));cameras=list('ABC') if args.camera=='all' else [args.camera];profiles=list(PROFILES) if args.profile=='all' else [args.profile]
    manifest={'round':13,'profiles':PROFILES,'inputs':{},'outputs':{},'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round09.py',HERE/'offline-round12.py']},'source_12_manifest_sha256':sha(SOURCE/'manifest.json'),'lookup_sha256':sha(r11.LOOKUP),'generated_rgb_used':False}
    if (out/'manifest.json').exists():
        old=json.loads((out/'manifest.json').read_text(encoding='utf-8'));assert old['profiles']==PROFILES;manifest['inputs']=old['inputs'];manifest['outputs']=old['outputs']
    for camera in cameras:
        base=SOURCE/f'base-{camera}.png';stage_path=SOURCE/f'{camera}-strong.png';assert sha(base)==source_m['inputs'][camera]['sha256'];assert sha(stage_path)==source_m['outputs'][f'{camera}-strong']['sha256'];raw=np.array(Image.open(base).convert('RGBA'));stage_rgba=np.array(Image.open(stage_path).convert('RGBA'));stage=stage_rgba[:,:,:3].astype(float)@r9.WEIGHTS/255
        for src,dest in [(base,out/f'base-{camera}.png'),(stage_path,out/f'{camera}-baseline-12strong.png')]:
            if dest.exists():assert sha(dest)==sha(src)
            else:shutil.copyfile(src,dest)
        shutil.copyfile(base,out/f'{camera}-off.png');assert sha(out/f'{camera}-off.png')==sha(base);f=fields(raw,stage);manifest['inputs'][camera]={'base_sha256':sha(base),'baseline_12strong_sha256':sha(stage_path),'off_byte_exact':True}
        for name in profiles:
            target,paint,events=process(stage,f,lookup,PROFILES[name]);again,again_mask,_=process(stage,f,lookup,PROFILES[name]);assert np.array_equal(target,again) and np.array_equal(paint,again_mask);a=stage_rgba.copy();a[:,:,:3]=r9.gray8(stage_rgba[:,:,:3]/255+(target-stage)[:,:,None]);a[~paint]=stage_rgba[~paint];assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[raw[:,:,3]==0],raw[raw[:,:,3]==0]);im=Image.fromarray(a);file=f'{camera}-{name}.png';im.save(out/file);r11.black(im,out/f'{camera}-{name}-on-black.png')
            for label,roi in {'full':(0,0,1280,1030),'edge':(650,280,1000,630),'floor':(680,740,1100,900),'flat':(180,250,500,500),'black':(820,450,880,530)}.items():labelled_pair(Image.fromarray(stage_rgba),im,roi,out/f'{camera}-{name}-comparison-{label}.png',file,f'{camera}-baseline-12strong.png')
            wall=(slice(250,500),slice(180,500));black=(slice(450,530),slice(820,880));manifest['outputs'][f'{camera}-{name}']={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'outside_effect_exact':True,'repeat_exact':True,'flat_wall_roi_exact_to_12strong':bool(np.array_equal(a[wall],stage_rgba[wall])),'black_interior_roi_exact_to_12strong':bool(np.array_equal(a[black],stage_rgba[black])),'events':events};print(json.dumps({f'{camera}-{name}':manifest['outputs'][f'{camera}-{name}']},indent=2),flush=True)
        assert sha(base)==manifest['inputs'][camera]['base_sha256'] and sha(stage_path)==manifest['inputs'][camera]['baseline_12strong_sha256']
    preview(out,manifest);(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
