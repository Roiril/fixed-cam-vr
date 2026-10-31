"""Recompute14 and19 surface from the input; add only missing low-frequency grey in detail regions."""
from pathlib import Path
import sys,argparse,json,shutil,base64,importlib.util
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
def module(name,file):
    s=importlib.util.spec_from_file_location(name,HERE/file);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
r14=module('r14','offline-round14.py');r12=module('r12','offline-round12.py');r19=module('r19','offline-round19.py');r9=r14.r9;r10=r12.r10;r11=r14.r11;SOURCE=r11.DEFAULT;DEFAULT=SOURCE.parent/'round-20';PROFILES={'soft':.25,'standard':.50,'strong':.75}

def reconstruct14(raw,lookup,stats):
    visible=raw[:,:,3]>0;l=raw[:,:,:3].astype(float)@r9.WEIGHTS/255
    if np.ptp(l[visible])<1e-7:return raw.copy(),raw.copy(),{'constant_input_passthrough':True}
    # Retain12's exact optical input convention to reproduce the successful14 signal.
    optical=r9.optical(l,'A');clean,pre,std,energy,_=r10.prepare(optical);p=r12.PROFILES['strong'];core,gate,dy=r12.line_core(clean,std,p);signal=pre+(core-pre)*gate*p['line_mix']+dy*gate*p['detail_gain'];target,faults=r12.faults(signal,energy,lookup,visible,p);target,dark=r12.dark_variation(target,l,p,visible);stage=r11.rgba(raw,target);sl=stage[:,:,:3].astype(float)@r9.WEIGHTS/255
    f=r14.fields(r14.extend_alpha(raw),sl);noise,h_rms=r14.synthesize(sl.shape,stats);target,paint,events=r14.process(sl,f,lookup,noise,h_rms,r14.PROFILES['strong']);candidate=stage.copy();candidate[:,:,:3]=r9.gray8(stage[:,:,:3]/255+(target-sl)[:,:,None]);candidate[~paint]=stage[~paint]
    # Match14's half-up quantizer and arithmetic order exactly, including fractional border coverage.
    distance=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);u=np.clip((distance-2)/18,0,1);fade=u*u*(3-2*u);a=raw.copy();a[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(candidate[:,:,:3].astype(float)-raw[:,:,:3])/255*fade[:,:,None])
    return a,stage,{'constant_input_passthrough':False,'faults':faults,'dark':dark,'abrasion':events,'legacy12_optical_input_convention':True}

def low_frequency_correction(a14,s19):
    l14=r19.extend_alpha(a14).astype(float)@r9.WEIGHTS/255;l19=r19.extend_alpha(s19).astype(float)@r9.WEIGHTS/255;low14=cv2.GaussianBlur(l14,(0,0),3);low19=cv2.GaussianBlur(l19,(0,0),3);mean=cv2.GaussianBlur(l19,(0,0),6);std=np.sqrt(np.maximum(0,cv2.GaussianBlur(l19*l19,(0,0),6)-mean*mean));texture=np.clip((std-.012)/.025,0,1);missing=np.clip((low19-low14-.010)/.035,0,1);mask=texture*missing;delta=cv2.GaussianBlur(mask*np.maximum(0,low19-low14),(0,0),3)
    return delta,mask,{'sigma_lowpass':3,'sigma_structure_std':6,'texture_std_gate':[.012,.037],'missing_grey_gate':[.010,.045],'restoration_direction':'positive only','HF_multiplier':1.0}

def combine(raw,a14,delta,k):
    # Keep the14 result outside the correction. Border fade already exists in14; add only guarded delta.
    d=cv2.distanceTransform((raw[:,:,3]>0).astype(np.uint8),cv2.DIST_L2,5);u=np.clip((d-2)/18,0,1);fade=u*u*(3-2*u);out=a14.copy();out[:,:,:3]=r9.gray8(a14[:,:,:3]/255+delta[:,:,None]*k*fade[:,:,None]);out[raw[:,:,3]==0]=raw[raw[:,:,3]==0];boundary=(raw[:,:,3]>0)&(d<=2);assert np.array_equal(out[:,:,3],raw[:,:,3]) and np.array_equal(out[raw[:,:,3]==0],raw[raw[:,:,3]==0]) and np.array_equal(out[boundary],raw[boundary]);return out,int(boundary.sum())

