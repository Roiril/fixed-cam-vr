"""Round14: short mixed-width abrasion, surviving interior detail, phase-randomized statistics."""
from pathlib import Path
import sys,json,argparse,importlib.util,shutil,base64
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r13',HERE/'offline-round13.py');r13=importlib.util.module_from_spec(s);s.loader.exec_module(r13);r9=r13.r9;r11=r13.r11;SOURCE=r13.SOURCE;DEFAULT=SOURCE.parent/'round-14'
PROFILES={'soft':{'groups':2000,'opacity':.35,'interior_share':.45,'flat_horizontal_rms_gain':1,'near_abrasion_rms':.001},'medium':{'groups':3600,'opacity':.50,'interior_share':.60,'flat_horizontal_rms_gain':2,'near_abrasion_rms':.0015},'strong':{'groups':5200,'opacity':.65,'interior_share':.70,'flat_horizontal_rms_gain':3,'near_abrasion_rms':.002}}

def extend_alpha(raw):
    valid=raw[:,:,3]>=250;assert valid.any();_,labels=cv2.distanceTransformWithLabels((~valid).astype(np.uint8),cv2.DIST_L2,cv2.DIST_MASK_5,labelType=cv2.DIST_LABEL_PIXEL);lut=np.zeros((int(labels.max())+1,3),np.uint8);lut[labels[valid]]=raw[:,:,:3][valid];filled=raw.copy();filled[:,:,:3]=lut[labels];filled[:,:,:3][valid]=raw[:,:,:3][valid];return filled

def synthesize(shape,stats):
    h,w=shape;fx=np.fft.fftfreq(w);fy=np.fft.fftfreq(h);sx=np.array(stats['frequency_x']);sy=np.array(stats['frequency_y']);px=np.array(stats['marginal_psd_x']);py=np.array(stats['marginal_psd_y']);ox=np.argsort(sx);oy=np.argsort(sy);interp_x=np.interp(fx,sx[ox],px[ox]);interp_y=np.interp(fy,sy[oy],py[oy]);horizontal=(np.abs(fy[:,None])>2*np.abs(fx[None,:]))&(np.abs(fy[:,None])>.02);filter=np.sqrt(np.maximum(0,interp_y[:,None]*interp_x[None,:]))*horizontal
    white=np.random.default_rng(1618033).normal(size=shape);field=np.fft.ifft2(np.fft.fft2(white)*filter).real;field-=field.mean();field/=max(field.std(),1e-12);field=np.clip(field,-3,3);field-=field.mean();field/=field.std()
    return field,stats['micro_rms']*np.sqrt(stats['horizontal_line_orientation_power'])

def fields(raw,stage):
    f=r13.fields(raw,stage);smooth,gx,span,peaks,lo,hi,distance,visible=f;d1=smooth-cv2.GaussianBlur(smooth,(0,0),1.2);d2=smooth-cv2.GaussianBlur(smooth,(0,0),2.5);persistent=np.where(d1*d2>0,np.minimum(np.abs(d1),np.abs(d2)),0);energy=np.sqrt(cv2.GaussianBlur(persistent*persistent,(0,0),.8));inside=cv2.erode(visible.astype(np.uint8),np.ones((19,19),np.uint8)).astype(bool);texture=(energy>.0045)&(span>.035)&inside
    edge_weights=peaks*span*np.abs(gx);texture_weights=texture*np.maximum(0,energy-.0045)*np.clip(span/.2,0,1);assert edge_weights.sum()>0 and texture_weights.sum()>0
    return f,energy,edge_weights/edge_weights.sum(),texture_weights/texture_weights.sum()

