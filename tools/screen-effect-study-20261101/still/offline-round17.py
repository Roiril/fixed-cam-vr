"""Independent tone-surface and low-frequency contour-wave probes. No new fragments."""
from pathlib import Path
import sys,argparse,importlib.util,json,shutil,base64
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r14',HERE/'offline-round14.py');r14=importlib.util.module_from_spec(s);s.loader.exec_module(r14);r9=r14.r9;r11=r14.r11;PREVIOUS=r14.DEFAULT;DEFAULT=PREVIOUS.parent/'round-17'

def tone_surface(rgba):
    extended=r14.extend_alpha(rgba);rgb=extended[:,:,:3].astype(np.float32)/255;l=rgb@r9.WEIGHTS;u8=r9.gray8(rgb)
    # Mean-shift makes local tone regions; blending keeps a continuous slope between them.
    mean_shift=cv2.pyrMeanShiftFiltering(u8,14,9,maxLevel=1).astype(np.float32)/255
    mapped=mean_shift@r9.WEIGHTS;mean=cv2.GaussianBlur(l,(0,0),4);std=np.sqrt(np.maximum(0,cv2.GaussianBlur(l*l,(0,0),4)-mean*mean));micro=l-cv2.GaussianBlur(l,(0,0),.65)
    calm=np.clip((.075-std)/.055,0,1);middle=np.clip((l-.025)/.07,0,1)*np.clip((.85-l)/.10,0,1);core_protect=np.clip((.027-np.abs(micro))/.020,0,1)
    # Smooth only the tone-change field so mean-shift region borders cannot become contour bands.
    tone_delta=cv2.GaussianBlur(mapped-l,(0,0),8)
    denoised=cv2.bilateralFilter(l.astype(np.float32),7,.025,3)
    weight=.70*middle*core_protect*(.35+.65*calm);target=l+tone_delta*weight+(denoised-l)*(.40*middle*core_protect)
    gain=np.divide(target,l,out=np.ones_like(target),where=l>1e-5);out=rgba.copy();out[:,:,:3]=r9.gray8(rgb*gain[:,:,None]);out[rgba[:,:,3]==0]=rgba[rgba[:,:,3]==0]
    return out,{'mean_shift_spatial_radius':14,'mean_shift_colour_radius_8bit':9,'tone_change_field_gaussian_sigma':8,'bilateral_micro_mix':.40,'maximum_tone_mix':.70,'weight_mean':float(weight[rgba[:,:,3]>0].mean()),'preserve_rgb_ratios_before_quantization':True}

