"""Round15: thicker short bands with opaque white/black cores;14 statistics/border rules fixed."""
from pathlib import Path
import sys,argparse,importlib.util,json,shutil,base64
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r14',HERE/'offline-round14.py');r14=importlib.util.module_from_spec(s);s.loader.exec_module(r14);r9=r14.r9;r11=r14.r11;SOURCE=r14.SOURCE;PREVIOUS=r14.DEFAULT;DEFAULT=PREVIOUS.parent/'round-15'
PROFILES={'soft':{'groups':3000,'height_probabilities':[.40,.50,.10,0],'core_mix':.65,'core_opacity':.94},'medium':{'groups':3600,'height_probabilities':[.32,.50,.17,.01],'core_mix':.85,'core_opacity':.985},'strong':{'groups':4000,'height_probabilities':[.25,.50,.23,.02],'core_mix':1.0,'core_opacity':1.0}}

def process(stage,f,lookup,noise,h_rms,p):
    (smooth,gx,span,peaks,low,high,distance,visible),energy,ew,tw=f;h,w=stage.shape;rng=np.random.default_rng(141421);maximum=max(v['groups'] for v in PROFILES.values());edge_anchors=rng.choice(h*w,size=maximum,p=ew.ravel());texture_anchors=rng.choice(h*w,size=maximum,p=tw.ravel());types=rng.random(maximum);runs=[v for row in lookup['runs_threshold_008'] for v in row];signed_detail=smooth-cv2.GaussianBlur(smooth,(0,0),1.2)
    result=stage.copy();paint=np.zeros(stage.shape,bool);band=np.zeros(stage.shape,bool);positive=np.zeros(stage.shape,bool);negative=np.zeros(stage.shape,bool);strands=longs=internal=0;lengths=[];heights=[]
    for group in range(p['groups']):
        is_internal=types[group]<.70;anchor=texture_anchors[group] if is_internal else edge_anchors[group];cy,cx=divmod(int(anchor),w);contrast=float(np.clip((high[cy,cx]-low[cy,cx])/.25,0,1));polarity=1 if gx[cy,cx]>0 else -1
        for child in range(int(rng.integers(5,13))):
            run=runs[int(rng.integers(len(runs)))];u=rng.random();length=max(1,min(5,run['length'])) if u<.85 else max(6,min(12,run['length'])) if u<.997 else int(rng.integers(16,33));height=int(rng.choice([1,2,3,4],p=p['height_probabilities']));bright=bool(signed_detail[cy,cx]>=0) if is_internal else bool(rng.random()<.5);direction=(1 if rng.random()<.5 else -1) if is_internal else (-polarity if bright else polarity)
            local=float(high[cy,cx] if bright else low[cy,cx]);core_value=float(rng.uniform(.78,.84) if bright else rng.uniform(.012,.018));value=local+(core_value-local)*p['core_mix'];x0=cx+int(rng.integers(-2,3));y0=cy+int(rng.integers(-4,5));opacity=p['core_opacity']*rng.uniform(.995,1)
            vertical=[1] if height==1 else [1,1] if height==2 else [.8,1,.8] if height==3 else [.65,1,1,.65];vertical=[(-1,.08),*list(enumerate(vertical)),(height,.08)]
            for k in range(length):
                x=x0+direction*k
                if x<0 or x>=w:continue
                position=(k+.5)/length;taper=1 if abs(position-.5)<=.30 else float(np.sin(np.pi*position)**1.5);center_x=taper>=.99
                for offset,alpha_y in vertical:
                    y=y0+offset
                    if y<0 or y>=h or not visible[y,x]:continue
                    alpha=opacity*taper*alpha_y
                    if not(center_x and alpha_y>=.99):alpha*=.4+.6*contrast
                    result[y,x]+=(value-result[y,x])*alpha;paint[y,x]=True;band[y,x]=True
                    if center_x and alpha_y>=.99:
                        if bright:positive[y,x]=True
                        else:negative[y,x]=True
            strands+=1;longs+=int(length>=16);internal+=int(is_internal);lengths.append(length);heights.append(height)
    # Keep14-strong's phase/statistics and dark/white gating exactly.
    mid=np.clip((stage-.05)/.12,0,1)*np.clip((.82-stage)/.14,0,1)*visible;near=np.exp(-distance/4.5)*(distance<14)*np.clip(span/.2,0,1);weight=mid*(h_rms*3+near*.002);weighted_noise=noise-float(np.sum(noise*weight)/max(1e-12,weight.sum()));result+=weighted_noise*weight;paint|=weight>0
    return np.clip(result,.001,.85),paint,band,positive,negative,{'bands':strands,'internal_bands':internal,'rare_tails':longs,'height_bins':{str(k):heights.count(k) for k in [1,2,3,4]},'length_bins':{'1..5':int(np.sum(np.array(lengths)<=5)),'6..12':int(np.sum((np.array(lengths)>=6)&(np.array(lengths)<=12))),'16..32':longs}}

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={};available={}
    for camera in m['inputs']:
        for key,file in [('base',f'base-{camera}.png'),('baseline',f'{camera}-baseline-14strong.png')]:images[f'{camera}-{key}']='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
        available[camera]=[]
    for key,v in m['outputs'].items():
        p=out/v['file'];assert sha(p)==v['sha256'];images[key]='data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode();camera,profile=key.split('-');available[camera].append(profile)
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>短い帯の芯を比較</title><style>CSS
.wide{max-width:72rem;padding:16px}img{display:block;width:100%;height:auto;background:var(--surface)}button,select{font:inherit;margin:4px;padding:8px 16px;border:0;background:var(--surface);color:var(--ink)}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>短い帯の白黒の芯を比較</h1><p>14版の平坦面と微小PSDを維持し、短片の厚みと芯だけを変えた実フィルタ出力です。</p><select id="camera" aria-label="カメラ"></select><select id="profile" aria-label="厚みと芯の強さ"></select><select id="mode" aria-label="比較対象"><option value="candidate">15版目</option><option value="baseline">14版目の強め</option><option value="base">追加加工前</option></select><p id="status"></p></div><div class="wide"><img id="image" alt="短い帯の厚みと芯を比較"></div><script>const images=IMAGES,available=AVAILABLE;const c=document.getElementById('camera'),p=document.getElementById('profile'),mode=document.getElementById('mode');for(const camera of Object.keys(available)){const o=document.createElement('option');o.value=o.textContent=camera;c.append(o)}function profiles(){p.replaceChildren();for(const profile of available[c.value]){const o=document.createElement('option');o.value=profile;o.textContent={soft:'弱め',medium:'中間',strong:'強め'}[profile];p.append(o)}show()}function show(){const k=mode.value==='candidate'?p.value:mode.value;document.getElementById('image').src=images[c.value+'-'+k];document.getElementById('status').textContent=c.value+' ／ '+k}c.onchange=profiles;p.onchange=mode.onchange=show;profiles();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)).replace('AVAILABLE',json.dumps(available)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--profile',choices=[*PROFILES,'all'],default='all');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);source_m=json.loads((SOURCE/'manifest.json').read_text(encoding='utf-8'));previous=json.loads((PREVIOUS/'manifest.json').read_text(encoding='utf-8'));stats_path=PREVIOUS/'reference-residual-statistics.json';stats=json.loads(stats_path.read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));cameras=list('ABC') if args.camera=='all' else [args.camera];profiles=list(PROFILES) if args.profile=='all' else [args.profile]
    manifest={'round':15,'profiles':PROFILES,'inputs':{},'outputs':{},'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round14.py',HERE/'offline-round09.py']},'reference_statistics_sha256':sha(stats_path),'noise_settings':'14strong identical phase1618033 / measured horizontal RMS*3 / near.002','generated_rgb_used':False}
    if (out/'manifest.json').exists():
        old=json.loads((out/'manifest.json').read_text(encoding='utf-8'));assert old['profiles']==PROFILES;manifest['inputs']=old['inputs'];manifest['outputs']=old['outputs']
    for camera in cameras:
        base=SOURCE/f'base-{camera}.png';baseline12=SOURCE/f'{camera}-strong.png';baseline14=PREVIOUS/f'{camera}-strong.png';assert sha(base)==source_m['inputs'][camera]['sha256'];assert sha(baseline12)==source_m['outputs'][f'{camera}-strong']['sha256'];assert sha(baseline14)==previous['outputs'][f'{camera}-strong']['sha256'];raw=np.array(Image.open(base).convert('RGBA'));stage_rgba=np.array(Image.open(baseline12).convert('RGBA'));baseline=np.array(Image.open(baseline14).convert('RGBA'));stage=stage_rgba[:,:,:3].astype(float)@r9.WEIGHTS/255
        for src,dest in [(base,out/f'base-{camera}.png'),(baseline14,out/f'{camera}-baseline-14strong.png')]:
            if dest.exists():assert sha(dest)==sha(src)
            else:shutil.copyfile(src,dest)
        shutil.copyfile(base,out/f'{camera}-off.png');assert sha(out/f'{camera}-off.png')==sha(base);filled=r14.extend_alpha(raw);f=r14.fields(filled,stage);noise,h_rms=r14.synthesize(stage.shape,stats);d=cv2.distanceTransform((raw[:,:,3]>0).astype(np.uint8),cv2.DIST_L2,5);u=np.clip((d-2)/18,0,1);fade=u*u*(3-2*u);boundary=(raw[:,:,3]>0)&(d<=2);manifest['inputs'][camera]={'base_sha256':sha(base),'baseline12_sha256':sha(baseline12),'baseline14_sha256':sha(baseline14),'off_byte_exact':True,'boundary_fade_px':[2,20]};baseline_l=baseline[:,:,:3].astype(float)@r9.WEIGHTS/255
        for name in profiles:
            target,paint,band,positive,negative,events=process(stage,f,lookup,noise,h_rms,PROFILES[name]);again,*_=process(stage,f,lookup,noise,h_rms,PROFILES[name]);assert np.array_equal(target,again);candidate=stage_rgba.copy();candidate[:,:,:3]=r9.gray8(stage_rgba[:,:,:3]/255+(target-stage)[:,:,None]);candidate[~paint]=stage_rgba[~paint];a=raw.copy();a[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(candidate[:,:,:3].astype(float)-raw[:,:,:3])/255*fade[:,:,None]);assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[raw[:,:,3]==0],raw[raw[:,:,3]==0]) and np.array_equal(a[boundary],raw[boundary]);assert not np.array_equal(a,baseline),'must be a new actual output';im=Image.fromarray(a);file=f'{camera}-{name}.png';im.save(out/file);r11.black(im,out/f'{camera}-{name}-on-black.png')
            for label,roi in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(650,280,1000,630),'flat':(180,250,500,500),'black':(820,450,880,530)}.items():labelled_pair(Image.fromarray(baseline),im,roi,out/f'{camera}-{name}-comparison-{label}.png',file,f'{camera}-baseline-14strong.png')
            labelled_pair(Image.fromarray(raw),im,(75,250,160,650),out/f'{camera}-{name}-comparison-border.png',file,f'base-{camera}.png');l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;eligible=band&(fade>.99);floor=(slice(740,900),slice(680,1100));flat=(slice(250,500),slice(180,500));black=(slice(450,530),slice(820,880));metrics={'white_fraction_in_bands':float((l[eligible]>=.75).mean()),'baseline14_white_fraction_same_band_mask':float((baseline_l[eligible]>=.75).mean()),'black_fraction_in_bands':float((l[eligible]<=.03).mean()),'white_positive_centers':float((l[positive]>=.75).mean()) if positive.any() else 0,'floor_white_fraction':float((l[floor]>=.75).mean()),'baseline14_floor_white_fraction':float((baseline_l[floor]>=.75).mean())}
            manifest['outputs'][f'{camera}-{name}']={'file':file,'sha256':sha(out/file),'different_from14':True,'alpha_exact':True,'hidden_exact':True,'repeat_exact':True,'boundary_2px_exact':True,'boundary_pixels':int(boundary.sum()),'flat_ROI_exact_to14':bool(np.array_equal(a[flat],baseline[flat])),'black_mean_delta_from14':float((l[black]-baseline_l[black]).mean()),'core_metrics':metrics,'events':events};print(json.dumps({f'{camera}-{name}':manifest['outputs'][f'{camera}-{name}']},indent=2),flush=True)
    preview(out,manifest);(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
