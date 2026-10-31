"""Decompose12/14: fewer bursty fine abrasions vs stronger input photo-tone organization."""
from pathlib import Path
import sys,argparse,importlib.util,json,shutil,base64
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
def module(name,file):
    s=importlib.util.spec_from_file_location(name,HERE/file);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
r14=module('r14','offline-round14.py');r12=module('r12','offline-round12.py');r9=r14.r9;r11=r14.r11;r10=r12.r10;DEFAULT=r14.DEFAULT.parent/'round-18'

def sparse_fields(f):
    core,energy,ew,tw=f;h,w=energy.shape;rng=np.random.default_rng(180181);coarse=rng.normal(size=(max(4,h//55),max(4,w//85))).astype(np.float32);field=cv2.resize(coarse,(w,h),interpolation=cv2.INTER_CUBIC);field=cv2.GaussianBlur(field,(0,0),15,8);field=(field-field.mean())/max(1e-8,float(field.std()));burst=np.clip((field+.25)/1.05,0,1)**2;ew=ew*burst;tw=tw*burst;assert ew.sum()>0 and tw.sum()>0
    return (core,energy,ew/ew.sum(),tw/tw.sum()),burst

def sparse_abrasion(stage,f,lookup,noise,h_rms):
    (smooth,gx,span,peaks,low,high,distance,visible),energy,ew,tw=f;h,w=stage.shape;rng=np.random.default_rng(141421);edge=rng.choice(h*w,2600,p=ew.ravel());texture=rng.choice(h*w,2600,p=tw.ravel());types=rng.random(2600);runs=[v for row in lookup['runs_threshold_008'] for v in row];result=stage.copy();paint=np.zeros(stage.shape,bool);events=skipped=0;heights={1:0,2:0};lengths=[]
    for group in range(2600):
        interior=types[group]<.70;cy,cx=divmod(int(texture[group] if interior else edge[group]),w);contrast=float(np.clip((high[cy,cx]-low[cy,cx])/.25,0,1));polarity=1 if gx[cy,cx]>0 else -1
        for child in range(int(rng.integers(5,13))):
            run=runs[int(rng.integers(len(runs)))];u=rng.random();length=max(1,min(5,run['length'])) if u<.85 else max(6,min(12,run['length'])) if u<.997 else int(rng.integers(16,33));bright=rng.random()<.5;value=float(high[cy,cx] if bright else low[cy,cx]);height=2 if contrast>.95 and rng.random()<.18 else 1;direction=(1 if rng.random()<.5 else -1) if interior else (-polarity if bright else polarity);x0=cx+int(rng.integers(-2,3));y0=cy+int(rng.integers(-4,5));opacity=.65*contrast*rng.uniform(.5,1)
            if .03<value<.25:skipped+=1;continue
            for k in range(length):
                x=x0+direction*k
                if not 0<=x<w:continue
                taper=1 if 1<=k<length-1 else .75
                for row in range(height):
                    y=y0+row
                    if 0<=y<h and visible[y,x]:result[y,x]+=(value-result[y,x])*opacity*taper;paint[y,x]=True
            events+=1;heights[height]+=1;lengths.append(length)
    mid=np.clip((stage-.05)/.12,0,1)*np.clip((.82-stage)/.14,0,1)*visible;near=np.exp(-distance/4.5)*(distance<14)*np.clip(span/.2,0,1);weight=mid*(h_rms*3+near*.002);centered=noise-float(np.sum(noise*weight)/max(1e-12,weight.sum()));result+=centered*weight;paint|=weight>0
    return np.clip(result,.001,.85),paint,{'groups':2600,'events':events,'dark_grey_fragments_suppressed':skipped,'heights':heights,'length_quantiles':np.quantile(lengths,[0,.5,.9,1]).tolist() if lengths else [],'burst_gate_seed':180181}

def photo_stage(raw,lookup):
    rgb=r14.extend_alpha(raw)[:,:,:3].astype(float)/255;l=rgb@r9.WEIGHTS;optical=r9.optical(l,'A');bilateral=cv2.bilateralFilter(optical.astype(np.float32),11,.065,5).astype(float)
    mean=cv2.GaussianBlur(bilateral,(0,0),7);std=np.sqrt(np.maximum(0,cv2.GaussianBlur(bilateral*bilateral,(0,0),7)-mean*mean));structure=np.clip((std-.012)/.05,0,1);enhanced=bilateral+(bilateral-mean)*(.70*structure)
    # Continuous input-driven mass curve, no uniform white/black block painting.
    clean,pre,s,std_energy,flat=r10.prepare(enhanced);pre=np.interp(np.clip(pre,0,1),[0,.025,.09,.25,.45,.65,.85,1],[.010,.012,.038,.18,.47,.77,.85,.85]);core,gate,dy=r12.line_core(clean,s,r12.PROFILES['strong']);signal=pre+(core-pre)*gate*.58+dy*gate*.60;visible=raw[:,:,3]>0
    target,faults=r12.faults(signal,std_energy,lookup,visible,r12.PROFILES['strong']);target,dark=r12.dark_variation(target,l,r12.PROFILES['strong'],visible)
    return target,{'bilateral_diameter':11,'bilateral_sigma_colour':.065,'bilateral_sigma_space':5,'local_contrast_gain':.70,'mass_curve_input':[0,.025,.09,.25,.45,.65,.85,1],'mass_curve_output':[.010,.012,.038,.18,.47,.77,.85,.85],'line_core_mix':.58,'faults':faults,'dark':dark}

def rgba(raw,stage_rgb,stage,target,paint):
    candidate=stage_rgb.copy();candidate[:,:,:3]=r9.gray8(stage_rgb[:,:,:3]/255+(target-stage)[:,:,None]);candidate[~paint]=stage_rgb[~paint];d=cv2.distanceTransform((raw[:,:,3]>0).astype(np.uint8),cv2.DIST_L2,5);u=np.clip((d-2)/18,0,1);fade=u*u*(3-2*u);out=raw.copy();out[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(candidate[:,:,:3].astype(float)-raw[:,:,:3])/255*fade[:,:,None]);boundary=(raw[:,:,3]>0)&(d<=2);assert np.array_equal(out[:,:,3],raw[:,:,3]) and np.array_equal(out[raw[:,:,3]==0],raw[raw[:,:,3]==0]) and np.array_equal(out[boundary],raw[boundary]);return out,int(boundary.sum())

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={key:'data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode() for key,file in [('base','base.png'),('14','baseline14.png'),('12','baseline12.png'),*[(k,v['file']) for k,v in m['outputs'].items()]]};options=''.join(f'<option value="{k}">{v}</option>' for k,v in [('A-sparse','A 擦れの疎密を変える'),('B-photo','B 写真の細部を整理'),('C-combined','C 両方を適用'),('14','14版目 強め'),('12','12版目 強め'),('base','加工前')] if k in images)
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>擦れの疎密と写真の細部を比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>擦れの疎密と写真の細部を比較</h1><p>同じ凍結入力から再構成した独立フィルタの出力です。</p><select id="choice" aria-label="比較対象">OPTIONS</select></div><div class="image"><img id="image" alt="加工の比較"></div><script>const images=IMAGES,s=document.getElementById('choice');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--input',type=Path);ap.add_argument('--combine',action='store_true');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));stats_path=r14.DEFAULT/'reference-residual-statistics.json';stats=json.loads(stats_path.read_text(encoding='utf-8'))
    if args.input:base=p12=p14=args.input
    else:
        base=r12.DEFAULT/'base-B.png';p12=r12.DEFAULT/'B-strong.png';p14=r14.DEFAULT/'B-strong.png';sm=json.loads((r12.DEFAULT/'manifest.json').read_text(encoding='utf-8'));pm=json.loads((r14.DEFAULT/'manifest.json').read_text(encoding='utf-8'));assert sha(base)==sm['inputs']['B']['sha256'];assert sha(p12)==sm['outputs']['B-strong']['sha256'];assert sha(p14)==pm['outputs']['B-strong']['sha256']
    raw=np.array(Image.open(base).convert('RGBA'));stage_rgb=np.array(Image.open(p12).convert('RGBA'));baseline=np.array(Image.open(p14).convert('RGBA'));stage=stage_rgb[:,:,:3].astype(float)@r9.WEIGHTS/255;mask=raw[:,:,3]>0;noise,h_rms=r14.synthesize(stage.shape,stats);f=r14.fields(r14.extend_alpha(raw),stage);sparse,burst=sparse_fields(f);photo,photo_settings=photo_stage(raw,lookup);again_photo,_=photo_stage(raw,lookup);assert np.array_equal(photo,again_photo);photo_rgb=raw.copy();orig_l=raw[:,:,:3].astype(float)@r9.WEIGHTS/255;photo_rgb[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(photo-orig_l)[:,:,None]);pf=r14.fields(r14.extend_alpha(raw),photo)
    for src,file in [(base,'base.png'),(base,'OFF.png'),(p12,'baseline12.png'),(p14,'baseline14.png')]:
        dest=out/file
        if dest.exists():assert sha(dest)==sha(src)
        else:shutil.copyfile(src,dest)
    m={'round':18,'input':{'raw_path':str(base.resolve()),'raw_sha256':sha(base),'baseline12_sha256':sha(p12),'baseline14_sha256':sha(p14),'OFF_byte_exact':sha(out/'OFF.png')==sha(base),'arbitrary_input':bool(args.input)},'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round14.py',HERE/'offline-round12.py']},'reference_statistics_sha256':sha(stats_path),'lookup_sha256':sha(r11.LOOKUP),'photo_stage_repeat_exact':True,'generated_RGB_used':False,'outputs':{}}
    configurations=[('A-sparse',stage_rgb,stage,sparse,sparse_abrasion),('B-photo',photo_rgb,photo,pf,None)]
    if args.combine:configurations.append(('C-combined',photo_rgb,photo,sparse_fields(pf)[0],sparse_abrasion))
    old_l=baseline[:,:,:3].astype(float)@r9.WEIGHTS/255
    for key,srgb,signal,ff,fn in configurations:
        def calculate():return fn(signal,ff,lookup,noise,h_rms) if fn else r14.process(signal,ff,lookup,noise,h_rms,r14.PROFILES['strong'])
        target,paint,events=calculate();repeat,rpaint,_=calculate();assert np.array_equal(target,repeat) and np.array_equal(paint,rpaint);a,count=rgba(raw,srgb,signal,target,paint);file=key+'.png';im=Image.fromarray(a);im.save(out/file);r11.black(im,out/(key+'-on-black.png'));l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;delta=l-old_l;floor=l[740:900,680:1100];wall=l[250:500,180:500]
        for label,roi in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530)}.items():
            if roi[2]<=a.shape[1] and roi[3]<=a.shape[0]:labelled_pair(Image.fromarray(baseline),im,roi,out/(key+'-comparison-'+label+'.png'),file,'baseline14.png')
        if a.shape[0]>=650 and a.shape[1]>=160:labelled_pair(Image.fromarray(raw),im,(75,250,160,650),out/(key+'-comparison-border.png'),file,'base.png')
        m['outputs'][key]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_RGBA_exact':count,'repeat_exact':True,'events':events,'photo_organization':photo_settings if key!='A-sparse' else None,'visible_changed_fraction_from14':float(np.any(a[:,:,:3]!=baseline[:,:,:3],axis=2)[mask].mean()),'luma_delta_RMS_from14':float(np.sqrt(np.mean(delta[mask]**2))),'floor_white_fraction_L075':float((floor>=.75).mean()) if floor.size else None,'floor_gray_fraction_L003to04':float(((floor>.03)&(floor<.4)).mean()) if floor.size else None,'wall_mean':float(wall.mean()) if wall.size else None};print(key,json.dumps(m['outputs'][key]),flush=True)
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