def contour_wave(rgba):
    extended=r14.extend_alpha(rgba);l=(extended[:,:,:3].astype(np.float32)@r9.WEIGHTS/255).astype(np.float32);h,w=l.shape;gx=cv2.Sobel(l,cv2.CV_32F,1,0,ksize=3)/8
    # Spatially varying low-frequency field: neither global row shift nor periodic sine.
    rng=np.random.default_rng(73019);coarse=rng.normal(size=(max(4,h//45),max(4,w//75))).astype(np.float32);field=cv2.resize(coarse,(w,h),interpolation=cv2.INTER_CUBIC);field=cv2.GaussianBlur(field,(0,0),28,12);field-=field.mean();field/=max(float(field.std()),1e-8);field=np.clip(field,-2,2)
    energy=cv2.GaussianBlur(np.abs(gx),(0,0),2.5);gate=np.clip((energy-.009)/.035,0,1);gate=cv2.GaussianBlur(gate,(0,0),3.0);shift=field*3.5*gate
    yy,xx=np.indices((h,w),dtype=np.float32);out=rgba.copy();out[:,:,:3]=cv2.remap(extended[:,:,:3],xx+shift,yy,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE);out[rgba[:,:,3]==0]=rgba[rgba[:,:,3]==0]
    mask=rgba[:,:,3]>0
    return out,{'field_seed':73019,'field_scale_px':[75,45],'gaussian_sigma_xy':[28,12],'amplitude_px':3.5,'maximum_abs_shift_px':float(np.abs(shift[mask]).max()),'shift_rms_px':float(np.sqrt(np.mean(shift[mask]**2))),'world_edge_gate_mean':float(gate[mask].mean()),'new_fragment_events':0}

def protect(raw,baseline,candidate):
    d=cv2.distanceTransform((raw[:,:,3]>0).astype(np.uint8),cv2.DIST_L2,5);u=np.clip((d-2)/18,0,1);fade=u*u*(3-2*u);out=baseline.copy();out[:,:,:3]=r9.gray8(baseline[:,:,:3]/255+(candidate[:,:,:3].astype(float)-baseline[:,:,:3])/255*fade[:,:,None]);out[raw[:,:,3]==0]=raw[raw[:,:,3]==0];boundary=(raw[:,:,3]>0)&(d<=2);assert np.array_equal(out[boundary],raw[boundary]);assert np.array_equal(out[:,:,3],raw[:,:,3]);assert np.array_equal(out[raw[:,:,3]==0],raw[raw[:,:,3]==0]);return out,boundary

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={key:'data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode() for key,file in [('base','base.png'),('baseline','baseline14.png'),*[(key,v['file']) for key,v in m['outputs'].items()]]}
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>面の階調と輪郭のうねり</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;background:var(--surface);color:var(--ink)}select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>面の階調と輪郭のうねり</h1><p>14版目を基準に、階調を面へ整理する変更とうねりの変更を個別に比較します。</p><select id="choice" aria-label="比較する画像"><option value="A-tone">A 階調を面へ整理</option><option value="B-wave">B 輪郭のうねり</option><option value="baseline">14版目 強め</option><option value="base">元の入力</option></select></div><div class="image"><img id="image" alt="面の階調と輪郭のうねりの比較"></div><script>const images=IMAGES;const s=document.getElementById('choice');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--input',type=Path,help='Optional arbitrary RGBA PNG; uses this input as its own baseline and OFF.');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True)
    if args.input:base=baseline_path=args.input
    else:
        base=r14.SOURCE/'base-B.png';baseline_path=PREVIOUS/'B-strong.png';sm=json.loads((r14.SOURCE/'manifest.json').read_text(encoding='utf-8'));pm=json.loads((PREVIOUS/'manifest.json').read_text(encoding='utf-8'));assert sha(base)==sm['inputs']['B']['sha256'];assert sha(baseline_path)==pm['outputs']['B-strong']['sha256']
    raw=np.array(Image.open(base).convert('RGBA'));baseline=np.array(Image.open(baseline_path).convert('RGBA'));assert raw.shape==baseline.shape;assert np.array_equal(raw[:,:,3],baseline[:,:,3])
    for src,file in [(base,'base.png'),(base,'OFF.png'),(baseline_path,'baseline14.png')]:
        p=out/file
        if p.exists():assert sha(p)==sha(src)
        else:shutil.copyfile(src,p)
    m={'round':17,'input':{'base_path':str(base.resolve()),'base_sha256':sha(base),'baseline_path':str(baseline_path.resolve()),'baseline_sha256':sha(baseline_path),'arbitrary_input_mode':bool(args.input),'OFF_byte_exact':sha(out/'OFF.png')==sha(base)},'source_code_sha256':sha(Path(__file__)),'generated_RGB_used':False,'outputs':{}};mask=raw[:,:,3]>0;old_l=baseline[:,:,:3].astype(float)@r9.WEIGHTS/255
    for key,fn in [('A-tone',tone_surface),('B-wave',contour_wave)]:
        candidate,settings=fn(baseline);again,_=fn(baseline);assert np.array_equal(candidate,again);a,boundary=protect(raw,baseline,candidate);file=key+'.png';im=Image.fromarray(a);im.save(out/file);r11.black(im,out/(key+'-on-black.png'));l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;delta=l-old_l;flat=(slice(250,500),slice(180,500));black=(slice(450,530),slice(820,880));micro=lambda z:z-cv2.GaussianBlur(z,(0,0),.65)
        for label,roi in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530)}.items():
            if roi[2]<=a.shape[1] and roi[3]<=a.shape[0]:labelled_pair(Image.fromarray(baseline),im,roi,out/(key+'-comparison-'+label+'.png'),file,'baseline14.png')
        labelled_pair(Image.fromarray(raw),im,(75,250,min(160,a.shape[1]),min(650,a.shape[0])),out/(key+'-comparison-border.png'),file,'base.png') if a.shape[1]>=160 and a.shape[0]>=650 else None
        m['outputs'][key]={'file':file,'sha256':sha(out/file),'settings':settings,'repeat_exact':True,'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact_to_raw':int(boundary.sum()),'visible_changed_fraction':float(np.any(a[:,:,:3]!=baseline[:,:,:3],axis=2)[mask].mean()),'luma_delta_RMS':float(np.sqrt(np.mean(delta[mask]**2))),'luma_delta_mean':float(delta[mask].mean()),'flat_luma_delta_RMS':float(np.sqrt(np.mean(delta[flat]**2))) if a.shape[0]>=500 and a.shape[1]>=500 else None,'flat_micro_RMS_ratio':float(np.std(micro(l)[flat])/max(1e-8,np.std(micro(old_l)[flat]))) if a.shape[0]>=500 and a.shape[1]>=500 else None,'black_luma_delta_mean':float(delta[black].mean()) if a.shape[0]>=530 and a.shape[1]>=880 else None};print(key,json.dumps(m['outputs'][key]),flush=True)
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
