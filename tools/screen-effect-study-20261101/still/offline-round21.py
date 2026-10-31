"""Round21: coarser isotropic internal sample grids;14-style processing in working pixels."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys,math
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r20',HERE/'offline-round20.py');r20=importlib.util.module_from_spec(s);s.loader.exec_module(r20);r14=r20.r14;r12=r20.r12;r19=r20.r19;r9=r20.r9;r10=r20.r10;r11=r20.r11;SOURCE=r20.SOURCE;DEFAULT=SOURCE.parent/'round-21';PROFILES={'scale075':.75,'scale060':.60,'scale050':.50}

def area_grid(raw,scale):
    h,w=raw.shape[:2];ww=int(math.ceil(w*scale));wh=int(math.ceil(h*scale));cover_w=ww/scale;cover_h=wh/scale;padw=max(0,math.ceil(cover_w)-w);padh=max(0,math.ceil(cover_h)-h)
    # Float area box preserves the same pitch in X/Y even when1030*.75 is half a pixel.
    rgb=r19.extend_alpha(raw);rgb=cv2.copyMakeBorder(rgb,0,padh,0,padw,cv2.BORDER_REPLICATE);alpha=cv2.copyMakeBorder(raw[:,:,3],0,padh,0,padw,cv2.BORDER_REPLICATE)
    small_rgb=np.array(Image.fromarray(rgb).resize((ww,wh),Image.Resampling.BOX,box=(0,0,cover_w,cover_h)));small_alpha=np.array(Image.fromarray(alpha).resize((ww,wh),Image.Resampling.BOX,box=(0,0,cover_w,cover_h)));small=np.dstack([small_rgb,small_alpha]);return small,{'working_size':[ww,wh],'isotropic_source_pitch':1/scale,'source_area_box':[0,0,cover_w,cover_h],'edge_padding':[padw,padh]}

def faults(pre,energy,lookup,visible,count):
    rng=np.random.default_rng(104729);h,w=pre.shape;weights=np.maximum(0,energy-.009)**1.4;weights[~visible]=0;assert weights.sum()>0;weights/=weights.sum();centers=rng.choice(h*w,size=count,p=weights.ravel());runs=[v for row in lookup['runs_threshold_008'] for v in row];jitter=np.array(lookup['jitter']);result=pre.copy();events=longs=0;p=r12.PROFILES['strong']
    for center in centers:
        cy,cx=divmod(int(center),w)
        for child in range(int(rng.integers(2,5))):
            run=runs[int(rng.integers(len(runs)))];long=rng.random()<.07;length=int(rng.integers(20,p['tail_max']+1)) if long else max(3,min(20,int(round(run['length']*p['length_scale']))));height=int(rng.integers(1,4));x=int(np.clip(cx+rng.integers(-10,11),0,w-1));y=int(np.clip(cy+rng.integers(-4,5),0,h-1));x1=min(w,x+length);y1=min(h,y+height)
            if energy[y,x]<.009:continue
            displacement=int(np.clip(np.round(jitter[int(rng.integers(len(jitter)))]*.9),-14,14));displacement=displacement if abs(displacement)>=2 else run['sign']*max(3,min(10,length));dx=np.clip(np.arange(x,x1)+displacement,0,w-1);dy=np.clip(np.arange(y,y1)+(run['sign'] if rng.random()<.25 else 0),0,h-1);replacement=pre[np.ix_(dy,dx)];old=pre[y:y1,x:x1];lag=pre[np.ix_(dy,np.clip(dx-run['sign']*2,0,w-1))];new=replacement+(replacement-lag)*.25;new=np.where(visible[np.ix_(dy,dx)],new,old);result[y:y1,x:x1]=old+(new-old)*.95;events+=1;longs+=int(long)
    return np.clip(result,.001,.85),{'groups':count,'events':events,'long_events':longs}

def working_filter(raw,scale,lookup,stats):
    work,grid=area_grid(raw,scale);visible=work[:,:,3]>0;l=work[:,:,:3].astype(float)@r9.WEIGHTS/255;g12=max(1,int(np.floor(4200*scale*scale+.5)));g14=max(1,int(np.floor(5200*scale*scale+.5)))
    if np.ptp(l[visible])<1e-7:return work[:,:,:3]/255,work,{'grid':grid,'groups12':g12,'groups14':g14,'constant_passthrough':True}
    optical=r9.optical(l,'A');clean,pre,std,energy,_=r10.prepare(optical);p=r12.PROFILES['strong'];core,gate,dy=r12.line_core(clean,std,p);signal=pre+(core-pre)*gate*p['line_mix']+dy*gate*p['detail_gain'];target,fault_info=faults(signal,energy,lookup,visible,g12);target,dark=r12.dark_variation(target,l,p,visible);stage=r11.rgba(work,target);sl=stage[:,:,:3].astype(float)@r9.WEIGHTS/255;f=r14.fields(r14.extend_alpha(work),sl);noise,h_rms=r14.synthesize(sl.shape,stats);p14=dict(r14.PROFILES['strong'],groups=g14);target,paint,abrasion=r14.process(sl,f,lookup,noise,h_rms,p14);candidate=stage.copy();candidate[:,:,:3]=r9.gray8(stage[:,:,:3]/255+(target-sl)[:,:,None]);candidate[~paint]=stage[~paint]
    return candidate[:,:,:3].astype(float)/255,work,{'grid':grid,'groups12':g12,'groups14':g14,'constant_passthrough':False,'faults':fault_info,'abrasion':abrasion,'physical_pitch_multiplier':1/scale,'unchanged_working_parameters':{'short_lengths_px':[1,5],'medium_lengths_px':[6,12],'rare_lengths_px':[16,32],'height_distribution':[.72,.265,.015],'opacity':.65,'interior_share':.70,'nlm_h':5.5}}

def restore_full(raw,working_rgb,scale):
    h,w=raw.shape[:2];y,x=np.indices((h,w),dtype=np.float32);mx=(x+.5)*scale-.5;my=(y+.5)*scale-.5;full=cv2.remap(working_rgb.astype(np.float32),mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE);a,count=r19.protect(raw,full);return a,count

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for key,file in [('base',f'base-{c}.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+key]='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>処理する格子の粗さを比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>処理する格子の粗さを比較</h1><p>最終寸法と画角は同じまま、内部の線の幅と間隔を変えています。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="内部解像度"><option value="scale075">75%</option><option value="scale060">60%</option><option value="scale050">50%</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="内部の画素間隔を変えた比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);sm=json.loads((SOURCE/'input-and-processing-manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));stats_path=r14.DEFAULT/'reference-residual-statistics.json';stats=json.loads(stats_path.read_text(encoding='utf-8'));cameras=['custom'] if args.input else list('ABC') if args.camera=='all' else [args.camera]
    m={'round':21,'profiles':PROFILES,'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round14.py',HERE/'offline-round12.py']},'completed_prior_output_pixels_used':False,'generated_RGB_used':False,'low_frequency_gray_restoration_used':False,'same_parameters_for_all_cameras':True,'upsampling':'bilinear remap with matching isotropic pixel centres; no nearest','inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else SOURCE/f'base-{c}.png';expected=sha(src)
        if not args.input:assert expected==sm['inputs'][c]['copy_sha256']
        for file in [f'base-{c}.png',f'{c}-OFF.png']:
            p=out/file
            if p.exists():assert sha(p)==expected
            else:shutil.copyfile(src,p)
        raw=np.array(Image.open(src).convert('RGBA'));m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_byte_exact':sha(out/f'{c}-OFF.png')==expected,'size':[raw.shape[1],raw.shape[0]]}
        for name,scale in PROFILES.items():
            rgb,work,settings=working_filter(raw,scale,lookup,stats);repeat,_,_=working_filter(raw,scale,lookup,stats);assert np.array_equal(rgb,repeat);a,count=restore_full(raw,rgb,scale);assert np.array_equal(a,restore_full(raw,repeat,scale)[0]);file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'{c}-{name}-on-black.png');Image.fromarray(work).save(out/f'{c}-{name}-working-input.png');Image.fromarray(r9.gray8(rgb)).save(out/f'{c}-{name}-working-output.png')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'base-{c}.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':count,'repeat_exact':True,'size_exact':a.shape==raw.shape,'settings':settings,'diagnostics':r19.diagnostics(a,raw)};print(c,name,json.dumps(m['outputs'][c+'-'+name]),flush=True)
        assert sha(src)==expected
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
