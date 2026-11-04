"""R28: zero-DC row convolutions from reference-derived contour residual derivatives."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys,types
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
HERE=Path(__file__).resolve().parent
s=importlib.util.spec_from_file_location('frozen23',HERE/'offline-round23.py');r=importlib.util.module_from_spec(s);s.loader.exec_module(r)
ATLAS=r.DEFAULT;OUT=ATLAS.parent/'round-28';SEED=280023;PROFILES={'kernel065':.65,'kernel100':1.,'kernel135':1.35}

def central(a):
    pad=np.pad(np.asarray(a,np.float64),((0,0),(1,1)),mode='constant');k=(pad[:,2:]-pad[:,:-2])*.5;k-=k.mean(axis=1,keepdims=True);return k

def convolution(l,k):
    assert l.shape[0]<=len(k);h,w=l.shape;out=np.zeros((h,w),np.float64);xx=np.arange(w)
    # True convolution: input[x-u]. cv2.filter2D would correlate and reverse the response.
    for i,u in enumerate(range(-24,25)):out+=k[:h,i,None]*l[:,np.clip(xx-u,0,w-1)]
    return out

def materials(out):
    am=json.loads((ATLAS/'atlas-manifest.json').read_text(encoding='utf-8'));arrays=[];items=[]
    for item in am['items'][:2]:
        f=ATLAS/item['file'];assert sha(f)==item['sha256'];z=np.load(f);assert set(z.files)=={'residual','u'};a=z['residual'].astype(np.float32);assert a.shape[1]==49;arrays.append(a);items.append(item)
    derivative=[central(a).astype(np.float32) for a in arrays]
    # Existing strip's third slot has .40 amplitude. Alias source01 and undo that slot multiplier.
    # This uses only atlas01/02, retaining the unmodified old phase/stretch/seam implementation.
    kernels,sequence=r.residual_strip([derivative[0],derivative[1],derivative[0]/.40],4096,SEED);wave,wave_sequence=r.residual_strip([arrays[0],arrays[1],arrays[0]/.40],4096,SEED);assert sequence==wave_sequence;kernels=kernels.astype(np.float64);kernels-=kernels.mean(axis=1,keepdims=True);assert np.max(np.abs(kernels.sum(axis=1)))<1e-12
    np.savez_compressed(out/'reference-derived-kernels.npz',kernel=kernels,u=np.arange(-24,25));(out/'kernel-source-blocks.json').write_text(json.dumps({'seed':SEED,'atlas_slots':[1,2,1],'slot_probabilities':[.45,.45,.10],'slot3_amplitude_compensated':True,'output_row_y_maps_to_strip_row_y':True,'blocks':sequence},indent=2),encoding='utf-8')
    for i,(a,k) in enumerate(zip(arrays,derivative),1):
        Image.fromarray(r.r9.gray8(.5+a*.5)).save(out/f'atlas{i:02d}-residual-neutral.png');Image.fromarray(r.r9.gray8(.5+k*.5)).save(out/f'atlas{i:02d}-derivative-neutral.png')
    Image.fromarray(r.r9.gray8(.5+kernels[:1030]*.5)).save(out/'active-row-kernels-neutral.png')
    return kernels,wave,{'name':'参照由来の輪郭残差のu微分kernel','atlas_manifest_path':str((ATLAS/'atlas-manifest.json').resolve()),'atlas_manifest_sha256':sha(ATLAS/'atlas-manifest.json'),'reference_path':am['reference_path'],'reference_sha256':am['reference_sha256'],'limitation':am['limitation'],'items':items,'formula':'k[y,u]=(R[y,u+1]-R[y,u-1])/2 with zero extension, then subtract row mean; 49 taps u=-24..24','strip_rows':4096,'normal_width':1.,'max_abs_row_DC':float(np.abs(kernels.sum(axis=1)).max()),'source_RGB_used_as_output':False,'source_scalar_residual_used':True,'seed':SEED,'atlas_slots':[1,2,1],'slot3_multiplier_undo':.40,'derivative_then_strip_vs_strip_then_derivative_max_error':float(np.abs(kernels-central(wave)).max())}

def calibrate(out,k,wave):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=192,384;c=192;x=np.arange(w);report={}
    for name,sign in [('positive',1),('negative',-1),('flat',0)]:
        l=np.repeat((np.where(x<c,.2,.7) if sign==1 else np.where(x<c,.7,.2) if sign==-1 else np.full(w,.4))[None,:],h,axis=0);delta=convolution(l,k);raw=np.dstack([r.r9.gray8(np.repeat(l[:,:,None],3,axis=2)),np.full((h,w),255,np.uint8)]);final,_=r.r19.protect(raw,l[:,:,None]+delta[:,:,None]);Image.fromarray(raw).save(folder/f'{name}-input.png');Image.fromarray(final).save(folder/f'{name}-output.png');labelled_pair(Image.fromarray(raw),Image.fromarray(final),(0,0,w,h),folder/f'{name}-comparison.png',f'{name}-output.png',f'{name}-input.png')
        if sign:
            # Discrete H at x>=c integrates the central difference to .5*(R[u]+R[u+1]).
            nextrow=np.pad(wave[:h,1:],((0,0),(0,1)));expected=sign*.5*.5*(wave[:h]+nextrow);actual=delta[:,c-24:c+25];corr=float(np.corrcoef(expected.ravel(),actual.ravel())[0,1]);error=float(np.abs(expected-actual).max());scores={shift:float(np.mean((actual[:,max(0,shift):min(49,49+shift)]-expected[:,max(0,-shift):min(49,49-shift)])**2)) for shift in range(-2,3)};best=min(scores,key=scores.get);assert corr>.99999 and error<2e-6 and best==0;report[name]={'same_sign_correlation':corr,'expected_discrete_half_pixel_average_max_error':error,'best_integer_shift_px':best,'positive_or_negative_step':sign}
        else:
            maximum=float(np.abs(delta).max());assert maximum<1e-12;assert np.array_equal(final,raw);report[name]={'maximum_raw_DC_response':maximum,'new_RGBA_pixels':int(np.any(final!=raw,axis=2).sum())}
    (out/'synthetic-calibration.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return report

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={'base':'data:image/png;base64,'+base64.b64encode((out/'base-B.png').read_bytes()).decode()};options=''
    for key,v in m['outputs'].items():images[key]='data:image/png;base64,'+base64.b64encode((out/v['file']).read_bytes()).decode();options+=f'<option value="{key}">{v["label"]}</option>'
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>参照残差の微分を畳み込む</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>参照残差の微分を畳み込む</h1><p>参照由来のscalar残差を行ごとのkernelとして全細部へ適用します。元形状や反射との完全な分離はできません。</p><select id="mode" aria-label="加工の強さ">OPTIONS<option value="base">加工前</option></select></div><div class="image"><img id="image" alt="行ごとの畳み込み比較"></div><script>const images=IMAGES,s=document.getElementById('mode');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def snapshot_sources(out,extra=()):
    found={};pending=[r,*extra];visited=set()
    while pending:
        mod=pending.pop()
        if id(mod) in visited:continue
        visited.add(id(mod));f=getattr(mod,'__file__',None)
        if f and Path(f).resolve().parent==HERE:
            found[Path(f).name]=Path(f);pending.extend(v for v in vars(mod).values() if isinstance(v,types.ModuleType))
    found[Path(__file__).name]=Path(__file__);snap=out/'source-snapshots';snap.mkdir(exist_ok=True)
    for name,f in found.items():shutil.copyfile(f,snap/name)
    (out/'source-frozen.json').write_text(json.dumps({'files':{str(f.resolve()):sha(f) for f in found.values()}},ensure_ascii=False,indent=2),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);ap.add_argument('--fault050',action='store_true');args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);src=r.SOURCE/'base-B.png';expected='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9';assert sha(src)==expected;freeze_sources=json.loads((ATLAS.parent/'round-27/source-frozen.json').read_text(encoding='utf-8'))['files'];assert all(sha(Path(p))==v for p,v in freeze_sources.items());kernels,wave,provenance=materials(out);calibration=calibrate(out,kernels,wave)
    raw=np.array(Image.open(src).convert('RGBA'));lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));stage,constant=r.r22.stage12(raw,lookup);assert np.array_equal(stage,r.r22.stage12(raw,lookup)[0]);Image.fromarray(stage).save(out/'B-stage12.png');assert sha(out/'B-stage12.png')==sha(ATLAS/'B-stage12.png');surface=r.r19.surface(raw);l=surface@r.r9.WEIGHTS;response=convolution(l,kernels);assert np.array_equal(response,convolution(l,kernels));span=cv2.dilate(l,np.ones((3,15),np.uint8))-cv2.erode(l,np.ones((3,15),np.uint8));gate=np.clip((span-.018)/.055,0,1);gate=gate*gate*(3-2*gate);Image.fromarray(r.r9.gray8(gate)).save(out/'B-span-gate.png');np.savez_compressed(out/'B-response.npz',raw_response=response,span=span,gate=gate)
    for name in ['base-B.png','B-OFF.png']:
        dest=out/name
        if dest.exists():assert sha(dest)==expected
        else:shutil.copyfile(src,dest)
    r.r19.black(raw,out/'base-B-on-black.png');m={'round':28,'source_sha256':sha(Path(__file__)),'input':{'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/'B-OFF.png')==expected},'baseline_stage12_sha256':sha(out/'B-stage12.png'),'baseline_exact_23stage12':True,'surface':'current input nearest-alpha-extended NLM5.5/bilateral surface L','reference_derived_material':provenance,'calibration':calibration,'formula':'delta[y,x]=clip(gain * spanGate[y,x] * sum_u kernel[y,u]*surfaceL[y,x-u],-.25,.25)','span_gate':{'window':[3,15],'smoothstep_span_start':.018,'smoothstep_span_full':.073},'old23_track_residual_used':False,'micro_noise_used':False,'chroma':'current stage RGB plus scalar L difference, gamut clip only','outputs':{}}
    profiles=[(name,gain,stage,1.) for name,gain in PROFILES.items()];optional_modules=[]
    if args.fault050:
        s=importlib.util.spec_from_file_location('frozen25',HERE/'offline-round25.py');q25=importlib.util.module_from_spec(s);s.loader.exec_module(q25);optional_modules.append(q25);parts=q25.signals(raw,lookup);assert np.array_equal(q25.stage(raw,parts,1.),stage);half=q25.stage(raw,parts,.5);Image.fromarray(half).save(out/'B-fault050-stage.png');profiles.append(('kernel100-fault050',1.,half,.5));m['optional_ablation_source_sha256']=sha(HERE/'offline-round25.py')
    visible=raw[:,:,3]>0;boundary=visible&(cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5)<=2);rois={'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}
    for name,gain,base,faultmix in profiles:
        delta=np.clip(response*gate*gain,-.25,.25);a,count=r.r19.protect(raw,base[:,:,:3]/255+delta[:,:,None]);again,_=r.r19.protect(raw,base[:,:,:3]/255+np.clip(convolution(l,kernels)*gate*gain,-.25,.25)[:,:,None]);assert np.array_equal(a,again);assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~visible],raw[~visible]);assert np.array_equal(a[boundary],raw[boundary]);file=f'B-{name}.png';Image.fromarray(a).save(out/file);r.r19.black(a,out/f'B-{name}-on-black.png')
        for label,box in rois.items():labelled_pair(Image.fromarray(raw),Image.fromarray(a),box,out/f'B-{name}-comparison-{label}.png',file,'base-B.png')
        label=f'強さ {gain:.2f}'+(' ／ 矩形欠けの寄与 .50' if faultmix!=1 else '');m['outputs'][name]={'file':file,'sha256':sha(out/file),'label':label,'gain':gain,'legacy_fault_mix':faultmix,'alpha_exact':True,'hidden_exact':True,'inner2px_exact':int(boundary.sum()),'repeat_exact':True,'diagnostics':r.r19.diagnostics(a,raw),'delta_max':float(np.abs(delta).max()),'delta_RMS_visible':float(np.sqrt(np.mean(delta[visible]**2))),'response_regions':{key:{'raw_RMS':float(np.sqrt(np.mean(response[b[1]:b[3],b[0]:b[2]]**2))),'gated_RMS':float(np.sqrt(np.mean(delta[b[1]:b[3],b[0]:b[2]]**2))),'gate_mean':float(gate[b[1]:b[3],b[0]:b[2]].mean())} for key,b in rois.items() if key!='full'}};print(name,m['outputs'][name]['sha256'],flush=True)
    assert sha(src)==expected;assert all(sha(Path(p))==v for p,v in freeze_sources.items());m['round27_sources_unchanged']=True;preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');snapshot_sources(out,optional_modules);print('Round28 complete',flush=True)
if __name__=='__main__':main()