def metrics(a,a14,raw,delta,k):
    l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;b=a14[:,:,:3].astype(float)@r9.WEIGHTS/255;visible=raw[:,:,3]>0;hp=lambda v:v-cv2.GaussianBlur(v,(0,0),3);old_hp=hp(b);new_hp=hp(l);den=float(np.sum(old_hp[visible]**2));projection=float(np.sum(old_hp[visible]*new_hp[visible])/max(1e-12,den));r={'delta_luma_RMS_from14':float(np.sqrt(np.mean((l[visible]-b[visible])**2))),'changed_visible_fraction':float(np.any(a[:,:,:3]!=a14[:,:,:3],axis=2)[visible].mean()),'HF_projection_to14':projection,'HF_correlation':float(np.corrcoef(old_hp[visible],new_hp[visible])[0,1]) if den>1e-12 else 1.0,'RGB_clip_fraction_before_u8':float(np.any(a14[:,:,:3]/255+delta[:,:,None]*k>1,axis=2)[visible].mean())}
    for name,(x0,y0,x1,y1) in {'floor':(680,740,1100,900),'wall':(180,250,500,500),'black':(820,450,880,530)}.items():
        if x1<=a.shape[1] and y1<=a.shape[0]:
            q=l[y0:y1,x0:x1];ob=b[y0:y1,x0:x1];r[name]={'mean':float(q.mean()),'mean_delta_from14':float((q-ob).mean()),'gray_L003to04_fraction':float(((q>.03)&(q<.4)).mean()),'HF_RMS_ratio':float(np.std(hp(l)[y0:y1,x0:x1])/max(1e-9,np.std(hp(b)[y0:y1,x0:x1])))}
    return r

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for key,file in [('base',f'base-{c}.png'),('14',f'{c}-recomputed14.png'),('surface',f'{c}-surface19.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+key]='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>擦れを保ち灰色の面を戻す</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>擦れを保ち灰色の面を戻す</h1><p>元入力から14版目と面Sを再計算し、細部に不足する低周波の灰色を補います。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="補う量"><option value="soft">25%</option><option value="standard">50%</option><option value="strong">75%</option><option value="14">再計算した14版目</option><option value="surface">19版目の面S</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="擦れと灰色の面の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='all');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);sm=json.loads((SOURCE/'input-and-processing-manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));stats_path=r14.DEFAULT/'reference-residual-statistics.json';stats=json.loads(stats_path.read_text(encoding='utf-8'));cameras=['custom'] if args.input else list('ABC') if args.camera=='all' else [args.camera]
    m={'round':20,'profiles':PROFILES,'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round14.py',HERE/'offline-round12.py',HERE/'offline-round19.py']},'lookup_sha256':sha(r11.LOOKUP),'reference_statistics_sha256':sha(stats_path),'completed_prior_output_pixels_used':False,'generated_RGB_used':False,'camera_parameters_identical':True,'inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else SOURCE/f'base-{c}.png';expected=sha(src)
        if not args.input:assert expected==sm['inputs'][c]['copy_sha256']
        for file in [f'base-{c}.png',f'{c}-OFF.png']:
            p=out/file
            if p.exists():assert sha(p)==expected
            else:shutil.copyfile(src,p)
        raw=np.array(Image.open(src).convert('RGBA'));a14,stage,details=reconstruct14(raw,lookup,stats);again,_,_=reconstruct14(raw,lookup,stats);assert np.array_equal(a14,again);s= r19.surface(raw);assert np.array_equal(s,r19.surface(raw));s19,_=r19.protect(raw,s);delta,mask,settings=low_frequency_correction(a14,s19)
        Image.fromarray(a14).save(out/f'{c}-recomputed14.png');r19.black(a14,out/f'{c}-recomputed14-on-black.png');Image.fromarray(s19).save(out/f'{c}-surface19.png');r19.black(s19,out/f'{c}-surface19-on-black.png');Image.fromarray(r9.gray8(np.clip(mask,0,1))).save(out/f'{c}-restoration-mask.png')
        exact=None
        if c=='B' and not args.input:
            # Read only for validation; never use these pixels to construct20.
            frozen=np.array(Image.open(r14.DEFAULT/'B-strong.png').convert('RGBA'));exact=bool(np.array_equal(a14,frozen));assert exact,'14strong source regeneration mismatch'
        m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/f'{c}-OFF.png')==expected,'recomputed14_sha256':sha(out/f'{c}-recomputed14.png'),'recomputed14_repeat_exact':True,'recomputed14_exact_to_frozen_B_validation_only':exact,'recomputed14_details':details,'surface19_sha256':sha(out/f'{c}-surface19.png'),'surface19_repeat_exact':True,'correction':settings,'mask_visible_mean':float(mask[raw[:,:,3]>0].mean()),'maximum_low_frequency_addition_before_k':float(delta.max())}
        for name,k in PROFILES.items():
            a,bcount=combine(raw,a14,delta,k);assert np.array_equal(a,combine(raw,a14,delta,k)[0]);file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'{c}-{name}-on-black.png')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(a14),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'{c}-recomputed14.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':bcount,'repeat_exact':True,'metrics':metrics(a,a14,raw,delta,k)};print(c,name,json.dumps(m['outputs'][c+'-'+name]),flush=True)
        assert sha(src)==expected
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
