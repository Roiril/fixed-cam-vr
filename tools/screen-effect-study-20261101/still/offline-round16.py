"""Round16: reconstruct14strong, then selected positive input peaks become short 1px cores."""
from pathlib import Path
import sys,argparse,importlib.util,json,shutil,base64
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r14',HERE/'offline-round14.py');r14=importlib.util.module_from_spec(s);s.loader.exec_module(r14);r9=r14.r9;r11=r14.r11;SOURCE=r14.SOURCE;PREVIOUS=r14.DEFAULT;DEFAULT=PREVIOUS.parent/'round-16'
PROFILES={'soft':{'burst_share':.20,'core_luma':.56,'core_opacity':.78},'medium':{'burst_share':.28,'core_luma':.64,'core_opacity':.87},'strong':{'burst_share':.34,'core_luma':.72,'core_opacity':.94}}

def process(baseline,f,lookup,p):
    (smooth,gx,span,peaks,low,high,distance,visible),energy,ew,tw=f;h,w=baseline.shape
    d1=smooth-cv2.GaussianBlur(smooth,(0,0),1.2);d2=smooth-cv2.GaussianBlur(smooth,(0,0),2.5)
    positive=(d1>.010)&(d2>.012)&(smooth>=cv2.dilate(smooth,np.ones((3,1),np.uint8))-.004)&visible
    weights=positive*(.75*tw+.25*ew);assert weights.sum()>0;weights/=weights.sum()
    rng=np.random.default_rng(60017);anchors=rng.choice(h*w,2400,p=weights.ravel());active=rng.random(2400);runs=[v for row in lookup['runs_threshold_008'] for v in row]
    result=baseline.copy();cores=np.zeros(baseline.shape,bool);tails=np.zeros(baseline.shape,bool);counts={1:0,2:0};selected=0;rare_white=0
    for index,anchor in enumerate(anchors):
        if active[index]>=p['burst_share']:continue
        cy,cx=divmod(int(anchor),w);ys,xs=np.where(positive[max(0,cy-3):min(h,cy+4),max(0,cx-5):min(w,cx+6)])
        if not len(ys):continue
        selected+=1
        for child in range(int(rng.integers(4,9))):
            chosen=int(rng.integers(len(ys)));y=int(ys[chosen]+max(0,cy-3));x0=int(xs[chosen]+max(0,cx-5));run=runs[int(rng.integers(len(runs)))];length=max(2,min(4,run['length']));direction=1 if rng.random()<.5 else -1
            # Only the very strong world boundary may emit a second row. Interior detail remains 1px.
            height=2 if peaks[y,x0] and span[y,x0]>.30 and rng.random()<.18 else 1;counts[height]+=1
            rare=bool(rng.random()<.01);value=.78 if rare else p['core_luma']+rng.uniform(-.018,.018);rare_white+=int(rare)
            for k in range(length):
                x=x0+direction*k
                if not(0<=x<w):continue
                for row in range(height):
                    yy=y+row
                    if yy>=h or not visible[yy,x]:continue
                    # A stable middle, just the endpoint is partially covered; no needle-shaped taper.
                    alpha=p['core_opacity']*(.70 if k in (0,length-1) and length>2 else 1)
                    result[yy,x]+=(value-result[yy,x])*alpha;cores[yy,x]=True
            # Local low-opacity grey tail. It never extends the white core or creates new long events.
            tail=int(rng.integers(2,7));gray=float(np.clip(high[y,x0],.04,.62))
            for k in range(tail):
                x=x0+direction*(length+k)
                if 0<=x<w and visible[y,x]:
                    alpha=.10*(1-k/tail);result[y,x]+=(gray-result[y,x])*alpha;tails[y,x]=True
    return np.clip(result,.001,.85),cores,tails,{'eligible_peak_pixels':int(positive.sum()),'burst_pool':2400,'selected_bursts':selected,'core_events':sum(counts.values()),'heights':counts,'rare_078_core_events':rare_white,'core_pixels':int(cores.sum()),'tail_pixels':int(tails.sum())}

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for key,file in [('base','base-B.png'),('baseline','B-baseline-14strong.png'),*[(k.split('-')[1],v['file']) for k,v in m['outputs'].items()]]:images[key]='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>短い擦れの明るい芯を比較</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}select{font:inherit;margin:4px;padding:8px;color:var(--ink);background:var(--surface)}select:focus-visible{outline:2px solid var(--link)}.image{max-width:1280px}</style><div class="wrap"><h1>短い擦れの明るい芯を比較</h1><p>14版目の擦れを基準に、入力の強い明るい細部の一部だけを変えています。</p><select id="choice" aria-label="比較する画像"><option value="soft">16版目 弱め</option><option value="medium">16版目 中間</option><option value="strong">16版目 強め</option><option value="baseline">14版目 強め</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="短い擦れの明るい芯"></div><script>const images=IMAGES;const s=document.getElementById('choice');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);sm=json.loads((SOURCE/'manifest.json').read_text(encoding='utf-8'));pm=json.loads((PREVIOUS/'manifest.json').read_text(encoding='utf-8'));stats_path=PREVIOUS/'reference-residual-statistics.json';stats=json.loads(stats_path.read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'))
    base=SOURCE/'base-B.png';stage_path=SOURCE/'B-strong.png';baseline_path=PREVIOUS/'B-strong.png';assert sha(base)==sm['inputs']['B']['sha256'];assert sha(stage_path)==sm['outputs']['B-strong']['sha256'];assert sha(baseline_path)==pm['outputs']['B-strong']['sha256'];raw=np.array(Image.open(base).convert('RGBA'));stage_rgba=np.array(Image.open(stage_path).convert('RGBA'));baseline=np.array(Image.open(baseline_path).convert('RGBA'));stage=stage_rgba[:,:,:3].astype(float)@r9.WEIGHTS/255
    f=r14.fields(r14.extend_alpha(raw),stage);noise,h_rms=r14.synthesize(stage.shape,stats);target14,paint14,_=r14.process(stage,f,lookup,noise,h_rms,r14.PROFILES['strong']);d=cv2.distanceTransform((raw[:,:,3]>0).astype(np.uint8),cv2.DIST_L2,5);u=np.clip((d-2)/18,0,1);fade=u*u*(3-2*u);boundary=(raw[:,:,3]>0)&(d<=2)
    def rgba(target,paint):
        c=stage_rgba.copy();c[:,:,:3]=r9.gray8(stage_rgba[:,:,:3]/255+(target-stage)[:,:,None]);c[~paint]=stage_rgba[~paint];a=raw.copy();a[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(c[:,:,:3].astype(float)-raw[:,:,:3])/255*fade[:,:,None]);return a
    assert np.array_equal(rgba(target14,paint14),baseline),'14strong reconstruction must match actual frozen baseline'
    for src,name in [(base,'base-B.png'),(base,'B-off.png'),(baseline_path,'B-baseline-14strong.png')]:
        dest=out/name
        if dest.exists():assert sha(dest)==sha(src)
        else:shutil.copyfile(src,dest)
    m={'round':16,'profiles':PROFILES,'input_B':{'base_sha256':sha(base),'stage12_sha256':sha(stage_path),'baseline14_sha256':sha(baseline_path),'reconstructed14_RGBA_exact':True,'off_byte_exact':sha(out/'B-off.png')==sha(base)},'reference_statistics_sha256':sha(stats_path),'generated_RGB_used':False,'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round14.py',HERE/'offline-round09.py']},'outputs':{}}
    baseline_l=baseline[:,:,:3].astype(float)@r9.WEIGHTS/255;floor=(slice(740,900),slice(680,1100));flat=(slice(250,500),slice(180,500));black=(slice(450,530),slice(820,880))
    for name,p in PROFILES.items():
        target,cores,tails,events=process(target14,f,lookup,p);again,cores2,tails2,_=process(target14,f,lookup,p);assert np.array_equal(target,again) and np.array_equal(cores,cores2) and np.array_equal(tails,tails2);a=rgba(target,paint14|cores|tails);assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[raw[:,:,3]==0],raw[raw[:,:,3]==0]) and np.array_equal(a[boundary],raw[boundary]);assert not np.array_equal(a,baseline);im=Image.fromarray(a);file=f'B-{name}.png';im.save(out/file);r11.black(im,out/f'B-{name}-on-black.png')
        for label,roi in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(650,280,1000,630),'flat':(180,250,500,500),'black':(820,450,880,530)}.items():labelled_pair(Image.fromarray(baseline),im,roi,out/f'B-{name}-comparison-{label}.png',file,'B-baseline-14strong.png')
        labelled_pair(Image.fromarray(raw),im,(75,250,160,650),out/f'B-{name}-comparison-border.png',file,'base-B.png');l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;eligible=cores&(fade>.99)
        m['outputs'][f'B-{name}']={'file':file,'sha256':sha(out/file),'different_from14':True,'alpha_exact':True,'hidden_exact':True,'boundary_2px_exact':True,'boundary_pixels':int(boundary.sum()),'repeat_exact':True,'flat_RGBA_exact_to14':bool(np.array_equal(a[flat],baseline[flat])),'black_mean_delta_from14':float((l[black]-baseline_l[black]).mean()),'metrics':{'selected_core_Lge055_fraction':float((l[eligible]>=.55).mean()),'selected_core_Lge075_fraction':float((l[eligible]>=.75).mean()),'floor_Lge055_fraction':float((l[floor]>=.55).mean()),'baseline14_floor_Lge055_fraction':float((baseline_l[floor]>=.55).mean()),'floor_Lge075_fraction':float((l[floor]>=.75).mean()),'visible_changed_fraction_from14':float(np.any(a[:,:,:3]!=baseline[:,:,:3],axis=2)[raw[:,:,3]>0].mean())},'events':events};print(name,json.dumps(m['outputs'][f'B-{name}'],ensure_ascii=True),flush=True)
    assert len(set(v['sha256'] for v in m['outputs'].values()))==3;preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
