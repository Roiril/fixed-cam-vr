"""R29 single fix: gate the input central derivative, then convolve with residual wave."""
from pathlib import Path
import argparse,importlib.util,json,shutil,sys,types
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('frozen28',HERE/'offline-round28-convolution.py');q=importlib.util.module_from_spec(s);s.loader.exec_module(q);r=q.r;OUT=q.OUT.parent/'round-29'

def signal(l,wave):
    pad=np.pad(l,((0,0),(1,1)),mode='edge');d=(pad[:,2:]-pad[:,:-2])*.5;span=cv2.dilate(l,np.ones((3,15),np.uint8))-cv2.erode(l,np.ones((3,15),np.uint8));gate=np.clip((span-.018)/.055,0,1);gate=gate*gate*(3-2*gate);source=d*gate;response=q.convolution(source,wave);return response,d,gate,source,span

def render(raw,stage,response,gain):
    delta=np.clip(response*gain,-.25,.25);a,count=r.r19.protect(raw,stage[:,:,:3]/255+delta[:,:,None]);return a,count,delta

def calibrate(out,kernels,wave):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=192,384;c=192;x=np.arange(w);report={};tailmask=np.repeat(((np.abs(x-c)>=8)&(np.abs(x-c)<=24))[None,:],h,axis=0)
    for name,sign in [('positive',1),('negative',-1),('flat',0)]:
        fixture=np.where(x<c,.2,.7) if sign==1 else np.where(x<c,.7,.2) if sign==-1 else np.full(w,.4);rgb=r.r9.gray8(np.repeat(np.repeat(fixture[None,:,None],h,axis=0),3,axis=2));raw=np.dstack([rgb,np.full((h,w),255,np.uint8)]);l=rgb.astype(float)@r.r9.WEIGHTS/255;response,d,gate,source,span=signal(l,wave);ungated=q.convolution(d,wave);old=q.convolution(l,kernels);equivalence=float(np.abs(ungated[:,24:-24]-old[:,24:-24]).max());assert equivalence<2e-6;expected=np.zeros_like(l)
        if sign:
            contrast=float(l[0,c+10]-l[0,c-10]);canonical=wave[:h].astype(np.float64);nexttap=np.pad(canonical[:,1:],((0,0),(0,1)));expected[:,c-24:c+25]=contrast*.5*canonical+contrast*.5*nexttap;error=float(np.abs(response-expected).max());corr=float(np.corrcoef(response.ravel(),expected.ravel())[0,1]);actual=response[:,c-24:c+25];target=expected[:,c-24:c+25];shifts={shift:float(np.mean((actual[:,max(0,shift):min(49,49+shift)]-target[:,max(0,-shift):min(49,49-shift)])**2)) for shift in range(-2,3)};best=min(shifts,key=shifts.get);assert error<2e-12 and corr>.99999 and best==0;tail=tailmask&(gate==0)&(np.abs(response)>1e-6);old_gated=old*gate;newtail=int(tail.sum());assert newtail>0 and np.count_nonzero(old_gated[tail])==0;item={'contrast_after_fixture_quantization':contrast,'raw_expected_max_error':error,'same_sign_correlation':corr,'best_integer_shift_px':best,'ungated_derivative_kernel_equivalence_inner24_max_error':equivalence,'new_raw_tail_pixels_beyond8_output_gate_zero':newtail,'old_raw_tail_pixels_same_positions':int(np.count_nonzero(old_gated[tail])),'profiles':{}}
        else:
            error=float(np.abs(response).max());assert error<1e-12;item={'raw_DC_response_max':error,'ungated_derivative_kernel_equivalence_inner24_max_error':equivalence,'profiles':{}}
        Image.fromarray(raw).save(folder/f'{name}-input.png')
        for profile,gain in q.PROFILES.items():
            a,count,delta=render(raw,raw,response,gain);expected_delta=np.clip(expected*gain,-.25,.25);cliperror=float(np.abs(delta-expected_delta).max());assert cliperror<2e-12;expected_rgba=raw.copy();expected_rgba[:,:,:3]=np.clip(np.rint((rgb.astype(float)/255+expected_delta[:,:,None])*255),0,255).astype(np.uint8);max8=int(np.abs(a.astype(int)-expected_rgba.astype(int)).max());assert max8==0;changed=np.any(a!=raw,axis=2);data={'gain':gain,'clipped_delta_expected_max_error':cliperror,'final_RGBA_expected_max_error_8bit':max8,'changed_RGBA_pixels':int(changed.sum())}
            if sign:data['new_final_tail_pixels_beyond8_output_gate_zero']=int((changed&tailmask&(gate==0)).sum());assert data['new_final_tail_pixels_beyond8_output_gate_zero']>0
            else:assert not changed.any()
            Image.fromarray(a).save(folder/f'{name}-{profile}.png');labelled_pair(Image.fromarray(raw),Image.fromarray(a),(0,0,w,h),folder/f'{name}-{profile}-comparison.png',f'{name}-{profile}.png',f'{name}-input.png');item['profiles'][profile]=data
        report[name]=item
    (out/'synthetic-calibration.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return report

def sources(out):
    pending=[q];visited=set();found={Path(__file__).name:Path(__file__)}
    while pending:
        m=pending.pop()
        if id(m) in visited:continue
        visited.add(id(m));f=getattr(m,'__file__',None)
        if f and Path(f).resolve().parent==HERE:found[Path(f).name]=Path(f);pending.extend(v for v in vars(m).values() if isinstance(v,types.ModuleType))
    dest=out/'source-snapshots';dest.mkdir(exist_ok=True)
    for name,p in found.items():shutil.copyfile(p,dest/name)
    (out/'source-frozen.json').write_text(json.dumps({'files':{str(p.resolve()):sha(p) for p in found.values()}},ensure_ascii=False,indent=2),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);src=r.SOURCE/'base-B.png';expected='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9';assert sha(src)==expected;oldsource=sha(HERE/'offline-round28-convolution.py');sf=json.loads((q.OUT/'source-frozen.json').read_text(encoding='utf-8'))['files'];assert all(sha(Path(p))==v for p,v in sf.items());kernels,wave,provenance=q.materials(out);Image.fromarray(r.r9.gray8(.5+wave[:1030]*.5)).save(out/'active-source-wave-neutral.png');np.savez_compressed(out/'reference-derived-wave.npz',wave=wave,u=np.arange(-24,25));calibration=calibrate(out,kernels,wave)
    raw=np.array(Image.open(src).convert('RGBA'));lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));stage,constant=r.r22.stage12(raw,lookup);assert np.array_equal(stage,r.r22.stage12(raw,lookup)[0]);Image.fromarray(stage).save(out/'B-stage12.png');assert sha(out/'B-stage12.png')==sha(q.ATLAS/'B-stage12.png');surface=r.r19.surface(raw);l=surface@r.r9.WEIGHTS;response,d,gate,source,span=signal(l,wave);assert np.array_equal(response,signal(l,wave)[0]);ungated=q.convolution(d,wave);old=q.convolution(l,kernels);equivalence=float(np.abs(ungated[:,24:-24]-old[:,24:-24]).max());assert equivalence<2e-6;np.savez_compressed(out/'B-source-response.npz',response=response,source_derivative=d,gated_source=source,source_gate=gate,span=span);Image.fromarray(r.r9.gray8(gate)).save(out/'B-source-span-gate.png')
    for name in ['base-B.png','B-OFF.png']:
        p=out/name
        if p.exists():assert sha(p)==expected
        else:shutil.copyfile(src,p)
    r.r19.black(raw,out/'base-B-on-black.png');m={'round':29,'source_sha256':sha(Path(__file__)),'fixed28_source_sha256':oldsource,'only_change':'input central derivative is gated before convolution; no output gate multiplication','input':{'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/'B-OFF.png')==expected},'baseline_stage12_sha256':sha(out/'B-stage12.png'),'baseline_exact23stage12':True,'legacy_fault_mix':1.,'reference_derived_material':provenance,'source_wave_seed':q.SEED,'gain_profiles':q.PROFILES,'formula':'D=central(surfaceL), Q=D*sameSpanGate, response[y,x]=sum_u wave[y,u]*Q[y,x-u]; delta=clip(gain*response,-.25,.25)','output_gate_used':False,'source_gate':{'window':[3,15],'smoothstep_start':.018,'smoothstep_full':.073},'actual_ungated_derivative_kernel_equivalence_inner24_max_error':equivalence,'calibration':calibration,'outputs':{}}
    rois={'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)};visible=raw[:,:,3]>0;boundary=visible&(cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5)<=2);baseline,_=r.r19.protect(raw,stage[:,:,:3]/255)
    for profile,gain in q.PROFILES.items():
        a,count,delta=render(raw,stage,response,gain);assert np.array_equal(a,render(raw,stage,signal(l,wave)[0],gain)[0]);assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~visible],raw[~visible]);assert np.array_equal(a[boundary],raw[boundary]);file=f'B-{profile}.png';Image.fromarray(a).save(out/file);r.r19.black(a,out/f'B-{profile}-on-black.png')
        for label,box in rois.items():labelled_pair(Image.fromarray(raw),Image.fromarray(a),box,out/f'B-{profile}-comparison-{label}.png',file,'base-B.png')
        for label,box in {'floor':rois['floor'],'edge':rois['edge']}.items():labelled_pair(Image.fromarray(baseline),Image.fromarray(a),box,out/f'B-{profile}-kernel-only-{label}.png',file,'共通stage12の下地')
        actual=(a[:,:,:3].astype(float)-baseline[:,:,:3])@r.r9.WEIGHTS/255;m['outputs'][profile]={'file':file,'sha256':sha(out/file),'label':f'強さ {gain:.2f} ／ 入力側でgate','gain':gain,'legacy_fault_mix':1.,'alpha_exact':True,'hidden_exact':True,'inner2px_exact':int(boundary.sum()),'repeat_exact':True,'actual_kernel_changed_pixels':int(np.any(a!=baseline,axis=2).sum()),'actual_floor_kernel_delta_RMS':float(np.sqrt(np.mean(actual[740:900,680:1100]**2))),'diagnostics':r.r19.diagnostics(a,raw),'response_regions':{key:{'raw_RMS':float(np.sqrt(np.mean(response[b[1]:b[3],b[0]:b[2]]**2))),'delta_RMS':float(np.sqrt(np.mean(delta[b[1]:b[3],b[0]:b[2]]**2)))} for key,b in rois.items() if key!='full'}};print(profile,m['outputs'][profile]['sha256'],flush=True)
    assert sha(src)==expected and sha(HERE/'offline-round28-convolution.py')==oldsource;assert all(sha(Path(p))==v for p,v in sf.items());m['prior_sources_unchanged']=True;q.preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');sources(out);print('Round29 complete',flush=True)
if __name__=='__main__':main()