def process(stage,f,lookup,noise,h_rms,p):
    (smooth,gx,span,peaks,low,high,distance,visible),energy,ew,tw=f;h,w=stage.shape;rng=np.random.default_rng(141421);maximum=max(v['groups'] for v in PROFILES.values());edge_anchors=rng.choice(h*w,size=maximum,p=ew.ravel());texture_anchors=rng.choice(h*w,size=maximum,p=tw.ravel());types=rng.random(maximum);runs=[v for row in lookup['runs_threshold_008'] for v in row];result=stage.copy();paint=np.zeros(stage.shape,bool);strands=longs=internal=0;lengths=[];heights=[]
    for group in range(p['groups']):
        is_internal=types[group]<p['interior_share'];anchor=texture_anchors[group] if is_internal else edge_anchors[group];cy,cx=divmod(int(anchor),w);contrast=float(np.clip((high[cy,cx]-low[cy,cx])/.25,0,1));polarity=1 if gx[cy,cx]>0 else -1
        for child in range(int(rng.integers(5,13))):
            run=runs[int(rng.integers(len(runs)))];u=rng.random();length=max(1,min(5,run['length'])) if u<.85 else max(6,min(12,run['length'])) if u<.997 else int(rng.integers(16,33));v=rng.random();height=1 if v<.72 else 2 if v<.985 else 3;bright=rng.random()<.5;direction=(1 if rng.random()<.5 else -1) if is_internal else (-polarity if bright else polarity);value=float(high[cy,cx] if bright else low[cy,cx]);x0=cx+int(rng.integers(-2,3));y0=cy+int(rng.integers(-4,5));opacity=p['opacity']*contrast*rng.uniform(.5,1)
            vertical=[1] if height==1 else [.78,.78] if height==2 else [.45,1,.45]
            for k in range(length):
                x=x0+direction*k
                if x<0 or x>=w:continue
                taper=float(np.sin(np.pi*(k+.5)/length)**.55)
                for row,alpha_y in enumerate(vertical):
                    y=y0+row
                    if y<0 or y>=h or not visible[y,x]:continue
                    alpha=opacity*taper*alpha_y;result[y,x]+=(value-result[y,x])*alpha;paint[y,x]=True
            strands+=1;longs+=int(length>=16);internal+=int(is_internal);lengths.append(length);heights.append(height)
    mid=np.clip((stage-.05)/.12,0,1)*np.clip((.82-stage)/.14,0,1)*visible;near=np.exp(-distance/4.5)*(distance<14)*np.clip(span/.2,0,1);amplitude=h_rms*p['flat_horizontal_rms_gain']+near*p['near_abrasion_rms'];weight=mid*amplitude;weighted_noise=noise-float(np.sum(noise*weight)/max(1e-12,weight.sum()));delta=weighted_noise*weight;result+=delta;paint|=weight>0
    return np.clip(result,.001,.85),paint,{'short_fragments':strands,'internal_fragments':internal,'rare_long_fragments':longs,'length_bins':{'1..5':int(np.sum(np.array(lengths)<=5)),'6..12':int(np.sum((np.array(lengths)>=6)&(np.array(lengths)<=12))),'16..32':longs},'height_bins':{str(k):heights.count(k) for k in [1,2,3]},'far_midplane_ideal_rms':h_rms*p['flat_horizontal_rms_gain'],'noise_weighted_mean':float(delta.sum()/max(1,visible.sum()))}

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--profile',choices=[*PROFILES,'all'],default='all');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);stats_path=out/'reference-residual-statistics.json';assert stats_path.exists(),'Run measure-round14.py first';stats=json.loads(stats_path.read_text(encoding='utf-8'));source_m=json.loads((SOURCE/'manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));cameras=list('ABC') if args.camera=='all' else [args.camera];profiles=list(PROFILES) if args.profile=='all' else [args.profile]
    manifest={'round':14,'profiles':PROFILES,'inputs':{},'outputs':{},'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'measure-round14.py',HERE/'offline-round09.py',HERE/'offline-round13.py']},'reference_statistics_sha256':sha(stats_path),'phase_seed':1618033,'noise_transfer':'marginal PSD scalar arrays and measured horizontal-sector RMS only; independent full-frame random phases; no residual pixels or source-position field','generated_rgb_used':False}
    if (out/'manifest.json').exists():
        old=json.loads((out/'manifest.json').read_text(encoding='utf-8'));assert old['profiles']==PROFILES;manifest['inputs']=old['inputs'];manifest['outputs']=old['outputs']
    for camera in cameras:
        base=SOURCE/f'base-{camera}.png';baseline=SOURCE/f'{camera}-strong.png';assert sha(base)==source_m['inputs'][camera]['sha256'];assert sha(baseline)==source_m['outputs'][f'{camera}-strong']['sha256'];raw=np.array(Image.open(base).convert('RGBA'));stage_rgba=np.array(Image.open(baseline).convert('RGBA'));stage=stage_rgba[:,:,:3].astype(float)@r9.WEIGHTS/255
        for src,dest in [(base,out/f'base-{camera}.png'),(baseline,out/f'{camera}-baseline-12strong.png')]:
            if dest.exists():assert sha(dest)==sha(src)
            else:shutil.copyfile(src,dest)
        shutil.copyfile(base,out/f'{camera}-off.png');assert sha(out/f'{camera}-off.png')==sha(base)
        dummy=np.zeros((7,7,4),np.uint8);dummy[2:5,2:5,:3]=[50,90,130];dummy[2:5,2:5,3]=255;extended_dummy=extend_alpha(dummy);assert np.all(extended_dummy[:,:,:3]==[50,90,130]) and np.array_equal(extended_dummy[:,:,3],dummy[:,:,3])
        filled=extend_alpha(raw);f=fields(filled,stage);noise,h_rms=synthesize(stage.shape,stats);distance_inside=cv2.distanceTransform((raw[:,:,3]>0).astype(np.uint8),cv2.DIST_L2,5);u=np.clip((distance_inside-2)/18,0,1);border_fade=u*u*(3-2*u);boundary=(raw[:,:,3]>0)&(distance_inside<=2);manifest['inputs'][camera]={'base_sha256':sha(base),'baseline_12strong_sha256':sha(baseline),'off_byte_exact':True,'horizontal_component_measured_rms':h_rms,'alpha_extension_dummy_calibration':True,'hidden_RGB_excluded_from_field_calculation':True,'base_difference_boundary_fade_px':[2,20]}
        for name in profiles:
            target,paint,events=process(stage,f,lookup,noise,h_rms,PROFILES[name]);again,again_paint,_=process(stage,f,lookup,noise,h_rms,PROFILES[name]);assert np.array_equal(target,again) and np.array_equal(paint,again_paint);candidate=stage_rgba.copy();candidate[:,:,:3]=r9.gray8(stage_rgba[:,:,:3]/255+(target-stage)[:,:,None]);candidate[~paint]=stage_rgba[~paint];a=raw.copy();a[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(candidate[:,:,:3].astype(float)-raw[:,:,:3])/255*border_fade[:,:,None]);assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[raw[:,:,3]==0],raw[raw[:,:,3]==0]);assert np.array_equal(a[boundary],raw[boundary]);im=Image.fromarray(a);file=f'{camera}-{name}.png';im.save(out/file);r11.black(im,out/f'{camera}-{name}-on-black.png')
            for label,roi in {'full':(0,0,1280,1030),'edge':(650,280,1000,630),'floor':(680,740,1100,900),'flat':(180,250,500,500),'black':(820,450,880,530)}.items():labelled_pair(Image.fromarray(stage_rgba),im,roi,out/f'{camera}-{name}-comparison-{label}.png',file,f'{camera}-baseline-12strong.png')
            labelled_pair(Image.fromarray(raw),im,(75,250,160,650),out/f'{camera}-{name}-comparison-border.png',file,f'base-{camera}.png')
            wall=(slice(250,500),slice(180,500));black=(slice(450,530),slice(820,880));delta=(a[:,:,:3].astype(float)-stage_rgba[:,:,:3])@r9.WEIGHTS/255;manifest['outputs'][f'{camera}-{name}']={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'repeat_exact':True,'boundary_2px_RGBA_exact_to_raw_base':True,'boundary_2px_pixels':int(boundary.sum()),'black_mean_delta':float(delta[black].mean()),'flat_added_rms_8bit_result':float(np.sqrt(np.mean(delta[wall]**2))),'flat_mean_delta':float(delta[wall].mean()),'events':events};print(json.dumps({f'{camera}-{name}':manifest['outputs'][f'{camera}-{name}']},indent=2),flush=True)
        assert sha(base)==manifest['inputs'][camera]['base_sha256'] and sha(baseline)==manifest['inputs'][camera]['baseline_12strong_sha256']
    r13.preview(out,manifest);html=(out/'preview.html').read_text(encoding='utf-8').replace('13版目','14版目').replace('薄い水平の糸と擦れ','短い擦れと微小変動').replace('12版の強めを固定し、輪郭の近くへ細い糸を重ねた実フィルタ出力です。','12版の強めから短片の幅と太さ、細部内部の擦れ、中間面の微小変動を比較します。');(out/'preview.html').write_text(html,encoding='utf-8');(out/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
